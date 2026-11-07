// A study: variants of one benchmark command, played in game sessions of their own in an interleaved order, and set
// against a baseline variant by their sessions (Statistics.cs). benchmark.sh study reads its file, plays its queue and
// writes its report; this file is the file's parser, the queue's order and the variants' numbers merged over their
// sessions.
//
// The file is a series of lines, and comments (#):
//
//   measure <options...>          what every session plays: benchmark.sh measure's options (or profile <options...>)
//   sessions <n>                  the game sessions each variant plays (5 unless said, the fewest a verdict stands on)
//   variant <name> [options...]   a variant: its name, and the options it adds to the command (none: the command as it is)
//   axis <option> <value>...      every variant once for each value (the option with that value added): a grid
//   baseline <name>               the variant the others are set against (default: the first)
//
// A value with spaces goes in quotes ('frame>50ms: moment'). Without variant lines the axes make the variants; with
// neither, the study is one variant. The variants' names join their axes' values: "main", "main low", "low".
//
// The order: round after round, each variant one session; a round plays them in turn, the next one the other way, so a
// drift in the Mac's heat falls on each variant the same (A B B A A B ...). Every session is a run of its own in the
// study's folder (<slot>-<variant>), so a study picks up where it stopped: a session whose run passed is done.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KsaPort.Common.Benchmark;

public sealed class StudyException(string message) : Exception(message);

// A variant: its name, and the options it adds to the study's command
public sealed record StudyVariant(string Name, IReadOnlyList<string> Options)
{
    // Its name as a folder's: letters, digits and . = + - kept, spaces as _
    public string Slug => new(Name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '=' or '+' or '-' ? c : c == ' ' ? '_' : '-').ToArray());
}

// A session in the queue: its place (from 1), the round it belongs to and the variant it plays
public sealed record StudySlot(int Number, int Round, StudyVariant Variant)
{
    // Its run's folder in the study's: "03-main_low" (from two digits; a slot played again: "03-main_low-2")
    public string Folder(int slots, int attempt = 1) =>
        $"{Number.ToString(new string('0', Math.Max(2, slots.ToString(System.Globalization.CultureInfo.InvariantCulture).Length)), System.Globalization.CultureInfo.InvariantCulture)}-{Variant.Slug}" +
        (attempt > 1 ? $"-{attempt}" : "");
}

public sealed class Study
{
    public const string FileName = "study.txt";
    public const int DefaultSessions = 5;

    // The verb every session plays (measure, profile) and its options
    public string Verb { get; private set; } = "";
    public IReadOnlyList<string> Command { get; private set; } = [];
    public int Sessions { get; private set; } = DefaultSessions;
    public IReadOnlyList<StudyVariant> Variants { get; private set; } = [];
    public StudyVariant Baseline { get; private set; } = null!;

    // The options study gives each session itself, which the command can't have
    private static readonly string[] OwnOptions = ["--sessions", "--ab", "--compare", "--save-plan"];

    public static Study Read(string path) => Parse(File.ReadAllLines(path), Path.GetFileName(path));

    public static Study Parse(IReadOnlyList<string> lines, string file = FileName)
    {
        var study = new Study();
        var named = new List<StudyVariant>();
        var axes = new List<(string Option, List<string> Values)>();
        string baseline = "";
        for (int n = 1; n <= lines.Count; n++)
        {
            List<string> words;
            try
            {
                words = Words(lines[n - 1]);
            }
            catch (FormatException e)
            {
                throw new StudyException($"{file}:{n}: {e.Message}");
            }
            if (words.Count == 0)
                continue;
            void Fail(string why) => throw new StudyException($"{file}:{n}: {why}");
            if (words.Skip(1).FirstOrDefault(w => OwnOptions.Contains(w)) is { } own)
                Fail($"{own} isn't for a study: it plays its own sessions and sets its variants against each other");
            switch (words[0])
            {
                case "measure" or "profile":
                    if (study.Verb.Length > 0)
                        Fail("a study has one command (measure or profile): every variant plays it");
                    study.Verb = words[0];
                    study.Command = words.Skip(1).ToList();
                    break;
                case "sessions":
                    if (words.Count != 2 || !int.TryParse(words[1], out var sessions) || sessions is < 1 or > 100)
                        Fail("sessions takes a number of game sessions, 1 to 100");
                    study.Sessions = int.Parse(words[1]);
                    break;
                case "variant":
                    if (words.Count < 2 || words[1].StartsWith('-'))
                        Fail("variant takes a name, then the options it adds");
                    if (named.Any(v => v.Name == words[1]))
                        Fail($"two variants named {words[1]}");
                    named.Add(new StudyVariant(words[1], words.Skip(2).ToList()));
                    break;
                case "axis":
                    if (words.Count < 4 || !words[1].StartsWith('-'))
                        Fail("axis takes an option, then two values or more (axis --preset low medium high)");
                    if (words.Skip(2).Distinct().Count() < words.Count - 2)
                        Fail("an axis with the same value twice");
                    axes.Add((words[1], words.Skip(2).ToList()));
                    break;
                case "baseline":
                    if (words.Count < 2)
                        Fail("baseline takes a variant's name");
                    baseline = string.Join(' ', words.Skip(1));
                    break;
                default:
                    Fail($"'{words[0]}' isn't a study's line (measure, profile, sessions, variant, axis, baseline)");
                    break;
            }
        }
        if (study.Verb.Length == 0)
            throw new StudyException($"{file}: no command: a line measure <options...> (or profile) says what every session plays");
        // Every variant once for each value of each axis
        IEnumerable<StudyVariant> grid = named.Count > 0 ? named : [new StudyVariant("", [])];
        foreach (var (option, values) in axes)
            grid = grid.SelectMany(v => values.Select(value => new StudyVariant((v.Name + " " + value).Trim(), [.. v.Options, option, value]))).ToList();
        var variants = grid.Select(v => v.Name.Length > 0 ? v : v with { Name = "base" }).ToList();
        if (variants.Select(v => v.Slug).Distinct().Count() < variants.Count)
            throw new StudyException($"{file}: two variants would share a folder ({string.Join(", ", variants.GroupBy(v => v.Slug).Where(g => g.Count() > 1).SelectMany(g => g).Select(v => v.Name))}): name them apart");
        study.Variants = variants;
        study.Baseline = baseline.Length == 0 ? variants[0]
            : variants.FirstOrDefault(v => v.Name == baseline) ?? throw new StudyException($"{file}: no variant {baseline} for the baseline ({string.Join(", ", variants.Select(v => v.Name))})");
        return study;
    }

    // The queue: each round every variant once, a round in turn and the next the other way
    public IReadOnlyList<StudySlot> Queue()
    {
        var slots = new List<StudySlot>();
        for (int round = 1; round <= Sessions; round++)
            foreach (var v in round % 2 == 1 ? Variants : Variants.Reverse())
                slots.Add(new StudySlot(slots.Count + 1, round, v));
        return slots;
    }

    // A variant's command line: the study's command with its options
    public string[] Arguments(StudyVariant v) => [.. Command, .. v.Options];

    // A line's words: spaces apart, quotes ('...' or "...") holding words together; # starts a comment
    public static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        foreach (var c in line)
        {
            if (quote != '\0')
            {
                if (c == quote)
                    quote = '\0';
                else
                    word.Append(c);
                continue;
            }
            if (c == '#' && !inWord)
                break;
            if (char.IsWhiteSpace(c))
            {
                if (inWord)
                    words.Add(word.ToString());
                word.Clear();
                inWord = false;
                continue;
            }
            inWord = true;
            if (c is '\'' or '"')
                quote = c;
            else
                word.Append(c);
        }
        if (quote != '\0')
            throw new FormatException($"a quote ({quote}) left open");
        if (inWord)
            words.Add(word.ToString());
        return words;
    }

    // A word as a study's line writes it: in quotes when it holds a space, a quote or a #
    public static string Quote(string word) =>
        word.Length > 0 && !word.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"' or '#') ? word
        : word.Contains('\'') ? $"\"{word}\"" : $"'{word}'";

    // The parts of a variant's runs, each game session of each run a sample of its own: a part's measurings of every run
    // (sessions numbered on across the runs), its numbers merged over the runs as a run merges its passes, its limit the
    // widest
    public static List<(string Scenario, PartRecord Part)> Merged(IEnumerable<RunRecord> runs)
    {
        var parts = new List<(string Scenario, string Name, List<(PartRecord Part, int Offset)> Of)>();
        int offset = 0;
        foreach (var run in runs)
        {
            foreach (var (s, p) in run.AllParts())
            {
                if (parts.FirstOrDefault(x => x.Scenario == s.Name && x.Name == p.Name) is { Of: not null } known)
                    known.Of.Add((p, offset));
                else
                    parts.Add((s.Name, p.Name, [(p, offset)]));
            }
            offset += Math.Max(run.Sessions, run.AllParts().SelectMany(x => x.Part.Measurings).Select(m => m.Session).DefaultIfEmpty(1).Max());
        }
        var merged = new List<(string, PartRecord)>();
        foreach (var (scenario, name, of) in parts)
        {
            var profiled = of.Select(x => x.Part.Profiled).OfType<SortedDictionary<string, double>>().Select(d => (IReadOnlyDictionary<string, double>)d).ToList();
            merged.Add((scenario, new PartRecord
            {
                Name = name, Runs = of.Sum(x => x.Part.Runs), Frames = of[0].Part.Frames, Seconds = of[0].Part.Seconds,
                Limit = of.Select(x => x.Part.Limit).Max(),
                Values = new SortedDictionary<string, double>(Measure.Merge(of.Select(x => (IReadOnlyDictionary<string, double>)x.Part.Values).ToList()), StringComparer.Ordinal),
                Measurings = of.SelectMany(x => x.Part.Measurings.Select(m => m with { Session = m.Session + x.Offset })).ToList(),
                Profiled = profiled.Count > 0 ? new SortedDictionary<string, double>(Measure.Merge(profiled), StringComparer.Ordinal) : null,
            }));
        }
        return merged;
    }
}

// A study's report as its page and report.md show it: the command, the baseline, each variant against it (the verdict, the
// parts, the screenshots of its first session against the baseline's), and the queue's sessions with their runs
public sealed record StudyPage(string Name, string Command, string Baseline, IReadOnlyList<string> BaselineOptions, int SessionsEach,
    IReadOnlyList<StudyPage.Variant> Variants, IReadOnlyList<StudyPage.Session> Sessions, RunRecord? First)
{
    // A variant against the baseline: no comparison while either side has no session that passed (Sentence says why)
    public sealed record Variant(string Name, IReadOnlyList<string> Options, Comparison? Comparison, string Sentence, bool? Good,
        IReadOnlyList<ShotResult> Shots, int ShotsDiffer, string? ShotsOf);

    public sealed record Session(int Number, int Round, string Variant, string? Run, string State);

    public int Played => Sessions.Count(s => s.Run != null);

    // Every part any variant was set against the baseline on, in the order they played
    public List<(string Scenario, string Part)> Rows() =>
        Variants.Where(v => v.Comparison != null).SelectMany(v => v.Comparison!.Parts.Concat(v.Comparison.Warps))
            .Where(p => p.Decides != null).Select(p => (p.Scenario, p.Name)).Distinct().ToList();

    public PartComparison? Cell(Variant v, string scenario, string part) =>
        v.Comparison?.Parts.Concat(v.Comparison.Warps).FirstOrDefault(p => p.Scenario == scenario && p.Name == part && p.Decides != null);

    // report.md, and the terminal's summary (without the sessions)
    public string Markdown(bool terminal = false)
    {
        var md = new StringBuilder();
        md.AppendLine($"## Study {Name}");
        md.AppendLine();
        md.AppendLine($"Every session: {Command}. {Played} of the {Sessions.Count} game sessions played ({SessionsEach} for each variant); " +
                      $"the baseline: {Baseline}{(BaselineOptions.Count > 0 ? $" ({string.Join(' ', BaselineOptions)})" : "")}.");
        md.AppendLine();
        md.AppendLine($"| Variant | Against {Baseline} |");
        md.AppendLine("|---|---|");
        foreach (var v in Variants)
            md.AppendLine($"| {v.Name} | {(v.Good == false ? $"**{v.Sentence}**" : v.Sentence)} |");
        md.AppendLine();
        var rows = Rows();
        if (rows.Count > 0)
        {
            var shown = Variants.Where(v => v.Comparison != null).ToList();
            md.AppendLine($"| Scenario | Part | | {Baseline} | {string.Join(" | ", shown.Select(v => v.Name))} |");
            md.AppendLine($"|---|---|---|---:|{string.Concat(shown.Select(_ => "---:|"))}");
            string last = "";
            foreach (var (scenario, part) in rows)
            {
                var first = shown.Select(v => Cell(v, scenario, part)).First(c => c != null)!.Decides!;
                var cells = shown.Select(v => Cell(v, scenario, part)?.Decides is { } d
                    ? Pages.Marked(d) + (d.Interval is [var lo, var hi] && !terminal ? FormattableString.Invariant($" ({lo:0.000}-{hi:0.000})") : "") : "");
                md.AppendLine($"| {(scenario == last ? "" : scenario)} | {part} | {first.Metric.Label} | {first.Metric.Format(first.A)} {first.Metric.Unit} | {string.Join(" | ", cells)} |");
                last = scenario;
            }
            md.AppendLine();
            var basis = shown.Select(v => v.Comparison!.Basis).Min();
            md.AppendLine(Pages.BasisNote(basis) + (terminal ? "" : " In brackets: the 95 % interval of the ratio of the medians."));
            md.AppendLine();
        }
        var moved = Variants.Where(v => v.Comparison != null)
            .SelectMany(v => v.Comparison!.Parts.Concat(v.Comparison.Warps).Where(p => p.Decides is { Changed: true }).Select(p => (v, p))).ToList();
        if (moved.Count > 0)
        {
            md.AppendLine("What moved:");
            md.AppendLine();
            foreach (var (v, p) in moved)
                md.AppendLine($"- {v.Name}: {p.Line()} ({Comparison.Text(p.Decides!)})");
            md.AppendLine();
        }
        if (!terminal)
        {
            md.AppendLine("| Session | Round | Variant | Run |");
            md.AppendLine("|---:|---:|---|---|");
            foreach (var s in Sessions)
                md.AppendLine($"| {s.Number} | {s.Round} | {s.Variant} | {s.Run ?? s.State} |");
            md.AppendLine();
        }
        return md.ToString();
    }
}
