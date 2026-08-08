using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 26 — Shore base: the reference scene for the SOLVER-EQUIVALENCE series.
//
// Scene 17 proved the KP07 port renders; this is the harness the series is judged in. It
// is 17's beach opened out to a real shoreline (ShoreScenario: 92 m, curved waterline,
// offshore sandbar) with two things 17 doesn't have:
//
//   1. SOLVERS as independent toggles. KP07 is the reference and starts ON; every
//      candidate cheap solver gets its own toggle, starts OFF, and is built one at a time
//      so it can be A/B'd against KP07 in the same frame, on the same bed, at the same
//      cost readout. Nothing here is a "mode switch" — they are meant to run side by side.
//   2. SURFACE as FLAT vs STYLED. Flat is the solver's height field, unlit and honest —
//      the view you judge a solver in. Styled is the full water shader. A solver that is
//      right in flat and wrong in styled is a shading problem, and vice versa, and keeping
//      them one click apart is what keeps those two failures from being confused.
//
// Verify (render, then Read the PNG):
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/26_shore_base.tscn 12 1600x900
public partial class ShoreBase : Node3D
{
    private const float Domain = ShoreScenario.Domain;   // 92.16 m
    private const float Half = Domain * 0.5f;
    private const int N = ShoreScenario.Grid;            // 768
    private const float Dx = ShoreScenario.Dx;           // 0.12 m
    private const float Dt = 0.003f;                     // CFL-matched to 17 (see ShoreScenario)
    private const int RelaxCells = 80;                   // must match RELAX_W in pass_boundary.glsl
    // shoreline blend band. It is a DEPTH, so it scales with dx — and it has to be wide
    // enough to cover a mesh quad or the waterline serrates.
    private const float ShoreTuck = 0.005f;
    private const string ShaderDir = "res://shaders/shorewaves/";
    private const string TexDir = "res://textures/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _flatMat = null!;
    private ShaderMaterial _styledMat = null!;
    private ShaderMaterial _sandMat = null!;
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

    // sim orchestration (main thread) — mirrors scene 17 / SimController._process
    private int _parity;
    private int _gparity;
    private float _accum;
    private float _simTime;
    private float _lastSimTime;
    private float _fpsAccum;
    private int _lastSteps;
    private bool _fireSolitary;

    // --- solvers. One flag each; all but the reference start off. ---
    private bool _kp07 = true;

    private Label? _readout;

    public override void _Ready()
    {
        _corners = ShoreScenario.BuildCorners();
        float[] bottomFloats = ShoreScenario.BuildBottomFloats(_corners);
        _bottomBytes = Bathymetry.FloatsToBytes(bottomFloats);
        _stateBytes = Bathymetry.StateBytesFromBottom(bottomFloats);

        // self-check: a sloped bed under the west relaxation zone reflects the wavemaker
        float flat = ShoreScenario.ShelfFlatnessError(_corners, RelaxCells);
        GD.Print($"[shore] {N}² @ dx={Dx} = {Domain:0.0} m · dt={Dt} · "
            + $"CFL={Dt * Mathf.Sqrt(9.81f * ShoreScenario.ShelfDepth) / Dx:0.000} · "
            + $"shelf flatness err={flat:0.0000} m {(flat < 1e-3f ? "ok" : "SLOPED — will reflect")}");

        BuildEnvironment();
        BuildTerrain();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        var s = new ShallowWaterKp(RenderingServer.GetRenderingDevice(), _bottomBytes, _stateBytes, N, Dx, Dt)
        {
            // mode 2 = west wavemaker + absorbing strips on E/N/S. A meandering shoreline
            // reaches the north and south edges, so mirror walls there would stand a
            // longshore reflection up within seconds.
            WaveMode = 2.0f,
            WaveDepth0 = ShoreScenario.ShelfDepth,
            WaveAmp = 0.585f,      // ~1.2 m face — shoals and breaks on the bar
            WavePeriod = 8.005f,   // ocean swell, not tank chop
            WaveRamp = 6.0f,
            SolitaryH = 0.9f,
            SolitaryX0 = 8.0f,
            MaxSubsteps = 12,
        };
        _solver = s;
    }

    private void BuildEnvironment()
    {
        // free-fly camera: starts on the berm looking obliquely across the bay, so the
        // shoreline curve and both break lines are in frame at once.
        _cam = new FreeCam { Fov = 58.0f, Far = 6000.0f, Current = true, Speed = 20.0f };
        AddChild(_cam);
        _cam.LookAtFromPosition(new Vector3(84.0f, 12.0f, 10.0f), new Vector3(52.0f, 0.0f, 48.0f), Vector3.Up);

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
        _sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        _sandMat.SetShaderParameter("tx_ground", _texGround);
        // The stock wet_col is nearly black and the swash band saturates, so the shore reads
        // as a burnt stripe at this scale. Half the darkening, slightly tighter band.
        _sandMat.SetShaderParameter("wet_darkness", 0.5f);
        _sandMat.SetShaderParameter("wet_gain", 0.85f);
        AddChild(new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = ShoreScenario.BuildTerrainMesh(_corners),   // real bed, world-space verts
            Position = Vector3.Zero,
            MaterialOverride = _sandMat,
        });
    }

    private void BuildWater()
    {
        _texState = new Texture2Drd();
        _texBottom = new Texture2Drd();
        _texDerived = new Texture2Drd();

        float tuck = ShoreTuck;

        _flatMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "debug_surface.gdshader") };
        _flatMat.SetShaderParameter("tx_state", _texState);
        _flatMat.SetShaderParameter("tx_bottom", _texBottom);
        _flatMat.SetShaderParameter("GRID_RES", (float)N);
        _flatMat.SetShaderParameter("tuck_h", tuck);

        _styledMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water.gdshader") };
        _styledMat.SetShaderParameter("tx_state", _texState);
        _styledMat.SetShaderParameter("tx_bottom", _texBottom);
        _styledMat.SetShaderParameter("tx_derived", _texDerived);
        _styledMat.SetShaderParameter("lace_tex", GD.Load<Texture2D>(TexDir + "voronoi_lace.png"));
        _styledMat.SetShaderParameter("flow_noise_tex", GD.Load<Texture2D>(TexDir + "flow_noise.png"));
        _styledMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _styledMat.SetShaderParameter("GRID_RES", (float)N);
        _styledMat.SetShaderParameter("tuck_h", tuck);
        // Tuned on screen (Ry, scene 26) — these are the shipped values, not guesses.
        _styledMat.SetShaderParameter("foam_breakup", 0.35f);
        _styledMat.SetShaderParameter("breakup_scale", 8.25f);
        _styledMat.SetShaderParameter("detail_base", 0.78f);
        _styledMat.SetShaderParameter("refraction_strength", 0.0f);

        _waterMi = new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(Domain, Domain), SubdivideWidth = N - 2, SubdivideDepth = N - 2 },
            Position = new Vector3(Half, 0.0f, Half),
            ExtraCullMargin = 60.0f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _styledMat,
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

        // KP07 off = the reference stops advancing. The surface freezes rather than
        // vanishing, which is what you want when a candidate solver takes over the frame.
        if (!_kp07) { _accum = 0.0f; _lastSteps = 0; return; }

        _accum += Mathf.Min(delta, 0.1f);
        int steps = Mathf.Clamp((int)(_accum / solver.Dt), 0, solver.MaxSubsteps);
        _accum -= steps * solver.Dt;
        _lastSteps = steps;
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
        _simTime += steps * solver.Dt;
    }

    private void UpdateReadout(float delta)
    {
        _fpsAccum += delta;
        if (_readout == null || _fpsAccum < 0.5f) { return; }
        float simRate = (_simTime - _lastSimTime) / _fpsAccum;
        _lastSimTime = _simTime;
        _fpsAccum = 0.0f;
        string dbg = _solver is { Ready: true }
            ? $"vol {_solver.DbgVolume:0} m³ · maxh {_solver.DbgMaxH:0.00} · maxu {_solver.DbgMaxSpeed:0.0} · nan {_solver.DbgNan:0}"
            : "solver init…";
        _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · sim ×{simRate:0.00} · {_lastSteps} substeps\n"
            + $"{N}² @ dx {Dx} = {Domain:0.0} m\n{dbg}";
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
        var ui = new DemoUI(this, "26 · Shore base — solver-equivalence harness",
            "Real KP07 on a 92 m shoreline (curved waterline, offshore sandbar, Dean profile). "
            + "This is the REFERENCE every cheap solver gets measured against — same bed, same "
            + "frame, same readout. Candidates are built one at a time and start OFF. "
            + "SPACE fires a solitary wave. Free cam: RMB look · WASD · Q/E down-up · Shift fast.");
        _readout = ui.AddReadout("— fps");

        ui.AddSection("Solvers");
        ui.AddToggle("KP07 — reference (central-upwind SWE)", true, v => _kp07 = v);
        Unbuilt(ui.AddToggle("MNA-RGB — not built yet", false, _ => { }));
        Unbuilt(ui.AddToggle("Grid 3D — not built yet", false, _ => { }));

        ui.AddSection("Surface");
        ui.AddOptions("Shading", new[] { "Flat", "Styled" }, 1,
            idx => _waterMi.MaterialOverride = idx == 0 ? _flatMat : _styledMat);
        ui.AddSlider("Foam breakup", 0.0f, 1.0f, 0.35f, v => _styledMat.SetShaderParameter("foam_breakup", v));
        ui.AddSlider("Breakup scale (m)", 1.0f, 30.0f, 8.25f, v => _styledMat.SetShaderParameter("breakup_scale", v));
        ui.AddSlider("Detail ripple", 0.0f, 1.0f, 0.78f, v => _styledMat.SetShaderParameter("detail_base", v));
        ui.AddSlider("Refraction", 0.0f, 0.3f, 0.0f, v => _styledMat.SetShaderParameter("refraction_strength", v));
        // widens the band over which the surface tucks under the sand — the waterline
        // serrates when it is narrower than a mesh quad, and a quad here is 12 cm.
        ui.AddSlider("Shore tuck (m)", 0.001f, 0.2f, ShoreTuck, v =>
        {
            _flatMat.SetShaderParameter("tuck_h", v);
            _styledMat.SetShaderParameter("tuck_h", v);
        });

        ui.AddSection("Shore / wet sand");
        ui.AddSlider("Wet darkness", 0.0f, 1.0f, 0.5f, v => _sandMat.SetShaderParameter("wet_darkness", v));
        ui.AddSlider("Wet spread", 0.0f, 3.0f, 0.85f, v => _sandMat.SetShaderParameter("wet_gain", v));
        ui.AddSlider("Sand brightness", 0.2f, 2.0f, 1.0f, v => _sandMat.SetShaderParameter("sand_brightness", v));
        ui.AddSlider("Wet gloss", 0.0f, 1.0f, 1.0f, v => _sandMat.SetShaderParameter("wet_gloss", v));
        ui.AddSlider("Stranded foam", 0.0f, 1.0f, 0.7f, v => _sandMat.SetShaderParameter("foam_strength", v));
        // The two timescales are separate things: DAMP SAND is the dark band left behind after
        // the swash pulls back; LACE is the foam stranded on it. They dry at different rates.
        ui.AddSlider("Damp sand dries (s)", 1.0f, 120.0f, 45.0f, v => { if (_solver != null) { _solver.DryTau = v; } });
        ui.AddSlider("Foam lace lasts (s)", 0.5f, 40.0f, 10.0f, v => { if (_solver != null) { _solver.StrandTau = v; } });
        ui.AddSlider("Re-wet erase (s)", 0.02f, 2.0f, 0.12f, v => { if (_solver != null) { _solver.RewetTau = v; } });

        ui.AddSection("Water detail");
        ui.AddSlider("Detail normal strength", 0.0f, 2.0f, 0.32f, v => _styledMat.SetShaderParameter("detail_normal_strength", v));
        ui.AddSlider("Detail scale (m)", 0.05f, 2.0f, 0.4f, v => _styledMat.SetShaderParameter("detail_scale", v));
        ui.AddSlider("Foam lace scale (m)", 0.1f, 10.0f, 1.2f, v => _styledMat.SetShaderParameter("lace_scale_coarse", v));
        ui.AddSlider("Flow speed", 0.0f, 4.0f, 1.0f, v => _styledMat.SetShaderParameter("flow_speed_scale", v));

        ui.AddSection("Sea state");
        ui.AddSlider("Wave amp (m)", 0.0f, 1.0f, 0.585f, v => { if (_solver != null) { _solver.WaveAmp = v; } });
        ui.AddSlider("Wave period (s)", 3.0f, 16.0f, 8.005f, v => { if (_solver != null) { _solver.WavePeriod = v; } });

        ui.AddSection("Render cost");
        ui.AddToggle("SSR (screen-space reflections)", false, v => _env.SsrEnabled = v);
        ui.AddToggle("SSAO", false, v => _env.SsaoEnabled = v);
        ui.AddToggle("SSIL", false, v => _env.SsilEnabled = v);
        ui.AddToggle("Glow (foam bloom)", false, v => _env.GlowEnabled = v);
    }

    // A solver slot that exists in the plan but not yet in code — shown so the series'
    // shape is visible, disabled so it can't lie about what is running.
    private static void Unbuilt(CheckButton c)
    {
        c.Disabled = true;
        c.Modulate = new Color(1.0f, 1.0f, 1.0f, 0.45f);
    }
}
