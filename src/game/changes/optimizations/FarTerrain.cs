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
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering;
using KSA.Rendering.Planet;
using KSA.Rendering.Raytracing;
using KSA.Rendering.Water.Rendering;
using RenderCore;
using RenderCore.Mesh;

namespace KsaMetal.Hooks.Optimizations;

// Far terrain. A view draws the terrain of one body only, the one within 80,000 km of its surface
// (Camera.GetEffectiveNearbyCelestial), at one level of detail (PlanetMeshCollection.GetLodLevel: the finest level whose
// RenderDistance is above the camera's altitude, every frame), and ground clutter is placed on that body only, by ray
// queries against its collection's finest level, below its ecotypes' generation range; yet
// PlanetMeshLibrary.GenerateInUse builds every level of every terrain mesh collection the system uses (18 levels of
// 12-60 MB each, 1.4 GB in the solar system) and an acceleration structure of the finest level for each one with clutter
// (~170 MB), and keeps them all. Now each level of a collection is built or not on its own: one that isn't points at the
// buffers of the nearest built level (the finer one on a tie), so whatever is written with it meanwhile stays valid, and
// a finest level that isn't built holds an acceleration structure without a handle, which clutter's sets take as none
// (GroundClutterRenderer.GenerateGroundClutter is skipped on such a body).
//
// As soon as loading has built a collection it keeps only its smallest level, except the home body's, and loading builds
// no acceleration structure for the ones it lets go. A collection one of whose bodies comes within 150,000 km of a camera
// is built on worker threads and swapped in at the start of a frame (Program.OnFrameCelestials, before the frame's views
// are prepared); the level a view is about to draw is built there and waited for when it isn't (a jump: a vessel switch,
// a benchmark place), and so is one a camera reached after the frame's start, before the planet renderer prepares its
// views. A level nothing wants for 5 s, that no frame the GPU may still be working on draws (PlanetViewportData.OnPreRender
// says which), is let go; a collection whose bodies are all farther than 300,000 km wants only its smallest level.
//
// Load Terrain by Altitude wants, near a body, only the levels around each
// camera's altitude instead of all of them: the level drawn and the ones above and below it, one more below while the
// camera descends, the small coarse ones, and the finest with its acceleration structure only below three
// times the height where clutter is placed (there it must be built: a clutter cell is marked loaded whether or not it
// was placed). The home body's collection loads whole and is trimmed in play. Each level takes
// 20-250 ms to build on a worker; far faster than a band a frame (time warp on an eccentric orbit), a level not built
// yet is drawn from the nearest one that is.
//
// The sets the game writes once with a level's buffers (PlanetRenderer._meshGeometrySets,
// PlanetFrustumCulling._cullingDescriptorSets, the generate sets of each ClutterCubeCellGrid for the finest) are written
// again each time its buffers change, while no frame in flight uses them (the GPU is waited for when one might).
// Without ray queries clutter places with a BVH of the finest level, which then stays as before.
//
// An ocean's collection (Earth's: 7 levels, 184 MB) goes the same way, by the altitude OceanRenderer.Render draws it
// at (the main view's nearby body, one level), and a prefix there builds the level it is about to draw when it isn't.
// Only the main view draws the ocean. It binds its level's vertices and indices each frame and nothing else of it:
// loading builds its finest level an acceleration structure when its body has ground clutter (7 MB), though only the
// terrain's is ever read, and
// each level an adjacency and a neighbour distance buffer (73 MB) that only the terrain's sets bind. The ocean's are
// built without the first and let go of the others as soon as a level is built; its levels then read as not loaded,
// and the game's exit leaves them to DisposeAllPrefix.
//
// CubeMesh.Generate runs on worker threads only: on the main thread, its Loading.Task draws and presents a whole
// loading screen frame (Loading.OnFrame), in the middle of the game's frame. The game's adjacency build isn't
// deterministic (unsynchronised counts in a Parallel.For), so a level built again can differ from the loaded one as two
// loads can.
[Hook(Phase.Default, Switch = nameof(Settings.UnloadFarTerrain))]
internal static class FarTerrain
{
    private const double NearM = 150_000e3, FarM = 300_000e3;
    private const long UnwantedMs = 5000;
    // More frames than the GPU can have in flight (the game's MaxFramesInFlight, its compute pool's frame)
    private const long QuietFrames = 10;
    // A level smaller than this stays built near the body with the window (the coarse ones, a few MB together)
    private const long SmallBytes = 8L << 20;

    private sealed class Collection(PlanetMeshCollection meshes, List<Celestial> bodies, bool raytraced, bool home, bool ocean)
    {
        public readonly PlanetMeshCollection Meshes = meshes;
        public List<Celestial> Bodies = bodies;
        public readonly bool Ocean = ocean;
        // Loading asked for an acceleration structure of the finest level (a body with ground clutter), never an ocean's
        public readonly bool Raytraced = raytraced;
        public readonly bool Home = home;
        // The level always built, its smallest; the finest when clutter places with a BVH
        public int Kept;
        public bool[] Built = [];  // the level has buffers of its own
        public bool[] Small = [];
        public bool[] Wanted = [];  // this frame
        public bool[] Must = [];  // a view is about to draw it
        public int[] Owner = [];  // the level whose buffers it holds
        public long[] UnwantedSince = [];  // ms on Clock, -1 while wanted
        public long[] DrawnFrame = [];  // the last frame a view drew it, or a level holding its buffers
        public long ClutterFrame = -1;  // the last frame clutter placed with the finest level's structure
        public double ClutterM;  // the altitude below which clutter is placed on its bodies
        public readonly Dictionary<Camera, double> LastAltitude = new();
        public Task<CubeMesh?[]>? Building;
        public int Finest => Built.Length - 1;
        public bool AllBuilt => Array.TrueForAll(Built, b => b);
    }

    // ClutterCubeCellGrid.CreateRayQueriesGenerateWrites
    private delegate void GenerateWrites(ClutterCubeCellGrid grid, Renderer renderer, Celestial celestial, CubeMesh finest,
        int shaderSlot, int frameIndex, ref Span<VkWriteDescriptorSet> writes);

    private static readonly Dictionary<PlanetMeshCollection, Collection> Collections = new();
    // The views' cameras; behind a map view also its flight camera, which wants levels but draws none. Main: the main
    // view's, the only one that draws the ocean
    private static readonly List<(Camera Camera, Celestial? Nearby, bool Drawn, bool Main)> Cameras = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static CelestialSystem? _system;
    private static long _frame;
    private static bool _window;
    private static FieldInfo? _geometrySets, _culling, _cullingSets;
    private static GenerateWrites? _clutterWrites;
    private static bool _failed;  // every collection built again, and nothing freed from then on (Hooks.Fail)
    private static bool _restored;

    public static string Apply(Harmony harmony)
    {
        var generate = Find.Method(typeof(PlanetMeshCollection), "Generate");
        var celestials = Find.Method(typeof(Program), "OnFrameCelestials");
        var disposeAll = Find.Method(typeof(PlanetMeshLibrary), nameof(PlanetMeshLibrary.DisposeAll));
        var prepare = Find.Method(typeof(PlanetViewportData), nameof(PlanetViewportData.OnPreRender));
        var planets = Find.Method(typeof(PlanetRenderer), nameof(PlanetRenderer.OnPreRender));
        var clutter = Find.Method(typeof(GroundClutterRenderer), "GenerateGroundClutter");
        _geometrySets = Find.Field(typeof(PlanetRenderer), "_meshGeometrySets");
        _culling = Find.Field(typeof(PlanetRenderer), "_planetFrustumCulling");
        _cullingSets = Find.Field(typeof(PlanetFrustumCulling), "_cullingDescriptorSets");
        _clutterWrites = AccessTools.MethodDelegate<GenerateWrites>(
            Find.Method(typeof(ClutterCubeCellGrid), "CreateRayQueriesGenerateWrites"));
        _window = Settings.LoadTerrainByAltitude;
        harmony.Patch(generate, prefix: new HarmonyMethod(typeof(FarTerrain), nameof(CollectionLoading)),
            postfix: new HarmonyMethod(typeof(FarTerrain), nameof(CollectionLoaded)));
        harmony.Patch(celestials, postfix: new HarmonyMethod(typeof(FarTerrain), nameof(Frame)));
        harmony.Patch(planets, prefix: new HarmonyMethod(typeof(FarTerrain), nameof(ViewsPreparing)));
        harmony.Patch(prepare, postfix: new HarmonyMethod(typeof(FarTerrain), nameof(ViewPrepared)));
        harmony.Patch(disposeAll, prefix: new HarmonyMethod(typeof(FarTerrain), nameof(DisposeAllPrefix)));
        harmony.Patch(clutter, prefix: new HarmonyMethod(typeof(FarTerrain), nameof(ClutterClear)));
        harmony.Patch(Find.Method(typeof(OceanRenderer), nameof(OceanRenderer.Render)),
            prefix: new HarmonyMethod(typeof(FarTerrain), nameof(OceanDrawing)));
        return _window ? "on, by altitude" : "on";
    }

    // Loading is about to build a collection: one it lets go is built without its acceleration structure, when clutter
    // places with ray queries; an ocean's never has one
    private static void CollectionLoading(PlanetMeshCollection __instance, ref PlanetMeshLibrary.GenerateFlags flags)
    {
        if (_failed)
            return;
        try
        {
            var system = Universe.CurrentSystem;
            if (system == null || __instance.Items.Count < 2)
                return;
            var terrain = Bodies(system, __instance);
            var ocean = OceanBodies(system, __instance);
            // A collection that is both a terrain and an ocean stays as loading builds it
            if (terrain.Count > 0 == ocean.Count > 0)
                return;
            bool isOcean = ocean.Count > 0;
            var homeBody = system.HomeBody as Celestial;
            bool home = isOcean ? OceanId(homeBody) == __instance.Id : homeBody?.BodyTemplate?.MeshCollection == __instance;
            bool raytraced = !isOcean && (flags & PlanetMeshLibrary.GenerateFlags.AccelerationStructure) != 0;
            Collections[__instance] = new Collection(__instance, isOcean ? ocean : terrain, raytraced, home, isOcean);
            _system = system;
            if (isOcean || !home && RayQueries)
                flags &= ~PlanetMeshLibrary.GenerateFlags.AccelerationStructure;
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
        }
    }

    // Loading built it: one of a body other than the home body keeps its smallest level only, before the next collection
    // is built. The home body's stays whole through loading, also with the window: the texture streamer sizes its budget
    // once, at its first frame, from the memory free then (CelestialTextureStreamer.ResolveBudget), and would take what
    // the window lets go; the window trims it a few seconds into play. An ocean's levels drop what the ocean never reads
    // at once, the home body's too (73 MB in Sol)
    private static void CollectionLoaded(PlanetMeshCollection __instance)
    {
        if (_failed || !Collections.TryGetValue(__instance, out var c))
            return;
        try
        {
            var levels = __instance.Items;
            int n = levels.Count;
            if (c.Ocean)
                foreach (var level in levels)
                    DropOceanBuffers(level);
            c.Kept = KeptLevel(__instance, c.Ocean);
            c.Built = Enumerable.Repeat(true, n).ToArray();
            c.Small = levels.Select(l => Size(l.Mesh) < SmallBytes).ToArray();
            c.Wanted = new bool[n];
            c.Must = new bool[n];
            c.Owner = Enumerable.Range(0, n).ToArray();
            c.UnwantedSince = Enumerable.Repeat(-1L, n).ToArray();
            c.DrawnFrame = Enumerable.Repeat(-1L, n).ToArray();
            c.ClutterM = ClutterAltitude(c);
            if (!c.Home)
                Free(c, Enumerable.Range(0, n).Where(i => i != c.Kept).ToList());
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
            // Loading was told to skip the acceleration structure: clutter's sets need one (ClutterClear keeps clutter
            // off this body)
            var finest = __instance.Items[^1];
            if (c.Raytraced && RayQueries && finest.TopLevelAccelerationStructure == null)
                finest.TopLevelAccelerationStructure = default(AccelerationStructure);
        }
    }

    // The altitude over the terrain below which the game places a body's clutter (ClutterEcotypeRenderData.Update): its
    // highest terrain and the farthest generation range of its ecotypes, the largest of the collection's bodies
    private static double ClutterAltitude(Collection c)
    {
        double most = 0;
        foreach (var body in c.Bodies)
        {
            double range = 0;
            foreach (var ecotype in body.BodyTemplate?.GroundClutterReference?.Ecotypes ?? [])
                range = Math.Max(range, ecotype.Placement.GenerationRange.InMeters());
            if (range > 0)
                most = Math.Max(most, body.MaxTerrainHeightApprox + range);
        }
        return most;
    }

    // Clutter's one dispatch with its generate sets: none on a body whose finest level holds no acceleration structure
    // (not built: no view is near enough, or not built again after a failure)
    private static bool ClutterClear(Celestial nearbyCelestial)
    {
        try
        {
            if (nearbyCelestial?.BodyTemplate?.MeshCollection is not { } meshes || !Collections.TryGetValue(meshes, out var c) ||
                !c.Raytraced || !RayQueries)
                return true;
            if (!Real(meshes.Items[^1].TopLevelAccelerationStructure))
                return false;
            c.ClutterFrame = _frame;
            return true;
        }
        catch (Exception e)
        {
            // Restored at the next frame's start (Frame)
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
            return true;
        }
    }

    private static bool RayQueries => Program.GetRenderer().RayQueriesSupported;

    private static int KeptLevel(PlanetMeshCollection meshes, bool ocean)
    {
        var levels = meshes.Items;
        if (!RayQueries && !ocean)
            return levels.Count - 1;
        int kept = 0;
        for (int i = 1; i < levels.Count; i++)
            if (Size(levels[i].Mesh) < Size(levels[kept].Mesh))
                kept = i;
        return kept;
    }

    private static long Size(SimpleVkMesh mesh) => (long)mesh.VertexCount * 16 + (long)mesh.IndexCount * 4;

    // An ocean level built: its adjacency and neighbour distances go (the uploads are done: CubeMesh.Generate waits for
    // them), and it reads as not loaded, so the game's exit doesn't dispose what it no longer has
    private static void DropOceanBuffers(CubeMesh level)
    {
        level.IsLoaded = false;
        var adjacency = level.AdjacencyBuffer;
        var distances = level.AverageNeighborDistanceBuffer;
        level.AdjacencyBuffer = default;
        level.AverageNeighborDistanceBuffer = default;
        if (!adjacency.IsNull())
            adjacency.Dispose();
        if (!distances.IsNull())
            distances.Dispose();
    }

    // The ocean's level about to be drawn, as OceanRenderer.Render picks it: built now when it isn't (the camera reached
    // it after the frame's start), and marked drawn
    private static void OceanDrawing(Celestial atmosphericBody, IViewport inViewport)
    {
        if (Collections.Count == 0 || _failed)
            return;
        try
        {
            if (!GameSettings.ShowOcean() || atmosphericBody?.BodyTemplate?.OceanReference?.MeshCollection is not { } meshes ||
                !Collections.TryGetValue(meshes, out var c) || c.Built.Length == 0)
                return;
            int level = meshes.GetLodLevel(inViewport.GetCamera().CurrentAltitudeKm * 1000.0);
            if (!c.Built[level])
            {
                Array.Fill(c.Must, false);
                c.Must[level] = true;
                BuildNow(c, "late");
            }
            c.DrawnFrame[level] = _frame;
            c.DrawnFrame[c.Owner[level]] = _frame;
        }
        catch (Exception e)
        {
            // Restored at the next frame's start (Frame), not in the middle of this one's recording
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
        }
    }

    // A view's terrain for this frame: its nearby body, at one of its collection's levels, in the buffers of that level or
    // of the one it points at
    private static void ViewPrepared(PlanetViewportData __instance)
    {
        if (_failed)
            return;
        try
        {
            if (__instance.NearbyCelestial?.BodyTemplate?.MeshCollection is { } meshes &&
                Collections.TryGetValue(meshes, out var c) && c.Built.Length > 0)
            {
                int level = __instance.MeshLodIndex;
                c.DrawnFrame[level] = _frame;
                c.DrawnFrame[c.Owner[level]] = _frame;
            }
        }
        catch (Exception e)
        {
            // Restored at the next frame's start (Frame)
            _failed = true;
            Hooks.Fail(nameof(FarTerrain), e);
        }
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
            Failed(e);
        }
    }

    private static void Failed(Exception e)
    {
        _failed = _restored = true;
        Hooks.Fail(nameof(FarTerrain), e);
        Restore();
    }

    // The planet renderer is about to prepare the views and place their clutter: a level a camera reached after the
    // frame's start is built now, before anything this frame binds its sets
    private static void ViewsPreparing()
    {
        if (Collections.Count == 0 || _failed)
            return;
        try
        {
            if (Universe.CurrentSystem == null)
                return;
            CollectCameras();
            foreach (var c in Collections.Values)
                if (c.Built.Length > 0 && MarkMust(c))
                    BuildNow(c, "late");
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // The views PlanetRenderer.OnPreRender prepares, the main one and the visible secondary ones that show terrain, their
    // cameras and the body each draws
    private static void CollectCameras()
    {
        Cameras.Clear();
        foreach (var view in ViewportRegistry.Views)
        {
            if (!Views.Prepared(view) || view.GetCamera() is not { } camera)
                continue;
            Cameras.Add((camera, camera.GetEffectiveNearbyCelestial(), true, view.IsMain()));
            // The map shows its own camera's terrain, far up; the flight it returns to keeps its levels
            if (Views.Flight(view, camera) is { } flight)
                Cameras.Add((flight, flight.GetEffectiveNearbyCelestial(), false, view.IsMain()));
        }
    }

    // The levels the views are about to draw, as PlanetViewportData.OnPreRender picks them, and the finest where clutter
    // is placed (ClutterEcotypeRenderData marks a cell loaded whether or not GenerateGroundClutter runs: one placed
    // without the finest stays empty); all of them without the window. True when one isn't built
    private static bool MarkMust(Collection c)
    {
        Array.Fill(c.Must, false);
        bool missing = false;
        foreach (var (camera, nearby, drawn, main) in Cameras)
        {
            if (!drawn || nearby == null || !c.Bodies.Contains(nearby) || c.Ocean && !OceanShown(main))
                continue;
            if (!_window)
            {
                Array.Fill(c.Must, true);
                return !c.AllBuilt;
            }
            double altitude = Altitude(c, camera, drawn);
            int level = c.Meshes.GetLodLevel(altitude);
            c.Must[level] = true;
            missing |= !c.Built[level];
            if (c.Raytraced && RayQueries && altitude < c.ClutterM * 1.1)
            {
                c.Must[c.Finest] = true;
                missing |= !c.Built[c.Finest];
            }
        }
        return missing;
    }

    // The levels wanted this frame. Without the window: all within 150,000 km of a camera, what is built until 300,000 km,
    // then the smallest only. With it, near a body: the window around each camera's altitude
    private static void MarkWanted(Collection c)
    {
        Array.Fill(c.Wanted, false);
        c.Wanted[c.Kept] = true;
        int n = c.Wanted.Length;
        foreach (var (camera, nearby, drawn, main) in Cameras)
        {
            if (c.Ocean && !OceanShown(main))
                continue;
            double distance = Distance(c, camera);
            if (!_window)
            {
                for (int i = 0; i < n; i++)
                    c.Wanted[i] |= distance < NearM || distance < FarM && c.Built[i];
                continue;
            }
            if (distance >= NearM)
                continue;
            double altitude = nearby != null && c.Bodies.Contains(nearby) ? Altitude(c, camera, drawn) : distance;
            int level = c.Meshes.GetLodLevel(altitude);
            for (int i = Math.Max(0, level - 1); i <= Math.Min(n - 1, level + 1); i++)
                c.Wanted[i] = true;
            if (c.LastAltitude.TryGetValue(camera, out var last) && altitude < last && level + 2 < n)
                c.Wanted[level + 2] = true;
            c.LastAltitude[camera] = altitude;
            for (int i = 0; i < n; i++)
                c.Wanted[i] |= c.Small[i];
            if (c.Raytraced && RayQueries && altitude < c.ClutterM * 3)
                c.Wanted[c.Finest] = true;
        }
    }

    // A camera's altitude as the level is picked from it: the terrain's by PlanetViewportData.OnPreRender, the ocean's by
    // OceanRenderer.Render from the main view's camera, the only one the game writes it for (for that camera the same
    // value); a map's flight camera keeps the one it had when the map opened, so it goes by the terrain's
    private static double Altitude(Collection c, Camera camera, bool drawn) =>
        (c.Ocean && drawn ? camera.CurrentAltitudeKm : Program.GetCurrentAltitudeKm(camera)) * 1000.0;

    // The ocean is drawn by the main view's cameras, unless the game hides it
    private static bool OceanShown(bool main) => main && GameSettings.ShowOcean();

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
                c.Bodies = c.Ocean ? OceanBodies(system, c.Meshes) : Bodies(system, c.Meshes);
        }
        // The UI, which opens and closes views, has run
        CollectCameras();
        long now = Clock.ElapsedMilliseconds;
        foreach (var c in Collections.Values)
        {
            if (c.Built.Length == 0)
                continue;
            // The ecotypes' ranges can change while the game runs (the ecotype editor)
            c.ClutterM = ClutterAltitude(c);
            if (MarkMust(c))
                BuildNow(c, "jump");
            for (int i = 0; i < c.Must.Length; i++)
                if (c.Must[i])
                {
                    c.DrawnFrame[i] = _frame;
                    c.DrawnFrame[c.Owner[i]] = _frame;
                }
            MarkWanted(c);
            if (c.Building is { IsCompleted: true })
                SwapIn(c, "ahead");
            else if (c.Building == null && Missing(c, c.Wanted) is { Count: > 0 } wanted)
                c.Building = Build(c, wanted);
            var unwanted = new List<int>();
            for (int i = 0; i < c.Built.Length; i++)
            {
                if (c.Wanted[i] || c.Must[i] || !c.Built[i] || i == c.Kept)
                    c.UnwantedSince[i] = -1;
                else if (c.UnwantedSince[i] < 0)
                    c.UnwantedSince[i] = now;
                else if (now - c.UnwantedSince[i] >= UnwantedMs && Quiet(c, i) && c.Building == null)
                    unwanted.Add(i);
            }
            if (unwanted.Count > 0)
                Free(c, unwanted);
        }
    }

    // The levels marked that aren't built
    private static List<int> Missing(Collection c, bool[] marked)
    {
        var missing = new List<int>();
        for (int i = 0; i < marked.Length; i++)
            if (marked[i] && !c.Built[i])
                missing.Add(i);
        return missing;
    }

    // No frame the GPU may still be working on draws the level, nor places clutter with it
    private static bool Quiet(Collection c, int level) =>
        _frame - c.DrawnFrame[level] > QuietFrames && (level != c.Finest || _frame - c.ClutterFrame > QuietFrames);

    // The surface distance from the camera to the nearest of the collection's bodies
    private static double Distance(Collection c, Camera camera)
    {
        double nearest = double.PositiveInfinity;
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

    // The bodies with this ocean; by id: loading builds the collections before the ocean renderer resolves its
    // references to them (OceanRenderer.PopulatePlanets)
    private static List<Celestial> OceanBodies(CelestialSystem system, PlanetMeshCollection meshes)
    {
        var bodies = new List<Celestial>();
        foreach (var body in system.All.AsSpan())
            if (body is Celestial celestial && OceanId(celestial) == meshes.Id)
                bodies.Add(celestial);
        return bodies;
    }

    private static string? OceanId(Astronomical? body) => body?.BodyTemplate?.OceanReference?.MeshCollection?.Id;

    // An acceleration structure with buffers, not the stand-in of a finest level not built
    private static bool Real(AccelerationStructure? structure) => structure is { } s && !s.Buffer.IsNull();

    // The levels give their buffers back, the finest its acceleration structures too, and they and the levels that held
    // their buffers point at the nearest level built; the sets written with them are written again first. No frame in
    // flight uses them
    private static void Free(Collection c, List<int> free)
    {
        // A clutter build on FarClutter's worker may be reading the finest level's buffers
        if (free.Contains(c.Finest) && c.Bodies.Any(FarClutter.OnWorker))
            FarClutter.Settle();
        var levels = c.Meshes.Items;
        var freed = new List<(SimpleVkMesh Mesh, BufferEx Adjacency, BufferEx Distances)>();
        var structures = new List<AccelerationStructure>();
        foreach (int i in free)
        {
            var level = levels[i];
            freed.Add((level.Mesh, level.AdjacencyBuffer, level.AverageNeighborDistanceBuffer));
            c.Built[i] = false;
            if (i == c.Finest && c.Raytraced && RayQueries)
            {
                if (Real(level.TopLevelAccelerationStructure))
                    structures.Add(level.TopLevelAccelerationStructure!.Value);
                if (Real(level.BottomLevelAccelerationStructure))
                    structures.Add(level.BottomLevelAccelerationStructure!.Value);
                // Clutter's sets need one (ClutterCubeCellGrid.BuildFrameResources throws without); no handle reads as none
                level.TopLevelAccelerationStructure = default(AccelerationStructure);
                level.BottomLevelAccelerationStructure = null;
            }
        }
        var changed = Point(c);
        WriteSets(c, changed);
        foreach (var (mesh, adjacency, distances) in freed)
        {
            mesh.Dispose();
            if (!adjacency.IsNull())
                adjacency.Dispose();
            if (!distances.IsNull())
                distances.Dispose();
        }
        var device = Program.GetRenderer().Device;
        foreach (var structure in structures)
            structure.Dispose(device);
        Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} {Levels(free)} let go");
    }

    // Each level not built takes the buffers of the nearest level built, the finer one on a tie. The levels whose buffers
    // changed
    private static List<int> Point(Collection c)
    {
        var levels = c.Meshes.Items;
        var changed = new List<int>();
        for (int i = 0; i < levels.Count; i++)
        {
            int owner = i;
            if (!c.Built[i])
                for (int d = 1; d < levels.Count; d++)
                {
                    if (i + d < levels.Count && c.Built[i + d])
                    {
                        owner = i + d;
                        break;
                    }
                    if (i - d >= 0 && c.Built[i - d])
                    {
                        owner = i - d;
                        break;
                    }
                }
            var level = levels[i];
            if (owner != i)
            {
                var source = levels[owner];
                if (level.Mesh != source.Mesh)
                    changed.Add(i);
                level.Mesh = source.Mesh;
                level.AdjacencyBuffer = source.AdjacencyBuffer;
                level.AverageNeighborDistanceBuffer = source.AverageNeighborDistanceBuffer;
            }
            else if (c.Owner[i] != i)
                changed.Add(i);
            c.Owner[i] = owner;
            // A collection with a level not built, or an ocean's, reads as not loaded: the game's exit leaves it to
            // DisposeAllPrefix
            level.IsLoaded = c.Built[i] && !c.Ocean;
        }
        return changed;
    }

    // These levels, the finest with its acceleration structure, made on a thread of its own and the thread pool, never the
    // main thread
    private static Task<CubeMesh?[]> Build(Collection c, List<int> build)
    {
        var levels = c.Meshes.Items.ToArray();
        int finest = levels.Length - 1;
        bool raytraced = c.Raytraced, ocean = c.Ocean;
        var renderer = Program.GetRenderer();
        var result = new TaskCompletionSource<CubeMesh?[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var built = new CubeMesh?[levels.Length];
            try
            {
                Parallel.ForEach(build, i =>
                {
                    built[i] = new CubeMesh { Mesh = null!, Id = levels[i].Id, BaseLayer = levels[i].BaseLayer, Layers = levels[i].Layers };
                    built[i]!.Generate(renderer, raytraced && i == finest);
                    if (ocean)
                        DropOceanBuffers(built[i]!);
                });
                result.SetResult(built);
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
                    DisposeStructures(level, renderer);
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

    // The levels a view draws this frame and those wanted, built now. The main thread waits; the work stays on the
    // builder's threads
    private static void BuildNow(Collection c, string why)
    {
        var start = Clock.ElapsedMilliseconds;
        if (c.Building is { } running)
        {
            ((IAsyncResult)running).AsyncWaitHandle.WaitOne();
            SwapIn(c, $"{why}, waited {Clock.ElapsedMilliseconds - start} ms");
        }
        // Only what this frame draws: the wanted rest follows on the builder's threads
        if (Missing(c, c.Must) is not { Count: > 0 } must)
            return;
        c.Building = Build(c, must);
        ((IAsyncResult)c.Building).AsyncWaitHandle.WaitOne();
        SwapIn(c, $"{why}, waited {Clock.ElapsedMilliseconds - start} ms");
    }

    // The built levels take the place of what they pointed at, and the sets are written with them, once no frame in
    // flight uses the levels whose buffers change
    private static void SwapIn(Collection c, string why)
    {
        var task = c.Building!;
        c.Building = null;
        var built = task.Result;
        var levels = c.Meshes.Items;
        var swapped = new List<int>();
        for (int i = 0; i < built.Length; i++)
        {
            if (built[i] is not { } level)
                continue;
            levels[i].Mesh = level.Mesh;
            levels[i].AdjacencyBuffer = level.AdjacencyBuffer;
            levels[i].AverageNeighborDistanceBuffer = level.AverageNeighborDistanceBuffer;
            if (Real(level.TopLevelAccelerationStructure))
            {
                levels[i].TopLevelAccelerationStructure = level.TopLevelAccelerationStructure;
                levels[i].BottomLevelAccelerationStructure = level.BottomLevelAccelerationStructure;
            }
            c.Built[i] = true;
            c.UnwantedSince[i] = -1;
            swapped.Add(i);
        }
        var changed = Point(c);
        foreach (int i in swapped)
            if (!changed.Contains(i))
                changed.Add(i);
        // An ocean's levels are in no set written once: nothing to wait for
        if (!c.Ocean && !changed.TrueForAll(i => Quiet(c, i)))
            Program.GetRenderer().Device.WaitIdle();
        WriteSets(c, changed);
        Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} {Levels(swapped)} built ({why})");
    }

    private static string Levels(List<int> levels) =>
        levels.Count == 1 ? $"level {levels[0]}" : $"levels {string.Join(",", levels.OrderBy(i => i))}";

    // After a failure: every level not built is built again, as the game would have it, and every level's sets are
    // written again (a failure may have left some with the buffers they held before)
    private static void Restore()
    {
        foreach (var c in Collections.Values)
        {
            if (c.Built.Length == 0)
                continue;
            try
            {
                Array.Fill(c.Must, true);
                bool window = _window;
                _window = false;
                try
                {
                    BuildNow(c, "restored");
                }
                finally
                {
                    _window = window;
                }
                WriteSets(c, Enumerable.Range(0, c.Built.Length).ToList());
            }
            catch (Exception e)
            {
                c.Building = null;
                Hooks.Log($"{nameof(FarTerrain)}: {c.Meshes.Id} not built again ({Hooks.Describe(e)}); its terrain is drawn from the levels it has, without ground clutter where its finest isn't built");
            }
        }
    }

    // The sets written with these levels' buffers, as they are now: the planet renderer's and frustum culling's per
    // level, and, with the finest, clutter's generate sets of its bodies. Without a planet renderer (loading), they are
    // written when it is built
    private static void WriteSets(Collection c, List<int> changed)
    {
        if (c.Ocean || changed.Count == 0 || Program.GetPlanetRenderer() is not { } planets)
            return;
        var meshes = c.Meshes;
        var renderer = Program.GetRenderer();
        var geometry = (Dictionary<KeyHash, VkDescriptorSet>)_geometrySets!.GetValue(planets)!;
        var culling = (Dictionary<KeyHash, VkDescriptorSet[]>)_cullingSets!.GetValue(_culling!.GetValue(planets))!;
        static VkDescriptorBufferInfo Info(VkBuffer buffer) => new() { Buffer = buffer, Offset = ByteSize.Zero, Range = VK.WHOLE_SIZE };
        Span<VkDescriptorBufferInfo> infos = stackalloc VkDescriptorBufferInfo[4];
        foreach (int i in changed)
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
        if (changed.Contains(c.Finest))
            foreach (var body in c.Bodies)
                WriteClutterSets(c, body);
    }

    // A body's clutter built on FarClutter's worker: its generate sets written with its finest level as it is now (the
    // writes above skipped them while it was built)
    public static void ClutterBuilt(Celestial body)
    {
        if (_failed || Program.GetPlanetRenderer() is null)
            return;
        foreach (var c in Collections.Values)
            if (!c.Ocean && c.Bodies.Contains(body))
                WriteClutterSets(c, body);
    }

    // As ClutterCubeCellGrid.BuildFrameResources writes them, one per view slot and frame slot
    private static void WriteClutterSets(Collection c, Celestial body)
    {
        var renderer = Program.GetRenderer();
        if (!c.Raytraced || !renderer.RayQueriesSupported || Program.GetPlanetRenderer()?.GroundClutterRenderer is not { } clutter ||
            !clutter.PlanetEcotypeRenderData.TryGetValue(body.Hash, out var ecotypes))
            return;
        var finest = c.Meshes.Items[^1];
        Span<VkWriteDescriptorSet> writes = stackalloc VkWriteDescriptorSet[13];
        foreach (var ecotype in ecotypes)
        {
            var grid = ecotype.CubeCellGrid;
            if (!grid.RenderDataInitialized || FarClutter.OnWorker(ecotype))
                continue;
            for (int slot = 0; slot < grid.GenerateDescriptorSets.Length; slot++)
                for (int f = 0; f < grid.GenerateDescriptorSets[slot].Length; f++)
                    _clutterWrites!(grid, renderer, body, finest, slot, f, ref writes);
        }
    }

    private static void Dispose(CubeMesh level)
    {
        level.Mesh.Dispose();
        if (!level.AdjacencyBuffer.IsNull())
            level.AdjacencyBuffer.Dispose();
        if (!level.AverageNeighborDistanceBuffer.IsNull())
            level.AverageNeighborDistanceBuffer.Dispose();
    }

    // As PlanetMeshCollection.DisposeMeshes disposes the finest level's
    private static void DisposeStructures(CubeMesh level, Renderer renderer)
    {
        if (renderer.RayQueriesSupported)
        {
            if (Real(level.TopLevelAccelerationStructure))
                level.TopLevelAccelerationStructure!.Value.Dispose(renderer.Device);
            if (Real(level.BottomLevelAccelerationStructure))
                level.BottomLevelAccelerationStructure!.Value.Dispose(renderer.Device);
        }
        else
        {
            level.Bvh?.Dispose();
        }
    }

    // The game's exit skips a collection that reads as not loaded: its built levels go here, as
    // PlanetMeshCollection.DisposeMeshes would dispose them, and so does a build nobody swapped in
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
                        {
                            if (level == null)
                                continue;
                            Dispose(level);
                            DisposeStructures(level, renderer);
                        }
                    }
                    catch (AggregateException)
                    {
                        // A failed build disposed what it made
                    }
                }
                if (c.Built.Length == 0 || c.AllBuilt && !c.Ocean)
                    continue;
                var levels = c.Meshes.Items;
                for (int i = 0; i < levels.Count; i++)
                    if (c.Built[i])
                        Dispose(levels[i]);
                // The finest level's structures when it is built (or the BVH it keeps); otherwise it holds the stand-in
                DisposeStructures(levels[^1], renderer);
            }
        }
        catch (Exception e)
        {
            Hooks.Log($"{nameof(FarTerrain)}: at exit, {Hooks.Describe(e)}");
        }
    }
}
