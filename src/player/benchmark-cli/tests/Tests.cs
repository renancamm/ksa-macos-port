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
// what the director writes. The build runs them (ctest); a failure stops the build.
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
            ("an A/B run's memory on each side", SideMemory),
            ("numbers in any region", AnyRegion),
            ("finish", FinishZip),
            ("comparing two runs", Compare),
            ("comparing runs of different modes", CompareModes),
            ("a timed part's numbers", TimedPart),
            ("the Warp Test in the report", WarpReport),
            ("the PNG reader and writer", Png),
            ("a run stopped on purpose", Stopped),
            ("a screenshot of nothing", BlankShot),
            ("runs that can't compare", NothingInCommon),
            ("an older run's report", OlderRun),
            ("the list of runs", ListOfRuns),
            ("the terminal's layout", Layout),
            ("the tests' script", Script),
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

    // A run's folder as the director and benchmark.sh leave it: its options with these facts, one session that played the
    // scenario earth (of this kind) with one part, earth1, of 100 frames (one of them a hitch), and ended
    private static string Run(string name, double frameMs = 10, string mode = "views", Fact[]? facts = null, double? gpuMs = null,
        Kind kind = Kind.Views, string ending = "quit normally", bool done = true, int sessions = 1)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(dir);
        new RunOptions { Test = "mixed", Mode = mode, Facts = [new("Power", "AC Power"), new("Low Power Mode", "off"), new("port settings", "defaults"), .. facts ?? []] }
            .Write(dir);
        var events = new EventLog(dir);
        events.Add("start", text: "a new game, from benchmark.sh");
        for (int n = 1; n <= sessions; n++)
            Session(dir, events, n, frameMs, gpuMs, kind, ending, done);
        return dir;
    }

    // One game session of the scenario earth: its part's frames, and its ending
    private static void Session(string dir, EventLog events, int session, double frameMs, double? gpuMs, Kind kind, string ending = "quit normally",
        bool done = true, Action<EventLog>? during = null, uint shaders = 0, string? variant = null)
    {
        events.Session = session;
        events.Add("session", text: variant);
        events.Add("loaded", value: 1.5);
        events.Add("scenario", "earth", text: Lineup.Name(kind));
        events.Add("open", "earth", "earth1", game: 0, value: 0);
        using (var frames = new FrameLog(dir, session))
        {
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
        new RunOptions { Test = "warp", Mode = "timed", Facts = [new("port settings", "defaults")] }.Write(dir);
        var events = new EventLog(dir, 1);
        events.Add("session");
        events.Add("scenario", "warpdebris", text: "timed");
        events.Add("open", "warpdebris", "debris", game: 1000, value: 0);
        using (var frames = new FrameLog(dir, 1))
            foreach (var r in Warped(heldBack))
                frames.Add(r.Script, r.Scenario, r.Part, r.Phase, r.FrameMs, null,
                    new SimSample(r.GameSeconds!.Value, r.Asked!.Value, r.SimMs!.Value, r.Substeps!.Value, r.Bubbles!.Value, r.Vehicles!.Value));
        events.Add("close", "warpdebris", "debris");
        events.Add("done");
        events.Add("ended", text: "quit normally");
        if (log != null)
            File.WriteAllText(Path.Combine(dir, "game.log"), log);
        return dir;
    }

    private static void Passed()
    {
        var dir = Run("passed");
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "passed");
        Equal(0, Problems(record).Length, "problems");
        Equal(0, Notes(record).Length, "notes");
        var part = Part(record);
        Equal(100, part.Frames, "frames");
        Equal(10.0, part.FrameMsMedian, "frame time's median");
        Equal(30.0, part.FrameMsMax, "worst frame");
        Equal(1, part.Hitches, "hitches");
        Equal(1.5, record.LoadSeconds, "loading");
        Equal(Status.Passed, RunRecord.Read(dir)?.Status, "run.json read back");
        var md = File.ReadAllText(Path.Combine(dir, "report.md"));
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
        File.WriteAllLines(Path.Combine(dir, "game.log"),
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
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "**Failed**: 10 problems", "report.md");
    }

    private static void Notes()
    {
        var dir = Path.Combine(_scratch, "notes");
        Directory.CreateDirectory(dir);
        new RunOptions
        {
            Test = "mixed", Mode = "views",
            Facts = [new("Power", "Battery Power"), new("Low Power Mode", "on"), new("KSA Metal switches", "KSA_MTL_GPU_PROFILE=x.csv"), new("Tools", "Instruments"),
                new("port settings", "defaults")]
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
        Contains(notes, "KSA Metal switches were set (KSA_MTL_GPU_PROFILE=x.csv)", "KSA Metal switches");
        Contains(notes, "measured with Instruments", "tools");
        Contains(notes, "the GPU was busy before the run (90 %)", "a busy GPU");
        Contains(notes, "crash report, dotnet-2026-10-26.ips", "a crash report");
        Contains(notes, "swapped memory to disk during the run (100.00 -> 600.00 MB)", "swap");
        Contains(notes, "shaders compiled while measuring earth earth1 (3)", "shaders compiled");
        Equal(8, notes.Length, "notes");
        // A line of the events cut short (a game that died writing it) is left out
        File.AppendAllText(Path.Combine(dir, EventLog.FileName), "{\"type\":\"err");
        Equal(Status.Passed, Finished(dir).Status, "an event cut short");
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
        Equal(12.0, part.FrameMsMedian, "frame time: the median of the runs");
        Equal("10,20", string.Join(",", part.FrameMsRange ?? []), "frame time's range");
        Equal(3, part.Runs, "the part's runs");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "| 12.0 (10.0-20.0) |", "report.md's range");
        // A part that never got to its speed in one repeat never did
        var merged = Summary.Merge([Summary.Of("debris", Warped(1), 1000, 0), Summary.Of("debris", Warped(0.5), 1000, 0), Summary.Of("debris", Warped(1), 1000, 0)]);
        Equal<double?>(null, merged.Warp?.SecondsToSpeed, "seconds to speed: never, in one of the runs");
    }

    // Each side's sessions: the worst pressure, the swap grown in all of them
    private static void SideMemory()
    {
        var dir = Run("side-memory", sessions: 0);
        var events = new EventLog(dir);
        Action<EventLog> Memory(string text) => e => e.Add("session-memory", text: text);
        Session(dir, events, 1, 10, 10, Kind.Views, variant: "A", during: Memory("pressure normal, swap 100.00 -> 100.00 MB"));
        Session(dir, events, 2, 10, 10, Kind.Views, variant: "B", during: Memory("pressure warning, swap 100.00 -> 400.00 MB"));
        Session(dir, events, 3, 10, 10, Kind.Views, variant: "A", during: Memory("pressure normal, swap 400.00 -> 450.00 MB"));
        Session(dir, events, 4, 10, 10, Kind.Views, variant: "B", during: Memory("pressure normal, swap 450.00 -> 650.00 MB"));
        Session(dir, events, 5, 10, 10, Kind.Views, variant: "A", during: Memory("pressure critical, swap ? -> ? MB"));
        var record = Finished(dir);
        Equal("worst memory pressure critical, swap grew 50 MB in 3 game sessions", record.Fact("While A played"), "side A");
        Equal("worst memory pressure warning, swap grew 500 MB in 2 game sessions", record.Fact("While B played"), "side B");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "macOS's memory while each side played: A worst memory pressure critical",
            "report.md's line");
        Equal<string?>(null, Finished(Run("plain-memory")).Fact("While A played"), "a run without sides");
    }

    // A Mac whose region writes 16,7 for 16.7: the record's numbers stay the same
    private static void AnyRegion()
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        var dir = Run("region", frameMs: 16.7);
        new EventLog(dir).Add("memory", text: "pressure normal, swap 100.00 -> 600.00 MB");
        var record = Finished(dir);
        Equal(Status.Passed, record.Status, "passed");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "| 16.7 |", "report.md's frame time");
        Equal(16.7, Part(record).FrameMsMedian, "the record's frame time");
        Contains(Notes(record), "swapped memory", "swap read with a decimal point");
    }

    // A run from benchmark.sh: its options without the port settings and the hooks, which come from the game's log
    private static void FinishZip()
    {
        var dir = Path.Combine(_scratch, "2026-10-26_0100");
        Directory.CreateDirectory(dir);
        new RunOptions { Test = "mixed", Mode = "views" }.Write(dir);
        Session(dir, new EventLog(dir), 1, 10, null, Kind.Views);
        File.WriteAllLines(Path.Combine(dir, "game.log"),
            ["==> port settings: render_scale=0.75", "[hooks] TerrainCache: skipped (KSA's code moved)", "[hooks] OutlineSkip: on"]);
        var record = Finished(dir);
        Equal(Status.Failed, record.Status, "a skipped hook fails the run");
        Equal("render_scale=0.75", record.Fact("port settings"), "the port settings, from the log");
        Equal("TerrainCache: skipped (KSA's code moved); OutlineSkip: on", record.Fact("Hooks"), "the hooks, from the log");
        Contains(Problems(record), "the hooks' TerrainCache: skipped", "the skipped hook");
        Contains(Notes(record), "port settings were changed (render_scale=0.75)", "the port settings");
        using var zip = ZipFile.OpenRead(Path.Combine(dir, "2026-10-26_0100.zip"));
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        foreach (var file in new[] { "run.json", "report.md", "report.html", "events.jsonl", "frames.csv", "options.json", "game.log" })
            Contains(names, $"2026-10-26_0100/{file}", "the zip");
    }

    private static void Compare()
    {
        var a = Run("a", frameMs: 10);
        var b = Run("b", frameMs: 11);
        var view = Gradient(64, 48);
        Images.Write(Path.Combine(a, "earth_earth1.png"), view);
        Images.Write(Path.Combine(b, "earth_earth1.png"), view);
        foreach (var dir in new[] { a, b })
            new EventLog(dir, 1).Add("shot", "earth", "earth1", "earth_earth1.png", value: 1);
        Finished(a);
        Finished(b);
        var (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(0, status, "exit status: the same screenshots");
        Contains([output], "Same image (1 of 1 screenshots)", "the verdict");
        Check(!output.Contains("|", StringComparison.Ordinal), "aligned columns, no Markdown");
        var written = File.ReadAllText(Path.Combine(b, "compare.md"));
        Contains([written], "| 10.0 | 11.0 | **10 % slower** |", "a part 10 % slower, marked");
        Contains([written], "| earth_earth1.png | 0.00% | 0.0 | same (up to 1%) |", "the same screenshot");
        Contains([written], "Same Mac, same version, same settings.", "the settings");
        Check(File.Exists(Path.Combine(b, "compare.html")), "the page");

        // Half the view changed
        var changed = Gradient(64, 48);
        Array.Fill(changed.Rgb, (byte)255, 0, changed.Rgb.Length / 2);
        Images.Write(Path.Combine(b, "earth_earth1.png"), changed);
        (status, output) = Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        Equal(1, status, "exit status: a screenshot differs");
        Contains([output], "differs (up to 1%)", "the screenshot that differs");
        Check(File.Exists(Path.Combine(b, "diff", "earth_earth1.png")), "the marked copy in b/diff/");

        // Within noise
        var c = Run("c", frameMs: 10.2);
        Finished(c);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, c));
        Contains([File.ReadAllText(Path.Combine(c, "compare.md"))], "| 10.0 | 10.2 | +2 % |", "a change within noise, unmarked");
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
        var reached = Summary.Of("debris", Warped(1), 1000, 0).Warp!;
        Equal(1000.0, reached.SpeedAsked, "the speed asked for");
        Check(reached.Speed is > 700 and < 800, $"the speed over the whole part: {reached.Speed}");
        Check(reached.SecondsToSpeed is > 5 and < 7, $"the seconds to 90 %: {reached.SecondsToSpeed}");
        Check(reached.GameDays is >= 0.17 and <= 0.18, $"the game days: {reached.GameDays}");
        Equal(1598L, reached.Substeps, "the substeps");
        Equal(12, reached.BubblesMax, "the most bubbles");
        Equal<double?>(null, Summary.Of("debris", Warped(0.5), 1000, 0).Warp!.SecondsToSpeed, "a part that never gets to 90 %");
        Equal<WarpRecord?>(null, Summary.Of("earth1", Warped(1), null, null).Warp, "a part of another kind");
    }

    private static void WarpReport()
    {
        var a = WarpRun("a", 1);
        Finished(a);
        var md = File.ReadAllText(Path.Combine(a, "report.md"));
        Contains([md], "**0.2 game days** in time warp", "the headline");
        Contains([md], "Time warp: 0.2 game days", "the table's line");
        Contains([md], "| warpdebris | debris | 0.17 |", "the part's row");
        var b = WarpRun("b", 0.5);
        Finished(b);
        Said(() => KsaMetal.Benchmark.Cli.Program.Compare(a, b));
        var written = File.ReadAllText(Path.Combine(b, "compare.md"));
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

    private static void Stopped()
    {
        var dir = Run("stopped", ending: "was stopped with Ctrl-C", done: false);
        new EventLog(dir, 1).Add("stopped", text: "stopped with Ctrl-C");
        var record = Finished(dir);
        Equal(Status.Stopped, record.Status, "stopped");
        Equal(0, Problems(record).Length, "no problems");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "**Stopped**", "report.md");
        // Stopped, with something wrong besides
        var both = Run("both", ending: "was stopped with Ctrl-C", done: false);
        var events = new EventLog(both, 1);
        events.Add("error", "earth", text: "12.0 s: '@launch' failed");
        events.Add("stopped", text: "stopped with Esc");
        var bothRecord = Finished(both);
        Equal(Status.Stopped, bothRecord.Status, "stopped with Esc");
        Contains(Problems(bothRecord), "'@launch' failed", "a real problem stays");
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
        Images.Write(Path.Combine(dir, "earth_earth1.png"), black);
        Contains(Problems(Finished(dir)), "earth_earth1.png: the screenshot is one colour", "the problem");
        // The sky gone white around a planet, as when the game's light breaks: a picture, but not a right one
        var flood = new Image(32, 32, new byte[32 * 32 * 3]);
        Array.Fill(flood.Rgb, (byte)255, 0, flood.Rgb.Length * 3 / 4);
        Equal(75.0, Images.WhitePercent(flood), "the white share");
        Images.Write(Path.Combine(dir, "earth_earth1.png"), flood);
        Contains(Problems(Finished(dir)), "75 % of the screenshot is pure white", "the problem");
        Images.Write(Path.Combine(dir, "earth_earth1.png"), stars);
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
        new RunOptions { Test = "warp", Mode = "jump" }.Write(b);
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
        Images.Write(Path.Combine(old, "earth_earth1.png"), Gradient(64, 48));
        Finished(old);
        Finished(fresh);
        Contains([Said(() => KsaMetal.Benchmark.Cli.Program.Compare(old, fresh)).Output], "b has no PNG", "no pictures in b");
        var changedPicture = Gradient(64, 48);
        Array.Fill(changedPicture.Rgb, (byte)255, 0, changedPicture.Rgb.Length / 2);
        Images.Write(Path.Combine(fresh, "earth_earth1.png"), changedPicture);
        Equal(1, Said(() => KsaMetal.Benchmark.Cli.Program.Compare(old, fresh)).Status, "an old run's loose limit doesn't hide a change");
    }

    // A run made before run.json: its report.json read into a record
    private static void OlderRun()
    {
        var dir = Path.Combine(_scratch, "2026-11-02_2057");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "report.json"), """
            {"schema":2,"system":{"Mac":"Mac17,2","Test":"mixed","Start":"a new game, from benchmark.sh"},"mode":"fast","runs":1,"passed":false,
             "stopped":false,"problems":["game.log: ERROR one"],"notes":["swap"],"sections":[{"name":"mixed","windows":[{"name":"kitten","frames":40,
             "seconds":1,"averageFps":40,"frameMsMedian":25,"gpuMsMedian":24.6}],"screenshots":["kitten"],"shotLimits":{"kitten":4},"errors":[],"live":false}]}
            """);
        var record = RunRecord.Read(dir);
        Check(record != null, "read");
        Equal("views", record?.Mode, "fast mode of the mixed section: views");
        Equal(Status.Failed, record?.Status, "failed");
        Equal("game.log: ERROR one", record?.Problems.FirstOrDefault()?.Text, "its problem");
        Equal(24.6, record?.Scenario("mixed")?.Part("kitten")?.GpuMsMedian, "its part");
        Equal(4.0, record?.Scenario("mixed")?.Shots.FirstOrDefault()?.Limit, "its screenshot's limit");
        Equal("Mac17,2", record?.Fact("Mac"), "its facts");
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

    private static Image Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(i * 7 % 251);
        return new Image(width, height, rgb);
    }
}
