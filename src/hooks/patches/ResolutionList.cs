using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Brutal.GlfwApi.Cocoa;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Resolution list. The game's Resolution setting (Display) lists the display's sizes in points, the way macOS sizes
// windows: on a 14" MacBook Pro 1512 x 982 is the whole display, drawn with 3024 x 1964 pixels, and nothing says so.
// On a Retina display the list now shows each size in pixels with "Retina" (3024 x 1964 Retina), the numbers a player
// knows from the display's specifications; the game still matches and saves the sizes in points. The GLFW shim lists
// only the kind of mode the display had when the game first asked (HiDPI on a Retina display), so every size there is
// drawn at twice its points; the pixels per point are read once per display too. Other displays keep the game's list.
internal static class ResolutionList
{
    private static FieldInfo _populatedScreen = null!;
    private static int _combos;
    // Pixels per point by display (CGDirectDisplayID), from its mode when first read
    private static readonly Dictionary<uint, int> Scales = new();
    private static readonly Dictionary<(int Width, int Height, int Scale), string> Labels = new();
    private static bool _failed;  // the game's list (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        _populatedScreen = Find.Field(typeof(GameSettings), "_populatedScreenIndex");
        _combos = 0;
        harmony.Patch(Find.Method(typeof(GameSettings), nameof(GameSettings.OnDrawUi)),
            transpiler: new HarmonyMethod(typeof(ResolutionList), nameof(DrawTranspiler)));
        if (_combos != 1)
            throw new Skip($"the Resolution combo found {_combos} times");
        return "on";
    }

    // GameSettings.DrawLiveValueCombo<ScreenResolution>(...) -> ResolutionCombo(...)
    private static IEnumerable<CodeInstruction> DrawTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var ours = Find.Method(typeof(ResolutionList), nameof(ResolutionCombo));
        foreach (var c in instructions)
        {
            if (c.operand is MethodInfo { Name: "DrawLiveValueCombo", IsGenericMethod: true } m &&
                m.GetGenericArguments()[0] == typeof(ScreenResolution))
            {
                _combos++;
                yield return new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c);
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
        bool changed = false;
        if (ConsoleWidgets.BeginComboControl(id, Label(value), pending))
        {
            foreach (var option in options)
            {
                bool selected = option.GetKey().Equals(value.GetKey(), StringComparison.OrdinalIgnoreCase);
                if (ImGui.Selectable(Label(option), selected, ImGuiSelectableFlags.None, (float2?)null))
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

    private static string Label(ScreenResolution size)
    {
        int scale = Scale();
        if (scale <= 1)
            return size.GetName();
        if (!Labels.TryGetValue((size.Width, size.Height, scale), out var label))
            Labels[(size.Width, size.Height, scale)] = label = $"{size.Width * scale} x {size.Height * scale} Retina";
        return label;
    }

    // Pixels per point of the display the list is for; 1 when it can't be read
    private static int Scale()
    {
        if (_failed)
            return 1;
        try
        {
            var monitor = GameSettings.GetMonitor((int)_populatedScreen.GetValue(null)!);
            uint display = monitor.IsNull() ? 0 : monitor.GetCocoaMonitor();
            if (display == 0)
                return 1;
            if (!Scales.TryGetValue(display, out int scale))
            {
                scale = 1;
                nint mode = CGDisplayCopyDisplayMode(display);
                if (mode != 0)
                {
                    nuint points = CGDisplayModeGetWidth(mode);
                    if (points > 0)
                        scale = (int)(CGDisplayModeGetPixelWidth(mode) / points);
                    CGDisplayModeRelease(mode);
                }
                Scales[display] = scale;
            }
            return scale;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("resolution_list", e);
            return 1;
        }
    }

    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [DllImport(CoreGraphics)]
    private static extern nint CGDisplayCopyDisplayMode(uint display);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetWidth(nint mode);

    [DllImport(CoreGraphics)]
    private static extern nuint CGDisplayModeGetPixelWidth(nint mode);

    [DllImport(CoreGraphics)]
    private static extern void CGDisplayModeRelease(nint mode);
}
