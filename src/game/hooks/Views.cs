using KSA;

namespace KsaMetal.Hooks;

// The views the game prepares terrain and clutter for (PlanetRenderer.OnPreRender): the main view and the visible
// secondary ones, those that draw terrain unless told otherwise. The hooks that follow the views' cameras (the far
// terrain, the far clutter, the far textures, the clutter fix) count them the same way.
internal static class Views
{
    public static bool Prepared(IViewport view, bool terrain = true) =>
        view.IsMain() || view.Type == ViewportType.Secondary && view.Visible && (!terrain || view.HasAny(ViewportOptionFlags.RenderTerrain));

    // Behind a map view, the flight camera it returns to, whose place still counts; null for any other view
    public static Camera? Flight(IViewport view, Camera camera) =>
        view is GameViewport { Mode: CameraMode.Map } game && game.BaseCamera != camera ? game.BaseCamera : null;
}
