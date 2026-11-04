// The run's two clocks. The script's clock counts the seconds of play: the steps, the camera's moves and the measured
// parts follow it. The game's clock is the simulation's, which @warp speeds up or pauses: the script says where it should
// be at every moment (GameTarget), and the speeds it asks for take it there. A slower Mac falls behind (the game slows
// the simulation down when it cannot keep up); a checkpoint puts the two back in step (Checkpoints.cs).
//
// The game's own step limit (Universe.LimitNextStep), set just before the game works out each step of the simulation
// (BeforeStep, from the hooks' prefix on Universe.GetJobSimStep), keeps the game from passing the moment the script
// expects: a flight's steps never pass the next checkpoint or @sync, a catch-up lands on its moment, and the step at the
// moment is none. A views scenario's setup plays the script in steps of SetupStep, each step of the simulation limited to
// the script's game time and asked at the script's speed, so every Mac runs the same steps.
using System;
using System.Collections.Generic;
using KSA;

namespace KsaMetal.Benchmark;

// What the next step of the simulation is, as the director plays: nothing to limit, a checkpoint catching up, a views
// scenario setting up, or a flight going on to its next stop
internal enum Stepping { Free, CatchUp, Setup, Flight }

internal sealed class Clock
{
    // How close the game gets to the game time it is taken to (game seconds)
    private const double Tolerance = 1e-4;
    // How long the simulation takes to ease to a stop at a checkpoint or @sync, and back to its speed after it
    private const double StopEase = 0.75;
    // While the script warps, how many seconds the game takes to make up what it fell behind
    private const double WarpCatchUp = 0.5;
    // A views scenario's setup: the script's clock goes on by this much a step, and the game's by what the script asks
    // for in it (a frame covers a step this small unless the game's physics slowed it down: the step is then finished in
    // the next frame)
    private const double SetupStep = 1.0 / 240;

    // The script's clock, the game time at the start and the game time the script expects now
    public double Script { get; private set; }
    public double GameStart { get; private set; }
    public double GameTarget { get; private set; }

    // The simulation speed the script asks for: @warp moves it from one speed to another over some seconds
    private double _warpFrom = 1, _warpTo = 1, _warpStart, _warpSeconds;
    private double _speedSent = double.NaN;
    // The times of the checkpoints and @sync steps on the script's clock, where the simulation eases to a stop
    private readonly List<double> _stops = new();
    // The next stop's game time in a flight, worked out again only when the script's speeds or its game time change
    // otherwise than by playing on
    private double _limitStop = double.NaN, _limitGame;

    // A views scenario's setup: the script's time its steps have gone on by since the director last asked, and the steps
    // the game fell short of (it runs a step at under the speed asked when its physics took most of the frame before)
    private double _setupAdvanced;
    public int ShortSteps { get; private set; }

    public void AddStop(double t) => _stops.Add(t);

    // Both clocks from the game's now, at this speed
    public void Begin(double speed)
    {
        GameStart = GameTarget = Universe.GetElapsedSeconds();
        _warpFrom = _warpTo = speed;
        _warpSeconds = 0;
        _limitStop = double.NaN;
    }

    // The script's clock back to its start for another pass, the game's taken as it is
    public void Rewind()
    {
        Script = 0;
        Resync();
    }

    // The game's clock taken as it is (after a timed scenario, which keeps no time with the script)
    public void Resync()
    {
        GameTarget = Universe.GetElapsedSeconds();
        _limitStop = double.NaN;
    }

    // A new world, its clock at its start, the game stopped
    public void NewWorld(double speed)
    {
        Begin(speed);
        _speedSent = double.NaN;
        SetSpeed(0);
    }

    // A flight at real time from here, whatever the scenario before it left
    public void RealTime()
    {
        Resync();
        _warpFrom = _warpTo = 1;
        _warpSeconds = 0;
        SetSpeed(1);
    }

    // The script plays on by dt: the game time it expects goes on by what it asks for meanwhile, and the speed asked for
    // follows, a little more while the script warps and the game falls behind (it keeps a little under the speed it is
    // asked for, which adds up to hours over a time-lapse)
    public void Play(double dt, bool timed)
    {
        GameTarget += GameSeconds(Script, Script + dt);
        Script += dt;
        double speed = SpeedAt(Script);
        if (!timed && speed > 2)
            speed = Math.Max(speed + (GameTarget - Universe.GetElapsedSeconds()) / WarpCatchUp, 0);
        SetSpeed(speed);
    }

    // Jumps the script's clock to t, the game paused (places jumped to)
    public void JumpTo(double t) => Script = t;

    // Back to a step's own time (the frame went a little past it), so that the game's clock catches up with the game time
    // of that moment
    public void BackTo(double stepAt)
    {
        _limitStop = double.NaN;
        GameTarget -= GameSeconds(stepAt, Script);
        Script = stepAt;
    }

    // @warp: the new speed starts at the step's time, the game time the frame already added after it, at the old speed,
    // made up at the new one
    public void Warp(double speed, double seconds, double stepAt)
    {
        _limitStop = double.NaN;
        double after = GameSeconds(stepAt, Script);
        _warpFrom = SpeedAt(stepAt);
        _warpTo = speed;
        _warpStart = stepAt;
        _warpSeconds = seconds;
        GameTarget += GameSeconds(stepAt, Script) - after;
    }

    // Asks the game for a simulation speed (0 pauses it), as a player does: the game takes it before its next step
    public void SetSpeed(double speed)
    {
        if (speed == _speedSent)
            return;
        _speedSent = speed;
        InputEvents.SimSpeedBuffer.Add(new InputEvents.SimSpeedData { Speed = new SimSpeed(speed) });
    }

    // The speed for after a checkpoint: paused, or what the script asks for now
    public void Resume(bool paused) => SetSpeed(paused ? 0 : SpeedAt(Script));

    // A checkpoint catching up: at least the scripted speed, more while far behind (the step limit lands the game on the
    // moment itself); true once it is there
    public bool CatchUp(double seconds)
    {
        double behind = GameTarget - Universe.GetElapsedSeconds();
        if (behind > Tolerance)
        {
            SetSpeed(Math.Max(Math.Max(_warpTo, 1), behind / seconds));
            return false;
        }
        SetSpeed(0);
        return true;
    }

    // The script's time a views scenario's setup went on by since the last time this was asked
    public double TakeSetupAdvance()
    {
        double advanced = _setupAdvanced;
        _setupAdvanced = 0;
        return advanced;
    }

    // Before the game works out its next step of the simulation, as the director plays: the game time that step may not
    // pass. In a setup the script first goes on by a step, up to nextStepAt at most (which the frame then plays)
    public void BeforeStep(Stepping stepping, double nextStepAt)
    {
        if (stepping == Stepping.Free)
            return;
        double now = Universe.GetElapsedSeconds();
        if (stepping == Stepping.Setup)
        {
            // The last step fell short of where it was to end: this one ends there instead
            if (now < GameTarget - Tolerance)
                ShortSteps++;
            else
            {
                double h = Math.Min(SetupStep, Math.Max(nextStepAt - Script, 0));
                GameTarget += GameSeconds(Script, Script + h);
                Script += h;
                _setupAdvanced += h;
            }
            // Asked now, for this step, as a player's request would be
            Universe.SetSimulationSpeed(SpeedAt(Script), alert: false);
            _speedSent = double.NaN;
        }
        if (Limit(stepping) is not { } limit)
            return;
        if (limit - now <= Tolerance)
        {
            // There already: no step at all, whatever speed was asked last
            Universe.SetSimulationSpeed(0, alert: false);
            _speedSent = double.NaN;
            return;
        }
        Universe.LimitNextStep(Universe.GetElapsedTime() + (limit - now));
    }

    // The game time the next step may not pass: the moment being caught up with, the script's in a setup, the next stop's
    // in a flight; null when there is none
    private double? Limit(Stepping stepping)
    {
        if (stepping is Stepping.CatchUp or Stepping.Setup)
            return GameTarget;
        foreach (double stop in _stops)
        {
            if (stop < Script)
                continue;
            if (stop != _limitStop)
                (_limitStop, _limitGame) = (stop, GameTarget + GameSeconds(Script, stop));
            return _limitGame;
        }
        return null;
    }

    // The simulation speed the script asks for at a time of its clock: @warp's, eased to a stop around checkpoints and
    // @sync steps, so that the world comes to rest at them as the camera does
    private double SpeedAt(double t)
    {
        double warp = _warpTo;
        if (t < _warpStart + _warpSeconds)
        {
            double u = Smooth(Math.Clamp((t - _warpStart) / _warpSeconds, 0, 1));
            // Between two speeds, by the same factor every second (from 1 to 100 passes 10 halfway)
            warp = _warpFrom > 0 && _warpTo > 0
                ? Math.Exp(Math.Log(_warpFrom) + (Math.Log(_warpTo) - Math.Log(_warpFrom)) * u)
                : _warpFrom + (_warpTo - _warpFrom) * u;
        }
        foreach (double stop in _stops)
        {
            double away = Math.Abs(t - stop);
            if (away < StopEase)
                warp *= Smooth(away / StopEase);
        }
        return warp;
    }

    // The game seconds the script asks for between two times of its clock: the speed summed in steps of a millisecond or
    // less (Simpson's rule), so the script's game time is the same at the same moment of its clock, whatever the frame
    // times on the way
    private double GameSeconds(double from, double to)
    {
        int n = 2 * Math.Max(1, (int)Math.Ceiling((to - from) / 0.002));
        double h = (to - from) / n, sum = SpeedAt(from) + SpeedAt(to);
        for (int i = 1; i < n; i++)
            sum += SpeedAt(from + i * h) * (i % 2 == 1 ? 4 : 2);
        return sum * h / 3;
    }

    private static double Smooth(double u) => u * u * (3 - 2 * u);
}
