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
            //   bool          "<variable>", <true | false>; with an on value of its own: "<variable>", "<on value>"
            //   int, number   "<variable>", <default>
            //   others        "<variable>"
            #pragma once

            #include <stdbool.h>

            """).Append('\n');
        foreach (var s in PortSettings.All.Where(s => s.Variable.Length > 0))
        {
            var value = s.Kind switch
            {
                "bool" when s.OnValue != "1" => s.Default == "false"
                    ? $", \"{s.OnValue}\""
                    : throw new FormatException($"{s.Key}: a setting with an on value of its own must be off by default"),
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

    // The hooks against the table: the hook column names hooks that Hooks.Apply runs (otherwise the tab couldn't show
    // the setting as unavailable), every key the hooks read is in the table (Settings.Load would throw, and no hook
    // would apply), and every live setting with a variable is read by them (the tab's Apply only changes what they read)
    private static void HooksCheck(Dictionary<string, string> code, List<string> problems)
    {
        var hooks = Regex.Matches(code["src/hooks/Hooks.cs"], @"Run\(""[^""]+"", ""([a-z_]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var s in PortSettings.All.Where(s => s.Hook.Length > 0 && !hooks.Contains(s.Hook)))
            problems.Add($"{s.Key}: its hook {s.Hook} isn't one Hooks.Apply runs (src/hooks/Hooks.cs).");
        var read = code.Where(c => c.Key.StartsWith("src/hooks/") && c.Key.EndsWith(".cs"))
            .SelectMany(c => Regex.Matches(c.Value, @"(?:Settings\.(?:Value|On|Number|Numbers|Get)|\bGet)\(""([a-z_0-9]+)""\)"))
            .Select(m => m.Groups[1].Value).ToHashSet();
        foreach (var key in read.Where(k => PortSettings.Get(k) == null))
            problems.Add($"the hooks read {key}, which isn't in the table.");
        foreach (var s in PortSettings.All.Where(s => s.Live && s.Variable.Length > 0 && !read.Contains(s.Key)))
            problems.Add($"{s.Key} is live, but no hook reads it.");
    }

    // The checks: port-settings.h made from the table; every variable the table has named in a README, the driver's,
    // the KTX shim's and the hooks' in their own README when their code reads it; every variable a README names in the
    // table or named by some code
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
                !p.EndsWith("port-settings.txt"))
            .ToDictionary(p => Path.GetRelativePath(repo, p), File.ReadAllText);
        var variables = PortSettings.All.Where(s => s.Variable.Length > 0).ToList();
        bool Names(string text, string variable) => Regex.IsMatch(text, $@"\b{variable}\b");

        foreach (var s in variables)
        {
            if (!readmes.Values.Any(r => Names(r, s.Variable)))
                problems.Add($"{s.Key}: no README names {s.Variable}.");
            // Read by native code through the header, or by the hooks by its key
            var readers = new List<string>();
            foreach (var component in new[] { "ksa-metal", "ktx-shim" })
                if (code.Any(c => c.Key.StartsWith($"src/{component}/") && Regex.IsMatch(c.Value, $@"\b{Macro(s)}\b")))
                    readers.Add($"src/{component}/README.md");
            if (code.Any(c => c.Key.StartsWith("src/hooks/") && c.Key.EndsWith(".cs") &&
                    Regex.IsMatch(c.Value, $@"\b(?:Settings\.)?(?:On|Value|Number|Numbers|Get)\(\s*""{s.Key}""")))
                readers.Add("src/hooks/README.md");
            foreach (var readme in readers.Where(r => !Names(readmes[r], s.Variable)))
                problems.Add($"{s.Key}: {readme} doesn't name {s.Variable}, which its code reads.");
        }
        HooksCheck(code, problems);
        var known = variables.Select(s => s.Variable).Concat(AppleVariables).ToHashSet();
        foreach (var (path, text) in readmes)
            foreach (var name in Regex.Matches(text, @"\b(?:KSA|MTL)_[A-Z0-9_]*[A-Z0-9]\b").Select(m => m.Value).Distinct())
                if (!known.Contains(name) && !code.Values.Any(c => Names(c, name)))
                    problems.Add($"{path} names {name}, which neither the table nor any code has.");

        foreach (var p in problems)
            Console.Error.WriteLine($"    {p}");
        if (problems.Count > 0)
            return 1;
        Console.WriteLine($"    The port settings: {PortSettings.All.Count} settings, {HeaderName} current, the READMEs in step.");
        return 0;
    }
}
