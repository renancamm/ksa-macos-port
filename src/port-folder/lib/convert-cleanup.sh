#!/bin/bash
# Removes what macOS cannot use from a KSA game folder: the Linux executables and libraries, and the Windows
# installer files. Safe to run again.
#
# Usage: lib/convert-cleanup.sh <game folder>
set -eo pipefail
source "$(dirname "$0")/common.sh"

GAME="$1"
[ -f "${GAME}/KSA.dll" ] || die "${GAME} has no KSA.dll."

# Linux binaries: every top-level file in ELF format (the .so libraries and their links, the KSA launcher,
# createdump, Brutal.Monitor.Subprocess). macOS cannot load any of them; the port brings macOS builds.
elf=()
for f in "${GAME}"/*; do
    [ -f "${f}" ] && [ "$(head -c 4 "${f}" | od -An -tx1 | tr -d ' \n')" = "7f454c46" ] && elf+=("${f}")
done
# Windows installer script and signing batch file
other=("${GAME}/ksa.iss" "${GAME}/ksa_sign.bat")

rm -rf ${elf[@]+"${elf[@]}"} "${other[@]}"
info "Removed ${#elf[@]} Linux binaries."
