using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 21 — wave_tank (fork). A scaled-up wave tank on the cheap LinearWaveSolver: a LINEAR
// RAMP bed (deep at x=0 → dry beach at x=Domain) gives shoaling via varying c=sqrt(g·depth),
// and an oscillating PADDLE at the deep end (edge wavemaker) pushes waves down the tank at a
// tunable speed. Rendered with water_lite for now — this scene is the base onto which the
// raytraced Moana (Wallis) render gets layered next. LEFT-CLICK to poke.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/21_wave_tank.tscn 10 1600x900
public partial class WaveTank : Node3D
{
    private const int N = 448;
    private const float Dx = 0.05f;
    private const float Domain = N * Dx;   // 22.4 m
    private const float Half = Domain * 0.5f;
    private const float Deep = -1.4f;      // bed at the paddle (deep) end
    private const float Shallow = 0.3f;    // bed at the beach (dry) end
    private const string ShaderDir = "res://shaders/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _waterMat = null!;
    private Texture2Drd _texState = null!;
    private Texture2Drd _texBottom = null!;

    private LinearWaveSolver? _solver;
    private byte[] _bedBytes = System.Array.Empty<byte>();
    private float _accum, _simTime, _fpsAccum;
    private bool _pokeQueued;
    private Vector2 _pokeXZ;
    private float _pokeStrength = 0.2f;
    private float _pokeRadius = 0.5f;
    private Label? _readout;

    public override void _Ready()
    {
        _bedBytes = BuildRampBed();
        BuildEnvironment();
        BuildTerrain();
        BuildWater();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private static byte[] BuildRampBed()
    {
        float slope = (Shallow - Deep) / Domain;
        var bed = new float[N * N];
        for (int j = 0; j < N; j++)
        {
            for (int i = 0; i < N; i++)
            {
                bed[j * N + i] = Deep + slope * ((i + 0.5f) * Dx);   // linear ramp in x
            }
        }
        var bytes = new byte[bed.Length * sizeof(float)];
        System.Buffer.BlockCopy(bed, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private void InitSolver()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _solver = new LinearWaveSolver(rd, N, Dx, useBathy: true)
        {
            Dt = 0.004f, Damp = 0.0012f, WaveAmp = 0.15f, WavePeriod = 3.0f, UseWavemaker = true,
        };
        _solver.UploadBathy(_bedBytes);
    }

    private void BuildEnvironment()
    {
        // overview from just off the deep end, looking down the tank toward the beach
        _cam = new FreeCam { Fov = 58.0f, Far = 4000.0f, Current = true, Speed = 8.0f };
        AddChild(_cam);
        _cam.LookAtFromPosition(new Vector3(-4.0f, 7.0f, Half), new Vector3(Domain, -0.5f, Half), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50.0f, 138.0f, 0.0f),
            LightColor = new Color(1.0f, 0.96f, 0.9f),
            LightEnergy = 1.35f,
            ShadowEnabled = true,
        });
        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.52f, 0.82f),
            SkyHorizonColor = new Color(0.74f, 0.83f, 0.90f),
            GroundBottomColor = new Color(0.42f, 0.44f, 0.46f),
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
        // exact ramp floor (the bed is planar → a low-res grid matches the analytic bed)
        var sandMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "sand_min.gdshader") };
        AddChild(new MeshInstance3D
        {
            Name = "Floor",
            Mesh = BuildRampMesh(48),
            Position = Vector3.Zero,
            MaterialOverride = sandMat,
        });
    }

    private static ArrayMesh BuildRampMesh(int g)
    {
        float slope = (Shallow - Deep) / Domain;
        Vector3 nrm = new Vector3(-slope, 1.0f, 0.0f).Normalized();
        var verts = new Vector3[(g + 1) * (g + 1)];
        var normals = new Vector3[(g + 1) * (g + 1)];
        var uvs = new Vector2[(g + 1) * (g + 1)];
        for (int j = 0; j <= g; j++)
        {
            for (int i = 0; i <= g; i++)
            {
                float x = i / (float)g * Domain;
                float z = j / (float)g * Domain;
                int k = j * (g + 1) + i;
                verts[k] = new Vector3(x, Deep + slope * x, z);
                normals[k] = nrm;
                uvs[k] = new Vector2(x / Domain, z / Domain);
            }
        }
        var idx = new int[g * g * 6];
        int p = 0;
        for (int j = 0; j < g; j++)
        {
            for (int i = 0; i < g; i++)
            {
                int a = j * (g + 1) + i, b = a + 1, c = a + (g + 1), d = c + 1;
                idx[p] = a; idx[p + 1] = b; idx[p + 2] = c;
                idx[p + 3] = b; idx[p + 4] = d; idx[p + 5] = c;
                p += 6;
            }
        }
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts;
        arr[(int)Mesh.ArrayType.Normal] = normals;
        arr[(int)Mesh.ArrayType.TexUV] = uvs;
        arr[(int)Mesh.ArrayType.Index] = idx;
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return m;
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
        _waterMat.SetShaderParameter("normal_strength", 2.6f);
        _waterMat.SetShaderParameter("depth_scale", 0.7f);

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
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {N}² wave tank · paddle at deep end · LMB poke";
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
        var ui = new DemoUI(this, "21 · wave_tank — slanted tank + paddle (C#)",
            "Scaled-up wave tank on the cheap LinearWaveSolver: a linear-ramp bed (deep → dry beach) "
            + "shoals the waves, and a paddle at the deep end pushes them down the tank. 'Paddle speed' "
            + "(period) changes the wave rate; the slant makes them travel at depth-dependent speeds. "
            + "Base for the raytraced Moana render (next). LEFT-CLICK to poke. Free cam: RMB · WASD.");
        _readout = ui.AddReadout("— fps");
        ui.AddToggle("Paddle (wavemaker)", true, v => { if (_solver != null) { _solver.UseWavemaker = v; } });
        ui.AddSlider("Paddle amplitude", 0.0f, 0.4f, 0.15f, v => { if (_solver != null) { _solver.WaveAmp = v; } });
        ui.AddSlider("Paddle speed (period s)", 1.0f, 8.0f, 3.0f, v => { if (_solver != null) { _solver.WavePeriod = v; } });
        ui.AddSlider("Damping", 0.0f, 0.02f, 0.0012f, v => { if (_solver != null) { _solver.Damp = v; } });
        ui.AddSlider("Poke strength (LMB)", 0.02f, 0.6f, 0.2f, v => _pokeStrength = v);
        ui.AddSlider("Poke radius (m)", 0.1f, 1.5f, 0.5f, v => _pokeRadius = v);
        ui.AddSlider("Normal strength", 0.5f, 6.0f, 2.6f, v => _waterMat.SetShaderParameter("normal_strength", v));
    }
}
