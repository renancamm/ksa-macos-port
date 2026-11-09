using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using KsaPort.Common;

// The player's port settings, port-settings.toml in the game's documents folder, become the environment variables that
// KSA Metal, its shims and the hooks read (PortSettings.Plan, in the shared library, says which). It runs in the
// game's process, before the game; KSA Metal and the shims read their variables when the game first uses them, later.
// A variable already set when the game starts wins over the file.
//
// It also puts the game's output in console.log in the documents folder's logs (unless it goes to a terminal), copies
// the commented template there when the player has no port-settings.toml: a fresh install's first start, where Auto
// Memory Saving, on by default, gives this Mac's Memory rows (AutoMemorySaving) and tells the hooks of it
// (KSA_PORT_FRESH: the game's own settings, Presets.FirstStart). For the Mac Port tab it sets KSA_PORT_SETTINGS (the
// file) and KSA_PORT_OUTSIDE (the variables set already), and for Restart KSA_PORT_SET (those it set, which the new
// game sets again).
internal static class PortEnvironment
{
    [DllImport("libc", SetLastError = true)]
    private static extern int setenv(string name, string value, int overwrite);
    [DllImport("libc")]
    private static extern int isatty(int fd);
    [DllImport("libc")]
    private static extern int dup2(int from, int to);

    // Applies the settings; false when they turn the hooks off (KSA_MTL_HOOKS=0)
    public static bool Apply(string resources)
    {
        string home = Environment.GetEnvironmentVariable("HOME") ?? "";
        string documents = PortSettings.DocumentsFolder();
        if (isatty(1) == 0)
            Output(Path.Combine(documents, "logs", "console.log"));

        PortSettings.LoadTable(resources);
        PortSettings.FilePath = Path.Combine(documents, "port-settings.toml");
        // Only the start that makes the file is a first start: a game started again (Restart) inherits this game's
        // environment
        Set("KSA_PORT_FRESH", "0");
        if (!File.Exists(PortSettings.FilePath) && File.Exists(PortSettings.TemplatePath))
        {
            try
            {
                Directory.CreateDirectory(documents);
                File.Copy(PortSettings.TemplatePath, PortSettings.FilePath);
                FirstStart();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[WARN] could not write {PortSettings.FilePath}: {e.Message}");
            }
        }
        var saved = PortSettings.ReadFile(warning => Console.WriteLine($"[WARN] {warning}"));
        var plan = PortSettings.Plan(saved, Environment.GetEnvironmentVariable, home);
        foreach (var (name, value) in plan.Set)
            Set(name, value);
        Console.WriteLine($"==> port settings: {(plan.Changed.Count > 0 ? string.Join(", ", plan.Changed) : "defaults")}");
        Set("KSA_PORT_SETTINGS", PortSettings.FilePath);
        Set("KSA_PORT_SET", string.Join(',', plan.Set.Select(v => v.Name)));
        Set("KSA_PORT_OUTSIDE", string.Join(',', plan.Outside));

        // A mod loader the launcher started instead of KSA, which loads KSA itself
        string? loader = Assembly.GetEntryAssembly()?.GetName().Name is { } entry && entry != "KSA" ? entry : null;
        Console.WriteLine($"==> {DateTime.Now.ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture)}: starting " +
                          $"{Path.Combine(resources, "KSA.dll")}{(loader != null ? $" through {loader} in {AppContext.BaseDirectory.TrimEnd('/')}" : "")}");
        return PortSettings.Get("hooks") is not { } hooks ||
               hooks.FromEnvironment(Environment.GetEnvironmentVariable(hooks.Variable), out _) == "true";
    }

    // A fresh install's first start: Auto Memory Saving, on in the template, sets this Mac's Memory rows
    private static void FirstStart()
    {
        Set("KSA_PORT_FRESH", "1");
        if ((PortSettings.ReadFile().GetValueOrDefault(AutoMemorySaving.Key) ?? PortSettings.Get(AutoMemorySaving.Key)?.Default) != "true")
            return;
        var tier = AutoMemorySaving.ThisMac();
        var rows = AutoMemorySaving.Port(tier);
        PortSettings.Save(rows);
        Console.WriteLine($"==> first start: Auto Memory Saving on, {AutoMemorySaving.MemoryGb()} GB ({AutoMemorySaving.Describe(tier)}): " +
                          (rows.Count(kv => kv.Value != PortSettings.Get(kv.Key)!.Default) is var on && on == 0 ? "its rows off" : $"{on} of its {rows.Count} rows on"));
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
