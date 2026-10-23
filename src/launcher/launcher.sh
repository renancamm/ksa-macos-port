#!/bin/bash
# The app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by lib/make-app.sh from the port folder.
#
# Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS:
#   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
#                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
#   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c)
# Output goes to console.log in the game's logs folder (~/Documents/My Games/Kitten Space Agency/logs, next
# to the game's own logs), or to the terminal when started from one. A StarMap instance
# (STARMAP_INSTANCE_PATH, the game's documents folder moved elsewhere) gets its own console.log.

CONTENTS="$(cd "$(dirname "$0")/.." && pwd)"
RES="${CONTENTS}/Resources"
LOG="${STARMAP_INSTANCE_PATH:-${HOME}/Documents/My Games/Kitten Space Agency}/logs/console.log"

if [ ! -t 1 ]; then
    mkdir -p "$(dirname "${LOG}")"
    exec > "${LOG}" 2>&1
fi

fail() {
    echo "ERROR: $1"
    osascript -e 'on run argv' -e 'display alert "Kitten Space Agency" message (item 1 of argv) as critical' \
        -e 'end run' "$1" > /dev/null 2>&1
    exit 1
}
[ -x "${CONTENTS}/MacOS/dotnet" ] || fail "The .NET runtime is missing from the app. Run ./port.sh in the port folder (ksa-macos-port)."
[ -f "${RES}/KSA.dll" ] || fail "The game files are missing from the app. Convert the game again with ./port.sh."

export DOTNET_ROOT="${CONTENTS}/MacOS"
export PATH="${CONTENTS}/MacOS:${PATH}"
export DYLD_LIBRARY_PATH="${RES}"
export DYLD_INSERT_LIBRARIES="${RES}/libGlfwShim.dylib"

# Drop the process serial number argument that older macOS versions pass to apps
ARGS=()
for arg in "$@"; do
    [[ "${arg}" == -psn_* ]] || ARGS+=("${arg}")
done

# With the StarMap mod loader in the app (starmap.sh), the game starts through it; KSA_STARMAP_DIR names another
# StarMap folder to use instead (the benchmark's, so that the app needs none). StarMap reads its config and DLLs
# from the current folder and needs the game's full path, which changes when the app moves: the config is written
# at every start.
STARMAP="${KSA_STARMAP_DIR:-${RES}/StarMap}"
if [ -f "${STARMAP}/StarMap.dll" ]; then
    echo "==> $(date): starting ${RES}/KSA.dll through StarMap in ${STARMAP}"
    cd "${STARMAP}" || exit 1
    game="${RES//\\/\\\\}/KSA.dll"
    printf '{ "GameLocation": "%s", "RepositoryLocation": "", "GameArguments": [] }\n' "${game//\"/\\\"}" \
        > StarMapConfig.json
    exec "${CONTENTS}/MacOS/dotnet" StarMap.dll ${ARGS[@]+"${ARGS[@]}"}
fi

echo "==> $(date): starting ${RES}/KSA.dll"
cd "${RES}" || exit 1
exec "${CONTENTS}/MacOS/dotnet" KSA.dll ${ARGS[@]+"${ARGS[@]}"}
