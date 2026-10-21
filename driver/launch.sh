#!/bin/bash
# Start the installed game on the KSA Metal driver instead of MoltenVK, with the player's own settings.
# Usage: driver/launch.sh [game arguments]   (KSA_APP overrides the app path; KSA_MTL_* variables pass through)
# Tessellation is masked off by the game (the driver does not report it); everything else uses settings.toml.
REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="${KSA_APP:-$HOME/Applications/Kitten Space Agency.app}"
RES="$APP/Contents/Resources"
[ -f "$REPO/deps/ksamtl/libvulkan.1.dylib" ] || { echo "build the driver first: driver/build.sh"; exit 1; }
[ -f "$RES/KSA.dll" ] || { echo "game not found at $APP (set KSA_APP)"; exit 1; }
export PATH="$APP/Contents/MacOS:$PATH" DOTNET_ROOT="$APP/Contents/MacOS"
# Our libvulkan.1.dylib first: Brutal (via libVulkanEx), VMA and GLFW all resolve Vulkan from it
export DYLD_LIBRARY_PATH="$REPO/deps/ksamtl:$RES"
[ -f "$RES/libGlfwShim.dylib" ] && export DYLD_INSERT_LIBRARIES="$RES/libGlfwShim.dylib"
cd "$RES" || exit 1
exec dotnet KSA.dll "$@"
