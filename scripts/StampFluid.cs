using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 07 — Phase 3c: PRESSURE-PROJECTION FLUID (Stam stable fluids). The one problem
// that DOESN'T fit the single-scalar stamp solver: a fluid needs a velocity field,
// advection, and a projection, not one A x = b. So the sim lives in a purpose-built
// scripts/lib/FluidSim.cs — but its pressure-projection step is the SAME matrix-free
// Jacobi relaxation the stamp solvers use, now solving ∇²p = div. Buoyant dye is
// injected, self-advects, and is kept divergence-free each tick → curling smoke.
// Left-click to inject; the shot auto-drives a wobbling plume.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/07_stamp_fluid.tscn 6 1280x900
public partial class StampFluid : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string SmokeShaderPath = "res://shaders/fluid_smoke.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _plane = null!;
    private ShaderMaterial _smokeMat = null!;
    private Texture2Drd _displayTex = null!;
    private FluidSim? _fluid;
    private Label? _readout;
    private int _tick;

    // sim tunables
    private float _dt = 1.0f;
    private int _iters = 40;
    private float _buoy = 2.5f;
    private float _injVel = 0.2f;      // upward injection at the source
    private float _dyeAmt = 0.12f;
    private float _srcRadius = 6.0f;
    private float _dissipV = 0.999f;
    private float _dissipD = 0.97f;    // fade dye fast so it stays wispy in the closed box
    private float _gain = 2.0f;

    private Vector2 _srcUv = new(0.5f, 0.16f);   // auto plume root (bottom-centre)

    public override void _Ready()
    {
        BuildEnvironment();
        BuildPlane();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        _fluid = new FluidSim(RenderingServer.GetRenderingDevice(), Grid);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 55.0f, Position = new Vector3(0, 5.2f, 7.6f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, -0.2f, 0), Vector3.Up);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.03f, 0.06f),
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
        _smokeMat = new ShaderMaterial { Shader = GD.Load<Shader>(SmokeShaderPath) };
        _smokeMat.SetShaderParameter("dye_tex", _displayTex);
        _smokeMat.SetShaderParameter("gain", _gain);
        _plane.MaterialOverride = _smokeMat;
        AddChild(_plane);
    }

    public override void _PhysicsProcess(double delta)
    {
        // auto plume: a wobbling buoyant source at the bottom (left-click overrides it)
        float wob = Mathf.Sin(_tick * 0.03f);
        float injVx = 0.5f * wob;
        var src = _srcUv;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var uv = MouseUv();
            if (uv.X >= 0.0f) { src = uv; injVx = 0.0f; }
        }
        else
        {
            src = new Vector2(0.5f + 0.08f * wob, 0.16f);
        }

        _displayTex.TextureRdRid = _fluid?.DyeRid ?? default;

        float[] add =
        {
            Grid.X, Grid.Y, _dt, _buoy,
            src.X * Grid.X, src.Y * Grid.Y, _srcRadius, injVx, _injVel, _dyeAmt,
            0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, _dt, _dissipV };
        float[] sim = { Grid.X, Grid.Y, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, _dt, _dissipD };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Step(addB, advVB, simB, advDB, iters)));

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"Stam fluid · {_iters} pressure iters · {Grid.X}² · {Engine.GetFramesPerSecond():0}fps";
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
        var plane = new Plane(_plane.GlobalBasis.Y.Normalized(), _plane.GlobalPosition);
        if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return new Vector2(-1, -1);
        }
        Vector3 local = _plane.GlobalTransform.AffineInverse() * hit;
        var uv = new Vector2(local.X / PlaneSize + 0.5f, local.Z / PlaneSize + 0.5f);
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
        var ui = new DemoUI(this, "07 · Pressure-projection fluid (C#)",
            "Phase 3c — Stam stable fluids, the problem that does NOT fit the single-scalar stamp solver: a "
            + "fluid needs a velocity field, advection, and a projection. So it's a purpose-built pipeline "
            + "(scripts/lib/FluidSim.cs) — advect velocity → take its divergence → solve ∇²p = div → subtract "
            + "∇p so the flow is divergence-free → advect the dye. The pressure solve is the SAME matrix-free "
            + "Jacobi relaxation the stamp solvers use, just embedded in the loop. Buoyant dye curls into "
            + "vortices. Left-click to inject; the shot auto-drives a plume.");
        _readout = ui.AddReadout("fluid —");
        ui.AddSlider("Buoyancy", 0.0f, 8.0f, _buoy, v => _buoy = v);
        ui.AddSlider("Inject velocity", 0.0f, 2.0f, _injVel, v => _injVel = v);
        ui.AddSlider("Dye amount", 0.0f, 1.5f, _dyeAmt, v => _dyeAmt = v);
        ui.AddSlider("Source radius", 3.0f, 24.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pressure iters", 4, 80, _iters, v => _iters = (int)v);
        ui.AddSlider("Advect dt", 0.2f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Velocity dissipation", 0.98f, 1.0f, _dissipV, v => _dissipV = v);
        ui.AddSlider("Dye dissipation", 0.95f, 1.0f, _dissipD, v => _dissipD = v);
        ui.AddSlider("Smoke gain", 0.5f, 4.0f, _gain,
            v => { _gain = v; _smokeMat.SetShaderParameter("gain", v); });
    }
}
