#!/bin/bash
# The app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by lib/make-app.sh from the port folder.
#
# Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS:
#   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
#                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
#   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c) and the KTX shim (src/ktx-shim/ktx_shim.c)
#   DOTNET_STARTUP_HOOKS   KSA Metal Hooks' loader (src/hooks/loader/), which applies the player's port settings
#                          (port-settings.toml in the game's documents folder) and puts the game's output in
#                          console.log, before the game starts
#   KSA_PORT_LAUNCHER      this script, for the hooks to find the app and start the game again
# This script stays out of the documents folder: started from the Finder, macOS refuses it there (it is a shell, not
# the app), while it lets the game's process in.

CONTENTS="$(cd "$(dirname "$0")/.." && pwd)"
RES="${CONTENTS}/Resources"

# Started again by the hooks (Restart) while the game was still running: begin once it has quit
if [ -n "${KSA_PORT_WAIT}" ]; then
    while kill -0 "${KSA_PORT_WAIT}" 2> /dev/null; do
        sleep 0.2
    done
    unset KSA_PORT_WAIT
fi

fail() {
    echo "ERROR: $1"
    osascript -e 'on run argv' -e 'display alert "Kitten Space Agency" message (item 1 of argv) as critical' \
        -e 'end run' "$1" > /dev/null 2>&1
    exit 1
}
[ -x "${CONTENTS}/MacOS/dotnet" ] || fail "The .NET runtime is missing from the app. Run ./port.sh in the port folder (ksa-macos-port)."
[ -f "${RES}/KSA.dll" ] || fail "The game files are missing from the app. Convert the game again with ./port.sh."

export KSA_PORT_LAUNCHER="${CONTENTS}/MacOS/KSA"
export DOTNET_ROOT="${CONTENTS}/MacOS"
case ":${PATH}:" in *":${CONTENTS}/MacOS:"*) ;; *) export PATH="${CONTENTS}/MacOS:${PATH}" ;; esac
export DYLD_LIBRARY_PATH="${RES}"
export DYLD_INSERT_LIBRARIES="${RES}/libGlfwShim.dylib:${RES}/libKtxShim.dylib"
HOOK="${RES}/Hooks/KsaMetalHooksLoader.dll"
if [ -f "${HOOK}" ]; then
    case ":${DOTNET_STARTUP_HOOKS}:" in
        *":${HOOK}:"*) ;;
        *) export DOTNET_STARTUP_HOOKS="${HOOK}${DOTNET_STARTUP_HOOKS:+:${DOTNET_STARTUP_HOOKS}}" ;;
    esac
fi

# Drop the process serial number argument that older macOS versions pass to apps
ARGS=()
for arg in "$@"; do
    [[ "${arg}" == -psn_* ]] || ARGS+=("${arg}")
done

# With the StarMap mod loader in the app (starmap.sh), the game starts through it; KSA_STARMAP_DIR names another
# StarMap folder to use instead. StarMap reads its config and DLLs from the current folder and needs the game's full
# path, which changes when the app moves: the config is written at every start.
STARMAP="${KSA_STARMAP_DIR:-${RES}/StarMap}"
if [ -f "${STARMAP}/StarMap.dll" ]; then
    cd "${STARMAP}" || exit 1
    game="${RES//\\/\\\\}/KSA.dll"
    printf '{ "GameLocation": "%s", "RepositoryLocation": "", "GameArguments": [] }\n' "${game//\"/\\\"}" \
        > StarMapConfig.json
    exec "${CONTENTS}/MacOS/dotnet" StarMap.dll ${ARGS[@]+"${ARGS[@]}"}
fi

cd "${RES}" || exit 1
exec "${CONTENTS}/MacOS/dotnet" KSA.dll ${ARGS[@]+"${ARGS[@]}"}
