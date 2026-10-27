using System.Diagnostics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// Loading screen pace. While the game loads, Loading.OnFrame draws the loading screen after every step it reports
// (Loading.Task, and the main thread's waits for parallel loading), and each frame waits for the display (the previous
// present, then a drawable): with Vsync, steps shorter than a refresh wait for one each. Frames on the main thread are
// now drawn at most every 33 ms, and a call in between returns at once, so the steps run on: on an M5 the game loads
// about 2 s faster. A new label (Loading.State) is always drawn. Other threads' calls are left to the game, which
// ignores them, so they never take the main thread's turn.
internal static class LoadingPace
{
    private static readonly long Interval = Stopwatch.Frequency * 33 / 1000;
    private static long _last;

    public static string Apply(Harmony harmony)
    {
        var state = Find.Method(typeof(Loading), nameof(Loading.State));
        var onFrame = Find.Method(typeof(Loading), nameof(Loading.OnFrame));
        harmony.Patch(state, prefix: new HarmonyMethod(typeof(LoadingPace), nameof(StatePrefix)));
        harmony.Patch(onFrame, prefix: new HarmonyMethod(typeof(LoadingPace), nameof(OnFramePrefix)));
        return "on";
    }

    private static void StatePrefix()
    {
        if (Program.IsMainThread())
            _last = 0;
    }

    private static bool OnFramePrefix()
    {
        if (!Program.IsMainThread())
            return true;
        long now = Stopwatch.GetTimestamp();
        if (now - _last < Interval)
            return false;
        _last = now;
        return true;
    }
}
