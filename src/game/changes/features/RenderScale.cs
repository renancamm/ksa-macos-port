using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Lighting;
using KSA.Rendering.PostProcessing;

namespace KsaMetal.Hooks.Features;

// Render scale (port-settings.toml), and the window's three sizes on a Retina display. The app is marked
// NSHighResolutionCapable, so the swapchain has the display's pixels and the game's UI is drawn at them (ImGui scales
// itself by the framebuffer's scale), while the window, the mouse and the UI's layout are in points. The main view
// renders at render_scale times the window's size in points, through the door the game's own high-resolution
// screenshots use (Renderer.ExtentOverride), and its final composite scales it up to the swapchain: with MetalFX
// (Upscale.cs) or, with upscaling off, with the composite's own stretch, filtered. The cameras keep the window's size
// in points (so picking and the UI's world positions stay right), while what the shaders see as the screen size is
// the render size. The light clustering's tiles and the UI coverage mask follow the render size too, and UI code sees the
// window's (UiCoordinates.cs). Pixel sizes measured on the window (the sun's glow, the bloom's blur, star and planet
// discs, ground clutter's level of detail) are converted: the sun's radius and the bloom here, the rest by the shader
// edits of src/game/changes/features/window-size.edits, from the window's height in the camera data. On a display
// without Retina at a render scale of 1 (and dynamic resolution at full size), the three sizes are one and nothing
// changes.
[Hook(Phase.Default)]
internal static class RenderScale
{
    public static bool Scaled;  // the main view renders at another size than the window's in points
    public static int2 Window;  // the window's size in points: the cameras', the UI's
    public static int2 Output;  // the swapchain's size in pixels, which the main view is scaled up to
    private static VkExtent2D? _ours;  // the override this patch set (a screenshot's is left alone)

    // Whether MetalFX can scale the main view's image up to the swapchain: not when it is larger, which a
    // high-resolution screenshot makes it (the console's screenshot <scale> sets its own override and rebuilds the main
    // view's targets without the swapchain)
    public static bool Upscalable(VkExtent2D render) => render.Width <= Output.X && render.Height <= Output.Y;

    // The game's fields the patch reads and writes, found when it applies (a missing one skips it)
    private static AccessTools.FieldRef<ScreenspaceRenderer, VkSampler> _sampler = null!;
    private static AccessTools.FieldRef<Cmaa2Renderer, RenderTarget?> _sceneLdr = null!;
    private static AccessTools.FieldRef<UboCameraData[]> _cameraData = null!;
    private static AccessTools.FieldRef<ScreenshotCapture, VkExtent2D> _captureExtent = null!;
    private static AccessTools.StructFieldRef<UboCameraData, int> _outputHeight = null!;

    public static string Apply(Harmony harmony)
    {
        _sampler = AccessTools.FieldRefAccess<ScreenspaceRenderer, VkSampler>(Find.Field(typeof(ScreenspaceRenderer), "_samplerRef"));
        _sceneLdr = AccessTools.FieldRefAccess<Cmaa2Renderer, RenderTarget?>(Find.Field(typeof(Cmaa2Renderer), "_sceneLdrTarget"));
        _cameraData = AccessTools.StaticFieldRefAccess<UboCameraData[]>(Find.Field(typeof(Program), "_cameraData"));
        _captureExtent = AccessTools.FieldRefAccess<ScreenshotCapture, VkExtent2D>(Find.Field(typeof(ScreenshotCapture), "_captureExtent"));
        // The camera data's spare int: the window's height, for the shader edits (0: drawn at the window's size)
        _outputHeight = AccessTools.StructFieldRefAccess<UboCameraData, int>(Find.Field(typeof(UboCameraData), "_padding2"));
        Upscale.Configure();
        var renderSize = new HarmonyMethod(typeof(RenderScale), nameof(RenderSizeTranspiler));
        harmony.Patch(Find.Method(typeof(Renderer), nameof(Renderer.Rebuild)),
            postfix: new HarmonyMethod(typeof(RenderScale), nameof(RebuildPostfix)));
        harmony.Patch(Find.Method(typeof(Program), "RenderFinalComposite"),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(CompositePrefix)),
            finalizer: new HarmonyMethod(typeof(RenderScale), nameof(CompositeFinalizer)));
        harmony.Patch(Find.Method(typeof(ScreenshotCapture), nameof(ScreenshotCapture.OnRenderGameCapture)),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(CapturePrefix)),
            finalizer: new HarmonyMethod(typeof(RenderScale), nameof(CaptureFinalizer)));
        harmony.Patch(Find.Method(typeof(ScreenspaceRenderer), nameof(ScreenspaceRenderer.AddImage)),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(AddImagePrefix)) { priority = Priority.First });
        harmony.Patch(Find.Method(typeof(Cmaa2Renderer), "CreateResourcesIfNeeded"),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(Cmaa2Prefix)),
            finalizer: new HarmonyMethod(typeof(RenderScale), nameof(Cmaa2Finalizer)));
        harmony.Patch(Find.Method(typeof(GameViewport), "SetSize"),
            postfix: new HarmonyMethod(typeof(RenderScale), nameof(SetSizePostfix)));
        harmony.Patch(Find.Method(typeof(Program), nameof(Program.SetCameraUbo)), transpiler: renderSize);
        harmony.Patch(Find.Method(typeof(Program), nameof(Program.UpdateShaderData)), transpiler: renderSize);
        harmony.Patch(Find.Method(typeof(Program), nameof(Program.UpdateShaderData)),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(ShaderDataPrefix)),
            transpiler: new HarmonyMethod(typeof(RenderScale), nameof(SunRadiusTranspiler)),
            postfix: new HarmonyMethod(typeof(RenderScale), nameof(ShaderDataPostfix)));
        int lighting = 0;
        foreach (var method in AccessTools.GetDeclaredMethods(typeof(ClusteredLightSystem)))
        {
            if (!PatchProcessor.GetOriginalInstructions(method).Any(IsFramebufferSize))
                continue;
            harmony.Patch(method, transpiler: renderSize);
            lighting++;
        }
        if (lighting == 0)
            throw new Skip("ClusteredLightSystem no longer reads the camera's framebuffer size");
        harmony.Patch(Find.Method(typeof(UiCoverageMaskSystem), "BuildSizedResources"),
            transpiler: new HarmonyMethod(typeof(RenderScale), nameof(MaskExtentTranspiler)));
        var maskScale = new HarmonyMethod(typeof(RenderScale), nameof(MaskScaleTranspiler));
        harmony.Patch(Find.Method(typeof(UiCoverageMaskSystem), nameof(UiCoverageMaskSystem.RecordMaskGeneration)),
            transpiler: maskScale);
        harmony.Patch(Find.Method(typeof(UiCoverageMaskSystem), "ReplayDrawData"), transpiler: maskScale);
        ApplyBloom(harmony);
        // The frame's level and Steady Frame Rate's rate steps, which work without MetalFX too (the full size their
        // lowest level); the game's sizes at a level only with MetalFX, which alone draws below the render size
        if (Upscale.Wanted || Settings.SteadyFrameRate != "off")
            DynamicResolution.Apply(harmony, levels: Upscale.Wanted);
        // Last: it sets the game's sample count, which a Skip wouldn't undo
        if (Upscale.Wanted)
            Upscale.Apply(harmony);
        string scale = Settings.RenderScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string upscaler = !Upscale.Wanted ? "filtered stretch"
            : Upscale.Spatial ? "MetalFX spatial, no CMAA2"
            : Settings.SkipGameAntiAliasing ? "MetalFX temporal, the game's anti-aliasing off" : "MetalFX temporal, no CMAA2";
        return $"{scale} of the window, {upscaler}";
    }

    // After every swapchain rebuild (the renderer's first one included): the main view's size, unless a screenshot set
    // its own
    private static void RebuildPostfix(Renderer __instance)
    {
        if (__instance.ExtentOverride is { } current && !(_ours is { } ours && current.Width == ours.Width && current.Height == ours.Height))
            return;
        VkExtent2D output = __instance.SwapchainExtent;
        Output = new int2(output.Width, output.Height);
        int2 points = Program.GetWindow() is { } window ? window.Size : int2.Zero;
        Window = points.X > 0 && points.Y > 0 ? points : Output;
        var render = new int2(Math.Clamp((int)MathF.Round(Window.X * (float)Settings.RenderScale), 1, Output.X),
            Math.Clamp((int)MathF.Round(Window.Y * (float)Settings.RenderScale), 1, Output.Y));
        Scaled = render != Window;
        _ours = new VkExtent2D(render.X, render.Y);
        __instance.ExtentOverride = _ours;
        // Through MetalFX, which takes the size drawn each frame (without it, the display's size alone, for Steady Frame
        // Rate's steps). With Spatial, which keeps no history to fill in, the levels go no lower than half the window,
        // the Performance card's size before dynamic resolution
        DynamicResolution.Register(Upscale.Wanted && Upscalable(_ours.Value) ? render : int2.Zero, Output,
            Upscale.Spatial ? new int2((Window.X + 1) / 2, (Window.Y + 1) / 2) : int2.Zero);
    }

    // The main view's composite stretches the render size to the swapchain
    private static void CompositePrefix(int shaderSlot, out int2? __state)
    {
        __state = Program.CompositeViewportSizeOverride;
        if (__state == null && shaderSlot == Program.MainViewport.ShaderSlot && Output != int2.Zero)
            Program.CompositeViewportSizeOverride = Output;
    }

    private static Exception? CompositeFinalizer(Exception? __exception, int2? __state)
    {
        Program.CompositeViewportSizeOverride = __state;
        return __exception;
    }

    // A screenshot's composite (the screenshot key's and the console's screenshot) fills the capture's image, which is
    // the swapchain's size or a multiple of it: without this it took the main view's size, and a screenshot on a
    // Retina display had the 3D view in its top left quarter
    private static void CapturePrefix(ScreenshotCapture __instance, out (int2? Before, int2 Ours) __state)
    {
        VkExtent2D capture = _captureExtent(__instance);
        __state = (Program.CompositeViewportSizeOverride, new int2(capture.Width, capture.Height));
        Program.CompositeViewportSizeOverride = __state.Ours;
    }

    // Given back unless the game set its own: a high-resolution screenshot's for the main view's composite after it
    private static Exception? CaptureFinalizer(Exception? __exception, (int2? Before, int2 Ours) __state)
    {
        if (Program.CompositeViewportSizeOverride == __state.Ours)
            Program.CompositeViewportSizeOverride = __state.Before;
        return __exception;
    }

    [ThreadStatic] private static Cmaa2Renderer? _cmaa2;  // the CMAA2 renderer building its passes

    private static void Cmaa2Prefix(Cmaa2Renderer __instance) => _cmaa2 = __instance;

    private static void Cmaa2Finalizer() => _cmaa2 = null;

    // What stretches the main view's image to the swapchain reads it filtered (the game samples it nearest, for the
    // one-to-one copy it makes): the final composite without MetalFX, and with CMAA2 its final blit, which reads the
    // anti-aliased image instead (and no MetalFX image). Before Upscale's prefix, which swaps the composite's image for
    // MetalFX's.
    private static void AddImagePrefix(ScreenspaceRenderer __instance, VkImageView inImageView)
    {
        if (Program.OffscreenTarget is not { ColorImage: { } color } main)
            return;
        bool composite = inImageView.Equals(color.ImageView);
        bool cmaa2 = !composite && _cmaa2 != null && _sceneLdr(_cmaa2) is { ColorImage: { } ldrColor } ldr &&
            inImageView.Equals(ldrColor.ImageView) && ldr.Extent.Width == main.Extent.Width &&
            ldr.Extent.Height == main.Extent.Height;
        if (!composite && !cmaa2)
            return;
        VkExtent2D render = main.Extent;
        bool stretched = (cmaa2 || !Upscale.Wanted || !Upscalable(render)) && (render.Width != Output.X || render.Height != Output.Y);
        _sampler(__instance) = stretched ? Program.LinearClampedSampler : Program.PointClampedSampler;
    }

    // The main view's cameras keep the window's size
    private static void SetSizePostfix(GameViewport __instance)
    {
        if (__instance.Type != ViewportType.Main || Window == int2.Zero)
            return;
        __instance.BaseCamera.Resize(Window);
        __instance.MapCamera.Resize(Window);
    }

    // Camera.FramebufferSize, a field: read as a value (ldfld) or by address (ldflda, for .X and .Y)
    private static bool IsFramebufferSize(CodeInstruction c) =>
        (c.opcode == OpCodes.Ldfld || c.opcode == OpCodes.Ldflda) && c.operand is FieldInfo f &&
        f.Name == nameof(Camera.FramebufferSize) && f.DeclaringType == typeof(Camera);

    // camera.FramebufferSize -> RenderSize(camera)
    private static IEnumerable<CodeInstruction> RenderSizeTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var value = Find.Method(typeof(RenderScale), nameof(RenderSize));
        var address = Find.Method(typeof(RenderScale), nameof(RenderSizeRef));
        int replaced = 0;
        foreach (var c in instructions)
        {
            if (IsFramebufferSize(c))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, c.opcode == OpCodes.Ldfld ? value : address)
                    .MoveLabelsFrom(c).MoveBlocksFrom(c);
            }
            else
            {
                yield return c;
            }
        }
        if (replaced == 0)
            throw new Skip($"{original.DeclaringType?.Name}.{original.Name} no longer reads the camera's framebuffer size");
    }

    // The main view's render size for its current camera, the camera's own size otherwise
    public static int2 RenderSize(Camera camera)
    {
        IGameViewport? main = Program.MainViewport;
        return main != null && main.GetCamera() == camera ? DynamicResolution.AtLevel(main.Size) : camera.FramebufferSize;
    }

    // The same, where the game reads a member of it (the game renders on one thread)
    private static int2 _renderSize;

    public static ref int2 RenderSizeRef(Camera camera)
    {
        _renderSize = RenderSize(camera);
        return ref _renderSize;
    }

    // The UI coverage mask: where opaque UI covers the main view, the game skips its pixels (a depth stamp, clouds,
    // sun bloom), reading the mask at the main view's pixels. The game makes it at the window's size; here it is made
    // at the render size, and the UI is drawn into it scaled to that size. Without this, the main view goes black in
    // patches beside the UI.
    private static int2 _mask;

    private static IEnumerable<CodeInstruction> MaskExtentTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var swapchain = Find.Getter(typeof(Renderer), nameof(Renderer.SwapchainExtent));
        int at = Find.Once(code, c => c.Calls(swapchain), "the mask's size");
        code[at] = new CodeInstruction(OpCodes.Call, Find.Method(typeof(RenderScale), nameof(MaskExtent)))
            .MoveLabelsFrom(code[at]).MoveBlocksFrom(code[at]);
        return code;
    }

    // The main view's size once it has one (the mask is rebuilt with the main view's targets), the window's before
    public static VkExtent2D MaskExtent(Renderer renderer)
    {
        VkExtent2D size = Program.MainViewport is { Size.X: > 0 } main
            ? new VkExtent2D(main.Size.X, main.Size.Y)
            : renderer.SwapchainExtent;
        _mask = new int2(size.Width, size.Height);
        return size;
    }

    // drawData.FramebufferScale -> MaskScale(ref drawData), where the mask is made
    private static IEnumerable<CodeInstruction> MaskScaleTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var scale = Find.Getter(typeof(ImDrawDataPtr), nameof(ImDrawDataPtr.FramebufferScale));
        var ours = Find.Method(typeof(RenderScale), nameof(MaskScale));
        int replaced = 0;
        foreach (var c in instructions)
        {
            if (c.Calls(scale))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c);
            }
            else
            {
                yield return c;
            }
        }
        if (replaced == 0)
            throw new Skip($"UiCoverageMaskSystem.{original.Name} no longer reads the UI's framebuffer scale");
    }

    private static float2 _maskScale;

    // From the UI's points to the mask's pixels. A quarter of a pixel over, so that the game's (int)(size x scale)
    // gives the mask's size, which it checks before it uses the mask
    public static ref float2 MaskScale(ref ImDrawDataPtr drawData)
    {
        float2 display = drawData.DisplaySize;
        _maskScale = display.X > 0 && display.Y > 0 && _mask.X > 0
            ? new float2((_mask.X + 0.25f) / display.X, (_mask.Y + 0.25f) / display.Y)
            : drawData.FramebufferScale;
        return ref _maskScale;
    }

    [ThreadStatic] private static bool _mainView;

    private static void ShaderDataPrefix(IViewport viewport) => _mainView = viewport.Type == ViewportType.Main;

    // The sun's radius, measured on the camera (the window), is stored in render pixels (the glow's follows from it)
    private static IEnumerable<CodeInstruction> SunRadiusTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var field = Find.Field(typeof(SunFlareData), nameof(SunFlareData.ScreenspaceSunRadius));
        int at = Find.Once(code, c => c.StoresField(field), "the sun's radius");
        code.Insert(at, new CodeInstruction(OpCodes.Call, Find.Method(typeof(RenderScale), nameof(SunRadius))));
        return code;
    }

    public static float SunRadius(float pixels)
    {
        if (!_mainView)
            return pixels;
        // In the pixels drawn: the render scale's, and dynamic resolution's level of them
        float render = Scaled ? pixels * (float)Settings.RenderScale : pixels;
        return render * DynamicResolution.Share;
    }

    // The bloom (the game's Global Bloom and Threshold Bloom, KawaseBloomRenderer, main view only) blurs through a chain
    // of images each half the size of the one before, from the render size down to about 100 pixels high, and spreads
    // its last samples by 20 render pixels: on the screen, its glow grew as the render scale went down (a third wider at
    // 75 %, twice as wide at 50 %, around a rocket's flames at night). The spread is measured on the window instead and,
    // below 100 %, the chain sized from it, as the game draws it without a render scale. The first size stays the render
    // size: it is the image the last step adds the glow to, and the game's shader tells that step from the others by its
    // height
    private static AccessTools.FieldRef<KawaseBloomRenderer, int2[]> _bloomSizes = null!;

    private static void ApplyBloom(Harmony harmony)
    {
        try
        {
            _bloomSizes = AccessTools.FieldRefAccess<KawaseBloomRenderer, int2[]>(Find.Field(typeof(KawaseBloomRenderer), "_renderingTargetSizes"));
            var build = Find.Method(typeof(KawaseBloomRenderer), nameof(KawaseBloomRenderer.Build));
            var update = Find.Method(typeof(KawaseBloomRenderer), nameof(KawaseBloomRenderer.UpdateShaderPass));
            // Both or neither: a chain from the window with the spread from the render size would be neither look
            var extent = Find.Getter(typeof(Renderer), nameof(Renderer.Extent));
            foreach (var method in new[] { build, update })
                if (!PatchProcessor.GetOriginalInstructions(method).Any(c => c.Calls(extent)))
                    throw new Skip($"KawaseBloomRenderer.{method.Name} no longer reads the renderer's size");
            harmony.Patch(build, transpiler: new HarmonyMethod(typeof(RenderScale), nameof(BloomChainTranspiler)),
                postfix: new HarmonyMethod(typeof(RenderScale), nameof(BloomBuildPostfix)));
            try
            {
                harmony.Patch(update, transpiler: new HarmonyMethod(typeof(RenderScale), nameof(BloomSpreadTranspiler)),
                    prefix: new HarmonyMethod(typeof(RenderScale), nameof(BloomUpdatePrefix)),
                    finalizer: new HarmonyMethod(typeof(RenderScale), nameof(BloomUpdateFinalizer)));
            }
            catch
            {
                harmony.Unpatch(build, HarmonyPatchType.All, harmony.Id);
                throw;
            }
        }
        catch (Exception e)
        {
            // The render scale runs on without it, the glow wider below 100 % and narrower above
            Hooks.Log($"Render scale's bloom: off ({Skip.In(e)?.Message ?? e.Message})");
        }
    }

    // Build: renderer.Extent -> BloomChain(renderer); UpdateShaderPass: renderer.Extent -> BloomSpread(renderer)
    private static IEnumerable<CodeInstruction> BloomChainTranspiler(IEnumerable<CodeInstruction> instructions) =>
        ReplaceExtent(instructions, nameof(BloomChain));

    private static IEnumerable<CodeInstruction> BloomSpreadTranspiler(IEnumerable<CodeInstruction> instructions) =>
        ReplaceExtent(instructions, nameof(BloomSpread));

    private static IEnumerable<CodeInstruction> ReplaceExtent(IEnumerable<CodeInstruction> instructions, string with)
    {
        var extent = Find.Getter(typeof(Renderer), nameof(Renderer.Extent));
        var ours = Find.Method(typeof(RenderScale), with);
        foreach (var c in instructions)
            yield return c.Calls(extent) ? new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c) : c;
    }

    // The size the chain starts from: below 100 %, the window's while the main view renders at this patch's render
    // size, its first image (half of it) lower than the render size, which at 50 % takes a pixel off (the shader would
    // take an image as high as the render size for the last step; in a window under about 200 points, where the chain
    // doesn't halve at all, it takes every image so, as the game does there without a render scale). Above 100 %, the
    // render size, as the game has it:
    // from the window, the first step would shrink the image 3 or 4 times over in one go, its filter made for twice,
    // and small highlights would drop in and out of the glow. The renderer's otherwise (a high-resolution screenshot's
    // override, or before the first rebuild)
    public static VkExtent2D BloomChain(Renderer renderer)
    {
        VkExtent2D render = renderer.Extent;
        if (!IsOurs(renderer) || Window.X <= 0 || Window.Y <= 0 || render.Height >= Window.Y)
            return render;
        int height = Math.Max(Math.Min(Window.Y, 2 * (int)render.Height - 1), 1);
        int width = Math.Max((int)MathF.Round(Window.X * (float)height / Window.Y), 1);
        return new VkExtent2D(width, height);
    }

    // The size the blur's spread is measured on: the window's at any render scale of this patch
    public static VkExtent2D BloomSpread(Renderer renderer) =>
        IsOurs(renderer) && Window.X > 0 && Window.Y > 0 ? new VkExtent2D(Window.X, Window.Y) : renderer.Extent;

    // Whether the renderer's size is this patch's (not a high-resolution screenshot's), or it has none
    public static bool OwnsExtent(Renderer renderer) =>
        renderer.ExtentOverride is not { } current || _ours is { } ours && current.Width == ours.Width && current.Height == ours.Height;

    private static bool IsOurs(Renderer renderer) =>
        Scaled && renderer.ExtentOverride is { } current && _ours is { } ours && current.Width == ours.Width && current.Height == ours.Height;

    // Dynamic resolution: the shader tells its last step, which adds the glow to the main view's image, by that image's
    // height, which the game passes as the chain's first size: the level's while the frame's data is written (otherwise
    // the last step took the main image for another of the chain and wrote the glow alone over the scene)
    private static void BloomUpdatePrefix(KawaseBloomRenderer __instance, out int2 __state)
    {
        __state = int2.Zero;
        if (!DynamicResolution.Active || _bloomSizes(__instance) is not { Length: > 0 } sizes)
            return;
        __state = sizes[0];
        sizes[0] = DynamicResolution.AtLevel(sizes[0]);
    }

    private static Exception? BloomUpdateFinalizer(Exception? __exception, KawaseBloomRenderer __instance, int2 __state)
    {
        if (__state != int2.Zero && _bloomSizes(__instance) is { Length: > 0 } sizes)
            sizes[0] = __state;
        return __exception;
    }

    // The chain's first size back to the main view's: the last step's dispatch and the shader's test for it
    private static void BloomBuildPostfix(KawaseBloomRenderer __instance, Renderer ____renderer)
    {
        if (!IsOurs(____renderer) || _bloomSizes(__instance) is not { Length: > 0 } sizes)
            return;
        VkExtent2D render = ____renderer.Extent;
        Hooks.Log($"Render scale's bloom: blurred from {sizes[0].X}x{sizes[0].Y} down to {sizes[^1].X}x{sizes[^1].Y}, added to {render.Width}x{render.Height}");
        sizes[0] = new int2(render.Width, render.Height);
    }

    // After the camera data of a view is set: the main view's pixel sizes, and MetalFX's jitter and reprojection
    private static void ShaderDataPostfix(IViewport viewport)
    {
        if (viewport.Type != ViewportType.Main)
            return;
        int slot = viewport.ShaderSlot;
        // Also at a render scale of 1 drawn at a dynamic resolution's lower level
        if ((Scaled || DynamicResolution.Active) && Window != int2.Zero)
            _outputHeight(ref _cameraData()[slot]) = Window.Y;
        if (Upscale.Wanted)
            Upscale.UpdateCamera(viewport, ref _cameraData()[slot]);
    }
}
