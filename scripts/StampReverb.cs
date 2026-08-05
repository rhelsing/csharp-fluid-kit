using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 06 — Phase 3b: 2D PLATE REVERB — the literal audio-DSP analog. Same
// GpuStampSolver as 03/04/05; the stamp (shaders/stamp/stamp_reverb.glslinc) is the
// damped-wave operator with two plate touches: CLAMPED Dirichlet-0 edges (the frame, so
// reflections build the plate's standing modes) and very low damping (a long, ringing
// reverb tail). A strike excites many modes at once; they interfere and slowly decay —
// shown on the divergent colormap so the +/- mode pattern and nodal lines read clearly.
// Auto-strikes for the shot; left-click to strike.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/06_stamp_reverb.tscn 6 1280x900
public partial class StampReverb : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string StampPath = "res://shaders/stamp/stamp_reverb.glslinc";
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

    // plate tunables (mapped into the push constant) — stiff+fast, LIGHTLY damped for a
    // long reverberant tail.
    private float _waveSpeed = 3.0f;
    private float _dt = 0.3f;
    private float _damping = 0.03f;   // decay — small = the plate rings on
    private float _leak = 0.001f;
    private int _iters = 24;
    private float _strikeRadius = 4.0f;
    private float _strikeStrength = 0.8f;

    private bool _auto = true;
    private float _strikeT;
    private const float StrikeInterval = 0.35f;   // dense strikes → an accumulating modal field
    private int _strikeI;
    private static readonly Vector2[] Strikes =
    {
        new(0.32f, 0.36f), new(0.70f, 0.30f), new(0.62f, 0.68f),
        new(0.36f, 0.66f), new(0.50f, 0.48f),
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
        _surfaceMat.SetShaderParameter("displacement", 0.25f);
        _surfaceMat.SetShaderParameter("normal_strength", 1.5f);
        _surfaceMat.SetShaderParameter("show_heightmap", 1.0f);   // mode colormap by default
        _surfaceMat.SetShaderParameter("colormap_gain", 6.0f);    // the modal tail is low-amplitude
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

        var strike = Vector4.Zero;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var uv = MouseUv();
            if (uv.X >= 0.0f)
            {
                strike = new Vector4(uv.X * Grid.X, uv.Y * Grid.Y, _strikeRadius, _strikeStrength);
            }
        }
        else if (_auto)
        {
            _strikeT += (float)delta;
            if (_strikeT >= StrikeInterval)
            {
                _strikeT = 0.0f;
                var uv = Strikes[_strikeI];
                _strikeI = (_strikeI + 1) % Strikes.Length;
                strike = new Vector4(uv.X * Grid.X, uv.Y * Grid.Y, _strikeRadius, _strikeStrength);
            }
        }

        _displayTex.TextureRdRid = _solver?.HeightRid ?? default; // default until InitSolver runs — harmless

        float beta = _dt * _dt * _waveSpeed * _waveSpeed;
        float a = _damping * _dt * 0.5f;
        int iters = _iters;
        // Push constant: 9 floats used by the stamp, padded to 12 (48B, a multiple of 16).
        float[] pc = { Grid.X, Grid.Y, beta, a, _leak, strike.X, strike.Y, strike.Z, strike.W, 0f, 0f, 0f };
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
        var ui = new DemoUI(this, "06 · 2D plate reverb (C#)",
            "Phase 3b — the literal audio-DSP analog behind the whole 'stamp = circuit' idea. Same "
            + "GpuStampSolver; the stamp is the damped-wave operator made into a PLATE: CLAMPED "
            + "Dirichlet-0 edges (the frame — reflections build the plate's standing modes) and very low "
            + "damping (a long, ringing reverb tail). A strike excites many modes that interfere and slowly "
            + "decay — shown on the divergent colormap so the +/- pattern and nodal lines read. Drop the "
            + "decay and the plate rings forever; raise it and the tail dies fast. Auto-strikes for the "
            + "shot; left-click to strike. Solver Jacobi/RBGS.");
        _readout = ui.AddReadout("residual —");
        ui.AddOptions("Solver", new[] { "Jacobi", "RBGS" }, _currentMode, idx => _pendingMode = idx);
        ui.AddToggle("Mode colormap", true,
            v => _surfaceMat.SetShaderParameter("show_heightmap", v ? 1.0f : 0.0f));
        ui.AddToggle("Auto-strike", _auto, v => _auto = v);
        ui.AddSlider("Tension / speed (c)", 0.5f, 4.0f, _waveSpeed, v => _waveSpeed = v);
        ui.AddSlider("Sim dt (stable at ANY value)", 0.05f, 1.0f, _dt, v => _dt = v);
        ui.AddSlider("Decay (damping)", 0.0f, 1.0f, _damping, v => _damping = v);
        ui.AddSlider("Strike radius", 2.0f, 20.0f, _strikeRadius, v => _strikeRadius = v);
        ui.AddSlider("Strike strength", 0.1f, 4.0f, _strikeStrength, v => _strikeStrength = v);
        ui.AddSlider("Sweeps / tick", 1, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Displacement", 0.0f, 1.0f, 0.25f,
            v => _surfaceMat.SetShaderParameter("displacement", v));
    }
}
