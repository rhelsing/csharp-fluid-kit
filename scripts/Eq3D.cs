using System;
using System.Collections.Generic;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 257b — EQ IN 3D. The equalizer on incompressibility, in a volume.
//
// The base plan defers this ("257 eq needs a 3D FFT — defer; 2D is the lab"), on the
// assumption that the transform would be the obstacle. It is not: the cosine transform is
// SEPARABLE, so three axes is three passes rather than a new algorithm — six pipelines from
// the same source, seven dispatches a frame. The real limit is cost, and it is bounded:
// ~0.9 ms at 56³ and ~7.5 ms at 96³, which is why the ladder stops at 96 where 257's stops
// at 512².
//
// ONE THING GENUINELY DOES NOT LIFT BY INSPECTION. The box is N × 1.5N × N, not a cube, so
// each axis must be normalised by ITS OWN length when forming κ. Use a single N and the
// filter tilts: a mode of the same physical wavelength lands at a different κ depending on
// which way it runs, and the "knee" becomes direction-dependent — an anisotropic material
// by accident rather than by design. (255 grain wants that anisotropy deliberately; this
// scene does not.)
//
// The reference is EXACT here too — W(k) = 1 IS the true projection, because the DCT-II /
// DCT-III pair is exactly invertible. 250b's reference is a multigrid V-cycle and 251b's is
// the same; this is the only 3D scene in the series whose accurate side is not an
// approximation of anything.
public partial class Eq3D : Scene250Base
{
    private static readonly Vector3 BoxExtents = new(1.4f, 2.1f, 1.4f);
    private const string VolumeShader = "res://shaders/artifact_volume.gdshader";

    private FluidSim3D? _fluid;
    private Texture3Drd _dyeTex = null!;
    private Texture3Drd _divTex = null!;
    private float _demoT;
    private float _stirPhase;

    private Vector3I Grid3 => new(N, N * 3 / 2, N);

    private float _dt = 1.3175f;
    private float _drift = -0.0125f;
    private float _pourAmt = 0.306f;
    private float _pourRadius = 2.755f;
    private float _pourSpeed = 1.5f;
    private float _viscosity = 0.5475f;
    private int _viscIters = 10;
    private bool _macCormack = true;
    private float _dissipD = 0.9904f;
    private float _dissipV = 0.999f;
    private bool _autoDemo = true;
    private bool _freeSlip = true;
    private float _curlEps = 0.6375f;

    private bool _stirOn = true;
    private float _stirStrength = 0.6f;
    private float _stirOrbit = 0.704f;
    private float _stirHeight = 0.1445f;
    private float _stirSpeed = 2.13f;
    private float _stirRadius = 4.0f;

    private float _camYaw;
    private float _camPitch = 11.95f;
    private float _camDist = 7.805f;

    private const float DyeDensity = 8.0f;
    private const float DiluteKnee = 0.03f;
    private const float DepthAbsorb = 0.22f;
    private const float Shade = 0.185f;
    private const float MarchSteps = 96.0f;

    // ── THE ARTIFACT: the curve ───────────────────────────────────────────────────
    private float _kneeWavelength = 1.6f;   // world metres — grid-proof
    private float _slope = 2.0f;
    private float _notchWavelength = 0.4f;
    private float _notchWidth = 0.06f;
    private float _notchDepth = 0.0f;
    private float _tilt = 1.0f;   // highpass by default — see Eq.cs

    protected override string SceneTitle => "257b · eq 3D — a designed residual spectrum, in a volume";

    protected override string SceneHint =>
        "The equalizer on incompressibility, lifted. The cosine transform is separable, so "
        + "three axes is three passes and not a new algorithm — the base plan deferred this "
        + "expecting an FFT to be the obstacle. p̂(k) = W(k)·p̂_exact(k): pull the knee down "
        + "and — at tilt 1 — long structures go compliant while short ones stay rigid. The "
        + "reference is "
        + "EXACT (W=1 is the true projection), the only 3D scene in the series whose accurate "
        + "side is not an approximation. Knee is a world wavelength, so it means the same "
        + "thing at 56³ and 96³ — and each axis is normalised by its own length, because the "
        + "box is not a cube.";

    protected override string ArtifactName => "eq (spectral W(k))";

    protected override bool UsesFlatPlane => false;

    // Direct O(N)-per-output transform: ~0.9 ms at 56³, ~7.5 ms at 96³, ~24 ms at 128³.
    protected override int RefGrid => 56;
    protected override int[] GridOptions => new[] { 56, 96, 128 };
    protected override string GridLabel(int n) => $"{n}×{n * 3 / 2}×{n}";
    protected override int GridDefault => 96;

    protected override float TimeScaleDefault => 0.514f;
    protected override float ArtifactGainDefault => 33.6f;
    protected override bool ArtifactViewDefault => true;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DensityRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid3,
            extras: true, spectral: true);
        _fluid.MeasureDivergence = true;
        _fluid.UseSpectral = true;
        if (!_fluid.SpectralReady) { GD.PushError("[257b] spectral path failed to compile"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
    }

    // World wavelength → normalised κ along the SHORTEST axis (x/z). A cosine mode of index
    // p has wavelength 2·extent/p in world units, so p = 2·extent/λ and κ = p/N.
    private float KappaFor(float worldWavelength)
    {
        float extent = BoxExtents.X * 2.0f;
        return Mathf.Clamp(2.0f * extent / Mathf.Max(worldWavelength, 1e-4f) / N, 1e-5f, 2.0f);
    }

    protected override void BuildDisplay()
    {
        var vol = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = BoxExtents * 2.0f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4.0f,
        };
        _dyeTex = new Texture3Drd();
        _divTex = new Texture3Drd();
        Mat = new ShaderMaterial { Shader = GD.Load<Shader>(VolumeShader) };
        Mat.SetShaderParameter("dye_tex", _dyeTex);
        Mat.SetShaderParameter("artifact_tex", _divTex);
        Mat.SetShaderParameter("box_extents", BoxExtents);
        Mat.SetShaderParameter("density", DyeDensity);
        Mat.SetShaderParameter("knee", DiluteKnee);
        Mat.SetShaderParameter("absorb", DepthAbsorb);
        Mat.SetShaderParameter("shade", Shade);
        Mat.SetShaderParameter("steps", MarchSteps);
        vol.MaterialOverride = Mat;
        AddChild(vol);
        UpdateCamera();
    }

    private void UpdateCamera()
    {
        if (FlyMode) { return; }
        float yaw = Mathf.DegToRad(_camYaw);
        float pitch = Mathf.DegToRad(_camPitch);
        var target = new Vector3(0f, 0.2f, 0f);
        Cam.Position = target + new Vector3(
            Mathf.Sin(yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * Mathf.Cos(pitch)) * _camDist;
        Cam.LookAt(target, Vector3.Up);
    }

    protected override string ReadoutText()
    {
        if (ReferenceOn)
        {
            return $"eq 3D · {Grid3.X}×{Grid3.Y}×{Grid3.Z} · W(k) = 1 — EXACT projection "
                + $"· t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
        }
        return $"eq 3D · {Grid3.X}×{Grid3.Y}×{Grid3.Z} · knee {_kneeWavelength:0.00} m "
            + $"(κ {KappaFor(_kneeWavelength):0.000}) · slope {_slope:0.0}"
            + $"{(_notchDepth > 0.01f ? $" · notch {_notchWavelength:0.00} m ×{_notchDepth:0.00}" : "")} "
            + $"· t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";
    }

    protected override void SimTick(double delta)
    {
        UpdateCamera();
        _dyeTex.TextureRdRid = FieldRid;
        _divTex.TextureRdRid = ArtifactRid;

        var g = Grid3;
        Vector3 src = Vector3.Zero;
        float amt = 0f;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && MouseXz() is Vector2 mxz)
        {
            src = new Vector3(
                Mathf.Clamp((mxz.X / (BoxExtents.X * 2.0f) + 0.5f) * g.X, 4, g.X - 4),
                g.Y - 6,
                Mathf.Clamp((mxz.Y / (BoxExtents.Z * 2.0f) + 0.5f) * g.Z, 4, g.Z - 4));
            amt = _pourAmt;
        }
        else if (_autoDemo)
        {
            _demoT += (float)delta * TimeScale;
            if (Mathf.PosMod(_demoT, 10.0f) < 0.5f)
            {
                src = new Vector3(
                    g.X * (0.5f + 0.2f * Mathf.Sin(_demoT * 0.6f)),
                    g.Y - 6,
                    g.Z * (0.5f + 0.2f * Mathf.Cos(_demoT * 0.47f)));
                amt = _pourAmt;
            }
        }

        float dt = Dt;
        float fadeD = Fade(_dissipD);
        float fadeV = Fade(_dissipV);

        float[] add =
        {
            g.X, g.Y, g.Z, 0f, dt, ScaleForce(_drift),
            src.X, src.Y, src.Z, ScaleRadius(_pourRadius),
            0f, -ScaleVel(_pourSpeed), 0f, amt, 0f, 0f,
        };
        float[] advV = { g.X, g.Y, g.Z, 0f, dt, fadeV, 0f, 0f };
        float[] sim = { g.X, g.Y, g.Z, 0f, _freeSlip ? 1f : 0f, 0f, 0f, 0f };
        float[] advD = { g.X, g.Y, g.Z, 0f, dt, _macCormack ? 1.0f : fadeD, 0f, 0f };
        float[] visc = { g.X, g.Y, g.Z, 0f, ScaleVisc(_viscosity) * TimeScale, 0f, 0f, 0f };
        float[] mc = { g.X, g.Y, g.Z, 0f, dt, fadeD, 0f, 0f };

        // THE A/B: bypass = W(k) ≡ 1, the exact projection.
        float[] spec =
        {
            g.X, g.Y, g.Z, 0f,
            KappaFor(_kneeWavelength), _slope,
            KappaFor(_notchWavelength), _notchWidth, _notchDepth,
            ReferenceOn ? 1f : 0f, _tilt, 0f,
        };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc), specB = ToBytes(spec);

        var extras = new List<byte[]>();
        if (_stirOn && _stirStrength > 0.01f)
        {
            _stirPhase += _stirSpeed * (float)delta * TimeScale;
            float or0 = _stirOrbit * g.X * 0.5f * 0.9f;
            float sx = g.X * 0.5f + Mathf.Cos(_stirPhase) * or0;
            float sz = g.Z * 0.5f + Mathf.Sin(_stirPhase) * or0;
            float sy = Mathf.Clamp(_stirHeight, 0.05f, 0.95f) * g.Y;
            var tang = new Vector2(-Mathf.Sin(_stirPhase), Mathf.Cos(_stirPhase)) * ScaleVel(_stirStrength);
            extras.Add(ToBytes(new[]
            {
                g.X, g.Y, g.Z, 0f, dt, 0f,
                sx, sy, sz, ScaleRadius(_stirRadius), tang.X, 0f, tang.Y, 0f, 0f, 0f,
            }));
        }
        byte[][]? extraArr = extras.Count > 0 ? extras.ToArray() : null;
        byte[]? confB = _curlEps > 0.01f
            ? ToBytes(new[] { g.X, g.Y, g.Z, 0f, dt, ScaleConfine(_curlEps), 0f, 0f })
            : null;

        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _fluid?.Step(addB, advVB, simB, advDB, 0, false,
                viscIters, viscB, mcOn, mcB, extraArr, confB, specB)));
    }

    private Vector2? MouseXz()
    {
        var mp = GetViewport().GetMousePosition();
        var top = new Plane(Vector3.Up, BoxExtents.Y * 0.9f);
        if (top.IntersectsRay(Cam.ProjectRayOrigin(mp), Cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return null;
        }
        return new Vector2(hit.X, hit.Z);
    }

    protected override void BuildSimKnobs(DemoUI ui)
    {
        ui.AddToggle("Auto-demo (pour bursts)", _autoDemo, on => _autoDemo = on);
        ui.AddToggle("MacCormack advection", _macCormack, on => _macCormack = on);
        ui.AddToggle("Free-slip walls (roll-up)", _freeSlip, on => _freeSlip = on);
        ui.AddSlider("Viscosity ν·dt", 0.0f, 1.5f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Buoyancy drift", -1.0f, 1.5f, _drift, v => _drift = v);
        ui.AddSlider("Vorticity confinement ε", 0.0f, 2.5f, _curlEps, v => _curlEps = v);
        ui.AddSlider("Pour amount", 0.0f, 0.6f, _pourAmt, v => _pourAmt = v);
        ui.AddSlider("Pour radius", 1.0f, 10.0f, _pourRadius, v => _pourRadius = v);
        ui.AddSlider("Pour speed (down)", 0.0f, 5.0f, _pourSpeed, v => _pourSpeed = v);
        ui.AddToggle("Stir · on (orbiting)", _stirOn, on => _stirOn = on);
        ui.AddSlider("Stir · strength", 0.0f, 3.0f, _stirStrength, v => _stirStrength = v);
        ui.AddSlider("Stir · orbit radius (frac)", 0.1f, 0.9f, _stirOrbit, v => _stirOrbit = v);
        ui.AddSlider("Stir · height (frac)", 0.05f, 0.95f, _stirHeight, v => _stirHeight = v);
        ui.AddSlider("Stir · speed (rad/s)", 0.0f, 6.0f, _stirSpeed, v => _stirSpeed = v);
        ui.AddSlider("Stir · radius (cells)", 1.0f, 12.0f, _stirRadius, v => _stirRadius = v);
        ui.AddSlider("Viscosity iters (the cost at high grids)", 4, 120, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
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

    protected override void BuildRenderKnobs(DemoUI ui)
    {
        ui.AddToggle("Artifact SOLO (residual alone)", false,
            on => Mat.SetShaderParameter("artifact_solo", on));
        ui.AddSlider("Camera · yaw (deg)", 0f, 360f, _camYaw, v => _camYaw = v);
        ui.AddSlider("Camera · pitch (deg)", -20f, 70f, _camPitch, v => _camPitch = v);
        ui.AddSlider("Camera · distance", 3.5f, 14f, _camDist, v => _camDist = v);
        ui.AddSlider("Render · dye density", 1.0f, 40.0f, DyeDensity, v => Mat.SetShaderParameter("density", v));
        ui.AddSlider("Render · dilute knee", 0.0f, 0.15f, DiluteKnee, v => Mat.SetShaderParameter("knee", v));
        ui.AddSlider("Render · depth absorb", 0.0f, 2.0f, DepthAbsorb, v => Mat.SetShaderParameter("absorb", v));
        ui.AddSlider("Render · shade", 0.0f, 1.0f, Shade, v => Mat.SetShaderParameter("shade", v));
        ui.AddSlider("Render · march steps", 16, 96, MarchSteps, v => Mat.SetShaderParameter("steps", v));
        ui.AddToggle("Render · cylinder mask (glass)", false,
            on => Mat.SetShaderParameter("use_cylinder", on));
    }

    protected override void OnGridChanged()
    {
        _dyeTex.TextureRdRid = default;
        _divTex.TextureRdRid = default;
    }

    public override void _ExitTree()
    {
        _dyeTex.TextureRdRid = default;
        _divTex.TextureRdRid = default;
        base._ExitTree();
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
