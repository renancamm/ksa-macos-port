using System.Runtime.InteropServices;
using Brutal.ImGuiApi;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Fixes;

// The game's extra windows (detached gauges, pop-outs) are created at macOS's floating level, and GLFW puts a fullscreen
// window above that: a window detached in Fullscreen mode went behind the game. While the main window is at the
// floating level or above (Always on Top, Fullscreen), the extra ones are one above it.
[Hook(Phase.Default)]
internal static class ExtraWindows
{
    private const long FloatingLevel = 3;

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern long GetLevel(nint window, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SetLevel(nint window, nint selector, long level);

    private static readonly nint LevelSelector = sel_registerName("level"), SetLevelSelector = sel_registerName("setLevel:");

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(ImGuiBackendGlfwImpl), nameof(ImGuiBackendGlfwImpl.NewFrame)),
            prefix: new HarmonyMethod(typeof(ExtraWindows), nameof(NewFramePrefix)));
        return "on";
    }

    private static void NewFramePrefix()
    {
        var viewports = ImGui.GetPlatformIO().Viewports;
        if (viewports.Count < 2)
            return;
        ImGuiViewportPtr main = ImGui.GetMainViewport();
        long mainLevel = GetLevel(main.PlatformHandleRaw, LevelSelector);
        long level = mainLevel >= FloatingLevel ? mainLevel + 1 : FloatingLevel;
        foreach (ImGuiViewportPtr viewport in viewports)
        {
            // PlatformHandle, not the raw one: a closed window's viewport stays a frame longer, its raw handle dangling
            if (viewport.ID != main.ID && viewport.PlatformHandle != 0 && GetLevel(viewport.PlatformHandleRaw, LevelSelector) != level)
                SetLevel(viewport.PlatformHandleRaw, SetLevelSelector, level);
        }
    }
}
