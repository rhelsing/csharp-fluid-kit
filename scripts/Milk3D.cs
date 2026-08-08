using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 32 — MILK IN COFFEE, 3D (§7 in a glass). The side view scene 18 can't do:
// milk poured from above SINKS through clear coffee, mushrooms off the bottom, and
// curls back up as it dilutes — real volumetric plumes, raymarched. Same three milk
// ingredients as scene 18, in 3D (f3_maccormack / f3_diffuse_vel / signed buoyancy),
// and the pressure projection can run on DEEP MULTIGRID (MgDeepSolver3D — the §10
// pressure-path swap) vs plain Jacobi, a live A/B toggle.
// Left-click pours at the clicked x/z; the auto-demo pours wandering bursts.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/32_milk_3d.tscn 8 1280x900
public partial class Milk3D : Node3D
{
    private static readonly Vector3I Grid = new(56, 84, 56);
    private static readonly Vector3 BoxExtents = new(1.4f, 2.1f, 1.4f);
    private const string ShaderPath = "res://shaders/milk_glass.gdshader";

    private Camera3D _cam = null!;
    private ShaderMaterial _mat = null!;
    private Texture3Drd _dyeTex = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private int _tick;

    // sim tunables
    private float _dt = 1.0f;
    private int _iters = 24;
    private float _drift = 0.24f;      // tuned POSITIVE: pour pushes down, gentle buoyancy lifts back — the billow loop
    private float _pourAmt = 0.12f;
    private float _pourRadius = 3.19f;
    private float _pourSpeed = 1.5f;   // downward injection velocity
    private float _viscosity = 0.38f;  // creamy
    private int _viscIters = 12;
    private bool _macCormack = true;
    private bool _useMg = false;   // Ryan's verdict: Jacobi WINS aesthetically — under-converged projection = soft billowy cushioning; MG's accuracy reads harsher. The A/B earned its keep.
    private float _dissipD = 0.996f;   // old milk clears between pours
    private float _dissipV = 0.999f;
    private bool _autoDemo = true;

    // time + camera + swirl controls
    private float _timeScale = 0.3473f; // slow motion — SL error shrinks with step size, so slow-mo is also SHARPER
    private float _camYaw = 0f;         // degrees around the glass
    private float _camPitch = 12.14f;   // degrees above horizontal
    private float _camDist = 7.785f;
    private bool _stirOn = true;        // tuned: fast, low, wide, gentle stir
    private float _stirStrength = 0.6f;
    private float _stirOrbit = 0.704f;  // orbit radius, fraction of glass radius
    private float _stirHeight = 0.1445f; // fraction of glass height — stirring low
    private float _stirSpeed = 2.13f;   // orbit rad/s
    private float _stirRadius = 4.0f;   // source blob radius (cells)
    private float _jostle = 0.23f;      // wandering whole-glass push (nudging the cup)
    private float _curlEps = 0.96f; // vorticity confinement (tuned; 2.5 was a hurricane — works higher with the gentle stir)      // vorticity confinement — the roll-and-curl knob
    private bool _freeSlip = true;      // walls: slide along, don't stick (mushroom roll-up)
    private float _stirPhase;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildGlass();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid, extras: true);
        _fluid.EnableMultigrid();
        _fluid.UseMultigrid = _useMg;
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 38.0f, Far = 200.0f, Current = true };
        AddChild(_cam);
        UpdateCamera();

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.16f, 0.13f, 0.11f),
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildGlass()
    {
        // the raymarched volume (a box mesh whose fragment shader marches the dye field)
        var vol = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = BoxExtents * 2.0f },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4.0f,
        };
        _dyeTex = new Texture3Drd();
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _mat.SetShaderParameter("dye_tex", _dyeTex);
        _mat.SetShaderParameter("box_extents", BoxExtents);
        vol.MaterialOverride = _mat;
        AddChild(vol);

        // a simple glass shell around the cylinder mask
        var glass = new MeshInstance3D
        {
            Mesh = new CylinderMesh
            {
                TopRadius = BoxExtents.X * 0.98f, BottomRadius = BoxExtents.X * 0.92f,
                Height = BoxExtents.Y * 2.0f + 0.15f, RadialSegments = 48,
            },
            MaterialOverride = new StandardMaterial3D
            {
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.85f, 0.90f, 0.95f, 0.07f),
                Roughness = 0.04f, Metallic = 0.1f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        AddChild(glass);
    }

    private void UpdateCamera()
    {
        float yaw = Mathf.DegToRad(_camYaw);
        float pitch = Mathf.DegToRad(_camPitch);
        var target = new Vector3(0f, 0.2f, 0f);
        var pos = target + new Vector3(
            Mathf.Sin(yaw) * Mathf.Cos(pitch),
            Mathf.Sin(pitch),
            Mathf.Cos(yaw) * Mathf.Cos(pitch)) * _camDist;
        _cam.Position = pos;
        _cam.LookAt(target, Vector3.Up);
    }

    public override void _PhysicsProcess(double delta)
    {
        UpdateCamera();
        _dyeTex.TextureRdRid = _fluid?.DensityRid ?? default;

        // pour source this tick (dye_amt 0 = none)
        Vector3 src = Vector3.Zero;
        float amt = 0f;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && MouseXz() is Vector2 mxz)
        {
            src = new Vector3(
                Mathf.Clamp((mxz.X / (BoxExtents.X * 2.0f) + 0.5f) * Grid.X, 4, Grid.X - 4),
                Grid.Y - 6,
                Mathf.Clamp((mxz.Y / (BoxExtents.Z * 2.0f) + 0.5f) * Grid.Z, 4, Grid.Z - 4));
            amt = _pourAmt;
        }
        else if (_autoDemo && _tick % 600 < 30)   // sparse pour bursts — structure needs dark coffee around it
        {
            float t = _tick / 60.0f;
            src = new Vector3(
                Grid.X * (0.5f + 0.2f * Mathf.Sin(t * 0.6f)),
                Grid.Y - 6,
                Grid.Z * (0.5f + 0.2f * Mathf.Cos(t * 0.47f)));
            amt = _pourAmt;
        }

        float dt = _dt * _timeScale;   // one knob = slow motion, all passes coherent
        // dissipation is per-tick — tie it to SIM time so the haze-to-plume ratio is
        // timescale-invariant (else slow-mo drowns in old dilute milk)
        float fadeD = Mathf.Pow(_dissipD, _timeScale);
        float fadeV = Mathf.Pow(_dissipV, _timeScale);
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f, dt, _drift,
            src.X, src.Y, src.Z, _pourRadius, 0f, -_pourSpeed, 0f, amt, 0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, dt, fadeV, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, _freeSlip ? 1f : 0f, 0f, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, Grid.Z, 0f, dt, _macCormack ? 1.0f : fadeD, 0f, 0f };
        float[] visc = { Grid.X, Grid.Y, Grid.Z, 0f, _viscosity * _timeScale, 0f, 0f, 0f };
        float[] mc = { Grid.X, Grid.Y, Grid.Z, 0f, dt, fadeD, 0f, 0f };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);

        // extra sources: orbiting spoon stir + wandering jostle (dye 0 — velocity only)
        var extras = new System.Collections.Generic.List<byte[]>();
        if (_stirOn && _stirStrength > 0.01f)
        {
            _stirPhase += _stirSpeed * (float)delta * _timeScale * 60f / 60f;
            float or0 = _stirOrbit * Grid.X * 0.5f * 0.9f;
            float sx = Grid.X * 0.5f + Mathf.Cos(_stirPhase) * or0;
            float sz = Grid.Z * 0.5f + Mathf.Sin(_stirPhase) * or0;
            float sy = Mathf.Clamp(_stirHeight, 0.05f, 0.95f) * Grid.Y;
            var tang = new Vector2(-Mathf.Sin(_stirPhase), Mathf.Cos(_stirPhase)) * _stirStrength;
            extras.Add(ToBytes(new[]
            {
                Grid.X, Grid.Y, Grid.Z, 0f, dt, 0f,
                sx, sy, sz, _stirRadius, tang.X, 0f, tang.Y, 0f, 0f, 0f,
            }));
        }
        if (_jostle > 0.005f)
        {
            float jt = _tick / 60.0f;
            var jv = new Vector2(Mathf.Sin(jt * 1.7f), Mathf.Cos(jt * 1.3f)) * _jostle * 0.12f;
            extras.Add(ToBytes(new[]
            {
                Grid.X, Grid.Y, Grid.Z, 0f, dt, 0f,
                Grid.X * 0.5f, Grid.Y * 0.5f, Grid.Z * 0.5f, Grid.X * 1.0f, jv.X, 0f, jv.Y, 0f, 0f, 0f,
            }));
        }
        byte[][]? extraArr = extras.Count > 0 ? extras.ToArray() : null;
        byte[]? confB = _curlEps > 0.01f
            ? ToBytes(new[] { Grid.X, Grid.Y, Grid.Z, 0f, dt, _curlEps, 0f, 0f })
            : null;
        int iters = _iters;
        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseMultigrid = _useMg;
            _fluid.Step(addB, advVB, simB, advDB, iters, false, viscIters, viscB, mcOn, mcB, extraArr, confB);
        }));

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"milk 3D · {Grid.X}×{Grid.Y}×{Grid.Z} · t×{_timeScale:0.00} · "
                + $"{(_useMg ? "deep MG" : $"Jacobi {_iters}")} · MC {(_macCormack ? "on" : "off")} · "
                + $"{Engine.GetFramesPerSecond():0}fps";
        }
    }

    private Vector2? MouseXz()
    {
        var mp = GetViewport().GetMousePosition();
        var plane = new Plane(new Vector3(0, 0, 1), 0.0f);   // pour position from the z=0 plane
        if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return null;
        }
        return new Vector2(hit.X, 0.0f);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    public override void _ExitTree()
    {
        if (_dyeTex != null) { _dyeTex.TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "32 · Milk in coffee — 3D glass",
            "§7 in 3D: milk poured from above sinks through clear coffee, mushrooms off the bottom, "
            + "and curls as it dilutes — raymarched volumetric plumes. Same milk ingredients as scene 18 "
            + "(signed density, MacCormack, implicit viscosity), plus the §10 pressure-path swap: "
            + "DEEP MULTIGRID vs Jacobi projection as a live A/B. Left-click pours at the clicked spot.");
        _readout = ui.AddReadout("milk 3D —");
        ui.AddToggle("Auto-demo (pour bursts)", _autoDemo, on => _autoDemo = on);
        ui.AddSlider("TIME scale (slow-mo; also sharper)", 0.05f, 1.5f, _timeScale, v => _timeScale = v);
        ui.AddSlider("Camera · yaw (deg)", 0f, 360f, _camYaw, v => _camYaw = v);
        ui.AddSlider("Camera · pitch (deg)", 2f, 80f, _camPitch, v => _camPitch = v);
        ui.AddSlider("Camera · distance", 3f, 14f, _camDist, v => _camDist = v);
        ui.AddToggle("Stir · spoon on (orbiting)", _stirOn, on => _stirOn = on);
        ui.AddSlider("Stir · strength", 0f, 4f, _stirStrength, v => _stirStrength = v);
        ui.AddSlider("Stir · orbit radius (frac)", 0.1f, 0.9f, _stirOrbit, v => _stirOrbit = v);
        ui.AddSlider("Stir · height (frac)", 0.05f, 0.95f, _stirHeight, v => _stirHeight = v);
        ui.AddSlider("Stir · speed (rad/s)", 0f, 3f, _stirSpeed, v => _stirSpeed = v);
        ui.AddSlider("Jostle (nudge the glass)", 0f, 2f, _jostle, v => _jostle = v);
        ui.AddSlider("CURL (vorticity confinement)", 0f, 1.5f, _curlEps, v => _curlEps = v);
        ui.AddToggle("Walls · free-slip (roll-up)", _freeSlip, on => _freeSlip = on);
        ui.AddToggle("Pressure · deep multigrid (off = Jacobi)", _useMg, on => _useMg = on);
        ui.AddToggle("MacCormack advection", _macCormack, on => _macCormack = on);
        ui.AddSlider("Viscosity ν·dt", 0.0f, 1.0f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Density drift (− sinks)", -3.0f, 1.0f, _drift, v => _drift = v);
        ui.AddSlider("Pour amount", 0.0f, 1.5f, _pourAmt, v => _pourAmt = v);
        ui.AddSlider("Pour radius", 1.5f, 8.0f, _pourRadius, v => _pourRadius = v);
        ui.AddSlider("Pour speed (down)", 0.0f, 4.0f, _pourSpeed, v => _pourSpeed = v);
        ui.AddSlider("Jacobi iters (MG off)", 8, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Viscosity iters", 4, 30, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Milk fade (per sim-s; low = clears)", 0.98f, 1.0f, _dissipD, v => _dissipD = v);
        ui.AddSlider("Render · milk density", 1.0f, 40.0f, 8.0f, v => _mat.SetShaderParameter("density", v));
        ui.AddSlider("Render · dilute knee", 0.0f, 0.15f, 0.03f, v => _mat.SetShaderParameter("knee", v));
        ui.AddSlider("Render · coffee absorb", 0.0f, 2.0f, 0.45f, v => _mat.SetShaderParameter("absorb", v));
        ui.AddSlider("Render · shade", 0.0f, 1.0f, 0.4f, v => _mat.SetShaderParameter("shade", v));
        ui.AddSlider("Render · march steps", 16, 96, 56, v => _mat.SetShaderParameter("steps", v));
    }
}
