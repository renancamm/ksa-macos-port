# Shared by build.sh, release.sh and the scripts in scripts/. Source it; it defines no behaviour of its own.
#
# The build runs on the maintainer's Mac and needs the Command Line Tools, cmake, ninja and the .NET 10 SDK. It
# shares the player-side helpers in src/port/lib/common.sh (names, versions, messages).

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPTS="${REPO}/scripts"
source "${REPO}/src/port/lib/common.sh"
PORT_LIB="${LIB}"

# deps/build/: the CMake build tree (cmake/, with every download in cmake/_deps/), the app's libraries (natives/) and
# the other pieces of the port folder (KSA_BUILD, set by release.sh for a fresh build). dist/: the port folder.
DEPS="${REPO}/deps"
BUILD="${KSA_BUILD:-${DEPS}/build}"
CMAKE_TREE="${BUILD}/cmake"
NATIVES="${BUILD}/natives"
DIST="${REPO}/dist"
OUT="${DIST}/ksa-macos-port"

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

# require_build_tools: stops with the install command of every missing tool
require_build_tools() {
    local missing=()
    # Without the Command Line Tools, /usr/bin/git, clang, ... only offer to install them
    xcode-select -p > /dev/null 2>&1 || missing+=("the Command Line Tools: xcode-select --install")
    command -v cmake > /dev/null || missing+=("cmake: brew install cmake (https://brew.sh)")
    command -v ninja > /dev/null || missing+=("ninja: brew install ninja")
    command -v dotnet > /dev/null && dotnet --list-sdks 2> /dev/null | grep -q '^10\.' ||
        missing+=("the .NET 10 SDK: brew install --cask dotnet-sdk")
    if [ ${#missing[@]} -gt 0 ]; then
        echo "Error: building the port needs:" >&2
        local m
        for m in "${missing[@]}"; do
            echo "  - ${m}" >&2
        done
        exit 1
    fi
}

# load_deps: where CMake put each download, and the versions the port folder records (cmake/dependencies.cmake)
load_deps() {
    [ -f "${CMAKE_TREE}/deps.sh" ] || die "the build tree has no deps.sh. Run ./build.sh."
    source "${CMAKE_TREE}/deps.sh"
}

# dotnet_sdk_version: the .NET SDK that builds the benchmark, as global.json picks it for this repository
dotnet_sdk_version() {
    (cd "${REPO}" && dotnet --version)
}
