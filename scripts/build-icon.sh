#!/bin/bash
# The app icon -> deps/build/icon/AppIcon.icns and its notice
#
# The kitten astronaut from KSA's title card (TITLE_CARD_URL in cmake/dependencies.cmake): the bottom 898x898 of the
# 900x1350 card.
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

step "Making the app icon"
CARD="${TITLE_CARD}"
[ -s "${CARD}" ] || die "the title card was not downloaded. Run ./build.sh."

OUT_DIR="${BUILD}/icon"
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
