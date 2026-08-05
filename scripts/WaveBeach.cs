using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 19 — wave_beach (fork). The cheap explicit LINEAR-wave core (LinearWaveSolver) on the
// SAME sloped beach as scene 17, with spatially-varying c = sqrt(g·depth) from the bathymetry
// → shoaling (waves slow, shorten, pile toward the shore) from a linear, stable, cheap solver
// + a west wavemaker. The "does a cheap linear core + shoaling recover the beach look?"
// experiment (concerns C1 core + C2 shoaling + C5 forcing). Compare vs scene 17 (KP07).
// Honest: linear ⇒ no real breaking/curl; that's the expected limitation. LEFT-CLICK to poke.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/19_wave_beach.tscn 10 1600x900
public partial class WaveBeach : Node3D
{
    private const int N = 608;
    private const float Dx = 0.05f;
    private const float Domain = N * Dx;   // 30.4 m
    private const float Half = Domain * 0.5f;
    private const string ShaderDir = "res://shaders/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _waterMat = null!;
    private Texture2Drd _texState = null!;
    private Texture2Drd _texBottom = null!;

    private LinearWaveSolver? _solver;
    private float[] _corners = System.Array.Empty<float>();
    private byte[] _bedBytes = System.Array.Empty<byte>();
    private float _accum, _simTime, _fpsAccum;
    private bool _pokeQueued;
    private Vector2 _pokeXZ;
    private float _pokeStrength = 0.2f;
    private float _pokeRadius = 0.5f;
    private Label? _readout;

    public override void _Ready()
    {
        _corners = BeachScenario.BuildCorners();
        float[] bottomFloats = BeachScenario.BuildBottomFloats(_corners);
        var bedR = new float[N * N];
        for (int i = 0; i < N * N; i++) { bedR[i] = bottomFloats[i * 4]; }   // r channel = cell-centre bed
        _bedBytes = BeachScenario.FloatsToBytes(bedR);

        BuildEnvironment();
        BuildTerrain();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _solver = new LinearWaveSolver(rd, N, Dx, useBathy: true)
        {
            Dt = 0.004f, Damp = 0.0012f, WaveAmp = 0.12f, WavePeriod = 4.0f, UseWavemaker = true,
        };
        _solver.UploadBathy(_bedBytes);
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
        // plain (dry) sand — this core has no ground-memory pass, so tx_ground is unbound
        var sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        AddChild(new MeshInstance3D
        {
            Name = "Terrain",
            Mesh = BeachScenario.BuildTerrainMesh(_corners),
            Position = Vector3.Zero,
            MaterialOverride = sandMat,
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
        _waterMat.SetShaderParameter("highlight_mode", 0);
        _waterMat.SetShaderParameter("normal_strength", 2.2f);

        _waterMi = new MeshInstance3D
        {
            Name = "Water",
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
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        _texBottom.TextureRdRid = solver.BathyRid;
        _texState.TextureRdRid = solver.HeightRid;

        _accum += Mathf.Min((float)delta, 0.05f);
        int steps = Mathf.Clamp((int)(_accum / solver.Dt), 0, 10);
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
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {N}² linear-wave beach (shoaling) · LMB poke";
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
        var ui = new DemoUI(this, "19 · wave_beach — linear core + shoaling (C#)",
            "The cheap explicit linear wave core (LinearWaveSolver) on the SAME sloped beach as scene "
            + "17, with varying c = sqrt(g·depth) → shoaling, driven by a west wavemaker. Tests whether a "
            + "cheap linear core recovers the beach feel. (Linear ⇒ no real breaking/curl — expected.) "
            + "LEFT-CLICK to poke. Free cam: RMB look · WASD · Q/E · Shift.");
        _readout = ui.AddReadout("— fps");
        ui.AddSlider("Wave amplitude", 0.0f, 0.3f, 0.12f, v => { if (_solver != null) { _solver.WaveAmp = v; } });
        ui.AddSlider("Wave period", 2.0f, 10.0f, 4.0f, v => { if (_solver != null) { _solver.WavePeriod = v; } });
        ui.AddToggle("Wavemaker", true, v => { if (_solver != null) { _solver.UseWavemaker = v; } });
        ui.AddSlider("Damping", 0.0f, 0.02f, 0.0012f, v => { if (_solver != null) { _solver.Damp = v; } });
        ui.AddSlider("Poke strength (LMB)", 0.02f, 0.6f, 0.2f, v => _pokeStrength = v);
        ui.AddSlider("Poke radius (m)", 0.1f, 1.5f, 0.5f, v => _pokeRadius = v);
        ui.AddSlider("Normal strength", 0.5f, 6.0f, 2.2f, v => _waterMat.SetShaderParameter("normal_strength", v));
    }
}
