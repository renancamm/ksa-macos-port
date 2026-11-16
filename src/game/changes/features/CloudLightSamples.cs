using HarmonyLib;
using KSA.Atmosphere.Rendering;
using KSA.Atmosphere.Rendering.ShaderStructs;

namespace KsaMetal.Hooks.Features;

// Fewer Cloud Light Samples, fewer_cloud_light_samples (port-settings.toml): at each step of RaymarchCloud.comp's march
// through a cloud, GetDensityToLight adds up the cloud toward the sun: the layer's light samples a light step apart
// (Earth's lower layer 4 over 1.2 km, its upper layer 2 over 1.4 km; the step shorter near the layer's top), the first
// one dithered, then the cloud shadow volume from one step past the last sample. The samples are a large share of the
// march's time. Where the layer's spare padding0 is 1, which a postfix on
// CloudLayerRenderData.GetStaticLayerDataToWrite sets, the port's shader edit (cloud-light-samples.edits) takes one
// sample at the same light step: the near cloud's own shading and the step before the volume stay, so the clouds keep
// their brightness, and the cloud the other samples covered comes from the shadow volume, which is coarser. Fewer
// samples over the same distance left a longer gap before the volume: brighter, flatter clouds.
[Hook(Phase.Default, Switch = nameof(Settings.FewerCloudLightSamples))]
internal static class CloudLightSamples
{
    private static AccessTools.StructFieldRef<StaticLayerData, int> _flag = null!;

    public static string Apply(Harmony harmony)
    {
        var field = Find.Field(typeof(StaticLayerData), "padding0");
        if (field.FieldType != typeof(int))
            throw new Skip("StaticLayerData.padding0 not an int");
        _flag = AccessTools.StructFieldRefAccess<StaticLayerData, int>(field);
        harmony.Patch(Find.Method(typeof(CloudLayerRenderData), "GetStaticLayerDataToWrite"),
            postfix: new HarmonyMethod(typeof(CloudLightSamples), nameof(Postfix)));
        return "one light sample at the layer's light step";
    }

    // Each layer's data as the game writes it to the GPU, when the layer is made and when its data is updated
    private static void Postfix(ref StaticLayerData __result) => _flag(ref __result) = 1;
}
