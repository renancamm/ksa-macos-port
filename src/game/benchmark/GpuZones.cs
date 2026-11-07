// The game's GPU zones in a run (a profiled pass, and RunPlan.Record.ZoneLabels): KSA Metal's zone mode (src/game/driver/
// diagnostics/zones.mm) and the game's GPU profiler on. Zones: KSA Metal splits render passes at the profiler's timestamps,
// so a zone inside a pass reads its own time, and GameProfilers reads the profiler's frames with the CPU ones (group gpu).
// Zone labels: KSA Metal answers the timestamps at once, and a postfix on the profiler's zone begin and end (the inlined
// Begin and End call them only while it is active) tells it the zone each encoder runs in, which names the encoder in a
// trace or a frame capture. In a game started with gpu_zones labels split and read the zones too, so that a trace times
// the same encoders as the zones, one for each; not when the run has a profiled pass, which splits them for itself. The
// postfixes go in the first time a run asks for labels and do nothing outside one. All of it goes back as it was at the
// run's end.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Brutal.VulkanApi;
using KSA;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

internal sealed class GpuZones
{
    // The hooks' Harmony: a postfix on a method of the game (Runs.Install)
    public static Action<MethodBase, MethodInfo>? Patch;
    private static bool _patched;
    private static volatile bool _labels;
    // Each zone's name as KSA Metal takes it, null-terminated UTF-8, made once (about 150 of them, for the game's life)
    private static readonly Dictionary<ProfilerTag, nint> Names = new(ReferenceEqualityComparer.Instance);

    private readonly bool _wasActive, _wasPaused;

    // Whether KSA Metal splits render passes at the zones, so that GameProfilers reads them
    public bool Split { get; }

    private GpuZones(bool labels, bool split)
    {
        Split = split;
        (_wasActive, _wasPaused) = (Profiler.Gpu.IsActive, Profiler.Gpu.Paused);
        Profiler.Gpu.IsActive = true;
        Profiler.Gpu.Paused = false;
        _labels = labels;
    }

    // On, the zones split and read (a profiled pass) or the encoders named after them (labels), or both; null when
    // neither is asked for, or when KSA Metal or the game can't. Labels alone split too in a game started with
    // gpu_zones, unless splitting is the profiled pass's alone (mayAlwaysSplit false)
    public static GpuZones? Start(bool zones, bool labels, Action<string> log, bool mayAlwaysSplit = true)
    {
        if (!zones && !labels)
            return null;
        if (!Profiler.Gpu.IsSupported)
        {
            log("the game's GPU zones: its GPU profiler isn't supported here");
            return null;
        }
        // Split when asked, or when the game has gpu_zones (KSA Metal refuses it otherwise)
        bool split = (zones || mayAlwaysSplit) && DriverStats.ZoneMode(ZoneMode.Split);
        if (!split && (zones || !DriverStats.ZoneMode(ZoneMode.Labels)))
        {
            log(zones ? "the game's GPU zones: KSA Metal can't split its render passes (does the game have KSA_MTL_GPU_ZONES=1?)"
                : "zone labels: KSA Metal has no zone mode");
            return null;
        }
        if (labels && !PatchLabels(log))
        {
            log("zone labels: the game's zone begin and end can't be patched; encoders keep their pipelines' names");
            // Without names, labels are of no use: the game's timestamps as they came
            if (!split)
            {
                DriverStats.ZoneMode(ZoneMode.AsTheyCome);
                return null;
            }
        }
        return new GpuZones(labels && _patched, split);
    }

    // The run's end: as they were
    public void Stop()
    {
        _labels = false;
        DriverStats.ZoneMode(ZoneMode.AsTheyCome);
        Profiler.Gpu.IsActive = _wasActive;
        Profiler.Gpu.Paused = _wasPaused;
    }

    private static bool PatchLabels(Action<string> log)
    {
        if (_patched)
            return true;
        if (Patch is not { } patch)
            return false;
        try
        {
            const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
            patch(typeof(GpuProfiler).GetMethod("BeginActive", Instance) ?? throw new MissingMethodException("GpuProfiler.BeginActive"),
                typeof(GpuZones).GetMethod(nameof(Begun), BindingFlags.Static | BindingFlags.NonPublic)!);
            patch(typeof(GpuProfiler).GetMethod("EndActive", Instance) ?? throw new MissingMethodException("GpuProfiler.EndActive"),
                typeof(GpuZones).GetMethod(nameof(Ended), BindingFlags.Static | BindingFlags.NonPublic)!);
            _patched = true;
        }
        catch (Exception e)
        {
            log($"zone labels: {e.Message}");
        }
        return _patched;
    }

    private static unsafe void Begun(ProfilerTag tag, CommandBuffer commandBuffer)
    {
        if (!_labels)
            return;
        VkCommandBuffer handle = commandBuffer.Handle;
        DriverStats.ZoneBegin(Unsafe.As<VkCommandBuffer, nint>(ref handle), (byte*)Name(tag));
    }

    private static void Ended(CommandBuffer commandBuffer)
    {
        if (!_labels)
            return;
        VkCommandBuffer handle = commandBuffer.Handle;
        DriverStats.ZoneEnd(Unsafe.As<VkCommandBuffer, nint>(ref handle));
    }

    private static nint Name(ProfilerTag tag)
    {
        lock (Names)
        {
            if (!Names.TryGetValue(tag, out var name))
                Names[tag] = name = Marshal.StringToCoTaskMemUTF8(Encoding.UTF8.GetString(tag.RawBytes));
            return name;
        }
    }
}
