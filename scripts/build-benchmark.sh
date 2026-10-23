#!/bin/bash
# src/benchmark/ -> deps/build/benchmark/: the benchmark mod and the report program
#
# The mod (src/benchmark/mod/) is compiled against KSA's assemblies and StarMap's API, which are references only:
# the game loads them, the mod doesn't ship them. So it is built for one KSA version, the one the port folder is made
# for. The report program (src/benchmark/report/) needs nothing from the game. Both are built with the .NET SDK that
# global.json picks.
#
# Usage: scripts/build-benchmark.sh <folder with KSA's converted assemblies>
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

KSA_DIR="${1%/}"
[ -f "${KSA_DIR}/KSA.dll" ] || die "usage: scripts/build-benchmark.sh <folder with KSA.dll>"
WORK="${BUILD}/benchmark"
# The SDK is chosen by the global.json of the folder dotnet runs in
cd "${REPO}"

# dotnet_build <project> <output folder> [msbuild arguments...]
dotnet_build() {
    local project="$1" out="$2"
    shift 2
    rm -rf "${out}"
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
        DOTNET_CLI_HOME="${WORK}/home" NUGET_PACKAGES="${WORK}/nuget" \
        dotnet build "${project}" -c Release -nologo -v quiet -nodeReuse:false -o "${out}" \
        -p:BaseIntermediateOutputPath="${WORK}/obj/$(basename "${project}" .csproj)/" "$@" \
        > "${WORK}/build.log" 2>&1 || { cat "${WORK}/build.log"; return 1; }
}

step "Building the benchmark mod against KSA $(game_version "${KSA_DIR}") (.NET SDK $(dotnet_sdk_version))"
mkdir -p "${WORK}"
dotnet_build "${REPO}/src/benchmark/mod/KsaMetalBenchmark.csproj" "${WORK}/mod" -p:KsaDir="${KSA_DIR}" \
    -p:StarMapDir="${STARMAP}" || die "the benchmark mod did not build (does this KSA version still match?)."
dotnet_build "${REPO}/src/benchmark/report/KsaMetalBenchmarkReport.csproj" "${WORK}/report" ||
    die "the report program did not build."
