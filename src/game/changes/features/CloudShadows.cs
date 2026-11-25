using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA.Atmosphere.Rendering;

namespace KsaMetal.Hooks.Features;

// Cloud shadow tuning, fewer_cloud_shadow_updates (port-settings.toml). CloudShadowVolume.UpdateVolumeDensity
// refreshes 24 slices of the cloud shadow volume per frame (8 for ambient light); with 6 (and 2), the volume costs
// about 1.5 ms less. Since KSA 5901 CloudShadowVolume.UpdateLightingVolumes also refreshes 4 of the 128 slices of the
// cloud lighting volumes every frame (the light the clouds scatter onto the ground and vessels, and the sky they
// hide); only every fourth call goes through (a full rebuild always does; one call a frame drawn below the clouds),
// so a sweep takes 128 frames, as long as the ambient shadows'. Fewer slices a call would save less: the layers run
// one after another and each one's time is mostly how long a texel's rays take, so 1 slice costs more than half of 4.
// Since KSA 5949 a slice blends its new light into what it held, with a weight (GetLightingBlendWeight) that brings it
// 95 % of the way in 10 s when each slice comes round every 32 frames; a call that goes through stands for four, so
// its weight is the one four calls in a row would add up to, 1 - (1 - w)^4: the cloud light's blend keeps the game's
// 10 s, though each slice comes round 4 times less often. Both volumes converge to the same image; the shadows take
// about 4 times longer to follow moving clouds and the sun.
[Hook(Phase.Default, Switch = nameof(Settings.FewerCloudShadowUpdates))]
internal static class CloudShadows
{
    private const int Slices = 6, AmbientSlices = 2, LightingInterval = 4;
    private static readonly ConditionalWeakTable<CloudShadowVolume, StrongBox<int>> LightingCalls = new();
    private static AccessTools.FieldRef<CloudShadowVolume, bool> _fullRebuild = null!;

    public static string Apply(Harmony harmony)
    {
        _fullRebuild = AccessTools.FieldRefAccess<CloudShadowVolume, bool>(
            Find.Field(typeof(CloudShadowVolume), "_fullRebuild"));
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "UpdateVolumeDensity"),
            transpiler: new HarmonyMethod(typeof(CloudShadows), nameof(Transpiler)));
        harmony.Patch(Find.Method(typeof(CloudShadowVolume), "UpdateLightingVolumes"),
            prefix: new HarmonyMethod(typeof(CloudShadows), nameof(LightingPrefix)),
            transpiler: new HarmonyMethod(typeof(CloudShadows), nameof(LightingTranspiler)));
        return "slower refresh";
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

    // false skips the call: the volumes keep their content and the next slice waits for the next call that goes through
    private static bool LightingPrefix(CloudShadowVolume __instance)
    {
        if (_fullRebuild(__instance))
            return true;
        var calls = LightingCalls.GetValue(__instance, _ => new StrongBox<int>());
        return calls.Value++ % LightingInterval == 0;
    }

    // GetLightingBlendWeight(), then Spread on what it returned
    private static IEnumerable<CodeInstruction> LightingTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var weight = Find.Method(typeof(CloudShadowVolume), "GetLightingBlendWeight");
        int call = Find.Once(code, c => c.Calls(weight), "GetLightingBlendWeight's call");
        code.Insert(call + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(CloudShadows), nameof(Spread))));
        return code;
    }

    // The weight of one call that stands for LightingInterval frames: 1 stays 1 (above x100 warp the game overwrites)
    private static float Spread(float weight) => 1f - MathF.Pow(1f - weight, LightingInterval);
}
