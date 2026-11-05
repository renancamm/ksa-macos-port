using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using KSA;
using KSA.Rendering;

namespace KsaMetal.Hooks.Fixes;

// Clutter resnap. The ground clutter is placed with a world's streamed heights and normals (the slopes its terrain
// modifiers shape the heights with) and tinted with its streamed colours. The game places clutter again when the
// heights stream in finer (GroundClutterRenderer.AccumulateViewportCells for what is drawn, ClutterEcotypePhysicalData.
// UpdateGroundClutter for what vessels collide with: CelestialTextureStreamer.GetHeightContentVersion, then a
// placement on each of the next MaxFramesInFlight frames), but not when the normals or colours do. Clutter keeps the
// slopes and tint of the moment the heights last changed, while the terrain drawn every frame takes the finer ones:
// after a jump, rocks sit metres too low, many small ones under the ground, by how much depending on the frame the
// streamer finishes each map. A change of the normals' content version (for what is drawn, the colours' too) now
// counts as a change of the heights': the remembered height version is cleared, and the game places again as it does
// for heights. Colliding clutter doesn't watch the colours: they only tint it, and placing it again drops its
// colliders until the readback a few frames later.
[Hook(Phase.Default)]
internal static class ClutterResnap
{
    private static FieldInfo? _entries, _normal, _diffuse, _contentVersion;
    private static AccessTools.FieldRef<GroundClutterRenderer, int[]>? _viewVersions;
    private static AccessTools.FieldRef<ClutterEcotypePhysicalData, int>? _physicalVersion;
    // The versions each view slot, and each colliding ecotype, was last placed with
    private static readonly Dictionary<int, (Celestial Body, long Version)> Views = new();
    private static readonly ConditionalWeakTable<ClutterEcotypePhysicalData, StrongBox<int>> Physical = new();
    private static bool _failed;

    public static string Apply(Harmony harmony)
    {
        var accumulate = Find.Method(typeof(GroundClutterRenderer), nameof(GroundClutterRenderer.AccumulateViewportCells));
        var physical = Find.Method(typeof(ClutterEcotypePhysicalData), nameof(ClutterEcotypePhysicalData.UpdateGroundClutter));
        var entry = AccessTools.Inner(typeof(CelestialTextureStreamer), "Entry") ?? throw new Skip("CelestialTextureStreamer.Entry not found");
        var map = AccessTools.Inner(typeof(CelestialTextureStreamer), "StreamedMap") ?? throw new Skip("CelestialTextureStreamer.StreamedMap not found");
        _entries = Find.Field(typeof(CelestialTextureStreamer), "_entries");
        _normal = Find.Field(entry, "Normal");
        _diffuse = Find.Field(entry, "Diffuse");
        _contentVersion = Find.Field(map, "ContentVersion");
        _viewVersions = AccessTools.FieldRefAccess<GroundClutterRenderer, int[]>(Find.Field(typeof(GroundClutterRenderer), "_lastHeightVersion"));
        _physicalVersion = AccessTools.FieldRefAccess<ClutterEcotypePhysicalData, int>(
            Find.Field(typeof(ClutterEcotypePhysicalData), "_lastHeightContentVersion"));
        harmony.Patch(accumulate, prefix: new HarmonyMethod(typeof(ClutterResnap), nameof(Accumulating)));
        harmony.Patch(physical, prefix: new HarmonyMethod(typeof(ClutterResnap), nameof(PhysicalUpdating)));
        return "on";
    }

    // Clutter is about to take the views' nearby bodies, as PlanetRenderer.OnPreRender picks the views
    private static void Accumulating(GroundClutterRenderer __instance)
    {
        if (_failed)
            return;
        try
        {
            foreach (var view in ViewportRegistry.Views)
            {
                if (!(view.IsMain() || view.Type == ViewportType.Secondary && view.Visible && view.HasAny(ViewportOptionFlags.RenderTerrain)) ||
                    view.GetCamera()?.GetEffectiveNearbyCelestial() is not { } body ||
                    Entry(body) is not { } entry)
                    continue;
                long version = (long)Version(entry, _normal!) << 32 | (uint)Version(entry, _diffuse!);
                int slot = view.ShaderSlot;
                if (Views.TryGetValue(slot, out var last) && last.Body == body && last.Version != version)
                    _viewVersions!(__instance)[slot] = int.MinValue;
                Views[slot] = (body, version);
            }
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // The clutter vessels collide with, near them: placed again when the body's normals change
    private static void PhysicalUpdating(ClutterEcotypePhysicalData __instance)
    {
        if (_failed)
            return;
        try
        {
            if (!__instance.EcotypeReference.Collideable || Entry(__instance.Celestial) is not { } entry)
                return;
            int version = Version(entry, _normal!);
            if (Physical.TryGetValue(__instance, out var last))
            {
                if (last.Value != version)
                    _physicalVersion!(__instance) = int.MinValue;
                last.Value = version;
            }
            else
                Physical.Add(__instance, new StrongBox<int>(version));
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    // From then on clutter is placed again as the game does: when the heights change
    private static void Failed(Exception e)
    {
        _failed = true;
        Hooks.Fail(nameof(ClutterResnap), e);
    }

    // The streamer's maps of the body, none while it holds none
    private static object? Entry(Celestial body) =>
        Program.GetPlanetRenderer()?.TextureStreamer is { } streamer && _entries!.GetValue(streamer) is IDictionary entries ? entries[body] : null;

    // A map's content version, 0 while the body has none of that kind
    private static int Version(object entry, FieldInfo field) =>
        field.GetValue(entry) is { } map ? (int)_contentVersion!.GetValue(map)! : 0;
}
