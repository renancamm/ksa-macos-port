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

// The Mac Port tab's presets: Performance, Balanced and Quality, and Game Defaults, whose values are the shared
// library's (TabPresets, which benchmark.sh's --tab-preset reads too): here they become the game's fields, found by
// name, and the cards. Auto Memory Saving (AutoMemorySaving, in the shared library) is the second preset beside them:
// the Memory rows are its own, which these leave alone, but on a Mac with 8 GB of memory or less and Auto Memory Saving
// on, every preset's textures and shadow maps are at most 2048 pixels wide and its texture streaming budget at most
// 512 MB. A preset's card in the tab makes its values pending changes, in the game's tabs and in this one, as if the
// player had set each row; the game's Apply keeps them, Discard drops them. Nothing else changes a setting, but a fresh
// install's first start, where Auto Memory Saving, on by default, sets its values (the loader the port settings,
// FirstStart here the game's). The card picked last is kept in the file (preset): the tab counts the settings that
// differ from it, unless they are another preset's. With Auto Memory Saving on, its limits sit on top of every card's.
//
// Every preset gives every setting any preset sets, so a preset after another leaves nothing of the first, except
// the port rows the tab can't change (set outside, their hook unavailable) and the render scale without upscaling,
// which does nothing then: those keep their values. The game's fields are found by name when the game starts: a field
// that moved leaves the presets out (skipped). Each one's key in settings.toml is checked against the shared library's
// too, for benchmark.sh, and a key that differs is said in the log.
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
    // A preset of the shared library's, with its values for this Mac: game fields by name, port settings by key
    public sealed class Preset(TabPresets.Preset data)
    {
        public string Name => data.Name;
        // Its value of the port setting that keeps the card picked last (Game Defaults: game_defaults)
        public string Id => data.Id;
        // What its card says, and its tooltip
        public string Short => data.Short;
        public string Help => data.Help;
        // The values for this Mac, from when the game starts
        public Dictionary<string, string> Game { get; private set; } = new();
        public Dictionary<string, string> Port { get; private set; } = new();

        public void For(Mac mac, bool memorySaving)
        {
            bool small = memorySaving && mac.Tier == AutoMemorySaving.Tier.Small;
            Game = TabPresets.GameValues(data, small) ?? GameDefaultValues(small);
            Port = TabPresets.Port(data);
        }
    }

    // What Auto Memory Saving goes by: this Mac's memory
    public sealed record Mac(string Chip, int MemoryGb)
    {
        public AutoMemorySaving.Tier Tier => AutoMemorySaving.TierOf(MemoryGb);
    }

    public static readonly Preset[] All = TabPresets.All.Select(p => new Preset(p)).ToArray();

    // The game's defaults are the same on every Mac, but for Auto Memory Saving's
    public static Preset GameDefaults => All[^1];

    // The port setting that keeps the card picked last, and the preset a value of it names
    public const string Key = TabPresets.Key;
    public static Preset Picked(string value) => All.FirstOrDefault(p => p.Id == value) ?? GameDefaults;

    // Game Defaults' game settings: those the other presets give, at the values of a new GameSettings (the game's
    // Reset), with Auto Memory Saving's limits when small
    private static Dictionary<string, string> GameDefaultValues(bool small)
    {
        var game = TabPresets.Keys.Keys.ToDictionary(name => name, name => Text(Field(name).GetValue(Owner(Field(name), GameDefaultSettings))));
        if (small)
            TabPresets.Limit(game);
        return game;
    }

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
            // The key matters to benchmark.sh's --tab-preset alone: the presets here set the field, so they stay on
            if (TomlKey(field) is { } key && key != TabPresets.KeyOf(name))
                Hooks.Log($"{nameof(Presets)}: {field.DeclaringType!.Name}.{name} is {key} in settings.toml, not {TabPresets.KeyOf(name)} (TabPresets)");
        }
        foreach (var (key, value) in preset.Port)
            if (!Settings.Get(key).Valid(value))
                throw new InvalidOperationException($"{key} has no value {value}");
    }

    // A field's key in settings.toml, from the game's TomlField attribute (in [graphics] for GameSettings.Graphics'
    // fields); null when it has none
    private static string? TomlKey(FieldInfo field) =>
        field.CustomAttributes.FirstOrDefault(a => a.AttributeType.Name.StartsWith("TomlField", StringComparison.Ordinal))?
            .ConstructorArguments.FirstOrDefault().Value is string key
            ? (field.DeclaringType == typeof(GameSettings.GraphicsSettings) ? "graphics." : "") + key
            : null;

    private static FieldInfo Field(string name)
    {
        if (!Fields.TryGetValue(name, out var field))
            Fields[name] = field = AccessTools.Field(typeof(GameSettings.GraphicsSettings), name) ?? Find.Field(typeof(GameSettings), name);
        return field;
    }

    // What a preset's card shows, from its values for this Mac. Shadows are one word for the filter and the terrain's
    // shadow map, which go up in steps together: the simple filter at 2048 or 4096, the soft one up to 4096 or at 8192.
    // Dynamic Res is on where the preset gives it what it needs, MetalFX and a steady rate (the game's Vsync aside,
    // which the presets leave alone). No MSAA line: it only follows MetalFX (off with it, the game's x2 without)
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
            ("Dynamic Res", port["dynamic_resolution"] == "true" && upscaling != "off" && port["steady_frame_rate"] != "off" ? "On" : "Off"),
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
