#!/bin/bash
# Builds the port folder, dist/ksa-macos-port/: everything the port needs except the game. Its commands work as in a
# release (dist/ksa-macos-port/port.sh, ...); ./release.sh zips it.
#
# Usage: ./build.sh [options]
#   Needs the Command Line Tools, cmake, ninja and the .NET 10 SDK. CMake builds everything (CMakeLists.txt), with
#   every download pinned in cmake/dependencies.cmake, installs the port folder and runs the tests; everything goes to
#   deps/build/, and a second build only compiles what changed.
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
# into deps/build/ksa/, and the build converts them there (the porter's convert-dotnet)
KSA_DIR="${BUILD}/ksa"
rm -rf "${KSA_DIR}"
mkdir -p "${KSA_DIR}"
if [ -f "${KSA_SOURCE}/Contents/Resources/KSA.dll" ]; then
    KSA_DIR="$(CDPATH='' cd -- "${KSA_SOURCE}/Contents/Resources" && pwd)"  # absolute: MSBuild reads a relative one from the project
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

# CMake builds everything (CMakeLists.txt): its output goes to a log, shown when a step fails
mkdir -p "${CMAKE_TREE}"
LOG="${CMAKE_TREE}/build.log"
DEBUG_MAP=ON
[ "${VERSION}" = dev ] || DEBUG_MAP=OFF
step "[1/4] Configure (CMake; the downloads of cmake/dependencies.cmake)"
cmake --preset default -B "${CMAKE_TREE}" -DKSA_NATIVES_DIR="${NATIVES}" -DKSA_ASSEMBLIES_DIR="${ASSEMBLIES}" \
    -DKSA_DIR="${KSA_DIR}" -DPORT_VERSION="${VERSION}" -DPORT_COMMIT="${COMMIT}" -DPORT_FOLDER="${OUT}" \
    -DKSA_MTL_OPT="${KSA_MTL_OPT:--O2}" -DKSA_MTL_DEBUG_MAP="${DEBUG_MAP}" > "${LOG}" 2>&1 ||
    { tail -40 "${LOG}"; die "CMake's configure failed (${LOG})."; }

step "[2/4] Build: the native libraries, the .NET projects, the icon"
cmake --build "${CMAKE_TREE}" >> "${LOG}" 2>&1 ||
    { grep -v xcrun_db "${LOG}" | tail -40
      ! grep -q 'error CS' "${LOG}" ||
          info "Does this KSA version still match? The hooks and the benchmark are built against its code."
      die "the build failed (${LOG})."; }
# What the settings' checks found (cmake/port-folder.cmake)
grep -h -E '^    The port settings:' "${LOG}" | tail -1 || true
info "CMake's output in ${LOG#"${REPO}/"}"
# For editors: compile_commands.json at the repository's root, where clangd and VS Code look for it
ln -sfn "${CMAKE_TREE}/compile_commands.json" "${REPO}/compile_commands.json"

step "[3/4] The port folder"
rm -rf "${OUT}"
cmake --install "${CMAKE_TREE}" --prefix "${OUT}" >> "${LOG}" 2>&1 ||
    { tail -40 "${LOG}"; die "the port folder could not be assembled (${LOG})."; }

step "[4/4] Tests (ctest), but the shader corpus's"
TESTS="${CMAKE_TREE}/tests.log"
ctest --test-dir "${CMAKE_TREE}" -LE corpus --output-on-failure > "${TESTS}" 2>&1 ||
    { tail -40 "${TESTS}"; die "tests failed (${TESTS#"${REPO}/"})."; }
info "The tests: passed."
grep -h -E '^    (The |All )' "${CMAKE_TREE}/Testing/Temporary/LastTest.log" || true

echo ""
step "Done: ${OUT#"${REPO}/"} (port ${VERSION}, KSA ${KSA_VERSION})"
if [ "${INSTALL}" = 1 ]; then
    "${OUT}/port.sh" --app "${APP}"
else
    info "Make or update the app with it: dist/ksa-macos-port/port.sh"
fi
