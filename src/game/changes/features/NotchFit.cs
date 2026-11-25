using System;
using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Fit Below the Notch, fit_below_the_notch (port-settings.toml; the Mac Port tab changes it while the game runs). On a
// display with a camera notch, Borderless and Fullscreen put the window's top row beside the camera, where the game's
// menu and the HUD's top panels lose their middle. The GLFW shim places the window (src/game/shims/glfw/cover.m): with
// the row on, below the notch, a black window filling the screen behind it. This tells the shim the row's value, when
// the game starts and on the first frame after Apply changes it (the ImGui backend's frame start, on the main thread,
// where the shim's windows live), and HudFit how much of the display the window gave up.
//
// A fresh install has no Resolution (0 x 0), and GameSettings.ApplyTo then saves the window's size as one: below the
// notch, a size no display mode has, which Fullscreen would then ask GLFW for. A prefix gives such settings the selected
// screen's mode first, the size the game would have saved without the fit.
[Hook(Phase.Default)]
internal static class NotchFit
{
    private static bool _on;
    private static bool _failed;
    private static Func<GameSettings, GlfwMonitor> _selectedMonitor = null!;

    public static string Apply(Harmony harmony)
    {
        _on = Settings.FitBelowTheNotch;
        _selectedMonitor = AccessTools.MethodDelegate<Func<GameSettings, GlfwMonitor>>(
            Find.Method(typeof(GameSettings), "GetSelectedMonitor"));
        try
        {
            glfwShimNotchInset();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new Skip("the GLFW shim doesn't place the window");
        }
        harmony.Patch(Find.Method(typeof(ImGuiBackendGlfwImpl), nameof(ImGuiBackendGlfwImpl.NewFrame)),
            prefix: new HarmonyMethod(typeof(NotchFit), nameof(NewFramePrefix)));
        harmony.Patch(Find.Method(typeof(GameSettings), nameof(GameSettings.ApplyTo), typeof(GlfwWindow)),
            prefix: new HarmonyMethod(typeof(NotchFit), nameof(ApplyToPrefix)));
        // Patched: only now the shim moves the window
        glfwShimFitBelowNotch(_on ? 1 : 0);
        return _on ? "on" : "game's";
    }

    private static void NewFramePrefix()
    {
        bool on = Settings.FitBelowTheNotch;
        if (on == _on || _failed)
            return;
        _on = on;
        Tell(on);
    }

    private static void ApplyToPrefix(GameSettings __instance)
    {
        if (_failed || !_on || (__instance.ScreenWidth != 0 && __instance.ScreenHeight != 0))
            return;
        try
        {
            var mode = _selectedMonitor(__instance).VideoMode;
            if (mode.Width > 0 && mode.Height > 0)
            {
                __instance.ScreenWidth = mode.Width;
                __instance.ScreenHeight = mode.Height;
            }
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void Tell(bool on)
    {
        try
        {
            glfwShimFitBelowNotch(on ? 1 : 0);
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // The game's own window again, as with the row off
    private static void Failed(Exception e)
    {
        _failed = true;
        Hooks.Fail(nameof(NotchFit), e);
        try
        {
            glfwShimFitBelowNotch(0);
        }
        catch (Exception)
        {
            // The shim isn't there: nothing moved the window
        }
    }

    // The notch's height in points the game's window was moved below, 0 when it wasn't (HudFit: the display's height
    // the window can have)
    public static float Inset
    {
        get
        {
            try
            {
                return (float)glfwShimNotchInset();
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return 0f;
            }
        }
    }

    [DllImport("libGlfwShim.dylib")]
    private static extern void glfwShimFitBelowNotch(int on);

    [DllImport("libGlfwShim.dylib")]
    private static extern double glfwShimNotchInset();
}
