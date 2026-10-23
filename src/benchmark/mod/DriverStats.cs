// What KSA Metal measures for each presented frame, read through its benchmark interface (src/ksa-metal/swapchain.mm,
// ksaMtlFrameStats and ksaMtlScreenshot). With another Vulkan driver the mod still measures CPU frame times.
using System;
using System.Runtime.InteropServices;

namespace KsaMetalBenchmark;

[StructLayout(LayoutKind.Sequential)]
public struct FrameStats
{
    public ulong Present;
    public double CpuMs, GpuMs;
    public uint Draws, Dispatches, Passes, Submits;
    public long AllocatedMb;
}

public static unsafe class DriverStats
{
    // The app's launcher puts the game folder first in the library path, so this is the driver the game uses
    private const string Driver = "libvulkan.1.dylib";

    [DllImport(Driver, EntryPoint = "ksaMtlFrameStats")]
    private static extern uint FrameStatsNative(FrameStats* stats, uint size);

    [DllImport(Driver, EntryPoint = "ksaMtlScreenshot")]
    private static extern void ScreenshotNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Driver, EntryPoint = "ksaMtlProfileFrames")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool ProfileFramesNative([MarshalAs(UnmanagedType.LPUTF8Str)] string label, uint count);

    public static readonly bool Available = Probe();

    // The driver's GPU profiler (KSA_MTL_GPUPROF) for the next frames; false when it is off
    public static bool ProfileFrames(string label, uint count)
    {
        try
        {
            return Available && ProfileFramesNative(label, count);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool Probe()
    {
        try
        {
            FrameStats s;
            return FrameStatsNative(&s, (uint)sizeof(FrameStats)) == sizeof(FrameStats);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public static FrameStats? Read()
    {
        if (!Available)
            return null;
        FrameStats s;
        FrameStatsNative(&s, (uint)sizeof(FrameStats));
        return s;
    }

    public static bool Screenshot(string path)
    {
        if (!Available)
            return false;
        ScreenshotNative(path);
        return true;
    }
}
