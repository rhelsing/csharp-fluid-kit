using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 25 — the SAME wave tank as scene 23, but every term of the sim is an MNA STAMP.
//
// Scene 23 is the explicit fork: one Laplacian pass at the 2D CFL limit, so wave speed is pinned
// to the grid and depth can only ever subtract from it. Here the whole tank is one stamped system
// A*h = b (mna_tank.glsl) relaxed matrix-free with K Jacobi sweeps — unconditionally stable.
// Consequences worth knowing while comparing the two side by side:
//   * bathymetry is not a coefficient hack: depth IS each pipe's conductance, so shoaling and
//     refraction come out of the stamp and deep water keeps its real speed.
//   * "Sim rate" no longer sets wave speed. Speed is sqrt(g*h) in pool units; Sim rate and
//     Sweeps buy accuracy. That is the CFL ceiling being gone.
//   * the boundary can be a matched resistor instead of a mirror, so the tank stops ringing.
//
// Old header follows, still true of the render half:
// Scene 23 — wave tank, NO RAYTRACING. The Wallace raytraced pool (one shader that re-traced
// walls/floor/sphere/sky per pixel) is gone; scene 24 still has it if you want to compare.
// Here the tank is ordinary geometry — four wall slabs, a tilted floor slab and a ball, each its
// own mesh with a normal material, lit by a real sun and shadowed normally. The only shader left
// is the water surface (tank_water.gdshader), and it only does surface work: sim-driven normals,
// screen-texture refraction, analytic depth tint off the ramp.
//
// The two changes that make scale actually READ:
//   * the camera is FIXED in world space — it no longer scales with the tank, so growing the pool
//     grows it in frame instead of producing an identical picture.
//   * the sim is 1024^2 with a small drop radius, so ripple wavelength is short relative to the
//     tank — waves read as waves in a big tank, not a bathtub filmed close up.
public partial class WaveTankMna : Node3D
{
    // Grid is switchable at runtime so the plan's "sweep grid size, show multigrid's flat
    // iteration count" experiment is actually possible. Changing it rebuilds the solver.
    private static readonly int[] GridSizes = { 256, 512, 1024 };
    private int _gridIdx = 2;
    private int SimSize => GridSizes[_gridIdx];
    private const string TexDir = "res://textures/webgpu_water/";
    private const int WaterDetail = 400;
    private double _msAccum; private int _msFrames; private float _msFrame;
    // ---- benchmark harness ----
    // Driven by CLI user args so a matrix can be swept from bash with no code edits:
    //   godot --path . res://scenes/25_wave_tank_mna.tscn -- solver=1 grid=2 sweeps=30 bench=8
    // Warms up, then averages, prints one CSV line to stdout and quits.
    // No raytracing in this scene (that is 24), but the water/tank render still lands inside
    // ms/frame. Hiding it makes the timing solver-dominated. Forced ON during benchmarks.
    private bool _renderOff;
    private float _benchSecs, _benchT;
    private double _bMs, _bRes, _bTicks; private int _bN;

    // Pool interior is normalized [-1,1]; _tank scales it into world units.
    private const float RimY = 0.2f;         // top of the walls
    private const float WallT = 0.06f;       // wall thickness
    private static readonly Vector3 LightDir = new(2.0f, 2.0f, -1.0f);

    // FIXED world-space camera — deliberately NOT scaled by _poolHalf.
    private static readonly Vector3 CamPos = new(-9.0f, 15.0f, -31.0f);
    private static readonly Vector3 CamTarget = new(2.0f, -3.0f, 0.0f);

    private Texture2D _tileTex = null!;
    private Texture2Drd _waterTex = null!;

    private ShaderMaterial _waterMat = null!;
    private StandardMaterial3D _tileMat = null!;

    // [tank knobs] The floor is a ramp  y = _floorBase + tan(_slopeDeg)*(x+1)  over x in [-1,1]:
    //   _slopeDeg   the bed angle in DEGREES (a real angle — x and y scale together, so it holds
    //               at any tank size). Fine enough to sit on 1 degree.
    //   _floorBase  height of the DEEP end. Raise it to shallow the whole tank; the walls grow to
    //               stay taller than the ramp, so there is no cap that breaks the geometry.
    // Dry beach appears wherever the ramp clears the waterline.
    private float _slopeDeg = 13.8f, _floorBase = -0.58f;
    private float _waterLevel = -0.18f, _poolHalf = 20.0f;
    private float _tileDensity = 2.15f, _rippleSize = 0.003f;
    private float _refraction = 0.03f, _absorption = 4.724f;
    private float _camLift = 0.1f;   // manual camera nudge on top of the waterline follow
    // FIXED TIMESTEP. The kernel has no dt — wave speed AND damping are per step — so running it
    // once per rendered frame tied both to frame rate. Now the sim ticks at _simHz regardless of
    // render rate, and damping is authored per SECOND and converted to per-step (2 update passes
    // per tick), so changing the tick rate changes wave SPEED without touching how fast waves die.
    private float _simHz = 197.4f;
    private float _dampPerSec = 0.9f;   // a little loss so a driven box cannot run away
    private double _simAcc;
    private int _ticksLast;
    private const int MaxTicksPerFrame = 12;

    // [wavemaker] A piston paddle spanning the deep-end wall (x = -1). It rests flush with the
    // wall and strokes forward by _padStroke pool units, _padHz times a second; the sim's paddle
    // pass pushes the water in front of its face by whatever it moved that tick. Stroke and rate
    // are the wave amplitude/wavelength controls — this replaces the ball entirely.
    private bool _padOn = true;
    private float _padStroke = 0.145f, _padHz = 0.6105f, _padGain = 0.021f, _padWidth = 0.0289f;
    private double _padPhase;
    // Rest BETWEEN strokes. The stroke itself still runs at _padHz (so the wavelength it emits is
    // unchanged), but the RETURN is stretched over _padDelay seconds — an elongated waveform,
    // seconds. That turns a continuous tone into discrete wave GROUPS — which also means the tank
    // is no longer being driven into a standing pattern the whole time.
    private float _padDelay = 2.55f;
    private double _padCycle;   // 0..1 position within the warped cycle

    // [curved face] The piston face is x = paddleX + amp*sin(lobes*PI*z + phase). Static phase =
    // a fixed bulge (lobes diverge, hollows focus); a travelling phase (_padSnakeHz) makes it a
    // snake wavemaker, which is how real directional basins produce oblique / short-crested seas.
    // Two independent options that compose:
    //   _padUndulate  bends the face by amp*sin(lobes*PI*z + phase); _padSnakeHz travels that
    //                 curve along z (snake wavemaker -> oblique fronts).
    //   _padSegmented quantises z into _padSegCount flat steps — a BANK of paddles. Off = one
    //                 continuous face that undulates, which is a different physical object: no
    //                 inter-segment steps, so no step diffraction.
    // Mesh-only cap (that many box instances); the SIM segment count is just a float and goes to
    // MaxSimSegs. Above the box cap the paddle draws as the ribbon instead.
    private const int MaxSegs = 64;
    private const float MaxSimSegs = 256.0f;
    private bool _padUndulate = true, _padSegmented = true;
    private float _padCurveAmp = 0.028f, _padLobes = 8.0f, _padSnakeHz = 0.945f, _padSegCount = 252.175f;
    private double _padCurvePhase;

    // MICRO layer — a second, independent jostler on the same face: many small segments snaking
    // fast. Summed with the macro undulation, so either or both can run.
    private bool _padMicroOn = true;
    private float _padMicroAmp = 0.009f, _padMicroLobes = 156.94f, _padMicroHz = 1.24f;
    private double _padMicroPhase;

    // [shoaling] Depth-dependent wave speed, off by default. c^2 ~ g*h, and the explicit scheme is
    // already at its CFL limit, so this can only SLOW waves over the shallows relative to the
    // deepest water — which is the correct direction: they shorten and pile up toward the beach.
    // Reference depth = the offshore depth that counts as "deep". Anything deeper runs at FULL
    // speed (the ratio clamps at 1), so the slowdown stays local to the shelf instead of demoting
    // the whole tank — that was the slow-mo. Green's law gain uses the same reference.
    private bool _shoal;
    private float _shoalRef = 0.0739f, _shoalGainMax = 3.0f;

    // [distress] Green's law: H ~ h^-1/4 while L ~ h^1/2, so steepness H/L ~ h^-3/4 — it escalates
    // into the shallows. Break where H/h crosses the trigger (0.78 is the standard criterion).
    // Agitators inject chop in a band around that predicted break line.
    private float _breakTrigger = 0.7785f;
    private bool _agitOn = false, _agitViz;
    private float _agitStrength = 0.004f, _agitScatter = 0.12f, _agitCount = 23.995f;
    private const int MaxAgit = 256;
    private readonly MeshInstance3D[] _agitMarks = new MeshInstance3D[MaxAgit];
    private int _agitIdx;
    private float _breakX = 2.0f;   // outside [-1,1] = nothing breaks inside the tank

    // k^2-selective loss: velocity diffusion, so chop dies fast while swell survives. This is the
    // knob that separates "settles" from "viscous" — flat decay can only do the latter.
    private float _chopDamp = 0.005f;

    // ---- MNA stamp knobs ----
    // _waveG    gravity: wave speed is sqrt(g*h) in POOL UNITS/SEC and no longer depends on dt.
    // _sweeps   Jacobi relaxations per tick — the accuracy/cost dial the implicit form buys us.
    // _absorb   matched-resistor conductance on the boundary cells (0 = mirror walls, as scene 23).
    // Stamp knobs, matching scene 50's set:
    //   _cn      0 = backward-Euler (dissipative) -> 1 = Crank-Nicolson (energy-neutral, rings)
    //   _leak    conductance to the rest datum — a DISPLACEMENT loss. Damping is a velocity loss,
    //            so a statically forced mound (dh/dt = 0) never sees it; only leak flattens it.
    //   sponge   graded quadratic absorbing ramp — no impedance step for waves to reflect off
    private float _waveG = 0.2885f, _sweeps = 20.37f, _normalScale = 1508.0f;
    private float _cn = 0.89f, _leak = 0.000257f;
    // Leak is the knob that matters and it lives near zero, so a linear 0-0.3 slider would give
    // ~0.001 per pixel — coarser than the value we are sitting at. Drive it from a 0..1 control
    // through a CUBE, so the bottom of the travel resolves ~1e-6 while the top still reaches 0.1.
    // DC-drift strategy. A monopole paddle pumps net volume into a closed box, so SOMETHING has
    // to stop the mean level running away. The uniform spring (mode 1) does it but turns the wave
    // equation into Klein-Gordon: omega^2 = c^2k^2 + kappa, i.e. a CUTOFF at f0 = sqrt(k)*Hz/2pi
    // below which waves cannot propagate at all. Modes 3 and 5 remove the drift without that floor.
    private int _leakMode = 1;
    private float _leakCutoffHz = 0.25f;   // mode 2 authoring
    private float _dcCorrect;              // mode 3, computed per tick
    private float _leakT = 0.137f;   // 0.1 * t^3 = 2.57e-4 (your tuned value, re-solved for the new cap)
    private float _gamma;   // scene-50 style direct damping: a += gamma*dt/2   // bleed as low as it goes
    private float _spongeW = 0.0f, _spongeA = 0.0f;   // sponge OFF for now
    // edge mask: 1 = -x paddle wall, 2 = +x beach, 4/8 = z walls. Default excludes the paddle.
    private bool _spongePaddleWall;
    private float _minDepth = 0.0345f;
    private float _bathyMix = 0.145f;
    private float _pokeStrength = 0.011f;   // click poke — the only way to make a BIG SHORT wave   // 0 = uniform depth (scene 23 parity) .. 1 = full depth-varying
    private float _substeps = 1.0f;

    // Candidate-point visualisation (render-only; agitates nothing yet).
    private bool _vizPoints;
    private bool _debugField;
    private float _debugGain = 208.48f;
    private float _vizDensity = 157.25f, _vizSize = 0.49f;
    private float _crestLevel = 0.0203f, _crestSlope = 0.10f;
    private float _fricDepth = 0.08f, _fricSlope = 0.2205f;
    private float _leadOffset = 0.236f;   // how far ahead of the crest the pink points sit
    private readonly MeshInstance3D[] _padSegs = new MeshInstance3D[MaxSegs];
    private MeshInstance3D _padRibbon = null!;
    private ImmediateMesh _padRibbonMesh = null!;

    private Node3D _tank = null!;
    private MeshInstance3D _floorMi = null!, _padMi = null!;
    private readonly MeshInstance3D[] _walls = new MeshInstance3D[4];
    private FreeCam _cam = null!;
    private bool _flyCam;
    // ---- SURF: the two knobs that make breaking waves ----
    // _surf swaps stamp_wave_tank (linear, the control) for stamp_wave_surf. Changing it
    // rebuilds the solver, same as changing grid — the stamp is compiled into the shader.
    //
    // Nonlinearity is the one that matters: the tank stamp's st_depth() reads the STATIC BED
    // only, so the wave never learns how tall it is. A linear wave cannot steepen, so it
    // cannot break, at any resolution with any solver (hypotheses.md H-W6). Turn this up and
    // the front face sharpens as the wave shoals.
    private bool _surf;
    private float _nonlinearity = 1.0f;

    // Aeration patch: waves crossing it slow and bend, because in this stamp wave speed IS the
    // conductance (H-B2). Drag it into the breaking zone to see refraction.
    private float _voidStrength;          // 0 = clear water
    private float _voidX = 0.35f;         // normalized -1..1
    private float _voidZ;
    private float _voidR = 0.25f;

    private IStampSolver? _solver;
    // 0 Jacobi · 1 RBGS · 2 ADI · 3 CG · 4 Multigrid (uniform β) · 5 Multigrid (deep)
    // · 6 Schwarz (block-dense) · 7 Spectral (uniform depth)
    private int _solverMode = 0;

    private bool _dragging, _dropActive;
    private Vector2 _dropCenter;
    private bool _autoDrip;
    private float _dripT, _fpsAccum;
    private readonly RandomNumberGenerator _rng = new();
    private Label? _readout;

    public override void _Ready()
    {
        _rng.Seed = 12345;
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("solver=")) { _solverMode = a.Substring(7).ToInt(); }
            else if (a.StartsWith("grid=")) { _gridIdx = Mathf.Clamp(a.Substring(5).ToInt(), 0, 2); }
            else if (a.StartsWith("sweeps=")) { _sweeps = a.Substring(7).ToFloat(); }
            else if (a.StartsWith("simhz=")) { _simHz = a.Substring(6).ToFloat(); }
            else if (a.StartsWith("bench=")) { _benchSecs = a.Substring(6).ToFloat(); }
            // bathy= drives extra.z, the knob that separates the solvers that invert the real
            // stamp from the ones that invert a uniform-depth stand-in (solver-ledger.md §7a).
            // It is the discriminating variable, so it has to be settable without the GUI.
            else if (a.StartsWith("bathy=")) { _bathyMix = Mathf.Clamp(a.Substring(6).ToFloat(), 0f, 1f); }
            else if (a.StartsWith("surf=")) { _surf = a.Substring(5).ToInt() != 0; }
            else if (a.StartsWith("nl=")) { _nonlinearity = a.Substring(3).ToFloat(); }
            else if (a.StartsWith("aer=")) { _voidStrength = Mathf.Clamp(a.Substring(4).ToFloat(), 0f, 0.95f); }
            // Wave HEIGHT is the nonlinearity knob — steepening scales with height/depth — and
            // the scene's defaults (gain 0.021 of a 4.0 range) make waves far too small for it
            // to show. Exposed so a surf run can open already tuned instead of hunting sliders.
            else if (a.StartsWith("padgain=")) { _padGain = a.Substring(8).ToFloat(); }
            else if (a.StartsWith("padstroke=")) { _padStroke = a.Substring(10).ToFloat(); }
        }
        if (_benchSecs > 0.0f)
        {
            // vsync pins fps at the refresh rate and hides all headroom — several configs read
            // 118-119 fps purely because the display caps there. Off for measurement.
            DisplayServer.WindowSetVsyncMode(DisplayServer.VSyncMode.Disabled);
            Engine.MaxFps = 0;
        }
        _tileTex = GD.Load<Texture2D>(TexDir + "tiles.jpg");
        _waterTex = new Texture2Drd();

        _tank = new Node3D();
        AddChild(_tank);
        BuildEnvironment();
        BuildTank();
        BuildPaddle();
        BuildWater();
        BuildAgitators();
        ApplyScale(_poolHalf);
        ApplyRenderOff();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        // The PHYSICS is the stamp; the solver is a dropdown. Same three functions drive
        // Jacobi, red-black Gauss-Seidel and Conjugate Gradient.
        var rd = RenderingServer.GetRenderingDevice();
        var grid = new Vector2I(SimSize, SimSize);
        string stamp = _surf
            ? "res://shaders/stamp/stamp_wave_surf.glslinc"
            : "res://shaders/stamp/stamp_wave_tank.glslinc";
        _solver = _solverMode switch
        {
            2 => new AdiStampSolver(rd, grid, stamp),
            3 => new GpuStampSolver(rd, grid, stamp, GpuStampSolver.Mode.Cg),
            // NOTE: MgvSolver's mg_smooth/mg_residual use a SCALAR beta, so it solves a
            // uniform-depth approximation of the stamp — exact only at bathymetry coupling 0.
            // Kept as the deliberate control for mode 5; see solver-ledger.md §7a.
            4 => new MgvSolver(rd, grid, stamp),
            5 => new MgDeepSolver(rd, grid, stamp),
            6 => new SchwarzSolver(rd, grid, stamp),
            // Exact only at bathymetry coupling 0 AND sponge 0 — it warns when it isn't.
            7 => new SpectralSolver(rd, grid, stamp, SpectralSolver.Basis.Cosine),
            // Sine basis = Dirichlet walls, which this stamp does NOT have (clamp = Neumann).
            // Kept as an option because it is the exact basis for a clamped stamp (scene 06)
            // and a usable CG preconditioner. It warns on construction.
            8 => new SpectralSolver(rd, grid, stamp, SpectralSolver.Basis.Sine),
            _ => new GpuStampSolver(rd, grid, stamp, (GpuStampSolver.Mode)Mathf.Clamp(_solverMode, 0, 1)),
        };
    }

    // Grid and stamp are both compiled into the shader, so changing either means a fresh
    // solver. Dropping the Texture2Drd rid first stops the surface sampling a freed texture
    // for the frame or two before the render thread catches up.
    private void RebuildSolver()
    {
        var old = _solver;
        _solver = null;
        _waterTex.TextureRdRid = default;
        RenderingServer.CallOnRenderThread(Callable.From(() => { old?.Free(); InitSolver(); }));
    }

    // rise per unit x; the ramp spans x in [-1,1] so the shallow end sits 2*Slope above the base
    private float Slope => Mathf.Tan(Mathf.DegToRad(_slopeDeg));
    private float FloorY(float x) => _floorBase + Slope * (x + 1.0f);

    // Where the wave is predicted to break: H(h) = H0*(href/h)^(1/4) grows as it shoals, and
    // breaking is H/h >= trigger, so  h_break = (H0 * href^0.25 / trigger)^0.8.  Solve the ramp
    // for the x that has that depth. Returns >1 when the wave never gets steep enough.
    private float BreakX()
    {
        float h0 = Mathf.Max(1.0e-4f, _waterLevel - FloorY(-1.0f));
        float href = Mathf.Min(_shoal ? _shoalRef : h0, h0);
        float bigH0 = _padStroke * _padGain * 0.5f;          // offshore height, from the piston
        if (bigH0 <= 1.0e-5f || _breakTrigger <= 1.0e-3f) { return 2.0f; }
        float hb = Mathf.Pow(bigH0 * Mathf.Pow(href, 0.25f) / _breakTrigger, 0.8f);
        if (hb >= h0) { return -1.0f; }                      // already breaking at the deep end
        float sl = Slope;
        if (sl <= 1.0e-5f) { return 2.0f; }                  // flat bed never shoals to break
        return (h0 - hb) / sl - 1.0f;
    }

    private static float Hash01(int i)
    {
        float v = Mathf.Sin(i * 12.9898f) * 43758.5453f;
        return v - Mathf.Floor(v);
    }

    // Deterministic per index, so the points hold still instead of flickering frame to frame.
    private Vector2 AgitPoint(int i)
    {
        float z = Hash01(i * 2 + 1) * 2.0f - 1.0f;
        float jx = (Hash01(i * 2 + 7) * 2.0f - 1.0f) * _agitScatter;
        return new Vector2(Mathf.Clamp(_breakX + jx, -1.0f, 1.0f), z);
    }

    // beta = (c*dt/dx)^2 with c^2 = g*h, i.e. dt^2*g/dx^2 scaled by each pipe's depth. Implicit,
    // so this is NOT bounded by 0.5 the way the explicit scheme was — it is free to exceed 1.
    private float BetaScale(double dt)
    {
        float dx = 2.0f / SimSize;
        return _waveG * (float)(dt * dt) / (dx * dx);
    }

    // amplitude x _dampPerSec per second -> the stamp's `a` (resistor to ground) for this dt
    private float StampA(double dt) =>
        (-Mathf.Log(Mathf.Clamp(_dampPerSec, 0.01f, 0.999f)) + _gamma) * (float)dt * 0.5f;

    // ---- scene ----
    private void BuildEnvironment()
    {
        _cam = new FreeCam { Fov = 50.0f, Near = 0.05f, Far = 4000.0f, Position = CamPos, Current = true, Speed = 14.0f };
        AddChild(_cam);
        // position comes from UpdateCamera() once the knobs are known

        // a real sun: the tank is lit and shadowed conventionally now
        var sun = new DirectionalLight3D { ShadowEnabled = true, LightEnergy = 1.4f };
        AddChild(sun);
        sun.LookAtFromPosition(LightDir.Normalized() * 20.0f, Vector3.Zero, Vector3.Up);

        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky
                {
                    SkyMaterial = new ProceduralSkyMaterial
                    {
                        SkyTopColor = new Color(0.22f, 0.42f, 0.72f),
                        SkyHorizonColor = new Color(0.74f, 0.82f, 0.90f),
                        GroundHorizonColor = new Color(0.62f, 0.63f, 0.64f),
                        GroundBottomColor = new Color(0.32f, 0.33f, 0.35f),
                    },
                },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 1.0f,
                ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
                TonemapMode = Godot.Environment.ToneMapper.Filmic,
            },
        });
    }

    // Four wall slabs + a floor slab, each an independent mesh with an ordinary material.
    private void BuildTank()
    {
        _tileMat = new StandardMaterial3D
        {
            AlbedoTexture = _tileTex,
            Uv1Triplanar = true,
            Uv1Scale = Vector3.One * _tileDensity,
            Roughness = 0.65f,
            Metallic = 0.0f,
        };

        for (int i = 0; i < 4; i++)
        {
            _walls[i] = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = _tileMat };
            _tank.AddChild(_walls[i]);
        }
        _floorMi = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = _tileMat };
        _tank.AddChild(_floorMi);
        UpdateTankGeometry();
    }

    // Floor slab rotated onto the ramp (the same plane the water shader tints against), and walls
    // sized to contain it — raise the floor past the old rim and the walls simply grow with it, so
    // "as high as you want" never turns the box inside out.
    private void UpdateTankGeometry()
    {
        float angle = Mathf.DegToRad(_slopeDeg);
        float len = 2.0f / Mathf.Cos(angle);
        _floorMi.Mesh = new BoxMesh { Size = new Vector3(len, 0.08f, 2.0f) };
        _floorMi.Transform = new Transform3D(
            new Basis(Vector3.Back, angle),
            new Vector3(0.0f, FloorY(0.0f) - 0.04f, 0.0f));

        // Walls track the WATER only, never the bed: raising the floor should push the ramp up
        // through the rim like a rising bank, not grow taller walls that box the camera out.
        float top = Mathf.Max(RimY, _waterLevel + 0.15f);
        float bottom = Mathf.Min(-1.0f, _floorBase - 0.1f);
        float h = top - bottom, cy = (top + bottom) * 0.5f;
        float outer = 1.0f + WallT * 0.5f;
        SetWall(0, new Vector3(-outer, cy, 0.0f), new Vector3(WallT, h, 2.0f + 2.0f * WallT));
        SetWall(1, new Vector3(outer, cy, 0.0f), new Vector3(WallT, h, 2.0f + 2.0f * WallT));
        SetWall(2, new Vector3(0.0f, cy, -outer), new Vector3(2.0f, h, WallT));
        SetWall(3, new Vector3(0.0f, cy, outer), new Vector3(2.0f, h, WallT));
        UpdatePaddleMesh();
    }

    private void SetWall(int i, Vector3 centre, Vector3 size)
    {
        _walls[i].Mesh = new BoxMesh { Size = size };
        _walls[i].Position = centre;
    }

    // Piston face at PaddleX(phase); the slab body sits behind it, inside the wall.
    private float PaddleX(double phase) => -1.0f + _padStroke * (float)(0.5 - 0.5 * Mathf.Cos((float)phase));

    // Face position at a given normalized z — the sim samples the same curve per texel.
    private float PaddleFaceX(double phase, float z)
    {
        float macro = _padUndulate
            ? _padCurveAmp * Mathf.Sin(_padLobes * Mathf.Pi * z + (float)_padCurvePhase)
            : 0.0f;
        float micro = _padMicroOn
            ? _padMicroAmp * Mathf.Sin(_padMicroLobes * Mathf.Pi * QuantZ(z) + (float)_padMicroPhase)
            : 0.0f;
        return PaddleX(phase) + macro + micro;
    }

    // segmented => snap z to the centre of its segment, exactly as the sim kernel does
    private float QuantZ(float z)
    {
        float n = Mathf.Max(1.0f, Mathf.Round(_padSegCount));
        return (Mathf.Floor((z * 0.5f + 0.5f) * n) + 0.5f) / n * 2.0f - 1.0f;
    }

    // Built as SEGMENTS rather than one slab, which is both how a real snake wavemaker is made
    // and the cheapest way for the mesh to actually show the curve.
    private void BuildPaddle()
    {
        var mat = new StandardMaterial3D { AlbedoColor = new Color(0.80f, 0.42f, 0.22f), Roughness = 0.5f };
        _padMi = new MeshInstance3D();     // kept as the parent handle for visibility
        _tank.AddChild(_padMi);
        for (int i = 0; i < MaxSegs; i++)
        {
            _padSegs[i] = new MeshInstance3D { Mesh = new BoxMesh(), MaterialOverride = mat };
            _tank.AddChild(_padSegs[i]);
        }
        // the continuous face is a per-frame ribbon, not boxes — ImmediateMesh is built for this
        _padRibbonMesh = new ImmediateMesh();
        _padRibbon = new MeshInstance3D
        {
            Mesh = _padRibbonMesh,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.80f, 0.42f, 0.22f),
                Roughness = 0.5f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            },
        };
        _tank.AddChild(_padRibbon);
        UpdatePaddleMesh();
    }

    // The piston spans the tank's full current depth, so it keeps working as the floor rises.
    private void UpdatePaddleMesh()
    {
        if (_padMi == null) { return; }
        // Walls track the WATER only, never the bed: raising the floor should push the ramp up
        // through the rim like a rising bank, not grow taller walls that box the camera out.
        float top = Mathf.Max(RimY, _waterLevel + 0.15f);
        float bottom = Mathf.Min(-1.0f, _floorBase - 0.1f);
        int n = (int)Mathf.Round(_padSegCount);
        // boxes only while the count is small enough to instance; past that the ribbon shows it
        bool boxes = _padOn && _padSegmented && n <= MaxSegs;
        for (int i = 0; i < MaxSegs; i++)
        {
            if (i >= n || !boxes) { _padSegs[i].Visible = false; continue; }
            float segZ = 2.0f / n;
            float z = -1.0f + segZ * (i + 0.5f);
            ((BoxMesh)_padSegs[i].Mesh).Size = new Vector3(WallT, top - bottom, segZ);
            _padSegs[i].Position = new Vector3(PaddleFaceX(_padPhase, z) - WallT * 0.5f, (top + bottom) * 0.5f, z);
            _padSegs[i].Visible = true;
        }

        _padRibbon.Visible = _padOn && !boxes;
        if (!_padRibbon.Visible) { return; }
        // enough strips to resolve the steps the sim is actually using
        int R = Mathf.Clamp(n * 6, 96, 1536);
        _padRibbonMesh.ClearSurfaces();
        _padRibbonMesh.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        for (int i = 0; i < R; i++)
        {
            float z0 = -1.0f + 2.0f * i / R, z1 = -1.0f + 2.0f * (i + 1) / R;
            float x0 = PaddleFaceX(_padPhase, z0), x1 = PaddleFaceX(_padPhase, z1);
            _padRibbonMesh.SurfaceSetNormal(new Vector3(1.0f, 0.0f, 0.0f));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x0, top, z0));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x0, bottom, z0));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x1, top, z1));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x1, top, z1));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x0, bottom, z0));
            _padRibbonMesh.SurfaceAddVertex(new Vector3(x1, bottom, z1));
        }
        _padRibbonMesh.SurfaceEnd();
    }

    private void BuildAgitators()
    {
        for (int i = 0; i < MaxAgit; i++)
        {
            _agitMarks[i] = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.022f, Height = 0.044f, RadialSegments = 10, Rings = 6 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = new Color(0.1f, 1.0f, 0.35f),
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                },
                Visible = false,
            };
            _tank.AddChild(_agitMarks[i]);
        }
    }

    // green = that point is live (the wave is breaking there), red = armed but nothing is breaking
    private void UpdateAgitViz(bool active)
    {
        int n = Mathf.Clamp((int)Mathf.Round(_agitCount), 1, MaxAgit);
        for (int i = 0; i < MaxAgit; i++)
        {
            bool show = _agitViz && _agitOn && i < n;
            _agitMarks[i].Visible = show;
            if (!show) { continue; }
            var pt = AgitPoint(i);
            _agitMarks[i].Position = new Vector3(pt.X, _waterLevel + 0.03f, pt.Y);
            ((StandardMaterial3D)_agitMarks[i].MaterialOverride).AlbedoColor =
                active ? new Color(0.1f, 1.0f, 0.35f) : new Color(1.0f, 0.25f, 0.2f);
        }
    }

    private void BuildWater()
    {
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tank/tank_water_mna.gdshader") };
        _waterMat.SetShaderParameter("sim_texel", 1.0f / SimSize);
        _waterMat.SetShaderParameter("normal_gain", _normalScale);
        _waterMat.SetShaderParameter("water_tex", _waterTex);
        PushWaterKnobs();
        _tank.AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(2.0f, 2.0f), SubdivideWidth = WaterDetail, SubdivideDepth = WaterDetail },
            MaterialOverride = _waterMat,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)),
        });
    }

    private void PushWaterKnobs()
    {
        _waterMat.SetShaderParameter("water_level", _waterLevel);
        _waterMat.SetShaderParameter("floor_base", _floorBase);
        _waterMat.SetShaderParameter("slope", Slope);
        _waterMat.SetShaderParameter("shoal_ref", _shoal ? _shoalRef : 0.0f);
        _waterMat.SetShaderParameter("shoal_gain_max", _shoalGainMax);
        _waterMat.SetShaderParameter("viz_points", _vizPoints ? 1.0f : 0.0f);
        _waterMat.SetShaderParameter("viz_density", _vizDensity);
        _waterMat.SetShaderParameter("viz_size", _vizSize);
        _waterMat.SetShaderParameter("crest_level", _crestLevel);
        _waterMat.SetShaderParameter("crest_slope", _crestSlope);
        _waterMat.SetShaderParameter("friction_depth", _fricDepth);
        _waterMat.SetShaderParameter("friction_slope", _fricSlope);
        _waterMat.SetShaderParameter("lead_offset", _leadOffset);
        _waterMat.SetShaderParameter("debug_field", _debugField ? 1.0f : 0.0f);
        _waterMat.SetShaderParameter("debug_gain", _debugGain);
    }

    // Hide every drawn mesh so ms/frame is dominated by the solve.
    private void ApplyRenderOff()
    {
        // Toggle the PARENT, never the children: walking children and setting Visible=true
        // clobbers the per-mesh flags that hide agitation markers and unused paddle segments.
        if (_tank != null) { _tank.Visible = !_renderOff; }
    }

    // Scale grows the TANK only — the camera holds its x/z, so the pool grows in frame.
    private void ApplyScale(float s)
    {
        _poolHalf = s;
        _tank.Scale = Vector3.One * s;
        UpdateCamera();
    }

    // Height is the one axis the camera cannot hold fixed: raising the floor lifts the whole water
    // surface (water_level is in pool units, so x_world = level * poolHalf), and at scale 20 a
    // floor of 0.6 puts the surface ~17 units up — above a camera parked at 15, which then just
    // stares at the outside of the wall. So the rig rides the waterline and keeps its x/z.
    private void UpdateCamera()
    {
        if (_flyCam) { return; }   // hands off while you are flying
        float lift = _waterLevel * _poolHalf + _camLift;
        _cam.Position = CamPos + new Vector3(0.0f, lift, 0.0f);
        _cam.LookAt(CamTarget + new Vector3(0.0f, lift, 0.0f), Vector3.Up);
    }

    public override void _Process(double delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        float dt = Mathf.Min((float)delta, 0.05f);

        var dropCenter = Vector2.Zero;
        float dropStrength = 0.0f;
        if (_dropActive) { dropCenter = _dropCenter; dropStrength = _pokeStrength; _dropActive = false; }
        else if (_autoDrip)
        {
            _dripT += dt;
            if (_dripT >= 0.35f) { _dripT = 0.0f; dropCenter = new Vector2(_rng.Randf() * 1.6f - 0.8f, _rng.Randf() * 1.6f - 0.8f); dropStrength = 0.02f; }
        }

        _waterTex.TextureRdRid = solver.HeightRid;

        // Fixed-timestep accumulator: consume real time in whole 1/_simHz ticks. A mouse drop is a
        // one-shot event so it rides the FIRST tick only. The PADDLE advances inside the loop —
        // each tick gets its own (old x -> new x), which is exactly the stroke the sim pushes with,
        // so the wavemaker is frame-rate independent for free.
        float dcx = dropCenter.X, dcz = dropCenter.Y, ds = dropStrength;
        bool dropPending = dropStrength > 0.0f;
        _breakX = BreakX();
        int agitN = Mathf.Clamp((int)Mathf.Round(_agitCount), 1, MaxAgit);
        bool agitActive = _agitOn && _breakX >= -1.0f && _breakX <= 1.0f;
        UpdateAgitViz(agitActive);
        float padW = _padWidth, padG = _padGain;
        bool padOn = _padOn;
        double h = 1.0 / Mathf.Max(1.0f, _simHz);
        _simAcc += dt;
        _ticksLast = 0;
        while (_simAcc >= h && _ticksLast < MaxTicksPerFrame)
        {
            bool drop = dropPending;
            float ddx = dcx, ddz = dcz, dds = ds;
            if (!drop && agitActive)
            {
                // round-robin: one agitator fires per sim tick, so the whole band stays alive
                var apt = AgitPoint(_agitIdx);
                _agitIdx = (_agitIdx + 1) % agitN;
                ddx = apt.X; ddz = apt.Y; dds = _agitStrength; drop = true;
            }
            float pxOld = PaddleX(_padPhase);
            float cphOld = (float)_padCurvePhase;
            float mPhOld = (float)_padMicroPhase;
            if (padOn)
            {
                // curve layers NEVER stop — they run at their own rates regardless of the stroke
                _padCurvePhase += 2.0 * Mathf.Pi * _padSnakeHz * h;
                _padMicroPhase += 2.0 * Mathf.Pi * _padMicroHz * h;

                // warped stroke: push over 1/_padHz, return stretched over _padDelay
                double push = 1.0 / Mathf.Max(0.001f, _padHz);
                double cycleT = push + _padDelay;
                _padCycle += h / cycleT;
                if (_padCycle >= 1.0) { _padCycle -= 1.0; }
                double sFrac = push / cycleT;
                double u = _padCycle;
                double warped = u < sFrac
                    ? 0.5 * u / sFrac                          // out to full extension, at rate
                    : 0.5 + 0.5 * (u - sFrac) / (1.0 - sFrac); // elongated return
                _padPhase = 2.0 * Mathf.Pi * warped;
            }
            float pxNew = PaddleX(_padPhase);
            float cphNew = (float)_padCurvePhase;
            float mPhNew = (float)_padMicroPhase;
            float cAmp = _padUndulate ? _padCurveAmp : 0.0f;
            float cLobes = _padLobes;
            float cSegs = Mathf.Max(1.0f, Mathf.Round(_padSegCount));
            float mAmp = _padMicroOn ? _padMicroAmp : 0.0f;
            float mLobes = _padMicroLobes;
            // ---- DC-drift strategy ----
            float kappa = 0.0f;
            float spongeA = _spongeA;
            _dcCorrect = 0.0f;
            switch (_leakMode)
            {
                case 1: kappa = _leak; break;                                   // uniform spring
                case 2:                                                          // authored cutoff
                    float w0 = 2.0f * Mathf.Pi * _leakCutoffHz / Mathf.Max(1.0f, _simHz);
                    kappa = w0 * w0;
                    break;
                case 3:                                                          // volume balance
                    // mean of what the paddle just injected: (dFace * gain * reach * 2) / area(4)
                    _dcCorrect = padOn ? (pxNew - pxOld) * padG * padW * 0.5f : 0.0f;
                    break;
                case 4: spongeA = Mathf.Max(spongeA, 0.35f); break;              // boundary only
                case 5: break;                                                   // dipole source
            }

            // one stamped tick — layout fixed by stamp_wave_tank.glslinc
            int subs = Mathf.Clamp((int)Mathf.Round(_substeps), 1, 4);
            double sh = h / subs;
            float halfN = SimSize * 0.5f;
            var pcf = new float[]
            {
                SimSize, SimSize, BetaScale(sh), StampA(sh),
                _leakMode == 3 ? _dcCorrect : kappa, _cn, _spongeW, spongeA,
                _floorBase, Slope, _waterLevel, _minDepth,
                padOn ? pxOld : 0f, padOn ? pxNew : 0f, padW, padOn ? padG : 0f,
                cAmp, cLobes, cphOld, cphNew,
                // SURF stamp spends the micro-paddle slots on the void/aeration patch, and
                // extra.x on nonlinearity (see stamp_wave_surf.glslinc). Mutually exclusive
                // with micro chop, which is why those are the slots it takes.
                _surf ? _voidStrength : mAmp,
                _surf ? _voidX : mLobes,
                _surf ? _voidZ : mPhOld,
                _surf ? _voidR : mPhNew,
                drop ? (ddx * 0.5f + 0.5f) * SimSize : 0f,
                drop ? (ddz * 0.5f + 0.5f) * SimSize : 0f,
                Mathf.Max(1.0f, _rippleSize * halfN),
                drop ? dds : 0f,
                _surf ? _nonlinearity : cSegs,
                (_spongePaddleWall ? 15f : 14f) + 16f * _leakMode, _bathyMix,
                Mathf.Max(0.02f, _waterLevel - FloorY(-1.0f)),
            };
            var pcb = new byte[pcf.Length * sizeof(float)];
            System.Buffer.BlockCopy(pcf, 0, pcb, 0, pcb.Length);
            int sweeps = Mathf.Clamp((int)Mathf.Round(_sweeps), 1, 64);
            bool measure = _ticksLast == 0 || _benchSecs > 0.0f;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                for (int sub = 0; sub < subs; sub++) { solver.Step(pcb, sweeps, measure && sub == 0); }
            }));
            dropPending = false;
            _simAcc -= h;
            _ticksLast++;
        }
        if (_ticksLast >= MaxTicksPerFrame) { _simAcc = 0.0; }   // drop the backlog, don't spiral
        UpdatePaddleMesh();

        _msAccum += delta * 1000.0; _msFrames++;

        if (_benchSecs > 0.0f)
        {
            _benchT += dt;
            if (_benchT > _benchSecs * 0.4f)   // discard warm-up / shader compile
            {
                _bMs += delta * 1000.0;
                _bRes += solver.LastResidual;
                _bTicks += _ticksLast;      // to recover the ACHIEVED sim rate
                _bN++;
            }
            if (_benchT >= _benchSecs)
            {
                int it = Mathf.Clamp((int)Mathf.Round(_sweeps), 1, 150);
                double ms = _bN > 0 ? _bMs / _bN : 0.0;
                double rs = _bN > 0 ? _bRes / _bN : 0.0;
                double tf = _bN > 0 ? _bTicks / _bN : 0.0;      // mean ticks per frame
                double fps = ms > 0 ? 1000.0 / ms : 0.0;
                double achievedHz = fps * tf;                    // sim ticks actually delivered
                double realtime = achievedHz / Mathf.Max(1.0f, _simHz);   // 1.0 = real time
                GD.Print($"BENCH,{solver.ModeName},{SimSize},{it},{solver.PassesPerStep(it)}," +
                         $"{fps:0.0},{ms:0.00},{tf:0.00},{achievedHz:0},{realtime:0.00},{rs:0.000000e+00}");
                GetTree().Quit();
            }
        }

        _fpsAccum += dt;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            string mode = _solver?.ModeName ?? "-";
            float res = _solver?.LastResidual ?? 0f;
            int passes = _solver?.PassesPerStep(Mathf.Clamp((int)Mathf.Round(_sweeps), 1, 150)) ?? 0;
            _msFrame = _msFrames > 0 ? (float)(_msAccum / _msFrames) : 0f;
            _msAccum = 0.0; _msFrames = 0;
            // ms/tick is DERIVED (frame time / ticks) and includes render — good for A/B at a
            // fixed scene, not an absolute solver cost. GPU timestamps are not exposed by RD.
            float msTick = _ticksLast > 0 ? _msFrame / _ticksLast : 0f;
            _readout.Text = $"{_msFrame:0.0} ms/f · {mode} {SimSize}² · {passes} passes/tick · {msTick:0.00} ms/tick · res {res:0.0000e+00} · κ {_leak:0.00000} ({_ticksLast} ticks/f)";
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Left)
        {
            _dragging = mb.Pressed && CastDrop(mb.Position);
        }
        else if (@event is InputEventMouseMotion mm && _dragging)
        {
            CastDrop(mm.Position);
        }
    }

    // Rays are cast into NORMALIZED pool space (origin / _poolHalf) — a uniform scale leaves the
    // direction alone, so the [-1,1] bounds below still hold at any tank size.
    private bool CastDrop(Vector2 pos)
    {
        Vector3 origin = _cam.ProjectRayOrigin(pos) / _poolHalf, dir = _cam.ProjectRayNormal(pos);
        return DropRay(origin, dir);
    }

    private bool DropRay(Vector3 origin, Vector3 dir)
    {
        if (Mathf.Abs(dir.Y) < 1e-6f) { return false; }
        float t = (_waterLevel - origin.Y) / dir.Y;
        if (t <= 0.0f) { return false; }
        var p = origin + dir * t;
        if (Mathf.Abs(p.X) < 1.0f && Mathf.Abs(p.Z) < 1.0f)
        {
            _dropCenter = new Vector2(p.X, p.Z);
            _dropActive = true;
            return true;
        }
        return false;
    }

    public override void _ExitTree()
    {
        if (_waterTex != null) { _waterTex.TextureRdRid = default; }
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "25 · wave tank — MNA stamp solver",
            "No raytracing (scene 24 still has it): walls, floor and the paddle are ordinary meshes "
            + "with normal materials lit by a real sun, and only the water surface is a shader — sim "
            + "normals, screen-texture refraction, analytic depth tint off the ramp. The ball is gone; "
            + "waves now come from a PISTON PADDLE spanning the deep-end wall. Stroke = how far it "
            + "moves off that wall (amplitude), Rate = how often (wavelength). It advances inside the "
            + "fixed-timestep loop, so it is frame-rate independent. The sim is still flat-depth: waves "
            + "do NOT shoal. DRAG the water for extra ripples.");
        _readout = ui.AddReadout("— fps");
        ui.AddToggle("Paddle (wavemaker)", _padOn, v => { _padOn = v; UpdatePaddleMesh(); });
        ui.AddToggle("Auto-drip (idle)", _autoDrip, v => _autoDrip = v);
        ui.AddSlider("Paddle stroke (off back wall)", 0.0f, 0.5f, _padStroke, v => _padStroke = v);
        ui.AddSlider("Paddle rate (strokes/sec)", 0.05f, 3.0f, _padHz, v => _padHz = v);
        ui.AddSlider("Return elongation (s)", 0.0f, 15.0f, _padDelay, v => _padDelay = v);
        ui.AddSlider("Paddle push gain", 0.001f, 4.0f, _padGain, v => _padGain = v);
        ui.AddSlider("Paddle reach (falloff)", 0.01f, 0.3f, _padWidth, v => _padWidth = v);
        // Curved face: 0 = straight piston. Lobes sets how many bulges across the tank; snake rate
        // travels the curve along z, which is what makes oblique / short-crested fronts.
        ui.AddToggle("Undulate face (curved edge)", _padUndulate, v => { _padUndulate = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro curve (amplitude)", 0.0f, 0.4f, _padCurveAmp, v => { _padCurveAmp = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro lobes (across z)", 0.5f, 8.0f, _padLobes, v => { _padLobes = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro snake rate (Hz, ± dir)", -1.5f, 1.5f, _padSnakeHz, v => _padSnakeHz = v);
        ui.AddToggle("Micro segments (2nd jostler)", _padMicroOn, v => { _padMicroOn = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro amplitude", 0.0f, 0.12f, _padMicroAmp, v => { _padMicroAmp = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro lobes", 2.0f, 256.0f, _padMicroLobes, v => { _padMicroLobes = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro snake rate (Hz, ± dir)", -4.0f, 4.0f, _padMicroHz, v => _padMicroHz = v);
        ui.AddSlider("Segments (micro step size)", 1.0f, MaxSimSegs, _padSegCount, v => { _padSegCount = v; UpdatePaddleMesh(); });
        ui.AddToggle("Segmented bank (mesh style)", _padSegmented, v => { _padSegmented = v; UpdatePaddleMesh(); });
        ui.AddToggle("EXTRA render gain (sim already shoals)", _shoal, v => { _shoal = v; PushWaterKnobs(); });
        ui.AddSlider("Extra gain ref depth", 0.02f, 1.0f, _shoalRef, v => { _shoalRef = v; PushWaterKnobs(); });
        ui.AddSlider("Extra gain max (double-counts)", 1.0f, 6.0f, _shoalGainMax, v => { _shoalGainMax = v; PushWaterKnobs(); });
        ui.AddSlider("Breaking trigger (H/h)", 0.2f, 1.5f, _breakTrigger, v => _breakTrigger = v);
        ui.AddToggle("Agitate at break line", _agitOn, v => _agitOn = v);
        ui.AddToggle("Show agitation points", _agitViz, v => _agitViz = v);
        ui.AddSlider("Agitation strength", 0.0f, 0.02f, _agitStrength, v => _agitStrength = v);
        ui.AddSlider("Agitation points", 1.0f, MaxAgit, _agitCount, v => _agitCount = v);
        ui.AddSlider("Agitation scatter", 0.0f, 0.5f, _agitScatter, v => _agitScatter = v);
        ui.AddSlider("Scale (tank half-width)", 1.0f, 20.0f, _poolHalf, ApplyScale);
        ui.AddSlider("Ripple size (drop radius)", 0.003f, 0.06f, _rippleSize, v => _rippleSize = v);
        ui.AddSlider("Click poke strength", 0.001f, 0.5f, _pokeStrength, v => _pokeStrength = v);
        ui.AddToggle("Fly camera (RMB look, WASD, Q/E)", _flyCam, v => { _flyCam = v; if (!v) { UpdateCamera(); } });
        // Slope is a true angle; 0-40 deg over 200 steps = 0.2 deg resolution, so 1.0 is exact.
        ui.AddSlider("Slope angle (deg)", 0.0f, 40.0f, _slopeDeg, v => { _slopeDeg = v; UpdateTankGeometry(); PushWaterKnobs(); });
        ui.AddSlider("Floor height (deep end)", -1.0f, 3.0f, _floorBase, v => { _floorBase = v; UpdateTankGeometry(); PushWaterKnobs(); });
        ui.AddSlider("Water level", -1.0f, 3.0f, _waterLevel, v => { _waterLevel = v; UpdateTankGeometry(); PushWaterKnobs(); UpdateCamera(); });
        ui.AddSlider("Camera height nudge", -20.0f, 40.0f, _camLift, v => { _camLift = v; UpdateCamera(); });
        ui.AddSlider("Tile density", 0.5f, 8.0f, _tileDensity, v => _tileMat.Uv1Scale = Vector3.One * v);
        ui.AddSlider("Refraction", 0.0f, 0.1f, _refraction, v => _waterMat.SetShaderParameter("refraction", v));
        ui.AddSlider("Depth absorption", 0.2f, 6.0f, _absorption, v => _waterMat.SetShaderParameter("absorption", v));
        // Sim rate now sets wave SPEED (the kernel advances a fixed amount per step); damping is
        // per second, so cranking the rate no longer changes how fast waves die.
        ui.AddSlider("Sim rate (Hz) = wave speed", 30.0f, 960.0f, _simHz, v => _simHz = v);
        ui.AddSlider("Damping · decay /sec (1 = off)", 0.90f, 1.0f, _dampPerSec, v => _dampPerSec = v);
        // k^2 loss: kills chop without touching swell. Raise this and RAISE wave decay together.
        ui.AddSlider("Damping · MNA γ (scene-50 style)", 0.0f, 2.0f, _gamma, v => _gamma = v);
        ui.AddSlider("Damping · chop (k²)", 0.0f, 0.5f, _chopDamp, v => _chopDamp = v);
        ui.AddSlider("Gravity g (wave speed)", 0.05f, 8.0f, _waveG, v => _waveG = v);
        ui.AddToggle("Render OFF (solver-only timing)", _renderOff, v => { _renderOff = v; ApplyRenderOff(); });
        ui.AddOptions("Grid (sweep me)", new[] { "256²", "512²", "1024²" }, _gridIdx, i =>
        {
            _gridIdx = i;
            RebuildSolver();
            _waterMat.SetShaderParameter("sim_texel", 1.0f / SimSize);
        });

        // ---- SURF ----
        // The stamp is compiled into the shader, so switching it rebuilds the solver exactly
        // like switching grid. Linear is the control: identical to every measurement in the
        // ledger. Surf adds nonlinear depth + the aeration patch.
        ui.AddOptions("Stamp", new[] { "Linear (tank)", "Surf (nonlinear + void)" }, _surf ? 1 : 0, i =>
        {
            _surf = i == 1;
            RebuildSolver();
        });
        // THE knob. 0 = the shipped linear operator, where the wave never learns how tall it
        // is and therefore cannot steepen or break. 1 = full physical nonlinearity: depth is
        // bed + surface, so the crest outruns the trough and the front face sharpens.
        // Strongest on a SLOPING bed — shoaling raises amplitude and lowers depth together.
        // Raise "Bathymetry coupling" and the slope angle with it.
        ui.AddSlider("Surf · nonlinearity (0 = linear)", 0.0f, 1.5f, _nonlinearity, v => _nonlinearity = v);
        // Aeration: waves crossing the patch slow and bend. Wave speed IS the conductance here,
        // so this is a real medium change, not a damping hack.
        ui.AddSlider("Surf · aeration strength", 0.0f, 0.95f, _voidStrength, v => _voidStrength = v);
        ui.AddSlider("Surf · aeration X", -1.0f, 1.0f, _voidX, v => _voidX = v);
        ui.AddSlider("Surf · aeration Z", -1.0f, 1.0f, _voidZ, v => _voidZ = v);
        ui.AddSlider("Surf · aeration radius", 0.02f, 1.0f, _voidR, v => _voidR = v);
        ui.AddOptions("Solver", new[]
        {
            "Jacobi", "RBGS", "ADI (line)", "CG",
            "Multigrid (uniform β)",   // the control: scalar-β operator, 2 levels
            "Multigrid (deep)",        // stamp operator, full pyramid
            "Schwarz (block-dense)",
            "Spectral DCT (Neumann)",   // matches this stamp's clamp() walls
            "Spectral DST (Dirichlet)", // wrong walls here on purpose — see SpectralSolver
        }, _solverMode, i =>
        {
            _solverMode = i;
            var old = _solver;
            _solver = null;
            _waterTex.TextureRdRid = default;   // stop pointing at a texture we are about to free
            RenderingServer.CallOnRenderThread(Callable.From(() => { old?.Free(); InitSolver(); }));
        });
        // For ADI this is FULL iterations (an x-line pass + a z-line pass each); 2-4 is plenty
        // because each pass solves its lines exactly instead of nudging one cell.
        ui.AddSlider("Sweeps / tick", 1.0f, 150.0f, _sweeps, v => _sweeps = v);
        ui.AddSlider("Substeps (oversampling)", 1.0f, 4.0f, _substeps, v => _substeps = v);
        ui.AddSlider("Scheme  BE 0 → 1 CN (rings)", 0.0f, 1.0f, _cn, v => _cn = v);
        ui.AddOptions("DC drift mode", new[]
        {
            "0 · none (κ=0, may drift)",
            "1 · uniform spring κ (cutoff!)",
            "2 · authored cutoff (Hz)",
            "3 · volume balance (no cutoff)",
            "4 · boundary leak only",
            "5 · dipole paddle (no net volume)",
        }, _leakMode, i => _leakMode = i);
        ui.AddSlider("  └ mode 2: cutoff f₀ (Hz)", 0.0f, 2.0f, _leakCutoffHz, v => _leakCutoffHz = v);
        ui.AddSlider("Rest leak κ  (curve → 0.1 max)", 0.0f, 1.0f, _leakT, v =>
        {
            _leakT = v;
            _leak = 0.1f * v * v * v;
        });
        ui.AddSlider("Min depth (shore clamp)", 0.005f, 0.3f, _minDepth, v => _minDepth = v);
        ui.AddSlider("Bathymetry coupling (0 = flat)", 0.0f, 1.0f, _bathyMix, v => _bathyMix = v);
        ui.AddToggle("Sponge on PADDLE wall (eats the stroke)", _spongePaddleWall, v => _spongePaddleWall = v);
        ui.AddSlider("Sponge width (px)", 0.0f, 48.0f, _spongeW, v => _spongeW = v);
        ui.AddSlider("Sponge damping", 0.0f, 1.0f, _spongeA, v => _spongeA = v);
        ui.AddSlider("Normal gain", 16.0f, 3000.0f, _normalScale, v =>
        {
            _normalScale = v;
            _waterMat.SetShaderParameter("normal_gain", v);
        });
        ui.AddToggle("DEBUG raw field (no discard)", _debugField, v => { _debugField = v; PushWaterKnobs(); });
        ui.AddSlider("Debug gain", 1.0f, 400.0f, _debugGain, v => { _debugGain = v; PushWaterKnobs(); });
        ui.AddToggle("Show candidate points", _vizPoints, v => { _vizPoints = v; PushWaterKnobs(); });
        ui.AddSlider("Point density", 10.0f, 500.0f, _vizDensity, v => { _vizDensity = v; PushWaterKnobs(); });
        ui.AddSlider("Point size", 0.05f, 0.6f, _vizSize, v => { _vizSize = v; PushWaterKnobs(); });
        ui.AddSlider("Crest level (yellow)", 0.0f, 0.15f, _crestLevel, v => { _crestLevel = v; PushWaterKnobs(); });
        ui.AddSlider("Crest front slope", 0.0f, 0.5f, _crestSlope, v => { _crestSlope = v; PushWaterKnobs(); });
        ui.AddSlider("Friction depth (orange)", 0.0f, 0.4f, _fricDepth, v => { _fricDepth = v; PushWaterKnobs(); });
        ui.AddSlider("Friction motion", 0.0f, 0.3f, _fricSlope, v => { _fricSlope = v; PushWaterKnobs(); });
        ui.AddSlider("Lead offset (pink ahead)", 0.0f, 0.4f, _leadOffset, v => { _leadOffset = v; PushWaterKnobs(); });
    }
}
