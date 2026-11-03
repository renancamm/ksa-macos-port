#!/bin/bash
# Assembles the port folder, dist/ksa-macos-port/, from src/, the pieces in deps/build/ and the downloads in
# deps/build/cmake/_deps/ (cmake/dependencies.cmake).
#
# Usage:
#   scripts/make-port-folder.sh commands <version> <KSA version> <commit>
#       Starts the folder anew with the commands, lib/, the notices and VERSION.
#   scripts/make-port-folder.sh payload
#       Adds the porter (lib/porter/) and payload/: the app without the game (app/Contents/), with the benchmark, and
#       StarMap.
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

folder_commands() {
    local version="$1" ksa="$2" commit="$3"
    rm -rf "${OUT}"
    mkdir -p "${OUT}/lib"
    cp "${REPO}"/src/port-folder/*.sh "${REPO}/src/port-folder/README.md" "${REPO}/src/benchmark/benchmark.sh" \
        "${OUT}/"
    cp "${REPO}"/src/port-folder/lib/*.sh "${OUT}/lib/"
    chmod +x "${OUT}"/*.sh
    cp "${REPO}/LICENSE" "${REPO}/THIRD_PARTY_NOTICES.md" "${OUT}/"
    # The BRUTAL packages the libraries come from, for the porter's version check of the game's
    printf '%s\n' "${BRUTAL_PACKAGES[@]}" > "${OUT}/lib/brutal.txt"
    printf 'version=%s\nksa=%s\ncommit=%s\ndotnet_sdk=%s\n' "${version}" "${ksa}" "${commit}" "$(dotnet_sdk_version)" \
        > "${OUT}/VERSION"
}

folder_payload() {
    local contents="${OUT}/payload/app/Contents" licenses
    [ -f "${OUT}/VERSION" ] || die "run scripts/make-port-folder.sh commands first."
    [ -f "${NATIVES}/libvulkan.1.dylib" ] && [ -x "${NATIVES}/KSA" ] && [ -x "${DOTNET_RUNTIME}/dotnet" ] &&
        [ -f "${BUILD}/icon/AppIcon.icns" ] &&
        [ -f "${ASSEMBLIES}/bin/KsaMetalBenchmark/KsaMetalBenchmark.dll" ] &&
        [ -f "${ASSEMBLIES}/bin/KsaMetalHooks/KsaMetalHooks.dll" ] &&
        [ -f "${ASSEMBLIES}/bin/KsaPortSettingsGenerator/KsaPortSettingsGenerator.dll" ] &&
        [ -f "${ASSEMBLIES}/bin/KsaPorter/KsaPorter.dll" ] ||
        die "the pieces of the port are not all built. Run ./build.sh."
    [ -f "${STARMAP}/StarMap.dll" ] && [ -f "${STARMAP}/StarMap.Core.dll" ] || die "the StarMap archive has an unexpected layout."
    step "Assembling ${OUT#"${REPO}/"}"
    rm -rf "${OUT}/payload" "${OUT}/lib/porter"
    # The port's program, which the commands run (src/porter/), with its shader edits
    ditto "${ASSEMBLIES}/bin/KsaPorter" "${OUT}/lib/porter"

    # The app without the game: launcher (src/launcher/launcher.c) and .NET runtime (ditto keeps Microsoft's
    # signatures), libraries and BRUTAL's crash-dump helper, icon
    mkdir -p "${contents}/Resources"
    ditto "${DOTNET_RUNTIME}" "${contents}/MacOS"
    cp "${NATIVES}/KSA" "${contents}/MacOS/KSA"
    # The port settings: their table, and the commented template the hooks copy for the player (src/shared/settings/)
    cp "${REPO}/src/shared/settings/port-settings.txt" "${contents}/Resources/"
    # What the tab's title says: the port's version
    cp "${OUT}/VERSION" "${contents}/Resources/VERSION"
    dotnet "${ASSEMBLIES}/bin/KsaPortSettingsGenerator/KsaPortSettingsGenerator.dll" template "${REPO}/src/shared/settings/port-settings.txt" \
        > "${contents}/Resources/port-settings.toml"
    cp -P "${NATIVES}"/*.dylib "${NATIVES}/Brutal.Monitor.Subprocess" "${contents}/Resources/"
    cp "${REPO}/third_party/fmod/libfmod.dylib" "${contents}/Resources/libfmod.dylib"
    chmod u+w "${contents}/Resources/libfmod.dylib"
    cp "${BUILD}/icon/AppIcon.icns" "${contents}/Resources/"
    # KSA Metal Hooks, which the launcher loads and which apply the port settings (src/hooks/), with the shared
    # library (src/shared/) and Harmony
    mkdir -p "${contents}/Resources/Hooks"
    cp "${ASSEMBLIES}/bin/KsaMetalHooksLoader/KsaMetalHooksLoader.dll" "${ASSEMBLIES}/bin/KsaMetalHooks/KsaMetalHooks.dll" \
        "${ASSEMBLIES}/bin/KsaPort.Common/KsaPort.Common.dll" "${HARMONY_DLL}" "${contents}/Resources/Hooks/"
    # The benchmark (src/benchmark/): its director, which the hooks start, the report program, the tour and its vehicles
    local bench="${contents}/Resources/Benchmark"
    mkdir -p "${bench}"
    cp "${ASSEMBLIES}/bin/KsaMetalBenchmark/KsaMetalBenchmark.dll" "${ASSEMBLIES}"/bin/KsaMetalBenchmarkReport/*.dll \
        "${ASSEMBLIES}"/bin/KsaMetalBenchmarkReport/*.json \
        "${REPO}/src/benchmark/benchmark.txt" "${bench}/"
    for v in "${REPO}"/src/benchmark/vehicles/*/; do
        mkdir -p "${bench}/vehicles/$(basename "${v}")"
        cp "${v}"* "${bench}/vehicles/$(basename "${v}")/"
    done

    # Every component's licence and notices (THIRD_PARTY_NOTICES.md lists them)
    licenses="${contents}/Resources/Licenses"
    mkdir -p "${licenses}/ksa-macos-port" "${licenses}/fmod" "${licenses}/dotnet" "${licenses}/ksa-icon" \
        "${licenses}/starmap" "${licenses}/harmony"
    cp "${REPO}/THIRD_PARTY_NOTICES.md" "${licenses}/"
    cp "${REPO}/LICENSE" "${licenses}/ksa-macos-port/"
    cp "${REPO}/third_party/fmod/LICENSE.TXT" "${REPO}/third_party/fmod/NOTICE.txt" "${licenses}/fmod/"
    cp "${DOTNET_RUNTIME}/LICENSE.txt" "${DOTNET_RUNTIME}/ThirdPartyNotices.txt" "${licenses}/dotnet/"
    cp -R "${NATIVES}"/licenses/* "${licenses}/"
    cp "${STARMAP_LICENSE}" "${licenses}/starmap/LICENSE.txt"
    cp "${HARMONY_LICENSE}" "${licenses}/harmony/LICENSE.txt"
    cp "${BUILD}/icon/NOTICE.txt" "${licenses}/ksa-icon/"

    # StarMap's release is its Windows build. Only two of its files are Windows-specific: StarMap.exe, a launcher the
    # app doesn't need (the launcher runs StarMap.dll with the app's .NET runtime), left out, and StarMap.dll's AMD64
    # mark, changed to ARM64 like KSA.dll's (the porter's mark-arm64). The rest is platform-neutral .NET.
    ditto "${STARMAP}" "${OUT}/payload/starmap"
    rm -f "${OUT}/payload/starmap/StarMap.exe"
    dotnet "${ASSEMBLIES}/bin/KsaPorter/KsaPorter.dll" mark-arm64 "${OUT}"/payload/starmap/*.dll
    echo "${STARMAP_VERSION}" > "${OUT}/payload/starmap/version.txt"
}

case "$1" in
    commands)
        [ $# = 4 ] || die "usage: scripts/make-port-folder.sh commands <version> <KSA version> <commit>"
        folder_commands "$2" "$3" "$4" ;;
    payload) folder_payload ;;
    *) die "usage: scripts/make-port-folder.sh commands <version> <KSA version> <commit> | payload" ;;
esac
