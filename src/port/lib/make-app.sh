#!/bin/bash
# Builds "Kitten Space Agency.app" around a converted game folder, or refreshes the port's files in one.
#
# Usage:
#   lib/make-app.sh create <converted game folder> <app>
#       Moves the folder into a new app as Contents/Resources. An app this port made earlier at that path is
#       replaced, keeping the game's window layout (imgui.ini) and StarMap, and goes to the Trash.
#   lib/make-app.sh refresh <app>
#       Puts this folder's launcher, .NET runtime, libraries, icon, licences and Info.plist into an existing app.
#
# The port's part of the app is payload/app/Contents/, the app without the game:
#   Contents/MacOS/KSA            the launcher
#   Contents/MacOS/dotnet, ...    Microsoft's .NET runtime
#   Contents/Resources/*.dylib    the native libraries, which the game finds next to it
#   Contents/Resources/Brutal.Monitor.Subprocess   BRUTAL's crash-dump helper, which the game starts
#   Contents/Resources/GamePatches/   the game patches, which the launcher loads, and Harmony
#   Contents/Resources/mac-settings.toml  the Mac settings' template, which the launcher copies for the player
#   Contents/Resources/AppIcon.icns, Licenses/
# The bundle is not signed as a whole: that would replace Microsoft's signature on dotnet. The quarantine
# attribute a browser download puts on the files is removed, so macOS opens the app as one made on this Mac.
set -eo pipefail
source "$(dirname "$0")/common.sh"

# install_port_files <app Contents folder>
install_port_files() {
    local contents="$1" res="$1/Resources" macos="$1/MacOS"

    # Native libraries: every .dylib next to the game is the port's (the game ships none for macOS). And BRUTAL's
    # crash-dump helper, which the game starts from beside it.
    rm -f "${res}"/*.dylib
    cp -P "${SKELETON}"/Resources/*.dylib "${SKELETON}/Resources/Brutal.Monitor.Subprocess" "${res}/"

    # .NET runtime and launcher (ditto keeps Microsoft's signatures)
    rm -rf "${macos}"
    ditto "${SKELETON}/MacOS" "${macos}"

    rm -rf "${res}/Licenses" "${res}/GamePatches"
    ditto "${SKELETON}/Resources/Licenses" "${res}/Licenses"
    ditto "${SKELETON}/Resources/GamePatches" "${res}/GamePatches"
    cp "${SKELETON}/Resources/AppIcon.icns" "${SKELETON}/Resources/mac-settings.toml" "${res}/"
    write_info_plist "${contents}"
    xattr -cr "${contents}" 2> /dev/null || true
}

# write_info_plist <app Contents folder>
write_info_plist() {
    local contents="$1" version
    version="$(game_version "${contents}/Resources")"
    # NSHighResolutionCapable false: one rendered pixel per point, so the game's Resolution setting is the
    # number of pixels it renders, instead of a 2x Retina framebuffer (the launcher turns it on for retina = true in
    # mac-settings.toml, where the GLFW shim keeps the game in points)
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
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>LSMinimumSystemVersion</key><string>${MACOS_MIN}</string>
    <key>LSRequiresNativeExecution</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.simulation-games</string>
    <key>NSHighResolutionCapable</key><false/>
    <key>KSAPortVersion</key><string>$(kit_value version)</string>
    <key>KSAPortCommit</key><string>$(kit_value commit)</string>
</dict>
</plist>
EOF
    plutil -lint "${contents}/Info.plist" > /dev/null
}

require_kit
case "$1" in
    create)
        GAME="${2%/}"
        APP="${3%/}"
        [ -f "${GAME}/KSA.dll" ] || die "${GAME} has no KSA.dll."
        [ -n "${APP}" ] || die "usage: lib/make-app.sh create <game folder> <app>"
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
        install_port_files "${TARGET}/Contents"
        if [ -n "${OLD}" ]; then
            [ -f "${OLD}/Contents/Resources/imgui.ini" ] && cp "${OLD}/Contents/Resources/imgui.ini" "${TARGET}/Contents/Resources/"
            if [ -d "${OLD}/Contents/Resources/StarMap" ]; then
                install_starmap "${TARGET}/Contents/Resources/StarMap"
                info "StarMap is in the new app too."
            fi
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
        [ -d "${APP}/Contents/Resources/StarMap" ] && install_starmap "${APP}/Contents/Resources/StarMap"
        touch "${APP}"
        ;;
    *)
        die "usage: lib/make-app.sh create <game folder> <app> | refresh <app>"
        ;;
esac
