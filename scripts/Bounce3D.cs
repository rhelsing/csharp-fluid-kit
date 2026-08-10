using System;
using System.Collections.Generic;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 251b — BOUNCE IN 3D. 251's over-relaxation artifact lifted the same way 250 → 250b
// was: nothing above the solver changes, one axis is added, and the kernel becomes its 3D
// twin (fs_pressure_rbgs → f3_pressure_rbgs, 4 neighbours → 6, /4 → /6).
//
// One thing genuinely does NOT survive the lift by inspection, and it is worth naming: the
// red-black parity must sum ALL THREE axes. A 2D (x+y) split does not two-colour a
// 6-neighbour lattice — half the "black" cells end up adjacent, and the pass reads values
// it is concurrently writing. The stamp solver met this first; solve_rbgs.glslinc carries
// the same warning, and f3_pressure_rbgs uses (x+y+z)&1.
//
// THREE solvers now live in FluidSim3D and all three stay reachable: Jacobi (250b's
// artifact), red-black SOR (this scene's artifact), deep multigrid (the reference for
// both). That makes the reference here the honest one — a converged solve at grid-
// independent cost — rather than 251's optimal-ω-run-long.
//
//   artifact  SOR ω, truncated            reference  deep multigrid
//   field     dye, ink raymarched         artifact view  |∇·v| volume in #E23D6D
public partial class Bounce3D : Scene250Base
{
    private static readonly Vector3 BoxExtents = new(1.4f, 2.1f, 1.4f);
    private const string VolumeShader = "res://shaders/artifact_volume.gdshader";

    private FluidSim3D? _fluid;
    private Texture3Drd _dyeTex = null!;
    private Texture3Drd _divTex = null!;
    private float _demoT;
    private float _stirPhase;

    private Vector3I Grid3 => new(N, N * 3 / 2, N);

    // Deliberately 250b's tuned fluid, unchanged — so switching between 250b and 251b shows
    // the SOLVER's character and nothing else. Only the projection differs.
    private float _dt = 1.3175f;
    private float _drift = -0.0125f;
    private float _pourAmt = 0.306f;
    private float _pourRadius = 2.755f;
    private float _pourSpeed = 1.5f;
    private float _viscosity = 0.5475f;
    private int _viscIters = 10;        // the expensive knob — first dial down if slow
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
    private float _jostle = 0.0f;

    private float _camYaw;
    private float _camPitch = 11.95f;
    private float _camDist = 7.805f;

    private const float DyeDensity = 8.0f;
    private const float DiluteKnee = 0.03f;
    private const float DepthAbsorb = 0.22f;
    private const float Shade = 0.185f;
    private const float MarchSteps = 96.0f;

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    private float _omega = 1.62f;   // >1 overshoots and is pulled back — ringing
    private int _k = 24;            // full red+black sweeps

    private const int ReferenceSweeps = 24;   // MgDeepSolver3D reads this as iters/12 V-cycles

    protected override string SceneTitle => "251b · bounce 3D — elasticity, one axis up";

    protected override string SceneHint =>
        "251's over-relaxed projection in a box. ω below 1 under-relaxes into a pillow; above "
        + "1 each update overshoots and is pulled back, leaving a correlated over/under "
        + "pattern that reads as ringing rather than squashing. Reference is the deep "
        + "multigrid path — a genuinely converged solve at grid-independent cost, so unlike "
        + "251 it needs no cap. Same fluid as 250b on purpose: flip between the two scenes "
        + "and the only difference is how the pressure solve steps.";

    protected override string ArtifactName => "bounce (SOR ω vs multigrid)";

    protected override bool UsesFlatPlane => false;

    protected override int RefGrid => 56;
    protected override int[] GridOptions => new[] { 56, 96, 128, 160, 192 };
    protected override string GridLabel(int n) => $"{n}×{n * 3 / 2}×{n}";
    protected override int GridDefault => 96;   // 250b's tuned tier, for a fair comparison

    protected override float TimeScaleDefault => 0.514f;
    protected override float ArtifactGainDefault => 33.6f;
    protected override bool ArtifactViewDefault => true;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DensityRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid3, extras: true, sor: true);
        _fluid.EnableMultigrid();
        _fluid.MeasureDivergence = true;
        _fluid.UseSor = true;
        if (!_fluid.SorReady) { GD.PushError("[251b] SOR path failed to compile — falling back to Jacobi"); }
    }

    protected override void FreeSim()
    {
        _fluid?.Free();
        _fluid = null;
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

    protected override string ReadoutText() =>
        $"bounce 3D · {Grid3.X}×{Grid3.Y}×{Grid3.Z} · "
        + $"{(ReferenceOn ? "deep MG (REFERENCE)" : $"ω {_omega:0.00} · K {_k}")} "
        + $"· t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";

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
        // pad.x = free-slip (gradient pass), pad.y = ω (rbgs pass) — both ignored elsewhere
        float[] sim = { g.X, g.Y, g.Z, 0f, _freeSlip ? 1f : 0f, _omega, 0f, 0f };
        float[] advD = { g.X, g.Y, g.Z, 0f, dt, _macCormack ? 1.0f : fadeD, 0f, 0f };
        float[] visc = { g.X, g.Y, g.Z, 0f, ScaleVisc(_viscosity) * TimeScale, 0f, 0f, 0f };
        float[] mc = { g.X, g.Y, g.Z, 0f, dt, fadeD, 0f, 0f };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

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
        if (_jostle > 0.005f)
        {
            var jv = new Vector2(Mathf.Sin(_demoT * 1.7f), Mathf.Cos(_demoT * 1.3f))
                * ScaleVel(_jostle * 0.12f);
            extras.Add(ToBytes(new[]
            {
                g.X, g.Y, g.Z, 0f, dt, 0f,
                g.X * 0.5f, g.Y * 0.5f, g.Z * 0.5f, g.X * 1.0f, jv.X, 0f, jv.Y, 0f, 0f, 0f,
            }));
        }
        byte[][]? extraArr = extras.Count > 0 ? extras.ToArray() : null;
        byte[]? confB = _curlEps > 0.01f
            ? ToBytes(new[] { g.X, g.Y, g.Z, 0f, dt, ScaleConfine(_curlEps), 0f, 0f })
            : null;

        // THE A/B: over-relaxed SOR against the converged multigrid solve.
        bool useMg = ReferenceOn;
        int iters = useMg ? ReferenceSweeps : _k;
        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseMultigrid = useMg;
            _fluid.Step(addB, advVB, simB, advDB, iters, false,
                viscIters, viscB, mcOn, mcB, extraArr, confB);
        }));
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
        ui.AddSlider("Jostle (nudge the box)", 0.0f, 1.0f, _jostle, v => _jostle = v);
        ui.AddSlider("Viscosity iters (the cost at high grids)", 4, 120, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        ui.AddSlider("ω over-relaxation (<1 pillow · >1 ring)", 0.3f, 1.95f, _omega, v => _omega = v);
        AddCellLockedSlider(ui, "Sweeps K (red+black)", 2, 120, _k, v => _k = (int)v);
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
