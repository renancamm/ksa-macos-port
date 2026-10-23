#!/bin/bash
# Measures how KSA runs on this Mac: plays a fixed tour of the game in one session and writes a report to share.
#
# Usage: ./benchmark.sh [options] [section...]
#        ./benchmark.sh compare <run folder> <run folder>
#   With no sections, plays all of them. The game takes over the screen: leave the mouse and keyboard alone until it
#   is done. The game runs from its own documents folder, with a copy of your settings: your files stay untouched.
#   Exits with 1 when the run failed (the game crashed or hung, a step failed, errors in its log).
#   compare prints how the second run differs from the first: settings, numbers part by part, and screenshots (exits
#   with 1 when screenshots differ).
#
# Options:
#   --fast            skip the way between screenshots: jump to each one, measure the view standing there, take it
#   --no-shots        take no screenshots
#   --hold <shot>     go to that screenshot's place (e.g. earth4 or launch_pad) fast and stay there, the game paused,
#                     until you quit it
#   --set <key=value> change one setting of settings.toml for the run (again for more), on top of the preset
#   --compare <run>   compare with that run folder at the end (and fail when screenshots differ)
#   --app <path>      the app to measure (default: ~/Applications/Kitten Space Agency.app)
#   --preset <name>   low or high: fixed settings in a 1280x800 window, the same on every Mac (default: yours)
#   --system <name>   sol (the solar system) or earth (Earth and the Moon only; skips the other worlds) (default: sol)
#   --explore         start the benchmark's game paused, to fly the free camera around and capture places for the
#                     tour (@key lines, for benchmark.txt) with benchkey [name] in its console; quit it when done
#
# Needs StarMap in the app (./starmap.sh; it is added when missing). The benchmark mod in payload/benchmark/ is built
# for the KSA version this port folder was made for.
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

BENCH="${PAYLOAD}/benchmark"
SCRIPT="${BENCH}/benchmark.txt"

# Presets: fixed settings, the same on every Mac, so reports compare. Without one, your own settings are used.
# Both run in a 1280x800 window without V-Sync (frame times are then the game's, not the display's).
PRESET_COMMON=('mode="Windowed"' width=1280 height=800 vsync=false fpslimit=240)
PRESET_LOW=('antialiasing="Disabled"' 'cloudUpscale="Low"' 'exhaustUpscale="Low"' ambientOcclusion=false
    terrainTessellation=false groundClutter=false 'kittenFurQuality="Low"' plumeTrailShadows=false
    'explosionQuality="Low"' 'oceanSimQuality="Low"' 'particleCountQuality="Low"' IVARayTracing=false
    celestialTextureCap=1024 vesselTextureCap=1024 cascadedShadowMapSize=1024 terrainShadowMapSize=1024)
PRESET_HIGH=('antialiasing="MultisampleX4"' 'cloudUpscale="High"' 'exhaustUpscale="High"' ambientOcclusion=true
    'ambientOcclusionQuality="High"' terrainTessellation=true groundClutter=true 'kittenFurQuality="High"'
    plumeTrailShadows=true 'explosionQuality="High"' 'oceanSimQuality="High"' 'particleCountQuality="High"'
    IVARayTracing=true celestialTextureCap=2048 vesselTextureCap=2048 cascadedShadowMapSize=2048
    terrainShadowMapSize=2048)
MOD_ID="ksa-metal-benchmark"
DOCS="${HOME}/Documents/My Games/Kitten Space Agency"
# The game's documents folder during the run: StarMap points the game at it (STARMAP_INSTANCE_PATH)
INSTANCE="${HOME}/Library/Caches/ksa-macos-port/benchmark/instance"

usage() {
    sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

# The report program runs on the .NET runtime that goes into the app
report() {
    DOTNET_ROOT="${SKELETON}/MacOS" "${SKELETON}/MacOS/dotnet" "${BENCH}/report/KsaMetalBenchmarkReport.dll" "$@"
}

require_kit
if [ "$1" = compare ]; then
    [ $# = 3 ] || usage 1
    report compare "$2" "$3"
    exit
fi

APP="${DEFAULT_APP}"
SECTIONS=()
PRESET=""
SYSTEM="sol"
EXPLORE=""
FAST=""
NO_SHOTS=""
HOLD=""
SETS=()
REFERENCE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --fast) FAST=1 ;;
        --no-shots) NO_SHOTS=1 ;;
        --hold) HOLD="$2"; shift ;;
        --set) SETS+=("$2"); shift ;;
        --compare) REFERENCE="${2%/}"; shift ;;
        --app) APP="${2%/}"; shift ;;
        --preset) PRESET="$2"; shift ;;
        --system) SYSTEM="$2"; shift ;;
        --explore) EXPLORE=1 ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SECTIONS+=("$1") ;;
    esac
    shift
done
case "${PRESET}" in
    "") PRESET_SETTINGS=() ;;
    low) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_LOW[@]}") ;;
    high) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_HIGH[@]}") ;;
    *) die "unknown preset '${PRESET}' (low or high)." ;;
esac
case "${SYSTEM}" in
    sol) SYSTEM_ID="Sol" ;;
    earth) SYSTEM_ID="SolLite" ;;
    *) die "unknown system '${SYSTEM}' (sol or earth)." ;;
esac
for s in ${SECTIONS[@]+"${SECTIONS[@]}"}; do
    grep -q "^section ${s}\( \|$\)" "${SCRIPT}" || die "no section '${s}' in the tour (payload/benchmark/benchmark.txt)."
done
# --hold <shot> or <section>_<shot>: its section only, fast
if [ -n "${HOLD}" ]; then
    # Each screenshot as <section>_<shot>
    shots="$(awk '$1 == "section" { s = $2 } $2 == "@shot" { print s "_" $3 }' "${SCRIPT}")"
    if ! echo "${shots}" | grep -qx "${HOLD}"; then
        matches="$(echo "${shots}" | grep "_${HOLD}$" || true)"
        [ -n "${matches}" ] || die "no screenshot '${HOLD}' in the tour (payload/benchmark/benchmark.txt)."
        [ "$(echo "${matches}" | wc -l)" -eq 1 ] || die "more than one screenshot '${HOLD}': $(echo ${matches})."
        HOLD="${matches}"
    fi
    SECTIONS=("${HOLD%%_*}")
    FAST=1
fi
[ -z "${REFERENCE}" ] || [ -f "${REFERENCE}/report.json" ] || die "no run at ${REFERENCE} (it has no report.json)."

check_mac
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."
RES="${APP}/Contents/Resources"
[ -f "${DOCS}/settings.toml" ] || die "start the game once before benchmarking it (it has no settings yet)."
pgrep -f "dotnet (KSA|StarMap).dll" > /dev/null && die "the game is running. Quit it first."

# 1. StarMap, which loads the benchmark mod
if [ ! -f "${RES}/StarMap/StarMap.dll" ]; then
    bash "${KIT}/starmap.sh" --app "${APP}"
fi
check_ksa_match "${INSTALLED}"

# 2. The game's documents folder for the run, made fresh: the game itself and the benchmark mod (none of your mods,
#    so they neither change the numbers nor stop the game from starting) and the bundled vehicles
rm -rf "${INSTANCE}"
mkdir -p "${INSTANCE}/mods/${MOD_ID}" "${INSTANCE}/Vehicles"
printf '[[mods]]\nid = "Core"\nenabled = true\n\n[[mods]]\nid = "%s"\nenabled = true\n' "${MOD_ID}" \
    > "${INSTANCE}/manifest.toml"
cp "${BENCH}/mod/mod.toml" "${BENCH}/mod/KsaMetalBenchmark.dll" "${INSTANCE}/mods/${MOD_ID}/"
cp -R "${BENCH}/vehicles/"* "${INSTANCE}/Vehicles/"

# The run's settings: yours, with the preset on top
SETTINGS="${INSTANCE}/settings.toml"
cp "${DOCS}/settings.toml" "${SETTINGS}"
# set_setting <key> <value>: one line of settings.toml (keys are unique across its sections)
set_setting() {
    grep -q "^$1 = " "${SETTINGS}" || die "settings.toml has no '$1' (does this KSA version still match?)."
    sed -i '' "s|^$1 = .*|$1 = $2|" "${SETTINGS}"
}
setting() {
    sed -n "s/^$1 = //p" "${SETTINGS}" | tr -d '"'
}
# The game starts straight into a new game of the chosen system, without its system menu, update popup or name labels,
# with the camera on the game's test vessel in orbit (a testing start, the one that keeps the system's vehicles)
for kv in selectSystemOnStart=false "lastSystemId=\"${SYSTEM_ID}\"" 'startGameType="Testing"' 'startVehicle="Rocket"' \
    'startParent="Earth"' 'startSituation="RocketStartingSituation"' checkForUpdates=false celestialNames=false \
    locationNames=false ${PRESET_SETTINGS[@]+"${PRESET_SETTINGS[@]}"}; do
    set_setting "${kv%%=*}" "${kv#*=}"
done
# --set: a value that is neither a number, true/false nor quoted is a string (TOML wants it quoted)
for kv in ${SETS[@]+"${SETS[@]}"}; do
    case "${kv}" in *=*) ;; *) die "--set needs key=value, not '${kv}'." ;; esac
    value="${kv#*=}"
    if ! [[ "${value}" =~ ^-?[0-9.]+$ || "${value}" == true || "${value}" == false || "${value}" == \"* ]]; then
        value="\"${value}\""
    fi
    set_setting "${kv%%=*}" "${value}"
done

# Explore: the same game, paused, with the benchkey console command; the keys go to a folder of their own
if [ -n "${EXPLORE}" ]; then
    OUT="${DOCS}/benchmarks/$(date +%Y-%m-%d_%H%M)_explore"
    mkdir -p "${OUT}"
    caffeinate -u -t 1
    step "Exploring KSA ${INSTALLED}: fly the free camera, type benchkey [name] in the console (\\) at each place, quit when done"
    STARMAP_INSTANCE_PATH="${INSTANCE}" KSA_BENCH_EXPLORE=1 KSA_BENCH_OUT="${OUT}" "${APP}/Contents/MacOS/KSA" > /dev/null 2>&1 || true
    if [ -f "${OUT}/keys.txt" ]; then
        info "Captured keys: ${OUT}/keys.txt"
    else
        info "No keys captured."
    fi
    exit
fi

# 3. The run
OUT="${DOCS}/benchmarks/$(date +%Y-%m-%d_%H%M)"
[ -z "${HOLD}" ] || OUT="${OUT}_hold"
[ ! -e "${OUT}" ] || OUT="${OUT}_$(date +%S)"
mkdir -p "${OUT}"
cp "${SETTINGS}" "${OUT}/settings.toml"
# What the report says about this Mac and the run (values first: bash 3.2 misreads quotes in $() inside "")
gpu_cores="$(system_profiler SPDisplaysDataType 2> /dev/null | awk -F': ' '/Total Number of Cores/ { print $2; exit }')"
displays="$(system_profiler SPDisplaysDataType 2> /dev/null | awk -F': ' '/Resolution:/ { print $2 }' | paste -sd ';' - | sed 's/;/; /g')"
power="$(pmset -g batt | head -1 | sed -n "s/.*'\(.*\)'.*/\1/p")"
low_power="$(pmset -g | awk '/lowpowermode/ { print ($2 == 1 ? "on" : "off"); exit }')"
switches="$(env | grep '^KSA_MTL_' | sed "s|${HOME}|~|g" | paste -sd ' ' - || true)"
port_version="$(/usr/libexec/PlistBuddy -c "Print :KSAPortVersion" "${APP}/Contents/Info.plist" 2> /dev/null || true)"
port_commit="$(/usr/libexec/PlistBuddy -c "Print :KSAPortCommit" "${APP}/Contents/Info.plist" 2> /dev/null || true)"
{
    echo "Mac: $(sysctl -n hw.model)"
    echo "Chip: $(sysctl -n machdep.cpu.brand_string)"
    echo "CPU cores: $(sysctl -n hw.perflevel0.physicalcpu) performance, $(sysctl -n hw.perflevel1.physicalcpu 2> /dev/null || echo 0) efficiency"
    echo "GPU cores: ${gpu_cores}"
    echo "Memory: $(($(sysctl -n hw.memsize) / 1073741824)) GB"
    echo "macOS: $(sw_vers -productVersion) ($(sw_vers -buildVersion))"
    echo "Displays: ${displays}"
    echo "Power: ${power}"
    echo "Low Power Mode: ${low_power}"
    echo "KSA: ${INSTALLED}"
    echo "Port: ${port_version} (${port_commit})"
    echo "StarMap: $(cat "${RES}/StarMap/version.txt" 2> /dev/null || echo unknown)"
    echo "System: ${SYSTEM_ID}"
    echo "Sections: ${SECTIONS[*]:-all}"
    echo "Mode: $([ -n "${FAST}" ] && echo fast || echo cinematic)$([ -n "${NO_SHOTS}" ] && echo ", no screenshots" || true)"
    echo "Preset: ${PRESET:-none, own settings}"
    echo "Settings changed: ${SETS[*]:-none}"
    echo "Window: $(setting width) x $(setting height), $(setting mode), on $(setting screenName)"
    echo "V-Sync: $(setting vsync), FPS limit $(setting fpslimit)"
    echo "Anti-aliasing: $(setting antialiasing)"
    echo "KSA Metal switches: ${switches:-none}"
} > "${OUT}/system.txt"
# Keeps the display awake while the script runs (the game needs it), and wakes it now: a display that went to sleep
# makes the game fail at start
caffeinate -d -i -w $$ &
caffeinate -u -t 1

# Loading takes up to a few minutes on a first start (shader compilation), then the script's own length, plus the
# time slower Macs spend catching up at checkpoints. Once playing, the mod touches its heartbeat file every second: a
# game that stops touching it has hung.
length="$(awk '$1 == "section" { total += max; max = 0 } $1 ~ /^[0-9.]+$/ && $1 > max { max = $1 }
    END { print int(total + max) }' "${SCRIPT}")"
timeout=$((length * 2 + 300))
hang=60
if [ -n "${HOLD}" ]; then
    step "Going to ${HOLD} in KSA ${INSTALLED}: it stays there, paused, until you quit the game"
    timeout=0
else
    step "Benchmarking KSA ${INSTALLED} ($([ -n "${FAST}" ] && echo "fast, a few" || echo "about $(((length + 60) / 60))") minutes)"
    info "The game takes over the screen. Leave the mouse and keyboard alone until it is done."
fi
KSA_BENCH_ONLY="${SECTIONS[*]}" KSA_BENCH_FAST="${FAST}" KSA_BENCH_NO_SHOTS="${NO_SHOTS}" KSA_BENCH_HOLD="${HOLD}" \
    STARMAP_INSTANCE_PATH="${INSTANCE}" KSA_BENCH_SCRIPT="${SCRIPT}" KSA_BENCH_OUT="${OUT}" \
    "${APP}/Contents/MacOS/KSA" > /dev/null 2>&1 &
pid=$!
start="$(date +%s)"
ending=""
alongside=""
while kill -0 "${pid}" 2> /dev/null; do
    now="$(date +%s)"
    # Another game at the same time shares the Mac's GPU and memory (and may be the end of this one)
    if [ -z "${alongside}" ] && pgrep -f "dotnet (KSA|StarMap).dll" | grep -qvx "${pid}"; then
        alongside=1
        warn "another game is running at the same time: this run's numbers won't count."
    fi
    if [ -f "${OUT}/heartbeat" ] && [ $((now - $(stat -f %m "${OUT}/heartbeat"))) -gt "${hang}" ]; then
        ending="hung in $(cat "${OUT}/heartbeat" | awk '{ print $1 " at " $2 " s" }') (no frame for ${hang} s): stopped"
    elif [ "${timeout}" -gt 0 ] && [ $((now - start)) -ge "${timeout}" ]; then
        ending="did not finish in ${timeout} s: stopped"
    fi
    if [ -n "${ending}" ]; then
        warn "the game ${ending}."
        kill -9 "${pid}" 2> /dev/null || true
        break
    fi
    sleep 1
done
status=0
wait "${pid}" 2> /dev/null || status=$?
if [ -z "${ending}" ]; then
    case "${status}" in
        0) ending="quit normally" ;;
        13[0-9] | 1[4-9][0-9]) ending="crashed (signal $((status - 128)))" ;;
        *) ending="quit with status ${status}" ;;
    esac
fi
echo "Game: ${ending}" > "${OUT}/run.txt"
[ -z "${alongside}" ] || echo "Alongside: another game" >> "${OUT}/run.txt"
rm -f "${OUT}/heartbeat"
# The game's log, without your home folder's name
sed "s|${HOME}|~|g" "${INSTANCE}/logs/console.log" > "${OUT}/game.log" 2> /dev/null || true
if [ -n "${HOLD}" ]; then
    step "Done: ${OUT}"
    exit
fi

failed=0
report report "${OUT}" > /dev/null || failed=1
echo ""
cat "${OUT}/report.md"
echo ""
if [ -n "${REFERENCE}" ]; then
    report compare "${REFERENCE}" "${OUT}" || failed=1
    echo ""
fi
if [ "${failed}" = 0 ]; then
    step "Done: ${OUT}"
else
    step "Done, with problems: ${OUT}"
fi
info "Compare with another run: ./benchmark.sh compare <other run folder> \"${OUT}\""
exit "${failed}"
