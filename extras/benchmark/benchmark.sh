#!/bin/bash
# Measures how KSA runs on this Mac: plays a fixed set of scenes, one game start each, and writes a report to share.
#
# Usage: extras/benchmark/benchmark.sh [options] [scene...]
#   With no scenes, plays all of them. The game takes over the screen: leave the mouse and keyboard alone until it
#   is done. The game runs from its own documents folder, with a copy of your settings: your files stay untouched.
#
# Options:
#   --app <path>      the app to measure (default: ~/Applications/Kitten Space Agency.app)
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

ALL_SCENES=(launch giga)
MOD_ID="ksa-metal-benchmark"
DOCS="${HOME}/Documents/My Games/Kitten Space Agency"
# The game's documents folder during the run: StarMap points the game at it (STARMAP_INSTANCE_PATH)
INSTANCE="${DEPS}/benchmark/instance"

usage() {
    sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

APP="${DEFAULT_APP}"
SCENES=()
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) SCENES+=("$1") ;;
    esac
    shift
done
[ ${#SCENES[@]} -gt 0 ] || SCENES=("${ALL_SCENES[@]}")
for s in "${SCENES[@]}"; do
    [ -f "${HERE}/scenes/${s}.txt" ] || die "no scene '${s}' (extras/benchmark/scenes/)."
done

check_mac
INSTALLED="$(app_version "${APP}")"
[ -n "${INSTALLED}" ] || die "no app made by this port at ${APP}. Use ./port.sh to make one (or pass --app)."
RES="${APP}/Contents/Resources"
[ -f "${DOCS}/settings.toml" ] || die "start the game once before benchmarking it (it has no settings yet)."
pgrep -f "dotnet (KSA|StarMap).dll" > /dev/null && die "the game is running. Quit it first."

# 1. StarMap and the mod
if [ ! -f "${RES}/StarMap/StarMap.dll" ]; then
    bash "${HERE}/../starmap/port-starmap.sh" --app "${APP}"
fi

SDK="${DEPS}/dotnet-sdk/${SDK_VERSION}"
if [ ! -x "${SDK}/dotnet" ]; then
    step "Fetching the .NET ${SDK_VERSION} SDK (Microsoft), to build the benchmark mod"
    ARCHIVE="${DEPS}/dotnet-sdk/dotnet-sdk-${SDK_VERSION}-osx-arm64.tar.gz"
    download "${SDK_URL}" "${ARCHIVE}" 512 "${SDK_SHA512}"
    rm -rf "${SDK}"
    mkdir -p "${SDK}"
    tar -xzf "${ARCHIVE}" -C "${SDK}"
    rm -f "${ARCHIVE}"
fi

step "Building the benchmark mod against KSA ${INSTALLED}"
MOD_OUT="${DEPS}/benchmark/mod"
mkdir -p "${DEPS}/benchmark"
DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    DOTNET_CLI_HOME="${DEPS}/benchmark/home" NUGET_PACKAGES="${DEPS}/benchmark/nuget" \
    "${SDK}/dotnet" build "${HERE}/mod/KsaMetalBenchmark.csproj" -c Release -nologo -v quiet -nodeReuse:false \
    -p:KsaDir="${RES}" -o "${MOD_OUT}" > "${DEPS}/benchmark/build.log" 2>&1 ||
    { cat "${DEPS}/benchmark/build.log"; die "the benchmark mod did not build (does this KSA version still match?)."; }

# 2. The game's documents folder for the run, made fresh: your settings, the game itself and the benchmark mod (none
#    of your mods, so they neither change the numbers nor stop the game from starting), the bundled vehicles
rm -rf "${INSTANCE}"
mkdir -p "${INSTANCE}/mods/${MOD_ID}" "${INSTANCE}/Vehicles"
cp "${DOCS}/settings.toml" "${INSTANCE}/"
printf '[[mods]]\nid = "Core"\nenabled = true\n\n[[mods]]\nid = "%s"\nenabled = true\n' "${MOD_ID}" \
    > "${INSTANCE}/manifest.toml"
cp "${HERE}/mod/mod.toml" "${MOD_OUT}/KsaMetalBenchmark.dll" "${INSTANCE}/mods/${MOD_ID}/"
cp -R "${HERE}/vehicles/"* "${INSTANCE}/Vehicles/"

# set_setting <key> <value>: one line of the run's settings.toml (keys are unique across its sections)
set_setting() {
    grep -q "^$1 = " "${INSTANCE}/settings.toml" || die "settings.toml has no '$1' (does this KSA version still match?)."
    sed -i '' "s|^$1 = .*|$1 = $2|" "${INSTANCE}/settings.toml"
}
# The game starts straight into the scene's start, without its system menu, update popup or name labels
set_setting selectSystemOnStart false
set_setting checkForUpdates false
set_setting celestialNames false
set_setting locationNames false

# 3. The run
OUT="${DOCS}/benchmarks/$(date +%Y-%m-%d_%H%M)"
mkdir -p "${OUT}"
# Keeps the display awake while the script runs (the game needs it)
caffeinate -d -i -w $$ &

step "Benchmarking KSA ${INSTALLED}: ${SCENES[*]}"
info "The game takes over the screen. Leave the mouse and keyboard alone until it is done."
for s in "${SCENES[@]}"; do
    scene="${HERE}/scenes/${s}.txt"
    start="$(sed -n 's/^start //p' "${scene}")"
    set_setting startGameType "\"${start:-Sandbox}\""
    # Loading takes up to a few minutes on a first start (shader compilation), then the scene's own length
    length="$(awk '$1 ~ /^[0-9.]+$/ && $1 > m { m = $1 } END { print int(m) }' "${scene}")"
    timeout=$((length + 300))

    info "${s} (about $((length + 20)) s)"
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
    cp "${INSTANCE}/logs/console.log" "${OUT}/${s}.log" 2> /dev/null || true
    [ -f "${OUT}/${s}.json" ] || warn "${s} wrote no results: see ${OUT}/${s}.log"
done

step "Done: ${OUT}"
