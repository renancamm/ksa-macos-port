#!/bin/bash
# Turns the Linux release of Kitten Space Agency into a Mac app: ~/Applications/Kitten Space Agency.app
#
# Usage: ./port.sh [options] [ksa_linux_vX.tar.gz | extracted folder]
#   With no archive, it takes the newest ksa_linux_v*.tar.gz in ~/Downloads.
#   For a new KSA version, run it again with the new archive (or use ./update.sh): the app is replaced, your
#   saves, settings and StarMap stay.
#
# Options:
#   --app <path>      where to put the app (default: ~/Applications/Kitten Space Agency.app)
#
# Steps (each one is a script in lib/ that can be read on its own):
#   1. extract the archive next to the app
#   2. convert            convert-cleanup, convert-dotnet, convert-shaders
#   3. app                make-app create
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

APP="${DEFAULT_APP}"
SOURCE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SOURCE="${1%/}" ;;
    esac
    shift
done

check_mac
require_kit
if [ -z "${SOURCE}" ]; then
    SOURCE="$(ls -t "${HOME}"/Downloads/ksa_linux_v*.tar.gz 2> /dev/null | head -1)"
    [ -n "${SOURCE}" ] || die "no ksa_linux_v*.tar.gz in ~/Downloads. Download the Linux version of KSA from https://ksa.ahwoo.com, or pass the archive's path."
    info "Using ${SOURCE}"
fi
[ -e "${SOURCE}" ] || die "${SOURCE} not found."
if [ -d "${SOURCE}" ]; then
    [ -f "${SOURCE}/KSA.dll" ] || die "${SOURCE} has no KSA.dll."
fi

# Extract next to the app, on the same volume, so moving the game into the app is instant
STAGE=""
if [ -d "${SOURCE}" ]; then
    step "[1/3] Using the folder ${SOURCE} (it is converted in place and moved into the app)"
    GAME="$(cd "${SOURCE}" && pwd)"
else
    step "[1/3] Extracting $(basename "${SOURCE}")"
    mkdir -p "$(dirname "${APP}")"
    STAGE="$(mktemp -d "$(dirname "${APP}")/.ksa-port.XXXXXX")"
    trap 'rm -rf "${STAGE}"' EXIT
    tar -xf "${SOURCE}" -C "${STAGE}"
    GAME="$(dirname "$(find "${STAGE}" -maxdepth 2 -name KSA.dll | head -1)")"
    [ -f "${GAME}/KSA.dll" ] || die "no KSA.dll in $(basename "${SOURCE}"). Is this the Linux version of KSA?"
fi
VERSION="$(game_version "${GAME}")"
info "KSA ${VERSION}"
check_ksa_match "${VERSION}"

step "[2/3] Converting the game for macOS"
run convert-cleanup "${GAME}"
run convert-dotnet "${GAME}" "${SKELETON}/MacOS"
run convert-shaders "${GAME}"

step "[3/3] Making the app"
run make-app create "${GAME}" "${APP}"

echo ""
step "Done: ${APP} (KSA $(app_version "${APP}"))"
info "Open \"${APP_NAME}\" from $(dirname "${APP}")."
