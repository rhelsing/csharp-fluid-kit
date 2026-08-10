using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 258 — RING (docs/artifacts-250.md Block B). The first scene off the fluid: a wave
// plate, where the artifact is the scheme's NUMERICAL DISSIPATION and the material it makes
// is a reverb tail.
//
//     cn = 0   backward-Euler. Unconditionally stable because it throws energy away every
//              step. Pluck it and the ring dies almost immediately — a dead room.
//     cn = 1   Crank-Nicolson. Symmetric in past and future, so it dissipates NOTHING and
//              still cannot blow up. Pluck it and the plate rings for as long as the
//              physical damping allows — a live room.
//
// This knob already existed, buried in `stamp_wave.glslinc` as a stability option and
// surfaced in scene 03 as "Scheme BE→CN". Block B's whole contribution is to stop treating
// it as a quality setting and name it as what it is: a decay-time control indistinguishable
// in kind from a reverb's.
//
// THE ARTIFACT VIEW IS DIFFERENT HERE, and the difference is instructive. Every Block A
// scene renders a RESIDUAL — the projection failed to remove some divergence, and there it
// is. A dissipative scheme leaves no residual at all; it leaves a shorter tail. There is
// nothing to point at, only something MISSING. So the readout is |h − h_prev| (via
// FieldProbe) — the ringing energy itself. Bright where the plate is still moving, dark
// where backward-Euler ate it.
//
// And so the reference has to be honest about its own limits: there is no exact answer for
// a damped wave to check against, and no "converged" solve to run — cn is not a truncation,
// it is a different equation. The A/B is therefore cn against cn = 1 (the non-dissipative
// end), which is the closest thing to "no scheme error" available. Weaker than 259's
// analytic ring or 257's exact projection, and labelled as such.
public partial class Ring : Scene250Base
{
    private GpuStampSolver? _solver;
    private FieldProbe? _energy;
    private float _demoT;
    private int _plucks;

    private static readonly string StampPath = "res://shaders/stamp/stamp_wave.glslinc";

    // medium
    private float _speed = 0.45f;      // cells/tick
    private float _dt = 1.0f;
    private float _damping = 0.004f;   // physical loss, distinct from the scheme's
    private float _leak = 0.002f;      // restoring pull to rest — kills DC drift
    private int _iters = 24;
    private bool _autoDemo = true;
    private float _pluckEvery = 3.0f;  // seconds of sim time
    private float _pluckRadius = 6.0f;
    private float _pluckStrength = 1.4f;
    private float _energyGain = 6.0f;

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    private float _cn = 0.35f;   // partway: audibly damped, still ringing

    protected override string SceneTitle => "258 · ring — scheme dissipation as reverb tail";

    protected override string SceneHint =>
        "A plate, plucked. cn=0 is backward-Euler: stable because it discards energy every "
        + "step, so the tail dies at once — a dead room. cn=1 is Crank-Nicolson: symmetric "
        + "in past and future, dissipates nothing, still unconditionally stable — a live "
        + "room. Same stability guarantee, opposite decay time, and the difference is purely "
        + "the discretisation. The artifact view shows |h−h_prev|, the ringing energy: a "
        + "dissipative scheme leaves nothing BEHIND to look at, only something missing. "
        + "Reference is cn=1, the closest thing to no scheme error — not an exact answer.";

    protected override string ArtifactName => "ring (θ-scheme cn)";

    // A height field, so unlike every Block A scene this is TOP-DOWN and wants the lit
    // relief mode — the base's SideView exists for exactly this split.
    protected override bool SideView => false;
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 8.0f;
    protected override float TimeScaleDefault => 1.0f;
    protected override int[] GridOptions => new[] { 256, 512 };
    protected override int GridDefault => 256;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _solver?.HeightRid ?? default;
    protected override Rid ArtifactRid => _energy?.Rid ?? default;

    protected override void BuildSim()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _solver = new GpuStampSolver(rd, Grid, StampPath, GpuStampSolver.Mode.Rbgs);
        if (!_solver.Ready) { GD.PushError("[258] stamp solver failed to init"); return; }
        _energy = new FieldProbe(rd, Grid, "stamp_energy", _solver.HeightRid, _solver.PrevRid);
        if (!_energy.Ready) { GD.PushError("[258] energy probe failed to compile"); }
    }

    protected override void FreeSim()
    {
        _energy?.Free();
        _energy = null;
        _solver?.Free();
        _solver = null;
    }

    protected override string ReadoutText()
    {
        float cn = ReferenceOn ? 1.0f : _cn;
        return $"ring · {N}² · cn {cn:0.00}"
            + $"{(ReferenceOn ? " (REFERENCE — no scheme dissipation)" : cn < 0.05f ? " (backward-Euler)" : "")}"
            + $" · plucks {_plucks} · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
    }

    protected override void SimTick(double delta)
    {
        // Poke: left click, or the auto-demo on SIM time. An IMPULSE, never held — scene 52's
        // lesson: a ringing medium under stationary periodic forcing accumulates resonantly
        // and the tail you are trying to judge is drowned by the driver.
        Vector4 poke = Vector4.Zero;
        var uv = PlaneMouseUv();
        if (Input.IsMouseButtonPressed(MouseButton.Left) && uv.X >= 0f)
        {
            poke = new Vector4(uv.X * N, uv.Y * N, ScaleRadius(_pluckRadius), _pluckStrength);
            _plucks++;
        }
        else if (_autoDemo)
        {
            float prev = _demoT;
            _demoT += (float)delta * TimeScale;
            if (Mathf.Floor(_demoT / _pluckEvery) != Mathf.Floor(prev / _pluckEvery))
            {
                // wander the strike point so it excites different modes each time
                float ang = _demoT * 1.7f;
                poke = new Vector4(
                    N * (0.5f + 0.26f * Mathf.Cos(ang)),
                    N * (0.5f + 0.26f * Mathf.Sin(ang * 0.83f)),
                    ScaleRadius(_pluckRadius), _pluckStrength);
                _plucks++;
            }
        }

        float dt = Dt;
        float c = ScaleVel(_speed);
        float beta = c * dt * c * dt;      // dt²·c² — the neighbour conductance
        float a = _damping * dt * 0.5f;
        float cn = ReferenceOn ? 1.0f : _cn;

        float[] pc = { N, N, beta, a, _leak, poke.X, poke.Y, poke.Z, poke.W, cn, 0f, 0f };
        byte[] pcB = ToBytes(pc);
        float[] ep = { N, N, _energyGain, 0f };
        byte[] epB = ToBytes(ep);
        int iters = _iters;

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _solver?.Step(pcB, iters);
            _energy?.Run(epB);
        }));
    }

    protected override void BuildSimKnobs(DemoUI ui)
    {
        ui.AddToggle("Auto-demo (periodic pluck)", _autoDemo, on => _autoDemo = on);
        ui.AddSlider("Wave speed (cells/tick)", 0.05f, 0.9f, _speed, v => _speed = v);
        ui.AddSlider("Damping (physical loss)", 0.0f, 0.08f, _damping, v => _damping = v);
        ui.AddSlider("Rest leak κ (kills DC drift)", 0.0f, 0.05f, _leak, v => _leak = v);
        ui.AddSlider("Solver iters", 4, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Pluck interval (s)", 0.5f, 8.0f, _pluckEvery, v => _pluckEvery = v);
        ui.AddSlider("Pluck radius", 2.0f, 20.0f, _pluckRadius, v => _pluckRadius = v);
        ui.AddSlider("Pluck strength", 0.1f, 4.0f, _pluckStrength, v => _pluckStrength = v);
        ui.AddSlider("Energy readout gain", 0.5f, 40.0f, _energyGain, v => _energyGain = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // The whole scene. 0 kills the tail, 1 lets it ring; everything between is a
        // decay time, chosen rather than inherited.
        ui.AddSlider("cn  (0 = backward-Euler dead · 1 = Crank-Nicolson live)", 0.0f, 1.0f,
            _cn, v => _cn = v);
        ui.AddSlider("dt (bigger step = coarser in time)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    protected override void BuildRenderKnobs(DemoUI ui)
    {
        // Height fields want the lit relief mode, not ink-on-paper.
        Mat.SetShaderParameter("mode", 2);
        Mat.SetShaderParameter("field_gain", 2.2f);
        ui.AddSlider("Field gain (height)", 0.2f, 8.0f, 2.2f, v => Mat.SetShaderParameter("field_gain", v));
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
