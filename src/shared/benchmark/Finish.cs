// The end of a run: its record (run.json) made from what it left (plan.json, the run's store, the game's logs, the
// screenshots), then its report (report.md, report.html) and the zip to share. Both hosts call it: benchmark.sh's
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
    // asksMac: whether to ask macOS about this Mac (the tests' runs are of no Mac); timeline: whether to write the run's
    // timeline now (a few hundred MB for a long run: the running game writes it when its report is opened)
    public static RunRecord Run(string dir, bool asksMac = true, bool timeline = true) => Culture(() => Make(dir, asksMac, null, timeline));

    // A run finished again from its store, as this version of the benchmark makes a record: its facts as its record had
    // them (the Mac and the app may have changed since), its numbers, checks and pages new. A record of any schema
    // gives its facts; without one, the Mac is asked
    public static RunRecord Again(string dir) => Culture(() =>
    {
        var kept = KeptFacts(dir);
        return Make(dir, kept == null, kept, true);
    });

    private static RunRecord Culture(Func<RunRecord> make)
    {
        // Its numbers are written the same whatever the culture of the thread it runs on (in the game, the player's)
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            return make();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = culture;
        }
    }

    // The facts a run's record (of any schema) has, but those Make adds itself; null without a record
    private static List<Fact>? KeptFacts(string dir)
    {
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, RunRecord.FileName)));
            if (node?["facts"] is not System.Text.Json.Nodes.JsonArray facts)
                return null;
            return facts.OfType<System.Text.Json.Nodes.JsonObject>()
                .Select(f => new Fact((string?)f["key"] ?? "", (string?)f["value"] ?? ""))
                .Where(f => f.Key != "Warm-up").ToList();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static RunRecord Make(string dir, bool asksMac, List<Fact>? kept, bool timeline)
    {
        var plan = RunPlan.Read(dir) ?? new RunPlan();
        var events = EventLog.Read(dir);
        var frames = FrameLog.Read(dir);
        var logs = Logs(dir);
        var name = Path.GetFileName(Path.GetFullPath(dir).TrimEnd('/'));
        // The probes' metrics known before the parts' numbers take them
        var probeMetrics = RunStore.ReadMetrics(dir);
        foreach (var m in probeMetrics)
            Metrics.Register(m);
        var frameGpu = RunStore.ReadFrameGpu(dir);
        var record = Core(dir, name, plan, events, frames, frameGpu);
        record.Moments = RunStore.ReadMoments(dir);
        record.ProbeMetrics = probeMetrics;
        record.Census = RunStore.ReadCensus(dir);
        record.Facts = kept ?? Facts.Gather(dir, plan.Host.App, plan.Host.Facts, logs.FirstOrDefault().Lines ?? [], asksMac,
            events.FirstOrDefault(e => e.Type == "hooks")?.Text);
        if (record.WarmUp is { } warmUp)
            record.Facts.Add(new Fact("Warm-up", warmUp.GpuMs is { } gpu
                ? $"a pass first, left out (GPU {gpu:F1} ms a frame)" : "a pass first, left out"));
        // How its GPU busy was measured: each frame's own (frame_gpu), or what completed between presents (a run of before)
        record.Facts.RemoveAll(f => f.Key == "GPU time");
        if (frameGpu.Count > 0)
            record.Facts.Add(new Fact("GPU time", Ground.OwnGpuTime));
        var crashes = CrashReports(record.Started.ToUniversalTime());
        record.Findings = Checks.Run(new RunEvidence(dir, record, events, logs, crashes));
        record.Status = events.Any(e => e.Type == "stopped") ? Status.Stopped : record.Problems.Any() ? Status.Failed : Status.Passed;
        record.Write(dir);
        // The timeline, which the report's Open Timeline button loads; without it, the button says how to write it
        try
        {
            if (timeline)
                Timeline.Write(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
        }
        Pages.WriteReport(dir, record);
        // The game's store of a tab's run, and benchmark.sh's own, closed before the zip takes them: the run is over
        RunStore.Close(dir);
        Package(dir, crashes);
        return record;
    }

    // What the record says from the run's events and frames: how it played, and each part's numbers
    private static RunRecord Core(string dir, string name, RunPlan plan, List<RunEvent> events, List<FrameRow> frames,
        Dictionary<int, Dictionary<ulong, double>> frameGpu)
    {
        var record = new RunRecord
        {
            Schema = RunRecord.CurrentSchema,
            Name = name,
            Started = events.FirstOrDefault()?.At.ToLocalTime() ?? Directory.GetCreationTime(dir),
            Test = plan.Play.Test.Length > 0 ? plan.Play.Test : "places",
            Mode = plan.Play.Mode,
            Start = events.LastOrDefault(e => e.Type == "start")?.Text ?? "",
            Sessions = Math.Max(1, events.Select(e => e.Session).DefaultIfEmpty(1).Max()),
            Passes = Math.Max(1, events.Select(e => e.Pass).DefaultIfEmpty(1).Max()),
            LoadSeconds = events.FirstOrDefault(e => e.Type == "loaded")?.Value,
            Drawable = events.LastOrDefault(e => e.Type == "drawable")?.Text,
            Scenarios = Scenarios(events, frames, RunStore.ReadShown(dir), RunStore.ReadWaits(dir), RunStore.ReadZones(dir), RunStore.ReadCounters(dir),
                frameGpu),
        };
        SetLimits(record, plan);
        if (events.FirstOrDefault(e => e.Type == "warmed") is { } warmed)
            record.WarmUp = WarmUp(record, frames, warmed.Session, PassKinds(events), frameGpu.GetValueOrDefault(warmed.Session));
        return record;
    }

    // What each pass of each session was (the pass event's kind): warm-up, measured or profiled; a run of before pass
    // events had its warm-up as pass 0 and measured the rest
    public static Func<int, int, string> PassKinds(IEnumerable<RunEvent> events)
    {
        var kinds = events.Where(e => e.Type == "pass" && e.Text != null).GroupBy(e => (e.Session, e.Pass)).ToDictionary(g => g.Key, g => g.First().Text!);
        return (session, pass) => kinds.GetValueOrDefault((session, pass)) ?? (pass == 0 ? "warm-up" : "measured");
    }

    // The scenarios as they played, in order: each part's measurings (the measured passes of every session) from their
    // frames, KSA Metal's shown records and the zones, merged; a profiled pass's apart; the screenshots of the first pass
    // (a warm-up's too) with their limits
    private static List<ScenarioRecord> Scenarios(List<RunEvent> events, List<FrameRow> frames, Dictionary<int, Dictionary<ulong, ShownRow>> shown, List<WaitRow> waits,
        List<ZoneRow> zones, Dictionary<(string Scenario, string Part, int Session, int Pass), Dictionary<string, List<double>>> counters,
        Dictionary<int, Dictionary<ulong, double>> frameGpu)
    {
        var kindOf = PassKinds(events);
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
        // The frame before each part's first: what its counts (shaders compiled) start from
        var before = new Dictionary<(string, string, int, int), FrameRow>();
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].Part.Length > 0 && (frames[i].Scenario, frames[i].Part, frames[i].Session, frames[i].Pass) is var key && !before.ContainsKey(key))
                before[key] = frames[i - 1];
        var zoneRows = zones.GroupBy(z => (z.Scenario, z.Part, z.Session, z.Pass)).ToDictionary(g => g.Key, g => g.ToList());
        // A part a trigger's heavy work disturbed (Triggers.cs) leaves the numbers in that pass
        var disturbed = events.Where(e => e.Type == "disturbed").Select(e => (e.Scenario, e.Part, e.Session, e.Pass)).ToHashSet();
        foreach (var scenario in scenarios)
        {
            var opens = events.Where(e => e.Type == "open" && e.Scenario == scenario.Name && kindOf(e.Session, e.Pass) != "warm-up" &&
                                          !disturbed.Contains((e.Scenario, e.Part, e.Session, e.Pass))).ToList();
            foreach (var part in opens.Select(e => e.Part!).Distinct().ToList())
            {
                var measured = new List<(RunEvent Open, Dictionary<string, double> Values, int Frames, double Seconds)>();
                var profiled = new List<Dictionary<string, double>>();
                foreach (var open in opens.Where(e => e.Part == part))
                {
                    var key = (scenario.Name, part, open.Session, open.Pass);
                    if (!rows.TryGetValue(key, out var r))
                        continue;
                    // Its session's presents: each game counts its own from 1
                    var values = Measure.Of(r, shown.GetValueOrDefault(open.Session), zoneRows.GetValueOrDefault(key), scenario.Kind == Kind.Timed ? open.Game : null,
                        (long?)open.Value, waits, before.GetValueOrDefault(key), frameGpu.GetValueOrDefault(open.Session));
                    // The probes' counters over the part's frames: their median, or the most (Across)
                    foreach (var (metric, samples) in counters.GetValueOrDefault(key) ?? [])
                        values[metric] = Math.Round(Metrics.Find(metric)?.Across switch { Across.Most => samples.Max(), Across.Mean => samples.Average(), _ => Measure.Median(samples) }, 3);
                    // The GPU's energy a frame, where the host measured its power: its mean over the part times the mean frame
                    // (W times ms is mJ; medians would jump between the beats of a capped frame rate); not for time warp,
                    // whose frames do ever more work
                    if (scenario.Kind != Kind.Timed && counters.GetValueOrDefault(key)?.GetValueOrDefault("host/gpu_power") is { Count: > 0 } watts)
                        values[Metrics.GpuEnergy.Name] = Math.Round(watts.Average() * r.Average(f => f.FrameMs), 1);
                    if (kindOf(open.Session, open.Pass) == "profiled")
                        profiled.Add(values);
                    else
                        measured.Add((open, values, r.Count, Math.Round(r.Sum(f => f.FrameMs) / 1000, 2)));
                }
                if (measured.Count == 0)
                    continue;
                var merged = Measure.Merge(measured.Select(m => (IReadOnlyDictionary<string, double>)m.Values).ToList());
                scenario.Parts.Add(new PartRecord
                {
                    Name = part, Runs = measured.Count, Frames = measured[0].Frames, Seconds = measured[0].Seconds,
                    Values = new SortedDictionary<string, double>(merged, StringComparer.Ordinal),
                    Measurings = measured.Select(m => new MeasuringRecord(m.Open.Session, m.Open.Pass,
                        new SortedDictionary<string, double>(m.Values.Where(kv => !Metrics.IsZone(kv.Key)).ToDictionary(), StringComparer.Ordinal))).ToList(),
                    Profiled = profiled.Count > 0
                        ? new SortedDictionary<string, double>(Measure.Merge(profiled.Select(p => (IReadOnlyDictionary<string, double>)p).ToList()), StringComparer.Ordinal)
                        : null,
                });
            }
            var firstPass = events.Where(e => e.Type == "shot" && e.Scenario == scenario.Name)
                .GroupBy(e => (e.Session, e.Pass)).FirstOrDefault();
            foreach (var shot in firstPass ?? Enumerable.Empty<RunEvent>())
                scenario.Shots.Add(new ShotRecord(shot.Part!, shot.Text!, shot.Value ?? NoiseProfile.DefaultShotLimit(scenario.Kind)));
        }
        return scenarios;
    }

    // The warm-up pass's GPU time against the measured passes' in the game it warmed, both as the headline weighs its
    // parts
    private static WarmUpRecord WarmUp(RunRecord record, List<FrameRow> frames, int session, Func<int, int, string> kindOf,
        IReadOnlyDictionary<ulong, double>? frameGpu)
    {
        var timed = record.Scenarios.Where(s => s.Kind == Kind.Timed).Select(s => s.Name).ToHashSet();
        var first = frames.Where(f => f.Part.Length > 0 && !timed.Contains(f.Scenario) && f.Session == session).ToList();
        double? Gpu(IEnumerable<FrameRow> rows)
        {
            double weighted = 0, seconds = 0;
            foreach (var part in rows.GroupBy(r => (r.Scenario, r.Part, r.Pass)))
            {
                var r = part.ToList();
                double s = r.Sum(f => f.FrameMs) / 1000;
                if (Measure.Of(r, null, null, null, null, frameGpu: frameGpu).TryGetValue(Metrics.GpuBusy.Name, out var gpu) && s > 0)
                    (weighted, seconds) = (weighted + gpu * s, seconds + s);
            }
            return seconds > 0 ? Math.Round(weighted / seconds, 2) : null;
        }
        return new WarmUpRecord(Gpu(first.Where(f => kindOf(session, f.Pass) == "warm-up")), Gpu(first.Where(f => kindOf(session, f.Pass) == "measured")));
    }

    // Each part's limit from its test's noise profile: benchmark.sh calibrate's (RunPlan.Host.NoiseFolder) before the one in
    // the app, next to its lineup
    private static void SetLimits(RunRecord record, RunPlan plan)
    {
        if (!File.Exists(plan.Play.Script))
            return;
        Lineup lineup;
        try
        {
            lineup = Lineup.Read(plan.Play.Script);
        }
        catch (LineupException)
        {
            return;
        }
        foreach (var scenario in record.Scenarios)
        {
            if (lineup.TestOf(scenario.Name) is not { } test)
                continue;
            var folder = plan.Host.NoiseFolder.Length > 0 && File.Exists(NoiseProfile.PathFor(plan.Host.NoiseFolder, test.Name))
                ? plan.Host.NoiseFolder : Path.GetDirectoryName(plan.Play.Script)!;
            var noise = NoiseProfile.Read(folder, test.Name);
            foreach (var part in scenario.Parts)
                if (noise.Parts.TryGetValue(part.Name, out var n))
                {
                    part.Limit = n.Limit;
                    part.FrameLimit = n.FrameLimit;
                }
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

    // A copy of a file of the run's store with the home folder's name as ~ in its events' texts
    private static void Store(string from, string to, string home)
    {
        // A copy that holds what the store's log (-wal) still had, as SQLite reads it; the file as it is when that fails
        try
        {
            using var source = Sqlite.Open(from, readOnly: true);
            source.Exec($"VACUUM INTO '{to.Replace("'", "''")}'");
        }
        catch (SqliteException)
        {
            if (File.Exists(to))
                File.Delete(to);
            File.Copy(from, to);
        }
        if (home.Length == 0)
            return;
        try
        {
            using var db = Sqlite.Open(to);
            using var update = db.Prepare("UPDATE events SET text = replace(text, ?, '~') WHERE instr(text, ?) > 0");
            update.Run(home, home);
        }
        catch (SqliteException)
        {
        }
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
                if (f is RunStore.GameFile or RunStore.HostFile)
                    Store(file, Path.Combine(stage, f), home);
                else if (f is RunRecord.FileName or "report.md" or "report.html" or RunPlan.FileName or "settings.toml" or "profile.md" or "trace.md" ||
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
