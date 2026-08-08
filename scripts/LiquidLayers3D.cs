using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 33 — LIQUID LAYERS, 3D. A FORK of scene 32; 32 is untouched and stays the milk scene.
//
// Three things 32 cannot do, and each needed one fork rather than a rewrite:
//
//  1. A FREE SURFACE. LiquidSim3D swaps in f3_pressure_free — p = 0 at air cells. 32 is milk IN
//     coffee: fully filled, no air, so its pressure solve never has to hold anything up. Give a
//     sealed solver a liquid with a TOP and it drains, regardless of iteration count.
//
//  2. A MOVABLE CONTAINER. Gravity is a vector in the FLUID's frame, so tilting the glass makes
//     it run downhill — and an ACCELERATING glass adds the -a pseudo-force, which is what makes
//     liquid pile against a wall when you shove the cup. Tilt and shove are different physics
//     and both are here.
//
//  3. LAYERS. The scalar field is DENSITY, not dye, and buoyancy is (rho - ambient)*g. Water,
//     oil and fog are one field at different values; they stratify by themselves with no
//     per-phase logic. Heavy sinks, light rises, they find their own levels — and the light
//     layer on top is free to curl.
//
// Everything 32 earned is kept: MacCormack advection, implicit viscosity, vorticity confinement,
// free-slip walls, and JACOBI as the pressure solver of record. That last one is not laziness —
// it is scene 32's measured verdict: an under-converged projection leaves residual divergence
// that cushions the flow into billows, and multigrid's honest incompressibility reads harsher.
// Accuracy is not aesthetics.
//
// Original header follows.
//
// Scene 32 — MILK IN COFFEE, 3D (§7 in a glass). The side view scene 18 can't do:
// milk poured from above SINKS through clear coffee, mushrooms off the bottom, and
// curls back up as it dilutes — real volumetric plumes, raymarched. Same three milk
// ingredients as scene 18, in 3D (f3_maccormack / f3_diffuse_vel / signed buoyancy),
// and the pressure projection can run on DEEP MULTIGRID (MgDeepSolver3D — the §10
// pressure-path swap) vs plain Jacobi, a live A/B toggle.
// Left-click pours at the clicked x/z; the auto-demo pours wandering bursts.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/32_milk_3d.tscn 8 1280x900
public partial class LiquidLayers3D : Node3D
{
    private static readonly Vector3I Grid = new(56, 84, 56);
    private static readonly Vector3 BoxExtents = new(1.4f, 2.1f, 1.4f);
    private const string ShaderPath = "res://shaders/milk_glass.gdshader";

    private Camera3D _cam = null!;
    private ShaderMaterial _mat = null!;
    private Texture3Drd _dyeTex = null!;
    private LiquidSim3D? _fluid;
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
    private float _camDist = 11.5f;   // pulled back: 32's framing starts inside this vessel
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

    // ---- container (tilt + shove) ----
    private float _tiltX, _tiltZ;          // degrees
    private float _shoveX, _shoveZ;        // pseudo-force from accelerating the vessel
    private bool _rock;                    // auto-rock, so sloshing is visible without hands
    private float _rockAmp = 14f, _rockHz = 0.28f, _rockT;

    // ---- layers ----
    private float _ambient = 0.05f;        // what counts as neutral: below this is air
    private float _airThresh = 0.08f;      // free-surface cutoff
    private float _pourDensity = 1.0f;     // what the tap pours: 1.0 water, ~0.8 oil, ~0.15 fog
    private float _fillLevel = 0.4f;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildGlass();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        _fluid = new LiquidSim3D(RenderingServer.GetRenderingDevice(), Grid, extras: true);
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
        if (_rock)
        {
            // Rocking the VESSEL, not the fluid. Tilt swings gravity's direction in the fluid
            // frame; the derivative of that motion is the shove. Together they are what a hand
            // moving a glass actually does.
            _rockT += (float)delta * _rockHz * Mathf.Tau;
            _tiltX = Mathf.Sin(_rockT) * _rockAmp;
            _tiltZ = Mathf.Cos(_rockT * 0.7f) * _rockAmp * 0.6f;
            _shoveX = Mathf.Cos(_rockT) * _rockAmp * _rockHz * 0.02f;
            _shoveZ = -Mathf.Sin(_rockT * 0.7f) * _rockAmp * _rockHz * 0.014f;
        }

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
        // Gravity in the FLUID's frame: world-down rotated by the inverse container tilt, plus
        // the -a pseudo-force from shoving the vessel. Tilting and shoving are different things
        // and both belong here.
        var basis = new Basis(Vector3.Right, Mathf.DegToRad(_tiltX))
                  * new Basis(Vector3.Back, Mathf.DegToRad(_tiltZ));
        Vector3 g = basis.Inverse() * Vector3.Down;
        g += new Vector3(-_shoveX, 0f, -_shoveZ);
        if (g.LengthSquared() > 1e-6f) { g = g.Normalized(); }

        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f,                       // size
            g.X, g.Y, g.Z, _drift,                            // gravity dir + strength
            src.X, src.Y, src.Z, _pourRadius,                 // source centre + radius
            0f, -_pourSpeed, 0f, amt * _pourDensity,          // injected velocity + density
            dt, _ambient, 0f, 0f,                             // dt, ambient density
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, dt, fadeV, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, _freeSlip ? 1f : 0f, _airThresh, 0f, 0f };
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
            _readout.Text = $"layers 3D · {Grid.X}×{Grid.Y}×{Grid.Z} · t×{_timeScale:0.00} · "
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
        var ui = new DemoUI(this, "33 · Liquid layers — free surface, movable vessel",
            "A FORK of scene 32 with three additions: a FREE SURFACE (p = 0 at air, so the liquid has a "
            + "top and can be held up), a MOVABLE VESSEL (tilt swings gravity in the fluid frame; shove "
            + "adds the -a pseudo-force, and they are different physics), and LAYERS (the field is "
            + "DENSITY, so water/oil/fog at different values stratify by themselves and the light one on "
            + "top is free to curl). Keeps 32's MacCormack, implicit viscosity, vorticity confinement, "
            + "free-slip walls and Jacobi-as-artist verdict.");
        _readout = ui.AddReadout("layers 3D —");
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
        // (DemoUI has no header helper — labels ride on the first slider of each group.)
        ui.AddToggle("— CONTAINER — ROCK the vessel", _rock, v => _rock = v);
        ui.AddSlider("Rock amplitude (deg)", 0f, 40f, _rockAmp, v => _rockAmp = v);
        ui.AddSlider("Rock rate (Hz)", 0.02f, 1.5f, _rockHz, v => _rockHz = v);
        ui.AddSlider("Tilt X (deg)", -45f, 45f, _tiltX, v => _tiltX = v);
        ui.AddSlider("Tilt Z (deg)", -45f, 45f, _tiltZ, v => _tiltZ = v);
        ui.AddSlider("Shove X", -0.6f, 0.6f, _shoveX, v => _shoveX = v);
        ui.AddSlider("Shove Z", -0.6f, 0.6f, _shoveZ, v => _shoveZ = v);
        
        ui.AddSlider("— LAYERS — pour density (1 water · .8 oil · .15 fog)", 0.05f, 1.6f, _pourDensity, v => _pourDensity = v);
        ui.AddSlider("Ambient density (neutral)", 0f, 0.5f, _ambient, v => _ambient = v);
        ui.AddSlider("Air threshold (free surface)", 0.01f, 0.5f, _airThresh, v => _airThresh = v);
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
