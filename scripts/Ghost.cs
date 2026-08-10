using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 260 — GHOST (docs/artifacts-250.md Block B). Advection error: what the scheme
// quietly REMOVES rather than what it leaves behind.
//
// Semi-Lagrangian advection is unconditionally stable because it interpolates — and every
// interpolation is a small low-pass filter. Applied once a frame for a few hundred frames,
// that filter eats the field: filaments thicken, contrast fades, structure dissolves into
// haze. Nothing has gone wrong, no residual has accumulated, and the fluid still obeys its
// equations. It has simply forgotten its own detail. MacCormack corrects the round-trip
// error and keeps the filaments — at the cost of a limiter that CHATTERS where the
// correction wants to overshoot.
//
// THE TEST IS SOLID-BODY ROTATION, and that is not decoration. A field spun rigidly about
// its centre has an exact answer: after a full revolution it must be IDENTICAL to where it
// started. Any difference is scheme error and nothing else — no pressure, no buoyancy, no
// boundary effects to argue about. So this scene forces the velocity field to a rigid
// rotation instead of solving for it, which turns "does the dye look mushy?" into "how far
// from its own initial condition has it drifted after N turns?".
//
// The artifact view is |∇dye| (`stamp_grad.glslinc` via FieldProbe), because the quantity
// that decays IS the gradient: bright means the field still has edges, dim means the scheme
// has smoothed them away. Like 258, there is no residual to point at — only something
// missing — and this is the readout that makes the absence visible.
//
//   artifact  semi-Lagrangian smearing     reference  MacCormack (second-order)
public partial class Ghost : Scene250Base
{
    private FluidSim? _fluid;
    private FieldProbe? _grad;
    private float _spin = 0.35f;      // revolutions per second of sim time
    private float _gradGain = 18.0f;
    private int _turns;
    private bool _seeded;
    private float _demoT;   // accumulated SIM time — see SimTick
    private Vector2 _prevMouseUv = new(-1, -1);

    // A mouse drag's dm is already a velocity in cells/tick (it carries N through the
    // ×N below), so this gain is dimensionless and takes no similarity row. 0.9 is
    // scene 18's 3.0 × 0.3, preserved exactly. The stir-strength slider is a velocity
    // and drives the auto-demo only — mixing the two would double-scale the drag.
    private const float DragGain = 0.9f;

    // ── defaults: Ryan's tuned Copy-values from the first live run (house rule). They
    // started as scene 18's 256² numbers carried through the similarity table, and every
    // one of these is in 512-REFERENCED world units — the Scale* helpers re-derive cell
    // units from the grid, so these stay correct if the dropdown moves.
    private float _dt = 1.1863f;
    private float _drift = 0.3f;           // tuned positive: the dye RISES here
    private float _pourAmt = 0.285f;
    private float _pourRadius = 12.26f;
    private float _pourPush = 2.43f;
    private float _stirStrength = 0.7f;    // much gentler than 18's — the stir was drowning it
    private float _stirRadius = 9.57f;
    private float _viscosity = 0.36f;
    private float _dissipD = 0.9957f;      // per-tick fades — routed through Fade()
    private float _dissipV = 0.999f;       // (not on the panel; 18's value)
    private bool _macCormack = true;       // dimensionless, unchanged
    private bool _autoDemo = true;

    // Viscosity Jacobi iterations do NOT take the K ∝ N² row. That row is for the
    // PRESSURE Poisson solve, whose slowest modes are domain-sized. This system is
    // (I − a∇²) with a finite a — diagonally dominant, spectral radius 4a/(1+4a) < 1
    // independent of N, so a fixed sweep count converges the same at every grid.
    private int _viscIters = 33;

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    private int _k = 132;                  // truncated Jacobi — the dial
    private const int ReferenceK = 400;    // the accurate solve, at the reference grid

    // HARD CEILING on the reference solve. The similarity table says the reference should
    // take K ∝ N², which at 1024² is 1600 sweeps a frame — that is not an A/B, that is a
    // freeze. Capped, the toggle stays instant at every grid.
    private const int MaxReferenceIters = 400;

    private int ReferenceIters => Mathf.Min(ScaleJacobi(ReferenceK), MaxReferenceIters);

    // When the cap bites, the reference is NOT matched-convergence any more, and part of
    // what you see in the A/B is the cap rather than the material. Never let that be
    // silent — the readout says so.
    private bool ReferenceCapped => ScaleJacobi(ReferenceK) > MaxReferenceIters;

    protected override string SceneTitle => "260 · ghost — advection smearing and limiter chatter";

    protected override string SceneHint =>
        "Solid-body rotation, which has an exact answer: after one full turn the dye must be "
        + "identical to how it started, so every difference is the advection scheme and "
        + "nothing else. Semi-Lagrangian is stable because it interpolates, and interpolation "
        + "is a low-pass — run it a few hundred frames and the filaments dissolve into haze "
        + "without anything having gone wrong. Toggle Reference for MacCormack, which keeps "
        + "the detail but whose limiter chatters where it wants to overshoot. Artifact view "
        + "is |∇dye|: the gradient is the thing being eaten, so bright = still sharp.";

    protected override string ArtifactName => "ghost (SL vs MacCormack)";

    // ── baked display/panel state, same tuned run ────────────────────────────────
    // The artifact view is OFF at open: at intensity 32.6 it reads as a wash over the
    // dye, so the scene opens on the material and you toggle to the residual. The
    // intensity is baked so that toggle is immediately legible.
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 32.6f;
    protected override float TimeScaleDefault => 0.34f;
    protected override float FieldGainDefault => 0.8052f;
    protected override float FieldGammaDefault => 0.6312f;

    // Tuned at 1024², so that is what it opens at — NOTE this departs from the base
    // doc's "default 512²". Flagged, not silent; one line to revert.
    protected override int GridDefault => 1024;

    // A VERTICAL SLICE, seen from the side. fs_add_milk's drift term (v.y += dt·drift·d)
    // is buoyancy in the plane, so the only orientation that makes it mean anything is
    // the one where the sim's y axis is world up. Rendering this top-down — which is what
    // scene 18 does, cup rim and all — turns buoyancy into an arbitrary sideways bias.
    // It also makes 250b a real lift instead of a reinterpretation: same up, one more axis.
    protected override bool SideView => true;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    // NOT DivRid: solid-body rotation is divergence-free by construction, so the usual
    // residual view would be blank. What decays here is the gradient.
    protected override Rid ArtifactRid => _grad?.Rid ?? default;

    // The base multiplies this by TIME scale to get Dt — dt stays a sim knob on top.
    protected override float BaseDt => _dt;

    protected override void BuildSim()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _fluid = new FluidSim(rd, Grid, "fs_add_milk", extras: true, rotation: true);
        // Without this, DivRid holds the divergence the solve was HANDED, which barely
        // moves with K. With it, the pass re-runs after the gradient subtract and DivRid
        // holds what the truncation left behind — the artifact.
        _grad = new FieldProbe(rd, Grid, "stamp_grad", _fluid.DyeRid, default);
        if (!_grad.Ready) { GD.PushError("[260] gradient probe failed to compile"); }
        _seeded = false;
        _demoT = 0f;
    }

    protected override void FreeSim()
    {
        _grad?.Free();
        _grad = null;
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText() =>
        $"ghost · {N}² · {(ReferenceOn ? "MacCormack (REFERENCE)" : "semi-Lagrangian")}"
        + $" · {_turns} full turns — the field should be identical to turn 0"
        + $" · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";

    protected override void SimTick(double delta)
    {
        float dt = Dt;
        _demoT += (float)delta * TimeScale;
        _turns = (int)(_demoT * _spin);

        // The test pattern is laid down ONCE and then only ever rotated. Re-injecting every
        // frame would hide the very thing being measured — fresh dye is always sharp.
        bool seed = !_seeded && _demoT > 0.15f;
        float[] add =
        {
            N, N, dt, 0f,
            N * 0.30f, N * 0.5f, ScaleRadius(_pourRadius), 1.6f, 0f,
            0f, 0f, 0f, 0f, 0f,
            0f, 0f,
        };
        float[] add2 =
        {
            N, N, dt, 0f,
            N * 0.5f, N * 0.74f, ScaleRadius(_pourRadius * 0.55f), 1.6f, 0f,
            0f, 0f, 0f, 0f, 0f,
            0f, 0f,
        };

        // ω in radians per TICK: _spin is revolutions per second of sim time, physics is 60 Hz.
        float omega = _spin * Mathf.Tau / 60.0f;
        float[] rot = { N, N, omega, 0f };
        float[] advD = { N, N, dt, 1.0f };   // dissipation pinned at 1 — only the SCHEME may remove dye
        float[] mc = { N, N, dt, 1.0f };
        float[] gp = { N, N, _gradGain, 0f };

        byte[] addB = ToBytes(add), add2B = ToBytes(add2);
        byte[] rotB = ToBytes(rot), advDB = ToBytes(advD), mcB = ToBytes(mc), gpB = ToBytes(gp);
        bool mcOn = ReferenceOn;   // THE A/B: the reference is the second-order scheme
        if (seed) { _seeded = true; }

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            if (seed) { _fluid.Seed(addB); _fluid.Seed(add2B); }
            _fluid.StepAdvectOnly(rotB, advDB, mcOn, mcB);
            _grad?.Run(gpB);
        }));
    }

    protected override void BuildSimKnobs(DemoUI ui)
    {
        ui.AddToggle("Auto-demo (pour + spiral stir)", _autoDemo, on => _autoDemo = on);
        ui.AddToggle("MacCormack advection (sharp filaments)", _macCormack, on => _macCormack = on);
        ui.AddSlider("Viscosity ν·dt (0 = watery)", 0.0f, 3.0f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Density drift (− sinks, + rises)", -6.0f, 6.0f, _drift, v => _drift = v);
        ui.AddSlider("Pour amount", 0.0f, 1.5f, _pourAmt, v => _pourAmt = v);
        ui.AddSlider("Pour radius", 4.0f, 32.0f, _pourRadius, v => _pourRadius = v);
        ui.AddSlider("Pour splash (radial)", 0.0f, 6.0f, _pourPush, v => _pourPush = v);
        ui.AddSlider("Stir strength (auto-demo)", 0.0f, 20.0f, _stirStrength, v => _stirStrength = v);
        ui.AddSlider("Stir radius", 6.0f, 48.0f, _stirRadius, v => _stirRadius = v);
        ui.AddSlider("Viscosity iters", 4, 40, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        ui.AddSlider("Spin (revolutions per second of sim time)", 0.0f, 1.2f, _spin, v => _spin = v);
        ui.AddSlider("dt (bigger step = a coarser interpolation each tick)", 0.25f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Gradient readout gain", 0.5f, 60.0f, _gradGain, v => _gradGain = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
