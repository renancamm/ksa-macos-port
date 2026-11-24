using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.Numerics;
using HarmonyLib;
using KSA;
using KsaMetal.Hooks.Features;

namespace KsaMetal.Hooks.Fixes;

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
    private static MethodInfo _viewports = null!, _console = null!;

    public static string Apply(Harmony harmony)
    {
        var frame = Find.Method(typeof(Program), "OnFrame", typeof(double), typeof(double));
        var size = Find.Getter(typeof(GameViewport), nameof(GameViewport.Size));
        _viewports = Find.Method(typeof(Program), "OnFrameViewports");
        _console = Find.Method(typeof(Program), "OnDrawUiConsole");
        harmony.Patch(frame,
            transpiler: new HarmonyMethod(typeof(UiViewSize), nameof(OnFrameTranspiler)),
            postfix: new HarmonyMethod(typeof(UiViewSize), nameof(Enter)) { priority = Priority.First },
            finalizer: new HarmonyMethod(typeof(UiViewSize), nameof(Finalizer)));
        harmony.Patch(size, prefix: new HarmonyMethod(typeof(UiViewSize), nameof(SizePrefix)));
        return "on";
    }

    // From after OnFrameViewports, which rebuilds views at their render size, to after OnDrawUiConsole, the UI's last
    private static IEnumerable<CodeInstruction> OnFrameTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        int end = Find.Once(code, c => c.Calls(_console), "the UI's last call");
        code.Insert(end + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UiViewSize), nameof(Leave))));
        int start = Find.Once(code, c => c.Calls(_viewports), "the views' update");
        code.Insert(start + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(UiViewSize), nameof(Enter))));
        return code;
    }

    private static void Enter() => _inUi = true;

    private static void Leave() => _inUi = false;

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
