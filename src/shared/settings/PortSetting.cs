using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KsaPort.Common;

// One port setting: a line of the table (port-settings.txt, whose header explains the columns), with the value
// port-settings.toml holds and the one the game runs with
public sealed class PortSetting
{
    public string Section = "", Key = "", Type = "", Default = "", Label = "", Help = "";
    public string[] Needs = [], By = [];
    // Its values in the Mac Port tab's presets Performance, Balanced and Quality (none: the presets leave it alone)
    public string[] Presets = [];
    // Its values with Auto Memory Saving on, for 8 GB of memory or less, 16 GB or less, and more (none: Auto Memory
    // Saving leaves it alone; an empty one: its preset's there)
    public string[] Memory = [];
    // Applies while the game runs (the table's "live"); shown in the Mac Port tab (its section's)
    public bool Live, InTab;
    // A choice's values and their labels (bool: on and off), and what some of them mean
    public (string Value, string Label)[] Choices = [];
    public readonly Dictionary<string, string> OptionHelp = new();
    public double Min, Max;
    // In the file; what the game started with, from the environment the loader set; the variable set outside that
    // decides it instead (name=value)
    public string Saved = "", Running = "";
    public string? Outside;

    public string Kind => Type.Split(':')[0];

    // The variable it sets: KSA_MTL_ and the key in capitals, Apple's for a setting Metal reads (metal:<variable>),
    // none for the tab's own
    public string Variable =>
        By.FirstOrDefault(b => b.StartsWith("metal:", StringComparison.Ordinal)) is { } metal ? metal[6..]
        : By.Contains("tab") ? "" : "KSA_MTL_" + Key.ToUpperInvariant();

    // The hook that does it (hooks:<hook>, by its class), which the tab shows the setting as unavailable without
    public string Hook => By.FirstOrDefault(b => b.StartsWith("hooks:", StringComparison.Ordinal)) is { } hook ? hook[6..] : "";

    // All of them, for a setting more than one hook does (Unload Far Terrain)
    public IEnumerable<string> Hooks => By.Where(b => b.StartsWith("hooks:", StringComparison.Ordinal)).Select(b => b[6..]);

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // How the tab shows a value: a choice's label, a scale in percent
    public string LabelOf(string value) =>
        Kind == "scale" && double.TryParse(value, NumberStyles.Float, Inv, out var f)
            ? $"{Math.Round(f * 100)}%"
            : Choices.FirstOrDefault(c => c.Value == value).Label ?? value;

    public bool Valid(string value)
    {
        switch (Kind)
        {
            case "text":
                return true;
            case "int":
                return long.TryParse(value, NumberStyles.None, Inv, out var i) && i >= Min && i <= Max;
            case "scale" or "number":
                return double.TryParse(value, NumberStyles.Float, Inv, out var d) && d >= Min && d <= Max;
            case "numbers":
                return value.Length == 0 || Numbers(value) is { } n && n.Length >= Min && n.Length <= Max;
            default:
                return Choices.Any(c => c.Value == value);
        }
    }

    // What the values are, in words: for a message about one that isn't
    public string Values() => Kind switch
    {
        "bool" or "choice" => string.Join(" or ", Choices.Select(c => c.Value)),
        "numbers" => $"{(Min == Max ? $"{Min}" : $"{Min} to {Max}")} numbers, comma separated",
        "text" => "a path or text",
        _ => $"a number from {Min.ToString(Inv)} to {Max.ToString(Inv)}",
    };

    // A numbers value's numbers ("4,128,3000"), or null when one of them isn't a number
    public static double[]? Numbers(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        var numbers = new double[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, Inv, out numbers[i]))
                return null;
        }
        return numbers;
    }

    // What the game runs with when the setting's variable holds env (null or empty: unset, the default); valid is false
    // when env isn't one of the setting's values, which then means the default too
    public string FromEnvironment(string? env, out bool valid)
    {
        valid = true;
        if (string.IsNullOrEmpty(env))
            return Default;
        switch (Kind)
        {
            case "bool":
                // 0 is off and 1 on
                if (env is "0" or "1")
                    return env == "0" ? "false" : "true";
                valid = false;
                return Default;
            case "text":
                return env;
            default:
                valid = Valid(env);
                return valid ? env : Default;
        }
    }

    // The value as the loader sets it in the setting's variable: a switch's 0 or 1, a path from ~ in home
    public string ToEnvironment(string value, string home) => Kind switch
    {
        "bool" => value == "false" ? "0" : "1",
        "text" when value.StartsWith('~') => home + value[1..],
        _ => value,
    };

    // The value as port-settings.toml writes it: a string in quotes, with \ and " escaped
    public string Toml(string value) =>
        Kind is "choice" or "text" or "numbers" ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : value;
}
