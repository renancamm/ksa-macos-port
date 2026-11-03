#!/bin/bash
# The port settings' generator (src/settings/): builds it, then makes src/settings/port-settings.h again when the table
# changed and checks that the READMEs name the table's variables. A release (any version but dev) only checks: the
# header it was tagged with must be what the table makes.
#
# Usage: scripts/build-settings.sh <version>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

MODE=generate
[ "${1:-dev}" = dev ] || MODE=check
mkdir -p "${ASSEMBLIES}"
dotnet_cli build "${REPO}/src/settings/generator/KsaPortSettingsGenerator.csproj" -c Release -nologo -v quiet -p:PortBuildDir="${ASSEMBLIES}" \
    -p:DebugType=none -nodeReuse:false > "${ASSEMBLIES}/settings-build.log" 2>&1 ||
    { cat "${ASSEMBLIES}/settings-build.log"; die "the port settings' generator didn't build."; }
dotnet "${ASSEMBLIES}/bin/KsaPortSettingsGenerator/KsaPortSettingsGenerator.dll" "${MODE}" "${REPO}" || die "the port settings' checks failed."
