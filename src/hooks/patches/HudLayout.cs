using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Brutal.GlfwApi.Cocoa;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// HUD layout (port-settings.toml, the Mac Port tab changes it while the game runs). The game lays out its flight HUD in
// fractions of the window's height (ScreenReference's conversions): a panel's size is such a fraction times the HUD
// scale (GaugeCanvas, RectTransform), its offset from its anchor (a corner or an edge's middle of the window) a fraction
// alone, and inside the panels the buttons and frames are fractions too (ImGauge), while the text of the hosted panels
// (resources, staging, crew, burn) has a fixed size (20 points times the HUD scale). So a window shrinks the HUD with
// it, on a display narrower than 16:9 the panels overlap, and in a window shorter than the game's layout (1440 high)
// the text overflows them: a MacBook's display is 16:10 and about 1000 points high.
// With hud_layout = "fit", the HUD's sizes belong to the display the window is on (NSWindow.screen, the one holding most
// of it), and its positions to the window: every read of the HUD scale in the panels' classes takes it times the
// display's height / the window's (sizes, not offsets, go through it there) and times min(1, the display's aspect /
// (16/9)); inside the panels, fractions are of the display's height; the hosted panels' scale (their fonts and spacing)
// is also multiplied by the window's height / 1440, which with the display's height / the window's makes it the
// display's / 1440. Fullscreen, the HUD is the 16:9 layout scaled to the display's width, its text in proportion to its
// panels. A window has the same panels at the same places in proportion to it: a small one overlaps them, and the
// game's HUD scale at the window's height / the display's gives its layout fitted to the window. "game" leaves the
// game's layout.
internal static class HudLayout
{
    private const float Aspect = 16f / 9f, LayoutHeight = 1440f;

    // The panels' classes, which read the HUD scale with every size (GameSettings.GetGaugeScale, a one-line method the
    // JIT inlines: its callers are patched, not the method)
    private static readonly Type[] Panels = [typeof(RectTransform), typeof(CanvasTarget), typeof(GaugeCanvas), typeof(GaugeInspector)];

    // The classes that draw inside the panels, whose fractions are all sizes but a window's offset (ScreenReference's
    // conversions are one-liners too)
    private static readonly Type[] Contents = [typeof(ImGauge), typeof(CrewPortraitPanel), typeof(ResourcesPanel)];

    public static string Apply(Harmony harmony)
    {
        var gaugeScale = Find.Method(typeof(GameSettings), nameof(GameSettings.GetGaugeScale));
        var uvToPixels = Find.Method(typeof(ScreenReference), nameof(ScreenReference.UvToPixels), typeof(float2));
        var pixelsToUv = Find.Method(typeof(ScreenReference), nameof(ScreenReference.PixelsToUv), typeof(float2));
        int panels = Patch(harmony, Panels, nameof(PanelsTranspiler), gaugeScale);
        _offsets = 0;
        int contents = Patch(harmony, Contents, nameof(ContentsTranspiler), uvToPixels, pixelsToUv);
        if (panels == 0 || contents == 0)
            throw new Skip(panels == 0 ? "the HUD no longer reads its scale" : "the HUD's panels no longer convert their sizes");
        if (_offsets != 1)
            throw new Skip($"ImGauge's window offset found {_offsets} times");
        harmony.Patch(Find.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.BeginGaugeHostScope)),
            prefix: new HarmonyMethod(typeof(HudLayout), nameof(HostScopePrefix)));
        return Settings.HudLayout == "fit" ? "fit to the display" : "game's";
    }

    // Patches the methods of these classes (and their nested ones) that call one of these
    private static int Patch(Harmony harmony, Type[] classes, string transpiler, params MethodInfo[] calls)
    {
        var method = new HarmonyMethod(typeof(HudLayout), transpiler);
        int patched = 0;
        foreach (var m in classes.SelectMany(Nested).SelectMany(AccessTools.GetDeclaredMethods))
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || !PatchProcessor.GetOriginalInstructions(m).Any(c => calls.Any(c.Calls)))
                continue;
            harmony.Patch(m, transpiler: method);
            patched++;
        }
        return patched;
    }

    private static IEnumerable<Type> Nested(Type type) => new[] { type }.Concat(type.GetNestedTypes(AccessTools.all).SelectMany(Nested));

    // GameSettings.GetGaugeScale() -> GaugeScale()
    private static IEnumerable<CodeInstruction> PanelsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var gaugeScale = Find.Method(typeof(GameSettings), nameof(GameSettings.GetGaugeScale));
        var ours = Find.Method(typeof(HudLayout), nameof(GaugeScale));
        foreach (var c in instructions)
            yield return c.Calls(gaugeScale) ? new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c) : c;
    }

    // The conversions ContentsTranspiler left alone, as offsets: ImGauge's window offset, once
    private static int _offsets;

    // ScreenReference.UvToPixels(uv) and PixelsToUv(pixels) -> UvToPixels and PixelsToUv here, but for an offset (an
    // argument read straight from a field named so: ImGauge's windows)
    private static IEnumerable<CodeInstruction> ContentsTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var replace = new Dictionary<MethodInfo, MethodInfo>
        {
            [Find.Method(typeof(ScreenReference), nameof(ScreenReference.UvToPixels), typeof(float2))] = Find.Method(typeof(HudLayout), nameof(UvToPixels)),
            [Find.Method(typeof(ScreenReference), nameof(ScreenReference.PixelsToUv), typeof(float2))] = Find.Method(typeof(HudLayout), nameof(PixelsToUv)),
        };
        CodeInstruction? previous = null;
        foreach (var c in instructions)
        {
            bool offset = previous?.operand is FieldInfo field && field.Name.Contains("Offset", StringComparison.Ordinal);
            var result = c;
            if (c.operand is MethodInfo m && replace.TryGetValue(m, out var ours))
            {
                if (offset)
                    _offsets++;
                else
                    result = new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c);
            }
            yield return result;
            previous = c;
        }
    }

    private static int2 Window => Program.GetWindow() is { } window ? window.Size : int2.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X, Y, Width, Height;
    }

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_msgSend(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern CGRect objc_msgSend_CGRect(nint receiver, nint selector);

    private static readonly nint ScreenSelector = sel_registerName("screen"), FrameSelector = sel_registerName("frame");
    private static float2 _display;
    private static long _displayRead;
    private static bool _failed;  // the game's layout, as with "game" (Hooks.Fail)

    // The size in points of the display the window is on (the window's size if macOS doesn't say), read again every
    // quarter of a second: when it changes, the panels' sizes change and the game lays each one out again
    private static float2 Display
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            if (_displayRead != 0 && Stopwatch.GetElapsedTime(_displayRead, now).TotalSeconds < 0.25)
                return _display;
            _displayRead = now;
            int2 window = Window;
            _display = new float2(window.X, window.Y);
            if (Program.GetWindow() is { } w && w.GetCocoaWindow() is var nsWindow && nsWindow != 0 &&
                objc_msgSend(nsWindow, ScreenSelector) is var screen && screen != 0)
            {
                var frame = objc_msgSend_CGRect(screen, FrameSelector);
                if (frame.Width > 0 && frame.Height > 0)
                    _display = new float2((float)frame.Width, (float)frame.Height);
            }
            return _display;
        }
    }

    private static bool Fit(out float2 display, out int2 window)
    {
        display = default;
        window = default;
        if (_failed || Settings.HudLayout != "fit")
            return false;
        try
        {
            display = Display;
            window = Window;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("hud_layout", e);
            return false;
        }
        return display.X > 0f && display.Y > 0f && window.Y > 0;
    }

    // The HUD scale, times the display's height over the window's and the share of the 16:9 layout's width the display has
    public static float GaugeScale()
    {
        float scale = GameSettings.GetGaugeScale();
        if (!Fit(out var display, out var window))
            return scale;
        return scale * display.Y / window.Y * MathF.Min(1f, display.X / display.Y / Aspect);
    }

    public static float2 UvToPixels(float2 uv) =>
        Fit(out var display, out _) ? new float2(display.Y * uv.X, display.Y * uv.Y) : ScreenReference.UvToPixels(uv);

    public static float2 PixelsToUv(float2 pixels) =>
        Fit(out var display, out _) ? new float2(pixels.X / display.Y, pixels.Y / display.Y) : ScreenReference.PixelsToUv(pixels);

    // The hosted panels' scale (from GaugeScale already: the display's height over the window's), times the window's
    // height over the layout's
    private static void HostScopePrefix(ref float hostScale)
    {
        if (Fit(out _, out var window))
            hostScale *= window.Y / LayoutHeight;
    }
}
