using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime;
using HarmonyLib;
using KSA;

namespace KsaMetal.Hooks.Optimizations;

// Freed memory, the game's half of Release Freed Memory (the launcher sets macOS's allocator's). Loading leaves the
// .NET heap with holes where its temporary data was: the background collections that run while the game plays sweep
// them but never move objects, so the holes stay committed for the session (Sol: 250-300 MB of a 550 MB heap). One
// blocking collection that compacts the heap, the large object heap too, runs at the end of a load: the game's start
// (a postfix on Program's constructor, while the loading screen still shows) and a saved game's load (a postfix on
// UncompressedSave.ApplyLoaded, after the game has let go of what it no longer needs; the load's pause grows by it). It
// takes 20-300 ms. Since KSA 5949 UncompressedSave.Load reads the save and hands it to ApplyLoaded, at once or, for a
// save made in another system, a frame later after switching to it: the compaction then also takes the old system's
// teardown.
[Hook(Phase.Default, Switch = nameof(Settings.ReleaseFreedMemory))]
internal static class FreedMemory
{
    public static string Apply(Harmony harmony)
    {
        var start = Find.Constructor(typeof(Program), typeof(IReadOnlyList<string>));
        var load = Find.Method(typeof(UncompressedSave), "ApplyLoaded");
        harmony.Patch(start, postfix: new HarmonyMethod(typeof(FreedMemory), nameof(Compact)));
        harmony.Patch(load, postfix: new HarmonyMethod(typeof(FreedMemory), nameof(Compact)));
        return "on";
    }

    private static void Compact()
    {
        long before = GC.GetGCMemoryInfo().HeapSizeBytes;
        var watch = Stopwatch.StartNew();
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        Hooks.Log($"{nameof(FreedMemory)}: .NET heap {before >> 20} -> {GC.GetGCMemoryInfo().HeapSizeBytes >> 20} MB in " +
                  $"{watch.ElapsedMilliseconds} ms");
    }
}
