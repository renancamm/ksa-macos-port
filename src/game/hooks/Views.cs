using System.Collections.Generic;
using Brutal.Numerics;
using KSA;

namespace KsaMetal.Hooks;

// The views the game prepares (PlanetRenderer.OnPreRender): the main view and the visible secondary ones, which the
// hooks that follow the views' cameras (the far textures) count the same way. And a camera's place in a body's frame,
// which the hooks that carry motion from one frame to the next share.
internal static class Views
{
    public static bool Prepared(IViewport view) => view.IsMain() || view.Type == ViewportType.Secondary && view.Visible;

    // Behind a map view, the flight camera it returns to, whose place still counts; null for any other view
    public static Camera? Flight(IViewport view, Camera camera) =>
        view is GameViewport { Mode: CameraMode.Map } game && game.BaseCamera != camera ? game.BaseCamera : null;

    // The prepared views' cameras, and behind a map the flight camera it returns to
    public static void Cameras(List<Camera> cameras)
    {
        cameras.Clear();
        foreach (var view in ViewportRegistry.Views)
        {
            if (!Prepared(view) || view.GetCamera() is not { } camera)
                continue;
            cameras.Add(camera);
            if (Flight(view, camera) is { } flight)
                cameras.Add(flight);
        }
    }

    // How far a camera is above a body's mean surface
    public static double SurfaceDistance(Camera camera, Celestial body) =>
        (camera.PositionEcl - body.GetPositionEcl()).Length() - body.MeanRadius;

    // Camera-centred ecliptic coordinates -> the nearby body's rotating frame, the frame its ground and the gas released
    // over it stand still in (or the ecliptic frame in space)
    public static double4x4 EgoToFrame(Camera camera, Celestial? body) =>
        body != null
            ? double4x4.CreateTranslation(camera.PositionEcl - body.GetPositionEcl()) * double4x4.CreateFromQuaternion(body.GetCce2Ccf())
            : double4x4.CreateTranslation(camera.PositionEcl);
}
