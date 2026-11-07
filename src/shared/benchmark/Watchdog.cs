// When a game counts as hung or stuck, the same for a run from benchmark.sh (its program watches the game from outside)
// and one from the Mac Port tab (a thread of the game's own watches it). Hung: the director's heartbeat event, written
// every second while it plays (Events.cs), is older than the limit. Stuck: the heartbeats still come, but the script has
// stood at one moment for longer than the limit with nothing done there (no screenshot, checkpoint or part begun or
// ended): the game's clock stopped, so a checkpoint waits for ever. Before the first heartbeat (the game loading) nothing
// is hung or stuck.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace KsaPort.Common.Benchmark;

public static class Watchdog
{
    // A minute; three when time warp can stall a frame for most of a minute (a timed scenario), or a tool holds a
    // checkpoint for minutes (a frame capture, a pass dump)
    public static int Limit(bool slowFrames) => slowFrames ? 180 : 60;

    // The session's last heartbeat when it is older than the limit, else null
    public static RunEvent? Hung(IReadOnlyList<RunEvent> events, int session, double limit) =>
        EventLog.LastHeartbeat(events, session) is { } beat && (DateTime.UtcNow - beat.At).TotalSeconds > limit ? beat : null;

    // The first of the session's last heartbeats that all say the same moment of one scenario, with nothing done since
    // it, when they span more than the limit, else null
    public static RunEvent? Stuck(IReadOnlyList<RunEvent> events, int session, double limit)
    {
        RunEvent? last = null, first = null;
        for (int i = events.Count - 1; i >= 0; i--)
        {
            var e = events[i];
            if (e.Session != session)
                continue;
            if (e.Type is "shot" or "checkpoint" or "open" or "close" or "scenario")
                break;
            if (e.Type != "heartbeat")
                continue;
            if (last == null)
            {
                if (e.Scenario == null)
                    return null;
                last = e;
            }
            else if (e.Scenario != last.Scenario || e.Pass != last.Pass || e.Value != last.Value)
                break;
            first = e;
        }
        return last != null && first != null && (last.At - first.At).TotalSeconds > limit ? first : null;
    }

    // Where a heartbeat says the game was: " in <scenario> at <seconds> s", or nothing before the first scenario
    public static string Where(RunEvent beat) =>
        beat.Scenario != null && beat.Value is { } at ? string.Create(CultureInfo.InvariantCulture, $" in {beat.Scenario} at {at:0.0} s") : "";
}
