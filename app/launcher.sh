#!/bin/bash
# The app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by scripts/make-app.sh.
#
# Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS:
#   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
#                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
#   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c)
# Output goes to ~/Library/Logs/Kitten Space Agency.log, or to the terminal when started from one.
# The game's own logs, settings and saves are in ~/Documents/My Games/Kitten Space Agency.

CONTENTS="$(cd "$(dirname "$0")/.." && pwd)"
RES="${CONTENTS}/Resources"
LOG="${HOME}/Library/Logs/Kitten Space Agency.log"

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
[ -x "${CONTENTS}/MacOS/dotnet" ] || fail "The .NET runtime is missing from the app. Run ./update.sh in the ksa-macos-port folder."
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

echo "==> $(date): starting ${RES}/KSA.dll"
cd "${RES}" || exit 1
exec "${CONTENTS}/MacOS/dotnet" KSA.dll ${ARGS[@]+"${ARGS[@]}"}
