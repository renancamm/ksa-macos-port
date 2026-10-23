#!/bin/bash
# Updates Kitten Space Agency.app: first this port folder to the newest release, then the app to a new game version
# when you have downloaded one, otherwise the app to this folder's version of the port. Saves and settings are not
# touched.
#
# Usage: ./update.sh [options] [ksa_linux_vX.tar.gz]
#   1. Checks GitHub for a newer release of the port. When there is one, it replaces this folder with it and starts
#      again from the new version.
#   2. With an archive, or when ~/Downloads has a ksa_linux_v*.tar.gz newer than the installed game: converts that
#      version and replaces the app (./port.sh). Otherwise puts this folder's files into the installed app.
#   It never checks online for new KSA versions: download them from https://ksa.ahwoo.com yourself.
#
# Options:
#   --app <path>      the app to update (default: ~/Applications/Kitten Space Agency.app)
#   --offline         skip step 1
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

ARGS=("$@")
APP="${DEFAULT_APP}"
OFFLINE=0
ARCHIVE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        --offline) OFFLINE=1 ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) ARCHIVE="$1" ;;
    esac
    shift
done

# json_value <file> <key path>: a value from GitHub's JSON answer, empty when it has none (plutil reads JSON)
json_value() {
    plutil -extract "$2" raw -o - "$1" 2> /dev/null || true
}

# self_update: replaces this folder with the newest release when it is newer, and starts its update.sh
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
    esac
    ditto -x -k "${tmp}/port.zip" "${tmp}/new"
    new="${tmp}/new/ksa-macos-port"
    [ -f "${new}/VERSION" ] && [ -f "${new}/update.sh" ] && [ -f "${new}/lib/common.sh" ] ||
        die "the download of ${tag} has an unexpected layout."

    # Replace this folder's contents. bash goes on reading this script from the removed file, and the folder itself
    # stays, so a Terminal in it stays there.
    [ -f "${KIT}/VERSION" ] && [ -f "${KIT}/update.sh" ] && [ -f "${KIT}/lib/common.sh" ] ||
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
    exec "${KIT}/update.sh" --offline ${ARGS[@]+"${ARGS[@]}"}
}

check_mac
require_kit

# 1. The port itself
[ "${OFFLINE}" = 1 ] || self_update

INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."

# 2. A newer game version from ~/Downloads
if [ -z "${ARCHIVE}" ]; then
    for candidate in "${HOME}"/Downloads/ksa_linux_v*.tar.gz; do
        [ -f "${candidate}" ] || continue
        v="$(basename "${candidate}" | sed -E 's/^ksa_linux_v([0-9.]+[0-9])\..*$/\1/')"
        if version_newer "${v}" "${INSTALLED}" && { [ -z "${ARCHIVE}" ] || version_newer "${v}" "${NEWEST}"; }; then
            ARCHIVE="${candidate}"
            NEWEST="${v}"
        fi
    done
    [ -n "${ARCHIVE}" ] && info "Found KSA ${NEWEST} in ~/Downloads (installed: ${INSTALLED})."
fi
if [ -n "${ARCHIVE}" ]; then
    exec "${KIT}/port.sh" --app "${APP}" "${ARCHIVE}"
fi

# 3. Same game version: this folder's files into the installed app
step "Updating the port's files in ${APP} (KSA ${INSTALLED})"
check_ksa_match "${INSTALLED}"
RES="${APP}/Contents/Resources"
run convert-cleanup "${RES}"
run convert-dotnet "${RES}" "${SKELETON}/MacOS"
run convert-shaders "${RES}"
run make-app refresh "${APP}"

echo ""
step "Done: ${APP} is up to date (KSA ${INSTALLED}, port $(kit_value version))."
info "To update the game itself, download the new Linux version from https://ksa.ahwoo.com and run ./update.sh again."
