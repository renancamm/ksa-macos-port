using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.VulkanApi;
using HarmonyLib;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Rendering.Water.Rendering;

namespace KsaMetalHooks;

// Ocean blending skip. The ocean is drawn blending into the image behind it (BlendTransmittance: colour = ocean + what
// is behind x the ocean's alpha, alpha = the ocean's x what is behind), and Ocean.frag writes alpha 0 everywhere except
// where the shore filter fades the ocean out; GetShoreFilterFade returns 0 with the camera below 10 km (half of
// SHORE_FILTER_ALTITUDE), and its fade is still 0 at 10 km. There the blend writes the ocean's colour and alpha 0
// unchanged, but it still reads every sample behind the ocean and keeps the GPU's hidden-surface removal off for the
// ocean's overlapping triangles. So with the camera below 9 km (a margin for the shader's float altitude) the solid
// ocean is drawn with twins of its two pipelines that don't blend: about 10 % less GPU time at sea level with MSAA on an
// M5, the same image. Wire-frame mode keeps the game's pipelines. The equivalence holds for the stock shader: Apply
// checks the lines it rests on and skips when they changed.
internal static class OceanBlending
{
    private const double MaxAltitude = 9000.0;
    // What the equivalence rests on, in Content/Core/Shaders/Water
    private static readonly (string File, string Line)[] ShaderLines =
    [
        ("Ocean.frag", "outColor = vec4(oceanColor + (1.0 - fresnel) * transmittance * backGroundColor.rgb, 0.0);"),
        ("Ocean.frag", "outColor = mix(outColor, vec4(0.0, 0.0, 0.0, 1.0), shoreFilterFade);"),
        ("OceanFragmentShaderFunctions.glsl", "if (cameraAltitude < 0.5 * SHORE_FILTER_ALTITUDE)\n        return 0.0;"),
        ("OceanFunctions.glsl", "#define SHORE_FILTER_ALTITUDE 20000.0"),
    ];
    // The game's solid pipelines (by handle) and their twins without blending
    private static readonly Dictionary<nint, VkPipeline> Twins = new();
    private static FieldInfo? _pipeline, _tessellationPipeline, _layout, _shaders, _tessellationShaders;
    private static MethodInfo? _create;
    private static object? _solid, _depth, _noBlend;
    [ThreadStatic] private static bool _low;
    private static bool _failed;  // the game's pipelines from then on (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        string water = Path.Combine(Hooks.Resources, "Content", "Core", "Shaders", "Water");
        foreach (var (file, line) in ShaderLines)
        {
            string path = Path.Combine(water, file);
            if (!File.Exists(path) || !File.ReadAllText(path).Replace("\r\n", "\n").Contains(line))
                throw new Skip($"the ocean shader changed ({file})");
        }
        _pipeline = Find.Field(typeof(OceanRenderer), "_oceanPipeline");
        _tessellationPipeline = Find.Field(typeof(OceanRenderer), "_oceanTesselationPipeline");
        _layout = Find.Field(typeof(OceanRenderer), "_oceanPipelineLayout");
        _shaders = Find.Field(typeof(OceanRenderer), "_oceanShaders");
        _tessellationShaders = Find.Field(typeof(OceanRenderer), "_oceanTesselationShaders");
        _create = Find.Method(typeof(OceanRenderer), "CreateOceanPipeline");
        var parameters = _create.GetParameters();
        if (parameters.Length != 6 || !parameters[4].ParameterType.IsEnum || parameters[5].ParameterType != typeof(bool))
            throw new Skip("OceanRenderer.CreateOceanPipeline changed");
        _solid = Enum.TryParse(parameters[4].ParameterType, "Solid", out var solid) ? solid : throw new Skip("OceanRenderMode.Solid not found");
        _depth = RenderingPresets.ReverseZDepthStencil.DepthTestWrite;
        _noBlend = Brutal.VulkanApi.Abstractions.Presets.BlendState.BlendNone;
        harmony.Patch(Find.Method(typeof(OceanRenderer), "Build"),
            postfix: new HarmonyMethod(typeof(OceanBlending), nameof(BuildPostfix)));
        harmony.Patch(Find.Method(typeof(OceanRenderer), "DestroyFrameResources"),
            postfix: new HarmonyMethod(typeof(OceanBlending), nameof(DestroyPostfix)));
        harmony.Patch(Find.Method(typeof(OceanRenderer), "PerformRendering"),
            prefix: new HarmonyMethod(typeof(OceanBlending), nameof(RenderPrefix)),
            transpiler: new HarmonyMethod(typeof(OceanBlending), nameof(Transpiler)));
        return "on";
    }

    // The game made its pipelines: make the twins, with the game's own CreateOceanPipeline and no blending
    private static void BuildPostfix(OceanRenderer __instance)
    {
        if (_failed)
            return;
        try
        {
            var layout = _layout!.GetValue(__instance);
            foreach (var (pipeline, shaders, tessellation) in new[] { (_pipeline!, _shaders!, false),
                         (_tessellationPipeline!, _tessellationShaders!, true) })
            {
                var twin = (VkPipeline)_create!.Invoke(__instance,
                    [_depth, _noBlend, shaders.GetValue(__instance), layout, _solid, tessellation])!;
                Twins[((VkPipeline)pipeline.GetValue(__instance)!).VkHandle] = twin;
            }
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("ocean_blending", e);
        }
    }

    // The game destroyed its pipelines: the twins go too
    private static void DestroyPostfix(Device device)
    {
        try
        {
            foreach (var twin in Twins.Values)
                device.DestroyPipeline(twin, null);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("ocean_blending", e);
        }
        Twins.Clear();
    }

    private static void RenderPrefix(IViewport inViewport, OceanRenderData oceanRenderData, Celestial atmosphericBody)
    {
        _low = false;
        if (_failed)
            return;
        try
        {
            _low = inViewport.GetCamera().GetPositionEgo(atmosphericBody).Length() - oceanRenderData.OceanRadius < MaxAltitude;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("ocean_blending", e);
        }
    }

    // Each load of the two solid pipelines goes through Pick
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var pick = Find.Method(typeof(OceanBlending), nameof(Pick));
        foreach (var field in new[] { _pipeline!, _tessellationPipeline! })
        {
            int load = Find.Once(code, c => c.LoadsField(field), field.Name);
            code.Insert(load + 1, new CodeInstruction(OpCodes.Call, pick));
        }
        return code;
    }

    private static VkPipeline Pick(VkPipeline pipeline) =>
        _low && Twins.TryGetValue(pipeline.VkHandle, out var twin) ? twin : pipeline;
}
