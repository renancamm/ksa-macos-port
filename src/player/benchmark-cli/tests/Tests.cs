using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

// The tests of the benchmark's record and of benchmark.sh's program, which run without the game: what a run's record
// says (what passes and what fails it, the conditions it notes, repeats merged into medians, numbers that don't depend
// on the Mac's region, an older run's report read), its report, page and zip, the comparison of two runs, the list of
// runs, and the PNG reader and writer the director shares. The runs are written with the director's own classes
// (Events.cs, Frames.cs) the way the game and benchmark.sh leave them, so the tests also check that the record reads
// what the director writes, and every finished run checks its store against its files (a test that wants no notes
// fails when they differ). The build runs them (ctest); a failure stops the build.
//
// Usage: dotnet KsaMetal.Benchmark.Cli.Tests.dll
internal static class Tests
{
    private static string _test = "", _scratch = "";
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"ksa-benchmark-tests-{Environment.ProcessId}");
        // Nothing of this Mac's: finish looks for crash reports in the home folder
        Environment.SetEnvironmentVariable("HOME", _scratch);
        var tests = new (string Name, Action Run)[]
        {
            ("a run that passed", Passed),
            ("a run with problems", Problems),
            ("the conditions noted", Notes),
            ("repeats", Repeats),
            ("numbers in any region", AnyRegion),
            ("finish", FinishZip),
            ("comparing two runs", Compare),
            ("comparing runs of different modes", CompareModes),
            ("a timed part's numbers", TimedPart),
            ("the Warp Test in the report", WarpReport),
            ("the PNG reader and writer", Png),
            ("a run stopped on purpose", Stopped),
            ("a game stuck at one moment", StuckGame),
            ("a screenshot of nothing", BlankShot),
            ("runs that can't compare", NothingInCommon),
            ("an older run", OlderRun),
            ("the list of runs", ListOfRuns),
            ("the terminal's layout", Layout),
            ("the tests' script", Script),
            ("the run's store", Store),
            ("KSA Metal's drain", Drain),
            ("the game's zones", ZoneSums),
            ("the triggers' rules", Rules),
            ("a part a trigger disturbed", Disturbed),
            ("a recording of the player's game", YourGame),
            ("the statistics", StatisticsTests),
            ("verdicts on sessions, passes and one run", Verdicts),
            ("the ground two runs stand on, and shaders from the frame before a part", GroundAndCounts),
            ("PresentMon's numbers", PresentMon),
            ("the zones that moved", ZonesMoved),
            ("a run's zones and slow frames in its record", ZonesInRecord),
            ("a run of before plans, finished again", BeforePlans),
            ("a profiled pass beside the measured one", ProfiledPass),
            ("a study's file", StudyFile),
            ("a study's variants by their sessions", StudyVariants),
            ("a probe's counters, metrics and census", ProbeNumbers),
        };
        foreach (var (name, run) in tests)
        {
            _test = name;
            Directory.CreateDirectory(_scratch);
            try
            {
                run();
            }
            catch (Exception e)
            {
                Failures.Add($"{name}: {e}");
            }
            finally
            {
                CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
                RunStore.CloseAll();
                RunStore.ProcessFile = RunStore.GameFile;
                Directory.Delete(_scratch, true);
            }
        }
        foreach (var failure in Failures)
            Console.WriteLine($"FAILED {failure}");
        Console.WriteLine(Failures.Count == 0
            ? $"    The benchmark's tests: {tests.Length} passed."
            : $"    The benchmark's tests: {Failures.Count} failures.");
        return Failures.Count == 0 ? 0 : 1;
    }

    private static void Check(bool ok, string what)
    {
        if (!ok)
            Failures.Add($"{_test}: {what}");
    }

    private static void Equal<T>(T expected, T actual, string what) =>
        Check(EqualityComparer<T>.Default.Equals(expected, actual), $"{what}: expected '{expected}', got '{actual}'");

    private static void Contains(IEnumerable<string> lines, string text, string what) =>
        Check(lines.Any(l => l.Contains(text, StringComparison.Ordinal)), $"{what}: no '{text}' in [{string.Join(" | ", lines)}]");

    // What a call of the program printed, and what it returned
    private static (int Status, string Output) Said(Func<int> call)
    {
        var (output, error) = (Console.Out, Console.Error);
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            return (call(), captured.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    private static RunRecord Finished(string dir) => Finish.Run(dir, asksMac: false);

    private static string[] Problems(RunRecord r) => r.Problems.Select(f => f.Text).ToArray();

    private static string[] Notes(RunRecord r) => r.Notes.Select(f => f.Text).ToArray();

    private static PartRecord Part(RunRecord r) => r.Scenarios.FirstOrDefault()?.Parts.FirstOrDefault() ?? throw new Exception("no part in the record");

    // A run's folder as the director and benchmark.sh leave it: its plan with these facts, one session that played the
    // scenario earth (of this kind) with one part, earth1, of 100 frames (the last one three times as long), and ended
    private static string Run(string name, double frameMs = 10, string mode = "views", Fact[]? facts = null, double? gpuMs = null,
        Kind kind = Kind.Views, string ending = "quit normally", bool done = true, int sessions = 1)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(dir);
        new RunPlan
        {
            Play = new() { Test = "mixed", Mode = mode },
            Host = new() { Facts = [new("Power", "AC Power"), new("Low Power Mode", "off"), new("port settings", "defaults"), .. facts ?? []] },
        }.Write(dir);
        var events = new EventLog(dir);
        events.Add("start", text: "a new game, from benchmark.sh");
        for (int n = 1; n <= sessions; n++)
            Session(dir, events, n, frameMs, gpuMs, kind, ending, done);
        return dir;
    }

    // One game session of the scenario earth: its part's frames, and its ending
    private static void Session(string dir, EventLog events, int session, double frameMs, double? gpuMs, Kind kind, string ending = "quit normally",
        bool done = true, Action<EventLog>? during = null, uint shaders = 0)
    {
        events.Session = session;
        events.Add("session");
        events.Add("loaded", value: 1.5);
        events.Add("scenario", "earth", text: Lineup.Name(kind));
        events.Add("open", "earth", "earth1", game: 0, value: 0);
        {
            var frames = new FrameLog(dir, session);
            for (int i = 0; i < 100; i++)
            {
                FrameStats? driver = gpuMs is { } g ? new FrameStats { Present = (ulong)i + 1, GpuMs = g, ShadersCompiled = i == 99 ? shaders : 0 } : null;
                frames.Add(i, "earth", "earth1", "Measure", i == 99 ? frameMs * 3 : frameMs, driver, null);
            }
        }
        events.Add("close", "earth", "earth1");
        during?.Invoke(events);
        if (done)
            events.Add("done");
        events.Add("ended", text: ending);
    }

    // A timed part of 20 s at 25 ms a frame: first at the speed asked for after 5 s, or held back all along
    private static List<FrameRow> Warped(double heldBack, double reachesAfter = 5)
    {
        var rows = new List<FrameRow>();
        double wall = 0, game = 1000;
        for (int i = 0; i < 800; i++)
        {
            wall += 0.025;
            game += 0.025 * (wall >= reachesAfter ? 1000 * heldBack : 10);
            rows.Add(new FrameRow(wall, 1, 1, 0, "warpdebris", "debris", "Play", 25, 0, null, 0, 0, 0, 0, 0, 0, game, 1000, 3, i * 2, 10 + i % 3, 40));
        }
        return rows;
    }

    // A run of the timed scenario warpdebris with one part, debris, from Warped
    private static string WarpRun(string name, double heldBack, string? log = null)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(dir);
        new RunPlan { Play = new() { Test = "warp", Mode = "timed" }, Host = new() { Facts = [new("port settings", "defaults")] } }.Write(dir);
        var events = new EventLog(dir, 1);
        events.Add("session");
        events.Add("scenario", "warpdebris", text: "timed");
        events.Add("open", "warpdebris", "debris", game: 1000, value: 0);
        var frames = new FrameLog(dir, 1);
        foreach (var r in Warped(heldBack))
            frames.Add(r.Script, r.Scenario, r.Part, r.Phase, r.FrameMs, null,
                new SimSample(r.GameSeconds!.Value, r.Asked!.Value, r.SimMs!.Value, r.Substeps!.Value, r.Bubbles!.Value, r.Vehicles!.Value));
        events.Add("close", "warpdebris", "debris");
        events.Add("done");
        events.Add("ended", text: "quit normally");
        if (log != null)
            File.WriteAllText(Path.Combine(RunFiles.Data(dir), "game.log"), log);
        return dir;
    }

    private static void Passed()
    {
        var dir = Run("passed", frameMs: 20);
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "passed");
        Equal(0, Problems(record).Length, "problems");
        Equal(0, Notes(record).Length, "notes");
        var part = Part(record);
        Equal(100, part.Frames, "frames");
        Equal<double?>(20.0, part[Metrics.FrameTime], "frame time's median");
        Equal<double?>(60.0, part[Metrics.Worst], "worst frame");
        Equal<double?>(1, part[Metrics.Hitches], "a hitch: over 50 ms and twice the median");
        Equal<double?>(0, part[Metrics.Over100], "frames over 100 ms");
        Equal<double?>(60.0, part[Metrics.Low1], "the 1% low: the slowest frame of 100");
        Equal(1.5, record.LoadSeconds, "loading");
        Equal(Status.Passed, RunRecord.Read(dir)?.Status, "run.json read back");
        var md = File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"));
        Contains([md], "**Passed**", "report.md");
        Contains([md], "| earth | earth1 | ", "report.md's part");
        Check(md.IndexOf("| earth | earth1 | ", StringComparison.Ordinal) < md.IndexOf(Pages.FactsHeading, StringComparison.Ordinal), "the numbers before the facts");
        var page = File.ReadAllText(Path.Combine(dir, "report.html"));
        Contains([page], "Passed", "report.html's verdict");
        Contains([page], "earth1", "report.html's part");
        var shown = Said(() => KsaMetal.Benchmark.Cli.Program.Show(dir));
        Contains([shown.Output], "earth1", "show: the numbers");
        Check(!shown.Output.Contains("AC Power", StringComparison.Ordinal), "show: not the facts about the Mac");
        Check(!shown.Output.Contains("|", StringComparison.Ordinal), "show: aligned columns, no Markdown");
    }

    private static void Problems()
    {
        var dir = Run("problems", facts: [new("Hooks", "TerrainCache: skipped (KSA's code moved); OutlineSkip: on; HudLayout: failed (NullReferenceException)")],
            ending: "hung, stopped", done: false);
        var events = new EventLog(dir, 1);
        events.Add("error", "earth", text: "12.0 s: '@launch Rocket at Pad' failed");
        events.Add("alongside");
        File.WriteAllLines(Path.Combine(RunFiles.Data(dir), "game.log"),
        [
            "[ksa-metal ERROR] pipeline: no function", "[ksa-metal ERROR] pipeline: no function", "[ksa-metal ERROR] pipeline: no function",
            "[ksa-metal ERROR] command buffer: kIOGPUCommandBufferCallbackErrorHang", "\x1b[31m21:04:05 ERROR Something broke\x1b[0m",
            "21:04:06 INFO all is well", "[ksa-metal] not an error", "21:04:07 ERROR error reaching master server [HTTP]: Connection refused"
        ]);
        var record = Finished(dir);
        Equal(Status.Failed, record.Status, "failed");
        var problems = Problems(record);
        Contains(problems, "the game hung, stopped", "the game's ending");
        Contains(problems, "another game ran at the same time", "a game alongside");
        Contains(problems, "the run did not play to its end", "an incomplete run");
        Contains(problems, "earth: 12.0 s: '@launch Rocket at Pad' failed", "a step that failed");
        Contains(problems, "the hooks' TerrainCache: skipped (KSA's code moved)", "a skipped hook");
        Contains(problems, "the hooks' HudLayout: failed (NullReferenceException)", "a failed hook");
        Check(!problems.Any(p => p.Contains("OutlineSkip", StringComparison.Ordinal)), "a hook that is on is no problem");
        Contains(problems, "game.log: [ksa-metal ERROR] pipeline: no function (3 times)", "KSA Metal's errors, counted");
        Contains(problems, "game.log: ERROR Something broke", "the game's own error, without its colours");
        Contains(problems, "the GPU hung or faulted", "a GPU reset");
        Check(!problems.Any(p => p.Contains("master server", StringComparison.Ordinal)), "the game's servers out of reach: no problem");
        Contains(Notes(record), "could not reach its master server", "but a note");
        Equal(10, problems.Length, "problems");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"))], "**Failed**: 10 problems", "report.md");
    }

    private static void Notes()
    {
        var dir = Path.Combine(_scratch, "notes");
        Directory.CreateDirectory(dir);
        new RunPlan
        {
            Play = new() { Test = "mixed", Mode = "views" },
            Host = new()
            {
                Facts = [new("Power", "Battery Power"), new("Low Power Mode", "on"), new("KSA Metal switches", "KSA_MTL_GPU_ZONES=1"), new("Tools", "Instruments"),
                    new("port settings", "defaults")]
            },
        }.Write(dir);
        var events = new EventLog(dir);
        events.Add("start", text: "a new game, from benchmark.sh");
        events.Add("gpu", value: 90);
        Session(dir, events, 1, 10, 5, Kind.Views, shaders: 3);
        events.Session = 0;
        events.Add("memory", text: "pressure warning, swap 100.00 -> 600.00 MB");
        // A crash report of the game's, made during the run
        var reports = Path.Combine(_scratch, "Library", "Logs", "DiagnosticReports");
        Directory.CreateDirectory(reports);
        File.WriteAllText(Path.Combine(reports, "dotnet-2026-10-26.ips"), "\"procName\" : \"dotnet\"");
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "notes don't fail a run");
        var notes = Notes(record);
        Contains(notes, "ran on battery", "battery");
        Contains(notes, "Low Power Mode was on", "Low Power Mode");
        Contains(notes, "KSA Metal switches were set (KSA_MTL_GPU_ZONES=1)", "KSA Metal switches");
        Contains(notes, "measured with Instruments", "tools");
        Contains(notes, "the GPU was busy before the run (90 %)", "a busy GPU");
        Contains(notes, "crash report, dotnet-2026-10-26.ips", "a crash report");
        Contains(notes, "swapped memory to disk during the run (100.00 -> 600.00 MB)", "swap");
        Contains(notes, "shaders compiled while measuring earth earth1 (3)", "shaders compiled");
        Equal(8, notes.Length, "notes");
    }

    private static void Repeats()
    {
        var dir = Run("repeats", frameMs: 12);
        var events = new EventLog(dir);
        Session(dir, events, 2, 10, null, Kind.Views, done: false);
        Session(dir, events, 3, 20, null, Kind.Views);
        var record = Finished(dir);
        Equal(Status.Failed, record.Status, "a session that didn't end fails the run");
        Equal(3, record.Sessions, "sessions");
        Contains(Problems(record), "the run did not play to its end", "the incomplete session");
        var part = Part(record);
        Equal<double?>(12.0, part[Metrics.FrameTime], "frame time: the median of the runs");
        Equal("10,20", string.Join(",", part.Range(Metrics.FrameTime) ?? []), "frame time's range");
        Equal("12,10,20", string.Join(",", part.Sessions(Metrics.FrameTime)), "each session's number, in the order they played");
        Equal(3, part.Runs, "the part's runs");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"))], "| 12.0 (10.0-20.0) |", "report.md's range");
        // A part that never got to its speed in one repeat never did
        var merged = Measure.Merge([Measure.Of(Warped(1), null, null, 1000, 0), Measure.Of(Warped(0.5), null, null, 1000, 0), Measure.Of(Warped(1), null, null, 1000, 0)]);
        Check(!merged.ContainsKey(Metrics.ToSpeed.Name), "seconds to speed: never, in one of the runs");
        Check(merged.ContainsKey(Metrics.Speed.Name), "the speed: their median");
    }

    // A Mac whose region writes 16,7 for 16.7: the record's numbers stay the same
    private static void AnyRegion()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var dir = Run("region", frameMs: 16.7);
        new EventLog(dir).Add("memory", text: "pressure normal, swap 100.00 -> 600.00 MB");
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "passed");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"))], "| 16.7 |", "report.md's frame time");
        Equal<double?>(16.7, Part(record)[Metrics.FrameTime], "the record's frame time");
        Contains(Notes(record), "swapped memory", "swap read with a decimal point");
    }

    // A run from benchmark.sh: its plan without the port settings and the hooks, which come from the game's log
    private static void FinishZip()
    {
        var dir = Path.Combine(_scratch, "2026-10-26_0100");
        var data = RunFiles.Create(dir);
        new RunPlan { Play = new() { Test = "mixed", Mode = "views" } }.Write(dir);
        Session(dir, new EventLog(dir), 1, 10, null, Kind.Views);
        File.WriteAllLines(Path.Combine(data, "game.log"),
            ["==> port settings: render_scale=0.75", "[hooks] TerrainCache: skipped (KSA's code moved)", "[hooks] OutlineSkip: on"]);
        var record = Finished(dir);
        Equal(Status.Failed, record.Status, "a skipped hook fails the run");
        Equal("render_scale=0.75", record.Fact("port settings"), "the port settings, from the log");
        Equal("TerrainCache: skipped (KSA's code moved); OutlineSkip: on", record.Fact("Hooks"), "the hooks, from the log");
        Contains(Problems(record), "the hooks' TerrainCache: skipped", "the skipped hook");
        Contains(Notes(record), "port settings were changed (render_scale=0.75)", "the port settings");
        // On top the page and the zip, everything else in data/
        Check(File.Exists(Path.Combine(dir, "report.html")) && File.Exists(Path.Combine(data, "report.md")) && File.Exists(Path.Combine(data, RunPlan.FileName)) &&
              File.Exists(Path.Combine(data, RunRecord.FileName)) && !File.Exists(Path.Combine(dir, RunRecord.FileName)), "report.html on top, the rest in data/");
        Contains([File.ReadAllText(Path.Combine(dir, "report.html"))], "In the <code>data</code> folder", "the page's footer");
        using var zip = ZipFile.OpenRead(Path.Combine(dir, "to-share_2026-10-26_0100.zip"));
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        Contains(names, "2026-10-26_0100/report.html", "the zip");
        foreach (var file in new[] { "run.json", "report.md", "run.db", "plan.json", "game.log" })
            Contains(names, $"2026-10-26_0100/data/{file}", "the zip");

        // The game's hooks event: what the hooks applied, without the lines they log while the run plays
        var said = Path.Combine(_scratch, "2026-10-26_0101");
        Directory.CreateDirectory(said);
        new RunPlan { Play = new() { Test = "mixed", Mode = "views" } }.Write(said);
        var events = new EventLog(said);
        Session(said, events, 1, 10, null, Kind.Views);
        events.Add("hooks", text: "TerrainCache: on; FarTerrain: on, by altitude");
        File.WriteAllLines(Path.Combine(RunFiles.Data(said), "game.log"),
            ["[hooks] TerrainCache: on", "[hooks] FarTerrain: on, by altitude", "[hooks] FarTerrain: EarthScale level 17 built (jump, waited 161 ms)",
             "[hooks] TerrainCache: failed while the game ran (NullReferenceException: x)"]);
        var hooked = Finished(said);
        Equal("TerrainCache: on; FarTerrain: on, by altitude; TerrainCache: failed while the game ran (NullReferenceException: x)", hooked.Fact("Hooks"),
            "the hooks, from the game's event, with one that failed after it");
        Contains(Problems(hooked), "the hooks' TerrainCache: failed while the game ran", "a hook failed while the run played");
    }

    private static void Compare()
    {
        var a = Run("a", frameMs: 10);
        var b = Run("b", frameMs: 12);
        var view = Gradient(64, 48);
        Images.Write(Path.Combine(RunFiles.Data(a), "earth_earth1.png"), view);
        Images.Write(Path.Combine(RunFiles.Data(b), "earth_earth1.png"), view);
        foreach (var dir in new[] { a, b })
            new EventLog(dir, 1).Add("shot", "earth", "earth1", "earth_earth1.png", value: 1);
        Finished(a);
        Finished(b);
        var (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(0, status, "exit status: the same screenshots");
        Contains([output], "Same image (1 of 1 screenshots)", "the verdict");
        Check(!output.Contains("|", StringComparison.Ordinal), "aligned columns, no Markdown");
        var written = File.ReadAllText(Path.Combine(RunFiles.Data(b), "compare.md"));
        Contains([written], "| 10.0 | 12.0 | **20 % slower** |", "a part 20 % slower, marked: past the screen's 10 %");
        Contains([written], "| earth_earth1.png | 0.00% | 0.0 | same (up to 1%) |", "the same screenshot");
        Contains([written], "Same Mac, same version, same settings.", "the settings");
        Check(File.Exists(Path.Combine(b, "compare.html")), "the page");

        // Half the view changed
        var changed = Gradient(64, 48);
        Array.Fill(changed.Rgb, (byte)255, 0, changed.Rgb.Length / 2);
        Images.Write(Path.Combine(RunFiles.Data(b), "earth_earth1.png"), changed);
        (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(1, status, "exit status: a screenshot differs");
        Contains([output], "differs (up to 1%)", "the screenshot that differs");
        Check(File.Exists(Path.Combine(RunFiles.Data(b), "diff", "earth_earth1.png")), "the marked copy in b/diff/");

        // Within noise
        var c = Run("c", frameMs: 10.2);
        Finished(c);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, c));
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(c), "compare.md"))], "| 10.0 | 10.2 | +2 % |", "a change within noise, unmarked");
        Check(!File.ReadAllText(Path.Combine(RunFiles.Data(c), "compare.md")).Contains("in a row", StringComparison.Ordinal), "one session a side: no word on heat");

        // Sessions a side, played run after run: the heat's warning, on the page too
        var (d, e) = (Run("d", sessions: 5), Run("e", sessions: 5));
        Finished(d);
        Finished(e);
        (_, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(d, e));
        Contains([output], "Each run played its sessions in a row", "the heat's warning");
        Contains([File.ReadAllText(Path.Combine(e, "compare.html"))], "Each run played its sessions in a row", "the warning on the page");

        // Hooks that differ: only those, each side's
        var (f, g) = (Run("f", facts: [new("Hooks", "TerrainCache: on; CloudSteps: off")]), Run("g", facts: [new("Hooks", "TerrainCache: on; CloudSteps: on")]));
        Finished(f);
        Finished(g);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(f, g));
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(g), "compare.md"))], "| Hooks | CloudSteps: off | CloudSteps: on |", "the hooks that differ");
        var h = Run("h", facts: [new("Hooks", "CloudSteps: off; TerrainCache: on")]);
        Finished(h);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(f, h));
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(h), "compare.md"))], "Same Mac, same version, same settings.", "the same hooks in another order");
    }

    private static void CompareModes()
    {
        var a = Run("jump", mode: "jump");
        var b = Run("flight", mode: "flight", kind: Kind.Flight);
        Finished(a);
        Finished(b);
        var (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(2, status, "exit status: runs that don't compare");
        Contains([output], "different modes (jump, flight)", "why");
    }

    private static void TimedPart()
    {
        var reached = Measure.Of(Warped(1), null, null, 1000, 0);
        double? V(Dictionary<string, double> v, Metric m) => v.TryGetValue(m.Name, out var x) ? x : null;
        Equal<double?>(1000.0, V(reached, Metrics.SpeedAsked), "the speed asked for");
        Check(V(reached, Metrics.Speed) is > 700 and < 800, $"the speed over the whole part: {V(reached, Metrics.Speed)}");
        Check(V(reached, Metrics.ToSpeed) is > 5 and < 7, $"the seconds to 90 %: {V(reached, Metrics.ToSpeed)}");
        Check(V(reached, Metrics.GameDays) is >= 0.17 and <= 0.18, $"the game days: {V(reached, Metrics.GameDays)}");
        Equal<double?>(1598, V(reached, Metrics.Substeps), "the substeps");
        Equal<double?>(12, V(reached, Metrics.Bubbles), "the most bubbles");
        Equal<double?>(null, V(Measure.Of(Warped(0.5), null, null, 1000, 0), Metrics.ToSpeed), "a part that never gets to 90 %");
        Equal<double?>(null, V(Measure.Of(Warped(1), null, null, null, null), Metrics.Speed), "a part of another kind");
    }

    private static void WarpReport()
    {
        var a = WarpRun("a", 1);
        Finished(a);
        var md = File.ReadAllText(Path.Combine(RunFiles.Data(a), "report.md"));
        Contains([md], "**0.2 game days** in time warp", "the headline");
        Contains([md], "Time warp: 0.2 game days", "the table's line");
        Contains([md], "| warpdebris | debris | 0.17 |", "the part's row");
        var b = WarpRun("b", 0.5);
        Finished(b);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        var written = File.ReadAllText(Path.Combine(RunFiles.Data(b), "compare.md"));
        Contains([written], "| Warp part |", "the warp table of a comparison");
        Contains([written], "**50 % slower**", "a part at half the speed");
        // The game's own errors in a timed scenario are a note, after it a problem again
        var c = WarpRun("c", 1, "[benchmark] scenario warpdebris (game time 0.0 s, 1.0 s into the run)\n12:00:01 ERROR Applying surface motion\n" +
                                "[benchmark] done after 9.0 s\n12:00:02 ERROR At the end\n");
        var record = Finished(c);
        Contains(Notes(record), "logged 1 errors during the time warp", "the note");
        Contains(Problems(record), "ERROR At the end", "an error after the timed scenario");
        Check(!Problems(record).Any(x => x.Contains("surface motion", StringComparison.Ordinal)), "an error in it is no problem");
    }

    private static void Png()
    {
        var image = Gradient(37, 21);
        var path = Path.Combine(_scratch, "image.png");
        Images.Write(path, image);
        var read = Images.Read(path);
        Check(read != null, "the image written reads back");
        Equal(37, read?.Width ?? 0, "its width");
        Equal(21, read?.Height ?? 0, "its height");
        Check(read != null && read.Rgb.SequenceEqual(image.Rgb), "its pixels");
        Equal(0.0, read != null ? Images.Compare(image, read).ChangedPercent : -1, "the same image: no block changed");
        Equal(100.0, Images.Compare(image, Gradient(36, 21)).ChangedPercent, "another size: every block changed");
        File.WriteAllBytes(path, File.ReadAllBytes(path)[..40]);
        Check(Images.Read(path) == null, "a PNG cut short (still being written) reads as none");
        Check(Images.Read(Path.Combine(_scratch, "none.png")) == null, "a missing file reads as none");
    }

    // Heartbeats a second apart for 90 s, all at the script's 3.0 s in mixed: stuck past a limit of 60 s, unless a
    // screenshot was taken there along the way (the Mixed Test's views all stand at one moment)
    private static void StuckGame()
    {
        static List<RunEvent> Beats(int shotAt)
        {
            var start = DateTime.UtcNow.AddSeconds(-90);
            var events = new List<RunEvent> { new() { Type = "scenario", Session = 1, Scenario = "mixed", At = start } };
            for (int i = 0; i <= 90; i++)
            {
                if (i == shotAt)
                    events.Add(new RunEvent { Type = "shot", Session = 1, Scenario = "mixed", Part = "kitten", At = start.AddSeconds(i) });
                events.Add(new RunEvent { Type = "heartbeat", Session = 1, Scenario = "mixed", Value = 3.0, At = start.AddSeconds(i) });
            }
            return events;
        }
        Equal(" in mixed at 3.0 s", Watchdog.Stuck(Beats(-1), 1, 60) is { } beat ? Watchdog.Where(beat) : "", "stuck");
        Equal(null, Watchdog.Stuck(Beats(45), 1, 60), "a screenshot on the way");
        Equal(null, Watchdog.Stuck(Beats(-1), 2, 60), "another session");
        Equal(null, Watchdog.Hung(Beats(-1), 1, 60), "not hung");
    }

    private static void Stopped()
    {
        var dir = Run("stopped", ending: "was stopped with Ctrl-C", done: false);
        new EventLog(dir, 1).Add("stopped", text: "stopped with Ctrl-C");
        var record = Finished(dir);
        Equal(Status.Stopped, record.Status, "stopped");
        Equal(0, Problems(record).Length, "no problems");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"))], "**Stopped**", "report.md");
        // Stopped, with something wrong besides
        var both = Run("both", ending: "was stopped with Ctrl-C", done: false);
        var events = new EventLog(both, 1);
        events.Add("error", "earth", text: "12.0 s: '@launch' failed");
        events.Add("stopped", text: "stopped with Esc");
        var bothRecord = Finished(both);
        Equal(Status.Stopped, bothRecord.Status, "stopped with Esc");
        Contains(Problems(bothRecord), "'@launch' failed", "a real problem stays");
        // A trigger's stop: the run's own, said so
        var triggered = Run("trigger stopped", done: false);
        new EventLog(triggered, 1).Add("stopped", "earth", text: "stopped by the trigger frame>50ms: stop (a frame of 60 ms in earth1)");
        var triggeredRecord = Finished(triggered);
        Equal(Status.Stopped, triggeredRecord.Status, "a trigger's stop");
        Contains(Notes(triggeredRecord), "the trigger frame>50ms: stop (a frame of 60 ms in earth1)", "the trigger's stop noted");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(triggered), "report.md"))], "**Stopped** by a trigger", "report.md");
        // A repeat stopped along the way is part of the same stop
        var repeat = Run("repeat stopped");
        var more = new EventLog(repeat);
        Session(repeat, more, 2, 10, null, Kind.Views, "was stopped with Ctrl-C", done: false);
        more.Add("stopped", text: "stopped with Ctrl-C");
        Equal(0, Problems(Finished(repeat)).Length, "a repeat stopped: no problems");
    }

    private static void BlankShot()
    {
        var black = new Image(32, 32, new byte[32 * 32 * 3]);
        Check(Images.Blank(black), "all black");
        var stars = new Image(32, 32, new byte[32 * 32 * 3]);
        stars.Rgb[500] = 60;
        Check(!Images.Blank(stars), "one star is a picture");
        var dir = Run("2026-10-26_0200");
        new EventLog(dir, 1).Add("shot", "earth", "earth1", "earth_earth1.png", value: 1);
        Images.Write(Path.Combine(RunFiles.Data(dir), "earth_earth1.png"), black);
        Contains(Problems(Finished(dir)), "earth_earth1.png: the screenshot is one colour", "the problem");
        // The sky gone white around a planet, as when the game's light breaks: a picture, but not a right one
        var flood = new Image(32, 32, new byte[32 * 32 * 3]);
        Array.Fill(flood.Rgb, (byte)255, 0, flood.Rgb.Length * 3 / 4);
        Equal(75.0, Images.WhitePercent(flood), "the white share");
        Images.Write(Path.Combine(RunFiles.Data(dir), "earth_earth1.png"), flood);
        Contains(Problems(Finished(dir)), "75 % of the screenshot is pure white", "the problem");
        Images.Write(Path.Combine(RunFiles.Data(dir), "earth_earth1.png"), stars);
        Equal(0, Problems(Finished(dir)).Length, "a picture: no problem, and none left from before");
    }

    // Said before a run plays, and when comparing
    private static void NothingInCommon()
    {
        var a = Run("a", mode: "jump");
        Finished(a);
        Equal<string?>(null, KsaMetal.Benchmark.Cli.Program.NotComparable(a, "jump", ["earth", "moon"]), "a scenario in common");
        Contains([KsaMetal.Benchmark.Cli.Program.NotComparable(a, "flight", ["earth"]) ?? ""], "jump mode", "another mode");
        Contains([KsaMetal.Benchmark.Cli.Program.NotComparable(a, "jump", ["jupiter"]) ?? ""], "nothing in common", "nothing in common");
        // Places measure only themselves
        Contains([KsaMetal.Benchmark.Cli.Program.NotComparable(a, "jump", ["earth_earth9"]) ?? ""], "no part in common", "a place that run didn't measure");
        Equal<string?>(null, KsaMetal.Benchmark.Cli.Program.NotComparable(a, "jump", ["earth_earth1", "earth_earth9"]), "one that it did");
        var b = WarpRun("b", 1);
        new RunPlan { Play = new() { Test = "warp", Mode = "jump" } }.Write(b);
        Finished(b);
        var compared = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(2, compared.Status, "compare: nothing in common");
        Contains([compared.Output], "no part in common", "compare: why");
        // The verdict: a slower run isn't good news, and what wasn't compared is said
        var c = Run("c", gpuMs: 10);
        var d = Run("d", gpuMs: 20);
        Finished(c);
        Finished(d);
        var slower = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(c, d));
        Contains([slower.Output], "GPU time 100 % higher", "the verdict");
        Contains([slower.Output], "No screenshots in a", "screenshots that weren't compared");
        Contains([File.ReadAllText(Path.Combine(d, "compare.html"))], "status bad", "the page's verdict");
        // The newer run decides a view's limit, and a has shots b lacks: said in a sentence
        var old = Run("old");
        new EventLog(old, 1).Add("shot", "earth", "earth1", "earth_earth1.png", value: 70);
        var fresh = Run("fresh");
        new EventLog(fresh, 1).Add("shot", "earth", "earth1", "earth_earth1.png", value: 1);
        Images.Write(Path.Combine(RunFiles.Data(old), "earth_earth1.png"), Gradient(64, 48));
        Finished(old);
        Finished(fresh);
        Contains([Said(() => KsaMetal.Benchmark.Cli.Program.Compare(old, fresh)).Output], "b has no PNG", "no pictures in b");
        var changedPicture = Gradient(64, 48);
        Array.Fill(changedPicture.Rgb, (byte)255, 0, changedPicture.Rgb.Length / 2);
        Images.Write(Path.Combine(RunFiles.Data(fresh), "earth_earth1.png"), changedPicture);
        Equal(1, Said(() => KsaMetal.Benchmark.Cli.Program.Compare(old, fresh)).Status, "an old run's loose limit doesn't hide a change");
    }

    // A run of an older record's schema, or older still (a report.json and run.json as its options): not read
    private static void OlderRun()
    {
        var dir = Path.Combine(_scratch, "2026-11-02_2057");
        RunFiles.Create(dir);
        File.WriteAllText(Path.Combine(RunFiles.Data(dir), RunRecord.FileName), $$"""{"schema":{{RunRecord.CurrentSchema - 1}},"name":"2026-11-02_2057","test":"mixed","scenarios":[]}""");
        Check(RunRecord.Read(dir) == null, "an older schema");
        File.WriteAllText(Path.Combine(RunFiles.Data(dir), RunRecord.FileName), """{"script":"benchmark.txt","only":"mixed"}""");
        Check(RunRecord.Read(dir) == null, "an older run's plan");
    }

    private static void ListOfRuns()
    {
        var folder = Path.Combine(_scratch, "benchmarks");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "2026-10-01_0100", "2026-10-02_0100", "2026-10-03_0100" })
            Finished(Run(Path.Combine("benchmarks", name), facts: [new("Test", "mixed"), new("Preset", "none, own settings")]));
        Directory.CreateDirectory(Path.Combine(folder, "2026-10-04_0100_hold"));
        var (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Runs(folder, 2));
        Equal(0, status, "exit status");
        Contains([output], "2026-10-03_0100", "the newest");
        Contains([output], "2026-10-02_0100", "the next");
        Check(!output.Contains("2026-10-01_0100", StringComparison.Ordinal), "only as many as asked");
        Contains([output], "Mixed Test", "the test's name");
        Contains([output], "2 of 3 runs", "how many there are");
    }

    private static void Layout()
    {
        var text = KsaMetal.Benchmark.Cli.Terminal.Layout("## Title\n\n**Passed**\n\n| A | B |\n|---|---:|\n| long name | 1 |\n| x | 100 |\n");
        Contains([text], "Title", "the heading");
        Check(!text.Contains('#') && !text.Contains('*') && !text.Contains('|'), $"no Markdown left: {text}");
        Contains([text], "long name    1", "columns aligned, numbers to the right");
        Contains([text], "x          100", "columns aligned, numbers to the right");
    }

    // benchmark.txt itself, without a game: what would otherwise fail minutes into a run
    private static void Script()
    {
        var root = Directory.GetCurrentDirectory();
        while (root != null && !File.Exists(Path.Combine(root, "src/game/benchmark/lineup/benchmark.txt")))
            root = Path.GetDirectoryName(root);
        if (root == null)
        {
            Check(false, "no src/game/benchmark/lineup/benchmark.txt above this folder");
            return;
        }
        var folder = Path.Combine(root, "src/game/benchmark/lineup");
        Lineup lineup;
        try
        {
            lineup = Lineup.Read(Path.Combine(folder, Lineup.FileName));
        }
        catch (LineupException e)
        {
            Check(false, e.Message);
            return;
        }
        Equal("mixed tour warp", string.Join(" ", lineup.Tests.Select(t => t.Name).OrderBy(t => t, StringComparer.Ordinal)), "the tests");
        Equal(Kind.Views, lineup.Test("mixed")?.Kind, "the Mixed Test's kind");
        Equal(Kind.Flight, lineup.Test("tour")?.Kind, "the Planetary Tour's kind");
        Equal(Kind.Timed, lineup.Test("warp")?.Kind, "the Warp Test's kind");
        foreach (var error in lineup.Validate())
            Check(false, error);
        // Each test's noise profile names screenshots and parts the test has
        foreach (var test in lineup.Tests)
        {
            var noise = NoiseProfile.Read(folder, test.Name);
            var shots = test.Scenarios.SelectMany(s => lineup.Scenario(s)!.Shots).ToHashSet();
            var parts = test.Scenarios.SelectMany(s => lineup.Scenario(s)!.Parts).ToHashSet();
            foreach (var shot in noise.Shots.Keys)
                Check(shots.Contains(shot), $"noise/{test.Name}.json: no screenshot {shot} in the test");
            foreach (var part in noise.Parts.Keys)
                Check(parts.Contains(part), $"noise/{test.Name}.json: no part {part} in the test");
        }
        // What the parser refuses, with the line
        foreach (var (text, says) in new[]
                 {
                     ("scenario a flight\n0 @shot one 5\n", ":2: @shot <name>"),
                     ("scenario a flight\n0 @nothing\n", ":2: no directive @nothing"),
                     ("scenario a sideways\n", ":1: scenario <name> flight|views|timed"),
                     ("scenario a flight\nscenario a views\n", ":2: scenario a twice"),
                     ("test t a b\nscenario a flight\n0 @shot x\nscenario b views\n0 @shot y\n", "test t mixes kinds"),
                     ("scenario a flight\n0 @shot x\nscenario b flight\n0 @shot x\n", "screenshot x in a and b"),
                 })
        {
            try
            {
                Lineup.Parse(text, "t.txt");
                Check(false, $"refused: {says}");
            }
            catch (LineupException e)
            {
                Check(e.Message.Contains(says, StringComparison.Ordinal), $"refused with '{says}': {e.Message}");
            }
        }
    }

    // Mann-Whitney U, Cliff's delta and the ratio's interval against values worked out by hand
    private static void StatisticsTests()
    {
        double[] low = [1, 2, 3, 4, 5], high = [6, 7, 8, 9, 10], odd = [1, 3, 5, 7, 9], even = [2, 4, 6, 8, 10];
        Equal(Math.Round(2 / 252.0, 6), Math.Round(Statistics.MannWhitney(low, high), 6), "5 against 5 apart: exact p = 2/252");
        Equal(1.0, Statistics.MannWhitney(low, low), "the same samples");
        Check(Statistics.MannWhitney(odd, even) > 0.5, "samples that interleave");
        Equal(1.0, Statistics.CliffsDelta(low, high), "every B higher");
        Equal(0.2, Statistics.CliffsDelta(odd, even), "15 pairs higher, 10 lower, of 25");
        // 12 a side: the normal approximation
        var a = Enumerable.Range(0, 12).Select(i => 10.0 + i * 0.01).ToArray();
        var b = a.Select(x => x + 1).ToArray();
        Check(Statistics.MannWhitney(a, b) < 0.001, $"12 against 12 apart: {Statistics.MannWhitney(a, b)}");
        var interval = Statistics.RatioInterval(low, high);
        Check(interval is [var lo, var hi] && lo <= 8.0 / 3 && hi >= 8.0 / 3 && lo > 1, $"the ratio's interval holds the ratio of the medians: {string.Join(",", interval ?? [])}");
        Equal(string.Join(",", interval ?? []), string.Join(",", Statistics.RatioInterval(low, high) ?? []), "the same samples, the same interval");
    }

    // A part measured in these sessions, each with these passes' GPU busy
    private static PartRecord Measured(params double[][] sessions)
    {
        var part = new PartRecord { Name = "earth1", Runs = sessions.Sum(x => x.Length), Frames = 100, Seconds = 1 };
        for (int s = 0; s < sessions.Length; s++)
            for (int p = 0; p < sessions[s].Length; p++)
                part.Measurings.Add(new MeasuringRecord(s + 1, p + 1, new SortedDictionary<string, double> { [Metrics.GpuBusy.Name] = sessions[s][p] }));
        foreach (var (k, v) in Measure.Merge(part.Measurings.Select(m => (IReadOnlyDictionary<string, double>)m.Values).ToList()))
            part.Values[k] = v;
        return part;
    }

    private static void Verdicts()
    {
        double[][] Sessions(params double[] medians) => medians.Select(m => new[] { m }).ToArray();
        var a = Measured(Sessions(10.0, 10.1, 9.9, 10.05, 9.95));
        var slower = Statistics.Judge(a, Measured(Sessions(11.0, 11.1, 10.9, 11.05, 10.95)), Metrics.GpuBusy)!;
        Equal(Basis.Sessions, slower.Basis, "5 sessions a side: by sessions");
        Equal(Change.Worse, slower.Change, "10 % more GPU time in every session");
        Check(slower.P < 0.05 && slower.Delta == 1, $"p {slower.P}, delta {slower.Delta}");
        Equal(Change.Same, Statistics.Judge(a, Measured(Sessions(10.02, 9.98, 10.08, 9.93, 10.0)), Metrics.GpuBusy)!.Change, "the same");
        Equal(Change.Inconclusive, Statistics.Judge(Measured(Sessions(10, 10.1, 9.9)), Measured(Sessions(11, 11.1, 10.9)), Metrics.GpuBusy)!.Change,
            "3 sessions a side: inconclusive, however far apart");
        var quick = Statistics.Judge(Measured([10.0, 10.1, 9.9]), Measured([10.5, 10.6, 10.4]), Metrics.GpuBusy)!;
        Equal(Basis.QuickRead, quick.Basis, "one session a side with passes: a quick read");
        Equal(Change.Worse, quick.Change, "ranges apart, 5 %");
        Equal(Change.Same, Statistics.Judge(Measured([10.0, 10.3, 9.9]), Measured([10.2, 10.6, 10.1]), Metrics.GpuBusy)!.Change, "ranges that overlap");
        var screen = Statistics.Judge(Measured([10.0]), Measured([10.4]), Metrics.GpuBusy)!;
        Equal(Basis.Screen, screen.Basis, "one measuring a side: a screen");
        Equal(Change.Same, screen.Change, "4 %: under the 5 % limit");
        var limited = Measured([10.0]);
        limited.Limit = 3;
        Equal(Change.Same, Statistics.Judge(limited, Measured([10.4]), Metrics.GpuBusy)!.Change, "4 %: past a calibrated limit of 3 %, a screen's drift");
        Equal(Change.Worse, Statistics.Judge(limited, Measured([11.2]), Metrics.GpuBusy)!.Change, "12 %: past the screen's 10 %");
        var tight = Measured([10.0, 10.1, 9.9]);
        tight.Limit = 6;
        Equal(Change.Same, Statistics.Judge(tight, Measured([10.5, 10.6, 10.4]), Metrics.GpuBusy)!.Change, "a quick read of 5 %: under a calibrated limit of 6 %");
        // The sessions' own spread is the noise: a calibrated limit of a run doesn't hold them back
        var wide = Measured(Sessions(10.0, 10.1, 9.9, 10.05, 9.95));
        wide.Limit = 6.7;
        var small = Statistics.Judge(wide, Measured(Sessions(9.6, 9.65, 9.55, 9.62, 9.58)), Metrics.GpuBusy)!;
        Equal(Change.Better, small.Change, "4 % less in every session: past p and delta, under the run's limit of 6.7 %");
        // The interval that goes with the test: 5 a side cut the 3 lowest and highest of the 25 ratios
        Equal(2, small.Interval!.Length, "an interval");
        Check(small.Interval[0] > 0.94 && small.Interval[1] < 0.97, $"its interval leaves out 1: {small.Interval[0]}-{small.Interval[1]}");
        var same = Statistics.Judge(a, Measured(Sessions(10.02, 9.98, 10.08, 9.93, 10.0)), Metrics.GpuBusy)!;
        Check(same.Interval![0] < 1 && same.Interval[1] > 1, $"the same: its interval holds 1, {same.Interval[0]}-{same.Interval[1]}");
        // 5 a side: the 3rd lowest and the 3rd highest of the 25 ratios; and an interval that leaves out 1 is a p under 0.05
        double[] xs = [10.0, 10.1, 9.9, 10.05, 9.95], ys = [9.6, 9.65, 9.55, 9.62, 9.58];
        var ratios = xs.SelectMany(x => ys.Select(y => y / x)).Order().ToArray();
        Equal(Math.Round(ratios[2], 4), small.Interval[0], "the interval's low end: the 3rd lowest ratio");
        Equal(Math.Round(ratios[^3], 4), small.Interval[1], "its high end: the 3rd highest");
        var random = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            var sa = Enumerable.Range(0, 5).Select(_ => 10 + random.NextDouble()).ToArray();
            var sb = Enumerable.Range(0, 5).Select(_ => 10 + random.NextDouble() + random.NextDouble() * 0.6).ToArray();
            var iv = Statistics.RatioInterval(sa, sb)!;
            if (Statistics.MannWhitney(sa, sb) < 0.05 != (iv[0] > 1 || iv[1] < 1))
                Check(false, $"trial {trial}: p {Statistics.MannWhitney(sa, sb)}, interval {iv[0]}-{iv[1]}");
        }
        Check(Statistics.RatioInterval([1, 2, 3], [2, 3, 4]) == null, "3 a side: no interval at 95 %");
        // Frame time by its own limit, and in a record of before (no FrameLimit) by the part's when it has no GPU time
        PartRecord Frames(double ms, double? limit, double? frameLimit, bool gpu)
        {
            var p = new PartRecord { Name = "earth1", Frames = 100, Limit = limit, FrameLimit = frameLimit };
            p.Measurings.Add(new MeasuringRecord(1, 1, new SortedDictionary<string, double> { [Metrics.FrameTime.Name] = ms }));
            p.Measurings.Add(new MeasuringRecord(1, 2, new SortedDictionary<string, double> { [Metrics.FrameTime.Name] = ms + 0.01 }));
            p.Values[Metrics.FrameTime.Name] = ms;
            if (gpu)
                p.Values[Metrics.GpuBusy.Name] = 10;
            return p;
        }
        Equal(8.0, Statistics.Judge(Frames(10, 3, 8, true), Frames(10.5, 3, 8, true), Metrics.FrameTime)!.Limit, "frame time: its own calibrated limit");
        Equal(6.0, Statistics.Judge(Frames(10, 6, null, false), Frames(10.5, 6, null, false), Metrics.FrameTime)!.Limit, "a record of before: the part's limit, frame time's");
        Equal(2.0, Statistics.Judge(Frames(10, 6, null, true), Frames(10.5, 6, null, true), Metrics.FrameTime)!.Limit, "a record of before with GPU time: no frame limit, 2 %");
        // Counts merge as their mean, a worst state as the worst
        var merged = Measure.Merge([new Dictionary<string, double> { [Metrics.Hitches.Name] = 4, [Metrics.Thermal.Name] = 0 },
            new Dictionary<string, double> { [Metrics.Hitches.Name] = 0, [Metrics.Thermal.Name] = 2 }]);
        Equal(2.0, merged[Metrics.Hitches.Name], "hitches: the mean");
        Equal(2.0, merged[Metrics.Thermal.Name], "the thermal state: the worst");
        // A probe's metric: the first definition read stands
        Metrics.Register(new Metric("probe/test/first", "First", "", Better.Lower, Across.Median, 0, "") { Limit = 3 });
        Metrics.Register(new Metric("probe/test/first", "First", "", Better.Lower, Across.Median, 0, "") { Limit = 30 });
        Equal(3.0, Metrics.Find("probe/test/first")!.Limit, "the first definition stands");
        // A count from 0 moves by its floor
        PartRecord Count(Metric m, int frames, params double[] values)
        {
            var p = new PartRecord { Name = "earth1", Frames = frames, Seconds = 1 };
            p.Measurings.Add(new MeasuringRecord(1, 1, new SortedDictionary<string, double> { [m.Name] = values[0] }));
            p.Values[m.Name] = values[0];
            return p;
        }
        Equal(Change.Worse, Statistics.Judge(Count(Metrics.Hitches, 300, 0), Count(Metrics.Hitches, 300, 3), Metrics.Hitches)!.Change, "hitches 0 to 3");
        Equal(Change.Same, Statistics.Judge(Count(Metrics.Hitches, 300, 0), Count(Metrics.Hitches, 300, 1), Metrics.Hitches)!.Change, "hitches 0 to 1: under the floor");
        Equal("0.0 to 3.0 frames", Comparison.Text(Statistics.Judge(Count(Metrics.Hitches, 300, 0), Count(Metrics.Hitches, 300, 3), Metrics.Hitches)!), "a count from 0 in words");
        Check(Statistics.Judge(Count(Metrics.FrameP99, 40, 30), Count(Metrics.FrameP99, 40, 60), Metrics.FrameP99) == null, "a P99 of 40 frames: not judged");
        Equal(Change.Worse, Statistics.Judge(Count(Metrics.FrameP99, 400, 30), Count(Metrics.FrameP99, 400, 40), Metrics.FrameP99)!.Change, "a P99 of 400 frames, 33 % higher");
        // A few percent of a small number: under CPU busy's floor of 0.25 ms
        PartRecord Cpu(params double[] ms)
        {
            var p = Measured(ms.Select(x => new[] { x }).ToArray());
            foreach (var (m, x) in p.Measurings.Zip(ms))
                m.Values[Metrics.CpuBusy.Name] = x;
            p.Values[Metrics.CpuBusy.Name] = Measure.Median(ms);
            return p;
        }
        Equal(Change.Same, Statistics.Judge(Cpu(2.40, 2.41, 2.39, 2.42, 2.40), Cpu(2.20, 2.19, 2.21, 2.18, 2.20), Metrics.CpuBusy)!.Change,
            "CPU busy 0.2 ms faster: 8 %, past the limit, under the floor");
        Equal(Change.Better, Statistics.Judge(Cpu(3.00, 3.01, 2.99, 3.02, 3.00), Cpu(2.60, 2.59, 2.61, 2.58, 2.60), Metrics.CpuBusy)!.Change,
            "CPU busy 0.4 ms faster: past both");
        var comparison = Comparison.Of([("earth", a)], [("earth", Measured(Sessions(10.0, 10.1, 9.9)))]);
        Contains([comparison.Sentence().Text], "inconclusive (3 sessions a side, 5 needed)", "the sentence of too few sessions");
        // The sentence names the parts that moved, even when the others move back: one part 20 % higher, four 5 % lower
        PartRecord One(string name, double gpu, double frame, double mb)
        {
            var p = new PartRecord { Name = name, Frames = 100, Seconds = 1 };
            p.Measurings.Add(new MeasuringRecord(1, 1, new SortedDictionary<string, double> { [Metrics.GpuBusy.Name] = gpu }));
            (p.Values[Metrics.GpuBusy.Name], p.Values[Metrics.FrameTime.Name], p.Values[Metrics.Footprint.Name]) = (gpu, frame, mb);
            return p;
        }
        string[] names = ["kitten", "explosion", "liftoff", "topdown", "cockpit"];
        var mixed = Comparison.Of(names.Select(n => ("mixed", One(n, 10, 10, 8000))),
            names.Select((n, i) => ("mixed", One(n, i == 0 ? 12 : 9.5, i == 0 ? 12 : 9.5, i == 0 ? 9000 : 8000))));
        var (text, good) = mixed.Sentence();
        Contains([text], "GPU time 20 % higher in kitten, 4 other parts within noise (screen)", "one part higher, the others within a screen's drift");
        Contains([text], "Memory 12 % higher in kitten", "memory, which doesn't drift, on a screen");
        Check(!text.Contains("Frame time", StringComparison.Ordinal), $"a frame time that moved with its GPU time is the same story: {text}");
        Equal(false, good, "a part got worse");
        // With Vsync a frame time that jumped a display step while its GPU time didn't move isn't another number that moved
        var stepped = Comparison.Of([("mixed", One("kitten", 25, 25, 8000))], [("mixed", One("kitten", 26, 28.6, 8000))], steps: true);
        Equal("GPU time within noise (screen)", stepped.Sentence().Text, "the frame time on the display's steps");
        // ... by sessions too, where a frame time and a latency are judged: 5 sessions a side, GPU the same, the display a step on
        PartRecord Paced(double frame, double latency)
        {
            var p = new PartRecord { Name = "kitten", Frames = 100, Seconds = 1 };
            for (int i = 0; i < 5; i++)
                p.Measurings.Add(new MeasuringRecord(i + 1, 1, new SortedDictionary<string, double>
                    { [Metrics.GpuBusy.Name] = 15 + i * 0.01, [Metrics.FrameTime.Name] = frame + i * 0.01, [Metrics.Latency.Name] = latency + i * 0.01 }));
            foreach (var (k, v) in Measure.Merge(p.Measurings.Select(m => (IReadOnlyDictionary<string, double>)m.Values).ToList()))
                p.Values[k] = v;
            return p;
        }
        var vsync = Comparison.Of([("mixed", Paced(16.7, 40))], [("mixed", Paced(33.3, 70))], steps: true).Sentence();
        Equal("GPU time: no change shown (5 sessions a side; the parts' 95 % intervals 0 to 0 %)", vsync.Text, "with Vsync, the display's step isn't another number that moved");
        Equal(true, vsync.Good, "nothing got worse");
        var free = Comparison.Of([("mixed", Paced(16.7, 40))], [("mixed", Paced(33.3, 70))]).Sentence();
        Contains([free.Text], "Frame time 99 % slower in kitten", "without Vsync the frame time is another number that moved");
        // A part only its other numbers moved in, with nothing that decides it (no GPU or frame time)
        var bare = Comparison.Of([("mixed", new PartRecord { Name = "kitten", Frames = 100, Values = { [Metrics.Footprint.Name] = 8000 } })],
            [("mixed", new PartRecord { Name = "kitten", Frames = 100, Values = { [Metrics.Footprint.Name] = 9000 } })]);
        Check(bare.Parts.Single().Decides == null && !bare.Parts.Single().ZonesWanted, "nothing decides it, no zones wanted");
        Contains([bare.Sentence().Text], "Memory 12 % higher in kitten", "its memory moved");
        Equal("mixed kitten Memory 8000 to 9000 MB (12 % higher)", bare.Parts.Single().Line(bare.Parts.Single().Others.Single()), "the line of another number");
        // Parts that went both ways: the counts first
        var both = Comparison.Of(Enumerable.Range(0, 8).Select(i => ("mixed", One($"p{i}", 10, 10, 8000))),
            Enumerable.Range(0, 8).Select(i => ("mixed", One($"p{i}", i < 4 ? 11.2 : 8.93, 10, 8000))));
        Contains([both.Sentence().Text], "GPU time: 4 of 8 parts lower, 4 higher (screen)", "the counts, no mean that rounds to 0");
    }

    private static void GroundAndCounts()
    {
        RunRecord Rec(string power, string vsync, string port, params Finding[] notes) => new()
        {
            Facts = [new("Chip", "Apple M5"), new("Power", power), new("Vsync", vsync), new("Port", port)], Findings = [.. notes],
        };
        var busy = new Finding(Severity.Note, "gpu-busy", "the GPU was busy before the run (40 %): something else may have shared it");
        var unlike = Ground.Unlike([Rec("AC Power", "true, FPS limit 240", "1"), Rec("Battery Power", "false, FPS limit 240", "1")]);
        Equal("Power Vsync", string.Join(' ', unlike.Select(u => u.Key)), "the power source and Vsync differ");
        Equal(0, Ground.Unlike([Rec("AC Power", "true", "1"), Rec("AC Power", "true", "2")]).Count, "another build is what two runs compare");
        Equal(1, Ground.Notes(Rec("AC Power", "true", "1", busy, new Finding(Severity.Note, "network", "no server"))).Count(), "the notes that move numbers");
        Check(Ground.Steps(Rec("AC Power", "true, FPS limit 240", "1")) && !Ground.Steps(Rec("AC Power", "false, FPS limit 240", "1")), "Vsync's steps");
        // A study whose variant sets Vsync isn't warned about it; a variant whose sessions played two builds is
        var warnings = StudyPage.Ground(new Dictionary<string, List<RunRecord>>
        {
            ["base"] = [Rec("AC Power", "true", "1", busy), Rec("AC Power", "true", "1")],
            ["uncapped"] = [Rec("AC Power", "false", "1"), Rec("AC Power", "false", "2")],
        });
        Check(!warnings.Any(w => w.Contains("Vsync", StringComparison.Ordinal)), $"the variant's own Vsync: {string.Join(" | ", warnings)}");
        Contains(warnings, "uncapped's own sessions differ: Port 1 (1), 2 (1).", "a variant's two builds");
        Contains(warnings, "The GPU was busy before the run (40 %): something else may have shared it (in 1 of 4 sessions).", "a note, counted");
        // Shaders compiled in a part count from the frame before it: its first frame's own compiles too
        FrameRow Row(string part, uint shaders, ulong present) =>
            new(present, 1, 1, 0, "earth", part, "Measure", 16.7, present, 8, 100, 1000, 2000, 0, shaders, 0, null, null, null, null, null, null);
        var rows = new List<FrameRow> { Row("earth1", 5, 1), Row("earth1", 7, 2) };
        Equal(3.0, Measure.Of(rows, null, null, null, null, before: Row("", 4, 0))[Metrics.Shaders.Name], "from the frame before");
        Equal(2.0, Measure.Of(rows, null, null, null, null)[Metrics.Shaders.Name], "without one, from the first frame");
    }

    // CPU busy, CPU wait, display latency, displayed time and animation error from KSA Metal's times of each present
    private static void PresentMon()
    {
        var frames = new List<FrameRow>();
        var shown = new Dictionary<ulong, ShownRow>();
        for (int i = 1; i <= 10; i++)
        {
            frames.Add(new FrameRow(i, 1, 1, 0, "earth", "earth1", "Measure", 16.7, (ulong)i, 8, 100, 1000, 2000, 0, 0, 0, null, null, null, null, null, null));
            double began = i * 0.0167;
            // The fifth frame shows a refresh late: its step on screen differs from its step at the start by 16.7 ms
            double late = i >= 5 ? 0.0167 : 0;
            shown[(ulong)i] = new ShownRow((ulong)i, began, began + 0.004, began + 0.012, began + 0.030 + late, 2);
        }
        // A wait for a drawable of 1.5 ms inside every frame, from 1 ms after it began
        var waits = shown.Values.Select(r => new WaitRow(r.Began + 0.001, 1.5, "nextDrawable"))
            // The present that holds it counts once with it
            .Concat(shown.Values.Select(r => new WaitRow(r.Began + 0.0009, 1.4, "vkQueuePresentKHR"))).OrderBy(w => w.Start).ToList();
        var v = Measure.Of(frames, shown, null, null, null, waits);
        Equal(2.4, v[Metrics.CpuBusy.Name], "CPU busy: from the start to the present, less the waits in it, each moment once");
        Equal(3.6, v[Metrics.CpuWait.Name], "CPU wait: for the earlier frame, and in it");
        Equal(4.0, Measure.Of(frames, shown, null, null, null)[Metrics.CpuBusy.Name], "CPU busy without waits");
        Equal(46.7, v[Metrics.Latency.Name], "display latency: the median, from the start to the showing");
        Equal(16.7, v[Metrics.Displayed.Name], "displayed time");
        Equal(Math.Round(16.7 / 9, 2), v[Metrics.AnimationError.Name], "animation error: one step of nine off by 16.7 ms");
    }

    // The zones named with a part's change: those that went its way, past the floors, the most first
    private static void ZonesMoved()
    {
        PartRecord Part(double gpu, params (string Zone, double Ms)[] zones)
        {
            var p = new PartRecord { Name = "earth4", Frames = 100, Seconds = 1 };
            p.Values[Metrics.GpuBusy.Name] = gpu;
            foreach (var (zone, ms) in zones)
                p.Values[Metrics.ZoneName("gpu", zone)] = ms;
            return p;
        }
        var c = Comparison.Of([("earth", Part(10, ("Clouds Raymarching", 3.0), ("Ocean", 2.0), ("Stars", 0.33)))],
            [("earth", Part(11.4, ("Clouds Raymarching", 4.2), ("Ocean", 1.9), ("Stars", 0.36), ("Godrays", 0.3)))]);
        var part = c.Parts.Single();
        Equal(Change.Worse, part.Decides?.Change, "14 % more GPU time: past the screen's 10 %");
        Equal("earth earth4 GPU busy +1.4 ms: Clouds Raymarching +1.20, Godrays +0.30", part.Line(), "the zones that moved its way");
        Contains([c.Sentence().Text], "GPU time 14 % higher in earth4 (screen)", "the sentence");
    }

    // Finish puts the parts' zones and the slow frames the triggers kept into the record
    private static void ZonesInRecord()
    {
        var dir = Run("zoned", frameMs: 20, gpuMs: 8);
        var store = RunStore.For(dir)!;
        store.AddZoneStats(new PartKey(1, 1, "earth", "earth1"), "main", "PrepareFrame", 100, 100, 1500, 1200, 15, 16, 20, 1);
        store.AddZoneStats(new PartKey(1, 1, "earth", "earth1"), "gpu", "GPU", 90, 90, 720, 0, 8, 8, 9, 0);
        store.AddZoneStats(new PartKey(1, 1, "earth", "earth1"), "gpu", "Ocean", 90, 90, 270, 270, 3, 3, 3, 0);
        store.AddMoment(1, 100, 120, new PartKey(1, 1, "earth", "earth1"), "PrepareFrame 101 ms; vkWaitForPresentKHR 95 ms; GPU 97 ms");
        var record = Finished(dir);
        var part = Part(record);
        Equal<double?>(12.0, part[Metrics.ZoneName("main", "PrepareFrame")], "the main thread's zone: own ms a frame of the part");
        Equal<double?>(3.0, part[Metrics.ZoneName("gpu", "Ocean")], "the GPU's zone: own ms a frame of the GPU's profiler");
        Check(part.Measurings.All(m => !m.Values.Keys.Any(Metrics.IsZone)), "no zones in a measuring's own numbers");
        Equal(1, record.Moments.Count, "the slow frame");
        Contains([File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"))], "| earth | earth1 | 120 | PrepareFrame 101 ms; vkWaitForPresentKHR 95 ms; GPU 97 ms |", "report.md's slow frames");
        Contains([File.ReadAllText(Path.Combine(dir, "report.html"))], "Where the time goes", "the page's zones");
    }

    // A run whose folder has options.json, not plan.json: report --again keeps its test, mode and limits, and its facts
    private static void BeforePlans()
    {
        var dir = Run("before-plans", frameMs: 20);
        File.Delete(Path.Combine(RunFiles.Data(dir), RunPlan.FileName));
        File.WriteAllText(Path.Combine(RunFiles.Data(dir), "options.json"), """
            {"script":"","only":"mixed","test":"mixed","mode":"views","warmUp":true,"repeat":2,"session":1,"facts":[{"key":"Power","value":"AC Power"}]}
            """);
        Equal("mixed views", RunPlan.Read(dir) is { } p ? $"{p.Play.Test} {p.Play.Mode}" : "", "the plan from the options");
        Equal("warm-up measured measured", string.Join(" ", RunPlan.Read(dir)!.Passes.Select(RunPlan.Name)), "its passes");
        File.WriteAllText(Path.Combine(RunFiles.Data(dir), RunRecord.FileName), """{"schema":3,"facts":[{"key":"Chip","value":"Apple M5"}]}""");
        var record = Finish.Again(dir);
        Equal("mixed", record.Test, "its test");
        Equal("views", record.Mode, "its mode");
        Equal("Apple M5", record.Fact("Chip"), "the older record's facts");
    }

    // A session's warm-up, measured and profiled passes: the part's numbers are the measured pass's, the profiled pass's
    // apart, and the warm-up is set against the measured pass alone
    private static void ProfiledPass()
    {
        var dir = Path.Combine(_scratch, "profiled");
        Directory.CreateDirectory(dir);
        new RunPlan { Play = new() { Test = "mixed", Mode = "views" }, Passes = [PassKind.WarmUp, PassKind.Measured, PassKind.Profiled] }.Write(dir);
        var events = new EventLog(dir, 1);
        events.Add("session");
        var frames = new FrameLog(dir, 1);
        foreach (var (pass, kind, gpu) in new[] { (0, "warm-up", 10.0), (1, "measured", 10.0), (2, "profiled", 11.3) })
        {
            events.Pass = frames.Pass = pass;
            events.Add("pass", text: kind);
            events.Add("scenario", "earth", text: "views");
            events.Add("open", "earth", "earth1", game: 0, value: 0);
            for (int i = 0; i < 50; i++)
                frames.Add(i, "earth", "earth1", "Measure", 16, new FrameStats { Present = (ulong)(pass * 100 + i + 1), GpuMs = gpu }, null);
            events.Add("close", "earth", "earth1");
            events.Add(kind == "warm-up" ? "warmed" : "done");
        }
        events.Add("ended", text: "quit normally");
        var record = Finished(dir);
        var part = Part(record);
        Equal<double?>(10.0, part[Metrics.GpuBusy], "the measured pass's GPU busy");
        Equal(1, part.Runs, "one measuring");
        Equal<double?>(11.3, part.Profiled?.GetValueOrDefault(Metrics.GpuBusy.Name), "the profiled pass's, apart");
        Equal<double?>(10.0, record.WarmUp?.MeasuredGpuMs, "the warm-up against the measured pass alone");
        Check(!Notes(record).Any(n => n.Contains("warmed up", StringComparison.Ordinal)), "no warming noted");
    }

    private static Image Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(i * 7 % 251);
        return new Image(width, height, rgb);
    }

    // The run's store: the game's run.db and the host's host.db, each process's events read in time order with the other's,
    // the frames, and the zip's copies with the home folder's name as ~
    private static void Store()
    {
        var dir = Run("stored", frameMs: 20, gpuMs: 8);
        RunStore.ProcessFile = RunStore.HostFile;
        new EventLog(dir, 1).Add("session-memory", text: $"pressure normal, swap 0 -> 0 MB, {_scratch}/swap");
        RunStore.ProcessFile = RunStore.GameFile;
        new EventLog(dir, 1).Add("note", text: "after the host's");
        Equal(100, FrameLog.Read(dir).Count, "frames");
        Equal(60.0, FrameLog.Read(dir)[99].FrameMs, "a frame");
        var types = EventLog.Read(dir).Select(e => e.Type).ToList();
        Equal("start", types.FirstOrDefault(), "the first event");
        Equal("session-memory note", string.Join(" ", types.TakeLast(2)), "the host's event, then the game's");
        var record = Finished(dir);
        Equal(0, Notes(record).Length, "notes");
        Check(File.Exists(Path.Combine(RunFiles.Data(dir), RunStore.GameFile)) && File.Exists(Path.Combine(RunFiles.Data(dir), RunStore.HostFile)), "both files");
        var unpacked = Path.Combine(_scratch, "unpacked");
        ZipFile.ExtractToDirectory(Path.Combine(dir, "to-share_stored.zip"), unpacked);
        var events = RunStore.ReadEvents(Path.Combine(unpacked, "stored"));
        Contains(events.Select(e => e.Text ?? ""), "swap 0 -> 0 MB, ~/swap", "the zip's store: the home folder as ~");
        Equal(100, RunStore.ReadFrames(Path.Combine(unpacked, "stored")).Count, "the zip's store: the frames");
    }

    // The records KSA Metal's drain hands over, read back field by field
    private static void Drain()
    {
        var bytes = new MemoryStream();
        var w = new BinaryWriter(bytes);
        void Header(ushort type, ushort size)
        {
            w.Write(type);
            w.Write(size);
            w.Write(0u);
        }
        Header(1, (ushort)(8 + 8 + 88));
        w.Write(12.5);
        w.Write(7UL);
        w.Write(16.6);
        w.Write(9.25);
        foreach (var x in new uint[] { 1200, 30, 22, 3 })
            w.Write(x);
        w.Write(4096L);
        w.Write(1);
        foreach (var x in new uint[] { 5, 6, 3024, 1964, 0 })
            w.Write(x);
        w.Write(8000L);
        w.Write(8.33);
        Header(2, 64);
        w.Write(7UL);
        foreach (var x in new[] { 12.49, 12.5, 12.51, 12.52, 0.4 })
            w.Write(x);
        w.Write(1u);
        w.Write(0u);
        Header(3, 72);
        w.Write(13.0);
        w.Write(4.5);
        w.Write(8UL);
        w.Write(System.Text.Encoding.UTF8.GetBytes("vkWaitForFences".PadRight(40, '\0')));
        Header(4, 40);
        w.Write(14.0);
        w.Write(-(64L << 20));
        w.Write(2u);
        w.Write(0u);
        w.Write(9UL);
        Header(5, 32);
        w.Write(7UL);
        w.Write(12.53);
        w.Write(8.75);
        var records = new List<DriverRecord>();
        DriverStats.Parse(bytes.ToArray(), records);
        Equal(5, records.Count, "records");
        if (records.Count < 5)
            return;
        var present = (PresentRecord)records[0];
        Equal(12.5, present.T, "present's time");
        Equal(7UL, present.Stats.Present, "present");
        Equal(9.25, present.Stats.GpuMs, "GPU ms");
        Equal(1964u, present.Stats.DrawableHeight, "drawable");
        Equal(8.33, present.Stats.DisplayMs, "display ms");
        Equal(new ShownRecord(7, 12.49, 12.5, 12.51, 12.52, 0.4, true), records[1], "shown");
        Equal(new WaitRecord(13, 4.5, 8, "vkWaitForFences"), records[2], "wait");
        Equal(new MemoryRecord(14, -(64L << 20), 2, 9), records[3], "memory");
        Equal(new FrameGpuRecord(7, 12.53, 8.75), records[4], "a frame's own GPU time");
        // GPU busy from each frame's own GPU time where there is one: the frame after a stall carried two frames' work
        FrameRow Row(ulong present, double gpu) => new(present, 1, 1, 0, "earth", "earth1", "Measure", 16.7, present, gpu, 100, 1000, 2000, 0, 0, 0,
            null, null, null, null, null, null);
        var rows = new List<FrameRow> { Row(1, 10), Row(2, 0.5), Row(3, 20), Row(4, 10), Row(5, 10) };
        var own = new Dictionary<ulong, double> { [1] = 10, [2] = 10, [3] = 10, [4] = 10, [5] = 10 };
        Equal(10.0, Measure.Of(rows, null, null, null, null, frameGpu: own)[Metrics.GpuP95.Name], "P95 of the frames' own: 10");
        Equal(20.0, Measure.Of(rows, null, null, null, null)[Metrics.GpuP95.Name], "P95 of what completed between presents: 20");
        var few = new Dictionary<ulong, double> { [1] = 10 };
        Equal(20.0, Measure.Of(rows, null, null, null, null, frameGpu: few)[Metrics.GpuP95.Name], "a part its own times barely cover: the column, never both");
        // Each game session counts its presents from 1: the store keeps them apart
        var dir = Path.Combine(_scratch, "sessions-gpu");
        Directory.CreateDirectory(dir);
        var store = RunStore.For(dir)!;
        store.Add(new FrameGpuRecord(1, 1.0, 10), session: 1);
        store.Add(new FrameGpuRecord(1, 9.0, 30), session: 2);
        store.Add(new ShownRecord(1, 1, 1.01, 1.02, 1.03, 0, false), session: 1);
        store.Add(new ShownRecord(1, 9, 9.01, 9.02, 9.05, 0, false), session: 2);
        RunStore.Close(dir);
        var byGpu = RunStore.ReadFrameGpu(dir);
        Equal(10.0, byGpu[1][1], "session 1's present 1");
        Equal(30.0, byGpu[2][1], "session 2's present 1");
        Equal(9.05, RunStore.ReadShown(dir)[2][1].Shown, "session 2's shown record of its present 1");
    }

    // The game's profiler zones summed per part, and a slow frame kept as a moment with its cause and slices
    private static void ZoneSums()
    {
        var dir = Path.Combine(_scratch, "zones");
        Directory.CreateDirectory(dir);
        var names = new[] { "Main Thread", "Render", "RecordCommandBuffer", "VehicleUpdate" };
        var log = new ZoneLog(RunStore.For(dir), i => names[i]);
        var part = new PartKey(1, 1, "earth", "earth1");
        double t = 100;
        // 10 frames of 16 ms: Render 10 ms, inside it RecordCommandBuffer 4 ms (allocating 1 MB)
        for (int f = 0; f < 10; f++, t += 0.016)
        {
            log.Add(new ZoneFrame("main", "Main Thread", [new(0, -1, t, 16, 0), new(1, 0, t + 0.001, 10, 0), new(2, 1, t + 0.002, 4, 1 << 20)]));
            log.EndFrame(t + 0.016, part);
        }
        // A slow frame: 120 ms, VehicleUpdate 100 of them; the driver waited 95 ms for a present, whose GPU took 97
        log.Add(new ZoneFrame("main", "Main Thread", [new(0, -1, t, 120, 0), new(3, 0, t + 0.001, 100, 0)]));
        log.AddWait(t + 0.002, 95, "vkWaitForPresentKHR");
        log.AddPresent(t + 0.119, 97);
        t += 0.120;
        log.EndFrame(t, part);
        log.Hitch(t, 120, part);
        // The frame after it, 20 ms, kept too: its cause is its own, not the slow frame's that ended just before it
        log.Add(new ZoneFrame("main", "Main Thread", [new(0, -1, t + 0.0005, 19.5, 0), new(3, 0, t + 0.001, 18, 0)]));
        t += 0.020;
        log.EndFrame(t, part);
        log.Hitch(t, 20, part);
        for (int f = 0; f < 20; f++, t += 0.016)
        {
            log.Add(new ZoneFrame("main", "Main Thread", [new(0, -1, t, 16, 0), new(1, 0, t + 0.001, 10, 0)]));
            log.EndFrame(t + 0.016, part);
        }
        log.EndFrame(t + 0.02, null);
        log.Close();
        RunStore.CloseAll();
        using var db = Sqlite.Open(Path.Combine(RunFiles.Data(dir), RunStore.GameFile));
        using (var q = db.Prepare("SELECT frames, calls, total_ms, self_ms, median_ms, max_ms, alloc_mb FROM zone_stats WHERE zone = 'Render'"))
        {
            Check(q.Step(), "Render's numbers");
            Equal(30L, q.Long(0), "Render: frames");
            Equal(30L, q.Long(1), "Render: calls");
            Equal(300.0, Math.Round(q.Double(2), 3), "Render: total ms");
            Equal(260.0, Math.Round(q.Double(3), 3), "Render: self ms (less RecordCommandBuffer's)");
            Equal(10.0, q.Double(4), "Render: median ms a frame");
            Equal(10.0, q.Double(5), "Render: worst");
        }
        using (var q = db.Prepare("SELECT median_ms, max_ms, alloc_mb FROM zone_stats WHERE zone = 'RecordCommandBuffer'"))
        {
            Check(q.Step(), "RecordCommandBuffer's numbers");
            Equal(0.0, q.Double(0), "RecordCommandBuffer: median, frames without it as 0");
            Equal(4.0, q.Double(1), "RecordCommandBuffer: worst");
            Equal(10.0, Math.Round(q.Double(2), 3), "RecordCommandBuffer: MB allocated");
        }
        using (var q = db.Prepare("SELECT id, frame_ms, part, cause FROM moments ORDER BY id"))
        {
            Check(q.Step(), "a moment");
            Equal(120.0, q.Double(1), "the moment's frame");
            Equal("earth1", q.Text(2), "the moment's part");
            Check(q.Text(3).StartsWith("VehicleUpdate 100.0 ms", StringComparison.Ordinal), $"the cause: {q.Text(3)}");
            Contains([q.Text(3)], "waited in vkWaitForPresentKHR 95.0 ms", "the cause's wait");
            Contains([q.Text(3)], "GPU up to 97.0 ms a frame", "the cause's GPU time");
            Check(q.Step(), "the frame after it");
            Check(q.Text(3).StartsWith("VehicleUpdate 18.0 ms", StringComparison.Ordinal), $"the frame after it, its own cause: {q.Text(3)}");
            Check(!q.Step(), "two moments");
        }
        using (var q = db.Prepare("SELECT COUNT(*), MAX(ms) FROM slices WHERE moment = 1"))
        {
            q.Step();
            Check(q.Long(0) > 3, "the moment's slices");
            Equal(120.0, q.Double(1), "the slow frame among them");
        }
    }

    private static void Rules()
    {
        var rule = Triggers.Parse("Frame > 50 ms: moment, screenshot, max 5");
        Equal(Condition.Frame, rule.When, "frame's condition");
        Equal(50.0, rule.Ms, "frame's limit");
        Equal("moment,screenshot", string.Join(",", rule.Actions.Select(Triggers.Name)), "the actions in order");
        Equal(5, rule.Max, "max");
        Equal("frame>50ms: moment, screenshot, max 5", rule.ToString(), "as written");
        Equal(rule.ToString(), Triggers.Parse(rule.ToString()).ToString(), "read again as written");
        // A limit in ms reaches the game as it was written (plan.json has the rules as text)
        foreach (var ms in new[] { 33.33333, 0.0001, 16.7 })
            Equal(ms, Triggers.Parse(Triggers.Parse(FormattableString.Invariant($"frame>{ms}ms: moment")).ToString()).Ms, $"frame>{ms}ms read again");
        Equal("broken: passes, capture", Triggers.Parse("broken: passes, capture, passes").ToString(), "an action once");
        Equal(3, Triggers.Parse("broken: passes").Max, "broken's default max");
        foreach (var (text, says) in new[]
                 {
                     ("frame>50ms moment", "a rule is"), ("frame>0ms: moment", "more than 0 ms"), ("warp: stop", "no condition 'warp'"),
                     ("frame>50ms: dance", "no action 'dance'"), ("frame>50ms: sample", "frame>50ms can't sample"),
                     ("checkpoint: stop", "checkpoint can't stop"), ("stuck: max 2", "no action"), ("broken: passes, max 0", "max takes"),
                     ("frame>50ms: 1", "no action '1'"),
                 })
        {
            try
            {
                Triggers.Parse(text);
                Check(false, $"'{text}' read as a rule");
            }
            catch (FormatException e)
            {
                Contains([e.Message], says, $"'{text}'");
            }
        }
        // The defaults, and a rule of the run's own in a default's place
        Equal("frame>100ms: moment; stuck: sample, stop", string.Join("; ", Triggers.For([])), "the defaults");
        Equal("frame>50ms: capture, max 1; checkpoint: passes; stuck: sample, stop",
            string.Join("; ", Triggers.For(["frame>50ms: capture, max 1", "checkpoint: passes"])), "own rules first, the other defaults after");
        Check(Triggers.HoldCheckpoints(Triggers.For(["checkpoint: passes"])), "a pass dump at checkpoints holds them");
        Check(!Triggers.HoldCheckpoints(Triggers.For(["frame>50ms: passes"])), "a slow frame's pass dump holds no checkpoint");
        Check(Triggers.TakeMinutes(Triggers.For(["frame>50ms: passes"])), "a slow frame's pass dump takes minutes");
        Check(!Triggers.TakeMinutes(Triggers.For([])), "the defaults take no minutes");
        // A run's plan: its rules as written, one that can't be read left out
        Equal("broken: screenshot; frame>100ms: moment; stuck: sample, stop",
            string.Join("; ", Triggers.For(new RunPlan { On = ["broken: screenshot", "nonsense"] })), "the rules of a run's plan");
    }

    private static void Disturbed()
    {
        // Two sessions; a trigger's capture worked while the first measured earth1
        var dir = Run("disturbed", sessions: 2);
        new EventLog(dir, 1).Add("disturbed", "earth", "earth1", text: "the trigger's capture worked while it measured");
        var record = Finished(dir);
        Equal(1, Part(record).Runs, "the disturbed measuring left out");
        Contains(Notes(record), "earth earth1: left out of the numbers, the trigger's capture worked while it measured", "the note");
        Equal(Status.Passed, record.Status, "a note, not a failure");
        // Disturbed in every session: no numbers for the part
        var all = Run("disturbed everywhere");
        new EventLog(all, 1).Add("disturbed", "earth", "earth1", text: "the trigger's passes worked while it measured");
        Equal(0, Finished(all).Scenarios.Single().Parts.Count, "no part left");
    }

    private static void YourGame()
    {
        // As the tab's Record leaves it (Runs.BeginHere, Director's here mode): one scenario and part, here, in the running game
        var dir = Path.Combine(_scratch, "here");
        Directory.CreateDirectory(dir);
        new RunPlan
        {
            Play = new() { Test = "here", Mode = "flight", Only = "here", Stay = true, Here = 20 },
            Host = new() { InGame = true, Facts = [new("port settings", "defaults")] },
        }.Write(dir);
        var events = new EventLog(dir, 1);
        events.Add("start", text: "in the running game, as it was played, from its settings");
        events.Add("session");
        events.Add("scenario", "here", text: "flight");
        events.Add("open", "here", "here", game: 100, value: 0);
        var frames = new FrameLog(dir, 1);
        for (int i = 0; i < 100; i++)
            frames.Add(i * 0.016, "here", "here", "Measure", 16, null, null);
        events.Add("close", "here", "here");
        events.Add("done");
        events.Add("ended", text: "kept running");
        // A mod's error while the player played: theirs, noted, not the run's problem
        File.WriteAllText(Path.Combine(RunFiles.Data(dir), "game.log"), "12:00:01 ERROR SomeMod: could not find its part\n");
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "passed");
        Contains(Notes(record), "the game logged 1 error while it was played", "the player's own errors noted");
        Equal("Your Game", Pages.Title(record), "its title");
        Equal(100, Part(record).Frames, "its frames");
        var report = File.ReadAllText(Path.Combine(RunFiles.Data(dir), "report.md"));
        Contains([report], "no two recordings play the same", "what it is");
        Check(!report.Contains("slowest", StringComparison.Ordinal), "no slowest of one part");
    }

    // A study's file: its lines, its variants (named, and a grid), the baseline, the order of the queue, what it refuses
    private static void StudyFile()
    {
        var study = Study.Parse([
            "# cloud light, at two presets",
            "measure mixed --defaults --repeat 1 --on 'frame>50ms: moment'",
            "sessions 3",
            "variant main",
            "variant light2 --port cloud_light=2   # fewer samples",
            "axis --preset low high",
            "baseline main high",
        ]);
        Equal("measure", study.Verb, "the verb");
        Equal("mixed --defaults --repeat 1 --on frame>50ms: moment", string.Join(' ', study.Command), "the command, a quoted word kept whole");
        Equal("main low,main high,light2 low,light2 high", string.Join(',', study.Variants.Select(v => v.Name)), "the variants, each with every value of the axis");
        Equal("main high", study.Baseline.Name, "the baseline named");
        Equal("--port cloud_light=2 --preset high", string.Join(' ', study.Variants[3].Options), "a variant's options, its axis's last");
        Equal("mixed --defaults --repeat 1 --on frame>50ms: moment --preset low", string.Join(' ', study.Arguments(study.Variants[0])), "a variant's command line");
        var queue = study.Queue();
        Equal(12, queue.Count, "3 sessions of 4 variants");
        Equal("main low,main high,light2 low,light2 high,light2 high,light2 low,main high,main low,main low",
            string.Join(',', queue.Take(9).Select(q => q.Variant.Name)), "in turn, the next round the other way");
        Equal("05-light2_high", queue[4].Folder(queue.Count), "a session's folder");
        Equal("05-light2_high-2", queue[4].Folder(queue.Count, 2), "played again");
        Equal("base", Study.Parse(["measure mixed"]).Variants.Single().Name, "no variants: one, the command as it is");
        Equal(Study.DefaultSessions, Study.Parse(["measure mixed"]).Sessions, "5 sessions unless said");
        Equal("low,medium", string.Join(',', Study.Parse(["profile", "axis --preset low medium"]).Variants.Select(v => v.Name)), "an axis alone names its variants");
        foreach (var w in new[] { "plain", "frame>50ms: moment", "it's", "#1" })
            Equal(w, Study.Words(Study.Quote(w)).Single(), $"{w} quoted and read back");
        foreach (var (lines, says) in new (string[], string)[]
        {
            (["variant a"], "no command"),
            (["measure mixed", "measure tour"], "one command"),
            (["measure mixed --sessions 3"], "--sessions isn't for a study"),
            (["measure mixed", "variant b --ab x=1"], "--ab isn't for a study"),
            (["measure mixed", "variant a", "variant a --preset low"], "two variants named a"),
            (["measure mixed", "axis --preset low"], "two values or more"),
            (["measure mixed", "sessions 0"], "1 to 100"),
            (["measure mixed", "baseline nobody"], "no variant nobody"),
            (["measure mixed", "repeat 3"], "'repeat' isn't a study's line"),
            (["measure mixed --on 'frame>50ms"], "a quote (') left open"),
            (["measure mixed", "variant a/b", "variant a-b"], "would share a folder"),
        })
        {
            try
            {
                Study.Parse(lines);
                Failures.Add($"{_test}: accepted {string.Join(" / ", lines)}");
            }
            catch (StudyException e)
            {
                Check(e.Message.Contains(says, StringComparison.Ordinal), $"refused with '{says}': {e.Message}");
            }
        }
    }

    // A study's variants: each run a session, merged per variant and judged against the baseline by those sessions; the
    // page and report.md from them
    private static void StudyVariants()
    {
        double[] jitter = [0, 0.1, -0.1, 0.05, -0.05];
        var a = jitter.Select((j, i) => Finished(Run($"a{i}", frameMs: 20, gpuMs: 10 + j))).ToList();
        var b = jitter.Select((j, i) => Finished(Run($"b{i}", frameMs: 20, gpuMs: 12 + j))).ToList();
        var merged = Study.Merged(b);
        var part = merged.Single().Part;
        Equal(5, part.Sessions(Metrics.GpuBusy).Length, "each run a session of its own");
        Equal("12,12.1,11.9,12.05,11.95", string.Join(",", part.Sessions(Metrics.GpuBusy)), "the sessions in the order the runs played");
        Equal(5, part.Runs, "the part's runs");
        var c = Comparison.Of(Study.Merged(a), merged);
        Equal(Basis.Sessions, c.Basis, "judged by sessions");
        Equal(Change.Worse, c.Parts.Single().Decides!.Change, "20 % more GPU time in every session");
        Contains([c.Sentence().Text], "GPU time 20 % higher in earth1 (5 sessions a side)", "the sentence");
        var (sentence, good) = c.Sentence();
        var page = new StudyPage("2026-11-08_0100_light", "./benchmark.sh measure mixed", "base", [], 5,
            [new StudyPage.Variant("light2", ["--port", "cloud_light=2"], c, sentence, good, [], 0, null),
             new StudyPage.Variant("broken", [], null, "no session of it passed yet", null, [], 0, null)],
            [new StudyPage.Session(1, 1, "base", "01-base", "passed"), new StudyPage.Session(2, 1, "light2", null, "to play")], a[0], []);
        var md = page.Markdown();
        Contains([md], "| light2 | **GPU time 20 % higher in earth1 (5 sessions a side)** |", "report.md: the variant's verdict, marked when worse");
        Contains([md], "| earth | earth1 | GPU busy | 10.0 ms | **20 % slower** (", "report.md: the part's row, with its interval");
        Contains([md], "- light2: earth earth1 GPU busy +2.0 ms (20 % slower)", "report.md: what moved");
        Contains([md], "| 2 | 1 | light2 | to play |", "report.md: a session still to play");
        Check(!page.Markdown(terminal: true).Contains("| Session |", StringComparison.Ordinal), "the terminal's summary: no sessions");
        Directory.CreateDirectory(Path.Combine(_scratch, "study"));
        Pages.WriteStudy(Path.Combine(_scratch, "study"), page);
        var html = File.ReadAllText(Path.Combine(_scratch, "study", "report.html"));
        Contains([html], "20 % slower", "the page's matrix");
        Contains([html], "01-base/report.html", "the page links each session's run");
        Contains([html], "no session of it passed yet", "a variant with nothing to compare");
    }

    // A probe's counters in the parts' numbers (the median of the part's frames, or the most), its metric registered and
    // carried by the record, judged by a comparison; the census in the record, the report and the page
    private static void ProbeNumbers()
    {
        RunRecord Probed(string name, double vram)
        {
            var dir = Run(name, frameMs: 20, gpuMs: 10);
            var store = RunStore.For(dir)!;
            store.AddMetric(new Metric("mem/game_vram", "Game VRAM", "MB", Better.Lower, Across.Median, 0, "the device memory the game allocated") { Limit = 5 });
            store.AddMetric(new Metric("probe/sim/tick", "tick", "ms", Better.Lower, Across.Most, 2, "the slowest vehicle job's last tick") { Limit = 5 });
            for (int i = 0; i < 10; i++)
            {
                store.AddCounter(i, 1, 1, "earth", "earth1", "mem/game_vram", vram + (i % 3));
                store.AddCounter(i, 1, 1, "earth", "earth1", "probe/sim/tick", i == 7 ? 4 : 1);
                store.AddCounter(i, 1, 1, "earth", null, "probe/sim/tick", 99);
            }
            store.AddCensus(5, 1, 1, "earth", "earth1", "Planet Textures", "game", 300L << 20);
            store.AddCensus(5, 1, 1, "earth", "earth1", "Shadow Cascades", "game", 100L << 20);
            store.AddCensus(5, 1, 1, "earth", "earth1", "private", "driver", 420L << 20);
            store.AddCensus(5, 1, 1, "earth", "earth1", "Planet Textures <- Streamer.Load", "site", 300L << 20);
            return Finished(dir);
        }
        var a = Probed("probed-a", 5000);
        var part = Part(a);
        Equal<double?>(5001.0, part["mem/game_vram"], "a counter: the median of the part's frames");
        Equal<double?>(4.0, part["probe/sim/tick"], "a counter that counts the most: its most, the frames outside the part left out");
        Check(part.Measurings[0].Values.ContainsKey("mem/game_vram"), "a counter in the measuring's own numbers (a session's sample)");
        Equal("mem/game_vram,probe/sim/tick", string.Join(",", a.ProbeMetrics.Select(m => m.Name)), "the record carries the probes' metrics");
        Equal("Game VRAM", RunRecord.Read(Path.Combine(_scratch, "probed-a"))?.ProbeMetrics[0].Label, "read back");
        Equal(5, a.Census.Count, "the census: its 4 owners and the game's total");
        Equal(400.0, a.Census.Single(c => c.Kind == "game-total").Mb, "the game's tags all together");
        var md = File.ReadAllText(Path.Combine(_scratch, "probed-a", RunFiles.DataFolder, "report.md"));
        Contains([md], "| earth | earth1 | 5001 | 4.00 |", "report.md's probe numbers");
        Contains([md], "- earth earth1: game 400 (Planet Textures 300, Shadow Cascades 100), KSA Metal 420, .NET heap 0", "report.md's census");
        var page = File.ReadAllText(Path.Combine(_scratch, "probed-a", "report.html"));
        Contains([page], "Who holds the memory", "the page's census");
        Contains([page], "Planet Textures &lt;- Streamer.Load", "another probe's census kind, folded");
        // The host's counters (the power channel) fall in the part open at their time
        var hosted = Run("hosted", frameMs: 20, gpuMs: 10);
        double from = 0, to = 0;
        RunStore.Flush(hosted);  // the part's open and close in the file, not still on the writer's queue
        RunStore.Query(Path.Combine(RunFiles.Data(hosted), RunStore.GameFile), "SELECT t, type FROM events WHERE type IN ('open', 'close')", q =>
        {
            if (q.Text(1) == "open")
                from = q.Double(0);
            else
                to = q.Double(0);
        });
        RunStore.ProcessFile = RunStore.HostFile;
        var host = RunStore.For(hosted)!;
        host.AddMetric(new Metric("host/gpu_power", "GPU power", "W", Better.Neither, Across.Median, 1, "the GPU's power"));
        host.AddCounter((from + to) / 2, 1, 0, null, null, "host/gpu_power", 20);
        host.AddCounter(to + 10, 1, 0, null, null, "host/gpu_power", 99);
        RunStore.Close(hosted);
        RunStore.ProcessFile = RunStore.GameFile;
        var hostedPart = Part(Finished(hosted));
        Equal<double?>(20.0, hostedPart["host/gpu_power"], "a host counter in the part open at its time, one after it left out");
        Equal<double?>(408.0, hostedPart[Metrics.GpuEnergy], "the GPU's energy a frame: 20 W times the mean frame (99 of 20 ms, one of 60)");
        var c = Comparison.Of(a.AllParts().Select(x => (x.Scenario.Name, x.Part)), Probed("probed-b", 5600).AllParts().Select(x => (x.Scenario.Name, x.Part)));
        var vram = c.Parts.Single().Verdicts.FirstOrDefault(v => v.Metric.Name == "mem/game_vram");
        Check(vram != null, "a comparison judges a probe's metric");
        Equal(Change.Worse, vram?.Change, "12 % more game VRAM, past its 5 % limit");
    }
}
