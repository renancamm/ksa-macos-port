#!/bin/bash
# src/ksa-metal/ -> dist/libvulkan.1.dylib (+ libvulkan.dylib)
#
# KSA Metal (README.md, "KSA Metal") implements the Vulkan calls KSA makes on top of Metal. It takes the place of
# the Vulkan loader: Brutal, VulkanEx and GLFW find their Vulkan functions in it, and VMA gets them from Brutal.
# Needs the static libraries from scripts/build-spirv-cross.sh and scripts/build-spirv-tools.sh.
# KSA_MTL_OPT overrides the optimization flag (default -O2).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Building KSA Metal"
VULKAN_HEADERS="${DEPS}/src/vulkan-headers"
SPVC="${DEPS}/src/spirv-cross"
SPVC_LIB="${DEPS}/build/spirv-cross"
SPVT="${DEPS}/src/spirv-tools"
SPVT_LIB="${DEPS}/build/spirv-tools/source"
OBJ="${DEPS}/build/ksa-metal"
fetch_git "${VULKAN_HEADERS}" "${VULKAN_HEADERS_SOURCE}"
[ -f "${SPVC_LIB}/libspirv-cross-msl.a" ] || die "run scripts/build-spirv-cross.sh first."
[ -f "${SPVT_LIB}/link/libSPIRV-Tools-link.a" ] || die "run scripts/build-spirv-tools.sh first."

OPT="${KSA_MTL_OPT:--O2}"
COMMON=(-arch arm64 -mmacosx-version-min="${MACOS_MIN}" -std=c++17 "${OPT}" -g -fvisibility=hidden -Wall
    -I"${VULKAN_HEADERS}/include" -I"${SPVC}" -I"${SPVT}/include" -I"${REPO}/src/ksa-metal")
OBJCXX=(-fobjc-arc -Wno-unused-parameter -Wno-unused-variable -Wno-unused-function -Wno-missing-braces)
SOURCES=(entry instance device format descriptor pipeline command swapchain internal accel profile shadercache
    rtpipeline rtspirv tessspirv)

# Objects are reused while their source, the shared headers and the flags stay the same
mkdir -p "${OBJ}"
FLAGS="${COMMON[*]} ${OBJCXX[*]}"
if [ "$(cat "${OBJ}/flags" 2> /dev/null)" != "${FLAGS}" ]; then
    rm -f "${OBJ}"/*.o
    echo "${FLAGS}" > "${OBJ}/flags"
fi
PIDS=()
OBJECTS=()
for s in "${SOURCES[@]}"; do
    if [ -f "${REPO}/src/ksa-metal/${s}.mm" ]; then
        src="${REPO}/src/ksa-metal/${s}.mm"
        extra=("${OBJCXX[@]}")
    else
        src="${REPO}/src/ksa-metal/${s}.cpp"
        extra=()
    fi
    o="${OBJ}/${s}.o"
    OBJECTS+=("${o}")
    if [ ! -f "${o}" ] || [ "${src}" -nt "${o}" ] || [ "${REPO}/src/ksa-metal/kmtl.h" -nt "${o}" ] ||
        [ "${REPO}/src/ksa-metal/rtspirv.h" -nt "${o}" ] || [ "${REPO}/src/ksa-metal/spvmodule.h" -nt "${o}" ] ||
        [ "${REPO}/src/ksa-metal/tessspirv.h" -nt "${o}" ]; then
        clang++ "${COMMON[@]}" ${extra[@]+"${extra[@]}"} -c "${src}" -o "${o}" &
        PIDS+=($!)
    fi
done
for p in ${PIDS[@]+"${PIDS[@]}"}; do
    wait "${p}"
done

clang++ -shared -fobjc-arc -arch arm64 -mmacosx-version-min="${MACOS_MIN}" -o "${DIST}/libvulkan.1.dylib" \
    "${OBJECTS[@]}" \
    "${SPVC_LIB}/libspirv-cross-msl.a" "${SPVC_LIB}/libspirv-cross-glsl.a" "${SPVC_LIB}/libspirv-cross-core.a" \
    "${SPVT_LIB}/link/libSPIRV-Tools-link.a" "${SPVT_LIB}/opt/libSPIRV-Tools-opt.a" "${SPVT_LIB}/libSPIRV-Tools.a" \
    -Wl,-install_name,@loader_path/libvulkan.1.dylib -Wl,-exported_symbols_list,/dev/stdin \
    -framework Metal -framework QuartzCore -framework Foundation -framework CoreGraphics -framework ImageIO \
    -framework UniformTypeIdentifiers <<< $'_vk*\n_ksaMtl*'
ln -sf libvulkan.1.dylib "${DIST}/libvulkan.dylib"
sign "${DIST}/libvulkan.1.dylib"
info "libvulkan.1.dylib: $(nm -gU "${DIST}/libvulkan.1.dylib" | grep -c ' _vk') Vulkan functions"

copy_licenses "${VULKAN_HEADERS}" vulkan-headers
