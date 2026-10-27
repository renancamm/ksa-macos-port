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
// port settings, made for this Mac's memory and chip. USE in the tab makes them pending changes, in the game's tabs
// and in this one, as if the player had set each row; the game's Apply keeps them, Discard drops them. Nothing else
// ever changes a setting.
//
// Every preset gives every setting any preset sets, so a preset after another leaves nothing of the first, except
// the port rows the tab can't change (set outside, their hook unavailable) and those that do nothing with the
// preset's other values (the render scale without upscaling): those keep their values. The game's fields are found
// by name when the game starts: a field that moved leaves the presets out (skipped).
internal static class Presets
{
    public sealed class Preset(string name, string help, Func<Mac, Dictionary<string, string>> game,
        Func<Mac, Dictionary<string, string>> port)
    {
        public string Name => name;
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
        new("Performance",
            "The fastest the game gets on this Mac: MetalFX draws the 3D view at half the window's size, and shadows, " +
            "clouds, smoke, the sky and effects are drawn the simple way, without ray tracing in the cockpit. The " +
            "image is softer and plainer.",
            mac => Game(mac, filter: "Pcf", cascades: 2048, terrain: 2048, milkyWay: "Low", clouds: "Low", effects: "Low",
                godrays: false, clutterShadows: false, plumeShadows: false, rayTracing: false),
            mac => Port(upscaling: "metalfx", scale: "0.5", tuning: true, plumeShadows: false)),
        new("Balanced",
            "Fast, and little of it shows: MetalFX draws the 3D view at three quarters of the window's size, with " +
            "simpler shadow filtering, clouds and smoke a step lower, and ray tracing in the cockpit where the Mac has it.",
            mac => Game(mac, filter: "Pcf", cascades: 2048, terrain: 4096, milkyWay: "Medium", clouds: "Medium",
                effects: "Medium", godrays: true, clutterShadows: true, plumeShadows: true, rayTracing: mac.RayTracing),
            mac => Port(upscaling: "metalfx", scale: "0.75", tuning: true, plumeShadows: true)),
        new("Quality",
            "The game's own image at the window's full size, with its anti-aliasing, soft shadows and ray tracing in " +
            "the cockpit where the Mac has it. Only what costs much and barely shows is set lower: the shadow maps' " +
            "sizes, the Milky Way, and the soft shadows close to the camera.",
            mac => Game(mac, filter: "Pcss", cascades: 2048, terrain: 4096, milkyWay: "Medium", clouds: "High",
                effects: "High", godrays: true, clutterShadows: true, plumeShadows: true, rayTracing: mac.RayTracing),
            mac => Port(upscaling: "off", scale: "1.0", tuning: false, plumeShadows: true)),
    ];

    // The game's settings, by field name: GameSettings' own (anti-aliasing: the game's, which MetalFX turns off), the
    // rest in GameSettings.Graphics
    private static Dictionary<string, string> Game(Mac mac, string filter, int cascades, int terrain, string milkyWay,
        string clouds, string effects, bool godrays, bool clutterShadows, bool plumeShadows, bool rayTracing)
    {
        // Ambient occlusion, the ocean and fur cost little (0-3 %): lower in Performance only
        string small = effects == "Low" ? "Low" : "High";
        var game = new Dictionary<string, string>
        {
            ["AntiAliasingMethod"] = "MultisampleX2",
            ["CascadedShadowMapFilter"] = filter,
            ["CascadedShadowMapSize"] = Text(cascades),
            ["TerrainShadowMapSize"] = Text(mac.SmallMemory ? Math.Min(terrain, 2048) : terrain),
            ["MilkyWayQuality"] = milkyWay,
            ["CloudUpscaling"] = clouds,
            ["CloudShadows"] = clouds,
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
        };
        // 8 GB: the textures at most 2048 pixels wide too
        if (mac.SmallMemory)
            game["TerrainTextureCap"] = game["VesselTextureCap"] = "2048";
        return game;
    }

    private static Dictionary<string, string> Port(string upscaling, string scale, bool tuning, bool plumeShadows) => new()
    {
        ["upscaling"] = upscaling,
        ["render_scale"] = scale,
        ["override_antialiasing"] = "true",
        ["cloud_shadow_tuning"] = tuning ? "slower_refresh" : "off",
        ["plume_shadow_tuning"] = tuning && plumeShadows ? "low" : "off",
        ["shadow_search"] = "true",
    };

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Text(bool value) => value ? "true" : "false";

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
            if (!Fields.TryGetValue(name, out var field))
                Fields[name] = field = AccessTools.Field(typeof(GameSettings.GraphicsSettings), name) ?? Find.Field(typeof(GameSettings), name);
            if (Parse(field, value) == null)
                throw new Skip($"{field.DeclaringType!.Name}.{name} has no value {value}");
        }
        foreach (var (key, value) in preset.Port)
            if (!Settings.Get(key).Valid(value))
                throw new InvalidOperationException($"{key} has no value {value}");
    }

    // A port setting the tab can change: not set outside, its hook not skipped or failed
    private static bool Settable(string key) =>
        Settings.Get(key) is var s && s.Outside == null && Hooks.Status.GetValueOrDefault(s.Hook) is not { Unavailable: true };

    // USE: the preset's values become the pending changes of the game's copy of its settings and of the tab
    public static void Use(Preset preset, GameSettings pending, IDictionary<string, string> port)
    {
        foreach (var (name, value) in preset.Game)
            Fields[name].SetValue(Owner(Fields[name], pending), Parse(Fields[name], value));
        foreach (var (key, value) in preset.Port.Where(kv => Settable(kv.Key)))
            port[key] = value;
    }

    // Whether these settings are the preset's
    public static bool Matches(Preset preset, GameSettings game, Func<string, string> port) =>
        preset.Game.All(kv => Equals(Fields[kv.Key].GetValue(Owner(Fields[kv.Key], game)), Parse(Fields[kv.Key], kv.Value))) &&
        preset.Port.Where(kv => Settable(kv.Key)).All(kv => Same(Settings.Get(kv.Key), port(kv.Key), kv.Value));

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
