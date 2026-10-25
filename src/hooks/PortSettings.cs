using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaMetalHooks;

// One port setting: a line of the table (Contents/Resources/port-settings.txt, whose header explains the columns), with
// the value port-settings.toml holds and the one the game runs with
public sealed class PortSetting
{
    public string Section = "", Key = "", Type = "", Default = "", Variable = "", OnValue = "1", Hook = "", Label = "", Help = "";
    public string[] Needs = [];
    public bool Live;
    // A choice's values and their labels (bool: on and off, available: Available and Off)
    public (string Value, string Label)[] Choices = [];
    public double Min, Max;
    // In the file; what the game started with, from the environment the launcher set; the variable set outside that
    // decides it instead (name=value)
    public string Saved = "", Running = "";
    public string? Outside;

    public string Kind => Type.Split(':')[0];

    // How the tab shows a value: a choice's label, a scale in percent
    public string LabelOf(string value) =>
        Kind == "scale" && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)
            ? $"{Math.Round(f * 100)}%"
            : Choices.FirstOrDefault(c => c.Value == value).Label ?? value;

    public bool Valid(string value)
    {
        switch (Kind)
        {
            case "text":
                return true;
            case "int":
                return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i >= Min && i <= Max;
            case "scale" or "number":
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= Min && d <= Max;
            default:
                return Choices.Any(c => c.Value == value);
        }
    }

    // The value as port-settings.toml writes it
    public string Toml(string value) => Kind is "choice" or "text" ? $"\"{value}\"" : value;
}

// The port settings as the Mac Port tab sees them. The launcher reads port-settings.toml when the game starts and sets
// the environment variables that KSA Metal and the hooks read; the tab shows the file's values, writes the ones it
// changes, and knows from the environment what the game runs with.
public static class PortSettings
{
    public static readonly List<PortSetting> All = new();
    // The sections, in the table's order: key and title
    public static readonly List<(string Key, string Title)> Sections = new();
    public static string FilePath = "", TemplatePath = "";

    public static PortSetting? Get(string key) => All.FirstOrDefault(s => s.Key == key);

    // Reads the table, the file, and the environment. Throws when the table is missing.
    public static void Load(string resources)
    {
        All.Clear();
        Sections.Clear();
        var table = Path.Combine(resources, "port-settings.txt");
        TemplatePath = Path.Combine(resources, "port-settings.toml");
        string section = "";
        foreach (var raw in File.ReadAllLines(table))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                section = line[1..end];
                Sections.Add((section, line[(end + 1)..].Trim()));
                continue;
            }
            var c = line.Split('|').Select(x => x.Trim()).ToArray();
            if (c.Length < 9)
                continue;
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
                case "available":
                    s.Choices = [("true", "Available"), ("false", "Off")];
                    break;
                case "choice":
                    s.Choices = parts[1].Split('/').Select(v => (v[..v.IndexOf('=')], v[(v.IndexOf('=') + 1)..])).ToArray();
                    break;
                case "scale" or "int" or "number":
                    s.Min = double.Parse(parts[1], CultureInfo.InvariantCulture);
                    s.Max = double.Parse(parts[2], CultureInfo.InvariantCulture);
                    break;
            }
            All.Add(s);
        }
        FilePath = Environment.GetEnvironmentVariable("KSA_PORT_SETTINGS") is { Length: > 0 } f
            ? f : Path.Combine(KSA.Constants.DocumentsFolderPath, "port-settings.toml");
        var outside = (Environment.GetEnvironmentVariable("KSA_PORT_OUTSIDE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var saved = ReadFile();
        foreach (var s in All)
        {
            s.Saved = saved.TryGetValue(s.Key, out var v) ? v : s.Default;
            s.Running = s.Variable.Length == 0 ? s.Saved : FromEnvironment(s);
            if (outside.Contains(s.Variable))
                s.Outside = $"{s.Variable}={Environment.GetEnvironmentVariable(s.Variable)}";
        }
    }

    // The values the file holds (valid ones; the launcher logs the others)
    public static Dictionary<string, string> ReadFile()
    {
        var values = new Dictionary<string, string>();
        if (!File.Exists(FilePath))
            return values;
        foreach (var raw in File.ReadAllLines(FilePath))
        {
            var line = raw.Split('#')[0].Trim();
            int eq = line.IndexOf('=');
            if (eq <= 0 || line[0] == '[')
                continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (Get(key) is { } s && s.Valid(value))
                values[key] = value;
        }
        return values;
    }

    // What the game runs with: the variable's value, read back into the setting's (unset: its default)
    private static string FromEnvironment(PortSetting s)
    {
        var env = Environment.GetEnvironmentVariable(s.Variable);
        if (string.IsNullOrEmpty(env))
            return s.Default;
        return s.Kind switch
        {
            "bool" or "available" => env == "0" ? "false" : s.OnValue == "1" || env == s.OnValue ? "true" : "false",
            "text" => env,
            _ => s.Valid(env) ? env : s.Default
        };
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

    // Writes these values into the file: a setting's line is replaced, or added to its section (the file is made from
    // the template when there is none)
    public static void Save(IReadOnlyDictionary<string, string> values)
    {
        var lines = (File.Exists(FilePath) ? File.ReadAllLines(FilePath) : File.Exists(TemplatePath) ? File.ReadAllLines(TemplatePath) : [])
            .ToList();
        foreach (var (key, value) in values)
        {
            if (Get(key) is not { } s)
                continue;
            var text = $"{key} = {s.Toml(value)}";
            var pattern = new Regex($@"^\s*{Regex.Escape(key)}\s*=");
            int at = lines.FindIndex(l => pattern.IsMatch(l));
            if (at >= 0)
            {
                lines[at] = text;
                continue;
            }
            int header = lines.FindIndex(l => l.Trim() == $"[{s.Section}]");
            if (header < 0)
            {
                lines.AddRange(["", $"[{s.Section}]", text]);
                continue;
            }
            // The end of the section: before the next one, and before its blank lines
            int end = lines.FindIndex(header + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0)
                end = lines.Count;
            while (end > header + 1 && lines[end - 1].Trim().Length == 0)
                end--;
            lines.Insert(end, text);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".new";
        File.WriteAllLines(temporary, lines);
        File.Move(temporary, FilePath, overwrite: true);
        foreach (var (key, value) in values)
            if (Get(key) is { } s)
                s.Saved = value;
    }
}
