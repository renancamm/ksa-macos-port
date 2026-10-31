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
using KSA.Rendering.Lighting;

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
internal static partial class MacPortTab
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
    // The tab draws nothing and leaves the game's settings alone (Hooks.Fail)
    private static bool _failed;

    public static string Apply(Harmony harmony)
    {
        var gs = typeof(GameSettings);
        _tabType = AccessTools.Inner(gs, "SettingsTab") ?? throw new Skip("GameSettings.SettingsTab not found");
        _openTab = Find.Field(gs, "_openTab");
        _temporary = Find.Field(gs, "_temporary");
        _order = Find.Field(gs, "_navRailOrder");
        _labels = Find.Field(gs, "_navRailLabels");
        _pendingFlags = Find.Field(gs, "_navRailPendingFlags");
        _hasAnyTabChanged = Find.Method(gs, "HasAnyTabChanged");
        // The rail with this tab first, so the window opens on it: the game's arrays are read-only fields, so the code
        // that reads them reads these
        var order = (Array)_order.GetValue(null)!;
        _railOrder = Array.CreateInstance(_tabType, order.Length + 1);
        _railOrder.SetValue(Enum.ToObject(_tabType, TabValue), 0);
        Array.Copy(order, 0, _railOrder, 1, order.Length);
        RailLabels = [Label, .. (string[])_labels.GetValue(null)!];
        RailPending = new bool[order.Length + 1];
        var rail = new HarmonyMethod(typeof(MacPortTab), nameof(RailTranspiler));
        foreach (var name in new[] { "RefreshNavRailPendingFlags", "HasAnyTabChanged", "DrawFooter" })
            harmony.Patch(Find.Method(gs, name), transpiler: rail);
        var railWidth = AccessTools.Method(typeof(ConsoleWidgets), nameof(ConsoleWidgets.NavRailWidth));
        _navRail = railWidth == null ? null : AccessTools.Method(typeof(ConsoleWidgets), nameof(ConsoleWidgets.NavRail),
            [typeof(ReadOnlySpan<char>), typeof(ReadOnlySpan<string>), typeof(int).MakeByRefType(), typeof(ReadOnlySpan<bool>), typeof(float), typeof(float).MakeByRefType()]);
        if (_navRail != null)
            harmony.Patch(railWidth, prefix: new HarmonyMethod(typeof(MacPortTab), nameof(RailWidthPrefix)));
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

    // OnDrawUi: the rail's arrays, the rail in its two groups (without the call, one group: the tab still works), and
    // the tab's rows once the content's body has begun (the game's own tabs draw nothing while this one is open)
    private static IEnumerable<CodeInstruction> DrawUiTranspiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var code = RailTranspiler(instructions, original).ToList();
        var rails = _navRail == null ? [] : code.Where(i => i.Calls(_navRail)).ToList();
        foreach (var rail in rails)
            rail.operand = AccessTools.Method(typeof(MacPortTab), nameof(Rail));
        if (rails.Count == 0)
            Hooks.Log("Mac Port tab: the settings' rail moved, so the tab stays in one group with the game's");
        int name = Find.Once(code, i => i.Is(OpCodes.Ldstr, "SettingsContent"), "the settings' content body");
        var beginBody = AccessTools.GetDeclaredMethods(typeof(ConsoleStyle)).Where(m => m.Name == nameof(ConsoleStyle.BeginBody)).ToList();
        int call = Find.Next(code, i => beginBody.Any(i.Calls), "the content body's BeginBody", name);
        code.Insert(call + 1, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MacPortTab), nameof(Draw))));
        return code;
    }

    // The rail's width while Rail draws it in groups, each of which would otherwise be as wide as its own labels
    private static float _railWidth;
    private static MethodInfo? _navRail;

    private static bool RailWidthPrefix(ref float __result)
    {
        if (_railWidth <= 0f)
            return true;
        __result = _railWidth;
        return false;
    }

    // ConsoleWidgets.NavRail, with this tab in a group of its own above the game's
    private static bool Rail(ReadOnlySpan<char> id, ReadOnlySpan<string> items, ref int active, ReadOnlySpan<bool> pending, float dividerHeight, out float height)
    {
        float s = ConsoleStyle.EffectiveInterfaceScale, gap = 6f * s;
        float width = ConsoleWidgets.NavRailWidth(items);
        float item = ConsoleWidgets.NavRailItemHeight();
        var at = ImGui.GetCursorScreenPos();
        float rule = at.Y + item + gap;
        float rest = rule + gap;
        _railWidth = width;
        int first = active == 0 ? 0 : -1, others = active - 1;
        bool clicked = ConsoleWidgets.NavRail(id, items[..1], ref first, pending[..1], rest - at.Y, out _);
        ImGui.GetWindowDrawList().AddLine(new float2(at.X, rule), new float2(at.X + width - ConsoleWidgets.NavRailTextInsetPx, rule),
            ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
        ImGui.SetCursorScreenPos(new float2(at.X, rest));
        bool clickedOther = ConsoleWidgets.NavRail("MacPortRail", items[1..], ref others, pending[1..], Math.Max(0f, at.Y + dividerHeight - rest), out _);
        _railWidth = 0f;
        active = clicked ? 0 : clickedOther ? others + 1 : active;
        height = rest - at.Y + item * (items.Length - 1);
        return clicked || clickedOther;
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
        if (_failed || Convert.ToInt32(__args[0], Inv) != TabValue)
            return;
        try
        {
            Sync();
            __result = Shown.Any(s => Pending[s.Key] != s.Saved);
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void RestartPostfix(object __instance, ref bool __result)
    {
        if (_failed)
            return;
        try
        {
            Sync();
            if (ReferenceEquals(__instance, _bound) && Shown.Any(NeedsRestart))
                __result = true;
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void Failed(Exception e)
    {
        _failed = true;
        Hooks.Fail("mac_port_tab", e);
    }

    // Apply, or the restart popup's Restart and Continue, saved the game's pending copy: the tab's values go to the file
    private static void SavePostfix(object __instance)
    {
        if (_failed || !ReferenceEquals(__instance, _bound))
            return;
        try
        {
            Save();
        }
        catch (Exception e)
        {
            Failed(e);
        }
    }

    private static void Save()
    {
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
        Hooks.Log($"Mac Port tab: saved {string.Join(", ", changed.Select(kv => $"{kv.Key} = {kv.Value}"))}");
    }

    // The hooks read a live setting's Running value (Settings) while the game runs
    private static void ApplyLive(PortSetting s, string value)
    {
        if (s.Outside == null)
            s.Running = value;
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
            "pcss" => game?.Graphics is not { } g || g.CascadedShadowMapFilter == CascadedShadowSystem.Filter.Pcss,
            _ => true
        };
    }

    // What a setting would run with: its value once its needs are met, its default otherwise
    private static string Effective(PortSetting s, string value) => PortSettings.Met(s, Value, Game) ? value : s.Default;

    // A switch still waiting for another setting does nothing yet, whichever way it is set
    private static bool NeedsRestart(PortSetting s) =>
        !s.Live && s.Outside == null && Pending[s.Key] != s.Saved && PortSettings.Met(s, Value, Game) && Pending[s.Key] != s.Running;

    // Why a setting can't change now (with a note beside it), or what it waits for (the tooltip says), or null
    private static string? Reason(PortSetting s, out string note)
    {
        note = "";
        if (s.Outside != null)
        {
            note = "set outside";
            return $"{s.Outside} is set where the game was started, and it decides this setting instead of the file.";
        }
        if (Hooks.Status.GetValueOrDefault(s.Hook) is { Unavailable: true } status)
        {
            note = "unavailable";
            return status.State == HookState.Skipped
                ? "Unavailable with this KSA version: the game's code it changes has moved, so it is left out (logs/console.log says more)."
                : "Unavailable: it ran into an error, so it is left out (logs/console.log says why).";
        }
        foreach (var need in s.Needs)
        {
            var one = new PortSetting { Needs = [need] };
            if (PortSettings.Met(one, Value, Game))
                continue;
            if (need == "game:vsync")
                return "Needs the game's Vsync (Display).";
            if (need == "game:plume_trail_shadows")
                return "Needs the game's Plume trails and Plume trail shadows (Graphics).";
            if (need == "game:pcss")
                return "Needs the game's Cascaded Shadow Filter on Pcss (Graphics).";
            int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
            var other = PortSettings.Get(ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need);
            var what = other == null ? need : ne < 0 && eq > 0 ? $"{other.Label} {other.LabelOf(need[(eq + 1)..])}" : other.Label;
            return $"Needs {what}.";
        }
        return null;
    }

    // The rows, while the tab is open (called inside the settings' content body). Not guarded as the postfixes are: an
    // exception between a row's begin and end would leave ImGui's stacks unbalanced for the rest of the frame.
    private static void Draw()
    {
        if (_failed || Convert.ToInt32(_openTab.GetValue(null), Inv) != TabValue)
            return;
        Sync();
        DrawTitle();
        DrawPresets();
        foreach (var (section, title, caption) in PortSettings.Sections)
        {
            if (section == "developer")
                continue;
            SectionHeader(title, caption);
            foreach (var s in PortSettings.All.Where(s => s.Section == section))
            {
                DrawRow(s);
                if (s.Key == "upscaling")
                    DrawRenderBoxes();
            }
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
        // A switch waiting for another setting can still be set, and its name says what it is about. Greyed out: what
        // can't change (it has a note), and any other control that does nothing yet
        ConsoleStyle.PushDisabled(reason is { Length: > 0 } && (note.Length > 0 || s.Kind != "bool"));
        if (pending)
            ConsoleWidgets.BeginPendingRow(s.Label, s.Kind == "choice" ? "" : s.LabelOf(s.Saved));
        else
            ConsoleWidgets.BeginRow(s.Label);
        // A segmented control is wider than the row's control column, so it says what the row was itself
        if (s.Kind == "choice")
        {
            var column = ImGui.GetCursorScreenPos();
            float labelEnd = ConsoleWidgets.RowMin.X + ConsoleWidgets.RowLabelInsetPx + LabelWidth(s.Label);
            // Past the row's right edge rather than over its label, with a very large interface scale
            ImGui.SetCursorScreenPos(new float2(MathF.Max(column.X + ConsoleWidgets.RowControlWidth - SegmentedWidth(s), labelEnd + ConsoleWidgets.GhostControlGapPx), column.Y));
            if (pending)
                Note(s.Label, "was " + s.LabelOf(s.Saved));
        }
        if (!pending && note.Length > 0)
            Note(s.Label, note);
        string? optionHelp = null;
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
                optionHelp = DrawSegmented(s, id, value, pending);
                break;
        }
        bool hovered = ConsoleWidgets.RowHovered;
        ConsoleWidgets.EndRow();
        ConsoleStyle.PopDisabled();
        if (hovered)
        {
            var tip = optionHelp ?? s.Help;
            if (optionHelp == null && s.OptionHelp.TryGetValue(value, out var meaning))
                tip += $"\n\n{s.LabelOf(value)}: {meaning}";
            if (reason is { Length: > 0 })
                tip += "\n\n" + reason;
            if (!s.Live)
                tip += "\n\nTakes effect when the game starts.";
            Tooltip(tip);
        }
    }

    // The presets: one card each, with what it sets, making its values the pending changes here and in the game's tabs.
    // The one in use has a soft fill, and the one the pending changes make is gold, as the game's Apply is then
    private static void DrawPresets()
    {
        SectionHeader("Presets", "Sets the game's graphics and these settings together.", first: true);
        var unavailable = Hooks.Status.GetValueOrDefault("presets") is { Unavailable: true } status
            ? status.State == HookState.Skipped
                ? "Unavailable with this KSA version: the game's settings it sets have moved (logs/console.log says more)."
                : "Unavailable: it ran into an error (logs/console.log says why)."
            : null;
        // Without the presets' values there is nothing for the cards to show
        if (unavailable != null || Presets.All.Any(p => p.Port.Count == 0))
        {
            Caption(unavailable ?? "Unavailable.", ConsoleWidgets.RowLabelInsetPx);
            return;
        }
        var pending = _temporary.GetValue(null) as GameSettings;
        var mac = Presets.ThisMac;
        var forMac = (mac.SmallMemory ? $"For this Mac's {mac.MemoryGb} GB of memory, the shadow maps and textures are at most 2048 pixels wide. " : "") +
            (mac.RayTracing ? "" : "The M1 and M2 have no ray tracing hardware, so the cockpit is drawn without it. ");
        var inUse = Presets.All.FirstOrDefault(p => Presets.Matches(p, GameSettings.Current, key => PortSettings.Get(key)!.Saved));
        var shown = pending == null ? inUse : Presets.All.FirstOrDefault(p => Presets.Matches(p, pending, key => Value(PortSettings.Get(key)!)));
        ConsoleStyle.PushDisabled(pending == null);
        var hovered = DrawCards(inUse, shown, pending);
        ConsoleStyle.PopDisabled();
        if (hovered != null)
            Tooltip(hovered.Help + "\n\n" + (hovered == Presets.GameDefaults ? "" : forMac) +
                "This makes them pending changes, here and in the game's tabs; Apply keeps them.");
        if (shown == null && pending != null)
        {
            string Port(string key) => Value(PortSettings.Get(key)!);
            var closest = Presets.All.MinBy(p => Presets.Differences(p, pending, Port))!;
            int count = Presets.Differences(closest, pending, Port);
            Caption($"Own settings: {count} setting{(count == 1 ? " differs" : "s differ")} from {closest.Name}.", ConsoleWidgets.RowLabelInsetPx);
        }
    }
}
