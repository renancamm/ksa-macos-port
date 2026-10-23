#!/bin/bash
# Assembles the port folder, dist/ksa-macos-port/, from src/, the pieces in deps/build/ and the downloads in
# deps/build/cmake/_deps/ (cmake/dependencies.cmake).
#
# Usage:
#   scripts/make-kit.sh scripts <version> <KSA version> <commit>
#       Starts the folder anew with the commands, lib/, the notices and VERSION.
#   scripts/make-kit.sh payload
#       Adds payload/: the app without the game (app/Contents/), StarMap and the benchmark.
set -eo pipefail
source "$(dirname "$0")/lib.sh"
load_deps

kit_scripts() {
    local version="$1" ksa="$2" commit="$3"
    rm -rf "${OUT}"
    mkdir -p "${OUT}/lib"
    cp "${REPO}"/src/port/*.sh "${REPO}/src/port/README.md" "${OUT}/"
    cp "${REPO}"/src/port/lib/*.sh "${OUT}/lib/"
    chmod +x "${OUT}"/*.sh
    cp "${REPO}/LICENSE" "${REPO}/THIRD_PARTY_NOTICES.md" "${OUT}/"
    # The BRUTAL packages the libraries come from, for lib/convert-dotnet.sh's version check
    printf '%s\n' "${BRUTAL_PACKAGES[@]}" > "${OUT}/lib/brutal.txt"
    printf 'version=%s\nksa=%s\ncommit=%s\ndotnet_sdk=%s\n' "${version}" "${ksa}" "${commit}" "$(dotnet_sdk_version)" \
        > "${OUT}/VERSION"
}

kit_payload() {
    local contents="${OUT}/payload/app/Contents" licenses
    [ -f "${OUT}/VERSION" ] || die "run scripts/make-kit.sh scripts first."
    [ -f "${NATIVES}/libvulkan.1.dylib" ] && [ -x "${DOTNET_RUNTIME}/dotnet" ] && [ -f "${BUILD}/icon/AppIcon.icns" ] &&
        [ -f "${BUILD}/benchmark/mod/KsaMetalBenchmark.dll" ] || die "the pieces of the port are not all built. Run ./build.sh."
    [ -f "${STARMAP}/StarMap.dll" ] && [ -f "${STARMAP}/StarMap.Core.dll" ] || die "the StarMap archive has an unexpected layout."
    step "Assembling ${OUT#"${REPO}/"}"
    rm -rf "${OUT}/payload"

    # The app without the game: launcher and .NET runtime (ditto keeps Microsoft's signatures), libraries, icon
    mkdir -p "${contents}/Resources"
    ditto "${DOTNET_RUNTIME}" "${contents}/MacOS"
    cp "${REPO}/src/launcher/launcher.sh" "${contents}/MacOS/KSA"
    chmod +x "${contents}/MacOS/KSA"
    cp -P "${NATIVES}"/*.dylib "${contents}/Resources/"
    cp "${REPO}/third_party/fmod/libfmod.dylib" "${contents}/Resources/libfmod.dylib"
    chmod u+w "${contents}/Resources/libfmod.dylib"
    cp "${BUILD}/icon/AppIcon.icns" "${contents}/Resources/"

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
    # mark, changed to ARM64 like KSA.dll's (src/port/lib/mark-arm64.sh). The rest is platform-neutral .NET.
    ditto "${STARMAP}" "${OUT}/payload/starmap"
    rm -f "${OUT}/payload/starmap/StarMap.exe"
    run mark-arm64 "${OUT}"/payload/starmap/*.dll
    echo "${STARMAP_VERSION}" > "${OUT}/payload/starmap/version.txt"

    local bench="${OUT}/payload/benchmark"
    mkdir -p "${bench}/mod" "${bench}/report"
    cp "${BUILD}/benchmark/mod/KsaMetalBenchmark.dll" "${REPO}/src/benchmark/mod/mod.toml" "${bench}/mod/"
    cp "${BUILD}"/benchmark/report/*.dll "${BUILD}"/benchmark/report/*.json "${bench}/report/"
    cp "${REPO}/src/benchmark/benchmark.txt" "${bench}/"
    for v in "${REPO}"/src/benchmark/vehicles/*/; do
        mkdir -p "${bench}/vehicles/$(basename "${v}")"
        cp "${v}"* "${bench}/vehicles/$(basename "${v}")/"
    done
}

case "$1" in
    scripts) [ $# = 4 ] || die "usage: scripts/make-kit.sh scripts <version> <KSA version> <commit>"; kit_scripts "$2" "$3" "$4" ;;
    payload) kit_payload ;;
    *) die "usage: scripts/make-kit.sh scripts <version> <KSA version> <commit> | payload" ;;
esac
