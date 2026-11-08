using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using Core;
using HarmonyLib;
using KSA;
using Key = (string File, int Cap, bool Mips, Brutal.VulkanApi.VkFormat Format, Brutal.KtxApi.KtxTranscodeFmt Transcode);

namespace KsaMetal.Hooks.Optimizations;

// Shared textures. The game makes a GPU image for each texture it names (TextureReference.Bind, on worker threads
// while loading), and some files are named more than once under other ids: the Moon's rock material also as Saturn's
// ring rocks, the ground textures of the Moon, Mars's snow and Pan's ice also as particles and frost (about 85 MB in
// the solar system), often in another category, whose size cap the game's settings set (the ring rocks' none, the
// Moon's rocks the Terrain textures'). A texture of the same file, the same cap (a cap at least the file's size counts
// as none) and the same manifest (mip chain, formats; XML may give one without a file beside the texture) now takes the image, view and bindless slot of the first one bound, and only that one frees them at exit (TextureReference.Dispose). Nothing else writes a
// texture's slot or image after binding. Height maps load and bind otherwise and keep their own, as do the maps the
// celestial streamer makes (Stream in the content). An error of the hook's own while a texture binds fails the hook:
// that texture and every one after it bind as the game binds them, and those already sharing keep their image.
[Hook(Phase.Default, Switch = nameof(Settings.LoadTexturesOnce))]
internal static class SharedTextures
{
    private static readonly Dictionary<Key, object> Gates = new();
    private static readonly Dictionary<Key, TextureReference> Owners = new();
    private static readonly HashSet<TextureReference> Sharers = new(ReferenceEqualityComparer.Instance);
    // The gate this thread holds while the game binds the first texture of a file, and that file
    [ThreadStatic] private static object? _held;
    [ThreadStatic] private static Key _heldKey;
    private static FieldInfo? _bound, _imgui;
    private static MethodInfo? _handle;
    private static long _bytes;
    private static bool _failed;

    public static string Apply(Harmony harmony)
    {
        var bind = Find.Method(typeof(TextureReference), nameof(TextureReference.Bind));
        var dispose = Find.Method(typeof(TextureReference), nameof(TextureReference.Dispose));
        var bindAll = Find.Method(typeof(ModLibrary), nameof(ModLibrary.Bind));
        _bound = Find.Field(typeof(TextureReference), "_isBound");
        _imgui = Find.Field(typeof(TextureReference), "_addToImgui");
        _handle = Find.Setter(typeof(TextureReference), nameof(TextureReference.BindlessHandle));
        TextureImages.Lookup();
        harmony.Patch(bind, prefix: new HarmonyMethod(typeof(SharedTextures), nameof(Binding)),
            postfix: new HarmonyMethod(typeof(SharedTextures), nameof(Bound)),
            finalizer: new HarmonyMethod(typeof(SharedTextures), nameof(BindEnd)));
        harmony.Patch(dispose, prefix: new HarmonyMethod(typeof(SharedTextures), nameof(Disposing)));
        harmony.Patch(bindAll, postfix: new HarmonyMethod(typeof(SharedTextures), nameof(AllBound)));
        return "on";
    }

    // A texture about to be bound: one of a file already bound takes that image; the first of a file is bound by the
    // game while this thread holds the file's gate, so a second one waits for it
    private static bool Binding(TextureReference __instance, Renderer renderer)
    {
        if (_failed)
            return true;
        try
        {
            return Bind(__instance, renderer);
        }
        catch (Exception e)
        {
            _failed = true;
            Hooks.Fail(nameof(SharedTextures), e);
            return true;
        }
    }

    private static bool Bind(TextureReference __instance, Renderer renderer)
    {
        var type = __instance.GetType();
        if (type != typeof(TextureReference) || (bool)_bound!.GetValue(__instance)! || __instance.TextureAsset == null ||
            TextureImages.Streamed(__instance))
            return true;
        var manifest = __instance.Manifest ?? new TextureManifest();
        // The largest size Bind lets the image have; one at least the file's size counts as none
        int cap = TextureImages.Cap(__instance);
        if (cap >= Math.Max(__instance.Width, __instance.Height))
            cap = 0;
        Key key = (Path.GetFullPath(__instance.ModPath), cap, manifest.MipMaps, manifest.Format,
            manifest.SuperCompressionBlockFormatFamily);
        object gate;
        TextureReference? owner;
        lock (Gates)
        {
            if (!Gates.TryGetValue(key, out gate!))
                Gates[key] = gate = new object();
        }
        Monitor.Enter(gate);
        try
        {
            lock (Gates)
                Owners.TryGetValue(key, out owner);
            if (owner == null || owner == __instance)
            {
                // Kept held while the game binds it: BindEnd lets go
                _held = gate;
                _heldKey = key;
                return true;
            }
            TextureImages.Set(__instance, owner.Texture, owner.ImageView);
            _handle!.Invoke(__instance, [owner.BindlessHandle]);
            // A sharer from here on, so that its Dispose never frees the owner's image, whatever throws below; bound last,
            // so that a texture the game binds after a failure here is still unbound
            lock (Gates)
            {
                Sharers.Add(__instance);
                _bytes += TextureImages.Size(owner.Texture);
            }
            _bound.SetValue(__instance, true);
            if ((bool)_imgui!.GetValue(__instance)!)
                __instance.BindToImgui(renderer.LinearSampler);
            return false;
        }
        finally
        {
            if (_held != gate)
                Monitor.Exit(gate);
        }
    }

    private static void Bound(TextureReference __instance)
    {
        if (_held != null && (bool)_bound!.GetValue(__instance)!)
            lock (Gates)
                Owners[_heldKey] = __instance;
    }

    private static Exception? BindEnd(Exception? __exception)
    {
        if (_held != null)
        {
            Monitor.Exit(_held);
            _held = null;
        }
        return __exception;
    }

    private static bool Disposing(TextureReference __instance)
    {
        lock (Gates)
            return !Sharers.Contains(__instance);
    }

    private static void AllBound()
    {
        lock (Gates)
            if (Sharers.Count > 0)
                Hooks.Log($"{nameof(SharedTextures)}: {Sharers.Count} textures share an image, {_bytes >> 20} MB");
    }
}
