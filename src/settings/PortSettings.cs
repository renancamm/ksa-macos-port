using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KsaPortSettings;

// The port settings: the table (port-settings.txt, in the app's Contents/Resources), the player's file
// (port-settings.toml in the game's documents folder) and the environment the game runs with. The hooks' loader reads
// the table and the file when the game starts and sets the variables Plan says; the hooks then read each setting back
// with Load, the Mac Port tab shows the file's values and writes the ones it changes (Save), and the benchmark writes a
// run's file with them.
public static class PortSettings
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

    // Reads the table in this folder (the app's Contents/Resources, or src/settings/), and the template's path next to
    // it. Throws when it is missing or a line isn't a setting.
    public static void LoadTable(string folder)
    {
        All.Clear();
        Sections.Clear();
        var table = Path.Combine(folder, "port-settings.txt");
        TemplatePath = Path.Combine(folder, "port-settings.toml");
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
            string[] List(string column) => column.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var s = new PortSetting
            {
                Section = section, Key = c[0], Type = c[1], Default = c[2], Needs = List(c[3]), Live = c[4] == "live",
                By = List(c[5]), Presets = List(c[6]), Label = c[7], Help = string.Join('|', c[8..]),
            };
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

    // Reads the table, the file, and the environment, as the hooks do once the loader set it: each setting's Running
    // value is its variable's, the default while its needs aren't met (the game's own settings count as met, as the
    // loader takes them). warn hears about a variable that holds none of its setting's values. Throws when the table is
    // missing or a line of it isn't a setting.
    public static void Load(string folder, Action<string>? warn = null)
    {
        LoadTable(folder);
        FilePath = Environment.GetEnvironmentVariable("KSA_PORT_SETTINGS") is { Length: > 0 } f
            ? f : Path.Combine(DocumentsFolder(), "port-settings.toml");
        var outside = (Environment.GetEnvironmentVariable("KSA_PORT_OUTSIDE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var saved = ReadFile();
        foreach (var s in All)
        {
            s.Saved = saved.TryGetValue(s.Key, out var v) ? v : s.Default;
            if (s.Variable.Length == 0)
            {
                s.Running = s.Saved;
                continue;
            }
            var env = Environment.GetEnvironmentVariable(s.Variable);
            s.Running = s.FromEnvironment(env, out bool valid);
            if (!valid)
                warn?.Invoke($"{s.Variable}={env} is none of {s.Key}'s values ({s.Type}): {s.Key} = {s.Default}");
            if (outside.Contains(s.Variable))
                s.Outside = $"{s.Variable}={env}";
        }
        // Needs on what the game runs with: a variable set outside can name a setting whose needs aren't met
        var unmet = All.Where(s => !Met(s, o => o.Running, _ => true)).ToList();
        foreach (var s in unmet)
            s.Running = s.Default;
    }

    // What the file's values (saved) make of the environment (env reads it): the variables to set, what the log says
    // of each setting the file changes, and the variables set already, which win over the file. A setting the file
    // changes is set once its needs are met (the game's own settings, which only the tab checks, count as met); while
    // one isn't, it keeps its default.
    public static (List<(string Name, string Value)> Set, List<string> Changed, List<string> Outside) Plan(
        IReadOnlyDictionary<string, string> saved, Func<string, string?> env, string home)
    {
        string Value(PortSetting s) => saved.TryGetValue(s.Key, out var v) ? v : s.Default;
        var set = new List<(string, string)>();
        var changed = new List<string>();
        var outside = All.Select(s => s.Variable).Where(v => v.Length > 0 && env(v) != null).Distinct().ToList();
        foreach (var s in All)
        {
            var value = Value(s);
            if (value == s.Default)
                continue;
            if (!Met(s, Value, _ => true))
            {
                changed.Add($"{s.Key} = {value} (off: needs {string.Join(',', s.Needs)})");
                continue;
            }
            if (s.Variable.Length == 0)
                continue;
            if (outside.Contains(s.Variable))
            {
                changed.Add($"{s.Key} = {value} (but {s.Variable}={env(s.Variable)} is set)");
                continue;
            }
            set.Add((s.Variable, s.ToEnvironment(value, home)));
            changed.Add($"{s.Key} = {value}");
        }
        return (set, changed, outside);
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
}
