// A run's record (run.json in its folder): everything its report, page, comparison and zip say, in one typed file.
// Finish.cs makes it from what the run left (Events.cs, Frames.cs, the game's logs, the screenshots); nothing else
// writes it, and everything that shows a run reads it. A run older than this format has its report.json read into one
// (Legacy.cs).
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
    public const int CurrentSchema = 3;
    public const string FileName = "run.json";

    // CurrentSchema in a record Finish made (an older run's run.json, its options, reads as 0)
    public int Schema { get; set; }
    // The folder's name, the date and time it started
    public string Name { get; set; } = "";
    public DateTime Started { get; set; }
    // The test (mixed, tour, warp, full; "places" for places chosen) and how it played (RunOptions.Mode)
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
            memory = Math.Max(memory, p.FootprintPeakMb ?? 0);
            if (p.Warp is { } warp)
            {
                days += warp.GameDays;
                continue;
            }
            if (p.Seconds <= 0)
                continue;
            frames += p.AverageFps * p.Seconds;
            seconds += p.Seconds;
            if (p.GpuMsMedian is { } gpu)
            {
                gpuWeighted += gpu * p.Seconds;
                gpuSeconds += p.Seconds;
            }
            if (p.AverageFps < slowest)
                (slowest, worst) = (p.AverageFps, (s, p));
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

    // The record in a run's folder, or one made from an older run's report.json; null when it has neither
    public static RunRecord? Read(string dir)
    {
        var path = Path.Combine(dir, FileName);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<RunRecord>(File.ReadAllText(path), Json) is { Schema: CurrentSchema } record)
                return record;
        }
        catch (JsonException)
        {
            // run.json is an older run's options, not a record
        }
        return Legacy.Read(dir);
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
    // The side of an A/B run it played (benchmark.sh measure --ab): A, B; null in other runs
    public string? Variant { get; set; }
    public List<PartRecord> Parts { get; set; } = [];
    // Its screenshots in the order taken, with the limit a comparison holds each to
    public List<ShotRecord> Shots { get; set; } = [];

    public PartRecord? Part(string name) => Parts.FirstOrDefault(p => p.Name == name);
}

public sealed record ShotRecord(string Name, string File, double Limit);

// A measured part: medians over its passes (the range of frame and GPU time across them when there were several)
public sealed class PartRecord
{
    public string Name { get; set; } = "";
    // How many times it was measured (passes and sessions together)
    public int Runs { get; set; } = 1;
    // How far its GPU or frame time may move between two runs of the same build before a comparison marks it (percent; the
    // test's noise profile: null, the comparison's own)
    public double? Limit { get; set; }
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public double AverageFps { get; set; }
    public double FrameMsMedian { get; set; }
    public double FrameMsP95 { get; set; }
    public double FrameMsP99 { get; set; }
    public double FrameMsMax { get; set; }
    // Frames longer than twice the part's median: stutters, whatever the frame rate
    public int Hitches { get; set; }
    public double? GpuMsMedian { get; set; }
    public double? GpuMsP95 { get; set; }
    public double? DrawsMedian { get; set; }
    public long? GpuMemoryPeakMb { get; set; }
    // The game's memory footprint at its peak (what Activity Monitor shows)
    public long? FootprintPeakMb { get; set; }
    // The Mac's thermal state at its worst: 0 nominal, 1 fair, 2 serious, 3 critical (macOS slows the chip down from 2)
    public int? ThermalMax { get; set; }
    // Shaders compiled and pipelines made while measuring: each is a stutter that a warm shader cache avoids
    public int? ShadersCompiled { get; set; }
    public int? PipelinesCreated { get; set; }
    public double[]? FrameMsRange { get; set; }
    public double[]? GpuMsRange { get; set; }
    // A timed scenario's part: how far time warp got
    public WarpRecord? Warp { get; set; }
}

// The game days the part got through, the speed it ran at and was asked for (1 is real time), the seconds it took to reach
// 90 % of that (null: it never did), the slowest vehicle job, substeps, and the most physics bubbles and vehicles
public sealed class WarpRecord
{
    public double GameDays { get; set; }
    public double Speed { get; set; }
    public double SpeedAsked { get; set; }
    public double? SecondsToSpeed { get; set; }
    public double SimMsMax { get; set; }
    public long Substeps { get; set; }
    public int BubblesMax { get; set; }
    public int VehiclesMax { get; set; }
    public double[]? SpeedRange { get; set; }
}
