#!/bin/bash
# Audits the port folder after a build: the commands and VERSION are there; every library in the app, the launcher and
# BRUTAL's crash-dump helper are arm64, run on MACOS_MIN, have a valid signature and depend only on macOS and the
# app's libraries, and the launcher's signature requires the app's identifier; StarMap is marked ARM64; every
# component has its licence; and nothing of the game is in it. Needs otool (Command Line Tools).
#
# Usage: scripts/check-port-folder.sh [port folder]   (default: dist/ksa-macos-port)
set -eo pipefail
source "$(dirname "$0")/lib.sh"

FOLDER="${1:-${OUT}}"
RES="${FOLDER}/payload/app/Contents/Resources"
step "Checking ${FOLDER#"${REPO}/"}"
problems=0
problem() {
    echo "    PROBLEM: $*" >&2
    problems=$((problems + 1))
}

for f in port.sh starmap.sh benchmark.sh README.md LICENSE THIRD_PARTY_NOTICES.md VERSION lib/common.sh lib/port.env \
    lib/brutal.txt lib/port-cli/KsaPort.Cli.dll lib/port-cli/KsaPort.Common.dll \
    lib/port-cli/KsaPort.Cli.runtimeconfig.json lib/port-cli/shader-edits/fixes/ground-clutter.edits \
    payload/app/Contents/MacOS/KSA payload/app/Contents/MacOS/dotnet \
    payload/starmap/StarMap.dll \
    payload/app/Contents/Resources/Benchmark/KsaMetal.Benchmark.dll \
    payload/app/Contents/Resources/Benchmark/KsaMetal.Benchmark.Cli.dll \
    payload/app/Contents/Resources/Benchmark/KsaPort.Common.dll \
    payload/app/Contents/Resources/Benchmark/benchmark.txt "payload/app/Contents/Resources/AppIcon.icns" \
    payload/app/Contents/Resources/port-settings.txt payload/app/Contents/Resources/port-settings.toml \
    payload/app/Contents/Resources/VERSION \
    payload/app/Contents/Resources/Hooks/KsaMetal.Hooks.Loader.dll \
    payload/app/Contents/Resources/Hooks/KsaMetal.Hooks.dll payload/app/Contents/Resources/Hooks/KsaPort.Common.dll \
    payload/app/Contents/Resources/Hooks/0Harmony.dll; do
    [ -e "${FOLDER}/${f}" ] || problem "${f} is missing"
done
for f in port.sh starmap.sh benchmark.sh payload/app/Contents/MacOS/KSA \
    payload/app/Contents/Resources/Brutal.Monitor.Subprocess; do
    [ -x "${FOLDER}/${f}" ] || problem "${f} is not executable"
done
if ! grep -q '^version=.' "${FOLDER}/VERSION" || ! grep -q '^ksa=20' "${FOLDER}/VERSION"; then
    problem "VERSION is incomplete"
fi

LAUNCHER="${FOLDER}/payload/app/Contents/MacOS/KSA"
for lib in "${RES}"/*.dylib "${RES}/Brutal.Monitor.Subprocess" "${LAUNCHER}"; do
    name="$(basename "${lib}")"
    if [ -L "${lib}" ]; then
        [ -e "${lib}" ] || problem "${name} is a broken link"
        continue
    fi
    file -b "${lib}" | grep -q "arm64" || problem "${name} is not arm64"
    minos="$(otool -arch arm64 -l "${lib}" | awk '/LC_BUILD_VERSION/ { found = 1 } found && $1 == "minos" { print $2; exit }')"
    if [ -z "${minos}" ] || version_newer "${minos}" "${MACOS_MIN}"; then
        problem "${name} needs macOS ${minos:-?}"
    fi
    codesign -v "${lib}" 2> /dev/null || problem "${name} has no valid signature"
    for dep in $(otool -arch arm64 -L "${lib}" | tail -n +2 | awk '{ print $1 }'); do
        case "${dep}" in
            /usr/lib/* | /System/*) ;;
            @loader_path/* | @rpath/*) [ -e "${RES}/${dep##*/}" ] || problem "${name} needs ${dep}, not in the app" ;;
            *) problem "${name} depends on ${dep}" ;;
        esac
    done
done

# macOS keeps the player's permissions for the launcher's requirement, which must stay the same from one build to the
# next
codesign -d -r- "${LAUNCHER}" 2> /dev/null | grep -qx "designated => identifier \"${BUNDLE_ID}\"" ||
    problem "the launcher's signature doesn't require only the app's identifier"

# StarMap.dll must carry the ARM64 mark (0xAA64) after its PE header signature
pe=$(od -An -tu4 -j 60 -N 4 "${FOLDER}/payload/starmap/StarMap.dll" | tr -d ' ')
[ "$(od -An -tx1 -j $((pe + 4)) -N 2 "${FOLDER}/payload/starmap/StarMap.dll" | tr -d ' \n')" = "64aa" ] ||
    problem "StarMap.dll is not marked ARM64"
[ ! -e "${FOLDER}/payload/starmap/StarMap.exe" ] || problem "StarMap.exe is in the folder"

for component in ksa-macos-port brutal spirv-cross spirv-tools spirv-headers vulkan-headers dotnet fmod starmap \
    harmony ksa-icon; do
    [ -n "$(ls "${RES}/Licenses/${component}" 2> /dev/null)" ] || problem "no licence for ${component}"
done

# Nothing of the game: its assemblies, its content, its Linux binaries
for pattern in KSA.dll 'Brutal.*.dll' Content '*.so' '*.so.*'; do
    found="$(find "${FOLDER}" -name "${pattern}" | head -1)"
    [ -z "${found}" ] || problem "game file in the folder: ${found#"${FOLDER}/"}"
done

hidden="$(find "${FOLDER}" -path '*/Contents/MacOS' -prune -o -name '.*' -print | head -1)"
[ -z "${hidden}" ] || problem "hidden file in the folder: ${hidden#"${FOLDER}/"}"

[ "${problems}" = 0 ] || die "${problems} problem(s) in ${FOLDER}"
info "All $(ls "${RES}"/*.dylib | wc -l | tr -d ' ') libraries, the launcher and the crash-dump helper are arm64," \
    "macOS ${MACOS_MIN}+, signed and self-contained;" \
    "licences complete; no game files."
