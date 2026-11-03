// The world put back the same way at @time and at the checkpoints of fast mode (Checkpoints.cs): the clouds' and the
// sea's frames, the sea's waves, the kittens' eyes, so that a view depends on its place and the game time alone.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using KSA;
using KSA.Atmosphere.Rendering;

namespace KsaMetalBenchmark;

public partial class Benchmark
{
    // Each layer of a celestial's clouds moves its flow and noise on by the simulated time of every frame that draws it,
    // so the clouds stand still while the camera is elsewhere and the same game time looks different after a different
    // tour. @time puts them back to their start: from there on they look the same whatever played before.
    //
    // The detail texture's frame goes along with the camera: every frame turns it and moves it on by the way the camera
    // went, so after the jumps of fast mode the clouds at a place depend on the places before it. Fast mode starts it
    // afresh at every checkpoint once the view stands still (detailOnly): a view's clouds then depend on where it is and
    // the game time alone.
    private static void ResetClouds(bool detailOnly = false)
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        var transparencies = typeof(Program).GetField("_planetTransparenciesRenderer", Private)?.GetValue(Program.Instance)
            as PlanetTransparenciesRenderer;
        if (transparencies?.GetCloudRenderer() is not { } clouds)
        {
            Log("no clouds to reset");
            return;
        }
        var fields = new (string Name, object Value)[]
        {
            ("_noiseScroll", default(Brutal.Numerics.double3)), ("_volumetricsFlowMapPhase", 0.0),
            ("_twoDimensionalFlowMapPhase", 0.0)
        };
        // The detail texture's frame: whether it has one, its origin and its offset. Origin and offset at zero are a new
        // layer's: its first frame starts the detail texture where the camera is. KSA 2026.10.25 keeps the frame per view
        // instead of once per layer, and also keeps where the noise was at the start of each frame.
        var scrollAtStart = typeof(CloudLayerRenderData).GetField("_noiseScrollAtFrameStart", Private);
        var views = typeof(CloudLayerRenderData).GetField("_viewportData", Private);
        var viewType = views?.FieldType.GetElementType();
        var detailFrame = new (FieldInfo? Layer, FieldInfo? View, object Value)[]
        {
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameInitialized", Private),
                viewType?.GetField("DetailTextureFrameInitialized"), false),
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameOrigin", Private),
                viewType?.GetField("DetailTextureFrameOrigin"), default(Brutal.Numerics.double3)),
            (typeof(CloudLayerRenderData).GetField("_detailTextureFrameUVOffset", Private),
                viewType?.GetField("DetailTextureFrameUVOffset"), default(Brutal.Numerics.double2))
        };
        if (detailFrame.Any(f => f.Layer == null && f.View == null))
            Log("the clouds have no detail texture frame to reset");
        foreach (var layers in clouds._planetToCloudRenderData.Values)
        {
            foreach (var layer in layers)
            {
                if (!detailOnly)
                {
                    foreach (var (name, value) in fields)
                    {
                        if (typeof(CloudLayerRenderData).GetField(name, Private) is { } field)
                            field.SetValue(layer, value);
                        else
                            Log($"the clouds have no {name}");
                    }
                    scrollAtStart?.SetValue(layer, default(Brutal.Numerics.double3));
                }
                var viewData = views?.GetValue(layer) as Array;
                foreach (var (layerField, viewField, value) in detailFrame)
                {
                    layerField?.SetValue(layer, value);
                    if (viewField == null || viewData == null)
                        continue;
                    foreach (var view in viewData)
                    {
                        if (view != null)
                            viewField.SetValue(view, value);
                    }
                }
            }
        }
    }

    // Fast mode: the sea's waves at once as the wind under the camera makes them. The game eases them towards that wind
    // by 1 % a frame (paused too), so after a jump they would change for seconds, by a number of frames that differs
    // from run to run. Each frame sets the wind it aims for; the next frame starts from there.
    private static void SettleSea()
    {
        if (Program.GetOceanRenderer() is not { } ocean)
            return;
        ocean.EffectiveWindSpeed = ocean.DebugWindSpeed;
        ocean.EffectiveFetch = ocean.DebugWindFetch;
    }

    // The sea's frame goes along with the camera the same way as the clouds' detail (OceanRenderData's tangent frame,
    // whose offset moves the waves' pattern): fast mode starts it afresh at every checkpoint too, so the waves of a view
    // depend on where it is and the game time alone
    private static void ResetSeaFrame()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        if (Program.GetOceanRenderer() is not { } ocean)
            return;
        var fields = new (string Name, object Value)[]
        {
            ("_oceanFrameInitialized", false), ("_oceanFrameOrigin", default(Brutal.Numerics.double3)),
            ("_oceanFrameOffset", default(Brutal.Numerics.double2))
        };
        foreach (var data in ocean._celestialToOceanRenderData.Values)
        {
            foreach (var (name, value) in fields)
            {
                if (typeof(OceanRenderData).GetField(name, Private) is { } field)
                    field.SetValue(data, value);
                else
                    Log($"the sea has no {name}");
            }
        }
    }

    // Fast mode: kittens on EVA with their eyes open. The game blinks a kitten after a random 1 to 6 s of the Mac's
    // clock (CatEyeAnim, seeded from the clock, paused too), so a view of its face caught it blinking in some runs: its
    // blink is put past its end, with no next one
    private static void OpenEyes()
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        if (Universe.CurrentSystem is not { } system)
            return;
        foreach (var kitten in system.All.OfType<KittenEva>())
        {
            var renderable = typeof(KittenEva).GetField("_renderable", Private)?.GetValue(kitten);
            var eyes = renderable?.GetType().GetField("_catEyeAnim", Private)?.GetValue(renderable);
            var blinkT = eyes?.GetType().GetField("_blinkT", Private);
            var next = eyes?.GetType().GetField("_nextBlinkTime", Private);
            if (blinkT == null || next == null)
            {
                Log("the kittens have no blink to stop");
                return;
            }
            blinkT.SetValue(eyes, 1e6f);
            next.SetValue(eyes, float.MaxValue);
        }
    }
}
