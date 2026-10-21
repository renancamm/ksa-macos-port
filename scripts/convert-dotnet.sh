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
#   4. KSA.dll is a 64-bit (PE32+) assembly marked AMD64 (machine 0x8664). .NET on Apple Silicon only loads
#      64-bit assemblies marked ARM64 (0xAA64). KSA.dll contains only IL code, so changing the mark is enough.
#      The other game DLLs are platform-neutral (PE32) and load as they are.
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

# 4. Machine type of 64-bit assemblies. PE layout: offset 0x3C holds the PE header offset; the header starts
#    with "PE\0\0", the 2-byte machine type follows, and the optional header magic (0x20B for PE32+) is 24 bytes in.
read_hex() { od -An -tx1 -j "$2" -N "$3" "$1" | tr -d ' \n'; }
for dll in "${GAME}"/*.dll; do
    pe=$(od -An -tu4 -j 60 -N 4 "${dll}" | tr -d ' ')
    [ "$(read_hex "${dll}" "${pe}" 4)" = "50450000" ] || continue
    [ "$(read_hex "${dll}" $((pe + 24)) 2)" = "0b02" ] || continue
    case "$(read_hex "${dll}" $((pe + 4)) 2)" in
        6486)
            printf '\x64\xaa' | dd of="${dll}" bs=1 seek=$((pe + 4)) count=2 conv=notrunc 2> /dev/null
            info "$(basename "${dll}"): AMD64 -> ARM64" ;;
        64aa) ;;
        *) die "$(basename "${dll}") has an unexpected machine type. The port needs an update for this KSA version." ;;
    esac
done
