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
            ("the PNG reader and writer", Png),
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
    }

    private static void Problems()
    {
        var dir = Run("problems", system: ["Hooks: terrain cache: skipped (KSA's code moved); outline skip: on; HUD layout: failed (NullReferenceException)"],
            run: ["Game: hung, stopped", "Alongside: another game", "Memory pressure: normal"]);
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
        Contains(problems, "game.log: 21:04:05 ERROR Something broke", "the game's own error, without its colours");
        Contains(problems, "the GPU hung or faulted", "a GPU reset");
        Equal(10, problems.Length, "problems");
        Contains([File.ReadAllText(Path.Combine(dir, "report.md"))], "**Failed**: 10 problems", "report.md");
    }

    private static void Notes()
    {
        var dir = Run("notes", system: ["Power: Battery Power", "Low Power Mode: on", "KSA Metal switches: KSA_MTL_GPUPROF=x.csv", "Tools: Instruments"],
            run: ["Game: quit normally", "Memory pressure: warning", "Swap used: 100.00 -> 600.00 MB"]);
        Results(dir, "results.json", 10, section: s => s.Windows[0].ShadersCompiled = 3);
        Equal(0, Report("report", dir).Status, "exit status: notes don't fail a run");
        var notes = Strings(ReportJson(dir)["notes"]);
        Contains(notes, "ran on battery", "battery");
        Contains(notes, "Low Power Mode was on", "Low Power Mode");
        Contains(notes, "KSA Metal switches were set (KSA_MTL_GPUPROF=x.csv)", "KSA Metal switches");
        Contains(notes, "measured with Instruments", "tools");
        Contains(notes, "memory pressure (warning)", "memory pressure");
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
        Contains([output], "| earth | earth1 | 10.0 | 11.0 | **+10% (-)** |", "a part 10% slower, marked");
        Contains([output], "| earth_earth1.png | 0.00% | 0.0 | same |", "the same screenshot");
        Contains([output], "Same settings.", "the settings");

        // Half the view changed
        var changed = Gradient(64, 48);
        Array.Fill(changed.Rgb, (byte)255, 0, changed.Rgb.Length / 2);
        Images.Write(Path.Combine(b, "earth_earth1.png"), changed);
        (status, output) = Report("compare", a, b);
        Equal(1, status, "exit status: a screenshot differs");
        Contains([output], "**differs**", "the screenshot that differs");
        Check(File.Exists(Path.Combine(b, "diff", "earth_earth1.png")), "the marked copy in b/diff/");

        // Within noise
        var c = Run("c", frameMs: 10.2);
        Report("report", c);
        Contains([Report("compare", a, c).Output], "| earth | earth1 | 10.0 | 10.2 | +2% |", "a change within noise, unmarked");
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

    private static Image Gradient(int width, int height)
    {
        var rgb = new byte[width * height * 3];
        for (int i = 0; i < rgb.Length; i++)
            rgb[i] = (byte)(i * 7 % 251);
        return new Image(width, height, rgb);
    }
}
