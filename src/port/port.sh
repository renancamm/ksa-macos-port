#!/bin/bash
# Turns the Linux release of Kitten Space Agency into a Mac app, ~/Applications/Kitten Space Agency.app, or updates
# the one you have. Saves, settings and StarMap stay.
#
# Usage: ./port.sh [options] [ksa_linux_vX.tar.gz | extracted folder]
#   With an archive, makes the app from it, replacing the one you have. Without one, takes the newest
#   ksa_linux_v*.tar.gz (or .tar) in ~/Downloads when it is newer than the app you have (or there is no app yet);
#   otherwise it puts this folder's files into the app you have.
#
# Options:
#   --update          first update this folder to the newest release of the port, from GitHub
#   --app <path>      where the app is (default: ~/Applications/Kitten Space Agency.app)
#
# Steps (each one is a script in lib/):
#   1. extract the archive next to the app
#   2. convert            convert-cleanup, convert-dotnet, convert-shaders
#   3. app                make-app create, or make-app refresh for the app you have
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

usage() {
    sed -n '2,17p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

ARGS=() # the arguments again, without --update, for the new port.sh after a self-update
APP="${DEFAULT_APP}"
SOURCE=""
UPDATE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --update) UPDATE=1 ;;
        --app) APP="${2%/}"; ARGS+=("$1" "$2"); shift ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SOURCE="${1%/}"; ARGS+=("$1") ;;
    esac
    shift
done

# json_value <file> <key path>: a value from GitHub's JSON answer, empty when it has none (plutil reads JSON)
json_value() {
    plutil -extract "$2" raw -o - "$1" 2> /dev/null || true
}

# self_update: replaces this folder with the newest release when it is newer, and starts its port.sh
self_update() {
    local current latest tag tmp i name url="" digest="" new f
    current="$(kit_value version)"
    case "${current}" in
        "" | dev*)
            info "This port folder was built locally (version ${current:-unknown}); it is not updated online."
            return 0 ;;
    esac
    step "Checking for a newer version of the port"
    tmp="$(mktemp -d)"
    if ! curl -fsSL --retry 2 -H "Accept: application/vnd.github+json" -o "${tmp}/release.json" \
        "${KSA_PORT_RELEASE_API:-https://api.github.com/repos/${RELEASES_REPO}/releases/latest}" 2> /dev/null; then
        warn "could not reach GitHub, so the port itself was not checked for updates."
        rm -rf "${tmp}"
        return 0
    fi
    # Releases of the port are tagged v<KSA build>.<n>
    tag="$(json_value "${tmp}/release.json" tag_name)"
    latest="${tag#v}"
    if ! [[ "${tag}" =~ ^v[0-9]+\.[0-9]+$ ]]; then
        info "No release of the port on GitHub to compare with."
        rm -rf "${tmp}"
        return 0
    fi
    if ! version_newer "${latest}" "${current}"; then
        info "This is the newest version (${current})."
        rm -rf "${tmp}"
        return 0
    fi
    i=0
    while name="$(plutil -extract "assets.${i}.name" raw -o - "${tmp}/release.json" 2> /dev/null)"; do
        case "${name}" in
            ksa-macos-port-*.zip)
                url="$(json_value "${tmp}/release.json" "assets.${i}.browser_download_url")"
                digest="$(json_value "${tmp}/release.json" "assets.${i}.digest")"
                break ;;
        esac
        i=$((i + 1))
    done
    [ -n "${url}" ] || die "release ${tag} has no download of the port."

    info "Downloading ${tag} (this folder has ${current})"
    curl -fsSL --retry 3 -o "${tmp}/port.zip" "${url}" || die "could not download ${url}"
    case "${digest}" in
        sha256:*) [ "$(shasum -a 256 "${tmp}/port.zip" | cut -d' ' -f1)" = "${digest#sha256:}" ] ||
            die "the download does not match the checksum GitHub publishes for it." ;;
        *) die "GitHub publishes no checksum for the download of ${tag}, so it was not installed." ;;
    esac
    ditto -x -k "${tmp}/port.zip" "${tmp}/new"
    new="${tmp}/new/ksa-macos-port"
    [ -f "${new}/VERSION" ] && [ -f "${new}/port.sh" ] && [ -f "${new}/lib/common.sh" ] ||
        die "the download of ${tag} has an unexpected layout."

    # Replace this folder's contents. bash goes on reading this script from the removed file, and the folder itself
    # stays, so a Terminal in it stays there.
    [ -f "${KIT}/VERSION" ] && [ -f "${KIT}/port.sh" ] && [ -f "${KIT}/lib/common.sh" ] ||
        die "${KIT} doesn't look like a port folder; not replacing it."
    for f in "${KIT}"/* "${KIT}"/.[!.]*; do
        if [ -e "${f}" ] || [ -L "${f}" ]; then
            rm -rf "${f}"
        fi
    done
    for f in "${new}"/*; do
        mv "${f}" "${KIT}/"
    done
    rm -rf "${tmp}"
    step "The port is now version ${latest}"
    exec "${KIT}/port.sh" ${ARGS[@]+"${ARGS[@]}"}
}

check_mac
require_kit
[ "${UPDATE}" = 0 ] || self_update

INSTALLED="$(app_version "${APP}")"
if [ -z "${SOURCE}" ]; then
    if [ -z "${INSTALLED}" ]; then
        SOURCE="$(newest_download)"
        [ -n "${SOURCE}" ] || die "no ksa_linux_v*.tar.gz (or .tar) in ~/Downloads. Download the Linux version of KSA from https://ksa.ahwoo.com, or pass the archive's path."
        info "Using ${SOURCE}"
    else
        # A KSA in ~/Downloads newer than the app's
        SOURCE="$(newest_download "${INSTALLED}")"
        [ -z "${SOURCE}" ] || info "Found KSA $(download_version "${SOURCE}") in ~/Downloads (the app has ${INSTALLED})."
    fi
fi

# No newer game: this folder's files into the app you have
if [ -z "${SOURCE}" ]; then
    step "Updating the port's files in ${APP} (KSA ${INSTALLED})"
    check_ksa_match "${INSTALLED}"
    RES="${APP}/Contents/Resources"
    run convert-cleanup "${RES}"
    run convert-dotnet "${RES}" "${SKELETON}/MacOS"
    run convert-shaders "${RES}"
    run make-app refresh "${APP}"
    echo ""
    step "Done: ${APP} is up to date (KSA ${INSTALLED}, port $(kit_value version))."
    info "To update the game itself, download its new Linux version into ~/Downloads and run ./port.sh again."
    exit
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
