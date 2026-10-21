#!/bin/bash
# Builds the natives from source and packages the ones this repository builds itself for a GitHub release.
# Run by .github/workflows/prebuilt.yml; works locally too.
#
# Usage: scripts/package-prebuilt.sh <output dir>
# Writes <output dir>/ksa-macos-natives-<stamp>.tar.gz and .sha256, and prints the release tag.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

OUT="$1"
[ -n "${OUT}" ] || die "usage: scripts/package-prebuilt.sh <output dir>"
mkdir -p "${OUT}"
OUT="$(cd "${OUT}" && pwd)"

rm -f "${DIST}/.stamp"
prepare_natives --from-source

STAMP="$(cat "${DIST}/.stamp")"
NAME="ksa-macos-natives-${STAMP:0:12}"
STAGE="$(mktemp -d)"
echo "${STAMP}" > "${STAGE}/stamp"
(cd "${DIST}" && tar -cf - "${BUILT_FILES[@]}") | tar -xf - -C "${STAGE}"
cp "${REPO}/LICENSE" "${STAGE}/LICENSE"
COPYFILE_DISABLE=1 tar -czf "${OUT}/${NAME}.tar.gz" -C "${STAGE}" .
rm -rf "${STAGE}"
(cd "${OUT}" && shasum -a 256 "${NAME}.tar.gz" > "${NAME}.tar.gz.sha256")
echo "natives-${STAMP:0:12}"
