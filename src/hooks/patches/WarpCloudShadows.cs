using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA.Atmosphere.Rendering;

namespace KsaMetalHooks.Patches;

// Lighter warp cloud shadows, lighter_warp_cloud_shadows (port-settings.toml). CloudShadowVolume.MoveVolume asks for a
// full rebuild, all 256 slices instead of 24, when the camera is more than 75 % of the volume's reach from where the
// volume was last placed. At very high warp in low orbit that is every frame: about 40 ms of GPU time, below 30 fps,
// where the game loses warp speed. A rebuild asked for within half a second of the last request refreshes the usual
// slices instead, so a single jump of the camera still gets its rebuild. During warp most slices hold another place's
// shadows; afterwards the sun's are right within about 11 frames and the ambient's within about 32 (4 times that with
// fewer_cloud_shadow_updates).
[Hook(Phase.Default, Switch = nameof(Settings.LighterWarpCloudShadows))]
internal static class WarpCloudShadows
{
    private static readonly long Recent = Stopwatch.Frequency / 2;
    private static readonly ConditionalWeakTable<CloudShadowVolume, StrongBox<long>> LastRequest = new();

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "UpdateVolumeDensity"),
            prefix: new HarmonyMethod(typeof(WarpCloudShadows), nameof(Prefix)));
        return "on";
    }

    private static void Prefix(CloudShadowVolume __instance, ref bool fullRebuild)
    {
        if (!fullRebuild)
            return;
        long now = Stopwatch.GetTimestamp();
        var last = LastRequest.GetValue(__instance, _ => new StrongBox<long>(now - 2 * Recent));
        fullRebuild = now - last.Value > Recent;
        last.Value = now;
    }
}
