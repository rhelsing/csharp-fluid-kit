using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 04 — Phase 2: on-grid ideal sources ("circuit vocabulary"). Same reusable
// GpuStampSolver and the same damped-wave medium as scene 03, but the physics stamp
// (shaders/stamp/stamp_flow.glslinc) now places the two MNA sources on the grid:
//   FAUCET = a persistent CURRENT source (pours into rhs) · DRAIN = a Dirichlet PIN
//   / ideal VOLTAGE source (a corner held to a fixed level).
// Pour in one corner, drain in another → the relaxation settles to the steady flow
// field — the resistor network of scene 20 at grid scale. "Keep the solver, change
// the circuit": only the stamp path differs from scene 03.
//
// Solver dropdown is Jacobi / RBGS only: those consume the stamp generically. CG's
// SpMV and the multigrid kernels inline the wave operator, so they can't solve this
// pinned system — deliberately omitted, not forgotten.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/04_stamp_flow.tscn 6 1280x900
public partial class StampFlow : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string StampPath = "res://shaders/stamp/stamp_flow.glslinc";
    private const string SurfaceShaderPath = "res://shaders/mna_wave_surface.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _surfaceMat = null!;
    private Texture2Drd _displayTex = null!;
    private IStampSolver? _solver;
    private Label? _readout;
    private int _tick;
    private int _currentMode = 1;   // 0 Jacobi · 1 RBGS  (default RBGS: fastest to steady state)
    private int _pendingMode = 1;

    // medium tunables (mapped into the push constant) — high damping so the pour
    // settles into the steady flow gradient rather than sloshing (damping changes only
    // the transient; the steady state is the same screened-Poisson solve), and near-zero
    // leak so the gradient spans the domain (the Dirichlet drain anchors the DC level).
    private float _waveSpeed = 0.903f;
    private float _dt = 0.401f;
    private float _damping = 1.4f;
    private float _leak = 0.018f;
    private int _iters = 24;

    // faucet = current source; drain = Dirichlet pin (voltage source). Positions in uv.
    // Drain held BELOW rest → a visible pit the flow runs down into.
    private Vector2 _srcUv = new(0.30f, 0.32f);
    private float _srcRadius = 15.825f;
    private float _srcStrength = 0.04f;
    private Vector2 _drnUv = new(0.70f, 0.68f);
    private float _drnRadius = 30.0f;
    private float _drnLevel = -1.0f;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = MakeSolver(_currentMode);
    }

    private IStampSolver MakeSolver(int mode)
    {
        var rd = RenderingServer.GetRenderingDevice();
        var solveMode = mode == 0 ? GpuStampSolver.Mode.Jacobi : GpuStampSolver.Mode.Rbgs;
        return new GpuStampSolver(rd, Grid, StampPath, solveMode);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 55.0f, Position = new Vector3(0, 5.2f, 7.6f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, -0.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-52, -46, 0),
            LightColor = new Color(1.0f, 0.95f, 0.88f),
            LightEnergy = 1.5f,
        });

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.28f, 0.46f, 0.78f),
            SkyHorizonColor = new Color(0.72f, 0.82f, 0.9f),
            GroundBottomColor = new Color(0.22f, 0.24f, 0.27f),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.0f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });

        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(PlaneSize + 0.6f, 0.4f, PlaneSize + 0.6f) },
            Position = new Vector3(0, -0.8f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.16f, 0.18f, 0.22f), Roughness = 0.9f },
        });
    }

    private void BuildWater()
    {
        _waterMi = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PlaneSize, PlaneSize), SubdivideWidth = 200, SubdivideDepth = 200 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4.0f,
        };

        _displayTex = new Texture2Drd();
        _surfaceMat = new ShaderMaterial { Shader = GD.Load<Shader>(SurfaceShaderPath) };
        _surfaceMat.SetShaderParameter("height_tex", _displayTex);
        _surfaceMat.SetShaderParameter("tex_size", new Vector2(Grid.X, Grid.Y));
        _surfaceMat.SetShaderParameter("plane_size", PlaneSize);
        _surfaceMat.SetShaderParameter("displacement", 0.56f);
        _surfaceMat.SetShaderParameter("normal_strength", 1.5f);
        _surfaceMat.SetShaderParameter("show_heightmap", 0.0f);
        _waterMi.MaterialOverride = _surfaceMat;
        AddChild(_waterMi);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_pendingMode != _currentMode)
        {
            _currentMode = _pendingMode;
            int m = _currentMode;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                _solver?.Free();
                _solver = MakeSolver(m);
            }));
            return;   // skip this tick; solver is being rebuilt on the render thread
        }

        // Left-click aims the faucet (drag the hose over the surface).
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var uv = MouseUv();
            if (uv.X >= 0.0f)
            {
                _srcUv = uv;
            }
        }

        _displayTex.TextureRdRid = _solver?.HeightRid ?? default; // default until InitSolver runs — harmless

        float beta = _dt * _dt * _waveSpeed * _waveSpeed;
        float a = _damping * _dt * 0.5f;
        int iters = _iters;
        // Push constant: 13 floats used by the stamp, padded to 16 (64B, a multiple of 16).
        float[] pc =
        {
            Grid.X, Grid.Y, beta, a, _leak,
            _srcUv.X * Grid.X, _srcUv.Y * Grid.Y, _srcRadius, _srcStrength,
            _drnUv.X * Grid.X, _drnUv.Y * Grid.Y, _drnRadius, _drnLevel,
            0f, 0f, 0f,
        };
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
        // Measure the residual at low rate (BufferGetData is a sync readback).
        _tick++;
        bool measure = _tick % 12 == 0;
        RenderingServer.CallOnRenderThread(Callable.From(() => _solver?.Step(bytes, iters, measure)));

        if (_readout != null && measure)
        {
            float res = _solver?.LastResidual ?? 0f;
            string mode = _solver?.ModeName ?? "—";
            _readout.Text = $"{mode} · ‖b−Ax‖ {res:E3} · {_iters} sw · {Engine.GetFramesPerSecond():0}fps";
        }
    }

    private Vector2 MouseUv()
    {
        var mp = GetViewport().GetMousePosition();
        var plane = new Plane(_waterMi.GlobalBasis.Y.Normalized(), _waterMi.GlobalPosition);
        if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return new Vector2(-1, -1);
        }
        Vector3 local = _waterMi.GlobalTransform.AffineInverse() * hit;
        var uv = new Vector2(local.X / PlaneSize + 0.5f, local.Z / PlaneSize + 0.5f);
        if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
        {
            return new Vector2(-1, -1);
        }
        return uv;
    }

    public override void _ExitTree()
    {
        if (_displayTex != null)
        {
            _displayTex.TextureRdRid = default;
        }
        RenderingServer.CallOnRenderThread(Callable.From(() => _solver?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "04 · Circuit sources on the grid (C#)",
            "Phase 2 — the same GpuStampSolver and damped-wave medium as scene 03, but the stamp now "
            + "places the two ideal MNA sources ON the grid. The FAUCET is a current source (a persistent "
            + "pour injected into b); the DRAIN is a Dirichlet PIN — an ideal voltage source holding one "
            + "corner to a fixed level. Relaxation settles to the steady flow field: a tilted gradient from "
            + "faucet to drain — the resistor network of scene 20 at grid scale. 'Keep the solver, change "
            + "the circuit': only the stamp path differs from 03. Left-click to aim the faucet. Solver is "
            + "Jacobi/RBGS (they consume the stamp generically; CG/multigrid inline the wave operator).");
        _readout = ui.AddReadout("residual —");
        ui.AddOptions("Solver", new[] { "Jacobi", "RBGS" }, _currentMode, idx => _pendingMode = idx);
        ui.AddToggle("Show height colormap", false,
            v => _surfaceMat.SetShaderParameter("show_heightmap", v ? 1.0f : 0.0f));
        ui.AddSlider("Faucet current", 0.0f, 2.0f, _srcStrength, v => _srcStrength = v);
        ui.AddSlider("Faucet radius", 3.0f, 30.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Drain level (voltage)", -1.0f, 1.0f, _drnLevel, v => _drnLevel = v);
        ui.AddSlider("Drain radius", 0.0f, 30.0f, _drnRadius, v => _drnRadius = v);
        ui.AddSlider("Wave speed (c)", 0.2f, 4.0f, _waveSpeed, v => _waveSpeed = v);
        ui.AddSlider("Sim dt (stable at ANY value)", 0.05f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Damping", 0.0f, 2.0f, _damping, v => _damping = v);
        ui.AddSlider("Rest leak κ", 0.0f, 0.2f, _leak, v => _leak = v);
        ui.AddSlider("Sweeps / tick", 1, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Displacement", 0.0f, 1.0f, 0.56f,
            v => _surfaceMat.SetShaderParameter("displacement", v));
    }
}
