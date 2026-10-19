#!/bin/bash
# ==============================================================================
# Kitten Space Agency (KSA) - Ported Game Folder -> Double-Clickable macOS .app
#
# Moves an already-ported game folder (see port_game.sh) into an app bundle and
# bundles a private copy of the .NET 10 runtime, so the game starts by simply
# opening the app - no Terminal and no system-wide .NET install required.
#
# Usage:
#   ./make_app.sh [--icon icon.icns|icon.png] /path/to/ported_game_dir [/path/to/App.app]
#
# Default icon:   app/AppIcon.icns, if present (not in the repo: it is game artwork)
# Default output: ~/Applications/Kitten Space Agency.app
# Undo:           mv "<App>.app/Contents/Resources" /path/to/ported_game_dir
# ==============================================================================
set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
APP_NAME="Kitten Space Agency"

ICON="${SCRIPT_DIR}/app/AppIcon.icns"
if [ "$1" = "--icon" ]; then
    ICON="$2"
    shift 2
    if [ ! -f "${ICON}" ]; then
        echo "Error: icon file not found: ${ICON}"
        exit 1
    fi
fi

GAME_DIR="${1%/}"
APP="${2:-${HOME}/Applications/${APP_NAME}.app}"

if [ -z "${GAME_DIR}" ] || [ ! -f "${GAME_DIR}/KSA.dll" ]; then
    echo "Usage: ./make_app.sh [--icon icon.icns|icon.png] /path/to/ported_game_dir [/path/to/App.app]"
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

if [ -e "${APP}" ]; then
    echo "Error: ${APP} already exists. Remove it or pass a different output path."
    exit 1
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
VERSION="$(basename "${GAME_DIR}" | grep -oE 'v[0-9]+(\.[0-9]+)+' | head -1 | tr -d v)"
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

# 4. Install launcher, refresh start_mac.command, write Info.plist (+ optional icon)
echo "==> [4/5] Installing launcher and Info.plist..."
cp "${SCRIPT_DIR}/app/launcher.sh" "${MACOS}/KSA"
cp "${SCRIPT_DIR}/start_mac.command" "${RES}/start_mac.command"
chmod +x "${MACOS}/KSA" "${RES}/start_mac.command"

ICON_ENTRY=""
if [[ "${ICON}" == *.icns && -f "${ICON}" ]]; then
    cp "${ICON}" "${RES}/AppIcon.icns"
elif [ -f "${ICON}" ]; then
    ICONSET="$(mktemp -d)/AppIcon.iconset"
    mkdir -p "${ICONSET}"
    for size in 16 32 128 256 512; do
        sips -z ${size} ${size} "${ICON}" --setProperty format png --out "${ICONSET}/icon_${size}x${size}.png" > /dev/null
        sips -z $((size * 2)) $((size * 2)) "${ICON}" --setProperty format png --out "${ICONSET}/icon_${size}x${size}@2x.png" > /dev/null
    done
    iconutil -c icns "${ICONSET}" -o "${RES}/AppIcon.icns"
    rm -rf "$(dirname "${ICONSET}")"
fi
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
    <string>io.github.ksa-macos-port.ksa</string>
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
touch "${APP}"

echo ""
echo "==> App created: ${APP}"
echo "    Launch it from Finder/Launchpad, or: open \"${APP}\""
echo "    Launcher log: ~/Library/Logs/${APP_NAME}.log"
echo "    To undo:      mv \"${RES}\" \"${GAME_DIR}\""
