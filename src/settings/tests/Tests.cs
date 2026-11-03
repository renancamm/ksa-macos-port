using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using KsaPortSettings;

// The tests of the port settings' library: the table (src/settings/port-settings.txt) and the rules its lines keep,
// reading and writing port-settings.toml, the needs, the values read back from the environment, and the variables the
// loader sets. scripts/build-dotnet.sh runs them; a failure stops the build. The generator checks the table against
// the rest of the repository (the hooks it names, the settings the hooks read).
//
// Usage: dotnet KsaPortSettingsTests.dll <repository>
internal static class Tests
{
    private static string _repo = "", _test = "", _scratch = "";
    private static readonly List<string> Failures = new();

    // The game's own settings a need can name: those MacPortTab.Game reads
    private static readonly string[] GameNeeds = ["vsync", "plume_trail_shadows", "pcss"];

    // The kinds of setting the Mac Port tab draws (MacPortTab.DrawRow); the others are the file's only ([developer])
    private static readonly string[] TabKinds = ["bool", "choice", "scale"];

    // The characters a value can have before a control runs out of room for it
    private const int ValueWidth = 18;

    private static int Main(string[] args)
    {
        _repo = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        _scratch = Path.Combine(Path.GetTempPath(), $"ksa-settings-tests-{Environment.ProcessId}");
        var tests = new (string Name, Action Run)[]
        {
            ("the table's lines", TableRules),
            ("the names the by column gives", Names),
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
            ? $"    The port settings' tests: {tests.Length} passed."
            : $"    The port settings' tests: {Failures.Count} failures.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            Failures.Add($"{_test}: {what}");
    }

    private static void Equal<T>(T expected, T actual, string what) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");

    private static void LoadTable() => PortSettings.LoadTable(SettingsFolder);

    private static PortSetting Setting(string key) => PortSettings.Get(key) ?? throw new Exception($"no setting {key}");

    // The table's folder in the repository
    private static string SettingsFolder => Path.Combine(_repo, "src", "settings");

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

    // A setting's variable is KSA_MTL_ and its key, or Apple's (metal:), or none (tab); its hook the class by hooks:
    // names
    private static void Names()
    {
        Equal("KSA_MTL_FUSED_TESSELLATION", Setting("fused_tessellation").Variable, "a variable");
        Equal("MTL_HUD_ENABLED", Setting("metal_performance_hud").Variable, "Apple's variable");
        Equal("", Setting("restart_for_a_clean_run").Variable, "the tab's own");
        Equal("TerrainCache", Setting("terrain_cache").Hook, "a hook");
        Equal("RenderScale", Setting("frame_interpolation").Hook, "a hook with the driver");
        Equal("", Setting("shader_cache").Hook, "the driver's");
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
            "skip_game_anti_aliasing = false", "unknown_setting = 1", "fit_hud_to_display = \"sideways\"", "trail_shadows = \"4,128\"",
            "trail_caches = \"0, 0, 16\"", "log_file = \"~/a#b \\\"q\\\".log\"  # a comment", "shot_dir = '/tmp/x#y'",
            "dump_msl = /tmp/msl # a comment",
        ]);
        var warnings = new List<string>();
        var values = PortSettings.ReadFile(warnings.Add);
        Equal("metalfx", values.GetValueOrDefault("upscaling"), "upscaling");
        Equal("0.75", values.GetValueOrDefault("render_scale"), "render_scale");
        Equal("false", values.GetValueOrDefault("skip_game_anti_aliasing"), "skip_game_anti_aliasing");
        Equal("0, 0, 16", values.GetValueOrDefault("trail_caches"), "trail_caches");
        Equal("~/a#b \"q\".log", values.GetValueOrDefault("log_file"), "a string with # and an escaped quote");
        Equal("/tmp/x#y", values.GetValueOrDefault("shot_dir"), "a string in single quotes");
        Equal("/tmp/msl", values.GetValueOrDefault("dump_msl"), "a value without quotes, before a comment");
        Check(!values.ContainsKey("fit_hud_to_display") && !values.ContainsKey("trail_shadows"), "an invalid value was read");
        Equal(3, warnings.Count, "warnings");
        Check(warnings.Any(w => w.Contains("unknown_setting")), "no warning for the unknown setting");
    }

    private static void Save()
    {
        PortSettings.FilePath = Path.Combine(_scratch, "port-settings.toml");
        PortSettings.TemplatePath = Path.Combine(_scratch, "template.toml");
        File.WriteAllLines(PortSettings.TemplatePath,
            ["# Port settings", "", "[image]", "", "# upscaling: help", "upscaling = \"off\"", "", "[interface]", "fit_hud_to_display = true", ""]);
        PortSettings.Save(new Dictionary<string, string>
        {
            ["upscaling"] = "metalfx", ["render_scale"] = "0.5", ["skip_hidden_crew_portraits"] = "false", ["trail_shadows"] = "4,128,3000",
            ["log_file"] = "~/logs #1/\"ksa\" \\ metal.log",
        });
        var lines = File.ReadAllLines(PortSettings.FilePath);
        Equal("upscaling = \"metalfx\"", lines[5], "the changed line");
        Equal("render_scale = 0.5", lines[6], "the line added to its section, before its blank lines");
        Equal("# upscaling: help", lines[4], "the comment kept");
        Check(lines.Contains("[optimizations]") && Array.IndexOf(lines, "skip_hidden_crew_portraits = false") > Array.IndexOf(lines, "[optimizations]"),
            "the setting of a section the file lacks, in a new section");
        Check(lines.Contains("trail_shadows = \"4,128,3000\""), "numbers written as a string");
        Equal("metalfx", Setting("upscaling").Saved, "the saved value");
        var values = PortSettings.ReadFile();
        Equal("false", values.GetValueOrDefault("skip_hidden_crew_portraits"), "read back");
        Equal("4,128,3000", values.GetValueOrDefault("trail_shadows"), "numbers read back");
        Equal("~/logs #1/\"ksa\" \\ metal.log", values.GetValueOrDefault("log_file"), "a string with #, quotes and a backslash read back");
    }

    private static void Needs()
    {
        var values = new Dictionary<string, string>();
        string Value(PortSetting s) => values.TryGetValue(s.Key, out var v) ? v : s.Default;
        bool Met(string key, bool game = true) => PortSettings.Met(Setting(key), Value, _ => game);
        Check(!Met("render_scale"), "render_scale without upscaling");
        Check(!Met("skip_game_anti_aliasing"), "skip_game_anti_aliasing without upscaling");
        values["upscaling"] = "spatial";
        Check(Met("render_scale"), "render_scale with spatial upscaling");
        Check(!Met("frame_interpolation"), "frame_interpolation with spatial upscaling");
        values["upscaling"] = "metalfx";
        Check(Met("frame_interpolation") && Met("skip_game_anti_aliasing"), "frame_interpolation with MetalFX");
        Check(Met("ray_tracing"), "ray_tracing with ray queries");
        values["ray_queries"] = "false";
        Check(!Met("ray_tracing"), "ray_tracing without ray queries");
        Check(!Met("gpu_profile_frames"), "gpu_profile_frames without gpu_profile");
        values["gpu_profile"] = "~/profile.csv";
        Check(Met("gpu_profile_frames"), "gpu_profile_frames with gpu_profile");
        Check(!Met("lighter_plume_shadows", game: false), "lighter_plume_shadows without the game's plume trail shadows");
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
        Is("strict_sync", "1", "true");
        Is("strict_sync", "strict", "false", valid: false);
        Is("fit_hud_to_display", "0", "false");
        Is("fit_hud_to_display", "game", "true", valid: false);
        Is("fewer_cloud_shadow_updates", "off", "false", valid: false);
        Is("upscaling", "Metalfx", "off", valid: false);
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

    // PortSettings.Load, as the hooks call it: the file, the environment, the variables set outside, and needs not met
    private static void HooksLoad()
    {
        var file = Path.Combine(_scratch, "port-settings.toml");
        File.WriteAllLines(file, ["[interface]", "fit_hud_to_display = false", "[image]", "upscaling = \"metalfx\""]);
        var variables = new Dictionary<string, string?>
        {
            ["KSA_PORT_SETTINGS"] = file, ["KSA_PORT_OUTSIDE"] = "KSA_MTL_RENDER_SCALE", ["KSA_MTL_RENDER_SCALE"] = "Always",
            ["KSA_MTL_FIT_HUD_TO_DISPLAY"] = "0", ["KSA_MTL_UPSCALING"] = "metalfx", ["KSA_MTL_TERRAIN_CACHE"] = "0", ["KSA_MTL_GPU_PROFILE_SPLIT"] = "1",
        };
        var before = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        foreach (var (name, value) in variables)
            Environment.SetEnvironmentVariable(name, value);
        try
        {
            var warnings = new List<string>();
            PortSettings.Load(SettingsFolder, warnings.Add);
            Equal(file, PortSettings.FilePath, "the file");
            Equal("false", Setting("fit_hud_to_display").Saved, "saved");
            Equal("false", Setting("fit_hud_to_display").Running, "running");
            Equal("false", Setting("terrain_cache").Running, "a switch off");
            Equal("1.0", Setting("render_scale").Running, "an invalid variable: the default");
            Equal("KSA_MTL_RENDER_SCALE=Always", Setting("render_scale").Outside, "set outside");
            Equal(null, Setting("fit_hud_to_display").Outside, "set from the file");
            Equal("false", Setting("gpu_profile_split").Running, "a variable whose setting's needs aren't met: the default");
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
            PortSettings.Plan(saved, v => env.GetValueOrDefault(v), "/home/kitten");

        var plan = Plan([]);
        Check(plan.Set.Count == 0 && plan.Changed.Count == 0, "the defaults set something");
        plan = Plan(new() { ["render_scale"] = "0.5" });
        Check(plan.Set.Count == 0, "render_scale set without upscaling");
        Check(plan.Changed.Any(c => c.Contains("off: needs upscaling!=off")), "no note that render_scale needs upscaling");

        plan = Plan(new()
        {
            ["upscaling"] = "metalfx", ["render_scale"] = "0.5", ["terrain_cache"] = "false", ["strict_sync"] = "true",
            ["log_file"] = "~/ksa.log", ["fit_hud_to_display"] = "true", ["restart_for_a_clean_run"] = "true",
        });
        var set = plan.Set.ToDictionary(v => v.Name, v => v.Value);
        Equal("metalfx", set.GetValueOrDefault("KSA_MTL_UPSCALING"), "a choice");
        Equal("0.5", set.GetValueOrDefault("KSA_MTL_RENDER_SCALE"), "a scale whose need is met");
        Equal("0", set.GetValueOrDefault("KSA_MTL_TERRAIN_CACHE"), "a switch off");
        Equal("1", set.GetValueOrDefault("KSA_MTL_STRICT_SYNC"), "a switch on");
        Equal("/home/kitten/ksa.log", set.GetValueOrDefault("KSA_MTL_LOG_FILE"), "a path from ~");
        Check(!set.ContainsKey("KSA_MTL_FIT_HUD_TO_DISPLAY"), "a setting at its default set");
        Equal(5, set.Count, "variables set");

        env["KSA_MTL_UPSCALING"] = "spatial";
        plan = Plan(new() { ["upscaling"] = "metalfx" });
        Check(plan.Set.Count == 0, "a variable set outside overwritten");
        Check(plan.Outside.Contains("KSA_MTL_UPSCALING"), "the variable set outside not noted");
        Check(plan.Changed.Any(c => c.Contains("but KSA_MTL_UPSCALING=spatial is set")), "the log doesn't say the variable wins");
    }
}
