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
            {
                note = "needs Vsync";
                return "Needs the game's Vsync (Display).";
            }
            if (need == "game:plume_trail_shadows")
            {
                note = "needs Plume trail shadows";
                return "Needs the game's Plume trails and Plume trail shadows (Graphics).";
            }
            if (need == "game:pcss")
            {
                note = "needs Pcss";
                return "Needs the game's Cascaded Shadow Filter on Pcss (Graphics).";
            }
            int ne = need.IndexOf("!=", StringComparison.Ordinal), eq = need.IndexOf('=');
            var other = PortSettings.Get(ne > 0 ? need[..ne] : eq > 0 ? need[..eq] : need);
            var what = other == null ? need : ne < 0 && eq > 0 ? $"{other.Label}: {other.LabelOf(need[(eq + 1)..])}" : other.Label;
            note = $"needs {what}";
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
        DrawPresets();
        foreach (var (section, title, caption) in PortSettings.Sections)
        {
            if (section == "developer")
                continue;
            SectionHeader(title, caption);
            foreach (var s in PortSettings.All.Where(s => s.Section == section))
            {
                DrawRow(s);
                if (s.Key == "render_scale")
                    DrawRenderSize();
            }
            if (section == "measure")
                DrawBenchmark();
        }
    }

    // A section's title and caption, with more room around them than the game's own tabs have, whose titles have no
    // caption: a gap above the title (the first section's sits at the top) and one under the caption
    private static void SectionHeader(string title, string caption)
    {
        float s = ConsoleStyle.EffectiveInterfaceScale;
        if (title != "Presets")
            ImGui.Dummy(new float2(0f, 12f * s));
        ConsoleWidgets.RegionHeader(title);
        Caption(caption);
        ImGui.Dummy(new float2(0f, 4f * s));
    }

    // The muted line under a section's title, in the game's small font (TextWrapped takes a printf format too)
    private static void Caption(string text)
    {
        if (text.Length == 0)
            return;
        ConsoleStyle.PushSignatureFont();
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.TextWrapped(text.Replace("%", "%%"));
        ImGui.PopStyleColor();
        ConsoleStyle.PopFont();
    }

    // Under Render Scale, in a caption's font: the 3D view's size in pixels in this window with the upscaling and
    // render scale the rows show, pending ones too (after a restart for those), and the window's pixels it is shown on.
    // The arithmetic is RenderScale's: the render scale only with an upscaler, in single precision
    private static void DrawRenderSize()
    {
        if (RenderScale.Window.X <= 0 || RenderScale.Window.Y <= 0 || RenderScale.Output.X <= 0 || RenderScale.Output.Y <= 0)
            return;
        string Shown(string key) => PortSettings.Get(key) is { } s ? s.Outside != null ? s.Running : Value(s) : "";
        float scale = Shown("upscaling") is "metalfx" or "spatial" &&
            double.TryParse(Shown("render_scale"), NumberStyles.Float, Inv, out var shown) ? (float)shown : 1f;
        int width = Math.Clamp((int)MathF.Round(RenderScale.Window.X * scale), 1, RenderScale.Output.X);
        int height = Math.Clamp((int)MathF.Round(RenderScale.Window.Y * scale), 1, RenderScale.Output.Y);
        Caption($"The 3D view: {width} x {height} pixels, shown on {RenderScale.Output.X} x {RenderScale.Output.Y}.");
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
                        if (s.OptionHelp.TryGetValue(choice, out var meaning) && ImGui.IsItemHovered())
                            Tooltip(meaning);
                        if (selected)
                            ImGui.SetItemDefaultFocus();
                    }
                    ConsoleWidgets.EndComboControl();
                }
                break;
        }
        bool hovered = ConsoleWidgets.RowHovered;
        ConsoleWidgets.EndRow();
        ConsoleStyle.PopDisabled();
        if (hovered)
        {
            var tip = s.Help;
            if (s.OptionHelp.TryGetValue(value, out var meaning))
                tip += $"\n\n{s.LabelOf(value)}: {meaning}";
            if (reason is { Length: > 0 })
                tip += "\n\n" + reason;
            if (!s.Live)
                tip += "\n\nTakes effect when the game starts.";
            Tooltip(tip);
        }
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

    // A row's tooltip, drawn after its row ends (a greyed-out row would fade it). The game's Tooltip is a printf format
    // to ImGui: a % is doubled, or "10-20 % faster" prints a number
    private static void Tooltip(string text) => ConsoleWidgets.Tooltip(Wrap(text).Replace("%", "%%"));

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

    // The presets: one button each on a row, making its values the pending changes here and in the game's tabs, and a
    // line under them with the preset in use. The one the pending changes make is drawn as the game's Apply is then
    private static void DrawPresets()
    {
        SectionHeader("Presets", "The game's graphics and these settings, chosen together for this Mac.");
        var unavailable = Hooks.Status.GetValueOrDefault("presets") is { Unavailable: true } status
            ? status.State == HookState.Skipped
                ? "Unavailable with this KSA version: the game's settings it sets have moved (logs/console.log says more)."
                : "Unavailable: it ran into an error (logs/console.log says why)."
            : null;
        var pending = _temporary.GetValue(null) as GameSettings;
        var mac = Presets.ThisMac;
        var forMac = (mac.SmallMemory ? $"For this Mac's {mac.MemoryGb} GB of memory, the shadow maps and textures are at most 2048 pixels wide. " : "") +
            (mac.RayTracing ? "" : "The M1 and M2 have no ray tracing hardware, so the cockpit is drawn without it. ");
        Presets.Preset? inUse = null, shown = null;
        if (unavailable == null)
        {
            inUse = Presets.All.FirstOrDefault(p => Presets.Matches(p, GameSettings.Current, key => PortSettings.Get(key)!.Saved));
            shown = pending == null ? inUse : Presets.All.FirstOrDefault(p => Presets.Matches(p, pending, key => Value(PortSettings.Get(key)!)));
        }
        float gap = ImGui.GetStyle().ItemSpacing.X;
        var size = new float2((ConsoleStyle.ContentAvailWidth() - gap * (Presets.All.Length - 1)) / Presets.All.Length, ConsoleWidgets.ButtonHeight);
        Presets.Preset? hovered = null;
        ConsoleStyle.PushDisabled(unavailable != null || pending == null);
        foreach (var preset in Presets.All)
        {
            if (preset != Presets.All[0])
                ImGui.SameLine();
            var label = preset.Name.ToUpperInvariant();
            var id = "MacPort_preset_" + preset.Name;
            bool clicked = shown != null && shown == preset && shown != inUse
                ? ConsoleWidgets.PrimaryButton(label, id, size)
                : ConsoleWidgets.Button(label, id, size);
            if (ImGui.IsItemHovered())
                hovered = preset;
            if (clicked && unavailable == null && pending != null)
            {
                Presets.Use(preset, pending, Pending);
                Hooks.Log($"Mac Port tab: {preset.Name} preset pending");
            }
        }
        ConsoleStyle.PopDisabled();
        if (hovered != null)
            Tooltip(unavailable ?? hovered.Help + "\n\n" + (hovered == Presets.GameDefaults ? "" : forMac) +
                "This makes them pending changes, here and in the game's tabs; Apply keeps them.");
        Caption(unavailable != null ? "Unavailable." :
            shown != null && shown != inUse ? $"Pending: {shown.Name}." + (inUse != null ? $" In use: {inUse.Name}." : "") :
            inUse == null ? "In use: none, the settings are your own." :
            shown == null ? $"In use: {inUse.Name}, with pending changes." :
            $"In use: {inUse.Name}.");
    }

    // The Measure section's benchmark button, and what the last run left
    private static void DrawBenchmark()
    {
        var temporary = _temporary.GetValue(null);
        bool changes = temporary != null && (bool)_hasAnyTabChanged.Invoke(null, [temporary])!;
        string note = "apply first";
        var why = changes ? "Apply or discard the changes first." : BenchmarkRun.CannotStart(out note);
        ConsoleStyle.PushDisabled(why != null);
        ConsoleWidgets.BeginRow("");
        if (why != null)
            Note("", note);
        if (ConsoleWidgets.PrimaryButton("RUN BENCHMARK") && why == null)
            BenchmarkRun.StartFromTab(PortSettings.Get("benchmark_mode")?.Saved == "full", PortSettings.Get("benchmark_restart")?.Saved == "true");
        bool hovered = ConsoleWidgets.RowHovered;
        ConsoleWidgets.EndRow();
        ConsoleStyle.PopDisabled();
        if (hovered)
            Tooltip(why ?? "Plays the benchmark with the settings above, and writes its report to the benchmarks folder in the game's documents folder. Leave the mouse and keyboard alone until it is done; Esc stops it.");
        if (BenchmarkRun.Last is { } last)
        {
            ConsoleWidgets.BeginRow("Last run");
            if (ConsoleWidgets.Button(last.Passed ? "SHOW REPORT" : "SHOW REPORT (PROBLEMS)"))
                BenchmarkRun.Show(last.Folder);
            bool lastHovered = ConsoleWidgets.RowHovered;
            ConsoleWidgets.EndRow();
            if (lastHovered)
                Tooltip(last.Summary);
        }
    }
}
