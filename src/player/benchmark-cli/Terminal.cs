// What the report program prints: its Markdown laid out for a terminal. Tables get aligned columns, their separator goes,
// and **bold** is bold on a terminal and plain text elsewhere (a file, a pipe), so the output reads the same in both.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace KsaMetal.Benchmark.Cli;

public static class Terminal
{
    private static bool Colour => !Console.IsOutputRedirected && Environment.GetEnvironmentVariable("NO_COLOR") is null &&
                                  Environment.GetEnvironmentVariable("TERM") != "dumb";

    private static string Bold(string text) => Colour ? $"\x1b[1m{text}\x1b[0m" : text;

    private static string Dim(string text) => Colour ? $"\x1b[2m{text}\x1b[0m" : text;

    // A line's **bold** parts, and the length it shows at
    private static (string Text, int Width) Inline(string line)
    {
        var parts = line.Split("**");
        var text = new StringBuilder();
        for (int i = 0; i < parts.Length; i++)
            text.Append(i % 2 == 1 && i < parts.Length - 1 ? Bold(parts[i]) : parts[i]);
        return (text.ToString(), parts.Sum(p => p.Length));
    }

    public static string Layout(string markdown)
    {
        var lines = markdown.Replace("\r", "").Split('\n');
        var output = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            if (!lines[i].StartsWith('|'))
            {
                var line = lines[i];
                if (line.StartsWith('#'))
                    output.AppendLine(Bold(line.TrimStart('#', ' ')));
                else if (line.Length > 0 && line[0] != '-' && line[0] != '*' && Muted(line))
                    foreach (var part in Wrapped(line))
                        output.AppendLine(Dim(part));
                else
                    output.AppendLine(Inline(line).Text);
                continue;
            }
            int end = i;
            while (end < lines.Length && lines[end].StartsWith('|'))
                end++;
            Table(output, lines[i..end]);
            i = end - 1;
        }
        return output.ToString().TrimEnd() + "\n";
    }

    // A note on how to read what comes before it, which a terminal shows dim
    private static bool Muted(string line) => line.StartsWith("Frame: ", StringComparison.Ordinal) || line.StartsWith("Speed: ", StringComparison.Ordinal) ||
                                              line.StartsWith("Changed: ", StringComparison.Ordinal) || line.StartsWith("Changes over", StringComparison.Ordinal) ||
                                              line.StartsWith("Both runs were repeated", StringComparison.Ordinal) || line.StartsWith("Fast mode: ", StringComparison.Ordinal) ||
                                              line.StartsWith("Each part is measured", StringComparison.Ordinal);

    // A long note in lines a window can show
    private static IEnumerable<string> Wrapped(string line)
    {
        const int width = 110;
        var current = new StringBuilder();
        foreach (var word in line.Split(' '))
        {
            if (current.Length > 0 && current.Length + word.Length + 1 > width)
            {
                yield return current.ToString();
                current.Clear();
            }
            current.Append(current.Length > 0 ? " " : "").Append(word);
        }
        yield return current.ToString();
    }

    private static void Table(StringBuilder output, string[] rows)
    {
        static string[] Cells(string row) => row.Trim().Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        var separator = Array.FindIndex(rows, r => r.Replace(" ", "").TrimStart('|').StartsWith("---", StringComparison.Ordinal) || r.Replace(" ", "").TrimStart('|').StartsWith(":--", StringComparison.Ordinal));
        var right = separator >= 0 ? Cells(rows[separator]).Select(c => c.EndsWith(':')).ToArray() : Array.Empty<bool>();
        var table = rows.Where((_, n) => n != separator).Select(Cells).ToList();
        // A table without a header (the facts about a run)
        if (separator == 1 && table[0].All(c => c.Length == 0))
            table.RemoveAt(0);
        int columns = table.Max(r => r.Length);
        var widths = new int[columns];
        foreach (var row in table)
            for (int c = 0; c < row.Length; c++)
                widths[c] = Math.Max(widths[c], Inline(row[c]).Width);
        for (int n = 0; n < table.Count; n++)
        {
            var line = new StringBuilder("  ");
            for (int c = 0; c < columns; c++)
            {
                var (text, width) = Inline(c < table[n].Length ? table[n][c] : "");
                int pad = widths[c] - width;
                bool toRight = c < right.Length && right[c];
                line.Append(toRight ? new string(' ', pad) + text : text + new string(' ', c == columns - 1 ? 0 : pad));
                if (c < columns - 1)
                    line.Append("  ");
            }
            var shown = line.ToString().TrimEnd();
            output.AppendLine(n == 0 && separator == 1 && rows[0].Replace("|", "").Trim().Length > 0 ? Dim(shown) : shown);
        }
    }
}
