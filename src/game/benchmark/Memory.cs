// The memory probe (--probe memory): who holds the game's memory. Once a second its counters: the game's VRAM (the
// device-local memory it allocated, which the game counts by tag on every allocation), its host-visible memory, KSA
// Metal's own (device memory of every type and its pools and rings), the .NET heap. At every checkpoint, after the
// screenshot, a census: each of the game's memory tags with its bytes ("CascadedShadowMap.ShadowTarget.Depth"), KSA
// Metal's memory by type, the .NET heap and the process's footprint, into the run's store (census) and its record. It
// reads what the game and the driver count anyway: no patch, nothing in the frame's way.
using System;
using System.Text;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

internal sealed class MemoryProbe : Probe
{
    private Counter _vram = null!, _hostVisible = null!, _driver = null!, _gc = null!;
    private double _next;

    public override string Name => "memory";

    public override string Says => "the memory census at each checkpoint";

    public override Cost Cost => Cost.Free;

    public override void Start()
    {
        _vram = Host.Metric("mem/game_vram", "Game VRAM", "MB", Better.Lower, Across.Median, 0, "the device memory the game allocated", 5);
        _hostVisible = Host.Metric("mem/game_host_visible", "Host-visible", "MB", Better.Lower, Across.Median, 0,
            "the memory the game allocated for the CPU to write", 5);
        _driver = Host.Metric("mem/driver", "KSA Metal", "MB", Better.Lower, Across.Median, 0, "KSA Metal's device memory, pools and rings", 5);
        _gc = Host.Metric("mem/gc_heap", "GC heap", "MB", Better.Lower, Across.Median, 0, ".NET's managed heap", 10);
        Count();
    }

    public override void Frame(double wall, double script, string phase)
    {
        if (wall < _next)
            return;
        _next = wall + 1;
        Count();
    }

    private void Count()
    {
        long vram = 0;
        for (int i = 0; i < KSA.Profiler.TagCount; i++)
            vram += Math.Max(0, KSA.Profiler.ReadVramBytes(i));
        _vram.Set(Mb(vram));
        _hostVisible.Set(Mb(KSA.Profiler.ReadHostVisibleVramBytes()));
        if (DriverStats.Memory() is { } driver)
            _driver.Set(Mb(driver[0] + driver[1] + driver[2] + driver[3]));
        _gc.Set(Mb(GC.GetGCMemoryInfo().HeapSizeBytes));
    }

    private static double Mb(long bytes) => Math.Round(bytes / 1048576.0, 1);

    public override Wait? AfterShot(View view)
    {
        for (int i = 0; i < KSA.Profiler.TagCount; i++)
        {
            long bytes = KSA.Profiler.ReadVramBytes(i);
            if (bytes > 0)
                Host.Census(Encoding.UTF8.GetString(KSA.Profiler.GetTag(i).RawBytes).TrimEnd('\0'), "game", bytes);
        }
        if (KSA.Profiler.ReadHostVisibleVramBytes() is > 0 and var host)
            Host.Census("host-visible", "host", host);
        if (DriverStats.Memory() is { } driver)
        {
            string[] kinds = ["private", "shared write-combined", "shared cached", "pools and rings"];
            for (int i = 0; i < kinds.Length; i++)
                if (driver[i] > 0)
                    Host.Census(kinds[i], "driver", driver[i]);
        }
        var gc = GC.GetGCMemoryInfo();
        Host.Census("managed heap", "runtime", gc.HeapSizeBytes);
        if (DriverStats.Read() is { FootprintMb: > 0 } stats)
            Host.Census("footprint", "process", stats.FootprintMb << 20);
        return null;
    }
}
