// The world as a view standing still needs it: the sea's waves settled at once. The clouds and the sea also change with
// where the camera has been (the game keeps their frames from its start): a test started in a game of its own, going from
// view to view in the same order, looks the same every run; a run in a game already playing, or of other places, doesn't
// have to.
using KSA;

namespace KsaMetal.Benchmark;

public partial class Director
{
    // Views standing still: the sea's waves at once as the wind under the camera makes them. The game eases them towards
    // that wind by 1 % a frame (paused too), so after a jump they would change for seconds, by a number of frames that
    // differs from run to run. Each frame sets the wind it aims for; the next frame starts from there.
    private static void SettleSea()
    {
        if (Program.GetOceanRenderer() is not { } ocean)
            return;
        ocean.EffectiveWindSpeed = ocean.DebugWindSpeed;
        ocean.EffectiveFetch = ocean.DebugWindFetch;
    }
}
