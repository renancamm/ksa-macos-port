#!/bin/bash
# SPIRV-Tools (with SPIRV-Headers) -> static libraries in deps/build/spirv-tools (linked into KSA Metal)
#
# The driver links KSA's ray tracing shaders into one compute kernel with SPIRV-Tools (src/ksa-metal/rtspirv.cpp).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building SPIRV-Tools"
HEADERS="${DEPS}/src/spirv-headers"
SRC="${DEPS}/src/spirv-tools"
BUILD="${DEPS}/build/spirv-tools"
fetch_git "${HEADERS}" "${SPIRV_HEADERS_SOURCE}"
fetch_git "${SRC}" "${SPIRV_TOOLS_SOURCE}"

cmake -S "${SRC}" -B "${BUILD}" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOS_MIN}" -DSPIRV-Headers_SOURCE_DIR="${HEADERS}" \
    -DSPIRV_SKIP_TESTS=ON -DSPIRV_SKIP_EXECUTABLES=ON -DSPIRV_WERROR=OFF > /dev/null
cmake --build "${BUILD}" -j "$(sysctl -n hw.ncpu)" --target SPIRV-Tools-static SPIRV-Tools-opt SPIRV-Tools-link > /dev/null

copy_licenses "${SRC}" spirv-tools
copy_licenses "${HEADERS}" spirv-headers
