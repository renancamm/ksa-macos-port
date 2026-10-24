#!/bin/bash
# The app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by lib/make-app.sh from the port folder.
#
# Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS:
#   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
#                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
#   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c)
#   DOTNET_STARTUP_HOOKS   the game patches (src/game-patches/), unless KSA_PATCHES=0
# The player's Mac settings, mac-settings.toml in the game's documents folder (a commented copy of
# Contents/Resources/mac-settings.toml is put there when there is none), reach KSA Metal and the game patches as
# environment variables; a variable already set when the game starts wins over the file.
# Output goes to console.log in the game's logs folder (~/Documents/My Games/Kitten Space Agency/logs, next
# to the game's own logs), or to the terminal when started from one. A StarMap instance
# (STARMAP_INSTANCE_PATH, the game's documents folder moved elsewhere) gets its own console.log.

CONTENTS="$(cd "$(dirname "$0")/.." && pwd)"
RES="${CONTENTS}/Resources"
DOCS="${STARMAP_INSTANCE_PATH:-${HOME}/Documents/My Games/Kitten Space Agency}"
LOG="${DOCS}/logs/console.log"

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
if [ "${KSA_PATCHES:-1}" != 0 ] && [ -f "${RES}/GamePatches/KsaPatchesLoader.dll" ]; then
    export DOTNET_STARTUP_HOOKS="${RES}/GamePatches/KsaPatchesLoader.dll${DOTNET_STARTUP_HOOKS:+:${DOTNET_STARTUP_HOOKS}}"
fi

SETTINGS="${DOCS}/mac-settings.toml"
if [ ! -f "${SETTINGS}" ] && [ -f "${RES}/mac-settings.toml" ]; then
    mkdir -p "${DOCS}" && cp "${RES}/mac-settings.toml" "${SETTINGS}" ||
        echo "[WARN] could not write ${SETTINGS}"
fi
CHANGED=()
if [ -f "${SETTINGS}" ]; then
    while IFS= read -r line || [ -n "${line}" ]; do
        line="${line%%#*}"
        [ -n "${line//[[:space:]]/}" ] || continue
        key="$(echo "${line%%=*}" | tr -d ' \t')"
        value="$(echo "${line#*=}" | tr -d ' \t"')"
        var="" setting="${value}"
        case "${key}=${value}" in
            render_scale=1 | render_scale=1.0 | upscaler=metalfx | retina=false | frame_interpolation=false | \
                cloud_shadows=normal) continue ;;
            render_scale=*)
                awk -v s="${value}" 'BEGIN { exit !(s ~ /^[0-9]*\.?[0-9]+$/ && s >= 0.5 && s < 1) }' || {
                    echo "[WARN] mac-settings.toml: render_scale must be from 0.5 to 1.0, not ${value}: ignored"
                    continue
                }
                var=KSA_RENDER_SCALE ;;
            upscaler=spatial | upscaler=none) var=KSA_UPSCALER ;;
            retina=true) var=KSA_MTL_RETINA setting=1 ;;
            frame_interpolation=true) var=KSA_MTL_FX_INTERPOLATE setting=1 ;;
            cloud_shadows=slow) var=KSA_CLOUD_SHADOWS ;;
            *) echo "[WARN] mac-settings.toml: unknown setting or value '${key} = ${value}': ignored"; continue ;;
        esac
        # A variable set when the game starts wins over the file
        if eval "[ -n \"\${${var}+x}\" ]"; then
            CHANGED+=("${key} = ${value} (but ${var}=$(eval "echo \"\${${var}}\"") is set)")
        else
            export "${var}=${setting}"
            CHANGED+=("${key} = ${value}")
        fi
    done < "${SETTINGS}"
fi
# Retina: the app must be marked NSHighResolutionCapable, which AppKit reads from Info.plist when the game starts
retina=false
[ "${KSA_MTL_RETINA:-0}" = 0 ] || retina=true
if [ "$(/usr/bin/plutil -extract NSHighResolutionCapable raw "${CONTENTS}/Info.plist" 2> /dev/null)" != "${retina}" ]; then
    /usr/bin/plutil -replace NSHighResolutionCapable -bool "${retina}" "${CONTENTS}/Info.plist" ||
        echo "[WARN] could not set NSHighResolutionCapable in Info.plist: retina = ${retina} may not take effect"
fi
echo "==> Mac settings: $( [ ${#CHANGED[@]} -gt 0 ] && (IFS=,; echo "${CHANGED[*]}" | sed 's/,/, /g') || echo defaults)"

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
