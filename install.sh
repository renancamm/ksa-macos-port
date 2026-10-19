#!/bin/bash
# ==============================================================================
# Kitten Space Agency (KSA) - One-Command macOS Installer & Updater
#
# Turns the Linux release of KSA into "Kitten Space Agency.app":
#   1. checks the prerequisites before doing any work
#   2. builds the native dylibs (build_all.sh) and MoltenVK (build_moltenvk.sh)
#      into dist/ if they are not built yet
#   3. extracts the Linux archive
#   4. ports it to macOS (port_game.sh)
#   5. builds the app (make_app.sh); an app from an earlier run is replaced and
#      the old one goes to the Trash, so the same command installs game updates
#   On a first install it also writes a settings.toml with anti-aliasing disabled.
#
# Usage:
#   ./install.sh [--rebuild] /path/to/ksa_linux_vX.tar.gz [/path/to/App.app]
#   ./install.sh [--rebuild] /path/to/extracted_linux_folder [/path/to/App.app]
#
#   --rebuild   rebuild dist/ first (after a git pull that changed the native code)
#   An extracted folder is ported in place and moved into the app.
#   Default app: ~/Applications/Kitten Space Agency.app
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
DIST_DIR="${SCRIPT_DIR}/dist"

REBUILD=0
if [ "$1" = "--rebuild" ]; then
    REBUILD=1
    shift
fi
SOURCE="${1%/}"
APP="${2:-${HOME}/Applications/Kitten Space Agency.app}"

usage() {
    echo "Usage: ./install.sh [--rebuild] /path/to/ksa_linux_vX.tar.gz [/path/to/App.app]"
    echo "       ./install.sh [--rebuild] /path/to/extracted_linux_folder [/path/to/App.app]"
}

if [ -z "${SOURCE}" ]; then
    usage
    exit 1
fi
if [ -d "${SOURCE}" ]; then
    if [ ! -f "${SOURCE}/KSA.dll" ]; then
        echo "Error: ${SOURCE} does not contain KSA.dll."
        exit 1
    fi
elif [ ! -f "${SOURCE}" ]; then
    echo "Error: ${SOURCE} not found."
    usage
    exit 1
fi

# ------------------------------------------------------------------------------
# 1. Prerequisites: check everything first, so a long build never fails halfway
# ------------------------------------------------------------------------------
echo "==> [1/5] Checking prerequisites..."
MISSING=()

[ "$(uname -m)" = "arm64" ] || MISSING+=("an Apple Silicon Mac (this is $(uname -m))")

HELPER_LIBS=(libstb libVulkanEx libvma libgli libimgui libimplot libktx_real libKtx libGlfwShim
             libglfw.3 libvulkan.1 libshaderc_shared.1 libSPIRV-Tools-shared)
NEED_HELPERS=${REBUILD}
for lib in "${HELPER_LIBS[@]}"; do
    [ -f "${DIST_DIR}/${lib}.dylib" ] || NEED_HELPERS=1
done
NEED_MOLTENVK=${REBUILD}
[ -f "${DIST_DIR}/libMoltenVK.dylib" ] || NEED_MOLTENVK=1

if [ "${NEED_HELPERS}" = 1 ] || [ "${NEED_MOLTENVK}" = 1 ]; then
    for cmd in clang clang++ git cmake make install_name_tool codesign python3; do
        command -v "$cmd" &> /dev/null || MISSING+=("'$cmd' (Xcode Command Line Tools: xcode-select --install; cmake: brew install cmake)")
    done
fi
if [ "${NEED_HELPERS}" = 1 ]; then
    for f in lib/libglfw.3.dylib lib/libvulkan.1.dylib lib/libshaderc_shared.1.dylib \
             lib/libSPIRV-Tools-shared.dylib include/vulkan/vulkan.h include/GLFW/glfw3.h include/glm/glm.hpp; do
        if [ ! -e "/opt/homebrew/${f}" ]; then
            MISSING+=("/opt/homebrew/${f} (brew install cmake glfw vulkan-headers vulkan-loader shaderc spirv-tools glm)")
        fi
    done
fi
if [ "${NEED_MOLTENVK}" = 1 ] && ! xcodebuild -version &> /dev/null; then
    MISSING+=("Xcode.app, selected with: sudo xcode-select -s /Applications/Xcode.app (needed once to build MoltenVK)")
fi

if ! dotnet --list-runtimes 2>/dev/null | grep -q "^Microsoft.NETCore.App 10\."; then
    MISSING+=(".NET 10 runtime (brew install --cask dotnet-sdk)")
fi

FMOD_FOUND=0
for FMOD_API in \
    "/Volumes/FMOD Programmers API Mac/FMOD Programmers API/api" \
    "/Applications/FMOD Programmers API/api" \
    "/Applications/FMOD SoundSystem/FMOD Studio API Mac/api"; do
    [ -f "${FMOD_API}/core/lib/libfmod.dylib" ] && FMOD_FOUND=1
done
if [ "${FMOD_FOUND}" = 0 ] && { [ ! -d "${SOURCE}" ] || [ ! -f "${SOURCE}/libfmodstudio.dylib" ]; }; then
    MISSING+=("FMOD Engine 2.03.xx for macOS: download it from https://www.fmod.com/download and open the .dmg so it mounts at '/Volumes/FMOD Programmers API Mac'")
fi

if [ ${#MISSING[@]} -gt 0 ]; then
    echo "Error: missing prerequisites:"
    for m in "${MISSING[@]}"; do
        echo "  - ${m}"
    done
    exit 1
fi
echo "    All prerequisites found."

# ------------------------------------------------------------------------------
# 2. Native libraries & MoltenVK (only what is missing, or everything with --rebuild)
# ------------------------------------------------------------------------------
echo "==> [2/5] Building native libraries..."
if [ "${REBUILD}" = 1 ]; then
    rm -rf "${DIST_DIR}"
fi
if [ "${NEED_HELPERS}" = 1 ]; then
    bash "${SCRIPT_DIR}/build_all.sh" "${DIST_DIR}"
else
    echo "    Helper dylibs already built in dist/ (use --rebuild to rebuild them)."
fi
if [ "${NEED_MOLTENVK}" = 1 ]; then
    bash "${SCRIPT_DIR}/build_moltenvk.sh" "${DIST_DIR}"
else
    echo "    libMoltenVK.dylib already built in dist/."
fi

# ------------------------------------------------------------------------------
# 3. Extract the archive next to the app (same volume, so make_app.sh's move is instant)
# ------------------------------------------------------------------------------
STAGE=""
if [ -d "${SOURCE}" ]; then
    echo "==> [3/5] Using extracted folder ${SOURCE} (it will be moved into the app)."
    GAME_DIR="$(cd "${SOURCE}" && pwd)"
else
    echo "==> [3/5] Extracting $(basename "${SOURCE}")..."
    mkdir -p "$(dirname "${APP}")"
    STAGE="$(mktemp -d "$(dirname "${APP}")/.ksa-install.XXXXXX")"
    trap '[ -n "${STAGE}" ] && echo "Error: install failed. Partly ported files are in ${STAGE} - delete that folder before retrying."' ERR
    # Keep the archive's name (e.g. ksa_linux_v2026.10.13.5602) for the folder
    GAME_DIR="${STAGE}/$(basename "${SOURCE}" | sed -E 's/\.(tar\.gz|tgz|tar\.xz|tar\.bz2|tar)$//')"
    mkdir -p "${GAME_DIR}"
    tar -xf "${SOURCE}" -C "${GAME_DIR}"
    if [ ! -f "${GAME_DIR}/KSA.dll" ]; then
        # Archive with a single top-level folder: use that folder
        INNER="$(dirname "$(find "${GAME_DIR}" -maxdepth 2 -name KSA.dll | head -1)")"
        if [ "${INNER}" = "." ]; then
            echo "Error: no KSA.dll found in $(basename "${SOURCE}"). Is this the Linux release of KSA?"
            rm -rf "${STAGE}"
            exit 1
        fi
        GAME_DIR="${INNER}"
    fi
fi

# ------------------------------------------------------------------------------
# 4. Port to macOS
# ------------------------------------------------------------------------------
echo "==> [4/5] Porting the game folder..."
KSA_INSTALLER=1 bash "${SCRIPT_DIR}/port_game.sh" "${GAME_DIR}" "${DIST_DIR}"

# ------------------------------------------------------------------------------
# 5. Build (or update) the app
# ------------------------------------------------------------------------------
echo "==> [5/5] Building the app..."
KSA_INSTALLER=1 bash "${SCRIPT_DIR}/make_app.sh" "${GAME_DIR}" "${APP}"

if [ -n "${STAGE}" ]; then
    trap - ERR
    rm -rf "${STAGE}"
fi

# First install only: start with anti-aliasing off (the game defaults to MSAA x2).
# The game fills every key missing from settings.toml with its default and then
# rewrites the full file, so this one key is enough. An existing file is left alone,
# so updates keep the player's settings.
SETTINGS="${HOME}/Documents/My Games/Kitten Space Agency/settings.toml"
if [ ! -f "${SETTINGS}" ]; then
    mkdir -p "$(dirname "${SETTINGS}")"
    printf '[display]\nantialiasing = "Disabled"\n' > "${SETTINGS}"
    echo "    Created ${SETTINGS} with anti-aliasing disabled."
fi

echo ""
echo "==> Done! Open \"$(basename "${APP}" .app)\" from Finder or Launchpad, or run:"
echo "    open \"${APP}\""
