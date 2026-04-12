#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd -- "${SCRIPT_DIR}/.." && pwd)"
PROJECT_PATH="${ROOT_DIR}/AvaPlayer.csproj"
ISS_PATH="${ROOT_DIR}/scripts/windows/AvaPlayer.iss"

APP_NAME="AvaPlayer"
RID="${RID:-win-x64}"
CONFIGURATION="${CONFIGURATION:-Release}"
PUBLISH_AOT="${PUBLISH_AOT:-false}"
VERSION="${VERSION:-}"
SKIP_PUBLISH="${SKIP_PUBLISH:-false}"
SKIP_ZIP="${SKIP_ZIP:-false}"
SKIP_EXE="${SKIP_EXE:-false}"
INNO_SETUP_ISCC_PATH="${INNO_SETUP_ISCC_PATH:-}"

print_help() {
  cat <<'EOF'
Build a Windows package from Linux.

Usage:
  scripts/package-windows-from-linux.sh [options]

Options:
  --rid <rid>                Windows RID, default: win-x64
  --configuration <config>   Build configuration, default: Release
  --version <version>        Override AssemblyVersion-derived package version
  --aot                      Enable Native AOT publish (disabled by default on Linux)
  --skip-publish             Reuse an existing publish directory
  --skip-zip                 Do not generate the portable zip archive
  --skip-exe                 Do not try to build the Inno Setup installer
  --iscc <path>              Override the ISCC.exe path used through Wine
  --help                     Show this help text

Notes:
  - The script always cross-publishes with EnableWindowsTargeting=true.
  - Native AOT is disabled by default because Linux -> Windows AOT usually needs
    extra Windows cross-compilation toolchains. Enable it only if your setup is ready.
  - ZIP output works with either `zip` or the Python 3 standard library.
  - EXE installer generation requires Wine + Inno Setup installed inside Wine.
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid)
      RID="$2"
      shift 2
      ;;
    --configuration|-c)
      CONFIGURATION="$2"
      shift 2
      ;;
    --version|-v)
      VERSION="$2"
      shift 2
      ;;
    --aot)
      PUBLISH_AOT=true
      shift
      ;;
    --skip-publish)
      SKIP_PUBLISH=true
      shift
      ;;
    --skip-zip)
      SKIP_ZIP=true
      shift
      ;;
    --skip-exe)
      SKIP_EXE=true
      shift
      ;;
    --iscc)
      INNO_SETUP_ISCC_PATH="$2"
      shift 2
      ;;
    --help|-h)
      print_help
      exit 0
      ;;
    *)
      echo "Unknown option: $1" >&2
      print_help >&2
      exit 1
      ;;
  esac
done

if [[ -z "${VERSION}" ]]; then
  VERSION="$(sed -n 's:.*<AssemblyVersion>\(.*\)</AssemblyVersion>.*:\1:p' "${PROJECT_PATH}" | head -n 1)"
fi

VERSION="${VERSION:-1.0.0}"

ARTIFACT_ROOT="${ROOT_DIR}/artifacts/package/${RID}/${VERSION}"
PUBLISH_DIR="${ARTIFACT_ROOT}/publish"
ZIP_PATH="${ARTIFACT_ROOT}/${APP_NAME}-${VERSION}-${RID}.zip"
EXE_PATH="${ARTIFACT_ROOT}/${APP_NAME}-${VERSION}-${RID}-setup.exe"

require_tool() {
  if ! command -v "$1" >/dev/null 2>&1; then
    echo "Missing required tool: $1" >&2
    exit 1
  fi
}

create_zip() {
  if command -v zip >/dev/null 2>&1; then
    (
      cd "${PUBLISH_DIR}"
      zip -qr "${ZIP_PATH}" .
    )
    return 0
  fi

  if command -v python3 >/dev/null 2>&1; then
    python3 - "${PUBLISH_DIR}" "${ZIP_PATH}" <<'PY'
import os
import sys
import zipfile

root = os.path.abspath(sys.argv[1])
out_path = os.path.abspath(sys.argv[2])

with zipfile.ZipFile(out_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
    for current_root, _, files in os.walk(root):
        for file_name in files:
            full_path = os.path.join(current_root, file_name)
            relative_path = os.path.relpath(full_path, root)
            archive.write(full_path, relative_path)
PY
    return 0
  fi

  cat <<'EOF'
Skipping ZIP because neither `zip` nor `python3` is available.
Install one of them to generate a Windows portable package:
  sudo apt install zip
EOF
}

find_iscc() {
  if [[ -n "${INNO_SETUP_ISCC_PATH}" && -f "${INNO_SETUP_ISCC_PATH}" ]]; then
    printf '%s\n' "${INNO_SETUP_ISCC_PATH}"
    return 0
  fi

  local candidates=(
    "${HOME}/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe"
    "${HOME}/.wine/drive_c/Program Files/Inno Setup 6/ISCC.exe"
    "${HOME}/.wine/drive_c/Program Files (x86)/Inno Setup 5/ISCC.exe"
    "${HOME}/.wine/drive_c/Program Files/Inno Setup 5/ISCC.exe"
  )
  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -f "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  done

  if [[ -d "${HOME}/.wine/drive_c" ]]; then
    candidate="$(find "${HOME}/.wine/drive_c" -type f -iname 'ISCC.exe' -print -quit 2>/dev/null || true)"
    if [[ -n "${candidate}" ]]; then
      printf '%s\n' "${candidate}"
      return 0
    fi
  fi

  return 1
}

note_missing_inno() {
  cat <<'EOF'
Skipping EXE installer because Wine or Inno Setup was not found.

Install the required tools on Linux:
  sudo apt install wine
  curl -LO https://jrsoftware.org/download.php/is.exe
  wine is.exe

Then rerun this script, or provide the compiler path explicitly:
  INNO_SETUP_ISCC_PATH="$HOME/.wine/drive_c/Program Files (x86)/Inno Setup 6/ISCC.exe" \
    scripts/package-windows-from-linux.sh
EOF
}

build_exe() {
  local iscc_path win_iss_path win_publish_dir win_output_dir win_repo_root

  if [[ "${SKIP_EXE}" == "true" ]]; then
    return 0
  fi

  if [[ "${RID}" != "win-x64" ]]; then
    echo "Skipping EXE installer because the current Inno Setup template is x64-specific (RID=${RID})."
    return 0
  fi

  if ! command -v wine >/dev/null 2>&1 || ! command -v winepath >/dev/null 2>&1; then
    note_missing_inno
    return 0
  fi

  if ! iscc_path="$(find_iscc)"; then
    note_missing_inno
    return 0
  fi

  win_iss_path="$(winepath -w "${ISS_PATH}")"
  win_publish_dir="$(winepath -w "${PUBLISH_DIR}")"
  win_output_dir="$(winepath -w "${ARTIFACT_ROOT}")"
  win_repo_root="$(winepath -w "${ROOT_DIR}")"

  WINEDEBUG="${WINEDEBUG:--all}" wine "${iscc_path}" \
    /Qp \
    "/DMyAppVersion=${VERSION}" \
    "/DMyPublishDir=${win_publish_dir}" \
    "/DMyOutputDir=${win_output_dir}" \
    "/DMyRepoRoot=${win_repo_root}" \
    "${win_iss_path}"
}

main() {
  require_tool dotnet
  mkdir -p "${ARTIFACT_ROOT}"

  if [[ "${SKIP_PUBLISH}" != "true" ]]; then
    rm -rf "${PUBLISH_DIR}"

    dotnet restore "${PROJECT_PATH}" \
      -r "${RID}" \
      -p:EnableWindowsTargeting=true \
      -p:TargetFramework=net10.0-windows

    dotnet publish "${PROJECT_PATH}" \
      -c "${CONFIGURATION}" \
      -r "${RID}" \
      --self-contained true \
      -p:EnableWindowsTargeting=true \
      -p:TargetFramework=net10.0-windows \
      -p:PublishAot="${PUBLISH_AOT}" \
      -o "${PUBLISH_DIR}"
  fi

  if [[ ! -d "${PUBLISH_DIR}" ]]; then
    echo "Publish directory does not exist: ${PUBLISH_DIR}" >&2
    exit 1
  fi

  if [[ "${SKIP_ZIP}" != "true" ]]; then
    create_zip
  fi

  build_exe

  cat <<EOF
Artifacts written to:
  ${ARTIFACT_ROOT}
EOF

  if [[ -f "${ZIP_PATH}" ]]; then
    echo "  ZIP : ${ZIP_PATH}"
  fi

  if [[ -f "${EXE_PATH}" ]]; then
    echo "  EXE : ${EXE_PATH}"
  fi
}

main "$@"
