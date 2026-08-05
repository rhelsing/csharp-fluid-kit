using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 17 — REACTIVE OCEAN. Scene 15's boat + ameye Gerstner water, plus the MNA
// reactive micro layer: a GPU Crank-Nicolson wave grid (ReactiveWaveField) the boat
// pokes as it drives, added on top of the Gerstner swell in the water shader so the hull
// carves REAL propagating wakes and impact ripples that the FFT/Gerstner macro physically
// can't do. "Macro = Gerstner (believable swell), micro = MNA (reactive detail)."
// The window camera-follows the boat (cell-snapped scroll), buoyancy reads the sum.
// Optional layers: scene 50's reactive foam (off — look revisit pending), the OceanMist
// living-fog box (∂h/∂t-coupled FluidSim3D + activity-lit particles), dipole poke
// (zero net volume — no stop-sink), and MNA substeps (oversampling).
// Later: toroidal-wrap window swap, cascades (docs/mna-next-steps.md + Ocean-* todos).
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/17_reactive_ocean.tscn 6 1600x1200
public partial class ReactiveOcean : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water_reactive.gdshader";
    private const string FloorShader = "res://shaders/caustics_floor.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";
    private const float BedDepth = -3.2f;

    // reactive window: 256² grid over an 80u square centred on the origin (Stage 1: fixed)
    private static readonly Vector2I RGrid = new(256, 256);
    private const float RWorldSize = 80.0f;
    private static readonly Vector2 ROrigin = new(-RWorldSize * 0.5f, -RWorldSize * 0.5f);

    private ReactiveHeightField _field = null!;
    private ReactiveWaveField _ripples = null!;
    private int _tick;
    private Texture2Drd _rippleTex = null!;
    private ShaderMaterial _mat = null!;
    private ShaderMaterial _floorMat = null!;
    private Godot.Environment _env = null!;
    private BoatRider _boat = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;

    private float _pokeStrength = 0.105f; // hull-down poke magnitude
    private float _pokeRadius = 1.105f;   // world units
    private float _rippleGain = 0.12f;    // sim height → world displacement
    private float _injectCutoff = 0.12f;  // below this speed-fraction → ZERO poke injection (belt+braces with the dipole)
    private bool _autoDrive = false;      // demo throttle (toggle on in the panel); off = you drive
    private bool _wakeFoam = true;        // reactive wake foam v1 (scene 50's raw-buffer look) — toggleable
    private float _wakeFoamIntensity = 1.0f;
    private Texture2Drd _foamTex = null!;

    // ── foam v3 layers (each independent, default off) ──
    private bool _foamTrail;              // foam stamped at the hull → tight speedboat trail
    private float _foamTrailAmount = 0.05f;
    private float _foamTrailRadius = 1.2f;
    private bool _foamDrift;              // foam advects along wave propagation
    private float _foamDriftGain = 8.0f;

    // ── bow spray: ballistic billboard spray at speed ──
    private const int NSpray = 600;
    private const float SprayLife = 1.1f;
    private MultiMesh _sprayMm = null!;
    private MultiMeshInstance3D _sprayMmi = null!;
    private QuadMesh _sprayQuad = null!;
    private bool _sprayOn;
    private float _sprayRate = 150f;      // particles/s at full speed
    private float _sprayKick = 2.5f;      // upward kick (m/s)
    private float _sprayAlpha = 0.5f;
    private float _sprayCarry;
    private int _sprayNext;
    private readonly Vector3[] _sprayPos = new Vector3[NSpray];
    private readonly Vector3[] _sprayVel = new Vector3[NSpray];
    private readonly float[] _sprayAge = new float[NSpray];
    private readonly System.Random _sprayRng = new(20260803);

    // ── living mist: thin FluidSim3D box riding the boat, coupled to ∂h/∂t ──
    private static readonly Vector3I MGrid = new(72, 18, 72);
    private const float MistBox = 40.0f, MistHeight = 10.0f, MistBaseY = -1.0f;
    private const int NMist = 7000;
    private const float MistMaxAge = 9.0f;
    private OceanMist _mist = null!;
    private Node3D _mistRig = null!;
    private MultiMesh _mistMm = null!;
    private bool _mistOn = false;   // living-fog layer — off for now (toggle in panel)
    private float _mistAlpha = 0.7f;
    private readonly Vector3[] _mistPos = new Vector3[NMist];
    private readonly float[] _mistAge = new float[NMist];
    private readonly System.Random _mistRng = new(20260802);
    private Vector3 _lastBoatPos;

    private Vector3 _camFocus;
    private bool _camFocusInit;
    private float _camFollowXz = 8.03f;
    private float _camSmoothY = 1.5285f;
    private float _camPosRate = 6f;

    public override void _Ready()
    {
        _ripples = new ReactiveWaveField(RGrid, RWorldSize, ROrigin)
        {
            Gain = _rippleGain,
            WaveSpeed = 0.44f, Dt = 0.2458f, Damping = 0.0f, Leak = 0.0015f, Cn = 0.21f, Iters = 40,
            FoamEnabled = true,    // wake foam v1 — panel toggle
            FoamThresh = 0.15f,    // dipole curvature runs much hotter than 50's gaussian — higher dead-zone
            FoamGain = 1.0f,
            DipolePoke = true,     // zero-net-volume poke — the stop-sink dies at the source
        };
        _field = new ReactiveHeightField(_ripples)
        {
            Steepness = 0.042f,
            Wavelength = 17.040f,
            Speed = 3.840f,
            Directions = new[] { 0.175f, 0.220f, 0.470f, 0.800f },
            // stable here because the wake is tame (low gain/poke, near-BE scheme); raise with care
            WakeInfluence = 1.75f,
        };

        _mist = new OceanMist(MGrid, MistBox, MistHeight, MistBaseY);

        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildBoat();
        BuildIslands();
        BuildMist();
        BuildSpray();
        BuildUi();

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            _ripples.Init(rd);
            _mist.Init(rd, _ripples.HeightRid, _ripples.PrevRid);
        }));
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_autoDrive) { Input.ActionPress("ui_up"); Input.ActionPress("ui_right"); }  // circle → stays in-window, curved wake

        // --- poke the reactive field from the boat, advance one tick ---
        if (_boat != null && _ripples != null)
        {
            Vector3 bp = _boat.GlobalPosition;
            var boatXz = new Vector2(bp.X, bp.Z);
            // poke scales with speed: ~0 at rest → _pokeStrength at full speed. So the hull
            // never sits in its own depression when slow (that's the wake-buoyancy sink), and
            // the wake only builds once you're moving fast enough to have outrun it.
            float speed01 = Mathf.Clamp(Mathf.Abs(_boat.CurrentSpeed) / Mathf.Max(_boat.MaxSpeed, 1e-3f), 0f, 1f);
            // below the cutoff → ZERO injection (no in-place pile-up when slow/stopping); above it,
            // ramp 0 → poke strength with speed.
            float above = Mathf.Max(0f, speed01 - _injectCutoff) / Mathf.Max(1e-3f, 1f - _injectCutoff);
            float strength = -_pokeStrength * above;   // hull displaces water downward
            float pr = _pokeRadius;
            _tick++;
            bool doRead = _tick % 3 == 0;   // throttle the buoyancy readback
            bool mistOn = _mistOn;
            bool mistRead = _tick % 2 == 0; // throttle the particle-velocity readback
            if (mistOn)
            {
                var boatDelta = boatXz - new Vector2(_lastBoatPos.X, _lastBoatPos.Z);
                _mist.Prepare(boatXz, boatDelta, _ripples.Origin, _ripples.WorldSize, _ripples.Grid.X);
                _mistRig.Position = new Vector3(bp.X, MistBaseY, bp.Z);
            }
            _lastBoatPos = bp;
            float trailAmt = _foamTrail ? _foamTrailAmount * speed01 : 0f;
            float trailRad = _foamTrailRadius;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
            {
                _ripples.SetCenter(boatXz);   // camera-follow: world-anchored scroll
                _ripples.PokeWorld(boatXz, pr, strength);
                if (trailAmt > 0f) { _ripples.DepositFoamWorld(boatXz, trailRad, trailAmt); }
                _ripples.Step();
                if (doRead) { _ripples.ReadBack(); }
                if (mistOn) { _mist.StepRt(mistRead); }
            }));
            _rippleTex.TextureRdRid = _ripples.HeightRid;
            _foamTex.TextureRdRid = _ripples.FoamRid;
            _mat.SetShaderParameter("ripple_origin", _ripples.Origin);   // window follows the boat
        }
    }

    public override void _Process(double delta)
    {
        // Wave time advances at RENDER rate (matches scene 50) — stepped per physics
        // tick it beats against any fps that isn't a clean multiple of 60. Physics
        // just samples the same continuous clock at tick time, so buoyancy stays true.
        _field.Time += (float)delta;
        _mat.SetShaderParameter("wave_time", _field.Time);
        UpdateFloorShadow();
        UpdateCamera((float)delta);
        UpdateMist((float)delta);
        UpdateSpray((float)delta);
    }

    public override void _ExitTree()
    {
        if (_rippleTex != null) { _rippleTex.TextureRdRid = default; }
        if (_foamTex != null) { _foamTex.TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _mist?.Free();      // first: its couple sets reference the solver's height textures
            _ripples?.Free();
        }));
    }

    private void UpdateFloorShadow()
    {
        if (_floorMat == null || _boat == null) { return; }
        _floorMat.SetShaderParameter("boat_pos", _boat.GlobalPosition);
        _floorMat.SetShaderParameter("boat_yaw", _boat.Yaw);
        _floorMat.SetShaderParameter("wave_time", _field.Time * _field.Speed);
        _floorMat.SetShaderParameter("steepness", _field.Steepness);
        _floorMat.SetShaderParameter("wl", _field.Wavelength);
        _floorMat.SetShaderParameter("wave_dirs", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
    }

    private void BuildEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42.0f, -120.0f, 0.0f),
            LightEnergy = 1.35f,
        };
        AddChild(_sun);

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.48f, 0.80f),
            SkyHorizonColor = new Color(0.74f, 0.82f, 0.88f),
            GroundBottomColor = new Color(0.30f, 0.34f, 0.36f),
        };
        _env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            // post effects: enable flags live on the panel (off = tuned look untouched);
            // the parameter values are pre-set so each toggle looks right immediately
            GlowIntensity = 0.6f,
            GlowHdrThreshold = 1.1f,
            FogDensity = 0.004f,
            FogAerialPerspective = 0.5f,
            FogLightColor = new Color(0.74f, 0.82f, 0.88f),   // matches the sky horizon
        };
        AddChild(new WorldEnvironment { Environment = _env });

        // tuned display prefs (Copy values): the DemoUI header knobs read these as initial
        var vp = GetViewport();
        vp.Scaling3DMode = Viewport.Scaling3DModeEnum.MetalfxSpatial;
        vp.Scaling3DScale = 0.387f;
        vp.Msaa3D = Viewport.Msaa.Msaa2X;
        vp.ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Fxaa;

        _cam = new Camera3D
        {
            Far = 3000.0f, Current = true, Position = new Vector3(0.0f, 3.5f, 8.0f),
            // we drive this camera at render rate in _Process — physics interpolation
            // would re-smooth it from stale physics snapshots (engine warns about it)
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
        };
        AddChild(_cam);
    }

    private void BuildSeabed()
    {
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(800, 800), SubdivideWidth = 32, SubdivideDepth = 32 },
            Position = new Vector3(0.0f, BedDepth, 0.0f),
            ExtraCullMargin = 80.0f,
        };
        _floorMat = new ShaderMaterial { Shader = GD.Load<Shader>(FloorShader) };
        _floorMat.SetShaderParameter("albedo", new Color(0.76f, 0.68f, 0.50f));
        _floorMat.SetShaderParameter("caustic_scale", 1.0f);
        _floorMat.SetShaderParameter("caustic_strength", 1.3f);
        _floorMat.SetShaderParameter("caustic_speed", 0.6f);
        _floorMat.SetShaderParameter("hull_half", new Vector2(1.0f, 2.6f));
        _floorMat.SetShaderParameter("sun_dir", -_sun.GlobalTransform.Basis.Z);
        _floorMat.SetShaderParameter("shadow_darkness", 0.75f);
        _floorMat.SetShaderParameter("shadow_softness", 0.4f);
        _floorMat.SetShaderParameter("shadow_size", 1.5f);
        _floorMat.SetShaderParameter("shadow_refracted", true);
        _floorMat.SetShaderParameter("shadow_refract_amount", 1.0f);
        mi.MaterialOverride = _floorMat;
        AddChild(mi);
    }

    private void BuildWater()
    {
        var mi = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(200, 200), SubdivideWidth = 400, SubdivideDepth = 400 },
            ExtraCullMargin = 40.0f,
        };
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShader) };

        _mat.SetShaderParameter("water_roughness", 0.035f);
        _mat.SetShaderParameter("foam_distance", 1.575f);
        _mat.SetShaderParameter("foam_crest", 0.900f);
        _mat.SetShaderParameter("normal_strength", 0.770f);
        _mat.SetShaderParameter("refraction_strength", 1.32f);
        _mat.SetShaderParameter("specular_smoothness", 0.485f);
        _mat.SetShaderParameter("depth_fade_distance", 11.0f);
        _mat.SetShaderParameter("water_color", new Color(0.09f, 0.52f, 0.62f));
        _mat.SetShaderParameter("shallow_color", new Color(0.46f, 0.82f, 0.80f));
        _mat.SetShaderParameter("steepness", _field.Steepness);
        _mat.SetShaderParameter("wavelength", _field.Wavelength);
        _mat.SetShaderParameter("wave_speed", _field.Speed);
        _mat.SetShaderParameter("directions", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
        _mat.SetShaderParameter("foam_tex", GD.Load<Texture2D>(FoamTex));
        _mat.SetShaderParameter("normal_tex", GD.Load<Texture2D>(NormalTex));
        _mat.SetShaderParameter("sun_direction", _sun.GlobalTransform.Basis.Z);
        _mat.SetShaderParameter("enable_depth_fade", true);
        _mat.SetShaderParameter("enable_shore_color", true);
        _mat.SetShaderParameter("enable_foam", false);  // off for now — clean water to read the wake
        _mat.SetShaderParameter("enable_normal_maps", true);
        _mat.SetShaderParameter("enable_refraction", true);
        _mat.SetShaderParameter("enable_lighting", false);

        // reactive layer
        _rippleTex = new Texture2Drd();
        _mat.SetShaderParameter("ripple_tex", _rippleTex);
        _mat.SetShaderParameter("ripple_origin", ROrigin);
        _mat.SetShaderParameter("ripple_size", RWorldSize);
        _mat.SetShaderParameter("ripple_gain", _rippleGain);
        _mat.SetShaderParameter("ripple_texel", 1.0f / RGrid.X);
        _mat.SetShaderParameter("ripple_edge_fade", 0.3753f);

        // reactive wake foam (scene 50's accumulate+decay buffer, optional overlay)
        _foamTex = new Texture2Drd();
        _mat.SetShaderParameter("wake_foam_tex", _foamTex);
        _mat.SetShaderParameter("wake_foam_intensity", _wakeFoam ? _wakeFoamIntensity : 0.0f);
        _mat.SetShaderParameter("wake_foam_v2", true);   // v2 default on (toggle off = exact v1)

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    private void BuildBoat()
    {
        // feel-solver values = Ryan's tuned Copy-values baked as defaults
        _boat = new BoatRider
        {
            Field = _field,
            MaxSpeed = 14.66f, MaxTurnSpeed = 2.005f, HullOffset = -0.099f,
            KBuoy = 42.9f, CLin = 0.75f, CSlam = 4.575f, KWave = 3.725f,
            Gravity = 8.1f, GravityAlways = true, Conform = 0.26f,
            LookaheadBase = 6.24f, AngStiffness = 40.5f, AngDamp = 1.35f, AngSlam = 0.5f,
            TurnBank = 0.416f,   // lean into turns
            Grip = 10.445f,      // course chases heading (lower = nose leads more)
            TrimAngle = 0.1802f, // bow-up at top speed (~10°)
            SpeedLift = 0.064f,  // planing rise at top speed
            TrimPivot = -2.5f,   // hinge at the transom: bow kicks up, stern planted
            TurnPivot = -2.45f,  // turns pivot at the stern: nose sweeps onto the heading
            // speed-feel rest ends seeded = the tuned (top-speed) values → lerp(x,x) = x,
            // byte-identical behaviour until an @rest slider is dialed away
            GripRest = 10.445f, ConformRest = 0.26f, KBuoyRest = 42.9f, CLinRest = 0.75f,
            AngStiffnessRest = 40.5f, AngDampRest = 1.35f, TurnPivotRest = -2.45f,
        };
        var hullMat = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.16f, 0.14f), Roughness = 0.65f };
        _boat.AddChild(new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 5.0f) }, MaterialOverride = hullMat });
        _boat.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 1.4f) }, MaterialOverride = hullMat,
            Position = new Vector3(0.0f, 0.0f, -2.9f), Scale = new Vector3(0.25f, 1.0f, 1.0f),
        });
        _boat.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.12f, 4.4f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.88f, 0.83f, 0.70f), Roughness = 0.8f },
            Position = new Vector3(0.0f, 0.40f, 0.2f),
        });
        _boat.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.6f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.92f, 0.94f, 0.96f), Roughness = 0.7f },
            Position = new Vector3(0.0f, 0.85f, 1.2f),
        });
        _boat.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = new Vector3(2.0f, 0.7f, 5.0f) } });
        AddChild(_boat);
        _boat.GlobalPosition = Vector3.Zero;
    }

    private void BuildIslands()
    {
        var sand = new StandardMaterial3D { AlbedoColor = new Color(0.80f, 0.72f, 0.52f), Roughness = 0.95f };
        float[][] specs =
        {
            new[] { 70.0f, -95.0f, 40.0f, 24.0f, 3.5f }, new[] { -90.0f, -120.0f, 34.0f, 20.0f, 2.5f },
            new[] { 60.0f, 140.0f, 50.0f, 26.0f, 4.0f }, new[] { -140.0f, 50.0f, 30.0f, 18.0f, 2.0f },
            new[] { 150.0f, 90.0f, 38.0f, 22.0f, 3.0f }, new[] { -55.0f, 120.0f, 28.0f, 16.0f, 2.0f },
        };
        foreach (var s in specs)
        {
            float px = s[0], pz = s[1], r = s[2], h = s[3], cap = s[4];
            float centerY = cap - h * 0.5f;
            AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = r, Height = h, RadialSegments = 24, Rings = 12 },
                MaterialOverride = sand, Position = new Vector3(px, centerY, pz),
            });
            float b = h * 0.5f;
            float ratio = Mathf.Clamp(Mathf.Abs(centerY) / b, 0.0f, 0.999f);
            float foot = r * Mathf.Sqrt(1.0f - ratio * ratio);
            var body = new StaticBody3D { Position = new Vector3(px, 0.0f, pz) };
            body.AddChild(new CollisionShape3D { Shape = new CylinderShape3D { Radius = foot, Height = 40.0f } });
            AddChild(body);
        }
    }

    // Grid-detail switch: rebuild the reactive field at a new density over the SAME 80u
    // window. All live-tuned values carry over; SpeedScale keeps the world wave speed
    // constant (tunables are in cell units, tuned at 256). Old GPU state is freed on the
    // render thread AFTER the mist unbinds its couple sets (they reference the solver's
    // height textures). A step queued around the swap no-ops on the not-yet-Ready field.
    // (Note: foam gain/threshold read cell-units curvature — at 512 the same wake has ~¼
    // the per-cell Laplacian, so foam needs retuning per density. Foam defaults off.)
    private void RebuildField(int n)
    {
        if (_ripples.Grid.X == n) { return; }
        var old = _ripples;
        var fresh = new ReactiveWaveField(new Vector2I(n, n), RWorldSize, old.Origin)
        {
            Gain = old.Gain, WaveSpeed = old.WaveSpeed, Dt = old.Dt, Damping = old.Damping,
            Leak = old.Leak, Cn = old.Cn, Iters = old.Iters, Substeps = old.Substeps,
            Follow = old.Follow, DipolePoke = old.DipolePoke,
            FoamEnabled = old.FoamEnabled, FoamDecay = old.FoamDecay,
            FoamGain = old.FoamGain, FoamThresh = old.FoamThresh, FoamAdvect = old.FoamAdvect,
            SpeedScale = n / (float)RGrid.X,
        };
        // detach display proxies from the old field's textures WHILE they're still alive —
        // the detach is an RS command queued ahead of the swap callable, so it processes
        // before old.Free(); otherwise the proxies release dead RIDs ("free invalid ID")
        _rippleTex.TextureRdRid = default;
        _foamTex.TextureRdRid = default;
        _ripples = fresh;
        _field.Ripples = fresh;
        _mat.SetShaderParameter("ripple_texel", 1.0f / n);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            var rd = RenderingServer.GetRenderingDevice();
            _mist.UnbindWater();
            old.Free();
            fresh.Init(rd);
            _mist.RebindWater(rd, fresh.HeightRid, fresh.PrevRid);
        }));
    }

    // Mist particles ride the fluid box (scene 12's MultiMesh pattern): box-local
    // billboards advected by the readback velocity — which carries the Galilean flow,
    // so wisps stay world-anchored while the rig follows the boat.
    private void BuildMist()
    {
        _mistRig = new Node3D { Position = new Vector3(0.0f, MistBaseY, 0.0f), Visible = _mistOn };
        AddChild(_mistRig);

        // alpha-Mix, not Add: additive wisps vanish over a sunlit sea (they only read
        // on dark stages like scene 12's floor)
        var quad = new QuadMesh { Size = new Vector2(2.4f, 2.4f) };
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            VertexColorUseAsAlbedo = true,
            DisableReceiveShadows = true,
            AlbedoTexture = MakeMistDotTexture(),
        };
        _mistMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = quad, InstanceCount = NMist,
        };
        for (int i = 0; i < NMist; i++)
        {
            _mistPos[i] = RespawnMist();
            _mistAge[i] = (float)_mistRng.NextDouble() * MistMaxAge;
        }
        _mistRig.AddChild(new MultiMeshInstance3D { Multimesh = _mistMm });
    }

    private static ImageTexture MakeMistDotTexture()
    {
        const int N = 32;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float dx = (x + 0.5f) / N - 0.5f, dy = (y + 0.5f) / N - 0.5f;
            // fat soft disc — a² falloff averages ~15% coverage and melts over bright water
            float a = Mathf.Clamp(1.0f - Mathf.Sqrt(dx * dx + dy * dy) * 1.9f, 0f, 1f);
            img.SetPixel(x, y, new Color(1, 1, 1, Mathf.Sqrt(a)));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private Vector3 RespawnMist()
    {
        float x = 2f + (float)_mistRng.NextDouble() * (MGrid.X - 4);
        float z = 2f + (float)_mistRng.NextDouble() * (MGrid.Z - 4);
        float y = 0.5f + (float)_mistRng.NextDouble() * (_mist.SlabCells + 1.0f);
        return new Vector3(x, y, z);
    }

    private Vector3 MistToLocal(Vector3 g) => new(
        g.X * _mist.CellXZ - MistBox * 0.5f, g.Y * _mist.CellY, g.Z * _mist.CellXZ - MistBox * 0.5f);

    private void UpdateMist(float delta)
    {
        if (!_mistOn || _mistMm == null) { return; }
        var vel = _mist.Vel;
        float adv = Mathf.Clamp(delta * 60f, 0f, 2f);
        for (int i = 0; i < NMist; i++)
        {
            Vector3 p = _mistPos[i];
            Vector3 v = _mist.SampleVel(vel, p);
            p += v * adv;
            _mistAge[i] += delta;
            bool oob = p.X < 1 || p.Y < 0 || p.Z < 1
                || p.X > MGrid.X - 2 || p.Y > MGrid.Y - 2 || p.Z > MGrid.Z - 2;
            if (oob || _mistAge[i] > MistMaxAge) { p = RespawnMist(); _mistAge[i] = 0f; }
            _mistPos[i] = p;

            float life = Mathf.Clamp(_mistAge[i] / MistMaxAge, 0f, 1f);
            // envelope: quick fade-in, hold, fade-out — NOT (1−life)·fadeIn·hug², whose
            // product collapses below the visibility knee of a bright sea
            float env = Mathf.Min(_mistAge[i] * 2.5f, 1f) * Mathf.Min((1f - life) * 3f, 1f);
            float hug = 1f - 0.5f * Mathf.Clamp(p.Y / MGrid.Y, 0f, 1f);   // mild thinning with height
            var near = new Color(0.97f, 0.98f, 1.0f);
            var far = new Color(0.85f, 0.89f, 0.95f);
            Color rgb = near.Lerp(far, life);
            // alpha rides local flow activity: wisps light up where the water churns
            // (wake, heave updrafts) and thin out in still air — the coupling made visible
            float activity = Mathf.Clamp(v.Length() * 4f, 0f, 1f);
            float a = _mistAlpha * env * hug * (0.15f + 0.85f * activity);
            var col = new Color(rgb.R, rgb.G, rgb.B, a);
            _mistMm.SetInstanceTransform(i, new Transform3D(Basis.Identity, MistToLocal(p)));
            _mistMm.SetInstanceColor(i, col);
        }
    }

    // Bow spray: ballistic white billboards thrown from the bow at speed — inherits the
    // boat's velocity, kicks up/outward, falls under gravity, dies at the waterline.
    private void BuildSpray()
    {
        _sprayQuad = new QuadMesh { Size = new Vector2(0.25f, 0.25f) };
        _sprayQuad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            VertexColorUseAsAlbedo = true,
            DisableReceiveShadows = true,
            AlbedoTexture = MakeMistDotTexture(),
        };
        _sprayMm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true, Mesh = _sprayQuad, InstanceCount = NSpray,
        };
        for (int i = 0; i < NSpray; i++)
        {
            _sprayAge[i] = SprayLife + 1f;   // born dead
            _sprayMm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(0, -50, 0)));
            _sprayMm.SetInstanceColor(i, new Color(1, 1, 1, 0));
        }
        _sprayMmi = new MultiMeshInstance3D { Multimesh = _sprayMm, Visible = _sprayOn };
        AddChild(_sprayMmi);
    }

    private void UpdateSpray(float delta)
    {
        if (!_sprayOn || _boat == null) { return; }
        float speed01 = Mathf.Clamp(Mathf.Abs(_boat.CurrentSpeed) / Mathf.Max(_boat.MaxSpeed, 1e-3f), 0f, 1f);

        if (speed01 > 0.35f)
        {
            _sprayCarry += _sprayRate * speed01 * delta;
            var xf = _boat.GlobalTransform;
            while (_sprayCarry >= 1f)
            {
                _sprayCarry -= 1f;
                int i = _sprayNext;
                _sprayNext = (_sprayNext + 1) % NSpray;
                float side = ((float)_sprayRng.NextDouble() < 0.5f ? -1f : 1f) * (0.4f + 0.6f * (float)_sprayRng.NextDouble());
                Vector3 spawn = xf.Origin + xf.Basis * new Vector3(side, 0.1f, -2.5f);
                Vector3 vel = _boat.Velocity * 0.85f
                    + xf.Basis.X * side * (0.4f + 1.0f * (float)_sprayRng.NextDouble()) * speed01 * 2.0f
                    + Vector3.Up * _sprayKick * speed01 * (0.6f + 0.4f * (float)_sprayRng.NextDouble());
                _sprayPos[i] = spawn;
                _sprayVel[i] = vel;
                _sprayAge[i] = 0f;
            }
        }

        for (int i = 0; i < NSpray; i++)
        {
            if (_sprayAge[i] > SprayLife) { continue; }
            _sprayAge[i] += delta;
            _sprayVel[i] += Vector3.Down * 9.0f * delta;
            _sprayPos[i] += _sprayVel[i] * delta;
            bool dead = _sprayAge[i] > SprayLife || _sprayPos[i].Y < -0.5f;
            if (dead)
            {
                _sprayAge[i] = SprayLife + 1f;
                _sprayMm.SetInstanceColor(i, new Color(1, 1, 1, 0));
                _sprayMm.SetInstanceTransform(i, new Transform3D(Basis.Identity, new Vector3(0, -50, 0)));
                continue;
            }
            float life = _sprayAge[i] / SprayLife;
            _sprayMm.SetInstanceTransform(i, new Transform3D(Basis.Identity, _sprayPos[i]));
            _sprayMm.SetInstanceColor(i, new Color(1f, 1f, 1f, _sprayAlpha * (1f - life)));
        }
    }

    private void UpdateCamera(float delta)
    {
        if (_boat == null) { return; }
        Vector3 bp = _boat.GlobalPosition;
        if (!_camFocusInit) { _camFocus = bp; _camFocusInit = true; }
        float kxz = 1.0f - Mathf.Exp(-_camFollowXz * delta);
        float ky = 1.0f - Mathf.Exp(-_camSmoothY * delta);
        _camFocus.X = Mathf.Lerp(_camFocus.X, bp.X, kxz);
        _camFocus.Z = Mathf.Lerp(_camFocus.Z, bp.Z, kxz);
        _camFocus.Y = Mathf.Lerp(_camFocus.Y, bp.Y, ky);
        Vector3 back = _boat.GlobalTransform.Basis.Z; back.Y = 0.0f; back = back.Normalized();
        Vector3 targetPos = _camFocus + back * 8.0f + Vector3.Up * 3.5f;
        float k = 1.0f - Mathf.Exp(-_camPosRate * delta);
        _cam.GlobalPosition = _cam.GlobalPosition.Lerp(targetPos, k);
        _cam.LookAt(_camFocus + Vector3.Up * 0.6f, Vector3.Up);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "17 · Reactive ocean (boat carves the swell)",
            "Arrow keys drive the boat. A GPU Crank-Nicolson MNA wave grid the hull POKES rides on top of "
            + "the Gerstner swell → real wakes the procedural ocean can't do. Full controls: MNA layer · "
            + "ameye water + stage toggles · boat feel-solver · camera · seabed.");

        // ── MNA reactive layer ──
        ui.AddToggle("Auto-drive (demo)", _autoDrive, on => { _autoDrive = on; if (!on) { Input.ActionRelease("ui_up"); Input.ActionRelease("ui_right"); } });
        ui.AddSlider("MNA · ripple gain (wake height)", 0.0f, 6.0f, _rippleGain, v => { _rippleGain = v; _mat.SetShaderParameter("ripple_gain", v); _ripples.Gain = v; });
        ui.AddSlider("MNA · poke strength (at full speed)", 0.0001f, 0.3f, _pokeStrength, v => _pokeStrength = v);
        ui.AddSlider("MNA · inject cutoff (speed frac, below = zero)", 0.0f, 0.6f, _injectCutoff, v => _injectCutoff = v);
        ui.AddToggle("MNA · dipole poke (zero net volume, no stop-sink)", _ripples.DipolePoke, on => _ripples.DipolePoke = on);
        ui.AddSlider("MNA · poke radius (m)", 1.0f, 8.0f, _pokeRadius, v => _pokeRadius = v);
        ui.AddSlider("MNA · wave speed", 0.2f, 5.0f, _ripples.WaveSpeed, v => _ripples.WaveSpeed = v);
        ui.AddSlider("MNA · dt", 0.05f, 1.5f, _ripples.Dt, v => _ripples.Dt = v);
        ui.AddSlider("MNA · damping", 0.0f, 0.5f, _ripples.Damping, v => _ripples.Damping = v);
        ui.AddSlider("MNA · leak", 0.0f, 0.1f, _ripples.Leak, v => _ripples.Leak = v);
        ui.AddSlider("MNA · scheme BE 0→1 CN", 0.0f, 1.0f, _ripples.Cn, v => _ripples.Cn = v);
        ui.AddSlider("MNA · sweeps / tick", 1, 40, _ripples.Iters, v => _ripples.Iters = (int)v);
        ui.AddSlider("MNA · substeps (oversampling)", 1, 8, _ripples.Substeps, v => _ripples.Substeps = (int)v);
        ui.AddOptions("MNA · grid detail (80u window)", new[] { "128² (fast)", "256² (tuned)", "512² (fine)" }, 1,
            i => RebuildField(i == 0 ? 128 : i == 1 ? 256 : 512));
        ui.AddToggle("MNA · camera-follow window", true, on => _ripples.Follow = on);
        ui.AddSlider("MNA · wake buoyancy (boat feels wake)", 0.0f, 2.0f, _field.WakeInfluence, v => _field.WakeInfluence = v);
        ui.AddSlider("MNA · edge fade / sponge", 0.02f, 0.4f, 0.3753f, v => _mat.SetShaderParameter("ripple_edge_fade", v));

        // ── Reactive wake foam (scene 50's layer — optional) ──
        ui.AddToggle("Foam · reactive wake foam", _wakeFoam, on =>
        {
            _wakeFoam = on;
            _ripples.FoamEnabled = on;
            _mat.SetShaderParameter("wake_foam_intensity", on ? _wakeFoamIntensity : 0.0f);
            if (!on) { RenderingServer.CallOnRenderThread(Callable.From(() => _ripples.ClearFoam())); }
        });
        ui.AddSlider("Foam · intensity", 0.0f, 2.0f, _wakeFoamIntensity, v =>
        {
            _wakeFoamIntensity = v;
            if (_wakeFoam) { _mat.SetShaderParameter("wake_foam_intensity", v); }
        });
        ui.AddSlider("Foam · gain (curvature)", 0.0f, 200.0f, _ripples.FoamGain, v => _ripples.FoamGain = v);
        ui.AddSlider("Foam · decay", 0.9f, 0.999f, _ripples.FoamDecay, v => _ripples.FoamDecay = v);
        ui.AddSlider("Foam · threshold", 0.0f, 0.3f, _ripples.FoamThresh, v => _ripples.FoamThresh = v);

        // ── Foam v2: v1 + toggleable extras (v2 off = exact v1) ──
        ui.AddToggle("Foam v2 · enable (v1 + extras)", true, on => _mat.SetShaderParameter("wake_foam_v2", on));
        ui.AddToggle("Foam v2 · matte (kill gloss)", true, on => _mat.SetShaderParameter("foam_matte", on));
        ui.AddSlider("Foam v2 · matte roughness", 0.3f, 1.0f, 0.9f, v => _mat.SetShaderParameter("foam_matte_rough", v));
        ui.AddToggle("Foam v2 · edge normals", true, on => _mat.SetShaderParameter("foam_edge_normals", on));
        ui.AddSlider("Foam v2 · normal strength", 0.0f, 8.0f, 3.0f, v => _mat.SetShaderParameter("foam_normal_strength", v));
        ui.AddToggle("Foam v2 · age bands (lace)", true, on => _mat.SetShaderParameter("foam_age_bands", on));
        ui.AddSlider("Foam v2 · band count", 1.0f, 8.0f, 3.0f, v => _mat.SetShaderParameter("foam_band_count", v));
        ui.AddSlider("Foam v2 · band softness", 0.05f, 1.0f, 0.35f, v => _mat.SetShaderParameter("foam_band_soft", v));
        ui.AddToggle("Foam v2 · churn (slope-driven)", true, on => _mat.SetShaderParameter("foam_churn", on));
        ui.AddSlider("Foam v2 · churn amount", 0.0f, 0.05f, 0.01f, v => _mat.SetShaderParameter("foam_churn_amount", v));
        ui.AddToggle("Foam v2 · micro normal", false, on => _mat.SetShaderParameter("foam_micro_normal", on));
        ui.AddSlider("Foam v2 · micro scale", 0.2f, 4.0f, 1.2f, v => _mat.SetShaderParameter("foam_micro_scale", v));
        ui.AddSlider("Foam v2 · micro strength", 0.0f, 2.0f, 0.5f, v => _mat.SetShaderParameter("foam_micro_strength", v));
        ui.AddToggle("Foam v2 · puff (vertex lift)", false, on => _mat.SetShaderParameter("foam_puff", on));
        ui.AddSlider("Foam v2 · puff amount (m)", 0.0f, 0.25f, 0.06f, v => _mat.SetShaderParameter("foam_puff_amount", v));

        // ── Foam v3 + macro layers (each independent, default off) ──
        ui.AddToggle("Foam v3 · trail deposit (hull track)", _foamTrail, on => _foamTrail = on);
        ui.AddSlider("Foam v3 · trail amount", 0.0f, 0.2f, _foamTrailAmount, v => _foamTrailAmount = v);
        ui.AddSlider("Foam v3 · trail radius (m)", 0.3f, 4.0f, _foamTrailRadius, v => _foamTrailRadius = v);
        ui.AddToggle("Foam v3 · drift (ride the waves)", _foamDrift, on =>
        {
            _foamDrift = on;
            _ripples.FoamAdvect = on ? _foamDriftGain : 0f;
        });
        ui.AddSlider("Foam v3 · drift gain", 0.0f, 30.0f, _foamDriftGain, v =>
        {
            _foamDriftGain = v;
            if (_foamDrift) { _ripples.FoamAdvect = v; }
        });
        ui.AddToggle("Whitecaps · Gerstner folding", false, on => _mat.SetShaderParameter("enable_whitecaps", on));
        ui.AddSlider("Whitecaps · fold start", 0.0f, 1.0f, 0.75f, v => _mat.SetShaderParameter("whitecap_start", v));
        ui.AddSlider("Whitecaps · feather", 0.01f, 0.5f, 0.15f, v => _mat.SetShaderParameter("whitecap_feather", v));
        ui.AddSlider("Whitecaps · intensity", 0.0f, 1.0f, 0.8f, v => _mat.SetShaderParameter("whitecap_intensity", v));
        ui.AddToggle("Shore band · beach foam", false, on => _mat.SetShaderParameter("enable_shore_band", on));
        ui.AddSlider("Shore band · intensity", 0.0f, 1.0f, 0.8f, v => _mat.SetShaderParameter("shore_band_intensity", v));
        ui.AddToggle("Spray · bow spray", _sprayOn, on =>
        {
            _sprayOn = on;
            if (_sprayMmi != null) { _sprayMmi.Visible = on; }
        });
        ui.AddSlider("Spray · rate (per s at speed)", 0.0f, 400.0f, _sprayRate, v => _sprayRate = v);
        ui.AddSlider("Spray · kick (m/s)", 0.0f, 6.0f, _sprayKick, v => _sprayKick = v);
        ui.AddSlider("Spray · size (m)", 0.05f, 0.8f, 0.25f, v => _sprayQuad.Size = new Vector2(v, v));
        ui.AddSlider("Spray · alpha", 0.0f, 1.0f, _sprayAlpha, v => _sprayAlpha = v);

        // ── Living mist (fog on the water — optional layer) ──
        ui.AddToggle("Mist · living fog", _mistOn, on =>
        {
            _mistOn = on;
            if (_mistRig != null) { _mistRig.Visible = on; }
        });
        ui.AddSlider("Mist · updraft (water heave → lift)", 0.0f, 40.0f, _mist.Updraft, v => _mist.Updraft = v);
        ui.AddSlider("Mist · source gain (wake → mist)", 0.0f, 30.0f, _mist.MistGain, v => _mist.MistGain = v);
        ui.AddSlider("Mist · source threshold", 0.0f, 0.03f, _mist.MistThresh, v => _mist.MistThresh = v);
        ui.AddSlider("Mist · ambient bed", 0.0f, 0.02f, _mist.Ambient, v => _mist.Ambient = v);
        ui.AddSlider("Mist · buoyancy", 0.0f, 4.0f, _mist.Buoyancy, v => _mist.Buoyancy = v);
        ui.AddSlider("Mist · fade", 0.9f, 0.999f, _mist.DyeFade, v => _mist.DyeFade = v);
        ui.AddSlider("Mist · particle alpha", 0.0f, 1.0f, _mistAlpha, v => _mistAlpha = v);
        ui.AddSlider("Mist · pressure iters", 4, 40, _mist.Iters, v => _mist.Iters = (int)v);

        // ── Water look (ameye) — stage toggles ──
        ui.AddToggle("2 · Depth fade", true, on => _mat.SetShaderParameter("enable_depth_fade", on));
        ui.AddToggle("3 · HSV shore color", true, on => _mat.SetShaderParameter("enable_shore_color", on));
        ui.AddToggle("4 · Foam", false, on => _mat.SetShaderParameter("enable_foam", on));
        ui.AddToggle("5 · Normal maps", true, on => _mat.SetShaderParameter("enable_normal_maps", on));
        ui.AddToggle("5 · Refraction", true, on => _mat.SetShaderParameter("enable_refraction", on));
        ui.AddToggle("6 · Toon lighting", false, on => _mat.SetShaderParameter("enable_lighting", on));

        // ── Post / environment (scene-level; viewport AA + upscaler are in the shared header) ──
        ui.AddToggle("Post · SSR (scene reflections on water)", false, on => _env.SsrEnabled = on);
        ui.AddSlider("Post · SSR max steps", 8, 256, 64, v => _env.SsrMaxSteps = (int)v);
        ui.AddToggle("Post · glow (sun glints)", false, on => _env.GlowEnabled = on);
        ui.AddSlider("Post · glow intensity", 0.0f, 2.0f, 0.6f, v => _env.GlowIntensity = v);
        ui.AddSlider("Post · glow threshold", 0.5f, 2.5f, 1.1f, v => _env.GlowHdrThreshold = v);
        ui.AddToggle("Post · distance fog (horizon haze)", false, on => _env.FogEnabled = on);
        ui.AddSlider("Post · fog density", 0.0f, 0.02f, 0.004f, v => _env.FogDensity = v);
        ui.AddSlider("Post · fog aerial perspective", 0.0f, 1.0f, 0.5f, v => _env.FogAerialPerspective = v);
        ui.AddToggle("Post · SSAO (contact shading)", false, on => _env.SsaoEnabled = on);
        ui.AddSlider("Post · SSAO intensity", 0.0f, 4.0f, 2.0f, v => _env.SsaoIntensity = v);
        ui.AddToggle("Post · SSIL (indirect light)", false, on => _env.SsilEnabled = on);
        ui.AddSlider("Post · SSIL intensity", 0.0f, 4.0f, 1.0f, v => _env.SsilIntensity = v);

        // ── Water tunables ──
        ui.AddSlider("Steepness", 0.0f, 0.4f, _field.Steepness, v => { _field.Steepness = v; SyncShaderWaves(); });
        ui.AddSlider("Wavelength", 6.0f, 30.0f, _field.Wavelength, v => { _field.Wavelength = v; SyncShaderWaves(); });
        ui.AddSlider("Wave speed", 0.0f, 4.0f, _field.Speed, v => { _field.Speed = v; SyncShaderWaves(); });
        ui.AddSlider("Roughness", 0.0f, 1.0f, 0.035f, v => _mat.SetShaderParameter("water_roughness", v));
        ui.AddSlider("Depth fade dist (clarity)", 3.0f, 20.0f, 11.0f, v => _mat.SetShaderParameter("depth_fade_distance", v));
        ui.AddSlider("Foam distance", 0.0f, 5.0f, 1.575f, v => _mat.SetShaderParameter("foam_distance", v));
        ui.AddSlider("Foam crest", 0.0f, 2.0f, 0.900f, v => _mat.SetShaderParameter("foam_crest", v));
        ui.AddSlider("Normal strength", 0.0f, 2.0f, 0.770f, v => _mat.SetShaderParameter("normal_strength", v));
        ui.AddSlider("Refraction", 0.0f, 4.0f, 1.32f, v => _mat.SetShaderParameter("refraction_strength", v));
        ui.AddSlider("Specular smooth", 0.0f, 1.0f, 0.485f, v => _mat.SetShaderParameter("specular_smoothness", v));
        ui.AddSlider("Dir 1", 0.0f, 1.0f, _field.Directions[0], v => { _field.Directions[0] = v; SyncShaderWaves(); });
        ui.AddSlider("Dir 2", 0.0f, 1.0f, _field.Directions[1], v => { _field.Directions[1] = v; SyncShaderWaves(); });
        ui.AddSlider("Dir 3", 0.0f, 1.0f, _field.Directions[2], v => { _field.Directions[2] = v; SyncShaderWaves(); });
        ui.AddSlider("Dir 4", 0.0f, 1.0f, _field.Directions[3], v => { _field.Directions[3] = v; SyncShaderWaves(); });

        // ── Boat feel solver ──
        ui.AddSlider("Max speed", 4.0f, 30.0f, _boat.MaxSpeed, v => _boat.MaxSpeed = v);
        ui.AddSlider("Max turn speed", 0.5f, 4.0f, _boat.MaxTurnSpeed, v => _boat.MaxTurnSpeed = v);
        ui.AddSlider("Turn bank (lean into turns; − = out)", -0.8f, 0.8f, _boat.TurnBank, v => _boat.TurnBank = v);
        ui.AddSlider("Grip (course chases heading; low = drifty)", 0.5f, 20.0f, _boat.Grip, v => _boat.Grip = v);
        ui.AddSlider("Trim · bow-up @ top speed (rad)", 0.0f, 0.35f, _boat.TrimAngle, v => _boat.TrimAngle = v);
        ui.AddSlider("Trim · lift @ top speed (m)", 0.0f, 0.8f, _boat.SpeedLift, v => _boat.SpeedLift = v);
        ui.AddSlider("Trim · hinge along hull (− stern, + bow)", -2.5f, 2.5f, _boat.TrimPivot, v => _boat.TrimPivot = v);
        ui.AddSlider("Turn pivot along hull (− stern, + bow)", -2.5f, 2.5f, _boat.TurnPivot, v => _boat.TurnPivot = v);
        ui.AddSlider("Hull offset (sit depth)", -0.4f, 1.0f, _boat.HullOffset, v => _boat.HullOffset = v);
        ui.AddSlider("Buoyancy k", 0.0f, 60.0f, _boat.KBuoy, v => _boat.KBuoy = v);
        ui.AddSlider("Linear damp", 0.0f, 30.0f, _boat.CLin, v => _boat.CLin = v);
        ui.AddSlider("Slam (quadratic)", 0.0f, 5.0f, _boat.CSlam, v => _boat.CSlam = v);
        ui.AddSlider("Launch kick", 0.0f, 5.0f, _boat.KWave, v => _boat.KWave = v);
        ui.AddSlider("Gravity", 0.0f, 20.0f, _boat.Gravity, v => _boat.Gravity = v);
        ui.AddToggle("Gravity always (else airborne)", _boat.GravityAlways, on => _boat.GravityAlways = on);
        ui.AddSlider("Conform (upright bias)", 0.0f, 1.0f, _boat.Conform, v => _boat.Conform = v);
        ui.AddSlider("Lookahead", 0.0f, 8.0f, _boat.LookaheadBase, v => _boat.LookaheadBase = v);
        ui.AddSlider("Ang stiffness", 0.0f, 100.0f, _boat.AngStiffness, v => _boat.AngStiffness = v);
        ui.AddSlider("Ang damp", 0.0f, 30.0f, _boat.AngDamp, v => _boat.AngDamp = v);
        ui.AddSlider("Ang slam", 0.0f, 5.0f, _boat.AngSlam, v => _boat.AngSlam = v);

        // ── Speed feel: each pair blends @rest → the existing slider's value (@ top
        // speed) by pow(speedRatio, exp). Rest = tuned values → identical until dialed. ──
        ui.AddToggle("Speed feel · curves on", _boat.SpeedCurves, on => _boat.SpeedCurves = on);
        ui.AddSlider("Speed feel · curve exp (high = late bite)", 0.25f, 4.0f, _boat.SpeedCurveExp, v => _boat.SpeedCurveExp = v);
        ui.AddSlider("@rest · Grip", 0.5f, 20.0f, _boat.GripRest, v => _boat.GripRest = v);
        ui.AddSlider("@rest · Conform (ride the swell)", 0.0f, 1.0f, _boat.ConformRest, v => _boat.ConformRest = v);
        ui.AddSlider("@rest · Buoyancy k", 0.0f, 60.0f, _boat.KBuoyRest, v => _boat.KBuoyRest = v);
        ui.AddSlider("@rest · Linear damp", 0.0f, 30.0f, _boat.CLinRest, v => _boat.CLinRest = v);
        ui.AddSlider("@rest · Ang stiffness", 0.0f, 100.0f, _boat.AngStiffnessRest, v => _boat.AngStiffnessRest = v);
        ui.AddSlider("@rest · Ang damp", 0.0f, 30.0f, _boat.AngDampRest, v => _boat.AngDampRest = v);
        ui.AddSlider("@rest · Turn pivot", -2.5f, 2.5f, _boat.TurnPivotRest, v => _boat.TurnPivotRest = v);

        // ── Camera ──
        ui.AddSlider("Cam vertical smooth (low = steadier)", 0.3f, 12.0f, _camSmoothY, v => _camSmoothY = v);
        ui.AddSlider("Cam follow XZ", 1.0f, 20.0f, _camFollowXz, v => _camFollowXz = v);

        // ── Seabed / hull shadow ──
        ui.AddSlider("Caustic strength", 0.0f, 2.0f, 1.3f, v => _floorMat.SetShaderParameter("caustic_strength", v));
        ui.AddSlider("Caustic scale", 0.1f, 2.0f, 1.0f, v => _floorMat.SetShaderParameter("caustic_scale", v));
        ui.AddSlider("Shadow darkness", 0.0f, 1.0f, 0.75f, v => _floorMat.SetShaderParameter("shadow_darkness", v));
        ui.AddSlider("Shadow softness", 0.0f, 1.0f, 0.4f, v => _floorMat.SetShaderParameter("shadow_softness", v));
        ui.AddSlider("Shadow size", 0.5f, 4.0f, 1.5f, v => _floorMat.SetShaderParameter("shadow_size", v));
        ui.AddToggle("Shadow refracted (ripple)", true, on => _floorMat.SetShaderParameter("shadow_refracted", on));
        ui.AddSlider("Shadow refract amount", 0.0f, 2.0f, 1.0f, v => _floorMat.SetShaderParameter("shadow_refract_amount", v));
    }

    private void SyncShaderWaves()
    {
        _mat.SetShaderParameter("steepness", _field.Steepness);
        _mat.SetShaderParameter("wavelength", _field.Wavelength);
        _mat.SetShaderParameter("wave_speed", _field.Speed);
        _mat.SetShaderParameter("directions", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
    }
}
