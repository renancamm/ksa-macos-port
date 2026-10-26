using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using KsaMetalHooks;

// The player's port settings, port-settings.toml in the game's documents folder, become the environment variables that
// KSA Metal, its shims and the hooks read: the table (Contents/Resources/port-settings.txt) says which setting is which
// variable. It runs in the game's process, before the game; KSA Metal and the shims read their variables when the game
// first uses them, later. A variable already set when the game starts wins over the file.
//
// It also puts the game's output in console.log in the documents folder's logs (unless it goes to a terminal), copies
// the commented template there when the player has no port-settings.toml. For the Mac Port tab it sets
// KSA_PORT_SETTINGS (the file) and KSA_PORT_OUTSIDE (the variables set already), and for Restart KSA_PORT_SET (those
// it set, which the new game sets again).
internal static class PortEnvironment
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);
    [DllImport("libc")]
    private static extern int isatty(int fd);
    [DllImport("libc")]
    private static extern int dup2(int from, int to);

    // Applies the settings; false when they turn the hooks off (KSA_HOOKS=0)
    public static bool Apply(string resources)
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        string documents = PortSettings.DocumentsFolder();
        if (isatty(1) == 0)
            Output(Path.Combine(documents, "logs", "console.log"));

        PortSettings.LoadTable(resources);
        PortSettings.FilePath = Path.Combine(documents, "port-settings.toml");
        if (!File.Exists(PortSettings.FilePath) && File.Exists(PortSettings.TemplatePath))
        {
            try
            {
                Directory.CreateDirectory(documents);
                File.Copy(PortSettings.TemplatePath, PortSettings.FilePath);
            }
            catch (Exception e)
            {
                Console.WriteLine($"[WARN] could not write {PortSettings.FilePath}: {e.Message}");
            }
        }
        var saved = PortSettings.ReadFile(warning => Console.WriteLine($"[WARN] {warning}"));
        var plan = Plan(saved, Environment.GetEnvironmentVariable, home);
        foreach (var (name, value) in plan.Set)
            Set(name, value);
        Console.WriteLine($"==> port settings: {(plan.Changed.Count > 0 ? string.Join(", ", plan.Changed) : "defaults")}");
        Set("KSA_PORT_SETTINGS", PortSettings.FilePath);
        Set("KSA_PORT_SET", string.Join(',', plan.Set.Select(v => v.Name)));
        Set("KSA_PORT_OUTSIDE", string.Join(',', plan.Outside));

        bool starMap = File.Exists(Path.Combine(AppContext.BaseDirectory, "StarMap.dll"));
        Console.WriteLine($"==> {DateTime.Now.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture)}: starting " +
                          $"{Path.Combine(resources, "KSA.dll")}{(starMap ? $" through StarMap in {AppContext.BaseDirectory.TrimEnd('/')}" : "")}");
        return Environment.GetEnvironmentVariable("KSA_HOOKS") != "0";
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
        var outside = PortSettings.All.Select(s => s.Variable).Where(v => v.Length > 0 && env(v) != null).Distinct().ToList();
        foreach (var s in PortSettings.All)
        {
            var value = Value(s);
            if (value == s.Default)
                continue;
            if (!PortSettings.Met(s, Value, _ => true))
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
            set.Add((s.Variable, s.Kind switch
            {
                "bool" or "available" => value == "false" ? "0" : s.OnValue,
                "text" when value.StartsWith('~') => home + value[1..],
                _ => value
            }));
            changed.Add($"{s.Key} = {value}");
        }
        return (set, changed, outside);
    }

    // Sets a variable for both: .NET code reads its own copy of the environment, KSA Metal and the shims read the
    // process's
    private static void Set(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        setenv(name, value, 1);
    }

    // Standard output and error to this file, from now on
    private static void Output(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var log = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            int fd = (int)log.SafeFileHandle.DangerousGetHandle();
            dup2(fd, 1);
            dup2(fd, 2);
        }
        catch (Exception e)
        {
            Console.WriteLine($"[WARN] could not write {path}: {e.Message}");
        }
    }
}
