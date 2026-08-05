using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 20 — ripple_pool (fork). A cheap explicit LINEAR-wave core (LinearWaveSolver) on a
// flat pool: LEFT-CLICK to drop radiating ripples. No raytracing — rendered with water_lite.
// The "cheap interactive fluid core" isolation (concern C1 core + C5 interactive forcing).
// Compare the feel vs the KP07 scenes: a fraction of the cost, unconditionally interactive.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/20_ripple_pool.tscn 4 1600x900
public partial class RipplePool : Node3D
{
    private const int N = 320;
    private const float Dx = 0.04f;
    private const float Domain = N * Dx;   // 12.8 m
    private const float Half = Domain * 0.5f;
    private const string ShaderDir = "res://shaders/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _waterMat = null!;
    private Texture2Drd _texState = null!;
    private Texture2Drd _texBottom = null!;

    private LinearWaveSolver? _solver;
    private float _accum, _simTime, _fpsAccum;
    private bool _pokeQueued;
    private Vector2 _pokeXZ;
    private float _pokeStrength = 0.15f;
    private float _pokeRadius = 0.4f;
    private Label? _readout;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _solver = new LinearWaveSolver(rd, N, Dx, useBathy: false)
        {
            Dt = 0.006f, Damp = 0.0015f, ConstC2 = 9.81f,   // c^2 = g · 1 m depth
        };
        var bed = new float[N * N];
        for (int i = 0; i < bed.Length; i++) { bed[i] = -1.0f; }   // flat pool bed at -1 m (render only)
        var bytes = new byte[bed.Length * sizeof(float)];
        System.Buffer.BlockCopy(bed, 0, bytes, 0, bytes.Length);
        _solver.UploadBathy(bytes);
    }

    private void BuildEnvironment()
    {
        _cam = new FreeCam { Fov = 60.0f, Far = 4000.0f, Current = true, Speed = 6.0f };
        AddChild(_cam);
        _cam.LookAtFromPosition(new Vector3(Half, 7.0f, Domain + 3.0f), new Vector3(Half, 0.0f, Half), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-55.0f, 130.0f, 0.0f),
            LightColor = new Color(1.0f, 0.97f, 0.92f),
            LightEnergy = 1.3f,
            ShadowEnabled = true,
        });
        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.52f, 0.82f),
            SkyHorizonColor = new Color(0.72f, 0.82f, 0.90f),
            GroundBottomColor = new Color(0.40f, 0.42f, 0.44f),
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

        // pool tub for grounding (opaque water hides the floor; the rim frames it)
        AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(Domain + 0.6f, 1.2f, Domain + 0.6f) },
            Position = new Vector3(Half, -0.7f, Half),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.20f, 0.22f, 0.26f), Roughness = 0.9f },
        });
    }

    private void BuildWater()
    {
        _texState = new Texture2Drd();
        _texBottom = new Texture2Drd();
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water_lite.gdshader") };
        _waterMat.SetShaderParameter("tx_state", _texState);
        _waterMat.SetShaderParameter("tx_bottom", _texBottom);
        _waterMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _waterMat.SetShaderParameter("GRID_RES", (float)N);
        _waterMat.SetShaderParameter("highlight_mode", 0);       // no sim-foam field on this core
        _waterMat.SetShaderParameter("normal_strength", 3.2f);   // small ripples need punchier normals
        _waterMat.SetShaderParameter("depth_scale", 0.5f);

        _waterMi = new MeshInstance3D
        {
            Name = "Water",
            Mesh = new PlaneMesh { Size = new Vector2(Domain, Domain), SubdivideWidth = N - 2, SubdivideDepth = N - 2 },
            Position = new Vector3(Half, 0.0f, Half),
            ExtraCullMargin = 10.0f,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = _waterMat,
        };
        AddChild(_waterMi);
    }

    public override void _Process(double delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        _texBottom.TextureRdRid = solver.BathyRid;
        _texState.TextureRdRid = solver.HeightRid;

        _accum += Mathf.Min((float)delta, 0.05f);
        int steps = Mathf.Clamp((int)(_accum / solver.Dt), 0, 8);
        _accum -= steps * solver.Dt;
        if (steps > 0)
        {
            bool poke = _pokeQueued;
            _pokeQueued = false;
            Vector2 pk = _pokeXZ;
            float pr = _pokeRadius, ps = _pokeStrength;
            float t0 = _simTime;
            RenderingServer.CallOnRenderThread(Callable.From(() => solver.Step(steps, t0, poke, pk.X, pk.Y, pr, ps)));
            _simTime += steps * solver.Dt;
        }

        _fpsAccum += (float)delta;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {N}² linear-wave pool · LMB to poke";
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
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
        float t = -o.Y / d.Y;
        if (t < 0.0f) { return new Vector2(-1, -1); }
        Vector3 hit = o + d * t;
        if (hit.X < 0.0f || hit.X > Domain || hit.Z < 0.0f || hit.Z > Domain) { return new Vector2(-1, -1); }
        return new Vector2(hit.X, hit.Z);
    }

    public override void _ExitTree()
    {
        if (_texState != null) { _texState.TextureRdRid = default; }
        if (_texBottom != null) { _texBottom.TextureRdRid = default; }
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "20 · ripple_pool — linear-wave core (C#)",
            "A cheap explicit linear wave-equation core (LinearWaveSolver) on a flat pool — the "
            + "Hugo-Elias / Wallace ripple family, no raytracing. LEFT-CLICK to drop ripples. "
            + "Free cam: RMB look · WASD · Q/E · Shift.");
        _readout = ui.AddReadout("— fps");
        ui.AddSlider("Poke strength (LMB)", 0.02f, 0.5f, 0.15f, v => _pokeStrength = v);
        ui.AddSlider("Poke radius (m)", 0.1f, 1.5f, 0.4f, v => _pokeRadius = v);
        ui.AddSlider("Damping", 0.0f, 0.02f, 0.0015f, v => { if (_solver != null) { _solver.Damp = v; } });
        ui.AddSlider("Wave speed c²", 2.0f, 30.0f, 9.81f, v => { if (_solver != null) { _solver.ConstC2 = v; } });
        ui.AddSlider("Normal strength", 0.5f, 8.0f, 3.2f, v => _waterMat.SetShaderParameter("normal_strength", v));
    }
}
