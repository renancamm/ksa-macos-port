// What happened during a run, in order (the events table of the run's store, Store.cs): what benchmark.sh or the Mac Port
// tab did around the game and what the director did in it. Each process writes its own file (the game run.db, benchmark.sh
// host.db); reading a run's events reads both, in time order. benchmark.sh watches the game's heartbeats there, and the
// game benchmark.sh's recording event.
//
//   start      the run began: Text how (a new game from benchmark.sh, the tab's restart, the running game)
//   session    a game session began (Session counts from 1; Pass, on every event, which time the scenarios play in it)
//   loaded     the first frame in the game: Value the seconds since the game started
//   heartbeat  the director is alive, every second: Scenario and Value where the script is (a game that stops writing
//              them has hung)
//   scenario   a scenario began: Scenario, Text its kind
//   open       a part's measuring began: Scenario, Part, Game the game's clock then, Text the vehicle followed
//   close      a part's measuring ended: Scenario, Part
//   shot       a screenshot was taken: Scenario, Part the screenshot's name, Text its file, Value its limit
//   checkpoint the game's clock landed at a checkpoint or @sync: Scenario, Part, Game, Value the script's game time
//   error      a step went wrong: Scenario, Text
//   note       worth knowing, not wrong: Text
//   drawable   the presented images' size: Text
//   done       the script played to its end
//   stopped    the run was stopped: Text why (Esc, Ctrl-C)
//   ended      a game session ended: Session, Text how ("quit normally", "crashed (signal 6) in mixed at 1.5 s"...)
//   alongside  another game ran during the session: Session
//   memory     macOS's memory pressure at its worst and the swap used before and after: Text
//   session-memory  the same for one game session: Session, Text
//   gpu        the GPU was busy before the run: Value its utilisation in percent
//   trigger    a trigger's rule ran (Triggers.cs): Scenario, Part (a part, or a checkpoint's screenshot), Text the rule
//              and why
//   disturbed  a trigger's heavy work went on while a part measured, which leaves the numbers: Scenario, Part, Text
using System;
using System.Collections.Generic;
using System.Linq;

namespace KsaPort.Common.Benchmark;

public sealed record RunEvent
{
    public DateTime At { get; init; } = DateTime.UtcNow;
    public string Type { get; init; } = "";
    public int Session { get; init; }
    public int Pass { get; init; } = 1;
    public string? Scenario { get; init; }
    public string? Part { get; init; }
    public string? Text { get; init; }
    public double? Game { get; init; }
    public double? Value { get; init; }
}

public sealed class EventLog
{
    private readonly string _dir;

    public int Session { get; set; }
    public int Pass { get; set; } = 1;

    public EventLog(string dir, int session = 0)
    {
        _dir = dir;
        Session = session;
    }

    // Adds an event to this process's file of the run's store, which writes it at once, so that a game that dies keeps the
    // ones before
    public void Add(string type, string? scenario = null, string? part = null, string? text = null, double? game = null, double? value = null) =>
        RunStore.For(_dir)?.Add(new RunEvent { Type = type, Session = Session, Pass = Pass, Scenario = scenario, Part = part, Text = text, Game = game, Value = value });

    // Every event of a run's folder, both processes' in time order; what this process gave its store is written first
    public static List<RunEvent> Read(string dir)
    {
        RunStore.Flush(dir);
        return RunStore.ReadEvents(dir);
    }

    // The last heartbeat of a session, or null: where its script was, and when
    public static RunEvent? LastHeartbeat(string dir, int session) => LastHeartbeat(Read(dir), session);

    public static RunEvent? LastHeartbeat(IEnumerable<RunEvent> events, int session) =>
        events.LastOrDefault(e => e.Session == session && e.Type is "heartbeat" or "loaded" or "scenario");
}
