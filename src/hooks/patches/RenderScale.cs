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
using KSA.Rendering.Lighting;

namespace KsaMetalHooks;

// Render scale (port-settings.toml). The main view renders at render_scale times the window's size, through the door
// the game's own high-resolution screenshots use (Renderer.ExtentOverride), and its final composite scales it back
// to the window, where the UI is drawn at full size. The cameras keep the window's size (so picking and the UI's
// world positions stay right), while what the shaders see as the screen size is the render size. The light
// clustering's tiles and the UI coverage mask follow the render size too. Pixel sizes measured on the window (the sun's glow, star and planet
// discs, ground clutter's level of detail) are converted: the sun's radius here, the rest by the shader edits
// convert-shaders.sh makes, from the window's height in the camera data. Upscale.cs scales the view back up with
// MetalFX (a render scale needs upscaling = "metalfx" or "spatial").
internal static class RenderScale
{
    public static bool Scaled;
    public static int2 Window;  // the swapchain's size, which the main view is scaled from
    private static VkExtent2D? _ours;  // the override this patch set (a screenshot's is left alone)

    public static string Apply(Harmony harmony)
    {
        Scaled = Settings.RenderScale < 1f;
        Upscale.Configure(Scaled);
        bool metalFx = Upscale.Wanted;
        if (!Scaled && !metalFx)
            return "off";
        var renderSize = new HarmonyMethod(typeof(RenderScale), nameof(RenderSizeTranspiler));
        if (Scaled)
        {
            harmony.Patch(Find.Method(typeof(Renderer), nameof(Renderer.Rebuild)),
                postfix: new HarmonyMethod(typeof(RenderScale), nameof(RebuildPostfix)));
            harmony.Patch(Find.Method(typeof(Program), "RenderFinalComposite"),
                prefix: new HarmonyMethod(typeof(RenderScale), nameof(CompositePrefix)),
                finalizer: new HarmonyMethod(typeof(RenderScale), nameof(CompositeFinalizer)));
            harmony.Patch(Find.Method(typeof(GameViewport), "SetSize"),
                postfix: new HarmonyMethod(typeof(RenderScale), nameof(SetSizePostfix)));
            harmony.Patch(Find.Method(typeof(Program), nameof(Program.SetCameraUbo)), transpiler: renderSize);
            harmony.Patch(Find.Method(typeof(Program), nameof(Program.UpdateShaderData)), transpiler: renderSize);
            harmony.Patch(Find.Method(typeof(Program), nameof(Program.UpdateShaderData)),
                prefix: new HarmonyMethod(typeof(RenderScale), nameof(ShaderDataPrefix)),
                transpiler: new HarmonyMethod(typeof(RenderScale), nameof(SunRadiusTranspiler)));
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
        }
        harmony.Patch(Find.Method(typeof(Program), nameof(Program.UpdateShaderData)),
            postfix: new HarmonyMethod(typeof(RenderScale), nameof(ShaderDataPostfix)));
        if (metalFx)
            Upscale.Apply(harmony);
        string upscaler = Upscale.Spatial ? "MetalFX spatial" : "MetalFX temporal";
        string scale = Settings.RenderScale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return Scaled ? $"{scale} of the window, {upscaler}" : $"1, {upscaler}";
    }

    // After every swapchain rebuild (the renderer's first one included): the main view's size, unless a screenshot set
    // its own
    private static void RebuildPostfix(Renderer __instance)
    {
        if (__instance.ExtentOverride is { } current && !(_ours is { } ours && current.Width == ours.Width && current.Height == ours.Height))
            return;
        VkExtent2D window = __instance.SwapchainExtent;
        Window = new int2(window.Width, window.Height);
        _ours = new VkExtent2D(Math.Max(1, (int)MathF.Round(window.Width * Settings.RenderScale)),
            Math.Max(1, (int)MathF.Round(window.Height * Settings.RenderScale)));
        __instance.ExtentOverride = _ours;
    }

    // The main view's composite stretches the render size to the window
    private static void CompositePrefix(int shaderSlot, out int2? __state)
    {
        __state = Program.CompositeViewportSizeOverride;
        if (__state == null && shaderSlot == Program.MainViewport.ShaderSlot && Window != int2.Zero)
            Program.CompositeViewportSizeOverride = Window;
    }

    private static Exception? CompositeFinalizer(Exception? __exception, int2? __state)
    {
        Program.CompositeViewportSizeOverride = __state;
        return __exception;
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

    private static readonly AccessTools.FieldRef<UboCameraData[]> CameraData =
        AccessTools.StaticFieldRefAccess<UboCameraData[]>(Find.Field(typeof(Program), "_cameraData"));

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

    // The camera data's spare int: the window's height, for the shader edits (0: not scaled)
    private static readonly AccessTools.StructFieldRef<UboCameraData, int> OutputHeight =
        AccessTools.StructFieldRefAccess<UboCameraData, int>(Find.Field(typeof(UboCameraData), "_padding2"));

    // After the camera data of a view is set: the main view's pixel sizes, and MetalFX's jitter and reprojection
    private static void ShaderDataPostfix(IViewport viewport)
    {
        if (viewport.Type != ViewportType.Main)
            return;
        int slot = viewport.ShaderSlot;
        if (Scaled && Window != int2.Zero)
            OutputHeight(ref CameraData()[slot]) = Window.Y;
        if (Upscale.Wanted)
            Upscale.UpdateCamera(viewport, ref CameraData()[slot]);
    }
}
