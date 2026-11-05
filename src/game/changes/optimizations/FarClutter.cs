using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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
// at load as before. A world takes about 0.3 s to build, on the main thread, nearly all of it compiling the clutter's
// shader variants. What the constructor made stays from load: the mesh atlas the collisions (ClutterEcotypePhysicalData)
// use too, the CPU's cell grids. Every other reader of these resources works on the nearby body only, the exclusions of
// other worlds wait in a list (QueueExclusionUpload), and RebuildFrameResources recreates the pipelines built, none
// before. The game's exit disposes one never built through DestroyFrameResources, which frees only its atlas here.
// Once built, a world's clutter stays for the session: DestroyFrameResources disposes without clearing what a second
// build would reuse. A build that throws leaves its ecotype half made, as the same error at load would have.
[Hook(Phase.Default, Switch = nameof(Settings.UnloadFarTerrain))]
internal static class FarClutter
{
    private const double NearM = 150_000e3;

    private static readonly List<ClutterEcotypeRenderData> Waiting = new();
    private static readonly HashSet<Celestial> Bodies = new();
    private static readonly List<Camera> Cameras = new();
    private static bool _building, _failed;

    public static string Apply(Harmony harmony)
    {
        var build = Find.Method(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.BuildRenderResources));
        var destroy = Find.Method(typeof(ClutterEcotypeRenderData), nameof(ClutterEcotypeRenderData.DestroyFrameResources));
        var accumulate = Find.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.AccumulateViewportCells));
        var celestials = Find.Method(typeof(Program), "OnFrameCelestials");
        harmony.Patch(build, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Building)));
        harmony.Patch(destroy, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Destroying)));
        harmony.Patch(accumulate, prefix: new HarmonyMethod(typeof(FarClutter), nameof(Accumulating)));
        harmony.Patch(celestials, postfix: new HarmonyMethod(typeof(FarClutter), nameof(Frame)));
        return "on";
    }

    // Loading builds an ecotype's resources: those of a world other than the home body wait
    private static bool Building(ClutterEcotypeRenderData __instance)
    {
        if (_building || _failed || Universe.CurrentSystem?.HomeBody == __instance.Celestial)
            return true;
        Waiting.Add(__instance);
        Bodies.Add(__instance.Celestial);
        return false;
    }

    // The game's exit, or a clutter renderer let go: one never built has only its atlas
    private static bool Destroying(ClutterEcotypeRenderData __instance)
    {
        if (!Waiting.Remove(__instance))
            return true;
        __instance.EcotypeMeshAtlas.Dispose();
        return false;
    }

    // Clutter is about to take the views' nearby bodies: one that still waits is built now
    private static void Accumulating()
    {
        if (Bodies.Count == 0 || _failed)
            return;
        try
        {
            foreach (var view in ViewportRegistry.Views)
                if (view.GetCamera()?.GetEffectiveNearbyCelestial() is { } body && Bodies.Contains(body))
                    Build(body, "now");
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // A world a camera comes within 150,000 km of is built ahead of it
    private static void Frame()
    {
        if (Bodies.Count == 0 || _failed)
            return;
        try
        {
            CollectCameras();
            Celestial? near = null;
            foreach (var body in Bodies)
                foreach (var camera in Cameras)
                    if ((camera.PositionEcl - body.GetPositionEcl()).Length() - body.MeanRadius < NearM)
                        near = body;
            if (near != null)
                Build(near, "ahead");
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
            if (!(view.IsMain() || view.Type == ViewportType.Secondary && view.Visible && view.HasAny(ViewportOptionFlags.RenderTerrain)) ||
                view.GetCamera() is not { } camera)
                continue;
            Cameras.Add(camera);
            if (view is GameViewport { Mode: CameraMode.Map } game && game.BaseCamera != camera)
                Cameras.Add(game.BaseCamera);
        }
    }

    // Its ecotypes, as loading would have built them
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

    // After an error: what still waits is built at once, as loading would have
    private static void Failed(Exception e)
    {
        _failed = true;
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
