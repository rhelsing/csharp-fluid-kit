using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 257 — EQ (docs/artifacts-250.md Block A, "the grand one"). An equalizer on
// incompressibility, and the scene that turns the series' premise from a description into
// a design tool.
//
// EVERY OTHER SCENE IN BLOCK A TAKES WHAT THE SOLVER LEAVES. 250 truncates Jacobi and gets
// whatever error a smoother happens to leave (smooth, domain-scale). 251 over-relaxes and
// gets ringing. 256 stops red-black early and gets a two-cell weave. In each case the
// artifact's SHAPE is a consequence of the algorithm, not a choice — you can dial how much
// of it there is, never what it is.
//
// Here the shape is the knob. In the cosine basis the Poisson operator is diagonal, so the
// exact projection is one divide per cell, and any curve W(k) can be applied to it:
//
//     p̂(k) = W(k) · p̂_exact(k)      W = shelf(knee, slope, tilt) × notch(centre, width, depth)
//
// THIS IS THE ANSWER TO 256. docs/artifacts-log.md §1 found by eye that a residual reads as
// a material when it is low-frequency and convects, and as noise when it sits at the grid
// frequency — squish good, plaid bad. That was a finding you had to live with. Here it is
// sliders. But note WHICH WAY the shelf has to face, because the base doc names a lowpass
// and a lowpass gives the wrong material:
//
//   tilt 1 (HIGHPASS, the default)  W→0 below the knee, ≈1 above. Long features
//     under-projected and compliant, short ones rigid. Squish's material, with plaid's
//     grid-frequency junk explicitly excluded. This is the combination §1 says people want.
//   tilt 0 (LOWPASS, what the plan names)  the mirror image: rigid at large scale,
//     compressible at small. That is fine-scale-only compressibility — i.e. 254
//     macro-honest's material, reached as a preset here rather than as its own scene.
//
// The notch then carves one band of scales out of whichever shelf is selected.
//
// AND THE REFERENCE IS EXACT. Not "converged", not "capped" — W(k) = 1 IS the true
// projection, because the transform pair is exactly invertible. No other scene in the
// series can say that: 250's reference is under-converged Jacobi, 251's is optimal-ω SOR
// run long, 250b's is a multigrid V-cycle. This one is right by construction.
//
// The knee is set as a WORLD WAVELENGTH, which makes it the series' only fully grid-proof
// artifact knob (the base doc's design response #1, the gold standard): "features longer
// than 1.2 m are compressible" means the same thing at 128² and at 512².
public partial class Eq : Scene250Base
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

    // ── THE ARTIFACT: the curve ───────────────────────────────────────────────────
    // Knee as a WORLD WAVELENGTH in metres. At the default tilt (highpass) structures
    // LONGER than this are left under-projected — compliant — and shorter ones are
    // projected properly. Tilt 0 mirrors that.
    private float _kneeWavelength = 1.6f;
    private float _slope = 2.0f;          // rolloff order: 1 gentle, 8 near-brick
    private float _notchWavelength = 0.4f;
    private float _notchWidth = 0.06f;    // in normalised κ
    private float _notchDepth = 0.0f;     // 0 = notch off

    // 1 = HIGHPASS: long features compliant, short ones rigid — squish's material with
    // plaid's grid-frequency junk kept out. That combination is the whole point (log §1),
    // so it is the default; 0 is the lowpass the base doc names, which is 254's material.
    private float _tilt = 1.0f;

    protected override string SceneTitle => "257 · eq — a designed residual spectrum";

    protected override string SceneHint =>
        "An equalizer on incompressibility. The cosine transform diagonalises the Poisson "
        + "operator, so the exact solve is one divide per cell and any curve can be applied "
        + "to it: p̂(k) = W(k)·p̂_exact(k). Everywhere else in Block A the artifact's SHAPE is "
        + "whatever the algorithm happens to leave; here it is drawn. At tilt 1 (default) the "
        + "knee sets the wavelength BELOW which the fluid stays rigid — long features go "
        + "compliant, short ones do not, which is squish's material without plaid's grid "
        + "junk: the law 256 found the hard way, now on a slider. Tilt 0 mirrors it into "
        + "fine-scale compressibility instead (254's material). The reference is EXACT — "
        + "W(k)=1 IS the true projection, not merely a converged one.";

    protected override string ArtifactName => "eq (spectral W(k))";

    protected override bool SideView => true;
    protected override bool ArtifactViewDefault => false;
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
            extras: true, spectral: true);
        _fluid.MeasureResidual = true;
        _fluid.UseSpectral = true;
        if (!_fluid.SpectralReady) { GD.PushError("[257] spectral path failed to compile — no eq without it"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    // World wavelength → normalised κ. A cosine mode of index p along one axis has
    // wavelength 2N/p cells = 2·WorldSize/p world units, and κ = p/N, so
    //     κ = 2·WorldSize / (λ_world · N) · (N/1) / 2  ⇒  κ = 2·WorldSize/λ / N · N/2...
    // more directly: p = 2·WorldSize/λ  and  κ = p/N.
    private float KappaFor(float worldWavelength) =>
        Mathf.Clamp(2.0f * WorldSize / Mathf.Max(worldWavelength, 1e-4f) / N, 1e-5f, 2.0f);

    protected override string ReadoutText()
    {
        if (ReferenceOn)
        {
            return $"eq · {N}² · W(k) = 1 — EXACT projection · t×{TimeScale:0.00} "
                + $"· {Engine.GetFramesPerSecond():0}fps";
        }
        return $"eq · {N}² · knee {_kneeWavelength:0.00} m (κ {KappaFor(_kneeWavelength):0.000}) "
            + $"· slope {_slope:0.0}{(_notchDepth > 0.01f ? $" · notch {_notchWavelength:0.00} m ×{_notchDepth:0.00}" : "")} "
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

        // THE A/B: bypass = W(k) ≡ 1, which is not an approximation of the right answer,
        // it IS the right answer. Everything else in the frame is identical.
        float[] spec =
        {
            N, N,
            KappaFor(_kneeWavelength), _slope,
            KappaFor(_notchWavelength), _notchWidth, _notchDepth,
            ReferenceOn ? 1f : 0f, _tilt,
            0f, 0f, 0f,   // pad to 12 floats = 48 B — see fs_spectral_project's block
        };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc), specB = ToBytes(spec);

        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _fluid?.Step(addB, advVB, simB, advDB, 0, viscIters, viscB, mcOn, mcB, specB)));
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
        // A WORLD wavelength, not a cell count — the only grid-proof artifact knob in the
        // series. Long features above the knee go compressible; short ones stay rigid.
        ui.AddSlider("Knee — wavelength (m) above which the fluid is soft", 0.08f, 6.0f,
            _kneeWavelength, v => _kneeWavelength = v);
        ui.AddSlider("Slope (1 gentle · 8 near-brick)", 0.5f, 8.0f, _slope, v => _slope = v);
        ui.AddSlider("Tilt — 0 lowpass (fine-scale soft) · 1 highpass (large-scale soft)",
            0.0f, 1.0f, _tilt, v => _tilt = v);
        ui.AddSlider("Notch — centre wavelength (m)", 0.08f, 6.0f, _notchWavelength,
            v => _notchWavelength = v);
        ui.AddSlider("Notch — width (κ)", 0.005f, 0.4f, _notchWidth, v => _notchWidth = v);
        ui.AddSlider("Notch — depth (0 = off)", 0.0f, 1.0f, _notchDepth, v => _notchDepth = v);
        ui.AddSlider("dt (bigger step = more to fix per tick)", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
