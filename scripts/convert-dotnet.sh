#!/bin/bash
# Makes KSA's .NET files run on the macOS .NET runtime the app bundles. Safe to run again.
#
# The Linux release is a self-contained linux-x64 .NET 10 program:
#   1. It carries the Linux build of the .NET runtime libraries (System.*.dll, mscorlib.dll, ...). Left in place,
#      .NET loads them and fails. They are exactly the DLLs whose names are also in Microsoft's macOS runtime, so
#      those are removed. The game's own DLLs and its NuGet packages (System.CommandLine, System.IO.Hashing,
#      Microsoft.Extensions.*) are not in the runtime and stay.
#   2. KSA.deps.json points the .NET host at the linux-x64 runtime pack: removed.
#   3. KSA.runtimeconfig.json describes a self-contained program: replaced by one that uses the shared
#      .NET 10 runtime (the one in the app).
#   4. KSA.dll is a 64-bit assembly marked AMD64, which .NET on Apple Silicon does not load: marked ARM64
#      (scripts/mark-arm64.sh). The other game DLLs are platform-neutral and load as they are.
#
# Usage: scripts/convert-dotnet.sh <game folder> <.NET runtime folder>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

GAME="$1"
RUNTIME="$2"
[ -f "${GAME}/KSA.dll" ] || die "${GAME} has no KSA.dll."
FRAMEWORK="$(ls -d "${RUNTIME}"/shared/Microsoft.NETCore.App/*/ 2> /dev/null | head -1)"
[ -n "${FRAMEWORK}" ] || die "no .NET runtime in ${RUNTIME}."

# The port's BRUTAL libraries come from the packages pinned in scripts/pins.sh, each matching one of the game's
# BRUTAL assemblies (Ahwoo.BRUTAL.Glfw: Brutal.Glfw.dll). An older game doesn't work with them (KSA 2026.10.13
# passes the 2026.10 VMA library no Vulkan functions and crashes); a newer one may expect functions they lack.
for entry in "${BRUTAL_PACKAGES[@]}"; do
    read -r id version _ <<< "${entry}"
    assembly="Brutal.${id#Ahwoo.BRUTAL.}.dll"
    brutal="$(LC_ALL=C grep -m 1 -aoE '20[0-9]{2}\.[0-9]+\.[0-9]+\+[0-9a-f]{40}' "${GAME}/${assembly}" 2> /dev/null | head -1 || true)"
    brutal="${brutal%%+*}"
    if [ -n "${brutal}" ] && version_newer "${version}" "${brutal}"; then
        die "this KSA is older than the port supports (${assembly} ${brutal}, the port's library is ${version})." \
            "Download the current Linux version of KSA from https://ksa.ahwoo.com."
    elif [ -n "${brutal}" ] && version_newer "${brutal}" "${version}"; then
        warn "this KSA uses ${assembly} ${brutal}, newer than the port's library (${version})." \
            "If the game fails to start, the port needs an update."
    fi
done

# 1. Linux runtime libraries
removed=0
for f in "${FRAMEWORK}"*.dll; do
    name="$(basename "${f}")"
    if [ -f "${GAME}/${name}" ]; then
        rm "${GAME}/${name}"
        removed=$((removed + 1))
    fi
done
info "Removed ${removed} Linux .NET runtime libraries."

# 2. and 3. .NET host configuration
rm -f "${GAME}/KSA.deps.json"
cat > "${GAME}/KSA.runtimeconfig.json" << 'EOF'
{
  "runtimeOptions": {
    "tfm": "net10.0",
    "frameworks": [
      {
        "name": "Microsoft.NETCore.App",
        "version": "10.0.0"
      }
    ],
    "rollForward": "LatestMinor",
    "configProperties": {
      "System.Reflection.Metadata.MetadataUpdater.IsSupported": false,
      "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false
    }
  }
}
EOF

# 4. Machine type of 64-bit assemblies
run mark-arm64 "${GAME}"/*.dll
