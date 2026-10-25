using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// HUD layout (port-settings.toml, the Mac Port tab changes it while the game runs). The game sizes its flight HUD's
// panels from the window's height (GaugeCanvas, RectTransform: a fraction of the height, times the HUD scale), places
// them from its width, and draws the text inside the hosted panels (resources, staging, crew, burn) at a fixed size
// (20 points times the HUD scale). On a display narrower than 16:9 the panels overlap, and in a window shorter than
// the game's layout (1440 high) the text overflows them: a MacBook's display is 16:10 and about 1000 points high.
// With hud_layout = "fit", every read of the HUD scale in the HUD's classes takes it times
// min(1, aspect / (16/9)), so the HUD is the 16:9 layout scaled to the width, and the hosted panels' scale (their
// fonts and spacing) is also multiplied by the window's height / 1440, so their text keeps its proportion to the
// panel. "game" leaves the game's layout.
internal static class HudLayout
{
    private const float Aspect = 16f / 9f, LayoutHeight = 1440f;

    // The classes that read the HUD scale (GameSettings.GetGaugeScale, a one-line method the JIT inlines: its callers
    // are patched, not the method)
    private static readonly Type[] Readers = [typeof(RectTransform), typeof(CanvasTarget), typeof(GaugeCanvas), typeof(GaugeInspector)];

    public static string Apply(Harmony harmony)
    {
        var gaugeScale = Find.Method(typeof(GameSettings), nameof(GameSettings.GetGaugeScale));
        var transpiler = new HarmonyMethod(typeof(HudLayout), nameof(GaugeScaleTranspiler));
        int patched = 0;
        foreach (var method in Readers.SelectMany(Nested).SelectMany(AccessTools.GetDeclaredMethods))
        {
            if (method.IsAbstract || method.IsGenericMethodDefinition ||
                !PatchProcessor.GetOriginalInstructions(method).Any(c => c.Calls(gaugeScale)))
                continue;
            harmony.Patch(method, transpiler: transpiler);
            patched++;
        }
        if (patched == 0)
            throw new Skip("the HUD no longer reads its scale");
        harmony.Patch(Find.Method(typeof(ConsoleStyle), nameof(ConsoleStyle.BeginGaugeHostScope)),
            prefix: new HarmonyMethod(typeof(HudLayout), nameof(HostScopePrefix)));
        return Settings.HudLayout == "fit" ? "fit to the display" : "game's";
    }

    private static IEnumerable<Type> Nested(Type type) => new[] { type }.Concat(type.GetNestedTypes(AccessTools.all).SelectMany(Nested));

    // GameSettings.GetGaugeScale() -> GaugeScale()
    private static IEnumerable<CodeInstruction> GaugeScaleTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var gaugeScale = Find.Method(typeof(GameSettings), nameof(GameSettings.GetGaugeScale));
        var ours = Find.Method(typeof(HudLayout), nameof(GaugeScale));
        foreach (var c in instructions)
            yield return c.Calls(gaugeScale) ? new CodeInstruction(OpCodes.Call, ours).MoveLabelsFrom(c).MoveBlocksFrom(c) : c;
    }

    private static int2 Window => Program.GetWindow() is { } window ? window.Size : int2.Zero;

    // The HUD scale, times the share of the 16:9 layout's width the window has
    public static float GaugeScale()
    {
        float scale = GameSettings.GetGaugeScale();
        int2 window = Window;
        if (Settings.HudLayout != "fit" || window.X <= 0 || window.Y <= 0)
            return scale;
        return scale * MathF.Min(1f, (float)window.X / window.Y / Aspect);
    }

    // The hosted panels' scale (from GaugeScale already), times the window's height over the layout's
    private static void HostScopePrefix(ref float hostScale)
    {
        int2 window = Window;
        if (Settings.HudLayout == "fit" && window.Y > 0)
            hostScale *= window.Y / LayoutHeight;
    }
}
