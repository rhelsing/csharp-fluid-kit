using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 259 — SMEAR (docs/artifacts-250.md Block B). Numerical DISPERSION: the discrete
// wave equation propagates different wavelengths at slightly different speeds, so a sharp
// impulse does not stay a clean expanding ring — it develops a trailing wake and the front
// arrives late.
//
// THIS SCENE HAS THE ONLY EXACT REFERENCE IN EITHER PLAN, and it is worth being precise
// about why that is rare. Every other A/B in the series compares a cheap solve against a
// better solve: 250 against more Jacobi, 251 against optimal-ω SOR, 250b against a
// multigrid V-cycle, 257 against its own exact projection. All of those are references
// because we believe them, not because we can derive them.
//
// Here the answer is a closed form. A single impulse on a uniform medium expands as a
// circular front of radius
//
//     r = c · t
//
// exactly, for all time. So the scene draws that circle — in the artifact colour, straight
// on top of the simulated field — and the GAP between the drawn ring and the computed
// wavefront IS the discretisation error, measured rather than inferred. Nothing to converge,
// nothing to cap, nothing to take on trust.
//
// The dial is SUBSTEPS. More substeps = smaller dt per solve = less dispersion = the front
// creeps back onto the analytic ring. So the artifact knob and the accuracy knob are the
// same knob, which is unusual in this series and makes the error unusually legible: you can
// watch the ring and the front converge.
//
// Single impulse ONLY, never periodic forcing — scene 52's lesson, and here it is not just
// about resonance: a second wavefront would make "which front should sit on the ring?"
// ambiguous and destroy the measurement.
public partial class Smear : Scene250Base
{
    private GpuStampSolver? _solver;
    private FieldProbe? _energy;
    private int _plucks;

    private static readonly string StampPath = "res://shaders/stamp/stamp_wave.glslinc";

    // medium
    private float _speed = 0.45f;      // cells/tick
    private float _dt = 1.0f;
    private float _damping = 0.004f;   // physical loss, distinct from the scheme's
    private float _leak = 0.002f;      // restoring pull to rest — kills DC drift
    private int _iters = 24;
    private float _pluckRadius = 6.0f;
    private float _pluckStrength = 1.4f;
    private float _energyGain = 6.0f;

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    private int _substeps = 1;    // 1 = maximum dispersion; more = the front finds the ring
    private float _cn = 1.0f;     // CN throughout: dissipation is 258's artifact, not this one

    private float _shotT;         // sim-seconds since the impulse — drives the analytic radius
    private bool _fired;

    protected override string SceneTitle => "259 · smear — dispersion, against r = c·t";

    protected override string SceneHint =>
        "One impulse, one expanding ring, and the EXACT answer drawn on top of it: a wave "
        + "front on a uniform medium sits at r = c·t for all time, so the circle in artifact "
        + "colour is not a guide — it is the right answer. The gap between it and the "
        + "simulated front is the dispersion error, measured. Raise SUBSTEPS and watch the "
        + "front climb back onto the ring: here the artifact dial and the accuracy dial are "
        + "the same knob. Space-bar or click re-fires the impulse; the clock restarts with it.";

    protected override string ArtifactName => "smear (dispersion · substeps)";

    // A height field, so unlike every Block A scene this is TOP-DOWN and wants the lit
    // relief mode — the base's SideView exists for exactly this split.
    protected override bool SideView => false;
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 4.0f;
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
        float c = ScaleVel(_speed);
        float rCells = c * _shotT * 60.0f;
        return $"smear · {N}² · substeps {_substeps} · analytic r = c·t = {rCells:0.0} cells"
            + $"{(ReferenceOn ? " · REFERENCE (ring hidden — judge the front alone)" : "")}"
            + $" · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
    }

    protected override void SimTick(double delta)
    {
        float dt = Dt / Mathf.Max(1, _substeps);
        float c = ScaleVel(_speed);

        // Fire one impulse at the centre; click or space re-fires and restarts the clock.
        Vector4 poke = Vector4.Zero;
        bool refire = Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsKeyPressed(Key.Space);
        if (!_fired || (refire && _shotT > 0.35f))
        {
            poke = new Vector4(N * 0.5f, N * 0.5f, ScaleRadius(_pluckRadius), _pluckStrength);
            _shotT = 0f;
            _fired = true;
            _plucks++;
        }
        else
        {
            // The analytic clock counts SUBSTEPS, not frames — that is what "t" means to the
            // scheme. Miscount it and the ring drifts off for a reason that has nothing to
            // do with dispersion, which would quietly discredit the whole measurement.
            _shotT += (float)delta * TimeScale;
        }

        // r = c·t in cells → UV. c is cells/tick and one tick is dt of sim time, so the
        // front has advanced c · (elapsed ticks) = c · (_shotT · 60) cells at 60 Hz physics.
        float rCells = c * _shotT * 60.0f;
        float rUv = Mathf.Clamp(rCells / N, 0f, 0.71f);
        Mat.SetShaderParameter("ring_radius", ReferenceOn ? 0f : rUv);

        float beta = c * dt * c * dt;
        float a = _damping * dt * 0.5f;
        float cn = _cn;

        float[] pc = { N, N, beta, a, _leak, poke.X, poke.Y, poke.Z, poke.W, cn, 0f, 0f };
        float[] pcQuiet = { N, N, beta, a, _leak, 0f, 0f, 0f, 0f, cn, 0f, 0f };
        byte[] pcB = ToBytes(pc), pcQ = ToBytes(pcQuiet);
        float[] ep = { N, N, _energyGain, 0f };
        byte[] epB = ToBytes(ep);
        int iters = _iters;
        int sub = Mathf.Max(1, _substeps);

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_solver == null) { return; }
            // The impulse is injected ONCE, on the first substep — repeating it per substep
            // would scale the strike with the substep count and make the A/B meaningless.
            for (int i = 0; i < sub; i++) { _solver.Step(i == 0 ? pcB : pcQ, iters); }
            _energy?.Run(epB);
        }));
    }

    protected override void BuildSimKnobs(DemoUI ui)
    {
        ui.AddSlider("Wave speed (cells/tick)", 0.05f, 0.9f, _speed, v => _speed = v);
        ui.AddSlider("Damping (physical loss)", 0.0f, 0.08f, _damping, v => _damping = v);
        ui.AddSlider("Rest leak κ (kills DC drift)", 0.0f, 0.05f, _leak, v => _leak = v);
        ui.AddSlider("Solver iters", 4, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Impulse radius (smaller = broader spectrum = more smear)", 2.0f, 20.0f, _pluckRadius, v => _pluckRadius = v);
        ui.AddSlider("Impulse strength", 0.1f, 4.0f, _pluckStrength, v => _pluckStrength = v);
        ui.AddSlider("Energy readout gain", 0.5f, 40.0f, _energyGain, v => _energyGain = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // The whole scene. 0 kills the tail, 1 lets it ring; everything between is a
        // decay time, chosen rather than inherited.
        // Same knob twice over: more substeps is both less artifact and more accuracy.
        AddCellLockedSlider(ui, "Substeps (1 = maximum smear)", 1, 8, _substeps, v => _substeps = (int)v);
        ui.AddSlider("dt (bigger step = more dispersion)", 0.25f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("cn (kept at 1 — dissipation is 258's artifact)", 0.0f, 1.0f, _cn, v => _cn = v);
    }

    protected override void BuildRenderKnobs(DemoUI ui)
    {
        // Height fields want the lit relief mode, not ink-on-paper.
        // Signed two-tone, not lit relief: a dispersive wake is a small OSCILLATION behind
        // the front, and plus/minus around paper shows its sign structure where shading
        // would just show bumps.
        Mat.SetShaderParameter("mode", 1);
        Mat.SetShaderParameter("field_gain", 3.0f);
        ui.AddSlider("Field gain", 0.2f, 12.0f, 3.0f, v => Mat.SetShaderParameter("field_gain", v));
        ui.AddSlider("Analytic ring width", 0.001f, 0.02f, 0.0035f,
            v => Mat.SetShaderParameter("ring_width", v));
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
