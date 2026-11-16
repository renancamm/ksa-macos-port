using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Brutal.Numerics;
using Brutal.VulkanApi;
using HarmonyLib;
using RenderCore;
using KSA;
using KSA.Atmosphere.Rendering;
using KSA.Atmosphere.Rendering.ShaderStructs;
using KSA.Rendering;
using KSA.Rendering.Particles;
using Particles = KSA.Rendering.Particles.ScreenspaceParticleRenderer<KSA.Rendering.Particles.ParticleUpdateData, KSA.Rendering.Particles.ParticleRenderData>;

namespace KsaMetal.Hooks.Features;

// Dynamic resolution: the main view drawn at a level of its render size, frame by frame, without rebuilding its
// targets. KSA Metal gives each image of that size (or a fraction of it) a texture per level in the same memory, and
// the passes that draw into them, the copies and the shaders' image sizes follow (src/game/driver/resources/dynres.mm).
// What the game works out from the main view's size itself takes the level's size from here: the cameras' screen size
// and MetalFX's input and jitter (RenderScale, Upscale), the bloom's last step (RenderScale), and the sizes that the
// clouds, the ambient occlusion, the exhausts, the atmosphere and the screen-space particles keep from their last
// rebuild and pass to their shaders, and the cockpit's ray tracing's image sizes (below). The driver picks each
// frame's level when the frame starts (Program.Render, before the camera data and any of its commands), with Dynamic
// Resolution by Steady Frame Rate's cadence (or the experiments dynamic_level, dynamic_target_ms); without them nothing
// registers and nothing changes. Steady Frame Rate's rate steps at the same call, with or without MetalFX.
internal static class DynamicResolution
{
    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlDynamicResolution(uint width, uint height, uint outputWidth, uint outputHeight,
        uint leastWidth, uint leastHeight);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlDynamicResolutionSize(out uint width, out uint height, out uint level);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlDynamicResolutionFrame(out uint width, out uint height, out uint level);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlImageSize(ulong image, out uint width, out uint height);

    private const int LevelSteps = 64;  // the driver's: a level is this many parts of the full size

    private static int2 _render;  // the main view's render size, as its targets are made
    private static int2 _level;   // its size at the level drawn (the render size at full)
    private static int _steps;    // the level, of LevelSteps (0: full)
    private static bool _driver;  // the driver has these calls (registered once)

    public static bool Active => _render != int2.Zero && _level != _render;

    // After each rebuild of the main view's targets (before they are made): its render size (zero: none), the size
    // MetalFX scales it up to, and the least size a level may have (zero: the driver's rule, from the display)
    public static void Register(int2 render, int2 output, int2 least)
    {
        try
        {
            ksaMtlDynamicResolution((uint)Math.Max(render.X, 0), (uint)Math.Max(render.Y, 0), (uint)Math.Max(output.X, 0),
                (uint)Math.Max(output.Y, 0), (uint)Math.Max(least.X, 0), (uint)Math.Max(least.Y, 0));
            ksaMtlDynamicResolutionSize(out uint w, out uint h, out uint level);
            _driver = true;
            // The driver registers levels only with Dynamic Resolution or the experiment's level: otherwise nothing changes here either
            _render = w > 0 && h > 0 ? render : int2.Zero;
            Take(w, h, level);
            if (Active)
                Hooks.Log($"Dynamic resolution: {render.X}x{render.Y} drawn at {_level.X}x{_level.Y}");
        }
        catch (EntryPointNotFoundException)
        {
            _render = _level = int2.Zero;
            _steps = 0;
            _driver = false;
        }
    }

    private static void Take(uint w, uint h, uint level)
    {
        _level = w > 0 && h > 0 ? new int2((int)w, (int)h) : _render;
        _steps = (int)level;
    }

    // Also without levels: the driver then has no display size, and the screenshot's frame no rate to step
    private static void RebuildPrefix()
    {
        if (_driver && !RenderScale.OwnsExtent(Program.GetRenderer()))
            Register(int2.Zero, int2.Zero, int2.Zero);
    }

    // Program.Render's prefix: this frame's level, and Steady Frame Rate's rate, which the driver also steps without
    // levels (no room below the render size)
    public static void FramePrefix()
    {
        if (!_driver)
            return;
        ksaMtlDynamicResolutionFrame(out uint w, out uint h, out uint level);
        Take(w, h, level);
    }

    // The share of the render size drawn, by height (1 at full)
    public static float Share => Active ? (float)_level.Y / _render.Y : 1f;

    // A size of the main view at its render size, at the level drawn; any other size as it is
    public static int2 AtLevel(int2 size) => Active && size == _render ? _level : size;

    // Any size of an image the driver draws at the level (the main view's or a fraction of it), as the driver scales it
    public static int2 Scale(int2 size) => !Active ? size : new int2(Scale(size.X), Scale(size.Y));

    private static int Scale(int size) =>
        Math.Max(1, (int)Math.Round((double)size * _steps / LevelSteps, MidpointRounding.AwayFromZero));

    // By RenderScale.Apply, with MetalFX or Steady Frame Rate: the frame's level and rate step (without it, none of the
    // rest), and with levels (MetalFX) the game's sizes, each part on its own: one the game no longer has is left out
    public static void Apply(Harmony harmony, bool levels)
    {
        try
        {
            harmony.Patch(Find.Method(typeof(Program), "Render", typeof(double), typeof(FrameResult), typeof(AcquiredFrame)),
                prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(FramePrefix)));
            // A high-resolution screenshot rebuilds the main view's targets at its own size without the swapchain
            // (RenderScale's registration doesn't see it): drawn at the full size, its targets not dynamic
            harmony.Patch(Find.Method(typeof(Program), nameof(Program.RebuildRenderer)),
                prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(RebuildPrefix)));
        }
        catch (Exception e)
        {
            Hooks.Log($"Dynamic resolution: off ({Skip.In(e)?.Message ?? e.Message})");
            return;
        }
        if (!levels)
            return;
        Part(harmony, "clouds", ApplyClouds);
        Part(harmony, "ambient occlusion", ApplyAmbientOcclusion);
        Part(harmony, "exhausts", ApplyExhausts);
        Part(harmony, "atmosphere", ApplyAtmosphere);
        Part(harmony, "particles", ApplyParticles);
        Part(harmony, "ray tracing", ApplyRayTracing);
    }

    private static void Part(Harmony harmony, string name, Action<Harmony> apply)
    {
        try
        {
            apply(harmony);
        }
        catch (Exception e)
        {
            Hooks.Log($"Dynamic resolution's {name}: off ({Skip.In(e)?.Message ?? e.Message})");
        }
    }

    // The clouds and smoke trails (CloudRenderer): the main view's screen and padded sizes, the low-resolution grid and
    // the thread groups, set when the targets are made (SetRenderTargetDimensions) and copied into the upscaling data
    // each frame. Before each copy, the game's own method sets them again for the level, and the data takes them
    private static MethodInfo _setDimensions = null!;
    private static FieldInfo _isMain = null!, _screenWidth = null!, _screenHeight = null!, _upscalingData = null!;
    private static FieldInfo _paddedWidth = null!, _paddedHeight = null!;

    private static void ApplyClouds(Harmony harmony)
    {
        Type resources = AccessTools.Inner(typeof(CloudRenderer), "ViewportResources")
            ?? throw new Skip("CloudRenderer.ViewportResources is gone");
        _setDimensions = Find.Method(typeof(CloudRenderer), "SetRenderTargetDimensions");
        _isMain = Find.Field(resources, "IsMain");
        _screenWidth = Find.Field(resources, "ScreenWidth");
        _screenHeight = Find.Field(resources, "ScreenHeight");
        _paddedWidth = Find.Field(resources, "PaddedUpscalingScreenWidth");
        _paddedHeight = Find.Field(resources, "PaddedUpscalingScreenHeight");
        _upscalingData = Find.Field(resources, "CloudUpscalingData");
        var prefix = new HarmonyMethod(typeof(DynamicResolution), nameof(CloudsPrefix));
        harmony.Patch(Find.Method(typeof(CloudRenderer), "UpdateUpscalingData"), prefix: prefix);
        harmony.Patch(Find.Method(typeof(CloudRenderer), "RenderVolumetricTrailsWithUpscaling"), prefix: prefix);
    }

    private static void CloudsPrefix(CloudRenderer __instance, object inResources)
    {
        if (_render == int2.Zero || !(bool)_isMain.GetValue(inResources)!)
            return;
        int2 want = Scale(_render);
        if ((int)_screenWidth.GetValue(inResources)! == want.X && (int)_screenHeight.GetValue(inResources)! == want.Y)
            return;
        _setDimensions.Invoke(__instance, [inResources, want]);
        var data = (CloudUpscalingData)_upscalingData.GetValue(inResources)!;
        int pw = (int)_paddedWidth.GetValue(inResources)!, ph = (int)_paddedHeight.GetValue(inResources)!;
        data.ScreenResolution = new float2(want.X, want.Y);
        data.InvScreenResolution = new float2(1f / want.X, 1f / want.Y);
        data.PaddedScreenResolution = new float2(pw, ph);
        data.InvPaddedScreenResolution = new float2(1f / pw, 1f / ph);
        _upscalingData.SetValue(inResources, data);
    }

    // The ambient occlusion (FidelityFX CACAO): every size and reciprocal in its constants, and its dispatches, from the
    // width and height of its last rebuild; the level's while it renders
    private static AccessTools.FieldRef<int> _aoWidth = null!, _aoHeight = null!;

    private static void ApplyAmbientOcclusion(Harmony harmony)
    {
        _aoWidth = AccessTools.StaticFieldRefAccess<int>(Find.Field(typeof(AmbientOcclusionRenderer), "_lastWidth"));
        _aoHeight = AccessTools.StaticFieldRefAccess<int>(Find.Field(typeof(AmbientOcclusionRenderer), "_lastHeight"));
        harmony.Patch(Find.Method(typeof(AmbientOcclusionRenderer), nameof(AmbientOcclusionRenderer.Render)),
            prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(AmbientOcclusionPrefix)),
            finalizer: new HarmonyMethod(typeof(DynamicResolution), nameof(AmbientOcclusionFinalizer)));
    }

    private static void AmbientOcclusionPrefix(out int2 __state)
    {
        __state = new int2(_aoWidth(), _aoHeight());
        if (!Active)
            return;
        int2 level = AtLevel(__state);
        _aoWidth() = level.X;
        _aoHeight() = level.Y;
    }

    private static Exception? AmbientOcclusionFinalizer(Exception? __exception, int2 __state)
    {
        _aoWidth() = __state.X;
        _aoHeight() = __state.Y;
        return __exception;
    }

    // The exhausts (VolumetricExhaustRenderer.PerformRenderingPass): each pass's size, which its shaders take pixels
    // to their place on the screen by (uv = fragment position / size)
    private static void ApplyExhausts(Harmony harmony) =>
        harmony.Patch(Find.Method(typeof(VolumetricExhaustRenderer), "PerformRenderingPass"),
            prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(ExhaustPassPrefix)));

    private static void ExhaustPassPrefix(ref int2 resolution) => resolution = Scale(resolution);

    // The atmosphere and the aurora (AtmosphereRenderer): where a planet's box starts on the screen, in pixels, and the
    // dispatch over it, from the main view's size
    private static void ApplyAtmosphere(Harmony harmony)
    {
        var transpiler = new HarmonyMethod(typeof(DynamicResolution), nameof(ViewportSizeTranspiler));
        harmony.Patch(Find.Method(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderWithCompute)), transpiler: transpiler);
        harmony.Patch(Find.Method(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderAuroraAfterClouds)), transpiler: transpiler);
    }

    // viewport.Size -> AtLevel(viewport.Size)
    private static IEnumerable<CodeInstruction> ViewportSizeTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var size = Find.Getter(typeof(IViewport), nameof(IViewport.Size));
        var atLevel = Find.Method(typeof(DynamicResolution), nameof(AtLevel));
        int replaced = 0;
        foreach (var c in instructions)
        {
            yield return c;
            if (c.Calls(size))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, atLevel);
            }
        }
        if (replaced == 0)
            throw new Skip($"AtmosphereRenderer.{original.Name} no longer reads the view's size");
    }

    // The screen-space particles (ScreenspaceParticleRenderer, the game's one kind of them): the quarter-size grid they
    // gather and bin into, and its tiles, from the last rebuild; the level's while they draw
    private static AccessTools.FieldRef<Particles, int2> _lowResDim = null!, _tileGrid = null!;

    private static void ApplyParticles(Harmony harmony)
    {
        _lowResDim = AccessTools.FieldRefAccess<Particles, int2>(Find.Field(typeof(Particles), "_lowResDim"));
        _tileGrid = AccessTools.FieldRefAccess<Particles, int2>(Find.Field(typeof(Particles), "_tileGrid"));
        harmony.Patch(Find.Method(typeof(Particles), nameof(Particles.WriteCommandsColor)),
            prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(ParticlesPrefix)),
            finalizer: new HarmonyMethod(typeof(DynamicResolution), nameof(ParticlesFinalizer)));
    }

    private static void ParticlesPrefix(Particles __instance, out (int2 Dim, int2 Grid) __state)
    {
        __state = (_lowResDim(__instance), _tileGrid(__instance));
        if (!Active)
            return;
        int2 dim = Scale(__state.Dim);
        _lowResDim(__instance) = dim;
        _tileGrid(__instance) = new int2((dim.X + 7) / 8, (dim.Y + 7) / 8);
    }

    private static Exception? ParticlesFinalizer(Exception? __exception, Particles __instance, (int2 Dim, int2 Grid) __state)
    {
        _lowResDim(__instance) = __state.Dim;
        _tileGrid(__instance) = __state.Grid;
        return __exception;
    }

    // The cockpit's ray tracing (RaytracingRenderer): its images' sizes (FullScreenImage, used by it alone), which it
    // launches its rays over and gives its shaders and its blur: the textures' as the driver has them at the level
    private static void ApplyRayTracing(Harmony harmony)
    {
        harmony.Patch(Find.Getter(typeof(FullScreenImage), nameof(FullScreenImage.Width)),
            postfix: new HarmonyMethod(typeof(DynamicResolution), nameof(RayImageWidthPostfix)));
        harmony.Patch(Find.Getter(typeof(FullScreenImage), nameof(FullScreenImage.Height)),
            postfix: new HarmonyMethod(typeof(DynamicResolution), nameof(RayImageHeightPostfix)));
    }

    private static void RayImageWidthPostfix(FullScreenImage __instance, ref int __result)
    {
        if (Active && TextureSize(__instance.Image) is { X: > 0 } size)
            __result = size.X;
    }

    private static void RayImageHeightPostfix(FullScreenImage __instance, ref int __result)
    {
        if (Active && TextureSize(__instance.Image) is { Y: > 0 } size)
            __result = size.Y;
    }

    private static int2 TextureSize(VkImage image)
    {
        ksaMtlImageSize(Unsafe.As<VkImage, ulong>(ref image), out uint w, out uint h);
        return new int2((int)w, (int)h);
    }
}
