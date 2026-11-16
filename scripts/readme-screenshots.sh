#!/bin/bash
# Makes the README's pictures: the scene a new game starts on (the game's test vessel in orbit), with Apple's Metal
# Performance HUD, and the game's settings open on the Mac Port tab with the Quality card just clicked, in the
# installed app at the settings below, on a fresh install's, in a 1280x800 window, muted, without the Welcome popup.
# The benchmark plays its readme scenario (src/game/benchmark/lineup/benchmark.txt) with the window tool, which takes a
# picture of the game's window at each view once the HUD's frame rate has settled; this script makes PNGs of them in
# .github/images/.
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

# The game's settings, on a fresh install's. The card picked last is Game Defaults, so that the scenario's click turns
# Quality's card gold: a click on the card the settings are based on leaves it as it is. The tab's picture then shows
# Quality's values as pending changes.
SETTINGS=(--defaults --tab-preset quality --port upscaling=metalfx --port steady_frame_rate=60 --port preset=game_defaults)

IMAGES="${REPO}/.github/images"
LOG="${BUILD}/readme-screenshots.log"
mkdir -p "${IMAGES}" "${BUILD}"

step "Playing the README's views in ${APP}"
"${OUT}/benchmark.sh" investigate window boot macport --app "${APP}" "${SETTINGS[@]}" \
    --set 'mode="Windowed"' --set width=1280 --set height=800 --set masterVolume=0 --set welcomePopup=false \
    --port metal_performance_hud=true 2>&1 | tee "${LOG}" || warn "the run had problems: ${LOG}"
pictures="$(sed -n 's/^.*Window: \(.*\) (a picture of.*$/\1/p' "${LOG}" | tail -1)"
[ -d "${pictures}" ] || die "the run left no pictures of the window (${LOG})."

# PNGs at most 1600 pixels wide: sharp on a Retina display at the README's width. The game's picture is the window's,
# with the HUD and the window's transparent rounded corners; the tab's is the driver's screenshot of the view, without
# the HUD.
picture() {
    [ -f "$1" ] || die "no picture $1."
    sips -Z 1600 "$1" --out "${IMAGES}/$2" > /dev/null
    info "${IMAGES#"${REPO}/"}/$2"
}
picture "${pictures}/readme_boot.png" game.png
picture "${pictures}/../readme_macport.png" mac-port-tab.png

step "Done"
