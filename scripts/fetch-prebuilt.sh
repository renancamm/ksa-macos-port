#!/bin/bash
# Prebuilt natives from this repository's GitHub releases -> dist/
#
# The libraries this repository builds itself (the GLFW shim and the Metal driver) are published by the
# "Prebuilt natives" workflow (.github/workflows/prebuilt.yml) for each version of their sources. The release is
# named after the sources' stamp (lib.sh natives_stamp), so it only matches identical sources. Building from
# source gives the same libraries.
#
# Usage: scripts/fetch-prebuilt.sh <stamp>
# Exit status 2: no release for these sources (the caller builds them instead).
# KSA_PREBUILT_URL overrides the download location (a folder URL, file:// works too).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

STAMP="$1"
[ -n "${STAMP}" ] || die "usage: scripts/fetch-prebuilt.sh <stamp>"
NAME="ksa-macos-natives-${STAMP:0:12}"
BASE="${KSA_PREBUILT_URL:-https://github.com/${PREBUILT_REPO}/releases/download/natives-${STAMP:0:12}}"

step "Fetching prebuilt natives (${NAME})"
if ! SUMS="$(curl -fsSL --retry 2 "${BASE}/${NAME}.tar.gz.sha256" 2> /dev/null)"; then
    info "None published for these sources."
    exit 2
fi
ARCHIVE="${DEPS}/prebuilt/${NAME}.tar.gz"
download "${BASE}/${NAME}.tar.gz" "${ARCHIVE}" 256 "${SUMS%% *}"

UNPACKED="$(mktemp -d)"
tar -xzf "${ARCHIVE}" -C "${UNPACKED}"
[ "$(cat "${UNPACKED}/stamp" 2> /dev/null)" = "${STAMP}" ] || die "${NAME} was built from other sources."
for f in "${BUILT_FILES[@]}"; do
    [ -e "${UNPACKED}/${f}" ] || [ -L "${UNPACKED}/${f}" ] || die "${NAME} has no ${f}."
    mkdir -p "$(dirname "${DIST}/${f}")"
    cp -R "${UNPACKED}/${f}" "${DIST}/${f}"
done
rm -rf "${UNPACKED}"
info "Installed ${#BUILT_FILES[@]} files into dist/."
