using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 03 — MNA stamp solver on the GPU (matrix-free). C# port of water-kit scene
// 25, now on the reusable GpuStampSolver: the RD orchestration lives in
// scripts/lib/GpuStampSolver.cs and the physics in the GLSL stamp
// (shaders/stamp/stamp_wave.glslinc, behind st_diag/st_conductance/st_rhs). This
// scene owns only the stage, the per-tick push constant, the poke, and the UI —
// swapping the stamp path would change the physics with everything else untouched.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/03_mna_wave.tscn 6 1280x900
public partial class MnaWave : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string StampPath = "res://shaders/stamp/stamp_wave.glslinc";
    private const string SurfaceShaderPath = "res://shaders/mna_wave_surface.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _surfaceMat = null!;
    private Texture2Drd _displayTex = null!;
    private IStampSolver? _solver;
    private Label? _readout;
    private int _tick;
    private int _currentMode = 1;   // 0 Jacobi · 1 RBGS · 2 CG · 3 Multigrid — CG excepted, all now honour the scheme slider
    private int _pendingMode = 1;

    // implicit-wave tunables (mapped into the push constant)
    private float _waveSpeed = 2.518f;
    private float _dt = 0.401f;
    private float _damping = 0.01f;    // a hair of loss so lossless-CN ripples eventually bleed off
    private float _leak = 0.006f;      // restoring pull to rest (leak-to-ground) — kills the DC drift
    private int _iters = 30;
    private float _pokeRadius = 2.66f;
    private float _pokeStrength = 0.178f;
    private float _cn = 1.0f;           // scheme: 0 = backward-Euler (damped) · 1 = Crank-Nicolson (rings)

    private bool _auto = false;
    private float _pokeT;
    private const float PokeInterval = 0.45f;
    private int _pokeI;
    private static readonly Vector2[] Pokes =
    {
        new(0.35f, 0.4f), new(0.65f, 0.6f), new(0.5f, 0.5f),
        new(0.4f, 0.68f), new(0.66f, 0.34f),
    };

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
        return mode switch
        {
            0 => new GpuStampSolver(rd, Grid, StampPath, GpuStampSolver.Mode.Jacobi),
            1 => new GpuStampSolver(rd, Grid, StampPath, GpuStampSolver.Mode.Rbgs),
            2 => new GpuStampSolver(rd, Grid, StampPath, GpuStampSolver.Mode.Cg),
            _ => new MgvSolver(rd, Grid, StampPath),
        };
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
        _surfaceMat.SetShaderParameter("displacement", 0.35f);
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

        var poke = Vector4.Zero;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var uv = MouseUv();
            if (uv.X >= 0.0f)
            {
                poke = new Vector4(uv.X * Grid.X, uv.Y * Grid.Y, _pokeRadius, -_pokeStrength);   // inverted: poke pushes the surface down-first
            }
        }
        else if (_auto)
        {
            _pokeT += (float)delta;
            if (_pokeT >= PokeInterval)
            {
                _pokeT = 0.0f;
                var uv = Pokes[_pokeI];
                _pokeI = (_pokeI + 1) % Pokes.Length;
                poke = new Vector4(uv.X * Grid.X, uv.Y * Grid.Y, _pokeRadius, -_pokeStrength);   // inverted: poke pushes the surface down-first
            }
        }

        _displayTex.TextureRdRid = _solver?.HeightRid ?? default; // default until InitSolver runs — harmless

        float beta = _dt * _dt * _waveSpeed * _waveSpeed;
        float a = _damping * _dt * 0.5f;
        int iters = _iters;
        // Push constants must be a multiple of 16 bytes: 9 floats (36B) → pad to 48B (12 floats).
        float[] pc = { Grid.X, Grid.Y, beta, a, _leak, poke.X, poke.Y, poke.Z, poke.W, _cn, 0f, 0f };
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
        // Measure the residual at low rate (BufferGetData is a sync readback).
        _tick++;
        bool measure = _tick % 12 == 0;
        // Snapshot into the render-thread call (closure captures by value this tick).
        RenderingServer.CallOnRenderThread(Callable.From(() => _solver?.Step(bytes, iters, measure)));

        // Throttle the readout to measure ticks only (not every frame) — cheaper and
        // avoids churning the UI layout.
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
        var ui = new DemoUI(this, "03 · MNA stamp solver on the GPU (C#)",
            "The scene-20 stamp idea at grid scale: each cell a node, each edge a pipe, each cell a "
            + "capacitor → the implicit damped-wave system, solved MATRIX-FREE by GPU Jacobi relaxation "
            + "(K sweeps/tick, ping-ponged compute dispatches). Now on the reusable GpuStampSolver — the "
            + "physics is a swappable GLSL stamp. Backward-Euler ⇒ UNCONDITIONALLY STABLE: crank dt, it "
            + "never explodes. Auto-pokes for the shot; left-click to poke. The readout shows the "
            + "achieved residual ‖b−Ax‖ (L2) via a GPU reduction — drop the sweeps and watch it climb.");
        _readout = ui.AddReadout("residual —");
        ui.AddOptions("Solver", new[] { "Jacobi", "RBGS", "CG", "Multigrid" }, _currentMode,
            idx => _pendingMode = idx);
        ui.AddToggle("Auto-poke", _auto, v => _auto = v);
        ui.AddToggle("Show height colormap", false,
            v => _surfaceMat.SetShaderParameter("show_heightmap", v ? 1.0f : 0.0f));
        ui.AddSlider("Wave speed (c)", 0.2f, 4.0f, _waveSpeed, v => _waveSpeed = v);
        ui.AddSlider("Sim dt (stable at ANY value)", 0.05f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Damping", 0.0f, 2.0f, _damping, v => _damping = v);
        ui.AddSlider("Rest leak κ (kills drift)", 0.0f, 0.2f, _leak, v => _leak = v);
        ui.AddSlider("Scheme  BE 0 → 1 CN (rings)", 0.0f, 1.0f, _cn, v => _cn = v);
        ui.AddSlider("Sweeps / tick", 1, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Poke radius", 2.0f, 24.0f, _pokeRadius, v => _pokeRadius = v);
        ui.AddSlider("Poke strength", 0.1f, 4.0f, _pokeStrength, v => _pokeStrength = v);
        ui.AddSlider("Displacement", 0.0f, 1.0f, 0.35f,
            v => _surfaceMat.SetShaderParameter("displacement", v));
    }
}
