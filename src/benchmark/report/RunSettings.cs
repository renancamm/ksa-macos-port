// A run's documents folder and settings (BenchmarkRun, Run.cs): your settings.toml with the start and the preset on
// top, --settings, --set, --defaults, and the port settings with --port.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using KsaPort.Common;

namespace KsaMetalBenchmarkReport;

public static partial class Program
{
    private sealed partial class BenchmarkRun
    {
        // 1. The game's documents folder for the run, made fresh: the game alone (none of your mods, so they neither change
        //    the numbers nor stop the game from starting; the hooks add the benchmark's vehicles from the app)
        private void Prepare()
        {
            if (Directory.Exists(Instance))
                Directory.Delete(Instance, true);
            Directory.CreateDirectory(Instance);
            File.WriteAllText(Path.Combine(Instance, "manifest.toml"), "[[mods]]\nid = \"Core\"\nenabled = true\n");

            // The run's settings: yours, with the preset on top
            _settings = Path.Combine(Instance, "settings.toml");
            File.Copy(Path.Combine(Docs, "settings.toml"), _settings);
            // The game starts straight into a new game of the chosen system, without its system menu, update popup,
            // changelog or name labels, with the camera on the game's test vessel in orbit (a testing start, the one that
            // keeps the system's vehicles)
            var start = new List<string> { "selectSystemOnStart=false", $"lastSystemId=\"{SystemId}\"", "startGameType=\"Testing\"",
                "startVehicle=\"Rocket\"", "startParent=\"Earth\"", "startSituation=\"RocketStartingSituation\"", "checkForUpdates=false",
                "displayChangelogOnChange=false", "celestialNames=false", "locationNames=false" };
            if (_preset.Length > 0)
                start.AddRange(PresetCommon.Concat(Presets[_preset]));
            foreach (var kv in start)
                SetSetting(kv[..kv.IndexOf('=')], kv[(kv.IndexOf('=') + 1)..]);
            // --settings: its "key = value" lines
            if (_settingsFile.Length > 0)
            {
                foreach (var line in File.ReadLines(_settingsFile))
                {
                    if (line.Length == 0 || line[0] is '#' or '[')
                        continue;
                    int eq = line.IndexOf('=');
                    var key = eq < 0 ? "" : Regex.Replace(line[..eq], "[ \t]", "");
                    if (key.Length == 0)
                        Die($"{_settingsFile}: not a 'key = value' line: {line}");
                    SetSetting(key, TomlValue(line[(eq + 1)..].Trim()));
                }
            }
            // --set
            foreach (var kv in _sets)
            {
                if (!kv.Contains('='))
                    Die($"--set needs key=value, not '{kv}'.");
                SetSetting(kv[..kv.IndexOf('=')], TomlValue(kv[(kv.IndexOf('=') + 1)..]));
            }
            // --defaults: only the lines set above, in their sections. The game gives every setting the file leaves out its
            // default, as on a fresh install, and saves the whole file when it starts.
            if (_defaults)
            {
                var kept = new StringBuilder();
                string section = "";
                int n = 0;
                foreach (var line in File.ReadAllLines(_settings))
                {
                    if (line.StartsWith('['))
                    {
                        section = line;
                        continue;
                    }
                    var f = Fields(line);
                    if (f.Length >= 2 && f[1] == "=" && _setKeys.Contains($" {f[0]} "))
                    {
                        if (section.Length > 0)
                        {
                            kept.Append(n++ > 0 ? "\n" : "").Append(section).Append('\n');
                            section = "";
                        }
                        kept.Append(line).Append('\n');
                    }
                }
                File.WriteAllText(_settings, kept.ToString());
            }

            // The run's port settings (the app's port-settings.toml template): yours unless a preset is played or
            // --defaults, --port on top
            var portSettings = Path.Combine(Instance, "port-settings.toml");
            var yours = Path.Combine(Docs, "port-settings.toml");
            var template = Path.Combine(_res, "port-settings.toml");
            if (_preset.Length == 0 && !_defaults && File.Exists(yours))
                File.Copy(yours, portSettings);
            else if (File.Exists(template))
                File.Copy(template, portSettings);
            else
                File.WriteAllText(portSettings, "");
            if (_ports.Count == 0)
                return;
            // --port: checked against the app's table (a key or value the game doesn't take would run an unchanged
            // game that looks like an A/B test), and written as the Mac Port tab writes them
            try
            {
                PortSettings.LoadTable(_res);
            }
            catch (Exception e) when (e is IOException or FormatException)
            {
                Die($"--port: the app's table of the port settings can't be read: {e.Message}");
            }
            var values = new Dictionary<string, string>();
            foreach (var kv in _ports)
            {
                if (!kv.Contains('='))
                    Die($"--port needs key=value, not '{kv}'.");
                string key = kv[..kv.IndexOf('=')], value = kv[(kv.IndexOf('=') + 1)..];
                if (PortSettings.Get(key) is not { } setting)
                    Die($"--port: there is no setting '{key}' in port-settings.toml.");
                else if (!setting.Valid(value))
                    Die($"--port {key}={value}: it takes {setting.Values()}.");
                values[key] = value;
            }
            PortSettings.FilePath = portSettings;
            PortSettings.Save(values);
        }

        // One line of settings.toml (keys are unique across its sections). As <section>.<key>, the key in that section,
        // added when the file lacks it (one a newer KSA added; the section must be there)
        private void SetSetting(string name, string value)
        {
            int dot = name.LastIndexOf('.');
            string key = name[(dot + 1)..], section = dot < 0 ? "" : name[..dot];
            var lines = File.ReadAllLines(_settings).ToList();
            var line = $"{key} = {value}";
            if (section.Length == 0)
            {
                if (!lines.Any(l => l.StartsWith(key + " = ")))
                    Die($"settings.toml has no '{key}' (does this KSA version still match?).");
                lines = lines.Select(l => l.StartsWith(key + " = ") ? line : l).ToList();
            }
            else
            {
                int header = lines.IndexOf($"[{section}]");
                int end = header < 0 ? -1 : lines.FindIndex(header + 1, l => l.StartsWith('['));
                if (end < 0)
                    end = lines.Count;
                var inside = header < 0 ? [] : Enumerable.Range(header + 1, end - header - 1).Where(i => lines[i].StartsWith(key + " = ")).ToList();
                if (inside.Count > 0)
                {
                    foreach (var i in inside)
                        lines[i] = line;
                }
                else
                {
                    if (header < 0)
                        Die($"settings.toml has no [{section}] section.");
                    Output.Info($"settings.toml has no {key} in [{section}]: added");
                    lines.Insert(header + 1, line);
                }
            }
            File.WriteAllText(_settings, string.Join('\n', lines) + "\n");
            _setKeys += key + " ";
        }

        // A bare word is a string, quoted as TOML wants it (Pcf -> "Pcf"); anything else as written
        private static string TomlValue(string value) =>
            Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_-]*$") && value is not ("true" or "false" or "inf" or "nan") ? $"\"{value}\"" : value;
    }
}
