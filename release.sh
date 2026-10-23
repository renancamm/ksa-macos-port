#!/bin/bash
# Makes a release of the port: builds the port folder from a clean copy of the committed sources, zips it and tests
# the zip by making an app with it and running the benchmark on that app. With --publish, it then tags the commit and
# uploads the zip as a draft GitHub release, to look over and publish on GitHub.
#
# Usage: ./release.sh [options] <ksa_linux_vX.tar.gz>   build and test dist/ksa-macos-port-v<version>.zip
#        ./release.sh --publish                         tag and upload the newest zip in dist/
#   The version is <KSA build>.<n>: the build number of the KSA the release is made for (5638 for 2026.10.19.5638)
#   and the n-th release of the port for it, counted from the v<KSA build>.* tags. Publishing needs gh, signed in,
#   and the commit pushed to GitHub.
#
# Options:
#   --no-game-test    skip the benchmark run on the test app (the game takes over the screen for a few minutes)
set -eo pipefail
source "$(dirname "$0")/scripts/lib.sh"

usage() {
    sed -n '2,14p' "$0" | sed 's/^# \{0,1\}//'
    exit "${1:-0}"
}

ARCHIVE=""
PUBLISH=0
GAME_TEST=1
while [ $# -gt 0 ]; do
    case "$1" in
        --publish) PUBLISH=1 ;;
        --no-game-test) GAME_TEST=0 ;;
        -h | --help) usage ;;
        -*) echo "Unknown option: $1"; usage 1 ;;
        *) ARCHIVE="$1" ;;
    esac
    shift
done

publish() {
    local zip version ksa commit tag
    zip="$(ls -t "${DIST}"/ksa-macos-port-v*.zip 2> /dev/null | head -1)"
    [ -n "${zip}" ] || die "no release in dist/. Make one first: ./release.sh <ksa_linux_vX.tar.gz>"
    version="$(unzip -p "${zip}" ksa-macos-port/VERSION | sed -n 's/^version=//p')"
    ksa="$(unzip -p "${zip}" ksa-macos-port/VERSION | sed -n 's/^ksa=//p')"
    commit="$(unzip -p "${zip}" ksa-macos-port/VERSION | sed -n 's/^commit=//p')"
    tag="v${version}"
    [ "${commit}" = "$(git -C "${REPO}" rev-parse --short HEAD)" ] ||
        die "$(basename "${zip}") was built from ${commit}, not from the checked-out commit. Make it again."
    command -v gh > /dev/null || die "publishing needs GitHub's gh command: brew install gh, then gh auth login"
    git -C "${REPO}" fetch -q origin
    git -C "${REPO}" branch -r --contains HEAD | grep -q "origin/" || die "push the commit to GitHub first."
    git -C "${REPO}" rev-parse -q --verify "refs/tags/${tag}" > /dev/null && die "the tag ${tag} exists already."

    step "Publishing ${tag} (KSA ${ksa}) as a draft release"
    git -C "${REPO}" tag "${tag}" HEAD
    git -C "${REPO}" push -q origin "${tag}"
    gh release create "${tag}" "${zip}" --repo "${RELEASES_REPO}" --draft --verify-tag \
        --title "Port ${version} for KSA ${ksa}" \
        --notes "The Mac port for Kitten Space Agency ${ksa}. Download $(basename "${zip}"), unzip it, and run \`./port.sh\` in the folder to make the app, or \`./update.sh\` to update an app made by an earlier version."
    step "Done: look over the draft on GitHub and publish it there."
}

if [ "${PUBLISH}" = 1 ]; then
    publish
    exit
fi

[ -n "${ARCHIVE}" ] || usage 1
[ -f "${ARCHIVE}" ] || die "${ARCHIVE} not found."
ARCHIVE="$(cd "$(dirname "${ARCHIVE}")" && pwd)/$(basename "${ARCHIVE}")"
[ -z "$(git -C "${REPO}" status --porcelain)" ] ||
    die "the checkout has changes. A release is built from the committed sources: commit or stash them first."
check_mac
require_build_tools

# The version: the KSA build number, and the next release number for it
WORK="${DEPS}/release"
rm -rf "${WORK}"
mkdir -p "${WORK}/ksa"
tar -xf "${ARCHIVE}" -C "${WORK}/ksa" --include '*KSA.dll'
KSA_DLL="$(find "${WORK}/ksa" -maxdepth 2 -name KSA.dll | head -1)"
[ -n "${KSA_DLL}" ] || die "no KSA.dll in ${ARCHIVE}. Is this the Linux version of KSA?"
KSA_VERSION="$(game_version "$(dirname "${KSA_DLL}")")"
KSA_BUILD_NO="${KSA_VERSION##*.}"
n=0
for tag in $(git -C "${REPO}" tag -l "v${KSA_BUILD_NO}.*") \
    $(git -C "${REPO}" ls-remote --tags origin "v${KSA_BUILD_NO}.*" 2> /dev/null | sed 's|.*refs/tags/||'); do
    t="${tag#v${KSA_BUILD_NO}.}"
    [[ "${t}" =~ ^[0-9]+$ ]] && [ "${t}" -gt "${n}" ] && n="${t}"
done
VERSION="${KSA_BUILD_NO}.$((n + 1))"
COMMIT="$(git -C "${REPO}" rev-parse --short HEAD)"
ZIP="${DIST}/ksa-macos-port-v${VERSION}.zip"
step "Releasing the port ${VERSION} for KSA ${KSA_VERSION}, from ${COMMIT}"

# 1. A clean copy of the committed sources, built with fresh build trees; the pinned downloads in deps/ are shared,
#    because each is checked against scripts/pins.sh
mkdir -p "${WORK}/src"
git -C "${REPO}" archive HEAD | tar -x -C "${WORK}/src" -f -
KSA_DEPS="${DEPS}" KSA_BUILD="${WORK}/build" bash "${WORK}/src/build.sh" --ksa "${ARCHIVE}" --version "${VERSION}" \
    --commit "${COMMIT}"

# 2. The zip: one folder, ksa-macos-port/
mkdir -p "${DIST}"
rm -f "${ZIP}"
ditto -c -k --norsrc --keepParent "${WORK}/src/dist/ksa-macos-port" "${ZIP}"
info "$(basename "${ZIP}"): $(du -h "${ZIP}" | cut -f1 | tr -d ' ')"

# 3. The test: unzip it, make an app with it from the archive, run the benchmark on that app
step "Testing $(basename "${ZIP}")"
TEST="${WORK}/test"
mkdir -p "${TEST}"
ditto -x -k "${ZIP}" "${TEST}"
TEST_APP="${TEST}/${APP_NAME}.app"
"${TEST}/ksa-macos-port/port.sh" --app "${TEST_APP}" "${ARCHIVE}"
[ "$(app_version "${TEST_APP}")" = "${KSA_VERSION}" ] || die "the test app was not made."
if [ "${GAME_TEST}" = 1 ]; then
    "${TEST}/ksa-macos-port/benchmark.sh" --app "${TEST_APP}" --fast || die "the benchmark failed on the test app."
fi
rm -rf "${WORK}"

echo ""
step "Ready: ${ZIP#"${REPO}/"} (port ${VERSION} for KSA ${KSA_VERSION})"
info "Publish it with: ./release.sh --publish"
