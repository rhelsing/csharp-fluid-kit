using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 253 — MEMORY (docs/artifacts-250.md Block A). Pressure hysteresis as viscoelasticity.
//
// Every Stam solver warm-starts: last frame's pressure is this frame's initial guess. With a
// truncated solve that means some of last frame's pressure SURVIVES — the solver never fully
// overwrites what it was handed. Normally that is an invisible implementation detail. Here
// it gets a dial:
//
//     p₀ ← μ · p_prev
//
//   μ = 0      cold start. The fluid forgets every frame — this is the control.
//   μ ≈ 1      the status quo (what 250 already does). Pressure persists between frames.
//   μ > 1      self-exciting: each frame re-amplifies what the last one left, and a
//              truncated solve cannot remove it fast enough. Slider stops at 1.05, and the
//              kernel rails the magnitude so the runaway stays explorable instead of
//              needing a relaunch.
//
// WHY THIS SCENE IS ALSO A TEST. docs/artifacts-log.md §1 proposes a law from 250 vs 256:
// a residual reads as a MATERIAL only if it is low-frequency and CONVECTS with the fluid;
// grid-pinned or Nyquist residuals read as noise. Memory's residual is smooth (it is a
// scaled copy of a pressure field, which is smooth by construction) and it rides inside the
// flow. So the law predicts this one WORKS. It is the cheap positive control — if memory
// reads as noise, the law is wrong.
//
//   artifact  μ on a truncated Jacobi solve    reference  converged RBGS at ω*, no memory
public partial class PressureMemory : Scene250Base
{
    private FluidSim? _fluid;
    private float _demoT;
    private Vector2 _prevMouseUv = new(-1, -1);

    private const float DragGain = 0.9f;

    // Same fluid as 250/251/256 — μ has to be the only thing that differs.
    private float _dt = 1.1863f;
    private float _drift = 0.3f;
    private float _pourAmt = 0.285f;
    private float _pourRadius = 12.26f;
    private float _pourPush = 2.43f;
    private float _stirStrength = 0.7f;
    private float _stirRadius = 9.57f;
    private float _viscosity = 0.36f;
    private int _viscIters = 20;
    private float _dissipD = 0.9957f;
    private float _dissipV = 0.999f;
    private bool _macCormack = true;
    private bool _autoDemo = true;

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    // Just past 1: pressure accumulates faster than a truncated solve removes it, so the
    // fluid pushes back against a HISTORY of being squeezed rather than against the
    // current frame — which is what viscoelastic recoil is.
    private float _mu = 1.01f;
    private int _k = 16;   // low on purpose: μ only matters while the solve is truncated

    private const int ReferenceSweeps = 200;
    private const int MaxReferenceSweeps = 200;

    private int ReferenceIters => Mathf.Min(ScaleSor(ReferenceSweeps), MaxReferenceSweeps);
    private bool ReferenceCapped => ScaleSor(ReferenceSweeps) > MaxReferenceSweeps;

    protected override string SceneTitle => "253 · memory — pressure hysteresis as viscoelasticity";

    protected override string SceneHint =>
        "The warm start, turned into an instrument. p₀ ← μ·p_prev: at μ=0 the fluid forgets "
        + "every frame, near 1 it remembers (the status quo), above 1 it re-amplifies what it "
        + "remembered and pushes back against a history of being squeezed — recoil. μ=1.0 is "
        + "exactly what 250 already does, so the interesting territory is either SIDE of 1. "
        + "K is deliberately low: memory only exists while the solve is truncated, because a "
        + "converged solve overwrites whatever it was handed. Reference is that converged "
        + "solve with memory off.";

    protected override string ArtifactName => "memory (warm-start μ)";

    protected override bool SideView => true;
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 32.6f;
    protected override float TimeScaleDefault => 0.34f;
    protected override float FieldGainDefault => 0.8052f;
    protected override float FieldGammaDefault => 0.6312f;
    protected override int GridDefault => 512;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk",
            extras: true, sor: true, warm: true);
        _fluid.MeasureResidual = true;
        if (!_fluid.WarmReady) { GD.PushError("[253] warm-scale pass failed to compile — no memory without it"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText()
    {
        string tag = ReferenceOn
            ? $"converged ω* · K {ReferenceIters}{(ReferenceCapped ? " (REFERENCE · capped)" : " (REFERENCE)")}"
            : $"μ {_mu:0.000}{(_mu > 1f ? " ⚠ self-exciting" : "")} · K {_k}";
        return $"memory · {N}² · {tag} · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
    }

    protected override void SimTick(double delta)
    {
        Vector2 pour = new(-1, -1);
        float pourAmt = 0f;
        Vector2 stirPos = new(0.5f, 0.5f);
        Vector2 stirVel = Vector2.Zero;

        var mouseUv = PlaneMouseUv();
        if (Input.IsMouseButtonPressed(MouseButton.Left) && mouseUv.X >= 0f)
        {
            pour = mouseUv;
            pourAmt = _pourAmt;
            if (_prevMouseUv.X >= 0f)
            {
                Vector2 dm = (mouseUv - _prevMouseUv) * N;
                if (dm.Length() > 0.5f)
                {
                    stirPos = mouseUv;
                    stirVel = dm.LimitLength(ScaleVel(12f)) * DragGain;
                    pourAmt = _pourAmt * 0.25f;
                }
            }
            _prevMouseUv = mouseUv;
        }
        else
        {
            _prevMouseUv = new Vector2(-1, -1);
            if (_autoDemo)
            {
                _demoT += (float)delta * TimeScale;
                float t = _demoT;
                if (Mathf.PosMod(t, 4.0f) < 1.17f)
                {
                    pour = new Vector2(0.5f + 0.16f * Mathf.Sin(t * 0.7f), 0.14f);
                    pourAmt = _pourAmt;
                }
                float sa = t * 0.9f;
                stirPos = new Vector2(0.5f + 0.22f * Mathf.Cos(sa), 0.5f + 0.22f * Mathf.Sin(sa));
                stirVel = new Vector2(-Mathf.Sin(sa), Mathf.Cos(sa)) * ScaleVel(_stirStrength);
            }
        }

        float dt = Dt;
        // THE A/B. Reference = the converged solve with memory OFF: RBGS at optimal ω, run
        // long. Artifact = truncated Jacobi carrying μ·p_prev into every frame.
        bool refOn = ReferenceOn;
        float omega = refOn ? SorOmega : 1.0f;
        int iters = refOn ? ReferenceIters : _k;

        float[] add =
        {
            N, N, dt, ScaleForce(_drift),
            pour.X * N, pour.Y * N, ScaleRadius(_pourRadius), pourAmt, ScaleVel(_pourPush),
            stirPos.X * N, stirPos.Y * N, ScaleRadius(_stirRadius), stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { N, N, dt, Fade(_dissipV) };
        // 3rd float ω (rbgs), 4th μ (warm scale) — every other projection pass pads them out
        float[] sim = { N, N, omega, _mu };
        float[] advD = { N, N, dt, _macCormack ? 1.0f : Fade(_dissipD) };
        float[] visc = { N, N, ScaleVisc(_viscosity), 0f };
        float[] mc = { N, N, dt, Fade(_dissipD) };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseSor = refOn;          // reference converges; the artifact side is Jacobi
            _fluid.UseWarmScale = !refOn;   // memory is the artifact, so the reference has none
            _fluid.Step(addB, advVB, simB, advDB, iters, viscIters, viscB, mcOn, mcB);
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
        ui.AddSlider("Viscosity iters", 4, 120, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // Hard stop at 1.05. Past roughly there the feedback outruns any affordable solve
        // and the kernel's rail is all that is holding it — the doc's "clamp".
        ui.AddSlider("μ warm-start memory (1 = status quo · >1 self-exciting)", 0.0f, 1.05f, _mu,
            v => _mu = v);
        AddCellLockedSlider(ui, "Jacobi K (memory needs truncation)", 4, 120, _k, v => _k = (int)v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
