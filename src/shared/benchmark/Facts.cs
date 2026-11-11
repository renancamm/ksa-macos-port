// What a run's record says about the Mac, the app and the run: macOS's answers, the app's Info.plist, what the run's
// host gave (RunPlan.Host.Facts: the test, the settings, the tools...), what the game's log says the port ran with, and
// the window from the run's settings.toml. Asked once the run has ended, so that the asking isn't measured.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaPort.Common.Benchmark;

public static class Facts
{
    // The host's facts in this order, and what one it leaves out means (null: left out)
    private static readonly (string Key, string? Default)[] RunFacts =
    [
        ("StarMap", "none"), ("System", null), ("Test", null), ("Scenarios", null), ("Mode", null), ("Preset", "none, own settings"),
        ("Settings changed", "none"), ("Start", null), ("KSA Metal switches", "none"), ("Tools", "none"),
        ("port settings", null), ("Hooks", null),
    ];

    // hooks: what the hooks applied, as the game said it when the world loaded (its hooks event), with the log's hooks that
    // failed after it; else the log's hooks lines (a run of before)
    public static List<Fact> Gather(string dir, string app, IReadOnlyList<Fact> given, IReadOnlyList<string> log, bool asksMac = true,
        string? hooks = null)
    {
        var facts = asksMac ? Mac(app) : [];
        var settings = File.Exists(Path.Combine(RunFiles.Data(dir), "settings.toml")) ? File.ReadAllLines(Path.Combine(RunFiles.Data(dir), "settings.toml")) : [];
        string Setting(string key) => settings.Select(l => l.Trim()).Where(l => l.StartsWith(key + " = ", StringComparison.Ordinal))
            .Select(l => l[(key.Length + 3)..].Trim('"')).FirstOrDefault() ?? "?";
        // What the port ran with, from the game's log when the host didn't say
        var hookLines = log.Where(l => l.StartsWith("[hooks] ", StringComparison.Ordinal)).Select(l => l[8..]).ToList();
        if (hooks is { Length: > 0 })
        {
            var said = hooks.Split("; ");
            var failed = hookLines.Where(l => l.Split(": ", 2) is [_, var status] && status.StartsWith("failed", StringComparison.Ordinal) && !said.Contains(l));
            hooks = string.Join("; ", said.Concat(failed));
        }
        var fromLog = new List<Fact>
        {
            new("port settings", log.Where(l => l.StartsWith("==> port settings: ", StringComparison.Ordinal)).Select(l => l[19..]).FirstOrDefault() ?? "?"),
            new("Hooks", hooks is { Length: > 0 } ? hooks : hookLines.Count > 0 ? string.Join("; ", hookLines) : "none"),
        };
        foreach (var (key, fallback) in RunFacts)
        {
            if (given.FirstOrDefault(f => f.Key == key) is { } fact)
                facts.Add(fact);
            else if (fromLog.FirstOrDefault(f => f.Key == key) is { } logged)
                facts.Add(logged);
            else if (fallback != null)
                facts.Add(new(key, fallback));
        }
        facts.AddRange(given.Where(f => !RunFacts.Any(r => r.Key == f.Key)));
        facts.Add(new("Window", $"{Setting("width")} x {Setting("height")}, {Setting("mode")}, on {Setting("screenName")}"));
        facts.Add(new("Vsync", $"{Setting("vsync")}, FPS limit {Setting("fpslimit")}"));
        facts.Add(new("Anti-aliasing", Setting("antialiasing")));
        return facts;
    }

    // This Mac and the app, as macOS and the app's Info.plist say
    private static List<Fact> Mac(string app)
    {
        string Plist(string key) => Run("/usr/libexec/PlistBuddy", "-c", $"Print :{key}", Path.Combine(app, "Contents", "Info.plist")).Trim();
        var displays = Run("/usr/sbin/system_profiler", "SPDisplaysDataType");
        string Display(string key) => string.Join("; ", displays.Split('\n').Select(l => l.Trim())
            .Where(l => l.StartsWith(key + ": ", StringComparison.Ordinal)).Select(l => l[(key.Length + 2)..]));
        long memory = long.TryParse(Sysctl("hw.memsize"), out var bytes) ? bytes / 1073741824 : 0;
        return
        [
            new("Mac", Sysctl("hw.model")),
            new("Chip", Sysctl("machdep.cpu.brand_string")),
            new("CPU cores", $"{Sysctl("hw.perflevel0.physicalcpu")} performance, {(Sysctl("hw.perflevel1.physicalcpu") is { Length: > 0 } e ? e : "0")} efficiency"),
            new("GPU cores", Display("Total Number of Cores").Split(';')[0]),
            new("Memory", $"{memory} GB"),
            new("macOS", $"{Run("/usr/bin/sw_vers", "-productVersion").Trim()} ({Run("/usr/bin/sw_vers", "-buildVersion").Trim()})"),
            new("Displays", Display("Resolution")),
            new("Power", Regex.Match(Run("/usr/bin/pmset", "-g", "batt"), "'(.*)'").Groups[1].Value),
            new("Low Power Mode", Regex.Match(Run("/usr/bin/pmset", "-g"), @"lowpowermode\s+(\d)").Groups[1].Value == "1" ? "on" : "off"),
            new("KSA", Plist("CFBundleShortVersionString")),
            new("Port", $"{Plist("KSAPortVersion")} ({Plist("KSAPortCommit")})"),
        ];
    }

    private static string Sysctl(string name) => Run("/usr/sbin/sysctl", "-n", name).Trim();

    // A program's output (empty when it fails)
    public static string Run(string program, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments)
                start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Result : "";
        }
        catch (Exception)
        {
            return "";
        }
    }
}
