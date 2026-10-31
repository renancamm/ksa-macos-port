using System;
using System.Diagnostics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Crew portraits. The faces in the crew panel are two 128x128 views of their own (the kitten and the vessel's parts
// around it, 0.4 to 0.8 ms a frame), drawn every frame while the controlled vessel has crew, also when the panel isn't
// on screen (UI hidden, map view). After CrewPortraitPanel.Update has chosen the portraits, the patch hides a
// portrait's view for this frame unless the panel was drawn in the previous one, the way the game hides it when
// portraits are off; the panel keeps showing the view's last image (skip_hidden_crew_portraits). With
// crew_portraits_at_15_fps, the portraits are also refreshed at most 15 times a second. With both off, the game draws
// them as it does without the patch. The Mac Port tab changes the settings while the game runs.
internal static class CrewPortraits
{
    private static long _frame, _drawnFrame = -2, _shownSince = -1;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double _lastRender = double.NegativeInfinity;
    private static bool _failed;  // the game draws the portraits as without the patch (Hooks.Fail)

    public static string Apply(Harmony harmony)
    {
        var update = Find.Method(typeof(CrewPortraitPanel), nameof(CrewPortraitPanel.Update));
        var draw = Find.Method(typeof(CrewPortraitPanel), nameof(CrewPortraitPanel.Draw));
        Find.Method(typeof(Program), nameof(Program.GetCrewPortraitViewport));
        harmony.Patch(update, postfix: new HarmonyMethod(typeof(CrewPortraits), nameof(UpdatePostfix)));
        harmony.Patch(draw, postfix: new HarmonyMethod(typeof(CrewPortraits), nameof(DrawPostfix)));
        return $"on, {(Settings.SkipHiddenCrewPortraits ? "when shown" : "every frame")}{(Settings.CrewPortraitsAt15Fps ? ", 15 a second" : "")}";
    }

    private static void DrawPostfix() => _drawnFrame = _frame;

    private const int ShownFps = 15;

    private static void UpdatePostfix()
    {
        _frame++;
        bool skip = Settings.SkipHiddenCrewPortraits, slow = Settings.CrewPortraitsAt15Fps;
        if (_failed || !skip && !slow)
            return;
        bool shown = _drawnFrame >= _frame - 1;
        if (!shown)
            _shownSince = -1;
        else if (_shownSince < 0)
            _shownSince = _frame;
        bool render = shown || !skip;
        if (render && slow && _frame > _shownSince + 1)
        {
            double now = Clock.Elapsed.TotalSeconds;
            render = now - _lastRender >= 1.0 / ShownFps;
        }
        if (render)
        {
            _lastRender = Clock.Elapsed.TotalSeconds;
            return;
        }
        try
        {
            for (int i = 0; i < CrewPortraitPanel.MAX_VISIBLE; i++)
                Program.GetCrewPortraitViewport(i).SetVisible(false);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail("crew_portraits", e);
        }
    }
}
