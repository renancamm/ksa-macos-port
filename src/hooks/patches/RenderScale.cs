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

namespace KsaMetalHooks;

// Render scale (port-settings.toml), and the window's three sizes on a Retina display. The app is marked
// NSHighResolutionCapable, so the swapchain has the display's pixels and the game's UI is drawn at them (ImGui scales
// itself by the framebuffer's scale), while the window, the mouse and the UI's layout are in points. The main view
// renders at render_scale times the window's size in points, through the door the game's own high-resolution
// screenshots use (Renderer.ExtentOverride), and its final composite scales it up to the swapchain: with MetalFX
// (Upscale.cs) or, with upscaling off, with the composite's own stretch, filtered. The cameras keep the window's size
// in points (so picking and the UI's world positions stay right), while what the shaders see as the screen size is
// the render size. The light clustering's tiles and the UI coverage mask follow the render size too, and the panels
// the game places from its main view's size (the Vehicle Editor's, the transfer planner's) the window's. Pixel sizes
// measured on the window (the sun's glow, star and planet discs, ground clutter's level of detail) are converted:
// the sun's radius here, the rest by the shader edits convert-shaders.sh makes, from the window's height in the
// camera data. On a display without Retina at a render scale of 1, the three sizes are one and nothing changes.
internal static class RenderScale
{
    public static bool Scaled;  // the main view renders at another size than the window's in points
    public static int2 Window;  // the window's size in points: the cameras', the UI's
    public static int2 Output;  // the swapchain's size in pixels, which the main view is scaled up to
    private static VkExtent2D? _ours;  // the override this patch set (a screenshot's is left alone)

    // The game's fields the patch reads and writes, found when it applies (a missing one skips it)
    private static AccessTools.FieldRef<ScreenspaceRenderer, VkSampler> _sampler = null!;
    private static AccessTools.FieldRef<Cmaa2Renderer, RenderTarget?> _sceneLdr = null!;
    private static AccessTools.FieldRef<UboCameraData[]> _cameraData = null!;
    private static AccessTools.StructFieldRef<UboCameraData, int> _outputHeight = null!;

    public static string Apply(Harmony harmony)
    {
        _sampler = AccessTools.FieldRefAccess<ScreenspaceRenderer, VkSampler>(Find.Field(typeof(ScreenspaceRenderer), "_samplerRef"));
        _sceneLdr = AccessTools.FieldRefAccess<Cmaa2Renderer, RenderTarget?>(Find.Field(typeof(Cmaa2Renderer), "_sceneLdrTarget"));
        _cameraData = AccessTools.StaticFieldRefAccess<UboCameraData[]>(Find.Field(typeof(Program), "_cameraData"));
        // The camera data's spare int: the window's height, for the shader edits (0: not scaled)
        _outputHeight = AccessTools.StructFieldRefAccess<UboCameraData, int>(Find.Field(typeof(UboCameraData), "_padding2"));
        Upscale.Configure();
        var renderSize = new HarmonyMethod(typeof(RenderScale), nameof(RenderSizeTranspiler));
        harmony.Patch(Find.Method(typeof(Renderer), nameof(Renderer.Rebuild)),
            postfix: new HarmonyMethod(typeof(RenderScale), nameof(RebuildPostfix)));
        harmony.Patch(Find.Method(typeof(Program), "RenderFinalComposite"),
            prefix: new HarmonyMethod(typeof(RenderScale), nameof(CompositePrefix)),
            finalizer: new HarmonyMethod(typeof(RenderScale), nameof(CompositeFinalizer)));
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
        PatchPanels(harmony);
        // Last: it sets the game's sample count, which a Skip wouldn't undo
        if (Upscale.Wanted)
            Upscale.Apply(harmony);
        string scale = Settings.RenderScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        string upscaler = !Upscale.Wanted ? "filtered stretch"
            : Upscale.Spatial ? "MetalFX spatial, no CMAA2"
            : Settings.On("override_antialiasing") ? "MetalFX temporal, the game's anti-aliasing off" : "MetalFX temporal, no CMAA2";
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
        var render = new int2(Math.Clamp((int)MathF.Round(Window.X * Settings.RenderScale), 1, Output.X),
            Math.Clamp((int)MathF.Round(Window.Y * Settings.RenderScale), 1, Output.Y));
        Scaled = render != Window;
        _ours = new VkExtent2D(render.X, render.Y);
        __instance.ExtentOverride = _ours;
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
        bool stretched = (cmaa2 || !Upscale.Wanted) && (render.Width != Output.X || render.Height != Output.Y);
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

    // The panels the game places from its main view's size: IViewport.Size -> UiSize(viewport) in every method of
    // these classes (and their nested ones) that reads it
    private static readonly Type[] Panels = [typeof(VehicleEditor), typeof(TransferPlanner), typeof(DockingPort)];

    private static void PatchPanels(Harmony harmony)
    {
        var size = Find.Getter(typeof(IViewport), nameof(IViewport.Size));
        var transpiler = new HarmonyMethod(typeof(RenderScale), nameof(UiSizeTranspiler));
        int patched = 0;
        foreach (var type in Panels.SelectMany(Nested))
        {
            foreach (var method in AccessTools.GetDeclaredMethods(type))
            {
                if (method.IsAbstract || method.IsGenericMethodDefinition ||
                    !PatchProcessor.GetOriginalInstructions(method).Any(c => c.Calls(size)))
                    continue;
                harmony.Patch(method, transpiler: transpiler);
                patched++;
            }
        }
        if (patched == 0)
            throw new Skip("the Vehicle Editor no longer places its panels from its view's size");
    }

    private static IEnumerable<Type> Nested(Type type) => new[] { type }.Concat(type.GetNestedTypes(AccessTools.all).SelectMany(Nested));

    private static IEnumerable<CodeInstruction> UiSizeTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var size = Find.Getter(typeof(IViewport), nameof(IViewport.Size));
        var ours = Find.Method(typeof(RenderScale), nameof(UiSize));
        foreach (var c in instructions)
            yield return c.Calls(size) ? new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c) : c;
    }

    // The main view's size in the UI's units (the window's points), any other view's own
    public static int2 UiSize(IViewport viewport) =>
        Window != int2.Zero && ReferenceEquals(viewport, Program.MainViewport) ? Window : viewport.Size;

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
        return main != null && main.GetCamera() == camera ? main.Size : camera.FramebufferSize;
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

    public static float SunRadius(float pixels) => _mainView && Scaled ? pixels * Settings.RenderScale : pixels;

    // After the camera data of a view is set: the main view's pixel sizes, and MetalFX's jitter and reprojection
    private static void ShaderDataPostfix(IViewport viewport)
    {
        if (viewport.Type != ViewportType.Main)
            return;
        int slot = viewport.ShaderSlot;
        if (Scaled && Window != int2.Zero)
            _outputHeight(ref _cameraData()[slot]) = Window.Y;
        if (Upscale.Wanted)
            Upscale.UpdateCamera(viewport, ref _cameraData()[slot]);
    }
}
