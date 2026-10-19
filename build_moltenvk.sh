#!/bin/bash
# ==============================================================================
# Kitten Space Agency (KSA) - MoltenVK Builder for macOS Apple Silicon
#
# Builds MoltenVK from dttdrv's ray-query branch (MoltenVK PR #2771, with the
# SPIRV-Cross changes from PR #2663), pinned to a tested commit and unmodified.
#
# Why this branch instead of upstream MoltenVK:
#   KSA places ground clutter (Earth's grass) with Vulkan ray queries. Upstream
#   MoltenVK has no VK_KHR_ray_query, so KSA falls back to a software BVH walk in
#   its shaders, which drops the frame rate to 0 near grass and can hang the GPU.
#   With this branch and MVK_CONFIG_ENABLE_EXPERIMENTAL_RAY_TRACING=1 (set by
#   start_mac.command), KSA uses Metal ray queries instead.
#
# No patches are applied: the branch already allows the 10 descriptor sets KSA
# uses (kMaxArgumentBuffers = 16 in its SPIRV-Cross).
#
# Usage: ./build_moltenvk.sh [output_dir] [checkout_dir]
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
OUTPUT_DIR="${1:-${SCRIPT_DIR}/dist}"
MVK_DIR="${2:-${SCRIPT_DIR}/deps/MoltenVK}"

MVK_REPO="https://github.com/dttdrv/MoltenVK.git"
MVK_BRANCH="macgaming/ray-query-pr"
MVK_COMMIT="176926b4eb7b6bd70f46a59f277b4417dd235f67"

mkdir -p "${OUTPUT_DIR}" "$(dirname "${MVK_DIR}")"

if [ ! -d "${MVK_DIR}/.git" ]; then
    echo "==> Fetching ${MVK_REPO} (${MVK_BRANCH} @ ${MVK_COMMIT:0:7})..."
    git init -q "${MVK_DIR}"
    git -C "${MVK_DIR}" remote add origin "${MVK_REPO}"
    git -C "${MVK_DIR}" fetch -q --depth 1 origin "${MVK_COMMIT}"
    git -C "${MVK_DIR}" checkout -q FETCH_HEAD
fi

cd "${MVK_DIR}"

# Refuse to build anything but the pinned, unmodified commit
if [ "$(git rev-parse HEAD)" != "${MVK_COMMIT}" ]; then
    echo "Error: ${MVK_DIR} is at $(git rev-parse --short HEAD), expected ${MVK_COMMIT:0:7}."
    echo "       Delete ${MVK_DIR} and run this script again."
    exit 1
fi
if [ -n "$(git status --porcelain --untracked-files=no)" ]; then
    echo "Error: ${MVK_DIR} has local modifications. Delete it and run this script again."
    exit 1
fi

if [ ! -d "External/SPIRV-Cross" ]; then
    echo "==> Fetching and building MoltenVK's macOS dependencies (SPIRV-Cross from dttdrv's fork)..."
    ./fetchDependencies --macos
fi

echo "==> Building libMoltenVK.dylib..."
make macos

cp "Package/Release/MoltenVK/dynamic/dylib/macOS/libMoltenVK.dylib" "${OUTPUT_DIR}/libMoltenVK.dylib"
install_name_tool -id "@loader_path/libMoltenVK.dylib" "${OUTPUT_DIR}/libMoltenVK.dylib"
codesign -f -s - "${OUTPUT_DIR}/libMoltenVK.dylib"

echo "==> libMoltenVK.dylib (ray-query branch @ ${MVK_COMMIT:0:7}) built at: ${OUTPUT_DIR}/libMoltenVK.dylib"
