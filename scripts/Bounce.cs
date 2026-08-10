using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 251 — BOUNCE (docs/artifacts-250.md Block A). The first true formula swap into the
// 250 template: same fluid, same panel, same palette, same artifact view. One thing differs
// — how the pressure solve steps — and that one thing is a different material.
//
// 250 truncates Jacobi and gets COMPRESSIBILITY: the fluid squashes because the projection
// never finished. 251 finishes about as much work, but each update OVERSHOOTS and is pulled
// back, so what is left behind is not a deficit — it is a spatially correlated over/under
// pattern. That reads as ELASTICITY: the fluid rings instead of squashing.
//
//     p_GS = (Σ₄ p_nb − div) / 4      p = p_old + ω (p_GS − p_old)
//
//   ω < 1    under-relaxed: a PILLOW. Pushes are absorbed and settle.
//   ω = 1    plain Gauss-Seidel — the honest, ~2×-Jacobi solver.
//   1 < ω < 2  over-relaxed: RINGING. The dial the scene exists for.
//   ω ≥ 2    divergent; the slider stops at 1.95.
//
// Note the reference here is genuinely better than 250's. 250 could only offer "more
// Jacobi" (capped, still under-converged). SOR at its optimal ω needs K ∝ N instead of
// K ∝ N², so the accurate side is affordable — Scene250Base.SorOmega and ScaleSor were
// written for exactly this and this is the first scene to use them.
//
// The same kernel is scene 256 plaid: ω = 1 with very few sweeps leaves the checkerboard
// the red-black split imprints. 256 is a preset here, not a second build.
public partial class Bounce : Scene250Base
{
    private FluidSim? _fluid;
    private float _demoT;
    private Vector2 _prevMouseUv = new(-1, -1);

    private const float DragGain = 0.9f;   // dm already carries N — dimensionless (see 250)

    // Fluid defaults inherited from 250's tuned run: this is deliberately the SAME fluid,
    // so that flipping between the two scenes shows the SOLVER's character and nothing else.
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
    private float _omega = 1.62f;   // over-relaxed: ringing, not squashing
    private int _k = 24;            // full red+black sweeps

    // The accurate side: optimal ω for this grid, run long. Capped for the same reason 250's
    // is — a reference you cannot afford to toggle is not a reference (learned the hard way).
    private const int ReferenceSweeps = 200;
    private const int MaxReferenceSweeps = 200;

    private int ReferenceIters => Mathf.Min(ScaleSor(ReferenceSweeps), MaxReferenceSweeps);
    private bool ReferenceCapped => ScaleSor(ReferenceSweeps) > MaxReferenceSweeps;

    protected override string SceneTitle => "251 · bounce — over-relaxation overshoot as elasticity";

    protected override string SceneHint =>
        "250's fluid, one solver step different. Red-black Gauss-Seidel with over-relaxation: "
        + "ω below 1 under-relaxes into a pillow that absorbs a push; above 1 every update "
        + "overshoots and is pulled back, leaving a correlated over/under pattern that reads "
        + "as ringing. Reference is optimal-ω SOR run long — a genuinely converged solve, "
        + "unlike 250's 'more Jacobi'. Artifact view shows the leftover |∇·v|: compare its "
        + "TEXTURE against 250's, not its amount. Same magnitude, different grain.";

    protected override string ArtifactName => "bounce (SOR ω)";

    protected override bool SideView => true;          // same vertical slice as 250
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 32.6f;   // 250/250b both landed near 33
    protected override float TimeScaleDefault => 0.34f;
    protected override float FieldGainDefault => 0.8052f;
    protected override float FieldGammaDefault => 0.6312f;

    // 512² — the base doc's default. 250 sits at 1024 because that is where it was tuned;
    // this one starts at spec until it has been tuned live.
    protected override int GridDefault => 512;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk",
            extras: true, sor: true);
        _fluid.MeasureResidual = true;   // DivRid = what the solve left, not what it was given
        _fluid.UseSor = true;            // this scene IS the SOR path
        if (!_fluid.SorReady) { GD.PushError("[251] SOR path failed to compile — falling back to Jacobi"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText()
    {
        string tag = ReferenceOn
            ? $"ω* {SorOmega:0.000} · K {ReferenceIters}{(ReferenceCapped ? " (REFERENCE · capped)" : " (REFERENCE)")}"
            : $"ω {_omega:0.00} · K {_k}";
        return $"bounce · {N}² · {tag} · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
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
                    // sourced low: buoyant dye climbs, and a rising plume is where an
                    // over-relaxed projection rings most visibly
                    pour = new Vector2(0.5f + 0.16f * Mathf.Sin(t * 0.7f), 0.14f);
                    pourAmt = _pourAmt;
                }
                float sa = t * 0.9f;
                stirPos = new Vector2(0.5f + 0.22f * Mathf.Cos(sa), 0.5f + 0.22f * Mathf.Sin(sa));
                stirVel = new Vector2(-Mathf.Sin(sa), Mathf.Cos(sa)) * ScaleVel(_stirStrength);
            }
        }

        float dt = Dt;
        // THE A/B: ω and the sweep count. Everything else about the solve is identical.
        float omega = ReferenceOn ? SorOmega : _omega;
        int iters = ReferenceOn ? ReferenceIters : _k;

        float[] add =
        {
            N, N, dt, ScaleForce(_drift),
            pour.X * N, pour.Y * N, ScaleRadius(_pourRadius), pourAmt, ScaleVel(_pourPush),
            stirPos.X * N, stirPos.Y * N, ScaleRadius(_stirRadius), stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { N, N, dt, Fade(_dissipV) };
        // third float is ω — fs_divergence / fs_gradient_sub declare it as pad and ignore it
        float[] sim = { N, N, omega, 0f };
        float[] advD = { N, N, dt, _macCormack ? 1.0f : Fade(_dissipD) };
        float[] visc = { N, N, ScaleVisc(_viscosity), 0f };
        float[] mc = { N, N, dt, Fade(_dissipD) };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _fluid?.Step(addB, advVB, simB, advDB, iters, viscIters, viscB, mcOn, mcB)));
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
        // 1.0 = honest Gauss-Seidel. Below = pillow, above = ring. 2.0 diverges.
        ui.AddSlider("ω over-relaxation (<1 pillow · >1 ring)", 0.3f, 1.95f, _omega, v => _omega = v);
        AddCellLockedSlider(ui, "Sweeps K (red+black)", 2, 120, _k, v => _k = (int)v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
