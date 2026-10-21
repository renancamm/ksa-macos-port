#!/bin/bash
# Audits dist/ after a build: every library is arm64, runs on MACOS_MIN, has a valid signature, depends only on
# macOS and on libraries in dist/, and every component has its licence. Needs otool (Command Line Tools).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Checking the native libraries"
problems=0
problem() {
    echo "    PROBLEM: $*" >&2
    problems=$((problems + 1))
}

for lib in "${DIST}"/*.dylib; do
    name="$(basename "${lib}")"
    if [ -L "${lib}" ]; then
        [ -e "${lib}" ] || problem "${name} is a broken link"
        continue
    fi
    file -b "${lib}" | grep -q "arm64" || problem "${name} is not arm64"
    minos="$(otool -l "${lib}" | awk '/LC_BUILD_VERSION/ { found = 1 } found && $1 == "minos" { print $2; exit }')"
    [ -n "${minos}" ] && ! version_newer "${minos}" "${MACOS_MIN}" || problem "${name} needs macOS ${minos:-?}"
    codesign -v "${lib}" 2> /dev/null || problem "${name} has no valid signature"
    for dep in $(otool -L "${lib}" | tail -n +2 | awk '{ print $1 }'); do
        case "${dep}" in
            /usr/lib/* | /System/*) ;;
            @loader_path/* | @rpath/*) [ -e "${DIST}/${dep##*/}" ] || problem "${name} needs ${dep}, not in dist/" ;;
            *) problem "${name} depends on ${dep}" ;;
        esac
    done
done

for component in brutal spirv-cross spirv-tools spirv-headers vulkan-headers; do
    [ -n "$(ls "${DIST}/licenses/${component}" 2> /dev/null)" ] || problem "no licence for ${component}"
done

[ "${problems}" = 0 ] || die "${problems} problem(s) in dist/"
info "All $(ls "${DIST}"/*.dylib | wc -l | tr -d ' ') libraries are arm64, macOS ${MACOS_MIN}+, signed and self-contained."
