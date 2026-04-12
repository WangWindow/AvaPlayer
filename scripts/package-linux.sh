#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
PROJECT_PATH="${ROOT_DIR}/AvaPlayer.csproj"

APP_NAME="AvaPlayer"
RID="${RID:-linux-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
VERSION="${VERSION:-$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "${PROJECT_PATH}" | head -n 1)}"
VERSION="${VERSION:-1.0.0}"

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

  if [[ -x "${ROOT_DIR}/artifacts/appimagetool-x86_64.AppImage" ]]; then
    printf '%s\n' "${ROOT_DIR}/artifacts/appimagetool-x86_64.AppImage"
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
  curl -L https://github.com/AppImage/appimagetool/releases/latest/download/appimagetool-x86_64.AppImage -o artifacts/appimagetool-x86_64.AppImage
  chmod +x artifacts/appimagetool-x86_64.AppImage
  APPIMAGE_TOOL_PATH="$PWD/artifacts/appimagetool-x86_64.AppImage" scripts/package-linux.sh
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

package_appimage() {
  local tool_path arch
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

  if command -v desktop-file-validate >/dev/null 2>&1; then
    desktop-file-validate "${APPDIR}/${APP_NAME}.desktop"
  else
    echo "desktop-file-validate not found; skipping desktop entry validation."
  fi

  mkdir -p "$(dirname "${APPIMAGE_PATH}")"
  APPIMAGE_EXTRACT_AND_RUN=1 ARCH="${arch}" "${tool_path}" "${APPDIR}" "${APPIMAGE_PATH}"
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
