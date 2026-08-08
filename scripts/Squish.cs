using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 250 — SQUISH (docs/artifacts-250.md, the proof of concept).
//
// The thesis, on known physics: a truncated pressure projection does not "fail to be
// incompressible", it BECOMES a compressible material. Jacobi's iteration count K is the
// only dial. High K = honest liquid. Low K = the fluid squashes and springs back, and the
// leftover divergence — the residual the solve never removed — is the artifact you can
// look at directly, in #E23D6D.
//
// This is scene 18's pipeline verbatim (FluidSim + fs_add_milk + MacCormack + implicit
// viscosity), re-skinned into the series palette and re-pointed at the residual. Nothing
// new is simulated. That is the point: it proves the palette, the standard panel, the
// artifact-view plumbing and the similarity table on physics we already trust, so that
// 251+ can be a formula swap into this template.
//
// Unlike 18, the domain is honest about what it is: a VERTICAL SLICE seen from the side
// (see SideView below). 18 renders the same buoyancy-driven pipeline top-down through a
// cup rim, which makes its drift term a sideways bias with no physical reading. Standing
// the slice up costs nothing and makes 250 → 250b a genuine dimensional lift.
//
//   artifact  Jacobi K, truncated       reference  K = 400, capped (see MaxReferenceIters)
//   field     dye, ink on paper         artifact view  |∇·v| AFTER projection
//
// Left-click pours, drag stirs; auto-demo pours a wandering stream and stirs a spiral.
public partial class Squish : Scene250Base
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

    protected override string SceneTitle => "250 · squish — residual divergence as compressibility";

    protected override string SceneHint =>
        "The PoC for the artifact series. Scene 18's fluid, unchanged; only the projection is "
        + "truncated. K is the whole instrument: high = honest incompressible liquid, low = a "
        + "material that squashes and springs. Toggle Reference for the accurate solve, and "
        + "Artifact view to look at the leftover |∇·v| itself — the compressibility, rendered. "
        + "K is deliberately NOT rescaled by the grid dropdown: the residual's correlation "
        + "length is measured in cells, so resolution is part of this instrument.";

    protected override string ArtifactName => "squish (Jacobi K)";

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
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    // The base multiplies this by TIME scale to get Dt — dt stays a sim knob on top.
    protected override float BaseDt => _dt;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk", extras: true);
        // Without this, DivRid holds the divergence the solve was HANDED, which barely
        // moves with K. With it, the pass re-runs after the gradient subtract and DivRid
        // holds what the truncation left behind — the artifact.
        _fluid.MeasureResidual = true;
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText()
    {
        int k = ReferenceOn ? ReferenceIters : _k;
        string tag = ReferenceOn ? (ReferenceCapped ? " (REFERENCE · capped)" : " (REFERENCE)") : "";
        return $"squish · {N}² · K {k}{tag} · t×{TimeScale:0.00} "
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
        float[] add =
        {
            N, N, dt, ScaleForce(_drift),
            pour.X * N, pour.Y * N, ScaleRadius(_pourRadius), pourAmt, ScaleVel(_pourPush),
            stirPos.X * N, stirPos.Y * N, ScaleRadius(_stirRadius), stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { N, N, dt, Fade(_dissipV) };
        float[] sim = { N, N, 0f, 0f };
        // MacCormack on: pass 1 runs dissip 1, the real fade lives in the MC pass
        float[] advD = { N, N, dt, _macCormack ? 1.0f : Fade(_dissipD) };
        float[] visc = { N, N, ScaleVisc(_viscosity), 0f };
        float[] mc = { N, N, dt, Fade(_dissipD) };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        // THE A/B: one number. Everything else about the solve is identical.
        int iters = ReferenceOn ? ReferenceIters : _k;
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
        ui.AddSlider("Viscosity iters", 4, 40, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // Truncation is the instrument. Left end squashes, right end approaches honest.
        AddCellLockedSlider(ui, "Jacobi K (truncation)", 4, 200, _k, v => _k = (int)v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
