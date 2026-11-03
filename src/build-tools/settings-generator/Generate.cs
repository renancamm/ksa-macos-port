using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KsaPort.Common;

// What is made from the table of the port settings (src/shared/settings/port-settings.txt), and the checks that keep
// the rest of the repository in step with it (src/shared/settings/README.md). The build runs it; what it makes goes
// into the build tree, written only when it differs, so that what is compiled from it is compiled again only then.
// KsaPort.Tools.SettingsGenerator's commands:
//
//   template <table>        the commented template of port-settings.toml, to standard output
//   header <table> <file>   the native code's settings (port-settings.h; CMakeLists.txt)
//   hooks <table> <file>    the hooks' settings (Settings.Generated.cs; the hooks' project)
//   check <repo>            the checks
//
// Exits with 1 when a check fails.
internal static class Generate
{
    private static int Main(string[] args)
    {
        try
        {
            switch (args)
            {
                case ["template", var table]:
                    Load(Path.GetDirectoryName(Path.GetFullPath(table))!);
                    Console.Out.Write(Template());
                    return 0;
                case ["header" or "hooks", var table, var file]:
                    Load(Path.GetDirectoryName(Path.GetFullPath(table))!);
                    Write(file, args[0] == "header" ? Header() : HooksSettings());
                    return 0;
                case ["check", var repo]:
                    return Check(Path.GetFullPath(repo));
                default:
                    Console.Error.WriteLine("usage: KsaPort.Tools.SettingsGenerator template <table> | " +
                        "header <table> <file> | hooks <table> <file> | check <repo>");
                    return 2;
            }
        }
        catch (Exception e) when (e is IOException or FormatException)
        {
            Console.Error.WriteLine($"Error: {e.Message}");
            return 1;
        }
    }

    private static void Load(string folder)
    {
        PortSettings.LoadTable(folder);
        if (PortSettings.All.Count == 0)
            throw new FormatException($"{folder}/port-settings.txt holds no settings");
        if (PortSettings.All.FirstOrDefault(s => s.Section.Length == 0) is { } outside)
            throw new FormatException($"port-settings.txt: {outside.Key} comes before the first [section]");
    }

    // The template the hooks copy for the player: each section with its caption, each setting with its values and its
    // help as a comment, at its default; the settings of the sections the tab doesn't show commented out
    private static string Template()
    {
        var t = new StringBuilder();
        t.Append("""
            # Port settings for Kitten Space Agency (ksa-macos-port)
            #
            # The Mac Port tab in the game's settings (Esc) changes these too, and says which take effect at once and which when
            # the game starts again. A line you delete takes its default (the value written here); delete the file to get this
            # one back. Messages about the file go to logs/console.log in this folder.

            """);
        bool first = true;
        foreach (var (key, _, caption, inTab) in PortSettings.Sections)
        {
            if (!inTab && first)
            {
                t.Append('\n').Append(Comment("The sections below aren't in the Mac Port tab: they are for work on the port, " +
                    "and for testing a problem. The READMEs of the port's repository (src/game/driver/, " +
                    "src/game/hooks/) explain them. Remove the # in front of a line to use it."));
                first = false;
            }
            t.Append($"\n[{key}]\n");
            if (caption.Length > 0)
                t.Append('\n').Append(Comment(caption));
            foreach (var s in PortSettings.All.Where(s => s.Section == key))
            {
                var help = s.Help + string.Concat(s.OptionHelp.Select(o => $" \"{o.Key}\": {o.Value}"));
                t.Append('\n').Append(Comment($"{s.Key} ({Values(s.Type)}): {help}"));
                t.Append(inTab ? "" : "# ").Append($"{s.Key} = {TomlValue(s)}\n");
            }
        }
        return t.ToString();
    }

    // Text as "# " comment lines of at most 118 columns
    private static string Comment(string text)
    {
        var t = new StringBuilder();
        var line = "#";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length + 1 + word.Length > 118)
            {
                t.Append(line).Append('\n');
                line = "#";
            }
            line += " " + word;
        }
        return t.Append(line).Append('\n').ToString();
    }

    // The values a type takes, as the template's comment shows them
    private static string Values(string type)
    {
        var parts = type.Split(':');
        switch (parts[0])
        {
            case "bool":
                return "true or false";
            case "text":
                return "a path or text, \"\" for none";
            case "numbers":
                return (parts[1] == parts[2] ? parts[1] : $"{parts[1]} to {parts[2]}") + " numbers, comma separated, \"\" for none";
            case "choice":
                var values = parts[1].Split('/').Select(v => $"\"{v[..v.IndexOf('=')]}\"").ToArray();
                return values.Length == 1 ? values[0] : string.Join(", ", values[..^1]) + " or " + values[^1];
            case "scale" or "int" or "number" when parts.Length == 3:
                return $"{parts[1]} to {parts[2]}";
            default:
                throw new FormatException($"port-settings.txt: {type} is not a type of the table");
        }
    }

    private static string TomlValue(PortSetting s) => s.Toml(s.Default);

    // A key as a name in code: PascalCase for C# (fused_tessellation: FusedTessellation), camelCase for C
    private static string Pascal(string key) => string.Concat(key.Split('_').Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    private static string Camel(string key) => char.ToLowerInvariant(Pascal(key)[0]) + Pascal(key)[1..];

    // What a by column's entry names: a component that reads the setting, with the README that documents it
    private static readonly Dictionary<string, string> Readers = new()
    {
        ["driver"] = "src/game/driver/README.md", ["ktx-shim"] = "src/game/shims/ktx/README.md",
        ["hooks"] = "src/game/hooks/README.md",
    };

    private static string Reader(string by) => by.StartsWith("hooks:", StringComparison.Ordinal) ? "hooks" : by;

    private static bool By(PortSetting s, string reader) => s.By.Any(b => Reader(b) == reader);

    // The settings of the native code: those the driver or the KTX shim does
    private static IEnumerable<PortSetting> Native => PortSettings.All.Where(s => By(s, "driver") || By(s, "ktx-shim"));

    // The header the native code reads its settings with: a field of PortSettings per setting the driver or the KTX
    // shim does, which portSettingsRead() reads from its variable with the values the shared library takes (one
    // that is none of the setting's means its default)
    private static string Header()
    {
        var t = new StringBuilder("""
            // The port settings of the native code, KSA Metal and the KTX shim: made from
            // src/shared/settings/port-settings.txt by KsaPort.Tools.SettingsGenerator
            // (src/shared/settings/README.md), which the native build runs. Edit the table, not this.
            //
            // portSettingsRead() reads each one from its variable, taking the values the shared library takes: a switch
            // 0 or 1, a number in the table's range, an int of digits only; unset, empty or anything else, the table's
            // default. Text is NULL when unset. A variable set outside the game is taken without its setting's needs,
            // which the hooks' loader applies to those it sets.
            #pragma once

            #include <stdbool.h>
            #include <stdlib.h>
            #include <string.h>

            typedef struct PortSettings {

            """);
        foreach (var s in Native)
        {
            var field = $"    {NativeType(s).Type} {Camel(s.Key)};";
            t.Append(field).Append(' ', Math.Max(1, 44 - field.Length)).Append($"// {s.Variable}\n");
        }
        t.Append("""
            } PortSettings;

            static inline const char* portSettingText(const char* name) {
                const char* v = getenv(name);
                return v && *v ? v : NULL;
            }
            static inline bool portSettingOn(const char* name, bool def) {
                const char* v = portSettingText(name);
                return v && !strcmp(v, "0") ? false : v && !strcmp(v, "1") ? true : def;
            }
            // A number in [min, max] (none, such as nan, isn't); an int, digits only
            static inline double portSettingNumber(const char* name, double def, double min, double max) {
                const char* v = portSettingText(name);
                char* end = NULL;
                double d = v ? strtod(v, &end) : def;
                return v && (end == v || *end || !(d >= min && d <= max)) ? def : d;
            }
            static inline int portSettingInt(const char* name, int def, int min, int max) {
                const char* v = portSettingText(name);
                if (!v) return def;
                for (const char* c = v; *c; c++)
                    if (*c < '0' || *c > '9') return def;
                long n = strtol(v, NULL, 10);
                return n >= min && n <= max ? (int)n : def;
            }

            static inline PortSettings portSettingsRead(void) {
                PortSettings s;

            """);
        foreach (var s in Native)
            t.Append($"    s.{Camel(s.Key)} = {NativeType(s).Read};\n");
        t.Append("    return s;\n}\n");
        return t.ToString();
    }

    // A native setting's C type, and how portSettingsRead reads it
    private static (string Type, string Read) NativeType(PortSetting s)
    {
        string Range() => $"{s.Min.ToString(CultureInfo.InvariantCulture)}, {s.Max.ToString(CultureInfo.InvariantCulture)}";
        return s.Kind switch
        {
            "bool" => ("bool", $"portSettingOn(\"{s.Variable}\", {s.Default})"),
            "int" => ("int", $"portSettingInt(\"{s.Variable}\", {s.Default}, {Range()})"),
            "number" or "scale" => ("double", $"portSettingNumber(\"{s.Variable}\", {s.Default}, {Range()})"),
            "text" or "choice" or "numbers" => ("const char*", $"portSettingText(\"{s.Variable}\")"),
            _ => throw new FormatException($"{s.Key}: the native code can't read a {s.Kind}"),
        };
    }

    // The hooks' typed settings: a property of Settings per setting the hooks do
    private static string HooksSettings()
    {
        var t = new StringBuilder("""
            // The port settings the hooks read, as properties of Settings (Settings.cs): made from
            // src/shared/settings/port-settings.txt by KsaPort.Tools.SettingsGenerator
            // (src/shared/settings/README.md), which the hooks' build runs. Edit the table, not this. A setting that
            // applies when the game starts is read once, when the hooks load; a live one each time, since the Mac
            // Port tab's Apply changes it.
            using KsaPort.Common;

            namespace KsaMetal.Hooks;

            public static partial class Settings
            {

            """);
        var hooks = PortSettings.All.Where(s => By(s, "hooks")).ToList();
        foreach (var s in hooks.Where(s => s.Live))
            t.Append($"    private static PortSetting _{Camel(s.Key)} = null!;\n");
        foreach (var s in hooks)
        {
            var (type, read) = HooksType(s);
            t.Append('\n').Append(s.Live
                ? $"    public static {type} {Pascal(s.Key)} => {read}(_{Camel(s.Key)});\n"
                : $"    public static {type} {Pascal(s.Key)} {{ get; private set; }}{type switch { "string" => " = \"\";", "double[]" => " = [];", _ => "" }}\n");
        }
        t.Append("\n    private static void Bind()\n    {\n");
        foreach (var s in hooks)
            t.Append(s.Live
                ? $"        _{Camel(s.Key)} = Get(\"{s.Key}\");\n"
                : $"        {Pascal(s.Key)} = {HooksType(s).Read}(Get(\"{s.Key}\"));\n");
        t.Append("    }\n}\n");
        return t.ToString();
    }

    // A hooks setting's C# type, and the Settings method that reads it
    private static (string Type, string Read) HooksType(PortSetting s) => s.Kind switch
    {
        "bool" => ("bool", "On"),
        "int" => ("int", "Int"),
        "number" or "scale" => ("double", "Number"),
        "numbers" => ("double[]", "Numbers"),
        _ => ("string", "Text"),
    };

    // Apple's variables, which Metal reads, that the READMEs name besides the table's
    private static readonly string[] AppleVariables = ["MTL_SHADER_VALIDATION", "MTL_DEBUG_LAYER"];

    // A read of the native settings: kmtlConfig().x, cfg.x (the driver's local name for it), portSettingsRead().x (the
    // KTX shim)
    private const string ConfigRead = @"(?:\)|\bcfg)\.";

    // Code without its comments and strings, where a name is a use
    private static string Uses(string code) =>
        Regex.Replace(code, @"//[^\n]*|/\*.*?\*/|@?""(?:\\.|[^""\\\n])*""", " ", RegexOptions.Singleline);

    // The class whose body holds the code at index: the one declared before the first unmatched { back from it
    private static string? Enclosing(string code, int index)
    {
        for (int depth = 0, i = index - 1; i >= 0; i--)
        {
            if (code[i] == '}')
                depth++;
            else if (code[i] == '{' && depth-- == 0)
                return Regex.Match(code[..i], @"\bclass\s+(\w+)[^;{}]*$").Groups[1] is { Success: true } name ? name.Value : null;
        }
        return null;
    }

    // A file made from the table, written only when it differs
    private static void Write(string file, string text)
    {
        if (File.Exists(file) && File.ReadAllText(file) == text)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        File.WriteAllText(file, text);
    }

    // The checks: each setting's by column what the code does (the driver and the KTX shim use the fields of the
    // settings it names for them, the hooks the properties of those it names for them, the hook it names is a class
    // marked [Hook] that reads it, in its class's file or one of the classes it applies, or as its switch); each
    // setting named by its key in a table row's first column in the README of each component that reads it; every
    // KSA_MTL_ variable the code names one of the table's; every variable a README names in the table or in some code
    private static int Check(string repo)
    {
        Load(Path.Combine(repo, "src", "shared", "settings"));
        var problems = new List<string>();

        var readmes = Directory.EnumerateFiles(Path.Combine(repo, "src"), "README.md", SearchOption.AllDirectories)
            .Append(Path.Combine(repo, "README.md"))
            .ToDictionary(p => Path.GetRelativePath(repo, p), File.ReadAllText);
        // The code: the components' sources, and the build's scripts and CMake files, without the table and what an
        // editor's build leaves in bin/ and obj/ (the hooks' typed settings among it)
        var code = Directory.EnumerateFiles(Path.Combine(repo, "src"), "*", SearchOption.AllDirectories)
            .Where(p => !Path.GetRelativePath(repo, p).Split('/').Any(d => d is "bin" or "obj"))
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "scripts")))
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "cmake")))
            .Concat(new[] { "build.sh", "release.sh", "CMakeLists.txt" }.Select(f => Path.Combine(repo, f)))
            .Where(p => Path.GetExtension(p) is ".cs" or ".c" or ".mm" or ".cpp" or ".h" or ".sh" or ".cmake" or ".txt")
            .ToDictionary(p => Path.GetRelativePath(repo, p), File.ReadAllText)
            .Where(c => c.Key != "src/shared/settings/port-settings.txt")
            .ToDictionary(c => c.Key, c => c.Value);
        bool Names(string text, string name) => Regex.IsMatch(text, $@"\b{name}\b");
        string Component(string folder) =>
            string.Join('\n', code.Where(c => c.Key.StartsWith($"src/{folder}/") && Path.GetExtension(c.Key) is ".c" or ".mm" or ".cpp" or ".h")
                .Select(c => Uses(c.Value)));
        var native = new Dictionary<string, string>
            { ["driver"] = Component("game/driver"), ["ktx-shim"] = Component("game/shims/ktx") };
        // config.mm adjusts the driver's settings on c: a field on the right of c.x = ... is read (frame_capture turns
        // labels on), the one it sets isn't
        foreach (Match m in Regex.Matches(Uses(code["src/game/driver/setup/config.mm"]), @"\bc\.(\w+) = ([^;]*);"))
            native["driver"] += "\n" + Regex.Replace(m.Groups[2].Value, $@"\bc\.(?!{m.Groups[1].Value}\b)", "cfg.");
        // The hooks' code: their folder and the changes they apply, but the porter's code among those (port/)
        var hooks = code.Where(c => (c.Key.StartsWith("src/game/hooks/") || c.Key.StartsWith("src/game/changes/")) &&
                c.Key.EndsWith(".cs") && !c.Key.Contains("/port/"))
            .ToDictionary(c => c.Key, c => Uses(c.Value));
        string hooksCode = string.Join('\n', hooks.Values);
        // The hooks: the classes marked [Hook(...)], with the setting that switches each, if any
        var marked = Regex.Matches(hooksCode, @"\[Hook\(([^\]]*)\)\]\s*internal\s+(?:\w+\s+)*class\s+(\w+)")
            .ToDictionary(m => m.Groups[2].Value, m => Regex.Match(m.Groups[1].Value, @"Switch = nameof\(Settings\.(\w+)\)").Groups[1].Value);
        // A class with a hook's Apply but no mark never applies
        foreach (var (file, text) in hooks)
            foreach (Match m in Regex.Matches(text, @"\bstatic\s+string\s+Apply\(Harmony\b"))
                if (Enclosing(text, m.Index) is { } owner && !marked.ContainsKey(owner))
                    problems.Add($"{file}: {owner} has a hook's Apply(Harmony) but no [Hook] mark, so it never applies.");
        // A hook's code: its class's file, and those of the classes it applies (RenderScale applies Upscale)
        string HookCode(string hook)
        {
            var file = hooks.FirstOrDefault(c => Regex.IsMatch(c.Value, $@"\bclass {hook}\b")).Value ?? "";
            var applied = Regex.Matches(file, @"\b(\w+)\.Apply\(").Select(m => m.Groups[1].Value).Where(c => c != hook);
            return file + string.Concat(applied.Select(c => hooks.FirstOrDefault(h => Regex.IsMatch(h.Value, $@"\bclass {c}\b")).Value));
        }

        // The other way: a component uses only the fields of its own settings
        var fields = Native.ToDictionary(s => Camel(s.Key));
        foreach (var (component, nativeCode) in native)
            foreach (var field in Regex.Matches(nativeCode, $@"{ConfigRead}(\w+)\b").Select(m => m.Groups[1].Value).Distinct())
                if (fields.TryGetValue(field, out var s) && !By(s, component))
                    problems.Add($"{component} uses the field {field}, but {s.Key} isn't by {component} in the table.");

        foreach (var s in PortSettings.All)
        {
            foreach (var by in s.By)
            {
                if (!(Readers.ContainsKey(Reader(by)) || by == "tab" || by.StartsWith("metal:", StringComparison.Ordinal)))
                    problems.Add($"{s.Key}: by {by} is none of driver, ktx-shim, hooks, hooks:<hook>, metal:<variable>, tab.");
                else if (native.TryGetValue(by, out var nativeCode) && !Regex.IsMatch(nativeCode, $@"{ConfigRead}{Camel(s.Key)}\b"))
                    problems.Add($"{s.Key}: by {by}, but its code doesn't use the field {Camel(s.Key)}.");
                else if (Reader(by) == "hooks" && !Regex.IsMatch(hooksCode, $@"\bSettings\.{Pascal(s.Key)}\b"))
                    problems.Add($"{s.Key}: by {by}, but no hook reads Settings.{Pascal(s.Key)}.");
                if (by.StartsWith("hooks:", StringComparison.Ordinal))
                {
                    var hook = by[6..];
                    if (!marked.TryGetValue(hook, out var hookSwitch))
                        problems.Add($"{s.Key}: its hook {hook} isn't a class marked [Hook] " +
                            "(src/game/hooks/Hooks.cs).");
                    else if (!Regex.IsMatch(HookCode(hook), $@"\bSettings\.{Pascal(s.Key)}\b") && hookSwitch != Pascal(s.Key))
                        problems.Add($"{s.Key}: its hook {hook} doesn't read it, in its code or as its switch.");
                }
                if (Readers.TryGetValue(Reader(by), out var readme) &&
                    !Regex.IsMatch(readmes[readme], $@"^\|[^|\n]*`{s.Key}`[^|\n]*\|", RegexOptions.Multiline))
                    problems.Add($"{s.Key}: {readme} doesn't name it, though its code reads it.");
            }
            if (s.By.Length == 0 || s.By.Contains("tab") && s.By.Length > 1)
                problems.Add($"{s.Key}: its by column needs one entry or more, and tab alone.");
            if (s.Live && !s.By.Any(b => Reader(b) == "hooks" || b == "tab"))
                problems.Add($"{s.Key} is live, but the hooks don't read it: the tab's Apply only changes what they read.");
        }

        // Variables: the code names only the table's KSA_MTL_ ones (the build's options are in its scripts and CMake
        // files)
        var variables = PortSettings.All.Select(s => s.Variable).Where(v => v.Length > 0).ToHashSet();
        foreach (var (path, text) in code.Where(c => Path.GetExtension(c.Key) is ".cs" or ".c" or ".mm" or ".cpp" or ".h"))
            foreach (var name in Regex.Matches(text, @"\bKSA_MTL_[A-Z0-9_]*[A-Z0-9]\b").Select(m => m.Value).Distinct())
                if (!variables.Contains(name))
                    problems.Add($"{path} names {name}, which isn't a variable of the table.");
        var known = variables.Concat(AppleVariables).ToHashSet();
        foreach (var (path, text) in readmes)
            foreach (var name in Regex.Matches(text, @"\b(?:KSA|MTL)_[A-Z0-9_]*[A-Z0-9]\b").Select(m => m.Value).Distinct())
                if (!known.Contains(name) && !code.Values.Any(c => Names(c, name)))
                    problems.Add($"{path} names {name}, which neither the table nor any code has.");

        foreach (var p in problems)
            Console.Error.WriteLine($"    {p}");
        if (problems.Count > 0)
            return 1;
        Console.WriteLine($"    The port settings: {PortSettings.All.Count} settings, the code and the READMEs in step.");
        return 0;
    }
}
