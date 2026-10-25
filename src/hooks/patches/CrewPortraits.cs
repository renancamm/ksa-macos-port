using System.Diagnostics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Crew portraits. The faces in the crew panel are two 128x128 views of their own (the kitten and the vessel's parts
// around it, 0.4 to 0.8 ms a frame), drawn every frame while the controlled vessel has crew, also when the panel isn't
// on screen (UI hidden, map view). After CrewPortraitPanel.Update has chosen the portraits, the patch hides a
// portrait's view for this frame unless the panel was drawn in the previous one, the way the game hides it when
// portraits are off; the panel keeps showing the view's last image. With KSA_PORTRAIT_FPS, a drawn panel is also
// refreshed at most that many times a second.
internal static class CrewPortraits
{
    private static long _frame, _drawnFrame = -2, _shownSince = -1;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastRender = double.NegativeInfinity;

    public static string Apply(Harmony harmony)
    {
        var update = Find.Method(typeof(CrewPortraitPanel), nameof(CrewPortraitPanel.Update));
        var draw = Find.Method(typeof(CrewPortraitPanel), nameof(CrewPortraitPanel.Draw));
        Find.Method(typeof(Program), nameof(Program.GetCrewPortraitViewport));
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(CrewPortraits), nameof(UpdatePostfix)));
        harmony.Patch(draw, postfix: new HarmonyMethod(typeof(CrewPortraits), nameof(DrawPostfix)));
        return Settings.PortraitFps > 0 ? $"on, at most {Settings.PortraitFps} per second" : "on";
    }

    private static void DrawPostfix() => _drawnFrame = _frame;

    private static void UpdatePostfix()
    {
        _frame++;
        bool shown = _drawnFrame >= _frame - 1;
        if (!shown)
            _shownSince = -1;
        else if (_shownSince < 0)
            _shownSince = _frame;
        bool render = shown;
        if (render && Settings.PortraitFps > 0 && _frame > _shownSince + 1)
        {
            double now = Clock.Elapsed.TotalSeconds;
            render = now - _lastRender >= 1.0 / Settings.PortraitFps;
        }
        if (render)
        {
            _lastRender = Clock.Elapsed.TotalSeconds;
            return;
        }
        for (int i = 0; i < CrewPortraitPanel.MAX_VISIBLE; i++)
            Program.GetCrewPortraitViewport(i).SetVisible(false);
    }
}
