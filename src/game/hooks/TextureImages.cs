using System;
using HarmonyLib;
using KSA;
using RenderCore;

namespace KsaMetal.Hooks;

// What the far textures and the shared textures hooks have in common about a TextureReference's image: the setters
// Bind writes, whether the celestial streamer makes it, the size cap Bind applies, and an image's size
internal static class TextureImages
{
    private static System.Reflection.MethodInfo? _texture, _view;

    // Looked up by each hook's Apply before it patches: Skip when the game's code moved
    public static void Lookup()
    {
        _texture = Find.Setter(typeof(TextureReference), nameof(TextureReference.Texture));
        _view = Find.Setter(typeof(TextureReference), nameof(TextureReference.ImageView));
    }

    // The texture's image and view, as Bind sets them
    public static void Set(TextureReference texture, object image, object view)
    {
        _texture!.Invoke(texture, [image]);
        _view!.Invoke(texture, [view]);
    }

    // A map the celestial streamer makes (Stream in the content, its file read by the streamer): Bind makes none
    public static bool Streamed(TextureReference texture) => texture.Stream;

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
