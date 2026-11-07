// What makes a run fail, and the conditions worth knowing about it (Finding): one list of checks over what the run left,
// the same for a run from benchmark.sh and one from the Mac Port tab. A failure's parts may not compare with other runs;
// a note doesn't fail the run, but the numbers of the parts it concerns may not compare either.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaPort.Common.Benchmark;

public sealed record RunEvidence(string Dir, RunRecord Record, IReadOnlyList<RunEvent> Events, IReadOnlyList<(string Name, string[] Lines)> Logs,
    IReadOnlyList<string> Crashes);

public static class Checks
{
    // A screenshot this much pure white, or more, shows a picture gone wrong: real views reach about a fifth (explosions)
    public const double BlownOutPercent = 30;

    private static readonly Regex Ansi = new(@"\x1b\[[0-9;]*m");
    private static readonly Regex GameError = new(@"^\d\d:\d\d:\d\d\s+(ERROR|FATAL)\b");
    private static readonly Regex ScenarioLine = new(@"^\[benchmark\] (?:section|scenario) (\S+) ");
    private static readonly Regex EndLine = new(@"^\[benchmark\] (done after|stopped:)");
    // The game's own trouble reaching its servers: the network's, not the run's
    private static readonly Regex Network = new(@"error reaching master server", RegexOptions.IgnoreCase);

    private delegate IEnumerable<Finding> Check(RunEvidence run);

    private static readonly Check[] All =
    [
        Endings, Played, StepErrors, Pictures, HooksApplied, LogErrors, Conditions, MemoryAndGpu, Crashes, PartConditions,
        TimedErrors, NetworkTrouble, Heat,
    ];

    public static List<Finding> Run(RunEvidence run) => All.SelectMany(c => c(run)).ToList();

    private static Finding Fail(string code, string text) => new(Severity.Fail, code, text);

    private static Finding Note(string code, string text) => new(Severity.Note, code, text);

    // How each game session ended; another game alongside; a game gone before its first frame with nothing to say why
    private static IEnumerable<Finding> Endings(RunEvidence run)
    {
        foreach (var e in run.Events.Where(e => e.Type == "ended"))
        {
            if (e.Text is not ("quit normally" or "kept running" or "was stopped with Ctrl-C"))
                yield return Fail("ended", $"the game {(run.Record.Sessions > 1 ? $"(session {e.Session}) " : "")}{e.Text}");
        }
        // A trigger's stop (Triggers.cs) is the run's own, not the player's
        foreach (var e in run.Events.Where(e => e.Type is "stopped" or "error" && e.Text?.StartsWith("stopped by the trigger ", StringComparison.Ordinal) == true))
            yield return Note("trigger-stop", e.Text!["stopped by ".Length..]);
        if (run.Events.Any(e => e.Type == "alongside"))
            yield return Fail("alongside", "another game ran at the same time, sharing the Mac's GPU and memory");
        if (run.Events.Any(e => e.Type == "ended" && e.Text?.Contains("before its first frame", StringComparison.Ordinal) == true) &&
            !run.Logs.Any(l => l.Lines.Any(x => x.Contains("Unhandled exception", StringComparison.Ordinal))))
            yield return Fail("display", "the display must be awake and the Mac unlocked for the game to start");
    }

    // Whether the script played to its end
    private static IEnumerable<Finding> Played(RunEvidence run)
    {
        if (run.Events.Any(e => e.Type == "stopped"))
            yield break;
        if (!run.Events.Any(e => e.Type == "scenario"))
            yield return Fail("nothing", "the run measured nothing: the benchmark didn't start in the game");
        else if (run.Events.Count(e => e.Type == "done") < run.Record.Sessions * run.Record.Passes)
            yield return Fail("unfinished", "the run did not play to its end");
    }

    // A step that failed, a vehicle that broke up, a screenshot missing, a view that didn't settle
    private static IEnumerable<Finding> StepErrors(RunEvidence run) =>
        run.Events.Where(e => e.Type == "error").Select(e => Fail("step", $"{(e.Pass == 0 ? "warm-up: " : e.Pass > 1 ? $"pass {e.Pass}: " : "")}{e.Scenario}: {e.Text}"));

    // Screenshots that show something went wrong with no other sign of it, which a comparison would only catch against a
    // run that had a picture: one colour from edge to edge, or mostly pure white
    private static IEnumerable<Finding> Pictures(RunEvidence run)
    {
        var shots = run.Record.Scenarios.SelectMany(s => s.Shots).Select(s => Path.Combine(run.Dir, s.File)).Where(File.Exists).ToList();
        return shots.AsParallel().AsOrdered().Select(f => (Name: Path.GetFileName(f), Image: Images.Read(f)))
            .Select(s => s.Image == null ? null : Images.Blank(s.Image) ? Fail("blank", $"{s.Name}: the screenshot is one colour from edge to edge")
                : Images.WhitePercent(s.Image) is >= BlownOutPercent and var white ? Fail("white", $"{s.Name}: {white:F0} % of the screenshot is pure white: the picture went wrong") : null)
            .OfType<Finding>().ToList();
    }

    // A hook that was skipped (the game's code it changes has moved) or failed: the run didn't play the port as it is
    private static IEnumerable<Finding> HooksApplied(RunEvidence run)
    {
        foreach (var hook in (run.Record.Fact("Hooks") ?? "").Split("; ", StringSplitOptions.RemoveEmptyEntries))
        {
            if (hook.Split(": ", 2) is [_, var status] &&
                (status.StartsWith("skipped", StringComparison.Ordinal) || status.StartsWith("failed", StringComparison.Ordinal)))
                yield return Fail("hook", $"the hooks' {hook}");
        }
    }

    // The game's log of each session: KSA Metal's errors, the game's own (but in a timed scenario, whose settings may hold
    // warp back), exceptions nobody caught, settings it couldn't read, the GPU reset by macOS
    private static IEnumerable<Finding> LogErrors(RunEvidence run)
    {
        var timed = run.Record.Scenarios.Where(s => s.Kind == Kind.Timed).Select(s => s.Name).ToHashSet();
        foreach (var (name, lines) in run.Logs)
        {
            var errors = new Dictionary<string, int>();
            bool gpuReset = false, inTimed = false;
            foreach (var raw in lines)
            {
                var line = Ansi.Replace(raw, "").Trim();
                if (ScenarioLine.Match(line) is { Success: true } scenario)
                    inTimed = timed.Contains(scenario.Groups[1].Value);
                else if (EndLine.IsMatch(line))
                    inTimed = false;
                // The game then runs on its defaults: every number would be of other settings than the run says
                if (line.Contains("failed to load settings file", StringComparison.Ordinal))
                    yield return Fail("settings", $"{name}: the game could not read the run's settings.toml and played with its defaults");
                if (Network.IsMatch(line))
                    continue;
                if (line.StartsWith("[ksa-metal ERROR]", StringComparison.Ordinal) || GameError.IsMatch(line) && !inTimed ||
                    line.StartsWith("Unhandled exception", StringComparison.Ordinal))
                {
                    gpuReset |= line.Contains("kIOGPUCommandBufferCallbackError", StringComparison.Ordinal);
                    // The same error at another second is the same error
                    var plain = GameError.Replace(line, "$1");
                    var key = plain.Length > 160 ? plain[..160] + "..." : plain;
                    errors[key] = errors.GetValueOrDefault(key) + 1;
                }
            }
            // macOS recovered the GPU from a hang or fault: the game's work since then was thrown away
            if (gpuReset)
                yield return Fail("gpu-reset", $"{name}: the GPU hung or faulted and macOS reset it: from then on the game's frames were thrown away, " +
                                               "so the numbers after it are not real");
            foreach (var (line, count) in errors.Take(10))
                yield return Fail("log", $"{name}: {line}{(count > 1 ? $" ({count} times)" : "")}");
            if (errors.Count > 10)
                yield return Fail("log", $"{name}: {errors.Count - 10} more kinds of errors");
        }
    }

    // The Mac's and the port's conditions: power, switches, settings changed, how the run started, tools
    private static IEnumerable<Finding> Conditions(RunEvidence run)
    {
        string Fact(string key) => run.Record.Fact(key) ?? "";
        if (Fact("Power").Contains("Battery", StringComparison.OrdinalIgnoreCase))
            yield return Note("battery", "ran on battery: a Mac can be slower than when plugged in");
        if (Fact("Low Power Mode") == "on")
            yield return Note("low-power", "Low Power Mode was on: the Mac runs slower");
        if (Fact("KSA Metal switches") is { Length: > 0 } switches && switches != "none")
            yield return Note("switches", $"KSA Metal switches were set ({switches}): they change what the driver does");
        if (Fact("port settings") is { Length: > 0 } port && port is not ("defaults" or "?"))
            yield return Note("port-settings", $"port settings were changed ({port}): they change what the game draws or how");
        // --port lines the game doesn't list: a value that is the default, or a setting waiting for another one (its "needs")
        if (Fact("Settings changed").IndexOf("port-settings: ", StringComparison.Ordinal) is >= 0 and var from)
        {
            var running = Fact("port settings");
            foreach (var kv in Fact("Settings changed")[(from + 15)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                if (running.Length > 0 && !running.Contains(kv.Split('=')[0] + "=", StringComparison.Ordinal) &&
                    !running.Contains(kv.Split('=')[0] + " =", StringComparison.Ordinal))
                    yield return Note("port-default", $"--port {kv} is not among the settings the game ran with: it is the setting's default, or it waits for another setting");
        }
        if (run.Record.InGame)
            yield return Note("in-game", "played in a running game, after what was played before: its numbers compare best with runs started the same way, and its screenshots aren't compared");
        if (Fact("Tools") is { Length: > 0 } tools && tools != "none")
            yield return Note("tools", $"measured with {tools}: their own cost is in the numbers");
        foreach (var e in run.Events.Where(e => e.Type == "disturbed"))
            yield return Note("disturbed", $"{(e.Pass > 1 ? $"pass {e.Pass}: " : "")}{e.Scenario} {e.Part}: left out of the numbers, {e.Text}");
    }

    // macOS's memory pressure, swap and a busy GPU, as the host saw them
    private static IEnumerable<Finding> MemoryAndGpu(RunEvidence run)
    {
        foreach (var e in run.Events.Where(e => e.Type == "memory"))
        {
            // "warning" is how a Mac with little memory runs this game, nearly always: only the worst level says something
            var m = Regex.Match(e.Text ?? "", @"pressure (\w+), swap ([0-9.]+) -> ([0-9.]+) MB");
            if (!m.Success)
                continue;
            if (m.Groups[1].Value == "critical")
                yield return Note("pressure", "macOS reported memory pressure (critical): the game may have had to wait for memory, which shows as stutters");
            if (double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) - double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > 256)
                yield return Note("swap", $"the Mac swapped memory to disk during the run ({m.Groups[2].Value} -> {m.Groups[3].Value} MB)");
        }
        foreach (var e in run.Events.Where(e => e.Type == "gpu"))
            yield return Note("gpu-busy", $"the GPU was busy before the run ({e.Value:F0} %): something else may have shared it");
    }

    private static IEnumerable<Finding> Crashes(RunEvidence run) =>
        run.Crashes.TakeLast(3).Select(c => Note("crash-report", $"macOS wrote a crash report, {Path.GetFileName(c)}, in ~/Library/Logs/DiagnosticReports (the zip holds it)"));

    // Heat that slowed the chip, shaders compiled while measuring
    private static IEnumerable<Finding> PartConditions(RunEvidence run)
    {
        var hot = run.Record.AllParts().Where(x => x.Part.ThermalMax >= 2).Select(x => $"{x.Scenario.Name} {x.Part.Name}").ToList();
        var compiled = run.Record.AllParts().Where(x => x.Part.ShadersCompiled > 0)
            .Select(x => $"{x.Scenario.Name} {x.Part.Name} ({x.Part.ShadersCompiled})").ToList();
        if (hot.Count > 0)
            yield return Note("hot", $"the Mac was hot enough to slow down during {string.Join(", ", hot)}");
        if (compiled.Count > 0)
            yield return Note("shaders", $"shaders compiled while measuring {string.Join(", ", compiled.Take(8))}{(compiled.Count > 8 ? $" and {compiled.Count - 8} more parts" : "")}: " +
                                         "the first run after an update of the game or the port compiles them, the next runs don't");
    }

    // The game's own errors during a timed scenario, by how often each came: a condition of the Warp Test at settings that
    // leave its stuck pieces as they are, not a broken run
    private static IEnumerable<Finding> TimedErrors(RunEvidence run)
    {
        var timed = run.Record.Scenarios.Where(s => s.Kind == Kind.Timed).Select(s => s.Name).ToHashSet();
        if (timed.Count == 0)
            yield break;
        var errors = new Dictionary<string, int>();
        var logs = new HashSet<string>();
        foreach (var (name, lines) in run.Logs)
        {
            bool inTimed = false;
            foreach (var raw in lines)
            {
                var line = Ansi.Replace(raw, "").Trim();
                if (ScenarioLine.Match(line) is { Success: true } scenario)
                    inTimed = timed.Contains(scenario.Groups[1].Value);
                else if (EndLine.IsMatch(line))
                    inTimed = false;
                else if (inTimed && GameError.IsMatch(line) && !Network.IsMatch(line))
                {
                    var plain = GameError.Replace(line, "").Trim();
                    errors[plain] = errors.GetValueOrDefault(plain) + 1;
                    logs.Add(name);
                }
            }
        }
        if (errors.Count > 0)
        {
            var common = errors.MaxBy(e => e.Value).Key;
            yield return Note("timed-errors", $"the game logged {errors.Values.Sum()} errors during the time warp, {errors.Count} kind{(errors.Count > 1 ? "s" : "")}, " +
                                              $"most often \"{(common.Length > 80 ? common[..80] + "..." : common)}\" ({string.Join(", ", logs.Order(StringComparer.Ordinal))})");
        }
    }

    // A Mac's heat moves GPU time: after a rest a run reads up to about a tenth low until the chip warms. With a
    // warm-up pass (benchmark.sh measure), what it showed; without one, how long the Mac had rested since the run
    // before ended (a run from the Mac Port tab follows the player's own playing, not a rest)
    private static IEnumerable<Finding> Heat(RunEvidence run)
    {
        if (run.Record.WarmUp is { } warmUp)
        {
            if (warmUp is { GpuMs: { } warm, MeasuredGpuMs: { } measured } && warm < measured * 0.95)
                yield return Note("warming", string.Create(CultureInfo.InvariantCulture,
                    $"the Mac warmed up during the warm-up pass (GPU {warm:F1} ms a frame, then {measured:F1} in the measured passes): it is left out of the numbers"));
            yield break;
        }
        if (run.Record.InGame || run.Record.Start.StartsWith("a new game, restarted", StringComparison.Ordinal) ||
            run.Events.FirstOrDefault() is not { } first || Path.GetDirectoryName(Path.GetFullPath(run.Dir).TrimEnd('/')) is not { } folder)
            yield break;
        var name = Path.GetFileName(Path.GetFullPath(run.Dir).TrimEnd('/'));
        var before = Directory.GetDirectories(folder).Select(d => Path.GetFileName(d)!)
            .Where(d => string.CompareOrdinal(d, name) < 0 && File.Exists(Path.Combine(folder, d, EventLog.FileName)))
            .OrderByDescending(d => d, StringComparer.Ordinal).FirstOrDefault();
        if (before == null || EventLog.Read(Path.Combine(folder, before)).LastOrDefault() is not { } ended)
            yield break;
        double rested = (first.At - ended.At).TotalMinutes;
        if (rested > 10)
            yield return Note("rested", string.Create(CultureInfo.InvariantCulture,
                $"the Mac had rested {rested:F0} minutes since the run before: GPU times read up to about a tenth low until it warms up (benchmark.sh measure warms it up first)"));
    }

    // The game couldn't reach its servers: nothing the run measures depends on them
    private static IEnumerable<Finding> NetworkTrouble(RunEvidence run)
    {
        if (run.Logs.Any(l => l.Lines.Any(x => Network.IsMatch(x))))
            yield return Note("network", "the game could not reach its master server (the network's or the server's trouble): nothing the run measures depends on it");
    }
}
