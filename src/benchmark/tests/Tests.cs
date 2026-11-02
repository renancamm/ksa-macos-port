using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using KsaMetalBenchmark;

// The tests of the report program, which runs without the game: the report of a run (what passes and what fails it,
// the conditions it notes, repeats merged into medians, numbers that don't depend on the Mac's region), finish (what
// the game's log adds, the zip), the comparison of two runs, and the PNG reader and writer it shares with the director.
// The runs are written with the director's own classes (Results.cs), so the tests also check that the report reads
// what the director writes. scripts/build-benchmark.sh runs them after building the report program; a failure stops
// the build.
//
// Usage: dotnet KsaMetalBenchmarkTests.dll
internal static class Tests
{
    private static string _test = "", _scratch = "";
    private static readonly List<string> Failures = new();

    private static int Main()
    {
        _scratch = Path.Combine(Path.GetTempPath(), $"ksa-benchmark-tests-{Environment.ProcessId}");
        var tests = new (string Name, Action Run)[]
        {
            ("a run that passed", Passed),
            ("a run with problems", Problems),
            ("the conditions noted", Notes),
            ("repeats", Repeats),
            ("numbers in any region", AnyRegion),
            ("finish", Finish),
            ("comparing two runs", Compare),
            ("comparing runs of different modes", CompareModes),
            ("a live part's numbers", LivePart),
            ("the Warp Test in the report", WarpReport),
            ("the PNG reader and writer", Png),
            ("a run stopped on purpose", Stopped),
            ("a screenshot of nothing", BlankShot),
            ("runs that can't compare", NothingInCommon),
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

    // The report program with these arguments: its exit status and what it printed
    private static (int Status, string Output) Report(params string[] args)
    {
        var (output, error) = (Console.Out, Console.Error);
        var captured = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(captured);
        try
        {
            return (KsaMetalBenchmarkReport.Program.Main(args), captured.ToString());
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    // A run's folder as the director and benchmark.sh leave it: one section, earth, with one part, earth1, of 100
    // frames (one of them a hitch), and system.txt and run.txt with these lines
    private static string Run(string name, double frameMs = 10, string mode = "fast", string[]? system = null,
        string[]? run = null)
    {
        var dir = Path.Combine(_scratch, name);
        Directory.CreateDirectory(dir);
        Results(dir, "results.json", frameMs, mode);
        File.WriteAllLines(Path.Combine(dir, "system.txt"),
            ["Mac: Mac17,2", "Power: AC Power", "Low Power Mode: off", $"Mode: {mode}", "port settings: defaults", .. system ?? []]);
        File.WriteAllLines(Path.Combine(dir, "run.txt"), run ?? ["Game: quit normally", "Memory pressure: normal"]);
        return dir;
    }

    // A results file the way the director writes it
    private static Results Results(string dir, string file, double frameMs, string mode = "fast", bool completed = true,
        Action<SectionResult>? section = null)
    {
        var window = new Window("earth1", 0);
        for (int i = 0; i < 99; i++)
            window.Add(frameMs, null);
        window.Add(frameMs * 3, null);
        var earth = new SectionResult("earth");
        earth.Windows.Add(window.Summarize());
        section?.Invoke(earth);
        var results = new Results(gpuTimes: false) { Mode = mode, Completed = completed };
        results.Sections.Add(earth);
        results.Write(Path.Combine(dir, file));
        return results;
    }

    private static JsonObject ReportJson(string dir) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "report.json"))) as JsonObject ?? throw new Exception("no report.json");

    private static string[] Strings(JsonNode? array) =>
        (array as JsonArray ?? new JsonArray()).Select(n => (string?)n ?? "").ToArray();

    private static JsonObject Part(JsonObject report) =>
        report["sections"]?[0]?["windows"]?[0] as JsonObject ?? throw new Exception("no part in the report");

    private static void Passed()
    {
        var dir = Run("passed");
        Equal(0, Report("report", dir).Status, "exit status");
        var report = ReportJson(dir);
        Equal(true, (bool?)report["passed"], "passed");
        Equal(0, Strings(report["problems"]).Length, "problems");
        Equal(0, Strings(report["notes"]).Length, "notes");
        var part = Part(report);
        Equal(100, (int?)part["frames"], "frames");
        Equal(10.0, (double?)part["frameMsMedian"], "frame time's median");
        Equal(30.0, (double?)part["frameMsMax"], "worst frame");
        Equal(1, (int?)part["hitches"], "hitches");
        var md = File.ReadAllText(Path.Combine(dir, "report.md"));
        Contains([md], "**Passed**", "report.md");
        Contains([md], "| earth | earth1 | ", "report.md's part");
        Check(md.IndexOf("| earth | earth1 | ", StringComparison.Ordinal) < md.IndexOf("### This Mac and the run", StringComparison.Ordinal), "the numbers before the facts");
        var page = File.ReadAllText(Path.Combine(dir, "report.html"));
        Contains([page], "Passed", "report.html's verdict");
        Contains([page], "earth1", "report.html's part");
        var shown = Report("show", dir);
        Contains([shown.Output], "earth1", "show: the numbers");
        Check(!shown.Output.Contains("Mac17,2", StringComparison.Ordinal), "show: not the facts about the Mac");
        Check(!shown.Output.Contains("|", StringComparison.Ordinal), "show: aligned columns, no Markdown");
    }

    private static void Problems()
    {
        var dir = Run("problems", system: ["Hooks: terrain cache: skipped (KSA's code moved); outline skip: on; HUD layout: failed (NullReferenceException)"],
            run: ["Game: hung, stopped", "Alongside-2: another game", "Memory pressure: normal"]);
        Results(dir, "results.json", 10, completed: false, section: s => s.Errors.Add("12.0 s: '@launch Rocket at Pad' failed"));
        File.WriteAllLines(Path.Combine(dir, "game.log"),
        [
            "[ksa-metal ERROR] pipeline: no function", "[ksa-metal ERROR] pipeline: no function", "[ksa-metal ERROR] pipeline: no function",
            "[ksa-metal ERROR] command buffer: kIOGPUCommandBufferCallbackErrorHang", "\x1b[31m21:04:05 ERROR Something broke\x1b[0m",
            "21:04:06 INFO all is well", "[ksa-metal] not an error"
        ]);
        Equal(1, Report("report", dir).Status, "exit status");
        var report = ReportJson(dir);
        Equal(false, (bool?)report["passed"], "passed");
        var problems = Strings(report["problems"]);
        Contains(problems, "the game hung, stopped", "the game's ending");
        Contains(problems, "another game ran at the same time", "a game alongside");
        Contains(problems, "the run did not play to its end", "an incomplete run");
        Contains(problems, "earth: 12.0 s: '@launch Rocket at Pad' failed", "a step that failed");
        Contains(problems, "the hooks' terrain cache: skipped (KSA's code moved)", "a skipped hook");
        Contains(problems, "the hooks' HUD layout: failed (NullReferenceException)", "a failed hook");
        Check(!problems.Any(p => p.Contains("outline skip", StringComparison.Ordinal)), "a hook that is on is no problem");
        Contains(problems, "game.log: [ksa-metal ERROR] pipeline: no function (3 times)", "KSA Metal's errors, counted");
        Contains(problems, "game.log: ERROR Something broke", "the game's own error, without its colours");
        Contains(problems, "the GPU hung or faulted", "a GPU reset");
        Equal(10, problems.Length, "problems");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "**Failed**: 10 problems", "report.md");
    }

    private static void Notes()
    {
        var dir = Run("notes", system: ["Power: Battery Power", "Low Power Mode: on", "KSA Metal switches: KSA_MTL_GPUPROF=x.csv", "Tools: Instruments"],
            run: ["Game: quit normally", "Memory pressure: warning", "Swap used: 100.00 -> 600.00 MB", "GPU before the run: 90 %"]);
        Results(dir, "results.json", 10, section: s => s.Windows[0].ShadersCompiled = 3);
        Equal(0, Report("report", dir).Status, "exit status: notes don't fail a run");
        var notes = Strings(ReportJson(dir)["notes"]);
        Contains(notes, "ran on battery", "battery");
        Contains(notes, "Low Power Mode was on", "Low Power Mode");
        Contains(notes, "KSA Metal switches were set (KSA_MTL_GPUPROF=x.csv)", "KSA Metal switches");
        Contains(notes, "measured with Instruments", "tools");
        Contains(notes, "the GPU was busy before the run (90 %)", "a busy GPU");
        Contains(notes, "swapped memory to disk during the run (100.00 -> 600.00 MB)", "swap");
        Contains(notes, "shaders compiled while measuring earth earth1 (3)", "shaders compiled");
        Equal(7, notes.Length, "notes");
    }

    private static void Repeats()
    {
        var dir = Run("repeats", frameMs: 12);
        Results(dir, "results-2.json", 10, completed: false);
        Results(dir, "results-3.json", 20, section: s => s.Windows[0].Hitches = 5);
        Equal(1, Report("report", dir).Status, "exit status: a repeat that didn't end fails the run");
        var report = ReportJson(dir);
        Equal(3, (int?)report["runs"], "runs");
        Contains(Strings(report["problems"]), "repeat 2 did not play to its end", "the incomplete repeat");
        var part = Part(report);
        Equal(12.0, (double?)part["frameMsMedian"], "frame time: the median of the runs");
        Equal("[10,20]", part["frameMsRange"]?.ToJsonString(), "frame time's range");
        Equal(5.0, (double?)part["hitches"], "hitches: the most of the runs");
        // A part that never got to its speed in one repeat never did
        var warp = Run("warp repeats");
        Results(warp, "results.json", 10, section: s => s.Windows.Add(Warped(1)));
        Results(warp, "results-2.json", 10, section: s => s.Windows.Add(Warped(0.5)));
        Results(warp, "results-3.json", 10, section: s => s.Windows.Add(Warped(1)));
        Report("report", warp);
        Check(ReportJson(warp)["sections"]?[0]?["windows"]?[1]?["secondsToSpeed"] is null, "seconds to speed: never, in one of the runs");
        Equal(3, (int?)part["runs"], "the part's runs");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "| 12.0 (10.0-20.0) |", "report.md's range");
    }

    // A Mac whose region writes 16,7 for 16.7: the report's numbers stay the same
    private static void AnyRegion()
    {
        var dir = Run("region", frameMs: 16.7);
        File.WriteAllLines(Path.Combine(dir, "run.txt"), ["Game: quit normally", "Swap used: 100.00 -> 600.00 MB"]);
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("pt-BR");
        Equal(0, Report("report", dir).Status, "exit status");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "| 16.7 |", "report.md's frame time");
        Equal(16.7, (double?)Part(ReportJson(dir))["frameMsMedian"], "report.json's frame time");
        Contains(Strings(ReportJson(dir)["notes"]), "swapped memory", "swap read with a decimal point");
    }

    // A run from the game: system.txt without the port settings and the hooks, which finish takes from the game's log
    private static void Finish()
    {
        var dir = Run("2026-10-26_0100");
        File.WriteAllLines(Path.Combine(dir, "system.txt"), ["Mac: Mac17,2", "Mode: fast"]);
        File.WriteAllLines(Path.Combine(dir, "game.log"),
            ["==> port settings: render_scale=0.75", "[hooks] terrain cache: skipped (KSA's code moved)", "[hooks] outline skip: on"]);
        Equal(1, Report("finish", dir).Status, "exit status: a skipped hook fails the run");
        var system = File.ReadAllLines(Path.Combine(dir, "system.txt"));
        Contains(system, "port settings: render_scale=0.75", "system.txt's port settings");
        Contains(system, "Hooks: terrain cache: skipped (KSA's code moved); outline skip: on", "system.txt's hooks");
        var report = ReportJson(dir);
        Contains(Strings(report["problems"]), "the hooks' terrain cache: skipped", "the skipped hook");
        Contains(Strings(report["notes"]), "port settings were changed (render_scale=0.75)", "the port settings");
        using var zip = ZipFile.OpenRead(Path.Combine(dir, "2026-10-26_0100.zip"));
        var names = zip.Entries.Select(e => e.FullName).ToArray();
        foreach (var file in new[] { "report.md", "report.json", "system.txt", "run.txt", "game.log", "results.json" })
            Contains(names, $"2026-10-26_0100/{file}", "the zip");
    }

    private static void Compare()
    {
        var a = Run("a", frameMs: 10);
        var b = Run("b", frameMs: 11);
        var view = Gradient(64, 48);
        Images.Write(Path.Combine(a, "earth_earth1.png"), view);
        Images.Write(Path.Combine(b, "earth_earth1.png"), view);
        Report("report", a);
        Report("report", b);
        var (status, output) = Report("compare", a, b);
        Equal(0, status, "exit status: the same screenshots");
        Contains([output], "Same image (1 of 1 screenshots)", "the verdict");
        Check(!output.Contains("|", StringComparison.Ordinal), "aligned columns, no Markdown");
        var written = File.ReadAllText(Path.Combine(b, "compare.md"));
        Contains([written], "| 10.0 | 11.0 | **10 % slower** |", "a part 10 % slower, marked");
        Contains([written], "| earth_earth1.png | 0.00% | 0.0 | same |", "the same screenshot");
        Contains([written], "Same Mac, same version, same settings.", "the settings");
        Check(File.Exists(Path.Combine(b, "compare.html")), "the page");

        // Half the view changed
        var changed = Gradient(64, 48);
        Array.Fill(changed.Rgb, (byte)255, 0, changed.Rgb.Length / 2);
        Images.Write(Path.Combine(b, "earth_earth1.png"), changed);
        (status, output) = Report("compare", a, b);
        Equal(1, status, "exit status: a screenshot differs");
        Contains([output], "differs (up to 1%)", "the screenshot that differs");
        Check(File.Exists(Path.Combine(b, "diff", "earth_earth1.png")), "the marked copy in b/diff/");

        // Within noise
        var c = Run("c", frameMs: 10.2);
        Report("report", c);
        Report("compare", a, c);
        Contains([File.ReadAllText(Path.Combine(c, "compare.md"))], "| 10.0 | 10.2 | +2 % |", "a change within noise, unmarked");
    }

    private static void CompareModes()
    {
        var a = Run("fast", mode: "fast");
        var b = Run("cinematic", mode: "cinematic");
        Report("report", a);
        Report("report", b);
        var (status, output) = Report("compare", a, b);
        Equal(2, status, "exit status: runs that don't compare");
        Contains([output], "different modes (fast, cinematic)", "why");
    }

    // A window of 20 s at 25 ms a frame: first at the speed asked for after 5 s, or held back all along
    private static WindowResult Warped(double heldBack, double reachesAfter = 5)
    {
        var window = new Window("debris", 0);
        double wall = 0, game = 1000;
        for (int i = 0; i < 800; i++)
        {
            wall += 0.025;
            game += 0.025 * (wall >= reachesAfter ? 1000 * heldBack : 10);
            window.Add(25, null);
            window.AddSim(25, new SimSample(game, 1000, 3, i * 2, 10 + i % 3, 40));
        }
        return window.Summarize();
    }

    private static void LivePart()
    {
        var reached = Warped(1);
        Equal<double?>(1000.0, reached.SpeedAsked, "the speed asked for");
        Check(reached.Speed is > 700 and < 800, $"the speed over the whole part: {reached.Speed}");
        Check(reached.SecondsToSpeed is > 5 and < 7, $"the seconds to 90 %: {reached.SecondsToSpeed}");
        Check(reached.GameDays is >= 0.17 and <= 0.18, $"the game days: {reached.GameDays}");
        Equal<long?>(1598L, reached.Substeps, "the substeps");
        Equal<int?>(12, reached.BubblesMax, "the most bubbles");
        Equal<double?>(null, Warped(0.5).SecondsToSpeed, "a part that never gets to 90 %");
        var plain = new Window("earth1", 0);
        plain.Add(10, null);
        Equal<double?>(null, plain.Summarize().GameDays, "a part of another section");
    }

    private static void WarpReport()
    {
        var a = Run("a", 10);
        Results(a, "results.json", 10, section: s => s.Windows.Add(Warped(1)));
        Report("report", a);
        var md = File.ReadAllText(Path.Combine(a, "report.md"));
        Contains([md], "**0.2 game days** in time warp", "the headline");
        Contains([md], "Time warp: 0.2 game days", "the table's line");
        Contains([md], "| debris | 0.2 |", "the part's row");
        var b = Run("b", 10);
        Results(b, "results.json", 10, section: s => s.Windows.Add(Warped(0.5)));
        Report("report", b);
        Report("compare", a, b);
        var written = File.ReadAllText(Path.Combine(b, "compare.md"));
        Contains([written], "| Warp part |", "the warp table of a comparison");
        Contains([written], "**50 % slower**", "a part at half the speed");
        // The game's own errors in a live section are a note, after it a problem again
        var results = new Results(gpuTimes: false) { Completed = true };
        var live = new SectionResult("warpdebris") { Live = true };
        live.Windows.Add(Warped(1));
        results.Sections.Add(live);
        results.Write(Path.Combine(a, "results.json"));
        File.WriteAllLines(Path.Combine(a, "game.log"), ["[benchmark] section warpdebris (game time 0.0 s, 1.0 s into the run)", "12:00:01 ERROR Applying surface motion",
            "[benchmark] done after 9.0 s", "12:00:02 ERROR At the end"]);
        Report("report", a);
        var report = ReportJson(a);
        Contains(Strings(report["notes"]), "logged 1 errors during the time warp", "the note");
        Contains(Strings(report["problems"]), "ERROR At the end", "an error after the live section");
        Check(!Strings(report["problems"]).Any(x => x.Contains("surface motion", StringComparison.Ordinal)), "an error in it is no problem");
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
        var dir = Run("stopped", run: ["Game: was stopped with Ctrl-C", "Memory pressure: normal"]);
        Results(dir, "results.json", 10, completed: false);
        Equal(1, Report("report", dir).Status, "exit status: it did not play to its end");
        var report = ReportJson(dir);
        Equal(false, (bool?)report["passed"], "passed");
        Equal(true, (bool?)report["stopped"], "stopped");
        Equal(0, Strings(report["problems"]).Length, "no problems");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "**Stopped**", "report.md");
        var esc = Run("esc");
        Results(esc, "results.json", 10, completed: false, section: s => s.Errors.Add("stopped with Esc"));
        Report("report", esc);
        Equal(true, (bool?)ReportJson(esc)["stopped"], "stopped with Esc");
        // Stopped, with something wrong besides
        var both = Run("both", run: ["Game: was stopped with Ctrl-C"]);
        Results(both, "results.json", 10, completed: false, section: s => s.Errors.Add("12.0 s: '@launch' failed"));
        Report("report", both);
        Contains(Strings(ReportJson(both)["problems"]), "'@launch' failed", "a real problem stays");
        // A repeat stopped along the way is part of the same stop
        var repeat = Run("repeat stopped", run: ["Game: quit normally", "Game 2: was stopped with Ctrl-C"]);
        Results(repeat, "results-2.json", 10, completed: false);
        Report("report", repeat);
        Equal(0, Strings(ReportJson(repeat)["problems"]).Length, "a repeat stopped: no problems");
    }

    private static void BlankShot()
    {
        var black = new Image(32, 32, new byte[32 * 32 * 3]);
        Check(Images.Blank(black), "all black");
        var stars = new Image(32, 32, new byte[32 * 32 * 3]);
        stars.Rgb[500] = 60;
        Check(!Images.Blank(stars), "one star is a picture");
        var dir = Run("2026-10-26_0200");
        Images.Write(Path.Combine(dir, "earth_earth1.png"), black);
        Equal(1, Report("finish", dir).Status, "exit status");
        Contains(Strings(ReportJson(dir)["problems"]), "earth_earth1.png: the screenshot is one colour", "the problem");
        Images.Write(Path.Combine(dir, "earth_earth1.png"), stars);
        Report("finish", dir);
        Equal(0, Strings(ReportJson(dir)["problems"]).Length, "a picture: no problem, and none left from before");
    }

    // Said before a run plays, and when comparing
    private static void NothingInCommon()
    {
        var a = Run("a");
        Report("report", a);
        Equal(0, Report("comparable", a, "fast", "earth moon").Status, "a section in common");
        Equal(0, Report("comparable", a, "fast", "earth_earth4").Status, "a place of a section in common");
        var modes = Report("comparable", a, "cinematic", "earth");
        Equal(2, modes.Status, "another mode");
        Contains([modes.Output], "fast mode", "why");
        var other = Report("comparable", a, "fast", "jupiter");
        Equal(2, other.Status, "nothing in common");
        Contains([other.Output], "nothing in common", "why");
        var b = Path.Combine(_scratch, "b");
        Directory.CreateDirectory(b);
        var results = new Results(gpuTimes: false) { Mode = "fast", Completed = true };
        results.Sections.Add(new SectionResult("jupiter") { Windows = { Warped(1) } });
        results.Write(Path.Combine(b, "results.json"));
        File.WriteAllLines(Path.Combine(b, "run.txt"), ["Game: quit normally"]);
        Report("report", b);
        var compared = Report("compare", a, b);
        Equal(2, compared.Status, "compare: nothing in common");
        Contains([compared.Output], "no part in common", "compare: why");
        // The verdict: a slower run isn't good news, and what wasn't compared is said
        var c = Run("c", frameMs: 10);
        var d = Run("d", frameMs: 10);
        File.Copy(Path.Combine(c, "system.txt"), Path.Combine(d, "system.txt"), true);
        Results(c, "results.json", 10, section: s => s.Windows[0].GpuMsMedian = 10);
        Results(d, "results.json", 10, section: s => s.Windows[0].GpuMsMedian = 20);
        Report("report", c);
        Report("report", d);
        var slower = Report("compare", c, d);
        Contains([slower.Output], "GPU time 100 % higher", "the verdict");
        Contains([slower.Output], "No screenshots in a", "screenshots that weren't compared");
        Contains([File.ReadAllText(Path.Combine(d, "compare.html"))], "status bad", "the page's verdict");
    }

    private static void ListOfRuns()
    {
        var folder = Path.Combine(_scratch, "benchmarks");
        Directory.CreateDirectory(folder);
        foreach (var name in new[] { "2026-10-01_0100", "2026-10-02_0100", "2026-10-03_0100" })
        {
            var dir = Run(Path.Combine("benchmarks", name), system: ["Test: mixed", "Preset: none, own settings"]);
            Report("report", dir);
        }
        Directory.CreateDirectory(Path.Combine(folder, "2026-10-04_0100_hold"));
        var (status, output) = Report("runs", folder, "2");
        Equal(0, status, "exit status");
        Contains([output], "2026-10-03_0100", "the newest");
        Contains([output], "2026-10-02_0100", "the next");
        Check(!output.Contains("2026-10-01_0100", StringComparison.Ordinal), "only as many as asked");
        Contains([output], "Mixed Test", "the test's name");
        Contains([output], "2 of 3 runs", "how many there are");
    }

    private static void Layout()
    {
        var text = KsaMetalBenchmarkReport.Terminal.Layout("## Title\n\n**Passed**\n\n| A | B |\n|---|---:|\n| long name | 1 |\n| x | 100 |\n");
        Contains([text], "Title", "the heading");
        Check(!text.Contains('#') && !text.Contains('*') && !text.Contains('|'), $"no Markdown left: {text}");
        Contains([text], "long name    1", "columns aligned, numbers to the right");
        Contains([text], "x          100", "columns aligned, numbers to the right");
    }

    // benchmark.txt itself, without a game: what would otherwise fail minutes into a run
    private static void Script()
    {
        var root = Directory.GetCurrentDirectory();
        while (root != null && !File.Exists(Path.Combine(root, "src/benchmark/benchmark.txt")))
            root = Path.GetDirectoryName(root);
        if (root == null)
        {
            Check(false, "no src/benchmark/benchmark.txt above this folder");
            return;
        }
        var directives = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(root, "src/benchmark/director/Directives.cs")), "case \"(@\\w+)\"")
            .Select(m => m.Groups[1].Value).ToHashSet();
        Check(directives.Contains("@shot"), "the director's directives are read");
        var sections = new Dictionary<string, List<string[]>>();
        var live = new HashSet<string>();
        var tests = new List<string[]>();
        List<string[]>? current = null;
        foreach (var line in File.ReadLines(Path.Combine(root, "src/benchmark/benchmark.txt")).Select(l => l.Trim()))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 2 && words[0] == "section")
            {
                Check(!sections.ContainsKey(words[1]), $"section {words[1]} twice");
                sections[words[1]] = current = new List<string[]>();
                if (words.Length > 2 && words[2] == "live")
                    live.Add(words[1]);
            }
            else if (words.Length >= 2 && words[0] == "test")
                tests.Add(words[1..]);
            else if (words.Length >= 2 && current != null && double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                current.Add(words);
        }
        Equal("mixed tour warp", string.Join(" ", tests.Select(t => t[0]).OrderBy(t => t, StringComparer.Ordinal)), "the tests");
        foreach (var test in tests)
        {
            var forms = string.Join(' ', test[1..]).Split(" | ");
            Equal(2, forms.Length, $"test {test[0]}: its two forms");
            foreach (var section in forms.SelectMany(f => f.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
                Check(sections.ContainsKey(section), $"test {test[0]}: no section {section}");
        }
        foreach (var (name, steps) in sections)
        {
            var shots = steps.Where(s => s[1] == "@shot").Select(s => s[2]).ToList();
            Equal(shots.Distinct().Count(), shots.Count, $"{name}: screenshots with one name");
            foreach (var step in steps.Where(s => s[1].StartsWith('@')))
                Check(directives.Contains(step[1]), $"{name}: no directive {step[1]}");
            foreach (var step in steps.Where(s => s[1] == "@shot" && s.Length > 3))
                Check(double.TryParse(step[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var limit) && limit is > 0 and <= 100, $"{name}: {step[2]}'s limit");
            foreach (var step in steps.Where(s => s[1] == "@measure"))
                Check(step.Length == 4 && double.TryParse(step[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0, $"{name}: {step[2]}'s time");
            // A vehicle step's vehicle is launched in the section
            var named = steps.Where(s => s[1] == "@launch").Select(s => Array.IndexOf(s, "as") is > 0 and var at ? s[at + 1] : s.FirstOrDefault(w => w.StartsWith("benchmark-", StringComparison.Ordinal)) ?? "").ToHashSet();
            foreach (var step in steps.Where(s => s[1] is "@vehicle" or "@control"))
                Check(named.Contains(step[2]), $"{name}: {step[1]} {step[2]}, which it doesn't launch");
            if (live.Contains(name))
                Check(steps.Where(s => s[1] == "@shot").All(s => s.Length > 3 && s[3] == "100"), $"{name}: a live section's screenshots differ from run to run: limit 100");
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
