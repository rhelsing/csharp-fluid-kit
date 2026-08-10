using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 252 — CROSSFADE (docs/artifacts-250.md Block A). A dial between honest and squishy,
// and the scene that makes the rest of the block composable rather than merely enumerable.
//
//     p = α · p_exact + (1 − α) · p_truncated
//
// The plan describes α as mixing multigrid with Jacobi, and lists this scene as blocked on
// a 2D multigrid hookup. It is not blocked any more, and the substitute is better than the
// original: 257's spectral solve is EXACT, so the honest end of the dial is not a
// well-converged approximation of the answer — it is the answer. Neither remaining Block A
// scene needs multigrid.
//
// WHY BLENDING TWO PRESSURE FIELDS IS LEGITIMATE. The projection is linear in the
// divergence, so a convex combination of two valid pressure solutions solves the same
// Poisson problem with a blended right-hand side. α does not cross-dissolve two pictures;
// every intermediate is a real solve of a real system. That is what makes this a MATERIAL
// dial rather than a rendering trick, and it is why 252 generalises: with four pressure
// paths in FluidSim the same kernel crossfades any pair.
//
// ORDER OF OPERATIONS MATTERS and is easy to get backwards. The truncated solve runs FIRST,
// so it still warm-starts from last frame's blended pressure — its memory is part of its
// character. The spectral solve runs second because it is direct: no initial guess can
// contaminate it. Reversed, the Jacobi side would warm-start from the exact answer and the
// artifact would quietly vanish.
//
//   artifact  α below 1, K low     reference  α = 1 — the exact solve, alone
public partial class Crossfade : Scene250Base
{
    private FluidSim? _fluid;
    private float _demoT;
    private Vector2 _prevMouseUv = new(-1, -1);

    private const float DragGain = 0.9f;

    // Same fluid as the rest of Block A.
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
    private float _alpha = 0.35f;   // mostly squishy, with enough honesty to hold together
    private int _k = 20;            // sweeps on the truncated side

    protected override string SceneTitle => "252 · crossfade — a dial between honest and squishy";

    protected override string SceneHint =>
        "Two solves of the same divergence every tick, blended. α = 1 is the exact spectral "
        + "projection; α = 0 is K truncated Jacobi sweeps and nothing else; in between is a "
        + "real material, not a cross-dissolve — the projection is linear, so every "
        + "intermediate genuinely solves a Poisson problem. This is the scene the plan had "
        + "waiting on a multigrid hookup that turned out to be unnecessary. Slide α slowly "
        + "with the artifact view on and watch the residual appear continuously rather than "
        + "switching.";

    protected override string ArtifactName => "crossfade (α exact ↔ truncated)";

    protected override bool SideView => true;
    protected override bool ArtifactViewDefault => true;
    protected override float ArtifactGainDefault => 32.6f;
    protected override float TimeScaleDefault => 0.34f;
    protected override float FieldGainDefault => 0.8052f;
    protected override float FieldGammaDefault => 0.6312f;

    // The direct O(N²)-per-axis transform is ~5 ms of reads at 512² and ~43 ms at 1024²,
    // so the ladder stops here. See fs_dct_1d for the O(N log N) upgrade path.
    protected override int[] GridOptions => new[] { 128, 256, 512 };
    protected override int GridDefault => 512;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk",
            extras: true, spectral: true, crossfade: true);
        _fluid.MeasureResidual = true;
        _fluid.UseCrossfade = true;
        if (!_fluid.CrossfadeReady) { GD.PushError("[252] crossfade path failed to compile"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText()
    {
        float a = ReferenceOn ? 1f : _alpha;
        return $"crossfade · {N}² · α {a:0.00}"
            + $"{(ReferenceOn ? " (REFERENCE — exact only)" : $" · K {_k}")} "
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
        float[] add =
        {
            N, N, dt, ScaleForce(_drift),
            pour.X * N, pour.Y * N, ScaleRadius(_pourRadius), pourAmt, ScaleVel(_pourPush),
            stirPos.X * N, stirPos.Y * N, ScaleRadius(_stirRadius), stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { N, N, dt, Fade(_dissipV) };
        float[] sim = { N, N, 0f, 0f };
        float[] advD = { N, N, dt, _macCormack ? 1.0f : Fade(_dissipD) };
        float[] visc = { N, N, ScaleVisc(_viscosity), 0f };
        float[] mc = { N, N, dt, Fade(_dissipD) };

        // The spectral side is always UNSHAPED here (bypass = 1 ⇒ W ≡ 1): 257 owns the
        // curve, 252 owns the blend. Mixing both dials into one scene would break the
        // series' one-artifact-per-scene rule and make the A/B unattributable.
        float[] spec = { N, N, 1f, 1f, 1f, 1f, 0f, 1f, 0f, 0f, 0f, 0f };

        // THE A/B: α. Reference pins it to 1 — the exact solve with no truncated share.
        float[] blend = { N, N, ReferenceOn ? 1f : _alpha, 0f };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc), specB = ToBytes(spec), blendB = ToBytes(blend);

        int iters = _k;
        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _fluid?.Step(addB, advVB, simB, advDB, iters, viscIters, viscB, mcOn, mcB, specB, blendB)));
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
        // THE dial. 1 = honest, 0 = fully squishy, and the whole point is that the middle
        // is not a blur of the two ends but its own material.
        ui.AddSlider("α  (1 = exact · 0 = truncated Jacobi)", 0.0f, 1.0f, _alpha, v => _alpha = v);
        AddCellLockedSlider(ui, "K — Jacobi sweeps on the squishy side", 2, 200, _k, v => _k = (int)v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
