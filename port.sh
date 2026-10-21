#!/bin/bash
# Turns the Linux release of Kitten Space Agency into a Mac app: ~/Applications/Kitten Space Agency.app
#
# Usage: ./port.sh [options] [ksa_linux_vX.tar.gz | extracted folder]
#   With no archive, it takes the newest ksa_linux_v*.tar.gz in ~/Downloads.
#   For a new KSA version, run it again with the new archive (or use ./update.sh): the app is replaced, your
#   saves and settings stay.
#
# Options:
#   --app <path>      where to put the app (default: ~/Applications/Kitten Space Agency.app)
#   --from-source     build the port's own libraries on this Mac instead of downloading them
#
# Steps (each one is a script in scripts/ that can be read on its own):
#   1. native libraries   fetch-brutal-natives, then fetch-prebuilt or build-* and check-natives
#   2. .NET runtime       fetch-dotnet
#   3. extract the archive next to the app
#   4. convert            convert-cleanup, convert-dotnet, convert-shaders
#   5. app                make-app create
set -eo pipefail
source "$(dirname "$0")/scripts/lib.sh"

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

APP="${DEFAULT_APP}"
FROM_SOURCE=""
SOURCE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        --from-source) FROM_SOURCE="--from-source" ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SOURCE="${1%/}" ;;
    esac
    shift
done

check_mac
if [ -z "${SOURCE}" ]; then
    SOURCE="$(ls -t "${HOME}"/Downloads/ksa_linux_v*.tar.gz 2> /dev/null | head -1)"
    [ -n "${SOURCE}" ] || die "no ksa_linux_v*.tar.gz in ~/Downloads. Download the Linux version of KSA from https://ksa.ahwoo.com, or pass the archive's path."
    info "Using ${SOURCE}"
fi
[ -e "${SOURCE}" ] || die "${SOURCE} not found."
if [ -d "${SOURCE}" ]; then
    [ -f "${SOURCE}/KSA.dll" ] || die "${SOURCE} has no KSA.dll."
fi

step "[1/5] Native libraries"
prepare_natives ${FROM_SOURCE}

step "[2/5] .NET runtime"
run fetch-dotnet

# Extract next to the app, on the same volume, so moving the game into the app is instant
STAGE=""
if [ -d "${SOURCE}" ]; then
    step "[3/5] Using the folder ${SOURCE} (it is converted in place and moved into the app)"
    GAME="$(cd "${SOURCE}" && pwd)"
else
    step "[3/5] Extracting $(basename "${SOURCE}")"
    mkdir -p "$(dirname "${APP}")"
    STAGE="$(mktemp -d "$(dirname "${APP}")/.ksa-port.XXXXXX")"
    trap 'rm -rf "${STAGE}"' EXIT
    tar -xf "${SOURCE}" -C "${STAGE}"
    GAME="$(dirname "$(find "${STAGE}" -maxdepth 2 -name KSA.dll | head -1)")"
    [ -f "${GAME}/KSA.dll" ] || die "no KSA.dll in $(basename "${SOURCE}"). Is this the Linux version of KSA?"
fi
info "KSA $(game_version "${GAME}")"

step "[4/5] Converting the game for macOS"
run convert-cleanup "${GAME}"
run convert-dotnet "${GAME}" "$(dotnet_runtime_dir)"
run convert-shaders "${GAME}"

step "[5/5] Making the app"
run make-app create "${GAME}" "${APP}"

echo ""
step "Done: ${APP} (KSA $(app_version "${APP}"))"
info "Open \"${APP_NAME}\" from $(dirname "${APP}")."
