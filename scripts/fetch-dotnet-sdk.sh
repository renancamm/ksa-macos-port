#!/bin/bash
# Microsoft's .NET SDK for macOS on Apple Silicon -> deps/dotnet-sdk/<version>/
#
# Builds the benchmark mod and its report program (scripts/build-benchmark.sh). Only the build needs it: the port
# folder carries their compiled DLLs. The version and SHA-512 are pinned in scripts/pins.sh.
set -eo pipefail
source "$(dirname "$0")/lib.sh"

SDK="$(dotnet_sdk_dir)"
if [ -x "${SDK}/dotnet" ]; then
    info ".NET SDK ${DOTNET_SDK_VERSION} is already downloaded."
    exit 0
fi

step "Fetching the .NET ${DOTNET_SDK_VERSION} SDK (Microsoft)"
ARCHIVE="${DEPS}/dotnet-sdk/dotnet-sdk-${DOTNET_SDK_VERSION}-osx-arm64.tar.gz"
download "${DOTNET_SDK_URL}" "${ARCHIVE}" 512 "${DOTNET_SDK_SHA512}"
rm -rf "${SDK}"
mkdir -p "${SDK}"
tar -xzf "${ARCHIVE}" -C "${SDK}"
rm -f "${ARCHIVE}"
[ -x "${SDK}/dotnet" ] || die "the .NET SDK archive has no dotnet host."
