// The benchmark's metrics: every number a run's record, report, comparison and study shows has one name here, with its
// unit, which way is better, how a part's measurings combine and how far it may move before a comparison marks it, so
// that nothing that shows a run computes a number of its own. A name is domain/stat: frame/time, gpu/busy, warp/speed;
// a zone of the game's profilers is a metric too (gpu/zone/Clouds Raymarching, cpu/zone/PrepareFrame, job/VehicleSolver/
// Solve), its own time a frame.
//
// The frame metrics are PresentMon's (version 2): frame time between two frames as the player feels it; CPU busy, the
// frame's work from when it began (the game's wait for an earlier frame over) to its present, less KSA Metal's waits in
// it (a drawable, a fence); CPU wait, those waits and the wait for the earlier frame, so that the two make the frame; GPU
// busy, the GPU's time on the frame; display latency, from when the frame began to when it was on screen; displayed
// time, how long it stayed there; animation error, how far the step between two frames' starts differs from the step
// between their showing (a judder the frame rate doesn't show). A hitch is a frame over 50 ms and over twice the median
// of the 60 before it; frames over 100 ms are counted apart. The 1% low is the mean of the slowest 1 % of frames.
//
// A probe's counters are metrics too (probe/<probe>/<counter>, the memory census's mem/...): a probe registers them with
// the run, its store keeps them and its record carries them, and Register makes them known wherever the record is read,
// so that comparisons and studies judge them as the others.
//
// Measure makes a measuring's numbers (one part in one pass of one session) from its frames, KSA Metal's shown records
// of its presents and the game's zone sums; Merge puts a part's measurings together.
using System;
using System.Collections.Generic;
using System.Linq;

namespace KsaPort.Common.Benchmark;

public enum Better { Lower, Higher, Neither }

// How a part's measurings make its number: their median, the most of them (the worst state), their mean (counts of
// events: a part measured more often doesn't collect more), or their median only when every measuring has the number (a
// speed reached in each)
public enum Across { Median, Most, Mean, All }

public sealed record Metric(string Name, string Label, string Unit, Better Better, Across Across, int Decimals, string Help)
{
    // How far it may move between two runs of the same build before a comparison marks it, in percent; null: shown, not
    // judged
    public double? Limit { get; init; }
    // Whether a test's noise profile calibrates its limit per part (Noise.cs)
    public bool Calibrated { get; init; }
    // The least change, in its unit, that counts besides the limit: a few percent of a small number is nothing (CPU busy
    // of 2 ms moving 0.1 ms), and a count that was 0 moves by this much at least
    public double Floor { get; init; }
    // Whether it moves with the Mac's heat and what else shares the GPU (times, energy), so that one run against another
    // can't tell a small change from that (Statistics.ScreenDrift)
    public bool Drifts { get; init; }
    // The fewest frames a part needs on both sides to judge it (a P99 of 20 frames is its worst frame)
    public int MinFrames { get; init; }

    public string Format(double value) => value.ToString(Decimals == 0 ? "0" : "0." + new string('0', Decimals), System.Globalization.CultureInfo.InvariantCulture);

    public override string ToString() => Name;
}

public static class Metrics
{
    private const double Default = 5;
    // The frames a part needs for its tail (P99, 1% low) to be more than its worst frame or two
    private const int TailFrames = 200;

    public static readonly Metric Fps = new("frame/fps", "FPS", "fps", Better.Higher, Across.Median, 1, "frames a second over the part");
    public static readonly Metric FrameTime = new("frame/time", "Frame time", "ms", Better.Lower, Across.Median, 1,
        "the time between two frames, the median") { Limit = Default, Calibrated = true, Drifts = true };
    public static readonly Metric FrameP95 = new("frame/p95", "P95 frame time", "ms", Better.Lower, Across.Median, 1, "95 % of frames took less");
    public static readonly Metric FrameP99 = new("frame/p99", "P99 frame time", "ms", Better.Lower, Across.Median, 1, "99 % of frames took less")
        { Limit = 10, Drifts = true, MinFrames = TailFrames };
    public static readonly Metric Low1 = new("frame/low1", "1% low", "ms", Better.Lower, Across.Median, 1, "the mean of the worst 1 % of frames")
        { Limit = 10, Drifts = true, MinFrames = TailFrames };
    public static readonly Metric Worst = new("frame/worst", "Worst frame", "ms", Better.Lower, Across.Median, 0, "the slowest frame");
    public static readonly Metric Hitches = new("frame/hitches", "Hitches", "frames", Better.Lower, Across.Mean, 1,
        "frames over 50 ms and twice the median of the 60 before: the stutters you see") { Limit = 50, Floor = 2 };
    public static readonly Metric Over100 = new("frame/over100", "Over 100 ms", "frames", Better.Lower, Across.Mean, 1, "frames over 100 ms")
        { Limit = 50, Floor = 2 };
    public static readonly Metric CpuBusy = new("frame/cpu_busy", "CPU busy", "ms", Better.Lower, Across.Median, 1,
        "the frame's work from its start to its present, less KSA Metal's waits in it, the median") { Limit = Default, Floor = 0.25, Drifts = true };
    public static readonly Metric CpuWait = new("frame/cpu_wait", "CPU wait", "ms", Better.Neither, Across.Median, 1,
        "how long a frame waited: for an earlier frame before it began, for a drawable or the GPU in it, the median");
    public static readonly Metric GpuBusy = new("gpu/busy", "GPU busy", "ms", Better.Lower, Across.Median, 1,
        "the GPU's time on a frame, the median") { Limit = Default, Calibrated = true, Drifts = true };
    public static readonly Metric GpuP95 = new("gpu/p95", "P95 GPU busy", "ms", Better.Lower, Across.Median, 1, "95 % of frames took the GPU less");
    // The GPU's power (benchmark.sh's power channel, host/gpu_power) times the frame time: what a frame costs in energy, the
    // same work done cheaper whatever the frame rate (power alone rises when frames come faster)
    public static readonly Metric GpuEnergy = new("gpu/energy", "GPU energy", "mJ", Better.Lower, Across.Median, 1,
        "the GPU's energy a frame: its power while the part played times the frame time") { Limit = 10, Drifts = true };
    public static readonly Metric Latency = new("frame/latency", "Display latency", "ms", Better.Lower, Across.Median, 1,
        "from a frame's start to its showing, the median") { Limit = 10, Floor = 1, Drifts = true };
    public static readonly Metric Displayed = new("frame/displayed", "Displayed time", "ms", Better.Neither, Across.Median, 1,
        "how long a frame stayed on screen, the median");
    public static readonly Metric AnimationError = new("frame/animation_error", "Animation error", "ms", Better.Lower, Across.Median, 2,
        "how far the steps between frames' starts and between their showing differ, on average");
    public static readonly Metric Draws = new("gpu/draws", "Draws", "", Better.Neither, Across.Median, 0, "draw calls a frame, the median");
    public static readonly Metric Footprint = new("mem/footprint", "Memory", "MB", Better.Lower, Across.Median, 0,
        "the game's memory footprint at its peak (Activity Monitor's)") { Limit = 5, Floor = 150 };
    public static readonly Metric GpuMemory = new("mem/gpu", "GPU memory", "MB", Better.Lower, Across.Median, 0,
        "what KSA Metal allocated for the GPU, at its peak") { Limit = 3, Floor = 64 };
    public static readonly Metric Thermal = new("mac/thermal", "Thermal state", "", Better.Lower, Across.Most, 0,
        "the Mac's thermal state at its worst: 0 nominal, 1 fair, 2 serious (macOS slows the chip), 3 critical");
    public static readonly Metric Shaders = new("gpu/shaders", "Shaders", "", Better.Lower, Across.Mean, 1,
        "shaders compiled while measuring, each one a stutter");
    public static readonly Metric Pipelines = new("gpu/pipelines", "Pipelines", "", Better.Lower, Across.Mean, 1, "pipelines made while measuring");
    public static readonly Metric GcPause = new("runtime/gc_pause", "GC pause", "ms", Better.Lower, Across.Median, 3,
        ".NET's garbage collection pauses, ms a frame");
    public static readonly Metric Jit = new("runtime/jit", "JIT", "ms", Better.Lower, Across.Median, 1, ".NET's compiling of code while measuring");
    public static readonly Metric GameDays = new("warp/days", "Game days", "days", Better.Higher, Across.Median, 2,
        "the game days the part got through");
    public static readonly Metric Speed = new("warp/speed", "Speed", "x", Better.Higher, Across.Median, 0,
        "game seconds a second (1 is real time)") { Limit = Default };
    public static readonly Metric SpeedAsked = new("warp/asked", "Asked", "x", Better.Neither, Across.Most, 0, "the speed asked for");
    public static readonly Metric ToSpeed = new("warp/to_speed", "To 90 %", "s", Better.Lower, Across.All, 1,
        "seconds until the game ran at 90 % of the speed asked for (none: never)");
    public static readonly Metric SimTick = new("sim/tick", "Physics", "ms", Better.Lower, Across.Most, 1, "the slowest vehicle job");
    public static readonly Metric Substeps = new("sim/substeps", "Substeps", "", Better.Neither, Across.Most, 0, "physics substeps");
    public static readonly Metric Bubbles = new("sim/bubbles", "Bubbles", "", Better.Neither, Across.Most, 0, "physics bubbles at the most");
    public static readonly Metric Vehicles = new("sim/vehicles", "Vehicles", "", Better.Neither, Across.Most, 0, "vehicles at the most");

    public static readonly IReadOnlyList<Metric> All =
    [
        Fps, FrameTime, FrameP95, FrameP99, Low1, Worst, Hitches, Over100, CpuBusy, CpuWait, GpuBusy, GpuP95, GpuEnergy, Latency, Displayed,
        AnimationError, Draws, Footprint, GpuMemory, Thermal, Shaders, Pipelines, GcPause, Jit, GameDays, Speed, SpeedAsked, ToSpeed,
        SimTick, Substeps, Bubbles, Vehicles,
    ];

    private static readonly Dictionary<string, Metric> ByName = All.ToDictionary(m => m.Name, StringComparer.Ordinal);

    // The probes' metrics known so far: from the runs read in this process
    private static readonly Dictionary<string, Metric> ByProbes = new(StringComparer.Ordinal);

    private static Metric? Registered(string name)
    {
        lock (ByProbes)
            return ByProbes.GetValueOrDefault(name);
    }

    // A probe's metric made known (a built-in one's name stays the built-in's)
    public static void Register(Metric m)
    {
        if (ByName.ContainsKey(m.Name))
            return;
        // The first definition read in this process stands: two runs compared judge a probe's number by one rule
        lock (ByProbes)
            ByProbes.TryAdd(m.Name, m);
    }

    // The metrics a comparison judges: the built-in ones, then the probes'
    public static IReadOnlyList<Metric> Judged
    {
        get
        {
            lock (ByProbes)
                return [.. All, .. ByProbes.Values.OrderBy(m => m.Name, StringComparer.Ordinal)];
        }
    }

    // The zones' metrics: a zone's own time a frame in a group of the game's profilers
    public const string GpuZones = "gpu/zone/", MainZones = "cpu/zone/", JobZones = "job/";

    public static string ZoneName(string group, string zone) =>
        group switch { "gpu" => GpuZones + zone, "main" => MainZones + zone, _ => $"{JobZones}{group}/{zone}" };

    public static bool IsZone(string name) => name.StartsWith(GpuZones, StringComparison.Ordinal) || name.StartsWith(MainZones, StringComparison.Ordinal) ||
                                              name.StartsWith(JobZones, StringComparison.Ordinal);

    // A zone's name without its group: "Clouds Raymarching"
    public static string ZoneLabel(string name) => name[(name.LastIndexOf('/') + 1)..];

    // A metric by its name: one of All, or a zone's (made on the way); null for a name that is neither
    public static Metric? Find(string name) =>
        ByName.TryGetValue(name, out var m) ? m
        : Registered(name) is { } r ? r
        : IsZone(name) ? new Metric(name, ZoneLabel(name), "ms", Better.Lower, Across.Median, 2, "the zone's own time a frame") { Limit = 10 }
        : null;
}

// What KSA Metal said of a present: when its frame began (its wait for an earlier frame over), was presented, finished on
// the GPU and was shown (0: never), on the store's clock in seconds, and how long it waited
public readonly record struct ShownRow(ulong Present, double Began, double Presented, double GpuEnd, double Shown, double WaitedMs);

// A wait of KSA Metal's (the store's waits): when it began on the store's clock, how long, what
public readonly record struct WaitRow(double Start, double Ms, string What);

// A zone's sum over one measuring (the store's zone_stats)
public readonly record struct ZoneRow(int Session, int Pass, string Scenario, string Part, string Group, string Zone, int Frames, int Calls,
    double TotalMs, double SelfMs);

public static class Measure
{
    // The window a hitch is held against: the median of this many frames before it
    private const int HitchWindow = 60;
    // Zones under this own time a frame are left out of a part's numbers
    private const double ZoneFloorMs = 0.01;

    // One measuring's numbers from its frames: the shown records of its presents (by present), KSA Metal's waits (in the
    // order they began), the zone sums of the measuring, and with a timed scenario's part the game's clock and substeps
    // when it opened
    public static Dictionary<string, double> Of(IReadOnlyList<FrameRow> frames, IReadOnlyDictionary<ulong, ShownRow>? shown,
        IEnumerable<ZoneRow>? zones, double? openGame, long? openSubsteps, IReadOnlyList<WaitRow>? waits = null, FrameRow? before = null)
    {
        var v = new Dictionary<string, double>(StringComparer.Ordinal);
        var frameMs = frames.Select(r => r.FrameMs).ToArray();
        var sorted = frameMs.OrderBy(x => x).ToArray();
        double seconds = frameMs.Sum() / 1000;
        Set(v, Metrics.Fps, seconds > 0 ? frameMs.Length / seconds : 0);
        if (sorted.Length > 0)
        {
            Set(v, Metrics.FrameTime, Percentile(sorted, 50));
            Set(v, Metrics.FrameP95, Percentile(sorted, 95));
            Set(v, Metrics.FrameP99, Percentile(sorted, 99));
            Set(v, Metrics.Worst, sorted[^1]);
            int worst = Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01));
            Set(v, Metrics.Low1, sorted[^worst..].Average());
            Set(v, Metrics.Hitches, HitchCount(frameMs));
            Set(v, Metrics.Over100, frameMs.Count(x => x > 100));
        }
        // One sample per presented frame: the game may run a frame without presenting, or present twice
        var presented = new List<FrameRow>();
        ulong last = 0;
        foreach (var r in frames)
        {
            if (r.GpuMs == null || r.Present == last)
                continue;
            last = r.Present;
            presented.Add(r);
        }
        if (presented.Count > 0)
        {
            var gpu = presented.Select(r => r.GpuMs!.Value).OrderBy(x => x).ToArray();
            Set(v, Metrics.GpuBusy, Percentile(gpu, 50));
            Set(v, Metrics.GpuP95, Percentile(gpu, 95));
            Set(v, Metrics.Draws, Percentile(presented.Select(r => (double)r.Draws).OrderBy(x => x).ToArray(), 50));
            Set(v, Metrics.GpuMemory, presented.Max(r => r.MetalMb));
            Set(v, Metrics.Footprint, presented.Max(r => r.FootprintMb));
            Set(v, Metrics.Thermal, presented.Max(r => r.Thermal));
            // Counted from the frame before the part, where there is one: the first frame's own compiles count too
            var from = before is { } b && b.Session == presented[0].Session ? b : presented[0];
            Set(v, Metrics.Shaders, Math.Max(0, (long)presented[^1].Shaders - from.Shaders));
            Set(v, Metrics.Pipelines, Math.Max(0, (long)presented[^1].Pipelines - from.Pipelines));
        }
        if (shown != null)
            Presented(v, presented.Select(r => shown.TryGetValue(r.Present, out var s) ? s : (ShownRow?)null).OfType<ShownRow>().ToList(), waits ?? []);
        if (frames.Count > 0 && frames.All(r => r.GcPauseMs != null))
        {
            Set(v, Metrics.GcPause, frames.Sum(r => r.GcPauseMs!.Value) / frames.Count, 3);
            Set(v, Metrics.Jit, frames.Sum(r => r.JitMs ?? 0));
        }
        if (openGame is { } game && frames.Count > 0 && frames.All(r => r.GameSeconds != null))
            Warp(v, frames, game, openSubsteps ?? frames[0].Substeps ?? 0);
        if (zones != null)
            Zones(v, zones, frames.Count);
        return v;
    }

    // PresentMon's breakdown of the frames that were presented, from KSA Metal's times of each and its waits
    private static void Presented(Dictionary<string, double> v, List<ShownRow> rows, IReadOnlyList<WaitRow> waits)
    {
        static double[] Ms(IEnumerable<double> seconds) => seconds.Select(s => s * 1000).OrderBy(x => x).ToArray();
        var began = rows.Where(r => r.Began > 0 && r.Presented >= r.Began).ToList();
        if (began.Count > 0)
        {
            var inside = began.Select(r => WaitedIn(waits, r.Began, r.Presented)).ToList();
            Set(v, Metrics.CpuBusy, Percentile(began.Zip(inside, (r, w) => Math.Max(0, (r.Presented - r.Began) * 1000 - w)).OrderBy(x => x).ToArray(), 50));
            Set(v, Metrics.CpuWait, Percentile(began.Zip(inside, (r, w) => r.WaitedMs + w).OrderBy(x => x).ToArray(), 50));
        }
        var onScreen = rows.Where(r => r.Began > 0 && r.Shown >= r.Began).ToList();
        if (onScreen.Count > 0)
            Set(v, Metrics.Latency, Percentile(Ms(onScreen.Select(r => r.Shown - r.Began)), 50));
        if (onScreen.Count > 1)
        {
            var steps = onScreen.Zip(onScreen.Skip(1)).ToList();
            Set(v, Metrics.Displayed, Percentile(Ms(steps.Select(p => p.Second.Shown - p.First.Shown)), 50));
            Set(v, Metrics.AnimationError, steps.Average(p => Math.Abs(p.Second.Began - p.First.Began - (p.Second.Shown - p.First.Shown))) * 1000);
        }
    }

    // The ms of KSA Metal's waits within a span of the store's clock (waits in the order they began; each clipped to it):
    // their union, since one may hold another (vkQueuePresentKHR the drawable's, vkQueueSubmit a residency commit)
    private static double WaitedIn(IReadOnlyList<WaitRow> waits, double from, double to)
    {
        int lo = 0, hi = waits.Count;
        // The first wait that ends after the span begins: waits are short, so one that began a second earlier has ended
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (waits[mid].Start < from - 1)
                lo = mid + 1;
            else
                hi = mid;
        }
        double ms = 0, covered = from;
        for (int i = lo; i < waits.Count && waits[i].Start < to; i++)
        {
            double start = Math.Max(waits[i].Start, covered), end = Math.Min(waits[i].Start + waits[i].Ms / 1000, to);
            if (end > start)
            {
                ms += (end - start) * 1000;
                covered = end;
            }
        }
        return ms;
    }

    // Frames over 50 ms and over twice the median of the frames before them (the first frames against those there are)
    private static int HitchCount(double[] frameMs)
    {
        int hitches = 0;
        var window = new List<double>(HitchWindow);
        for (int i = 0; i < frameMs.Length; i++)
        {
            if (frameMs[i] > 50)
            {
                window.Clear();
                for (int j = Math.Max(0, i - HitchWindow); j < i; j++)
                    window.Add(frameMs[j]);
                window.Sort();
                if (window.Count == 0 || frameMs[i] > 2 * window[(window.Count - 1) / 2])
                    hitches++;
            }
        }
        return hitches;
    }

    // Time warp: how far the game's clock got by the Mac's clock, from the moment the part opened, so that a first frame
    // that stalls counts
    private static void Warp(Dictionary<string, double> v, IReadOnlyList<FrameRow> frames, double openGame, long openSubsteps)
    {
        var sim = new List<(double Wall, double Game)> { (0, openGame) };
        double wall = 0;
        foreach (var r in frames)
        {
            wall += r.FrameMs / 1000;
            sim.Add((wall, r.GameSeconds!.Value));
        }
        double asked = frames.Max(r => r.Asked ?? 0), span = sim[^1].Wall, gameSpan = sim[^1].Game - openGame;
        Set(v, Metrics.GameDays, gameSpan / 86400);
        Set(v, Metrics.Speed, span > 0 ? Math.Round(gameSpan / span) : 0);
        Set(v, Metrics.SpeedAsked, Math.Round(asked));
        if (SecondsToSpeed(sim, asked) is { } t)
            Set(v, Metrics.ToSpeed, t);
        Set(v, Metrics.SimTick, frames.Max(r => r.SimMs ?? 0));
        Set(v, Metrics.Substeps, Math.Max((frames[^1].Substeps ?? 0) - openSubsteps, 0));
        Set(v, Metrics.Bubbles, frames.Max(r => r.Bubbles ?? 0));
        Set(v, Metrics.Vehicles, frames.Max(r => r.Vehicles ?? 0));
    }

    // The first time the game's clock ran at 90 % of the speed asked for, over a second of the Mac's clock (null: never)
    private static double? SecondsToSpeed(List<(double Wall, double Game)> sim, double asked)
    {
        int from = 0;
        foreach (var (wall, game) in sim)
        {
            while (wall - sim[from].Wall > 1)
                from++;
            double span = wall - sim[from].Wall;
            if (span > 0.5 && (game - sim[from].Game) / span >= 0.9 * asked)
                return wall;
        }
        return null;
    }

    // Each zone's own time a frame: the GPU's against its profiler's own frames (published late, fewer than the part's),
    // the threads' against the part's frames
    private static void Zones(Dictionary<string, double> v, IEnumerable<ZoneRow> zones, int frames)
    {
        var rows = zones.ToList();
        int gpuFrames = rows.Where(z => z.Group == "gpu" && z.Zone == "GPU").Select(z => z.Frames).DefaultIfEmpty(0).Max();
        foreach (var z in rows)
        {
            int n = z.Group == "gpu" ? gpuFrames : frames;
            if (n <= 0 || z.Group == "gpu" && z.Zone == "GPU")
                continue;
            double perFrame = z.SelfMs / n;
            if (perFrame >= ZoneFloorMs)
                v[Metrics.ZoneName(z.Group, z.Zone)] = Math.Round(perFrame, 3);
        }
    }

    // Several measurings of one part (passes, sessions) as one: each metric as its Across says
    public static Dictionary<string, double> Merge(IReadOnlyList<IReadOnlyDictionary<string, double>> measurings)
    {
        var merged = new Dictionary<string, double>(StringComparer.Ordinal);
        if (measurings.Count == 1)
        {
            foreach (var (k, x) in measurings[0])
                merged[k] = x;
            return merged;
        }
        foreach (var name in measurings.SelectMany(m => m.Keys).Distinct())
        {
            var metric = Metrics.Find(name);
            var values = measurings.Where(m => m.ContainsKey(name)).Select(m => m[name]).ToList();
            switch (metric?.Across ?? Across.Median)
            {
                case Across.Most:
                    merged[name] = values.Max();
                    break;
                case Across.Mean:
                    merged[name] = Math.Round(values.Average(), 3);
                    break;
                case Across.All when values.Count < measurings.Count:
                    break;
                default:
                    merged[name] = Math.Round(Median(values), 3);
                    break;
            }
        }
        return merged;
    }

    public static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToArray();
        return v.Length == 0 ? double.NaN : v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        int i = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    private static void Set(Dictionary<string, double> v, Metric m, double x, int digits = 2) => v[m.Name] = Math.Round(x, digits);
}
