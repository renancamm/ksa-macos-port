#!/bin/bash
# Makes the README's pictures: the scene a new game starts on (the game's test vessel in orbit) and the game's settings
# open on the Mac Port tab, each with Apple's Metal Performance HUD, in the installed app at a fresh install's settings
# in a 1280x800 window, muted, without the Welcome popup. The benchmark plays its readme scenario
# (src/game/benchmark/lineup/benchmark.txt) with the window tool, which takes a picture of the game's window at each
# view; this script makes them JPEGs in .github/images/ and writes the caption under the first into README.md.
#
# Usage: scripts/readme-screenshots.sh [--app <path>]
#   Needs the port folder (./build.sh --install first, so that the app's benchmark has the readme scenario), the display
#   awake, and the Screen Recording permission for the terminal that runs it (System Settings > Privacy & Security >
#   Screen & System Audio Recording). The game takes over the screen for about two minutes.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

APP="${HOME}/Applications/${APP_NAME}.app"
while [ $# -gt 0 ]; do
    case "$1" in
        --app) APP="${2%/}"; shift ;;
        -h | --help) sed -n '2,/^set -eo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 0 ;;
        *) die "no option $1 (--app <path>)." ;;
    esac
    shift
done
[ -x "${OUT}/benchmark.sh" ] || die "no port folder: run ./build.sh --install first."
[ -d "${APP}" ] || die "no app at ${APP}."

IMAGES="${REPO}/.github/images"
LOG="${BUILD}/readme-screenshots.log"
mkdir -p "${IMAGES}" "${BUILD}"

step "Playing the README's views in ${APP}"
"${OUT}/benchmark.sh" investigate window boot macport --app "${APP}" --defaults \
    --set 'mode="Windowed"' --set width=1280 --set height=800 --set masterVolume=0 --set welcomePopup=false \
    --port metal_performance_hud=true 2>&1 | tee "${LOG}" || warn "the run had problems: ${LOG}"
pictures="$(sed -n 's/^.*Window: \(.*\) (a picture of.*$/\1/p' "${LOG}" | tail -1)"
[ -d "${pictures}" ] || die "the run left no pictures of the window (${LOG})."

# JPEGs at most 1920 pixels wide: sharp on a Retina display at the README's width, a few hundred KB each
picture() {
    [ -f "${pictures}/readme_$1.png" ] || die "no picture of $1 in ${pictures}."
    sips -s format jpeg -s formatOptions 85 -Z 1920 "${pictures}/readme_$1.png" --out "${IMAGES}/$2" > /dev/null
    info "${IMAGES#"${REPO}/"}/$2"
}
picture boot game.jpg
picture macport mac-port-tab.jpg

# The caption under the game's picture: the Mac, the KSA version and the settings
hardware="$(system_profiler SPHardwareDataType)"
model="$(sed -n 's/^ *Model Name: //p' <<< "${hardware}")"
chip="$(sed -n 's/^ *Chip: //p' <<< "${hardware}")"
ksa="$(app_version "${APP}")"
caption="${model:-A Mac}${chip:+ with ${chip}}${ksa:+, KSA ${ksa}}, the game's default settings in a 1280x800 window"
# The caption as sed's replacement: its \, & and | escaped
escaped="${caption//\\/\\\\}"
escaped="${escaped//&/\\&}"
escaped="${escaped//|/\\|}"
sed -i '' "s|<sub>[^<]*</sub>\(.*readme-screenshots.sh.*\)$|<sub>${escaped}</sub>\1|" "${REPO}/README.md"
grep -qF "<sub>${caption}</sub>" "${REPO}/README.md" || warn "README.md has no caption line to write: ${caption}"
step "Done: ${caption}"
