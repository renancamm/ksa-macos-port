using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
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

    // Moves down by this much: an item's spacing is part of it
    private static void Gap(float height) => ImGui.Dummy(new float2(0f, Math.Max(0f, height - ImGui.GetStyle().ItemSpacing.Y)));

    // The wordmark, plain ASCII that needs a font whose characters are all one width (the player can choose another: the
    // words alone then), with the port's version and what the tab is for on the right, as far as the width allows
    private const string Description = "Mac-only settings that improve\ncompatibility and performance.";
    private static readonly string[] Wordmark =
    [
        @" __  __   _   ___   ___  ___  ___ _____",
        @"|  \/  | /_\ / __| | _ \/ _ \| _ \_   _|",
        @"| |\/| |/ _ \ (__  |  _/ (_) |   / | |",
        @"|_|  |_/_/ \_\___| |_|  \___/|_|_\ |_|",
    ];

    private static string? _version;

    // "PORT v5707.1", or "PORT dev 1a2b3c4" for a development build: from the VERSION file the build puts in the app
    private static string PortVersion()
    {
        if (_version != null)
            return _version;
        _version = "";
        try
        {
            var lines = System.IO.File.ReadAllLines(System.IO.Path.Combine(Hooks.Resources, "VERSION"));
            string Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + "=", StringComparison.Ordinal))?[(name.Length + 1)..] ?? "";
            string version = Field("version"), commit = Field("commit");
            _version = version == "dev" ? $"PORT dev {commit}" : version.Length > 0 ? $"PORT v{version}" : "";
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Hooks.Log($"Mac Port tab: no version to show: {e.Message}");
        }
        return _version;
    }

    private static void DrawTitle()
    {
        float s = ConsoleStyle.EffectiveInterfaceScale, inset = ConsoleWidgets.RowLabelInsetPx;
        var at = ImGui.GetCursorScreenPos();
        float right = at.X + ConsoleStyle.ContentAvailWidth();
        var draw = ImGui.GetWindowDrawList();
        ConsoleStyle.PushLabelFont(13f / 15f);
        float line = MathF.Round(ImGui.GetFontSize() * 0.87f);
        bool mono = ImGui.CalcTextSize("i").X == ImGui.CalcTextSize("M").X;
        float height = 6f * s + line * Wordmark.Length;
        float left = at.X + inset;
        if (mono)
        {
            for (int i = 0; i < Wordmark.Length; i++)
                draw.AddText(new float2(at.X + inset, at.Y + 6f * s + line * i), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), Wordmark[i]);
            left += ImGui.CalcTextSize(Wordmark[1]).X;
        }
        ConsoleStyle.PopFont();
        if (!mono)
        {
            ConsoleStyle.PushNavRailFont();
            draw.AddText(new float2(at.X + inset, at.Y + (height - ImGui.GetFontSize()) * 0.5f), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), Label);
            left += ImGui.CalcTextSize(Label).X;
            ConsoleStyle.PopFont();
        }
        ConsoleStyle.PushSignatureFont();
        float small = ImGui.GetFontSize();
        var version = PortVersion();
        float versionWidth = ImGui.CalcTextSize(version).X;
        if (right - versionWidth > left + 12f * s)
            draw.AddText(new float2(right - versionWidth, at.Y), ConsoleStyle.ApplyAlpha(ConsoleStyle.TextLabelU32), version);
        var description = Description.Split('\n');
        float rowHeight = MathF.Round(small);
        if (right - description.Max(d => ImGui.CalcTextSize(d).X) > left + 20f * s)
            for (int i = 0; i < description.Length; i++)
                draw.AddText(new float2(right - ImGui.CalcTextSize(description[i]).X, at.Y + height - rowHeight * (description.Length - i)),
                    ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), description[i]);
        ConsoleStyle.PopFont();
        Gap(height + 22f * s);
    }

    // A section's rule, title and caption on the rows' label edge, with a wide gap above (the first one follows the
    // tab's title) and a smaller one under
    private static void SectionHeader(string title, string caption, bool first = false)
    {
        float s = ConsoleStyle.EffectiveInterfaceScale, inset = ConsoleWidgets.RowLabelInsetPx;
        if (!first)
            Gap(36f * s);
        var at = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddLine(new float2(at.X + inset, at.Y), new float2(at.X + ConsoleStyle.ContentAvailWidth(), at.Y),
            ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
        ImGui.SetCursorScreenPos(new float2(at.X + inset, at.Y + 12f * s));
        ConsoleStyle.PushLabelFont();
        ImGui.TextColored(in ConsoleStyle.TextPrimary, title.ToUpperInvariant().Replace("%", "%%"));
        ConsoleStyle.PopFont();
        Caption(caption, inset);
        Gap(10f * s);
    }

    // The muted line under a section's title, in the game's small font (TextWrapped takes a printf format too)
    private static void Caption(string text, float inset = 0f)
    {
        if (text.Length == 0)
            return;
        if (inset > 0f)
            ImGui.SetCursorScreenPos(ImGui.GetCursorScreenPos() + new float2(inset, 0f));
        ConsoleStyle.PushSignatureFont();
        ImGui.PushStyleColor(ImGuiCol.Text, in ConsoleStyle.TextMuted);
        ImGui.TextWrapped(text.Replace("%", "%%"));
        ImGui.PopStyleColor();
        ConsoleStyle.PopFont();
    }

    // How far MetalFX Temporal scales on this Mac, past which Spatial does the rest (0: not known)
    [DllImport("libvulkan.1.dylib")]
    private static extern float vkGetUpscaleMaxScaleKSA();

    private static float? _temporalMax;

    private static float TemporalMax()
    {
        if (_temporalMax == null)
        {
            try
            {
                _temporalMax = vkGetUpscaleMaxScaleKSA();
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                _temporalMax = 0f;
            }
        }
        return _temporalMax.Value;
    }

    // The 3D view's size in pixels with these upscaling and render scale values. The arithmetic is RenderScale's: the
    // render scale only with an upscaler, in single precision
    private static (int Width, int Height) ViewSize(string upscaling, string renderScale)
    {
        float scale = upscaling is "metalfx" or "spatial" && double.TryParse(renderScale, NumberStyles.Float, Inv, out var shown) ? (float)shown : 1f;
        return (Math.Clamp((int)MathF.Round(RenderScale.Window.X * scale), 1, RenderScale.Output.X),
            Math.Clamp((int)MathF.Round(RenderScale.Window.Y * scale), 1, RenderScale.Output.Y));
    }

    // Text cut into lines that fit a width, in the current font
    private static List<string> WrapTo(string text, float width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var longer = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && ImGui.CalcTextSize(longer).X > width)
            {
                lines.Add(line);
                line = word;
            }
            else
                line = longer;
        }
        lines.Add(line);
        return lines;
    }

    private static void DashedLine(ImDrawListPtr draw, float2 from, float2 to, uint color)
    {
        float s = ConsoleStyle.EffectiveInterfaceScale, dash = 4f * s, step = 7f * s;
        float length = MathF.Abs(to.X - from.X) + MathF.Abs(to.Y - from.Y);
        for (float at = 0f; at < length; at += step)
        {
            float a = at / length, b = MathF.Min(at + dash, length) / length;
            draw.AddLine(from + (to - from) * a, from + (to - from) * b, color, ConsoleStyle.WindowBorderThicknessPx);
        }
    }

    // Under the MetalFX row: what is drawn and what it is shown on, as two boxes to one scale, with the upscaling and
    // render scale the rows show, pending ones too. A size that needs a restart is filled gold, with an outline of the
    // one the game draws now
    private static void DrawRenderBoxes()
    {
        if (RenderScale.Window.X <= 0 || RenderScale.Window.Y <= 0 || RenderScale.Output.X <= 0 || RenderScale.Output.Y <= 0)
            return;
        string Shown(string key) => PortSettings.Get(key) is { } p ? p.Outside != null ? p.Running : Value(p) : "";
        string Running(string key) => PortSettings.Get(key)?.Running ?? "";
        var mode = Shown("upscaling");
        var now = ViewSize(mode, Shown("render_scale"));
        var then = ViewSize(Running("upscaling"), Running("render_scale"));
        bool changed = now != then;
        float s = ConsoleStyle.EffectiveInterfaceScale, inset = ConsoleWidgets.RowLabelInsetPx;
        float padX = 16f * s, padTop = 14f * s, padBottom = 12f * s, gap = 14f * s;
        var at = ImGui.GetCursorScreenPos() + new float2(inset, 4f * s);
        float width = ConsoleStyle.ContentAvailWidth() - inset;
        float boxWidth = MathF.Min(196f * s, (width - padX * 2f - gap * 2f) * 0.3f);
        float k = boxWidth / RenderScale.Output.X;
        float boxHeight = RenderScale.Output.Y * k;
        ConsoleStyle.PushSignatureFont();
        float small = ImGui.GetFontSize();
        ConsoleStyle.PopFont();
        float captionLine = MathF.Round(small);
        float height = padTop + boxHeight + 8f * s + captionLine * 2f + padBottom;
        var draw = ImGui.GetWindowDrawList();
        var panelEnd = at + new float2(width, height);
        draw.AddRectFilled(in at, in panelEnd, ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32), ConsoleStyle.FrameRoundingPx);
        draw.AddRect(in at, in panelEnd, ConsoleStyle.ApplyAlpha(ConsoleStyle.FrameU32), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
        uint fill = ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(ConsoleStyle.ActiveToolU32, 0.45f));
        uint muted = ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), frame = ConsoleStyle.ApplyAlpha(ConsoleStyle.FrameU32);
        // What is drawn, from the display's corner
        var box = at + new float2(padX, padTop);
        var boxEnd = box + new float2(boxWidth, boxHeight);
        DashedLine(draw, box, new float2(boxEnd.X, box.Y), frame);
        DashedLine(draw, new float2(boxEnd.X, box.Y), boxEnd, frame);
        DashedLine(draw, boxEnd, new float2(box.X, boxEnd.Y), frame);
        DashedLine(draw, new float2(box.X, boxEnd.Y), box, frame);
        draw.AddRectFilled(new float2(box.X, boxEnd.Y - now.Height * k), new float2(box.X + now.Width * k, boxEnd.Y),
            changed ? ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(ConsoleStyle.PendingU32, 0.55f)) : fill);
        if (changed)
            draw.AddRect(new float2(box.X, boxEnd.Y - then.Height * k), new float2(box.X + then.Width * k, boxEnd.Y), muted, 0f, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
        // The window's size in points: 100 % of Render Scale
        float full = box.X + MathF.Min(RenderScale.Window.X, RenderScale.Output.X) * k;
        float fullTop = boxEnd.Y - MathF.Min(RenderScale.Window.Y, RenderScale.Output.Y) * k;
        DashedLine(draw, new float2(box.X, fullTop), new float2(full, fullTop), muted);
        DashedLine(draw, new float2(full, fullTop), new float2(full, boxEnd.Y), muted);
        ConsoleStyle.PushLabelFont(10f / 15f);
        var hundred = ImGui.CalcTextSize("100 %");
        var hundredAt = new float2(full - hundred.X - 3f * s, fullTop + 2f * s);
        // On the panel's colour, as the drawn box, grey or gold, can lie behind it
        draw.AddRectFilled(hundredAt - new float2(2f * s, 0f), hundredAt + hundred + new float2(2f * s, 0f), ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32));
        draw.AddText(in hundredAt, muted, "100 %");
        ConsoleStyle.PopFont();
        // What it is shown on: the drawn box's grey, fainter, as only its size matters
        var shown = new float2(at.X + width - padX - boxWidth, box.Y);
        var shownEnd = shown + new float2(boxWidth, boxHeight);
        draw.AddRectFilled(in shown, in shownEnd, ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(ConsoleStyle.ActiveToolU32, 0.18f)));
        draw.AddRect(in shown, in shownEnd, fill, 0f, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
        // The boxes' captions, and how one becomes the other between them
        float text = boxEnd.Y + 8f * s;
        uint primary = ConsoleStyle.ApplyAlpha(ConsoleStyle.TextPrimaryU32), label = ConsoleStyle.ApplyAlpha(ConsoleStyle.TextLabelU32);
        float x = (float)RenderScale.Output.X / now.Width;
        float mostTemporal = TemporalMax();
        var note = $"{Math.Round(100.0 * now.Width * now.Height / (RenderScale.Output.X * RenderScale.Output.Y))} % of the window's pixels." +
            (mode == "metalfx" && mostTemporal > 0f && x > mostTemporal + 0.001f ? $" Past Temporal's largest scale (x{mostTemporal.ToString("0.#", Inv)}), Spatial does the rest." : "");
        float midLeft = boxEnd.X + gap, midRight = shown.X - gap, mid = (midLeft + midRight) * 0.5f;
        ConsoleStyle.PushSignatureFont();
        draw.AddText(new float2(box.X, text), muted, "DRAWN");
        draw.AddText(new float2(box.X, text + captionLine), primary, $"{now.Width} x {now.Height}");
        draw.AddText(new float2(shown.X, text), muted, "SHOWN ON");
        draw.AddText(new float2(shown.X, text + captionLine), primary, $"{RenderScale.Output.X} x {RenderScale.Output.Y}");
        var how = mode switch
        {
            "metalfx" => "METALFX TEMPORAL",
            "spatial" => "METALFX SPATIAL",
            _ => now.Width == RenderScale.Output.X && now.Height == RenderScale.Output.Y ? "SHOWN AS IS" : "THE GAME STRETCHES IT",
        };
        var noteLines = WrapTo(note, midRight - midLeft);
        ConsoleStyle.PopFont();
        ConsoleStyle.PushNavRailFont();
        float large = ImGui.GetFontSize();
        var factor = $"x{x.ToString("0.#", Inv)}";
        float factorWidth = ImGui.CalcTextSize(factor).X;
        ConsoleStyle.PopFont();
        float space = 6f * s, arrowHeight = 9f * s;
        float content = small + space + arrowHeight + space + large + space + captionLine * noteLines.Count;
        float y = box.Y + MathF.Max(0f, (boxHeight - content) * 0.5f);
        ConsoleStyle.PushSignatureFont();
        draw.AddText(new float2(mid - ImGui.CalcTextSize(how).X * 0.5f, y), label, how);
        ConsoleStyle.PopFont();
        y += small + space;
        float arrow = y + 4f * s;
        draw.AddLine(new float2(midLeft, arrow), new float2(midRight - 6f * s, arrow), muted, ConsoleStyle.WindowBorderThicknessPx);
        draw.AddTriangleFilled(new float2(midRight, arrow), new float2(midRight - 8f * s, arrow - 4f * s), new float2(midRight - 8f * s, arrow + 4f * s), muted);
        y += arrowHeight + space;
        ConsoleStyle.PushNavRailFont();
        draw.AddText(new float2(mid - factorWidth * 0.5f, y), primary, factor);
        ConsoleStyle.PopFont();
        y += large + space;
        ConsoleStyle.PushSignatureFont();
        foreach (var line in noteLines)
        {
            draw.AddText(new float2(mid - ImGui.CalcTextSize(line).X * 0.5f, y), muted, line);
            y += captionLine;
        }
        ConsoleStyle.PopFont();
        bool hovered = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(in at, in panelEnd);
        Gap(height + 10f * s);
        if (hovered)
            Tooltip("Both boxes are drawn to one scale. Render Scale is a share of the window's size: on a Retina display, 100 % is half the window's pixels on each side, and 200 % matches them. On any other display, 100 % already matches them, and more changes nothing.");
    }

    // A row's segmented control: as wide as its labels need, right-aligned, from the left edge SegmentedWidth leaves.
    // The game's Segmented always fills the row and has no pending state
    private static float SegmentedWidth(PortSetting s)
    {
        ConsoleStyle.PushLabelFont();
        float widest = s.Choices.Max(c => ImGui.CalcTextSize(c.Label).X);
        ConsoleStyle.PopFont();
        return (widest + 20f * ConsoleStyle.EffectiveInterfaceScale) * s.Choices.Length;
    }

    // Returns the help of the option under the mouse
    private static string? DrawSegmented(PortSetting s, string id, string value, bool pending)
    {
        float total = SegmentedWidth(s), each = total / s.Choices.Length, height = ConsoleStyle.FrameHeightPx;
        var at = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var end = at + new float2(total, height);
        bool enabled = ImGui.GetStyle().Alpha >= 1f;
        string? hovered = null;
        draw.AddRectFilled(in at, in end, ConsoleStyle.ApplyAlpha(ConsoleStyle.InsetU32), ConsoleStyle.FrameRoundingPx);
        for (int i = 0; i < s.Choices.Length; i++)
        {
            var (choice, label) = s.Choices[i];
            var pos = at + new float2(each * i, 0f);
            var size = new float2(each, height);
            ImGui.SetCursorScreenPos(in pos);
            bool clicked = false;
            if (enabled)
                clicked = ImGui.InvisibleButton($"##{id}{i}", in size);
            else
                ImGui.Dummy(in size);
            bool over = enabled && ImGui.IsItemHovered();
            if (over)
                hovered = s.OptionHelp.GetValueOrDefault(choice);
            if (clicked)
                Pending[s.Key] = choice;
            bool active = choice == value;
            var corners = i == 0 ? ImDrawFlags.RoundCornersLeft : i == s.Choices.Length - 1 ? ImDrawFlags.RoundCornersRight : ImDrawFlags.RoundCornersNone;
            if (active)
                draw.AddRectFilled(in pos, pos + size, ConsoleStyle.ApplyAlpha(pending ? ConsoleStyle.PendingU32 : ConsoleStyle.ActiveToolU32), ConsoleStyle.FrameRoundingPx, corners);
            else if (over)
                draw.AddRectFilled(in pos, pos + size, ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx, corners);
            if (i > 0)
                draw.AddLine(pos, pos + new float2(0f, height), ConsoleStyle.ApplyAlpha(ConsoleStyle.FrameU32), ConsoleStyle.WindowBorderThicknessPx);
            ConsoleStyle.PushLabelFont();
            draw.AddText(pos + (size - ImGui.CalcTextSize(label)) * 0.5f,
                ConsoleStyle.ApplyAlpha(active ? ConsoleStyle.PanelU32 : over ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextLabelU32), label);
            ConsoleStyle.PopFont();
        }
        draw.AddRect(in at, in end, ConsoleStyle.ApplyAlpha(pending ? ConsoleStyle.PendingU32 : ConsoleStyle.FrameU32), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
        return hovered;
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
            ConsoleStyle.PushLabelFont();
            float labelEnd = ConsoleWidgets.RowMin.X + ConsoleWidgets.RowLabelInsetPx + ImGui.CalcTextSize(s.Label).X;
            ConsoleStyle.PopFont();
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

    // The cards, four on a row, or two when what they show doesn't fit. Returns the one under the mouse
    private static Presets.Preset? DrawCards(Presets.Preset? inUse, Presets.Preset? shown, GameSettings? pending)
    {
        float s = ConsoleStyle.EffectiveInterfaceScale, inset = ConsoleWidgets.RowLabelInsetPx, gap = 8f * s, pad = 10f * s;
        var at = ImGui.GetCursorScreenPos() + new float2(inset, 0f);
        float avail = ConsoleStyle.ContentAvailWidth() - inset;
        var values = Presets.All.Select(Presets.Summary).ToArray();
        ConsoleStyle.PushSignatureFont();
        float small = ImGui.GetFontSize(), noteLine = MathF.Round(small), rowLine = noteLine;
        float need = values.SelectMany(v => v).Max(r => ImGui.CalcTextSize(r.Label).X + ImGui.CalcTextSize(r.Value).X) + gap + pad * 2f;
        ConsoleStyle.PopFont();
        ConsoleStyle.PushLabelFont();
        need = MathF.Max(need, Presets.All.Max(p => ImGui.CalcTextSize(p.Name.ToUpperInvariant()).X) + pad * 2f);
        ConsoleStyle.PopFont();
        ConsoleStyle.PushSignatureFont();
        int columns = (avail - gap * 3f) / 4f >= need ? 4 : 2;
        float width = (avail - gap * (columns - 1)) / columns;
        var notes = Presets.All.Select(p => WrapTo(p.Short, width - pad * 2f)).ToArray();
        int noteLines = notes.Max(n => n.Count);
        ConsoleStyle.PopFont();
        ConsoleStyle.PushLabelFont();
        float nameHeight = ImGui.GetFontSize();
        ConsoleStyle.PopFont();
        int rows = values[0].Length;
        float space = 7f * s, rule = pad + nameHeight + space + noteLine * noteLines + space;
        float height = rule + space + rowLine * rows + pad;
        var draw = ImGui.GetWindowDrawList();
        bool enabled = ImGui.GetStyle().Alpha >= 1f;
        Presets.Preset? hovered = null;
        for (int i = 0; i < Presets.All.Length; i++)
        {
            var preset = Presets.All[i];
            var pos = at + new float2((width + gap) * (i % columns), (height + gap) * (i / columns));
            var size = new float2(width, height);
            var end = pos + size;
            ImGui.SetCursorScreenPos(in pos);
            bool clicked = false;
            if (enabled)
                clicked = ImGui.InvisibleButton($"##MacPort_preset_{preset.Name}", in size);
            else
                ImGui.Dummy(in size);
            // A greyed-out card still has its tooltip
            bool over = ImGui.IsMouseHoveringRect(in pos, in end) && ImGui.IsWindowHovered();
            if (over)
                hovered = preset;
            if (clicked && pending != null)
            {
                Presets.Use(preset, pending, Pending);
                Hooks.Log($"Mac Port tab: {preset.Name} preset pending");
            }
            bool next = shown == preset && shown != inUse, use = inUse == preset;
            uint Colour(uint normal) => ConsoleStyle.ApplyAlpha(next ? ConsoleStyle.PanelU32 : normal);
            if (next)
                draw.AddRectFilled(in pos, in end, ConsoleStyle.ApplyAlpha(ConsoleStyle.PendingU32), ConsoleStyle.FrameRoundingPx);
            else if (use)
                draw.AddRectFilled(in pos, in end, ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(ConsoleStyle.ActiveToolU32, 0.1f)), ConsoleStyle.FrameRoundingPx);
            else if (over && enabled)
                draw.AddRectFilled(in pos, in end, ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32), ConsoleStyle.FrameRoundingPx);
            uint border = next ? ConsoleStyle.PendingU32 : use ? ConsoleStyle.ActiveToolU32 : over && enabled ? ConsoleStyle.TextMutedU32 : ConsoleStyle.FrameU32;
            draw.AddRect(in pos, in end, ConsoleStyle.ApplyAlpha(border), ConsoleStyle.FrameRoundingPx, ImDrawFlags.None, ConsoleStyle.WindowBorderThicknessPx);
            float y = pos.Y + pad, left = pos.X + pad, right = end.X - pad;
            ConsoleStyle.PushLabelFont();
            draw.AddText(new float2(left, y), Colour(use ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextLabelU32), preset.Name.ToUpperInvariant());
            ConsoleStyle.PopFont();
            y += nameHeight + space;
            ConsoleStyle.PushSignatureFont();
            foreach (var line in notes[i])
            {
                draw.AddText(new float2(left, y), Colour(use ? ConsoleStyle.TextLabelU32 : ConsoleStyle.TextMutedU32), line);
                y += noteLine;
            }
            y = pos.Y + rule;
            draw.AddLine(new float2(left, y), new float2(right, y),
                next ? ConsoleStyle.ApplyAlpha(ConsoleStyle.ApplyAlpha(ConsoleStyle.PanelU32, 0.45f)) : ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), ConsoleStyle.WindowBorderThicknessPx);
            y += space;
            foreach (var (label, value) in values[i])
            {
                draw.AddText(new float2(left, y), Colour(use ? ConsoleStyle.TextLabelU32 : ConsoleStyle.TextMutedU32), label);
                draw.AddText(new float2(right - ImGui.CalcTextSize(value).X, y), Colour(use ? ConsoleStyle.TextPrimaryU32 : ConsoleStyle.TextLabelU32), value);
                y += rowLine;
            }
            ConsoleStyle.PopFont();
        }
        int lines = (Presets.All.Length + columns - 1) / columns;
        ImGui.SetCursorScreenPos(new float2(at.X - inset, at.Y + (height + gap) * lines - gap));
        Gap(7f * s);
        return hovered;
    }

    // The benchmark's tours
    private static readonly (bool Full, string Name, string Time, string What, string Help)[] Tours =
    [
        (false, "Fast", "about 1 min", "Earth from space down to the grass, rockets and a cockpit, all paused.", "About 1 minute: a kitten in orbit, Earth from space down to the grass, then rockets and a cockpit, all with the game paused."),
        (true, "Full", "about 8 min", "From Earth to Jupiter, with a launch and a 2,000-part rocket.", "About 8 minutes: from Earth to Jupiter, with a kitten in the grass, a launch and a 2,000-part rocket."),
    ];

    // The Benchmark section's table: a row for each tour with its button, then what the last run left, and under them
    // why no tour can start
    private static void DrawBenchmark()
    {
        var temporary = _temporary.GetValue(null);
        bool changes = temporary != null && (bool)_hasAnyTabChanged.Invoke(null, [temporary])!;
        var why = changes ? "Apply or discard the changes first." : BenchmarkRun.CannotStart();
        var last = BenchmarkRun.Last;
        const string show = "SHOW REPORT";
        float s = ConsoleStyle.EffectiveInterfaceScale, inset = ConsoleWidgets.RowLabelInsetPx, gap = 12f * s;
        Gap(6f * s);
        float x = ImGui.GetCursorScreenPos().X + inset, right = ImGui.GetCursorScreenPos().X + ConsoleStyle.ContentAvailWidth();
        float buttonWidth = ConsoleWidgets.ButtonWidth(show);
        ConsoleStyle.PushLabelFont();
        float nameWidth = MathF.Max(Tours.Max(t => ImGui.CalcTextSize(t.Name).X), ImGui.CalcTextSize("Last Run").X);
        ConsoleStyle.PopFont();
        ConsoleStyle.PushSignatureFont();
        float small = ImGui.GetFontSize(), line = MathF.Round(small);
        float timeWidth = MathF.Max(Tours.Max(t => ImGui.CalcTextSize(t.Time).X), last != null ? ImGui.CalcTextSize(last.Date).X : 0f);
        ConsoleStyle.PopFont();
        float what = x + nameWidth + gap, button = right - buttonWidth, time = button - gap - timeWidth, whatWidth = time - gap - what;
        var draw = ImGui.GetWindowDrawList();
        uint hairline = ConsoleStyle.ApplyAlpha(ConsoleStyle.HairlineU32), muted = ConsoleStyle.ApplyAlpha(ConsoleStyle.TextMutedU32), label = ConsoleStyle.ApplyAlpha(ConsoleStyle.TextLabelU32);
        float y = ImGui.GetCursorScreenPos().Y;
        ConsoleStyle.PushSignatureFont();
        draw.AddText(new float2(x, y), muted, "Tour");
        draw.AddText(new float2(what, y), muted, "What It Plays");
        draw.AddText(new float2(time, y), muted, "Time");
        ConsoleStyle.PopFont();
        y += small + 6f * s;
        draw.AddLine(new float2(x, y), new float2(right, y), hairline, ConsoleStyle.WindowBorderThicknessPx);
        string? tip = null;

        // One row: returns whether its button was pressed
        bool Row(string name, string[] text, uint textColour, string when, string buttonLabel, string id, string help, bool disabled)
        {
            float height = MathF.Max(ConsoleWidgets.ButtonHeight, line * text.Length) + 12f * s;
            var min = new float2(x, y);
            var max = new float2(right, y + height);
            bool over = ImGui.IsWindowHovered() && ImGui.IsMouseHoveringRect(in min, in max);
            if (over)
            {
                draw.AddRectFilled(in min, in max, ConsoleStyle.ApplyAlpha(ConsoleStyle.RowHoverWashU32));
                tip = help;
            }
            ConsoleStyle.PushLabelFont();
            draw.AddText(new float2(x, y + (height - ImGui.GetFontSize()) * 0.5f), label, name);
            ConsoleStyle.PopFont();
            ConsoleStyle.PushSignatureFont();
            float textY = y + (height - line * text.Length) * 0.5f;
            foreach (var t in text)
            {
                draw.AddText(new float2(what, textY + (line - small) * 0.5f), textColour, t);
                textY += line;
            }
            draw.AddText(new float2(time, y + (height - small) * 0.5f), label, when);
            ConsoleStyle.PopFont();
            ImGui.SetCursorScreenPos(new float2(button, y + (height - ConsoleWidgets.ButtonHeight) * 0.5f));
            ConsoleStyle.PushDisabled(disabled);
            bool pressed = ConsoleWidgets.Button(buttonLabel, id, new float2(buttonWidth, ConsoleWidgets.ButtonHeight));
            ConsoleStyle.PopDisabled();
            y += height;
            return pressed && !disabled;
        }

        for (int i = 0; i < Tours.Length; i++)
        {
            var (full, name, when, playing, help) = Tours[i];
            ConsoleStyle.PushSignatureFont();
            var text = WrapTo(playing, whatWidth).ToArray();
            ConsoleStyle.PopFont();
            if (Row(name, text, muted, when, "RUN", "MacPort_run_" + name, why ?? help + "\n\nIt plays with the settings above, and writes its report to the benchmarks folder in the game's documents folder. Leave the mouse and keyboard alone until it is done; Esc stops it.", why != null))
                BenchmarkRun.StartFromTab(full, PortSettings.Get("restart_for_a_clean_run")?.Saved == "true");
            if (i < Tours.Length - 1 || last != null)
                draw.AddLine(new float2(x, y), new float2(right, y), hairline, ConsoleStyle.WindowBorderThicknessPx);
        }
        if (last != null && Row("Last Run", [last.Outcome], ConsoleStyle.ApplyAlpha(last.Passed ? ConsoleStyle.TextLabelU32 : ConsoleStyle.DangerU32), last.Date, show, "MacPort_show_report", last.Summary, false))
            BenchmarkRun.Show(last.Folder);
        ImGui.SetCursorScreenPos(new float2(x - inset, y));
        Gap(4f * s);
        if (why != null)
            Caption(why, inset);
        if (tip != null)
            Tooltip(tip);
    }
}
