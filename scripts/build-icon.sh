#!/bin/bash
# The app icon: AppIcon.icns and its notice, NOTICE.txt, in <out dir>. The build runs it (cmake/port-folder.cmake).
#
# The kitten astronaut from KSA's title card (TITLE_CARD_URL in cmake/dependencies.cmake): the bottom 898x898 of the
# 900x1350 card.
#
# Usage: scripts/build-icon.sh <title card> <its URL> <out dir>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

[ $# = 3 ] || die "usage: scripts/build-icon.sh <title card> <its URL> <out dir>"
CARD="$1"
TITLE_CARD_URL="$2"
OUT_DIR="$3"
[ -s "${CARD}" ] || die "the title card was not downloaded. Run ./build.sh."

TMP="$(mktemp -d)"
rm -rf "${OUT_DIR}"
mkdir -p "${OUT_DIR}" "${TMP}/AppIcon.iconset"
sips -s format png -c 898 898 --cropOffset 452 1 "${CARD}" --out "${TMP}/card.png" > /dev/null ||
    die "${TITLE_CARD_URL} is not the title card it was: the icon needs a new crop in scripts/build-icon.sh."
for size in 16 32 128 256 512; do
    sips -z ${size} ${size} "${TMP}/card.png" --out "${TMP}/AppIcon.iconset/icon_${size}x${size}.png" > /dev/null
    sips -z $((size * 2)) $((size * 2)) "${TMP}/card.png" --out "${TMP}/AppIcon.iconset/icon_${size}x${size}@2x.png" > /dev/null
done
iconutil -c icns "${TMP}/AppIcon.iconset" -o "${OUT_DIR}/AppIcon.icns"
rm -rf "${TMP}"

cat > "${OUT_DIR}/NOTICE.txt" << EOF
App icon (AppIcon.icns)

The icon is cropped from the title card of Kitten Space Agency (${TITLE_CARD_URL}).
The artwork is copyright Ahwoo Limited (RocketWerkz). All rights reserved by its owner.

It is used only to identify the game the app runs. It is not covered by the MIT License of this project or by
any other licence in this folder. It will be removed on request of its owner.
EOF
