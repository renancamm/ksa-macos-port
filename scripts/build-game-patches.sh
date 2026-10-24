#!/bin/bash
# src/game-patches/ -> deps/build/game-patches/: the game patches and their loader, with Harmony
#
# The patches (src/game-patches/patches/) are compiled against KSA's assemblies, references only, like the benchmark
# mod: they are built for the KSA version the port folder is made for, and skip what moved in another one. The loader
# (src/game-patches/loader/) references nothing of the game.
#
# Usage: scripts/build-game-patches.sh <folder with KSA's converted assemblies>
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-game-patches.sh <folder with KSA.dll>"
[ -f "${HARMONY_DLL}" ] || die "Harmony is missing from the build tree. Run ./build.sh."
WORK="${BUILD}/game-patches"
cd "${REPO}"

step "Building the game patches against KSA $(game_version "${KSA_DIR}") (Harmony ${HARMONY_VERSION})"
dotnet_build "${WORK}" "${REPO}/src/game-patches/loader/KsaPatchesLoader.csproj" "${WORK}/loader" ||
    die "the game patches' loader did not build."
dotnet_build "${WORK}" "${REPO}/src/game-patches/patches/KsaPatches.csproj" "${WORK}/patches" -p:KsaDir="${KSA_DIR}" \
    -p:HarmonyDll="${HARMONY_DLL}" || die "the game patches did not build (does this KSA version still match?)."
rm -rf "${WORK}/out"
mkdir -p "${WORK}/out"
cp "${WORK}/loader/KsaPatchesLoader.dll" "${WORK}/patches/KsaPatches.dll" "${HARMONY_DLL}" "${WORK}/out/"
