#!/bin/bash
# ==============================================================================
# Kitten Space Agency (KSA) - Ported Game Folder -> Double-Clickable macOS .app
#
# Moves an already-ported game folder (see port_game.sh) into an app bundle and
# bundles a private copy of the .NET 10 runtime, so the game starts by simply
# opening the app - no Terminal and no system-wide .NET install required.
#
# Usage:
#   ./make_app.sh /path/to/ported_game_dir [/path/to/App.app]
#
# App icon:       app/AppIcon.icns if present, otherwise built from KSA's title card,
#                 downloaded at build time (game artwork is never stored in this repo)
# Default output: ~/Applications/Kitten Space Agency.app
# Game updates:   if the app already exists (built by this script), the new app replaces
#                 it and the old one goes to the Trash
# Undo:           mv "<App>.app/Contents/Resources" /path/to/ported_game_dir
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_NAME="Kitten Space Agency"
TITLE_CARD_URL="https://ahwoo.com/ksa/title-card.jpg"

GAME_DIR="${1%/}"
APP="${2:-${HOME}/Applications/${APP_NAME}.app}"

if [ -z "${GAME_DIR}" ] || [ ! -f "${GAME_DIR}/KSA.dll" ]; then
    echo "Usage: ./make_app.sh /path/to/ported_game_dir [/path/to/App.app]"
    echo "Error: Target folder must contain KSA.dll."
    exit 1
fi

for f in libMoltenVK.dylib MoltenVK_icd.json KSA.deps.json.bak; do
    if [ ! -e "${GAME_DIR}/${f}" ]; then
        echo "Error: ${GAME_DIR}/${f} not found - this folder has not been ported yet."
        echo "       Run ./port_game.sh \"${GAME_DIR}\" first."
        exit 1
    fi
done

BUNDLE_ID="io.github.ksa-macos-port.ksa"
OLD_APP=""
if [ -e "${APP}" ]; then
    if [ "$(/usr/libexec/PlistBuddy -c "Print :CFBundleIdentifier" "${APP}/Contents/Info.plist" 2>/dev/null)" != "${BUNDLE_ID}" ]; then
        echo "Error: ${APP} already exists and was not built by make_app.sh."
        echo "       Remove it or pass a different output path."
        exit 1
    fi
    OLD_APP="${APP}"
    OLD_VERSION="$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "${APP}/Contents/Info.plist" 2>/dev/null || echo old)"
    # Build next to the old app and swap at the end, so a failed build leaves it untouched
    APP="$(dirname "${OLD_APP}")/.$(basename "${OLD_APP}").new"
    if [ -e "${APP}" ]; then
        echo "Error: ${APP} is left over from a failed update."
        echo "       Move its Contents/Resources folder back out if you need it, delete it, and run this script again."
        exit 1
    fi
fi

# 1. Locate a .NET 10 runtime to bundle
echo "==> [1/5] Locating .NET 10 runtime..."
if [ -n "${DOTNET_ROOT}" ] && [ -x "${DOTNET_ROOT}/dotnet" ]; then
    DOTNET_HOME="${DOTNET_ROOT}"
elif command -v dotnet &> /dev/null; then
    DOTNET_HOME="$(dirname "$(realpath "$(command -v dotnet)")")"
fi

NETCORE_DIR="$(ls -d "${DOTNET_HOME}"/shared/Microsoft.NETCore.App/10.* 2>/dev/null | sort -V | tail -1)"
FXR_DIR="$(ls -d "${DOTNET_HOME}"/host/fxr/* 2>/dev/null | sort -V | tail -1)"

if [ -z "${NETCORE_DIR}" ] || [ -z "${FXR_DIR}" ]; then
    echo "Error: no .NET 10 runtime found to bundle."
    echo "       Install it with: brew install --cask dotnet-sdk"
    exit 1
fi
echo "    Using $(basename "${NETCORE_DIR}") from ${DOTNET_HOME}"

GAME_DIR="$(cd "${GAME_DIR}" && pwd)"
# Game version: from KSA.dll (e.g. 2026.10.13.5602), else from the folder name, else 1.0
VERSION="$(LC_ALL=C grep -aoE '20[0-9]{2}\.[0-9]{1,2}\.[0-9]{1,2}\.[0-9]+' "${GAME_DIR}/KSA.dll" | head -1)"
VERSION="${VERSION:-$(basename "${GAME_DIR}" | grep -oE 'v[0-9]+(\.[0-9]+)+' | head -1 | tr -d v)}"
VERSION="${VERSION:-1.0}"

# 2. Move the game folder into the bundle
echo "==> [2/5] Moving ${GAME_DIR} into ${APP}..."
mkdir -p "${APP}/Contents/MacOS"
mv "${GAME_DIR}" "${APP}/Contents/Resources"
RES="${APP}/Contents/Resources"

# 3. Bundle the .NET runtime next to the launcher (ditto keeps Microsoft's signatures)
echo "==> [3/5] Bundling .NET runtime..."
MACOS="${APP}/Contents/MacOS"
ditto "${DOTNET_HOME}/dotnet" "${MACOS}/dotnet"
ditto "${FXR_DIR}" "${MACOS}/host/fxr/$(basename "${FXR_DIR}")"
ditto "${NETCORE_DIR}" "${MACOS}/shared/Microsoft.NETCore.App/$(basename "${NETCORE_DIR}")"
for f in LICENSE.txt ThirdPartyNotices.txt; do
    [ -f "${DOTNET_HOME}/${f}" ] && cp "${DOTNET_HOME}/${f}" "${MACOS}/"
done

# 4. Install launcher, refresh start_mac.command, write Info.plist and the app icon
echo "==> [4/5] Installing launcher and Info.plist..."
cp "${SCRIPT_DIR}/app/launcher.sh" "${MACOS}/KSA"
cp "${SCRIPT_DIR}/start_mac.command" "${RES}/start_mac.command"
chmod +x "${MACOS}/KSA" "${RES}/start_mac.command"

if [ -f "${SCRIPT_DIR}/app/AppIcon.icns" ]; then
    cp "${SCRIPT_DIR}/app/AppIcon.icns" "${RES}/AppIcon.icns"
else
    # Use the bottom square of the title card (the kitten astronaut). The card is
    # 900x1350; keep the bottom 898x898 (sips skips the crop if an offset is 0).
    echo "    Downloading the KSA title card for the app icon..."
    ICON_TMP="$(mktemp -d)"
    if curl -fsSLo "${ICON_TMP}/card.jpg" "${TITLE_CARD_URL}" \
        && sips -s format png -c 898 898 --cropOffset 452 1 "${ICON_TMP}/card.jpg" --out "${ICON_TMP}/card.png" > /dev/null; then
        ICONSET="${ICON_TMP}/AppIcon.iconset"
        mkdir -p "${ICONSET}"
        for size in 16 32 128 256 512; do
            sips -z ${size} ${size} "${ICON_TMP}/card.png" --out "${ICONSET}/icon_${size}x${size}.png" > /dev/null
            sips -z $((size * 2)) $((size * 2)) "${ICON_TMP}/card.png" --out "${ICONSET}/icon_${size}x${size}@2x.png" > /dev/null
        done
        iconutil -c icns "${ICONSET}" -o "${RES}/AppIcon.icns"
    else
        echo "    Warning: could not get ${TITLE_CARD_URL} - building the app without an icon."
    fi
    rm -rf "${ICON_TMP}"
fi

ICON_ENTRY=""
if [ -f "${RES}/AppIcon.icns" ]; then
    ICON_ENTRY="<key>CFBundleIconFile</key>
    <string>AppIcon</string>"
fi

# NSHighResolutionCapable=false: render 1 pixel per point (like Wine) instead of a 2x
# Retina framebuffer, so the game's resolution setting maps to real rendered pixels.
cat > "${APP}/Contents/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>
    <string>KSA</string>
    <key>CFBundleName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key>
    <string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key>
    <string>${BUNDLE_ID}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>LSMinimumSystemVersion</key>
    <string>13.0</string>
    <key>LSRequiresNativeExecution</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.simulation-games</string>
    <key>NSHighResolutionCapable</key>
    <false/>
    ${ICON_ENTRY}
</dict>
</plist>
EOF
plutil -lint "${APP}/Contents/Info.plist" > /dev/null

# 5. Clear quarantine and nudge Finder/LaunchServices. The bundle itself is deliberately
#    not re-signed: that would replace Microsoft's hardened-runtime signature on dotnet.
echo "==> [5/5] Clearing quarantine attributes..."
xattr -cr "${APP}" 2>/dev/null || true

# Game update: move the old app to the Trash and put the new one in its place
if [ -n "${OLD_APP}" ]; then
    TRASHED="${HOME}/.Trash/${APP_NAME} ${OLD_VERSION} $(date +%Y%m%d-%H%M%S).app"
    echo "    Moving the previous app (${OLD_VERSION}) to the Trash..."
    mkdir -p "${HOME}/.Trash"
    mv "${OLD_APP}" "${TRASHED}"
    mv "${APP}" "${OLD_APP}"
    APP="${OLD_APP}"
    RES="${APP}/Contents/Resources"
fi
touch "${APP}"

echo ""
echo "==> App created: ${APP} (KSA ${VERSION})"
[ -n "${OLD_APP}" ] && echo "    Previous app:  ${TRASHED}"
echo "    Launch it from Finder/Launchpad, or: open \"${APP}\""
echo "    Launcher log: ~/Library/Logs/${APP_NAME}.log"
# install.sh deletes the folder GAME_DIR was extracted into, so there is nothing to undo to
if [ -z "${KSA_INSTALLER}" ]; then
    echo "    To undo:      mv \"${RES}\" \"${GAME_DIR}\""
fi
