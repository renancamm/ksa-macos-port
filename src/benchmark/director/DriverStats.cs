// What KSA Metal measures for each presented frame, read through its benchmark interface (src/ksa-metal/swapchain.mm:
// ksaMtlFrameStats, ksaMtlScreenshot, ksaMtlMarker, ksaMtlCapture, ksaMtlCapturing; profile.mm: ksaMtlProfileFrames). The
// app ships the director with the KSA Metal it was built with, so the driver has all of it. With another Vulkan driver
// the mod still measures CPU frame times.
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
    public int ThermalState;
    public uint ShadersCompiled, PipelinesCreated, DrawableWidth, DrawableHeight, Reserved;
    public long FootprintMb;
    public double DisplayMs;
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

    [DllImport(Driver, EntryPoint = "ksaMtlMarker")]
    private static extern void MarkerNative([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.U1)] bool begin);

    [DllImport(Driver, EntryPoint = "ksaMtlCapture")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CaptureNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint frames);

    [DllImport(Driver, EntryPoint = "ksaMtlCapturing")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CapturingNative();

    // Whether the game's driver is KSA Metal, filling every field of FrameStats
    public static readonly bool Available = Probe();

    // Begins or ends a signpost interval named after a measured part, for Instruments traces
    public static void Marker(string name, bool begin)
    {
        if (Available)
            MarkerNative(name, begin);
    }

    // An Xcode GPU frame capture of the next frames into path (.gputrace); false when it can't (the game needs
    // MTL_CAPTURE_ENABLED=1)
    public static bool Capture(string path, uint frames) => Available && CaptureNative(path, frames);

    // Until the capture's file is written
    public static bool Capturing() => Available && CapturingNative();

    // The driver's GPU profiler (KSA_MTL_GPUPROF) for the next frames; false when it is off
    public static bool ProfileFrames(string label, uint count) => Available && ProfileFramesNative(label, count);

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
        FrameStats s = default;
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
