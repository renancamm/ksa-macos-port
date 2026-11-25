using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection.Emit;
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
// the ambient's and the cloud light within about 32 (each 4 times that with fewer_cloud_shadow_updates). Since KSA 5949
// the cloud lighting volumes blend each slice's new light into what it held (fully only above x100 warp), which would
// leave another place's light fading for about 10 s: for one sweep of their 128 slices after the last skipped
// rebuild, each slice is written whole (weight 1), as a rebuild would have.
[Hook(Phase.Default, Switch = nameof(Settings.LighterWarpCloudShadows))]
internal static class WarpCloudShadows
{
    private static readonly long Recent = Stopwatch.Frequency / 2;
    private static readonly ConditionalWeakTable<CloudShadowVolume, StrongBox<long>> LastRequest = new();
    // The cloud lighting volumes' slices still to write whole since the last skipped rebuild
    private static readonly ConditionalWeakTable<CloudShadowVolume, StrongBox<int>> Sweep = new();
    private const int LightingSlices = 128, SlicesPerCall = 4;
    private static AccessTools.FieldRef<CloudShadowVolume, bool>? _useFlip;

    public static string Apply(Harmony harmony)
    {
        _useFlip = AccessTools.FieldRefAccess<CloudShadowVolume, bool>(
            Find.Field(typeof(CloudShadowVolume), "_shadowVolumeUseFlip"));
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "MoveVolume"),
            postfix: new HarmonyMethod(typeof(WarpCloudShadows), nameof(Postfix)));
        // Without it another place's cloud light fades for about 10 s after warp
        Hooks.Part("Lighter Warp Cloud Shadows' cloud light sweep", () => harmony.Patch(
            Find.Method(typeof(CloudShadowVolume), "UpdateLightingVolumes"),
            transpiler: new HarmonyMethod(typeof(WarpCloudShadows), nameof(LightingTranspiler))));
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
        {
            if (Sweep.TryGetValue(__instance, out var sweep))
                sweep.Value = 0;  // the rebuild writes every slice whole
            return;
        }
        __result = false;
        ref bool useFlip = ref _useFlip!(__instance);
        useFlip = !useFlip;
        Sweep.GetValue(__instance, _ => new StrongBox<int>()).Value = LightingSlices;
    }

    // GetLightingBlendWeight(), then Whole on what it returned and the volume (a full rebuild doesn't call it)
    private static IEnumerable<CodeInstruction> LightingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var weight = Find.Method(typeof(CloudShadowVolume), "GetLightingBlendWeight");
        int call = Find.Once(code, c => c.Calls(weight), "GetLightingBlendWeight's call");
        code.InsertRange(call + 1, [
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(WarpCloudShadows), nameof(Whole))),
        ]);
        return code;
    }

    // 1 while the sweep after a skipped rebuild lasts (the call writes SlicesPerCall slices), else the game's weight
    private static float Whole(float weight, CloudShadowVolume volume)
    {
        if (!Sweep.TryGetValue(volume, out var left) || left.Value <= 0)
            return weight;
        left.Value -= SlicesPerCall;
        return 1f;
    }
}
