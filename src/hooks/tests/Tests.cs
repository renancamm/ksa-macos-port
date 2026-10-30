using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using KsaMetalHooks;

// The tests of the port settings' code that runs without the game: the table (src/launcher/port-settings.txt) and the
// rules its lines keep, reading and writing port-settings.toml, the needs, the values read back from the environment,
// and the variables the loader sets. scripts/build-hooks.sh runs them after building the hooks; a failure stops the
// build.
//
// Usage: dotnet KsaMetalHooksTests.dll <repository>
internal static class Tests
{
    private static string _repo = "", _test = "", _scratch = "";
    private static readonly List<string> Failures = new();

    // The game's own settings a need can name: those MacPortTab.Game reads
    private static readonly string[] GameNeeds = ["vsync", "plume_trail_shadows", "pcss"];

    // The kinds of setting the Mac Port tab draws (MacPortTab.DrawRow); the others are the file's only ([developer])
    private static readonly string[] TabKinds = ["bool", "available", "choice", "scale"];

    // The characters a dropdown's control shows before it cuts a value off
    private const int ValueWidth = 18;

    private static int Main(string[] args)
    {
        _repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        _scratch = Path.Combine(Path.GetTempPath(), $"ksa-hooks-tests-{Environment.ProcessId}");
        var tests = new (string Name, Action Run)[]
        {
            ("the table's lines", TableRules),
            ("the hooks the table names", TableHooks),
            ("the settings the hooks read", SettingsRead),
            ("a malformed table", MalformedTable),
            ("reading the file", ReadFile),
            ("writing the file", Save),
            ("needs", Needs),
            ("values from the environment", FromEnvironment),
            ("the hooks' view of the settings", HooksLoad),
            ("what the loader sets", LoaderPlan),
        };
        foreach (var (name, run) in tests)
        {
            _test = name;
            Directory.CreateDirectory(_scratch);
            try
            {
                LoadTable();
                run();
            }
            catch (Exception e)
            {
                Failures.Add($"{name}: {e}");
            }
            finally
            {
                Directory.Delete(_scratch, true);
            }
        }
        foreach (var failure in Failures)
            Console.WriteLine($"FAILED {failure}");
        Console.WriteLine(Failures.Count == 0
            ? $"    The hooks' tests: {tests.Length} passed."
            : $"    The hooks' tests: {Failures.Count} failures.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            Failures.Add($"{_test}: {what}");
    }

    private static void Equal<T>(T expected, T actual, string what) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");

    private static void LoadTable() => PortSettings.LoadTable(Path.Combine(_repo, "src", "launcher"));

    private static PortSetting Setting(string key) => PortSettings.Get(key) ?? throw new Exception($"no setting {key}");

    private static string Source(params string[] path) => File.ReadAllText(Path.Combine([_repo, "src", "hooks", .. path]));

    // Each line of the table: its key and variable are its own, its default is one of its values, its needs name
    // settings that exist (or the game's that the tab checks) with values they take, without a loop, the tab draws its
    // kind and its values fit, and its help can be read back. Each of the tab's sections has a caption.
    private static void TableRules()
    {
        var all = PortSettings.All;
        Check(all.Count > 30, $"only {all.Count} settings read");
        foreach (var key in all.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key))
            Check(false, $"{key} is in the table more than once");
        foreach (var variable in all.Where(s => s.Variable.Length > 0).GroupBy(s => s.Variable).Where(g => g.Count() > 1))
            Check(false, $"{variable.Key} is the variable of {string.Join(" and ", variable.Select(s => s.Key))}");
        var sections = PortSettings.Sections.Select(s => s.Key).ToHashSet();
        foreach (var (key, _, caption) in PortSettings.Sections.Where(s => s.Key != "developer"))
            Check(caption.Length > 0, $"section {key} has no caption");
        foreach (var s in all)
        {
            Check(s.Valid(s.Default), $"{s.Key}'s default '{s.Default}' is none of its values ({s.Type})");
            Check(sections.Contains(s.Section), $"{s.Key} is in no section");
            Check(!s.Help.Contains('|'), $"{s.Key}'s help holds |, the table's separator");
            if (s.Section != "developer")
            {
                Check(TabKinds.Contains(s.Kind), $"{s.Key}'s kind, {s.Kind}, isn't one the Mac Port tab draws");
                Check(s.Label.Length > 0, $"{s.Key} has no label for its row in the tab");
                foreach (var (_, label) in s.Choices.Where(c => c.Label.Length > ValueWidth))
                    Check(false, $"{s.Key}'s value '{label}' is longer than the tab's {ValueWidth} characters");
            }
            foreach (var need in s.Needs)
            {
                if (need.StartsWith("game:", StringComparison.Ordinal))
                {
                    Check(GameNeeds.Contains(need[5..]), $"{s.Key} needs {need}, which the tab doesn't check");
                    continue;
                }
                int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
                var key = ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need;
                var value = ne > 0 ? need[(ne + 2)..] : eq > 0 ? need[(eq + 1)..] : null;
                if (PortSettings.Get(key) is not { } other)
                    Check(false, $"{s.Key} needs {key}, which isn't a setting");
                else if (value != null)
                    Check(other.Valid(value), $"{s.Key} needs {need}, which isn't one of {key}'s values");
            }
            Check(!NeedsLoop(s, []), $"{s.Key}'s needs lead back to it");
        }
    }

    private static bool NeedsLoop(PortSetting s, HashSet<string> seen)
    {
        if (!seen.Add(s.Key))
            return true;
        foreach (var need in s.Needs.Where(n => !n.StartsWith("game:", StringComparison.Ordinal)))
        {
            int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
            if (PortSettings.Get(ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need) is { } other && NeedsLoop(other, [.. seen]))
                return true;
        }
        return false;
    }

    // The hook column names hooks that Hooks.Apply runs: otherwise the tab couldn't show the setting as unavailable
    private static void TableHooks()
    {
        var keys = Regex.Matches(Source("Hooks.cs"), @"Run\(""[^""]+"", ""([a-z_]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        Check(keys.Count > 5, $"only {keys.Count} hooks found in Hooks.cs");
        foreach (var s in PortSettings.All.Where(s => s.Hook.Length > 0))
            Check(keys.Contains(s.Hook), $"{s.Key}'s hook {s.Hook} isn't one Hooks.Apply runs");
    }

    // Every key the hooks read is in the table (Settings.Load would throw, and no hook would apply), and every live
    // setting with a variable is read by them (the tab's Apply only changes what they read)
    private static void SettingsRead()
    {
        var hooks = Path.Combine(_repo, "src", "hooks");
        var read = Directory.GetFiles(hooks, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"(?:Settings\.(?:Value|On|Number|Numbers|Get)|\bGet)\(""([a-z_0-9]+)""\)"))
            .Select(m => m.Groups[1].Value).ToHashSet();
        Check(read.Count > 10, $"only {read.Count} settings found read by the hooks");
        foreach (var key in read)
            Check(PortSettings.Get(key) != null, $"the hooks read {key}, which isn't in the table");
        foreach (var s in PortSettings.All.Where(s => s.Live && s.Variable.Length > 0))
            Check(read.Contains(s.Key), $"{s.Key} is live, but no hook reads it");
    }

    private static void MalformedTable()
    {
        File.WriteAllText(Path.Combine(_scratch, "port-settings.txt"), "[image] Image\nupscaling | choice:off=Off | off\n");
        try
        {
            PortSettings.LoadTable(_scratch);
            Check(false, "a line of 3 columns was read");
        }
        catch (FormatException e)
        {
            Check(e.Message.Contains("line 2"), $"the error doesn't say which line: {e.Message}");
        }
    }

    private static void ReadFile()
    {
        PortSettings.FilePath = Path.Combine(_scratch, "port-settings.toml");
        File.WriteAllLines(PortSettings.FilePath,
        [
            "# Port settings", "", "[image]", "upscaling = \"metalfx\"   # a comment", "render_scale = 0.75",
            "override_antialiasing = false", "unknown_setting = 1", "hud_layout = \"sideways\"", "trail_shadows = \"4,128\"",
            "trail_caches = \"0, 0, 16\"", "log_file = \"~/a#b \\\"q\\\".log\"  # a comment", "shot_dir = '/tmp/x#y'",
            "dump_msl = /tmp/msl # a comment",
        ]);
        var warnings = new List<string>();
        var values = PortSettings.ReadFile(warnings.Add);
        Equal("metalfx", values.GetValueOrDefault("upscaling"), "upscaling");
        Equal("0.75", values.GetValueOrDefault("render_scale"), "render_scale");
        Equal("false", values.GetValueOrDefault("override_antialiasing"), "override_antialiasing");
        Equal("0, 0, 16", values.GetValueOrDefault("trail_caches"), "trail_caches");
        Equal("~/a#b \"q\".log", values.GetValueOrDefault("log_file"), "a string with # and an escaped quote");
        Equal("/tmp/x#y", values.GetValueOrDefault("shot_dir"), "a string in single quotes");
        Equal("/tmp/msl", values.GetValueOrDefault("dump_msl"), "a value without quotes, before a comment");
        Check(!values.ContainsKey("hud_layout") && !values.ContainsKey("trail_shadows"), "an invalid value was read");
        Equal(3, warnings.Count, "warnings");
        Check(warnings.Any(w => w.Contains("unknown_setting")), "no warning for the unknown setting");
    }

    private static void Save()
    {
        PortSettings.FilePath = Path.Combine(_scratch, "port-settings.toml");
        PortSettings.TemplatePath = Path.Combine(_scratch, "template.toml");
        File.WriteAllLines(PortSettings.TemplatePath,
            ["# Port settings", "", "[image]", "", "# upscaling: help", "upscaling = \"off\"", "", "[interface]", "hud_layout = \"fit\"", ""]);
        PortSettings.Save(new Dictionary<string, string>
        {
            ["upscaling"] = "metalfx", ["render_scale"] = "0.5", ["crew_portraits"] = "shown_15", ["trail_shadows"] = "4,128,3000",
            ["log_file"] = "~/logs #1/\"ksa\" \\ metal.log",
        });
        var lines = File.ReadAllLines(PortSettings.FilePath);
        Equal("upscaling = \"metalfx\"", lines[5], "the changed line");
        Equal("render_scale = 0.5", lines[6], "the line added to its section, before its blank lines");
        Equal("# upscaling: help", lines[4], "the comment kept");
        Check(lines.Contains("[optimizations]") && Array.IndexOf(lines, "crew_portraits = \"shown_15\"") > Array.IndexOf(lines, "[optimizations]"),
            "the setting of a section the file lacks, in a new section");
        Check(lines.Contains("trail_shadows = \"4,128,3000\""), "numbers written as a string");
        Equal("metalfx", Setting("upscaling").Saved, "the saved value");
        var values = PortSettings.ReadFile();
        Equal("shown_15", values.GetValueOrDefault("crew_portraits"), "read back");
        Equal("4,128,3000", values.GetValueOrDefault("trail_shadows"), "numbers read back");
        Equal("~/logs #1/\"ksa\" \\ metal.log", values.GetValueOrDefault("log_file"), "a string with #, quotes and a backslash read back");
    }

    private static void Needs()
    {
        var values = new Dictionary<string, string>();
        string Value(PortSetting s) => values.TryGetValue(s.Key, out var v) ? v : s.Default;
        bool Met(string key, bool game = true) => PortSettings.Met(Setting(key), Value, _ => game);
        Check(!Met("render_scale"), "render_scale without upscaling");
        Check(!Met("override_antialiasing"), "override_antialiasing without upscaling");
        values["upscaling"] = "spatial";
        Check(Met("render_scale"), "render_scale with spatial upscaling");
        Check(!Met("frame_interpolation"), "frame_interpolation with spatial upscaling");
        values["upscaling"] = "metalfx";
        Check(Met("frame_interpolation") && Met("override_antialiasing"), "frame_interpolation with MetalFX");
        Check(Met("ray_tracing"), "ray_tracing with ray queries");
        values["ray_queries"] = "false";
        Check(!Met("ray_tracing"), "ray_tracing without ray queries");
        Check(!Met("gpu_profile_frames"), "gpu_profile_frames without gpu_profile");
        values["gpu_profile"] = "~/profile.csv";
        Check(Met("gpu_profile_frames"), "gpu_profile_frames with gpu_profile");
        Check(!Met("plume_shadow_tuning", game: false), "plume_shadow_tuning without the game's plume trail shadows");
        values["tessellation"] = "false";
        Check(!Met("fused_tessellation"), "fused_tessellation without tessellation");
    }

    private static void FromEnvironment()
    {
        void Is(string key, string? env, string expected, bool valid = true)
        {
            Equal(expected, Setting(key).FromEnvironment(env, out bool v), $"{key} from '{env}'");
            Equal(valid, v, $"{key} from '{env}' valid");
        }
        Is("terrain_cache", null, "true");
        Is("terrain_cache", "", "true");
        Is("terrain_cache", "0", "false");
        Is("terrain_cache", "1", "true");
        Is("strict_sync", "strict", "true");
        Is("strict_sync", "1", "false");
        Is("hud_layout", "game", "game");
        Is("hud_layout", "Game", "fit", valid: false);
        Is("render_scale", "0.75", "0.75");
        Is("render_scale", "0.3", "1.0", valid: false);
        Is("worker_park_ms", "abc", "0.25", valid: false);
        Is("trail_shadows", "4,128,3000", "4,128,3000");
        Is("trail_shadows", "4,128", "", valid: false);
        Is("trail_shadows", "4,128,3km", "", valid: false);
        Is("trail_caches", "0,0,16", "0,0,16");
        Is("trail_caches", "0,0,16,33", "0,0,16,33");
        Is("log_file", "~/ksa.log", "~/ksa.log");
    }

    // PortSettings.Load, as Settings.Load calls it: the file, the environment, and the variables set outside
    private static void HooksLoad()
    {
        var file = Path.Combine(_scratch, "port-settings.toml");
        File.WriteAllLines(file, ["[interface]", "hud_layout = \"game\"", "[image]", "upscaling = \"metalfx\""]);
        var variables = new Dictionary<string, string?>
        {
            ["KSA_PORT_SETTINGS"] = file, ["KSA_PORT_OUTSIDE"] = "KSA_CREW_PORTRAITS", ["KSA_CREW_PORTRAITS"] = "Always",
            ["KSA_HUD_LAYOUT"] = "game", ["KSA_UPSCALER"] = "metalfx", ["KSA_TERRAIN_CACHE"] = "0",
        };
        var before = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            var warnings = new List<string>();
            PortSettings.Load(Path.Combine(_repo, "src", "launcher"), warnings.Add);
            Equal(file, PortSettings.FilePath, "the file");
            Equal("game", Setting("hud_layout").Saved, "saved");
            Equal("game", Setting("hud_layout").Running, "running");
            Equal("false", Setting("terrain_cache").Running, "a switch off");
            Equal("shown", Setting("crew_portraits").Running, "an invalid variable: the default");
            Equal("KSA_CREW_PORTRAITS=Always", Setting("crew_portraits").Outside, "set outside");
            Equal(null, Setting("hud_layout").Outside, "set from the file");
            Equal(1, warnings.Count, "warnings");
        }
        finally
        {
            foreach (var (name, value) in before)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void LoaderPlan()
    {
        var env = new Dictionary<string, string>();
        (List<(string Name, string Value)> Set, List<string> Changed, List<string> Outside) Plan(Dictionary<string, string> saved) =>
            PortEnvironment.Plan(saved, v => env.GetValueOrDefault(v), "/home/kitten");

        var plan = Plan([]);
        Check(plan.Set.Count == 0 && plan.Changed.Count == 0, "the defaults set something");
        plan = Plan(new() { ["render_scale"] = "0.5" });
        Check(plan.Set.Count == 0, "render_scale set without upscaling");
        Check(plan.Changed.Any(c => c.Contains("off: needs upscaling!=off")), "no note that render_scale needs upscaling");

        plan = Plan(new()
        {
            ["upscaling"] = "metalfx", ["render_scale"] = "0.5", ["terrain_cache"] = "false", ["strict_sync"] = "true",
            ["log_file"] = "~/ksa.log", ["hud_layout"] = "fit", ["benchmark_restart"] = "true",
        });
        var set = plan.Set.ToDictionary(v => v.Name, v => v.Value);
        Equal("metalfx", set.GetValueOrDefault("KSA_UPSCALER"), "a choice");
        Equal("0.5", set.GetValueOrDefault("KSA_RENDER_SCALE"), "a scale whose need is met");
        Equal("0", set.GetValueOrDefault("KSA_TERRAIN_CACHE"), "a switch off");
        Equal("strict", set.GetValueOrDefault("KSA_MTL_SYNC"), "a switch whose on is a value");
        Equal("/home/kitten/ksa.log", set.GetValueOrDefault("KSA_MTL_LOG_FILE"), "a path from ~");
        Check(!set.ContainsKey("KSA_HUD_LAYOUT"), "a setting at its default set");
        Equal(5, set.Count, "variables set");

        env["KSA_UPSCALER"] = "spatial";
        plan = Plan(new() { ["upscaling"] = "metalfx" });
        Check(plan.Set.Count == 0, "a variable set outside overwritten");
        Check(plan.Outside.Contains("KSA_UPSCALER"), "the variable set outside not noted");
        Check(plan.Changed.Any(c => c.Contains("but KSA_UPSCALER=spatial is set")), "the log doesn't say the variable wins");
    }
}
