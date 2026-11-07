using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace KsaMetal.Hooks.Optimizations;

// Far clutter. Loading builds the ground clutter's GPU resources for every world that has clutter
// (ClutterEcotypeRenderData.BuildRenderResources: its cell grid's buffers, two views' buffers and pipelines), while the
// game places and draws clutter only on a view's nearby body, within 80,000 km of its surface
// (GroundClutterRenderer.AccumulateViewportCells and what runs after it); in the solar system the Moon's and Mars's rocks
// take 66 MB each. Now a world's are built when a view's camera (a map's flight camera too) first comes within
// 150,000 km of its surface, and before AccumulateViewportCells when a view's nearby body still waits; the home body's
// at load as before. A world takes about 0.3 s to build, nearly all of it compiling the clutter's shader variants: one
// built ahead is built on a worker thread, its uploads recorded there into a staging pool the main thread submits when it
// is done (the cell grid submits its own from the worker, under the driver's submit lock); one a view needs at once is
// built on the main thread, or waited for. What the constructor made stays from load: the mesh atlas the collisions
// (ClutterEcotypePhysicalData) use too, the CPU's cell grids. Every other reader of these resources works on the nearby
// body only, the exclusions of other worlds wait in a list (QueueExclusionUpload), and RebuildFrameResources recreates
// the pipelines built, none before. While the worker builds, what it reads must stay: a build on the main thread, the
// renderer's and a viewport's rebuild (render targets, passes, the shadow cascades), the game's exit and FarTerrain
// letting the finest level of its world go wait for it; FarTerrain's writes of the clutter's generate sets skip its
// ecotypes, which are written with the finest level as it is then once built. The worker's build allocates descriptor sets from the game's shared pool (ClutterViewResources),
// whose counts DescriptorPoolEx keeps without a lock: its allocations and frees take one here. The game's exit disposes
// one never built through DestroyFrameResources, which frees only its atlas here. Once built, a world's clutter stays
// for the session: DestroyFrameResources disposes without clearing what a second build would reuse. A build that throws
// leaves its ecotype half made, as the same error at load would have.
[Hook(Phase.Default, Switch = nameof(Settings.UnloadFarTerrain))]
internal static class FarClutter
{
    private const double NearM = 150_000e3;

    private static readonly List<ClutterEcotypeRenderData> Waiting = new();
    private static readonly HashSet<Celestial> Bodies = new();
    private static readonly List<Camera> Cameras = new();
    private static readonly object PoolLock = new();
    private static bool _failed;
    [ThreadStatic] private static bool _building;
    private static Job? _job;

    // A world built on a worker: its ecotypes, and the staging pool of their uploads, its command buffer begun by the main
    // thread
    private sealed class Job(Celestial body, string why, StagingPool pool, CommandBuffer commands)
    {
        public readonly Celestial Body = body;
        public readonly string Why = why;
        public readonly StagingPool Pool = pool;
        public readonly CommandBuffer Commands = commands;
        public readonly List<ClutterEcotypeRenderData> Ecotypes = new();
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public Task Task = Task.CompletedTask;
    }

    // Whether the worker is building this ecotype now
    public static bool OnWorker(ClutterEcotypeRenderData data) => _job is { } job && job.Ecotypes.Contains(data);

    // Whether the worker is building this world's clutter now
    public static bool OnWorker(Celestial body) => _job is { } job && job.Body == body;

    public static string Apply(Harmony harmony)
    {
        var build = Find.Method(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.BuildRenderResources));
        var destroy = Find.Method(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.DestroyFrameResources));
        var rebuild = Find.Method(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.RebuildFrameResources));
        var accumulate = Find.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.AccumulateViewportCells));
        var celestials = Find.Method(typeof(Program), "OnFrameCelestials");
        var settling = new[]
        {
            Find.Method(typeof(Program), nameof(Program.RebuildRenderer)), Find.Method(typeof(Program), "RebuildViewport"),
            Find.Method(typeof(Program), nameof(Program.Dispose)),
        };
        var pools = new[]
        {
            Find.Method(typeof(DescriptorPoolEx), "AllocateDescriptorSet", typeof(DescriptorSetLayoutEx)),
            Find.Method(typeof(DescriptorPoolEx), "AllocateDescriptorSets", typeof(DescriptorSetLayoutEx), typeof(Span<VkDescriptorSet>)),
            Find.Method(typeof(DescriptorPoolEx), "AllocateDescriptorSets", typeof(Span<DescriptorSetLayoutEx>), typeof(Span<VkDescriptorSet>)),
            Find.Method(typeof(DescriptorPoolEx), "FreeDescriptorSets", typeof(Span<VkDescriptorSet>)),
        };
        harmony.Patch(build, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Building)));
        harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Destroying)));
        harmony.Patch(rebuild, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Settle)));
        foreach (var method in settling)
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Settle)));
        harmony.Patch(accumulate, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Accumulating)));
        harmony.Patch(celestials, postfix: new HarmonyMethod(typeof(FarClutter), nameof(Frame)));
        foreach (var pool in pools)
            harmony.Patch(pool, prefix: new HarmonyMethod(typeof(FarClutter), nameof(PoolEnter)),
                finalizer: new HarmonyMethod(typeof(FarClutter), nameof(PoolExit)));
        return "on";
    }

    // Loading builds an ecotype's resources: those of a world other than the home body wait
    private static bool Building(ClutterEcotypeRenderData __instance)
    {
        if (_building || _failed)
            return true;
        try
        {
            if (Universe.CurrentSystem?.HomeBody == __instance.Celestial)
                return true;
            Waiting.Add(__instance);
            Bodies.Add(__instance.Celestial);
            return false;
        }
        catch (Exception e)
        {
            // Loading builds this one itself, and what waited is built now
            Waiting.Remove(__instance);
            Failed(e);
            return true;
        }
    }

    // The game's exit, or a clutter renderer let go: one being built is finished first, one never built has only its atlas
    private static bool Destroying(ClutterEcotypeRenderData __instance)
    {
        if (OnWorker(__instance))
            Settle();
        if (!Waiting.Remove(__instance))
            return true;
        try
        {
            __instance.EcotypeMeshAtlas.Dispose();
        }
        catch (Exception e)
        {
            // Nothing else of this one to free (the game's DestroyFrameResources would dispose buffers it never made); the
            // worlds still waiting are built now, as after any failure
            Failed(e);
        }
        return false;
    }

    // What the worker reads about to change (the renderer or a viewport rebuilt, a setting's pipelines made again, the
    // game's exit), or the same shaders about to be compiled here: its build finished first
    public static void Settle()
    {
        try
        {
            if (_job is { } job)
                Finish(job, waited: true);
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // The shared pool's counts: one thread at a time (a world built on the worker allocates from it)
    private static void PoolEnter(out bool __state)
    {
        Monitor.Enter(PoolLock);
        __state = true;
    }

    private static void PoolExit(bool __state)
    {
        if (__state)
            Monitor.Exit(PoolLock);
    }

    // Clutter is about to take the views' nearby bodies: one being built is waited for, one that still waits is built now
    private static void Accumulating()
    {
        if (_failed || (_job == null && Bodies.Count == 0))
            return;
        try
        {
            foreach (var view in ViewportRegistry.Views)
            {
                if (view.GetCamera()?.GetEffectiveNearbyCelestial() is not { } body)
                    continue;
                if (_job is { } job && job.Body == body)
                    Finish(job, waited: true);
                else if (Bodies.Contains(body))
                {
                    Settle();
                    Build(body, "now");
                }
            }
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // A world a camera comes within 150,000 km of is built ahead of it on a worker; one built there is finished
    private static void Frame()
    {
        if (_failed)
            return;
        try
        {
            if (_job is { Task.IsCompleted: true } done)
                Finish(done, waited: false);
            if (_job != null || Bodies.Count == 0)
                return;
            CollectCameras();
            Celestial? near = null;
            foreach (var body in Bodies)
                foreach (var camera in Cameras)
                    if ((camera.PositionEcl - body.GetPositionEcl()).Length() - body.MeanRadius < NearM)
                        near = body;
            if (near != null)
                Start(near, "ahead");
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // As FarTerrain counts them: the main view's camera and the visible secondary views' that draw terrain, and behind a
    // map its flight camera
    private static void CollectCameras()
    {
        Cameras.Clear();
        foreach (var view in ViewportRegistry.Views)
        {
            if (!Views.Prepared(view) || view.GetCamera() is not { } camera)
                continue;
            Cameras.Add(camera);
            if (Views.Flight(view, camera) is { } flight)
                Cameras.Add(flight);
        }
    }

    // Its ecotypes, as loading would have built them, on the main thread
    private static void Build(Celestial body, string why)
    {
        var start = Stopwatch.StartNew();
        Bodies.Remove(body);
        foreach (var data in Waiting.Where(d => d.Celestial == body).ToList())
        {
            Waiting.Remove(data);
            BuildOne(data);
        }
        Hooks.Log($"{nameof(FarClutter)}: {body.Id} built ({why}, {start.ElapsedMilliseconds} ms)");
    }

    // Its ecotypes on a worker: their staging pool made and its command buffer begun here, the rest there
    private static void Start(Celestial body, string why)
    {
        var renderer = Program.GetRenderer();
        var pool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
        var commands = pool.NextCommandBuffer();
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        var job = new Job(body, why, pool, commands);
        Bodies.Remove(body);
        foreach (var data in Waiting.Where(d => d.Celestial == body).ToList())
        {
            Waiting.Remove(data);
            job.Ecotypes.Add(data);
        }
        job.Task = Task.Factory.StartNew(() =>
        {
            _building = true;
            try
            {
                foreach (var data in job.Ecotypes)
                    data.BuildRenderResources(job.Pool, job.Commands);
            }
            finally
            {
                _building = false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        _job = job;
    }

    // A worker's build done (or waited for): its uploads submitted and waited for, here on the main thread, and its
    // generate sets written with the terrain as it is now
    private static void Finish(Job job, bool waited)
    {
        var wait = Stopwatch.StartNew();
        _job = null;
        try
        {
            job.Task.Wait();
        }
        catch (AggregateException)
        {
            // Said below, after the uploads of what was made
        }
        long waitedMs = wait.ElapsedMilliseconds;
        job.Commands.End();
        job.Pool.Dispose();
        if (job.Task.Exception?.InnerException is { } e)
        {
            Failed(e);
            return;
        }
        FarTerrain.ClutterBuilt(job.Body);
        Hooks.Log($"{nameof(FarClutter)}: {job.Body.Id} built ({job.Why} on a worker, {job.Clock.ElapsedMilliseconds} ms; " +
                  $"{(waited ? $"waited {waitedMs} ms, " : "")}the main thread {wait.ElapsedMilliseconds} ms)");
    }

    // After an error: what still waits is built at once, as loading would have
    private static void Failed(Exception e)
    {
        _failed = true;
        if (_job is { } running)
        {
            try
            {
                Finish(running, waited: true);
            }
            catch (Exception again)
            {
                Hooks.Log($"{nameof(FarClutter)}: {running.Body.Id} not finished, {Hooks.Describe(again)}");
            }
        }
        Hooks.Fail(nameof(FarClutter), e);
        foreach (var data in Waiting.ToList())
        {
            Waiting.Remove(data);
            try
            {
                BuildOne(data);
            }
            catch (Exception again)
            {
                Hooks.Log($"{nameof(FarClutter)}: {data.Celestial.Id} {data.EcotypeName} not built, {Hooks.Describe(again)}");
            }
        }
        Bodies.Clear();
    }

    // With a staging pool of its own: disposing it submits the uploads and waits for them
    private static void BuildOne(ClutterEcotypeRenderData data)
    {
        var renderer = Program.GetRenderer();
        using var pool = renderer.Allocator.CreateStagingPool(renderer.GraphicsAndCompute, 1);
        var commands = pool.NextCommandBuffer();
        commands.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);
        _building = true;
        try
        {
            data.BuildRenderResources(pool, commands);
        }
        finally
        {
            _building = false;
            commands.End();
        }
    }
}
