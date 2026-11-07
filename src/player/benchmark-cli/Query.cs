// query and timeline: a run's store read directly. query runs SQL on it (run.db, with host.db attached as host) and
// prints the rows; timeline writes it as a trace for Perfetto's viewer and opens the page that hands it over (Timeline.cs).
using System;
using System.IO;
using System.Linq;
using System.Text;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    // Rows printed at most: more are for sqlite3 itself
    private const int QueryRows = 200;

    private static int QueryCommand(string[] args)
    {
        if (args.Length is 0 or > 2)
            Die("query [run] '<sql>': SQL on a run's store, e.g. query \"SELECT what, COUNT(*), MAX(ms) FROM waits GROUP BY what\".");
        var dir = StoreOf(args.Length == 2 ? args[0] : "last");
        var sql = args[^1];
        using var db = Sqlite.Open(Path.Combine(dir, RunStore.GameFile), readOnly: true);
        if (File.Exists(Path.Combine(dir, RunStore.HostFile)))
            db.Exec($"ATTACH '{Path.Combine(dir, RunStore.HostFile).Replace("'", "''")}' AS host");
        Sqlite.Statement q;
        try
        {
            q = db.Prepare(sql);
        }
        catch (SqliteException e)
        {
            Die(e.Message.Replace(sql + ": ", ""));
            return 1;
        }
        using (q)
        {
            var md = new StringBuilder();
            int rows = 0;
            while (q.Step())
            {
                if (rows == 0)
                {
                    md.AppendLine($"| {string.Join(" | ", Enumerable.Range(0, q.Columns).Select(q.Name))} |");
                    md.AppendLine($"|{string.Concat(Enumerable.Range(0, q.Columns).Select(_ => "---|"))}");
                }
                if (++rows <= QueryRows)
                    md.AppendLine($"| {string.Join(" | ", Enumerable.Range(0, q.Columns).Select(i => q.Text(i).Replace('|', '/')))} |");
            }
            Console.Write(rows == 0 ? "No rows.\n" : Terminal.Layout(md.ToString()));
            if (rows > QueryRows)
                Output.Info($"{rows} rows, the first {QueryRows} shown: for all of them, sqlite3 {Path.Combine(dir, RunStore.GameFile)}");
        }
        return 0;
    }

    private static int TimelineCommand(string[] args)
    {
        if (args.Length > 1)
            Die("timeline [run]: one run.");
        var dir = StoreOf(args.Length == 1 ? args[0] : "last");
        Timeline.Write(dir);
        Output.Info($"The timeline: {Path.Combine(dir, Timeline.PageFile)} (its trace: {Timeline.TraceFile}, for ui.perfetto.dev)");
        if (!Console.IsOutputRedirected)
            Tool.Exec("open", [Path.Combine(dir, Timeline.PageFile)], out _);
        return 0;
    }

    // A run's folder with a store
    private static string StoreOf(string run)
    {
        var dir = Unpacked(RunFolder(run));
        if (!File.Exists(Path.Combine(dir, RunStore.GameFile)))
            Die($"{Path.GetFileName(dir.TrimEnd('/'))} has no store (run.db): a run older than the store has none.");
        return dir;
    }
}
