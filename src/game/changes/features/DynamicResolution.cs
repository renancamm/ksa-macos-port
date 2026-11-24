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
// the passes that draw into them, the copies and the shaders' image sizes follow
// (src/game/driver/resources/dynamicresolution.mm; the level's choice: presentation/framerate.mm). What the game works
// out from the main view's size itself takes the level's size from here: the cameras' screen size and MetalFX's input
// and jitter (RenderScale, Upscale), and below the sizes that the clouds, the ambient occlusion, the exhausts, the
// atmosphere and the screen-space particles keep from their last rebuild and pass to their shaders, the cockpit's ray
// tracing's image sizes and the bloom's last step: all of them, or no level is drawn. The driver picks each frame's
// level when the frame starts (Program.Render, before the camera data and any of its commands), with Dynamic Resolution
// by Steady Frame Rate's cadence (or the experiment dynamic_level); without them nothing registers and nothing changes.
// Steady Frame Rate's rate steps at the same call, with or without MetalFX.
internal static class DynamicResolution
{
    // The driver's calls (src/game/driver/exports.h): the Frame Rate section's controller, and dynamic resolution's
    // sizes
    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlFrameRateView(ulong swapchain, uint width, uint height, uint outputWidth,
        uint outputHeight, uint leastWidth, uint leastHeight);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlFrameRateSize(out uint width, out uint height, out uint level);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlFrameRateBeginFrame(out uint width, out uint height, out uint level);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlDynamicImageSize(ulong image, out uint width, out uint height);

    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlDynamicSizeAt(ref uint width, ref uint height);

    private static int2 _render;  // the main view's render size, as its targets are made
    private static int2 _level;   // its size at the level drawn (the render size at full)
    private static bool _driver;  // the driver has these calls (registered once)
    private static bool _applied; // the frame's call and the rebuild's are patched (Apply)
    private static bool _levels;  // every one of the game's sizes follows a level (Apply's parts): else none is drawn
    private static bool _missing; // the driver has no such calls (an older one): nothing to register

    public static bool Active => _render != int2.Zero && _level != _render;

    // After each rebuild of the main view's targets (before they are made): the main window's swapchain (default: none),
    // the main view's render size (zero: none), the size MetalFX scales it up to, and the least size a level may have
    // (zero: the driver's rule, from the display). Without all of the game's sizes at a level (_levels), the render size
    // is none: the driver draws no level, and Steady Frame Rate's rate still steps
    public static void Register(VkSwapchainKHR swapchain, int2 render, int2 output, int2 least)
    {
        if (!_applied || _missing)
            return;
        if (!_levels)
            render = int2.Zero;
        try
        {
            ksaMtlFrameRateView(Unsafe.As<VkSwapchainKHR, ulong>(ref swapchain), (uint)Math.Max(render.X, 0),
                (uint)Math.Max(render.Y, 0), (uint)Math.Max(output.X, 0), (uint)Math.Max(output.Y, 0),
                (uint)Math.Max(least.X, 0), (uint)Math.Max(least.Y, 0));
            ksaMtlFrameRateSize(out uint w, out uint h, out _);
            _driver = true;
            // The driver registers levels only with Dynamic Resolution or the experiment's level: otherwise nothing changes here either
            _render = w > 0 && h > 0 ? render : int2.Zero;
            Take(w, h);
            if (Active)
                Hooks.Log($"Dynamic resolution: {render.X}x{render.Y} drawn at {_level.X}x{_level.Y}");
        }
        catch (EntryPointNotFoundException e)
        {
            _render = _level = int2.Zero;
            _driver = false;
            _missing = true;
            Hooks.Log($"Dynamic resolution: off, the driver has no {e.Message}");
        }
    }

    private static void Take(uint w, uint h) => _level = w > 0 && h > 0 ? new int2((int)w, (int)h) : _render;

    // Also without levels: the driver then has no swapchain to follow, and the screenshot's frame no rate to step
    private static void RebuildPrefix()
    {
        if (_driver && !RenderScale.OwnsExtent(Program.GetRenderer()))
            Register(default, int2.Zero, int2.Zero, int2.Zero);
    }

    // Program.Render's prefix: this frame's level, and Steady Frame Rate's rate, which the driver also steps without
    // levels (no room below the render size)
    public static void FramePrefix()
    {
        if (!_driver)
            return;
        ksaMtlFrameRateBeginFrame(out uint w, out uint h, out _);
        Take(w, h);
    }

    // The share of the render size drawn, by height (1 at full)
    public static float Share => Active ? (float)_level.Y / _render.Y : 1f;

    // A size of the main view at its render size, at the level drawn; any other size as it is
    public static int2 AtLevel(int2 size) => Active && size == _render ? _level : size;

    // Any size of an image the driver draws at the level (the main view's or a fraction of it), as the driver sizes its
    // texture there: a fraction of the main view's size at the level, rounded as the game rounds its own
    public static int2 Scale(int2 size)
    {
        if (!Active)
            return size;
        uint w = (uint)Math.Max(size.X, 0), h = (uint)Math.Max(size.Y, 0);
        ksaMtlDynamicSizeAt(ref w, ref h);
        return new int2((int)w, (int)h);
    }

    // By RenderScale.Apply, with MetalFX or Steady Frame Rate: the frame's level and rate step (without it, none of the
    // rest), and with levels (MetalFX with Dynamic Resolution or dynamic_level) the game's sizes, each part on its own.
    // One the game no longer has leaves its system drawing at the full size into a level's textures (the bloom's: black
    // frames with a glow; the clouds': the scene magnified): then no level is drawn at all
    public static void Apply(Harmony harmony, bool levels)
    {
        if (!Hooks.Part("Dynamic resolution", () => ApplyFrame(harmony)))
            return;
        _applied = true;
        if (!levels)
            return;
        // Every part tried, so that the log names each one missing
        bool Part(string name, Action<Harmony> apply) => Hooks.Part($"Dynamic resolution's {name}", () => apply(harmony));
        bool all = Part("clouds", ApplyClouds);
        all &= Part("ambient occlusion", ApplyAmbientOcclusion);
        all &= Part("exhausts", ApplyExhausts);
        all &= Part("atmosphere", ApplyAtmosphere);
        all &= Part("particles", ApplyParticles);
        all &= Part("ray tracing", ApplyRayTracing);
        all &= Part("bloom", ApplyBloom);
        _levels = all;
        if (!all)
            Hooks.Log("Dynamic resolution: no levels, as one of the game's sizes wouldn't follow them");
    }

    // The frame's level and rate step: a prefix on the frame's start, and on a high-resolution screenshot's rebuild
    private static void ApplyFrame(Harmony harmony)
    {
        var render = Find.Method(typeof(Program), "Render", typeof(double), typeof(FrameResult), typeof(AcquiredFrame));
        var frame = new HarmonyMethod(typeof(DynamicResolution), nameof(FramePrefix));
        // A high-resolution screenshot rebuilds the main view's targets at its own size without the swapchain
        // (RenderScale's registration doesn't see it): drawn at the full size, its targets not dynamic
        var rebuild = Find.Method(typeof(Program), nameof(Program.RebuildRenderer));
        harmony.Patch(render, prefix: frame);
        try
        {
            harmony.Patch(rebuild, prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(RebuildPrefix)));
        }
        catch
        {
            harmony.Unpatch(render, frame.method);
            throw;
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
        _setDimensions = Find.Method(typeof(CloudRenderer), "SetRenderTargetDimensions", resources, typeof(int2));
        _isMain = Find.Field(resources, "IsMain", typeof(bool));
        _screenWidth = Find.Field(resources, "ScreenWidth", typeof(int));
        _screenHeight = Find.Field(resources, "ScreenHeight", typeof(int));
        _paddedWidth = Find.Field(resources, "PaddedUpscalingScreenWidth", typeof(int));
        _paddedHeight = Find.Field(resources, "PaddedUpscalingScreenHeight", typeof(int));
        _upscalingData = Find.Field(resources, "CloudUpscalingData", typeof(CloudUpscalingData));
        var update = Find.Method(typeof(CloudRenderer), "UpdateUpscalingData");
        var trails = Find.Method(typeof(CloudRenderer), "RenderVolumetricTrailsWithUpscaling");
        var prefix = new HarmonyMethod(typeof(DynamicResolution), nameof(CloudsPrefix));
        harmony.Patch(update, prefix: prefix);
        harmony.Patch(trails, prefix: prefix);
    }

    private static void CloudsPrefix(CloudRenderer __instance, object inResources)
    {
        if (_render == int2.Zero || !(bool)_isMain.GetValue(inResources)!)
            return;
        int2 want = _level;
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
        var atmosphere = Find.Method(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderWithCompute));
        var aurora = Find.Method(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderAuroraAfterClouds));
        var transpiler = new HarmonyMethod(typeof(DynamicResolution), nameof(ViewportSizeTranspiler));
        harmony.Patch(atmosphere, transpiler: transpiler);
        try
        {
            harmony.Patch(aurora, transpiler: transpiler);
        }
        catch
        {
            harmony.Unpatch(atmosphere, transpiler.method);
            throw;
        }
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
    private const int ParticleTile = 8;  // the game's tile, in grid pixels (CreateResources: (dim + 7) / 8)
    private static bool _tileChanged;

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
        if (__state.Grid != Tiles(__state.Dim) && !_tileChanged)
        {
            _tileChanged = true;
            Hooks.Log($"Dynamic resolution's particles: the game's grid {__state.Grid} isn't {ParticleTile}-pixel tiles of {__state.Dim}");
        }
        int2 dim = Scale(__state.Dim);
        _lowResDim(__instance) = dim;
        _tileGrid(__instance) = Tiles(dim);
    }

    private static int2 Tiles(int2 dim) =>
        new((dim.X + ParticleTile - 1) / ParticleTile, (dim.Y + ParticleTile - 1) / ParticleTile);

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
        var width = Find.Getter(typeof(FullScreenImage), nameof(FullScreenImage.Width));
        var height = Find.Getter(typeof(FullScreenImage), nameof(FullScreenImage.Height));
        harmony.Patch(width, postfix: new HarmonyMethod(typeof(DynamicResolution), nameof(RayImageWidthPostfix)));
        harmony.Patch(height, postfix: new HarmonyMethod(typeof(DynamicResolution), nameof(RayImageHeightPostfix)));
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
        ksaMtlDynamicImageSize(Unsafe.As<VkImage, ulong>(ref image), out uint w, out uint h);
        return new int2((int)w, (int)h);
    }

    // The bloom (KawaseBloomRenderer): its shader tells its last step, which adds the glow to the main view's image, by
    // that image's height, which the game passes as the chain's first size (RenderScale sizes the chain): the level's
    // while the frame's data is written (otherwise the last step took the main image for another of the chain and wrote
    // the glow alone over the scene). Each frame writes its own slot of the data (the frame in flight's), but the
    // overall bloom's SetBlurSettings writes slot 0 too, every frame: a frame still on the GPU with slot 0 then read the
    // next frame's height, and a step up to a larger level drew it black with a glow. With levels, a slot other than the
    // frame's is left alone; the frame that uses it writes it
    private static AccessTools.FieldRef<KawaseBloomRenderer, int2[]> _bloomSizes = null!;

    private static void ApplyBloom(Harmony harmony)
    {
        _bloomSizes = AccessTools.FieldRefAccess<KawaseBloomRenderer, int2[]>(Find.Field(typeof(KawaseBloomRenderer), "_renderingTargetSizes"));
        harmony.Patch(Find.Method(typeof(KawaseBloomRenderer), nameof(KawaseBloomRenderer.UpdateShaderPass)),
            prefix: new HarmonyMethod(typeof(DynamicResolution), nameof(BloomUpdatePrefix)),
            finalizer: new HarmonyMethod(typeof(DynamicResolution), nameof(BloomUpdateFinalizer)));
    }

    private static bool BloomUpdatePrefix(KawaseBloomRenderer __instance, int frameIndex, out int2 __state)
    {
        __state = int2.Zero;
        if (_render != int2.Zero && frameIndex != Program.Instance.ResourceFrameIndex)
            return false;
        if (!Active || _bloomSizes(__instance) is not { Length: > 0 } sizes)
            return true;
        __state = sizes[0];
        sizes[0] = AtLevel(sizes[0]);
        return true;
    }

    private static Exception? BloomUpdateFinalizer(Exception? __exception, KawaseBloomRenderer __instance, int2 __state)
    {
        if (__state != int2.Zero && _bloomSizes(__instance) is { Length: > 0 } sizes)
            sizes[0] = __state;
        return __exception;
    }
}
