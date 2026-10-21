#!/bin/bash
# Microsoft's .NET runtime for macOS on Apple Silicon -> deps/dotnet/<version>/
#
# KSA is a .NET 10 program. The Linux release carries a Linux runtime; the app gets Microsoft's macOS runtime
# instead (the version and SHA-512 are pinned in scripts/pins.sh). Its dotnet host is signed by Microsoft with
# the entitlements the launcher relies on (DYLD_* variables, unsigned libraries, JIT).
set -eo pipefail
source "$(dirname "$0")/lib.sh"

RUNTIME="$(dotnet_runtime_dir)"
if [ -x "${RUNTIME}/dotnet" ] && [ -d "${RUNTIME}/shared/Microsoft.NETCore.App/${DOTNET_VERSION}" ]; then
    info ".NET runtime ${DOTNET_VERSION} is already downloaded."
    exit 0
fi

step "Fetching the .NET ${DOTNET_VERSION} runtime (Microsoft)"
ARCHIVE="${DEPS}/dotnet/dotnet-runtime-${DOTNET_VERSION}-osx-arm64.tar.gz"
download "${DOTNET_URL}" "${ARCHIVE}" 512 "${DOTNET_SHA512}"
rm -rf "${RUNTIME}"
mkdir -p "${RUNTIME}"
tar -xzf "${ARCHIVE}" -C "${RUNTIME}"
[ -x "${RUNTIME}/dotnet" ] || die "the .NET archive has no dotnet host."
