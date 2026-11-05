# shellcheck shell=bash disable=SC2034  # the variables are the commands'
# Shared by the port's commands (port.sh, starmap.sh, benchmark.sh) and the build. Source it; it defines no behaviour of
# its own.
#
# Everything here runs on macOS's own bash 3.2 and base tools: the port folder needs nothing else installed. The
# commands' work is done by the port's program, lib/port-cli/KsaPort.Cli.dll (src/player/port-cli/ in the repository),
# which run_port_cli starts on the folder's .NET runtime. The folder is what ./build.sh makes (dist/ksa-macos-port/)
# and what a release contains:
#   port.sh, starmap.sh, benchmark.sh              the commands
#   lib/                                           this file, port.env, the port's program (port-cli/) with its
#                                                  shader edits, brutal.txt
#   payload/app/Contents/                          the app without the game: launcher, .NET runtime, libraries,
#                                                  hooks, benchmark, icon, licences
#   payload/starmap/                               StarMap, which starmap.sh adds to the app
#   VERSION                                        version=, ksa= (the KSA it was made for), commit=,
#                                                  dotnet_sdk= (the .NET SDK that built the benchmark)

LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PORT_FOLDER="$(cd "${LIB}/.." && pwd)"
PAYLOAD="${PORT_FOLDER}/payload"
SKELETON="${PAYLOAD}/app/Contents"

# The port's names: APP_NAME, BUNDLE_ID, RELEASES_REPO, MACOS_MIN
# shellcheck source=port.env
source "${LIB}/port.env"
DEFAULT_APP="${HOME}/Applications/${APP_NAME}.app"

step() { echo "==> $*"; }
info() { echo "    $*"; }
warn() { echo "    WARNING: $*" >&2; }
die()  { echo "Error: $*" >&2; exit 1; }

# run_port_cli <command> [args...]: the port's program, on this folder's .NET runtime
run_port_cli() {
    DOTNET_ROOT="${SKELETON}/MacOS" "${SKELETON}/MacOS/dotnet" "${LIB}/port-cli/KsaPort.Cli.dll" "$@"
}

# version_file <key>: a value from the folder's VERSION file (version, ksa, commit)
version_file() {
    sed -n "s/^$1=//p" "${PORT_FOLDER}/VERSION" 2> /dev/null
}

# require_port_folder: stops when this is not a built port folder (for example the scripts in the repository's
# src/player/port-folder/).
# A browser download marks every file as quarantined, and macOS then holds programs in it, like the .NET runtime the
# benchmark's report runs on, for a Gatekeeper check: the folder's own files lose that mark.
require_port_folder() {
    [ -f "${PORT_FOLDER}/VERSION" ] && [ -x "${SKELETON}/MacOS/dotnet" ] && [ -f "${LIB}/port-cli/KsaPort.Cli.dll" ] ||
        die "${PORT_FOLDER} is not a complete port folder. Download a release, or build one with ./build.sh."
    xattr -dr com.apple.quarantine "${PORT_FOLDER}" 2> /dev/null || true
}

# game_version <game dir>: KSA's version from KSA.dll (for example 2026.10.19.5638)
game_version() {
    LC_ALL=C grep -aoE '20[0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2}\.[0-9]+' "$1/KSA.dll" | head -1
}

# version_newer <a> <b>: true when dotted version a is newer than b
version_newer() {
    local IFS=. a b i
    read -r -a a <<< "$1"
    read -r -a b <<< "$2"
    for i in 0 1 2 3; do
        [ "${a[i]:-0}" -gt "${b[i]:-0}" ] && return 0
        [ "${a[i]:-0}" -lt "${b[i]:-0}" ] && return 1
    done
    return 1
}

# download_version <archive>: KSA's version from an archive's name (2026.10.19.5638 for ksa_linux_v2026.10.19.5638.tar.gz)
download_version() {
    basename "$1" | sed -nE 's/^ksa_linux_v([0-9]+(\.[0-9]+)+).*$/\1/p'
}

# newest_download [<version>]: the KSA archive in ~/Downloads with the newest version, newer than <version> when
# given; empty when there is none. Safari unpacks the .gz of a download, so a .tar counts too.
newest_download() {
    local f v newest="" best="${1:-0}"
    for f in "${HOME}"/Downloads/ksa_linux_v*.tar.gz "${HOME}"/Downloads/ksa_linux_v*.tar; do
        [ -f "${f}" ] || continue
        v="$(download_version "${f}")"
        if [ -n "${v}" ] && version_newer "${v}" "${best}"; then
            newest="${f}"
            best="${v}"
        fi
    done
    echo "${newest}"
}

# app_version <app>: the KSA version an app built by this port contains, empty for anything else
app_version() {
    local plist="$1/Contents/Info.plist"
    [ "$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "${plist}" 2> /dev/null)" = "${BUNDLE_ID}" ] || return 0
    /usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${plist}" 2> /dev/null
}

# check_ksa_match <KSA version>: warns when it differs from the KSA this port folder was made for
check_ksa_match() {
    local made_for
    made_for="$(version_file ksa)"
    [ -n "${made_for}" ] && [ "$1" != "${made_for}" ] || return 0
    if version_newer "$1" "${made_for}"; then
        warn "this version of the port was made for KSA ${made_for}, not ${1}. If something doesn't work, ./port.sh --update" \
            "may find a newer one."
    else
        warn "this version of the port was made for KSA ${made_for}, not ${1}. Download the current Linux version" \
            "of KSA from https://ksa.ahwoo.com."
    fi
}

check_mac() {
    [ "$(uname -m)" = "arm64" ] || die "this needs an Apple Silicon Mac."
    local major
    major="$(sw_vers -productVersion | cut -d. -f1)"
    [ "${major}" -ge "${MACOS_MIN%%.*}" ] || die "this needs macOS ${MACOS_MIN} or later (this Mac has $(sw_vers -productVersion))."
}
