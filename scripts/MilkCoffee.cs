using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 18 — MILK IN COFFEE (docs/mna-next-steps.md §7, first build). Fluid-in-fluid:
// the same Stam pipeline as scene 07, plus the three things that make it read as milk
// instead of smoke:
//   · signed Boussinesq drift — the force scales with dye concentration, so milk is
//     "heavier-then-lighter as it mixes" (fs_add_milk)
//   · MacCormack (BFECC) dye advection — second-order, so filaments stay filaments
//     instead of diffusing to mush in ten frames (fs_maccormack; toggle to compare!)
//   · implicit viscosity — cream-thick without blowing up (fs_diffuse_vel)
// Left-click POURS (dye + radial splash), drag STIRS (velocity along the mouse).
// The auto-demo pours a wandering stream and stirs a slow spiral for the harness.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/18_milk_coffee.tscn 8 1280x900
public partial class MilkCoffee : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string ShaderPath = "res://shaders/milk_coffee.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _plane = null!;
    private ShaderMaterial _mat = null!;
    private Texture2Drd _displayTex = null!;
    private FluidSim? _fluid;
    private Label? _readout;
    private int _tick;
    private Vector2 _prevMouseUv = new(-1, -1);

    // sim tunables
    private float _dt = 1.0f;
    private int _iters = 40;
    private float _drift = -0.35f;     // signed: − sinks (cold milk), + rises (crema)
    private float _pourAmt = 0.55f;
    private float _pourRadius = 5.0f;
    private float _pourPush = 0.6f;
    private float _stirStrength = 3.0f;
    private float _stirRadius = 9.0f;
    private float _viscosity = 0.18f;  // ν·dt (cell units); 0 = watery
    private int _viscIters = 20;
    private bool _macCormack = true;
    private float _dissipD = 0.9995f;  // milk doesn't evaporate
    private float _dissipV = 0.999f;
    private bool _autoDemo = true;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildPlane();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid, "fs_add_milk", extras: true);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 40.0f, Position = new Vector3(0, 9.2f, 0.01f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(Vector3.Zero, Vector3.Forward);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.13f, 0.10f, 0.08f),
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildPlane()
    {
        _plane = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PlaneSize, PlaneSize) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4.0f,
        };
        _displayTex = new Texture2Drd();
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderPath) };
        _mat.SetShaderParameter("dye_tex", _displayTex);
        _mat.SetShaderParameter("texel", 1.0f / Grid.X);
        _plane.MaterialOverride = _mat;
        AddChild(_plane);
    }

    public override void _PhysicsProcess(double delta)
    {
        _displayTex.TextureRdRid = _fluid?.DyeRid ?? default;

        // sources this tick: pour (amt>0) and stir (vx/vy != 0)
        Vector2 pour = new(-1, -1);
        float pourAmt = 0f;
        Vector2 stirPos = new(0.5f, 0.5f);
        Vector2 stirVel = Vector2.Zero;

        var mouseUv = MouseUv();
        bool mouseDown = Input.IsMouseButtonPressed(MouseButton.Left);
        if (mouseDown && mouseUv.X >= 0f)
        {
            pour = mouseUv;
            pourAmt = _pourAmt;
            if (_prevMouseUv.X >= 0f)
            {
                // dragging: stir along the mouse motion
                Vector2 dm = (mouseUv - _prevMouseUv) * Grid.X;
                if (dm.Length() > 0.5f)
                {
                    stirPos = mouseUv;
                    stirVel = dm.LimitLength(6f) * _stirStrength * 0.3f;
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
                float t = _tick / 60.0f;
                if (_tick % 240 < 70)   // pour bursts from a wandering stream
                {
                    pour = new Vector2(0.5f + 0.16f * Mathf.Sin(t * 0.7f), 0.5f + 0.16f * Mathf.Cos(t * 0.53f));
                    pourAmt = _pourAmt;
                }
                // slow spiral stir, always on
                float sa = t * 0.9f;
                stirPos = new Vector2(0.5f + 0.22f * Mathf.Cos(sa), 0.5f + 0.22f * Mathf.Sin(sa));
                stirVel = new Vector2(-Mathf.Sin(sa), Mathf.Cos(sa)) * _stirStrength;
            }
        }

        float[] add =
        {
            Grid.X, Grid.Y, _dt, _drift,
            pour.X * Grid.X, pour.Y * Grid.Y, _pourRadius, pourAmt, _pourPush,
            stirPos.X * Grid.X, stirPos.Y * Grid.Y, _stirRadius, stirVel.X, stirVel.Y,
            0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, _dt, _dissipV };
        float[] sim = { Grid.X, Grid.Y, 0f, 0f };
        // MacCormack on: pass 1 runs dissip 1, real dissip lives in the MC pass
        float[] advD = { Grid.X, Grid.Y, _dt, _macCormack ? 1.0f : _dissipD };
        float[] visc = { Grid.X, Grid.Y, _viscosity, 0f };
        float[] mc = { Grid.X, Grid.Y, _dt, _dissipD };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        byte[] viscB = ToBytes(visc), mcB = ToBytes(mc);
        int iters = _iters;
        int viscIters = _viscosity > 0.0005f ? _viscIters : 0;
        bool mcOn = _macCormack;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            _fluid?.Step(addB, advVB, simB, advDB, iters, viscIters, viscB, mcOn, mcB)));

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"milk·coffee · {Grid.X}² · MC {(_macCormack ? "on" : "off")} · ν {_viscosity:0.00} · {Engine.GetFramesPerSecond():0}fps";
        }
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private Vector2 MouseUv()
    {
        var mp = GetViewport().GetMousePosition();
        var plane = new Plane(Vector3.Up, 0.0f);
        if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return new Vector2(-1, -1);
        }
        var uv = new Vector2(hit.X / PlaneSize + 0.5f, hit.Z / PlaneSize + 0.5f);
        if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) { return new Vector2(-1, -1); }
        return uv;
    }

    public override void _ExitTree()
    {
        if (_displayTex != null) { _displayTex.TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "18 · Milk in coffee (fluid-in-fluid)",
            "§7 first build — a dye that IS a fluid moving in a fluid. Same Stam pipeline as scene 07 "
            + "plus: signed Boussinesq drift (force ∝ concentration → heavier-then-lighter as it mixes), "
            + "MacCormack second-order advection (filaments stay sharp — toggle it to see the difference), "
            + "and implicit viscosity (cream-thick, unconditionally stable). Left-click pours; drag stirs.");
        _readout = ui.AddReadout("milk —");
        ui.AddToggle("Auto-demo (pour + spiral stir)", _autoDemo, on => _autoDemo = on);
        ui.AddToggle("MacCormack advection (sharp filaments)", _macCormack, on => _macCormack = on);
        ui.AddSlider("Viscosity ν·dt (0 = watery)", 0.0f, 1.5f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Density drift (− sinks, + rises)", -3.0f, 3.0f, _drift, v => _drift = v);
        ui.AddSlider("Pour amount", 0.0f, 1.5f, _pourAmt, v => _pourAmt = v);
        ui.AddSlider("Pour radius", 2.0f, 16.0f, _pourRadius, v => _pourRadius = v);
        ui.AddSlider("Pour splash (radial)", 0.0f, 3.0f, _pourPush, v => _pourPush = v);
        ui.AddSlider("Stir strength", 0.0f, 10.0f, _stirStrength, v => _stirStrength = v);
        ui.AddSlider("Stir radius", 3.0f, 24.0f, _stirRadius, v => _stirRadius = v);
        ui.AddSlider("Pressure iters", 4, 80, _iters, v => _iters = (int)v);
        ui.AddSlider("Viscosity iters", 4, 40, _viscIters, v => _viscIters = (int)v);
        ui.AddSlider("Milk fade", 0.99f, 1.0f, _dissipD, v => _dissipD = v);
        ui.AddSlider("Milk gamma (blend curve)", 0.2f, 3.0f, 0.85f, v => _mat.SetShaderParameter("milk_gamma", v));
        ui.AddSlider("Cream relief (fold lighting)", 0.0f, 3.0f, 1.0f, v => _mat.SetShaderParameter("relief", v));
    }
}
