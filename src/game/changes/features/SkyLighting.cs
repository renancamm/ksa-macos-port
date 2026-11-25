using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Features;

// Sky lighting updates, fewer_sky_lighting_updates (port-settings.toml). Since KSA 5801 each view that draws an
// atmosphere filters its sky into a small prefiltered sky LUT every frame (AtmosphereRenderer.RenderPrefilteredSkyLut:
// 64 x 64 x 6 texels, the wide lobes summed over a 24 x 32 grid of the sky, the narrowest, and every lobe above the
// atmosphere, of 128 samples), the light the terrain, the grass, the buildings and the vessels take from the sky within
// 10 km of the camera, and the ocean's sky reflection at any distance. It costs the same at every resolution. The sky
// it filters changes with the camera's height and the sun's angle: it is filtered every fourth frame, and at once for a
// view's new LUT, another atmosphere, or a camera that moved more than 1 km from where the planet's centre saw it at the
// last update (a cut, a jump, a vessel switch; the planet's turning, which moves the sun, counts too: every frame at high
// warp). Between updates the sky light and the ocean's sky reflection trail by up to three frames; with the game paused
// and the camera still, the image is the game's. On an M5, five games a side at the Balanced preset, it saves about
// 0.2 ms, 1 % of the frame in the Mixed Test's kitten and cockpit views (the others within noise).
[Hook(Phase.Default, Switch = nameof(Settings.FewerSkyLightingUpdates))]
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
        var resources = AccessTools.Inner(typeof(AtmosphereRenderer), "ViewportResources")
            ?? throw new Skip("AtmosphereRenderer.ViewportResources not found");
        _lut = Find.Field(resources, "PrefilteredSkyLut");
        _slot = Find.Field(resources, "CurrentPlanetSlot", typeof(int));
        // The callee first, then its caller: a patch reaches only code compiled after it (Phase.BeforeCallers)
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
