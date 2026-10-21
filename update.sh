#!/bin/bash
# Updates Kitten Space Agency.app: puts in the new game version when you have downloaded one, otherwise applies
# this folder's version of the port to the app. Your saves and settings are not touched.
#
# Usage: ./update.sh [options] [ksa_linux_vX.tar.gz]
#   With an archive, or when ~/Downloads has a ksa_linux_v*.tar.gz newer than the installed game: converts that
#   version and replaces the app (./port.sh). Otherwise applies the port's files to the installed app.
#   It never checks online for new KSA versions: download them from https://ksa.ahwoo.com yourself.
#
# Options:
#   --pull            first update this folder with git pull (needs git, from the Command Line Tools)
#   --app <path>      the app to update (default: ~/Applications/Kitten Space Agency.app)
#   --from-source     build the port's own libraries on this Mac instead of downloading them
set -eo pipefail
source "$(dirname "$0")/scripts/lib.sh"

usage() {
    sed -n '2,13p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

ARGS=()
APP="${DEFAULT_APP}"
FROM_SOURCE=""
PULL=0
ARCHIVE=""
while [ $# -gt 0 ]; do
    # Everything but --pull, for the restart after a pull
    [ "$1" = --pull ] || ARGS+=("$1")
    case "$1" in
        --app) APP="${2%/}"; ARGS+=("$2"); shift ;;
        --from-source) FROM_SOURCE="--from-source" ;;
        --pull) PULL=1 ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) ARCHIVE="$1" ;;
    esac
    shift
done

check_mac

# 1. With --pull, the port itself. The new version of this script may differ, so it starts again after a pull.
if [ "${PULL}" = 1 ]; then
    [ -d "${REPO}/.git" ] || die "--pull needs a git clone of the port. For a ZIP download, download it again."
    have_dev_tools || die "git needs the Command Line Tools: xcode-select --install"
    step "Updating the port (git pull)"
    before="$(git -C "${REPO}" rev-parse HEAD)"
    git -C "${REPO}" pull --ff-only || die "git pull failed."
    if [ "$(git -C "${REPO}" rev-parse HEAD)" != "${before}" ]; then
        git -C "${REPO}" log --oneline "${before}..HEAD" | sed 's/^/    /'
        exec "${REPO}/update.sh" ${ARGS[@]+"${ARGS[@]}"}
    fi
fi

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
    exec "${REPO}/port.sh" --app "${APP}" ${FROM_SOURCE} "${ARCHIVE}"
fi

# 3. Same game version: refresh the port's files in the installed app
step "Updating the port's files in ${APP} (KSA ${INSTALLED})"
prepare_natives ${FROM_SOURCE}
run fetch-dotnet
RES="${APP}/Contents/Resources"
run convert-cleanup "${RES}"
run convert-dotnet "${RES}" "$(dotnet_runtime_dir)"
run convert-shaders "${RES}"
run make-app refresh "${APP}"

echo ""
step "Done: ${APP} is up to date (KSA ${INSTALLED})."
info "To update the game itself, download the new Linux version from https://ksa.ahwoo.com and run ./update.sh again."
