// The power channel: the GPU's power and clock while a game session plays, from macOS's IOReport (a private interface,
// read without root, as Activity Monitor's energy figures are), sampled every half second into the run's host.db as
// counters: host/gpu_power (W, from the Energy Model group's GPU Energy), host/gpu_active (the share of the time the GPU
// wasn't off, from GPU Stats' performance states) and host/gpu_clock (MHz, the states' residency weighted by their
// frequencies, the IORegistry's pmgr voltage-states9). Finish puts each sample in the part it fell in, by time. Off
// (--channels -power) or missing (another macOS, a Mac without the group): the run goes on without it, said once.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

internal sealed class Power : IDisposable
{
    private const string IOReport = "/usr/lib/libIOReport.dylib", CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [DllImport(IOReport)] private static extern IntPtr IOReportCopyChannelsInGroup(IntPtr group, IntPtr subgroup, ulong a, ulong b, ulong c);
    [DllImport(IOReport)] private static extern void IOReportMergeChannels(IntPtr into, IntPtr from, IntPtr unused);
    [DllImport(IOReport)] private static extern IntPtr IOReportCreateSubscription(IntPtr unused, IntPtr channels, out IntPtr subscribed, ulong a, IntPtr b);
    [DllImport(IOReport)] private static extern IntPtr IOReportCreateSamples(IntPtr subscription, IntPtr channels, IntPtr unused);
    [DllImport(IOReport)] private static extern IntPtr IOReportCreateSamplesDelta(IntPtr a, IntPtr b, IntPtr unused);
    [DllImport(IOReport)] private static extern IntPtr IOReportChannelGetGroup(IntPtr channel);
    [DllImport(IOReport)] private static extern IntPtr IOReportChannelGetChannelName(IntPtr channel);
    [DllImport(IOReport)] private static extern IntPtr IOReportChannelGetUnitLabel(IntPtr channel);
    [DllImport(IOReport)] private static extern long IOReportSimpleGetIntegerValue(IntPtr channel, int index);
    [DllImport(IOReport)] private static extern int IOReportStateGetCount(IntPtr channel);
    [DllImport(IOReport)] private static extern IntPtr IOReportStateGetNameForIndex(IntPtr channel, int index);
    [DllImport(IOReport)] private static extern long IOReportStateGetResidency(IntPtr channel, int index);

    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
    [DllImport(CF)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CFStringGetCString(IntPtr text, byte[] buffer, long size, uint encoding);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreateMutableCopy(IntPtr allocator, long capacity, IntPtr dictionary);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CF)] private static extern void CFRelease(IntPtr value);

    private const uint Utf8 = 0x08000100;
    private const double Every = 0.5;

    // Neither better nor worse: a faster frame does more of the GPU's work a second
    public static readonly Metric GpuPower = new("host/gpu_power", "GPU power", "W", Better.Neither, Across.Median, 1,
        "the GPU's power, from macOS's energy model") { Limit = 10 };
    public static readonly Metric GpuActive = new("host/gpu_active", "GPU active", "%", Better.Neither, Across.Median, 0,
        "the share of the time the GPU wasn't off");
    public static readonly Metric GpuClock = new("host/gpu_clock", "GPU clock", "MHz", Better.Neither, Across.Median, 0,
        "the GPU's clock while on, its states' time weighted by their frequencies");

    private readonly IntPtr _subscription, _channels, _all;
    private readonly RunStore _store;
    private readonly int _session;
    private readonly double[] _clocks;
    private readonly Action<string> _said;
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _clockSaid;

    private Power(IntPtr subscription, IntPtr channels, IntPtr all, RunStore store, int session, double[] clocks, Action<string> said)
    {
        (_subscription, _channels, _all, _store, _session, _clocks, _said) = (subscription, channels, all, store, session, clocks, said);
        foreach (var m in new[] { GpuPower, GpuActive, GpuClock })
            store.AddMetric(m);
        _thread = new Thread(Sample) { IsBackground = true, Name = "power" };
        _thread.Start();
    }

    // The channel for a game session, or null (said once) when macOS doesn't give it
    public static Power? Start(RunStore? store, int session, Action<string> said)
    {
        if (store == null)
            return null;
        try
        {
            using var energy = new CfString("Energy Model");
            using var gpu = new CfString("GPU Stats");
            using var states = new CfString("GPU Performance States");
            IntPtr e = IOReportCopyChannelsInGroup(energy.Ref, IntPtr.Zero, 0, 0, 0), g = IOReportCopyChannelsInGroup(gpu.Ref, states.Ref, 0, 0, 0);
            if (e == IntPtr.Zero || g == IntPtr.Zero)
            {
                Release(e, g);
                said("No power channel: macOS has no GPU energy or states here.");
                return null;
            }
            var channels = CFDictionaryCreateMutableCopy(IntPtr.Zero, 0, e);
            IOReportMergeChannels(channels, g, IntPtr.Zero);
            Release(e, g);
            var subscription = IOReportCreateSubscription(IntPtr.Zero, channels, out var subscribed, 0, IntPtr.Zero);
            if (subscription == IntPtr.Zero)
            {
                Release(channels, subscribed);
                said("No power channel: macOS refused the subscription.");
                return null;
            }
            return new Power(subscription, subscribed, channels, store, session, Clocks(), said);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            said($"No power channel: {e.Message}");
            return null;
        }
    }

    // The GPU's frequency of each performance state in MHz (the first, off, 0), from the IORegistry; none when it isn't there
    private static double[] Clocks()
    {
        if (Tool.Exec("ioreg", ["-r", "-n", "pmgr", "-l"], out var text) != 0 ||
            Regex.Match(text, "\"voltage-states9\" = <([0-9a-f]+)>") is not { Success: true } m)
            return [];
        var bytes = Convert.FromHexString(m.Groups[1].Value);
        return Enumerable.Range(0, bytes.Length / 8).Select(i => BitConverter.ToUInt32(bytes, i * 8) / 1e6).ToArray();
    }

    // Every half second until stopped; a failure ends the channel, said once, never the run
    private void Sample()
    {
        var last = IntPtr.Zero;
        double lastT = 0;
        try
        {
            while (!_stop)
            {
                var now = IOReportCreateSamples(_subscription, _channels, IntPtr.Zero);
                double t = RunClock.Now();
                if (now != IntPtr.Zero && last != IntPtr.Zero)
                {
                    var delta = IOReportCreateSamplesDelta(last, now, IntPtr.Zero);
                    if (delta != IntPtr.Zero)
                    {
                        try
                        {
                            Read(delta, lastT, t);
                        }
                        finally
                        {
                            CFRelease(delta);
                        }
                    }
                }
                if (now != IntPtr.Zero)
                {
                    Release(last);
                    (last, lastT) = (now, t);
                }
                Thread.Sleep(TimeSpan.FromSeconds(Every));
            }
        }
        catch (Exception e)
        {
            _said($"The power channel stopped: {e.Message}");
        }
        finally
        {
            Release(last);
        }
    }

    // One delta's numbers, from one time to another, as counters at its middle (the part it falls in)
    private void Read(IntPtr delta, double from, double to)
    {
        using var key = new CfString("IOReportChannels");
        var items = CFDictionaryGetValue(delta, key.Ref);
        double dt = to - from, t = (from + to) / 2;
        if (items == IntPtr.Zero || dt <= 0)
            return;
        double? joules = null;
        long on = 0, all = 0;
        double weighted = 0;
        // Time in a state the frequency table doesn't have: no clock then
        bool unknown = false;
        for (long i = 0, n = CFArrayGetCount(items); i < n; i++)
        {
            var c = CFArrayGetValueAtIndex(items, i);
            var group = Text(IOReportChannelGetGroup(c));
            var name = Text(IOReportChannelGetChannelName(c));
            if (group == "Energy Model" && name == "GPU Energy")
            {
                double? scale = Text(IOReportChannelGetUnitLabel(c)) switch { "nJ" => 1e-9, "uJ" or "µJ" => 1e-6, "mJ" => 1e-3, "J" => 1, _ => null };
                if (scale is { } sc)
                    joules = IOReportSimpleGetIntegerValue(c, 0) * sc;
            }
            else if (group == "GPU Stats" && name == "GPUPH")
            {
                for (int s = 0, count = IOReportStateGetCount(c); s < count; s++)
                {
                    long residency = IOReportStateGetResidency(c, s);
                    all += residency;
                    if (Text(IOReportStateGetNameForIndex(c, s)) is "OFF" or "IDLE" || s == 0)
                        continue;
                    on += residency;
                    if (s < _clocks.Length)
                        weighted += residency * _clocks[s];
                    else if (residency > 0)
                        unknown = true;
                }
            }
        }
        if (joules is { } j)
            _store.AddCounter(t, _session, 0, null, null, GpuPower.Name, Math.Round(j / dt, 2));
        if (all > 0)
            _store.AddCounter(t, _session, 0, null, null, GpuActive.Name, Math.Round(100.0 * on / all, 1));
        if (on > 0 && _clocks.Length > 1 && !unknown)
            _store.AddCounter(t, _session, 0, null, null, GpuClock.Name, Math.Round(weighted / on));
        else if (unknown && !_clockSaid)
        {
            _clockSaid = true;
            _said($"The GPU ran in a state past the {_clocks.Length} the IORegistry lists: no GPU clock for that time.");
        }
    }

    private static void Release(params IntPtr[] values)
    {
        foreach (var v in values)
            if (v != IntPtr.Zero)
                CFRelease(v);
    }

    private static string Text(IntPtr cfString)
    {
        if (cfString == IntPtr.Zero)
            return "";
        var buffer = new byte[256];
        return CFStringGetCString(cfString, buffer, buffer.Length, Utf8) ? System.Text.Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0) is var end and >= 0 ? end : buffer.Length) : "";
    }

    public void Dispose()
    {
        _stop = true;
        // Released only once the sampler is done with them
        if (_thread.Join(2000))
            Release(_subscription, _channels, _all);
    }

    // A CFString made from a .NET one, released after use
    private readonly struct CfString(string text) : IDisposable
    {
        public IntPtr Ref { get; } = CFStringCreateWithCString(IntPtr.Zero, text, Utf8);

        public void Dispose()
        {
            if (Ref != IntPtr.Zero)
                CFRelease(Ref);
        }
    }
}
