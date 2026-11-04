using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Planet;
using RenderCore;
using RenderCore.Mesh;

namespace KsaMetal.Hooks.Optimizations;

// Far terrain. A view draws the terrain of one body only, the one within 80,000 km of its surface
// (Camera.GetEffectiveNearbyCelestial), at one level of detail, yet PlanetMeshLibrary.GenerateInUse builds every level
// of every terrain mesh collection the system uses (18 levels of 20-60 MB each, 1.4 GB in the solar system) and keeps
// them all. Now, as soon as loading has built a collection, it keeps only its finest level, which holds the
// acceleration structure ground clutter is placed with (clutter's own sets name it), except the collection of the
// system's home body; its other levels point at the finest's buffers, so whatever is written with them meanwhile stays
// valid. A collection is built again on worker threads when one of its bodies comes within 150,000 km of a camera,
// and swapped in at the start of a frame (Program.OnFrameCelestials, before the frame's views are prepared); one a
// view is about to draw that isn't built (a jump: a vessel switch, a benchmark place) is built there and waited for. A
// collection whose bodies have all been farther than 300,000 km for 5 s, and that no view drew for more frames than
// the GPU can have in flight (PlanetViewportData.OnPreRender says which it draws), is let go again. The two kinds of
// per-level sets the game writes once, when its planet renderer is built (PlanetRenderer._meshGeometrySets,
// PlanetFrustumCulling._cullingDescriptorSets), are written again each time, while no frame in flight uses them; a
// collection drawn while let go (the camera moved after the frame's start) draws its finest level at every level, and
// is swapped in after the GPU is idle. Ocean collections stay as loading built them.
//
// CubeMesh.Generate runs on worker threads only: on the main thread, its Loading.Task draws and presents a whole
// loading screen frame (Loading.OnFrame), in the middle of the game's frame. The game's adjacency build isn't
// deterministic (unsynchronised counts in a Parallel.For), so a level built again can differ from the loaded one as two
// loads can.
[Hook(Phase.Default, Switch = nameof(Settings.UnloadFarTerrain))]
internal static class FarTerrain
{
    private const double NearM = 150_000e3, FarM = 300_000e3;
    private const long FarMs = 5000;
    // More frames than the GPU can have in flight (the game's MaxFramesInFlight, its compute pool's frame)
    private const long QuietFrames = 10;

    private sealed class Collection(PlanetMeshCollection meshes, List<Celestial> bodies)
    {
        public readonly PlanetMeshCollection Meshes = meshes;
        public List<Celestial> Bodies = bodies;
        public bool Built = true;
        public Task<CubeMesh[]>? Building;
        public long NeededFrame = -1;  // the last frame a view was about to draw it, at the frame's start
        public long DrawnFrame = -1;  // the last frame a view drew it
        public long FarSince = -1;  // ms on Clock
    }

    private static readonly Dictionary<PlanetMeshCollection, Collection> Collections = new();
    private static readonly HashSet<Collection> Needed = new();
    private static readonly List<Camera> Cameras = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static CelestialSystem? _system;
    private static long _frame;
    private static FieldInfo? _geometrySets, _culling, _cullingSets;
    private static bool _failed;  // every collection built again, and nothing freed from then on (Hooks.Fail)
    private static bool _restored;

    public static string Apply(Harmony harmony)
    {
        var generate = Find.Method(typeof(PlanetMeshCollection), "Generate");
        var celestials = Find.Method(typeof(Program), "OnFrameCelestials");
        var disposeAll = Find.Method(typeof(PlanetMeshLibrary), nameof(PlanetMeshLibrary.DisposeAll));
        var prepare = Find.Method(typeof(PlanetViewportData), nameof(PlanetViewportData.OnPreRender));
        _geometrySets = Find.Field(typeof(PlanetRenderer), "_meshGeometrySets");
        _culling = Find.Field(typeof(PlanetRenderer), "_planetFrustumCulling");
        _cullingSets = Find.Field(typeof(PlanetFrustumCulling), "_cullingDescriptorSets");
        harmony.Patch(generate, postfix: new HarmonyMethod(typeof(FarTerrain), nameof(CollectionLoaded)));
        harmony.Patch(celestials, postfix: new HarmonyMethod(typeof(FarTerrain), nameof(Frame)));
        harmony.Patch(prepare, postfix: new HarmonyMethod(typeof(FarTerrain), nameof(ViewPrepared)));
        harmony.Patch(disposeAll, prefix: new HarmonyMethod(typeof(FarTerrain), nameof(DisposeAllPrefix)));
        return "on";
    }

    // Loading built a whole collection: a terrain collection of a body other than the home body keeps its finest level
    // only, before the next collection is built
    private static void CollectionLoaded(PlanetMeshCollection __instance)
    {
        if (_failed)
            return;
        try
        {
            var system = Universe.CurrentSystem;
            if (system == null || __instance.Items.Count < 2 || IsOcean(system, __instance))
                return;
            var bodies = Bodies(system, __instance);
            if (bodies.Count == 0)
                return;
            var collection = Collections[__instance] = new Collection(__instance, bodies);
            _system = system;
            if ((system.HomeBody as Celestial)?.BodyTemplate?.MeshCollection != __instance)
                Free(collection);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
        }
    }

    // A view's terrain for this frame: its nearby body, at one of its collection's levels
    private static void ViewPrepared(PlanetViewportData __instance)
    {
        if (__instance.NearbyCelestial?.BodyTemplate?.MeshCollection is { } meshes &&
            Collections.TryGetValue(meshes, out var drawn))
            drawn.DrawnFrame = _frame;
    }

    private static void Frame()
    {
        if (Collections.Count == 0)
            return;
        if (_failed)
        {
            if (!_restored)
            {
                _restored = true;
                Restore();
            }
            return;
        }
        try
        {
            Update();
        }
        catch (Exception e)
        {
            _failed = _restored = true;
            Hooks.Fail(nameof(FarTerrain), e);
            Restore();
        }
    }

    private static void Update()
    {
        _frame++;
        var system = Universe.CurrentSystem;
        if (system == null)
            return;
        if (system != _system)
        {
            _system = system;
            foreach (var c in Collections.Values)
                c.Bodies = Bodies(system, c.Meshes);
        }
        // The collections the views draw this frame: the views PlanetRenderer.OnPreRender prepares, the main one and the
        // visible secondary ones that show terrain (the UI, which opens and closes views, runs before this)
        Needed.Clear();
        Cameras.Clear();
        foreach (var view in ViewportRegistry.Views)
        {
            if (!(view.IsMain() || view.Type == ViewportType.Secondary && view.Visible && view.HasAny(ViewportOptionFlags.RenderTerrain)) ||
                view.GetCamera() is not { } camera)
                continue;
            Cameras.Add(camera);
            if (camera.GetEffectiveNearbyCelestial()?.BodyTemplate?.MeshCollection is { } meshes &&
                Collections.TryGetValue(meshes, out var needed))
                Needed.Add(needed);
        }
        long now = Clock.ElapsedMilliseconds;
        foreach (var c in Needed)
        {
            if (!c.Built)
                BuildNow(c, "jump");
            c.NeededFrame = _frame;
            c.FarSince = -1;
        }
        foreach (var c in Collections.Values)
        {
            if (c.NeededFrame == _frame)
                continue;
            double distance = Distance(c);
            if (c.Built)
            {
                if (distance <= FarM)
                    c.FarSince = -1;
                else if (c.FarSince < 0)
                    c.FarSince = now;
                else if (now - c.FarSince >= FarMs && Quiet(c))
                    Free(c);
            }
            else if (c.Building is { IsCompleted: true })
            {
                SwapIn(c, "ahead");
            }
            else if (c.Building == null && distance < NearM)
            {
                c.Building = Build(c);
            }
        }
    }

    // No frame the GPU may still be working on draws it
    private static bool Quiet(Collection c) => _frame - Math.Max(c.NeededFrame, c.DrawnFrame) > QuietFrames;

    // The surface distance from the nearest camera to the nearest of the collection's bodies
    private static double Distance(Collection c)
    {
        double nearest = double.PositiveInfinity;
        foreach (var camera in Cameras)
            foreach (var body in c.Bodies)
                nearest = Math.Min(nearest, (camera.PositionEcl - body.GetPositionEcl()).Length() - body.MeanRadius);
        return nearest;
    }

    private static List<Celestial> Bodies(CelestialSystem system, PlanetMeshCollection meshes)
    {
        var bodies = new List<Celestial>();
        foreach (var body in system.All.AsSpan())
            if (body is Celestial celestial && celestial.BodyTemplate?.MeshCollection == meshes)
                bodies.Add(celestial);
        return bodies;
    }

    private static bool IsOcean(CelestialSystem system, PlanetMeshCollection meshes)
    {
        foreach (var body in system.All.AsSpan())
            if (body.BodyTemplate?.OceanReference?.MeshCollection == meshes)
                return true;
        return false;
    }

    // Every level but the finest gives its buffers back and points at the finest's; the sets written with them, when
    // the planet renderer exists, are written again first. No frame in flight uses the collection
    private static void Free(Collection c)
    {
        var levels = c.Meshes.Items;
        var finest = levels[^1];
        var freed = new List<(SimpleVkMesh Mesh, BufferEx Adjacency, BufferEx Distances)>();
        for (int i = 0; i < levels.Count - 1; i++)
        {
            var level = levels[i];
            if (level.Mesh != finest.Mesh)
                freed.Add((level.Mesh, level.AdjacencyBuffer, level.AverageNeighborDistanceBuffer));
            level.Mesh = finest.Mesh;
            level.AdjacencyBuffer = finest.AdjacencyBuffer;
            level.AverageNeighborDistanceBuffer = finest.AverageNeighborDistanceBuffer;
            // The collection then reads as not loaded: the game's exit leaves it to DisposeAllPrefix
            level.IsLoaded = false;
        }
        c.Built = false;
        c.FarSince = -1;
        WriteSets(c.Meshes);
        foreach (var (mesh, adjacency, distances) in freed)
        {
            mesh.Dispose();
            adjacency.Dispose();
            distances.Dispose();
        }
        Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} let go");
    }

    // Its levels but the finest, made on a thread of its own and the thread pool, never the main thread
    private static Task<CubeMesh[]> Build(Collection c)
    {
        var levels = c.Meshes.Items.Take(c.Meshes.Items.Count - 1).ToArray();
        var renderer = Program.GetRenderer();
        var result = new TaskCompletionSource<CubeMesh[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var built = new CubeMesh?[levels.Length];
            try
            {
                Parallel.For(0, levels.Length, i =>
                {
                    built[i] = new CubeMesh { Mesh = null!, Id = levels[i].Id, BaseLayer = levels[i].BaseLayer, Layers = levels[i].Layers };
                    built[i]!.Generate(renderer, false);
                });
                result.SetResult(built!);
            }
            catch (Exception e)
            {
                // What each level made before the error (Generate makes its buffers one after another)
                foreach (var level in built)
                {
                    if (level == null)
                        continue;
                    if (!level.AdjacencyBuffer.IsNull())
                        level.AdjacencyBuffer.Dispose();
                    if (!level.AverageNeighborDistanceBuffer.IsNull())
                        level.AverageNeighborDistanceBuffer.Dispose();
                    level.Mesh?.Dispose();
                }
                result.SetException(e);
            }
        })
        {
            IsBackground = true,
            Name = $"{nameof(FarTerrain)} {c.Meshes.Id}",
        };
        thread.Start();
        return result.Task;
    }

    // Built now: a view draws it this frame. The main thread waits; the work stays on the builder's threads
    private static void BuildNow(Collection c, string why)
    {
        var start = Clock.ElapsedMilliseconds;
        c.Building ??= Build(c);
        ((IAsyncResult)c.Building).AsyncWaitHandle.WaitOne();
        SwapIn(c, $"{why}, waited {Clock.ElapsedMilliseconds - start} ms");
    }

    // The built levels take the place of the aliases, and the sets are written with them, once no frame in flight
    // draws the collection
    private static void SwapIn(Collection c, string why)
    {
        if (!Quiet(c))
            Program.GetRenderer().Device.WaitIdle();
        var task = c.Building!;
        c.Building = null;
        var built = task.Result;
        var levels = c.Meshes.Items;
        for (int i = 0; i < built.Length; i++)
        {
            levels[i].Mesh = built[i].Mesh;
            levels[i].AdjacencyBuffer = built[i].AdjacencyBuffer;
            levels[i].AverageNeighborDistanceBuffer = built[i].AverageNeighborDistanceBuffer;
            levels[i].IsLoaded = true;
        }
        c.Built = true;
        c.FarSince = -1;
        WriteSets(c.Meshes);
        Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} built ({why})");
    }

    // After a failure: every collection let go is built again, as the game would have it
    private static void Restore()
    {
        foreach (var c in Collections.Values)
        {
            if (c.Built)
                continue;
            try
            {
                BuildNow(c, "restored");
            }
            catch (Exception e)
            {
                c.Building = null;
                Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} not built again ({Hooks.Describe(e)}); its terrain is drawn from its finest level");
            }
        }
    }

    // The planet renderer's and frustum culling's per-level sets, from the levels' buffers as they are now. Without a
    // planet renderer (loading), it writes them itself when it is built
    private static void WriteSets(PlanetMeshCollection meshes)
    {
        if (Program.GetPlanetRenderer() is not { } planets)
            return;
        var renderer = Program.GetRenderer();
        var geometry = (Dictionary<KeyHash, VkDescriptorSet>)_geometrySets!.GetValue(planets)!;
        var culling = (Dictionary<KeyHash, VkDescriptorSet[]>)_cullingSets!.GetValue(_culling!.GetValue(planets))!;
        static VkDescriptorBufferInfo Info(VkBuffer buffer) => new() { Buffer = buffer, Offset = ByteSize.Zero, Range = VK.WHOLE_SIZE };
        Span<VkDescriptorBufferInfo> infos = stackalloc VkDescriptorBufferInfo[4];
        for (int i = 0; i < meshes.Items.Count - 1; i++)
        {
            var level = meshes.Items[i];
            // As PlanetRenderer.BuildFrameResources writes them
            if (geometry.TryGetValue(level.Hash, out var set))
            {
                infos[0] = Info(level.Mesh.IndexBuffer);
                infos[1] = Info(level.Mesh.GetVertexBuffer(MeshAttribute.Position));
                infos[2] = Info(level.AdjacencyBuffer.VkBuffer);
                infos[3] = Info(level.AverageNeighborDistanceBuffer.VkBuffer);
                Utils.WriteDescriptorSets(set, default, default, default, default, infos, renderer);
            }
            // As the PlanetFrustumCulling constructor writes them, one a frame slot
            if (culling.TryGetValue(level.Hash, out var sets))
                for (int f = 0; f < sets.Length; f++)
                {
                    infos[0] = Info(planets.GetRenderableVertexBuffer(f).VkBuffer);
                    infos[1] = Info(level.Mesh.IndexBuffer);
                    infos[2] = Info(planets.GetCullingIndexBuffer(f).VkBuffer);
                    infos[3] = Info(planets.GetTessellatedPatchIndexBuffer(f).VkBuffer);
                    Utils.WriteDescriptorSets(sets[f], default, default, default, default, infos, renderer);
                }
        }
    }

    private static void Dispose(CubeMesh level)
    {
        level.Mesh.Dispose();
        level.AdjacencyBuffer.Dispose();
        level.AverageNeighborDistanceBuffer.Dispose();
    }

    // The game's exit skips a collection that reads as not loaded: a let-go collection's finest level goes here, as
    // PlanetMeshCollection.DisposeMeshes would dispose it, and so does a build nobody swapped in
    private static void DisposeAllPrefix()
    {
        try
        {
            var renderer = Program.GetRenderer();
            foreach (var c in Collections.Values)
            {
                if (c.Building is { } building)
                {
                    c.Building = null;
                    try
                    {
                        foreach (var level in building.Result)
                            Dispose(level);
                    }
                    catch (AggregateException)
                    {
                        // A failed build disposed what it made
                    }
                }
                if (c.Built)
                    continue;
                var finest = c.Meshes.Items[^1];
                Dispose(finest);
                if (renderer.RayQueriesSupported)
                {
                    finest.TopLevelAccelerationStructure?.Dispose(renderer.Device);
                    finest.BottomLevelAccelerationStructure?.Dispose(renderer.Device);
                }
                else
                {
                    finest.Bvh?.Dispose();
                }
            }
        }
        catch (Exception e)
        {
            Hooks.Log($"{nameof(FarTerrain)}: at exit, {Hooks.Describe(e)}");
        }
    }
}
