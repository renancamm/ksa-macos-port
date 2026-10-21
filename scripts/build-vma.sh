#!/bin/bash
# Vulkan Memory Allocator -> dist/libvma.dylib
#
# KSA allocates its GPU memory through VMA (Brutal.Vulkan.Vma). RocketWerkz's build of it doesn't work with the
# BRUTAL version KSA ships (see src/vma.cpp), so this builds upstream VMA, linked against the Metal driver's
# libvulkan.1.dylib (scripts/build-driver.sh).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building Vulkan Memory Allocator"
SRC="${DEPS}/src/vma"
VULKAN_HEADERS="${DEPS}/src/vulkan-headers"
fetch_git "${SRC}" "${VMA_SOURCE}"
fetch_git "${VULKAN_HEADERS}" "${VULKAN_HEADERS_SOURCE}"
[ -f "${DIST}/libvulkan.1.dylib" ] || die "run scripts/build-driver.sh first."

clang++ -O2 -DNDEBUG -std=c++17 -shared -fPIC -arch arm64 -mmacosx-version-min="${MACOS_MIN}" -w \
    -I"${SRC}/include" -I"${VULKAN_HEADERS}/include" -L"${DIST}" -lvulkan.1 \
    -Wl,-install_name,@loader_path/libvma.dylib \
    -o "${DIST}/libvma.dylib" "${REPO}/src/vma.cpp"
sign "${DIST}/libvma.dylib"

copy_licenses "${SRC}" vma
