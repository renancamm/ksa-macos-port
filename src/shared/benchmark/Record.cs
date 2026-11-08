// A run's record (run.json in its folder): everything its report, page, comparison and zip say, in one typed file.
// Finish.cs makes it from what the run left (Events.cs, Frames.cs, the game's logs, the screenshots); nothing else
// writes it, and everything that shows a run reads it. A record of an older schema isn't read: its run is too old to
// compare.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KsaPort.Common.Benchmark;

public enum Status { Passed, Failed, Stopped }

public enum Severity { Fail, Note }

// What a check found: a failure makes the run fail; a note is a condition worth knowing (its numbers may not compare)
public sealed record Finding(Severity Severity, string Code, string Text);

// A fact about the Mac or the run, in the order the report lists them
public sealed record Fact(string Key, string Value);

// GPU ms a frame over the measured parts, weighted by their length as the headline is: in the warm-up pass, and in
// the measured passes of the same game
public sealed record WarmUpRecord(double? GpuMs, double? MeasuredGpuMs);

public sealed class RunRecord
{
    public const int CurrentSchema = 4;
    public const string FileName = "run.json";

    // CurrentSchema in a record Finish made (an older run's run.json, its options, reads as 0)
    public int Schema { get; set; }
    // The folder's name, the date and time it started
    public string Name { get; set; } = "";
    public DateTime Started { get; set; }
    // The test (mixed, tour, warp, full; "places" for places chosen) and how it played (RunPlan.Play.Mode)
    public string Test { get; set; } = "";
    public string Mode { get; set; } = "";
    public Status Status { get; set; }
    public List<Finding> Findings { get; set; } = [];
    // This Mac, the app, the settings, the tools
    public List<Fact> Facts { get; set; } = [];
    // How the run started: benchmark.sh's own game, the tab's restart, or a game already playing
    public string Start { get; set; } = "";
    // The game sessions it played, and how many times each scenario played in them
    public int Sessions { get; set; } = 1;
    public int Passes { get; set; } = 1;
    public double? LoadSeconds { get; set; }
    // The size in pixels of the images the game presented
    public string? Drawable { get; set; }
    public List<ScenarioRecord> Scenarios { get; set; } = [];
    // benchmark.sh measure's warm-up pass, left out of the numbers: its GPU time and the measured passes' in the same
    // game
    public WarmUpRecord? WarmUp { get; set; }
    // The slow frames a trigger kept (by default a measured part's frames of 100 ms or more), with what took their time
    public List<MomentRecord> Moments { get; set; } = [];
    // The metrics the run's probes registered (their values are in the parts'), and who held the memory at each checkpoint
    // of the first pass that took a census (the memory probe)
    public List<Metric> ProbeMetrics { get; set; } = [];
    public List<CensusRecord> Census { get; set; } = [];

    [JsonIgnore]
    public bool InGame => Start.StartsWith("in the running game", StringComparison.Ordinal);

    [JsonIgnore]
    public IEnumerable<Finding> Problems => Findings.Where(f => f.Severity == Severity.Fail);

    [JsonIgnore]
    public IEnumerable<Finding> Notes => Findings.Where(f => f.Severity == Severity.Note);

    public string? Fact(string key) => Facts.FirstOrDefault(f => f.Key == key)?.Value;

    public IEnumerable<(ScenarioRecord Scenario, PartRecord Part)> AllParts() =>
        Scenarios.SelectMany(s => s.Parts.Select(p => (s, p)));

    public ScenarioRecord? Scenario(string name) => Scenarios.FirstOrDefault(s => s.Name == name);

    // The main numbers: frame rate and GPU time over the measured parts (time warp's aside), weighted by their length;
    // the slowest part; the game days in time warp; memory at its peak
    public Headline Headline()
    {
        double frames = 0, seconds = 0, gpuWeighted = 0, gpuSeconds = 0, memory = 0, slowest = double.MaxValue, days = 0;
        (ScenarioRecord, PartRecord)? worst = null;
        foreach (var (s, p) in AllParts())
        {
            memory = Math.Max(memory, p[Metrics.Footprint] ?? 0);
            if (p.Warped)
            {
                days += p[Metrics.GameDays] ?? 0;
                continue;
            }
            if (p.Seconds <= 0 || p[Metrics.Fps] is not { } fps)
                continue;
            frames += fps * p.Seconds;
            seconds += p.Seconds;
            if (p[Metrics.GpuBusy] is { } gpu)
            {
                gpuWeighted += gpu * p.Seconds;
                gpuSeconds += p.Seconds;
            }
            if (fps < slowest)
                (slowest, worst) = (fps, (s, p));
        }
        return new Headline(seconds > 0 ? frames / seconds : null, gpuSeconds > 0 ? gpuWeighted / gpuSeconds : null, worst, days, memory);
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public void Write(string dir)
    {
        var path = Path.Combine(dir, FileName);
        File.WriteAllText(path + ".new", JsonSerializer.Serialize(this, Json));
        File.Move(path + ".new", path, overwrite: true);
    }

    // The record in a run's folder; null when it has none of this schema
    public static RunRecord? Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            if (!File.Exists(path) || JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), Json) is not { Schema: CurrentSchema } record)
                return null;
            // Its probes' metrics, judged as the others wherever it is compared
            foreach (var m in record.ProbeMetrics)
                Metrics.Register(m);
            return record;
        }
        catch (JsonException)
        {
            // An older run's run.json, its options or a record of another schema
            return null;
        }
    }
}

public sealed record Headline(double? Fps, double? GpuMs, (ScenarioRecord Scenario, PartRecord Part)? Slowest, double WarpDays, double MemoryMb)
{
    // In a few words, as the Mac Port tab shows it: "30 FPS", "78.4 game days"
    public string Short()
    {
        var words = new List<string>();
        if (Fps is { } fps)
            words.Add(FormattableString.Invariant($"{fps:F0} FPS"));
        if (WarpDays > 0)
            words.Add(FormattableString.Invariant($"{WarpDays:F1} game days"));
        return string.Join(", ", words);
    }
}

public sealed class ScenarioRecord
{
    public string Name { get; set; } = "";
    public Kind Kind { get; set; }
    public List<PartRecord> Parts { get; set; } = [];
    // Its screenshots in the order taken, with the limit a comparison holds each to
    public List<ShotRecord> Shots { get; set; } = [];

    public PartRecord? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);
}

public sealed record ShotRecord(string Name, string File, double Limit);

// A measured part: its numbers by metric (Metrics.cs), its measurings (passes and sessions) merged, and each measuring's
// own; the zones' times are in the merged numbers only
public sealed class PartRecord
{
    public string Name { get; set; } = "";
    // How many times it was measured (passes and sessions together)
    public int Runs { get; set; } = 1;
    // How far its GPU or frame time may move between two runs of the same build before a comparison marks it (percent; the
    // test's noise profile: null, the metric's own)
    public double? Limit { get; set; }
    // The first measuring's frames and seconds
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public SortedDictionary<string, double> Values { get; set; } = new(StringComparer.Ordinal);
    public List<MeasuringRecord> Measurings { get; set; } = [];
    // A profiled pass's numbers (benchmark.sh profile): its GPU and frame time with the GPU zones on, and the zones; kept
    // apart, never compared with a measured pass
    public SortedDictionary<string, double>? Profiled { get; set; }

    public double? this[Metric m] => Values.TryGetValue(m.Name, out var v) ? v : null;

    public double? this[string metric] => Values.TryGetValue(metric, out var v) ? v : null;

    // A timed scenario's part: how far time warp got
    [JsonIgnore]
    public bool Warped => Values.ContainsKey(Metrics.Speed.Name);

    // The lowest and highest of its measurings, with more than one
    public double[]? Range(Metric m) =>
        Measurings.Select(x => x.Values.TryGetValue(m.Name, out var v) ? v : (double?)null).OfType<double>().Order().ToArray() is { Length: > 1 } r
            ? [r[0], r[^1]] : null;

    // Each game session's number: the median of its measurings, in the order the sessions played (a verdict's samples)
    public double[] Sessions(Metric m) =>
        Measurings.GroupBy(x => x.Session).OrderBy(g => g.Key)
            .Select(g => g.Select(x => x.Values.TryGetValue(m.Name, out var v) ? v : (double?)null).OfType<double>().ToList())
            .Where(v => v.Count > 0).Select(v => Measure.Median(v)).ToArray();

    // The measurings' numbers, in the order they played
    public double[] Samples(Metric m) =>
        Measurings.Select(x => x.Values.TryGetValue(m.Name, out var v) ? v : (double?)null).OfType<double>().ToArray();

    // Its zones of a group of metrics (Metrics.GpuZones...), the most own time first, from the measured passes or the
    // profiled one
    public IEnumerable<(string Name, double Ms)> Zones(string prefix, bool profiled = false) =>
        ((profiled ? Profiled : Values) ?? []).Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
        .OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value));
}

// One measuring of a part: the game session and pass it played in, and its numbers (no zones)
public sealed record MeasuringRecord(int Session, int Pass, SortedDictionary<string, double> Values);

// An owner of memory at a checkpoint: one of the game's memory tags (kind "game"), a kind of KSA Metal's memory
// ("driver"), the .NET heap ("runtime"), in MB
public sealed record CensusRecord(string Scenario, string Shot, string Owner, string Kind, double Mb);

// A frame a trigger kept: where, how long, and what took its time (the main thread's zones with the most own time, KSA
// Metal's waits, the GPU's time around it)
public sealed record MomentRecord(string Scenario, string Part, int Session, int Pass, double FrameMs, string Cause);
