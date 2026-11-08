// Every frame of a run (the frames table of the run's store, Store.cs), a row each: the raw data the parts' numbers come
// from (Metrics.cs), kept so that a shared run can answer later questions, and a better summary applies to old runs too. Frame time is the
// time between two game frames, as the player feels it (it includes waiting for Vsync); GPU time is the time the GPU
// spent on a presented frame, which KSA Metal reports a few frames late (nothing over a part of seconds). The driver's
// columns are those of its latest present: a frame that presented nothing repeats them, the present column tells.
using System;
using System.Collections.Generic;
using System.Linq;

namespace KsaPort.Common.Benchmark;

// What the simulation did in a frame: the game's clock, the speed asked for, the slowest vehicle job, substeps so far,
// physics bubbles and vehicles
public readonly record struct SimSample(double GameSeconds, double Asked, double TickMs, long Substeps, int Bubbles, int Vehicles);

// What the .NET runtime did in a frame: GC pause, collections by generation, JIT time and methods compiled, lock
// contentions, MB allocated (RuntimeCounters)
public readonly record struct RuntimeSample(double GcPauseMs, int Gen0, int Gen1, int Gen2, double JitMs, int JitMethods, long Contentions,
    double AllocMb);

// The .NET runtime's own counters, each frame's share: free to read, so every run has them
public sealed class RuntimeCounters
{
    private TimeSpan _pause = GC.GetTotalPauseDuration(), _jit = System.Runtime.JitInfo.GetCompilationTime();
    private int _gen0 = GC.CollectionCount(0), _gen1 = GC.CollectionCount(1), _gen2 = GC.CollectionCount(2);
    private long _methods = System.Runtime.JitInfo.GetCompiledMethodCount(), _contentions = System.Threading.Monitor.LockContentionCount,
        _allocated = GC.GetTotalAllocatedBytes();

    // Since the last call
    public RuntimeSample Sample()
    {
        var pause = GC.GetTotalPauseDuration();
        var jit = System.Runtime.JitInfo.GetCompilationTime();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long methods = System.Runtime.JitInfo.GetCompiledMethodCount(), contentions = System.Threading.Monitor.LockContentionCount,
            allocated = GC.GetTotalAllocatedBytes();
        var s = new RuntimeSample((pause - _pause).TotalMilliseconds, gen0 - _gen0, gen1 - _gen1, gen2 - _gen2, (jit - _jit).TotalMilliseconds,
            (int)(methods - _methods), contentions - _contentions, (allocated - _allocated) / 1048576.0);
        (_pause, _jit, _gen0, _gen1, _gen2, _methods, _contentions, _allocated) = (pause, jit, gen0, gen1, gen2, methods, contentions, allocated);
        return s;
    }
}

// A frame as the store has it. Part is the part whose numbers the frame counts in, empty when none does; GC pause and JIT
// time are the .NET runtime's in the frame (none in a frame the runtime's counters didn't see)
public sealed record FrameRow(double Time, int Session, int Pass, double Script, string Scenario, string Part, string Phase, double FrameMs,
    ulong Present, double? GpuMs, uint Draws, long MetalMb, long FootprintMb, int Thermal, uint Shaders, uint Pipelines,
    double? GameSeconds, double? Asked, double? SimMs, long? Substeps, int? Bubbles, int? Vehicles, double? GcPauseMs = null, double? JitMs = null);

public sealed class FrameLog
{
    private readonly RunStore? _store;
    private readonly int _session;

    // Which time the scenario plays in this session (a run's passes, from 1)
    public int Pass { get; set; } = 1;
    private readonly System.Diagnostics.Stopwatch _since = System.Diagnostics.Stopwatch.StartNew();

    // A session's frames, after those of the sessions before it
    public FrameLog(string dir, int session)
    {
        _session = session;
        _store = RunStore.For(dir);
    }

    public void Add(double script, string? scenario, string? part, string phase, double frameMs, FrameStats? driver, SimSample? sim,
        RuntimeSample? runtime = null) =>
        _store?.AddFrame(Columns(RunClock.Now(), _since.Elapsed.TotalSeconds, _session, Pass, script, scenario, part, phase, frameMs, driver, sim, runtime));

    // A frame's row in the store's order (its time on the store's clock first, the runtime's work in it last), times
    // rounded (to 0.1 ms, the rest to the thousandth); null where the driver, the simulation or the runtime said nothing.
    // One array a frame: the recorder's own allocations fall in the frames it measures
    public static object?[] Columns(double t, double time, int session, int pass, double script, string? scenario, string? part, string phase,
        double frameMs, FrameStats? driver, SimSample? sim, RuntimeSample? runtime = null)
    {
        static double R(double x, int digits) => Math.Round(x, digits, MidpointRounding.AwayFromZero);
        var d = driver ?? default;
        var m = sim ?? default;
        var r = runtime ?? default;
        bool hasDriver = driver != null, hasSim = sim != null, has = runtime != null;
        return
        [
            t, R(time, 4), session, pass, R(script, 3), scenario ?? "", part ?? "", phase, R(frameMs, 3),
            hasDriver ? d.Present : null, hasDriver ? R(d.GpuMs, 3) : null, hasDriver ? R(d.CpuMs, 3) : null, hasDriver ? d.Draws : null,
            hasDriver ? d.Passes : null, hasDriver ? d.AllocatedMb : null, hasDriver ? d.FootprintMb : null, hasDriver ? d.ThermalState : null,
            hasDriver ? d.ShadersCompiled : null, hasDriver ? d.PipelinesCreated : null, hasDriver ? R(d.DisplayMs, 3) : null,
            hasSim ? R(m.GameSeconds, 3) : null, hasSim ? R(m.Asked, 3) : null, hasSim ? R(m.TickMs, 3) : null, hasSim ? m.Substeps : null,
            hasSim ? m.Bubbles : null, hasSim ? m.Vehicles : null,
            has ? r.GcPauseMs : null, has ? r.Gen0 : null, has ? r.Gen1 : null, has ? r.Gen2 : null, has ? r.JitMs : null, has ? r.JitMethods : null,
            has ? r.Contentions : null, has ? r.AllocMb : null,
        ];
    }

    // Into the store's file now: the director does it at every part's end and every second, so that a game that dies
    // keeps its frames (the store writes four times a second besides)
    public void Flush() => _store?.Wake();

    // The frames of a run's folder, in the order they came; what this process gave its store is written first
    public static List<FrameRow> Read(string dir)
    {
        RunStore.Flush(dir);
        return RunStore.ReadFrames(dir);
    }
}
