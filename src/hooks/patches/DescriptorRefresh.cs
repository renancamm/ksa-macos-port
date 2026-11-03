using System;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace KsaMetalHooks.Patches;

// A game bug: renderers rewrite a descriptor when LastFrameUpdated < DeviceVector.FullLastFrameMoved, the later of two
// frame numbers: when the vector last moved and when the shared pool (DeviceHostSharedMemory) last got a new buffer. A
// change after the rewrite but in the same frame compares equal, so that frame slot keeps the old address, inside a
// disposed buffer, until the vector moves again. The glow (StaticCelestialDistanceRendering) then reads whatever took
// that memory, and every other frame comes out white. These postfixes make the two numbers versions that rise with
// every change, so no change can compare equal. Loading runs them hundreds ahead of the frame count. Nothing reads them
// as frames, but most renderers store the frame count as LastFrameUpdated, so they rewrite their descriptors every
// frame until it catches up.
internal static class DescriptorRefresh
{
    private static readonly object Lock = new();
    private static ulong _version;

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(DeviceHostSharedMemory), "Resize"),
            postfix: new HarmonyMethod(typeof(DescriptorRefresh), nameof(PoolResized)));
        harmony.Patch(Find.Method(typeof(DeviceVector), nameof(DeviceVector.Resize)),
            postfix: new HarmonyMethod(typeof(DescriptorRefresh), nameof(VectorMoved)));
        return "on";
    }

    private static void PoolResized(DeviceHostSharedMemory __instance) => __instance.WrappedBuffer.FrameAllocated = NextVersion();

    private static void VectorMoved(DeviceVector __instance) => __instance.FrameMoved = NextVersion();

    private static ulong NextVersion()
    {
        lock (Lock)
            return _version = Math.Max(_version, Program.GetRenderer().FrameCount) + 1;
    }
}
