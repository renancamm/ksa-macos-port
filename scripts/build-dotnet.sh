#!/bin/bash
# The port's .NET projects (ksa-macos-port.slnx) -> deps/build/assemblies/bin/: KSA Metal Hooks and their loader, the
# benchmark's director and report program, the port settings' generator, and the tests, which run here
#
# The hooks (src/hooks/) and the director (src/benchmark/director/) are compiled against KSA's assemblies, references
# only: they are built for the KSA version the port folder is made for, and the hooks skip what moved in another one.
# The loader, the report program and the tests reference nothing of the game. Each project's output is in a folder of
# its own there, by the project's name (src/Directory.Build.props); the SDK is the one global.json picks.
#
# Usage: scripts/build-dotnet.sh <folder with KSA's converted assemblies>
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-dotnet.sh <folder with KSA.dll>"
[ -f "${HARMONY_DLL}" ] || die "Harmony is missing from the build tree. Run ./build.sh."
WORK="${ASSEMBLIES}"
# The output starts anew; obj/ stays, so a second build only compiles what changed
rm -rf "${WORK:?}/bin"
mkdir -p "${WORK}"
cd "${REPO}"

step "Building the hooks and the benchmark against KSA $(game_version "${KSA_DIR}")" \
    "(.NET SDK $(dotnet_sdk_version), Harmony ${HARMONY_VERSION})"
# No PDB: the port folder doesn't ship them, and a DLL built with one records its path
dotnet_cli build "${REPO}/ksa-macos-port.slnx" -c Release -nologo -v quiet \
    -p:PortBuildDir="${WORK}" -p:DebugType=none -p:KsaDir="${KSA_DIR}" -p:HarmonyDll="${HARMONY_DLL}" -nodeReuse:false \
    > "${WORK}/build.log" 2>&1 ||
    { cat "${WORK}/build.log"; die "the .NET build failed (does this KSA version still match?)."; }

dotnet "${ASSEMBLIES}/bin/KsaMetalHooksTests/KsaMetalHooksTests.dll" "${REPO}" || die "the hooks' tests failed."
dotnet "${ASSEMBLIES}/bin/KsaMetalBenchmarkTests/KsaMetalBenchmarkTests.dll" || die "the benchmark's tests failed."
