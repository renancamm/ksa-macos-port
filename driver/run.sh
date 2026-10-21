#!/bin/bash
# Test run of the game on the KSA Metal driver: exits after a number of presents and saves screenshots.
# Usage: driver/run.sh <name> <exit-after-presents> <shot presents> [SETTING=VALUE ...] [-- ENV=VALUE ...]
#   SETTING   a settings.toml key for this run only, e.g. antialiasing='"Cmaa2"'; settings.toml is backed up and
#             always restored
#   KSA_APP overrides the app path; KSA_RUN_TIMEOUT kills the game after that many seconds (default 180)
# Output: deps/captures/<name>.log, <name>.ksamtl.csv, deps/ksamtl-runs/<name>/shot_<n>.png
set -u
NAME="$1"; EXIT_AFTER="$2"; SHOTS="$3"; shift 3
SETTINGS=(); while [ $# -gt 0 ] && [ "$1" != "--" ]; do SETTINGS+=("$1"); shift; done
[ "${1:-}" = "--" ] && shift
REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="${KSA_APP:-$HOME/Applications/Kitten Space Agency.app}"
RES="$APP/Contents/Resources"
CFG="$HOME/Documents/My Games/Kitten Space Agency/settings.toml"
BACKUP="$CFG.before-run"
OUT="$REPO/deps/captures"
SHOTDIR="$REPO/deps/ksamtl-runs/$NAME"
TIMEOUT="${KSA_RUN_TIMEOUT:-180}"
[ -f "$REPO/deps/ksamtl/libvulkan.1.dylib" ] || { echo "build the driver first: driver/build.sh"; exit 1; }
[ -f "$RES/KSA.dll" ] || { echo "game not found at $APP (set KSA_APP)"; exit 1; }
mkdir -p "$OUT"; rm -rf "$SHOTDIR"; mkdir -p "$SHOTDIR"

gamepids() { ps -axo pid,command | grep -E "dotnet (KSA|StarMap).dll" | grep -v grep | awk '{print $1}'; }
killgame() { for q in $(gamepids); do kill ${1:-} "$q" 2>/dev/null; done; }
trap 'killgame -9; [ -f "$BACKUP" ] && mv "$BACKUP" "$CFG"' EXIT

killgame -9; sleep 1
[ -f "$BACKUP" ] || cp "$CFG" "$BACKUP"
cp "$BACKUP" "$CFG"
# Defaults: what the driver supports today (no tessellation feature is reported, so the game skips it anyway);
# selectSystemOnStart is the "show config" checkbox: with it on the game waits in the config window
DEFAULTS=(selectSystemOnStart=false checkForUpdates=false groundClutter=false terrainTessellation=false)
for kv in "${DEFAULTS[@]}" ${SETTINGS[@]+"${SETTINGS[@]}"}; do
  k="${kv%%=*}"; v="${kv#*=}"
  grep -q "^${k} = " "$CFG" || { echo "unknown setting: $k"; exit 2; }
  /usr/bin/sed -i '' "s|^${k} = .*|${k} = ${v}|" "$CFG"
done

# Our libvulkan.1.dylib first: Brutal (via libVulkanEx), VMA and GLFW all resolve Vulkan from it.
# The proxy variables point at a closed port so the game makes no network calls during test runs.
ENVS=(PATH="$APP/Contents/MacOS:$PATH" DOTNET_ROOT="$APP/Contents/MacOS" DYLD_LIBRARY_PATH="$REPO/deps/ksamtl:$RES"
  KSA_MTL_LOG="${KSA_MTL_LOG:-1}" KSA_MTL_FRAMES="$OUT/$NAME.ksamtl.csv" KSA_MTL_EXIT_AFTER="$EXIT_AFTER"
  KSA_MTL_SHOTS="$SHOTS" KSA_MTL_SHOT_DIR="$SHOTDIR" HTTP_PROXY=http://127.0.0.1:9 http_proxy=http://127.0.0.1:9 "$@")
[ -f "$RES/libGlfwShim.dylib" ] && ENVS+=(DYLD_INSERT_LIBRARIES="$RES/libGlfwShim.dylib")

cd "$RES" || exit 1
START=$(date +%s)
env "${ENVS[@]}" "$APP/Contents/MacOS/dotnet" KSA.dll > "$OUT/$NAME.log" 2>&1 &
P=$!
# KSA_RUN_FOOTPRINT_AT=<s>: sample `footprint` of the game process that many seconds after start
if [ -n "${KSA_RUN_FOOTPRINT_AT:-}" ]; then
  ( sleep "$KSA_RUN_FOOTPRINT_AT"; footprint -p "$P" > "$OUT/$NAME.footprint.txt" 2>&1 ) &
fi
while kill -0 "$P" 2>/dev/null; do
  # A game wedged in a GPU wait ignores SIGTERM: follow up with SIGKILL
  [ $(( $(date +%s) - START )) -ge "$TIMEOUT" ] && { echo "timeout after ${TIMEOUT}s, killing"; killgame; sleep 5; killgame -9; break; }
  sleep 1
done
wait "$P" 2>/dev/null; RC=$?
echo "$NAME: exit=$RC time=$(( $(date +%s) - START ))s log=$OUT/$NAME.log"
grep -E "^\[ksamtl" "$OUT/$NAME.log" | grep -v "first call" | sed -E 's/0x[0-9a-f]+/X/g' | sort | uniq -c | sort -rn | head -${KSA_RUN_LINES:-30}
echo "--- log tail"; tail -${KSA_RUN_TAIL:-6} "$OUT/$NAME.log"
