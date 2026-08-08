using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 28 — Shore CURL: the fork where the wave is made to break forward.
//
// A VERBATIM fork of scene 26 (ShoreBase) at the moment of forking. 26 is the reference and
// must stay untouched — it is what every curl experiment is judged against, so nothing here
// may edit a file 26 reads. Concretely that means:
//   * shared and READ-ONLY: ShallowWaterKp, ShoreScenario, Bathymetry, sand_min.gdshader,
//     debug_surface.gdshader — identical bed, identical solver, so an A/B is meaningful;
//   * forked because this scene EDITS it: water_curl.gdshader (a copy of water.gdshader).
//     The moment a curl experiment needs a vertex-shader change, it goes there, not in
//     water.gdshader, or scenes 17 and 26 change underneath us.
//
// THREE ROUTES, and they are not exclusive — E gives the LOOK now, B gives the DYNAMICS later:
//   A  Trochoidal displacement — write VERTEX.xz as well as .y so the sheet can lean and
//      fold. Cheapest; limited by 0.12 m quads and by a sheet having no inside.
//   B  A moving 3D box riding the break line, Jacobi-projected, feeding energy back into the
//      surface. Jacobi (not multigrid) on purpose: it is fine-grid only, so the sampled-mask
//      coarsening failure that stalled scene 27 cannot arise (solver-ledger.md §10, §7a).
//      The moving window is scene 50's clipmap trick one dimension up.
//   E  The barrel as an SDF layer, ported from ../water-kit scene 210 (barrel_carve), which
//      already smooth-subtracts a ridged capsule from the swell to wrap a tube-shaped void.
//      No topology limit, no mesh resolution ceiling, resolution-independent.
//
// Original scene-26 header follows.
//
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
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/28_shore_curl.tscn 12 1600x900
public partial class ShoreCurl : Node3D
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
    private ShaderMaterial _barrelMat = null!;
    private MeshInstance3D _barrelMi = null!;
    private ShaderMaterial _trackMat = null!;
    private MeshInstance3D _trackMi = null!;
    private int _trackMethod = 0;   // field overlays off: lines are real geometry now

    // --- slice debug lines: actual LINE GEOMETRY, not a screen-space distance test ---
    // Testing "is this pixel near a segment" inside a fragment shader produced contour blobs,
    // because every pixel re-derived its own origin and neighbours disagreed. Two vertices per
    // slice, handed to the renderer, cannot do that: a line is a line.
    private MeshInstance3D _lineMi = null!;
    private ImmediateMesh _lineMesh = null!;
    private bool _linesOn = true;
    private float _lineLen = 5.0f;
    private float _lineSpacing = 8.0f;
    private float _lineMinAge = 0.01f;
    private bool _lineScaleBySpeed;
    private int _lineTick;
    private int _lineCount;   // tuned on screen: normals/steepness field
    private CurlAge? _age;
    private Texture2Drd _texAge = null!;
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
    private int _tickStat;

    // --- solvers. One flag each; all but the reference start off. ---
    private bool _kp07 = true;
    // Sim speed. Tuning a barrel needs the wave held still enough to watch a single front bore
    // in — at 1.0 a breaker crosses the surf zone faster than a slider can be dragged. Scales
    // the accumulator, so substeps and the age field slow together and stay in step.
    private float _simSpeed = 1.0f;
    private Vector3 _brC = new(56.0f, 0.4f, 42.0f);

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
            DryTau = 45.03f,
            StrandTau = 9.98f,
            RewetTau = 0.119f,
        };
        _solver = s;

        // Break-age memory. Owned by scene 28, NOT by ShallowWaterKp — adding a pass to the
        // solver would change scene 26's per-frame cost and void the A/B.
        var ca = new CurlAge(RenderingServer.GetRenderingDevice(), N);
        if (ca.Ready)
        {
            ca.Bind(s.StateRids, s.BottomRid, s.DerivedRid, Dx);
            _age = ca;
        }
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
        _texAge = new Texture2Drd();
        _sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        _sandMat.SetShaderParameter("tx_ground", _texGround);
        // The stock wet_col is nearly black and the swash band saturates, so the shore reads
        // as a burnt stripe at this scale. Half the darkening, slightly tighter band.
        _sandMat.SetShaderParameter("wet_darkness", 0.5f);
        _sandMat.SetShaderParameter("wet_gain", 0.855f);
        _sandMat.SetShaderParameter("sand_brightness", 1.001f);
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

        _styledMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water_curl.gdshader") };
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
        _styledMat.SetShaderParameter("detail_scale", 0.401f);
        _styledMat.SetShaderParameter("lace_scale_coarse", 1.189f);
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
        BuildBarrel();
        BuildTracker();
        BuildLines();
    }

    // The barrel as an ADDITIONAL layer: its own box, raymarching an SDF built from the SAME
    // sim textures the mesh water uses, with 210's ridged capsule subtracted. It discards
    // wherever it misses, so the mesh water underneath is untouched — this composites over
    // scene 26's look rather than replacing it.
    // Debug overlay for the wave TRACKER. Hidden by default and hidden whenever the method is
    // Off — Godot skips hidden geometry outright, so "off" costs nothing at all, which is the
    // requirement (curl-hypotheses.md §2).
    private void BuildTracker()
    {
        _trackMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "tracker_debug.gdshader") };
        _trackMat.SetShaderParameter("tx_state", _texState);
        _trackMat.SetShaderParameter("tx_bottom", _texBottom);
        _trackMat.SetShaderParameter("tx_derived", _texDerived);
        _trackMat.SetShaderParameter("tx_age", _texAge);
        _trackMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _trackMat.SetShaderParameter("method", 0);
        // draw AFTER the water: equal-AABB transparents otherwise sort arbitrarily
        _trackMat.RenderPriority = 20;
        _trackMat.SetShaderParameter("sl_spacing", 8.0f);
        _trackMat.SetShaderParameter("sl_age_centre", 0.03f);
        _trackMat.SetShaderParameter("channel", 1);        // steepness only
        // TUNED ON SCREEN (Ry). Note how far this is from the 0.06 I guessed: the breaking
        // front needs a MUCH higher steepness gate than ordinary swell, which is exactly the
        // number a barrel spawn rule will need.
        _trackMat.SetShaderParameter("steep_min", 0.515f);
        _trackMat.SetShaderParameter("steep_max", 0.76f);
        _trackMat.SetShaderParameter("aeration_gain", 1.08f);

        // Coarser than the water mesh on purpose: this reads a field, not a silhouette, so it
        // does not need 588k verts to be legible.
        _trackMi = new MeshInstance3D
        {
            Name = "TrackerDebug",
            Mesh = new PlaneMesh { Size = new Vector2(Domain, Domain), SubdivideWidth = 383, SubdivideDepth = 383 },
            Position = new Vector3(Half, 0.0f, Half),
            ExtraCullMargin = 60.0f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _trackMat,
            Visible = false,
        };
        AddChild(_trackMi);
    }

    private void BuildLines()
    {
        _lineMesh = new ImmediateMesh();
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            VertexColorUseAsAlbedo = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Disabled,
            NoDepthTest = true,          // a debug annotation should never hide behind a wave
            RenderPriority = 30,
        };
        _lineMi = new MeshInstance3D
        {
            Name = "SliceLines",
            Mesh = _lineMesh,
            MaterialOverride = mat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 200.0f,
            Visible = true,
        };
        AddChild(_lineMi);
    }

    // Rebuild from slice records. Throttled: the readback is a full GPU stall, and debug lines
    // do not need 120 Hz (solver-ledger.md §9a).
    private void RebuildLines()
    {
        var age = _age;
        if (_lineMesh == null) { return; }
        _lineMesh.ClearSurfaces();
        if (!_linesOn || age is not { Ready: true }) { _lineCount = 0; return; }

        var slices = age.ExtractSlices(Domain, _lineSpacing, _lineMinAge);
        _lineCount = slices.Count;
        if (slices.Count == 0) { GD.Print("[lines] ZERO slices extracted"); return; }

        _lineMesh.SurfaceBegin(Mesh.PrimitiveType.Lines);
        foreach (var sl in slices)
        {
            float len = _lineScaleBySpeed ? _lineLen * Mathf.Clamp(sl.Speed / 0.6f, 0.25f, 3.0f) : _lineLen;
            var a = new Vector3(sl.Pos.X, 0.0f, sl.Pos.Y);
            var b = a + new Vector3(sl.Dir.X, 0.0f, sl.Dir.Y) * len;
            a.Y = SurfaceY(a) + 0.35f;
            b.Y = SurfaceY(b) + 0.35f;
            _lineMesh.SurfaceSetColor(new Color(0.15f, 0.95f, 1.0f));   // tail: cyan
            _lineMesh.SurfaceAddVertex(a);
            _lineMesh.SurfaceSetColor(new Color(1.0f, 0.9f, 0.2f));      // head: yellow
            _lineMesh.SurfaceAddVertex(b);
        }
        _lineMesh.SurfaceEnd();
        if (_lineTick % 120 == 1) { GD.Print($"[lines] slices={_lineCount} spacing={_lineSpacing} minAge={_lineMinAge}"); }
    }

    // Flat still-water reference. Deliberately NOT the displaced surface: draping an annotation
    // over the wave is what made the old version read as a refracted stripe.
    private float SurfaceY(Vector3 p) => 0.0f;

    private void UpdateTrackerVisibility(bool wantVisual)
    {
        _trackMat.SetShaderParameter("method", _trackMethod);
        _trackMi.Visible = wantVisual && _trackMethod > 0;
    }

    private void BuildBarrel()
    {
        _barrelMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "barrel_carve.gdshader") };
        _barrelMat.SetShaderParameter("tx_state", _texState);
        _barrelMat.SetShaderParameter("tx_bottom", _texBottom);
        _barrelMat.SetShaderParameter("tx_derived", _texDerived);
        _barrelMat.SetShaderParameter("tx_age", _texAge);
        _barrelMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        // OFF by default — placement is unsolved (curl-hypotheses.md §3), so it ships dark
        // rather than shipping a slab. The tuned shape values below survive for when the
        // spawner can finally place it.
        _barrelMat.SetShaderParameter("sl_enabled", 0.0f);
        _barrelMat.SetShaderParameter("br_fixed", 0.0f);   // dynamic slices, ghost-visualised
        _barrelMat.SetShaderParameter("sl_smooth", 0.7018f);
        _barrelMat.SetShaderParameter("steps", 80);
        // Measured, not guessed: the age field tops out near 0.08 and is ~5 cells wide, so a
        // cross-section centred at 0.10 sat OUTSIDE the range that exists and spanned 10 cm.
        _barrelMat.SetShaderParameter("sl_age_centre", 0.03f);
        _barrelMat.SetShaderParameter("sl_age_metres", 20.0f);

        // Spans the DOMAIN, not a spot. Sized 34x8x34 around a fixed point back when the carve
        // was one capsule in one place; the carve is now field-driven and lives wherever the age
        // field is live, i.e. along the entire breaking front. A small box clipped it.
        // It is only a container for ray entry — the shader discards on miss, so nothing of it
        // is ever seen unless the shader fails to compile.
        _barrelMi = new MeshInstance3D
        {
            Name = "BarrelLayer",
            Mesh = new BoxMesh { Size = new Vector3(Domain, 10.0f, Domain) },
            Position = new Vector3(Half, 1.0f, Half),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 40.0f,
            MaterialOverride = _barrelMat,
        };
        AddChild(_barrelMi);
    }

    public override void _Process(double delta)
    {
        StepSim((float)delta);
        UpdateReadout((float)delta);
        // Throttled: ExtractSlices does a full texture readback, which stalls the GPU. Debug
        // lines do not need 120 Hz (solver-ledger.md §9a).
        if (_linesOn && _lineTick++ % 12 == 0) { RebuildLines(); }
    }

    private void StepSim(float delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        _texBottom.TextureRdRid = solver.BottomRid;
        _texDerived.TextureRdRid = solver.DerivedRid;
        if (_age is { Ready: true }) { _texAge.TextureRdRid = _age.AgeRid; }

        // KP07 off = the reference stops advancing. The surface freezes rather than
        // vanishing, which is what you want when a candidate solver takes over the frame.
        if (!_kp07) { _accum = 0.0f; _lastSteps = 0; return; }

        _accum += Mathf.Min(delta, 0.1f) * _simSpeed;
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
        var age = _age;
        float period = solver.WavePeriod;
        bool wantStats = _tickStat++ % 30 == 0;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            solver.Step(steps, t0, parity, solitary, gparity);
            // after the solver, so derived/state are this frame's
            age?.Step(steps * solver.Dt, finalParity, period);
            if (wantStats) { age?.CaptureStats(); }
        }));

        _parity = finalParity;
        _gparity = finalGparity;
        _simTime += steps * solver.Dt;
    }

    private void UpdateReadout(float delta)
    {
        _fpsAccum += delta;
        if (_readout == null || _fpsAccum < 0.5f) { return; }
        if (_age is { Ready: true } ageDbg)
        {
            GD.Print($"[age] max={ageDbg.StatMax:0.000} mean={ageDbg.StatMean:0.000} "
                + $"live={ageDbg.StatLive} bytes={ageDbg.StatBytes}");
        }
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
        foreach (var t in new[] { _texAge }) { if (t != null) { t.TextureRdRid = default; } }
        var a = _age;
        _age = null;
        if (a != null) { RenderingServer.CallOnRenderThread(Callable.From(() => a.Free())); }
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "28 · Shore curl — making the wave break forward",
            "Real KP07 on a 92 m shoreline (curved waterline, offshore sandbar, Dean profile). "
            + "This is the REFERENCE every cheap solver gets measured against — same bed, same "
            + "frame, same readout. Candidates are built one at a time and start OFF. "
            + "SPACE fires a solitary wave. Free cam: RMB look · WASD · Q/E down-up · Shift fast.");
        _readout = ui.AddReadout("— fps");

        ui.AddSection("Tests");
        ui.AddSlider("Sim speed", 0.05f, 1.5f, 1.0f, v => _simSpeed = v);

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

        ui.AddSection("Slice debug lines");
        ui.AddToggle("Show slice lines", true, v => { _linesOn = v; RebuildLines(); });
        ui.AddSlider("Line spacing (m)", 1.0f, 40.0f, 8.0f, v => { _lineSpacing = v; RebuildLines(); });
        ui.AddSlider("Line length (m)", 0.5f, 20.0f, 5.0f, v => { _lineLen = v; RebuildLines(); });
        ui.AddSlider("Min age to place", 0.0f, 0.5f, 0.01f, v => { _lineMinAge = v; RebuildLines(); });
        ui.AddToggle("Length = speed", false, v => { _lineScaleBySpeed = v; RebuildLines(); });

        ui.AddSection("Wave tracker");
        bool showTrack = true;
        ui.AddOptions("Method", new[] { "Off", "1 · Normals (field)", "2 · Probes (points)", "3 · Break curve", "4 · Break AGE (memory)", "5 · Placement vectors" }, 0,
            idx => { _trackMethod = idx; UpdateTrackerVisibility(showTrack); });
        ui.AddToggle("Show tracker debug", true, v => { showTrack = v; UpdateTrackerVisibility(v); });
        ui.AddOptions("Channel", new[] { "All", "Steepness only", "Aeration only", "Direction only" }, 1,
            idx => _trackMat.SetShaderParameter("channel", idx));
        ui.AddSlider("Steep min", 0.0f, 1.0f, 0.515f, v => _trackMat.SetShaderParameter("steep_min", v));
        ui.AddSlider("Steep max", 0.0f, 1.0f, 0.76f, v => _trackMat.SetShaderParameter("steep_max", v));
        ui.AddSlider("Vector height (m)", 0.0f, 3.0f, 1.2f, v => _trackMat.SetShaderParameter("lift", v));
        ui.AddSlider("Vector length (m)", 0.2f, 12.0f, 5.0f, v => _trackMat.SetShaderParameter("vec_len", v));
        ui.AddSlider("Vector width (m)", 0.02f, 2.0f, 0.9f, v => _trackMat.SetShaderParameter("vec_width", v));
        ui.AddSlider("Aeration gain", 0.0f, 4.0f, 1.08f, v => _trackMat.SetShaderParameter("aeration_gain", v));

        ui.AddSection("Barrel — FIXED capsule (210)");
        // The master gate. It was set to 0 in code with no control to raise it, which made
        // every downstream symptom — no tubes, no ghost, no carve — look like a placement or
        // SDF bug. Nothing renders past this line when it is off.
        ui.AddToggle("Barrel layer", false,
            v => _barrelMat.SetShaderParameter("sl_enabled", v ? 1.0f : 0.0f));
        // Placement by hand. No age, no field, no lifecycle — the shape from water-kit 210,
        // driven by sliders exactly as 210 drives it. Automatic placement is a separate problem
        // and could not be debugged while it was unclear whether the SHAPE rendered at all.
        ui.AddToggle("Fixed capsule (off = age slices)", false,
            v => _barrelMat.SetShaderParameter("br_fixed", v ? 1.0f : 0.0f));
        ui.AddSlider("Centre X (across-shore)", 0.0f, Domain, 56.0f,
            v => { _brC.X = v; _barrelMat.SetShaderParameter("br_center", _brC); });
        ui.AddSlider("Centre Y (height)", -3.0f, 6.0f, 0.4f,
            v => { _brC.Y = v; _barrelMat.SetShaderParameter("br_center", _brC); });
        ui.AddSlider("Centre Z (along-shore)", 0.0f, Domain, 42.0f,
            v => { _brC.Z = v; _barrelMat.SetShaderParameter("br_center", _brC); });
        ui.AddSlider("Yaw (peel angle)", -180.0f, 180.0f, 0.0f, v => _barrelMat.SetShaderParameter("br_yaw_deg", v));
        ui.AddSlider("Pitch", -45.0f, 45.0f, 0.0f, v => _barrelMat.SetShaderParameter("br_pitch_deg", v));
        ui.AddSlider("Capsule radius (m)", 0.1f, 8.0f, 1.6f, v => _barrelMat.SetShaderParameter("br_radius", v));
        ui.AddSlider("Capsule length (m)", 1.0f, 40.0f, 14.0f, v => _barrelMat.SetShaderParameter("br_half_len", v));

        ui.AddSection("Barrel — slice lifecycle");
        // The ghost marches the carve SOLID, so it shows the tubes as objects — where they are,
        // how big, which way they spin. On by default because placement is what is being tuned;
        // with it off you are judging a subtraction by its absence, which is what made the first
        // barrel attempt unfalsifiable.
        ui.AddToggle("Placement ghost (magenta)", true,
            v => _barrelMat.SetShaderParameter("sl_debug", v ? 1.0f : 0.0f));
        // Both timing modes exist because neither is obviously right: wall-clock tunes
        // directly, phase-driven couples the crash to the swell's own frequency.
        ui.AddToggle("Phase-driven (off = wall clock)", false,
            v => { if (_age != null) { _age.PhaseDriven = v; } });
        ui.AddSlider("Crash time (s)", 0.2f, 8.0f, 0.5f, v => { if (_age != null) { _age.CrashSeconds = v; } });
        ui.AddSlider("Retire time (s)", 0.1f, 5.0f, 2.0f, v => { if (_age != null) { _age.DecaySeconds = v; } });
        ui.AddSlider("Birth steepness", 0.0f, 1.5f, 0.515f, v => { if (_age != null) { _age.BirthSteep = v; } });
        // The knob that made age sweep at all: at 0 the field ages per-column and never leaves
        // the blue end; at 1 it rides the front at full sqrt(g·h) and accumulates a real life.
        ui.AddSlider("Age rides wave (celerity)", 0.0f, 2.0f, 1.0f, v => { if (_age != null) { _age.Celerity = v; } });
        ui.AddSlider("Spacing (m)", 0.5f, 40.0f, 8.0f, v => { _barrelMat.SetShaderParameter("sl_spacing", v); _trackMat.SetShaderParameter("sl_spacing", v); });
        ui.AddSlider("Radius (m, circular)", 0.1f, 8.0f, 1.5f, v => _barrelMat.SetShaderParameter("sl_circle_r", v));
        ui.AddSlider("Yaw vs wave (deg)", -180.0f, 180.0f, 0.0f, v => _barrelMat.SetShaderParameter("sl_yaw_off", v));
        ui.AddSlider("Pitch vs wave (deg)", -90.0f, 90.0f, 0.0f, v => _barrelMat.SetShaderParameter("sl_pitch_off", v));
        ui.AddSlider("Offset across (m)", -20.0f, 20.0f, 0.0f, v => _barrelMat.SetShaderParameter("sl_off_across", v));
        ui.AddSlider("Offset up (m)", -8.0f, 8.0f, 0.0f, v => _barrelMat.SetShaderParameter("sl_off_up", v));
        ui.AddSlider("Offset along (m)", -20.0f, 20.0f, 0.0f, v => _barrelMat.SetShaderParameter("sl_off_along", v));
        ui.AddSlider("Slice length (m)", 0.3f, 20.0f, 3.0f, v => _barrelMat.SetShaderParameter("sl_len", v));
        ui.AddSlider("Bore radius (m)", 0.05f, 3.0f, 0.55f, v => _barrelMat.SetShaderParameter("sl_radius", v));
        ui.AddSlider("Radius by wave size", 0.0f, 3.0f, 1.0f, v => _barrelMat.SetShaderParameter("sl_radius_by_steep", v));
        ui.AddSlider("Bore depth (m)", 0.0f, 3.0f, 0.45f, v => _barrelMat.SetShaderParameter("sl_depth", v));
        ui.AddSlider("Crash amount", 0.0f, 1.0f, 0.85f, v => _barrelMat.SetShaderParameter("sl_crash", v));
        ui.AddSlider("Age span (m)", 0.2f, 40.0f, 20.0f, v => _barrelMat.SetShaderParameter("sl_age_metres", v));
        ui.AddSlider("Widest at age", 0.0f, 1.0f, 0.03f, v => { _barrelMat.SetShaderParameter("sl_age_centre", v); _trackMat.SetShaderParameter("sl_age_centre", v); });
        ui.AddSlider("Slice variability", 0.0f, 1.0f, 0.7f, v => _barrelMat.SetShaderParameter("sl_seed_var", v));
        ui.AddSlider("Lip feather", 0.05f, 4.0f, 0.7018f, v => _barrelMat.SetShaderParameter("sl_smooth", v));
        ui.AddSlider("Ridge depth", 0.0f, 1.5f, 0.18f, v => _barrelMat.SetShaderParameter("sl_ridge_amp", v));
        ui.AddSlider("Ridge spin (rad/s)", 0.0f, 4.0f, 0.6f, v => _barrelMat.SetShaderParameter("sl_spin", v));
        ui.AddSlider("Foam gain", 0.0f, 4.0f, 1.4f, v => _barrelMat.SetShaderParameter("foam_gain", v));
        ui.AddSlider("Raymarch steps", 16, 192, 80, v => _barrelMat.SetShaderParameter("steps", (int)v));
        ui.AddSlider("Spin variability", 0.0f, 1.0f, 0.35f, v => _barrelMat.SetShaderParameter("sl_spin_var", v));


        ui.AddSection("Shore / wet sand");
        ui.AddSlider("Wet darkness", 0.0f, 1.0f, 0.5f, v => _sandMat.SetShaderParameter("wet_darkness", v));
        ui.AddSlider("Wet spread", 0.0f, 3.0f, 0.855f, v => _sandMat.SetShaderParameter("wet_gain", v));
        ui.AddSlider("Sand brightness", 0.2f, 2.0f, 1.001f, v => _sandMat.SetShaderParameter("sand_brightness", v));
        ui.AddSlider("Wet gloss", 0.0f, 1.0f, 1.0f, v => _sandMat.SetShaderParameter("wet_gloss", v));
        ui.AddSlider("Stranded foam", 0.0f, 1.0f, 0.7f, v => _sandMat.SetShaderParameter("foam_strength", v));
        // The two timescales are separate things: DAMP SAND is the dark band left behind after
        // the swash pulls back; LACE is the foam stranded on it. They dry at different rates.
        ui.AddSlider("Damp sand dries (s)", 1.0f, 120.0f, 45.03f, v => { if (_solver != null) { _solver.DryTau = v; } });
        ui.AddSlider("Foam lace lasts (s)", 0.5f, 40.0f, 9.98f, v => { if (_solver != null) { _solver.StrandTau = v; } });
        ui.AddSlider("Re-wet erase (s)", 0.02f, 2.0f, 0.119f, v => { if (_solver != null) { _solver.RewetTau = v; } });

        ui.AddSection("Water detail");
        ui.AddSlider("Detail normal strength", 0.0f, 2.0f, 0.32f, v => _styledMat.SetShaderParameter("detail_normal_strength", v));
        ui.AddSlider("Detail scale (m)", 0.05f, 2.0f, 0.401f, v => _styledMat.SetShaderParameter("detail_scale", v));
        ui.AddSlider("Foam lace scale (m)", 0.1f, 10.0f, 1.189f, v => _styledMat.SetShaderParameter("lace_scale_coarse", v));
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
