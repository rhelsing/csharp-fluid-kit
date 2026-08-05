using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 17 — Shorewaves: a C# port of the KP07 shallow-water breaking-wave solver from
// ../shorewaves (GDScript), built in toggleable LAYERS over a live FPS readout so the cost
// of every stage is a real-time A/B.
//   M0 stage · M1 KP07 solver (ShallowWaterKp) on a sloped beach (BeachScenario).
//   M2 — full water.gdshader surface (screen-space refraction + foam) with a Debug/Full
//     switch, and SSR/SSAO/SSIL/glow as individual toggles: turn the heavy post-processing
//     stack OFF and watch the FPS jump while the shader's own refraction still carries the
//     look (the "raytracing for performance" thesis, made measurable).
//   M3 — wet-sand terrain driven by the ground-memory pass + a resolution-scale slider;
//     SPACE fires a solitary wave.
// Verify (render, then Read the PNG):
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/17_shorewaves.tscn 8 1600x900
public partial class Shorewaves : Node3D
{
    private const float Domain = ShallowWaterKp.Domain;   // 30.4 m
    private const float Half = Domain * 0.5f;
    private const string ShaderDir = "res://shaders/shorewaves/";
    private const string TexDir = "res://textures/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _debugMat = null!;
    private ShaderMaterial _fullMat = null!;
    private Godot.Environment _env = null!;

    // shared sim-output textures (repointed each frame)
    private Texture2Drd _texState = null!;
    private Texture2Drd _texBottom = null!;
    private Texture2Drd _texDerived = null!;
    private Texture2Drd _texGround = null!;

    private ShallowWaterKp? _solver;
    private float[] _corners = System.Array.Empty<float>();
    private byte[] _bottomBytes = System.Array.Empty<byte>();
    private byte[] _stateBytes = System.Array.Empty<byte>();

    // sim orchestration (main thread) — mirrors SimController._process
    private int _parity;
    private int _gparity;
    private float _accum;
    private float _simTime;
    private float _lastSimTime;
    private float _fpsAccum;
    private bool _running = true;
    private bool _fireSolitary;

    private Label? _readout;

    public override void _Ready()
    {
        _corners = BeachScenario.BuildCorners();
        float[] bottomFloats = BeachScenario.BuildBottomFloats(_corners);
        _bottomBytes = BeachScenario.FloatsToBytes(bottomFloats);
        _stateBytes = BeachScenario.StateBytesFromBottom(bottomFloats);

        BuildEnvironment();
        BuildTerrain();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new ShallowWaterKp(RenderingServer.GetRenderingDevice(), _bottomBytes, _stateBytes);
    }

    private void BuildEnvironment()
    {
        // free-fly camera: start at a nice over-the-beach angle, then RMB-look + WASD to roam
        _cam = new FreeCam { Fov = 55.0f, Far = 4000.0f, Current = true, Speed = 8.0f };
        AddChild(_cam);
        _cam.LookAtFromPosition(new Vector3(24.0f, 3.5f, 6.0f), new Vector3(16.0f, 0.0f, 15.0f), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48.0f, 142.0f, 0.0f),
            LightColor = new Color(1.0f, 0.96f, 0.9f),
            LightEnergy = 1.35f,
            ShadowEnabled = true,
        });

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.52f, 0.82f),
            SkyHorizonColor = new Color(0.75f, 0.83f, 0.90f),
            GroundBottomColor = new Color(0.42f, 0.45f, 0.42f),
        };
        // Post-fx all provisioned but OFF — the toggles below turn them on so their cost is
        // visible against the shader's built-in refraction.
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.0f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            SsrEnabled = false, SsrMaxSteps = 64,
            SsaoEnabled = false, SsaoRadius = 1.0f, SsaoIntensity = 2.0f,
            SsilEnabled = false, SsilRadius = 5.0f,
            GlowEnabled = false, GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Screen,
            GlowHdrThreshold = 1.2f, GlowIntensity = 0.4f,
        };
        AddChild(new WorldEnvironment { Environment = _env });
    }

    private void BuildTerrain()
    {
        _texGround = new Texture2Drd();
        var sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        sandMat.SetShaderParameter("tx_ground", _texGround);
        AddChild(new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = BeachScenario.BuildTerrainMesh(_corners),   // real sloped bed, world-space verts
            Position = Vector3.Zero,
            MaterialOverride = sandMat,
        });
    }

    private void BuildWater()
    {
        _texState = new Texture2Drd();
        _texBottom = new Texture2Drd();
        _texDerived = new Texture2Drd();

        _debugMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "debug_surface.gdshader") };
        _debugMat.SetShaderParameter("tx_state", _texState);
        _debugMat.SetShaderParameter("tx_bottom", _texBottom);

        _fullMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water.gdshader") };
        _fullMat.SetShaderParameter("tx_state", _texState);
        _fullMat.SetShaderParameter("tx_bottom", _texBottom);
        _fullMat.SetShaderParameter("tx_derived", _texDerived);
        _fullMat.SetShaderParameter("lace_tex", GD.Load<Texture2D>(TexDir + "voronoi_lace.png"));
        _fullMat.SetShaderParameter("flow_noise_tex", GD.Load<Texture2D>(TexDir + "flow_noise.png"));
        _fullMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _fullMat.SetShaderParameter("GRID_RES", (float)ShallowWaterKp.N);

        _waterMi = new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(Domain, Domain), SubdivideWidth = 606, SubdivideDepth = 606 },
            Position = new Vector3(Half, 0.0f, Half),
            ExtraCullMargin = 40.0f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _fullMat,   // start on the full surface
        };
        AddChild(_waterMi);
    }

    public override void _Process(double delta)
    {
        StepSim((float)delta);
        UpdateReadout((float)delta);
    }

    private void StepSim(float delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        _texBottom.TextureRdRid = solver.BottomRid;
        _texDerived.TextureRdRid = solver.DerivedRid;

        if (!_running) { _accum = 0.0f; return; }

        _accum += Mathf.Min(delta, 0.1f);
        int steps = Mathf.Clamp((int)(_accum / ShallowWaterKp.Dt), 0, ShallowWaterKp.MaxSubsteps);
        _accum -= steps * ShallowWaterKp.Dt;
        if (steps == 0) { return; }

        int finalParity = (_parity + steps) % 2;
        int finalGparity = _gparity ^ 1;
        // repoint displayed textures to the FINAL parity on the main thread, before queueing
        // the render-thread step (exactly as SimController does).
        _texState.TextureRdRid = solver.StateRids[finalParity];
        _texGround.TextureRdRid = solver.GroundRids[finalGparity];

        bool solitary = _fireSolitary;
        _fireSolitary = false;
        int parity = _parity, gparity = _gparity;
        float t0 = _simTime;
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.Step(steps, t0, parity, solitary, gparity)));

        _parity = finalParity;
        _gparity = finalGparity;
        _simTime += steps * ShallowWaterKp.Dt;
    }

    private void UpdateReadout(float delta)
    {
        _fpsAccum += delta;
        if (_readout == null || _fpsAccum < 0.5f) { return; }
        float simRate = (_simTime - _lastSimTime) / _fpsAccum;
        _lastSimTime = _simTime;
        _fpsAccum = 0.0f;
        string dbg = _solver is { Ready: true }
            ? $"vol {_solver.DbgVolume:0} m³ · maxh {_solver.DbgMaxH:0.00} · nan {_solver.DbgNan:0}"
            : "solver init…";
        _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · sim ×{simRate:0.00}\n{dbg}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space })
        {
            _fireSolitary = true;
        }
    }

    public override void _ExitTree()
    {
        foreach (var t in new[] { _texState, _texBottom, _texDerived, _texGround })
        {
            if (t != null) { t.TextureRdRid = default; }
        }
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "17 · Shorewaves — KP07 shallow water (C#)",
            "C# port of the ../shorewaves breaking-wave solver (BEACH). Real KP07 fluid on the GPU, "
            + "full water surface with screen-space refraction + flow-advected foam lace, wet-sand shore "
            + "from the ground-memory pass. SPACE fires a solitary wave. Flip the post-fx OFF and watch "
            + "the FPS climb while refraction still carries the look — the perf thesis, live. "
            + "Free cam: RMB look · WASD · Q/E down-up · Shift fast.");
        _readout = ui.AddReadout("— fps");
        ui.AddOptions("Surface", new[] { "Debug", "Full" }, 1,
            idx => _waterMi.MaterialOverride = idx == 0 ? _debugMat : _fullMat);
        ui.AddToggle("Run sim", _running, v => _running = v);
        ui.AddToggle("SSR (screen-space reflections)", false, v => _env.SsrEnabled = v);
        ui.AddToggle("SSAO", false, v => _env.SsaoEnabled = v);
        ui.AddToggle("SSIL", false, v => _env.SsilEnabled = v);
        ui.AddToggle("Glow (foam bloom)", false, v => _env.GlowEnabled = v);
        ui.AddSlider("Resolution scale", 0.5f, 1.0f, 1.0f, v => GetViewport().Scaling3DScale = v);
    }
}
