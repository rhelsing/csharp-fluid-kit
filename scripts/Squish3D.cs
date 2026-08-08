using System;
using System.Collections.Generic;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 250b — SQUISH IN 3D (docs/artifacts-250.md, the other half of the proof of concept).
//
// The lift is literal, not a re-skin. 250 is a vertical slice with buoyancy along its y axis
// (fs_add_milk: v.y += dt·drift·dye); this is scene 32's box with the SAME term in the same
// place (f3_add_source: v.y += dt·buoy·dye). Same up, same material, one more axis. That is
// what makes 250/250b a template: everything above the solver — palette, panel, TIME scale,
// similarity table, artifact view — crossed dimensions unchanged.
//
// THE A/B IS STRONGER HERE, and it is worth being precise about why. 250's reference is more
// Jacobi, which is under-converged either way and had to be capped to stay interactive. This
// one toggles the deep multigrid path that FluidSim3D already carries, and multigrid's
// iteration count is grid-INDEPENDENT by construction — so the honest solve costs the same at
// every resolution and needs no cap at all. 250's weak reference is a 2D-only problem.
//
// It is also the experiment that started the series: scene 32's own comment records the
// verdict that Jacobi WINS aesthetically — under-converged projection reads as soft billowy
// cushioning, multigrid's accuracy reads harsher. This scene is that A/B, named.
//
//   artifact  Jacobi K, truncated        reference  deep multigrid (grid-independent)
//   field     dye, ink raymarched        artifact view  |∇·v| volume, second channel
public partial class Squish3D : Scene250Base
{
    private static readonly Vector3 BoxExtents = new(1.4f, 2.1f, 1.4f);
    private const string VolumeShader = "res://shaders/artifact_volume.gdshader";

    private FluidSim3D? _fluid;
    private Texture3Drd _dyeTex = null!;
    private Texture3Drd _divTex = null!;
    private float _demoT;
    private float _stirPhase;

    // A glass is taller than it is wide: the box is N × 1.5N × N, so at the reference grid
    // this is scene 32's 56×84×56 exactly.
    private Vector3I Grid3 => new(N, N * 3 / 2, N);

    // ── defaults: scene 32's tuned values, which are already authored at 56³ — this
    // scene's reference grid — so they carry over verbatim and the Scale* helpers take
    // them from there. Every one is in world units; the dropdown re-derives cells.
    private float _dt = 1.5975f;        // a bigger step leaves MORE for the projection to fix
    // NEGATIVE, tuned: the dye sinks and pools rather than billowing up. Note this inverts
    // the sign 250 was tuned at — the two scenes share the term, not the taste.
    private float _drift = -0.575f;
    private float _pourAmt = 0.306f;
    private float _pourRadius = 2.755f;
    private float _pourSpeed = 1.5f;    // downward injection velocity
    private float _viscosity = 0.5475f;
    private int _viscIters = 30;        // diagonally dominant ⇒ N-independent (see 250)
    private bool _macCormack = true;
    private float _dissipD = 0.9904f;
    private float _dissipV = 0.999f;
    private bool _autoDemo = true;
    private bool _freeSlip = true;      // walls slide, don't stick — lets the mushroom roll up
    private float _curlEps = 0.6375f;   // vorticity confinement

    private bool _stirOn = true;
    private float _stirStrength = 0.6f;
    private float _stirOrbit = 0.704f;  // orbit radius, fraction of box radius
    private float _stirHeight = 0.1445f;
    private float _stirSpeed = 2.13f;   // orbit rad/s
    private float _stirRadius = 4.0f;
    private float _jostle = 0.015f;     // tuned near-off: the stir alone carries it

    private float _camYaw;
    private float _camPitch = 11.95f;
    private float _camDist = 7.805f;

    // Render, same tuned run. One source for both the material push and the slider, so
    // what the scene opens with and what the slider reads can't drift apart.
    private const float DyeDensity = 8.0f;
    private const float DiluteKnee = 0.03f;
    private const float DepthAbsorb = 0.22f;   // was 0.45 — less murk through the depth
    private const float Shade = 0.185f;        // was 0.4 — flatter, lets the palette read
    private const float MarchSteps = 96.0f;    // was 56 — worth it at this density

    // ── THE ARTIFACT ──────────────────────────────────────────────────────────────
    private int _k = 24;   // truncated Jacobi sweeps — the dial (scene 32's tuned value)

    // The reference path. MgDeepSolver3D reads this as sweeps and runs iters/12 V-cycles,
    // so 24 is scene 32's proven 2 cycles. NO cap needed and none wanted: multigrid's cost
    // per cycle scales with the grid but its cycle COUNT does not.
    private const int ReferenceSweeps = 24;

    protected override string SceneTitle => "250b · squish 3D — the same artifact, one axis up";

    protected override string SceneHint =>
        "250's vertical slice, lifted. The buoyancy term is identical (v.y += dt·drift·dye), so "
        + "this is the same material in a box. Reference here is the DEEP MULTIGRID path, not "
        + "more Jacobi — a genuinely converged solve whose cost per frame doesn't blow up with "
        + "the grid. Toggle it against low K and judge which one you'd rather have: scene 32's "
        + "verdict was that Jacobi's under-converged squish looks better than the honest answer. "
        + "Artifact view marches the |∇·v| volume as a second channel.";

    protected override string ArtifactName => "squish (Jacobi K vs multigrid)";

    protected override bool UsesFlatPlane => false;   // volume raymarch, not a plane

    // 3D budgets are nothing like 2D's: 1024³ is a billion cells. Reference is scene 32's grid.
    protected override int RefGrid => 56;
    protected override int[] GridOptions => new[] { 40, 56, 72 };
    protected override string GridLabel(int n) => $"{n}×{n * 3 / 2}×{n}";
    protected override int GridDefault => 56;

    protected override float TimeScaleDefault => 0.3473f;   // scene 32's tuned slow-mo
    // 33.6, tuned — and 250 landed on 32.6 independently. My 6.0 guess was an order out in
    // both dimensions, so ~33 is the real scale for a divergence field in this palette.
    protected override float ArtifactGainDefault => 33.6f;
    protected override bool ArtifactViewDefault => false;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DensityRid ?? default;
    protected override Rid ArtifactRid => _fluid?.DivRid ?? default;

    protected override void BuildSim()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid3, extras: true);
        _fluid.EnableMultigrid();              // the reference path — free, already built
        _fluid.MeasureDivergence = true;       // DivRid = the residual, not the solve's rhs
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
        // Assigning the base's Mat is what wires the artifact toggle + intensity slider to
        // this shader — the panel needs no 3D special case at all.
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

    // The orbit rig and the fly cam both want to own the transform, so the orbit yields.
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
        $"squish 3D · {Grid3.X}×{Grid3.Y}×{Grid3.Z} · "
        + $"{(ReferenceOn ? "deep MG (REFERENCE)" : $"Jacobi {_k}")} · t×{TimeScale:0.00} "
        + $"· {Engine.GetFramesPerSecond():0}fps";

    protected override void SimTick(double delta)
    {
        UpdateCamera();
        _dyeTex.TextureRdRid = FieldRid;
        _divTex.TextureRdRid = ArtifactRid;

        var g = Grid3;

        // pour source this tick (amt 0 = none) — from the TOP, pushing down
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
            // sparse bursts on SIM time (scene 32's 30-in-600 ticks = 0.5 s in 10 s):
            // the structure needs clear space around it to read
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

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        // extra source passes: orbiting stir + wandering jostle (dye 0 — velocity only)
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

        // THE A/B: the artifact is truncated Jacobi, the reference is the multigrid path.
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

    // Mouse ray against the horizontal plane at the box's top — where a pour lands.
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
        ui.AddSlider("Viscosity iters", 4, 40, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Dye fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        AddCellLockedSlider(ui, "Jacobi K (truncation)", 4, 200, _k, v => _k = (int)v);
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
