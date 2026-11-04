// What happened during a run, in order (events.jsonl in its folder, a line each): what benchmark.sh or the Mac Port tab
// did around the game and what the director did in it. Each writer appends whole lines; Finish.cs reads them all.
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
//   gpu        the GPU was busy before the run: Value its utilisation in percent
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    public const string FileName = "events.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _path;
    private readonly object _lock = new();

    public int Session { get; set; }
    public int Pass { get; set; } = 1;

    public EventLog(string dir, int session = 0)
    {
        _path = Path.Combine(dir, FileName);
        Session = session;
    }

    // Adds an event as a line of its own, at once, so that a game that dies keeps the ones before
    public void Add(string type, string? scenario = null, string? part = null, string? text = null, double? game = null, double? value = null)
    {
        var e = new RunEvent { Type = type, Session = Session, Pass = Pass, Scenario = scenario, Part = part, Text = text, Game = game, Value = value };
        var line = JsonSerializer.Serialize(e, Json) + "\n";
        lock (_lock)
            File.AppendAllText(_path, line);
    }

    // Every event of a run's folder; a line cut short (a game that died while writing it) is left out
    public static List<RunEvent> Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        var events = new List<RunEvent>();
        if (!File.Exists(path))
            return events;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (line.Length > 0 && JsonSerializer.Deserialize<RunEvent>(line, Json) is { } e)
                    events.Add(e);
            }
            catch (JsonException)
            {
            }
        }
        return events;
    }

    // The last heartbeat of a session, or null: where its script was, and when
    public static RunEvent? LastHeartbeat(string dir, int session) =>
        Read(dir).LastOrDefault(e => e.Session == session && e.Type is "heartbeat" or "loaded" or "scenario");
}
