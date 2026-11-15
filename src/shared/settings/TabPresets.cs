using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsaPort.Common;

// The Mac Port tab's presets as data: Performance, Balanced and Quality, each a set of the game's own graphics settings
// and of port settings (the settings table's presets column), and Game Defaults, the game's defaults of the same
// settings and the table's defaults. Two read them: the tab (the hooks' Presets, src/game/hooks/tab/), which sets the
// game's fields by name and draws the cards, and benchmark.sh (--tab-preset), which writes the same values into a run's
// settings.toml by key and its port-settings.toml. With Auto Memory Saving on and 8 GB or less, its limits sit on top
// of every preset's (Limit).
public static class TabPresets
{
    // The port setting that keeps the card picked last; its values are the presets' ids
    public const string Key = "preset";

    // A preset: its id (the value of Key), its card's name, what the card says and its tooltip, its column in the table's
    // presets (-1: the table's defaults), and its game settings by field (null: the game's defaults of the settings the
    // others give, Game Defaults)
    public sealed record Preset(string Id, string Name, string Short, string Help, int Index, IReadOnlyDictionary<string, string>? Game);

    // The game's settings a preset gives: the field (GameSettings', or GameSettings.Graphics' for a key in [graphics])
    // and its key in settings.toml. The hooks check each key against the field's own when the game starts
    public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>
    {
        ["AntiAliasingMethod"] = "display.antialiasing",
        ["CascadedShadowMapFilter"] = "graphics.cascadedShadowMapFilter",
        ["CascadedShadowMapSize"] = "graphics.cascadedShadowMapSize",
        ["TerrainShadowMapSize"] = "graphics.terrainShadowMapSize",
        ["MilkyWayQuality"] = "graphics.milkyWayQuality",
        ["CloudUpscaling"] = "graphics.cloudUpscale",
        ["CloudShadows"] = "graphics.cloudShadows",
        ["Godrays"] = "graphics.terrainGodrays",
        ["GroundClutterShadowCasting"] = "graphics.groundClutterShadowCasting",
        ["PlumeTrailShadows"] = "graphics.plumeTrailShadows",
        ["ExplosionQuality"] = "graphics.explosionQuality",
        ["ExhaustUpscaling"] = "graphics.exhaustUpscale",
        ["ParticleQuality"] = "graphics.particleCountQuality",
        ["AuroraQuality"] = "graphics.auroraQuality",
        ["AmbientOcclusionQuality"] = "graphics.ambientOcclusionQuality",
        ["OceanSimQuality"] = "graphics.oceanSimQuality",
        ["KittenFurQuality"] = "graphics.kittenFurQuality",
        ["IVARayTracing"] = "graphics.IVARayTracing",
        ["RaytracedOpaqueReflectionResolutionScale"] = "graphics.raytracedOpaqueReflectionResolutionScale",
        ["RaytracedShadowResolutionScale"] = "graphics.raytracedResolutionScale",
        ["RaytracedReflectionBounces"] = "graphics.raytracedReflectionBounces",
        ["RaytracedIndirectResolutionScale"] = "graphics.raytracedIndirectResolutionScale",
        ["TerrainTextureCap"] = "graphics.celestialTextureCap",
    };

    public static readonly Preset[] All =
    [
        // MetalFX spatial at half size, without anti-aliasing: temporal there is past its largest scale on a Retina or
        // HiDPI display (4x a side, 3x on an M5) and left 4-pixel blocks on moving things (the rocket, the sea), and the
        // spatial upscaler without the game's MSAA costs 1.3-1.9 ms less a frame (M5, fast run). Thin edges, such as
        // branches against the sky, are rougher
        new("performance", "Performance", "Fastest. Softer, with rougher edges.",
            "The highest frame rate: MetalFX Spatial at half the window's size, without the game's Anti-Aliasing, " +
            "with simpler shadows, clouds and effects and no cockpit ray tracing. With Vsync, it holds half the " +
            "display's refresh rate. Time warp slows down less.",
            0,
            Game(antiAliasing: "Disabled", filter: "Pcf", cascades: 2048, terrain: 2048, milkyWay: "High", clouds: "Low",
                cloudShadows: "Medium", effects: "Low", godrays: false, clutterShadows: false, plumeShadows: false,
                rayTracing: false, planetTextures: 2048)),
        new("balanced", "Balanced", "Faster, with little visible change.",
            "MetalFX Temporal at three quarters of the window's size, with simpler shadow filtering and lighter " +
            "cockpit ray tracing. Time warp slows down less.",
            1,
            Game(antiAliasing: "MultisampleX2", filter: "Pcf", cascades: 2048, terrain: 4096, milkyWay: "High", clouds: "High",
                cloudShadows: "Medium", effects: "Medium", godrays: true, clutterShadows: true, plumeShadows: true,
                rayTracing: true, exhausts: "High", indirectLight: 25)),
        new("quality", "Quality", "Best looking, a little faster.",
            "The game's image at the window's size, with its anti-aliasing and soft shadows. Only what costs much " +
            "and barely shows is lighter: shadow sizes, nearby soft shadows, cockpit ray tracing, the Milky Way.",
            2,
            Game(antiAliasing: "MultisampleX2", filter: "Pcss", cascades: 2048, terrain: 4096, milkyWay: "High", clouds: "High",
                cloudShadows: "High", effects: "High", godrays: true, clutterShadows: true, plumeShadows: true,
                rayTracing: true)),
        new("game_defaults", "Game Defaults", "The game's own graphics settings.",
            "The game's own graphics, with MetalFX and the Extra Performance rows off. Other settings, such as the " +
            "Field of View, stay as they are.",
            -1, null),
    ];

    // The preset of an id, or null
    public static Preset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    // A preset's game settings by field, with Auto Memory Saving's limits when it is on with 8 GB or less (small); null
    // for Game Defaults, whose values are the game's
    public static Dictionary<string, string>? GameValues(Preset preset, bool small)
    {
        if (preset.Game == null)
            return null;
        var game = new Dictionary<string, string>(preset.Game);
        if (small)
            Limit(game);
        return game;
    }

    // Auto Memory Saving's limits with 8 GB or less on a preset's game settings: those it gives, and vessel textures
    public static void Limit(Dictionary<string, string> game)
    {
        foreach (var (field, _, atMost) in AutoMemorySaving.SmallGame)
            game[field] = Text(AutoMemorySaving.Limit(game.TryGetValue(field, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : 0, atMost));
    }

    // A field's key in settings.toml: a preset's, or one of Auto Memory Saving's limits
    public static string KeyOf(string field) =>
        Keys.TryGetValue(field, out var key) ? key : AutoMemorySaving.SmallGame.First(g => g.Field == field).Key;

    // The port settings a preset gives, from the table's presets column (Performance, Balanced, Quality; -1: the
    // defaults), without the render scale when it doesn't upscale. Every Extra Performance row is on in Performance, and
    // in Balanced where its change is small; Quality keeps the game's own image but for what costs much and barely shows:
    // Baked Milky Way (sky pixels at most 1 of 255 apart, the game's Milky Way High at no cost) and the simpler PCSS
    // search. The tumbling physics, the debris settling and the warp cloud shadows change warp, not the image, and are on
    // in Balanced too: warp is too much of the game to leave to Performance. The slower crew portraits are on in
    // Performance only
    public static Dictionary<string, string> Port(Preset preset)
    {
        var port = PortSettings.All.Where(s => s.Presets.Length > 0)
            .ToDictionary(s => s.Key, s => preset.Index < 0 ? s.Default : s.Presets[preset.Index]);
        if (port.GetValueOrDefault("upscaling") == "off")
            port.Remove("render_scale");
        return port;
    }

    // The game's settings, by field name: GameSettings' own (anti-aliasing: the game's, which MetalFX Temporal turns
    // off), the rest in GameSettings.Graphics
    private static Dictionary<string, string> Game(string antiAliasing, string filter, int cascades, int terrain,
        string milkyWay, string clouds, string cloudShadows, string effects, bool godrays, bool clutterShadows,
        bool plumeShadows, bool rayTracing, int planetTextures = 0, string? exhausts = null, int indirectLight = 50)
    {
        // Exhausts and particles at High cost about nothing under MetalFX Temporal 0.75 (High vs Low: exhausts ~0 ms,
        // particles under 1, M5, Mixed Test in a 1440x900 window), and the exhaust's blocks are visibly smaller in motion;
        // explosions at High cost up to ~4 ms (vs Low) while one is on screen, the heaviest moments: Balanced keeps those
        // at Medium. Its clouds are at High: Medium's grain at cloud edges shows in motion, and High costs 0.1-0.6 ms
        // more near the ground (the Tour's descent to earth4)
        exhausts ??= effects;
        // With Baked Milky Way (Performance, Balanced and Quality), Milky Way High costs what Low and Medium do (M5,
        // fast run: every view within 0.1 ms). Cloud Shadows Low costs what Medium does, so no preset goes below Medium.
        //
        // Ambient occlusion, the ocean and fur cost little (0-3 %): lower in Performance only
        string small = effects == "Low" ? "Low" : "High";
        return new Dictionary<string, string>
        {
            ["AntiAliasingMethod"] = antiAliasing,
            ["CascadedShadowMapFilter"] = filter,
            ["CascadedShadowMapSize"] = Text(cascades),
            ["TerrainShadowMapSize"] = Text(terrain),
            ["MilkyWayQuality"] = milkyWay,
            ["CloudUpscaling"] = clouds,
            ["CloudShadows"] = cloudShadows,
            ["Godrays"] = Text(godrays),
            ["GroundClutterShadowCasting"] = Text(clutterShadows),
            ["PlumeTrailShadows"] = Text(plumeShadows),
            ["ExplosionQuality"] = effects,
            ["ExhaustUpscaling"] = exhausts,
            ["ParticleQuality"] = exhausts,
            ["AuroraQuality"] = effects,
            ["AmbientOcclusionQuality"] = small,
            ["OceanSimQuality"] = small,
            ["KittenFurQuality"] = small,
            ["IVARayTracing"] = Text(rayTracing),
            // IVA ray tracing at 50 % reflection and shadow resolution, 1 reflection bounce (the game's: 100/100/4): the
            // cockpit's GPU time -20 % (Balanced) / -23 % (Quality), 3.6-5.6 % of pixels change by more than 2 of 255,
            // sparkles on chrome a little blockier. Performance traces no rays: set there too, so that every preset
            // gives every setting
            ["RaytracedOpaqueReflectionResolutionScale"] = "50",
            ["RaytracedShadowResolutionScale"] = "50",
            ["RaytracedReflectionBounces"] = "1",
            // The cockpit's indirect light at 25 % of the size (the game's: 50 %) where MetalFX Temporal keeps a
            // history of it (Balanced; Upscale.cs, RayHistoryPostfix): the cockpit's GPU time -11 % (M5, a still
            // cockpit at Balanced: 15.8 -> 14.1 ms), the same brightness, steadier than the game's at 50 %. Without
            // the history (Quality draws at the window's size, without MetalFX) its blotches at 25 % move from frame to
            // frame, so the others keep the game's
            ["RaytracedIndirectResolutionScale"] = Text(indirectLight),
            // The game's terrain textures at most this many pixels wide (0: the game's, none), in every preset, so that
            // each one sets it: the planets' textures loaded whole (Earth's clouds, tiling and biome maps, the moons'
            // rock), the finest level the streamer gives the planets' colour maps (Earth, the Moon, Mars; not their
            // heights and normals), ground clutter and rock particles. Performance's 2048 saves about 0.3 GB of graphics
            // memory in the solar system (M5, Mixed Test views, at an 8 GB Mac's texture budget). With 8 GB or less,
            // Auto Memory Saving limits it in every preset, and the vessels' textures and the shadow maps too (Limit)
            ["TerrainTextureCap"] = Text(planetTextures),
        };
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(bool value) => value ? "true" : "false";
}
