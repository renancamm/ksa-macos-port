// The end of a run: its record (run.json) made from what it left (options.json, events.jsonl, frames.csv, the game's
// logs, the screenshots), then its report (report.md, report.html) and the zip to share. Both hosts call it: benchmark.sh's
// program after the game, and the Mac Port tab's runs in the game. It changes nothing it reads, so a run can be finished
// again when a summary or a check improves.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaPort.Common.Benchmark;

public static class Finish
{
    // asksMac: whether to ask macOS about this Mac (the tests' runs are of no Mac)
    public static RunRecord Run(string dir, bool asksMac = true)
    {
        // Its numbers are written the same whatever the culture of the thread it runs on (in the game, the player's)
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return Make(dir, asksMac);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    private static RunRecord Make(string dir, bool asksMac)
    {
        var options = RunOptions.Read(dir) ?? new RunOptions();
        var events = EventLog.Read(dir);
        var frames = FrameLog.Read(dir);
        var logs = Logs(dir);
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        var started = events.FirstOrDefault()?.At.ToLocalTime() ?? Directory.GetCreationTime(dir);
        var record = new RunRecord
        {
            Schema = RunRecord.CurrentSchema,
            Name = name,
            Started = started,
            Test = options.Test.Length > 0 ? options.Test : "places",
            Mode = options.Mode,
            Start = events.LastOrDefault(e => e.Type == "start")?.Text ?? "",
            Sessions = Math.Max(1, events.Select(e => e.Session).DefaultIfEmpty(1).Max()),
            Passes = Math.Max(1, events.Select(e => e.Pass).DefaultIfEmpty(1).Max()),
            LoadSeconds = events.FirstOrDefault(e => e.Type == "loaded")?.Value,
            Drawable = events.LastOrDefault(e => e.Type == "drawable")?.Text,
            Scenarios = Scenarios(events, frames),
        };
        record.Facts = Facts.Gather(dir, options.App, options.Facts, logs.FirstOrDefault().Lines ?? [], asksMac);
        var crashes = CrashReports(record.Started.ToUniversalTime());
        record.Findings = Checks.Run(new RunEvidence(dir, record, events, logs, crashes));
        record.Status = events.Any(e => e.Type == "stopped") ? Status.Stopped : record.Problems.Any() ? Status.Failed : Status.Passed;
        record.Write(dir);
        Pages.WriteReport(dir, record);
        Package(dir, crashes);
        return record;
    }

    // The scenarios as they played, in order: each part's measurings (sessions, passes) summed up from their frames and
    // merged, the screenshots of the first pass with their limits
    private static List<ScenarioRecord> Scenarios(List<RunEvent> events, List<FrameRow> frames)
    {
        var scenarios = new List<ScenarioRecord>();
        foreach (var e in events.Where(e => e.Type == "scenario" && e.Scenario != null))
        {
            if (scenarios.Any(s => s.Name == e.Scenario))
                continue;
            scenarios.Add(new ScenarioRecord
            {
                Name = e.Scenario!,
                Kind = Enum.TryParse<Kind>(e.Text, ignoreCase: true, out var kind) ? kind : Kind.Flight
            });
        }
        var rows = frames.Where(r => r.Part.Length > 0).GroupBy(r => (r.Scenario, r.Part, r.Session, r.Pass)).ToDictionary(g => g.Key, g => (IReadOnlyList<FrameRow>)g.ToList());
        foreach (var scenario in scenarios)
        {
            var parts = events.Where(e => e.Type == "open" && e.Scenario == scenario.Name).Select(e => e.Part!).Distinct().ToList();
            foreach (var part in parts)
            {
                var measurings = events.Where(e => e.Type == "open" && e.Scenario == scenario.Name && e.Part == part)
                    .Select(open => rows.TryGetValue((scenario.Name, part, open.Session, open.Pass), out var r)
                        ? Summary.Of(part, r, scenario.Kind == Kind.Timed ? open.Game : null, (long?)open.Value)
                        : null)
                    .OfType<PartRecord>().ToList();
                if (measurings.Count > 0)
                    scenario.Parts.Add(Summary.Merge(measurings));
            }
            var firstPass = events.Where(e => e.Type == "shot" && e.Scenario == scenario.Name).GroupBy(e => (e.Session, e.Pass)).FirstOrDefault();
            foreach (var shot in firstPass ?? Enumerable.Empty<RunEvent>())
                scenario.Shots.Add(new ShotRecord(shot.Part!, shot.Text!, shot.Value ?? NoiseProfile.DefaultShotLimit(scenario.Kind)));
        }
        return scenarios;
    }

    // The game's log of each session: game.log, game-2.log...
    private static List<(string Name, string[] Lines)> Logs(string dir) =>
        Directory.GetFiles(dir, "game*.log").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal)
            .Select(f => (Path.GetFileName(f), File.ReadAllLines(f))).ToList();

    // The game's crash reports since the run started
    private static List<string> CrashReports(DateTime startedUtc)
    {
        var reports = Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? "", "Library", "Logs", "DiagnosticReports");
        bool TheGame(string crash)
        {
            try
            {
                return Regex.IsMatch(File.ReadAllText(crash), "\"procName\" *: *\"dotnet\"|KSA\\.dll|StarMap");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return (Directory.Exists(reports) ? Directory.GetFiles(reports, "*.ips") : [])
            .Where(crash => File.GetCreationTimeUtc(crash) >= startedUtc && TheGame(crash)).ToList();
    }

    // <dir>/<name>.zip: what a shared run holds, with the game's crash reports of the run, the home folder's name as ~
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
                if (f is RunRecord.FileName or "report.md" or "report.html" or EventLog.FileName or FrameLog.FileName or RunOptions.FileName
                        or "settings.toml" or "profile.md" or "trace.md" ||
                    f.StartsWith("game", StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal))
                    File.WriteAllText(Path.Combine(stage, f), File.ReadAllText(file).Replace(home, "~"));
                else if (f.Contains('_') && f.EndsWith(".png", StringComparison.Ordinal))
                    Facts.Run("/usr/bin/sips", "-s", "format", "jpeg", "-s", "formatOptions", "70", "-Z", "1600", file, "--out",
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
    }
}
