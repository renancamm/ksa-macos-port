#!/bin/bash
# src/glfw_shim.c -> dist/libGlfwShim.dylib
#
# The launcher loads the shim with DYLD_INSERT_LIBRARIES. It replaces a few GLFW and CoreGraphics calls to fix
# resolution lists, Fullscreen resolution, window positions and raw mouse errors (see the comment at the top of
# src/glfw_shim.c). It links RocketWerkz's GLFW from scripts/fetch-brutal-natives.sh, with the headers of the
# fork commit that library is built from.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building the GLFW shim"
SRC="${DEPS}/src/glfw-rocketwerkz"
fetch_git "${SRC}" "${GLFW_SOURCE}"
[ -f "${DIST}/libglfw.3.dylib" ] || die "run scripts/fetch-brutal-natives.sh first."

clang -O2 -DNDEBUG -shared -fPIC -arch arm64 -mmacosx-version-min="${MACOS_MIN}" -Wall \
    -I"${SRC}/include" -L"${DIST}" -lglfw.3 -framework CoreGraphics -framework CoreFoundation \
    -Wl,-install_name,@loader_path/libGlfwShim.dylib -Wl,-rpath,@loader_path \
    -o "${DIST}/libGlfwShim.dylib" "${REPO}/src/glfw_shim.c"
sign "${DIST}/libGlfwShim.dylib"
