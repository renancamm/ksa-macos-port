using System.Runtime.InteropServices;
using Brutal.GlfwApi;
using HarmonyLib;
using KSA;
using Render;

namespace KsaMetalHooks;

// HDR brightness. The game's HDR10 output is in nits; KSA Metal shows it with EDR, maps the game's paper white
// (Display > HDR Paper White) to the display's SDR white, the brightness of other windows, and tells macOS the peak
// (HDR Peak Brightness). KSA Metal needs both: the game keeps paper white to itself and sends the peak only when it
// rebuilds the swapchain. A postfix on GameSettings.ApplyTo(GlfwWindow), where the game hands its Display settings to
// its renderer when it starts and at every Apply, passes both on.
internal static class Hdr
{
    [DllImport("libvulkan.1.dylib")]
    private static extern void ksaMtlHdrNits(float paperWhite, float peak);

    public static string Apply(Harmony harmony)
    {
        var apply = Find.Method(typeof(GameSettings), nameof(GameSettings.ApplyTo), typeof(GlfwWindow));
        Find.Getter(typeof(HdrOutput), nameof(HdrOutput.PaperWhiteNits));
        Find.Getter(typeof(HdrOutput), nameof(HdrOutput.PeakNits));
        ApplyPostfix();  // a driver without it fails the hook here
        harmony.Patch(apply, postfix: new HarmonyMethod(typeof(Hdr), nameof(ApplyPostfix)));
        return "on";
    }

    private static void ApplyPostfix() => ksaMtlHdrNits(HdrOutput.PaperWhiteNits, HdrOutput.PeakNits);
}
