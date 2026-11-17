using System.Diagnostics;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA.Atmosphere.Rendering;

namespace KsaMetal.Hooks.Features;

// Lighter warp cloud shadows, lighter_warp_cloud_shadows (port-settings.toml). CloudShadowVolume.MoveVolume asks for a
// full rebuild when the camera is more than 75 % of the volume's reach from where the volume was last placed: all 256
// slices of the shadow volumes instead of 24 and 8, and since KSA 5901 all 128 of the cloud lighting volumes instead of
// 4. At very high warp in low orbit that is every frame: about 40 ms of GPU time on an M5 in KSA 5801 (the shadow
// volumes alone), below 30 fps, where the game loses warp speed. A rebuild asked for within half a second of the last
// request refreshes the usual slices instead, so a single jump of the camera still gets its rebuild. A full rebuild
// also swaps the volumes it writes (flip and flop) without resampling them, so the skipped rebuild swaps them back: the
// volumes in use keep their content while the volume follows the camera, as on a frame without a move. During warp most
// slices hold another place's shadows and cloud light; afterwards the sun's shadows are right within about 11 frames,
// the ambient's within about 32 (4 times that with fewer_cloud_shadow_updates) and the cloud light within 32.
[Hook(Phase.Default, Switch = nameof(Settings.LighterWarpCloudShadows))]
internal static class WarpCloudShadows
{
    private static readonly long Recent = Stopwatch.Frequency / 2;
    private static readonly ConditionalWeakTable<CloudShadowVolume, StrongBox<long>> LastRequest = new();
    private static AccessTools.FieldRef<CloudShadowVolume, bool>? _useFlip;

    public static string Apply(Harmony harmony)
    {
        _useFlip = AccessTools.FieldRefAccess<CloudShadowVolume, bool>(
            Find.Field(typeof(CloudShadowVolume), "_shadowVolumeUseFlip"));
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "MoveVolume"),
            postfix: new HarmonyMethod(typeof(WarpCloudShadows), nameof(Postfix)));
        return "on";
    }

    // __result: the move asks for a full rebuild (UpdateVolumes passes it to the shadow and the cloud lighting volumes)
    private static void Postfix(CloudShadowVolume __instance, ref bool __result)
    {
        if (!__result)
            return;
        long now = Stopwatch.GetTimestamp();
        var last = LastRequest.GetValue(__instance, _ => new StrongBox<long>(now - 2 * Recent));
        bool rebuild = now - last.Value > Recent;
        last.Value = now;
        if (rebuild)
            return;
        __result = false;
        ref bool useFlip = ref _useFlip!(__instance);
        useFlip = !useFlip;
    }
}
