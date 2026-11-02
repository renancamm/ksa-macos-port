// Measurement windows and the run's results file (written again after every section, so a crash keeps the ones
// before it).
//
// Frame time is the time between two game frames, as the player feels it (it includes waiting for Vsync). GPU time
// is the time the GPU spent on a presented frame; KSA Metal reports it a few frames late, which does not matter over
// a window of seconds.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KsaMetalBenchmark;

// What the simulation did in a frame: the game's clock, the speed asked for, the slowest vehicle job, substeps so far,
// physics bubbles and vehicles
public readonly record struct SimSample(double GameSeconds, double Asked, double TickMs, long Substeps, int Bubbles, int Vehicles);

public class Window
{
    // A part of a live section (time warp): how far the game's clock got, by the Mac's clock
    private readonly List<(double Wall, SimSample Sim)> _sim = new();
    private double _simWall;
    private readonly List<double> _frameMs = new();
    private readonly List<double> _gpuMs = new();
    private readonly List<double> _draws = new();
    private long _memoryPeakMb, _footprintPeakMb;
    private int _thermalMax;
    private uint _shadersFirst, _shadersLast, _pipelinesFirst, _pipelinesLast;
    private ulong _lastPresent;

    public string Name { get; }
    public double EndsAt { get; }
    // The vehicle the camera followed when the window opened, if any
    public string? Vehicle { get; init; }
    public int Frames => _frameMs.Count;

    public Window(string name, double endsAt)
    {
        Name = name;
        EndsAt = endsAt;
    }

    public void Add(double frameMs, FrameStats? driver)
    {
        _frameMs.Add(frameMs);
        // One sample per presented frame: the game may run a frame without presenting, or present twice
        if (driver is { } d && d.Present != _lastPresent)
        {
            _lastPresent = d.Present;
            _gpuMs.Add(d.GpuMs);
            _draws.Add(d.Draws);
            _memoryPeakMb = Math.Max(_memoryPeakMb, d.AllocatedMb);
            _footprintPeakMb = Math.Max(_footprintPeakMb, d.FootprintMb);
            _thermalMax = Math.Max(_thermalMax, d.ThermalState);
            if (_gpuMs.Count == 1)
                (_shadersFirst, _pipelinesFirst) = (d.ShadersCompiled, d.PipelinesCreated);
            (_shadersLast, _pipelinesLast) = (d.ShadersCompiled, d.PipelinesCreated);
        }
    }

    public void AddSim(double frameMs, SimSample sim)
    {
        _simWall += frameMs / 1000;
        _sim.Add((_simWall, sim));
    }

    // The first time the game's clock ran at 90 % of the speed asked for, over a second of the Mac's clock (null: never)
    private double? SecondsToSpeed(double asked)
    {
        int from = 0;
        foreach (var (wall, sim) in _sim)
        {
            while (wall - _sim[from].Wall > 1)
                from++;
            double span = wall - _sim[from].Wall;
            if (span > 0.5 && (sim.GameSeconds - _sim[from].Sim.GameSeconds) / span >= 0.9 * asked)
                return Round(wall);
        }
        return null;
    }

    public WindowResult Summarize()
    {
        var frames = _frameMs.OrderBy(x => x).ToArray();
        var gpu = _gpuMs.OrderBy(x => x).ToArray();
        double seconds = _frameMs.Sum() / 1000;
        var result = new WindowResult
        {
            Name = Name,
            Frames = frames.Length,
            Seconds = Math.Round(seconds, 2),
            AverageFps = Round(seconds > 0 ? frames.Length / seconds : 0),
            FrameMsMedian = Round(Percentile(frames, 50)),
            FrameMsP95 = Round(Percentile(frames, 95)),
            FrameMsP99 = Round(Percentile(frames, 99)),
            FrameMsMax = Round(frames.Length > 0 ? frames[^1] : 0),
            Hitches = frames.Count(x => x > 2 * Percentile(frames, 50)),
            GpuMsMedian = gpu.Length > 0 ? Round(Percentile(gpu, 50)) : null,
            GpuMsP95 = gpu.Length > 0 ? Round(Percentile(gpu, 95)) : null,
            DrawsMedian = _draws.Count > 0 ? Percentile(_draws.OrderBy(x => x).ToArray(), 50) : null,
            GpuMemoryPeakMb = gpu.Length > 0 ? _memoryPeakMb : null,
            FootprintPeakMb = gpu.Length > 0 ? _footprintPeakMb : null,
            ThermalMax = gpu.Length > 0 ? _thermalMax : null,
            ShadersCompiled = gpu.Length > 0 ? (int)(_shadersLast - _shadersFirst) : null,
            PipelinesCreated = gpu.Length > 0 ? (int)(_pipelinesLast - _pipelinesFirst) : null,
        };
        if (_sim.Count < 2)
            return result;
        var (first, last) = (_sim[0], _sim[^1]);
        double asked = _sim.Max(x => x.Sim.Asked), wallSpan = last.Wall - first.Wall;
        result.GameDays = Round((last.Sim.GameSeconds - first.Sim.GameSeconds) / 86400);
        result.Speed = wallSpan > 0 ? Math.Round((last.Sim.GameSeconds - first.Sim.GameSeconds) / wallSpan) : 0;
        result.SpeedAsked = Math.Round(asked);
        result.SecondsToSpeed = SecondsToSpeed(asked);
        result.SimMsMax = Round(_sim.Max(x => x.Sim.TickMs));
        result.Substeps = Math.Max(last.Sim.Substeps - first.Sim.Substeps, 0);
        result.BubblesMax = _sim.Max(x => x.Sim.Bubbles);
        result.VehiclesMax = _sim.Max(x => x.Sim.Vehicles);
        return result;
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

public class WindowResult
{
    public string Name { get; set; } = "";
    public int Frames { get; set; }
    public double Seconds { get; set; }
    public double AverageFps { get; set; }
    public double FrameMsMedian { get; set; }
    public double FrameMsP95 { get; set; }
    public double FrameMsP99 { get; set; }
    public double FrameMsMax { get; set; }
    // Frames longer than twice the window's median: stutters, whatever the frame rate
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
    // A part of a live section: the game days the part got through, the speed it ran at and was asked for (1 is real
    // time), the seconds it took to reach 90 % of that (null: it never did), the slowest vehicle job, substeps, and the most
    // physics bubbles and vehicles
    public double? GameDays { get; set; }
    public double? Speed { get; set; }
    public double? SpeedAsked { get; set; }
    public double? SecondsToSpeed { get; set; }
    public double? SimMsMax { get; set; }
    public long? Substeps { get; set; }
    public int? BubblesMax { get; set; }
    public int? VehiclesMax { get; set; }
}

public class SectionResult
{
    public string Name { get; }
    public List<WindowResult> Windows { get; } = new();
    // The screenshots taken, by their names in the script, and how much of those that physics or particles change may
    // differ from run to run (percent)
    public List<string> Screenshots { get; } = new();
    public Dictionary<string, double> ShotLimits { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Live { get; init; }

    public SectionResult(string name) => Name = name;
}

public class Results
{
    public bool GpuTimes { get; }
    // How the script played: "cinematic" (measured along the way) or "fast" (measured at each checkpoint)
    public string Mode { get; init; } = "cinematic";
    public bool Shots { get; init; } = true;
    // Whether the script played to its end
    public bool Completed { get; set; }
    public double LoadSeconds { get; set; }
    // The size in pixels of the images the game presented (a window's points times the display's scale)
    public string? Drawable { get; set; }
    public List<SectionResult> Sections { get; } = new();

    private static readonly JsonSerializerOptions Options =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public Results(bool gpuTimes) => GpuTimes = gpuTimes;

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, Options));
}

// Every frame of the run, one line each (frames.csv): the raw data the parts' summaries come from, kept so that a
// shared run can answer later questions. The driver's columns are those of its latest present (a frame that presented
// nothing repeats them: the present column tells).
public class FrameLog : IDisposable
{
    private readonly StreamWriter _out;
    private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

    public FrameLog(string path)
    {
        _out = new StreamWriter(path);
        _out.WriteLine("time,script,section,part,phase,frame_ms,present,gpu_ms,cpu_ms,draws,passes,metal_mb,footprint_mb," +
                       "thermal,shaders,pipelines,display_ms,game_s,asked,sim_ms,substeps,bubbles,vehicles");
    }

    public void Add(double script, string? section, string? part, string phase, double frameMs, FrameStats? driver, SimSample? sim = null)
    {
        var line = FormattableString.Invariant($"{_since.Elapsed.TotalSeconds:F4},{script:F3},{section},{part},{phase},{frameMs:F3}");
        if (driver is { } d)
        {
            line += FormattableString.Invariant(
                $",{d.Present},{d.GpuMs:F3},{d.CpuMs:F3},{d.Draws},{d.Passes},{d.AllocatedMb},{d.FootprintMb},{d.ThermalState},{d.ShadersCompiled},{d.PipelinesCreated},{d.DisplayMs:F3}");
        }
        else
            line += ",,,,,,,,,,,";
        if (sim is { } m)
            line += FormattableString.Invariant($",{m.GameSeconds:F3},{m.Asked:F3},{m.TickMs:F3},{m.Substeps},{m.Bubbles},{m.Vehicles}");
        else
            line += ",,,,,,";
        _out.WriteLine(line);
    }

    public void Dispose() => _out.Dispose();
}
