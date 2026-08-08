using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_irlen — isolated variable: IMPULSE-RESPONSE KERNEL LENGTH.
//
// On ExpPoolScene, so unlike the original 24_ir this shares the frozen baseline AND can have
// obstacles — capturing an IR with a column in the pool is the interesting follow-on, since a
// STATIC scatterer is completely free in a linear operator: its diffraction, shadow and fringes
// are all baked into the measurement exactly, for any input signal, forever.
//
// The variable: how many ticks of response to record, 64 to 1024. At 256^2 x 4 bytes that is
// 16 MB to 268 MB, and 268 MB is the practical ceiling.
//
// Why it matters, given "nothing needs to ring": if a short kernel looks the same as a long
// one, the memory freed is what makes MULTI-SOURCE affordable — 8 sources at 64 slices costs
// less than one source at 512. The whole question is whether the tail is load-bearing.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_irlen.tscn 16 1280x900
public partial class ExpIrLen : ExpPoolScene
{
    private static readonly int[] Lengths = { 64, 128, 256, 512, 1024 };

    private enum Phase { Capture, Replay, Sim }

    private IrField? _ir;
    private int _lenIdx = 2;
    private int _depth = 256;
    private Phase _phase = Phase.Capture;
    private int _slice;
    private bool _fired, _haveIr;
    private bool _pendingRebuild;

    private static readonly Vector2 ImpulseAt = new(-0.25f, -0.2f);
    private const float ImpulseStrength = 0.066f;

    private float[] _history = new float[1024];
    private int _sigKind;
    private float _sigT, _phaseT;
    private float _sigInterval = 1.0f, _sigStrength = 1.0f, _replayGain = 1.0f;
    private readonly RandomNumberGenerator _rng = new();
    private Label? _cost;

    protected override (string Title, string Hint) SceneInfo => (
        "24_irlen · IR kernel length",
        "Isolated variable: how many ticks of impulse response get recorded, 64 to 1024 slices "
        + "(16 MB to 268 MB at 256²). CAPTURE fires one impulse and records; REPLAY switches the "
        + "solver OFF and rebuilds the surface as Σₖ s[t−k]·h(x,y,k). If a short kernel looks the "
        + "same as a long one, the memory freed is what makes MULTI-SOURCE affordable — 8 sources "
        + "at 64 slices costs less than one at 512. The question is whether the tail is "
        + "load-bearing.");

    protected override string StateText() => _phase switch
    {
        Phase.Capture => $"CAPTURE {_slice}/{_depth}",
        Phase.Replay => $"REPLAY — solver OFF — {_depth} slices",
        _ => "SIM — live solver",
    };

    // In REPLAY the solver must not advance at all.
    protected override bool StepSolver => _phase != Phase.Replay;

    protected override void OnSolverReady() => RebuildIr();

    private void RebuildIr()
    {
        var old = _ir;
        _ir = null;
        if (old != null) { old.Free(); }
        _ir = new IrField(RenderingServer.GetRenderingDevice(), Grid, _depth);
        _history = new float[_depth];
        _slice = 0;
        _fired = false;
        _haveIr = false;
        _phase = Phase.Capture;
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
        if (_pendingRebuild)
        {
            _pendingRebuild = false;
            RenderingServer.CallOnRenderThread(Callable.From(RebuildIr));
            return;
        }
        var ir = _ir;
        if (ir == null || !ir.Ready) { return; }

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
            return;
        }
        // Replay: shift the drive history; no drops, the solver is not running.
        float s = NextDrive(dt);
        for (int k = _history.Length - 1; k > 0; --k) { _history[k] = _history[k - 1]; }
        _history[0] = s;
    }

    protected override void AfterStep(float dt)
    {
        var ir = _ir;
        var solver = Solver;
        if (ir == null || !ir.Ready || solver == null || !solver.Ready) { return; }

        if (_phase == Phase.Capture)
        {
            int k = _slice;
            RenderingServer.CallOnRenderThread(Callable.From(() => ir.CaptureSlice(solver.DisplayRid, k)));
            if (++_slice >= _depth)
            {
                _haveIr = true;
                _phase = Phase.Replay;
                Array.Clear(_history);
            }
            return;
        }
        if (_phase == Phase.Replay && _haveIr)
        {
            var h = (float[])_history.Clone();
            float g = _replayGain;
            BindIrDisplay(ir);
            RenderingServer.CallOnRenderThread(Callable.From(() => ir.Convolve(h, g)));
        }

        if (_cost != null)
        {
            long mb = (long)Grid * Grid * _depth * 4 / (1024 * 1024);
            _cost.Text = _phase == Phase.Replay
                ? $"{_depth} slices · {mb} MB · {ir.LastTapCount} active taps"
                : $"{_depth} slices · {mb} MB";
        }
    }

    public override void _ExitTree()
    {
        var ir = _ir;
        _ir = null;
        if (ir != null) { RenderingServer.CallOnRenderThread(Callable.From(() => ir.Free())); }
        base._ExitTree();
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        _cost = ui.AddReadout("—");
        ui.AddSection("THE VARIABLE — kernel length");
        ui.AddOptions("Slices", new[] { "64", "128", "256", "512", "1024" }, 2, v =>
        {
            _lenIdx = v;
            _depth = Lengths[v];
            _pendingRebuild = true;   // rebuilding touches the GPU; do it on the next tick
        });
        ui.AddOptions("Mode", new[] { "Replay (IR)", "Sim (live solver)" }, 0,
            v => { if (v == 1) { _phase = Phase.Sim; } else if (_haveIr) { _phase = Phase.Replay; Array.Clear(_history); } });
        ui.AddToggle("Re-capture", false, v => { if (v) { _pendingRebuild = true; } });

        ui.AddSection("Drive signal");
        ui.AddOptions("Signal", new[] { "Impulse train", "Swell", "Noise" }, 0, v => { _sigKind = v; Array.Clear(_history); });
        ui.AddSlider("Interval (s)", 0.05f, 3.0f, _sigInterval, v => _sigInterval = v);
        ui.AddSlider("Strength", 0.05f, 3.0f, _sigStrength, v => _sigStrength = v);
        ui.AddSlider("Replay gain", 0.0f, 3.0f, _replayGain, v => _replayGain = v);
    }
}
