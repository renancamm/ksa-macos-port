#!/bin/bash
# Build the KSA Metal driver into deps/ksamtl/libvulkan.1.dylib.
# Needs: Xcode command line tools, Vulkan headers (Homebrew vulkan-headers), cmake + ninja for SPIRV-Cross.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/.." && pwd)"
OUT="$REPO/deps/ksamtl"
SPVC="$REPO/deps/SPIRV-Cross"
SPVC_PIN=300681bb
VULKAN_INCLUDE="${VULKAN_INCLUDE:-/opt/homebrew/include}"
GIT=/Library/Developer/CommandLineTools/usr/bin/git

if [ ! -f "$SPVC/build/libspirv-cross-msl.a" ]; then
  if [ ! -d "$SPVC" ]; then
    "$GIT" clone -q https://github.com/KhronosGroup/SPIRV-Cross.git "$SPVC"
  fi
  "$GIT" -C "$SPVC" checkout -q "$SPVC_PIN"
  cmake -S "$SPVC" -B "$SPVC/build" -G Ninja -DCMAKE_BUILD_TYPE=Release -DSPIRV_CROSS_CLI=OFF \
    -DSPIRV_CROSS_ENABLE_TESTS=OFF -DSPIRV_CROSS_ENABLE_HLSL=OFF -DSPIRV_CROSS_ENABLE_CPP=OFF \
    -DSPIRV_CROSS_ENABLE_REFLECT=OFF -DSPIRV_CROSS_ENABLE_C_API=OFF -DSPIRV_CROSS_ENABLE_UTIL=OFF > /dev/null
  ninja -C "$SPVC/build" > /dev/null
fi

mkdir -p "$OUT/obj"
OPT="${KSA_MTL_OPT:--O2}"
CXXFLAGS=(-std=c++17 -fobjc-arc $OPT -g -fvisibility=hidden -Wall -Wno-unused-parameter -Wno-unused-variable
  -Wno-unused-function -Wno-missing-braces -I"$VULKAN_INCLUDE" -I"$SPVC" -I"$HERE")
SRCS=(entry instance device format descriptor pipeline command swapchain internal accel)
PIDS=()
for s in "${SRCS[@]}"; do
  if [ ! -f "$OUT/obj/$s.o" ] || [ "$HERE/$s.mm" -nt "$OUT/obj/$s.o" ] || [ "$HERE/kmtl.h" -nt "$OUT/obj/$s.o" ]; then
    clang++ "${CXXFLAGS[@]}" -c "$HERE/$s.mm" -o "$OUT/obj/$s.o" &
    PIDS+=($!)
  fi
done
for p in ${PIDS[@]+"${PIDS[@]}"}; do wait "$p"; done
OBJS=(); for s in "${SRCS[@]}"; do OBJS+=("$OUT/obj/$s.o"); done
clang++ -shared -fobjc-arc -o "$OUT/libvulkan.1.dylib" "${OBJS[@]}" \
  "$SPVC/build/libspirv-cross-msl.a" "$SPVC/build/libspirv-cross-glsl.a" "$SPVC/build/libspirv-cross-core.a" \
  -Wl,-install_name,@loader_path/libvulkan.1.dylib -Wl,-exported_symbols_list,/dev/stdin \
  -framework Metal -framework QuartzCore -framework Foundation -framework CoreGraphics -framework ImageIO \
  -framework UniformTypeIdentifiers <<< "_vk*"
ln -sf libvulkan.1.dylib "$OUT/libvulkan.dylib"
codesign -f -s - "$OUT/libvulkan.1.dylib" 2>&1 | grep -v "replacing existing" || true
echo "built $OUT/libvulkan.1.dylib ($(nm -gU "$OUT/libvulkan.1.dylib" | grep -c ' _vk') vk exports)"
