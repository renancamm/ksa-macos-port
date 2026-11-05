// The benchmark's lineup (lineup/benchmark.txt): its tests and the scenarios they play, read by this parser alone. The
// director plays it, benchmark.sh lists, checks and times it, the Mac Port tab reads its tests, and the build's tests
// check it (Validate).
//
// The file is a series of scenarios and test lines, and comments (#).
//
//   test <name> <scenario>...           a test: the scenarios it plays, one after the other, all of one kind
//   scenario <name> <kind> [needs <celestial>...]
//                                       starts a scenario; one whose celestials the loaded system lacks is skipped
//   <seconds> <command>                 a step of the scenario playing, timed from its start: a directive (Directives.cs)
//                                       or one of the game's console commands. Steps at one time run in the order written
//
// A scenario's kind is how it keeps time (the director's clocks):
//   flight  the script's clock and the game's run together; every @shot is a checkpoint where the game catches up with
//           the game time the script expects, so the screenshots show the same moments on every Mac (the Planetary Tour)
//   views   the vehicles set up at a fixed pace, then the game paused, the camera going from view to view, each measured
//           standing still once it stops changing (the Mixed Test)
//   timed   every part takes the same time on any Mac, whatever the game gets through in it; nothing waits for the game
//           (the Warp Test)
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace KsaPort.Common.Benchmark;

public enum Kind { Flight, Views, Timed }

// A step: its time in the scenario, its command and the command's words, and its line in the file
public sealed record Step(double At, string Command, int Line)
{
    public string[] Words { get; } = Command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    public bool IsDirective => Command.StartsWith('@');
}

public sealed class Scenario
{
    public required string Name { get; init; }
    public required Kind Kind { get; init; }
    public required IReadOnlyList<string> Needs { get; init; }
    public required IReadOnlyList<Step> Steps { get; init; }
    public required int Line { get; init; }

    // Its length on the script's clock: its last step's time
    public double Length => Steps.Count > 0 ? Steps.Max(s => s.At) : 0;

    // Its screenshots' names, in the order they are taken
    public IEnumerable<string> Shots => Steps.Where(s => s.Words is ["@shot", _, ..]).Select(s => s.Words[1]);

    // Its measured parts: @measure's in a flight or a timed scenario, a views scenario's screenshots
    public IEnumerable<string> Parts => Kind == Kind.Views ? Shots : Steps.Where(s => s.Words is ["@measure", _, ..]).Select(s => s.Words[1]);
}

public sealed record Test(string Name, IReadOnlyList<string> Scenarios, Kind Kind, int Line);

public sealed class LineupException(string message) : Exception(message);

public sealed class Lineup
{
    public const string FileName = "benchmark.txt";

    public IReadOnlyList<Test> Tests { get; }
    public IReadOnlyList<Scenario> Scenarios { get; }
    // "full": the three tests, one after the other, in this order (the lineup's)
    public static readonly IReadOnlyList<string> Full = ["mixed", "tour", "warp"];

    private Lineup(List<Test> tests, List<Scenario> scenarios)
    {
        Tests = tests;
        Scenarios = scenarios;
    }

    public static Lineup Read(string path) => Parse(File.ReadAllText(path), path);

    // The lineup in that text; a line it can't read is a LineupException naming the line
    public static Lineup Parse(string text, string path = FileName)
    {
        var tests = new List<Test>();
        var scenarios = new List<Scenario>();
        var testLines = new List<(string[] Words, int Line)>();
        (string Name, Kind Kind, string[] Needs, List<Step> Steps, int Line)? current = null;
        var lines = text.Split('\n');

        void Close()
        {
            if (current is not { } c)
                return;
            // Steps at one time run in the order they are written
            scenarios.Add(new Scenario
            {
                Name = c.Name, Kind = c.Kind, Needs = c.Needs, Line = c.Line,
                Steps = c.Steps.OrderBy(s => s.At).ThenBy(s => s.Line).ToList()
            });
            current = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            int n = i + 1;
            var line = lines[i].Trim();
            var words = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0 || words[0].StartsWith('#'))
                continue;
            if (words[0] == "scenario")
            {
                Close();
                if (words.Length < 3 || Enum.GetValues<Kind>().Where(k => Name(k) == words[2]).Select(k => (Kind?)k).FirstOrDefault() is not { } kind)
                    throw new LineupException($"{path}:{n}: scenario <name> flight|views|timed [needs <celestial>...]");
                string[] needs = [];
                if (words.Length > 3)
                {
                    if (words[3] != "needs" || words.Length == 4)
                        throw new LineupException($"{path}:{n}: after the kind only \"needs <celestial>...\"");
                    needs = words[4..];
                }
                if (scenarios.Any(s => s.Name == words[1]))
                    throw new LineupException($"{path}:{n}: scenario {words[1]} twice");
                current = (words[1], kind, needs, new List<Step>(), n);
                continue;
            }
            if (words[0] == "test")
            {
                if (words.Length < 3)
                    throw new LineupException($"{path}:{n}: test <name> <scenario>...");
                testLines.Add((words, n));
                continue;
            }
            if (!double.TryParse(words[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var at) || at < 0 || !char.IsDigit(words[0][0]))
                throw new LineupException($"{path}:{n}: neither a step (<seconds> <command>), a scenario, a test nor a comment (#)");
            if (current is not { } open)
                throw new LineupException($"{path}:{n}: a step before the first scenario");
            if (words.Length < 2)
                throw new LineupException($"{path}:{n}: a time without a command");
            var command = line[(line.IndexOf(words[1], words[0].Length, StringComparison.Ordinal))..].Trim();
            var step = new Step(at, command, n);
            if (step.IsDirective && Directives.Check(step.Words) is { } wrong)
                throw new LineupException($"{path}:{n}: {wrong}");
            open.Steps.Add(step);
        }
        Close();

        foreach (var (words, n) in testLines)
        {
            if (tests.Any(t => t.Name == words[1]) || words[1] == "full")
                throw new LineupException($"{path}:{n}: test {words[1]} twice (or named full, which plays them all)");
            var played = new List<Scenario>();
            foreach (var name in words[2..])
                played.Add(scenarios.FirstOrDefault(s => s.Name == name) ?? throw new LineupException($"{path}:{n}: test {words[1]}: no scenario {name}"));
            if (played.Select(s => s.Kind).Distinct().Count() > 1)
                throw new LineupException($"{path}:{n}: test {words[1]} mixes kinds: {string.Join(", ", played.Select(s => $"{s.Name} {Name(s.Kind)}"))}");
            tests.Add(new Test(words[1], played.Select(s => s.Name).ToList(), played[0].Kind, n));
        }

        // A place is a screenshot's name, one across the lineup
        var seen = new Dictionary<string, string>();
        foreach (var scenario in scenarios)
        {
            foreach (var shot in scenario.Shots)
            {
                if (seen.TryGetValue(shot, out var other))
                    throw new LineupException($"{path}: screenshot {shot} in {other} and {scenario.Name}: a place's name is one across the lineup");
                if (scenarios.Any(s => s.Name == shot))
                    throw new LineupException($"{path}: screenshot {shot} is also a scenario's name");
                seen[shot] = scenario.Name;
            }
        }
        return new Lineup(tests, scenarios);
    }

    public static string Name(Kind kind) => kind.ToString().ToLowerInvariant();

    public Test? Test(string name) => Tests.FirstOrDefault(t => t.Name == name);

    public Scenario? Scenario(string name) => Scenarios.FirstOrDefault(s => s.Name == name);

    // The scenario a place (a screenshot's name) is in
    public Scenario? ScenarioOf(string place) => Scenarios.FirstOrDefault(s => s.Shots.Contains(place));

    // The test a scenario is in, if any
    public Test? TestOf(string scenario) => Tests.FirstOrDefault(t => t.Scenarios.Contains(scenario));

    // What is wrong with the lineup beyond what Parse reads: steps that name vehicles the scenario never launches, a views
    // scenario without screenshots, a timed scenario's steps that only a checkpoint can take. Empty when nothing is
    public List<string> Validate()
    {
        var errors = new List<string>();
        foreach (var s in Scenarios)
        {
            var launched = s.Steps.Where(t => t.Words is ["@launch", ..]).Select(t => Directives.LaunchedId(t.Words)).ToHashSet();
            launched.UnionWith(s.Steps.Where(t => t.Words is ["@kitten", ..]).Select(t => t.Words[3]));
            foreach (var step in s.Steps.Where(t => t.Words is ["@vehicle" or "@control", _]))
                if (!launched.Contains(step.Words[1]))
                    errors.Add($"{s.Name}:{step.Line}: {step.Words[0]} {step.Words[1]}, which it doesn't launch");
            if (s.Kind == Kind.Views && !s.Shots.Any())
                errors.Add($"{s.Name}: a views scenario measures its screenshots, and it has none");
            if (s.Kind == Kind.Views && s.Steps.Any(t => t.Words is ["@measure", ..]))
                errors.Add($"{s.Name}: a views scenario measures each view standing: no @measure");
            if (s.Kind != Kind.Views && !s.Steps.Any(t => t.Words is ["@measure", ..]))
                errors.Add($"{s.Name}: nothing measured (no @measure)");
            if (s.Kind == Kind.Flight && s.Steps.Any(t => t.Words is ["@warp", ..] or ["@reset"]))
                errors.Add($"{s.Name}: a flight keeps the game's clock at real time: no @warp or @reset");
        }
        foreach (var t in Tests)
            if (!Full.Contains(t.Name))
                errors.Add($"test {t.Name}: full plays {string.Join(", ", Full)}, not it");
        foreach (var name in Full)
            if (Test(name) == null)
                errors.Add($"no test {name}, which full plays");
        // The scenarios in the order full plays them, so that a game plays them in the lineup's order
        var order = Full.Select(Test).OfType<Test>().SelectMany(t => t.Scenarios).ToList();
        if (!order.SequenceEqual(Scenarios.Select(s => s.Name).Where(order.Contains)))
            errors.Add($"the scenarios are not in the order full plays them: {string.Join(" ", order)}");
        return errors;
    }
}
