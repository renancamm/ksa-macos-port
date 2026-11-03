using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KsaPortSettings;

// What is made from the table of the port settings (src/settings/port-settings.txt), and the checks that keep the rest
// of the repository in step with it (src/settings/README.md):
//
//   KsaPortSettingsGenerator template <table>   the commented template of port-settings.toml, to standard output
//   KsaPortSettingsGenerator generate <repo>    port-settings.h written again when the table changed, then the checks
//   KsaPortSettingsGenerator check <repo>       the checks, with port-settings.h as it is: stale counts as a problem
//
// Exits with 1 when a check fails.
internal static class Generate
{
    private const string HeaderName = "port-settings.h";

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
                case ["generate" or "check", var repo]:
                    return Check(Path.GetFullPath(repo), write: args[0] == "generate");
                default:
                    Console.Error.WriteLine("usage: KsaPortSettingsGenerator template <table> | generate <repo> | check <repo>");
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
    // help as a comment, at its default; [developer]'s settings commented out
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
        foreach (var (key, _, caption) in PortSettings.Sections)
        {
            t.Append($"\n[{key}]\n");
            if (caption.Length > 0)
                t.Append('\n').Append(Comment(caption));
            if (key == "developer")
                t.Append('\n').Append(Comment("For work on the port, and for testing a problem: the driver's README in the " +
                    "port's repository (src/ksa-metal/) explains them. Remove the # in front of a line to use it."));
            foreach (var s in PortSettings.All.Where(s => s.Section == key))
            {
                var help = s.Help + string.Concat(s.OptionHelp.Select(o => $" \"{o.Key}\": {o.Value}"));
                t.Append('\n').Append(Comment($"{s.Key} ({Values(s.Type)}): {help}"));
                t.Append(key == "developer" ? "# " : "").Append($"{s.Key} = {TomlValue(s)}\n");
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

    // The header the native code reads its settings' variables from
    private static string Header()
    {
        var t = new StringBuilder("""
            // The port settings that set a variable, for the native code (KSA Metal, the KTX shim): made from
            // port-settings.txt by KsaPortSettingsGenerator (README.md in this folder), which build.sh runs. Edit the
            // table, not this.
            //
            // PORT_SETTING_<KEY> is the setting's variable and its default, as the readers take them:
            //   bool          "<variable>", <true | false>
            //   int, number   "<variable>", <default>
            //   others        "<variable>"
            #pragma once

            #include <stdbool.h>

            """).Append('\n');
        foreach (var s in PortSettings.All.Where(s => s.Variable.Length > 0))
        {
            var value = s.Kind switch
            {
                "bool" => $", {s.Default}",
                "int" or "number" => $", {(s.Default.Length > 0 ? s.Default : "0")}",
                _ => "",
            };
            t.Append($"#define {Macro(s)} \"{s.Variable}\"{value}\n");
        }
        return t.ToString();
    }

    // Apple's variables, which Metal reads, that the READMEs name besides the table's
    private static readonly string[] AppleVariables = ["MTL_SHADER_VALIDATION", "MTL_DEBUG_LAYER"];

    private static string Macro(PortSetting s) => "PORT_SETTING_" + s.Key.ToUpperInvariant();

    // What a by column's entry names: a component that reads the setting, with the README that documents it
    private static readonly Dictionary<string, string> Readers = new()
    {
        ["driver"] = "src/ksa-metal/README.md", ["ktx-shim"] = "src/ktx-shim/README.md", ["hooks"] = "src/hooks/README.md",
    };

    private static string Reader(string by) => by.StartsWith("hooks:", StringComparison.Ordinal) ? "hooks" : by;

    // The checks: port-settings.h made from the table; each setting's by column what the code does (the driver and
    // the KTX shim read the settings it names through the header and no others; the hooks read the settings it names,
    // and run the hooks it names); each setting named by its key in a table row's first column in the README of each
    // component that reads it; every KSA_MTL_ variable the code names one of the table's; every variable a README
    // names in the table or in some code
    private static int Check(string repo, bool write)
    {
        string folder = Path.Combine(repo, "src", "settings");
        Load(folder);
        var problems = new List<string>();

        string headerPath = Path.Combine(folder, HeaderName), header = Header();
        string current = File.Exists(headerPath) ? File.ReadAllText(headerPath) : "";
        if (current != header)
        {
            if (write)
            {
                File.WriteAllText(headerPath, header);
                Console.WriteLine($"    src/settings/{HeaderName} made again from the table: commit it with the table.");
            }
            else
            {
                problems.Add($"src/settings/{HeaderName} is not what the table makes: run ./build.sh and commit it.");
            }
        }

        var readmes = Directory.EnumerateFiles(Path.Combine(repo, "src"), "README.md", SearchOption.AllDirectories)
            .Append(Path.Combine(repo, "README.md"))
            .ToDictionary(p => Path.GetRelativePath(repo, p), File.ReadAllText);
        // The code: the components' sources, and the build's scripts and CMake files
        var code = Directory.EnumerateFiles(Path.Combine(repo, "src"), "*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "scripts")))
            .Concat(Directory.EnumerateFiles(Path.Combine(repo, "cmake")))
            .Concat(new[] { "build.sh", "release.sh", "CMakeLists.txt" }.Select(f => Path.Combine(repo, f)))
            .Where(p => Path.GetExtension(p) is ".cs" or ".c" or ".mm" or ".cpp" or ".h" or ".sh" or ".cmake" or ".txt" &&
                !p.EndsWith("port-settings.txt") && !p.EndsWith(HeaderName))
            .ToDictionary(p => Path.GetRelativePath(repo, p), File.ReadAllText);
        bool Names(string text, string name) => Regex.IsMatch(text, $@"\b{name}\b");
        string[] In(string component) => code.Where(c => c.Key.StartsWith($"src/{component}/")).Select(c => c.Value).ToArray();

        // The settings each component reads
        var native = new Dictionary<string, HashSet<string>>();
        foreach (var (component, folderName) in new[] { ("driver", "ksa-metal"), ("ktx-shim", "ktx-shim") })
            native[component] = In(folderName).SelectMany(c => Regex.Matches(c, @"\bPORT_SETTING_([A-Z0-9_]+)\b"))
                .Select(m => m.Groups[1].Value.ToLowerInvariant()).ToHashSet();
        var hooksCode = code.Where(c => c.Key.StartsWith("src/hooks/") && c.Key.EndsWith(".cs")).Select(c => c.Value).ToArray();
        var hooksRead = hooksCode.SelectMany(c => Regex.Matches(c, @"\b(?:On|Value|Number|Numbers|Get)\(""([a-z_0-9]+)""\)"))
            .Select(m => m.Groups[1].Value).ToHashSet();
        var hooksRun = Regex.Matches(code["src/hooks/Hooks.cs"], @"\bRun\((\w+)\.Apply\b").Select(m => m.Groups[1].Value).ToHashSet();

        foreach (var s in PortSettings.All)
        {
            foreach (var by in s.By)
            {
                if (!(Readers.ContainsKey(Reader(by)) || by == "tab" || by.StartsWith("metal:", StringComparison.Ordinal)))
                    problems.Add($"{s.Key}: by {by} is none of driver, ktx-shim, hooks, hooks:<hook>, metal:<variable>, tab.");
                else if (native.TryGetValue(by, out var read) && !read.Contains(s.Key))
                    problems.Add($"{s.Key}: by {by}, but its code doesn't read {Macro(s)}.");
                else if (Reader(by) == "hooks" && !hooksRead.Contains(s.Key))
                    problems.Add($"{s.Key}: by {by}, but no hook reads it.");
                if (by.StartsWith("hooks:", StringComparison.Ordinal) && !hooksRun.Contains(by[6..]))
                    problems.Add($"{s.Key}: its hook {by[6..]} isn't one Hooks.Apply runs (src/hooks/Hooks.cs).");
                if (Readers.TryGetValue(Reader(by), out var readme) &&
                    !Regex.IsMatch(readmes[readme], $@"^\|[^|\n]*`{s.Key}`[^|\n]*\|", RegexOptions.Multiline))
                    problems.Add($"{s.Key}: {readme} doesn't name it, though its code reads it.");
            }
            if (s.By.Length == 0 || s.By.Contains("tab") && s.By.Length > 1)
                problems.Add($"{s.Key}: its by column needs one entry or more, and tab alone.");
            if (s.Live && !s.By.Any(b => Reader(b) == "hooks" || b == "tab"))
                problems.Add($"{s.Key} is live, but the hooks don't read it: the tab's Apply only changes what they read.");
        }
        foreach (var (component, read) in native)
            foreach (var key in read.Where(k => PortSettings.Get(k) is not { } s || !s.By.Contains(component)))
                problems.Add($"{component} reads PORT_SETTING_{key.ToUpperInvariant()}, but {key} isn't by {component} in the table.");
        foreach (var key in hooksRead.Where(k => PortSettings.Get(k) is not { } s || !s.By.Any(b => Reader(b) == "hooks" || b == "tab")))
            problems.Add($"the hooks read {key}, which isn't by hooks in the table.");

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
        Console.WriteLine($"    The port settings: {PortSettings.All.Count} settings, {HeaderName} current, the code and the READMEs in step.");
        return 0;
    }
}
