using System;
using HarmonyLib;
using KSA;
using RenderCore;

namespace KsaMetal.Hooks;

// What the far textures and the shared textures hooks have in common about a TextureReference's image: the setters
// Bind writes, whether the celestial streamer replaces it, the size cap Bind applies, and an image's size
internal static class TextureImages
{
    private static System.Reflection.MethodInfo? _texture, _view;
    private static Func<TextureReference, int>? _coarseCap;

    // Looked up by each hook's Apply before it patches: Skip when the game's code moved
    public static void Lookup()
    {
        _texture = Find.Setter(typeof(TextureReference), nameof(TextureReference.Texture));
        _view = Find.Setter(typeof(TextureReference), nameof(TextureReference.ImageView));
        _coarseCap = AccessTools.MethodDelegate<Func<TextureReference, int>>(Find.Method(typeof(TextureReference), "StreamingCoarseCap"));
    }

    // The texture's image and view, as Bind sets them
    public static void Set(TextureReference texture, object image, object view)
    {
        _texture!.Invoke(texture, [image]);
        _view!.Invoke(texture, [view]);
    }

    // A map the celestial streamer replaces (its coarse cap is set): its image isn't Bind's to keep
    public static bool Streamed(TextureReference texture) => _coarseCap!(texture) > 0;

    // The largest size Bind lets the image have, as it works it out: the category's cap from the game's settings,
    // the manifest's when smaller (0: none)
    public static int Cap(TextureReference texture)
    {
        int cap = GameSettings.GetMaxTextureSize(texture.Category);
        int manifestCap = texture.Manifest?.MaxSize ?? 0;
        if (manifestCap != 0 && (cap == 0 || manifestCap < cap))
            cap = manifestCap;
        return cap;
    }

    public static long Size(SimpleVkTexture texture) => (long)texture.ImageEx.AllocationInfo.MemAllocationInfo.AllocSize;
}
