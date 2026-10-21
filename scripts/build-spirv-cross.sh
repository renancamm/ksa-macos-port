#!/bin/bash
# SPIRV-Cross -> static libraries in deps/build/spirv-cross (linked into the Metal driver)
#
# The driver translates KSA's SPIR-V shaders to Metal Shading Language with SPIRV-Cross.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building SPIRV-Cross"
SRC="${DEPS}/src/spirv-cross"
BUILD="${DEPS}/build/spirv-cross"
fetch_git "${SRC}" "${SPIRV_CROSS_SOURCE}"

cmake -S "${SRC}" -B "${BUILD}" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOS_MIN}" -DSPIRV_CROSS_CLI=OFF -DSPIRV_CROSS_ENABLE_TESTS=OFF \
    -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF -DSPIRV_CROSS_ENABLE_REFLECT=OFF \
    -DSPIRV_CROSS_ENABLE_C_API=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF > /dev/null
cmake --build "${BUILD}" -j "$(sysctl -n hw.ncpu)" --target spirv-cross-core spirv-cross-glsl spirv-cross-msl > /dev/null

copy_licenses "${SRC}" spirv-cross
