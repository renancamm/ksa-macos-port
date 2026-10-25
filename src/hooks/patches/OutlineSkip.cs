using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Outline skip. PartSelectedRenderer.Run draws the outline around selected parts, a compute pass over the whole view
// (0.5 to 1 ms), every frame, also when nothing is selected. Only selected parts mark the image it outlines, so it now
// runs only while a part is selected, counted in Part.Selected's setter. The image is the same.
internal static class OutlineSkip
{
    private static int _selected;

    public static string Apply(Harmony harmony)
    {
        var setter = Find.Setter(typeof(Part), nameof(Part.Selected));
        var run = Find.Method(typeof(PartSelectedRenderer), nameof(PartSelectedRenderer.Run));
        harmony.Patch(setter, prefix: new HarmonyMethod(typeof(OutlineSkip), nameof(SelectedPrefix)));
        harmony.Patch(run, prefix: new HarmonyMethod(typeof(OutlineSkip), nameof(RunPrefix)));
        return "on";
    }

    private static void SelectedPrefix(Part __instance, bool value)
    {
        if (__instance.Selected != value)
            _selected += value ? 1 : -1;
    }

    private static bool RunPrefix() => _selected > 0;
}
