#!/bin/bash
# Upstream GLFW 3.5.1 -> dist/libglfw.3.dylib (+ libglfw.dylib, the name Brutal.Glfw loads)
#
# KSA opens its window and reads input through GLFW. This is upstream GLFW, unmodified, built as a shared
# library. RocketWerkz's own build is not used (see scripts/fetch-brutal-natives.sh).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building GLFW"
SRC="${DEPS}/src/glfw"
BUILD="${DEPS}/build/glfw"
fetch_git "${SRC}" "${GLFW_SOURCE}"

cmake -S "${SRC}" -B "${BUILD}" -DCMAKE_BUILD_TYPE=Release -DCMAKE_OSX_ARCHITECTURES=arm64 \
    -DCMAKE_OSX_DEPLOYMENT_TARGET="${MACOS_MIN}" -DBUILD_SHARED_LIBS=ON \
    -DGLFW_BUILD_EXAMPLES=OFF -DGLFW_BUILD_TESTS=OFF -DGLFW_BUILD_DOCS=OFF > /dev/null
cmake --build "${BUILD}" -j "$(sysctl -n hw.ncpu)" > /dev/null

cp -L "${BUILD}/src/libglfw.3.dylib" "${DIST}/libglfw.3.dylib"
install_name_tool -id @loader_path/libglfw.3.dylib "${DIST}/libglfw.3.dylib"
sign "${DIST}/libglfw.3.dylib"
ln -sf libglfw.3.dylib "${DIST}/libglfw.dylib"

copy_licenses "${SRC}" glfw
