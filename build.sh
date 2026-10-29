#!/bin/bash
# Builds the port folder, dist/ksa-macos-port/: everything the port needs except the game. Its commands work as in a
# release (dist/ksa-macos-port/port.sh, ...); ./release.sh zips it.
#
# Usage: ./build.sh [options]
#   Needs the Command Line Tools, cmake, ninja and the .NET 10 SDK. The native libraries are built by CMake
#   (CMakeLists.txt), with every download pinned in cmake/dependencies.cmake; everything goes to deps/build/, and a
#   second build only compiles what changed.
#
# Options:
#   --ksa <archive | folder | app>  the KSA the benchmark and the hooks are built against and the folder
#                                   is made for (default: the installed app, otherwise the newest ksa_linux_v*.tar.gz
#                                   or .tar in ~/Downloads)
#   --install                       then update the installed app with it (port.sh --app)
#   --app <path>                    the app for --install (default: ~/Applications/Kitten Space Agency.app)
#   --version <v>, --commit <id>    what VERSION says (default: dev, and the checkout's commit); set by release.sh
set -eo pipefail
source "$(dirname "$0")/scripts/lib.sh"

usage() {
    sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

KSA_SOURCE=""
INSTALL=0
APP="${DEFAULT_APP}"
VERSION="dev"
COMMIT=""
while [ $# -gt 0 ]; do
    case "$1" in
        --ksa) KSA_SOURCE="${2%/}"; shift ;;
        --install) INSTALL=1 ;;
        --app) APP="${2%/}"; shift ;;
        --version) VERSION="$2"; shift ;;
        --commit) COMMIT="$2"; shift ;;
        -h | --help) usage ;;
        *) echo "Unknown option: $1"; usage 1 ;;
    esac
    shift
done

check_mac
require_build_tools
[ -n "${COMMIT}" ] || COMMIT="$(git -C "${REPO}" describe --always --dirty 2> /dev/null || echo unknown)"
if [ -z "${KSA_SOURCE}" ]; then
    if [ -n "$(app_version "${APP}")" ]; then
        KSA_SOURCE="${APP}"
    else
        KSA_SOURCE="$(newest_download)"
        [ -n "${KSA_SOURCE}" ] || die "no KSA to build against: no app at ${APP} and no ksa_linux_v*.tar.gz (or .tar) in ~/Downloads. Pass --ksa."
    fi
fi

# KSA's assemblies, for the benchmark and the hooks: an app's are converted already; an archive's or a folder's are copied
# into deps/build/ksa/ and converted there (lib/convert-dotnet.sh, once the port's scripts are in place)
KSA_DIR="${BUILD}/ksa"
CONVERT=1
rm -rf "${KSA_DIR}"
mkdir -p "${KSA_DIR}"
if [ -f "${KSA_SOURCE}/Contents/Resources/KSA.dll" ]; then
    KSA_DIR="$(CDPATH='' cd -- "${KSA_SOURCE}/Contents/Resources" && pwd)"  # absolute: MSBuild reads a relative one from the project
    CONVERT=0
elif [ -f "${KSA_SOURCE}/KSA.dll" ]; then
    cp "${KSA_SOURCE}"/*.dll "${KSA_DIR}/"
elif [ -f "${KSA_SOURCE}" ]; then
    tar -xf "${KSA_SOURCE}" -C "${KSA_DIR}" --include '*.dll'
    found="$(find "${KSA_DIR}" -maxdepth 2 -name KSA.dll | head -1)"
    [ -n "${found}" ] || die "no KSA.dll in ${KSA_SOURCE}. Is this the Linux version of KSA?"
    KSA_DIR="$(dirname "${found}")"
else
    die "${KSA_SOURCE} is neither a KSA archive, a KSA folder nor an app made by this port."
fi
KSA_VERSION="$(game_version "${KSA_DIR}")"
step "Building the port ${VERSION} (${COMMIT}) for KSA ${KSA_VERSION}"

# The native libraries, and the downloads the other steps use. CMake's output goes to a log, shown when it fails.
step "[1/6] Native libraries (CMake)"
mkdir -p "${CMAKE_TREE}"
LOG="${CMAKE_TREE}/build.log"
DEBUG_MAP=ON
[ "${VERSION}" = dev ] || DEBUG_MAP=OFF
{ cmake --preset default -B "${CMAKE_TREE}" -DKSA_NATIVES_DIR="${NATIVES}" -DKSA_MTL_OPT="${KSA_MTL_OPT:--O2}" \
    -DKSA_MTL_DEBUG_MAP="${DEBUG_MAP}" &&
    cmake --build "${CMAKE_TREE}"; } > "${LOG}" 2>&1 || { grep -v xcrun_db "${LOG}" | tail -40; die "the native build failed (${LOG})."; }
info "Libraries in ${NATIVES#"${REPO}/"}, CMake's output in ${LOG#"${REPO}/"}"
# For editors: compile_commands.json at the repository's root, where clangd and VS Code look for it
ln -sfn "${CMAKE_TREE}/compile_commands.json" "${REPO}/compile_commands.json"

step "[2/6] The port's scripts"
run make-kit scripts "${VERSION}" "${KSA_VERSION}" "${COMMIT}"

step "[3/6] Icon"
run build-icon

step "[4/6] Benchmark"
load_deps
[ "${CONVERT}" = 0 ] || bash "${OUT}/lib/convert-dotnet.sh" "${KSA_DIR}" "${DOTNET_RUNTIME}" > /dev/null
run build-benchmark "${KSA_DIR}"

step "[5/6] Hooks"
run build-hooks "${KSA_DIR}"

step "[6/6] The port folder"
run make-kit payload
run check-kit

echo ""
step "Done: ${OUT#"${REPO}/"} (port ${VERSION}, KSA ${KSA_VERSION})"
if [ "${INSTALL}" = 1 ]; then
    "${OUT}/port.sh" --app "${APP}"
else
    info "Make or update the app with it: dist/ksa-macos-port/port.sh"
fi
