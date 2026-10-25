using System;
using System.Collections.Generic;
using System.Globalization;

namespace KsaMetalHooks;

// The hooks' settings: what the game runs with. The loader sets each setting port-settings.toml changes as an
// environment variable, and a variable set before the game starts wins over the file; here each setting's variable is
// read against the table of the port settings (Contents/Resources/port-settings.txt, which says which variable, which
// values and which default): unset, or holding none of the setting's values (the log says so), it means the default.
// A live setting (the table's "live") changes while the game runs: the Mac Port tab sets its value on Apply.
public static class Settings
{
    private static readonly Dictionary<string, PortSetting> ByKey = new();
    // Read every frame
    private static PortSetting _crewPortraits = null!, _hudLayout = null!;

    // Settings that apply when the game starts, read once
    public static string Upscaler { get; private set; } = "off";
    // The 3D view's size, as a fraction of the window's size in points: with an upscaler only
    public static float RenderScale { get; private set; } = 1f;

    // Reads the table, the file and the environment. Throws when the table is missing or wrong, or names none of the
    // settings read here.
    public static void Load()
    {
        PortSettings.Load(Hooks.Resources, warning => Hooks.Log(warning));
        ByKey.Clear();
        foreach (var s in PortSettings.All)
            ByKey[s.Key] = s;
        _crewPortraits = Get("crew_portraits");
        _hudLayout = Get("hud_layout");
        Upscaler = Value("upscaling");
        RenderScale = Upscaler is "metalfx" or "spatial" ? (float)Number("render_scale") : 1f;
    }

    public static PortSetting Get(string key) =>
        ByKey.TryGetValue(key, out var s) ? s : throw new KeyNotFoundException($"the port settings' table has no {key}");

    // A setting's value as the game runs with it
    public static string Value(string key) => Get(key).Running;

    public static bool On(string key) => Value(key) == "true";

    public static double Number(string key) => double.Parse(Value(key), CultureInfo.InvariantCulture);

    // A numbers setting's numbers (none while it is unset)
    public static double[] Numbers(string key) => Value(key) is { Length: > 0 } v ? PortSetting.Numbers(v) ?? [] : [];

    // always, shown or shown_15: when the crew panel's faces are drawn
    public static string CrewPortraits => _crewPortraits.Running;

    // fit or game: the flight HUD fitted to the display, or the game's
    public static string HudLayout => _hudLayout.Running;
}
