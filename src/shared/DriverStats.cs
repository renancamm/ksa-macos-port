// What KSA Metal measures for each presented frame, read through its benchmark interface
// (src/game/driver/diagnostics/instruments.mm: ksaMtlFrameStats, ksaMtlScreenshot, ksaMtlMarker, ksaMtlCapture,
// ksaMtlCapturing; passdump.mm: ksaMtlPassDump, ksaMtlPassDumping, ksaMtlGpuErrors; zones.mm: ksaMtlZoneMode, ksaMtlZoneBegin, ksaMtlZoneEnd;
// drain.mm: ksaMtlDrainOpen, ksaMtlDrain, ksaMtlDrainDropped, ksaMtlDrainClose, the records below; instruments.mm:
// ksaMtlMemory).
// The app ships the director with the KSA Metal it was built with, so the driver has all of it. With another Vulkan
// driver the mod still measures CPU frame times.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KsaPort.Common;

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

// What KSA Metal's drain hands over (drain.mm), each with the time on the store's clock (RunClock, seconds)
public abstract record DriverRecord;

// A present: the driver's numbers for it, as ksaMtlFrameStats has them
public sealed record PresentRecord(double T, FrameStats Stats) : DriverRecord;

// When a present's frame began, was presented, finished on the GPU and was shown (0: never shown), how long it waited
// for an earlier frame, and whether pacing ended that wait
public sealed record ShownRecord(ulong Present, double Began, double Presented, double GpuEnd, double Shown, double WaitedMs, bool Paced)
    : DriverRecord;

// A call that blocked the game longer than the drain's threshold: what (a Vulkan call, a wait inside one), from when, how long
public sealed record WaitRecord(double T, double Ms, ulong Present, string What) : DriverRecord;

// An allocation (bytes positive) or a free (negative) of 16 MB or more, of a Vulkan memory type
public sealed record MemoryRecord(double T, long Bytes, uint Type, ulong Present) : DriverRecord;

// A frame's own GPU time, when its present's command buffer completed: the present (as PresentRecord numbers it), when,
// the GPU's busy ms since the frame before's present completed
public sealed record FrameGpuRecord(ulong Present, double T, double GpuMs) : DriverRecord;

public enum ZoneMode : uint { AsTheyCome, Labels, Split }

public static unsafe class DriverStats
{
    // The app's launcher puts the game folder first in the library path, so this is the driver the game uses
    private const string Driver = "libvulkan.1.dylib";

    [DllImport(Driver, EntryPoint = "ksaMtlFrameStats")]
    private static extern uint FrameStatsNative(FrameStats* stats, uint size);

    [DllImport(Driver, EntryPoint = "ksaMtlScreenshot")]
    private static extern void ScreenshotNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(Driver, EntryPoint = "ksaMtlZoneMode")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool ZoneModeNative(uint mode);

    [DllImport(Driver, EntryPoint = "ksaMtlZoneBegin")]
    private static extern void ZoneBeginNative(nint commandBuffer, byte* name);

    [DllImport(Driver, EntryPoint = "ksaMtlZoneEnd")]
    private static extern void ZoneEndNative(nint commandBuffer);

    [DllImport(Driver, EntryPoint = "ksaMtlMarker")]
    private static extern void MarkerNative([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.U1)] bool begin);

    [DllImport(Driver, EntryPoint = "ksaMtlCapture")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CaptureNative([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint frames);

    [DllImport(Driver, EntryPoint = "ksaMtlCapturing")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool CapturingNative();

    [DllImport(Driver, EntryPoint = "ksaMtlPassDump")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool PassDumpNative([MarshalAs(UnmanagedType.LPUTF8Str)] string dir);

    [DllImport(Driver, EntryPoint = "ksaMtlPassDumping")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool PassDumpingNative();

    [DllImport(Driver, EntryPoint = "ksaMtlGpuErrors")]
    private static extern uint GpuErrorsNative();

    [DllImport(Driver, EntryPoint = "ksaMtlMemory")]
    private static extern uint MemoryNative([Out] long[] values, uint count);

    [DllImport(Driver, EntryPoint = "ksaMtlDrainOpen")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool DrainOpenNative(uint version, double waitMs, uint capacity);

    [DllImport(Driver, EntryPoint = "ksaMtlDrain")]
    private static extern uint DrainNative(byte* records, uint capacity);

    [DllImport(Driver, EntryPoint = "ksaMtlDrainDropped")]
    private static extern ulong DrainDroppedNative();

    [DllImport(Driver, EntryPoint = "ksaMtlDrainClose")]
    private static extern void DrainCloseNative();

    // The records' layout, which drain.mm's kDrainVersion names
    private const uint DrainVersion = 1;
    private const int Header = 8;
    private static readonly byte[] DrainBuffer = new byte[64 << 10];

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

    // Every pass's images of the next frame into dir, and all the images at its end
    public static bool PassDump(string dir) => Available && PassDumpNative(dir);

    // Until they are written
    public static bool PassDumping() => Available && PassDumpingNative();

    // The bytes KSA Metal holds now: device memory by type (private, shared write-combined, shared cached), then its own
    // (descriptor pools, upload rings); null with a driver of before it
    public static long[]? Memory()
    {
        if (!Available)
            return null;
        try
        {
            var values = new long[4];
            return MemoryNative(values, 4) == 4 ? values : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    // Command buffers that failed: after a hang the GPU refuses this process's work, and a dump or capture holds nothing
    public static uint GpuErrors() => Available ? GpuErrorsNative() : 0;

    // How KSA Metal answers the game's GPU profiler's timestamps (zones.mm): as they come, labels only, or splitting render
    // passes (needs KSA_MTL_GPU_ZONES=1); false when it can't
    public static bool ZoneMode(ZoneMode mode) => Available && ZoneModeNative((uint)mode);

    // A zone of the game's GPU profiler begins or ends in a recording: the name of the encoders in it (a null-terminated
    // UTF-8 name)
    public static void ZoneBegin(nint commandBuffer, byte* name) => ZoneBeginNative(commandBuffer, name);

    public static void ZoneEnd(nint commandBuffer) => ZoneEndNative(commandBuffer);

    // Starts KSA Metal recording into its drain: from then on every present, every frame shown, every wait of at least
    // waitMs and every allocation of 16 MB or more; false when the driver has no drain of this layout
    public static bool OpenDrain(double waitMs)
    {
        if (!Available)
            return false;
        try
        {
            return DrainOpenNative(DrainVersion, waitMs, 1 << 20);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Everything recorded since the last call, in the order it came; the drain keeps what doesn't fit for the next
    public static void Drain(List<DriverRecord> into)
    {
        lock (DrainBuffer)
        {
            fixed (byte* p = DrainBuffer)
            {
                uint n;
                do
                {
                    n = DrainNative(p, (uint)DrainBuffer.Length);
                    Parse(new ReadOnlySpan<byte>(p, (int)n), into);
                } while (n > DrainBuffer.Length / 2);
            }
        }
    }

    // Records lost because the drain was full when they came
    public static ulong DrainDropped() => DrainDroppedNative();

    // Stops the driver recording into the drain; what wasn't read is gone
    public static void CloseDrain() => DrainCloseNative();

    // The records in bytes the drain wrote: each a header (type, size of the whole record, 4 bytes unused), then its fields
    public static void Parse(ReadOnlySpan<byte> bytes, List<DriverRecord> into)
    {
        while (bytes.Length >= Header)
        {
            ushort type = MemoryMarshal.Read<ushort>(bytes), size = MemoryMarshal.Read<ushort>(bytes[2..]);
            if (size < Header || size > bytes.Length)
                return;
            var r = bytes[Header..size];
            switch (type)
            {
                case 1 when r.Length >= 8 + sizeof(FrameStats):
                    into.Add(new PresentRecord(D(r, 0), MemoryMarshal.Read<FrameStats>(r[8..])));
                    break;
                case 2 when r.Length >= 56:
                    into.Add(new ShownRecord(U(r, 0), D(r, 8), D(r, 16), D(r, 24), D(r, 32), D(r, 40), MemoryMarshal.Read<uint>(r[48..]) != 0));
                    break;
                case 3 when r.Length >= 64:
                    var what = r.Slice(24, 40);
                    int end = what.IndexOf((byte)0);
                    into.Add(new WaitRecord(D(r, 0), D(r, 8), U(r, 16), Encoding.UTF8.GetString(end < 0 ? what : what[..end])));
                    break;
                case 4 when r.Length >= 32:
                    into.Add(new MemoryRecord(D(r, 0), MemoryMarshal.Read<long>(r[8..]), MemoryMarshal.Read<uint>(r[16..]), U(r, 24)));
                    break;
                case 5 when r.Length >= 24:
                    into.Add(new FrameGpuRecord(U(r, 0), D(r, 8), D(r, 16)));
                    break;
            }
            bytes = bytes[size..];
        }
    }

    private static double D(ReadOnlySpan<byte> r, int at) => MemoryMarshal.Read<double>(r[at..]);

    private static ulong U(ReadOnlySpan<byte> r, int at) => MemoryMarshal.Read<ulong>(r[at..]);

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
