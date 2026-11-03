#!/bin/bash
# The port's .NET projects (ksa-macos-port.slnx) -> deps/build/assemblies/bin/: the porter, KSA Metal Hooks and their
# loader, the benchmark's director and report program, the shared library, the settings' generator and the tests, which
# run here
#
# The hooks (src/game/hooks/) and the director (src/game/benchmark/) are compiled against KSA's assemblies, references
# only: they are built for the KSA version the port folder is made for, and the hooks skip what moved in another one.
# The porter, the shared library, the loader, the report program and the tests reference nothing of the game. With
# --convert, the assemblies are an archive's or a folder's copy: the porter, built first, converts them
# (convert-dotnet). Each project's output is in a folder of its own there, by the project's name (src/Directory.Build.props); the SDK is the
# one global.json picks.
#
# Usage: scripts/build-dotnet.sh <folder with KSA's assemblies> [--convert]
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-dotnet.sh <folder with KSA.dll> [--convert]"
[ -f "${HARMONY_DLL}" ] || die "Harmony is missing from the build tree. Run ./build.sh."
WORK="${ASSEMBLIES}"
# The output starts anew, but the build's tools', which the native build made and keeps track of; obj/ stays, so a
# second build only compiles what changed
mkdir -p "${WORK}/bin"
find "${WORK}/bin" -mindepth 1 -maxdepth 1 ! -name KsaMetalEntries ! -name KsaPortSettingsGenerator -exec rm -rf {} +
cd "${REPO}"

step "Building the .NET projects against KSA $(game_version "${KSA_DIR}")" \
    "(.NET SDK $(dotnet_sdk_version), Harmony ${HARMONY_VERSION})"
# No PDB: the port folder doesn't ship them, and a DLL built with one records its path
BUILD_OPTIONS=(-c Release -nologo -v quiet -p:PortBuildDir="${WORK}" -p:DebugType=none -nodeReuse:false)
if [ "${2:-}" = --convert ]; then
    dotnet_cli build "${REPO}/src/player/porter/KsaPorter.csproj" "${BUILD_OPTIONS[@]}" > "${WORK}/build.log" 2>&1 ||
        { cat "${WORK}/build.log"; die "the porter didn't build."; }
    KSA_PORT_FOLDER="${OUT}" dotnet "${WORK}/bin/KsaPorter/KsaPorter.dll" convert-dotnet "${KSA_DIR}" "${DOTNET_RUNTIME}" \
        > /dev/null || die "KSA's assemblies could not be converted."
fi
dotnet_cli build "${REPO}/ksa-macos-port.slnx" "${BUILD_OPTIONS[@]}" -p:KsaDir="${KSA_DIR}" -p:HarmonyDll="${HARMONY_DLL}" \
    > "${WORK}/build.log" 2>&1 ||
    { cat "${WORK}/build.log"; die "the .NET build failed (does this KSA version still match?)."; }

dotnet "${ASSEMBLIES}/bin/KsaPorterTests/KsaPorterTests.dll" "${REPO}" || die "the porter's tests failed."
dotnet "${ASSEMBLIES}/bin/KsaPort.Common.Tests/KsaPort.Common.Tests.dll" "${REPO}" || die "the port settings' tests failed."
dotnet "${ASSEMBLIES}/bin/KsaMetalBenchmarkTests/KsaMetalBenchmarkTests.dll" || die "the benchmark's tests failed."
