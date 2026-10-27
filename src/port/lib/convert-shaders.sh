#!/bin/bash
# Edits KSA's shaders (Content/Core/Shaders/) at port time: KSA compiles its GLSL shaders at startup, so the changes
# go into the shader sources. Safe to run again.
#
# Two bugs in the ground clutter shaders:
# 1. PrefixSum.comp declares its ecotype uniform block by hand and leaves out the 16-byte
#    `TextureHandles textureHandles` member that the real EcotypeData layout (GroundClutterCommon.glsl) has
#    before `numObjects`. The shader then reads a texture index as the object count and reads and writes past
#    its buffers: corrupted clutter and GPU hangs, worst with clutter shadows. Fix: add the missing member.
# 2. Solid.vert (colour pass) and SolidDepth.vert (depth pre-pass) must compute bit-identical positions, because
#    the colour pass draws with depth compare EQUAL. Without `invariant gl_Position` Metal's fast math computes
#    them slightly differently: black stripes and see-through patches on clutter. Fix: declare it invariant.
# For the hooks' render scale (src/hooks/), where the shaders' screen size is the render size:
# 3. Sizes that mean pixels on the window use the window's size, which the patches put in the camera data's spare
#    int (0 without render scale: then nothing changes): the level of detail of ground clutter (CullInstances.comp)
#    and of ring rocks (RingMeshCulling.comp), the size of star and planet discs (StaticCelestialDistance.vert) and
#    of the starfield's glow (Star.frag).
# 4. LightFineClusterCompact.comp: a tile whose depth range comes out inverted makes `maxBin - minBin + 1u` wrap
#    to about 4 billion iterations, a GPU hang. Fix: skip such a tile.
#
# For the hooks' shadow search limit (src/hooks/):
# 5. CascadedShadows.glsl: the soft shadows' blocker search ends at the limit in texels the patch puts in the cascades'
#    spare _pad1 (0 without it: the game's search, as far as the widest penumbra).
#
# For speed, with the same image:
# 6. The terrain's (Planet.frag), the vessels' and the kitten's shaders (seven in Mesh/, and Spline.frag) multiply
#    the sun's light by the cascaded shadows for every pixel, also where that light is already zero (surfaces turned
#    away from the sun): the soft shadows' 32 taps for nothing, about half of a cockpit's frame. Fix: look the shadow
#    up only where the light isn't zero (zero times a shadow, at most 1, is zero).
# 7. The cockpit's ray tracing (Raytracing/raygen.rgen) traces a shadow ray towards each light that casts shadows,
#    also from surfaces turned away from that light, whose light is zero. Fix: trace it only where the light isn't
#    zero (the ray's result only ever multiplies that light).
#
# Each fix prints [FIXED], [OK] (already fixed) or [WARN] (the code around it changed in this KSA version:
# either RocketWerkz fixed the bug, or this script needs an update).
#
# Usage: lib/convert-shaders.sh <game folder>
set -eo pipefail
source "$(dirname "$0")/common.sh"

SHADERS="$1/Content/Core/Shaders"
[ -d "${SHADERS}" ] || die "${SHADERS} not found. Is this a KSA game folder?"

# fix <file> <texture-handles|invariant-position|replace> [<old> <new> <what>]: keeps the file's line endings (the
# shaders use CRLF). replace changes the one place <old> appears into <new> (\n in them: a line break).
fix() {
    perl - "${SHADERS}/$1" "$2" "${3:-}" "${4:-}" "${5:-}" << 'PERL'
use strict;
use warnings;
my ($path, $fix, $old, $new, $what) = @ARGV;
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
} elsif ($fix eq 'replace') {
    $old =~ s/\\n/$nl/g;
    $new =~ s/\\n/$nl/g;
    if (index($text, $new) >= 0) { status("OK", "already $what") }
    my $count = () = $text =~ /\Q$old\E/g;
    if ($count != 1) { status("WARN", "the code changed in this KSA version, skipped: $what. Please open an issue.") }
    $text =~ s/\Q$old\E/$new/;
    $done = $what;
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

fix Planet/GroundClutter/PrefixSum.comp texture-handles
fix Planet/GroundClutter/Solid.vert invariant-position
fix Planet/GroundClutter/SolidDepth.vert invariant-position
note="// macOS port: the window's size (render scale)"
fix Common/Global.glsl replace "    int pad0;\n    mat4 galacticPlane;" \
    "    int pad0;\n    mat4 galacticPlane;\n${note}\n#define KSA_WINDOW_HEIGHT (global.camera.pad0 > 0 ? float(global.camera.pad0) : float(global.camera.screenHeight))\n#define KSA_WINDOW_WIDTH (global.camera.pad0 > 0 ? float(global.camera.pad0) * global.camera.aspectRatio : float(global.camera.screenWidth))" \
    "defined the window's size"
fix Planet/GroundClutter/CullInstances.comp replace "projRadius * global.camera.screenHeight)" \
    "projRadius * KSA_WINDOW_HEIGHT)" "made the level of detail use the window's height"
fix Rings/Culling/RingMeshCulling.comp replace "projRadius * global.camera.screenHeight * 0.5;" \
    "projRadius * KSA_WINDOW_HEIGHT * 0.5;" "made the ring rocks' level of detail use the window's height"
fix StaticCelestialDistance.vert replace \
    "scalePixel / global.camera.screenWidth;\n    vertPosition.y *= instanceData.scalePixel / global.camera.screenHeight;" \
    "scalePixel / KSA_WINDOW_WIDTH;\n    vertPosition.y *= instanceData.scalePixel / KSA_WINDOW_HEIGHT;" \
    "made the disc sizes use the window's size"
fix Star.frag replace \
    "    float screenWidth = global.camera.screenWidth;\n    float screenHeight = global.camera.screenHeight;" \
    "    float screenWidth = KSA_WINDOW_WIDTH;\n    float screenHeight = KSA_WINDOW_HEIGHT;" \
    "made the starfield's glow use the window's size"
fix Lighting/LightFineClusterCompact.comp replace "    uint binCount = maxBin - minBin + 1u;" \
    "    if (maxBin < minBin) return;  // macOS port: an inverted depth range would loop for billions of bins\n    uint binCount = maxBin - minBin + 1u;" \
    "skipped tiles with an inverted depth range"
fix Lighting/CascadedShadows.glsl replace "    float searchRadiusTexels = maxPenumbraWorld / texelWorldSize;" \
    "    float searchRadiusTexels = maxPenumbraWorld / texelWorldSize;\n    if (shadowUbo._pad1 > 0.0) searchRadiusTexels = min(searchRadiusTexels, shadowUbo._pad1);  // macOS port: the hooks' shadow search limit" \
    "limited the soft shadows' search where the hooks ask"
note="// macOS port: no shadow where there is no light"
for shader in Mesh/Fur.frag Mesh/ModelPbr.frag; do
    fix "${shader}" replace "    sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
        "    if (any(notEqual(sunlight, vec3(0.0))))  ${note}\n        sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
        "looked the shadows up only where there is sunlight"
done
fix Mesh/ModelTranslucent.frag replace "        sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
    "        if (any(notEqual(sunlight, vec3(0.0))))  ${note}\n            sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
    "looked the shadows up only where there is sunlight"
for shader in Mesh/MeshGlassIndirect.frag Mesh/MeshGlassIndirectRaytraced.frag; do
    fix "${shader}" replace "        sunlight *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
        "        if (any(notEqual(sunlight, vec3(0.0))))  ${note}\n            sunlight *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
        "looked the shadows up only where there is sunlight"
done
for shader in Mesh/MeshIndirect.frag Spline.frag; do
    fix "${shader}" replace "        lightColor *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
        "        if (any(notEqual(lightColor, vec3(0.0))))  ${note}\n            lightColor *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
        "looked the shadows up only where there is sunlight"
done
fix Mesh/StaticObject.frag replace \
    "        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth)" \
    "        float cascadeShadow = 1.0;\n        if (any(notEqual(lightColor, vec3(0.0))))  ${note}\n            cascadeShadow = GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth);\n        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * cascadeShadow" \
    "looked the shadows up only where there is sunlight"
fix Planet/Planet.frag replace \
    "    float cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
    "    float cascadeShadowFactor = 1.0;\n    if (any(notEqual(lightColor, vec3(0.0))))  ${note}\n        cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
    "looked the shadows up only where there is sunlight"
fix Raytracing/raygen.rgen replace "        if(lightData.color.w > 0.5){" \
    "        if(lightData.color.w > 0.5 && any(notEqual(light, vec3(0.0)))){  // macOS port: no shadow ray where there is no light" \
    "traced shadow rays only where there is light"
