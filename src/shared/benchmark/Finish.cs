// The end of a run: its record (run.json) made from what it left (options.json, events.jsonl, frames.csv, the game's
// logs, the screenshots), checked against the run's store (Store.cs), then its report (report.md, report.html) and the
// zip to share. Both hosts call it: benchmark.sh's
// program after the game, and the Mac Port tab's runs in the game. It changes nothing it reads, so a run can be finished
// again when a summary or a check improves.
using System;
using System.Collections.Generic;
using System.Globalization;
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
        // This process's rows of the run's store into its files, before anything reads them
        RunStore.FlushAll();
        var options = RunOptions.Read(dir) ?? new RunOptions();
        var events = EventLog.Read(dir);
        var frames = FrameLog.Read(dir);
        var logs = Logs(dir);
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        var record = Core(dir, name, options, events, frames);
        record.Facts = Facts.Gather(dir, options.App, options.Facts, logs.FirstOrDefault().Lines ?? [], asksMac);
        if (record.WarmUp is { } warmUp)
            record.Facts.Add(new Fact("Warm-up", warmUp.GpuMs is { } gpu
                ? $"a pass first, left out (GPU {gpu:F1} ms a frame)" : "a pass first, left out"));
        record.Facts.AddRange(SideMemory(events));
        var crashes = CrashReports(record.Started.ToUniversalTime());
        record.Findings = Checks.Run(new RunEvidence(dir, record, events, logs, crashes));
        if (StoreDiffers(dir, name, options, record, events) is { } where)
            record.Findings.Add(new Finding(Severity.Note, "store", $"the run's store says otherwise than its files about {where}"));
        record.Status = events.Any(e => e.Type == "stopped") ? Status.Stopped : record.Problems.Any() ? Status.Failed : Status.Passed;
        record.Write(dir);
        Pages.WriteReport(dir, record);
        Package(dir, crashes);
        // The game's store of a tab's run, and benchmark.sh's own, closed: the run is over
        RunStore.Close(dir);
        return record;
    }

    // What the record says from the run's events and frames: how it played, and each part's numbers
    private static RunRecord Core(string dir, string name, RunOptions options, List<RunEvent> events, List<FrameRow> frames)
    {
        var record = new RunRecord
        {
            Schema = RunRecord.CurrentSchema,
            Name = name,
            Started = events.FirstOrDefault()?.At.ToLocalTime() ?? Directory.GetCreationTime(dir),
            Test = options.Test.Length > 0 ? options.Test : "places",
            Mode = options.Mode,
            Start = events.LastOrDefault(e => e.Type == "start")?.Text ?? "",
            Sessions = Math.Max(1, events.Select(e => e.Session).DefaultIfEmpty(1).Max()),
            Passes = Math.Max(1, events.Select(e => e.Pass).DefaultIfEmpty(1).Max()),
            LoadSeconds = events.FirstOrDefault(e => e.Type == "loaded")?.Value,
            Drawable = events.LastOrDefault(e => e.Type == "drawable")?.Text,
            Scenarios = Scenarios(events, frames),
        };
        SetLimits(record, options);
        if (events.FirstOrDefault(e => e.Type == "warmed") is { } warmed)
            record.WarmUp = WarmUp(record, frames, warmed.Session);
        return record;
    }

    // The run's store (Store.cs) against the files the record is made from: what the record would say from each, compared;
    // the first part that differs, or null when they agree or the run has no store. A game that crashed, hung or was
    // killed leaves each with what it had written by then (the store commits four times a second, frames.csv every
    // second): such a run isn't compared
    private static string? StoreDiffers(string dir, string name, RunOptions options, RunRecord fromFiles, List<RunEvent> events)
    {
        if (events.Any(e => e.Type == "ended" && e.Text is not ("quit normally" or "kept running")) || RunStore.Read(dir) is not { } stored)
            return null;
        var fromStore = Core(dir, name, options, stored.Events, stored.Frames);
        static string Json(object? o) => System.Text.Json.JsonSerializer.Serialize(o);
        var parts = new (string What, Func<RunRecord, object?> Of)[]
        {
            ("its start", r => new { r.Started, r.Start }), ("its sessions and passes", r => new { r.Sessions, r.Passes }),
            ("its loading", r => new { r.LoadSeconds, r.Drawable }), ("its warm-up", r => r.WarmUp),
        };
        foreach (var (what, of) in parts)
            if (Json(of(fromFiles)) != Json(of(fromStore)))
                return what;
        if (fromFiles.Scenarios.Count != fromStore.Scenarios.Count)
            return "its scenarios";
        foreach (var (a, b) in fromFiles.Scenarios.Zip(fromStore.Scenarios))
            if (Json(a) != Json(b))
                return $"scenario {a.Name}";
        return null;
    }

    // An A/B run's memory on each side, from its sessions: macOS's worst memory pressure and the swap the Mac grew while
    // they played ("While A played", "While B played"). A session whose swap couldn't be read counts its pressure
    private static IEnumerable<Fact> SideMemory(List<RunEvent> events)
    {
        var variants = events.Where(e => e.Type == "session" && e.Text is { Length: > 0 }).GroupBy(e => e.Session)
            .ToDictionary(g => g.Key, g => g.First().Text!);
        foreach (var side in variants.Values.Distinct().Order())
        {
            string worst = "normal";
            double grew = 0;
            int sessions = 0;
            foreach (var e in events.Where(e => e.Type == "session-memory" && variants.GetValueOrDefault(e.Session) == side))
            {
                var m = Regex.Match(e.Text ?? "", @"pressure (\w+), swap (\S+) -> (\S+) MB");
                if (!m.Success)
                    continue;
                sessions++;
                if (m.Groups[1].Value == "critical" || m.Groups[1].Value == "warning" && worst == "normal")
                    worst = m.Groups[1].Value;
                if (double.TryParse(m.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var before) &&
                    double.TryParse(m.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var after))
                    grew += Math.Max(0, after - before);
            }
            if (sessions > 0)
                yield return new Fact($"While {side} played", FormattableString.Invariant(
                    $"worst memory pressure {worst}, swap grew {grew:F0} MB in {sessions} game session{(sessions == 1 ? "" : "s")}"));
        }
    }

    // The scenarios as they played, in order: each part's measurings (sessions, passes, but the warm-up's) summed up from
    // their frames and merged, the screenshots of the first pass (a warm-up's too) with their limits. An A/B run's
    // scenarios are there once for each side, the sessions of each merged
    private static List<ScenarioRecord> Scenarios(List<RunEvent> events, List<FrameRow> frames)
    {
        var variants = events.Where(e => e.Type == "session").GroupBy(e => e.Session)
            .ToDictionary(g => g.Key, g => g.First().Text is { Length: > 0 } v ? v : null);
        string? VariantOf(int session) => variants.GetValueOrDefault(session);
        var scenarios = new List<ScenarioRecord>();
        foreach (var e in events.Where(e => e.Type == "scenario" && e.Scenario != null))
        {
            var variant = VariantOf(e.Session);
            if (scenarios.Any(s => s.Name == e.Scenario && s.Variant == variant))
                continue;
            scenarios.Add(new ScenarioRecord
            {
                Name = e.Scenario!, Variant = variant,
                Kind = Enum.TryParse<Kind>(e.Text, ignoreCase: true, out var kind) ? kind : Kind.Flight
            });
        }
        var rows = frames.Where(r => r.Part.Length > 0).GroupBy(r => (r.Scenario, r.Part, r.Session, r.Pass)).ToDictionary(g => g.Key, g => (IReadOnlyList<FrameRow>)g.ToList());
        foreach (var scenario in scenarios)
        {
            var opens = events.Where(e => e.Type == "open" && e.Scenario == scenario.Name && e.Pass > 0 && VariantOf(e.Session) == scenario.Variant).ToList();
            foreach (var part in opens.Select(e => e.Part!).Distinct().ToList())
            {
                var measurings = opens.Where(e => e.Part == part)
                    .Select(open => rows.TryGetValue((scenario.Name, part, open.Session, open.Pass), out var r)
                        ? Summary.Of(part, r, scenario.Kind == Kind.Timed ? open.Game : null, (long?)open.Value)
                        : null)
                    .OfType<PartRecord>().ToList();
                if (measurings.Count > 0)
                    scenario.Parts.Add(Summary.Merge(measurings));
            }
            var firstPass = events.Where(e => e.Type == "shot" && e.Scenario == scenario.Name && VariantOf(e.Session) == scenario.Variant)
                .GroupBy(e => (e.Session, e.Pass)).FirstOrDefault();
            foreach (var shot in firstPass ?? Enumerable.Empty<RunEvent>())
                scenario.Shots.Add(new ShotRecord(shot.Part!, shot.Text!, shot.Value ?? NoiseProfile.DefaultShotLimit(scenario.Kind)));
        }
        return scenarios;
    }

    // The warm-up pass's GPU time against the measured passes' in the game it warmed, both as the headline weighs its
    // parts
    private static WarmUpRecord WarmUp(RunRecord record, List<FrameRow> frames, int session)
    {
        var timed = record.Scenarios.Where(s => s.Kind == Kind.Timed).Select(s => s.Name).ToHashSet();
        var first = frames.Where(f => f.Part.Length > 0 && !timed.Contains(f.Scenario) && f.Session == session).ToList();
        static double? Gpu(IEnumerable<FrameRow> rows)
        {
            double weighted = 0, seconds = 0;
            foreach (var part in rows.GroupBy(r => (r.Scenario, r.Part, r.Pass)))
                if (Summary.Of(part.Key.Part, part.ToList(), null, null) is { GpuMsMedian: { } gpu, Seconds: > 0 } p)
                    (weighted, seconds) = (weighted + gpu * p.Seconds, seconds + p.Seconds);
            return seconds > 0 ? Math.Round(weighted / seconds, 2) : null;
        }
        return new WarmUpRecord(Gpu(first.Where(f => f.Pass == 0)), Gpu(first.Where(f => f.Pass > 0)));
    }

    // Each part's limit from its test's noise profile: benchmark.sh calibrate's (RunOptions.NoiseFolder) before the one in
    // the app, next to its lineup
    private static void SetLimits(RunRecord record, RunOptions options)
    {
        if (!File.Exists(options.Script))
            return;
        Lineup lineup;
        try
        {
            lineup = Lineup.Read(options.Script);
        }
        catch (LineupException)
        {
            return;
        }
        foreach (var scenario in record.Scenarios)
        {
            if (lineup.TestOf(scenario.Name) is not { } test)
                continue;
            var folder = options.NoiseFolder.Length > 0 && File.Exists(NoiseProfile.PathFor(options.NoiseFolder, test.Name))
                ? options.NoiseFolder : Path.GetDirectoryName(options.Script)!;
            var noise = NoiseProfile.Read(folder, test.Name);
            foreach (var part in scenario.Parts)
                if (noise.Parts.ContainsKey(part.Name))
                    part.Limit = noise.PartLimit(part.Name);
        }
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
                    f.StartsWith("game", StringComparison.Ordinal) && f.EndsWith(".log", StringComparison.Ordinal) ||
                    f.StartsWith("stuck", StringComparison.Ordinal) && f.EndsWith(".txt", StringComparison.Ordinal))
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
