#!/bin/bash
# Marks 64-bit .NET assemblies built for AMD64 as ARM64, so .NET on Apple Silicon loads them.
#
# A 64-bit (PE32+) assembly carries the machine type it was built for: AMD64 (0x8664) for KSA.dll and StarMap.dll.
# .NET on Apple Silicon only loads 64-bit assemblies marked ARM64 (0xAA64). These contain only IL code, so
# changing the mark is enough. Platform-neutral (PE32) assemblies load as they are and are left alone, and so are
# files that are not assemblies. Safe to run again.
#
# Usage: scripts/mark-arm64.sh <file.dll...>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

# PE layout: offset 0x3C holds the PE header offset; the header starts with "PE\0\0", the 2-byte machine type
# follows, and the optional header magic (0x20B for PE32+) is 24 bytes in.
read_hex() { od -An -tx1 -j "$2" -N "$3" "$1" | tr -d ' \n'; }
for dll in "$@"; do
    pe=$(od -An -tu4 -j 60 -N 4 "${dll}" | tr -d ' ')
    [ "$(read_hex "${dll}" "${pe}" 4)" = "50450000" ] || continue
    [ "$(read_hex "${dll}" $((pe + 24)) 2)" = "0b02" ] || continue
    case "$(read_hex "${dll}" $((pe + 4)) 2)" in
        6486)
            printf '\x64\xaa' | dd of="${dll}" bs=1 seek=$((pe + 4)) count=2 conv=notrunc 2> /dev/null
            info "$(basename "${dll}"): AMD64 -> ARM64" ;;
        64aa) ;;
        *) die "$(basename "${dll}") has an unexpected machine type. The port needs an update for this version." ;;
    esac
done
