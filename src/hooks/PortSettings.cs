using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaMetalHooks;

// The port settings as the Mac Port tab sees them. The loader reads port-settings.toml when the game starts and sets
// the environment variables that KSA Metal and the hooks read (PortSettingsTable.cs reads the table and the file); the
// tab shows the file's values, writes the ones it changes, and knows from the environment what the game runs with.
public static partial class PortSettings
{
    // Reads the table, the file, and the environment. Throws when the table is missing.
    public static void Load(string resources)
    {
        LoadTable(resources);
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
