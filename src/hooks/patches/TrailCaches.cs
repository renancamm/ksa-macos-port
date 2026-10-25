using System;
using System.Globalization;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Trail volume caches (experiment, KSA_TRAIL_CACHE=<large MiB>,<small MiB>,<tiny MiB>[,<minimum segments>]): the
// raymarch reads a dense cell of smoke from a baked lattice when the cell has a volume cache, and otherwise walks a
// subset of the cell's segments, about five times slower. The game gives each of the three lattice sizes an 8 MiB
// atlas (60 caches of 16^3, 481 of 8^3, 3,855 of 4^3) and caches only cells with 33 segments or more.
internal static class TrailCaches
{
    private static int[] _values = [];

    public static string Apply(Harmony harmony)
    {
        string spec = Environment.GetEnvironmentVariable("KSA_TRAIL_CACHE");
        if (string.IsNullOrEmpty(spec))
            return "game's" + ApplyShadows(harmony);
        _values = Array.ConvertAll(spec.Split(','), v => int.Parse(v, CultureInfo.InvariantCulture));
        if (_values.Length is < 3 or > 4)
            throw new Skip("KSA_TRAIL_CACHE takes 3 or 4 numbers");
        harmony.Patch(Find.Method(typeof(VolumetricTrailVolumeCache), "CreateVolumeCacheAtlasResources"),
            prefix: new HarmonyMethod(typeof(TrailCaches), nameof(Prefix)));
        return spec + ApplyShadows(harmony);
    }

    private static void Prefix(VolumetricTrailVolumeCache __instance)
    {
        __instance.LargeVolumeCacheAtlasBudgetMiB = _values[0];
        __instance.SmallVolumeCacheAtlasBudgetMiB = _values[1];
        __instance.TinyVolumeCacheAtlasBudgetMiB = _values[2];
        if (_values.Length == 4)
            __instance.VolumeCacheMinimumSegmentCount = _values[3];
    }

    // KSA_TRAIL_SHADOWS=<cascades>,<resolution>,<max distance m>: the smoke's sun shadow cascades, drawn with the
    // game's plumeTrailShadows setting (8 of 512^2 out to 20 km in the game)
    private static float[] _shadows = [];

    private static string ApplyShadows(Harmony harmony)
    {
        string spec = Environment.GetEnvironmentVariable("KSA_TRAIL_SHADOWS");
        if (string.IsNullOrEmpty(spec))
            return "";
        _shadows = Array.ConvertAll(spec.Split(','), v => float.Parse(v, CultureInfo.InvariantCulture));
        harmony.Patch(Find.Method(typeof(VolumetricTrailShadowCascades), "CreateResources"),
            prefix: new HarmonyMethod(typeof(TrailCaches), nameof(ShadowsPrefix)));
        return "; shadows " + spec;
    }

    private static void ShadowsPrefix(VolumetricTrailShadowCascades __instance)
    {
        __instance.ShadowCascadeCount = (int)_shadows[0];
        __instance.ShadowCascadeResolution = (int)_shadows[1];
        __instance.ShadowCascadeMaxDistance = _shadows[2];
    }
}
