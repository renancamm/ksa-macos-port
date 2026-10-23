# Shared by build.sh, release.sh and the scripts in scripts/. Source it; it defines no behaviour of its own.
#
# The build runs on the maintainer's Mac and needs the Command Line Tools and cmake. It shares the player-side
# helpers in src/port/lib/common.sh (names, versions, messages) and adds downloading, building and signing.

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPTS="${REPO}/scripts"
source "${REPO}/src/port/lib/common.sh"
PORT_LIB="${LIB}"

# deps/: pinned downloads and source checkouts, each checked against scripts/pins.sh, so release.sh can share them
# (KSA_DEPS). deps/build/: build trees and the pieces of the port folder (KSA_BUILD). dist/: the port folder.
DEPS="${KSA_DEPS:-${REPO}/deps}"
BUILD="${KSA_BUILD:-${DEPS}/build}"
DIST="${REPO}/dist"
OUT="${DIST}/ksa-macos-port"
NATIVES="${BUILD}/natives"      # every .dylib for the app, and licences/ of what is compiled into them

# Everything we compile targets MACOS_MIN explicitly, otherwise a binary inherits the build Mac's version
export MACOSX_DEPLOYMENT_TARGET="${MACOS_MIN}"

source "${SCRIPTS}/pins.sh"

# run <script name> [args...]: runs scripts/<name>.sh, or a step of the port folder (src/port/lib/<name>.sh)
run() {
    local name="$1"
    shift
    if [ -f "${SCRIPTS}/${name}.sh" ]; then
        bash "${SCRIPTS}/${name}.sh" "$@"
    else
        bash "${PORT_LIB}/${name}.sh" "$@"
    fi
}

# True when the Command Line Tools are installed (without them /usr/bin/git, clang, ... only offer to install them)
have_dev_tools() {
    xcode-select -p > /dev/null 2>&1
}

require_build_tools() {
    local missing=()
    have_dev_tools || missing+=("the Command Line Tools: xcode-select --install")
    command -v cmake > /dev/null || missing+=("cmake: brew install cmake (https://brew.sh)")
    if [ ${#missing[@]} -gt 0 ]; then
        echo "Error: building the port needs:" >&2
        local m
        for m in "${missing[@]}"; do
            echo "  - ${m}" >&2
        done
        exit 1
    fi
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

# copy_licenses <source checkout> <name>: its licence, notice and LICENSES/ files -> deps/build/natives/licenses/<name>/
copy_licenses() {
    local src="$1" out="${NATIVES}/licenses/$2" f
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

# dotnet_runtime_dir: where scripts/fetch-dotnet.sh puts the pinned runtime
dotnet_runtime_dir() {
    echo "${DEPS}/dotnet/${DOTNET_VERSION}"
}

# dotnet_sdk_dir: where scripts/fetch-dotnet-sdk.sh puts the pinned SDK
dotnet_sdk_dir() {
    echo "${DEPS}/dotnet-sdk/${DOTNET_SDK_VERSION}"
}
