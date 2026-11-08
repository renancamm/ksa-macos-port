// study <file> | study resume [study] | study report [study]: variants of one command played in game sessions of their
// own, in an interleaved order, and set against the baseline variant by their sessions (Study.cs has the file and the
// order). A study lives in a folder of its own in the benchmarks folder's studies/: its file (study.txt, read again on
// resume, so the one it came from can change), your settings as they were when it began (every session starts from
// them), each session's run in <slot>-<variant>, and its report. It waits while another game runs; Ctrl-C stops the
// session playing and the study, which picks up there, after a crash or a reboot too: a session whose run passed is done,
// one that failed is played again at once, and one that failed twice is left out.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark.Cli;

public static partial class Program
{
    private static string Studies => Path.Combine(Benchmarks, "studies");

    // A session that failed this many times, or played this many without passing (stops aside), is left out
    private const int Attempts = 4, Failures = 2;

    private static int StudyCommand(string[] args)
    {
        switch (args)
        {
            case ["resume", .. var rest] when rest.Length <= 1:
                return PlayStudy(StudyFolder(rest.FirstOrDefault(), unfinished: true));
            case ["report", .. var rest] when rest.Length <= 1:
                return StudyReport(StudyFolder(rest.FirstOrDefault(), unfinished: false));
            case [var file] when !file.StartsWith('-') && file is not ("resume" or "report"):
                if (!File.Exists(file))
                    Die($"no study file {file}.");
                Study study;
                try
                {
                    study = Study.Read(file);
                }
                catch (StudyException e)
                {
                    Die(e.Message);
                    return 1;
                }
                return PlayStudy(NewStudy(study, File.ReadAllText(file), Path.GetFileNameWithoutExtension(file)));
            default:
                Die("study <file> | study resume [study] | study report [study].");
                return 1;
        }
    }

    // A study's folder, made for it: its variants checked as runs are before any game starts, its file copied in
    internal static string NewStudy(Study study, string text, string name)
    {
        foreach (var v in study.Variants)
        {
            try
            {
                new BenchmarkRun().Validate(study.Arguments(v), StudyPurpose(study));
            }
            catch (PortException e)
            {
                Die($"the variant {v.Name}: {e.Message}");
            }
        }
        var folder = Path.Combine(Studies, $"{DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture)}_{new StudyVariant(name, []).Slug}");
        if (Directory.Exists(folder))
            folder += "_" + DateTime.Now.ToString("ss", CultureInfo.InvariantCulture);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, Study.FileName), text);
        // Your settings as they are now: a game you play while the study waits saves its own when it quits
        foreach (var file in new[] { "settings.toml", "port-settings.toml" })
            if (File.Exists(Path.Combine(Docs, file)))
                File.Copy(Path.Combine(Docs, file), Path.Combine(folder, file));
        return folder;
    }

    // Whether no benchmark run holds the lock of a game starting or playing
    private static bool Free()
    {
        using var playing = BenchmarkRun.Playing();
        return playing != null;
    }

    // Why a session can't play now (a game running, a benchmark run's lock, pgrep without an answer), or null
    private static string? BusyNow() => BenchmarkRun.GamesRunning() switch
    {
        null => "pgrep can't tell whether a game is running",
        { Count: > 0 } => "A game is running",
        _ => Free() ? null : "Another benchmark run is playing",
    };

    private static Purpose StudyPurpose(Study study) => study.Verb == "profile" ? Purpose.Profile : Purpose.Measure;

    // A study by its folder's path or name in studies/; none: the newest (with sessions still to play, for resume)
    private static string StudyFolder(string? name, bool unfinished)
    {
        if (name != null)
        {
            foreach (var dir in new[] { name.TrimEnd('/'), Path.Combine(Studies, name.TrimEnd('/')) })
                if (File.Exists(Path.Combine(dir, Study.FileName)))
                    return dir;
            Die($"no study '{name}' (a study's folder holds its {Study.FileName}).");
        }
        var all = (Directory.Exists(Studies) ? Directory.GetDirectories(Studies) : []).Where(d => File.Exists(Path.Combine(d, Study.FileName)))
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal).ToList();
        if (all.Count == 0)
            Die($"no study yet in {Studies}.");
        if (!unfinished)
            return all[0];
        return all.FirstOrDefault(d => SlotStates(d, ReadStudy(d)).Any(s => s.State == SlotState.ToPlay))
               ?? throw new PortException($"every study in {Studies} is done (./benchmark.sh study report shows the last).");
    }

    private static Study ReadStudy(string folder)
    {
        try
        {
            return Study.Read(Path.Combine(folder, Study.FileName));
        }
        catch (StudyException e)
        {
            throw new PortException(e.Message);
        }
    }

    private enum SlotState { Done, ToPlay, LeftOut }

    // Each session of the queue: whether it is done (its run, the one that passed), still to play (and as which attempt),
    // or left out after too many attempts
    private static List<(StudySlot Slot, SlotState State, string? Run, int Next)> SlotStates(string folder, Study study)
    {
        var queue = study.Queue();
        var states = new List<(StudySlot, SlotState, string?, int)>();
        foreach (var slot in queue)
        {
            int attempt = 1, failed = 0, played = 0;
            string? passed = null;
            for (; Directory.Exists(Path.Combine(folder, slot.Folder(queue.Count, attempt))); attempt++)
            {
                var dir = Path.Combine(folder, slot.Folder(queue.Count, attempt));
                switch (Record(dir)?.Status ?? (File.Exists(Path.Combine(dir, "error.txt")) ? Status.Failed : null))
                {
                    case Status.Passed:
                        passed ??= dir;
                        break;
                    case Status.Failed:
                        failed++;
                        played++;
                        break;
                    case null:
                        played++;
                        break;
                }
            }
            var state = passed != null ? SlotState.Done : failed >= Failures || played >= Attempts ? SlotState.LeftOut : SlotState.ToPlay;
            states.Add((slot, state, passed, attempt));
        }
        return states;
    }

    // A record of another schema's status (a benchmark updated while a study played): a session that passed stays played,
    // its report made again from its store
    private static Status? OlderStatus(string dir)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(RunFiles.Data(dir), RunRecord.FileName)));
            return json.RootElement.TryGetProperty("status", out var s) && Enum.TryParse<Status>(s.GetString(), true, out var status) ? status : null;
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    // A session's record, made again from its store when it is of another schema (once: it is written as this schema),
    // so that its state is what this benchmark makes of it; null when there is none or it can't be made
    private static RunRecord? Record(string dir)
    {
        if (RunRecord.Read(dir) is { } record)
            return record;
        if (OlderStatus(dir) == null || !File.Exists(Path.Combine(RunFiles.Data(dir), RunStore.GameFile)))
            return null;
        Output.Info($"{Path.GetFileName(dir)}: its record is of another schema, made again from its store");
        try
        {
            return Finish.Again(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException or System.Text.Json.JsonException
                                       or FormatException or InvalidOperationException or NullReferenceException)
        {
            Output.Warn($"{Path.GetFileName(dir)}: its record could not be made again ({e.Message}): played again");
            return null;
        }
    }

    // The study's queue played from where it stands, then its report; one terminal at a time plays a study (its lock)
    private static int PlayStudy(string folder)
    {
        FileStream studying;
        try
        {
            studying = new FileStream(Path.Combine(folder, ".playing"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new PortException($"the study {Path.GetFileName(folder)} is playing in another terminal.");
        }
        using var held = studying;
        var study = ReadStudy(folder);
        var states = SlotStates(folder, study);
        int total = states.Count, done = states.Count(s => s.State == SlotState.Done);
        Output.Step($"Study {Path.GetFileName(folder)}: {study.Variants.Count} variant{(study.Variants.Count > 1 ? "s" : "")}, {study.Sessions} game session{(study.Sessions > 1 ? "s" : "")} each, " +
                    $"{(done > 0 ? $"{done} of {total} played" : $"{total} in all")}");
        Output.Info($"Every session: ./benchmark.sh {study.Verb} {string.Join(' ', study.Command.Select(Study.Quote))}");
        foreach (var v in study.Variants)
            Output.Info($"{(v == study.Baseline ? "Baseline " : "")}{v.Name}: {(v.Options.Count > 0 ? string.Join(' ', v.Options.Select(Study.Quote)) : "as the command has it")}");
        Output.Info("Ctrl-C stops the session playing and the study; ./benchmark.sh study resume plays the rest.");
        bool stopped = false;
        ConsoleCancelEventHandler stop = (_, e) =>
        {
            e.Cancel = true;
            stopped = true;
        };
        Console.CancelKeyPress += stop;
        using var term = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c =>
        {
            c.Cancel = true;
            stopped = true;
        });
        var took = new List<double>();
        try
        {
            // The queue in its order; a session that failed played again at once, in its turn
            foreach (var slot in states.Where(s => s.State == SlotState.ToPlay).Select(s => s.Slot))
            {
                for (int tries = 0; tries < 2 && !stopped;)
                {
                    var now = SlotStates(folder, study)[slot.Number - 1];
                    if (now.State != SlotState.ToPlay)
                        break;
                    switch (PlaySession(folder, study, slot, now.Next, total, states, took, () => stopped))
                    {
                        case Played.Stopped:
                            stopped = true;
                            break;
                        // A game or a run got in first after the wait: waited for again, no try spent
                        case Played.NotNow:
                            break;
                        default:
                            tries++;
                            break;
                    }
                }
                if (stopped)
                    break;
            }
        }
        finally
        {
            Console.CancelKeyPress -= stop;
        }
        Console.WriteLine();
        int status = StudyReport(folder);
        if (SlotStates(folder, study).Any(s => s.State == SlotState.ToPlay))
        {
            Output.Step($"Stopped: {folder}");
            Output.Info($"To play the rest: ./benchmark.sh study resume {Path.GetFileName(folder)}");
            return 1;
        }
        return status;
    }

    private enum Played { Yes, Stopped, NotNow }

    // One session of the queue, once no other game is running
    private static Played PlaySession(string folder, Study study, StudySlot slot, int attempt, int total,
        List<(StudySlot Slot, SlotState State, string? Run, int Next)> states, List<double> took, Func<bool> stopped)
    {
        // The queue waits for a game that is running (another study's, or yours) or a benchmark run starting
        bool said = false;
        while (!stopped() && BusyNow() is { } why)
        {
            if (!said)
                Output.Info($"{why}: the study waits.");
            said = true;
            Thread.Sleep(10_000);
        }
        if (stopped())
            return Played.Stopped;
        int left = states.Count(s => s.State == SlotState.ToPlay && s.Slot.Number >= slot.Number);
        Console.WriteLine();
        Output.Step($"Session {slot.Number} of {total}: {slot.Variant.Name} (round {slot.Round}{(attempt > 1 ? $", attempt {attempt}" : "")})" +
                    (took.Count > 0 ? $", about {Math.Ceiling(took.Average() * left / 60)} minutes left" : ""));
        var started = Stopwatch.StartNew();
        // Each variant's first round takes the screenshots, which its report holds against the baseline's
        var run = new BenchmarkRun
        {
            Into = Path.Combine(folder, slot.Folder(total, attempt)),
            TakesShots = slot.Round == 1,
            Yours = File.Exists(Path.Combine(folder, "settings.toml")) ? folder : Docs,
        };
        try
        {
            run.Run(study.Arguments(slot.Variant), StudyPurpose(study));
        }
        // A game or a run that started since the wait: the session waits again. Anything else (its app gone, a full disk)
        // is an attempt that failed, its folder holding why, so that a variant that never plays is left out in the end
        catch (Exception e) when (e is PortException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException
                                       or FormatException or InvalidOperationException)
        {
            if (!Directory.Exists(run.Into) && BusyNow() != null)
            {
                Output.Info($"session {slot.Number}: {e.Message}");
                return Played.NotNow;
            }
            Output.Warn($"session {slot.Number}: {e.Message}");
            Directory.CreateDirectory(run.Into);
            File.WriteAllText(Path.Combine(run.Into, "error.txt"), e.Message + "\n");
        }
        took.Add(started.Elapsed.TotalSeconds);
        return run.Stopped ? Played.Stopped : Played.Yes;
    }

    // The study's report: each variant against the baseline by its sessions, part by part, with its screenshots held
    // against the baseline's; report.md, report.html, and the summary here
    private static int StudyReport(string folder)
    {
        var study = ReadStudy(folder);
        var states = SlotStates(folder, study);
        var runs = study.Variants.ToDictionary(v => v, v => states.Where(s => s.Slot.Variant == v && s.Run != null)
            .Select(s => (Dir: s.Run!, Record: Record(s.Run!)!)).Where(r => r.Record != null).ToList());
        var baseRuns = runs[study.Baseline];
        var baseParts = Study.Merged(baseRuns.Select(r => r.Record));
        var variants = new List<StudyPage.Variant>();
        foreach (var v in study.Variants.Where(v => v != study.Baseline))
        {
            if (runs[v].Count == 0 || baseRuns.Count == 0)
            {
                variants.Add(new StudyPage.Variant(v.Name, v.Options, null, runs[v].Count == 0 ? "no session of it passed yet" : "no session of the baseline passed yet", null, [], 0, null));
                continue;
            }
            var c = Comparison.Of(baseParts, Study.Merged(runs[v].Select(r => r.Record)), runs[v].Concat(baseRuns).Any(r => Ground.Steps(r.Record)));
            // The screenshots of each one's first session that took them
            static (string Dir, RunRecord Record)? WithShots(List<(string Dir, RunRecord Record)> of) =>
                of.Cast<(string Dir, RunRecord Record)?>().FirstOrDefault(r => Directory.GetFiles(RunFiles.Data(r!.Value.Dir), "*_*.png").Length > 0);
            var shots = new List<ShotResult>();
            string? image = null;
            int different = 0;
            string? shotsOf = null;
            if (WithShots(baseRuns) is { } a && WithShots(runs[v]) is { } b && !a.Record.InGame && !b.Record.InGame)
            {
                Screenshots(a.Dir, b.Dir, a.Record, b.Record, shots);
                int compared = shots.Count(s => s.State != "free");
                different = shots.Count(s => s.State is "differs" or "unreadable");
                if (compared > 0)
                    image = different == 0 ? $"same image ({compared} of {compared} screenshots)" : $"{different} of {compared} screenshots differ";
                shotsOf = Path.GetRelativePath(folder, RunFiles.Data(b.Dir));
            }
            var (sentence, good) = c.Sentence(image, different == 0);
            variants.Add(new StudyPage.Variant(v.Name, v.Options, c, sentence, good, shots, different, shotsOf));
        }
        var sessions = states.Select(s => new StudyPage.Session(s.Slot.Number, s.Slot.Round, s.Slot.Variant.Name,
            s.Run != null ? Path.GetRelativePath(folder, s.Run) : null,
            s.State switch { SlotState.Done => "passed", SlotState.LeftOut => "left out: it failed", _ => "to play" })).ToList();
        var page = new StudyPage(Path.GetFileName(folder), $"./benchmark.sh {study.Verb} {string.Join(' ', study.Command.Select(Study.Quote))}",
            study.Baseline.Name, study.Baseline.Options, study.Sessions, variants, sessions, baseRuns.FirstOrDefault().Record,
            StudyPage.Ground(runs.Where(r => r.Value.Count > 0).ToDictionary(r => r.Key.Name, r => r.Value.Select(x => x.Record).ToList())));
        File.WriteAllText(Path.Combine(folder, "report.md"), page.Markdown());
        Pages.WriteStudy(folder, page);
        Console.Write(Terminal.Layout(page.Markdown(terminal: true)));
        Console.WriteLine();
        Output.Step($"Study: {folder}");
        Output.Info($"Its page: {folder}/report.html");
        return states.Any(s => s.State == SlotState.LeftOut) || variants.Any(v => v.Comparison == null) ? 1 : 0;
    }
}
