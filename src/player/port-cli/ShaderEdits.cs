using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KsaPort.Common;

namespace KsaPort.Cli;

// The port's edits to the game's shaders (Content/Core/Shaders/): KSA compiles its GLSL shaders when it starts, so
// the changes go into the sources. They are data, the .edits files in shader-edits/ next to the port's program, from
// src/game/changes/ (README.md there describes the format): fixes/ the game needs on the port, features/ that a port
// setting needs, optimizations/ that keep the same image, faster. Safe to run again: an edit made already is [OK].
//
// Each edit prints [FIXED], [OK] (made already) or [WARN] (the code around it changed in this KSA version: either
// RocketWerkz fixed the bug, or the edit needs an update). An edit that skips work comes after a check of the code that
// uses what it skips, and an edit that needs another one is made only with it: a KSA version that changed either keeps
// the game's code, never a different image or a shader that doesn't compile.
public static class ShaderEdits
{
    public enum Status { Fixed, Ok, Warn, Skipped }

    public sealed class Step
    {
        public string Kind = "", Id = "", Path = "", What = "", DoneIf = "", DoneIfLine = "", Source = "";
        public string[] Needs = [];
        public string? Find, Replace;
        // What earlier versions of the port made of find
        public readonly List<string> Earlier = new();
        // A check's expectations: text the file (or another, by its path) contains, and how often words appear
        public readonly List<(string Path, string Text)> Contains = new();
        public readonly List<(string Path, string Word, int[] Counts)> Counts = new();
    }

    // The groups, in the order they are made
    public static readonly string[] Groups = ["fixes", "features", "optimizations"];

    // A file holds the port's shadow edits once it holds this: the checks' counts then allow the later numbers
    private const string ShadowEditMarker = "!PORT_SKIP_UNLIT_SHADOWS";

    // The steps of every .edits file in <folder>, group by group, each group's files by name
    public static List<Step> Read(string folder)
    {
        var steps = new List<Step>();
        foreach (var group in Groups)
        {
            var dir = System.IO.Path.Combine(folder, group);
            if (!Directory.Exists(dir))
                throw new IOException($"{dir} is missing. Download the port again.");
            foreach (var file in Directory.GetFiles(dir, "*.edits").OrderBy(f => f, StringComparer.Ordinal))
                steps.AddRange(Parse(File.ReadAllText(file), System.IO.Path.GetRelativePath(folder, file)));
        }
        var ids = new HashSet<string>();
        foreach (var s in steps)
        {
            if (!ids.Add(s.Id))
                throw new FormatException($"{s.Source}: {s.Id} is there twice");
            foreach (var n in s.Needs.Where(n => !ids.Contains(n) || n == s.Id))
                throw new FormatException($"{s.Source}: {s.Id} needs {n}, which no earlier step is");
        }
        return steps;
    }

    // One .edits file. A line "@@ <directive> [<arguments>]" starts a step or gives one of its parts; a text part's
    // text is the lines up to the next "@@" line, without its trailing empty lines (no text ends with a line break)
    public static List<Step> Parse(string text, string source)
    {
        var steps = new List<Step>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        Step? step = null;
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.StartsWith("@@"))
            {
                if (step == null && (line.Length == 0 || line.StartsWith('#')))
                    continue;  // the file's comment
                if (line.Trim().Length == 0)
                    continue;
                throw new FormatException($"{source}, line {i + 1}: text outside a block");
            }
            var parts = line.Length > 3 ? line[3..].Split(' ', 2) : [""];
            string directive = parts[0], rest = parts.Length > 1 ? parts[1] : "";
            string Fail(string what) => throw new FormatException($"{source}, line {i + 1}: {what}");
            string Block()
            {
                int start = i + 1;
                while (i + 1 < lines.Length && !lines[i + 1].StartsWith("@@"))
                    i++;
                var block = lines[start..(i + 1)].ToList();
                while (block.Count > 0 && block[^1].Trim().Length == 0)
                    block.RemoveAt(block.Count - 1);
                return block.Count > 0 ? string.Join('\n', block) : Fail($"{directive} without text");
            }
            switch (directive)
            {
                case "#":
                    break;
                case "edit" or "check" or "code":
                    var args = rest.Split(' ', 2);
                    if (args.Length != 2)
                        Fail($"{directive} needs an id and a shader");
                    step = new Step { Kind = directive, Id = args[0], Path = args[1], Source = source };
                    steps.Add(step);
                    break;
                case "what" when step != null:
                    step.What = rest;
                    break;
                case "needs" when step != null:
                    step.Needs = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "done-if" when step is { Kind: "edit" }:
                    step.DoneIf = rest;
                    break;
                case "done-if-line" when step is { Kind: "edit" }:
                    step.DoneIfLine = rest;
                    break;
                case "find" when step is { Kind: "edit" }:
                    step.Find = Block();
                    break;
                case "replace" when step is { Kind: "edit" }:
                    step.Replace = Block();
                    break;
                case "earlier" when step is { Kind: "edit" }:
                    step.Earlier.Add(Block());
                    break;
                case "contains" when step is { Kind: "check" }:
                    var other = rest.Length > 0 ? rest : step.Path;
                    step.Contains.Add((other, Block()));
                    break;
                case "counts" when step is { Kind: "check" }:
                    var words = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                    var file = step.Path;
                    if (words.Count > 0 && !words[0].Contains('='))
                    {
                        file = words[0];
                        words.RemoveAt(0);
                    }
                    foreach (var w in words)
                    {
                        var kv = w.Split('=');
                        if (kv.Length != 2)
                            Fail($"counts: {w} is not <word>=<n>[,<n>...]");
                        step.Counts.Add((file, kv[0], kv[1].Split(',').Select(int.Parse).ToArray()));
                    }
                    break;
                default:
                    Fail($"unknown or misplaced @@ {directive}");
                    break;
            }
        }
        foreach (var s in steps)
        {
            if (s.What.Length == 0)
                throw new FormatException($"{source}: {s.Id} has no what");
            if (s.Kind == "edit" && (s.Find == null || s.Replace == null))
                throw new FormatException($"{source}: {s.Id} needs find and replace");
            if (s.Kind == "check" && s.Contains.Count == 0 && s.Counts.Count == 0)
                throw new FormatException($"{source}: {s.Id} checks nothing");
        }
        return steps;
    }

    // Makes the steps on the shaders of <game> and prints a line for each edit; the statuses, by id
    public static Dictionary<string, Status> Apply(string game, List<Step> steps, Action<string> print)
    {
        var shaders = System.IO.Path.Combine(game, "Content", "Core", "Shaders");
        if (!Directory.Exists(shaders))
            throw new IOException($"{shaders} not found. Is this a KSA game folder?");
        var status = new Dictionary<string, Status>();
        foreach (var step in steps)
        {
            var name = System.IO.Path.GetFileName(step.Path);
            if (step.Needs.Any(n => status[n] is not (Status.Fixed or Status.Ok)))
            {
                status[step.Id] = Status.Skipped;
                if (step.Kind != "check")
                    print($"    [SKIP] {name}: {step.What}, as an edit it needs was not made");
                continue;
            }
            var path = System.IO.Path.Combine(shaders, step.Path);
            switch (step.Kind)
            {
                case "check":
                    status[step.Id] = Holds(shaders, step) ? Status.Ok : Status.Warn;
                    if (status[step.Id] == Status.Warn)
                        print($"    [WARN] {name}: the code changed in this KSA version, skipped: {step.What}. Please open an issue.");
                    break;
                case "code":
                    status[step.Id] = step.Id switch
                    {
                        "milky-way" => MilkyWay.Apply(shaders, print),
                        _ => throw new FormatException($"{step.Source}: no code edit named {step.Id}"),
                    };
                    break;
                default:
                    status[step.Id] = Edit(path, step, out var line);
                    print($"    [{(status[step.Id] == Status.Fixed ? "FIXED" : status[step.Id] == Status.Ok ? "OK" : "WARN")}] {name}: {line}");
                    break;
            }
        }
        return status;
    }

    // An edit on one file, which keeps its line endings (the shaders use CRLF). In this order: the replacement there
    // already is made; the longest earlier version's replacement there once is made again (one an older version's
    // holds would leave the rest of it behind); done-if there is made; find there once is replaced; anything else is a
    // [WARN]
    private static Status Edit(string path, Step step, out string line)
    {
        if (!File.Exists(path))
        {
            line = "not found";
            return Status.Warn;
        }
        var text = ReadBytes(path);
        var nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string Lines(string s) => Bytes(s).Replace("\n", nl);
        string find = Lines(step.Find!), replace = Lines(step.Replace!);
        if (text.Contains(replace, StringComparison.Ordinal))
        {
            line = $"already {step.What}";
            return Status.Ok;
        }
        var what = step.What;
        // The earlier edits first: one can hold find (the line a declaration was added after)
        if (step.Earlier.Select(Lines).Where(e => Count(text, e) == 1).MaxBy(e => e.Length) is { } earlier)
        {
            find = earlier;
            what = $"updated: {what}";
        }
        else if ((step.DoneIf.Length > 0 && text.Contains(Bytes(step.DoneIf), StringComparison.Ordinal)) ||
                 (step.DoneIfLine.Length > 0 && Regex.IsMatch(text, $@"^[ \t]*{Regex.Escape(Bytes(step.DoneIfLine))}", RegexOptions.Multiline)))
        {
            line = $"already {step.What}";
            return Status.Ok;
        }
        if (Count(text, find) != 1)
        {
            line = $"the code changed in this KSA version, skipped: {step.What}. Please open an issue.";
            return Status.Warn;
        }
        int at = text.IndexOf(find, StringComparison.Ordinal);
        File.WriteAllBytes(path, Encoding.Latin1.GetBytes(text[..at] + replace + text[(at + find.Length)..]));
        line = what;
        return Status.Fixed;
    }

    // Whether the files read as a check expects: its texts there, and each word as often as one of its counts (the
    // first for the game's file, any of them once the file holds the port's shadow edits)
    private static bool Holds(string shaders, Step check)
    {
        var texts = new Dictionary<string, string?>();
        string? Text(string p)
        {
            if (!texts.TryGetValue(p, out var t))
            {
                var full = System.IO.Path.Combine(shaders, p);
                texts[p] = t = File.Exists(full) ? ReadBytes(full).Replace("\r\n", "\n") : null;
            }
            return t;
        }
        foreach (var (p, expected) in check.Contains)
            if (Text(p)?.Contains(Bytes(expected), StringComparison.Ordinal) != true)
                return false;
        foreach (var (p, word, counts) in check.Counts)
        {
            var t = Text(p);
            if (t == null)
                return false;
            var allowed = t.Contains(ShadowEditMarker, StringComparison.Ordinal) ? counts : counts[..1];
            if (!allowed.Contains(Regex.Matches(t, $@"\b{Regex.Escape(word)}\b", RegexOptions.ECMAScript).Count))
                return false;
        }
        return true;
    }

    // How often <what> is in <text>, not overlapping
    private static int Count(string text, string what)
    {
        int n = 0;
        for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(what, at + what.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    // A file as its bytes, one char each, and an edit's text as the same bytes: an edit leaves every other byte of a
    // shader as it was, whatever its encoding
    public static string ReadBytes(string path) => Encoding.Latin1.GetString(File.ReadAllBytes(path));
    public static string Bytes(string text) => Encoding.Latin1.GetString(Encoding.UTF8.GetBytes(text));
}
