using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsaPort.Common.Benchmark;

// What one play of the scenarios is for: a warm-up (its numbers kept apart: the Mac's heat, the caches), measured (the
// run's numbers, the only ones compared), or profiled (the game's GPU zones on, KSA Metal splitting render passes at
// them: named zones, at a cost of their own; its numbers kept apart)
public enum PassKind { WarmUp, Measured, Profiled }

// A run's plan (plan.json in its folder): what plays, its passes, what it records besides what every run records, what
// reacts, and what its host says about it. benchmark.sh's program writes it for each game session, or the hooks for a
// run from the Mac Port tab, and the game gets the folder as KSA_BENCH_OUT; the director and the hooks read it there
// when the game starts, and Finish.cs when the run ends. A plan never changes a setting: settings are the run's.
public sealed class RunPlan
{
    public const string FileName = "plan.json";

    public PlayPlan Play { get; set; } = new();
    // The passes, in the order they play in each game session: benchmark.sh measure's a warm-up and three measured, profile's
    // a warm-up, a measured and a profiled one
    public List<PassKind> Passes { get; set; } = [PassKind.Measured];
    public RecordPlan Record { get; set; } = new();
    // The run's own triggers, as written ("frame>50ms: moment", Triggers.cs), and the defaults after them
    public List<string> On { get; set; } = [];
    // A clean run's folder, which the broken trigger holds each screenshot against: one under half the size of that run's
    // same screenshot, or with far more pure black, looks broken
    public string Against { get; set; } = "";
    public HostPlan Host { get; set; } = new();

    // The run's folder: where run.json is, and the results and screenshots go
    [JsonIgnore]
    public string Out { get; set; } = "";

    // A pass's number in a session's events: from 0 when the first pass is the warm-up, else from 1 (a run of before plans
    // numbered its warm-up 0 and its measured passes from 1)
    public int Number(int index) => Passes.FirstOrDefault() == PassKind.WarmUp ? index : index + 1;

    // The tool a run with a profiled pass names in its facts
    public const string ProfiledTool = "the game's GPU zones in a profiled pass";

    // A pass kind as the pass event and the record name it: warm-up, measured, profiled
    public static string Name(PassKind kind) => kind switch { PassKind.WarmUp => "warm-up", PassKind.Profiled => "profiled", _ => "measured" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public void Write(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json));
        Out = folder;
    }

    // The plan in a run's folder, or one made from the options.json of a run of before plans (report --again finishes
    // such a run from its store); null when it holds neither or they can't be read
    public static RunPlan? Read(string folder)
    {
        try
        {
            var path = Path.Combine(folder, FileName);
            var plan = File.Exists(path) ? JsonSerializer.Deserialize<RunPlan>(File.ReadAllText(path), Json) : FromOptions(folder);
            if (plan != null)
                plan.Out = folder;
            return plan;
        }
        catch (System.Exception e) when (e is IOException or JsonException or System.InvalidOperationException)
        {
            return null;
        }
    }

    // A run's options.json (its one flat list, warmUp and repeat for its passes) as a plan
    private static RunPlan? FromOptions(string folder)
    {
        var path = Path.Combine(folder, "options.json");
        if (!File.Exists(path) || System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path)) is not System.Text.Json.Nodes.JsonObject o)
            return null;
        System.Text.Json.Nodes.JsonObject Pick(params string[] keys)
        {
            var picked = new System.Text.Json.Nodes.JsonObject();
            foreach (var k in keys)
                if (o[k] is { } v)
                    picked[k] = v.DeepClone();
            return picked;
        }
        var plan = new System.Text.Json.Nodes.JsonObject
        {
            ["play"] = Pick("script", "only", "fast", "mode", "test", "noShots", "hold", "here", "explore", "stay"),
            ["record"] = Pick("zoneLabels", "capture", "film"),
            ["host"] = Pick("session", "noiseFolder", "app", "facts", "inGame", "restarted", "waitForRecording"),
        };
        foreach (var k in new[] { "on", "against" })
            if (o[k] is { } v)
                plan[k] = v.DeepClone();
        var passes = new System.Text.Json.Nodes.JsonArray();
        if (o["warmUp"]?.GetValue<bool>() == true)
            passes.Add("warm-up");
        for (int i = 0; i < System.Math.Clamp(o["repeat"]?.GetValue<int>() ?? 1, 1, 100); i++)
            passes.Add("measured");
        plan["passes"] = passes;
        return JsonSerializer.Deserialize<RunPlan>(plan, Json);
    }
}

// What plays, and how
public sealed class PlayPlan
{
    // The script (benchmark.txt)
    public string Script { get; set; } = "";
    // What to play (space separated): sections, or places as <section>_<shot> (the section up to that checkpoint, its
    // other checkpoints only keeping the clocks in step); empty: all of it
    public string Only { get; set; } = "";
    // The places chosen jumped to, the game paused on the way (a flight's), instead of played: for a close look at a place
    public bool Fast { get; set; }
    // How it plays, for its report: the kind of its scenarios (views, flight, timed, several for full), "jump" with Fast
    public string Mode { get; set; } = "";
    // The test it plays (mixed, tour, warp, full), for its report
    public string Test { get; set; } = "";
    // No screenshots (the checkpoints still keep the game's clock in step)
    public bool NoShots { get; set; }
    // <section>_<shot>: stop at that checkpoint for good, the game paused, until it is quit
    public string Hold { get; set; } = "";
    // No script: the player's own game recorded as it plays for that many seconds, one part named here, from the Mac Port
    // tab (Director's here mode); nothing of the game is changed
    public double Here { get; set; }
    // No script: the explore mode's console command instead (Explore.cs)
    public bool Explore { get; set; }
    // The game goes on when the script ends (Done), instead of quitting
    public bool Stay { get; set; }
}

// What the run records besides what every run does (its frames and events, KSA Metal's drain, the game's profilers, the
// runtime's counters): the tools'
public sealed class RecordPlan
{
    // Encoders named after the game's GPU zones, for a trace or a frame capture: its GPU profiler on, KSA Metal answering
    // its timestamps at once (the game needs KSA_MTL_LABELS=1)
    public bool ZoneLabels { get; set; }
    // An Xcode GPU frame capture of the view at every checkpoint, <section>_<shot>.gputrace (the game needs
    // MTL_CAPTURE_ENABLED=1)
    public bool Capture { get; set; }
    // A screenshot every that many seconds of the script's clock, into film/, to see moves
    public double Film { get; set; }
    // The camera's place every frame into camera-log.csv, to check its moves are smooth
    public bool CameraLog { get; set; }
}

// What the run's host says: the game session, where noise profiles are, the app, and the facts for the record
public sealed class HostPlan
{
    // Which game session of the run this is, from 1
    public int Session { get; set; } = 1;
    // A folder whose noise/ holds profiles that a test's own (in the app, next to the lineup) gives way to: the benchmarks
    // folder, where benchmark.sh calibrate writes them
    public string NoiseFolder { get; set; } = "";
    // The app played, and what the host says about the run for its record (the test, the settings, the tools...)
    public string App { get; set; } = "";
    public List<Fact> Facts { get; set; } = [];
    // The run plays in a game that was running already (no loading time)
    public bool InGame { get; set; }
    // The Mac Port tab's "Restart for a Clean Run": the game started again for it, and quits at the end
    public bool Restarted { get; set; }
    // Wait in the game before playing (up to a minute) for the "recording" event, so that a tool started once the game is
    // up (an Instruments trace) sees the whole run
    public bool WaitForRecording { get; set; }
}
