# Shared by port.sh, update.sh and the scripts in scripts/. Source it; it defines no behaviour of its own.
#
# Everything here runs on macOS's own bash 3.2 and base tools. The Command Line Tools (clang, git, python3,
# otool) are needed only to build the natives from source; their /usr/bin entries are stubs without them.

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPTS="${REPO}/scripts"
DIST="${REPO}/dist"     # native libraries for the app; owned by the build and replaced as a whole
DEPS="${REPO}/deps"     # downloads, source checkouts and build trees (git-ignored)

APP_NAME="Kitten Space Agency"
BUNDLE_ID="io.github.ksa-macos-port.ksa"
DEFAULT_APP="${HOME}/Applications/${APP_NAME}.app"

# Oldest macOS the app supports: KSA Metal uses macOS 26 API (MTLSamplerDescriptor.lodBias).
# Everything we compile targets it explicitly, otherwise a binary inherits the build Mac's version.
MACOS_MIN="26.0"
export MACOSX_DEPLOYMENT_TARGET="${MACOS_MIN}"

# The libraries this repository builds itself, in build order (scripts/build-*.sh), and what they put in dist/.
BUILD_STEPS=(build-glfw-shim build-spirv-cross build-spirv-tools build-ksa-metal)
BUILT_FILES=(libGlfwShim.dylib libvulkan.1.dylib libvulkan.dylib
             licenses/spirv-cross licenses/spirv-tools licenses/spirv-headers licenses/vulkan-headers)

source "${SCRIPTS}/pins.sh"

step() { echo "==> $*"; }
info() { echo "    $*"; }
warn() { echo "    WARNING: $*" >&2; }
die()  { echo "Error: $*" >&2; exit 1; }

# run <script name> [args...]: runs scripts/<name>.sh
run() {
    local name="$1"
    shift
    bash "${SCRIPTS}/${name}.sh" "$@"
}

# True when the Command Line Tools are installed (without them /usr/bin/git, clang, ... only offer to install them)
have_dev_tools() {
    xcode-select -p > /dev/null 2>&1
}

# check_sha <file> <256|512> <expected hex>
check_sha() {
    [ "$(shasum -a "$2" "$1" | cut -d' ' -f1)" = "$3" ]
}

# download <url> <file> <256|512> <expected hex>: downloads unless the file is already there with that checksum
download() {
    local url="$1" file="$2" algo="$3" sum="$4"
    if [ -f "${file}" ] && check_sha "${file}" "${algo}" "${sum}"; then
        return 0
    fi
    mkdir -p "$(dirname "${file}")"
    curl -fsSL --retry 3 -o "${file}.part" "${url}" || die "could not download ${url}"
    if ! check_sha "${file}.part" "${algo}" "${sum}"; then
        rm -f "${file}.part"
        die "${url} does not match its pinned SHA-${algo} (scripts/pins.sh)"
    fi
    mv "${file}.part" "${file}"
}

# fetch_git <dir> "<url> <commit>": a checkout of exactly that commit, refusing local changes
fetch_git() {
    local dir="$1" url="${2% *}" commit="${2#* }"
    if [ ! -d "${dir}/.git" ]; then
        rm -rf "${dir}"
        git init -q "${dir}"
        git -C "${dir}" remote add origin "${url}"
    fi
    if [ "$(git -C "${dir}" rev-parse -q --verify HEAD 2> /dev/null)" != "${commit}" ]; then
        info "Fetching ${url} @ ${commit:0:10}"
        git -C "${dir}" fetch -q --depth 1 origin "${commit}"
        git -C "${dir}" -c advice.detachedHead=false checkout -q --force FETCH_HEAD
    fi
    [ -z "$(git -C "${dir}" status --porcelain)" ] || die "${dir} has local changes. Delete it and run again."
}

# copy_licenses <source checkout> <name>: its licence, notice and LICENSES/ files -> dist/licenses/<name>/
copy_licenses() {
    local src="$1" out="${DIST}/licenses/$2" f
    mkdir -p "${out}"
    for f in "${src}"/LICENSE* "${src}"/NOTICE* "${src}"/COPYING* "${src}"/LICENSES; do
        [ -e "${f}" ] && cp -R "${f}" "${out}/"
    done
    [ -n "$(ls "${out}")" ] || die "no licence file found in ${src}"
}

# sign <file...>: ad-hoc signature (needed after linking or install_name_tool, or macOS kills the process)
sign() {
    codesign -f -s - "$@" 2>&1 | grep -v "replacing existing signature" || true
}

# Identifies the sources of the natives: SHA-256 over every file that goes into them. Names the prebuilt release.
natives_stamp() {
    (
        cd "${REPO}"
        ls scripts/lib.sh scripts/pins.sh scripts/fetch-brutal-natives.sh scripts/build-*.sh scripts/check-natives.sh \
            src/*/*.c src/*/*.mm src/*/*.cpp src/*/*.h | LC_ALL=C sort | while read -r f; do shasum -a 256 "${f}"; done
    ) | shasum -a 256 | cut -d' ' -f1
}

# prepare_natives [--from-source]: makes dist/ match the current sources. RocketWerkz's libraries always come
# from nuget.org; ours come from the matching GitHub release when there is one, otherwise they are built here.
prepare_natives() {
    local stamp
    stamp="$(natives_stamp)"
    if [ -f "${DIST}/.stamp" ] && [ "$(cat "${DIST}/.stamp")" = "${stamp}" ]; then
        info "Native libraries are up to date (dist/)."
        return 0
    fi
    rm -rf "${DIST}"
    mkdir -p "${DIST}"
    run fetch-brutal-natives

    local prebuilt=1
    if [ "$1" = "--from-source" ]; then
        prebuilt=0
    else
        run fetch-prebuilt "${stamp}" || {
            [ $? = 2 ] || exit 1
            prebuilt=0
        }
    fi
    if [ "${prebuilt}" = 0 ]; then
        require_build_tools
        local s
        for s in "${BUILD_STEPS[@]}"; do
            run "${s}"
        done
        run check-natives
    fi
    echo "${stamp}" > "${DIST}/.stamp"
}

require_build_tools() {
    local missing=()
    have_dev_tools || missing+=("the Command Line Tools: xcode-select --install")
    command -v cmake > /dev/null || missing+=("cmake: brew install cmake (https://brew.sh)")
    if [ ${#missing[@]} -gt 0 ]; then
        echo "Error: no prebuilt libraries match these sources, so they have to be built on this Mac. That needs:" >&2
        local m
        for m in "${missing[@]}"; do
            echo "  - ${m}" >&2
        done
        exit 1
    fi
}

# dotnet_runtime_dir: where scripts/fetch-dotnet.sh puts the pinned runtime
dotnet_runtime_dir() {
    echo "${DEPS}/dotnet/${DOTNET_VERSION}"
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

check_mac() {
    [ "$(uname -m)" = "arm64" ] || die "this needs an Apple Silicon Mac."
    local major
    major="$(sw_vers -productVersion | cut -d. -f1)"
    [ "${major}" -ge "${MACOS_MIN%%.*}" ] || die "this needs macOS ${MACOS_MIN} or later (this Mac has $(sw_vers -productVersion))."
}
