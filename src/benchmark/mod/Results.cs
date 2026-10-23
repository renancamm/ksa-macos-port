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
    private long _memoryPeakMb;
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
    public List<SectionResult> Sections { get; } = new();

    public Results(bool gpuTimes) => GpuTimes = gpuTimes;

    public void Write(string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(path, JsonSerializer.Serialize(this, options));
    }
}
