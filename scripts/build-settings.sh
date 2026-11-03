#!/bin/bash
# The port settings' checks (src/build-tools/settings-generator/): builds the generator, then checks the repository
# against the settings table: the hooks and the code read what the table says, and the READMEs name the table's
# variables. The files the generator makes are made where they are compiled (CMakeLists.txt, the hooks' project).
#
# Usage: scripts/build-settings.sh
set -eo pipefail
source "$(dirname "$0")/lib.sh"

mkdir -p "${ASSEMBLIES}"
dotnet_cli build "${REPO}/src/build-tools/settings-generator/KsaPort.Tools.SettingsGenerator.csproj" -c Release -nologo -v quiet \
    -p:PortBuildDir="${ASSEMBLIES}" -p:DebugType=none -nodeReuse:false > "${ASSEMBLIES}/settings-build.log" 2>&1 ||
    { cat "${ASSEMBLIES}/settings-build.log"; die "the port settings' generator didn't build."; }
dotnet "${ASSEMBLIES}/bin/KsaPort.Tools.SettingsGenerator/KsaPort.Tools.SettingsGenerator.dll" check "${REPO}" ||
    die "the port settings' checks failed."
