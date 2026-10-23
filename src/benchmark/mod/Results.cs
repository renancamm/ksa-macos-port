// Measurement windows and the run's results file (written again after every section, so a crash keeps the ones
// before it).
//
// Frame time is the time between two game frames, as the player feels it (it includes waiting for V-Sync). GPU time
// is the time the GPU spent on a presented frame; KSA Metal reports it a few frames late, which does not matter over
// a window of seconds.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KsaMetalBenchmark;

public class Window
{
    private readonly List<double> _frameMs = new();
    private readonly List<double> _gpuMs = new();
    private readonly List<double> _draws = new();
    private long _memoryPeakMb, _footprintPeakMb;
    private int _thermalMax = -1;
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
            if (DriverStats.Full)
            {
                _footprintPeakMb = Math.Max(_footprintPeakMb, d.FootprintMb);
                _thermalMax = Math.Max(_thermalMax, d.ThermalState);
                if (_gpuMs.Count == 1)
                    (_shadersFirst, _pipelinesFirst) = (d.ShadersCompiled, d.PipelinesCreated);
                (_shadersLast, _pipelinesLast) = (d.ShadersCompiled, d.PipelinesCreated);
            }
        }
    }

    public WindowResult Summarize()
    {
        var frames = _frameMs.OrderBy(x => x).ToArray();
        var gpu = _gpuMs.OrderBy(x => x).ToArray();
        double seconds = _frameMs.Sum() / 1000;
        return new WindowResult
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
            FootprintPeakMb = _thermalMax >= 0 ? _footprintPeakMb : null,
            ThermalMax = _thermalMax >= 0 ? _thermalMax : null,
            ShadersCompiled = _thermalMax >= 0 ? (int)(_shadersLast - _shadersFirst) : null,
            PipelinesCreated = _thermalMax >= 0 ? (int)(_pipelinesLast - _pipelinesFirst) : null,
        };
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

    public Results(bool gpuTimes) => GpuTimes = gpuTimes;

    public void Write(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }
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
                       "thermal,shaders,pipelines,display_ms");
    }

    public void Add(double script, string? section, string? part, string phase, double frameMs, FrameStats? driver)
    {
        var line = FormattableString.Invariant($"{_since.Elapsed.TotalSeconds:F4},{script:F3},{section},{part},{phase},{frameMs:F3}");
        if (driver is { } d)
        {
            line += FormattableString.Invariant($",{d.Present},{d.GpuMs:F3},{d.CpuMs:F3},{d.Draws},{d.Passes},{d.AllocatedMb}");
            line += DriverStats.Full
                ? FormattableString.Invariant($",{d.FootprintMb},{d.ThermalState},{d.ShadersCompiled},{d.PipelinesCreated},{d.DisplayMs:F3}")
                : ",,,,,";
        }
        else
            line += ",,,,,,,,,,,";
        _out.WriteLine(line);
    }

    public void Dispose() => _out.Dispose();
}
