using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Brutal.ImGuiApi.Abstractions;
using HarmonyLib;
using KSA;
using KsaMetalBenchmark;
using KsaPort.Common;

namespace KsaMetalHooks.App;

// The console command "framecapture [note]": for a picture that goes wrong now and then, everything that can say where it
// went wrong, from the moment it is on the screen, in a folder of its own (framecaptures/<date>_<time> in the documents
// folder):
//   before.png        KSA Metal's image of the frame the command ran in
//   passes/           the next frame's passes: their images and numbers, in passes.txt
//   frame.gputrace    an Xcode GPU frame capture of a frame after them, every pass and its inputs (needs the frame_capture
//                     port setting, set when the game starts, and 20 GB free)
//   after.png         KSA Metal's image of the frame after it
//   state.txt         the time, the note, the camera's place, the warp, the driver's numbers and the switches set
//   console.log, settings.toml, port-settings.toml
// If after.png shows the fault too, the fault lasted through those frames, and passes/ shows the first draw that has it.
[Hook(Phase.PortParts)]
internal sealed class FrameCaptureCommand
{
    private enum Step { Idle, Passes, Capturing, After }

    private const long CaptureNeedsBytes = 20L << 30;
    private const double CaptureLimitSeconds = 120;
    private const int ShotFrames = 120;

    private static readonly FrameCaptureCommand Instance = new();
    private static bool _registered;

    private Step _step;
    private string _folder = "";
    private string? _noCapture;
    private bool _noPasses;
    private readonly Stopwatch _captureTime = new();
    private int _frames;

    public static string Apply(Harmony harmony)
    {
        harmony.Patch(Find.Method(typeof(Program), "OnFrame", typeof(double), typeof(double)),
            postfix: new HarmonyMethod(typeof(FrameCaptureCommand), nameof(FramePostfix)));
        return "on";
    }

    private static void FramePostfix()
    {
        try
        {
            if (!_registered)
            {
                _registered = true;
                Program.TerminalInterface.RegisterCommand(new Action<string>(Instance.Record), Instance);
            }
            Instance.OnFrame();
        }
        catch (Exception e)
        {
            Hooks.Log($"FrameCaptureCommand: {e}");
            Instance._step = Step.Idle;
        }
    }

    [TerminalAction("framecapture", "save a frame capture, images and the game's state, to see where the picture went wrong: framecapture [note]", ArgParseMode.Unparsed)]
    private void Record(string note)
    {
        if (_step != Step.Idle)
        {
            Program.ConsoleWindow.Print("framecapture: still saving the last one");
            return;
        }
        try
        {
            _folder = Path.Combine(Constants.DocumentsFolderPath, "framecaptures", DateTime.Now.ToString("yyyy-MM-dd_HHmmss_fff"));
            Directory.CreateDirectory(_folder);
            File.WriteAllText(Path.Combine(_folder, "state.txt"), State(note.Trim()));
            DriverStats.Screenshot(Path.Combine(_folder, "before.png"));
            _noPasses = !DriverStats.PassDump(Path.Combine(_folder, "passes"));
        }
        // A command's exception would end the game
        catch (Exception e)
        {
            Program.ConsoleWindow.Print($"framecapture: could not save it: {e.Message}");
            return;
        }
        _captureTime.Restart();
        _step = Step.Passes;
        Program.ConsoleWindow.Print($"framecapture: saving to {_folder}");
    }

    // After the capture's frame (at once without a capture), the image after it; then a moment for it to be written,
    // and the log, which by then says how the capture went
    private void OnFrame()
    {
        switch (_step)
        {
            case Step.Passes:
                if (DriverStats.PassDumping() && _captureTime.Elapsed.TotalSeconds < CaptureLimitSeconds)
                    return;
                try
                {
                    _noCapture = DriverStats.GpuErrors() > 0 ? "the GPU has failed and refuses this game's work: a capture would replay as nonsense"
                        : new DriveInfo(_folder).AvailableFreeSpace < CaptureNeedsBytes ? "less than 20 GB free"
                        : DriverStats.Capturing() ? "another capture is still running"
                        : !DriverStats.Available ? "the game isn't on KSA Metal"
                        : DriverStats.Capture(CapturePath, 1) ? null
                        : "set frame_capture = true in port-settings.toml and restart the game";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _noCapture = e.Message;
                }
                _captureTime.Restart();
                _step = Step.Capturing;
                return;
            case Step.Capturing:
                if (DriverStats.Capturing() && _captureTime.Elapsed.TotalSeconds < CaptureLimitSeconds)
                    return;
                DriverStats.Screenshot(Path.Combine(_folder, "after.png"));
                _frames = 0;
                _step = Step.After;
                return;
            case Step.After:
                if (++_frames < 3 || !File.Exists(Path.Combine(_folder, "after.png")) && _frames < ShotFrames)
                    return;
                _step = Step.Idle;
                Finish();
                return;
        }
    }

    private void Finish()
    {
        try
        {
            Keep(Runs.ConsoleLog);
            Keep(Path.Combine(Constants.DocumentsFolderPath, "settings.toml"));
            Keep(PortSettings.FilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Program.ConsoleWindow.Print($"framecapture: not all of the files: {e.Message}");
        }
        if (_noPasses)
            Program.ConsoleWindow.Print(DriverStats.GpuErrors() > 0
                ? "framecapture: no passes/: the GPU has failed and refuses this game's work"
                : "framecapture: no passes/: KSA Metal could not start a pass dump");
        // The driver only logs a capture that doesn't start
        if (_noCapture == null && DriverStats.Capturing())
            _noCapture = "it is still being written";
        else if (_noCapture == null && !Directory.Exists(CapturePath) && !File.Exists(CapturePath))
            _noCapture = "it did not start (the log says why)";
        Program.ConsoleWindow.Print(_noCapture == null
            ? $"framecapture: saved to {_folder}"
            : $"framecapture: saved to {_folder}, without a frame capture: {_noCapture}");
    }

    private string CapturePath => Path.Combine(_folder, "frame.gputrace");

    private void Keep(string? file)
    {
        if (file != null && File.Exists(file))
            File.Copy(file, Path.Combine(_folder, Path.GetFileName(file)), true);
    }

    private static string State(string note)
    {
        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}"));
        text.AppendLine($"note: {note}");
        text.AppendLine(FormattableString.Invariant($"game time: {Universe.GetElapsedSeconds():F1} s"));
        text.AppendLine(FormattableString.Invariant($"warp: {Universe.GetSimulationSpeed()}"));
        text.AppendLine($"camera: {Benchmark.CurrentKey() ?? "near no celestial"}");
        if (DriverStats.Read() is { } f)
        {
            text.AppendLine(FormattableString.Invariant(
                $"frame: present {f.Present}, CPU {f.CpuMs:F1} ms, GPU {f.GpuMs:F1} ms, display {f.DisplayMs:F1} ms, {f.Draws} draws, {f.Dispatches} dispatches, {f.Passes} passes"));
            text.AppendLine($"image: {f.DrawableWidth} x {f.DrawableHeight}");
            text.AppendLine($"memory: footprint {f.FootprintMb} MB, GPU {f.AllocatedMb} MB");
            text.AppendLine($"thermal state: {f.ThermalState}");
            text.AppendLine($"failed command buffers: {DriverStats.GpuErrors()}");
        }
        else
            text.AppendLine("frame: not on KSA Metal");
        foreach (var name in Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Select(v => (string)v.Key)
                     .Where(k => k.StartsWith("KSA_", StringComparison.Ordinal) || k.StartsWith("MTL_", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            text.AppendLine($"{name}={Environment.GetEnvironmentVariable(name)}");
        return text.ToString();
    }
}
