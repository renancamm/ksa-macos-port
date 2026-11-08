using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using Brutal.TextureApi;
using Brutal.TextureApi.Abstractions;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Core;
using HarmonyLib;
using KSA;
using KSA.Rendering.Planet;
using RenderCore;

namespace KsaMetal.Hooks.Optimizations;

// Far textures. The game makes every texture's GPU image at load and keeps it, yet much of what a world has is read
// only near it: its biome maps and ground materials while it is a view's nearby body (within 80,000 km of its
// surface), its ground clutter's materials then too, its volumetric clouds below their orbit transition (Jupiter
// 9,000 km), its ring rocks within 20 km of the ring. These images of every body but the home body (about 0.25 GB in
// the solar system) are let go when the system loads (PlanetMeshLibrary.GenerateInUse, before its terrain, render
// data and planet renderer allocate, so they take the memory: an image freed later leaves a hole in the driver's
// blocks), made on a worker thread when one of their bodies comes within 200,000 km of a view's camera, waited for
// before the views are prepared when one is within 100,000 km (a jump: the uploads begin before Unload Far Terrain
// waits for the terrain), and let go again when all their bodies have been farther than 300,000 km for 5 s. An image
// is let go only when every texture the game bound to its bindless slot (ModLibrary.Binders; Load Textures Once puts
// several on one slot) is one of these and none is also elsewhere in a body's template (the home body's, a far-seen
// layer's: the 2D clouds, the distant spheres' maps, the ring's own), so a texture particles or frost use stays.
// Letting go points the slot and the textures at the game's empty texture of the same kind (cube or 2D) and frees
// the image after the frames in flight; a biome map is also in sets written once, the body's TextureSet (bindings 9,
// 10) and each view's MeshSets (10, 11), written again each time. Only TextureReference images, made as their Bind
// makes them (size cap, reduction, mip chain); the others (a height map's, the streamer's) keep theirs.
[Hook(Phase.Default, Switch = nameof(Settings.UnloadFarTextures))]
internal static class FarTextures
{
    // Within NearM it is made at once: the farthest it is read is a ring rock of Saturn's, 82,000 km above the planet
    private const double NearM = 100_000e3, AheadM = 200_000e3, FarM = 300_000e3;
    private const long FarMs = 5000;
    // More frames than the GPU can have in flight (the game's MaxFramesInFlight is 2)
    private const long QuietFrames = 10;

    // One image the game made: its bindless slot, the textures that hold it, the bodies near which it is read
    private sealed class Image(int handle, List<TextureReference> refs, bool cube)
    {
        public readonly int Handle = handle;
        public readonly List<TextureReference> Refs = refs;
        public readonly bool Cube = cube;
        public readonly HashSet<Celestial> Bodies = new(ReferenceEqualityComparer.Instance);
        public readonly List<(Celestial Body, int Index)> Biomes = new();  // CelestialRenderData's views 5, 6
        public SimpleVkTexture? Texture;  // null while let go
        public Task<SimpleVkTexture>? Making;
        public long NeededFrame = -1, FarSince = -1;
        public int Faults;  // makings that failed, logged once
        public string Name => System.IO.Path.GetFileName(Refs[0].LocalPath);
    }

    private static readonly List<Image> Images = new();
    private static readonly HashSet<TextureReference> LetGo = new(ReferenceEqualityComparer.Instance);
    private static readonly List<(SimpleVkTexture Texture, long Frame)> Dying = new();
    private static readonly List<Camera> Cameras = new();
    private static readonly HashSet<Celestial> Landed = new(ReferenceEqualityComparer.Instance);
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static CelestialSystem? _system;
    private static long _frame;
    private static bool _failed, _exiting;
    private static FieldInfo? _imageViews, _textures, _viewports;

    public static string Apply(Harmony harmony)
    {
        var generate = Find.Method(typeof(PlanetMeshLibrary), nameof(PlanetMeshLibrary.GenerateInUse));
        var celestials = Find.Method(typeof(Program), "OnFrameCelestials");
        var planets = Find.Method(typeof(PlanetRenderer), nameof(PlanetRenderer.OnPreRender));
        var clutter = Find.Method(typeof(PlanetRenderer), nameof(PlanetRenderer.UpdateGroundClutter));
        var dispose = Find.Method(typeof(TextureReference), nameof(TextureReference.Dispose));
        _imageViews = Find.Field(typeof(CelestialRenderData), "_imageViews");
        _textures = Find.Field(typeof(CelestialRenderData), "_textures");
        _viewports = Find.Field(typeof(PlanetRenderer), "_planetViewportData");
        TextureImages.Lookup();
        harmony.Patch(generate, prefix: new HarmonyMethod(typeof(FarTextures), nameof(SystemLoading)));
        // Before Unload Far Terrain's postfix, which waits for a jump's terrain: the textures upload meanwhile
        harmony.Patch(celestials, postfix: new HarmonyMethod(typeof(FarTextures), nameof(Frame)) { priority = Priority.High });
        harmony.Patch(planets, prefix: new HarmonyMethod(typeof(FarTextures), nameof(ViewsPreparing)));
        harmony.Patch(clutter, prefix: new HarmonyMethod(typeof(FarTextures), nameof(ViewsPreparing)));
        harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(FarTextures), nameof(Disposing)));
        return "on";
    }

    // Loading is about to build the system's terrain, render data and planet renderer: every image is let go now, so
    // what loading allocates next takes its memory (freed later, it would stay with the driver's blocks), and the
    // render data and the renderer's sets take the empty textures; the first frames make what a view is near
    private static void SystemLoading()
    {
        if (_failed)
            return;
        try
        {
            if (Universe.CurrentSystem is not { } system || system == _system)
                return;
            RestoreAll();
            _system = system;
            Collect(system);
            long bytes = 0;
            foreach (var image in Images)
            {
                var texture = image.Texture!;
                bytes += Size(texture);
                Release(image);
            }
            // No frame has drawn them
            foreach (var (texture, _) in Dying)
                texture.Dispose();
            Dying.Clear();
            Hooks.Log($"{nameof(FarTextures)}: {Images.Count} let go at load, {bytes >> 20} MB");
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void Frame()
    {
        if (_failed)
            return;
        try
        {
            _frame++;
            var system = Universe.CurrentSystem;
            if (system == null || Program.GetPlanetRenderer() == null)
                return;
            if (system != _system)
            {
                RestoreAll();
                _system = system;
                Collect(system);
            }
            Update();
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // The planet renderer is about to prepare the views, or to place the clutter vessels collide with (also on a path
    // that doesn't prepare views first): what is needed this frame is waited for, the uploads the frame's start began
    // and those of a body the camera reached after it
    private static void ViewsPreparing()
    {
        if (_failed || Images.Count == 0)
            return;
        try
        {
            CollectCameras();
            var now = new List<Image>();
            foreach (var image in Images)
                if (Distance(image) < NearM)
                {
                    if (image.Texture == null)
                        now.Add(image);
                    image.NeededFrame = _frame;
                }
            MakeNow(now, "jump");
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void Failed(Exception e)
    {
        _failed = true;
        Hooks.Fail(nameof(FarTextures), e);
        try
        {
            RestoreAll();
        }
        catch (Exception again)
        {
            Hooks.Log($"{nameof(FarTextures)}: restoring, {Hooks.Describe(again)}");
        }
    }

    // The images of every body but the home body that only views near it read, by bindless slot
    private static void Collect(CelestialSystem system)
    {
        var candidates = new Dictionary<TextureReference, HashSet<Celestial>>(ReferenceEqualityComparer.Instance);
        var kept = new List<TextureReference>();
        var biomes = new List<(TextureReference Texture, Celestial Body, int Index)>();
        var home = system.HomeBody as Celestial;
        foreach (var body in system.All.AsSpan())
        {
            if (body is not Celestial celestial || celestial.BodyTemplate is not { } template)
                continue;
            // What is read only near the body: walked for a body other than the home body; the rest of the template is
            // kept, all of it for the home body
            var nearOnly = new List<object?>
            {
                template.BiomesReference, template.TerrainReference?.BiomeMaterials, template.GroundClutterReference,
                template.RingsReference?.RingObjects
            };
            foreach (var layer in template.CloudsReference?.Layers ?? [])
                nearOnly.Add(layer.VolumetricCloud);
            nearOnly.RemoveAll(o => o == null);
            bool far = celestial != home;
            Walk(template, kept, far ? new HashSet<object>(nearOnly!, ReferenceEqualityComparer.Instance) : null);
            if (!far)
                continue;
            var found = new List<TextureReference>();
            foreach (var root in nearOnly)
                Walk(root, found, null);
            if (template.BiomesReference is { } biomeMaps)
            {
                // CelestialRenderData takes a biome map only when it is a cubemap
                if (biomeMaps.BiomeIdMap?.Value is { } id && id.TextureAsset?.Texture.IsCubemap == true)
                    biomes.Add((id, celestial, 5));
                if (biomeMaps.BiomeControlMap?.Value is { } control && control.TextureAsset?.Texture.IsCubemap == true)
                    biomes.Add((control, celestial, 6));
            }
            foreach (var texture in found)
            {
                if (!candidates.TryGetValue(texture, out var bodies))
                    candidates[texture] = bodies = new HashSet<Celestial>(ReferenceEqualityComparer.Instance);
                bodies.Add(celestial);
            }
        }
        foreach (var texture in kept)
            candidates.Remove(texture);
        // Every texture the game bound, by slot: a slot is let go only when all of them are candidates
        var slots = new Dictionary<int, List<TextureReference>>();
        foreach (var binder in ModLibrary.Binders)
            if (binder is TextureReference { Texture: not null } texture)
            {
                if (!slots.TryGetValue(texture.BindlessHandle, out var list))
                    slots[texture.BindlessHandle] = list = new List<TextureReference>();
                list.Add(texture);
            }
        long bytes = 0;
        foreach (var (handle, refs) in slots)
        {
            if (!refs.TrueForAll(r => candidates.ContainsKey(r) && Remakeable(r)))
                continue;
            var image = new Image(handle, refs, refs[0].TextureAsset.Texture.IsCubemap) { Texture = refs[0].Texture };
            foreach (var r in refs)
                image.Bodies.UnionWith(candidates[r]);
            foreach (var (texture, body, index) in biomes)
                if (refs.Contains(texture))
                    image.Biomes.Add((body, index));
            Images.Add(image);
            bytes += Size(image.Texture);
        }
        Hooks.Log($"{nameof(FarTextures)}: {Images.Count} textures of far worlds can be let go, {bytes >> 20} MB: " +
            string.Join(", ", Images.ConvertAll(i => $"{i.Name} ({string.Join("/", System.Linq.Enumerable.Select(i.Bodies, b => b.Id))})")));
    }

    // The textures under a body's reference: fields of the game's own types and their lists, through the content's
    // references to what they resolved to (a level deeper for none), not back up to a body (the root may be one), and not
    // into the objects in skip. An object is walked again when reached by a shorter path: a resolved asset is shared
    // (a clutter material is under its ecotype's MaterialReferences and, deeper, under its objects' LODs), and the
    // depth limit would otherwise cut its fields on the longer path met first
    private static void Walk(object? root, List<TextureReference> found, HashSet<object>? skip)
    {
        var seen = new Dictionary<object, int>(ReferenceEqualityComparer.Instance);
        void Visit(object? o, int depth)
        {
            if (o == null || depth > 8 || seen.TryGetValue(o, out int was) && was <= depth || skip != null && skip.Contains(o))
                return;
            seen[o] = depth;
            if (o is TextureReference texture)
            {
                found.Add(texture);
                return;
            }
            var type = o.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DataReference<>))
            {
                Visit(type.GetField(nameof(DataReference<TextureReference>.Value))!.GetValue(o), depth);
                return;
            }
            if (o is IEnumerable list and not string)
            {
                // Not into lists of values (a mesh's data)
                var element = type.IsArray ? type.GetElementType() : type.IsGenericType ? type.GetGenericArguments()[0] : null;
                if (element == null || element.IsValueType)
                    return;
                foreach (var item in list)
                    Visit(item, depth + 1);
                return;
            }
            if (type.Assembly != typeof(Celestial).Assembly || o is Celestial or Mod || depth > 0 && o is AstronomicalTemplate)
                return;
            for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    if (!field.FieldType.IsValueType && field.FieldType != typeof(string))
                        Visit(field.GetValue(o), depth + 1);
        }
        Visit(root, 0);
    }

    private static bool Remakeable(TextureReference r) =>
        r.GetType() == typeof(TextureReference) && r.TextureAsset != null && !TextureImages.Streamed(r);

    // The views' cameras, and the bodies with a vessel near their surface: GroundClutterRenderer.OnFrameViewport places
    // the clutter it collides with (biome maps, placement maps) wherever the cameras are, within the radius it uses
    private static void CollectCameras()
    {
        Cameras.Clear();
        foreach (var view in ViewportRegistry.Views)
            if (Views.Prepared(view, terrain: false) && view.GetCamera() is { } camera)
                Cameras.Add(camera);
        Landed.Clear();
        foreach (var image in Images)
            foreach (var body in image.Bodies)
            {
                if (Landed.Contains(body))
                    continue;
                double radius = body.MeanRadius + body.MaxTerrainHeightApprox * 1.25;
                foreach (var child in body.Children)
                    if (child is Vehicle vehicle && vehicle.GetPositionCce().LengthSquared() <= radius * radius)
                    {
                        Landed.Add(body);
                        break;
                    }
            }
    }

    // The surface distance from the nearest camera to the nearest of the image's bodies, none with a vessel on it
    private static double Distance(Image image)
    {
        foreach (var body in image.Bodies)
            if (Landed.Contains(body))
                return 0;
        double nearest = double.PositiveInfinity;
        foreach (var camera in Cameras)
            foreach (var body in image.Bodies)
                nearest = Math.Min(nearest, (camera.PositionEcl - body.GetPositionEcl()).Length() - body.MeanRadius);
        return nearest;
    }

    private static void Update()
    {
        CollectCameras();
        long now = Clock.ElapsedMilliseconds;
        int letGo = 0, made = 0;
        long letGoBytes = 0, madeBytes = 0;
        foreach (var image in Images)
        {
            double distance = Distance(image);
            if (distance < NearM)
            {
                // Begun now, waited for before the views are prepared (ViewsPreparing)
                if (image.Texture == null && image.Making is not { IsFaulted: false })
                {
                    if (image.Making is { IsFaulted: true } && image.Faults++ == 0)
                        Hooks.Log($"{nameof(FarTextures)}: {image.Name} not made ({Hooks.Describe(image.Making.Exception!)}), tried again each frame it is near");
                    image.Making = Make(image);
                }
                image.NeededFrame = _frame;
                image.FarSince = -1;
            }
            else if (image.Texture == null)
            {
                if (image.Making is { IsFaulted: true })
                {
                    Hooks.Log($"{nameof(FarTextures)}: {image.Name} not made ({Hooks.Describe(image.Making.Exception!)}), again later");
                    image.Making = null;
                }
                else if (image.Making is { IsCompleted: true })
                {
                    madeBytes += SwapIn(image);
                    made++;
                }
                else if (image.Making == null && distance < AheadM)
                    image.Making = Make(image);
            }
            else if (distance <= FarM)
                image.FarSince = -1;
            else if (image.FarSince < 0)
                image.FarSince = now;
            else if (now - image.FarSince >= FarMs && _frame - image.NeededFrame > QuietFrames)
            {
                letGoBytes += Size(image.Texture);
                Release(image);
                letGo++;
            }
        }
        if (made > 0)
            Hooks.Log($"{nameof(FarTextures)}: {made} made ahead, {madeBytes >> 20} MB");
        if (letGo > 0)
            Hooks.Log($"{nameof(FarTextures)}: {letGo} let go, {letGoBytes >> 20} MB");
        for (int i = Dying.Count - 1; i >= 0; i--)
            if (_frame - Dying[i].Frame > QuietFrames)
            {
                Dying[i].Texture.Dispose();
                Dying.RemoveAt(i);
            }
    }

    // The image as TextureReference.Bind makes it, on a worker thread
    private static Task<SimpleVkTexture> Make(Image image)
    {
        var texture = image.Refs[0];
        var renderer = Program.GetRenderer();
        return Task.Run(() =>
        {
            int cap = TextureImages.Cap(texture);
            var asset = texture.TextureAsset;
            var descriptor = RenderCore.TextureDescriptor.FromTexture(asset.Texture);
            var reduction = !asset.Texture.Format.Descriptor().IsBlockCompressed && descriptor.MipLevels <= 1
                ? SimpleVkTexture.CreateOptions.ReductionMethod.Downsample
                : SimpleVkTexture.CreateOptions.ReductionMethod.ClosestMipLevel;
            var options = new SimpleVkTexture.CreateOptions(cap, reduction, texture.Manifest?.MipMaps ?? true);
            using var staging = renderer.Allocator.CreateStagingPool(renderer.Graphics, 1);
            return new SimpleVkTexture(renderer.Allocator, staging, asset, options);
        });
    }

    // Images a view needs this frame: all made at once, waited for
    private static void MakeNow(List<Image> images, string why)
    {
        if (images.Count == 0)
            return;
        var watch = Stopwatch.StartNew();
        foreach (var image in images)
        {
            if (image.Making is { IsFaulted: true })
                image.Making = null;
            image.Making ??= Make(image);
        }
        long bytes = 0;
        foreach (var image in images)
        {
            image.Making!.Wait();
            bytes += SwapIn(image);
        }
        Hooks.Log($"{nameof(FarTextures)}: {images.Count} made ({why}), {bytes >> 20} MB, waited {watch.ElapsedMilliseconds} ms");
    }

    private static long SwapIn(Image image)
    {
        var texture = image.Making!.Result;
        image.Making = null;
        image.Texture = texture;
        Point(image, texture, texture.ImageViewEx.VkImageView);
        foreach (var r in image.Refs)
            LetGo.Remove(r);
        return Size(texture);
    }

    private static void Release(Image image)
    {
        var empty = image.Cube ? TextureReference.EmptyCubemap : TextureReference.EmptyWhite;
        Point(image, empty.Texture, empty.ImageView);
        foreach (var r in image.Refs)
            LetGo.Add(r);
        Dying.Add((image.Texture!, _frame));
        image.Texture = null;
        image.FarSince = -1;
    }

    // The slot, the textures and the sets written once with the image's view
    private static void Point(Image image, SimpleVkTexture texture, VkImageView view)
    {
        foreach (var r in image.Refs)
            TextureImages.Set(r, texture, view);
        Program.Instance.BindlessTextures.SetTexture(image.Handle, view);
        // While loading, the render data and the planet renderer, made later, take the textures' views
        var planets = Program.GetPlanetRenderer();
        var device = Program.GetRenderer().Device;
        var viewports = planets != null ? (PlanetViewportData[])_viewports!.GetValue(planets)! : [];
        foreach (var (body, index) in image.Biomes)
        {
            // A struct whose arrays are null until Universe.SetupRenderData makes it
            object data = body.RenderData;
            if (_imageViews!.GetValue(data) is not VkImageView[] views || _textures!.GetValue(data) is not SimpleVkTexture[] textures)
                continue;
            views[index] = view;
            textures[index] = texture;
            if (planets == null)
                continue;
            // As the PlanetRenderer constructor writes them: TextureSet from binding 4, each view's MeshSets from 8
            WriteImage(device, body.TextureSet, index + 4, view);
            foreach (var viewport in viewports)
                if (viewport.MeshSets.TryGetValue(body.Hash, out var sets))
                    foreach (var set in sets)
                        WriteImage(device, set, index + 5, view);
        }
    }

    private static unsafe void WriteImage(Device device, VkDescriptorSet set, int binding, VkImageView view)
    {
        var info = new VkDescriptorImageInfo { ImageView = view, ImageLayout = VkImageLayout.ShaderReadOnlyOptimal };
        var write = new VkWriteDescriptorSet
        {
            DescriptorType = VkDescriptorType.SampledImage,
            DescriptorCount = 1,
            DstSet = set,
            DstBinding = binding,
            ImageInfo = &info
        };
        device.UpdateDescriptorSets(new ReadOnlySpan<VkWriteDescriptorSet>(ref write), default);
    }

    // Every image made again, and kept from then on
    private static void RestoreAll()
    {
        foreach (var image in Images)
            if (image.Texture == null)
                try
                {
                    MakeNow([image], "restored");
                }
                catch (Exception e)
                {
                    Hooks.Log($"{nameof(FarTextures)}: {image.Name} not restored, {Hooks.Describe(e)}");
                }
        Images.Clear();
    }

    private static long Size(SimpleVkTexture texture) => TextureImages.Size(texture);

    // At exit a let-go texture holds the empty texture, which the game frees itself
    private static bool Disposing(TextureReference __instance)
    {
        if (!_exiting)
        {
            // The game is freeing its textures before the device: the uploads still running end, and what isn't held
            // by a texture is freed
            _exiting = true;
            foreach (var image in Images)
                try
                {
                    if (image.Making?.Wait(5000) == true && image.Making.Result != image.Texture)
                        image.Making.Result.Dispose();
                }
                catch (Exception e)
                {
                    Hooks.Log($"{nameof(FarTextures)}: at exit, {Hooks.Describe(e)}");
                }
            try
            {
                foreach (var (texture, _) in Dying)
                    texture.Dispose();
            }
            catch (Exception e)
            {
                Hooks.Log($"{nameof(FarTextures)}: at exit, {Hooks.Describe(e)}");
            }
            Dying.Clear();
        }
        return !LetGo.Contains(__instance);
    }
}
