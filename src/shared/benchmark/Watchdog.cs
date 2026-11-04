// When a game counts as hung, the same for a run from benchmark.sh (its program watches the game from outside) and one
// from the Mac Port tab (a thread of the game's own watches it): the director's heartbeat event, written every second
// while it plays (Events.cs), is older than the limit. Before the first heartbeat (the game loading) nothing is hung.
using System;
using System.Globalization;

namespace KsaPort.Common.Benchmark;

public static class Watchdog
{
    // A minute without a heartbeat; three when time warp can stall a frame for most of a minute (a timed scenario), or a
    // tool holds a checkpoint for minutes (a frame capture, a pass dump)
    public static int Limit(bool slowFrames) => slowFrames ? 180 : 60;

    // The session's last heartbeat when it is older than the limit, else null
    public static RunEvent? Hung(string dir, int session, double limit) =>
        EventLog.LastHeartbeat(dir, session) is { } beat && (DateTime.UtcNow - beat.At).TotalSeconds > limit ? beat : null;

    // Where a heartbeat says the game was: " in <scenario> at <seconds> s", or nothing before the first scenario
    public static string Where(RunEvent beat) =>
        beat.Scenario != null && beat.Value is { } at ? string.Create(CultureInfo.InvariantCulture, $" in {beat.Scenario} at {at:0.0} s") : "";
}
