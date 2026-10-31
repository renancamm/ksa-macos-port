using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// The Mac Port tab's presets: Performance, Balanced and Quality, each a set of the game's own graphics settings and of
// port settings, made for this Mac's memory and chip, and Game Defaults, the defaults of the same settings. A preset's
// card in the tab makes them pending changes, in the game's tabs and in this one, as if the player had set each row;
// the game's Apply keeps them, Discard drops them. Nothing else ever changes a setting.
//
// Every preset gives every setting any preset sets, so a preset after another leaves nothing of the first, except
// the port rows the tab can't change (set outside, their hook unavailable) and the render scale without upscaling,
// which does nothing then: those keep their values. The game's fields are found
// by name when the game starts: a field that moved leaves the presets out (skipped).
//
// In Fullscreen, every preset also sets the Resolution to the size the screen's desktop has (Displays), so the render
// scale alone decides the 3D view's size and the display keeps its mode. Windowed, the Resolution is the window's size,
// and Borderless doesn't use it: those keep it. The card reads the Mode as it is then: a Mode set to Fullscreen after it
// keeps the Resolution, and unless that is the desktop's size the tab then shows no preset in use. An error reading
// the display makes the presets unavailable, as any (Hooks.Fail), without taking the tab with them.
internal static class Presets
{
    public sealed class Preset(string name, string shortHelp, string help, Func<Mac, Dictionary<string, string>> game,
        Func<Mac, Dictionary<string, string>> port)
    {
        public string Name => name;
        // What its card says, and its tooltip
        public string Short => shortHelp;
        public string Help => help;
        // The values for this Mac, from when the game starts: game fields by name, port settings by key
        public Dictionary<string, string> Game { get; private set; } = new();
        public Dictionary<string, string> Port { get; private set; } = new();

        public void For(Mac mac)
        {
            Game = game(mac);
            Port = port(mac);
            if (Port["upscaling"] == "off")
                Port.Remove("render_scale");
        }
    }

    // What the presets go by: memory, and whether the GPU traces rays in hardware (from the M3 on)
    public sealed record Mac(string Chip, int MemoryGb, bool RayTracing)
    {
        public bool SmallMemory => MemoryGb is > 0 and <= 8;
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
            "cockpit ray tracing. Softer and plainer, with rougher edges, slower crew portraits, and lighter physics for tumbling pieces.",
            mac => Game(mac, antiAliasing: "Disabled", filter: "Pcf", cascades: 2048, terrain: 2048,
                milkyWay: "High", clouds: "Low", cloudShadows: "Medium", effects: "Low", godrays: false,
                clutterShadows: false, plumeShadows: false, rayTracing: false),
            mac => Port(upscaling: "spatial", scale: "0.5", tuning: true, milkyWayBake: true, performanceOnly: true)),
        new("Balanced", "Faster, with little visible change.",
            "Faster, with little visible change: MetalFX Temporal draws the 3D view at three quarters of the window's size, " +
            "with simpler shadow filtering, clouds and smoke a step lower, and lighter ray tracing in the cockpit where " +
            "the Mac has it.",
            mac => Game(mac, antiAliasing: "MultisampleX2", filter: "Pcf", cascades: 2048, terrain: 4096,
                milkyWay: "High", clouds: "Medium", cloudShadows: "Medium", effects: "Medium", godrays: true, clutterShadows: true,
                plumeShadows: true, rayTracing: mac.RayTracing),
            mac => Port(upscaling: "metalfx", scale: "0.75", tuning: true, milkyWayBake: true, performanceOnly: false)),
        new("Quality", "Window size. Trims what barely shows.",
            "The game's own image at the window's full size, with its anti-aliasing, soft shadows and ray tracing in " +
            "the cockpit where the Mac has it. Only what costs much and barely shows is lower or baked: shadow map " +
            "sizes, soft shadows near the camera, cockpit ray tracing, the Milky Way.",
            mac => Game(mac, antiAliasing: "MultisampleX2", filter: "Pcss", cascades: 2048, terrain: 4096,
                milkyWay: "High", clouds: "High", cloudShadows: "High", effects: "High", godrays: true, clutterShadows: true,
                plumeShadows: true, rayTracing: mac.RayTracing),
            mac => Port(upscaling: "off", scale: "1.0", tuning: false, milkyWayBake: true, performanceOnly: false)),
        new("Game Defaults", "The game's own graphics settings.",
            "The game's own image: its default graphics for every setting the other presets change, and the Image and " +
            "Extra Performance rows at their defaults, without MetalFX. Everything else, such as the Field of View or " +
            "the Optimizations, stays as it is.",
            mac =>
            {
                var defaults = new GameSettings();
                return Defaults(Game(mac, "Disabled", "Pcf", 2048, 2048, "Low", "Low", "Low", "Low", false, false, false, false),
                    name => Text(Field(name).GetValue(Owner(Field(name), defaults))));
            },
            mac => Defaults(Port("off", "1.0", false, false, false), key => Settings.Get(key).Default)),
    ];

    // Not made for this Mac: the game's defaults are the same on every Mac
    public static Preset GameDefaults => All[^1];

    // The settings another preset sets, at their defaults: the game's are a new GameSettings' (the game's Reset), the
    // port's the table's
    private static Dictionary<string, string> Defaults(Dictionary<string, string> settings, Func<string, string> value) =>
        settings.Keys.ToDictionary(key => key, value);

    // The game's settings, by field name: GameSettings' own (anti-aliasing: the game's, which MetalFX Temporal turns
    // off), the rest in GameSettings.Graphics
    private static Dictionary<string, string> Game(Mac mac, string antiAliasing, string filter, int cascades, int terrain,
        string milkyWay, string clouds, string cloudShadows, string effects, bool godrays, bool clutterShadows,
        bool plumeShadows, bool rayTracing)
    {
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
            ["TerrainShadowMapSize"] = Text(mac.SmallMemory ? Math.Min(terrain, 2048) : terrain),
            ["MilkyWayQuality"] = milkyWay,
            ["CloudUpscaling"] = clouds,
            ["CloudShadows"] = cloudShadows,
            ["Godrays"] = Text(godrays),
            ["GroundClutterShadowCasting"] = Text(clutterShadows),
            ["PlumeTrailShadows"] = Text(plumeShadows),
            ["ExplosionQuality"] = effects,
            ["ExhaustUpscaling"] = effects,
            ["ParticleQuality"] = effects,
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
        // 8 GB: the textures at most 2048 pixels wide too
        if (mac.SmallMemory)
            game["TerrainTextureCap"] = game["VesselTextureCap"] = "2048";
        return game;
    }

    // The Extra Performance rows (tuning) are on in Performance and Balanced, whose changes to the image are small; the
    // plume shadows' in Performance too, which turns the game's plume shadows off: they are faster when a player turns
    // those on. The Milky Way bake, on in Performance, Balanced and Quality, changes sky pixels by at most 1 of 255 and
    // gives Quality the game's Milky Way High at no cost; otherwise Quality keeps the game's own image. The tumbling
    // physics, which changes the physics and not the image, and the slower crew portraits are on in Performance only
    private static Dictionary<string, string> Port(string upscaling, string scale, bool tuning, bool milkyWayBake, bool performanceOnly) => new()
    {
        ["upscaling"] = upscaling,
        ["render_scale"] = scale,
        ["skip_game_anti_aliasing"] = "true",
        ["fewer_cloud_shadow_updates"] = Text(tuning),
        ["lighter_plume_shadows"] = Text(tuning),
        ["simpler_pcss_shadows"] = "true",
        ["baked_milky_way"] = Text(milkyWayBake),
        ["crew_portraits_at_15_fps"] = Text(performanceOnly),
        ["lighter_tumbling_physics"] = Text(performanceOnly),
    };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(bool value) => value ? "true" : "false";
    private static string Text(object? value) => value is bool b ? Text(b) : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    public static Mac ThisMac { get; private set; } = new("", 0, true);
    private static readonly Dictionary<string, FieldInfo> Fields = new();

    public static string Apply(Harmony harmony)
    {
        ThisMac = Detect();
        foreach (var preset in All)
        {
            // An 8 GB Mac's values are checked on every Mac
            preset.For(ThisMac with { MemoryGb = 8 });
            Check(preset);
            preset.For(ThisMac);
            Check(preset);
        }
        return $"on, {ThisMac.Chip}, {ThisMac.MemoryGb} GB";
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
            ("Effects", game["ExhaustUpscaling"]),
            ("Ray Trace", game["IVARayTracing"] != "true" ? "Off" : game["RaytracedOpaqueReflectionResolutionScale"] == "100" ? "On" : "Lighter"),
        ];
    }

    // How many of a preset's settings have another value here
    public static int Differences(Preset preset, GameSettings game, Func<string, string> port) =>
        preset.Game.Count(kv => !Equals(Fields[kv.Key].GetValue(Owner(Fields[kv.Key], game)), Parse(Fields[kv.Key], kv.Value))) +
        preset.Port.Where(kv => Settable(kv.Key)).Count(kv => !Same(Settings.Get(kv.Key), port(kv.Key), kv.Value)) +
        (FullscreenSize(game) is { Width: > 0 } size && (game.ScreenWidth != size.Width || game.ScreenHeight != size.Height) ? 1 : 0);

    // A port setting the tab can change: not set outside, its hook not skipped or failed
    private static bool Settable(string key) =>
        Settings.Get(key) is var s && s.Outside == null && Hooks.Status.GetValueOrDefault(s.Hook) is not { Unavailable: true };

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
        (FullscreenSize(game) is not { Width: > 0 } size || (game.ScreenWidth == size.Width && game.ScreenHeight == size.Height));

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
            Hooks.Fail("presets", e);
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

    // The chip's name ("Apple M2 Pro"), its memory, and whether it traces rays in hardware: an M1 or M2 doesn't, a
    // chip whose name isn't "Apple M<n>" is taken to
    private static Mac Detect()
    {
        var chip = SysctlText("machdep.cpu.brand_string");
        var m = Regex.Match(chip, @"^Apple M(\d+)");
        long memory = SysctlLong("hw.memsize");
        return new Mac(chip, (int)Math.Round(memory / 1073741824.0), !m.Success || int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 3);
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, ref long value, ref nint size, nint newValue, nint newSize);

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, [Out] byte[]? value, ref nint size, nint newValue, nint newSize);

    private static long SysctlLong(string name)
    {
        long value = 0;
        nint size = sizeof(long);
        return SysctlByName(name, ref value, ref size, 0, 0) == 0 ? value : 0;
    }

    private static string SysctlText(string name)
    {
        nint size = 0;
        if (SysctlByName(name, null, ref size, 0, 0) != 0 || size <= 0)
            return "";
        var bytes = new byte[size];
        return SysctlByName(name, bytes, ref size, 0, 0) == 0 ? Encoding.UTF8.GetString(bytes, 0, (int)size).TrimEnd('\0') : "";
    }
}
