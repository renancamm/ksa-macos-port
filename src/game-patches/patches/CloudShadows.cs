using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;
using KSA.Atmosphere.Rendering;

namespace KsaPatches;

// Cloud shadows, "slow" (port-settings.toml). CloudShadowVolume.UpdateVolumeDensity refreshes 24 slices of the cloud
// shadow volume per frame (8 for ambient light). With 6 (and 2), the volume costs about 1.5 ms less; it converges to
// the same shadows, but they take about 4 times longer to follow moving clouds.
internal static class CloudShadows
{
    private const int Slices = 6, AmbientSlices = 2;

    public static string Apply(Harmony harmony)
    {
        if (!Settings.SlowCloudShadows)
            return "normal";
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "UpdateVolumeDensity"),
            transpiler: new HarmonyMethod(typeof(CloudShadows), nameof(Transpiler)));
        return "slow";
    }

    // fullRebuild ? 256 : 24 and fullRebuild ? 256 : 8: the per-frame counts are the constants just before a
    // "br, ldc 256" pair
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        Replace(code, 24, Slices);
        Replace(code, 8, AmbientSlices);
        return code;
    }

    private static void Replace(List<CodeInstruction> code, int from, int to)
    {
        var at = Enumerable.Range(0, code.Count - 2).Where(i =>
            code[i].LoadsConstant(from) && code[i + 1].Branches(out _) && code[i + 2].LoadsConstant(256)).ToList();
        if (at.Count != 1)
            throw new Skip($"the slice count {from} found {at.Count} times");
        code[at[0]] = new CodeInstruction(OpCodes.Ldc_I4, to).MoveLabelsFrom(code[at[0]]);
    }
}
