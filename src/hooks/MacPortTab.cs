using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace KsaMetalHooks;

// The Mac Port tab in the game's settings (Esc). Its rows are the port settings (PortSettings, from the table in
// Contents/Resources/port-settings.txt), drawn with the game's own widgets, and they take part in the game's pending
// changes: the rail's and the rows' pending marks, Apply, Discard, Reset and Close, and "restart required on apply"
// with the game's Restart / Continue / Discard popup for the settings that only take effect when the game starts.
//
// The game keeps its pending changes in a copy of its settings (GameSettings._temporary), made anew when the window
// opens and by Apply, Discard and Reset. The tab's pending values go with that copy: a new copy brings them back to
// the file's values (Reset: to the defaults), and the copy's Save, the one Apply and the popup make, writes them to
// port-settings.toml. Live settings then take effect at once; the others when the game starts again.
internal static class MacPortTab
{
    // The tab's value in the game's SettingsTab enum, past the game's own
    private const int TabValue = 100;
    private const string Label = "MAC PORT";
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Type _tabType = null!;
    private static FieldInfo _openTab = null!, _temporary = null!, _order = null!, _labels = null!, _pendingFlags = null!;
    private static MethodInfo _hasAnyTabChanged = null!;
    private static Array _railOrder = null!;
    public static string[] RailLabels = [];
    public static bool[] RailPending = [];

    // The game's pending copy the tab's pending values go with, and those values (only the tab's sections: the
    // developer settings stay as the file has them)
    private static object? _bound;
    private static bool _reset;
    private static readonly Dictionary<string, string> Pending = new();

    public static string Apply(Harmony harmony)
    {
        PortSettings.Load(Hooks.Resources);
        var gs = typeof(GameSettings);
        _tabType = AccessTools.Inner(gs, "SettingsTab") ?? throw new Skip("GameSettings.SettingsTab not found");
        _openTab = Find.Field(gs, "_openTab");
        _temporary = Find.Field(gs, "_temporary");
        _order = Find.Field(gs, "_navRailOrder");
        _labels = Find.Field(gs, "_navRailLabels");
        _pendingFlags = Find.Field(gs, "_navRailPendingFlags");
        _hasAnyTabChanged = Find.Method(gs, "HasAnyTabChanged");
        // The rail with one tab more: the game's arrays are read-only fields, so the code that reads them reads these
        var order = (Array)_order.GetValue(null)!;
        _railOrder = Array.CreateInstance(_tabType, order.Length + 1);
        Array.Copy(order, _railOrder, order.Length);
        _railOrder.SetValue(Enum.ToObject(_tabType, TabValue), order.Length);
        RailLabels = [.. (string[])_labels.GetValue(null)!, Label];
        RailPending = new bool[order.Length + 1];
        var rail = new HarmonyMethod(typeof(MacPortTab), nameof(RailTranspiler));
        foreach (var name in new[] { "RefreshNavRailPendingFlags", "HasAnyTabChanged", "DrawFooter" })
            harmony.Patch(Find.Method(gs, name), transpiler: rail);
        harmony.Patch(Find.Method(gs, nameof(GameSettings.OnDrawUi)), transpiler: new HarmonyMethod(typeof(MacPortTab), nameof(DrawUiTranspiler)));
        harmony.Patch(Find.Method(gs, "DrawFooter"), transpiler: new HarmonyMethod(typeof(MacPortTab), nameof(ResetTranspiler)));
        harmony.Patch(Find.Method(gs, "IsTabChanged"), postfix: new HarmonyMethod(typeof(MacPortTab), nameof(IsTabChangedPostfix)));
        harmony.Patch(Find.Method(gs, "RequiresRestartOnApply"), postfix: new HarmonyMethod(typeof(MacPortTab), nameof(RestartPostfix)));
        harmony.Patch(Find.Method(gs, nameof(GameSettings.Save)), postfix: new HarmonyMethod(typeof(MacPortTab), nameof(SavePostfix)));
        return "on";
    }

    // The rail's three arrays, wherever the game reads them
    private static IEnumerable<CodeInstruction> RailTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        int replaced = 0;
        foreach (var i in instructions)
        {
            if (i.LoadsField(_order))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MacPortTab), nameof(RailOrder))).MoveLabelsFrom(i);
                yield return new CodeInstruction(OpCodes.Castclass, _tabType.MakeArrayType());
            }
            else if (i.LoadsField(_labels) || i.LoadsField(_pendingFlags))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(MacPortTab),
                    i.LoadsField(_labels) ? nameof(RailLabels) : nameof(RailPending))).MoveLabelsFrom(i);
            }
            else
                yield return i;
        }
        if (replaced == 0)
            throw new Skip($"{original.Name} no longer reads the settings' rail");
    }

    private static object RailOrder() => _railOrder;

    // OnDrawUi: the rail's arrays, and the tab's rows once the content's body has begun (the game's own tabs draw
    // nothing while this one is open)
    private static IEnumerable<CodeInstruction> DrawUiTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = RailTranspiler(instructions, original).ToList();
        int name = Find.Once(code, i => i.Is(OpCodes.Ldstr, "SettingsContent"), "the settings' content body");
        var beginBody = AccessTools.GetDeclaredMethods(typeof(ConsoleStyle)).Where(m => m.Name == nameof(ConsoleStyle.BeginBody)).ToList();
        int call = Find.Next(code, i => beginBody.Any(i.Calls), "the content body's BeginBody", name);
        code.Insert(call + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MacPortTab), nameof(Draw))));
        return code;
    }

    // DrawFooter's Reset makes the game's pending copy from its defaults: the tab's start from the defaults too
    private static IEnumerable<CodeInstruction> ResetTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var defaults = Find.Getter(typeof(GameSettings), "Default");
        int at = Find.Once(code, i => i.Calls(defaults), "Reset's defaults");
        code.Insert(at + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MacPortTab), nameof(OnReset))));
        return code;
    }

    private static void OnReset() => _reset = true;

    private static void IsTabChangedPostfix(object[] __args, ref bool __result)
    {
        if (Convert.ToInt32(__args[0], Inv) != TabValue)
            return;
        Sync();
        __result = Shown.Any(s => Pending[s.Key] != s.Saved);
    }

    private static void RestartPostfix(object __instance, ref bool __result)
    {
        Sync();
        if (ReferenceEquals(__instance, _bound) && Shown.Any(NeedsRestart))
            __result = true;
    }

    // Apply, or the restart popup's Restart and Continue, saved the game's pending copy: the tab's values go to the file
    private static void SavePostfix(object __instance)
    {
        if (!ReferenceEquals(__instance, _bound))
            return;
        var changed = Shown.Where(s => Pending[s.Key] != s.Saved).ToDictionary(s => s.Key, s => Pending[s.Key]);
        if (changed.Count == 0)
            return;
        try
        {
            PortSettings.Save(changed);
        }
        catch (Exception e)
        {
            Hooks.Log($"Mac Port tab: could not write {PortSettings.FilePath}: {e.Message}");
            TimedAlert.CreateWarning("The port settings could not be saved");
            return;
        }
        foreach (var s in Shown.Where(s => s.Live && changed.ContainsKey(s.Key)))
            ApplyLive(s, s.Saved);
        // macOS reads Retina's mark in Info.plist as the game starts: it is set now, for the next start
        if (PortSettings.Get("retina") is { } retina)
        {
            try
            {
                PortSettings.MarkRetina(Hooks.Resources, retina.Outside != null
                    ? retina.Running == "true"
                    : retina.Saved == "true" && PortSettings.Met(retina, s => s.Saved, _ => true));
            }
            catch (Exception e)
            {
                Hooks.Log($"Mac Port tab: could not set NSHighResolutionCapable in Info.plist: {e.Message}");
            }
        }
        Hooks.Log($"Mac Port tab: saved {string.Join(", ", changed.Select(kv => $"{kv.Key} = {kv.Value}"))}");
    }

    private static void ApplyLive(PortSetting s, string value)
    {
        if (s.Outside != null)
            return;
        s.Running = value;
        if (s.Key == "crew_portraits")
            Settings.CrewPortraits = value;
    }

    // The settings the tab shows
    private static IEnumerable<PortSetting> Shown => PortSettings.All.Where(s => s.Section != "developer");

    // The tab's values go with the game's pending copy of its settings
    private static void Sync()
    {
        var temporary = _temporary.GetValue(null);
        if (ReferenceEquals(temporary, _bound) && Pending.Count > 0)
            return;
        _bound = temporary;
        // Reset leaves alone what the tab doesn't let the player change: the developer settings, those set outside
        foreach (var s in PortSettings.All)
            Pending[s.Key] = _reset && s.Section != "developer" && s.Outside == null ? s.Default : s.Saved;
        _reset = false;
    }

    private static string Value(PortSetting s) => Pending.TryGetValue(s.Key, out var v) ? v : s.Saved;

    private static bool Game(string what)
    {
        var game = _temporary.GetValue(null) as GameSettings;
        return what switch
        {
            "vsync" => game?.Vsync ?? true,
            "plume_trail_shadows" => game?.Graphics is not { } g || g.PlumeTrails && g.PlumeTrailShadows,
            _ => true
        };
    }

    // What a setting would run with: its value once its needs are met, its default otherwise
    private static string Effective(PortSetting s, string value) => PortSettings.Met(s, Value, Game) ? value : s.Default;

    private static bool NeedsRestart(PortSetting s) =>
        !s.Live && s.Outside == null && Pending[s.Key] != s.Saved && Effective(s, Pending[s.Key]) != s.Running;

    // Why a setting can't change now, or null
    private static string? Reason(PortSetting s, out string note)
    {
        note = "";
        if (s.Outside != null)
        {
            note = "set outside";
            return $"{s.Outside} is set where the game was started, and it decides this setting instead of the file.";
        }
        if (s.Hook.Length > 0 && Hooks.Status.GetValueOrDefault(s.Hook, "").StartsWith("skipped", StringComparison.Ordinal))
        {
            note = "unavailable";
            return "Unavailable: the game's code changed, so the hook that does this was left out (logs/console.log says more).";
        }
        foreach (var need in s.Needs)
        {
            var one = new PortSetting { Needs = [need] };
            if (PortSettings.Met(one, Value, Game))
                continue;
            if (need == "game:vsync")
            {
                note = "needs V-Sync";
                return "Needs the game's V-Sync (Display).";
            }
            if (need == "game:plume_trail_shadows")
            {
                note = "needs Plume trail shadows";
                return "Needs the game's Plume trails and Plume trail shadows (Graphics).";
            }
            int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
            var other = PortSettings.Get(ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need);
            var what = other == null ? need : ne < 0 && eq > 0 ? $"{other.Label}: {other.LabelOf(need[(eq + 1)..])}" : other.Label;
            note = $"needs {what}";
            return $"Needs {what}.";
        }
        return null;
    }

    // The rows, while the tab is open (called inside the settings' content body)
    private static void Draw()
    {
        if (Convert.ToInt32(_openTab.GetValue(null), Inv) != TabValue)
            return;
        Sync();
        foreach (var (section, title) in PortSettings.Sections)
        {
            if (section == "developer")
                continue;
            ConsoleWidgets.RegionHeader(title);
            foreach (var s in PortSettings.All.Where(s => s.Section == section))
                DrawRow(s);
            if (section == "benchmark")
                DrawBenchmark();
        }
    }

    private static void DrawRow(PortSetting s)
    {
        string value = s.Outside != null ? s.Running : Pending[s.Key];
        bool pending = s.Outside == null && value != s.Saved;
        var reason = Reason(s, out var note);
        if (reason == null && !pending && !s.Live && Effective(s, s.Saved) != s.Running)
        {
            note = "after a restart";
            reason = "";
        }
        ConsoleStyle.PushDisabled(reason is { Length: > 0 });
        if (pending)
            ConsoleWidgets.BeginPendingRow(s.Label, s.LabelOf(s.Saved));
        else
            ConsoleWidgets.BeginRow(s.Label);
        if (!pending && note.Length > 0)
            Note(s.Label, note);
        var id = "MacPort_" + s.Key;
        switch (s.Kind)
        {
            case "bool":
            {
                bool on = value == "true";
                if (ConsoleWidgets.Checkbox(id, ref on, pending))
                    Pending[s.Key] = on ? "true" : "false";
                break;
            }
            case "scale":
            {
                int percent = (int)Math.Round(double.Parse(value, Inv) * 100);
                if (ConsoleWidgets.SliderInt(id, ref percent, (int)Math.Round(s.Min * 100), (int)Math.Round(s.Max * 100), $"{percent}%", "100%", pending))
                    Pending[s.Key] = (Math.Round(percent / 5.0) * 5 / 100).ToString("0.0#", Inv);
                break;
            }
            default:
                if (ConsoleWidgets.BeginComboControl(id, s.LabelOf(value), pending))
                {
                    foreach (var (choice, label) in s.Choices)
                    {
                        bool selected = choice == value;
                        if (ImGui.Selectable(label, selected, ImGuiSelectableFlags.None, (float2?)null))
                            Pending[s.Key] = choice;
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ConsoleWidgets.EndComboControl();
                }
                break;
        }
        if (ConsoleWidgets.RowHovered)
        {
            var tip = s.Help;
            if (reason is { Length: > 0 })
                tip += "\n\n" + reason;
            if (!s.Live && s.Section != "benchmark")
                tip += "\n\nTakes effect when the game starts.";
            ConsoleWidgets.Tooltip(Wrap(tip));
        }
        ConsoleWidgets.EndRow();
        ConsoleStyle.PopDisabled();
    }

    // A short note in front of the row's control, where a pending row shows what it was
    private static void Note(string label, string note)
    {
        var at = ImGui.GetCursorScreenPos();
        ConsoleStyle.PushLabelFont();
        float labelEnd = ConsoleWidgets.RowMin.X + ConsoleWidgets.RowLabelInsetPx + ImGui.CalcTextSize(label).X;
        ConsoleStyle.PopFont();
        ConsoleStyle.PushValueFont();
        var size = ImGui.CalcTextSize(note);
        var pos = new float2(at.X - ConsoleWidgets.GhostControlGapPx - size.X, at.Y + (ConsoleStyle.FrameHeightPx - size.Y) * 0.5f);
        if (pos.X > labelEnd + ConsoleWidgets.GhostControlGapPx)
            ImGui.GetWindowDrawList().AddText(in pos, ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), note);
        ConsoleStyle.PopFont();
    }

    // Tooltips are as wide as their longest line
    private static string Wrap(string text, int width = 72)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var line = "";
            foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line);
                    line = "";
                }
                line = line.Length == 0 ? word : line + " " + word;
            }
            lines.Add(line);
        }
        return string.Join('\n', lines);
    }

    // The benchmark section's button, and what the last run left
    private static void DrawBenchmark()
    {
        var temporary = _temporary.GetValue(null);
        bool changes = temporary != null && (bool)_hasAnyTabChanged.Invoke(null, [temporary])!;
        var why = changes ? "Apply or discard the changes first." : BenchmarkRun.CannotStart();
        ConsoleStyle.PushDisabled(why != null);
        ConsoleWidgets.BeginRow("");
        if (why != null)
            Note("", changes ? "apply first" : "not now");
        if (ConsoleWidgets.PrimaryButton("RUN BENCHMARK") && why == null)
            BenchmarkRun.StartFromTab(PortSettings.Get("benchmark_mode")?.Saved == "full", PortSettings.Get("benchmark_restart")?.Saved == "true");
        if (ConsoleWidgets.RowHovered)
            ConsoleWidgets.Tooltip(Wrap(why ?? "Plays the benchmark with the settings above, and writes its report to the benchmarks folder in the game's documents folder. Leave the mouse and keyboard alone until it is done; Esc stops it."));
        ConsoleWidgets.EndRow();
        ConsoleStyle.PopDisabled();
        if (BenchmarkRun.Last is { } last)
        {
            ConsoleWidgets.BeginRow("Last run");
            if (ConsoleWidgets.Button(last.Passed ? "SHOW REPORT" : "SHOW REPORT (PROBLEMS)"))
                BenchmarkRun.Show(last.Folder);
            if (ConsoleWidgets.RowHovered)
                ConsoleWidgets.Tooltip(Wrap(last.Summary));
            ConsoleWidgets.EndRow();
        }
    }
}
