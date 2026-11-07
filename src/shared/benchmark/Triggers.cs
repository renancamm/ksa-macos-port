// A run's triggers: rules of the form "<condition>: <action>, <action>", each run when its condition holds, a limited
// number of times. The run's host writes them into the run's plan (RunPlan.On: benchmark.sh's --on, and its
// investigate catch and passes); the director runs them in the game (src/game/benchmark/Triggers.cs), and the host
// watching the game the actions only it can take (a thread sample of a stuck game). Every run carries the defaults; a
// rule of the run's own for the same condition takes the default's place.
//
//   Condition       Holds                                                              Actions
//   frame>N ms      a frame of a measured part took N ms or more                       moment, screenshot, passes, capture, stop
//   broken          a checkpoint's screenshot looks broken: 30 % pure white, or        screenshot, passes, capture, stop
//                   against a clean run's (RunPlan.Against) far smaller or far
//                   more pure black
//   checkpoint      a checkpoint's screenshot is there                                 screenshot, passes, capture
//   stuck           the game draws its frames but its clock stands still               sample, stop
//                   (Watchdog.Stuck); the run stops whatever the rule says
//   gpu-error       a command buffer failed (KSA Metal counts them): the GPU refuses   moment, screenshot, stop
//                   the game's work from then on
//
//   moment       the zones, KSA Metal's waits and the GPU times around the frame, in the run's store (Zones.cs)
//   screenshot   an image of a frame after it, in caught/
//   passes       KSA Metal's pass dump of the next frame, in caught/<name>/
//   capture      an Xcode GPU frame capture of the next frame, caught/<name>.gputrace (the game needs MTL_CAPTURE_ENABLED=1)
//   sample       what the game's threads are doing, 5 s of /usr/bin/sample, in stuck.txt
//   stop         the run ends there
//
// "max N" among the actions: the rule runs N times a pass at most (by default 20 for frame, 3 for broken, 1 for gpu-error). A
// cheap action (moment, stop) copies what is in memory; a heavy one in a measured part makes frames there slow, so the
// part's numbers in that pass are left out of the run's record, which says so (the "disturbed" event).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace KsaPort.Common.Benchmark;

public enum Condition { Frame, Broken, Checkpoint, Stuck, GpuError }

public enum TriggerAction { Moment, Screenshot, Passes, Capture, Sample, Stop }

// A rule: its condition (Ms: frame's limit), its actions in the order they run, and how many times it runs at most
public sealed record Rule(Condition When, double Ms, IReadOnlyList<TriggerAction> Actions, int Max)
{
    public bool Has(TriggerAction action) => Actions.Contains(action);

    // As the rule is written: "frame>100ms: moment, max 20"
    public override string ToString() =>
        $"{Triggers.Name(When, Ms)}: {string.Join(", ", Actions.Select(Triggers.Name))}{(Max != Triggers.DefaultMax(When) ? $", max {Max}" : "")}";
}

public static class Triggers
{
    // The rules every run carries: a slow frame kept as a moment, and a stuck game's threads sampled before it stops
    public static readonly string[] Defaults = ["frame>100ms: moment", "stuck: sample, stop"];

    private static readonly Regex Frame = new(@"^frame\s*>\s*(\d+(?:\.\d+)?)\s*(?:ms)?$", RegexOptions.CultureInvariant);

    private static readonly Dictionary<string, Condition> Conditions = new()
    {
        ["broken"] = Condition.Broken, ["checkpoint"] = Condition.Checkpoint, ["stuck"] = Condition.Stuck, ["gpu-error"] = Condition.GpuError,
    };

    private static readonly Dictionary<Condition, TriggerAction[]> Allowed = new()
    {
        [Condition.Frame] = [TriggerAction.Moment, TriggerAction.Screenshot, TriggerAction.Passes, TriggerAction.Capture, TriggerAction.Stop],
        [Condition.Broken] = [TriggerAction.Screenshot, TriggerAction.Passes, TriggerAction.Capture, TriggerAction.Stop],
        [Condition.Checkpoint] = [TriggerAction.Screenshot, TriggerAction.Passes, TriggerAction.Capture],
        [Condition.Stuck] = [TriggerAction.Sample, TriggerAction.Stop],
        [Condition.GpuError] = [TriggerAction.Moment, TriggerAction.Screenshot, TriggerAction.Stop],
    };

    public static int DefaultMax(Condition when) => when switch
    {
        Condition.Frame => 20,
        Condition.Broken => 3,
        Condition.GpuError or Condition.Stuck => 1,
        _ => int.MaxValue,
    };

    // Whether an action makes the game's frames slow while it works: anything but a moment and a stop
    public static bool Heavy(TriggerAction action) => action is not (TriggerAction.Moment or TriggerAction.Stop);

    public static string Name(TriggerAction action) => action.ToString().ToLowerInvariant();

    public static string Name(Condition when, double ms = 0) => when switch
    {
        Condition.Frame => $"frame>{ms.ToString("0.################", CultureInfo.InvariantCulture)}ms",
        Condition.GpuError => "gpu-error",
        _ => when.ToString().ToLowerInvariant(),
    };

    // A rule as written; FormatException says what is wrong with it
    public static Rule Parse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon < 0)
            throw new FormatException($"'{text}': a rule is <condition>: <action>[, <action>...], as 'frame>50ms: moment'");
        var head = text[..colon].Trim().ToLowerInvariant();
        Condition when;
        double ms = 0;
        if (Frame.Match(head) is { Success: true } frame)
        {
            when = Condition.Frame;
            ms = double.Parse(frame.Groups[1].Value, CultureInfo.InvariantCulture);
            if (ms <= 0)
                throw new FormatException($"'{text}': a frame's limit is more than 0 ms");
        }
        else if (!Conditions.TryGetValue(head, out when))
            throw new FormatException($"'{text}': no condition '{head}' (frame>N ms, broken, checkpoint, stuck, gpu-error)");
        var actions = new List<TriggerAction>();
        int max = DefaultMax(when);
        foreach (var raw in text[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var word = raw.ToLowerInvariant();
            if (word.StartsWith("max", StringComparison.Ordinal))
            {
                if (!int.TryParse(word[3..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out max) || max < 1)
                    throw new FormatException($"'{text}': max takes a number of times, 1 or more");
                continue;
            }
            if (!Enum.TryParse<TriggerAction>(word, ignoreCase: true, out var action) || !Enum.IsDefined(action) || word.Any(char.IsDigit))
                throw new FormatException($"'{text}': no action '{raw}' (moment, screenshot, passes, capture, sample, stop)");
            if (!Allowed[when].Contains(action))
                throw new FormatException($"'{text}': {Name(when, ms)} can't {Name(action)} ({string.Join(", ", Allowed[when].Select(Name))})");
            if (!actions.Contains(action))
                actions.Add(action);
        }
        if (actions.Count == 0)
            throw new FormatException($"'{text}': no action");
        return new Rule(when, ms, actions, max);
    }

    // The rules of a run: its own, and the defaults for the conditions it has none for
    public static List<Rule> For(IEnumerable<string> own)
    {
        var rules = own.Select(Parse).ToList();
        foreach (var rule in Defaults.Select(Parse))
            if (!rules.Any(r => r.When == rule.When))
                rules.Add(rule);
        return rules;
    }

    // The rules in a run's plan; a rule that can't be read is left out (the host checked them before the game started)
    public static List<Rule> For(RunPlan plan)
    {
        var own = new List<string>();
        foreach (var text in plan.On)
        {
            try
            {
                own.Add(Parse(text).ToString());
            }
            catch (FormatException)
            {
            }
        }
        return For(own);
    }

    // Whether rules dump passes or capture frames, which take minutes: the watchdog allows more (Watchdog.Limit)
    public static bool TakeMinutes(IEnumerable<Rule> rules) => rules.Any(r => r.Has(TriggerAction.Passes) || r.Has(TriggerAction.Capture));

    // Whether rules hold checkpoints for that work: the script's clock then stands still with nothing done, and a stuck game
    // can't be told from them
    public static bool HoldCheckpoints(IEnumerable<Rule> rules) =>
        rules.Any(r => r.When is Condition.Broken or Condition.Checkpoint && (r.Has(TriggerAction.Passes) || r.Has(TriggerAction.Capture)));
}
