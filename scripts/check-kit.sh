#!/bin/bash
# Audits the port folder after a build: the commands and VERSION are there; every library in the app, and BRUTAL's
# crash-dump helper, is arm64, runs on MACOS_MIN, has a valid signature and depends only on macOS and the app's
# libraries; StarMap is marked ARM64; every component has its licence; and nothing of the game is in it. Needs otool
# (Command Line Tools).
#
# Usage: scripts/check-kit.sh [port folder]   (default: dist/ksa-macos-port)
set -eo pipefail
source "$(dirname "$0")/lib.sh"

KIT_DIR="${1:-${OUT}}"
RES="${KIT_DIR}/payload/app/Contents/Resources"
step "Checking ${KIT_DIR#"${REPO}/"}"
problems=0
problem() {
    echo "    PROBLEM: $*" >&2
    problems=$((problems + 1))
}

for f in port.sh starmap.sh benchmark.sh README.md LICENSE THIRD_PARTY_NOTICES.md VERSION lib/common.sh \
    lib/brutal.txt payload/app/Contents/MacOS/KSA payload/app/Contents/MacOS/dotnet payload/starmap/StarMap.dll \
    payload/benchmark/mod/KsaMetalBenchmark.dll payload/benchmark/report/KsaMetalBenchmarkReport.dll \
    payload/benchmark/benchmark.txt "payload/app/Contents/Resources/AppIcon.icns" \
    payload/app/Contents/Resources/Hooks/KsaMetalHooksLoader.dll \
    payload/app/Contents/Resources/Hooks/KsaMetalHooks.dll payload/app/Contents/Resources/Hooks/0Harmony.dll; do
    [ -e "${KIT_DIR}/${f}" ] || problem "${f} is missing"
done
for f in port.sh starmap.sh benchmark.sh payload/app/Contents/MacOS/KSA \
    payload/app/Contents/Resources/Brutal.Monitor.Subprocess; do
    [ -x "${KIT_DIR}/${f}" ] || problem "${f} is not executable"
done
grep -q '^version=.' "${KIT_DIR}/VERSION" && grep -q '^ksa=20' "${KIT_DIR}/VERSION" || problem "VERSION is incomplete"

for lib in "${RES}"/*.dylib "${RES}/Brutal.Monitor.Subprocess"; do
    name="$(basename "${lib}")"
    if [ -L "${lib}" ]; then
        [ -e "${lib}" ] || problem "${name} is a broken link"
        continue
    fi
    file -b "${lib}" | grep -q "arm64" || problem "${name} is not arm64"
    minos="$(otool -arch arm64 -l "${lib}" | awk '/LC_BUILD_VERSION/ { found = 1 } found && $1 == "minos" { print $2; exit }')"
    [ -n "${minos}" ] && ! version_newer "${minos}" "${MACOS_MIN}" || problem "${name} needs macOS ${minos:-?}"
    codesign -v "${lib}" 2> /dev/null || problem "${name} has no valid signature"
    for dep in $(otool -arch arm64 -L "${lib}" | tail -n +2 | awk '{ print $1 }'); do
        case "${dep}" in
            /usr/lib/* | /System/*) ;;
            @loader_path/* | @rpath/*) [ -e "${RES}/${dep##*/}" ] || problem "${name} needs ${dep}, not in the app" ;;
            *) problem "${name} depends on ${dep}" ;;
        esac
    done
done

# StarMap.dll must carry the ARM64 mark (0xAA64) after its PE header signature
pe=$(od -An -tu4 -j 60 -N 4 "${KIT_DIR}/payload/starmap/StarMap.dll" | tr -d ' ')
[ "$(od -An -tx1 -j $((pe + 4)) -N 2 "${KIT_DIR}/payload/starmap/StarMap.dll" | tr -d ' \n')" = "64aa" ] ||
    problem "StarMap.dll is not marked ARM64"
[ ! -e "${KIT_DIR}/payload/starmap/StarMap.exe" ] || problem "StarMap.exe is in the folder"

for component in ksa-macos-port brutal spirv-cross spirv-tools spirv-headers vulkan-headers dotnet fmod starmap \
    harmony ksa-icon; do
    [ -n "$(ls "${RES}/Licenses/${component}" 2> /dev/null)" ] || problem "no licence for ${component}"
done

# Nothing of the game: its assemblies, its content, its Linux binaries
for pattern in KSA.dll 'Brutal.*.dll' Content '*.so' '*.so.*'; do
    found="$(find "${KIT_DIR}" -name "${pattern}" | head -1)"
    [ -z "${found}" ] || problem "game file in the folder: ${found#"${KIT_DIR}/"}"
done

hidden="$(find "${KIT_DIR}" -path '*/Contents/MacOS' -prune -o -name '.*' -print | head -1)"
[ -z "${hidden}" ] || problem "hidden file in the folder: ${hidden#"${KIT_DIR}/"}"

[ "${problems}" = 0 ] || die "${problems} problem(s) in ${KIT_DIR}"
info "All $(ls "${RES}"/*.dylib | wc -l | tr -d ' ') libraries and the crash-dump helper are arm64, macOS ${MACOS_MIN}+," \
    "signed and self-contained;" \
    "licences complete; no game files."
