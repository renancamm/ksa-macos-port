using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using KSA;
using KsaPort.Common;

namespace KsaMetal.Hooks.Tab;

// The Mac Port tab's presets: Performance, Balanced and Quality, each a set of the game's own graphics settings and
// of port settings (the settings table's presets column), and Game Defaults, the defaults of the same settings. Auto
// Memory Saving (AutoMemorySaving, in the shared library) is the second preset beside them: the Memory rows are its
// own, which these leave alone (Fewer Effects at Once it shares with Performance), but on a Mac with 8 GB of memory
// or less and Auto Memory Saving on, every preset's textures and shadow maps are at most 2048 pixels wide and its
// texture streaming budget at most 512 MB. A preset's card in the tab makes its values pending changes, in the game's
// tabs and in this one, as if the player had set each row; the game's Apply keeps them, Discard drops them. Nothing
// else changes a setting, but a fresh install's first start, where Auto Memory Saving, on by default, sets its
// values (the loader the port settings, FirstStart here the game's). The card picked last is kept in the file
// (preset): the tab counts the settings that differ from it, unless they are another preset's. With Auto Memory
// Saving on, its values sit on top of every card's, as its limits do: Fewer Effects at Once with 8 GB or less.
//
// Every preset gives every setting any preset sets, so a preset after another leaves nothing of the first, except
// the port rows the tab can't change (set outside, their hook unavailable) and the render scale without upscaling,
// which does nothing then: those keep their values. The game's fields are found
// by name when the game starts: a field that moved leaves the presets out (skipped).
//
// In Fullscreen, every preset also sets the Resolution to the size the screen's desktop has (Displays), so the render
// scale alone decides the 3D view's size and the display keeps its mode. Windowed, the Resolution is the window's size,
// and Borderless doesn't use it: those keep it. The card reads the Mode as it is then: a Mode set to Fullscreen after it
// keeps the Resolution, and unless that is the desktop's size (or 0 x 0, the window's) it counts as a setting that
// differs. An error reading
// the display makes the presets unavailable, as any (Hooks.Fail), without taking the tab with them.
[Hook(Phase.PortParts)]
internal static class Presets
{
    // A preset: its game settings for a Mac, and its port settings, the table's presets column at this index (-1: the
    // table's defaults, Game Defaults)
    public sealed class Preset(string name, string shortHelp, string help, Func<Mac, Dictionary<string, string>> game,
        int index)
    {
        public string Name => name;
        // Its value of the port setting that keeps the card picked last (Game Defaults: game_defaults)
        public string Id => name.ToLowerInvariant().Replace(' ', '_');
        // What its card says, and its tooltip
        public string Short => shortHelp;
        public string Help => help;
        // The values for this Mac, from when the game starts: game fields by name, port settings by key
        public Dictionary<string, string> Game { get; private set; } = new();
        public Dictionary<string, string> Port { get; private set; } = new();

        public void For(Mac mac, bool memorySaving)
        {
            Game = game(mac);
            if (memorySaving && mac.Tier == AutoMemorySaving.Tier.Small)
                Limit(Game);
            Port = Port(index);
            if (Port["upscaling"] == "off")
                Port.Remove("render_scale");
            if (memorySaving)
                foreach (var (key, value) in AutoMemorySaving.Port(mac.Tier).Where(kv => Port.ContainsKey(kv.Key)))
                    Port[key] = value;
        }
    }

    // What Auto Memory Saving goes by: this Mac's memory
    public sealed record Mac(string Chip, int MemoryGb)
    {
        public AutoMemorySaving.Tier Tier => AutoMemorySaving.TierOf(MemoryGb);
    }

    public static readonly Preset[] All =
    [
        // MetalFX spatial at half size, without anti-aliasing: temporal there is past its largest scale on a Retina or
        // HiDPI display (4x a side, 3x on an M5) and left 4-pixel blocks on moving things (the rocket, the sea), and the
        // spatial upscaler without the game's MSAA costs 1.3-1.9 ms less a frame (M5, fast run). Thin edges, such as
        // branches against the sky, are rougher
        new("Performance", "Fastest. Softer, with rougher edges.",
            "The fastest the game gets: MetalFX Spatial draws the 3D view at half the window's size with the " +
            "game's Anti-Aliasing off; shadows, clouds, smoke and effects are drawn the simple way, without " +
            "cockpit ray tracing, and planets and grass get less detailed textures, for less memory. Softer and " +
            "plainer, with rougher edges, slower crew portraits and room for fewer effects at once. With Vsync the frame rate " +
            "holds steady at half the display's refresh rate: 60 FPS on a 120 Hz display, 30 on a 60 Hz one. Time warp slows down " +
            "less: lighter physics for tumbling pieces, debris that settles and stays where it lies, and cloud shadows " +
            "that fall out of place during very high warp.",
            mac => Game(mac, antiAliasing: "Disabled", filter: "Pcf", cascades: 2048, terrain: 2048,
                milkyWay: "High", clouds: "Low", cloudShadows: "Medium", effects: "Low", godrays: false,
                clutterShadows: false, plumeShadows: false, rayTracing: false, planetTextures: 2048),
            0),
        new("Balanced", "Faster, with little visible change.",
            "Faster, with little visible change: MetalFX Temporal draws the 3D view at three quarters of the window's size, " +
            "with simpler shadow filtering, clouds and smoke a step lower, and lighter ray tracing in the cockpit. " +
            "Time warp slows down less: lighter physics for tumbling pieces, debris that settles and stays " +
            "where it lies, and cloud shadows that fall out of place during very high warp.",
            mac => Game(mac, antiAliasing: "MultisampleX2", filter: "Pcf", cascades: 2048, terrain: 4096,
                milkyWay: "High", clouds: "Medium", cloudShadows: "Medium", effects: "Medium", godrays: true, clutterShadows: true,
                plumeShadows: true, rayTracing: true, exhausts: "High"),
            1),
        new("Quality", "Window size. Trims what barely shows.",
            "The game's own image at the window's full size, with its anti-aliasing, soft shadows and ray tracing in " +
            "the cockpit. Only what costs much and barely shows is lower or baked: shadow map " +
            "sizes, soft shadows near the camera, cockpit ray tracing, the Milky Way.",
            mac => Game(mac, antiAliasing: "MultisampleX2", filter: "Pcss", cascades: 2048, terrain: 4096,
                milkyWay: "High", clouds: "High", cloudShadows: "High", effects: "High", godrays: true, clutterShadows: true,
                plumeShadows: true, rayTracing: true),
            2),
        new("Game Defaults", "The game's own graphics settings.",
            "The game's own image: its default graphics for every setting the other presets change, and the Image and " +
            "Extra Performance rows at their defaults, without MetalFX. Everything else, such as the Field of View or " +
            "the Optimizations, stays as it is.",
            mac =>
            {
                var defaults = new GameSettings();
                return Defaults(Game(mac, "Disabled", "Pcf", 2048, 2048, "Low", "Low", "Low", "Low", false, false, false, false, 0),
                    name => Text(Field(name).GetValue(Owner(Field(name), defaults))));
            },
            -1),
    ];

    // The game's defaults are the same on every Mac, but for Auto Memory Saving's
    public static Preset GameDefaults => All[^1];

    // The port setting that keeps the card picked last, and the preset a value of it names
    public const string Key = "preset";
    public static Preset Picked(string value) => All.FirstOrDefault(p => p.Id == value) ?? GameDefaults;

    // The game's settings another preset sets, at their defaults: a new GameSettings' (the game's Reset)
    private static Dictionary<string, string> Defaults(Dictionary<string, string> settings, Func<string, string> value) =>
        settings.Keys.ToDictionary(key => key, value);

    // The game's settings, by field name: GameSettings' own (anti-aliasing: the game's, which MetalFX Temporal turns
    // off), the rest in GameSettings.Graphics
    private static Dictionary<string, string> Game(Mac mac, string antiAliasing, string filter, int cascades, int terrain,
        string milkyWay, string clouds, string cloudShadows, string effects, bool godrays, bool clutterShadows,
        bool plumeShadows, bool rayTracing, int planetTextures = 0, string? exhausts = null)
    {
        // Exhausts and particles at High cost about nothing under MetalFX Temporal 0.75 (High vs Low: exhausts ~0 ms,
        // particles under 1, M5, Mixed Test in a 1440x900 window), and the exhaust's blocks are visibly smaller in motion;
        // explosions at High cost up to ~4 ms (vs Low) while one is on screen, the heaviest moments: Balanced keeps those
        // at Medium
        exhausts ??= effects;
        // With the Milky Way bake (Performance, Balanced and Quality), Milky Way High costs what Low and Medium do (M5,
        // fast run: every view within 0.1 ms). Cloud Shadows Low costs what Medium does, so no preset goes below Medium.
        //
        // Ambient occlusion, the ocean and fur cost little (0-3 %): lower in Performance only
        string small = effects == "Low" ? "Low" : "High";
        var game = new Dictionary<string, string>
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
            // sparkles on chrome a little blockier. Performance traces no rays: set there too, for the rule above
            ["RaytracedOpaqueReflectionResolutionScale"] = "50",
            ["RaytracedShadowResolutionScale"] = "50",
            ["RaytracedReflectionBounces"] = "1",
        };
        // The game's terrain textures at most this many pixels wide (0: the game's, none), in every preset, so that
        // each one sets it: the planets' textures loaded whole (Earth's clouds, tiling and biome maps, the moons'
        // rock), the finest level the streamer gives the planets' colour maps (Earth, the Moon, Mars; not their
        // heights and normals), ground clutter and rock particles. Performance's 2048 saves about 0.3 GB of graphics
        // memory in the solar system (M5, Mixed Test views, at an 8 GB Mac's texture budget). With 8 GB or less, Auto
        // Memory Saving limits it in every preset, and the vessels' textures and the shadow maps too (Limit)
        game["TerrainTextureCap"] = Text(planetTextures);
        return game;
    }

    // Auto Memory Saving's limits with 8 GB or less on a preset's game settings: those it gives, and vessel textures
    private static void Limit(Dictionary<string, string> game)
    {
        foreach (var (field, _, atMost) in AutoMemorySaving.SmallGame)
            game[field] = Text(AutoMemorySaving.Limit(game.TryGetValue(field, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : 0, atMost));
    }

    // The port settings a preset gives, from the table's presets column (Performance, Balanced, Quality; -1: the
    // defaults). Every Extra Performance row is on in Performance, and in Balanced where its change is small; Quality
    // keeps the game's own image but for what costs much and barely shows: the Milky Way bake (sky pixels at most 1 of
    // 255 apart, the game's Milky Way High at no cost) and the simpler PCSS search. The tumbling physics, the debris
    // settling and the warp cloud shadows change warp, not the image, and are on in Balanced too: warp is too much of
    // the game to leave to Performance. The slower crew portraits are on in Performance only
    private static Dictionary<string, string> Port(int index) =>
        PortSettings.All.Where(s => s.Presets.Length > 0).ToDictionary(s => s.Key, s => index < 0 ? s.Default : s.Presets[index]);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(bool value) => value ? "true" : "false";
    private static string Text(object? value) => value is bool b ? Text(b) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    public static Mac ThisMac { get; private set; } = new("", AutoMemorySaving.MemoryGb());
    private static readonly Dictionary<string, FieldInfo> Fields = new();

    public static string Apply(Harmony harmony)
    {
        ThisMac = Detect();
        foreach (var preset in All)
        {
            // Every tier's values are checked on every Mac
            foreach (int gb in new[] { 8, 16, 32 })
            {
                preset.For(ThisMac with { MemoryGb = gb }, true);
                Check(preset);
            }
            preset.For(ThisMac, false);
            Check(preset);
        }
        if (!Settings.Get(Key).Choices.Select(c => c.Value).ToHashSet().SetEquals(All.Select(p => p.Id)))
            throw new InvalidOperationException($"{Key}'s values aren't the presets'");
        Refresh(Settings.Get(AutoMemorySaving.Key).Saved == "true");
        foreach (var (field, _, _) in AutoMemorySaving.SmallGame)
            Field(field);
        harmony.Patch(Find.Method(typeof(GameSettings), nameof(GameSettings.LoadFromFile)),
            postfix: new HarmonyMethod(typeof(Presets), nameof(FirstStart)));
        return $"on, {ThisMac.Chip}, {ThisMac.MemoryGb} GB, memory {AutoMemorySaving.Describe(ThisMac.Tier)}";
    }

    // The presets' values with Auto Memory Saving on or not
    public static void Refresh(bool memorySaving)
    {
        foreach (var preset in All)
            preset.For(ThisMac, memorySaving);
    }

    // A fresh install's first start (the loader made port-settings.toml, KSA_PORT_FRESH), Auto Memory Saving on, 8 GB
    // or less: the game's settings, just loaded, take the limits before anything is drawn, and are saved with them
    private static void FirstStart()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("KSA_PORT_FRESH") != "1" || Settings.Get(AutoMemorySaving.Key).Saved != "true" ||
                ThisMac.Tier != AutoMemorySaving.Tier.Small)
                return;
            UseMemory(GameSettings.Current);
            GameSettings.Current.Save();
            Hooks.Log($"{nameof(Presets)}: first start, textures and shadow maps at most 2048 pixels wide, streaming at most 512 MB, for {ThisMac.MemoryGb} GB");
        }
        catch (Exception e)
        {
            Hooks.Log($"{nameof(Presets)}: first start, {Hooks.Describe(e)}");
        }
    }

    // Auto Memory Saving's limits for this Mac on these game settings (8 GB or less); the Memory rows' values when on
    public static void UseMemory(GameSettings game)
    {
        if (ThisMac.Tier != AutoMemorySaving.Tier.Small)
            return;
        foreach (var (name, _, atMost) in AutoMemorySaving.SmallGame)
        {
            var field = Fields[name];
            int value = Convert.ToInt32(field.GetValue(Owner(field, game)), CultureInfo.InvariantCulture);
            field.SetValue(Owner(field, game), Parse(field, Text(AutoMemorySaving.Limit(value, atMost))));
        }
    }

    // The game's own settings, as its Reset makes them: read, never changed
    private static GameSettings? _gameDefaults;
    private static GameSettings GameDefaultSettings => _gameDefaults ??= new GameSettings();

    // Auto Memory Saving off, 8 GB or less: the game's own values for the limits its tabs can't change
    public static void UseGameMemory(GameSettings game)
    {
        if (ThisMac.Tier != AutoMemorySaving.Tier.Small)
            return;
        foreach (var name in AutoMemorySaving.OffRestores)
        {
            var field = Field(name);
            field.SetValue(Owner(field, game), field.GetValue(Owner(field, GameDefaultSettings)));
        }
    }

    // How many of Auto Memory Saving's values for this Mac have another value here: its rows, and with 8 GB or less
    // the game's limits
    public static int MemoryDifferences(GameSettings game, Func<string, string> port)
    {
        int count = AutoMemorySaving.Port(ThisMac.Tier).Where(kv => Settable(kv.Key)).Count(kv => port(kv.Key) != kv.Value);
        if (ThisMac.Tier != AutoMemorySaving.Tier.Small)
            return count;
        return count + AutoMemorySaving.SmallGame.Count(g =>
            Convert.ToInt32(Fields[g.Field].GetValue(Owner(Fields[g.Field], game)), CultureInfo.InvariantCulture) is var v &&
            AutoMemorySaving.Limit(v, g.AtMost) != v);
    }

    private static void Check(Preset preset)
    {
        foreach (var (name, value) in preset.Game)
        {
            var field = Field(name);
            if (Parse(field, value) == null)
                throw new Skip($"{field.DeclaringType!.Name}.{name} has no value {value}");
        }
        foreach (var (key, value) in preset.Port)
            if (!Settings.Get(key).Valid(value))
                throw new InvalidOperationException($"{key} has no value {value}");
    }

    private static FieldInfo Field(string name)
    {
        if (!Fields.TryGetValue(name, out var field))
            Fields[name] = field = AccessTools.Field(typeof(GameSettings.GraphicsSettings), name) ?? Find.Field(typeof(GameSettings), name);
        return field;
    }

    // What a preset's card shows, from its values for this Mac. Shadows are one word for the filter and the terrain's
    // shadow map, which go up in steps together: the simple filter at 2048 or 4096, the soft one up to 4096 or at 8192.
    // MSAA is what is drawn: with MetalFX Temporal and Skip Game Anti-Aliasing, the game's setting is turned off
    public static (string Label, string Value)[] Summary(Preset preset)
    {
        var (game, port) = (preset.Game, preset.Port);
        var upscaling = port["upscaling"];
        var scale = port.TryGetValue("render_scale", out var value) ? double.Parse(value, CultureInfo.InvariantCulture) : 1.0;
        bool soft = game["CascadedShadowMapFilter"] == "Pcss";
        int terrain = int.Parse(game["TerrainShadowMapSize"], CultureInfo.InvariantCulture);
        return
        [
            ("Render Scale", $"{Math.Round(scale * 100)} %"),
            ("MetalFX", Settings.Get("upscaling").LabelOf(upscaling)),
            ("MSAA", upscaling == "metalfx" && port["skip_game_anti_aliasing"] == "true" ? "Off" : game["AntiAliasingMethod"] switch
            {
                "MultisampleX2" => "x2",
                "MultisampleX4" => "x4",
                "MultisampleX8" => "x8",
                _ => "Off",
            }),
            ("Shadows", soft ? terrain >= 8192 ? "Very High" : "High" : terrain >= 4096 ? "Medium" : "Low"),
            ("Clouds", game["CloudUpscaling"]),
            ("Effects", game["ExplosionQuality"]),
            ("Ray Trace", game["IVARayTracing"] != "true" ? "Off" : game["RaytracedOpaqueReflectionResolutionScale"] == "100" ? "On" : "Lighter"),
        ];
    }

    // How many of a preset's settings have another value here
    public static int Differences(Preset preset, GameSettings game, Func<string, string> port) =>
        preset.Game.Count(kv => !Equals(Fields[kv.Key].GetValue(Owner(Fields[kv.Key], game)), Parse(Fields[kv.Key], kv.Value))) +
        preset.Port.Where(kv => Settable(kv.Key)).Count(kv => !Same(Settings.Get(kv.Key), port(kv.Key), kv.Value)) +
        (OtherSize(game) ? 1 : 0);

    // A port setting the tab can change: not set outside, none of its hooks skipped or failed
    public static bool Settable(string key) =>
        Settings.Get(key) is var s && s.Outside == null && !s.Hooks.Any(h => Hooks.Status.GetValueOrDefault(h) is { Unavailable: true });

    // A preset's card: its values become the pending changes of the game's copy of its settings and of the tab
    public static void Use(Preset preset, GameSettings pending, IDictionary<string, string> port)
    {
        foreach (var (name, value) in preset.Game)
            Fields[name].SetValue(Owner(Fields[name], pending), Parse(Fields[name], value));
        foreach (var (key, value) in preset.Port.Where(kv => Settable(kv.Key)))
            port[key] = value;
        if (FullscreenSize(pending) is { Width: > 0 } size)
        {
            pending.ScreenWidth = size.Width;
            pending.ScreenHeight = size.Height;
        }
    }

    // Whether these settings are the preset's
    public static bool Matches(Preset preset, GameSettings game, Func<string, string> port) =>
        preset.Game.All(kv => Equals(Fields[kv.Key].GetValue(Owner(Fields[kv.Key], game)), Parse(Fields[kv.Key], kv.Value))) &&
        preset.Port.Where(kv => Settable(kv.Key)).All(kv => Same(Settings.Get(kv.Key), port(kv.Key), kv.Value)) &&
        !OtherSize(game);

    // In Fullscreen, a Resolution other than the desktop's size: 0 x 0 is the window's, which the game takes (Reset's)
    private static bool OtherSize(GameSettings game) =>
        FullscreenSize(game) is { Width: > 0 } size && (game.ScreenWidth, game.ScreenHeight) != (0, 0) &&
        (game.ScreenWidth != size.Width || game.ScreenHeight != size.Height);

    private static bool _displayFailed;

    // In Fullscreen, the size the screen's desktop has; 0 x 0 otherwise, or when macOS doesn't say
    private static (int Width, int Height) FullscreenSize(GameSettings game)
    {
        if (_displayFailed || game.ScreenDisplayMode != GameSettings.DisplayMode.Fullscreen)
            return default;
        try
        {
            return Displays.Desktop(GameSettings.GetMonitor(game.ScreenIndex, game.ScreenName)) is { } desktop
                ? (desktop.Width, desktop.Height)
                : default;
        }
        catch (Exception e)
        {
            _displayFailed = true;
            Hooks.Fail(nameof(Presets), e);
            return default;
        }
    }

    // A scale's values are numbers ("1" and "1.0" are the same)
    private static bool Same(PortSetting s, string a, string b) =>
        a == b || s.Kind == "scale" && double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
        double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && Math.Abs(x - y) < 1e-6;

    private static object Owner(FieldInfo field, GameSettings game) =>
        field.DeclaringType == typeof(GameSettings) ? game : game.Graphics;

    private static object? Parse(FieldInfo field, string value)
    {
        var type = field.FieldType;
        if (type.IsEnum)
            return Enum.TryParse(type, value, false, out var e) && Enum.IsDefined(type, e!) ? e : null;
        if (type == typeof(bool))
            return bool.TryParse(value, out var b) ? b : null;
        if (type == typeof(ushort))
            return ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : null;
        if (type == typeof(int))
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;
        return null;
    }

    // The chip's name ("Apple M2 Pro", for the log) and the memory (KSA_PORT_MEMORY_GB stands in for it)
    private static Mac Detect() => new(SysctlText("machdep.cpu.brand_string"), AutoMemorySaving.MemoryGb());

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, [Out] byte[]? value, ref nint size, nint newValue, nint newSize);

    private static string SysctlText(string name)
    {
        nint size = 0;
        if (SysctlByName(name, null, ref size, 0, 0) != 0 || size <= 0)
            return "";
        var bytes = new byte[size];
        return SysctlByName(name, bytes, ref size, 0, 0) == 0 ? Encoding.UTF8.GetString(bytes, 0, (int)size).TrimEnd('\0') : "";
    }
}
