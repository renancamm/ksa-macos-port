#!/bin/bash
# KSA Metal's entry table (src/ksa-metal/entries/): builds its generator, then makes src/ksa-metal/setup/entries.inc again
# from Vulkan's registry, which CMake's configure downloaded (Vulkan-Headers), and the driver's sources. A release (any
# version but dev) only checks: the table it was tagged with must be what they make.
#
# Usage: scripts/build-entries.sh <version>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

MODE=generate
[ "${1:-dev}" = dev ] || MODE=check
REGISTRY="${CMAKE_TREE}/_deps/vulkan_headers-src/registry/vk.xml"
[ -f "${REGISTRY}" ] || die "Vulkan's registry is missing from the build tree (${REGISTRY}). Run ./build.sh."
mkdir -p "${ASSEMBLIES}"
dotnet_cli build "${REPO}/src/ksa-metal/entries/KsaMetalEntries.csproj" -c Release -nologo -v quiet \
    -p:PortBuildDir="${ASSEMBLIES}" -p:DebugType=none -nodeReuse:false > "${ASSEMBLIES}/entries-build.log" 2>&1 ||
    { cat "${ASSEMBLIES}/entries-build.log"; die "the entry table's generator didn't build."; }
dotnet "${ASSEMBLIES}/bin/KsaMetalEntries/KsaMetalEntries.dll" "${MODE}" "${REGISTRY}" "${REPO}/src/ksa-metal" ||
    die "KSA Metal's entry table is not what the registry and the sources make."
