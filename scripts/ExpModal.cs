using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_modal — Path E finished: measure the basin, FIT it, replay it with resonators.
//
// Tests H-E1, H-E2 and H-E3 in docs/hypotheses.md.
//
// 24_ir does sample space — whole-field slices, an FIR, 268 MB at 1024 slices. path-e §2 says
// that is the wrong space: diagonalize and the system separates into independent damped
// oscillators, one per mode. This scene does that:
//
//   CAPTURE — fire one impulse, and each tick project the field onto the analytic basis to get
//             a_n[k], one scalar series per mode instead of one whole field per tick.
//   FIT     — per mode, omega from the dominant DFT bin and sigma from a log-envelope
//             regression over the peaks. Printed to the panel so you can see what was measured.
//   REPLAY  — a bank of 2-pole resonators, solver OFF. Two floats of state per mode. NO stored
//             kernel at all, which is the actual claim: "a few hundred resonators replaces a
//             million-cell solve".
//
// Three-way A/B on one drive: SIM · FIR (24_ir's path) · MODAL. Costs on the readout.
//
// Expected to be interesting rather than clean: H-E2 predicts modal wins on MEMORY (zero vs
// 268 MB) but probably NOT on per-frame arithmetic, because painting N modes across 65k cells
// is the same per-cell cost the FIR pays. 24_cxm_field already showed that trap.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_modal.tscn 16 1280x900
public partial class ExpModal : ExpPoolScene
{
    private enum Phase { Capture, Replay, Sim }

    private const int CaptureTicks = 240;
    private const int ModeCount = 8;

    private ModalFit _fit = null!;
    private ResonatorBank _bank = null!;
    private Phase _phase = Phase.Capture;
    private int _tick;
    private bool _fired, _haveFit;

    private static readonly Vector2 ImpulseAt = new(-0.25f, -0.2f);
    private const float ImpulseStrength = 0.066f;

    private int _sigKind;
    private float _sigT, _phaseT, _sigInterval = 1.0f, _sigStrength = 1.0f;
    private float _basisGain = 30.0f;
    private readonly RandomNumberGenerator _rng = new();
    private Label? _fitReadout;

    protected override (string Title, string Hint) SceneInfo => (
        "24_modal · measure → fit → resonators",
        "Path E finished. CAPTURE fires one impulse and projects each tick onto the analytic "
        + "basis, so the response becomes 8 scalar time series instead of 8 whole fields. FIT "
        + "extracts each mode's frequency (dominant DFT bin) and decay (log-envelope "
        + "regression). REPLAY runs a bank of 2-pole resonators with the solver OFF — two floats "
        + "of state per mode and NO stored kernel, against the FIR's 268 MB. Tests H-E1/E2/E3.");

    protected override string StateText() => _phase switch
    {
        Phase.Capture => $"CAPTURE {_tick}/{CaptureTicks} (projecting {ModeCount} modes)",
        Phase.Replay => $"MODAL REPLAY — solver OFF — {ModeCount} resonators, 0 bytes stored",
        _ => "SIM — live solver",
    };

    protected override bool StepSolver => _phase != Phase.Replay;

    public override void _Ready()
    {
        _fit = new ModalFit(Grid, ModeCount, CaptureTicks);
        _bank = new ResonatorBank(ModeCount);
        base._Ready();
    }

    protected override void OnSolverReady() => StartCapture();

    private void StartCapture()
    {
        _phase = Phase.Capture;
        _tick = 0;
        _fired = false;
        _haveFit = false;
        _fit.Reset();
        _bank.Reset();
        Solver?.Reset();
    }

    private float NextDrive(float dt)
    {
        _sigT += dt;
        _phaseT += dt;
        switch (_sigKind)
        {
            case 1: return _sigStrength * 0.06f * (MathF.Sin(_phaseT * 1.7f) + 0.6f * MathF.Sin(_phaseT * 2.63f));
            case 2: return _sigStrength * 0.10f * (_rng.Randf() * 2.0f - 1.0f);
            default:
                if (_sigT < _sigInterval) { return 0.0f; }
                _sigT = 0.0f;
                return _sigStrength;
        }
    }

    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        if (_phase == Phase.Capture)
        {
            bool fire = !_fired;
            _fired = true;
            if (fire) { outDrops.Add(new Vector3(ImpulseAt.X, ImpulseAt.Y, ImpulseStrength)); }
            return;
        }
        if (_phase == Phase.Sim)
        {
            float e = NextDrive(dt) * ImpulseStrength;
            if (MathF.Abs(e) > 1.0e-7f) { outDrops.Add(new Vector3(ImpulseAt.X, ImpulseAt.Y, e)); }
        }
    }

    protected override void AfterStep(float dt)
    {
        var solver = Solver;
        if (solver == null || !solver.Ready) { return; }

        if (_phase == Phase.Capture)
        {
            // Project this tick. A full readback per tick is a sync stall, which is fine here
            // and ONLY here — capture is offline and one-time.
            float[] field = null!;
            RenderingServer.CallOnRenderThread(Callable.From(() => field = solver.ReadField()));
            RenderingServer.ForceSync();
            if (field != null && field.Length > 0) { _fit.Accumulate(field); }

            if (++_tick >= CaptureTicks)
            {
                _fit.Fit();
                _bank.Configure(_fit);
                _haveFit = true;
                _phase = Phase.Replay;
                ReportFit();
            }
            return;
        }

        if (_phase == Phase.Replay && _haveFit)
        {
            // Drive the resonators, then paint their amplitudes through the SAME basis the
            // projection used. Every cell is an independent weighted sum of N cosines.
            float x = NextDrive(dt);
            float[] amps = _bank.Process(x);
            var a = (float[])amps.Clone();
            float g = _basisGain;
            RenderingServer.CallOnRenderThread(Callable.From(() => solver.ModalBasis(a, ModeCount, g, false)));
        }
    }

    private void ReportFit()
    {
        if (_fitReadout == null) { return; }
        var sb = new System.Text.StringBuilder();
        for (int m = 0; m < ModeCount; ++m) { sb.AppendLine(_fit.Describe(m)); }
        _fitReadout.Text = sb.ToString();
        GD.Print("[modal fit]\n" + sb);
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        _fitReadout = ui.AddReadout("(fit pending)");
        ui.AddSection("THE VARIABLE — how the response is represented");
        ui.AddOptions("Mode", new[] { "Modal (resonators, solver OFF)", "Sim (live solver)" }, 0,
            v => { if (v == 1) { _phase = Phase.Sim; } else if (_haveFit) { _phase = Phase.Replay; _bank.Reset(); } else { StartCapture(); } });
        ui.AddToggle("Re-capture + re-fit", false, v => { if (v) { StartCapture(); } });
        ui.AddSlider("Basis gain", 0.0f, 200.0f, _basisGain, v => _basisGain = v);

        ui.AddSection("Drive signal");
        ui.AddOptions("Signal", new[] { "Impulse train", "Swell", "Noise" }, 0, v => _sigKind = v);
        ui.AddSlider("Interval (s)", 0.05f, 3.0f, _sigInterval, v => _sigInterval = v);
        ui.AddSlider("Strength", 0.05f, 3.0f, _sigStrength, v => _sigStrength = v);
    }
}
