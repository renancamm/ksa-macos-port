#!/bin/bash
# Adds StarMap, the KSA mod loader (https://github.com/StarMapLoader/StarMap), to Kitten Space Agency.app. The app
# then starts the game through StarMap, which loads the StarMap mods in the game's mods folder.
#
# Usage: ./starmap.sh [options]
#   ./port.sh and ./update.sh keep StarMap in the app once it is there.
#
# Options:
#   --app <path>      the app to change (default: ~/Applications/Kitten Space Agency.app)
#   --remove          take StarMap out of the app again
#
# StarMap's release is its Windows build. Only two of its files are Windows-specific: StarMap.exe, a launcher the
# app does not need (it runs StarMap.dll with its own .NET runtime), and StarMap.dll's AMD64 mark, which this
# port's build changes to ARM64 like KSA.dll's (lib/mark-arm64.sh). The rest is platform-neutral .NET.
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

usage() {
    sed -n '2,10p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

APP="${DEFAULT_APP}"
REMOVE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        --remove) REMOVE=1 ;;
        -h | --help) usage ;;
        *) echo "Unknown option: $1"; usage 1 ;;
    esac
    shift
done

check_mac
require_kit
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."

if [ "${REMOVE}" = 1 ]; then
    rm -rf "${APP}/Contents/Resources/StarMap"
    touch "${APP}"
    step "Done: StarMap removed from ${APP}. The game starts without mods."
    exit 0
fi

step "Adding StarMap $(cat "${PAYLOAD}/starmap/version.txt") to ${APP} (KSA ${INSTALLED})"
check_ksa_match "${INSTALLED}"
install_starmap "${APP}"
touch "${APP}"

step "Done: the game now starts through StarMap."
info "Mods go in ~/Documents/My Games/Kitten Space Agency/mods/<mod>/. To start without mods: --remove."
