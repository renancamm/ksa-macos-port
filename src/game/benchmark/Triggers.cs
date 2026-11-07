// The run's triggers in the game (the rules: src/shared/benchmark/Triggers.cs). Every frame: a measured part's slow
// frame and KSA Metal's GPU errors; at every checkpoint, after its screenshot: a broken picture and the checkpoint itself.
// A moment is the director's to keep (Frame's answer) and a stop its to take once the work before it is done;
// screenshots, pass dumps and frame captures go into caught/, one after the other. At a checkpoint they hold it until they
// are done; after a frame the run plays on while they work, and a part measured meanwhile is disturbed. A rule's limit
// counts in each pass of the scenarios. Each time a rule runs, a "trigger" event says which and why.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

internal sealed class TriggerRunner : IProbe
{
    // Far smaller than the clean run's file, far more pure black (percent): broken
    private const double Below = 0.5, MoreBlack = 25;
    // The longest a dump or a capture takes (s), which a checkpoint and a stop wait for at most
    private const double WorkSeconds = 150;

    private readonly List<Rule> _rules;
    private readonly int[] _ran;
    private readonly string? _against;
    private readonly ProbeContext _context;
    private readonly EventLog _events;
    private uint _gpuErrors;
    // What a frame's rules started, one after the other, and the one at work
    private readonly Queue<(string What, Func<Wait?> Start)> _queue = new();
    private (string What, Wait Done)? _working;
    private readonly HashSet<PartKey> _disturbed = new();
    private readonly int _session;
    private int _caught, _pass = -1;
    // A checkpoint's work under way; frames after a slow frame's work, which it slowed itself
    private bool _holding;
    private int _quiet;
    private const int QuietFrames = 3;
    // A rule's stop, held until the work before it is done (or has taken too long)
    private (string Why, bool Failed)? _stop;
    private readonly Stopwatch _stopWaits = new();

    public TriggerRunner(List<Rule> rules, string against, int session, ProbeContext context, EventLog events)
    {
        _rules = rules;
        _session = session;
        _ran = new int[rules.Count];
        _against = against.Length > 0 ? against : null;
        _context = context;
        _events = events;
        _gpuErrors = DriverStats.GpuErrors();
    }

    public string Name => "triggers " + string.Join("; ", _rules);

    // Whether work the rules started goes on: a slow frame's, or a checkpoint's
    public bool Busy => _queue.Count > 0 || _working != null || _holding;

    // A rule's stop, once the work its rules started is done: why, and whether the run failed there
    public (string Why, bool Failed)? Stop =>
        _stop != null && (!Busy || _stopWaits.Elapsed.TotalSeconds >= WorkSeconds) ? _stop : null;

    private void StopAfterWork(string why, bool failed)
    {
        _stop ??= (why, failed);
        _stopWaits.Start();
    }

    // Every frame: how long it took (0: no frame of the script), the part it counted in (null: none), the scenario
    // playing and the pass. Whether to keep it as a moment
    public bool Frame(double frameMs, PartKey? part, string? scenario, int pass)
    {
        // Each pass has its rules' limits anew: a warm-up's slow frames leave the measured passes theirs
        if (pass != _pass)
        {
            _pass = pass;
            Array.Clear(_ran);
        }
        bool moment = false;
        uint errors = DriverStats.GpuErrors();
        bool failed = errors > _gpuErrors;
        _gpuErrors = errors;
        // Once a stop is due, only the work before it goes on
        for (int i = 0; i < _rules.Count && _stop == null; i++)
        {
            var rule = _rules[i];
            if (_ran[i] >= rule.Max)
                continue;
            string? why = rule.When switch
            {
                Condition.Frame when part is { } p && frameMs >= rule.Ms && _quiet == 0 => FormattableString.Invariant($"a frame of {frameMs:F0} ms in {p.Part}"),
                Condition.GpuError when failed => $"{errors} command buffers failed",
                _ => null,
            };
            if (why == null)
                continue;
            _ran[i]++;
            _events.Add("trigger", scenario, part?.Part, text: $"{rule}: {why}");
            _context.Log($"trigger {rule}: {why}");
            // A later session's own names: its files don't go into the first's
            ++_caught;
            var number = _session > 1 ? $"{_caught}-{_session}" : $"{_caught}";
            var name = rule.When == Condition.GpuError ? $"gpu-error_{number}" : $"{scenario}_{part?.Part}_{number}";
            foreach (var action in rule.Actions)
            {
                switch (action)
                {
                    case TriggerAction.Moment:
                        moment = true;
                        break;
                    case TriggerAction.Stop:
                        StopAfterWork($"stopped by the trigger {rule} ({why})", rule.When == Condition.GpuError);
                        break;
                    default:
                        _queue.Enqueue((Triggers.Name(action), () => Start(action, name, name)));
                        break;
                }
            }
        }
        Work();
        // A frame that work slowed is the work's, not one to catch: none counts until a few frames after it
        _quiet = _working != null || _queue.Count > 0 ? QuietFrames : Math.Max(0, _quiet - 1);
        // A heavy action makes the frames slow while it works (a warm-up's are never in the numbers)
        if (part is { Pass: > 0 } measured && _working is { } w && _disturbed.Add(measured))
            _events.Add("disturbed", measured.Scenario, measured.Part, text: $"the trigger's {w.What} worked while it measured");
        return moment;
    }

    // The queue's work: the next started once the one before is done
    private void Work()
    {
        while (true)
        {
            if (_working is { } w && !w.Done())
                return;
            _working = null;
            if (_queue.Count == 0)
                return;
            var (what, start) = _queue.Dequeue();
            if (start() is { } done)
                _working = (what, done);
        }
    }

    // After a checkpoint's screenshot: the rules of broken pictures and of checkpoints, their work holding the checkpoint
    public Wait? AfterShot(View view)
    {
        var rules = Enumerable.Range(0, _rules.Count)
            .Where(i => _rules[i].When is Condition.Broken or Condition.Checkpoint && _ran[i] < _rules[i].Max).ToList();
        if (rules.Count == 0 || _stop != null)
            return null;
        // A slow frame's work and a dump the driver's watch started go first (KSA Metal does one at a time); this view's
        // own comes after them. The size counts once the driver has written all of the file
        _holding = true;
        var work = Probes.Then(Probes.Until(() => _queue.Count == 0 && _working == null && !DriverStats.PassDumping() && !DriverStats.Capturing() &&
                                                  Images.Written(view.Path), WorkSeconds), () =>
        {
            // A stop that came meanwhile (a GPU error): nothing more
            if (_stop != null || Images.Read(view.Path) == null)
                return null;
            var actions = new List<TriggerAction>();
            string? broken = null, stop = null;
            foreach (var i in rules)
            {
                var rule = _rules[i];
                string? why = rule.When == Condition.Checkpoint ? "a checkpoint" : broken ??= LooksBroken(view.Path);
                if (why == null)
                    continue;
                _ran[i]++;
                _events.Add("trigger", view.Scenario, view.Shot, text: $"{rule}: {why}");
                _context.Log($"{view.Shot}: trigger {rule}: {why}");
                actions.AddRange(rule.Actions.Where(a => !actions.Contains(a)));
                if (rule.Has(TriggerAction.Stop))
                    stop ??= $"stopped by the trigger {rule} ({view.Shot}: {why})";
            }
            var name = Path.GetFileNameWithoutExtension(view.Path) + (_session > 1 ? $"-{_session}" : "");
            // The dump first, of the frame after the screenshot; whether the fault lasted, in an image after the rest
            Wait? chain = null;
            foreach (var action in new[] { TriggerAction.Passes, TriggerAction.Capture, TriggerAction.Screenshot }.Where(actions.Contains))
                chain = chain == null ? Start(action, name, view.Shot) : Probes.Then(chain, () => Start(action, name, view.Shot));
            // A stop once the checkpoint's work is done
            return stop == null ? chain : Probes.Then(chain ?? (() => true), () =>
            {
                StopAfterWork(stop, false);
                return null;
            });
        });
        return Probes.Then(work, () =>
        {
            _holding = false;
            return null;
        });
    }

    // A heavy action under this name in caught/, and the wait for its work; null when it can't run. For the log: what
    // it was for
    private Wait? Start(TriggerAction action, string name, string label)
    {
        var dir = Path.Combine(_context.Dir, "caught");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        switch (action)
        {
            case TriggerAction.Screenshot:
                var image = path + ".png";
                if (!DriverStats.Screenshot(image))
                    return null;
                return Probes.Until(() => Images.Written(image), 10);
            case TriggerAction.Passes or TriggerAction.Capture when DriverStats.GpuErrors() > 0:
                _context.Log($"{label}: no {Triggers.Name(action)}: the GPU has failed and refuses this game's work");
                return null;
            case TriggerAction.Passes:
                if (!DriverStats.PassDump(path))
                {
                    _context.Log($"{label}: no pass dump: KSA Metal could not start one");
                    return null;
                }
                _context.Log($"{label}: its passes in caught/{name}/ (present {DriverStats.Read()?.Present})");
                return Probes.Then(Probes.Until(() => !DriverStats.PassDumping(), 120), () =>
                {
                    if (DriverStats.PassDumping())
                        _context.Log($"{label}: the pass dump did not finish");
                    return null;
                });
            case TriggerAction.Capture:
                var gputrace = path + ".gputrace";
                if (!DriverStats.Capture(gputrace, 1))
                {
                    _context.Log($"{label}: no frame capture: KSA Metal could not start one (does the game have MTL_CAPTURE_ENABLED=1?)");
                    return null;
                }
                return Probes.Then(Probes.Until(() => !DriverStats.Capturing(), 120), () =>
                {
                    _context.Log($"{label}: frame capture {(Directory.Exists(gputrace) || File.Exists(gputrace) ? Path.GetFileName(gputrace) : "missing")}");
                    return null;
                });
            default:
                return null;
        }
    }

    // What is wrong with the screenshot, or null. A real view reaches about a fifth of pure white, whatever the clean run
    // shows. Against the clean run's, at the same size in pixels (at another, the bytes say nothing): far smaller, or far
    // more pure black, which a dark view has anyway
    private string? LooksBroken(string path)
    {
        var shot = Images.Read(path);
        if (shot == null)
            return null;
        if (Images.WhitePercent(shot) >= Checks.BlownOutPercent)
            return "pure white";
        if (_against == null)
            return null;
        var clean = new FileInfo(Path.Combine(_against, Path.GetFileName(path)));
        var reference = clean.Exists ? Images.Read(clean.FullName) : null;
        if (reference == null || shot.Width != reference.Width || shot.Height != reference.Height)
            return null;
        if (new FileInfo(path).Length < clean.Length * Below)
            return "far smaller than in the clean run";
        return PureBlackPercent(shot) > PureBlackPercent(reference) + MoreBlack ? "far more black than in the clean run" : null;
    }

    private static double PureBlackPercent(Image image)
    {
        long black = 0;
        var rgb = image.Rgb;
        for (int i = 0; i < rgb.Length; i += 3)
            if (rgb[i] <= 1 && rgb[i + 1] <= 1 && rgb[i + 2] <= 1)
                black++;
        return 100.0 * black / Math.Max(1, rgb.Length / 3);
    }
}
