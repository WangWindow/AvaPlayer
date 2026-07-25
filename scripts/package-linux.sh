#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
PROJECT_PATH="${ROOT_DIR}/AvaPlayer/AvaPlayer.csproj"

APP_NAME="AvaPlayer"

detect_default_rid() {
  case "$(uname -m)" in
    x86_64|amd64) printf 'linux-x64\n' ;;
    aarch64|arm64) printf 'linux-arm64\n' ;;
    *)
      echo "Unsupported host architecture: $(uname -m). Pass --rid linux-x64 or --rid linux-arm64." >&2
      exit 1
      ;;
  esac
}

RID="${RID:-$(detect_default_rid)}"
CONFIGURATION="${CONFIGURATION:-Release}"
APPIMAGE_TOOL_PATH="${APPIMAGE_TOOL_PATH:-}"
SKIP_APPIMAGE="${SKIP_APPIMAGE:-false}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid|-r)
      [[ $# -ge 2 ]] || { echo "Missing value for $1." >&2; exit 2; }
      RID="$2"
      shift 2
      ;;
    --configuration|-c)
      [[ $# -ge 2 ]] || { echo "Missing value for $1." >&2; exit 2; }
      CONFIGURATION="$2"
      shift 2
      ;;
    --version)
      [[ $# -ge 2 ]] || { echo "Missing value for $1." >&2; exit 2; }
      VERSION="$2"
      shift 2
      ;;
    --appimage-tool)
      [[ $# -ge 2 ]] || { echo "Missing value for $1." >&2; exit 2; }
      APPIMAGE_TOOL_PATH="$2"
      shift 2
      ;;
    --skip-appimage)
      SKIP_APPIMAGE=true
      shift
      ;;
    --help|-h)
      cat <<'EOF'
Usage: scripts/package-linux.sh [options]

Options:
  -r, --rid RID                  Target RID (default: host architecture)
  -c, --configuration CONFIG     Build configuration (default: Release)
      --version VERSION          Override application version
      --appimage-tool PATH       Use a specific appimagetool binary
      --skip-appimage            Skip AppImage generation
EOF
      exit 0
      ;;
    *)
      echo "Unknown argument: $1. Use --help for usage." >&2
      exit 2
      ;;
  esac
done

VERSION="${VERSION:-$(sed -n 's:.*<Version[^>]*>\([^<]*\)</Version>.*:\1:p' "${ROOT_DIR}/Directory.Build.props" | head -n 1)}"
if [[ -z "${VERSION}" ]]; then
  echo "Version is not defined. Pass --version or define it in Directory.Build.props." >&2
  exit 1
fi

ARTIFACT_ROOT="${ROOT_DIR}/artifacts/package/${RID}/${VERSION}"
PUBLISH_DIR="${ARTIFACT_ROOT}/publish"
APPDIR="${ARTIFACT_ROOT}/${APP_NAME}.AppDir"
TAR_PATH="${ARTIFACT_ROOT}/${APP_NAME}-${VERSION}-${RID}.tar.gz"
ZIP_PATH="${ARTIFACT_ROOT}/${APP_NAME}-${VERSION}-${RID}.zip"
APPIMAGE_PATH="${ARTIFACT_ROOT}/${APP_NAME}-${VERSION}-${RID}.AppImage"
ICON_PATH="${ROOT_DIR}/assets/logo.png"

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required tool: $1" >&2
    exit 1
  fi
}

detect_appimage_tool() {
  if [[ -n "${APPIMAGE_TOOL_PATH:-}" ]]; then
    printf '%s\n' "${APPIMAGE_TOOL_PATH}"
    return 0
  fi

  if command -v appimagetool >/dev/null 2>&1; then
    command -v appimagetool
    return 0
  fi

  local tool_name
  case "${RID}" in
    linux-x64) tool_name="appimagetool-x86_64.AppImage" ;;
    linux-arm64) tool_name="appimagetool-aarch64.AppImage" ;;
    *) return 1 ;;
  esac

  if [[ -x "${ROOT_DIR}/artifacts/${tool_name}" ]]; then
    printf '%s\n' "${ROOT_DIR}/artifacts/${tool_name}"
    return 0
  fi

  return 1
}

map_arch() {
  case "$1" in
    linux-x64) printf 'x86_64\n' ;;
    linux-arm64) printf 'aarch64\n' ;;
    *)
      echo "Unsupported AppImage RID: $1" >&2
      exit 1
      ;;
  esac
}

note_missing_zip() {
  cat <<'EOF'
Skipping zip archive because the `zip` command is not installed.
Install it with your package manager, for example:
  sudo apt install zip
EOF
}

note_missing_appimagetool() {
  cat <<'EOF'
Skipping AppImage because appimagetool was not found.
Download one of the official binaries and point the script at it:
  mkdir -p artifacts/
  curl -L https://github.com/AppImage/appimagetool/releases/latest/download/appimagetool-<arch>.AppImage -o artifacts/appimagetool-<arch>.AppImage
  chmod +x artifacts/appimagetool-<arch>.AppImage
  scripts/package-linux.sh --appimage-tool "$PWD/artifacts/appimagetool-<arch>.AppImage"
EOF
}

create_desktop_file() {
  cat > "${APPDIR}/${APP_NAME}.desktop" <<EOF
[Desktop Entry]
Name=${APP_NAME}
Exec=${APP_NAME}
Icon=avaplayer
Type=Application
Terminal=false
Categories=AudioVideo;Audio;Player;
Keywords=music;audio;player;
X-AppImage-Version=${VERSION}
EOF
}

create_apprun() {
  cat > "${APPDIR}/AppRun" <<'EOF'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/usr/bin${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
exec "$HERE/usr/bin/AvaPlayer" "$@"
EOF
  chmod +x "${APPDIR}/AppRun"
}

create_metainfo() {
  local metainfo_dir="${APPDIR}/usr/share/metainfo"
  mkdir -p "${metainfo_dir}"
  cat > "${metainfo_dir}/${APP_NAME}.metainfo.xml" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<component type="desktop-application">
  <id>${APP_NAME}</id>
  <name>${APP_NAME}</name>
  <summary>Audio player</summary>
  <metadata_license>MIT</metadata_license>
  <project_license>MIT</project_license>
  <description>
    <p>A lightweight audio player built with Avalonia.</p>
  </description>
  <releases>
    <release version="${VERSION}" date="$(date +%Y-%m-%d)" />
  </releases>
  <update_contact></update_contact>
</component>
EOF
}

package_appimage() {
  local tool_path arch
  if [[ "${SKIP_APPIMAGE}" == "true" ]]; then
    echo "Skipping AppImage because it was disabled."
    return 0
  fi

  if ! tool_path="$(detect_appimage_tool)"; then
    note_missing_appimagetool
    return 0
  fi

  arch="$(map_arch "${RID}")"

  rm -rf "${APPDIR}"
  mkdir -p "${APPDIR}/usr/bin"
  cp -a "${PUBLISH_DIR}/." "${APPDIR}/usr/bin/"
  cp "${ICON_PATH}" "${APPDIR}/avaplayer.png"
  cp "${ICON_PATH}" "${APPDIR}/.DirIcon"
  create_desktop_file
  create_apprun
  create_metainfo

  if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "${APPDIR}/${APP_NAME}.desktop"
  else
    echo "desktop-file-validate not found; skipping desktop entry validation."
  fi

  mkdir -p "$(dirname "${APPIMAGE_PATH}")"
  VERSION="${VERSION}" APPIMAGE_EXTRACT_AND_RUN=1 ARCH="${arch}" "${tool_path}" "${APPDIR}" "${APPIMAGE_PATH}"
}

main() {
  require_tool dotnet
  require_tool tar

  rm -rf "${ARTIFACT_ROOT}"
  mkdir -p "${ARTIFACT_ROOT}"

  dotnet publish "${PROJECT_PATH}" \
    -c "${CONFIGURATION}" \
    -r "${RID}" \
    --self-contained true \
    -o "${PUBLISH_DIR}"

  # Remove debug symbol files (.pdb) from publish output before packaging
  find "${PUBLISH_DIR}" -name '*.pdb' -type f -delete

  tar -C "${PUBLISH_DIR}" -czf "${TAR_PATH}" .

  if command -v zip >/dev/null 2>&1; then
    (
      cd "${PUBLISH_DIR}"
      zip -qr "${ZIP_PATH}" .
    )
  else
    note_missing_zip
  fi

  package_appimage

  cat <<EOF
Artifacts written to:
  ${ARTIFACT_ROOT}
EOF
}

main "$@"
