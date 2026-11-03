using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KsaMetalHooks.Features;

namespace KsaMetalHooks.Fixes;

// The window as UI code sees it, the game's and mods' alike. The main view's size is the render size (RenderScale.cs),
// smaller than the window below a render scale of 1, while what's drawn on it is in the window's points: a panel placed
// from it landed short of the screen's edge, and a mod's overlay sized from it clipped what it drew. While the frame's UI
// is drawn, on its thread, and in the postfixes on the frame's end (mods', through StarMap's AfterOnFrame: the frame has
// been rendered by then), the size is the window's. Everything else, the render passes first, keeps the render size.
//
// One transpiler on Program.OnFrame opens and closes that span: patching the UI methods themselves would compile them
// fully optimised, and a small method they inline would then miss the patch a mod makes on it later.
[Hook(Phase.AfterRenderPatches)]
internal static class UiViewSize
{
    [ThreadStatic]
    private static bool _inUi;

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(Program), "OnFrame"),
            transpiler: new HarmonyMethod(typeof(UiViewSize), nameof(OnFrameTranspiler)),
            postfix: new HarmonyMethod(typeof(UiViewSize), nameof(Enter)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(UiViewSize), nameof(Finalizer)));
        harmony.Patch(Find.Getter(typeof(GameViewport), nameof(GameViewport.Size)),
            prefix: new HarmonyMethod(typeof(UiViewSize), nameof(SizePrefix)));
        return "on";
    }

    // From after OnFrameViewports, which rebuilds views at their render size, to after OnDrawUiConsole, the UI's last
    private static IEnumerable<CodeInstruction> OnFrameTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var viewports = Find.Method(typeof(Program), "OnFrameViewports");
        var console = Find.Method(typeof(Program), "OnDrawUiConsole");
        int end = Find.Once(code, c => c.Calls(console), "the UI's last call");
        code.Insert(end + 1, new CodeInstruction(OpCodes.Call, Find.Method(typeof(UiViewSize), nameof(Leave))));
        int start = Find.Once(code, c => c.Calls(viewports), "the views' update");
        code.Insert(start + 1, new CodeInstruction(OpCodes.Call, Find.Method(typeof(UiViewSize), nameof(Enter))));
        return code;
    }

    public static void Enter() => _inUi = true;

    public static void Leave() => _inUi = false;

    private static Exception? Finalizer(Exception? __exception)
    {
        _inUi = false;
        return __exception;
    }

    private static bool SizePrefix(GameViewport __instance, ref int2 __result)
    {
        if (!_inUi || RenderScale.Window == int2.Zero || !ReferenceEquals(__instance, Program.MainViewport))
            return true;
        __result = RenderScale.Window;
        return false;
    }
}

// ImGui works in desktop coordinates with several windows on, so the game adds the main viewport's position to what it
// places. A mod that doesn't is right only with the window at the desktop's corner: elsewhere (windowed, or on a display
// left of the main one) its overlay opens outside the window, which makes ImGui give it an OS window of its own, and what
// it draws is off by the window's position. Here the desktop starts at the window's corner: every position the game
// reads of a window or a display is relative to the main window's, and one set through Position on another window is
// turned back. Only placing the main window itself uses the real ones; a mod can't read those, and no mod moves it.
[Hook(Phase.BeforeCallers)]
internal static class WindowOrigin
{
    private static AccessTools.FieldRef<GlfwWindow, int2> _position = null!;
    private static int2 _lastOrigin;
    private static int _placing;

    private static int2 Origin => _placing == 0 && Program.GetWindow() is { } main ? _position(main) : int2.Zero;

    public static string Apply(Harmony harmony)
    {
        _position = AccessTools.FieldRefAccess<GlfwWindow, int2>(Find.Field(typeof(GlfwWindow), "_position"));
        var relative = new HarmonyMethod(typeof(WindowOrigin), nameof(RelativePosition));
        harmony.Patch(Find.Getter(typeof(GlfwWindow), nameof(GlfwWindow.Position)), postfix: relative);
        harmony.Patch(Find.Getter(typeof(GlfwWindow), nameof(GlfwWindow.X)),
            postfix: new HarmonyMethod(typeof(WindowOrigin), nameof(RelativeX)));
        harmony.Patch(Find.Getter(typeof(GlfwWindow), nameof(GlfwWindow.Y)),
            postfix: new HarmonyMethod(typeof(WindowOrigin), nameof(RelativeY)));
        harmony.Patch(Find.Setter(typeof(GlfwWindow), nameof(GlfwWindow.Position)),
            prefix: new HarmonyMethod(typeof(WindowOrigin), nameof(SetPosition)));
        harmony.Patch(Find.Getter(typeof(GlfwMonitor), nameof(GlfwMonitor.Position)), postfix: relative);
        harmony.Patch(Find.Method(typeof(GlfwMonitor), nameof(GlfwMonitor.GetMonitorWorkArea)),
            postfix: new HarmonyMethod(typeof(WindowOrigin), nameof(RelativeWorkArea)));

        var enter = new HarmonyMethod(typeof(WindowOrigin), nameof(EnterPlacing));
        var leave = new HarmonyMethod(typeof(WindowOrigin), nameof(LeavePlacing));
        foreach (MethodBase placing in new MethodBase[]
        {
            Find.Method(typeof(GameSettings), nameof(GameSettings.ApplyTo), typeof(GlfwWindow)),
            Find.Method(typeof(GlfwWindow), nameof(GlfwWindow.CenterToMonitor)),
            Find.Setter(typeof(GlfwWindow), nameof(GlfwWindow.Monitor)),
            Find.Setter(typeof(GlfwWindow), nameof(GlfwWindow.X)),
            Find.Setter(typeof(GlfwWindow), nameof(GlfwWindow.Y)),
        })
            harmony.Patch(placing, prefix: enter, finalizer: leave);

        harmony.Patch(Find.Method(typeof(ImGuiBackendGlfwImpl), nameof(ImGuiBackendGlfwImpl.NewFrame)),
            prefix: new HarmonyMethod(typeof(WindowOrigin), nameof(NewFramePrefix)));
        return "on";
    }

    private static void RelativePosition(ref int2 __result) => __result -= Origin;

    private static void RelativeX(ref int __result) => __result -= Origin.X;

    private static void RelativeY(ref int __result) => __result -= Origin.Y;

    private static void RelativeWorkArea(ref int2 pos) => pos -= Origin;

    private static void SetPosition(GlfwWindow __instance, ref int2 value)
    {
        if (!ReferenceEquals(__instance, Program.GetWindow()))
            value += Origin;
    }

    private static void EnterPlacing() => _placing++;

    private static Exception? LeavePlacing(Exception? __exception)
    {
        _placing--;
        return __exception;
    }

    // The window moved: the other windows stayed, so ImGui reads their positions again
    private static void NewFramePrefix()
    {
        int2 origin = Origin;
        if (origin == _lastOrigin)
            return;
        _lastOrigin = origin;
        foreach (ImGuiViewportPtr viewport in ImGui.GetPlatformIO().Viewports)
            if (viewport.ID != ImGui.GetMainViewport().ID)
                viewport.PlatformRequestMove = true;
    }
}
