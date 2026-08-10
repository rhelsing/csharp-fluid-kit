using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 255 — GRAIN (docs/artifacts-250.md Block A). Directional residual as ANISOTROPIC
// STIFFNESS: a material that is harder to compress one way than the other, produced by
// nothing but the order in which the solver visited the grid.
//
// ADI (`fs_pressure_adi.glslinc`) solves every LINE along one axis exactly — a tridiagonal
// Thomas solve, so information crosses the entire domain in a single pass — while the
// cross-axis neighbours are held at their previous values and do not move at all. Alternate
// x and y and the two directions converge at wildly different rates; truncate the
// alternation and that imbalance is frozen into the residual as stripes along whichever
// axis was swept last.
//
// ⚠ THIS SCENE IS A FALSIFICATION TEST, NOT A BID FOR A NICE MATERIAL.
// docs/artifacts-log.md §1 derived a law from 250 (loved) against 256 (rejected): a residual
// reads as a material only if it is LOW-FREQUENCY and CONVECTS with the fluid; grid-aligned
// residuals welded to the lattice read as noise. Sweep-line stripes are grid-aligned and do
// not convect — the same shape as plaid. **The law predicts this scene fails.**
//
// So there are two good outcomes and no bad one:
//   · it looks like noise  → the law survives a real attempt to break it, and the series
//                            gains a predictive rule instead of two anecdotes.
//   · it looks like a material → the law is WRONG, which is worth more than a tenth scene
//                            that works, because everything downstream was leaning on it.
//
// One honest asymmetry to watch for: unlike plaid's two-cell checkerboard, grain's stripes
// are DOMAIN-scale in one direction. If the law is going to fail anywhere, it is here — the
// residual is low-frequency along the sweep even though it is lattice-locked across it.
//
//   artifact  alternations, truncated     reference  converged RBGS at ω*
public partial class Grain : Scene250Base
{
    private FluidSim? _fluid;
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
    private int _k = 1;                    // ADI alternations — the dial
    private const int ReferenceK = 200;    // converged RBGS sweeps at optimal ω

    private const int MaxReferenceIters = 200;

    private int ReferenceIters => Mathf.Min(ScaleSor(ReferenceK), MaxReferenceIters);

    // When the cap bites, the reference is NOT matched-convergence any more, and part of
    // what you see in the A/B is the cap rather than the material. Never let that be
    // silent — the readout says so.
    private bool ReferenceCapped => ScaleSor(ReferenceK) > MaxReferenceIters;

    protected override string SceneTitle => "255 · grain — directional residual as anisotropic stiffness";

    protected override string SceneHint =>
        "ADI: each line along one axis is solved EXACTLY (tridiagonal), the cross-axis "
        + "neighbours are frozen, then the axes swap. Truncate the alternation and the two "
        + "directions are left at different convergence — the fluid is stiffer one way than "
        + "the other, purely from the order the solver visited things. BUILT TO TEST A "
        + "CLAIM: the log's §1 law says grid-aligned residuals that don't convect read as "
        + "noise, which predicts this scene FAILS the way 256 plaid did. If it reads as a "
        + "real material instead, the law is wrong — and that is the more useful result. "
        + "Look along the stripes, not at them.";

    protected override string ArtifactName => "grain (ADI alternations)";

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
    protected override int GridDefault => 512;

    // A vertical slice, as everywhere in Block A. fs_add_milk's drift term (v.y += dt·drift·d)
    // is buoyancy in the plane, so the only orientation that makes it mean anything is
    // the one where the sim's y axis is world up. Rendering this top-down — which is what
    // scene 18 does, cup rim and all — turns buoyancy into an arbitrary sideways bias.
    // It also makes 250b a real lift instead of a reinterpretation: same up, one more axis.
    protected override bool SideView => true;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    // The base multiplies this by TIME scale to get Dt — dt stays a sim knob on top.
    protected override float BaseDt => _dt;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk",
            extras: true, sor: true, adi: true);
        // Without this, DivRid holds the divergence the solve was HANDED, which barely
        // moves with K. With it, the pass re-runs after the gradient subtract and DivRid
        // holds what the truncation left behind — the artifact.
        _fluid.MeasureResidual = true;
        if (!_fluid.AdiReady) { GD.PushError("[255] ADI path failed to compile — no grain without it"); }
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
            : $"{_k} alternation{(_k == 1 ? "" : "s")} (x then y)";
        return $"grain · {N}² · {tag} · t×{TimeScale:0.00} "
            + $"· ν·dt {ScaleVisc(_viscosity):0.00} cells² · {Engine.GetFramesPerSecond():0}fps";
    }

    protected override void SimTick(double delta)
    {
        // sources this tick: pour (amt > 0) and stir (vx/vy != 0)
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
                    pourAmt = _pourAmt * 0.25f;   // a drag mostly stirs, lightly pours
                }
            }
            _prevMouseUv = mouseUv;
        }
        else
        {
            _prevMouseUv = new Vector2(-1, -1);
            if (_autoDemo)
            {
                // The demo runs on SIM time, not wall time: accumulated (not t×scale on a
                // tick count, which jumps the phase whenever the slider moves), so slow-mo
                // slows the pour cadence and the stir together with the fluid.
                _demoT += (float)delta * TimeScale;
                float t = _demoT;
                if (Mathf.PosMod(t, 4.0f) < 1.17f)   // scene 18's 70-in-240 ticks, in seconds
                {
                    // Source low and wandering along the floor: with drift positive the dye
                    // is buoyant, so this reads as a rising plume — and a plume is where a
                    // truncated projection shows itself, squashing as it climbs.
                    pour = new Vector2(0.5f + 0.16f * Mathf.Sin(t * 0.7f), 0.14f);
                    pourAmt = _pourAmt;
                }
                float sa = t * 0.9f;
                stirPos = new Vector2(0.5f + 0.22f * Mathf.Cos(sa), 0.5f + 0.22f * Mathf.Sin(sa));
                stirVel = new Vector2(-Mathf.Sin(sa), Mathf.Cos(sa)) * ScaleVel(_stirStrength);
            }
        }

        float dt = Dt;
        bool refOn = ReferenceOn;
        float[] add =
        {
            N, N, dt, ScaleForce(_drift),
            pour.X * N, pour.Y * N, ScaleRadius(_pourRadius), pourAmt, ScaleVel(_pourPush),
            stirPos.X * N, stirPos.Y * N, ScaleRadius(_stirRadius), stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { N, N, dt, Fade(_dissipV) };
        float[] sim = { N, N, refOn ? SorOmega : 0f, 0f };
        // MacCormack on: pass 1 runs dissip 1, the real fade lives in the MC pass
        float[] advD = { N, N, dt, _macCormack ? 1.0f : Fade(_dissipD) };
        float[] visc = { N, N, ScaleVisc(_viscosity), 0f };
        float[] mc = { N, N, dt, Fade(_dissipD) };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        // THE A/B: one number. Everything else about the solve is identical.
        int iters = refOn ? ReferenceIters : _k;
        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseAdi = !refOn;   // the reference is a converged point solve, not ADI
            _fluid.UseSor = refOn;
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
        ui.AddSlider("Viscosity iters", 4, 40, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // 1 alternation = one exact x pass and one exact y pass. The anisotropy is loudest
        // at 1 and washes out as the axes equalise — the dial dissolving its own artifact,
        // the same shape of knob as 256's sweep count.
        AddCellLockedSlider(ui, "Alternations (1 = maximum grain)", 1, 12, _k, v => _k = (int)v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
