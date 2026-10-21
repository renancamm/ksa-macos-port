#!/bin/bash
# Fixes two bugs in KSA's ground clutter shaders (Content/Core/Shaders/Planet/GroundClutter/). KSA compiles its
# GLSL shaders at startup, so the fixes go into the shader sources. Safe to run again.
#
# 1. PrefixSum.comp declares its ecotype uniform block by hand and leaves out the 16-byte
#    `TextureHandles textureHandles` member that the real EcotypeData layout (GroundClutterCommon.glsl) has
#    before `numObjects`. The shader then reads a texture index as the object count and reads and writes past
#    its buffers: corrupted clutter and GPU hangs, worst with clutter shadows. Fix: add the missing member.
# 2. Solid.vert (colour pass) and SolidDepth.vert (depth pre-pass) must compute bit-identical positions, because
#    the colour pass draws with depth compare EQUAL. Without `invariant gl_Position` Metal's fast math computes
#    them slightly differently: black stripes and see-through patches on clutter. Fix: declare it invariant.
#
# Each fix prints [FIXED], [OK] (already fixed) or [WARN] (the code around it changed in this KSA version:
# either RocketWerkz fixed the bug, or this script needs an update).
#
# Usage: scripts/convert-shaders.sh <game folder>
set -eo pipefail
source "$(dirname "$0")/lib.sh"

SHADERS="$1/Content/Core/Shaders/Planet/GroundClutter"
[ -d "${SHADERS}" ] || die "${SHADERS} not found. Is this a KSA game folder?"

# fix <file> <texture-handles|invariant-position>: keeps the file's line endings (the shaders use CRLF)
fix() {
    perl - "${SHADERS}/$1" "$2" << 'PERL'
use strict;
use warnings;
my ($path, $fix) = @ARGV;
my $name = (split m{/}, $path)[-1];
sub status { printf "    [%s] %s: %s\n", $_[0], $name, $_[1]; exit 0 }
open(my $in, '<:raw', $path) or do { status("WARN", "not found") };
my $text = do { local $/; <$in> };
close($in);
my $nl = $text =~ /\r\n/ ? "\r\n" : "\n";
my $note = "// macOS port: ";
my $done;
if ($fix eq 'texture-handles') {
    if ($text =~ /^\s*TextureHandles\s+textureHandles\s*;/m) { status("OK", "already has textureHandles") }
    my $old = "    PlacementParams placementParams;$nl    uint numObjects;$nl";
    my $count = () = $text =~ /\Q$old\E/g;
    if ($count != 1) { status("WARN", "uniform block changed in this KSA version, fix skipped. Please open an issue.") }
    my $new = "    PlacementParams placementParams;$nl"
        . "    TextureHandles textureHandles;  ${note}was missing; must match EcotypeData (GroundClutterCommon.glsl)$nl"
        . "    uint numObjects;$nl";
    $text =~ s/\Q$old\E/$new/;
    $done = "added TextureHandles textureHandles";
} else {
    if ($text =~ /^\s*invariant\s+gl_Position\s*;/m) { status("OK", "already declares gl_Position invariant") }
    my $count = () = $text =~ /^void main\(\)/mg;
    if ($count != 1) { status("WARN", "main() changed in this KSA version, fix skipped. Please open an issue.") }
    my $insert = "${note}depth pre-pass and colour pass (depth EQUAL) need bit-identical positions on Metal$nl"
        . "invariant gl_Position;$nl$nl";
    $text =~ s/^(void main\(\))/$insert$1/m;
    $done = "declared gl_Position invariant";
}
open(my $out, '>:raw', $path) or die "cannot write $path: $!";
print $out $text;
close($out);
status("FIXED", $done);
PERL
}

fix PrefixSum.comp texture-handles
fix Solid.vert invariant-position
fix SolidDepth.vert invariant-position
