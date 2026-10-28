using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.GlfwApi;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Resolution list. The game's Resolution setting (Display) lists the display's sizes in points, the way macOS sizes
// windows: on a 14" MacBook Pro 1512 x 982 is the whole display, drawn with 3024 x 1964 pixels, and nothing says so.
// The list now shows each size in pixels, the numbers on the display's specifications, and marks the one that fills
// the display's own pixels "native" (3024 x 1964 native); the game still matches and saves the sizes in points. The
// GLFW shim lists only the kind of mode the display's desktop has (HiDPI on a Retina display or a 4K monitor at its
// default), so every size there is drawn at the same pixels per point: its desktop mode's (Displays).
//
// Picking another Screen leaves the Resolution and Refresh rate of the old one, which the new list may not have:
// the box then shows a size the list doesn't. A size or rate the new screen doesn't have becomes its native size
// (else its desktop's) and its highest rate at that size.
internal static class ResolutionList
{
    private static FieldInfo _populatedScreen = null!;
    private static Action<GameSettings, ScreenSelection> _updateScreenSelection = null!;
    private static int _combos, _screenCalls;
    private static readonly Dictionary<(int Width, int Height, int Scale, bool Native), string> Labels = new();
    private static bool _failed;  // the game's list (Hooks.Fail)

    private readonly record struct Display(int Scale, (int Width, int Height)? Native);

    public static string Apply(Harmony harmony)
    {
        _populatedScreen = Find.Field(typeof(GameSettings), "_populatedScreenIndex");
        _updateScreenSelection = AccessTools.MethodDelegate<Action<GameSettings, ScreenSelection>>(
            Find.Method(typeof(GameSettings), "UpdateScreenSelection"));
        _combos = _screenCalls = 0;
        harmony.Patch(Find.Method(typeof(GameSettings), nameof(GameSettings.OnDrawUi)),
            transpiler: new HarmonyMethod(typeof(ResolutionList), nameof(DrawTranspiler)));
        if (_combos != 1)
            throw new Skip($"the Resolution combo found {_combos} times");
        if (_screenCalls != 1)
            throw new Skip($"UpdateScreenSelection found {_screenCalls} times");
        return "on";
    }

    // GameSettings.DrawLiveValueCombo<ScreenResolution>(...) -> ResolutionCombo(...),
    // _temporary.UpdateScreenSelection(value) -> ScreenChosen(_temporary, value)
    private static IEnumerable<CodeInstruction> DrawTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var combo = Find.Method(typeof(ResolutionList), nameof(ResolutionCombo));
        var screen = Find.Method(typeof(ResolutionList), nameof(ScreenChosen));
        foreach (var c in instructions)
        {
            if (c.operand is MethodInfo { Name: "DrawLiveValueCombo", IsGenericMethod: true } m &&
                m.GetGenericArguments()[0] == typeof(ScreenResolution))
            {
                _combos++;
                yield return new CodeInstruction(OpCodes.Call, combo).MoveLabelsFrom(c).MoveBlocksFrom(c);
            }
            else if (c.operand is MethodInfo { Name: "UpdateScreenSelection" } u && u.DeclaringType == typeof(GameSettings))
            {
                _screenCalls++;
                yield return new CodeInstruction(OpCodes.Call, screen).MoveLabelsFrom(c).MoveBlocksFrom(c);
            }
            else
            {
                yield return c;
            }
        }
    }

    // The game's DrawLiveValueCombo with the sizes' labels
    public static bool ResolutionCombo(ReadOnlySpan<char> id, ref ScreenResolution value, List<ScreenResolution> options, bool pending)
    {
        var display = ListDisplay();
        bool changed = false;
        if (ConsoleWidgets.BeginComboControl(id, Label(value, display), pending))
        {
            foreach (var option in options)
            {
                bool selected = option.GetKey().Equals(value.GetKey(), StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(Label(option, display), selected, ImGuiSelectableFlags.None, (float2?)null))
                {
                    value = option;
                    changed = true;
                }
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ConsoleWidgets.EndComboControl();
        }
        return changed;
    }

    // The game's UpdateScreenSelection, then the new screen's size and rate where it doesn't have the chosen ones
    public static void ScreenChosen(GameSettings settings, ScreenSelection screen)
    {
        _updateScreenSelection(settings, screen);
        if (_failed)
            return;
        try
        {
            FitScreen(settings, GameSettings.GetMonitor(screen.Index));
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("resolution_list", e);
        }
    }

    private static void FitScreen(GameSettings settings, GlfwMonitor monitor)
    {
        if (monitor.IsNull())
            return;
        var modes = monitor.VideoModes;
        bool hasSize = false, hasRate = false;
        foreach (var mode in modes)
        {
            hasSize |= mode.Width == settings.ScreenWidth && mode.Height == settings.ScreenHeight;
            hasRate |= mode.RefreshRate == settings.ScreenRefreshRate;
        }
        if (!hasSize && FullSize(monitor, modes) is { } size)
            settings.UpdateScreenSize(size.Width, size.Height);
        if (hasRate)
            return;
        int rate = 0;
        foreach (var mode in modes)
        {
            if (mode.Width == settings.ScreenWidth && mode.Height == settings.ScreenHeight)
                rate = Math.Max(rate, mode.RefreshRate);
        }
        if (rate > 0)
            settings.ScreenRefreshRate = rate;
    }

    // The listed size that fills the display's own pixels, else the desktop's; null when neither is listed
    private static (int Width, int Height)? FullSize(GlfwMonitor monitor, ReadOnlySpan<GlfwVidMode> modes)
    {
        if (Displays.Desktop(monitor) is not { } desktop)
            return null;
        var native = Displays.Native(monitor);
        (int Width, int Height)? found = null;
        foreach (var mode in modes)
        {
            if (native is { } n && mode.Width * desktop.Scale == n.Width && mode.Height * desktop.Scale == n.Height)
                return (mode.Width, mode.Height);
            if (mode.Width == desktop.Width && mode.Height == desktop.Height)
                found = (mode.Width, mode.Height);
        }
        return found;
    }

    private static string Label(ScreenResolution size, Display? display)
    {
        if (display is not { } d)
            return size.GetName();
        int width = size.Width * d.Scale, height = size.Height * d.Scale;
        bool native = d.Native is { } n && n.Width == width && n.Height == height;
        if (!Labels.TryGetValue((size.Width, size.Height, d.Scale, native), out var label))
            Labels[(size.Width, size.Height, d.Scale, native)] = label = native ? $"{width} x {height} native" : $"{width} x {height}";
        return label;
    }

    // The pixels per point and own pixels of the display the list is for; null when they can't be read
    private static Display? ListDisplay()
    {
        if (_failed)
            return null;
        try
        {
            var monitor = GameSettings.GetMonitor((int)_populatedScreen.GetValue(null)!);
            return Displays.Desktop(monitor) is { } desktop ? new Display(desktop.Scale, Displays.Native(monitor)) : null;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("resolution_list", e);
            return null;
        }
    }
}
