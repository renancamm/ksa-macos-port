// The lineup's directives as the director acts on them: one handler for each row of their table
// (src/shared/benchmark/Directives.cs, which says what each takes and does; the lineup's parser checks every step against
// it). The timing directives are here, the vehicles' in Vehicles.cs, the camera's in the camera (Camera.cs, Path.cs,
// Rig.cs). A run that finds a row without a handler, or a handler without a row, says so in its record.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using KSA;
using KsaPort.Common;
using KsaPort.Common.Benchmark;

namespace KsaMetal.Benchmark;

public partial class Director
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    // The handlers, by directive
    private Dictionary<string, Func<string[], bool>> Handlers()
    {
        var handlers = new Dictionary<string, Func<string[], bool>>
        {
            ["@measure"] = Measure,
            ["@shot"] = Shot,
            ["@warp"] = WarpStep,
            ["@sync"] = _ =>
            {
                Sync();
                return true;
            },
            ["@reset"] = _ =>
            {
                Reset();
                return true;
            },
            // The camera's flight (Path.cs) is laid out before the run; the step only marks the key's time
            ["@key"] = _ => true,
            ["@place"] = Place,
            ["@camera"] = a => _camera.SetCameraMode(a[1]),
            ["@front"] = _camera.StartFront,
            ["@chase"] = _camera.StartChase,
            ["@watch"] = _camera.StartWatch,
            ["@aim"] = _camera.StartAim,
            ["@fov"] = Fov,
            ["@look"] = _camera.LookIva,
            ["@sun"] = CameraOperator.Sun,
            ["@settings"] = a => a[1] switch
            {
                "open" or "close" when a.Length == 2 => OpenSettings(a[1] == "open"),
                "pick" when a.Length == 3 => PickPreset(a[2]),
                _ => false,
            },
        };
        foreach (var (name, handler) in _vehicles.Handlers())
            handlers[name] = handler;
        return handlers;
    }

    // What doesn't match between the directives' table and the handlers: each is an error of the run
    private IEnumerable<string> UnhandledDirectives() =>
        Directives.All.Where(d => !_handlers.ContainsKey(d.Name)).Select(d => $"the director has no handler for {d.Name}")
            .Concat(_handlers.Keys.Where(k => Directives.Find(k) == null).Select(k => $"the director handles {k}, which the directives' table lacks"));

    // A directive step; its words were checked against the table when the lineup was read
    private bool Directive(Step step)
    {
        if (_handlers.TryGetValue(step.Words[0], out var handler))
            return handler(step.Words);
        Log($"unknown directive '{step.Command}'");
        return false;
    }

    // @measure <name> <seconds>
    private bool Measure(string[] a)
    {
        if (!double.TryParse(a[2], NumberStyles.Float, Inv, out var seconds))
            return false;
        CloseWindow();
        _window = new Window(a[1], _clock.Script + seconds, FollowedVehicle());
        // Views are measured standing still instead
        if (!Fast)
            Opened(_window);
        return true;
    }

    // @shot <name>
    private bool Shot(string[] a)
    {
        // How much it may change from run to run: the test's noise profile, or a flight's when its places were jumped
        // to (the clouds and the sea there depend on the way the camera came, which the places chosen change)
        var playing = _sections[_section];
        _shotLimit = _noise.GetValueOrDefault(playing.Name)?.ShotLimit(a[1], playing.Kind) ?? NoiseProfile.DefaultShotLimit(playing.Kind);
        // The game stays paused: nothing to keep in step
        if (Paused && !IsChosen(a[1]))
            return true;
        StartCheckpoint(a[1]);
        return true;
    }

    // @warp <speed> [seconds]
    private bool WarpStep(string[] a)
    {
        double seconds = 0;
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var speed) || speed < 0 ||
            a.Length == 3 && !double.TryParse(a[2], NumberStyles.Float, Inv, out seconds))
            return false;
        _clock.Warp(speed, seconds, _stepAt);
        return true;
    }

    // @place <celestial> <lat> <lon> <alt> <heading> <pitch>
    private bool Place(string[] a)
    {
        if (CameraOperator.ParseKey(_clock.Script, a, true) is not { } key)
            return false;
        _camera.SetCameraMode("free");
        _camera.Place(key);
        CameraOperator.ForgetUp();
        _camera.UpdateCamera(0);
        return true;
    }

    // @fov <degrees>
    private bool Fov(string[] a)
    {
        if (!double.TryParse(a[1], NumberStyles.Float, Inv, out var fov) || fov <= 0 || Program.GetHoveredCamera() is not { } camera)
            return false;
        _camera.KeepFieldOfView(camera.GetFieldOfView());
        camera.SetFieldOfView((float)fov);
        return true;
    }

    // @reset
    private void Reset()
    {
        CloseWindow();
        _camera.ReleaseCamera();
        _camera.EndRig();
        _vehicles.Reset();
        World.NewWorld();
        // A timed scenario's game stays stopped until its @warp; another runs at real time after the steps at this time
        _clock.NewWorld(Live ? 0 : 1);
        _phase = Phase.NewWorld;
        _phaseFrames = 0;
    }

    // @settings open|close: the game's settings window, open on the rail's first tab, which the hooks make the Mac Port
    // tab (the window keeps the tab it was last on: GameSettings._openTabIndex). The game draws it with its interface,
    // which a run hides: a postfix on Program.DrawFps, which the game runs every frame either way, draws it alone over
    // the scene while the interface is hidden
    private bool OpenSettings(bool open)
    {
        if (!open)
        {
            GameSettings.Close();
            return true;
        }
        var index = typeof(GameSettings).GetField("_openTabIndex", BindingFlags.NonPublic | BindingFlags.Static);
        if (index == null)
            Log("@settings: GameSettings._openTabIndex not found: the window opens on the tab it was last on");
        index?.SetValue(null, 0);
        if (!_settingsAlone)
        {
            try
            {
                Probes.Patcher!(Probes.Find!(typeof(Program), "DrawFps", Type.EmptyTypes), null,
                    typeof(Director).GetMethod(nameof(DrawSettingsAlone), BindingFlags.NonPublic | BindingFlags.Static), null);
                _settingsAlone = true;
            }
            catch (Exception e)
            {
                Log($"@settings: the window can't be drawn without the game's interface ({e.Message}): the interface shows");
                Program.DrawUI = true;
            }
        }
        GameSettings.Show = true;
        return true;
    }

    private static bool _settingsAlone;

    // @settings pick <preset>: the Mac Port tab clicks that card the next time it draws its cards in the window open now,
    // as a player does (Runs.Install has the hooks' click). The card turns gold unless the settings are based on it
    // already, its values pending until the window closes
    private bool PickPreset(string id)
    {
        if (TabPresets.Find(id) == null)
        {
            Log($"@settings pick: no preset '{id}' ({string.Join(", ", TabPresets.All.Select(p => p.Id))})");
            return false;
        }
        if (Runs.PickPreset(id))
            return true;
        Log("@settings pick: the Mac Port tab can't pick a preset (logs/console.log says why)");
        return false;
    }

    // After Program.DrawFps: the settings window, open with the interface hidden
    private static void DrawSettingsAlone()
    {
        try
        {
            if (GameSettings.Show && !Program.DrawUI)
                GameSettings.OnDrawUi(Program.GetCamera());
        }
        catch (Exception e)
        {
            Log($"@settings: the window alone failed, closed: {e.Message}");
            GameSettings.Close();
        }
    }
}
