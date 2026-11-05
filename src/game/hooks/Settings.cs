using System.Collections.Generic;
using System.Globalization;
using KsaPort.Common;

namespace KsaMetal.Hooks;

// The hooks' settings: what the game runs with. The loader sets each setting port-settings.toml changes as an
// environment variable, and a variable set before the game starts wins over the file; here the shared library
// (src/shared/settings/) reads each variable back against the table: unset, holding none of the setting's values (the
// log says so), or with its needs not met, it means the default. Each setting the hooks do is a property, typed as the
// table says (Settings.Generated.cs, made from the table): Settings.TerrainCache, Settings.RenderScale. A live setting
// (the table's "live") changes while the game runs: the Mac Port tab sets its value on Apply.
public static partial class Settings
{
    private static readonly Dictionary<string, PortSetting> ByKey = new();

    // Reads the table, the file and the environment. Throws when the table is missing or wrong, or lacks a setting
    // the hooks read.
    public static void Load()
    {
        // KsaMetal.Hooks.Hooks: Hooks here is the hooks setting
        PortSettings.Load(KsaMetal.Hooks.Hooks.Resources, warning => KsaMetal.Hooks.Hooks.Log(warning));
        ByKey.Clear();
        foreach (var s in PortSettings.All)
            ByKey[s.Key] = s;
        Bind();
    }

    public static PortSetting Get(string key) =>
        ByKey.TryGetValue(key, out var s) ? s : throw new KeyNotFoundException($"the port settings' table has no {key}");

    // A setting's value as the game runs with it, by its type
    private static bool On(PortSetting s) => s.Running == "true";
    private static string Text(PortSetting s) => s.Running;
    private static int Int(PortSetting s) => int.Parse(s.Running, CultureInfo.InvariantCulture);
    private static double Number(PortSetting s) => double.Parse(s.Running, CultureInfo.InvariantCulture);
    // A numbers setting's numbers (none while it is unset)
    private static double[] Numbers(PortSetting s) => s.Running is { Length: > 0 } v ? PortSetting.Numbers(v) ?? [] : [];
}
