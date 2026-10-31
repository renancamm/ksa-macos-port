using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace KsaMetalHooks;

// One port setting: a line of the table (Contents/Resources/port-settings.txt, whose header explains the columns), with
// the value port-settings.toml holds and the one the game runs with
public sealed class PortSetting
{
    public string Section = "", Key = "", Type = "", Default = "", Variable = "", OnValue = "1", Hook = "", Label = "", Help = "";
    public string[] Needs = [];
    public bool Live;
    // A choice's values and their labels (bool: on and off), and what some of them mean
    public (string Value, string Label)[] Choices = [];
    public readonly Dictionary<string, string> OptionHelp = new();
    public double Min, Max;
    // In the file; what the game started with, from the environment the loader set; the variable set outside that
    // decides it instead (name=value)
    public string Saved = "", Running = "";
    public string? Outside;

    public string Kind => Type.Split(':')[0];

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
                // 0 is off, anything else on, but for a variable whose on is a value of its own (KSA_MTL_SYNC=strict)
                return env == "0" ? "false" : OnValue == "1" || env == OnValue ? "true" : "false";
            case "text":
                return env;
            default:
                valid = Valid(env);
                return valid ? env : Default;
        }
    }

    // The value as port-settings.toml writes it: a string in quotes, with \ and " escaped
    public string Toml(string value) =>
        Kind is "choice" or "text" or "numbers" ? $"\"{value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"" : value;
}

// The table and the file, as both the loader (which sets the environment from them when the game starts) and the Mac
// Port tab read them. This file is compiled into both.
public static partial class PortSettings
{
    public static readonly List<PortSetting> All = new();
    // The sections, in the table's order: key, title and the line under it
    public static readonly List<(string Key, string Title, string Caption)> Sections = new();
    public static string FilePath = "", TemplatePath = "";

    public static PortSetting? Get(string key) => All.FirstOrDefault(s => s.Key == key);

    // The game's documents folder, where port-settings.toml is: STARMAP_INSTANCE_PATH moves it (StarMap, the
    // benchmark's runs)
    public static string DocumentsFolder() =>
        Environment.GetEnvironmentVariable("STARMAP_INSTANCE_PATH") is { Length: > 0 } instance
            ? instance
            : Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Documents", "My Games", "Kitten Space Agency");

    // Reads the table in the app's Contents/Resources. Throws when it is missing or a line isn't a setting.
    public static void LoadTable(string resources)
    {
        All.Clear();
        Sections.Clear();
        var table = Path.Combine(resources, "port-settings.txt");
        TemplatePath = Path.Combine(resources, "port-settings.toml");
        string section = "";
        var lines = File.ReadAllLines(table);
        for (int n = 0; n < lines.Length; n++)
        {
            var line = lines[n].Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                section = line[1..end];
                var title = line[(end + 1)..].Split('|', 2);
                Sections.Add((section, title[0].Trim(), title.Length > 1 ? title[1].Trim() : ""));
                continue;
            }
            var c = line.Split('|').Select(x => x.Trim()).ToArray();
            // An indented line: what one of the values of the setting above means
            if (char.IsWhiteSpace(lines[n][0]))
            {
                if (All.Count == 0 || All[^1].Section != section || c.Length != 2 ||
                    All[^1].Choices.All(o => o.Value != c[0]))
                    throw new FormatException($"{table}, line {n + 1}: not a value of the setting above");
                All[^1].OptionHelp[c[0]] = c[1];
                continue;
            }
            if (c.Length < 9)
                throw new FormatException($"{table}, line {n + 1}: {c.Length} columns, not 9");
            var s = new PortSetting
            {
                Section = section, Key = c[0], Type = c[1], Default = c[2], Variable = c[3].Split('=')[0], Live = c[5] == "live",
                Hook = c[6], Label = c[7], Help = string.Join('|', c[8..]),
                Needs = c[4].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            };
            if (c[3].Contains('='))
                s.OnValue = c[3][(c[3].IndexOf('=') + 1)..];
            var parts = s.Type.Split(':');
            switch (parts[0])
            {
                case "bool":
                    s.Choices = [("true", "On"), ("false", "Off")];
                    break;
                case "choice":
                    s.Choices = parts[1].Split('/').Select(v => (v[..v.IndexOf('=')], v[(v.IndexOf('=') + 1)..])).ToArray();
                    break;
                // numbers: the counts of numbers
                case "scale" or "int" or "number" or "numbers":
                    s.Min = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    s.Max = double.Parse(parts[2], CultureInfo.InvariantCulture);
                    break;
            }
            All.Add(s);
        }
    }

    // The values the file holds, the valid ones; warn hears about the lines left out
    public static Dictionary<string, string> ReadFile(Action<string>? warn = null)
    {
        var values = new Dictionary<string, string>();
        if (!File.Exists(FilePath))
            return values;
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or '[')
                continue;
            int eq = line.IndexOf('=');
            var key = eq > 0 ? line[..eq].Trim() : line;
            var value = eq > 0 ? Unquote(line[(eq + 1)..].Trim()) : "";
            if (eq <= 0 || Get(key) is not { } s)
                warn?.Invoke($"port-settings.toml: unknown setting '{line}': ignored");
            else if (!s.Valid(value))
                warn?.Invoke($"port-settings.toml: {key} can't be '{value}' ({s.Type}): ignored");
            else
                values[key] = value;
        }
        return values;
    }

    // A value as the file holds it: a string in quotes ("..." with \" and \\ inside, or '...'), or the text before a
    // comment
    private static string Unquote(string text)
    {
        if (text.StartsWith('\''))
        {
            int end = text.IndexOf('\'', 1);
            return end > 0 ? text[1..end] : text[1..];
        }
        if (!text.StartsWith('"'))
            return text.Split('#')[0].Trim();
        var value = new StringBuilder();
        for (int i = 1; i < text.Length && text[i] != '"'; i++)
            value.Append(text[i] == '\\' && i + 1 < text.Length ? text[++i] : text[i]);
        return value.ToString();
    }

    // Whether a setting's needs are met, with the values value() gives (a need that names the game is checked by
    // game(), the game's own pending settings)
    public static bool Met(PortSetting s, Func<PortSetting, string> value, Func<string, bool> game)
    {
        foreach (var need in s.Needs)
        {
            if (need.StartsWith("game:", StringComparison.Ordinal))
            {
                if (!game(need[5..]))
                    return false;
                continue;
            }
            int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
            var key = ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need;
            if (Get(key) is not { } other)
                continue;
            // What the other setting is once its own needs are applied
            var v = Met(other, value, game) ? value(other) : other.Default;
            bool ok = ne > 0 ? v != need[(ne + 2)..] : eq > 0 ? v == need[(eq + 1)..] : v is not ("" or "false" or "0");
            if (!ok)
                return false;
        }
        return true;
    }
}
