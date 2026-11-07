// The game's own profilers as the director's zones (ZoneLog, src/shared/benchmark/Zones.cs): switched on while a run plays,
// and read every frame for the frames they finished since. Sources: the main thread's (Profiler.MainThread, a frame each
// game frame) and the job runners' of the game's job systems (JobSystems.Profilers: the orbit and vehicle solvers and the
// others, a frame each game frame they had jobs; the game arms them on the main thread before each batch), and with the
// game's GPU zones (GpuZones.cs: a profiled pass) its GPU profiler's (group gpu: a frame each game frame, published once
// the GPU has run it, so a frame or two late, its start the time it was read). The worker pools'
// profilers stay off: switched while a worker is in a burst, the game's profiler is left with a scope open and throws on
// that worker. Each source's switches are put back at the end. KSA_BENCH_ZONES=0 leaves them all off, to measure what
// they cost.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using KSA;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

internal sealed class GameProfilers
{
    private sealed class Source(string group, IProfilerSource profiler)
    {
        public readonly string Group = group;
        public readonly IProfilerSource Profiler = profiler;
        public readonly bool WasActive = profiler.IsActive, WasPaused = profiler.Paused;
        public int Last = -1;
    }

    private readonly List<Source> _sources = new();
    private readonly Dictionary<int, string> _names = new();
    // The game's profilers time with Stopwatch; the store's clock is the same Mac clock, give or take this
    private readonly double _offset;
    private readonly Action<string> _log;
    // What reading and summing cost the game's main thread: in all, the frames, the worst frame
    private long _spent, _worst;
    private int _frames;
    // When the last frame ended, on the store's clock
    private double _now;

    public ZoneLog Zones { get; }

    private GameProfilers(RunStore? store, Action<string> log, bool gpu)
    {
        _log = log;
        _offset = RunClock.Now() - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        Zones = new ZoneLog(store, Name);
        _sources.Add(new Source("main", Profiler.MainThread));
        foreach (var scheduler in JobSystems.Profilers)
            foreach (var runner in scheduler.Runners)
                _sources.Add(new Source(scheduler.Name, runner));
        if (gpu)
            _sources.Add(new Source("gpu", Profiler.Gpu));
        foreach (var s in _sources)
        {
            s.Profiler.IsActive = true;
            s.Profiler.Paused = false;
        }
    }

    // On for a run, unless KSA_BENCH_ZONES=0, with the GPU profiler's zones or without; null when off or when this game's
    // profilers can't be reached
    public static GameProfilers? Start(RunStore? store, Action<string> log, bool gpu)
    {
        if (Environment.GetEnvironmentVariable("KSA_BENCH_ZONES") == "0")
            return null;
        try
        {
            return new GameProfilers(store, log, gpu);
        }
        catch (Exception e) when (e is MissingMemberException or TypeLoadException or NullReferenceException)
        {
            log($"the game's profilers: {e.Message}; no zones in this run");
            return null;
        }
    }

    // Every frame: the sources read, and the game frame's zones counted in its part (none: null)
    public void Frame(PartKey? part)
    {
        long started = Stopwatch.GetTimestamp();
        // On again each frame: the game's profiler window, once a player opened it, sets them every frame
        foreach (var s in _sources)
        {
            s.Profiler.IsActive = true;
            s.Profiler.Paused = false;
        }
        double now = RunClock.Now();
        Read();
        Zones.EndFrame(now, part);
        _now = now;
        long spent = Stopwatch.GetTimestamp() - started;
        _spent += spent;
        _worst = Math.Max(_worst, spent);
        _frames++;
    }

    // The frame Frame just counted, of that many ms, kept as a moment of this part (a trigger's)
    public void Moment(double frameMs, PartKey part) => Zones.Hitch(_now, frameMs, part);

    // The GPU profiler's frames read too, from now (a profiled pass begins), or no more (it ends): its switches are
    // GpuZones'
    public void Gpu(bool on)
    {
        if (on && !_sources.Exists(s => s.Group == "gpu"))
            _sources.Add(new Source("gpu", Profiler.Gpu));
        else if (!on)
            _sources.RemoveAll(s => s.Group == "gpu");
    }

    // What the sources finished since
    private void Read()
    {
        foreach (var s in _sources)
        {
            var frames = s.Profiler.Frames;
            // The main thread's frame ends inside Program.OnFrame, before the director's turn: its current frame is complete
            int last = s.Group == "main" ? s.Profiler.CurrentFrameIndex : s.Profiler.LastCompleteFrameIndex;
            if (last < 0 || frames.Length == 0)
                continue;
            // From the run's start on
            if (s.Last < 0)
            {
                s.Last = last;
                continue;
            }
            for (int n = 0; s.Last != last && n < frames.Length; n++)
            {
                s.Last = (s.Last + 1) % frames.Length;
                if (Convert(s, frames[s.Last]) is { } frame)
                    Zones.Add(frame);
            }
        }
    }

    private ZoneFrame? Convert(Source source, ProfilerFrame frame)
    {
        if (!frame.IsValid || frame.SampleCount == 0)
            return null;
        var samples = frame.AsSpan();
        double toSeconds = 1.0 / Stopwatch.Frequency, start = frame.WallTimestamp * toSeconds + _offset;
        var zones = new ZoneSample[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            var s = samples[i];
            zones[i] = new ZoneSample(s.IdIndex, s.ParentIndex == ushort.MaxValue ? -1 : s.ParentIndex, start + s.StartTicks * toSeconds,
                s.Ticks * toSeconds * 1000, (long)s.AllocatedBytes);
        }
        return new ZoneFrame(source.Group, source.Profiler.Name, zones);
    }

    // A zone's name, on any thread (the zones' writing tasks ask)
    private string Name(int index)
    {
        lock (_names)
        {
            if (_names.TryGetValue(index, out var name))
                return name;
            try
            {
                name = Encoding.UTF8.GetString(Profiler.GetTag(index).RawBytes);
            }
            catch (Exception e) when (e is IndexOutOfRangeException or NullReferenceException)
            {
                name = $"zone {index}";
            }
            _names[index] = name;
            return name;
        }
    }

    // The run's end: what is left written, the switches as they were
    public void Stop()
    {
        try
        {
            Read();
            Zones.Close();
        }
        finally
        {
            foreach (var s in _sources)
            {
                s.Profiler.IsActive = s.WasActive;
                s.Profiler.Paused = s.WasPaused;
            }
        }
        double us = 1e6 / Stopwatch.Frequency;
        _log(FormattableString.Invariant(
            $"the game's profilers: {_sources.Count} sources, read in {(_frames > 0 ? _spent * us / _frames : 0):F1} us a frame (worst {_worst * us:F0} us) over {_frames} frames"));
    }
}
