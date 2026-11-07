using System;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Simpler Dense Smoke, simpler_dense_smoke (port-settings.toml), and the file's experiment trail_caches (<large MiB>,
// <small MiB>,<tiny MiB>[,<minimum segments>]), which wins over it: the raymarch reads a dense cell of smoke from a
// baked lattice when the cell has a volume cache, and otherwise walks a subset of the cell's segments, about five
// times slower. The game gives each of the three lattice sizes an 8 MiB atlas (60 caches of 16^3, 481 of 8^3, 3,855
// of 4^3) and caches only cells with 33 segments or more. Simpler Dense Smoke sets 0,0,16: no room for the 16^3 and
// 8^3 caches, twice the room for the 4^3 ones; the densest smoke about 4 % faster on an M5 at Balanced, a dense
// carpet a little coarser seen from afar.
[Hook(Phase.Default)]
internal static class DenseSmoke
{
    private static int[] _values = [];

    public static string Apply(Harmony harmony)
    {
        _values = Array.ConvertAll(Settings.TrailCaches, v => (int)v);
        bool simpler = _values.Length == 0 && Settings.SimplerDenseSmoke;
        if (simpler)
            _values = [0, 0, 16];
        if (_values.Length == 0)
            return "game's";
        // With no room for any cache the game's atlases would be 1x1x1 texels, which its renderer refuses at start
        if (_values[0] <= 0 && _values[1] <= 0 && _values[2] <= 0)
            return "game's (" + string.Join(',', _values) +
                " leaves no room for any cache, which the game can't start with)";
        harmony.Patch(Find.Method(typeof(VolumetricTrailVolumeCache), "CreateVolumeCacheAtlasResources"),
            prefix: new HarmonyMethod(typeof(DenseSmoke), nameof(Prefix)));
        return (simpler ? "simpler dense smoke, " : "") + string.Join(',', _values);
    }

    private static void Prefix(VolumetricTrailVolumeCache __instance)
    {
        __instance.LargeVolumeCacheAtlasBudgetMiB = _values[0];
        __instance.SmallVolumeCacheAtlasBudgetMiB = _values[1];
        __instance.TinyVolumeCacheAtlasBudgetMiB = _values[2];
        if (_values.Length == 4)
            __instance.VolumeCacheMinimumSegmentCount = _values[3];
    }
}
