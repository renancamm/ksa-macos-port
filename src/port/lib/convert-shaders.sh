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
# 6. The terrain's (Planet.frag), the vessels' and the kitten's shaders (seven in Mesh/, and Spline.frag) multiply
#    the sun's light by the cascaded shadows for every pixel, also where that light is already zero (surfaces turned
#    away from the sun): the soft shadows' 32 taps for nothing, about half of a cockpit's frame. Fix: look the shadow
#    up only where the light isn't zero (zero times a shadow, at most 1, is zero). The ground clutter's Solid.frag
#    multiplies the cascaded shadows with the planet's, the terrain's, the clouds' and the trails' shadows, for its
#    light and the grass's glow from behind: it looks the cascaded shadows up only where those let sunlight through
#    (at night, or in a mountain's shadow, nowhere). The terrain's own sun shadow, the clouds' and the trails' shadows
#    likewise only multiply light that can be zero: the sun's on the terrain (Planet.frag), and the planets' shadow on
#    the ocean's colour (Water/Ocean.frag, cloud shadows). They take derivatives, so they are skipped only where a
#    whole 2x2 quad of pixels has no light (quad operations, which KSA Metal supports): on the night side, on slopes
#    turned from the sun and in an eclipse. Where the terrain is in the planet's own shadow with no forward lights
#    (vessels' lamps), every term of its colour is zero: such quads are drawn black without their textures.
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
note="// macOS port: a lookup with no weight adds nothing"
fix Clouds/CloudAntiTiling.glsl replace \
    "        vec3 uv = uvw + FastHash(uvec3(vertices[i], vertices[i].y));\n        float noise0 =" \
    "        if (vertexWeights[i] == 0.0) continue;  ${note}\n        vec3 uv = uvw + FastHash(uvec3(vertices[i], vertices[i].y));\n        float noise0 =" \
    "skipped the clouds' noise lookups with no weight"
fix Clouds/CloudAntiTiling.glsl replace \
    "        vec3 hash = FastHash(uvec3(vertices[i], vertices[i].y));\n\n        // Randomize flowmap phase" \
    "        if (vertexWeights[i] == 0.0) continue;  ${note}\n        vec3 hash = FastHash(uvec3(vertices[i], vertices[i].y));\n\n        // Randomize flowmap phase" \
    "skipped the clouds' flowing noise lookups with no weight"
# 6 and 7 read PORT_SKIP_UNLIT_SHADOWS, a specialization constant (id 900) that KSA Metal sets to false when
# KSA_MTL_UNLIT_SHADOW_SKIP is 0. A shader whose declaration was skipped keeps the game's code: it wouldn't compile.
# declare_switch <file> <line>: declares it after <line>, once (the check is the name, so a reworded declaration can't
# be added twice)
declare_switch() {
    if grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/$1"; then
        echo "    [OK] ${1##*/}: already declares the switch for shadows where there is no light"
    else
        fix "$1" replace "$2" "$2\nlayout(constant_id = 900) const bool PORT_SKIP_UNLIT_SHADOWS = true;  // macOS port" \
            "declared the switch for shadows where there is no light"
    fi
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
    fix Mesh/StaticObject.frag replace \
        "        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth)" \
        "        float cascadeShadow = 1.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightColor, vec3(0.0))))  ${note}\n            cascadeShadow = GetCascadedShadow(inWorldPosition, geometricNormal, dotNL, viewDepth);\n        lightColor *= getSunShadowFiltered(terrainShadowUbo, inWorldPosition, geometricNormal)\n                    * cascadeShadow" \
        "looked the shadows up only where there is sunlight"
    fix Planet/Planet.frag replace \
        "    float cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
        "    float cascadeShadowFactor = 1.0;\n    if (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(lightColor, vec3(0.0))))  ${note}\n        cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, worldNormal, dotNL, -(global.camera.view * inWorldPosition).z);" \
        "looked the shadows up only where there is sunlight"
    fix Planet/Planet.frag replace "#version 450\n" \
        "#version 450\n#extension GL_KHR_shader_subgroup_quad : require  // macOS port: the shadows' skip below decides per 2x2 quad\n" \
        "enabled the quad operations for the shadows' skip"
    if grep -qs GL_KHR_shader_subgroup_quad "${SHADERS}/Planet/Planet.frag"; then
        fix Planet/Planet.frag replace \
            "    float sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, sphereWorldNormal);\n    float cloudShadows = GetCloudShadows(inWorldPosition.xyz, global.lighting.planetPosition.xyz, directionToSun);\n    float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);" \
            "    // macOS port: the terrain's, the clouds' and the trails' shadows only multiply the sun's light, also where it is\n    // zero; they take derivatives, so they are skipped only where the whole 2x2 quad has none\n    uint portLit = (!PORT_SKIP_UNLIT_SHADOWS || (any(notEqual(lightColor, vec3(0.0))) && selfShadowFactor > 0.0)) ? 1u : 0u;\n    portLit |= subgroupQuadSwapHorizontal(portLit);  // every lane swaps: no branch before the swaps\n    portLit |= subgroupQuadSwapVertical(portLit);\n    float sunShadowFactor = 1.0, cloudShadows = 1.0, trailShadows = 1.0;\n    if (portLit != 0u)\n    {\n        sunShadowFactor = getSunShadowFiltered(terrainShadowUbo, inWorldPosition.xyz, sphereWorldNormal);\n        cloudShadows = GetCloudShadows(inWorldPosition.xyz, global.lighting.planetPosition.xyz, directionToSun);\n        trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);\n    }" \
            "looked the terrain's other shadows up only where there is sunlight"
        fix Planet/Planet.frag replace \
            "    if (IsTerrainOccludedByOcean(sphereVector))\n    {\n        outColor = vec4(vec3(0.0), 1.0);\n        return;\n    }\n" \
            "    // macOS port: in the planet's own shadow (at night), with no forward lights, every term of the colour below is\n    // multiplied by zero, so it is black, as under the ocean: the textures and lighting are skipped, per 2x2 quad (they\n    // take derivatives); the swaps come before any return\n    bool portOcean = IsTerrainOccludedByOcean(sphereVector);\n    uint portBlack = (portOcean || (PORT_SKIP_UNLIT_SHADOWS && global.lighting.forwardLightCount == 0 &&\n        getSelfCelestialShadow(inWorldPosition.xyz, global.lighting.sunPosition, sphereWorldNormal, uboPlanet.celestialIndex) == 0.0)) ? 1u : 0u;\n    portBlack &= subgroupQuadSwapHorizontal(portBlack);\n    portBlack &= subgroupQuadSwapVertical(portBlack);\n    if (portOcean || portBlack != 0u)\n    {\n        outColor = vec4(vec3(0.0), 1.0);\n        return;\n    }\n" \
            "drew the terrain black without its textures where it is in the planet's shadow and unlit"
    fi
    fix Planet/GroundClutter/Solid.frag replace \
        "        float cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, meshWorldNormal, dotNL, viewDepth);\n        float cloudShadows = GetCloudShadows(inWorldPosition.xyz, planetPos(), directionToSun);\n        float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);" \
        "        float cloudShadows = GetCloudShadows(inWorldPosition.xyz, planetPos(), directionToSun);\n        float trailShadows = GetTrailShadowTransmittance(inWorldPosition.xyz);\n        float cascadeShadowFactor = 1.0;\n        if (!PORT_SKIP_UNLIT_SHADOWS || shadowFactor * cloudShadows * sunShadowFactor * trailShadows != 0.0)  ${note}\n            cascadeShadowFactor = GetCascadedShadow(inWorldPosition.xyz, meshWorldNormal, dotNL, viewDepth);" \
        "looked the grass's shadows up only where its other shadows let sunlight through"
fi
declare_switch Water/Ocean.frag "#include \"../Clouds/CloudShadows.glsl\""
if grep -qs "constant_id = 900" "${SHADERS}/Water/Ocean.frag"; then
    fix Water/Ocean.frag replace "#version 450\n" \
        "#version 450\n#extension GL_KHR_shader_subgroup_quad : require  // macOS port: the cloud shadows' skip below decides per 2x2 quad\n" \
        "enabled the quad operations for the cloud shadows' skip"
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
fi
if grep -qs PORT_SKIP_UNLIT_SHADOWS "${SHADERS}/Raytracing/raygen.rgen"; then
    fix Raytracing/raygen.rgen replace "        if(lightData.color.w > 0.5){" \
        "        if(lightData.color.w > 0.5 && (!PORT_SKIP_UNLIT_SHADOWS || any(notEqual(light, vec3(0.0))))){  // macOS port: no shadow ray where there is no light" \
        "traced shadow rays only where there is light"
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
