using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 05 — Phase 3a: implicit HEAT / diffusion. The "same solver, new circuit"
// generality proof — identical GpuStampSolver and surface shader as scenes 03/04,
// only the stamp path changes (shaders/stamp/stamp_heat.glslinc). Backward-Euler
// diffusion: unconditionally stable, but where the wave stamp ripples and the flow
// stamp settles, heat spreads monotonically. A persistent HOT source and a Dirichlet
// PIN (a fixed-temperature spot) drive it; shown as the divergent temperature colormap
// (blue = cold, white = ambient, red = hot). Left-click paints heat.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/05_stamp_heat.tscn 6 1280x900
public partial class StampHeat : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string StampPath = "res://shaders/stamp/stamp_heat.glslinc";
    private const string SurfaceShaderPath = "res://shaders/mna_wave_surface.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _surfaceMat = null!;
    private Texture2Drd _displayTex = null!;
    private IStampSolver? _solver;
    private Label? _readout;
    private int _tick;
    private int _currentMode = 1;   // 0 Jacobi · 1 RBGS
    private int _pendingMode = 1;

    // diffusion tunables (mapped into the push constant)
    private float _kappa = 0.5f;     // α·dt — diffusivity per step (spread rate)
    private float _inertia = 0.0f;   // thermal memory (0 = pure heat); also keeps h_prev bound
    private float _leak = 0.02f;     // leak to ambient — bounds an isolated hot blob
    private int _iters = 24;

    // hot source = current source; cold/hot spot = Dirichlet pin (voltage source). uv.
    private Vector2 _srcUv = new(0.35f, 0.40f);
    private float _srcRadius = 16.0f;
    private float _srcStrength = 0.08f;
    private Vector2 _pinUv = new(0.68f, 0.66f);
    private float _pinRadius = 16.0f;
    private float _pinLevel = -0.8f;

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
        _surfaceMat.SetShaderParameter("displacement", 0.3f);
        _surfaceMat.SetShaderParameter("normal_strength", 1.5f);
        _surfaceMat.SetShaderParameter("show_heightmap", 1.0f);   // temperature colormap by default
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

        // Left-click paints heat (drag the hot source over the surface).
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var uv = MouseUv();
            if (uv.X >= 0.0f)
            {
                _srcUv = uv;
            }
        }

        _displayTex.TextureRdRid = _solver?.HeightRid ?? default; // default until InitSolver runs — harmless

        int iters = _iters;
        // Push constant: 13 floats used by the stamp, padded to 16 (64B, a multiple of 16).
        float[] pc =
        {
            Grid.X, Grid.Y, _kappa, _inertia, _leak,
            _srcUv.X * Grid.X, _srcUv.Y * Grid.Y, _srcRadius, _srcStrength,
            _pinUv.X * Grid.X, _pinUv.Y * Grid.Y, _pinRadius, _pinLevel,
            0f, 0f, 0f,
        };
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
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
        var ui = new DemoUI(this, "05 · Implicit heat / diffusion (C#)",
            "Phase 3a — the cheapest 'same solver, new circuit' proof: identical GpuStampSolver and surface "
            + "shader as 03/04, only the stamp path changes. Backward-Euler heat is unconditionally stable "
            + "(crank the diffusivity, it never blows up), but where the wave stamp RIPPLES and the flow "
            + "stamp settles, heat spreads MONOTONELY. A persistent hot source pours in and a Dirichlet PIN "
            + "holds a fixed-temperature spot; shown as the divergent temperature colormap (blue cold · "
            + "white ambient · red hot). Left-click paints heat. Solver Jacobi/RBGS.");
        _readout = ui.AddReadout("residual —");
        ui.AddOptions("Solver", new[] { "Jacobi", "RBGS" }, _currentMode, idx => _pendingMode = idx);
        ui.AddToggle("Temperature colormap", true,
            v => _surfaceMat.SetShaderParameter("show_heightmap", v ? 1.0f : 0.0f));
        ui.AddSlider("Hot source power", 0.0f, 0.4f, _srcStrength, v => _srcStrength = v);
        ui.AddSlider("Hot source radius", 3.0f, 30.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pin temperature", -1.0f, 1.0f, _pinLevel, v => _pinLevel = v);
        ui.AddSlider("Pin radius", 0.0f, 30.0f, _pinRadius, v => _pinRadius = v);
        ui.AddSlider("Diffusivity κ", 0.02f, 2.0f, _kappa, v => _kappa = v);
        ui.AddSlider("Thermal memory", 0.0f, 1.0f, _inertia, v => _inertia = v);
        ui.AddSlider("Leak to ambient", 0.0f, 0.2f, _leak, v => _leak = v);
        ui.AddSlider("Sweeps / tick", 1, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Displacement", 0.0f, 1.0f, 0.3f,
            v => _surfaceMat.SetShaderParameter("displacement", v));
    }
}
