#!/bin/bash
# StarMap, the KSA mod loader (https://github.com/StarMapLoader/StarMap) -> deps/build/starmap/
#
# StarMap's release is its Windows build. Only two of its files are Windows-specific: StarMap.exe, a launcher the
# app doesn't need (the launcher runs StarMap.dll with the app's .NET runtime), left out here, and StarMap.dll's
# AMD64 mark, changed to ARM64 like KSA.dll's (src/port/lib/mark-arm64.sh). The rest is platform-neutral .NET.
# Its licence and that of Harmony, which it includes, go to deps/build/starmap-licenses/.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Fetching StarMap ${STARMAP_VERSION}"
ARCHIVE="${DEPS}/starmap/StarMap-${STARMAP_VERSION}.zip"
download "${STARMAP_URL}" "${ARCHIVE}" 256 "${STARMAP_SHA256}"

TARGET="${BUILD}/starmap"
rm -rf "${TARGET}"
mkdir -p "${TARGET}"
ditto -x -k "${ARCHIVE}" "${TARGET}"
[ -f "${TARGET}/StarMap.dll" ] && [ -f "${TARGET}/StarMap.Core.dll" ] || die "the StarMap archive has an unexpected layout."
rm -f "${TARGET}/StarMap.exe"
run mark-arm64 "${TARGET}"/*.dll
echo "${STARMAP_VERSION}" > "${TARGET}/version.txt"

LICENSES="${BUILD}/starmap-licenses"
rm -rf "${LICENSES}"
mkdir -p "${LICENSES}/starmap" "${LICENSES}/harmony"
for entry in "starmap ${STARMAP_LICENSE_URL}" "harmony ${HARMONY_LICENSE_URL}"; do
    name="${entry%% *}"
    cached="${DEPS}/starmap/${name}-LICENSE-${STARMAP_VERSION}.txt"
    if [ ! -s "${cached}" ]; then
        curl -fsSL --retry 3 -o "${cached}" "${entry#* }" || die "could not download ${entry#* }"
    fi
    cp "${cached}" "${LICENSES}/${name}/LICENSE.txt"
done
