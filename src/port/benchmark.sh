#!/bin/bash
# Measures how KSA runs on this Mac: plays a fixed tour of the game in one session and writes a report, and a zip of
# it in the run's folder to share if you like.
#
# Usage: ./benchmark.sh [options] [section or place...]
#        ./benchmark.sh compare <run> <run>
#   With no sections, plays the whole tour (with --fast, its short test), with your own settings. A place is a
#   screenshot of the tour (earth4, or giga_pad where a name is in more than one section): its section up to there.
#   The game takes over the screen: leave the mouse and keyboard alone until it is done. The game runs from its own
#   documents folder, with a copy of your settings: your files stay untouched. Exits with 1 when the run failed (the
#   game crashed or hung, a step failed, errors in its log).
#   compare prints how the second run differs from the first: the Macs, settings, numbers part by part, and
#   screenshots (exits with 1 when screenshots differ). A run is a run folder or a shared zip.
#
# Options:
#   --preset <name>    low, medium or high: fixed settings in a 1280x800 window, the same on every Mac (default: yours)
#   --defaults         the settings of a fresh install instead of yours: the game's own and the port's defaults
#   --system <name>    sol (the solar system) or earth (Earth and the Moon only: for Macs with 8 GB of memory)
#                      (default: sol, or earth in fast mode when the places played are all on Earth or the Moon)
#   --fast             a short test: three places on Earth, the game paused and the camera jumping between them,
#                      then a rocket's launch and explosion (with sections or places: those, the camera's flight
#                      paused and jumping, vehicles played as written); measures the views standing still
#   --no-shots         take no screenshots
#   --set <key=value>  change one setting of settings.toml for the run (again for more), on top of the preset
#   --settings <file>  change the settings in that file ("key = value" lines, as in settings.toml), on top of the
#                      preset and before --set
#   --port <key=value>  change one setting of port-settings.toml for the run (again for more), on top of yours (with
#                      a preset or --defaults: on top of the defaults)
#   --compare <run>    compare with that run at the end (and fail when screenshots differ)
#   --app <path>       the app to measure (default: ~/Applications/Kitten Space Agency.app)
#
# For work on the port (see the benchmark's README):
#   --hold <place>     go to that place fast and stay there, the game paused, until you quit it
#   --repeat <n>       play it n times, one game session each, and report the median and range of each number
#   --profile          KSA Metal's GPU profiler on every measured part: profile.md, the encoders that cost the most
#   --hud              Apple's Metal Performance HUD over the game, its per-frame log, and its reports (started from
#                      its menu) in the run folder
#   --trace            record an Instruments trace (Metal System Trace) of the run, parts marked; needs Xcode
#   --trace-template <name>  another Instruments template for --trace (xcrun xctrace list templates)
#   --capture          an Xcode GPU frame capture of the view at each place played (.gputrace, to open in Xcode)
#   --explore          start the benchmark's game paused, to fly the free camera around and capture places for the
#                      tour (@key lines, for benchmark.txt) with benchkey [name] in its console; quit it when done
#
# The benchmark is in the app (Contents/Resources/Benchmark/), built for the KSA version this port folder was made for:
# KSA Metal Hooks start its director when the game starts with the run's variables set, in a documents folder of its
# own. The Mac Port tab in the game's settings runs it too.
set -eo pipefail
source "$(dirname "$0")/lib/common.sh"

# Presets: fixed settings, the same on every Mac, so reports compare. Without one, your own settings are used.
# All run in a 1280x800 window without V-Sync (frame times are then the game's, not the display's).
PRESET_COMMON=('mode="Windowed"' width=1280 height=800 vsync=false fpslimit=240)
PRESET_LOW=('antialiasing="Disabled"' 'cloudUpscale="Low"' 'exhaustUpscale="Low"' ambientOcclusion=false
    terrainTessellation=false groundClutter=false 'kittenFurQuality="Low"' plumeTrailShadows=false
    'explosionQuality="Low"' 'oceanSimQuality="Low"' 'particleCountQuality="Low"' IVARayTracing=false
    celestialTextureCap=1024 vesselTextureCap=1024 cascadedShadowMapSize=1024 terrainShadowMapSize=1024)
PRESET_MEDIUM=('antialiasing="MultisampleX2"' 'cloudUpscale="Medium"' 'exhaustUpscale="Medium"' ambientOcclusion=true
    'ambientOcclusionQuality="Medium"' terrainTessellation=true groundClutter=true 'kittenFurQuality="Medium"'
    plumeTrailShadows=false 'explosionQuality="Medium"' 'oceanSimQuality="Medium"' 'particleCountQuality="Medium"'
    IVARayTracing=false celestialTextureCap=2048 vesselTextureCap=1024 cascadedShadowMapSize=2048
    terrainShadowMapSize=1024)
PRESET_HIGH=('antialiasing="MultisampleX4"' 'cloudUpscale="High"' 'exhaustUpscale="High"' ambientOcclusion=true
    'ambientOcclusionQuality="High"' terrainTessellation=true groundClutter=true 'kittenFurQuality="High"'
    plumeTrailShadows=true 'explosionQuality="High"' 'oceanSimQuality="High"' 'particleCountQuality="High"'
    IVARayTracing=true celestialTextureCap=2048 vesselTextureCap=2048 cascadedShadowMapSize=2048
    terrainShadowMapSize=2048)
DOCS="${HOME}/Documents/My Games/Kitten Space Agency"
# The game's documents folder during the run (STARMAP_INSTANCE_PATH, which the hooks and StarMap both follow)
INSTANCE="${HOME}/Library/Caches/ksa-macos-port/benchmark/instance"
ISSUES="https://github.com/${RELEASES_REPO}/issues/new?template=benchmark-report.md"

usage() {
    sed -n '2,44p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

APP="${DEFAULT_APP}"
# The report program, from the app, on its .NET runtime
report() {
    DOTNET_ROOT="${APP}/Contents/MacOS" "${APP}/Contents/MacOS/dotnet" \
        "${APP}/Contents/Resources/Benchmark/KsaMetalBenchmarkReport.dll" "$@"
}

require_kit
if [ "$1" = compare ]; then
    [ $# = 3 ] || usage 1
    [ -f "${APP}/Contents/Resources/Benchmark/KsaMetalBenchmarkReport.dll" ] ||
        die "no benchmark in ${APP}: update it with ./port.sh."
    report compare "$2" "$3"
    exit
fi

PLACES=()
PRESET=""
DEFAULTS=""
SYSTEM=""
EXPLORE=""
FAST=""
NO_SHOTS=""
HOLD=""
SETS=()
PORTS=()
SETTINGS_FILE=""
REFERENCE=""
REPEAT=1
PROFILE=""
HUD=""
TRACE=""
TEMPLATE="Metal System Trace"
CAPTURE=""
while [ $# -gt 0 ]; do
    case "$1" in
        --fast) FAST=1 ;;
        --no-shots) NO_SHOTS=1 ;;
        --hold) HOLD="$2"; shift ;;
        --set) SETS+=("$2"); shift ;;
        --port) PORTS+=("$2"); shift ;;
        --settings) SETTINGS_FILE="$2"; shift ;;
        --compare) REFERENCE="${2%/}"; shift ;;
        --app) APP="${2%/}"; shift ;;
        --preset) PRESET="$2"; shift ;;
        --defaults) DEFAULTS=1 ;;
        --system) SYSTEM="$2"; shift ;;
        --repeat) REPEAT="$2"; shift ;;
        --profile) PROFILE=1 ;;
        --hud) HUD=1 ;;
        --trace) TRACE=1 ;;
        --trace-template) TRACE=1; TEMPLATE="$2"; shift ;;
        --capture) CAPTURE=1 ;;
        --explore) EXPLORE=1 ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) PLACES+=("$1") ;;
    esac
    shift
done
case "${PRESET}" in
    "") PRESET_SETTINGS=() ;;
    low) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_LOW[@]}") ;;
    medium) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_MEDIUM[@]}") ;;
    high) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_HIGH[@]}") ;;
    *) die "unknown preset '${PRESET}' (low, medium or high)." ;;
esac
[[ "${REPEAT}" =~ ^[1-9][0-9]*$ ]] || die "--repeat needs a number of runs, not '${REPEAT}'."
[ -z "${SETTINGS_FILE}" ] || [ -f "${SETTINGS_FILE}" ] || die "no settings file ${SETTINGS_FILE}."
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."
RES="${APP}/Contents/Resources"
SCRIPT="${RES}/Benchmark/benchmark.txt"
[ -f "${SCRIPT}" ] && [ -f "${RES}/Hooks/KsaMetalHooks.dll" ] ||
    die "the app at ${APP} has no benchmark: update it with ./port.sh."

# Places: a section, <section>_<shot>, or a screenshot's name alone where only one section has it
SHOTS="$(awk '$1 == "section" { s = $2 } $2 == "@shot" { print s "_" $3 }' "${SCRIPT}")"
# place <name>: the section or <section>_<shot> it names
place() {
    if grep -q "^section $1\( \|$\)" "${SCRIPT}" || echo "${SHOTS}" | grep -qx "$1"; then
        echo "$1"
        return
    fi
    local matches
    matches="$(echo "${SHOTS}" | grep "_$1$" || true)"
    [ -n "${matches}" ] || die "no section or screenshot '$1' in the tour (the app's Benchmark/benchmark.txt)."
    [ "$(echo "${matches}" | wc -l)" -eq 1 ] || die "more than one screenshot '$1': ${matches//$'\n'/ }."
    echo "${matches}"
}
ONLY=()
for p in ${PLACES[@]+"${PLACES[@]}"}; do
    ONLY+=("$(place "${p}")")
done
# --hold <place>: its section up to there, fast
if [ -n "${HOLD}" ]; then
    HOLD="$(place "${HOLD}")"
    echo "${SHOTS}" | grep -qx "${HOLD}" || die "--hold needs a screenshot's place, not a section."
    ONLY=("${HOLD}")
    FAST=1
    REPEAT=1
fi
# Places named (the tools that take GB at each place need some)
NAMED=${#ONLY[@]}
SECTIONS="${ONLY[*]:-all}"
# Without places: fast mode's short test, or the cinematic tour (the tour's "fast" and "tour" lines)
if [ "${NAMED}" = 0 ]; then
    if [ -n "${FAST}" ]; then
        read -r -a ONLY <<< "$(awk '$1 == "fast" { $1 = ""; print }' "${SCRIPT}")"
        SECTIONS="${ONLY[*]}"
    else
        read -r -a ONLY <<< "$(awk '$1 == "tour" { $1 = ""; print }' "${SCRIPT}")"
    fi
fi
# Fast mode plays in Earth's small system when its sections need no other celestial than the Moon
if [ -z "${SYSTEM}" ] && [ -n "${FAST}" ]; then
    SYSTEM=earth
    for p in "${ONLY[@]}"; do
        awk -v s="${p%%_*}" '$1 == "section" && $2 == s && $3 == "needs" { for (i = 4; i <= NF; i++) if ($i != "Luna") n++ }
            END { exit n > 0 }' "${SCRIPT}" || SYSTEM=sol
    done
fi
case "${SYSTEM:-sol}" in
    sol) SYSTEM_ID="Sol" ;;
    earth) SYSTEM_ID="SolLite" ;;
    *) die "unknown system '${SYSTEM}' (sol or earth)." ;;
esac
[ -z "${REFERENCE}" ] || [ -f "${REFERENCE}/report.json" ] || [ -f "${REFERENCE}" ] ||
    die "no run at ${REFERENCE} (no report.json, and not a zip)."

check_mac
[ -f "${DOCS}/settings.toml" ] || die "start the game once before benchmarking it (it has no settings yet)."
pgrep -f "dotnet (KSA|StarMap).dll" > /dev/null && die "the game is running. Quit it first."

# The tools for work on the port, each checked before the game starts: they may run on a Mac without them
TOOLS=()
# driver_has <function>: whether the app's KSA Metal has that benchmark function
driver_has() {
    LC_ALL=C grep -q -a "$1" "${RES}/libvulkan.1.dylib" 2> /dev/null
}
# free_gb: free space where the results go
free_gb() {
    df -g "${DOCS}" | awk 'NR == 2 { print $4 }'
}
if [ -n "${PROFILE}${HUD}${TRACE}${CAPTURE}" ]; then
    [ "${REPEAT}" = 1 ] || die "--repeat measures without tools: use --profile, --hud, --trace or --capture in a run of their own."
    driver_has ksaMtlFrameStats || die "the tools need the app's KSA Metal driver, which this app doesn't have."
fi
if [ -n "${PROFILE}" ]; then
    driver_has ksaMtlProfileFrames || die "this app's KSA Metal has no GPU profiler: update it with ./port.sh."
    [ -z "${HUD}" ] || die "--profile and --hud can't run together: both use the GPU's counters."
    TOOLS+=("GPU profiler")
fi
if [ -n "${HUD}" ]; then
    TOOLS+=("Metal HUD")
fi
if [ -n "${TRACE}" ]; then
    xcrun -f xctrace > /dev/null 2>&1 ||
        die "--trace needs Xcode (from the App Store), not only the Command Line Tools. With Xcode installed: sudo xcode-select -s /Applications/Xcode.app"
    templates="$(xcrun xctrace list templates 2> /dev/null || true)"
    echo "${templates}" | grep -qx "${TEMPLATE}" ||
        die "Instruments has no template '${TEMPLATE}' (xcrun xctrace list templates)."
    [ "${NAMED}" -gt 0 ] || die "a trace of the whole tour takes tens of GB: choose sections or places (e.g. --fast jupiter2 --trace)."
    [ "$(free_gb)" -ge 10 ] || die "--trace needs 10 GB free on this disk ($(free_gb) GB free)."
    TOOLS+=("Instruments (${TEMPLATE})")
fi
if [ -n "${CAPTURE}" ]; then
    driver_has ksaMtlCapture || die "this app's KSA Metal can't capture frames: update it with ./port.sh."
    [ "${NAMED}" -gt 0 ] || die "--capture takes several GB at every screenshot: choose places (e.g. --fast jupiter2 --capture)."
    [ "$(free_gb)" -ge 20 ] || die "--capture needs 20 GB free on this disk ($(free_gb) GB free)."
    [ -d /Applications/Xcode.app ] || warn "the captures open in Xcode, which isn't in /Applications."
    TOOLS+=("GPU frame capture")
fi

check_ksa_match "${INSTALLED}"

# 1. The game's documents folder for the run, made fresh: the game alone (none of your mods, so they neither change
#    the numbers nor stop the game from starting; the hooks add the benchmark's vehicles from the app)
rm -rf "${INSTANCE}"
mkdir -p "${INSTANCE}"
printf '[[mods]]\nid = "Core"\nenabled = true\n' > "${INSTANCE}/manifest.toml"

# The run's settings: yours, with the preset on top
SETTINGS="${INSTANCE}/settings.toml"
cp "${DOCS}/settings.toml" "${SETTINGS}"
SET_KEYS=" "
# set_setting <key> <value>: one line of settings.toml (keys are unique across its sections)
set_setting() {
    grep -q "^$1 = " "${SETTINGS}" || die "settings.toml has no '$1' (does this KSA version still match?)."
    sed -i '' "s|^$1 = .*|$1 = $2|" "${SETTINGS}"
    SET_KEYS="${SET_KEYS}$1 "
}
# The game starts straight into a new game of the chosen system, without its system menu, update popup, changelog or
# name labels, with the camera on the game's test vessel in orbit (a testing start, the one that keeps the system's
# vehicles)
for kv in selectSystemOnStart=false "lastSystemId=\"${SYSTEM_ID}\"" 'startGameType="Testing"' 'startVehicle="Rocket"' \
    'startParent="Earth"' 'startSituation="RocketStartingSituation"' checkForUpdates=false \
    displayChangelogOnChange=false celestialNames=false locationNames=false \
    ${PRESET_SETTINGS[@]+"${PRESET_SETTINGS[@]}"}; do
    set_setting "${kv%%=*}" "${kv#*=}"
done
# --settings: its "key = value" lines as they are
if [ -n "${SETTINGS_FILE}" ]; then
    while IFS= read -r line || [ -n "${line}" ]; do
        case "${line}" in "" | \#* | \[*) continue ;; esac
        key="$(echo "${line%%=*}" | tr -d ' \t')"
        value="$(echo "${line#*=}" | sed 's/^[[:space:]]*//; s/[[:space:]]*$//')"
        [ -n "${key}" ] && [ "${key}" != "${line}" ] || die "${SETTINGS_FILE}: not a 'key = value' line: ${line}"
        set_setting "${key}" "${value}"
    done < "${SETTINGS_FILE}"
fi
# --set: a value that is neither a number, true/false nor quoted is a string (TOML wants it quoted)
for kv in ${SETS[@]+"${SETS[@]}"}; do
    case "${kv}" in *=*) ;; *) die "--set needs key=value, not '${kv}'." ;; esac
    value="${kv#*=}"
    if ! [[ "${value}" =~ ^-?[0-9.]+$ || "${value}" == true || "${value}" == false || "${value}" == \"* ]]; then
        value="\"${value}\""
    fi
    set_setting "${kv%%=*}" "${value}"
done
# --defaults: only the lines set above, in their sections. The game gives every setting the file leaves out its
# default, as on a fresh install, and saves the whole file when it starts.
if [ -n "${DEFAULTS}" ]; then
    awk -v keep="${SET_KEYS}" '/^\[/ { section = $0; next }
        $2 == "=" && index(keep, " " $1 " ") { if (section != "") { print (n++ ? "\n" : "") section; section = "" } print }' \
        "${SETTINGS}" > "${SETTINGS}.new"
    mv "${SETTINGS}.new" "${SETTINGS}"
fi

# The run's port settings (the app's port-settings.toml template): yours unless a preset is played or --defaults,
# --port on top
PORT_SETTINGS="${INSTANCE}/port-settings.toml"
if [ -z "${PRESET}${DEFAULTS}" ] && [ -f "${DOCS}/port-settings.toml" ]; then
    cp "${DOCS}/port-settings.toml" "${PORT_SETTINGS}"
else
    cp "${APP}/Contents/Resources/port-settings.toml" "${PORT_SETTINGS}" 2> /dev/null || : > "${PORT_SETTINGS}"
fi
for kv in ${PORTS[@]+"${PORTS[@]}"}; do
    case "${kv}" in *=*) ;; *) die "--port needs key=value, not '${kv}'." ;; esac
    key="${kv%%=*}"
    if grep -q "^${key} *=" "${PORT_SETTINGS}"; then
        sed -i '' "s|^${key} *=.*|${key} = ${kv#*=}|" "${PORT_SETTINGS}"
    else
        echo "${key} = ${kv#*=}" >> "${PORT_SETTINGS}"
    fi
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

memory_gb=$(($(sysctl -n hw.memsize) / 1073741824))
if [ -z "${SYSTEM}" ] && [ "${memory_gb}" -le 8 ]; then
    info "This Mac has ${memory_gb} GB of memory: if the game runs out of it, --system earth plays Earth and the Moon only."
fi

# 2. The run
OUT="${DOCS}/benchmarks/$(date +%Y-%m-%d_%H%M)"
[ -z "${HOLD}" ] || OUT="${OUT}_hold"
[ ! -e "${OUT}" ] || OUT="${OUT}_$(date +%S)"
mkdir -p "${OUT}"
cp "${SETTINGS}" "${OUT}/settings.toml"
# What the report says about this Mac and the run (the report program asks macOS about the Mac)
switches="$(env | grep -E '^KSA_(MTL_|HOOKS)' | sed "s|${HOME}|~|g" | paste -sd ' ' - || true)"
tools="$(IFS=,; echo "${TOOLS[*]:-none}")"
tools="${tools//,/, }"
changed="${SETS[*]:-}${PORTS[*]:+${SETS[*]:+ }port-settings: ${PORTS[*]}}"
[ -z "${SETTINGS_FILE}" ] || changed="$(basename "${SETTINGS_FILE}")${changed:+ }${changed}"
starmap=none
[ ! -f "${RES}/StarMap/StarMap.dll" ] || starmap="$(cat "${RES}/StarMap/version.txt" 2> /dev/null || echo "in the app")"
preset="${PRESET:-none, own settings}"
[ -z "${DEFAULTS}" ] || preset="${PRESET:-none}, a fresh install's settings"
# system_facts: system.txt, with the window from the run's settings.toml
system_facts() {
    report system "${OUT}" "${APP}" "StarMap=${starmap}" "System=${SYSTEM_ID}" "Sections=${SECTIONS}" \
        "Mode=$([ -n "${FAST}" ] && echo fast || echo cinematic)$([ -n "${NO_SHOTS}" ] && echo ", no screenshots" || true)" \
        "Preset=${preset}" "Settings changed=${changed:-none}" "Start=a new game, from benchmark.sh" \
        "KSA Metal switches=${switches:-none}" "Tools=${tools}" > /dev/null
}
system_facts || die "the report program did not run."
# Keeps the display awake while the script runs (the game needs it), and wakes it now: a display that went to sleep
# makes the game fail at start
caffeinate -d -i -w $$ &
caffeinate -u -t 1

# What the tools add to the game's environment
GAME_ENV=()
if [ -n "${PROFILE}" ]; then
    # Nothing profiled until the mod opens a part
    GAME_ENV+=("KSA_MTL_GPUPROF=${OUT}/gpuprof.csv" "KSA_MTL_GPUPROF_FRAMES=0,0" "KSA_BENCH_PROFILE=60")
fi
if [ -n "${HUD}" ]; then
    mkdir -p "${OUT}/hud"
    GAME_ENV+=(MTL_HUD_ENABLED=1 MTL_HUD_LOG_ENABLED=1 MTL_HUD_LOG_SHADER_ENABLED=1 MTL_HUD_ENCODER_TIMING_ENABLED=1
        MTL_HUD_INSIGHTS_ENABLED=1 "MTL_HUD_REPORT_URL=${OUT}/hud" KSA_MTL_LABELS=1)
fi
if [ -n "${TRACE}" ]; then
    GAME_ENV+=("KSA_BENCH_WAIT=${OUT}/.recording" KSA_MTL_LABELS=1)
fi
if [ -n "${CAPTURE}" ]; then
    GAME_ENV+=(MTL_CAPTURE_ENABLED=1 KSA_BENCH_CAPTURE=1 KSA_MTL_LABELS=1)
fi

# Loading takes up to a few minutes on a first start (shader compilation), then the script's own length, plus the
# time slower Macs spend catching up at checkpoints. Once playing, the mod touches its heartbeat file every second: a
# game that stops touching it has hung.
length="$(awk -v play=" ${ONLY[*]%%_*} " '$1 == "section" { total += max; max = 0; on = index(play, " " $2 " ") }
    on && $1 ~ /^[0-9.]+$/ && $1 > max { max = $1 } END { print int(total + max) }' "${SCRIPT}")"
timeout=$((length * 2 + 300))
hang=60
[ -z "${CAPTURE}" ] || hang=180
pressure=1
swap_before="$(sysctl -n vm.swapusage | sed -n 's/.*used = \([0-9.]*\)M.*/\1/p')"
if [ -n "${HOLD}" ]; then
    step "Going to ${HOLD} in KSA ${INSTALLED}: it stays there, paused, until you quit the game"
    timeout=0
else
    what="about $(((length + 60) / 60)) minutes"
    [ -z "${FAST}" ] || what="fast$([ "${NAMED}" -gt 0 ] || echo ", about a minute")"
    [ "${REPEAT}" = 1 ] || what="${REPEAT} times, ${what} each"
    step "Benchmarking KSA ${INSTALLED} (${what})"
    info "The game takes over the screen. Leave the mouse and keyboard alone until it is done."
fi
[ ${#TOOLS[@]} = 0 ] || info "With ${tools}."

# play <run number>: one game session; its ending goes to run.txt
play() {
    local n="$1" suffix="" label="Game" pid start now ending="" alongside="" status=0 tracer="" level
    if [ "${n}" -gt 1 ]; then
        suffix="-${n}"
        label="Game ${n}"
        info "Run ${n} of ${REPEAT}"
    fi
    rm -f "${OUT}/heartbeat" "${OUT}/.recording"
    env ${GAME_ENV[@]+"${GAME_ENV[@]}"} KSA_BENCH_ONLY="${ONLY[*]}" KSA_BENCH_FAST="${FAST}" KSA_BENCH_HOLD="${HOLD}" \
        KSA_BENCH_NO_SHOTS="$([ -n "${NO_SHOTS}" ] || [ "${n}" -gt 1 ] && echo 1 || true)" KSA_BENCH_RUN="${suffix}" \
        STARMAP_INSTANCE_PATH="${INSTANCE}" KSA_BENCH_SCRIPT="${SCRIPT}" KSA_BENCH_OUT="${OUT}" \
        "${APP}/Contents/MacOS/KSA" > /dev/null 2>&1 &
    pid=$!
    start="$(date +%s)"
    while kill -0 "${pid}" 2> /dev/null; do
        now="$(date +%s)"
        # --trace: once the game is up (its first heartbeat), Instruments attaches, and the tour starts when it records
        if [ -n "${TRACE}" ] && [ -z "${tracer}" ] && [ -f "${OUT}/heartbeat" ]; then
            # Points of Interest holds the parts (KSA Metal's signposts)
            xcrun xctrace record --template "${TEMPLATE}" --instrument "Points of Interest" --attach "${pid}" \
                --output "${OUT}/trace.trace" --no-prompt > "${OUT}/xctrace.log" 2>&1 &
            tracer=$!
            local waited=0
            until grep -q -i "recording\|ctrl-c" "${OUT}/xctrace.log" 2> /dev/null || [ "${waited}" -ge 60 ] ||
                ! kill -0 "${tracer}" 2> /dev/null; do
                sleep 1
                waited=$((waited + 1))
            done
            kill -0 "${tracer}" 2> /dev/null || warn "Instruments did not start: $(tail -3 "${OUT}/xctrace.log")"
            sleep 2
            touch "${OUT}/.recording"
        fi
        # Another game at the same time shares the Mac's GPU and memory (and may be the end of this one)
        if [ -z "${alongside}" ] && pgrep -f "dotnet (KSA|StarMap).dll" | grep -qvx "${pid}"; then
            alongside=1
            warn "another game is running at the same time: this run's numbers won't count."
        fi
        # macOS's memory pressure: 1 normal, 2 warning, 4 critical
        level="$(sysctl -n kern.memorystatus_vm_pressure_level 2> /dev/null || echo 1)"
        [ "${level}" -le "${pressure}" ] || pressure="${level}"
        if [ -f "${OUT}/heartbeat" ] && [ $((now - $(stat -f %m "${OUT}/heartbeat"))) -gt "${hang}" ]; then
            ending="hung in $(awk '{ print $1 " at " $2 " s" }' "${OUT}/heartbeat") (no frame for ${hang} s): stopped"
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
    wait "${pid}" 2> /dev/null || status=$?
    if [ -z "${ending}" ]; then
        case "${status}" in
            0) ending="quit normally" ;;
            13[0-9] | 1[4-9][0-9]) ending="crashed (signal $((status - 128)))" ;;
            *) ending="quit with status ${status}" ;;
        esac
    fi
    echo "${label}: ${ending}" >> "${OUT}/run.txt"
    [ -z "${alongside}" ] || echo "Alongside${suffix}: another game" >> "${OUT}/run.txt"
    # Instruments stops recording when the game quits; it then writes the trace
    if [ -n "${tracer}" ]; then
        info "Instruments is writing the trace"
        kill -INT "${tracer}" 2> /dev/null || true
        wait "${tracer}" 2> /dev/null || true
    fi
    rm -f "${OUT}/heartbeat" "${OUT}/.recording"
    # The game's log, without your home folder's name
    sed "s|${HOME}|~|g" "${INSTANCE}/logs/console.log" > "${OUT}/game${suffix}.log" 2> /dev/null || true
}

: > "${OUT}/run.txt"
for n in $(seq 1 "${REPEAT}"); do
    play "${n}"
done
# --defaults: the settings as the game saved them, with its defaults filled in
if [ -n "${DEFAULTS}" ]; then
    cp "${SETTINGS}" "${OUT}/settings.toml"
    system_facts || true
fi
case "${pressure}" in 1) pressure=normal ;; 2) pressure=warning ;; *) pressure=critical ;; esac
swap_after="$(sysctl -n vm.swapusage | sed -n 's/.*used = \([0-9.]*\)M.*/\1/p')"
echo "Memory pressure: ${pressure}" >> "${OUT}/run.txt"
echo "Swap used: ${swap_before:-?} -> ${swap_after:-?} MB" >> "${OUT}/run.txt"

# The tools' output
if [ -n "${HUD}" ]; then
    # The HUD's log, from the game's: once a second "metal-HUD: <first frame>,<graphics MB>,<process MB>" and each
    # frame's present interval and GPU time (ms), and the shaders it saw compiled
    grep -h 'metal-HUD:\|CompileShader' "${OUT}/game.log" > "${OUT}/hud.log" || true
    info "Metal HUD: $(grep -c 'metal-HUD' "${OUT}/hud.log" 2> /dev/null || echo 0) log lines in hud.log, reports in hud/"
fi
if [ -n "${TRACE}" ] && [ -e "${OUT}/trace.trace" ]; then
    # Its tables of the parts and of the GPU's work, for the report program's summary (trace.md)
    xcrun xctrace export --input "${OUT}/trace.trace" --toc --output "${OUT}/trace-toc.xml" > /dev/null 2>&1 || true
    xcrun xctrace export --input "${OUT}/trace.trace" --output "${OUT}/trace-parts.xml" \
        --xpath '/trace-toc/run[@number="1"]/data/table[@schema="OSSignpostIntervals"]' > /dev/null 2>&1 || true
    xcrun xctrace export --input "${OUT}/trace.trace" --output "${OUT}/trace-gpu.xml" \
        --xpath '/trace-toc/run[@number="1"]/data/table[@schema="metal-gpu-intervals"]' > /dev/null 2>&1 || true
    report trace "${OUT}" > /dev/null || warn "the trace's summary failed (the trace itself is fine)."
    info "Trace: ${OUT}/trace.trace (open it in Instruments; the parts are under Points of Interest), summary in trace.md"
fi
if [ -n "${CAPTURE}" ]; then
    for c in "${OUT}"/*.gputrace; do
        [ -e "${c}" ] && info "Frame capture: ${c} (open it in Xcode)"
    done
fi
if [ -n "${HOLD}" ]; then
    step "Done: ${OUT}"
    exit
fi

# The report, then the zip to share (report finish): the report, every frame's numbers, the logs and crash reports of
# the run, the settings, and the screenshots as JPEG; your home folder's name replaced by ~
failed=0
ZIP="$(report finish "${OUT}" | tail -1)" || failed=1
[ -z "${PROFILE}" ] || report profile "${OUT}" > /dev/null || true
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
info "Compare with another run: ./benchmark.sh compare <other run> \"${OUT}\""
info "To share it (up to you): ${ZIP} ($(du -h "${ZIP}" | cut -f1 | tr -d ' ')) holds the report, the numbers,"
info "the logs and the screenshots. Attach it to a new issue: ${ISSUES}"
exit "${failed}"
