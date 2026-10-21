#!/bin/bash
# Builds "Kitten Space Agency.app" around a converted game folder, or refreshes the port's files in one.
#
# Usage:
#   scripts/make-app.sh create <converted game folder> <app>
#       Moves the folder into a new app as Contents/Resources. An app this port made earlier at that path is
#       replaced, keeping its icon and the game's window layout (imgui.ini), and goes to the Trash.
#   scripts/make-app.sh refresh <app>
#       Puts the current native libraries, .NET runtime, launcher, Info.plist and licences into an existing app.
#
# The app holds:
#   Contents/MacOS/KSA            app/launcher.sh
#   Contents/MacOS/dotnet, ...    Microsoft's .NET runtime (scripts/fetch-dotnet.sh)
#   Contents/Resources/           the game, plus dist/*.dylib and FMOD's libfmod.dylib
#   Contents/Resources/Licenses/  the licences and notices of everything above
# The bundle is not signed as a whole: that would replace Microsoft's signature on dotnet. A locally made app
# has no quarantine attribute, so macOS opens it without one.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

# Files earlier versions of this port put into the game folder that the current one no longer uses
OBSOLETE=(MoltenVK_icd.json libMoltenVK.dylib start_mac.command libktx_real.dylib FMOD_LICENSE.TXT FMOD_NOTICE.txt
    libfmodL.dylib libfmodstudio.dylib libfmodstudioL.dylib)

# install_port_files <app Contents folder>
install_port_files() {
    local contents="$1" res="$1/Resources" macos="$1/MacOS" f
    local runtime
    runtime="$(dotnet_runtime_dir)"
    [ -f "${DIST}/.stamp" ] || die "dist/ is not ready."
    [ -x "${runtime}/dotnet" ] || die "the .NET runtime is not downloaded."

    for f in "${OBSOLETE[@]}"; do
        rm -f "${res}/${f}"
    done

    # Native libraries: dist/ as a whole, plus FMOD
    for f in "${DIST}"/*.dylib; do
        rm -f "${res}/$(basename "${f}")"
        cp -P "${f}" "${res}/"
    done
    cp "${REPO}/third_party/fmod/libfmod.dylib" "${res}/libfmod.dylib"
    chmod u+w "${res}/libfmod.dylib"

    # .NET runtime and launcher (ditto keeps Microsoft's signatures)
    rm -rf "${macos}"
    mkdir -p "${macos}"
    ditto "${runtime}" "${macos}"
    cp "${REPO}/app/launcher.sh" "${macos}/KSA"
    chmod +x "${macos}/KSA"

    install_licenses "${res}/Licenses" "${runtime}"
    install_icon "${res}"
    write_info_plist "${contents}"
    xattr -cr "${contents}" 2> /dev/null || true
}

# install_licenses <folder> <.NET runtime>
install_licenses() {
    local out="$1" runtime="$2"
    rm -rf "${out}"
    mkdir -p "${out}"
    cp "${REPO}/THIRD_PARTY_NOTICES.md" "${out}/"
    mkdir -p "${out}/ksa-macos-port" "${out}/fmod" "${out}/dotnet"
    cp "${REPO}/LICENSE" "${out}/ksa-macos-port/"
    cp "${REPO}/third_party/fmod/LICENSE.TXT" "${REPO}/third_party/fmod/NOTICE.txt" "${out}/fmod/"
    cp "${runtime}/LICENSE.txt" "${runtime}/ThirdPartyNotices.txt" "${out}/dotnet/"
    cp -R "${DIST}"/licenses/* "${out}/"
}

# install_icon <Resources folder>: app/AppIcon.icns if you add one, otherwise the kitten astronaut from KSA's
# title card (the bottom 898x898 of the 900x1350 card), downloaded and cropped here. Kept when already there.
install_icon() {
    local res="$1" tmp size
    if [ -f "${REPO}/app/AppIcon.icns" ]; then
        cp "${REPO}/app/AppIcon.icns" "${res}/AppIcon.icns"
        return 0
    fi
    [ -f "${res}/AppIcon.icns" ] && return 0
    info "Downloading KSA's title card for the app icon (${TITLE_CARD_URL})"
    tmp="$(mktemp -d)"
    if curl -fsSL -o "${tmp}/card.jpg" "${TITLE_CARD_URL}" &&
        sips -s format png -c 898 898 --cropOffset 452 1 "${tmp}/card.jpg" --out "${tmp}/card.png" > /dev/null; then
        mkdir -p "${tmp}/AppIcon.iconset"
        for size in 16 32 128 256 512; do
            sips -z ${size} ${size} "${tmp}/card.png" --out "${tmp}/AppIcon.iconset/icon_${size}x${size}.png" > /dev/null
            sips -z $((size * 2)) $((size * 2)) "${tmp}/card.png" \
                --out "${tmp}/AppIcon.iconset/icon_${size}x${size}@2x.png" > /dev/null
        done
        iconutil -c icns "${tmp}/AppIcon.iconset" -o "${res}/AppIcon.icns"
    else
        warn "could not download the title card; the app has no icon."
    fi
    rm -rf "${tmp}"
}

# write_info_plist <app Contents folder>
write_info_plist() {
    local contents="$1" version port icon=""
    version="$(game_version "${contents}/Resources")"
    port="unknown"
    if have_dev_tools && [ -d "${REPO}/.git" ]; then
        port="$(git -C "${REPO}" describe --always --dirty 2> /dev/null || echo unknown)"
    fi
    [ -f "${contents}/Resources/AppIcon.icns" ] && icon="<key>CFBundleIconFile</key><string>AppIcon</string>"
    # NSHighResolutionCapable false: one rendered pixel per point, so the game's Resolution setting is the
    # number of pixels it renders (as on Windows), instead of a 2x Retina framebuffer
    cat > "${contents}/Info.plist" << EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>KSA</string>
    <key>CFBundleName</key><string>${APP_NAME}</string>
    <key>CFBundleDisplayName</key><string>${APP_NAME}</string>
    <key>CFBundleIdentifier</key><string>${BUNDLE_ID}</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>${version:-1.0}</string>
    <key>CFBundleVersion</key><string>${version:-1.0}</string>
    <key>CFBundleInfoDictionaryVersion</key><string>6.0</string>
    <key>LSMinimumSystemVersion</key><string>${MACOS_MIN}</string>
    <key>LSRequiresNativeExecution</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.simulation-games</string>
    <key>NSHighResolutionCapable</key><false/>
    <key>KSAPortVersion</key><string>${port}</string>
    <key>KSAPortNatives</key><string>$(cat "${DIST}/.stamp")</string>
    ${icon}
</dict>
</plist>
EOF
    plutil -lint "${contents}/Info.plist" > /dev/null
}

case "$1" in
    create)
        GAME="${2%/}"
        APP="${3%/}"
        [ -f "${GAME}/KSA.dll" ] || die "${GAME} has no KSA.dll."
        [ -n "${APP}" ] || die "usage: scripts/make-app.sh create <game folder> <app>"
        OLD=""
        TARGET="${APP}"
        if [ -e "${APP}" ]; then
            OLD_VERSION="$(app_version "${APP}")"
            [ -n "${OLD_VERSION}" ] || die "${APP} exists and was not made by this port. Move it away or choose another path."
            OLD="${APP}"
            # Build next to the old app and swap at the end, so a failure leaves the old one untouched
            TARGET="$(dirname "${APP}")/.$(basename "${APP}").new"
            [ ! -e "${TARGET}" ] || die "${TARGET} is left over from a failed update. Delete it and run again."
        fi
        mkdir -p "${TARGET}/Contents"
        mv "${GAME}" "${TARGET}/Contents/Resources"
        if [ -n "${OLD}" ]; then
            for f in AppIcon.icns imgui.ini; do
                [ -f "${OLD}/Contents/Resources/${f}" ] && cp "${OLD}/Contents/Resources/${f}" "${TARGET}/Contents/Resources/"
            done
        fi
        install_port_files "${TARGET}/Contents"
        if [ -n "${OLD}" ]; then
            TRASHED="${HOME}/.Trash/${APP_NAME} ${OLD_VERSION} $(date +%Y%m%d-%H%M%S).app"
            mkdir -p "${HOME}/.Trash"
            mv "${OLD}" "${TRASHED}"
            mv "${TARGET}" "${APP}"
            info "The previous app (${OLD_VERSION}) is in the Trash."
        fi
        touch "${APP}"
        ;;
    refresh)
        APP="${2%/}"
        [ -n "$(app_version "${APP}")" ] || die "${APP} is not an app made by this port."
        install_port_files "${APP}/Contents"
        touch "${APP}"
        ;;
    *)
        die "usage: scripts/make-app.sh create <game folder> <app> | refresh <app>"
        ;;
esac
