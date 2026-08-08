using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cxm_loop — Path A's missing half: CLOSE THE LOOP. Tests H-A1.
//
// path-a §4 specifies a FEEDBACK architecture:
//
//     sponge zone ──send──► input diffuser → figure-of-eight ──return──► forcing
//
// Everything built before this is the RETURN only. The tank was driven by the same excitation
// as the sim — a parallel voice, not a loop. Until water feeds the tank, it is not Path A.
//
// Here the send is real: a GPU reduction sums |h| over the outer annulus (the doc's "energy
// currently thrown away by the relaxation zone") and that scalar becomes the tank's input. The
// tank's taps return into the water. Water → tank → water.
//
// H-A1: with loop gain < 1 this is stable, and after the drive stops the sea state persists and
// keeps evolving rather than decaying flat.
//
// REFUTED IF either failure mode shows: it dies immediately at every gain (the loop is not
// really closing), or it runs away at every gain that produces anything visible (no usable
// window between silent and unstable).
//
// The send readout is the thing to watch — if it stays pinned at zero the loop is open no
// matter what the gain says.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm_loop.tscn 16 1280x900
public partial class ExpCxmLoop : ExpCxmScene
{
    private float _loopGain = 0.35f;
    private float _sendBand = 0.35f;
    private float _sendLevel;
    private float _sendSmooth;
    private bool _loopClosed = true;
    private bool _driveOff;

    private static readonly Vector2[] TapPos =
    {
        new(-0.55f, -0.45f), new(0.50f, -0.55f), new(0.60f, 0.50f), new(-0.45f, 0.58f),
        new(0.00f, -0.62f), new(0.62f, 0.00f), new(0.00f, 0.62f), new(-0.62f, 0.00f),
    };

    protected override (string Title, string Hint) SceneInfo => (
        "24_cxm_loop · closed water→tank→water",
        "Path A's missing half. Everything before this was the RETURN only — the tank ran on the "
        + "same excitation as the sim, a parallel voice rather than a loop. Here the SEND is "
        + "real: a GPU reduction sums |h| over the outer annulus (the 'energy thrown away by the "
        + "relaxation zone') and feeds it to the tank input; the tank's taps return into the "
        + "water. H-A1 says gain < 1 is stable and sustains after the drive stops. Watch the "
        + "send level — pinned at zero means the loop is not closing at all.");

    protected override string StateText() =>
        !_loopClosed ? $"loop OPEN (return only) · send {_sendSmooth:0.0000}"
        : $"LOOP CLOSED · gain {_loopGain:0.00} · send {_sendSmooth:0.00000} · tank out {LastTankOut:0.000}"
          + (_driveOff ? " · DRIVE OFF" : "");

    public override void _Ready()
    {
        TapCount = 6;
        base._Ready();
    }

    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        // The external drive is what STARTS the loop; H-A1 is about what happens once it stops.
        if (!_driveOff) { base.CollectDrive(dt, outDrops); }

        var solver = Solver;
        if (solver == null || !solver.Ready) { return; }

        // --- THE SEND: water energy becomes the tank's input ---
        if (_loopClosed)
        {
            float raw = 0.0f;
            RenderingServer.CallOnRenderThread(Callable.From(() => raw = solver.ReadSend(_sendBand)));
            RenderingServer.ForceSync();
            _sendLevel = raw;
            // Smooth it: the raw sum is an energy envelope sampled once per frame, and feeding
            // a jumpy DC-ish level straight into an allpass cascade just rings the diffuser.
            _sendSmooth += (raw - _sendSmooth) * 0.25f;
        }
        else { _sendLevel = 0.0f; _sendSmooth = 0.0f; }

        // send is now a MEAN amplitude, same units as the drive, so gain is a real ratio
        float excitation = _loopClosed ? _sendSmooth * _loopGain : 0.0f;
        // plus whatever the external drive contributed this frame
        if (outDrops.Count > 0) { excitation += outDrops[0].Z; }

        RunTank(dt, excitation);
        if (!TankOn) { return; }
        for (int i = 0; i < TapCount && i < TapPos.Length; ++i)
        {
            if (MathF.Abs(TapValues[i]) < 1.0e-7f) { continue; }
            outDrops.Add(new Vector3(TapPos[i].X, TapPos[i].Y, TapValues[i]));
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — is the loop closed?");
        ui.AddToggle("Close the loop (send on)", _loopClosed, v => { _loopClosed = v; Tank.Reset(); });
        ui.AddSlider("Loop gain", 0.0f, 3.0f, _loopGain, v => _loopGain = v);
        ui.AddSlider("Send band (annulus width)", 0.05f, 1.0f, _sendBand, v => _sendBand = v);
        // H-A1's actual test: kill the drive and see whether anything survives.
        ui.AddToggle("Drive OFF (does it sustain?)", _driveOff, v => _driveOff = v);
        AddTankControls(ui, true);
    }
}
