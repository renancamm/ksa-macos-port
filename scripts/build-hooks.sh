#!/bin/bash
# src/hooks/ -> deps/build/hooks/: KSA Metal Hooks and their loader, with Harmony
#
# The hooks (src/hooks/) are compiled against KSA's assemblies and the benchmark's director, references only: they
# are built for the KSA version the port folder is made for, and skip what moved in another one. The loader
# (src/hooks/loader/) references nothing of the game.
#
# Usage: scripts/build-hooks.sh <folder with KSA's converted assemblies>
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-hooks.sh <folder with KSA.dll>"
[ -f "${HARMONY_DLL}" ] || die "Harmony is missing from the build tree. Run ./build.sh."
BENCHMARK_DLL="${BUILD}/benchmark/director/KsaMetalBenchmark.dll"
[ -f "${BENCHMARK_DLL}" ] || die "the benchmark's director is not built. Run ./build.sh."
WORK="${BUILD}/hooks"
cd "${REPO}"

step "Building KSA Metal Hooks against KSA $(game_version "${KSA_DIR}") (Harmony ${HARMONY_VERSION})"
dotnet_build "${WORK}" "${REPO}/src/hooks/loader/KsaMetalHooksLoader.csproj" "${WORK}/loader" ||
    die "the hooks' loader did not build."
dotnet_build "${WORK}" "${REPO}/src/hooks/KsaMetalHooks.csproj" "${WORK}/hooks" -p:KsaDir="${KSA_DIR}" \
    -p:HarmonyDll="${HARMONY_DLL}" -p:BenchmarkDll="${BENCHMARK_DLL}" ||
    die "the hooks did not build (does this KSA version still match?)."
rm -rf "${WORK}/out"
mkdir -p "${WORK}/out"
cp "${WORK}/loader/KsaMetalHooksLoader.dll" "${WORK}/hooks/KsaMetalHooks.dll" "${HARMONY_DLL}" "${WORK}/out/"
