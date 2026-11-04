// Every frame of a run (frames.csv in its folder), one line each: the raw data the parts' numbers come from (Summary),
// kept so that a shared run can answer later questions, and a better summary applies to old runs too. Frame time is the
// time between two game frames, as the player feels it (it includes waiting for Vsync); GPU time is the time the GPU
// spent on a presented frame, which KSA Metal reports a few frames late (nothing over a part of seconds). The driver's
// columns are those of its latest present: a frame that presented nothing repeats them, the present column tells.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KsaPort.Common.Benchmark;

// What the simulation did in a frame: the game's clock, the speed asked for, the slowest vehicle job, substeps so far,
// physics bubbles and vehicles
public readonly record struct SimSample(double GameSeconds, double Asked, double TickMs, long Substeps, int Bubbles, int Vehicles);

// A frame as frames.csv has it. Part is the part whose numbers the frame counts in, empty when none does
public sealed record FrameRow(double Time, int Session, int Pass, double Script, string Scenario, string Part, string Phase, double FrameMs,
    ulong Present, double? GpuMs, uint Draws, long MetalMb, long FootprintMb, int Thermal, uint Shaders, uint Pipelines,
    double? GameSeconds, double? Asked, double? SimMs, long? Substeps, int? Bubbles, int? Vehicles);

public sealed class FrameLog : IDisposable
{
    public const string FileName = "frames.csv";

    private const string Header = "time,session,pass,script,scenario,part,phase,frame_ms,present,gpu_ms,cpu_ms,draws,passes,metal_mb,footprint_mb," +
                                  "thermal,shaders,pipelines,display_ms,game_s,asked,sim_ms,substeps,bubbles,vehicles";

    private readonly StreamWriter _out;
    private readonly int _session;

    // Which time the scenario plays in this session (a run's passes, from 1)
    public int Pass { get; set; } = 1;
    private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

    // A session's frames, after those of the sessions before it
    public FrameLog(string dir, int session)
    {
        var path = Path.Combine(dir, FileName);
        bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
        // A session before that was killed may have left its last line cut short: this session's start on a line of its own
        bool cut = !fresh && LastByte(path) != '\n';
        _out = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
        if (fresh)
            _out.WriteLine(Header);
        else if (cut)
            _out.WriteLine();
        _session = session;
    }

    public void Add(double script, string? scenario, string? part, string phase, double frameMs, FrameStats? driver, SimSample? sim)
    {
        var line = FormattableString.Invariant($"{_since.Elapsed.TotalSeconds:F4},{_session},{Pass},{script:F3},{scenario},{part},{phase},{frameMs:F3}");
        if (driver is { } d)
            line += FormattableString.Invariant(
                $",{d.Present},{d.GpuMs:F3},{d.CpuMs:F3},{d.Draws},{d.Passes},{d.AllocatedMb},{d.FootprintMb},{d.ThermalState},{d.ShadersCompiled},{d.PipelinesCreated},{d.DisplayMs:F3}");
        else
            line += ",,,,,,,,,,,";
        if (sim is { } m)
            line += FormattableString.Invariant($",{m.GameSeconds:F3},{m.Asked:F3},{m.TickMs:F3},{m.Substeps},{m.Bubbles},{m.Vehicles}");
        else
            line += ",,,,,,";
        _out.WriteLine(line);
    }

    // Onto the disk: the director does it at every part's end and every second, so that a game that dies keeps its frames
    public void Flush() => _out.Flush();

    public void Dispose() => _out.Dispose();

    private static int LastByte(string path)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        f.Seek(-1, SeekOrigin.End);
        return f.ReadByte();
    }

    // The frames of a run's folder; a line cut short (a game that died while writing it) is left out
    public static List<FrameRow> Read(string dir)
    {
        var rows = new List<FrameRow>();
        var path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
            return rows;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        var header = reader.ReadLine()?.Split(',') ?? [];
        int Col(string name) => Array.IndexOf(header, name);
        // An older run's frames.csv has its scenarios as sections, and no sessions or passes
        int time = Col("time"), session = Col("session"), pass = Col("pass"), script = Col("script"),
            scenario = Col("scenario") is >= 0 and var sc ? sc : Col("section"), part = Col("part"),
            phase = Col("phase"), frame = Col("frame_ms"), present = Col("present"), gpu = Col("gpu_ms"), draws = Col("draws"),
            metal = Col("metal_mb"), footprint = Col("footprint_mb"), thermal = Col("thermal"), shaders = Col("shaders"),
            pipelines = Col("pipelines"), game = Col("game_s"), asked = Col("asked"), simMs = Col("sim_ms"), substeps = Col("substeps"),
            bubbles = Col("bubbles"), vehicles = Col("vehicles");
        while (reader.ReadLine() is { } line)
        {
            var f = line.Split(',');
            if (f.Length != header.Length)
                continue;
            string S(int i) => i >= 0 ? f[i] : "";
            double? D(int i) => double.TryParse(S(i), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            long? L(int i) => long.TryParse(S(i), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
            rows.Add(new FrameRow(D(time) ?? 0, (int)(L(session) ?? 1), (int)(L(pass) ?? 1), D(script) ?? 0, S(scenario), S(part), S(phase), D(frame) ?? 0,
                (ulong)(L(present) ?? 0), D(gpu), (uint)(L(draws) ?? 0), L(metal) ?? 0, L(footprint) ?? 0, (int)(L(thermal) ?? 0),
                (uint)(L(shaders) ?? 0), (uint)(L(pipelines) ?? 0), D(game), D(asked), D(simMs), L(substeps), (int?)L(bubbles),
                (int?)L(vehicles)));
        }
        return rows;
    }
}

// A part's numbers from its frames (one measuring of it); Merge puts several measurings of one part together
public static class Summary
{
    public static PartRecord Of(string name, IReadOnlyList<FrameRow> frames, double? openGame, long? openSubsteps)
    {
        var frameMs = frames.Select(r => r.FrameMs).OrderBy(x => x).ToArray();
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
        var gpu = presented.Select(r => r.GpuMs!.Value).OrderBy(x => x).ToArray();
        double seconds = frameMs.Sum() / 1000;
        var part = new PartRecord
        {
            Name = name,
            Frames = frameMs.Length,
            Seconds = Math.Round(seconds, 2),
            AverageFps = Round(seconds > 0 ? frameMs.Length / seconds : 0),
            FrameMsMedian = Round(Percentile(frameMs, 50)),
            FrameMsP95 = Round(Percentile(frameMs, 95)),
            FrameMsP99 = Round(Percentile(frameMs, 99)),
            FrameMsMax = Round(frameMs.Length > 0 ? frameMs[^1] : 0),
            Hitches = frameMs.Count(x => x > 2 * Percentile(frameMs, 50)),
        };
        if (gpu.Length > 0)
        {
            part.GpuMsMedian = Round(Percentile(gpu, 50));
            part.GpuMsP95 = Round(Percentile(gpu, 95));
            part.DrawsMedian = Percentile(presented.Select(r => (double)r.Draws).OrderBy(x => x).ToArray(), 50);
            part.GpuMemoryPeakMb = presented.Max(r => r.MetalMb);
            part.FootprintPeakMb = presented.Max(r => r.FootprintMb);
            part.ThermalMax = presented.Max(r => r.Thermal);
            part.ShadersCompiled = (int)(presented[^1].Shaders - presented[0].Shaders);
            part.PipelinesCreated = (int)(presented[^1].Pipelines - presented[0].Pipelines);
        }
        if (openGame is { } game && frames.Count > 0 && frames.All(r => r.GameSeconds != null))
            part.Warp = Warp(frames, game, openSubsteps ?? frames[0].Substeps ?? 0);
        return part;
    }

    // Time warp: how far the game's clock got by the Mac's clock, from the moment the part opened, so that a first frame
    // that stalls counts
    private static WarpRecord Warp(IReadOnlyList<FrameRow> frames, double openGame, long openSubsteps)
    {
        var sim = new List<(double Wall, double Game)> { (0, openGame) };
        double wall = 0;
        foreach (var r in frames)
        {
            wall += r.FrameMs / 1000;
            sim.Add((wall, r.GameSeconds!.Value));
        }
        double asked = frames.Max(r => r.Asked ?? 0), span = sim[^1].Wall;
        double gameSpan = sim[^1].Game - openGame;
        return new WarpRecord
        {
            GameDays = Round(gameSpan / 86400),
            Speed = span > 0 ? Math.Round(gameSpan / span) : 0,
            SpeedAsked = Math.Round(asked),
            SecondsToSpeed = SecondsToSpeed(sim, asked),
            SimMsMax = Round(frames.Max(r => r.SimMs ?? 0)),
            Substeps = Math.Max((frames[^1].Substeps ?? 0) - openSubsteps, 0),
            BubblesMax = frames.Max(r => r.Bubbles ?? 0),
            VehiclesMax = frames.Max(r => r.Vehicles ?? 0),
        };
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
                return Round(wall);
        }
        return null;
    }

    // Several measurings of one part (passes, sessions): the median of each number, the most of those that count events
    // (hitches, shaders compiled, the Mac's heat, physics), the range of frame and GPU time and of the speed reached; the
    // first measuring's frames and seconds
    public static PartRecord Merge(IReadOnlyList<PartRecord> runs)
    {
        if (runs.Count == 1)
            return runs[0];
        static double Median(IEnumerable<double> values)
        {
            var v = values.OrderBy(x => x).ToArray();
            return v.Length == 0 ? double.NaN : Math.Round(v.Length % 2 == 1 ? v[v.Length / 2] : (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2, 2);
        }
        static double? MedianOf(IEnumerable<double?> values) => values.OfType<double>().ToList() is { Count: > 0 } v ? Median(v) : null;
        static double[]? Range(IEnumerable<double?> values) =>
            values.OfType<double>().OrderBy(x => x).ToArray() is { Length: > 1 } v ? [v[0], v[^1]] : null;
        var m = new PartRecord
        {
            Name = runs[0].Name,
            Runs = runs.Count,
            Frames = runs[0].Frames,
            Seconds = runs[0].Seconds,
            AverageFps = Median(runs.Select(r => r.AverageFps)),
            FrameMsMedian = Median(runs.Select(r => r.FrameMsMedian)),
            FrameMsP95 = Median(runs.Select(r => r.FrameMsP95)),
            FrameMsP99 = Median(runs.Select(r => r.FrameMsP99)),
            FrameMsMax = Median(runs.Select(r => r.FrameMsMax)),
            Hitches = runs.Max(r => r.Hitches),
            GpuMsMedian = MedianOf(runs.Select(r => r.GpuMsMedian)),
            GpuMsP95 = MedianOf(runs.Select(r => r.GpuMsP95)),
            DrawsMedian = MedianOf(runs.Select(r => r.DrawsMedian)),
            GpuMemoryPeakMb = (long?)MedianOf(runs.Select(r => (double?)r.GpuMemoryPeakMb)),
            FootprintPeakMb = (long?)MedianOf(runs.Select(r => (double?)r.FootprintPeakMb)),
            ThermalMax = runs.Max(r => r.ThermalMax),
            ShadersCompiled = runs.Max(r => r.ShadersCompiled),
            PipelinesCreated = runs.Max(r => r.PipelinesCreated),
            FrameMsRange = Range(runs.Select(r => (double?)r.FrameMsMedian)),
            GpuMsRange = Range(runs.Select(r => r.GpuMsMedian)),
        };
        var warps = runs.Select(r => r.Warp).OfType<WarpRecord>().ToList();
        if (warps.Count > 0)
        {
            m.Warp = new WarpRecord
            {
                GameDays = Median(warps.Select(w => w.GameDays)),
                Speed = Median(warps.Select(w => w.Speed)),
                SpeedAsked = warps.Max(w => w.SpeedAsked),
                // A part that didn't get to its speed in one of the runs didn't, whatever the others did
                SecondsToSpeed = warps.Any(w => w.SecondsToSpeed == null) ? null : Median(warps.Select(w => w.SecondsToSpeed!.Value)),
                SimMsMax = warps.Max(w => w.SimMsMax),
                Substeps = warps.Max(w => w.Substeps),
                BubblesMax = warps.Max(w => w.BubblesMax),
                VehiclesMax = warps.Max(w => w.VehiclesMax),
                SpeedRange = Range(warps.Select(w => (double?)w.Speed)),
            };
        }
        return m;
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0)
            return 0;
        int i = (int)Math.Ceiling(p / 100 * sorted.Length) - 1;
        return sorted[Math.Clamp(i, 0, sorted.Length - 1)];
    }

    private static double Round(double x) => Math.Round(x, 2);
}
