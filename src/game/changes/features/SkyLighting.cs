using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Sky lighting updates, fewer_sky_lighting_updates (port-settings.toml). Since KSA 5801 each view that draws an
// atmosphere filters its sky into a small prefiltered sky LUT every frame (AtmosphereRenderer.RenderPrefilteredSkyLut:
// 64 x 64 x 7 texels of 128 sky samples each), the light the terrain, the grass, the buildings and the vessels take
// from the sky within 10 km of the camera. It costs the same at every resolution, about 0.36 ms on an M5: 3-5 % of a
// frame at the Performance and Balanced presets. The sky it filters changes with the camera's height and the sun's
// angle: it is filtered every fourth frame, and at once for a view's new LUT, another atmosphere, or a camera that moved
// more than 1 km from where the planet's centre saw it at the last update (a cut, a jump, a vessel switch; the planet's
// turning, which moves the sun, counts too: every frame at high warp). Between updates the nearby sky light trails by
// up to three frames; with the game paused and the camera still, the image is the game's.
[Hook(Phase.Default)]
internal static class SkyLighting
{
    private const int Every = 4;
    private const double MovedMetres = 1000;

    private sealed class State
    {
        public int Slot = -1, Skipped;
        public double3 Camera;
    }

    // By the view's LUT image: a new image (a new view, a resize) is filtered at once
    private static readonly ConditionalWeakTable<object, State> States = new();
    private static FieldInfo _lut = null!, _slot = null!;

    public static string Apply(Harmony harmony)
    {
        if (!Settings.FewerSkyLightingUpdates)
            return "off";
        var resources = AccessTools.Inner(typeof(AtmosphereRenderer), "ViewportResources")
            ?? throw new Skip("AtmosphereRenderer.ViewportResources not found");
        _lut = Find.Field(resources, "PrefilteredSkyLut");
        _slot = Find.Field(resources, "CurrentPlanetSlot");
        // The callee first, then its caller (the hooks' README: patch callees before callers)
        harmony.Patch(Find.Method(typeof(AtmosphereRenderer), "RenderPrefilteredSkyLut"),
            prefix: new HarmonyMethod(typeof(SkyLighting), nameof(Prefix)));
        harmony.Patch(Find.Method(typeof(AtmosphereRenderer), nameof(AtmosphereRenderer.RenderWithCompute)),
            prefix: new HarmonyMethod(typeof(SkyLighting), nameof(PlanetPrefix)));
        return $"every {Every} frames";
    }

    // The camera's place from the centre of the atmosphere's planet, for the view's LUT filtered next (render thread)
    private static double3 _camera;
    private static bool _known;

    private static void PlanetPrefix(PlanetTransparenciesRenderer.PlanetWithTransparencyInstance planetInstance, IViewport inViewport)
    {
        var body = planetInstance.AtmosphericBody;
        _known = body != null;
        if (body != null)
            _camera = inViewport.GetCamera().PositionEcl - body.GetPositionEcl();
    }

    // inResources: the view's ViewportResources (private to the game); the atmosphere it filters is its planet slot
    private static bool Prefix(object inResources)
    {
        var lut = _lut.GetValue(inResources);
        if (lut == null)
            return true;
        var state = States.GetValue(lut, _ => new State());
        int slot = (int)_slot.GetValue(inResources)!;
        if (!_known || slot != state.Slot || state.Skipped >= Every - 1 || (_camera - state.Camera).Length() > MovedMetres)
        {
            state.Slot = slot;
            state.Camera = _camera;
            state.Skipped = 0;
            return true;
        }
        state.Skipped++;
        return false;
    }
}
