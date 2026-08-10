using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 256 — PLAID (docs/artifacts-250.md Block A). Checkerboard-correlated residual as a
// WOVEN texture. No new kernel: this is 251's red-black Gauss-Seidel at ω = 1 and very few
// sweeps, which the doc lists four scenes later but which is a preset of what 251 built.
//
// Why a checkerboard falls out of it. Red-black splits the grid into two interleaved
// sublattices and relaxes them in turn: red updates against the OLD field, black then
// updates against a field where red is already fresh. That asymmetry is the entire point of
// Gauss-Seidel — it is why it beats Jacobi — but it means the two colours are never in the
// same state of convergence. Run it to convergence and the difference vanishes. Stop after
// one or two sweeps and it is frozen into the residual as a two-cell weave.
//
//   K = 1     one red pass, one black pass. Maximum weave.
//   K → 8     the sublattices equalise and the plaid dissolves into plain squish.
//
// TWO THINGS THIS SCENE NEEDS THAT NO EARLIER ONE DID, both consequences of the artifact
// being exactly two cells wide — the grid-locked case the base doc warns about:
//
//   1. PIXEL-EXACT SAMPLING. A two-cell checkerboard through a bilinear sampler averages to
//      flat grey. Not faint — GONE. The display samples with texelFetch here.
//   2. A COARSE GRID BY DEFAULT. At 512² two cells is a couple of screen pixels and the
//      weave aliases into moiré. 256² is the default and 128² is in the dropdown, because
//      here the grid IS an aesthetic knob (design response #2) rather than a fidelity one.
public partial class Plaid : Scene250Base
{
    private FluidSim? _fluid;
    private float _demoT;
    private Vector2 _prevMouseUv = new(-1, -1);

    private const float DragGain = 0.9f;

    // Same fluid as 250 and 251, deliberately — the weave has to be the solver's doing.
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
    private int _k = 2;             // red+black sweeps — the doc's 1–8 range
    private float _omega = 1.0f;    // pinned at plain Gauss-Seidel; 251 owns the ω dial

    private const int ReferenceSweeps = 200;
    private const int MaxReferenceSweeps = 200;

    private int ReferenceIters => Mathf.Min(ScaleSor(ReferenceSweeps), MaxReferenceSweeps);
    private bool ReferenceCapped => ScaleSor(ReferenceSweeps) > MaxReferenceSweeps;

    protected override string SceneTitle => "256 · plaid — checkerboard residual as woven texture";

    protected override string SceneHint =>
        "Red-black Gauss-Seidel at ω=1, stopped after one or two sweeps. The two sublattices "
        + "are never equally converged — that asymmetry IS Gauss-Seidel — so truncating it "
        + "freezes a two-cell weave into the residual. K=1 is maximum plaid; by K=8 it has "
        + "dissolved back into plain squish. The artifact is exactly two cells wide, so the "
        + "GRID DROPDOWN IS AN AESTHETIC KNOB here, not a quality one: 128² is chunky weave, "
        + "512² is moiré. Pixel-exact sampling is on because bilinear filtering averages a "
        + "two-cell checkerboard to flat grey — the artifact would vanish into the sampler.";

    protected override string ArtifactName => "plaid (RBGS sweeps)";

    protected override bool SideView => true;              // same vertical slice as 250/251
    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 32.6f;
    protected override float TimeScaleDefault => 0.34f;
    protected override float FieldGainDefault => 0.8052f;
    protected override float FieldGammaDefault => 0.6312f;

    // Both of these are the artifact being cell-scaled, not preference — see the header.
    protected override bool PixelExactDefault => true;
    protected override int[] GridOptions => new[] { 128, 256, 512 };
    protected override int GridDefault => 256;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk",
            extras: true, sor: true);
        _fluid.MeasureResidual = true;
        _fluid.UseSor = true;
        if (!_fluid.SorReady) { GD.PushError("[256] RBGS path failed to compile — no plaid without it"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText()
    {
        string tag = ReferenceOn
            ? $"K {ReferenceIters}{(ReferenceCapped ? " (REFERENCE · capped)" : " (REFERENCE)")}"
            : $"K {_k} · ω {_omega:0.00}";
        return $"plaid · {N}² · {tag} · weave = 2 cells ≈ {2.0f / N:P2} of the plane "
            + $"· t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
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
        // THE A/B: sweep count. The reference runs the SAME solver to convergence, which is
        // what makes the point — the weave is not the solver, it is stopping the solver.
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
        // The doc's range. 1 is maximum weave; past 8 the sublattices have equalised and
        // there is no plaid left to look at — which is itself the demonstration.
        AddCellLockedSlider(ui, "Sweeps K (1 = max weave)", 1, 8, _k, v => _k = (int)v);
        // Left reachable rather than hidden: at ω≠1 plaid and bounce superpose, which is the
        // layering claim of the series in its smallest possible form.
        ui.AddSlider("ω (1 = plain GS — 251's dial, here for layering)", 0.3f, 1.95f, _omega,
            v => _omega = v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
