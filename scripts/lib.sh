# shellcheck shell=bash disable=SC2034  # the variables are the scripts'
# Shared by build.sh, release.sh and the scripts in scripts/. Source it; it defines no behaviour of its own.
#
# The build runs on the maintainer's Mac and needs the Command Line Tools, cmake, ninja and the .NET 10 SDK. It
# shares the port folder's helpers in src/player/port-folder/lib/common.sh (names, versions, messages).

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPTS="${REPO}/scripts"
source "${REPO}/src/player/port-folder/lib/common.sh"

# deps/build/: the CMake build tree (cmake/, with every download in cmake/_deps/), the app's libraries (natives/), the
# .NET projects' build (assemblies/, their output in assemblies/bin/) and the other pieces of the port folder
# (KSA_BUILD, set by release.sh for a fresh build). dist/: the port folder.
DEPS="${REPO}/deps"
BUILD="${KSA_BUILD:-${DEPS}/build}"
CMAKE_TREE="${BUILD}/cmake"
NATIVES="${BUILD}/natives"
ASSEMBLIES="${BUILD}/assemblies"
DIST="${REPO}/dist"
OUT="${DIST}/ksa-macos-port"

# run <step> [args...]: runs a step of the build, scripts/<step>.sh. (A step of the port folder runs from its lib/ in
# src/player/port-folder/, as LIB names it.)
run() {
    local name="$1"
    shift
    bash "${SCRIPTS}/${name}.sh" "$@"
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
    # shellcheck source=/dev/null  # written by cmake/deps-file.cmake
    source "${CMAKE_TREE}/deps.sh"
}

# dotnet_cli <args...>: dotnet as the build runs it: the SDK's state and the packages it restores in the build tree
dotnet_cli() {
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
        DOTNET_CLI_HOME="${ASSEMBLIES}/home" NUGET_PACKAGES="${ASSEMBLIES}/nuget" dotnet "$@"
}

# dotnet_sdk_version: the .NET SDK that builds the benchmark and the hooks, as global.json picks it for this
# repository
dotnet_sdk_version() {
    (cd "${REPO}" && dotnet --version)
}
