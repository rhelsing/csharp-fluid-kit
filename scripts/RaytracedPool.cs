using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

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
public partial class RaytracedPool : Node3D
{
    private const int SimSize = 1024;
    private const string TexDir = "res://textures/webgpu_water/";
    private const int WaterDetail = 400;

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
    private float _simHz = 118.35f;
    private float _dampPerSec = 0.748f;
    private double _simAcc;
    private int _ticksLast;
    private const int MaxTicksPerFrame = 12;

    // [wavemaker] A piston paddle spanning the deep-end wall (x = -1). It rests flush with the
    // wall and strokes forward by _padStroke pool units, _padHz times a second; the sim's paddle
    // pass pushes the water in front of its face by whatever it moved that tick. Stroke and rate
    // are the wave amplitude/wavelength controls — this replaces the ball entirely.
    private bool _padOn = true;
    private float _padStroke = 0.1825f, _padHz = 0.2123f, _padGain = 3.259f, _padWidth = 0.0941f;
    private double _padPhase;

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
    private float _padCurveAmp = 0.062f, _padLobes = 3.65f, _padSnakeHz = 0.1425f, _padSegCount = 124.675f;
    private double _padCurvePhase;

    // MICRO layer — a second, independent jostler on the same face: many small segments snaking
    // fast. Summed with the macro undulation, so either or both can run.
    private bool _padMicroOn = true;
    private float _padMicroAmp = 0.0252f, _padMicroLobes = 102.33f, _padMicroHz = 0.18f;
    private double _padMicroPhase;

    // [shoaling] Depth-dependent wave speed, off by default. c^2 ~ g*h, and the explicit scheme is
    // already at its CFL limit, so this can only SLOW waves over the shallows relative to the
    // deepest water — which is the correct direction: they shorten and pile up toward the beach.
    // Reference depth = the offshore depth that counts as "deep". Anything deeper runs at FULL
    // speed (the ratio clamps at 1), so the slowdown stays local to the shelf instead of demoting
    // the whole tank — that was the slow-mo. Green's law gain uses the same reference.
    private bool _shoal;
    private float _shoalRef = 0.1523f, _shoalGainMax = 3.0f;

    // [distress] Green's law: H ~ h^-1/4 while L ~ h^1/2, so steepness H/L ~ h^-3/4 — it escalates
    // into the shallows. Break where H/h crosses the trigger (0.78 is the standard criterion).
    // Agitators inject chop in a band around that predicted break line.
    private float _breakTrigger = 0.7785f;
    private bool _agitOn = true, _agitViz;
    private float _agitStrength = 0.004f, _agitScatter = 0.12f, _agitCount = 23.995f;
    private const int MaxAgit = 64;
    private readonly MeshInstance3D[] _agitMarks = new MeshInstance3D[MaxAgit];
    private int _agitIdx;
    private float _breakX = 2.0f;   // outside [-1,1] = nothing breaks inside the tank

    // k^2-selective loss: velocity diffusion, so chop dies fast while swell survives. This is the
    // knob that separates "settles" from "viscous" — flat decay can only do the latter.
    private float _chopDamp = 0.05f;

    // Candidate-point visualisation (render-only; agitates nothing yet).
    private bool _vizPoints = true;
    private float _vizDensity = 157.25f, _vizSize = 0.49f;
    private float _crestLevel = 0.0203f, _crestSlope = 0.10f;
    private float _fricDepth = 0.08f, _fricSlope = 0.0405f;
    private float _leadOffset = 0.236f;   // how far ahead of the crest the pink points sit
    private readonly MeshInstance3D[] _padSegs = new MeshInstance3D[MaxSegs];
    private MeshInstance3D _padRibbon = null!;
    private ImmediateMesh _padRibbonMesh = null!;

    private Node3D _tank = null!;
    private MeshInstance3D _floorMi = null!, _padMi = null!;
    private readonly MeshInstance3D[] _walls = new MeshInstance3D[4];
    private Camera3D _cam = null!;
    private WebgpuWaterSolver? _solver;

    private bool _dragging, _dropActive;
    private Vector2 _dropCenter;
    private bool _autoDrip;
    private float _dripT, _fpsAccum;
    private readonly RandomNumberGenerator _rng = new();
    private Label? _readout;

    public override void _Ready()
    {
        _rng.Seed = 12345;
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
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new WebgpuWaterSolver(RenderingServer.GetRenderingDevice(), SimSize)
        {
            DropRadius = _rippleSize,
            Damping = PerStepDamping(),
        };
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

    // "amplitude x _dampPerSec every second" -> the per-step factor the kernel multiplies by.
    // Two update passes per tick, _simHz ticks per second.
    private float PerStepDamping() => Mathf.Pow(_dampPerSec, 1.0f / Mathf.Max(1.0f, 2.0f * _simHz));

    // ---- scene ----
    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 50.0f, Near = 0.05f, Far = 500.0f, Position = CamPos, Current = true };
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
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tank/tank_water.gdshader") };
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
        if (_dropActive) { dropCenter = _dropCenter; dropStrength = 0.01f; _dropActive = false; }
        else if (_autoDrip)
        {
            _dripT += dt;
            if (_dripT >= 0.35f) { _dripT = 0.0f; dropCenter = new Vector2(_rng.Randf() * 1.6f - 0.8f, _rng.Randf() * 1.6f - 0.8f); dropStrength = 0.02f; }
        }

        _waterTex.TextureRdRid = solver.DisplayRid;
        solver.DropRadius = _rippleSize;
        solver.Damping = PerStepDamping();

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
                _padPhase += 2.0 * Mathf.Pi * _padHz * h;
                _padCurvePhase += 2.0 * Mathf.Pi * _padSnakeHz * h;   // travelling face = snake mode
                _padMicroPhase += 2.0 * Mathf.Pi * _padMicroHz * h;
            }
            float pxNew = PaddleX(_padPhase);
            float cphNew = (float)_padCurvePhase;
            float mPhNew = (float)_padMicroPhase;
            // shoaling reads the SAME ramp the renderer draws; ref depth is the offshore knob
            float shoalRef = _shoal ? Mathf.Max(0.02f, _shoalRef) : 0.0f;
            float cAmp = _padUndulate ? _padCurveAmp : 0.0f;
            float cLobes = _padLobes;
            float cSegs = Mathf.Max(1.0f, Mathf.Round(_padSegCount));
            float mAmp = _padMicroOn ? _padMicroAmp : 0.0f;
            float mLobes = _padMicroLobes;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
                solver.Step(drop, ddx, ddz, dds, false, default, default, padOn, pxOld, pxNew, padW, padG,
                    cAmp, cLobes, cphOld, cphNew, cSegs,
                    mAmp, mLobes, mPhOld, mPhNew,
                    shoalRef, _floorBase, Slope, _waterLevel, _chopDamp)));
            dropPending = false;
            _simAcc -= h;
            _ticksLast++;
        }
        if (_ticksLast >= MaxTicksPerFrame) { _simAcc = 0.0; }   // drop the backlog, don't spiral
        UpdatePaddleMesh();

        _fpsAccum += dt;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · sim {SimSize}² @ {_simHz:0} Hz ({_ticksLast} ticks/frame)";
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
        var ui = new DemoUI(this, "23 · wave tank (C#) — piston wavemaker",
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
        ui.AddSlider("Paddle push gain", 0.1f, 4.0f, _padGain, v => _padGain = v);
        ui.AddSlider("Paddle reach (falloff)", 0.01f, 0.3f, _padWidth, v => _padWidth = v);
        // Curved face: 0 = straight piston. Lobes sets how many bulges across the tank; snake rate
        // travels the curve along z, which is what makes oblique / short-crested fronts.
        ui.AddToggle("Undulate face (curved edge)", _padUndulate, v => { _padUndulate = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro curve (amplitude)", 0.0f, 0.4f, _padCurveAmp, v => { _padCurveAmp = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro lobes (across z)", 0.5f, 8.0f, _padLobes, v => { _padLobes = v; UpdatePaddleMesh(); });
        ui.AddSlider("Macro snake rate (Hz)", 0.0f, 1.5f, _padSnakeHz, v => _padSnakeHz = v);
        ui.AddToggle("Micro segments (2nd jostler)", _padMicroOn, v => { _padMicroOn = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro amplitude", 0.0f, 0.12f, _padMicroAmp, v => { _padMicroAmp = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro lobes", 2.0f, 256.0f, _padMicroLobes, v => { _padMicroLobes = v; UpdatePaddleMesh(); });
        ui.AddSlider("Micro snake rate (Hz)", 0.0f, 4.0f, _padMicroHz, v => _padMicroHz = v);
        ui.AddSlider("Segments (micro step size)", 1.0f, MaxSimSegs, _padSegCount, v => { _padSegCount = v; UpdatePaddleMesh(); });
        ui.AddToggle("Segmented bank (mesh style)", _padSegmented, v => { _padSegmented = v; UpdatePaddleMesh(); });
        ui.AddToggle("Shoaling (speed + wave height)", _shoal, v => { _shoal = v; PushWaterKnobs(); });
        ui.AddSlider("Shoal ref depth (offshore)", 0.02f, 1.0f, _shoalRef, v => { _shoalRef = v; PushWaterKnobs(); });
        ui.AddSlider("Shoal gain max (H growth)", 1.0f, 6.0f, _shoalGainMax, v => { _shoalGainMax = v; PushWaterKnobs(); });
        ui.AddSlider("Breaking trigger (H/h)", 0.2f, 1.5f, _breakTrigger, v => _breakTrigger = v);
        ui.AddToggle("Agitate at break line", _agitOn, v => _agitOn = v);
        ui.AddToggle("Show agitation points", _agitViz, v => _agitViz = v);
        ui.AddSlider("Agitation strength", 0.0f, 0.02f, _agitStrength, v => _agitStrength = v);
        ui.AddSlider("Agitation points", 1.0f, MaxAgit, _agitCount, v => _agitCount = v);
        ui.AddSlider("Agitation scatter", 0.0f, 0.5f, _agitScatter, v => _agitScatter = v);
        ui.AddSlider("Scale (tank half-width)", 1.0f, 20.0f, _poolHalf, ApplyScale);
        ui.AddSlider("Ripple size (drop radius)", 0.003f, 0.06f, _rippleSize, v => _rippleSize = v);
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
        ui.AddSlider("Wave decay (amplitude/sec)", 0.2f, 1.0f, _dampPerSec, v => _dampPerSec = v);
        // k^2 loss: kills chop without touching swell. Raise this and RAISE wave decay together.
        ui.AddSlider("Chop damping (k²)", 0.0f, 0.5f, _chopDamp, v => _chopDamp = v);
        ui.AddToggle("Show candidate points", _vizPoints, v => { _vizPoints = v; PushWaterKnobs(); });
        ui.AddSlider("Point density", 10.0f, 200.0f, _vizDensity, v => { _vizDensity = v; PushWaterKnobs(); });
        ui.AddSlider("Point size", 0.05f, 0.6f, _vizSize, v => { _vizSize = v; PushWaterKnobs(); });
        ui.AddSlider("Crest level (yellow)", 0.0f, 0.15f, _crestLevel, v => { _crestLevel = v; PushWaterKnobs(); });
        ui.AddSlider("Crest front slope", 0.0f, 0.5f, _crestSlope, v => { _crestSlope = v; PushWaterKnobs(); });
        ui.AddSlider("Friction depth (orange)", 0.0f, 0.4f, _fricDepth, v => { _fricDepth = v; PushWaterKnobs(); });
        ui.AddSlider("Friction motion", 0.0f, 0.3f, _fricSlope, v => { _fricSlope = v; PushWaterKnobs(); });
        ui.AddSlider("Lead offset (pink ahead)", 0.0f, 0.4f, _leadOffset, v => { _leadOffset = v; PushWaterKnobs(); });
    }
}
