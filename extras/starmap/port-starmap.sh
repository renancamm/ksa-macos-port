#!/bin/bash
# Adds StarMap, the KSA mod loader (https://github.com/StarMapLoader/StarMap), to Kitten Space Agency.app. The app
# then starts the game through StarMap, which loads the StarMap mods in the game's mods folder.
#
# Usage: extras/starmap/port-starmap.sh [options]
#   Run it again after a new KSA version: ./port.sh makes a new app, without StarMap. ./update.sh keeps it.
#
# Options:
#   --app <path>      the app to change (default: ~/Applications/Kitten Space Agency.app)
#   --remove          take StarMap out of the app again
#
# StarMap's release is its Windows build. Only two of its files are Windows-specific: StarMap.exe, a launcher
# the app does not need (it runs StarMap.dll with its own .NET runtime), and StarMap.dll's AMD64 mark, changed to
# ARM64 like KSA.dll's (scripts/mark-arm64.sh). The rest is platform-neutral .NET.
set -eo pipefail
source "$(dirname "$0")/../../scripts/lib.sh"

# The StarMap release, its SHA-256, and the KSA version it is made for
STARMAP_VERSION="0.4.8"
STARMAP_URL="https://github.com/StarMapLoader/StarMap/releases/download/${STARMAP_VERSION}/StarMap-${STARMAP_VERSION}.zip"
STARMAP_SHA256="5324240f15d6841835158940057359ef011149bbc30b4d50f7b27480df7b2d5c"
STARMAP_KSA="2026.10.19.5638"

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
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."
TARGET="${APP}/Contents/Resources/StarMap"

if [ "${REMOVE}" = 1 ]; then
    rm -rf "${TARGET}"
    touch "${APP}"
    step "Done: StarMap removed from ${APP}. The game starts without mods."
    exit 0
fi

if [ "${INSTALLED}" != "${STARMAP_KSA}" ]; then
    warn "StarMap ${STARMAP_VERSION} is made for KSA ${STARMAP_KSA}, the app has KSA ${INSTALLED}." \
        "If the game fails to start, remove StarMap again with --remove."
fi

step "Fetching StarMap ${STARMAP_VERSION}"
ARCHIVE="${DEPS}/starmap/StarMap-${STARMAP_VERSION}.zip"
download "${STARMAP_URL}" "${ARCHIVE}" 256 "${STARMAP_SHA256}"

step "Adding StarMap to ${APP} (KSA ${INSTALLED})"
rm -rf "${TARGET}"
mkdir -p "${TARGET}"
ditto -x -k "${ARCHIVE}" "${TARGET}"
[ -f "${TARGET}/StarMap.dll" ] && [ -f "${TARGET}/StarMap.Core.dll" ] || die "the StarMap archive has an unexpected layout."
rm -f "${TARGET}/StarMap.exe"
run mark-arm64 "${TARGET}"/*.dll
touch "${APP}"

step "Done: the game now starts through StarMap."
info "Mods go in ~/Documents/My Games/Kitten Space Agency/mods/<mod>/. To start without mods: --remove."
