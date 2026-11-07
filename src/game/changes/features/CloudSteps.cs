using System.Globalization;
using HarmonyLib;
using KSA.Atmosphere.Rendering;
using KSA.Atmosphere.Rendering.ShaderStructs;

namespace KsaMetal.Hooks.Features;

// Fewer Cloud Steps, fewer_cloud_steps (port-settings.toml): RaymarchCloud.comp steps through a cloud layer at a
// step that starts at the layer's step size and grows with the distance from the camera up to the layer's largest step
// (Earth's lower layer: 70 m + 0.006 x the distance, at most 1.5 km; its upper layer 110 m + 0.0044 x, at most 4.5 km).
// The first two are made Factor times as large and the largest step is kept, so the march takes fewer steps through
// clouds whose steps stay below it (the lower layer's within about 150 km of the camera, the upper layer's within about
// 650 km), and their dither, which the game's temporal upscaling averages out, stays as fine. Longer steps at the
// largest one too changed the look of the lower layer seen from orbit, for no time saved.
[Hook(Phase.Default, Switch = nameof(Settings.FewerCloudSteps))]
internal static class CloudSteps
{
    private const float Factor = 1.5f;

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(CloudLayerRenderData), "GetStaticLayerDataToWrite"),
            postfix: new HarmonyMethod(typeof(CloudSteps), nameof(Postfix)));
        return $"steps x{Factor.ToString(CultureInfo.InvariantCulture)} below the largest";
    }

    // Each layer's data as the game writes it to the GPU, when the layer is made and when its data is updated
    private static void Postfix(ref StaticLayerData __result)
    {
        __result.RaymarchingStepSize *= Factor;
        __result.RaymarchingStepSizeIncreaseFactor *= Factor;
    }
}
