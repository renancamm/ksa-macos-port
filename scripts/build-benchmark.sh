#!/bin/bash
# src/benchmark/ -> deps/build/benchmark/: the benchmark's director and the report program
#
# The director (src/benchmark/director/), which KSA Metal Hooks start in the game, is compiled against KSA's
# assemblies, which are references only: the game loads them, the director doesn't ship them. So it is built for one
# KSA version, the one the port folder is made for. The report program (src/benchmark/report/) needs nothing from the
# game. Both are built with the .NET SDK that global.json picks.
#
# Usage: scripts/build-benchmark.sh <folder with KSA's converted assemblies>
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-benchmark.sh <folder with KSA.dll>"
WORK="${BUILD}/benchmark"
cd "${REPO}"

step "Building the benchmark's director against KSA $(game_version "${KSA_DIR}") (.NET SDK $(dotnet_sdk_version))"
dotnet_build "${WORK}" "${REPO}/src/benchmark/director/KsaMetalBenchmark.csproj" "${WORK}/director" -p:KsaDir="${KSA_DIR}" ||
    die "the benchmark's director did not build (does this KSA version still match?)."
dotnet_build "${WORK}" "${REPO}/src/benchmark/report/KsaMetalBenchmarkReport.csproj" "${WORK}/report" ||
    die "the report program did not build."
