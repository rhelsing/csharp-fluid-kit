using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 22 — wave_tank_v2 (fork). Evan Wallace's WebGPU heightfield sim (water-kit scene 27)
// ported to C# (WebgpuWaterSolver), on a flat pool, rendered with our cheap water_lite. This is
// the authentic sim base for the raytraced (Moana / ww_*) render and the slant/paddle, next.
// LEFT-CLICK to drop ripples.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/22_wave_tank_v2.tscn 4 1600x900
public partial class WaveTankV2 : Node3D
{
    private const int N = 256;
    private const float Domain = 12.0f;
    private const float Half = Domain * 0.5f;
    private const string ShaderDir = "res://shaders/shorewaves/";

    private FreeCam _cam = null!;
    private MeshInstance3D _waterMi = null!;
    private ShaderMaterial _waterMat = null!;
    private Texture2Drd _texState = null!;

    private WebgpuWaterSolver? _solver;
    private bool _dropQueued;
    private Vector2 _dropXZ;
    private float _dropStrength = 0.03f;
    private float _fpsAccum;
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
        _solver = new WebgpuWaterSolver(RenderingServer.GetRenderingDevice(), N);
    }

    private void BuildEnvironment()
    {
        _cam = new FreeCam { Fov = 58.0f, Far = 4000.0f, Current = true, Speed = 6.0f };
        AddChild(_cam);
        _cam.LookAtFromPosition(new Vector3(Half, 8.0f, Domain + 4.0f), new Vector3(Half, 0.0f, Half), Vector3.Up);

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
        _waterMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "water_lite.gdshader") };
        _waterMat.SetShaderParameter("tx_state", _texState);
        _waterMat.SetShaderParameter("tx_bottom", FlatBed(-1.0f));
        _waterMat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _waterMat.SetShaderParameter("GRID_RES", (float)N);
        _waterMat.SetShaderParameter("highlight_mode", 0);
        _waterMat.SetShaderParameter("normal_strength", 3.5f);
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

    private static ImageTexture FlatBed(float bed)
    {
        var f = new float[16];
        for (int i = 0; i < f.Length; i++) { f[i] = bed; }
        var data = new byte[f.Length * sizeof(float)];
        System.Buffer.BlockCopy(f, 0, data, 0, data.Length);
        var img = Image.CreateFromData(4, 4, false, Image.Format.Rf, data);
        return ImageTexture.CreateFromImage(img);
    }

    public override void _Process(double delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }
        _texState.TextureRdRid = solver.DisplayRid;

        bool drop = _dropQueued;
        _dropQueued = false;
        float dcx = _dropXZ.X / Domain * 2.0f - 1.0f;
        float dcz = _dropXZ.Y / Domain * 2.0f - 1.0f;
        float ds = _dropStrength;
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.Step(drop, dcx, dcz, ds)));

        _fpsAccum += (float)delta;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {N}² Wallace webgpu sim · LMB drop";
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left })
        {
            Vector2 xz = MouseWorldXZ();
            if (xz.X >= 0.0f) { _dropXZ = xz; _dropQueued = true; }
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
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "22 · wave_tank_v2 — Wallace webgpu sim (C#)",
            "Evan Wallace's WebGPU heightfield sim (water-kit scene 27) ported to C# "
            + "(WebgpuWaterSolver) on a flat pool. LEFT-CLICK to drop ripples. Authentic sim base for "
            + "the raytraced (Moana) render + slant/paddle next. Free cam: RMB look · WASD · Q/E · Shift.");
        _readout = ui.AddReadout("— fps");
        ui.AddSlider("Drop strength (LMB)", 0.005f, 0.1f, 0.03f, v => _dropStrength = v);
        ui.AddSlider("Normal strength", 0.5f, 8.0f, 3.5f, v => _waterMat.SetShaderParameter("normal_strength", v));
    }
}
