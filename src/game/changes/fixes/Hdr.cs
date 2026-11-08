using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using HarmonyLib;
using KSA;
using Render;

namespace KsaMetal.Hooks.Fixes;

// HDR brightness. The game's HDR10 output is in nits; KSA Metal shows it with EDR, maps the game's UI brightness
// (Display > HDR UI Brightness) to the display's SDR white, the brightness of other windows, and tells macOS the peak
// (HDR Peak Brightness). KSA Metal needs both: the game keeps UI brightness to itself and sends the peak only when it
// changes. A postfix on GameSettings.ApplyTo(GlfwWindow), where the game hands its Display settings to its renderer
// when it starts and at every Apply, passes both on, and one on GameSettings.PreviewHdrBrightness, which sets the
// sliders' values each frame the Display settings are open, so that the UI stays at SDR white while one moves.
[Hook(Phase.PortParts)]
internal static class Hdr
{
    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlHdrNits(float uiBrightness, float peak);

    public static string Apply(Harmony harmony)
    {
        var apply = Find.Method(typeof(GameSettings), nameof(GameSettings.ApplyTo), typeof(GlfwWindow));
        var preview = Find.Method(typeof(GameSettings), "PreviewHdrBrightness");
        Find.Getter(typeof(HdrOutput), nameof(HdrOutput.UiBrightnessNits));
        Find.Getter(typeof(HdrOutput), nameof(HdrOutput.PeakNits));
        ApplyPostfix();  // a driver without it fails the hook here
        harmony.Patch(apply, postfix: new HarmonyMethod(typeof(Hdr), nameof(ApplyPostfix)));
        harmony.Patch(preview, postfix: new HarmonyMethod(typeof(Hdr), nameof(ApplyPostfix)));
        return "on";
    }

    private static void ApplyPostfix() => ksaMtlHdrNits(HdrOutput.UiBrightnessNits, HdrOutput.PeakNits);
}
