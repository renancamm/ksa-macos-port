// The end of a run (the finish command): what the game's log says that system.txt lacks, the report, screenshots that
// show a broken picture, the game's crash reports, and the zip to share.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using KsaPort.Common;

namespace KsaMetalBenchmarkReport;

public static partial class Program
{
    // After the run: what the launcher and the hooks said (from the first session's log), the report, and the zip
    private static int Finish(string dir)
    {
        var system = Path.Combine(dir, "system.txt");
        // The run started when its folder was made: crash reports made since then are the run's
        var started = Directory.GetCreationTimeUtc(dir);
        var have = KeyValues(system).Select(kv => kv.key).ToHashSet();
        var log = Path.Combine(dir, "game.log");
        var lines = File.Exists(log) ? File.ReadAllLines(log) : [];
        var added = new List<string>();
        if (!have.Contains("port settings"))
            added.Add($"port settings: {lines.Where(l => l.StartsWith("==> port settings: ", StringComparison.Ordinal)).Select(l => l[19..]).FirstOrDefault() ?? "?"}");
        if (!have.Contains("Hooks"))
        {
            var hooks = lines.Where(l => l.StartsWith("[hooks] ", StringComparison.Ordinal)).Select(l => l[8..]).ToList();
            added.Add($"Hooks: {(hooks.Count > 0 ? string.Join("; ", hooks) : "none")}");
        }
        File.AppendAllLines(system, added);
        BlankShots(dir);
        // What macOS wrote about a crash of the game, for the report to name
        var crashes = CrashReports(started);
        var known = KeyValues(Path.Combine(dir, "run.txt")).Where(kv => kv.key == "Crash report").Select(kv => kv.value).ToHashSet();
        File.AppendAllLines(Path.Combine(dir, "run.txt"), crashes.Select(Path.GetFileName).OfType<string>().Where(c => !known.Contains(c)).Select(c => $"Crash report: {c}"));
        int status = Report(dir);
        Package(dir, crashes);
        return status;
    }

    // Screenshots that show something went wrong with no other sign of it, which a comparison would only catch against a run
    // that had a picture: one colour from edge to edge, or mostly pure white. For Problems: blank.txt
    private const double BlownOutPercent = 30;

    private static void BlankShots(string dir)
    {
        var blank = Directory.GetFiles(dir, "*_*.png").OrderBy(f => f, StringComparer.Ordinal)
            .AsParallel().AsOrdered().Select(f => (Name: Path.GetFileName(f), Image: Images.Read(f)))
            .Select(s => s.Image == null ? null : Images.Blank(s.Image) ? $"{s.Name}: the screenshot is one colour from edge to edge"
                : Images.WhitePercent(s.Image) is >= BlownOutPercent and var white ? $"{s.Name}: {white:F0} % of the screenshot is pure white: the picture went wrong" : null)
            .OfType<string>().ToList();
        var file = Path.Combine(dir, "blank.txt");
        if (blank.Count > 0)
            File.WriteAllLines(file, blank);
        else
            File.Delete(file);
    }

    // The game's crash reports since the run started
    private static List<string> CrashReports(DateTime started)
    {
        var reports = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library", "Logs", "DiagnosticReports");
        bool TheGame(string crash)
        {
            try
            {
                return Regex.IsMatch(File.ReadAllText(crash), "\"procName\" *: *\"dotnet\"|KSA\\.dll|StarMap");
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }
        return (Directory.Exists(reports) ? Directory.GetFiles(reports, "*.ips") : [])
            .Where(crash => File.GetCreationTimeUtc(crash) >= started && TheGame(crash)).ToList();
    }

    // <dir>/<name>.zip: what a shared run holds, with the game's crash reports of the run
    private static void Package(string dir, List<string> crashes)
    {
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        var zip = Path.Combine(dir, name + ".zip");
        var home = Environment.GetEnvironmentVariable("HOME") ?? "";
        var stage = Path.Combine(Path.GetTempPath(), $"ksa-metal-benchmark-{Environment.ProcessId}", name);
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in Directory.GetFiles(dir))
            {
                var f = Path.GetFileName(file);
                if (f is "report.md" or "report.html" or "report.json" or "system.txt" or "run.txt" or "blank.txt" or "settings.toml" or "profile.md" ||
                    f.StartsWith("game", StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal) ||
                    f.StartsWith("frames", StringComparison.Ordinal) && f.EndsWith(".csv", StringComparison.Ordinal) ||
                    f.StartsWith("results", StringComparison.Ordinal) && f.EndsWith(".json", StringComparison.Ordinal))
                    File.Copy(file, Path.Combine(stage, f));
                else if (f.Contains('_') && f.EndsWith(".png", StringComparison.Ordinal))
                    Run("/usr/bin/sips", "-s", "format", "jpeg", "-s", "formatOptions", "70", "-Z", "1600", file, "--out",
                        Path.Combine(stage, Path.ChangeExtension(f, ".jpg")));
            }
            var user = Environment.UserName;
            foreach (var crash in crashes)
            {
                Directory.CreateDirectory(Path.Combine(stage, "crashes"));
                File.WriteAllText(Path.Combine(stage, "crashes", Path.GetFileName(crash)),
                    File.ReadAllText(crash).Replace(home, "~").Replace($"/Users/{user}", "~"));
            }
            File.Delete(zip);
            ZipFile.CreateFromDirectory(Path.GetDirectoryName(stage)!, zip);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(stage)!, recursive: true);
        }
        Console.WriteLine(zip);
    }
}
