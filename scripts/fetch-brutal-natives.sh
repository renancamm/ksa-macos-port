#!/bin/bash
# Native libraries from RocketWerkz's BRUTAL packages on nuget.org -> dist/
#
# KSA is built on BRUTAL, RocketWerkz's open source framework. Its packages ship the native libraries the
# BRUTAL assemblies load, built by RocketWerkz for macOS on Apple Silicon (runtimes/osx-arm64/native/). The
# Linux release of KSA only has the Linux builds of them. This takes the macOS builds, unmodified, from the
# packages pinned in scripts/pins.sh, with BRUTAL's licence, notice and the packages' third-party notices.
#
# GLFW is not taken from here: RocketWerkz's GLFW keeps the desktop video mode in fullscreen, which would undo
# the GLFW shim's Fullscreen resolution switch. scripts/build-glfw.sh builds upstream GLFW instead.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Fetching RocketWerkz's native libraries (nuget.org)"
mkdir -p "${DIST}/licenses/brutal"
for entry in "${BRUTAL_PACKAGES[@]}"; do
    read -r id version sha512 <<< "${entry}"
    lower="$(echo "${id}" | tr '[:upper:]' '[:lower:]')"
    package="${DEPS}/nuget/${lower}.${version}.nupkg"
    download "${NUGET_URL}/${lower}/${version}/${lower}.${version}.nupkg" "${package}" 512 "${sha512}"

    unpacked="$(mktemp -d)"
    unzip -q "${package}" 'runtimes/osx-arm64/native/*' LICENSE.txt NOTICE.txt 'LICENSES/*' -d "${unpacked}"
    natives=("${unpacked}"/runtimes/osx-arm64/native/*.dylib)
    [ -f "${natives[0]}" ] || die "${id} ${version} has no macOS libraries."
    cp "${natives[@]}" "${DIST}/"
    cp "${unpacked}/LICENSE.txt" "${unpacked}/NOTICE.txt" "${unpacked}"/LICENSES/* "${DIST}/licenses/brutal/"
    rm -rf "${unpacked}"
    info "${id} ${version}: $(cd "${DIST}" && for f in "${natives[@]}"; do printf '%s ' "$(basename "${f}")"; done)"
done
