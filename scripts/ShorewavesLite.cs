using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 18 — Shorewaves LITE: a copy of scene 17 that swaps the heavy water.gdshader for a
// deliberately cheap surface (shaders/shorewaves/water_lite.gdshader) so the KP07 sim can
// run at high FPS. Same solver (ShallowWaterKp) + sloped beach (BeachScenario) + free cam.
// Scene 17 is frozen as the reference artifact; this is where visualization evolves.
//   Later: approximate the sim itself with an MNA stamp for more headroom.
// Verify (render, then Read the PNG):
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/18_shorewaves_lite.tscn 8 1600x900
public partial class ShorewavesLite : Node3D
{
    private const float Domain = ShallowWaterKp.DefaultDomain;   // 30.4 m
    private const float Half = Domain * 0.5f;
    private const string ShaderDir = "res://shaders/shorewaves/";
    private const string TexDir = "res://textures/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _waterMat = null!;
    private ShaderMaterial _sandMat = null!;

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
    private int _maxSub = ShallowWaterKp.DefaultMaxSubsteps;
    private bool _pokeQueued;
    private Vector2 _pokeXZ;
    private float _pokeStrength = 0.3f;
    private float _pokeRadius = 0.6f;

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
        // No post-processing — the whole point of the lite scene is a cheap frame.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 1.0f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildTerrain()
    {
        _texGround = new Texture2Drd();
        _sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        _sandMat.SetShaderParameter("tx_ground", _texGround);
        AddChild(new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = BeachScenario.BuildTerrainMesh(_corners),
            Position = Vector3.Zero,
            MaterialOverride = _sandMat,
        });
    }

    private void BuildWater()
    {
        _texState = new Texture2Drd();
        _texBottom = new Texture2Drd();
        _texDerived = new Texture2Drd();

        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water_lite.gdshader") };
        _waterMat.SetShaderParameter("tx_state", _texState);
        _waterMat.SetShaderParameter("tx_bottom", _texBottom);
        _waterMat.SetShaderParameter("tx_derived", _texDerived);
        _waterMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _waterMat.SetShaderParameter("GRID_RES", (float)ShallowWaterKp.DefaultN);
        _waterMat.SetShaderParameter("caustics_tex", GD.Load<Texture2D>(TexDir + "caustics.png"));

        _waterMi = new MeshInstance3D
        {
            Name = "Water",
            // Lower geometry than scene 17 (384 vs 606): per-pixel normals from the height
            // field carry the detail, so the mesh can be lighter.
            Mesh = new PlaneMesh { Size = new Vector2(Domain, Domain), SubdivideWidth = 384, SubdivideDepth = 384 },
            Position = new Vector3(Half, 0.0f, Half),
            ExtraCullMargin = 40.0f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _waterMat,
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
        int steps = Mathf.Clamp((int)(_accum / ShallowWaterKp.DefaultDt), 0, _maxSub);
        _accum -= steps * ShallowWaterKp.DefaultDt;
        if (steps == 0) { return; }

        int finalParity = (_parity + steps) % 2;
        int finalGparity = _gparity ^ 1;
        _texState.TextureRdRid = solver.StateRids[finalParity];
        _texGround.TextureRdRid = solver.GroundRids[finalGparity];

        bool solitary = _fireSolitary;
        _fireSolitary = false;
        bool poke = _pokeQueued;
        _pokeQueued = false;
        Vector2 pk = _pokeXZ;
        float pr = _pokeRadius, ps = _pokeStrength;
        int parity = _parity, gparity = _gparity;
        float t0 = _simTime;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
            solver.Step(steps, t0, parity, solitary, gparity, poke, pk.X, pk.Y, pr, ps)));

        _parity = finalParity;
        _gparity = finalGparity;
        _simTime += steps * ShallowWaterKp.DefaultDt;
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
        else if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            // [exp B4] left-click drops a ripple where the ray hits the still-water plane
            Vector2 xz = MouseWorldXZ();
            if (xz.X >= 0.0f) { _pokeXZ = xz; _pokeQueued = true; }
        }
    }

    private Vector2 MouseWorldXZ()
    {
        Vector2 mp = GetViewport().GetMousePosition();
        Vector3 o = _cam.ProjectRayOrigin(mp);
        Vector3 d = _cam.ProjectRayNormal(mp);
        if (Mathf.Abs(d.Y) < 1e-4f) { return new Vector2(-1, -1); }
        float t = -o.Y / d.Y;                       // intersect the y = 0 SWL plane
        if (t < 0.0f) { return new Vector2(-1, -1); }
        Vector3 hit = o + d * t;
        if (hit.X < 0.0f || hit.X > Domain || hit.Z < 0.0f || hit.Z > Domain) { return new Vector2(-1, -1); }
        return new Vector2(hit.X, hit.Z);
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
        var ui = new DemoUI(this, "18 · Shorewaves LITE — fast surface (C#)",
            "Copy of scene 17 with a deliberately cheap water shader (shine + Fresnel + smoothed "
            + "per-pixel normals, no refraction/foam) so the KP07 sim runs at high FPS. Same solver. "
            + "Scene 17 is the frozen reference. SPACE fires a solitary wave. "
            + "Free cam: RMB look · WASD · Q/E · Shift fast.");
        _readout = ui.AddReadout("— fps");
        ui.AddToggle("Run sim", _running, v => _running = v);

        ui.AddSection("Render");
        ui.AddOptions("Highlight (distinct color)", new[] { "None", "Foam", "Aeration", "Speed" }, 1,
            idx => _waterMat.SetShaderParameter("highlight_mode", idx));
        ui.AddSlider("Highlight amount", 0.0f, 1.0f, 0.9f, v => _waterMat.SetShaderParameter("highlight_amount", v));
        ui.AddSlider("Shine", 0.0f, 1.0f, 0.5f, v => _waterMat.SetShaderParameter("shine", v));
        ui.AddSlider("Fresnel", 0.0f, 1.0f, 0.6f, v => _waterMat.SetShaderParameter("fresnel_strength", v));
        ui.AddSlider("Normal smoothing", 0.5f, 6.0f, 1.5f, v => _waterMat.SetShaderParameter("normal_smooth", v));
        ui.AddSlider("Normal strength", 0.1f, 6.0f, 1.6f, v => _waterMat.SetShaderParameter("normal_strength", v));
        ui.AddSlider("Depth tint", 0.05f, 3.0f, 0.9f, v => _waterMat.SetShaderParameter("depth_scale", v));
        ui.AddSlider("Resolution scale", 0.5f, 1.0f, 1.0f, v => GetViewport().Scaling3DScale = v);

        ui.AddSection("Experiments (A/B toggles — you judge)");
        ui.AddToggle("Exp C · Bicubic normals", false, v => _waterMat.SetShaderParameter("use_bicubic", v));
        ui.AddToggle("Exp Det · FBM detail normals", false, v => _waterMat.SetShaderParameter("use_detail", v));
        ui.AddToggle("Exp D · K-field aperiodic drift", false, v => _waterMat.SetShaderParameter("use_kfield", v));
        ui.AddToggle("Exp F · Decorrelated phase", false, v => _waterMat.SetShaderParameter("use_decorrelate", v));
        ui.AddToggle("Exp B3 · Breaking heuristic foam", false, v => _waterMat.SetShaderParameter("use_break_heuristic", v));
        ui.AddSlider("Detail strength", 0.0f, 2.0f, 0.5f, v => _waterMat.SetShaderParameter("detail_strength", v));
        ui.AddSlider("Detail scale (m)", 0.05f, 2.0f, 0.35f, v => _waterMat.SetShaderParameter("detail_scale", v));
        ui.AddSlider("Detail speed", 0.0f, 4.0f, 1.0f, v => _waterMat.SetShaderParameter("detail_speed", v));
        ui.AddToggle("Exp R3 · Caustics in shallows", false, v => _waterMat.SetShaderParameter("use_caustics", v));
        ui.AddSlider("Caustic strength", 0.0f, 1.0f, 0.35f, v => _waterMat.SetShaderParameter("caustic_strength", v));
        ui.AddSlider("Caustic scale (m)", 0.5f, 8.0f, 2.5f, v => _waterMat.SetShaderParameter("caustic_scale", v));
        ui.AddSlider("B4 · Poke strength (LMB)", 0.05f, 1.0f, 0.3f, v => _pokeStrength = v);
        ui.AddSlider("B4 · Poke radius (m)", 0.2f, 2.0f, 0.6f, v => _pokeRadius = v);

        ui.AddSection("Sea (wavemaker)");
        ui.AddSlider("Wave amplitude", 0.05f, 0.45f, 0.18f, v => { if (_solver != null) { _solver.WaveAmp = v; } });
        ui.AddSlider("Wave period", 2.0f, 12.0f, 5.0f, v => { if (_solver != null) { _solver.WavePeriod = v; } });
        ui.AddSlider("Offshore depth", 0.6f, 3.0f, 1.2f, v => { if (_solver != null) { _solver.WaveDepth0 = v; } });
        ui.AddSlider("Ramp-in", 1.0f, 10.0f, 5.0f, v => { if (_solver != null) { _solver.WaveRamp = v; } });
        ui.AddToggle("Exp E · Incommensurate waves", false, v => { if (_solver != null) { _solver.Incommensurate = v; } });

        ui.AddSection("Physics");
        ui.AddSlider("Bed friction (Manning)", 0.01f, 0.06f, 0.03f, v => { if (_solver != null) { _solver.Manning = v; } });
        ui.AddSlider("Slope limiter θ", 1.0f, 2.0f, 1.3f, v => { if (_solver != null) { _solver.Theta = v; } });
        ui.AddSlider("Desing κ", 0.005f, 0.05f, 0.01f, v => { if (_solver != null) { _solver.Kappa = v; } });
        ui.AddSlider("Substeps / frame", 4, 16, 12, v => _maxSub = (int)v);

        ui.AddSection("Foam");
        ui.AddSlider("Foam amount (K_FOAM)", 0.0f, 4.0f, 1.5f, v => { if (_solver != null) { _solver.KFoam = v; } });

        ui.AddSection("Solitary wave (SPACE)");
        ui.AddSlider("Solitary height", 0.1f, 0.6f, 0.36f, v => { if (_solver != null) { _solver.SolitaryH = v; } });
        ui.AddSlider("Solitary launch X", 1.0f, 10.0f, 3.0f, v => { if (_solver != null) { _solver.SolitaryX0 = v; } });
    }
}
