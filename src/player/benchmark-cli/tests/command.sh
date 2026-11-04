#!/bin/bash
# The port folder's benchmark.sh against a stand-in for the game: its commands, what it refuses before a game starts, and
# how a run ends (passed, crashed, hung, stopped with Ctrl-C). No game plays, nothing takes over the screen, and everything
# happens under a home folder of its own. ./build.sh runs it after making the port folder; a failure stops the build.
#
# Usage: src/player/benchmark-cli/tests/command.sh [<port folder>]
set -uo pipefail
REPO="$(cd "$(dirname "$0")/../../.." && pwd)"
PORT_FOLDER="${1:-${REPO}/dist/ksa-macos-port}"
BENCH="${PORT_FOLDER}/benchmark.sh"
RESOURCES="${PORT_FOLDER}/payload/app/Contents/Resources"

# benchmark.sh refuses to play beside a real game: then there is nothing to test here, and nothing to disturb
if pgrep -f "dotnet (KSA|StarMap).dll" > /dev/null 2>&1; then
    echo "    The benchmark script's tests: skipped (the game is running)."
    exit 0
fi

# A short path: the run's home folder shows in what is printed. mktemp -d fails in some sandboxes, so a plain name it is.
T="${TMPDIR:-/tmp}/ksa-bench-script-tests-$$"
rm -rf "${T}"
mkdir -p "${T}/home/Documents/My Games/Kitten Space Agency"
trap 'rm -rf "${T}"' EXIT
cat > "${T}/home/Documents/My Games/Kitten Space Agency/settings.toml" <<'SETTINGS'
[display]
width = 1512
height = 982
mode = "Fullscreen"
vsync = true
[game]
selectSystemOnStart = true
lastSystemId = "Sol"
startGameType = "Normal"
startVehicle = "x"
startParent = "x"
startSituation = "x"
checkForUpdates = true
displayChangelogOnChange = true
celestialNames = true
locationNames = true
SETTINGS

# The stand-in: an app by this port's bundle id whose game writes what the director writes, then does what FAKE_GAME says
APP="${T}/Fake Game.app"
mkdir -p "${APP}/Contents/MacOS" "${APP}/Contents/Resources/Hooks" "${APP}/Contents/Resources/Benchmark"
touch "${APP}/Contents/Resources/Hooks/KsaMetal.Hooks.dll"
cp "${RESOURCES}/Benchmark/benchmark.txt" "${APP}/Contents/Resources/Benchmark/"
cp "${RESOURCES}/port-settings.txt" "${APP}/Contents/Resources/" 2> /dev/null
cat > "${APP}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<plist version="1.0"><dict>
<key>CFBundleIdentifier</key><string>$(sed -n 's/^BUNDLE_ID="\(.*\)"/\1/p' "${PORT_FOLDER}/lib/port.env")</string>
<key>CFBundleShortVersionString</key><string>$(sed -n 's/^ksa=//p' "${PORT_FOLDER}/VERSION")</string>
<key>KSAPortVersion</key><string>test</string><key>KSAPortCommit</key><string>test</string>
</dict></plist>
PLIST
cat > "${APP}/Contents/MacOS/KSA" <<'GAME'
#!/bin/bash
mkdir -p "${STARMAP_INSTANCE_PATH}/logs"
echo "==> port settings: defaults" > "${STARMAP_INSTANCE_PATH}/logs/console.log"
case "${FAKE_GAME:-pass}" in
    nothing) exit 134 ;;
esac
# The run's options, which benchmark.sh's program writes for the director
option() { plutil -extract "$1" raw -o - "${KSA_BENCH_OUT}/options.json"; }
only="$(option only)"
first="${only%% *}"
session="$(option session)"
events="${KSA_BENCH_OUT}/events.jsonl"
event() { echo "{\"at\":\"$(date -u +%Y-%m-%dT%H:%M:%SZ)\",\"session\":${session},\"pass\":1,$1}" >> "${events}"; }
event "\"type\":\"heartbeat\",\"scenario\":\"${first}\",\"value\":1.5"
case "${FAKE_GAME:-pass}" in
    crash) kill -ABRT $$ ;;
    hang) sleep 60; exit 0 ;;
    long) for i in 1 2 3 4 5 6 7 8 9 10; do sleep 1; event "\"type\":\"heartbeat\",\"scenario\":\"${first}\",\"value\":1.5"; done; exit 0 ;;
esac
event "\"type\":\"scenario\",\"scenario\":\"${first}\",\"text\":\"views\""
event "\"type\":\"open\",\"scenario\":\"${first}\",\"part\":\"part1\",\"game\":0,\"value\":0"
frames="${KSA_BENCH_OUT}/frames.csv"
[ -s "${frames}" ] || echo "time,session,pass,script,scenario,part,phase,frame_ms,present,gpu_ms,cpu_ms,draws,passes,metal_mb,footprint_mb,thermal,shaders,pipelines,display_ms,game_s,asked,sim_ms,substeps,bubbles,vehicles" > "${frames}"
for i in $(seq 1 100); do
    echo "${i},${session},1,0,${first},part1,Measure,20,${i},${FAKE_GPU:-15},1,100,4,1000,2000,0,0,0,8.3,,,,,," >> "${frames}"
done
event "\"type\":\"close\",\"scenario\":\"${first}\",\"part\":\"part1\""
event "\"type\":\"done\""
exit 0
GAME
chmod +x "${APP}/Contents/MacOS/KSA"

failures=0
fail() {
    echo "FAILED $1"
    failures=$((failures + 1))
}
# bench <args...>: benchmark.sh under the home folder of its own, its output in OUTPUT and its exit status in STATUS
bench() {
    OUTPUT="$(HOME="${T}/home" "${BENCH}" "$@" 2>&1)"
    STATUS=$?
}
expect() { # <what> <exit status> <text in the output>
    [ "${STATUS}" = "$2" ] || fail "$1: exit status ${STATUS}, not $2: ${OUTPUT}"
    case "${OUTPUT}" in
        *"$3"*) ;;
        *) fail "$1: no '$3' in: ${OUTPUT}" ;;
    esac
}
runs_count() { ls -1 "${T}/home/Documents/My Games/Kitten Space Agency/benchmarks" 2> /dev/null | wc -l | tr -d ' '; }

# Before any run
bench list
expect "list" 0 "mixed"
expect "list's places" 0 "earth4"
bench runs
expect "runs, none yet" 0 "No runs yet."
bench runs nonsense
expect "runs, not a number" 1 "takes a number of runs"
bench report
expect "report, no run yet" 1 "there is no last run"
bench compare
expect "compare, no run yet" 2 "there is no previous run"

# Refused before a game starts
bench --app "${APP}" mxied
expect "a test's name mistyped" 1 "./benchmark.sh list shows them"
bench --app "${APP}" --set
expect "an option without its value" 1 "--set needs a value"
bench --app "${APP}" --nonsense
expect "an unknown option" 1 "no option --nonsense"
bench --app "${APP}" --port nothing_like_this=true
expect "--port: an unknown setting" 1 "there is no setting"
if [ -f "${RESOURCES}/port-settings.txt" ]; then
    bench --app "${APP}" --port debris_settles_in_warp=1
    expect "--port: a bool with 1" 1 "it takes true or false"
    bench --app "${APP}" --port upscaling=fxaa
    expect "--port: a value not in the choice" 1 "it takes off or metalfx or spatial"
    bench --app "${APP}" --port render_scale=9
    expect "--port: a number out of range" 1 "it takes a number from 0.5 to 2"
fi
bench --app "${APP}" --compare nothing_like_this
expect "--compare: no such run" 1 "no run 'nothing_like_this'"
[ "$(runs_count)" = 0 ] || fail "a refused run left a folder"

# A run that passes
FAKE_GAME=pass bench --app "${APP}"
expect "a run that passes" 0 "Passed"
expect "its numbers, aligned" 0 "part1"
expect "how to see it" 0 "./benchmark.sh report"
case "${OUTPUT}" in *"|---"*) fail "Markdown in the terminal: ${OUTPUT}" ;; esac
RUN="$(ls "${T}/home/Documents/My Games/Kitten Space Agency/benchmarks" | sort | tail -1)"
DIR="${T}/home/Documents/My Games/Kitten Space Agency/benchmarks/${RUN}"
for f in report.md run.json report.html events.jsonl frames.csv options.json settings.toml "${RUN}.zip"; do
    [ -f "${DIR}/${f}" ] || fail "a run that passes: no ${f}"
done

# A relative --app, comparing by name, "last" and "previous"
sleep 1
(cd "${T}" && FAKE_GPU=30 HOME="${T}/home" "${BENCH}" --app "Fake Game.app" --compare last > "${T}/second.txt" 2>&1)
STATUS=$?
OUTPUT="$(cat "${T}/second.txt")"
expect "a relative --app with --compare last" 0 "GPU time 100 % higher"
bench runs
expect "runs" 0 "2 of 2 runs"
bench report last
expect "report last" 0 "Passed"
bench compare previous last
expect "compare by keyword" 0 "100 % slower"
bench compare "${RUN}"
expect "compare by a folder's name, against the last" 0 "100 % slower"
bench compare
expect "compare, no runs named" 0 "100 % slower"
bench --app "${APP}" tour --compare last
expect "--compare: another kind, refused before the game starts" 1 "views mode"
[ "$(runs_count)" = 2 ] || fail "--compare refused, yet a run started"

# How a run ends
sleep 1
FAKE_GAME=crash bench --app "${APP}"
expect "a crash" 1 "crashed (signal 6) in mixed at 1.5 s"
expect "a crash: done with problems" 1 "Done, with problems"
sleep 1
FAKE_GAME=nothing bench --app "${APP}"
expect "a game that dies at the start" 1 "before its first frame"
sleep 1
KSA_BENCH_HANG_LIMIT=2 FAKE_GAME=hang bench --app "${APP}"
expect "a game that hangs" 1 "hung in mixed at 1.5 s"
sleep 1
# As a terminal does it: the signal to the whole process group, once the run has started (a job started with & has SIGINT
# ignored and shares this script's group: perl gives it a group of its own and puts the signal back)
before="$(ls -d "${T}/home/Documents/My Games/Kitten Space Agency/benchmarks/"*/options.json | wc -l)"
FAKE_GAME=long HOME="${T}/home" perl -e 'setpgrp(0, 0); $SIG{INT} = "DEFAULT"; exec @ARGV' "${BENCH}" --app "${APP}" > "${T}/stopped.txt" 2>&1 &
script=$!
for _ in $(seq 1 100); do
    [ "$(ls -d "${T}/home/Documents/My Games/Kitten Space Agency/benchmarks/"*/options.json | wc -l)" -gt "${before}" ] && break
    sleep 0.2
done
sleep 1
kill -INT -- "-${script}"
wait "${script}"
STATUS=$?
OUTPUT="$(cat "${T}/stopped.txt")"
expect "Ctrl-C" 1 "you stopped the run"
pgrep -f "${APP}/Contents/MacOS/KSA" > /dev/null
[ $? = 1 ] || fail "Ctrl-C left the game running (or pgrep can't look)"

# Clean: only runs, the oldest
B="${T}/home/Documents/My Games/Kitten Space Agency/benchmarks"
mkdir "${B}/2020-01-01_0100" "${B}/2020-01-02_0100_hold" "${B}/2020-01-03_0100_explore" "${B}/a baseline" "${B}/zz renamed"
# A renamed run isn't the last one
cp "${DIR}/run.json" "${B}/zz renamed/"
bench report last
case "${OUTPUT}" in *"zz renamed"*) fail "last took a folder that isn't named by its date: ${OUTPUT}" ;; esac
touch "${B}/stray.zip"
HOME="${T}/home" "${BENCH}" clean --keep 1 < /dev/null > "${T}/clean.txt" 2>&1
STATUS=$?
OUTPUT="$(cat "${T}/clean.txt")"
expect "clean without a terminal only says" 1 "it asks first"
[ -d "${B}/2020-01-01_0100" ] || fail "clean deleted runs without asking"
newest="$(ls "${B}" | grep -E '^20' | grep -v explore | sort | tail -1)"
bench clean --keep 1 --yes
expect "clean" 0 "Deleted."
[ ! -e "${B}/2020-01-01_0100" ] && [ ! -e "${B}/2020-01-02_0100_hold" ] || fail "clean left old runs"
[ -d "${B}/${newest}" ] || fail "clean deleted the newest run"
[ -d "${B}/2020-01-03_0100_explore" ] && [ -d "${B}/a baseline" ] && [ -f "${B}/stray.zip" ] || fail "clean deleted what isn't a run"
bench clean --keep 1 --yes
expect "clean with nothing to do" 0 "Nothing to delete"

if [ "${failures}" = 0 ]; then
    echo "    The benchmark script's tests: passed."
else
    echo "    The benchmark script's tests: ${failures} failures."
    exit 1
fi
