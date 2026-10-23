#!/bin/bash
# Assembles the port folder, dist/ksa-macos-port/, from src/ and the pieces in deps/build/.
#
# Usage:
#   scripts/make-kit.sh scripts <version> <KSA version> <commit>
#       Starts the folder anew with the commands, lib/, the notices and VERSION.
#   scripts/make-kit.sh payload
#       Adds payload/: the app without the game (app/Contents/), StarMap and the benchmark.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

kit_scripts() {
    local version="$1" ksa="$2" commit="$3" entry id v _
    rm -rf "${OUT}"
    mkdir -p "${OUT}/lib"
    cp "${REPO}"/src/port/*.sh "${REPO}/src/port/README.md" "${OUT}/"
    cp "${REPO}"/src/port/lib/*.sh "${OUT}/lib/"
    chmod +x "${OUT}"/*.sh
    cp "${REPO}/LICENSE" "${REPO}/THIRD_PARTY_NOTICES.md" "${OUT}/"
    # The BRUTAL packages the libraries come from, for lib/convert-dotnet.sh's version check
    for entry in "${BRUTAL_PACKAGES[@]}"; do
        read -r id v _ <<< "${entry}"
        echo "${id} ${v}"
    done > "${OUT}/lib/brutal.txt"
    printf 'version=%s\nksa=%s\ncommit=%s\n' "${version}" "${ksa}" "${commit}" > "${OUT}/VERSION"
}

kit_payload() {
    local contents="${OUT}/payload/app/Contents" runtime licenses
    runtime="$(dotnet_runtime_dir)"
    [ -f "${OUT}/VERSION" ] || die "run scripts/make-kit.sh scripts first."
    [ -f "${NATIVES}/libvulkan.1.dylib" ] && [ -x "${runtime}/dotnet" ] && [ -f "${BUILD}/icon/AppIcon.icns" ] &&
        [ -f "${BUILD}/starmap/StarMap.dll" ] && [ -f "${BUILD}/benchmark/mod/KsaMetalBenchmark.dll" ] ||
        die "the pieces of the port are not all built. Run ./build.sh."
    step "Assembling ${OUT#"${REPO}/"}"
    rm -rf "${OUT}/payload"

    # The app without the game: launcher and .NET runtime (ditto keeps Microsoft's signatures), libraries, icon
    mkdir -p "${contents}/Resources"
    ditto "${runtime}" "${contents}/MacOS"
    cp "${REPO}/src/launcher/launcher.sh" "${contents}/MacOS/KSA"
    chmod +x "${contents}/MacOS/KSA"
    cp -P "${NATIVES}"/*.dylib "${contents}/Resources/"
    cp "${REPO}/third_party/fmod/libfmod.dylib" "${contents}/Resources/libfmod.dylib"
    chmod u+w "${contents}/Resources/libfmod.dylib"
    cp "${BUILD}/icon/AppIcon.icns" "${contents}/Resources/"

    # Every component's licence and notices (THIRD_PARTY_NOTICES.md lists them)
    licenses="${contents}/Resources/Licenses"
    mkdir -p "${licenses}/ksa-macos-port" "${licenses}/fmod" "${licenses}/dotnet" "${licenses}/ksa-icon"
    cp "${REPO}/THIRD_PARTY_NOTICES.md" "${licenses}/"
    cp "${REPO}/LICENSE" "${licenses}/ksa-macos-port/"
    cp "${REPO}/third_party/fmod/LICENSE.TXT" "${REPO}/third_party/fmod/NOTICE.txt" "${licenses}/fmod/"
    cp "${runtime}/LICENSE.txt" "${runtime}/ThirdPartyNotices.txt" "${licenses}/dotnet/"
    cp -R "${NATIVES}"/licenses/* "${BUILD}"/starmap-licenses/* "${licenses}/"
    cp "${BUILD}/icon/NOTICE.txt" "${licenses}/ksa-icon/"

    ditto "${BUILD}/starmap" "${OUT}/payload/starmap"

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
