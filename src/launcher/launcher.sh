#!/bin/bash
# The app's executable (Kitten Space Agency.app/Contents/MacOS/KSA), installed by lib/make-app.sh from the port folder.
#
# Starts KSA (Contents/Resources/KSA.dll) with the .NET runtime in Contents/MacOS:
#   DYLD_LIBRARY_PATH      the game folder first, so every library, including KSA Metal as
#                          libvulkan.1.dylib, comes from the app and never from elsewhere on the Mac
#   DYLD_INSERT_LIBRARIES  the GLFW shim (src/glfw-shim/glfw_shim.c) and the KTX shim (src/ktx-shim/ktx_shim.c)
#   DOTNET_STARTUP_HOOKS   KSA Metal Hooks (src/hooks/), unless the port settings turn them off (then only for a
#                          benchmark run, KSA_BENCH_OUT, which the hooks start)
# The player's port settings, port-settings.toml in the game's documents folder (a commented copy of
# Contents/Resources/port-settings.toml is put there when there is none), reach KSA Metal, its shims and its hooks as
# environment variables: Contents/Resources/port-settings.txt says which setting is which variable. A variable already
# set when the game starts wins over the file. For the hooks, which show the settings in the game, the launcher also
# sets KSA_PORT_SETTINGS (the file), KSA_PORT_SET (the variables it set from the file), KSA_PORT_OUTSIDE (those that
# were set already) and KSA_PORT_LAUNCHER (this script, to start the game again).
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

trim() {
    local s="$1"
    s="${s#"${s%%[![:space:]]*}"}"
    echo "${s%"${s##*[![:space:]]}"}"
}

# The game started again from its settings (KSA Metal Hooks) inherits what this script set the last time: those
# variables came from the file, not from outside
for var in ${KSA_PORT_SET//,/ }; do
    unset "${var}"
done
unset KSA_PORT_SET KSA_PORT_OUTSIDE

# The table of the port settings, one setting per line (its header explains the columns)
KEYS=() TYPES=() DEFAULTS=() VARS=() NEEDS=()
TABLE="${RES}/port-settings.txt"
if [ -f "${TABLE}" ]; then
    while IFS='|' read -r key type default var needs _; do
        key="$(trim "${key}")"
        case "${key}" in "" | \#* | \[*) continue ;; esac
        KEYS+=("${key}")
        TYPES+=("$(trim "${type}")")
        DEFAULTS+=("$(trim "${default}")")
        VARS+=("$(trim "${var}")")
        NEEDS+=("$(trim "${needs}")")
    done < "${TABLE}"
fi
VALUES=("${DEFAULTS[@]}")
# index_of <key>: the setting's index in the table, or nothing
index_of() {
    local i
    for i in "${!KEYS[@]}"; do
        [ "${KEYS[i]}" = "$1" ] && echo "${i}" && return
    done
}
# valid <type> <value>: whether a value is one the type takes
valid() {
    local type="$1" value="$2" range
    case "${type}" in
        bool | available) [ "${value}" = true ] || [ "${value}" = false ] ;;
        text) true ;;
        choice:*) case "/${type#choice:}" in *"/${value}="*) true ;; *) false ;; esac ;;
        scale:* | number:* | int:*)
            range="${type#*:}"
            awk -v v="${value}" -v lo="${range%%:*}" -v hi="${range#*:}" -v int="${type%%:*}" 'BEGIN {
                ok = int == "int" ? v ~ /^[0-9]+$/ : v ~ /^[0-9]*\.?[0-9]+$/
                exit !(ok && v + 0 >= lo + 0 && v + 0 <= hi + 0) }' ;;
        *) false ;;
    esac
}

SETTINGS="${DOCS}/port-settings.toml"
if [ ! -f "${SETTINGS}" ] && [ -f "${RES}/port-settings.toml" ]; then
    mkdir -p "${DOCS}" && cp "${RES}/port-settings.toml" "${SETTINGS}" ||
        echo "[WARN] could not write ${SETTINGS}"
fi
if [ -f "${SETTINGS}" ]; then
    while IFS= read -r line || [ -n "${line}" ]; do
        line="${line%%#*}"
        [ -n "${line//[[:space:]]/}" ] || continue
        case "$(trim "${line}")" in \[*) continue ;; esac
        key="$(trim "${line%%=*}")"
        value="$(trim "${line#*=}")"
        value="${value#\"}"
        value="${value%\"}"
        i="$(index_of "${key}")"
        if [ -z "${i}" ] || [ "${key}" = "${line}" ]; then
            echo "[WARN] port-settings.toml: unknown setting '$(trim "${line}")': ignored"
        elif ! valid "${TYPES[i]}" "${value}"; then
            echo "[WARN] port-settings.toml: ${key} can't be '${value}' (${TYPES[i]}): ignored"
        else
            VALUES[i]="${value}"
        fi
    done < "${SETTINGS}"
fi

# Each setting's value once its needs are met (a setting only needs those before it in the table); unmet, its default
on() {
    case "$1" in "" | false | 0 | 0.0) false ;; *) true ;; esac
}
met() {
    local need j
    for need in ${1//,/ }; do
        case "${need}" in
            game:*) continue ;;
            *!=*) j="$(index_of "${need%%!=*}")"; [ "${EFFECTIVE[j]}" != "${need#*!=}" ] || return 1 ;;
            *=*) j="$(index_of "${need%%=*}")"; [ "${EFFECTIVE[j]}" = "${need#*=}" ] || return 1 ;;
            *) j="$(index_of "${need}")"; on "${EFFECTIVE[j]}" || return 1 ;;
        esac
    done
}
EFFECTIVE=()
CHANGED=() SET=() OUTSIDE=()
for i in "${!KEYS[@]}"; do
    EFFECTIVE[i]="${DEFAULTS[i]}"
    var="${VARS[i]%%=*}"
    [ -z "${var}" ] || [ -z "$(eval "echo \"\${${var}+x}\"")" ] || OUTSIDE+=("${var}")
    [ "${VALUES[i]}" != "${DEFAULTS[i]}" ] || continue
    if ! met "${NEEDS[i]}"; then
        CHANGED+=("${KEYS[i]} = ${VALUES[i]} (off: needs ${NEEDS[i]})")
        continue
    fi
    EFFECTIVE[i]="${VALUES[i]}"
    [ -n "${var}" ] || continue
    case "${TYPES[i]}" in
        bool | available)
            if [ "${VALUES[i]}" = false ]; then
                setting=0
            elif [ "${VARS[i]}" != "${var}" ]; then
                setting="${VARS[i]#*=}"
            else
                setting=1
            fi ;;
        text) setting="${VALUES[i]/#\~/${HOME}}" ;;
        *) setting="${VALUES[i]}" ;;
    esac
    if [ -n "$(eval "echo \"\${${var}+x}\"")" ]; then
        CHANGED+=("${KEYS[i]} = ${VALUES[i]} (but ${var}=$(eval "echo \"\${${var}}\"") is set)")
    else
        export "${var}=${setting}"
        SET+=("${var}")
        CHANGED+=("${KEYS[i]} = ${VALUES[i]}")
    fi
done
echo "==> port settings: $( [ ${#CHANGED[@]} -gt 0 ] && (IFS=,; echo "${CHANGED[*]}" | sed 's/,/, /g') || echo defaults)"
export KSA_PORT_SETTINGS="${SETTINGS}" KSA_PORT_LAUNCHER="${CONTENTS}/MacOS/KSA"
export KSA_PORT_SET="$(IFS=,; echo "${SET[*]}")" KSA_PORT_OUTSIDE="$(IFS=,; echo "${OUTSIDE[*]}")"

export DOTNET_ROOT="${CONTENTS}/MacOS"
case ":${PATH}:" in *":${CONTENTS}/MacOS:"*) ;; *) export PATH="${CONTENTS}/MacOS:${PATH}" ;; esac
export DYLD_LIBRARY_PATH="${RES}"
export DYLD_INSERT_LIBRARIES="${RES}/libGlfwShim.dylib:${RES}/libKtxShim.dylib"
HOOK="${RES}/Hooks/KsaMetalHooksLoader.dll"
if { [ "${KSA_HOOKS:-1}" != 0 ] || [ -n "${KSA_BENCH_OUT}" ]; } && [ -f "${HOOK}" ]; then
    case ":${DOTNET_STARTUP_HOOKS}:" in
        *":${HOOK}:"*) ;;
        *) export DOTNET_STARTUP_HOOKS="${HOOK}${DOTNET_STARTUP_HOOKS:+:${DOTNET_STARTUP_HOOKS}}" ;;
    esac
fi

# Retina: the app must be marked NSHighResolutionCapable, which AppKit reads from Info.plist when the game starts
retina=false
[ "${KSA_MTL_RETINA:-0}" = 0 ] || retina=true
if [ "$(/usr/bin/plutil -extract NSHighResolutionCapable raw "${CONTENTS}/Info.plist" 2> /dev/null)" != "${retina}" ]; then
    /usr/bin/plutil -replace NSHighResolutionCapable -bool "${retina}" "${CONTENTS}/Info.plist" ||
        echo "[WARN] could not set NSHighResolutionCapable in Info.plist: retina may not take effect"
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
