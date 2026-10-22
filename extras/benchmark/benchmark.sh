#!/bin/bash
# Measures how KSA runs on this Mac: plays a fixed set of scenes, one game start each, and writes a report to share.
#
# Usage: extras/benchmark/benchmark.sh [options] [scene...]
#        extras/benchmark/benchmark.sh compare <run folder> <run folder>
#   With no scenes, plays all of them. The game takes over the screen: leave the mouse and keyboard alone until it
#   is done. The game runs from its own documents folder, with a copy of your settings: your files stay untouched.
#   compare prints how the second run differs from the first, scene by scene.
#
# Options:
#   --app <path>      the app to measure (default: ~/Applications/Kitten Space Agency.app)
#   --preset <name>   low or high: fixed settings in a 1280x800 window, the same on every Mac (default: yours)
#
# Needs StarMap in the app (extras/starmap/port-starmap.sh; it is added when missing). The benchmark mod is built on
# this Mac against your copy of the game, with Microsoft's .NET SDK (downloaded once into deps/).
set -eo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
source "${HERE}/../../scripts/lib.sh"

# Microsoft's .NET SDK for building the mod (the feature band of the runtime in the app), SHA-512 from Microsoft's
# release metadata (https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json)
SDK_VERSION="10.0.113"
SDK_URL="https://builds.dotnet.microsoft.com/dotnet/Sdk/${SDK_VERSION}/dotnet-sdk-${SDK_VERSION}-osx-arm64.tar.gz"
SDK_SHA512="1fe7e83b47037e175611504f0b238a18d95589e1413a22ea44de43d8a5bffc283b9aadbd5b2c4de3cef243ac5d0167f2c75d22e5e719ecea4b9e08707ca1050a"

ALL_SCENES=(orbit flyby jupiter launch giga explosion sea iva eva)

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
INSTANCE="${DEPS}/benchmark/instance"
SDK="${DEPS}/dotnet-sdk/${SDK_VERSION}"
REPORT="${DEPS}/benchmark/report/KsaMetalBenchmarkReport.dll"

usage() {
    sed -n '2,15p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

fetch_sdk() {
    [ -x "${SDK}/dotnet" ] && return 0
    step "Fetching the .NET ${SDK_VERSION} SDK (Microsoft), to build the benchmark mod"
    local archive="${DEPS}/dotnet-sdk/dotnet-sdk-${SDK_VERSION}-osx-arm64.tar.gz"
    download "${SDK_URL}" "${archive}" 512 "${SDK_SHA512}"
    rm -rf "${SDK}"
    mkdir -p "${SDK}"
    tar -xzf "${archive}" -C "${SDK}"
    rm -f "${archive}"
}

# build <project> <output folder> [msbuild arguments...]
build() {
    local project="$1" out="$2"
    shift 2
    mkdir -p "${DEPS}/benchmark"
    DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
        DOTNET_CLI_HOME="${DEPS}/benchmark/home" NUGET_PACKAGES="${DEPS}/benchmark/nuget" \
        "${SDK}/dotnet" build "${project}" -c Release -nologo -v quiet -nodeReuse:false -o "${out}" "$@" \
        > "${DEPS}/benchmark/build.log" 2>&1 || { cat "${DEPS}/benchmark/build.log"; return 1; }
}

report() {
    "${SDK}/dotnet" "${REPORT}" "$@"
}

if [ "$1" = compare ]; then
    [ $# = 3 ] || usage 1
    fetch_sdk
    build "${HERE}/report/KsaMetalBenchmarkReport.csproj" "${DEPS}/benchmark/report" || die "the report program did not build."
    report compare "$2" "$3"
    exit
fi

APP="${DEFAULT_APP}"
SCENES=()
PRESET=""
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        --preset) PRESET="$2"; shift ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SCENES+=("$1") ;;
    esac
    shift
done
[ ${#SCENES[@]} -gt 0 ] || SCENES=("${ALL_SCENES[@]}")
case "${PRESET}" in
    "") PRESET_SETTINGS=() ;;
    low) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_LOW[@]}") ;;
    high) PRESET_SETTINGS=("${PRESET_COMMON[@]}" "${PRESET_HIGH[@]}") ;;
    *) die "unknown preset '${PRESET}' (low or high)." ;;
esac
for s in "${SCENES[@]}"; do
    [ -f "${HERE}/scenes/${s}.txt" ] || die "no scene '${s}' (extras/benchmark/scenes/)."
done

check_mac
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."
RES="${APP}/Contents/Resources"
[ -f "${DOCS}/settings.toml" ] || die "start the game once before benchmarking it (it has no settings yet)."
pgrep -f "dotnet (KSA|StarMap).dll" > /dev/null && die "the game is running. Quit it first."

# 1. StarMap, the mod and the report program
if [ ! -f "${RES}/StarMap/StarMap.dll" ]; then
    bash "${HERE}/../starmap/port-starmap.sh" --app "${APP}"
fi
fetch_sdk
step "Building the benchmark mod against KSA ${INSTALLED}"
MOD_OUT="${DEPS}/benchmark/mod"
build "${HERE}/mod/KsaMetalBenchmark.csproj" "${MOD_OUT}" -p:KsaDir="${RES}" ||
    die "the benchmark mod did not build (does this KSA version still match?)."
build "${HERE}/report/KsaMetalBenchmarkReport.csproj" "${DEPS}/benchmark/report" || die "the report program did not build."

# 2. The game's documents folder for the run, made fresh: the game itself and the benchmark mod (none of your mods,
#    so they neither change the numbers nor stop the game from starting), the bundled vehicles, and Gemini 7 from
#    the game's own vehicles (the EVA scene's capsule)
rm -rf "${INSTANCE}"
mkdir -p "${INSTANCE}/mods/${MOD_ID}" "${INSTANCE}/Vehicles"
printf '[[mods]]\nid = "Core"\nenabled = true\n\n[[mods]]\nid = "%s"\nenabled = true\n' "${MOD_ID}" \
    > "${INSTANCE}/manifest.toml"
cp "${HERE}/mod/mod.toml" "${MOD_OUT}/KsaMetalBenchmark.dll" "${INSTANCE}/mods/${MOD_ID}/"
cp -R "${HERE}/vehicles/"* "${INSTANCE}/Vehicles/"
cp -R "${RES}/Content/Core/defaultvehicles/Gemini7" "${INSTANCE}/Vehicles/" 2> /dev/null ||
    warn "this KSA has no Gemini7 vehicle: the EVA scene will fail."

# The run's settings: yours, with the preset on top. Each scene starts from these and adds its own.
BASE="${DEPS}/benchmark/settings.toml"
cp "${DOCS}/settings.toml" "${BASE}"
# set_setting <file> <key> <value>: one line of a settings.toml (keys are unique across its sections)
set_setting() {
    grep -q "^$2 = " "$1" || die "settings.toml has no '$2' (does this KSA version still match?)."
    sed -i '' "s|^$2 = .*|$2 = $3|" "$1"
}
setting() {
    sed -n "s/^$1 = //p" "${BASE}" | tr -d '"'
}
# The game starts straight into the scene's start, without its system menu, update popup or name labels
for kv in selectSystemOnStart=false checkForUpdates=false celestialNames=false locationNames=false \
    ${PRESET_SETTINGS[@]+"${PRESET_SETTINGS[@]}"}; do
    set_setting "${BASE}" "${kv%%=*}" "${kv#*=}"
done

# 3. The run
OUT="${DOCS}/benchmarks/$(date +%Y-%m-%d_%H%M)"
mkdir -p "${OUT}"
cp "${BASE}" "${OUT}/settings.toml"
# What the report says about this Mac and the run (values first: bash 3.2 misreads quotes in $() inside "")
gpu_cores="$(system_profiler SPDisplaysDataType 2> /dev/null | awk -F': ' '/Total Number of Cores/ { print $2; exit }')"
displays="$(system_profiler SPDisplaysDataType 2> /dev/null | awk -F': ' '/Resolution:/ { print $2 }' | paste -sd ';' - | sed 's/;/; /g')"
power="$(pmset -g batt | head -1 | sed -n "s/.*'\(.*\)'.*/\1/p")"
low_power="$(pmset -g | awk '/lowpowermode/ { print ($2 == 1 ? "on" : "off"); exit }')"
switches="$(env | grep '^KSA_MTL_' | sed "s|${HOME}|~|g" | paste -sd ' ' - || true)"
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
    if have_dev_tools && [ -d "${REPO}/.git" ]; then
        echo "Port: $(git -C "${REPO}" describe --always --dirty 2> /dev/null)"
    fi
    echo "StarMap: $(cat "${RES}/StarMap/version.txt" 2> /dev/null || echo unknown)"
    echo "Preset: ${PRESET:-none, own settings}"
    echo "Window: $(setting width) x $(setting height), $(setting mode), on $(setting screenName)"
    echo "V-Sync: $(setting vsync), FPS limit $(setting fpslimit)"
    echo "Anti-aliasing: $(setting antialiasing)"
    echo "KSA Metal switches: ${switches:-none}"
} > "${OUT}/system.txt"
# Keeps the display awake while the script runs (the game needs it)
caffeinate -d -i -w $$ &

step "Benchmarking KSA ${INSTALLED}: ${SCENES[*]}"
info "The game takes over the screen. Leave the mouse and keyboard alone until it is done."
for s in "${SCENES[@]}"; do
    scene="${HERE}/scenes/${s}.txt"
    cp "${BASE}" "${INSTANCE}/settings.toml"
    start="$(sed -n 's/^start //p' "${scene}")"
    set_setting "${INSTANCE}/settings.toml" startGameType "\"${start:-Sandbox}\""
    while read -r key value; do
        set_setting "${INSTANCE}/settings.toml" "${key}" "${value}"
    done < <(sed -n 's/^setting //p' "${scene}")
    # Loading takes up to a few minutes on a first start (shader compilation), then the scene's own length
    length="$(awk '$1 ~ /^[0-9.]+$/ && $1 > m { m = $1 } END { print int(m) }' "${scene}")"
    timeout=$((length + 300))

    info "${s} (about $((length + 20)) s)"
    # A display that went to sleep makes the game fail at start
    caffeinate -u -t 1
    STARMAP_INSTANCE_PATH="${INSTANCE}" KSA_BENCH_SCENE="${scene}" KSA_BENCH_OUT="${OUT}" \
        "${APP}/Contents/MacOS/KSA" > /dev/null 2>&1 &
    pid=$!
    waited=0
    while kill -0 "${pid}" 2> /dev/null; do
        if [ "${waited}" -ge "${timeout}" ]; then
            warn "${s} did not finish in ${timeout} s: stopped."
            kill -9 "${pid}" 2> /dev/null || true
            break
        fi
        sleep 1
        waited=$((waited + 1))
    done
    wait "${pid}" 2> /dev/null || true
    # The log, without your home folder's name
    sed "s|${HOME}|~|g" "${INSTANCE}/logs/console.log" > "${OUT}/${s}.log" 2> /dev/null || true
    [ -f "${OUT}/${s}.json" ] || warn "${s} wrote no results: see ${OUT}/${s}.log"
done

report report "${OUT}" > /dev/null
echo ""
cat "${OUT}/report.md"
echo ""
step "Done: ${OUT}"
info "Compare with another run: extras/benchmark/benchmark.sh compare <other run folder> \"${OUT}\""
