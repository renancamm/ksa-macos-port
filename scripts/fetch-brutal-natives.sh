#!/bin/bash
# Native libraries from RocketWerkz's BRUTAL packages on nuget.org -> deps/build/natives/
#
# KSA is built on BRUTAL, RocketWerkz's open source framework. Its packages ship the native libraries the
# BRUTAL assemblies load, built by RocketWerkz for macOS on Apple Silicon (runtimes/osx-arm64/native/). The
# Linux release of KSA only has the Linux builds of them. This takes the macOS builds, unmodified, from the
# packages pinned in scripts/pins.sh, with BRUTAL's licence, notice and the packages' third-party notices.
#
# Some packages ship one library under several names (GLFW as libglfw.dylib, libglfw.3.dylib and
# libglfw.3.5.dylib), as identical copies. The copies become links to the one named by the library's install name,
# so that the game and the GLFW shim, which links that name, load the same GLFW.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

step "Fetching RocketWerkz's native libraries (nuget.org)"
mkdir -p "${NATIVES}/licenses/brutal"
for entry in "${BRUTAL_PACKAGES[@]}"; do
    read -r id version sha512 <<< "${entry}"
    lower="$(echo "${id}" | tr '[:upper:]' '[:lower:]')"
    package="${DEPS}/nuget/${lower}.${version}.nupkg"
    download "${NUGET_URL}/${lower}/${version}/${lower}.${version}.nupkg" "${package}" 512 "${sha512}"

    unpacked="$(mktemp -d)"
    unzip -q "${package}" 'runtimes/osx-arm64/native/*' LICENSE.txt NOTICE.txt 'LICENSES/*' -d "${unpacked}"
    natives=("${unpacked}"/runtimes/osx-arm64/native/*.dylib)
    [ -f "${natives[0]}" ] || die "${id} ${version} has no macOS libraries."
    cp "${natives[@]}" "${NATIVES}/"
    for f in "${natives[@]}"; do
        name="$(basename "${f}")"
        install_name="$(LC_ALL=C grep -m 1 -aoE '@rpath/[A-Za-z0-9._+-]+\.dylib' "${f}" | head -1 || true)"
        install_name="${install_name#@rpath/}"
        if [ -n "${install_name}" ] && [ "${install_name}" != "${name}" ] && cmp -s "${f}" "${NATIVES}/${install_name}"; then
            ln -sf "${install_name}" "${NATIVES}/${name}"
        fi
    done
    cp "${unpacked}/LICENSE.txt" "${unpacked}/NOTICE.txt" "${unpacked}"/LICENSES/* "${NATIVES}/licenses/brutal/"
    rm -rf "${unpacked}"
    info "${id} ${version}: $(cd "${NATIVES}" && for f in "${natives[@]}"; do printf '%s ' "$(basename "${f}")"; done)"
done
