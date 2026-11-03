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
# For speed, with the same image (the tab's Skip Shadows on Unlit Sides, which KSA Metal can turn off):
# 6. The terrain's (Planet.frag), the vessels', the kitten's and the buildings' shaders (seven in Mesh/, and
#    Spline.frag) multiply the sun's light by the cascaded shadows for every pixel, also where that light is already
#    zero (surfaces turned away from the sun): the soft shadows' 32 taps for nothing, about half of a cockpit's frame.
#    Fix: look the shadow up only where the light isn't zero (zero times a shadow, at most 1, is zero). The terrain's
#    own sun shadow, the clouds' and the trails' shadows likewise only multiply light that can be zero: the sun's on the
#    terrain (Planet.frag) and on the buildings (Mesh/StaticObject.frag), the planet's own shadow on the ground clutter
#    (Planet/GroundClutter/Solid.frag, for its light and the grass's glow from behind; its cascaded shadows also only
#    where all of those let sunlight through), and the planets' shadow on the ocean's colour (Water/Ocean.frag, cloud
#    shadows). The terrain's and the clouds' shadows take derivatives, so they are skipped only where a whole 2x2 quad
#    of pixels has no light (quad operations, which KSA Metal supports): on the night side, on slopes turned from the
#    sun and in an eclipse. Where the terrain is in the planet's own shadow with no forward lights (vessels' lamps),
#    every term of its colour is zero: such quads are drawn black without their textures. The godrays' march
#    (Atmosphere/Godrays.glsl, which the clouds' raymarch runs too) multiplies the light reaching each step by the
#    terrain's, the trails' and the clouds' shadows (with clouds off, the first two): they are looked up only where that
#    light isn't zero (not on the night side), and the clouds' only where the others let some through.
# 7. The cockpit's ray tracing (Raytracing/raygen.rgen) traces a shadow ray towards each light that casts shadows,
#    also from surfaces turned away from that light, whose light is zero. Fix: trace it only where the light isn't
#    zero (the ray's result only ever multiplies that light).
#
# For the hooks' Milky Way bake (src/hooks/):
# 8. MilkyWay.frag works out the Milky Way's dust clouds for every pixel of the band, from the view direction alone:
#    about 90 noise lookups each. The edit moves them into a function, which reads them from the strip the hooks fill
#    once instead when the hooks bind it in the noise texture's place (it isn't square), and writes
#    MilkyWayBake.comp, the compute shader that fills the strip with that same function.
#
# For speed, with the same image:
# 9. The clouds' noise (Clouds/CloudAntiTiling.glsl) blends three lookups of a 3D noise texture at every sample of the
#    clouds (the raymarch, the shadow volume, the clouds' shadows on the ground), weighted by where the sample lies in
#    a triangle grid; near a triangle's edges one weight is zero, near its corners two, and the lookup is still made
#    and added times zero. Fix: skip a lookup whose weight is zero.
# 10. The clouds' density (Clouds/CloudFunctions.glsl) erodes the cloud's shape with that noise and clamps the result
#    to 1; deep inside a cloud the result is 1 whatever the noise (0..1), and the noise is still looked up: often at
#    the samples of the raymarch's light rays, also at its own and the shadow volume's. Fix: look the noise up only
#    where it can change the result. The erosion's other output, the noise profile, only reaches an unused variable
#    (Clouds/ScatteringFunctions.glsl); the edit is made only while that file reads as it does here.
#
# Each fix prints [FIXED], [OK] (already fixed) or [WARN] (the code around it changed in this KSA version:
# either RocketWerkz fixed the bug, or this script needs an update). An edit that skips work also checks the code that
# uses what it skips, and an edit that needs another one is made only with it: a KSA version that changed either gets
# [WARN] and the game's code, never a different image or a shader that doesn't compile.
#
# Usage: lib/convert-shaders.sh <game folder>
set -eo pipefail
source "$(dirname "$0")/common.sh"

SHADERS="$1/Content/Core/Shaders"
[ -d "${SHADERS}" ] || die "${SHADERS} not found. Is this a KSA game folder?"

# fix <file> <texture-handles|invariant-position|replace> [<old> <new> <what> [<earlier>]]: keeps the file's line
# endings (the shaders use CRLF). replace changes the one place <old> appears into <new> (\n in them: a line break), or
# the one place <earlier> appears: what an earlier version of this script made of <old>.
fix() {
    perl - "${SHADERS}/$1" "$2" "${3:-}" "${4:-}" "${5:-}" "${6:-}" << 'PERL'
use strict;
use warnings;
my ($path, $fix, $old, $new, $what, $earlier) = @ARGV;
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
    s/\\n/$nl/g for $old, $new, $earlier;
    if (index($text, $new) >= 0) { status("OK", "already $what") }
    # The earlier edit first: it can hold <old> (the line a declaration was added after)
    if ($earlier ne '' && (() = $text =~ /\Q$earlier\E/g) == 1) { ($old, $what) = ($earlier, "updated: $what") }
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

# skipped <file> <what>: says that an edit was left out because the code it relies on changed
skipped() {
    echo "    [WARN] ${1##*/}: the code changed in this KSA version, skipped: $2. Please open an issue."
}

# reads <file> <expectation>...: whether <file> still reads as an edit assumes, mainly the code that uses what the edit
# skips: a KSA version that uses it elsewhere then keeps the game's code instead of getting a different image. An
# expectation is text the file contains (\n: a line break), or #<word>=<n>[,<n>...]: how many times a word appears:
# the first <n> in the game's file, the others once the file holds the shadow edits (they test
# !PORT_SKIP_UNLIT_SHADOWS), as this version of the script and earlier ones leave it
reads() {
    perl - "${SHADERS}/$1" "${@:2}" << 'PERL'
use strict;
use warnings;
my ($path, @expected) = @ARGV;
open(my $in, '<:raw', $path) or exit 1;
my $text = do { local $/; <$in> };
close($in);
$text =~ s/\r\n/\n/g;
my $edited = index($text, "!PORT_SKIP_UNLIT_SHADOWS") >= 0;
for (@expected) {
    if (/^#(\w+)=([\d,]+)$/) {
        my ($word, @counts) = ($1, split(/,/, $2));
        @counts = ($counts[0]) unless $edited;
        my $count = () = $text =~ /\b$word\b/g;
        exit 1 unless grep { $_ == $count } @counts;
    } else {
        exit 1 if index($text, s/\\n/\n/gr) < 0;
    }
}
exit 0;
PERL
}

fix Planet/GroundClutter/PrefixSum.comp texture-handles
fix Planet/GroundClutter/Solid.vert invariant-position
fix Planet/GroundClutter/SolidDepth.vert invariant-position
note="// macOS port: the window's size (render scale)"
fix Common/Global.glsl replace "    int pad0;\n    mat4 galacticPlane;" \
    "    int pad0;\n    mat4 galacticPlane;\n${note}\n#define KSA_WINDOW_HEIGHT (global.camera.pad0 > 0 ? float(global.camera.pad0) : float(global.camera.screenHeight))\n#define KSA_WINDOW_WIDTH (global.camera.pad0 > 0 ? float(global.camera.pad0) * global.camera.aspectRatio : float(global.camera.screenWidth))" \
    "defined the window's size"
# The rest use the window's size: without it they wouldn't compile
if grep -qs KSA_WINDOW_HEIGHT "${SHADERS}/Common/Global.glsl"; then
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
else
    skipped Common/Global.glsl "the level of detail, the disc sizes and the starfield's glow at the window's size"
fi
fix Lighting/LightFineClusterCompact.comp replace "    uint binCount = maxBin - minBin + 1u;" \
    "    if (maxBin < minBin) return;  // macOS port: an inverted depth range would loop for billions of bins\n    uint binCount = maxBin - minBin + 1u;" \
    "skipped tiles with an inverted depth range"
# The hooks write the limit into the cascades' spare float
if reads Lighting/CascadedShadows.glsl "    float _pad1;"; then
    fix Lighting/CascadedShadows.glsl replace "    float searchRadiusTexels = maxPenumbraWorld / texelWorldSize;" \
        "    float searchRadiusTexels = maxPenumbraWorld / texelWorldSize;\n    if (shadowUbo._pad1 > 0.0) searchRadiusTexels = min(searchRadiusTexels, shadowUbo._pad1);  // macOS port: the hooks' shadow search limit" \
        "limited the soft shadows' search where the hooks ask"
else
    skipped Lighting/CascadedShadows.glsl "the soft shadows' search limit"
fi
note="// macOS port: a lookup with no weight adds nothing"
fix Clouds/CloudAntiTiling.glsl replace \
    "        vec3 uv = uvw + FastHash(uvec3(vertices[i], vertices[i].y));\n        float noise0 =" \
    "        if (vertexWeights[i] == 0.0) continue;  ${note}\n        vec3 uv = uvw + FastHash(uvec3(vertices[i], vertices[i].y));\n        float noise0 =" \
    "skipped the clouds' noise lookups with no weight"
fix Clouds/CloudAntiTiling.glsl replace \
    "        vec3 hash = FastHash(uvec3(vertices[i], vertices[i].y));\n\n        // Randomize flowmap phase" \
    "        if (vertexWeights[i] == 0.0) continue;  ${note}\n        vec3 hash = FastHash(uvec3(vertices[i], vertices[i].y));\n\n        // Randomize flowmap phase" \
    "skipped the clouds' flowing noise lookups with no weight"
scattering="${SHADERS}/Clouds/ScatteringFunctions.glsl"
if grep -qsF "    highDetail = highDetail * erosionMaxDepth + (1.0 - erosionMaxDepth);" "${scattering}" &&
    grep -qsF "    return clamp(erodedValue / (1.0 - edgeSharpness), 0.0, 1.0);" "${scattering}" &&
    grep -qsF "    return Remap(dimensionalProfile, 0.0, 1.0, MULTIPLE_SCATTERING_EDGE_EFFECT_MIN_VALUE, 1.0);" "${scattering}"; then
    fix Clouds/CloudFunctions.glsl replace \
        "    float noiseSample = SampleInterpolatedCloudTypeNoise(noiseSampler, cloudSpacePosition, innerCloudRadius,\n        currentCloudTypeIndex, nextCloudTypeIndex, cloudTypeInterpolationFraction, interpolatedMipLevels,\n        currentNoiseScale, nextNoiseScale, currentNoiseOffset, nextNoiseOffset, minMaxInvNoiseScales, flowValue);\n\n    // Density, sharpness, multipleScatteringBrightness\n    vec3 interpolatedCloudProperties = mix(uboCloudLayer.cloudTypesData[currentCloudTypeIndex].xzw,\n        uboCloudLayer.cloudTypesData[nextCloudTypeIndex].xzw, cloudTypeInterpolationFraction);\n    \n    float cloudShape = ApplyErosion(noiseSample, dimensionalProfile, CLOUD_EROSION_MAX_DEPTH, interpolatedCloudProperties.y, noiseProfile);" \
        "    // Density, sharpness, multipleScatteringBrightness\n    vec3 interpolatedCloudProperties = mix(uboCloudLayer.cloudTypesData[currentCloudTypeIndex].xzw,\n        uboCloudLayer.cloudTypesData[nextCloudTypeIndex].xzw, cloudTypeInterpolationFraction);\n\n    // macOS port: deep inside a cloud the erosion below is 1 for any noise (0..1, the noise only adds; 1.001: a\n    // margin for rounding): no lookup there. Its noise profile only reaches GetMultipleScatteringEdgeEffect's unused depth\n    float noiseSample = 0.0;\n    float sharpness = interpolatedCloudProperties.y;\n    if (!(sharpness < 1.0 && dimensionalProfile - CLOUD_EROSION_MAX_DEPTH >= 1.001 * (1.0 - sharpness)))\n    {\n        noiseSample = SampleInterpolatedCloudTypeNoise(noiseSampler, cloudSpacePosition, innerCloudRadius,\n            currentCloudTypeIndex, nextCloudTypeIndex, cloudTypeInterpolationFraction, interpolatedMipLevels,\n            currentNoiseScale, nextNoiseScale, currentNoiseOffset, nextNoiseOffset, minMaxInvNoiseScales, flowValue);\n    }\n    \n    float cloudShape = ApplyErosion(noiseSample, dimensionalProfile, CLOUD_EROSION_MAX_DEPTH, interpolatedCloudProperties.y, noiseProfile);" \
        "skipped the clouds' noise where it can't change their density"
else
    skipped Clouds/CloudFunctions.glsl "the noise where it can't change their density"
fi
# 6 and 7 read PORT_SKIP_UNLIT_SHADOWS, a specialization constant (id 900) that KSA Metal sets to false when
# KSA_MTL_UNLIT_SHADOW_SKIP is 0. A shader whose declaration was skipped keeps the game's code: it wouldn't compile.
# declare_switch <file> <line>: declares it after <line>, behind a guard, as a shader may include two of the files that
# declare it; an earlier version's declaration without the guard gets it, one reworded by another version is kept
switch="layout(constant_id = 900) const bool PORT_SKIP_UNLIT_SHADOWS = true;  // macOS port"
declare_switch() {
    if grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/$1" && ! grep -qsF "${switch}" "${SHADERS}/$1"; then
        echo "    [OK] ${1##*/}: already declares the switch for shadows where there is no light"
    else
        fix "$1" replace "$2" \
            "$2\n#ifndef PORT_SKIP_UNLIT_SHADOWS_DECLARED\n#define PORT_SKIP_UNLIT_SHADOWS_DECLARED\n${switch}\n#endif" \
            "declared the switch for shadows where there is no light" "$2\n${switch}"
    fi
}
# quad_ops <file>: enables the quad operations a skip decides with, per 2x2 quad
quad_ops() {
    fix "$1" replace "#version 450\n" \
        "#version 450\n#extension GL_KHR_shader_subgroup_quad : require  // macOS port: the shadows' skip below decides per 2x2 quad\n" \
        "enabled the quad operations for the shadows' skip" \
        "#version 450\n#extension GL_KHR_shader_subgroup_quad : require  // macOS port: the cloud shadows' skip below decides per 2x2 quad\n"
}
declare_switch Lighting/CascadedShadows.glsl "#define CASCADED_SHADOWS_GLSL"
declare_switch Raytracing/raygen.rgen "layout(location = 1) rayPayloadEXT RayPayload rayPayload;"
if grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/Lighting/CascadedShadows.glsl"; then
    note="// macOS port: no shadow where there is no light"
    for shader in Mesh/Fur.frag Mesh/ModelPbr.frag; do
        fix "${shader}" replace "    sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
            "    if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(sunlight, vec3(0.0))))  ${note}\n        sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
            "looked the shadows up only where there is sunlight"
    done
    fix Mesh/ModelTranslucent.frag replace "        sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
        "        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(sunlight, vec3(0.0))))  ${note}\n            sunlight *= GetCascadedShadow(inWorldPos, N, dotNL, viewDepth);" \
        "looked the shadows up only where there is sunlight"
    for shader in Mesh/MeshGlassIndirect.frag Mesh/MeshGlassIndirectRaytraced.frag; do
        fix "${shader}" replace "        sunlight *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
            "        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(sunlight, vec3(0.0))))  ${note}\n            sunlight *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
            "looked the shadows up only where there is sunlight"
    done
    for shader in Mesh/MeshIndirect.frag Spline.frag; do
        fix "${shader}" replace "        lightColor *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
            "        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightColor, vec3(0.0))))  ${note}\n            lightColor *= GetCascadedShadow(inWorldPosition, N, dotNL, viewDepth);" \
            "looked the shadows up only where there is sunlight"
    done

    # The buildings: every shadow multiplies the sun's light in one statement, also the planets' shadow (0 at night).
    # SHOW_SUN, the branch around it, is the same for the whole draw: no branch before the swaps
    quad_ops Mesh/StaticObject.frag
    if grep -qs GL_KHR_shader_subgroup_quad "${SHADERS}/Mesh/StaticObject.frag"; then
        fix Mesh/StaticObject.frag replace \
            "        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth)\n                    * GetCloudShadows(inWorldPosition, planetPos(), L)\n                    * GetTrailShadowTransmittance(inWorldPosition)\n                    * celestialShadow;" \
            "        // macOS port: the shadows only multiply the sun's light, also where it is zero (turned away, or in a planet's\n        // shadow); the terrain's and the clouds' take derivatives, so they are skipped only where the whole 2x2 quad\n        // has none\n        bool portSunlit = !PORT_SKIP_UNLIT_SHADOWS || (any(notEqual(lightColor, vec3(0.0))) && celestialShadow != 0.0);\n        uint portLit = portSunlit ? 1u : 0u;\n        portLit |= subgroupQuadSwapHorizontal(portLit);  // every lane swaps: no branch before the swaps\n        portLit |= subgroupQuadSwapVertical(portLit);\n        float portTerrainShadow = 1.0, portCascadeShadow = 1.0, portCloudShadow = 1.0, portTrailShadow = 1.0;\n        if (portLit != 0u)\n        {\n            portTerrainShadow = getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal);\n            portCloudShadow = GetCloudShadows(inWorldPosition, planetPos(), L);\n        }\n        if (portSunlit)\n        {\n            portCascadeShadow = GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth);\n            portTrailShadow = GetTrailShadowTransmittance(inWorldPosition);\n        }\n        lightColor *= portTerrainShadow * portCascadeShadow * portCloudShadow * portTrailShadow * celestialShadow;" \
            "looked the shadows up only where there is sunlight" \
            "        float cascadeShadow = 1.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightColor, vec3(0.0))))  ${note}\n            cascadeShadow = GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth);\n        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * cascadeShadow\n                    * GetCloudShadows(inWorldPosition, planetPos(), L)\n                    * GetTrailShadowTransmittance(inWorldPosition)\n                    * celestialShadow;"
    fi

    # The terrain: the sun's light, the planet's own shadow and the other shadows only reach the final colour's first
    # term, and at night with no forward lights its other two terms are zero too
    if reads Planet/Planet.frag \
        "    vec4 finalColor = vec4(lightColor * selfShadowFactor * combinedShadowFactor + environmentLighting * ao + punctualLighting, 1.0f);" \
        "    float combinedShadowFactor = sunShadowFactor * cloudShadows * cascadeShadowFactor * trailShadows;" \
        "        vec3 prefilteredAmbient = selfShadowFactor * GetAmbient(" \
        "        vec3 prefilteredReflection = prefilteredAmbient * 0.5;" \
        "                combinedShadowFactor *= ringTransmittance;\n                environmentLighting *= ringTransmittance;" \
        "    vec3 punctualLighting = SampleForwardLights(inWorldPosition.xyz, worldNormal, surfaceColor * averageLuminosity, metallic);" \
        "    outColor = finalColor;" \
        "#finalColor=11" "#combinedShadowFactor=3" "#environmentLighting=4" "#punctualLighting=2" "#outColor=3" \
        "#prefilteredAmbient=3" "#selfShadowFactor=3,4,5" "#cascadeShadowFactor=2,3" "#sunShadowFactor=2,3" \
        "#cloudShadows=2,3" "#trailShadows=2,3" &&
        reads Common/Lighting.glsl "    vec3 diffuse = filteredDiffuseMap * albedo;\n    \n    vec3 specular = vec3(0.0);" \
            "    if (!all(equal(filteredSpecularMap, vec3(0.0))))" "    return kD * diffuse + specular;" &&
        reads Lighting/PunctualLighting.glsl "    vec3 irradiance = vec3(0.0);\n\n    for (int i = 0; i < global.lighting.forwardLightCount; ++i)" \
            "    return (1.0 - inMetallic) * inAlbedo * (1.0 / PI) * irradiance;"; then
        fix Planet/Planet.frag replace \
            "    float cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
            "    float cascadeShadowFactor = 1.0;\n    if (!PORT_SKIP_UNLIT_SHADOWS || (any(notEqual(lightColor, vec3(0.0))) && selfShadowFactor > 0.0))  ${note}\n        cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
            "looked the shadows up only where there is sunlight" \
            "    float cascadeShadowFactor = 1.0;\n    if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightColor, vec3(0.0))))  ${note}\n        cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);"
        quad_ops Planet/Planet.frag
        if grep -qs GL_KHR_shader_subgroup_quad "${SHADERS}/Planet/Planet.frag"; then
            fix Planet/Planet.frag replace \
                "    if (IsTerrainOccludedByOcean(sphereVector))\n    {\n        outColor = vec4(vec3(0.0), 1.0);\n        return;\n    }\n" \
                "    // macOS port: in the planet's own shadow (at night), with no forward lights, every term of the colour below is\n    // multiplied by zero, so it is black, as under the ocean: the textures and lighting are skipped, per 2x2 quad (they\n    // take derivatives); the swaps come before any return. The planet's shadow is worked out here once for both\n    bool portOcean = IsTerrainOccludedByOcean(sphereVector);\n    float portSelfShadow = 0.0;\n    if (!portOcean)\n        portSelfShadow = getSelfCelestialShadow(inWorldPosition.xyz, global.lighting.sunPosition, sphereWorldNormal, uboPlanet.celestialIndex);\n    uint portBlack = (portOcean || (PORT_SKIP_UNLIT_SHADOWS && global.lighting.forwardLightCount == 0 && portSelfShadow == 0.0)) ? 1u : 0u;\n    portBlack &= subgroupQuadSwapHorizontal(portBlack);\n    portBlack &= subgroupQuadSwapVertical(portBlack);\n    // a quad the ocean cuts short keeps the game's shadows below: its returned pixels' swaps would be undefined\n    uint portOceanQuad = portOcean ? 1u : 0u;\n    portOceanQuad |= subgroupQuadSwapHorizontal(portOceanQuad);\n    portOceanQuad |= subgroupQuadSwapVertical(portOceanQuad);\n    if (portOcean || portBlack != 0u)\n    {\n        outColor = vec4(vec3(0.0), 1.0);\n        return;\n    }\n" \
                "drew the terrain black without its textures where it is in the planet's shadow and unlit" \
                "    // macOS port: in the planet's own shadow (at night), with no forward lights, every term of the colour below is\n    // multiplied by zero, so it is black, as under the ocean: the textures and lighting are skipped, per 2x2 quad (they\n    // take derivatives); the swaps come before any return\n    bool portOcean = IsTerrainOccludedByOcean(sphereVector);\n    uint portBlack = (portOcean || (PORT_SKIP_UNLIT_SHADOWS && global.lighting.forwardLightCount == 0 &&\n        getSelfCelestialShadow(inWorldPosition.xyz, global.lighting.sunPosition, sphereWorldNormal, uboPlanet.celestialIndex) == 0.0)) ? 1u : 0u;\n    portBlack &= subgroupQuadSwapHorizontal(portBlack);\n    portBlack &= subgroupQuadSwapVertical(portBlack);\n    if (portOcean || portBlack != 0u)\n    {\n        outColor = vec4(vec3(0.0), 1.0);\n        return;\n    }\n"
        fi
        if grep -qs "float portSelfShadow" "${SHADERS}/Planet/Planet.frag"; then
            fix Planet/Planet.frag replace \
                "    float selfShadowFactor = getSelfCelestialShadow(inWorldPosition.xyz, sunPosition, sphereWorldNormal, uboPlanet.celestialIndex);" \
                "    float selfShadowFactor = portSelfShadow;  // macOS port: worked out above" \
                "reused the planet's shadow worked out for the black skip"
        fi
        if grep -qs "uint portOceanQuad" "${SHADERS}/Planet/Planet.frag"; then
            fix Planet/Planet.frag replace \
                "    float sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, sphereWorldNormal);\n    float cloudShadows = GetCloudShadows(inWorldPosition.xyz, global.lighting.planetPosition.xyz, directionToSun);\n    float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);" \
                "    // macOS port: the terrain's, the clouds' and the trails' shadows only multiply the sun's light, also where it is\n    // zero; the first two take derivatives, so they are skipped only where the whole 2x2 quad has none\n    bool portSunlit = !PORT_SKIP_UNLIT_SHADOWS || (any(notEqual(lightColor, vec3(0.0))) && selfShadowFactor > 0.0);\n    uint portLit = (portSunlit || portOceanQuad != 0u) ? 1u : 0u;\n    portLit |= subgroupQuadSwapHorizontal(portLit);\n    portLit |= subgroupQuadSwapVertical(portLit);\n    float sunShadowFactor = 1.0, cloudShadows = 1.0, trailShadows = 1.0;\n    if (portLit != 0u)\n    {\n        sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, sphereWorldNormal);\n        cloudShadows = GetCloudShadows(inWorldPosition.xyz, global.lighting.planetPosition.xyz, directionToSun);\n    }\n    if (portSunlit)\n        trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);" \
                "looked the terrain's other shadows up only where there is sunlight" \
                "    // macOS port: the terrain's, the clouds' and the trails' shadows only multiply the sun's light, also where it is\n    // zero; they take derivatives, so they are skipped only where the whole 2x2 quad has none\n    uint portLit = (!PORT_SKIP_UNLIT_SHADOWS || (any(notEqual(lightColor, vec3(0.0))) && selfShadowFactor > 0.0)) ? 1u : 0u;\n    portLit |= subgroupQuadSwapHorizontal(portLit);  // every lane swaps: no branch before the swaps\n    portLit |= subgroupQuadSwapVertical(portLit);\n    float sunShadowFactor = 1.0, cloudShadows = 1.0, trailShadows = 1.0;\n    if (portLit != 0u)\n    {\n        sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, sphereWorldNormal);\n        cloudShadows = GetCloudShadows(inWorldPosition.xyz, global.lighting.planetPosition.xyz, directionToSun);\n        trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);\n    }"
        fi
    else
        skipped Planet/Planet.frag "the terrain's shadows where there is no sunlight"
    fi

    # The ground clutter: every shadow multiplies the planet's own (0 at night), for its light and for the grass's
    # glow from behind
    if reads Planet/GroundClutter/Solid.frag \
        "        combinedShadowFactor = shadowFactor * cascadeShadowFactor * cloudShadows * sunShadowFactor * trailShadows;" \
        "    outColor = vec4(lightColor * combinedShadowFactor + environmentLighting * ao + punctualLighting, 1);" \
        "        vec3 fLt = vec3(combinedShadowFactor * (fLTDot + ltAmbient) * (1-thicknessSample));" \
        "#combinedShadowFactor=4" "#shadowFactor=2,3,4" "#sunShadowFactor=2,3,4" "#cloudShadows=2,3,4" \
        "#trailShadows=2,3,4" "#cascadeShadowFactor=2,3"; then
        quad_ops Planet/GroundClutter/Solid.frag
        if grep -qs GL_KHR_shader_subgroup_quad "${SHADERS}/Planet/GroundClutter/Solid.frag"; then
            fix Planet/GroundClutter/Solid.frag replace \
                "        float sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, meshWorldNormal);\n        float cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, meshWorldNormal, dotNL, viewDepth);\n        float cloudShadows = GetCloudShadows(inWorldPosition.xyz, planetPos(), directionToSun);\n        float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);" \
                "        // macOS port: the other shadows only multiply the planet's own, also where it is zero (at night). The terrain's\n        // and the clouds' take derivatives, so they are skipped only where the whole 2x2 quad is in it, and the\n        // cascades' also where those let no sunlight through\n        bool portSunlit = !PORT_SKIP_UNLIT_SHADOWS || shadowFactor != 0.0;\n        uint portLit = portSunlit ? 1u : 0u;\n        portLit |= subgroupQuadSwapHorizontal(portLit);  // every lane swaps: no branch before the swaps\n        portLit |= subgroupQuadSwapVertical(portLit);\n        float sunShadowFactor = 1.0, cloudShadows = 1.0, trailShadows = 1.0;\n        if (portLit != 0u)\n        {\n            sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, meshWorldNormal);\n            cloudShadows = GetCloudShadows(inWorldPosition.xyz, planetPos(), directionToSun);\n        }\n        if (portSunlit)\n            trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);\n        float cascadeShadowFactor = 1.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || shadowFactor * cloudShadows * sunShadowFactor * trailShadows != 0.0)\n            cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, meshWorldNormal, dotNL, viewDepth);" \
                "looked the grass's shadows up only where the planet's shadow lets sunlight through" \
                "        float sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, meshWorldNormal);\n        float cloudShadows = GetCloudShadows(inWorldPosition.xyz, planetPos(), directionToSun);\n        float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);\n        float cascadeShadowFactor = 1.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || shadowFactor * cloudShadows * sunShadowFactor * trailShadows != 0.0)  ${note}\n            cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, meshWorldNormal, dotNL, viewDepth);"
        fi
    else
        skipped Planet/GroundClutter/Solid.frag "the grass's shadows where there is no sunlight"
    fi
fi

# The godrays' march: its shadows only multiply the light received, in (1 - shadows) * light
declare_switch Atmosphere/Godrays.glsl "#define GODRAYS_GLSL_INCLUDED"
if grep -qs "constant_id = 900" "${SHADERS}/Atmosphere/Godrays.glsl" &&
    reads Atmosphere/Godrays.glsl \
        "        vec3 lightShadowed = (1.0 - sunShadowFactor * cloudShadowFactor) * lightReceived;" \
        "#sunShadowFactor=4,7" "#cloudShadowFactor=3" "#lightShadowed=4" "#lightReceived=4,6"; then
    fix Atmosphere/Godrays.glsl replace \
        "        // Apply trail shadows alongside terrain shadows\n        float sunShadowFactor = getSunShadow(shadowUbo, rayPosition, zenithDirection) *\n            GetTrailShadowTransmittance(rayPosition);\n\n        float seaLevelAltitude = max(distanceFromPlanetCenter - atmosphereData.bottomRadius, 0.0);\n        vec2 densityFalloff = exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight));\n        float ozoneDensity = saturate(1.0 - abs(seaLevelAltitude - atmosphereData.ozoneCoefficientsAndAltitude.w) / atmosphereData.ozoneExtent);\n        vec3 localAbsorption = rayleighAbsorption * densityFalloff.x + mieAbsorption * densityFalloff.y + atmosphereData.ozoneCoefficientsAndAltitude.xyz * ozoneDensity;\n        vec3 stepTransmittance = exp(-localAbsorption * currentStepSize);\n\n        vec3 lightReceived = GetTransmittanceFromLut(atmosphereDataUbo.planetRadius, atmosphereData.bottomRadius, atmosphereData.topRadius,\n            distanceFromPlanetCenter, dot(zenithDirection, directionToSun), transmittanceLut, lutLayer, transmittanceLutDimensions);\n        \n        float cloudShadowFactor = 1.0;\n        vec3 shadowVolumeIntersection;\n\n        if (GetShadowVolumeSamplePosition(rayPosition, planetPosition, directionToSun," \
        "        float seaLevelAltitude = max(distanceFromPlanetCenter - atmosphereData.bottomRadius, 0.0);\n        vec2 densityFalloff = exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight));\n        float ozoneDensity = saturate(1.0 - abs(seaLevelAltitude - atmosphereData.ozoneCoefficientsAndAltitude.w) / atmosphereData.ozoneExtent);\n        vec3 localAbsorption = rayleighAbsorption * densityFalloff.x + mieAbsorption * densityFalloff.y + atmosphereData.ozoneCoefficientsAndAltitude.xyz * ozoneDensity;\n        vec3 stepTransmittance = exp(-localAbsorption * currentStepSize);\n\n        vec3 lightReceived = GetTransmittanceFromLut(atmosphereDataUbo.planetRadius, atmosphereData.bottomRadius, atmosphereData.topRadius,\n            distanceFromPlanetCenter, dot(zenithDirection, directionToSun), transmittanceLut, lutLayer, transmittanceLutDimensions);\n\n        // macOS port: the shadows below only multiply the light received: no terrain or trail shadow where it is\n        // zero, no cloud shadow where those make it zero\n        float sunShadowFactor = 0.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightReceived, vec3(0.0))))\n        {\n            // Apply trail shadows alongside terrain shadows\n            sunShadowFactor = getSunShadow(shadowUbo, rayPosition, zenithDirection) *\n                GetTrailShadowTransmittance(rayPosition);\n        }\n\n        float cloudShadowFactor = 1.0;\n        vec3 shadowVolumeIntersection;\n\n        if ((!PORT_SKIP_UNLIT_SHADOWS || sunShadowFactor != 0.0) && GetShadowVolumeSamplePosition(rayPosition, planetPosition, directionToSun," \
        "looked the godrays' shadows up only where sunlight reaches the march's steps"
    fix Atmosphere/Godrays.glsl replace \
        "        // Apply trail shadows alongside terrain shadows\n        float sunShadowFactor = getSunShadow(shadowUbo, rayPosition, zenithDirection) *\n            GetTrailShadowTransmittance(rayPosition);\n\n        float seaLevelAltitude = max(distanceFromPlanetCenter - atmosphereData.bottomRadius, 0.0);\n        vec2 densityFalloff = exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight));\n        float ozoneDensity = saturate(1.0 - abs(seaLevelAltitude - atmosphereData.ozoneCoefficientsAndAltitude.w) / atmosphereData.ozoneExtent);\n        vec3 localAbsorption = rayleighAbsorption * densityFalloff.x + mieAbsorption * densityFalloff.y + atmosphereData.ozoneCoefficientsAndAltitude.xyz * ozoneDensity;\n        vec3 stepTransmittance = exp(-localAbsorption * currentStepSize);\n\n        vec3 lightReceived = GetTransmittanceFromLut(atmosphereDataUbo.planetRadius, atmosphereData.bottomRadius, atmosphereData.topRadius,\n            distanceFromPlanetCenter, dot(zenithDirection, directionToSun), transmittanceLut, lutLayer, transmittanceLutDimensions);\n\n        vec3 lightShadowed = (1.0 - sunShadowFactor) * lightReceived;" \
        "        float seaLevelAltitude = max(distanceFromPlanetCenter - atmosphereData.bottomRadius, 0.0);\n        vec2 densityFalloff = exp(-seaLevelAltitude / vec2(rayleighScaleHeight, mieScaleHeight));\n        float ozoneDensity = saturate(1.0 - abs(seaLevelAltitude - atmosphereData.ozoneCoefficientsAndAltitude.w) / atmosphereData.ozoneExtent);\n        vec3 localAbsorption = rayleighAbsorption * densityFalloff.x + mieAbsorption * densityFalloff.y + atmosphereData.ozoneCoefficientsAndAltitude.xyz * ozoneDensity;\n        vec3 stepTransmittance = exp(-localAbsorption * currentStepSize);\n\n        vec3 lightReceived = GetTransmittanceFromLut(atmosphereDataUbo.planetRadius, atmosphereData.bottomRadius, atmosphereData.topRadius,\n            distanceFromPlanetCenter, dot(zenithDirection, directionToSun), transmittanceLut, lutLayer, transmittanceLutDimensions);\n\n        // macOS port: the shadows below only multiply the light received: none where it is zero\n        float sunShadowFactor = 0.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightReceived, vec3(0.0))))\n        {\n            // Apply trail shadows alongside terrain shadows\n            sunShadowFactor = getSunShadow(shadowUbo, rayPosition, zenithDirection) *\n                GetTrailShadowTransmittance(rayPosition);\n        }\n\n        vec3 lightShadowed = (1.0 - sunShadowFactor) * lightReceived;" \
        "looked the godrays' shadows up only where sunlight reaches the march's steps, with clouds off"
elif grep -qs "constant_id = 900" "${SHADERS}/Atmosphere/Godrays.glsl"; then
    skipped Atmosphere/Godrays.glsl "the godrays' shadows where no sunlight reaches the march"
fi

# The ocean: the cloud shadows only reach its own colour, which the planets' shadow multiplies at the end
declare_switch Water/Ocean.frag "#include \"../Clouds/CloudShadows.glsl\""
if grep -qs "constant_id = 900" "${SHADERS}/Water/Ocean.frag" &&
    reads Water/Ocean.frag "    sunSpecular *= cloudShadow;" "mix(cloudShadow, 1.0, 0.5));" \
        "    vec3 foamColor = 0.15 * foam * (cloudShadow * transmittanceToSun" \
        "    oceanColor = oceanColor * (1.0 - foam) + foamColor;" \
        "    outColor = vec4(oceanColor + (1.0 - fresnel) * transmittance * backGroundColor.rgb, 0.0);" \
        "#cloudShadow=4,5" "#sunSpecular=5" "#foamColor=2" "#oceanColor=5" "#outColor=4"; then
    quad_ops Water/Ocean.frag
    if grep -qs GL_KHR_shader_subgroup_quad "${SHADERS}/Water/Ocean.frag"; then
        fix Water/Ocean.frag replace \
            "    float cloudShadow = GetCloudShadows(inOceanWorldPosition.xyz, global.lighting.planetPosition.xyz, L);" \
            "    // macOS port: the cloud shadows only reach the ocean's own colour, which the planets' shadow multiplies at the\n    // end, also where it is zero (at night); they take derivatives, so they are skipped only where a whole 2x2 quad is in it\n    float portCelestialShadow = getCelestialShadow(inOceanWorldPosition.xyz, global.lighting.sunPosition);\n    uint portLit = (!PORT_SKIP_UNLIT_SHADOWS || portCelestialShadow != 0.0) ? 1u : 0u;\n    portLit |= subgroupQuadSwapHorizontal(portLit);  // every lane swaps: no branch before the swaps\n    portLit |= subgroupQuadSwapVertical(portLit);\n    float cloudShadow = 1.0;\n    if (portLit != 0u)\n        cloudShadow = GetCloudShadows(inOceanWorldPosition.xyz, global.lighting.planetPosition.xyz, L);" \
            "looked the ocean's cloud shadows up only where the planets' shadow lets sunlight through"
        if grep -qs "float portCelestialShadow" "${SHADERS}/Water/Ocean.frag"; then
            fix Water/Ocean.frag replace "    oceanColor *= getCelestialShadow(inOceanWorldPosition.xyz, global.lighting.sunPosition);" \
                "    oceanColor *= portCelestialShadow;  // macOS port: worked out above" \
                "reused the planets' shadow worked out for the cloud shadows' skip"
        fi
    fi
elif grep -qs "constant_id = 900" "${SHADERS}/Water/Ocean.frag"; then
    skipped Water/Ocean.frag "the cloud shadows where the planets' shadow lets no sunlight through"
fi

# The cockpit's ray tracing: the shadow ray's result only multiplies the light
if grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/Raytracing/raygen.rgen" &&
    reads Raytracing/raygen.rgen "            light *= shadow;\n            light += indirectContrib;\n        } else {\n            light *= shadow;\n        }" \
        "#shadow=4,5"; then
    fix Raytracing/raygen.rgen replace "        if(lightData.color.w > 0.5){" \
        "        if(lightData.color.w > 0.5 && (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(light, vec3(0.0))))){  // macOS port: no shadow ray where there is no light" \
        "traced shadow rays only where there is light"
elif grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/Raytracing/raygen.rgen"; then
    skipped Raytracing/raygen.rgen "the shadow rays where there is no light"
fi

# 8. MilkyWay.frag: the clouds' function, and MilkyWayBake.comp made from it. The bake writes the clouds for each
# texel of a strip that spans the band, the longitude -PI..PI across and sampleDir.y -BAND_HEIGHT..BAND_HEIGHT down
# (texel centres), with the directions the fragment shader would see there.
perl - "${SHADERS}/MilkyWay.frag" "${SHADERS}/MilkyWayBake.comp" << 'PERL'
use strict;
use warnings;
my ($path, $bake) = @ARGV;
sub status { printf "    [%s] %s: %s\n", $_[0], $_[1], $_[2]; }
sub warned { status("WARN", "MilkyWay.frag", "the code changed in this KSA version, skipped: $_[0]. Please open an issue."); unlink $bake; exit 0 }
open(my $in, '<:raw', $path) or do { status("WARN", "MilkyWay.frag", "not found"); exit 0 };
my $text = do { local $/; <$in> };
close($in);
my $nl = $text =~ /\r\n/ ? "\r\n" : "\n";
# The lookup, the part of the edit that is the port's own: a file edited by another version of this script says so
my $lookup = join($nl,
    "float portMilkyWayCloudsAt(vec2 cloudUV, vec3 sampleDir)",
    "{",
    "    ivec2 size = textureSize(milkyWayNoise, 0);",
    "    if (size.x == size.y)",
    "        return portMilkyWayClouds(cloudUV, sampleDir);",
    "    vec2 strip = vec2(cloudUV.x / (2.0 * PI) + 0.5, cloudUV.y / (2.0 * BAND_HEIGHT) + 0.5);",
    "    strip.y = clamp(strip.y, 0.5 / float(size.y), 1.0 - 0.5 / float(size.y));",
    "    return textureLod(milkyWayNoise, strip, 0.0).r;",
    "}");
if ($text =~ /portMilkyWayCloudsAt/ && index($text, $lookup) < 0) {
    status("WARN", "MilkyWay.frag", "edited by another version of the port, skipped: the Milky Way's clouds in a function. Port the game again from its archive.");
    unlink $bake;
    exit 0;
}
if ($text !~ /portMilkyWayCloudsAt/) {
    my $count = () = $text =~ /^void main\(\)/mg;
    $count == 1 or warned("the Milky Way's clouds in a function");
    $text =~ s{(^    vec2 cloudUV = vec2\(fastAtan2\(-sampleDir\.x, sampleDir\.z\), sampleDir\.y\);\r?\n)(.*?^        cloudsFinal = mix\(0\.0, mix\(seamClouds, cloudsFinal, seamMask\), seamMask\);\r?\n    \}\r?\n)}
              {$1$nl    float cloudsFinal = portMilkyWayCloudsAt(cloudUV, sampleDir);  // macOS port: the clouds' function$nl}ms
        or warned("the Milky Way's clouds in a function");
    my $body = $2;
    # The rest of main must not need what moved into the function
    my ($rest) = $text =~ /^void main\(\)(.*)/ms;
    $rest =~ /\b(?:cloudsLowA|cloudsLowB|cloudsHigh|seamClouds|seamThresh|seamMask|SEED_\w+)\b/ and warned("the Milky Way's clouds in a function");
    my $function = join($nl,
        "// macOS port: the clouds depend on the view direction alone. The hooks' Milky Way bake fills a strip with them",
        "// once (MilkyWayBake.comp, made from this file) and binds it in milkyWayNoise's place: it isn't square.",
        "float portMilkyWayClouds(vec2 cloudUV, vec3 sampleDir)",
        "{") . $body . join($nl,
        "    return cloudsFinal;",
        "}",
        "",
        $lookup, "", "");
    $text =~ s/^(void main\(\))/$function$1/m;
    open(my $out, '>:raw', $path) or die "cannot write $path: $!";
    print $out $text;
    close($out);
    status("FIXED", "MilkyWay.frag", "put the clouds in a function the hooks' bake shares");
} else {
    status("OK", "MilkyWay.frag", "already puts the clouds in a function the hooks' bake shares");
}
# The bake: this file without its inputs, outputs and main, the cubemap's binding replaced by the strip's
my $comp = $text;
($comp =~ s/^layout\s*\(location\s*=\s*0\)\s*(?:in|out)\s[^\n]*\n//mg) == 2 or warned("the bake's inputs and outputs");
$comp =~ s{^layout\s*\(set\s*=\s*1,\s*binding\s*=\s*0\)\s*uniform\s+samplerCube\s+milkyWay;}
          {layout (set = 1, binding = 0, r16f) uniform writeonly image2D portMilkyWayStrip;${nl}layout (local_size_x = 8, local_size_y = 8) in;}m
    or warned("the bake's bindings");
$comp =~ /^layout\s*\(set\s*=\s*1,\s*binding\s*=\s*1\)\s*uniform\s+sampler2D\s+milkyWayNoise;/m or warned("the bake's bindings");
$comp =~ s/^void main\(\).*//ms;
$comp =~ s/^(#version[^\n]*\n)/$1\/\/ macOS port: made from MilkyWay.frag by the port, for the hooks' Milky Way bake$nl/
    or warned("the bake's version line");
$comp .= join($nl,
    "void main()",
    "{",
    "    ivec2 size = imageSize(portMilkyWayStrip);",
    "    ivec2 texel = ivec2(gl_GlobalInvocationID.xy);",
    "    if (any(greaterThanEqual(texel, size)))",
    "        return;",
    "    vec2 cloudUV = vec2(((float(texel.x) + 0.5) / float(size.x) - 0.5) * 2.0 * PI,",
    "                        ((float(texel.y) + 0.5) / float(size.y) - 0.5) * 2.0 * BAND_HEIGHT);",
    "    float across = sqrt(1.0 - cloudUV.y * cloudUV.y);",
    "    vec3 sampleDir = vec3(-sin(cloudUV.x) * across, cloudUV.y, cos(cloudUV.x) * across);",
    "    imageStore(portMilkyWayStrip, texel, vec4(portMilkyWayClouds(cloudUV, sampleDir)));",
    "}", "");
if (open(my $old, '<:raw', $bake)) {
    my $same = do { local $/; <$old> } eq $comp;
    close($old);
    if ($same) { status("OK", "MilkyWayBake.comp", "already made the Milky Way bake's shader"); exit 0 }
}
open(my $out, '>:raw', $bake) or die "cannot write $bake: $!";
print $out $comp;
close($out);
status("FIXED", "MilkyWayBake.comp", "made the Milky Way bake's shader");
PERL
