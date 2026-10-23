# Shared by the port's commands and the steps in lib/. Source it; it defines no behaviour of its own.
#
# Everything here runs on macOS's own bash 3.2 and base tools: the port folder needs nothing else installed.
# The folder is what ./build.sh makes (dist/ksa-macos-port/) and what a release contains:
#   port.sh, starmap.sh, benchmark.sh              the commands
#   lib/                                           the steps they run, this file, brutal.txt
#   payload/app/Contents/                          the app without the game: launcher, .NET runtime, libraries,
#                                                  icon, licences
#   payload/starmap/, payload/benchmark/           the extras
#   VERSION                                        version=, ksa= (the KSA it was made for), commit=,
#                                                  dotnet_sdk= (the .NET SDK that built the benchmark)

LIB="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
KIT="$(cd "${LIB}/.." && pwd)"
PAYLOAD="${KIT}/payload"
SKELETON="${PAYLOAD}/app/Contents"

APP_NAME="Kitten Space Agency"
BUNDLE_ID="io.github.ksa-macos-port.ksa"
DEFAULT_APP="${HOME}/Applications/${APP_NAME}.app"
# Where releases are published (port.sh --update)
RELEASES_REPO="renancamm/ksa-macos-port"

# Oldest macOS the app supports: KSA Metal uses macOS 26 API (MTLSamplerDescriptor.lodBias).
MACOS_MIN="26.0"

step() { echo "==> $*"; }
info() { echo "    $*"; }
warn() { echo "    WARNING: $*" >&2; }
die()  { echo "Error: $*" >&2; exit 1; }

# run <step name> [args...]: runs lib/<name>.sh
run() {
    local name="$1"
    shift
    bash "${LIB}/${name}.sh" "$@"
}

# kit_value <key>: a value from the folder's VERSION file (version, ksa, commit)
kit_value() {
    sed -n "s/^$1=//p" "${KIT}/VERSION" 2> /dev/null
}

# require_kit: stops when this is not a built port folder (for example the scripts in the repository's src/port/).
# A browser download marks every file as quarantined, and macOS then holds programs in it, like the .NET runtime the
# benchmark's report runs on, for a Gatekeeper check: the folder's own files lose that mark.
require_kit() {
    [ -f "${KIT}/VERSION" ] && [ -x "${SKELETON}/MacOS/dotnet" ] ||
        die "${KIT} is not a complete port folder. Download a release, or build one with ./build.sh."
    xattr -dr com.apple.quarantine "${KIT}" 2> /dev/null || true
}

# game_version <game dir>: KSA's version from KSA.dll (for example 2026.10.19.5638)
game_version() {
    LC_ALL=C grep -aoE '20[0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2}\.[0-9]+' "$1/KSA.dll" | head -1
}

# version_newer <a> <b>: true when dotted version a is newer than b
version_newer() {
    local IFS=.
    local a=($1) b=($2) i
    for i in 0 1 2 3; do
        [ "${a[i]:-0}" -gt "${b[i]:-0}" ] && return 0
        [ "${a[i]:-0}" -lt "${b[i]:-0}" ] && return 1
    done
    return 1
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
    made_for="$(kit_value ksa)"
    [ -n "${made_for}" ] && [ "$1" != "${made_for}" ] || return 0
    if version_newer "$1" "${made_for}"; then
        warn "this version of the port was made for KSA ${made_for}, not ${1}. If something doesn't work, ./port.sh --update" \
            "may find a newer one."
    else
        warn "this version of the port was made for KSA ${made_for}, not ${1}. Download the current Linux version" \
            "of KSA from https://ksa.ahwoo.com."
    fi
}

# install_starmap <folder>: a copy of the port folder's StarMap there (the launcher starts the game through the app's
# Contents/Resources/StarMap, or through the folder KSA_STARMAP_DIR names)
install_starmap() {
    local target="$1"
    rm -rf "${target}"
    ditto "${PAYLOAD}/starmap" "${target}"
    xattr -cr "${target}" 2> /dev/null || true
}

check_mac() {
    [ "$(uname -m)" = "arm64" ] || die "this needs an Apple Silicon Mac."
    local major
    major="$(sw_vers -productVersion | cut -d. -f1)"
    [ "${major}" -ge "${MACOS_MIN%%.*}" ] || die "this needs macOS ${MACOS_MIN} or later (this Mac has $(sw_vers -productVersion))."
}
