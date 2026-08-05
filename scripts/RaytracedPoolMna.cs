using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24 — raytraced_pool_mna. The SAME raytraced pool render as scene 23, but driven by the
// MNA implicit stamp solver (MnaWaterSolver) instead of Wallace's sim — the pluggable-substrate
// proof (water-kit scene 26). A normal-pack kernel bridges the MNA height into the ww_ shaders'
// water_tex layout. Auto-pokes; LEFT-CLICK to poke. "Milk" physics, photoreal pool.
public partial class RaytracedPoolMna : Node3D
{
    private const int Grid = 256;
    private static readonly Vector2I CausticSize = new(1024, 1024);
    private const string ShDir = "res://shaders/webgpu_water/";
    private const string TexDir = "res://textures/webgpu_water/";

    private static readonly Vector3 SphereCenter = new(-0.4f, -0.75f, 0.2f);
    private const float SphereRadius = 0.25f;
    private static readonly Vector3 LightDir = new(2.0f, 2.0f, -1.0f);
    private const int WaterDetail = 200;

    private Texture2D _tileTex = null!;
    private Cubemap _skyTex = null!;
    private Texture2Drd _waterTex = null!;
    private ViewportTexture _causticTex = null!;
    private ShaderMaterial _poolMat = null!, _sphereMat = null!, _surfaceMat = null!, _causticMat = null!;
    private readonly System.Collections.Generic.List<ShaderMaterial> _sharedMats = new();
    private float _causticIntensity = 0.35f, _ior = 1.333f, _fresnelMin = 0.25f;

    private Camera3D _cam = null!;
    private MnaWaterSolver? _solver;

    // MNA tunables
    private float _waveSpeed = 2.0f, _dt = 0.4f, _damping = 0.16f;
    private int _iters = 30;
    private float _pokeRadius = 7.0f, _pokeStrength = 0.1f;
    private bool _auto = true;
    private float _pokeT;
    private int _pokeI;
    private static readonly Vector2[] Pokes = { new(0.4f, 0.4f), new(0.62f, 0.6f), new(0.5f, 0.5f), new(0.36f, 0.66f) };
    private Label? _readout;
    private float _fpsAccum;

    public override void _Ready()
    {
        _tileTex = GD.Load<Texture2D>(TexDir + "tiles.jpg");
        _skyTex = BuildCubemap();
        _waterTex = new Texture2Drd();
        BuildEnvironment();
        BuildCaustics();
        BuildPool();
        BuildSphere();
        BuildSurface();
        PushSharedUniforms();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new MnaWaterSolver(RenderingServer.GetRenderingDevice(), Grid);
    }

    private static Image LoadImg(string path)
    {
        var img = GD.Load<Texture2D>(path).GetImage();
        if (img.IsCompressed()) { img.Decompress(); }
        img.Convert(Image.Format.Rgba8);
        return img;
    }

    private Cubemap BuildCubemap()
    {
        var imgs = new Godot.Collections.Array<Image>
        {
            LoadImg(TexDir + "xpos.jpg"), LoadImg(TexDir + "xneg.jpg"),
            LoadImg(TexDir + "ypos.jpg"), LoadImg(TexDir + "yneg.jpg"),
            LoadImg(TexDir + "zpos.jpg"), LoadImg(TexDir + "zneg.jpg"),
        };
        var cm = new Cubemap();
        cm.CreateFromImages(imgs);
        return cm;
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 45.0f, Near = 0.02f, Far = 100.0f, Position = new Vector3(1.27f, 1.19f, -3.40f), Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0.0f, -0.5f, 0.0f), Vector3.Up);
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.55f, 0.62f, 0.72f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.6f, 0.65f, 0.7f),
            AmbientLightEnergy = 1.0f,
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private MeshInstance3D MakeWaterPlane() => new()
    {
        Mesh = new PlaneMesh { Size = new Vector2(2.0f, 2.0f), SubdivideWidth = WaterDetail, SubdivideDepth = WaterDetail },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100)),
    };

    private void BuildCaustics()
    {
        var vp = new SubViewport
        {
            Size = CausticSize, OwnWorld3D = true, TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(vp);
        var cam = new Camera3D { Position = new Vector3(0, 5, 0), Projection = Camera3D.ProjectionType.Orthogonal, Size = 4.0f, Far = 100.0f, Current = true };
        vp.AddChild(cam);
        cam.LookAt(Vector3.Zero, Vector3.Forward);
        var mi = MakeWaterPlane();
        _causticMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_caustics.gdshader") };
        _causticMat.SetShaderParameter("water_tex", _waterTex);
        mi.MaterialOverride = _causticMat;
        vp.AddChild(mi);
        _sharedMats.Add(_causticMat);
        _causticTex = vp.GetTexture();
    }

    private void BuildPool()
    {
        _poolMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_pool.gdshader") };
        _poolMat.SetShaderParameter("tile_tex", _tileTex);
        _poolMat.SetShaderParameter("water_tex", _waterTex);
        _poolMat.SetShaderParameter("caustic_tex", _causticTex);
        _sharedMats.Add(_poolMat);
        AddChild(new MeshInstance3D { Mesh = BuildPoolMesh(), MaterialOverride = _poolMat, CustomAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)) });
    }

    private static ArrayMesh BuildPoolMesh()
    {
        Vector3 Octant(int i) => new((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1);
        int[][] faces = { new[] { 0, 4, 2, 6 }, new[] { 1, 3, 5, 7 }, new[] { 2, 6, 3, 7 }, new[] { 0, 2, 1, 3 }, new[] { 4, 5, 6, 7 } };
        var verts = new System.Collections.Generic.List<Vector3>();
        foreach (var f in faces)
        {
            Vector3 v0 = Octant(f[0]), v1 = Octant(f[1]), v2 = Octant(f[2]), v3 = Octant(f[3]);
            verts.AddRange(new[] { v0, v1, v2, v2, v1, v3 });
        }
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return m;
    }

    private void BuildSphere()
    {
        _sphereMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_sphere.gdshader") };
        _sphereMat.SetShaderParameter("water_tex", _waterTex);
        _sphereMat.SetShaderParameter("caustic_tex", _causticTex);
        _sharedMats.Add(_sphereMat);
        AddChild(new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f, RadialSegments = 32, Rings = 24 },
            MaterialOverride = _sphereMat,
            Transform = new Transform3D(Basis.FromScale(Vector3.One * SphereRadius), SphereCenter),
        });
    }

    private void BuildSurface()
    {
        _surfaceMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_surface.gdshader") };
        _surfaceMat.SetShaderParameter("tile_tex", _tileTex);
        _surfaceMat.SetShaderParameter("water_tex", _waterTex);
        _surfaceMat.SetShaderParameter("caustic_tex", _causticTex);
        _surfaceMat.SetShaderParameter("sky_tex", _skyTex);
        _sharedMats.Add(_surfaceMat);
        var mi = MakeWaterPlane();
        mi.MaterialOverride = _surfaceMat;
        AddChild(mi);
    }

    private void PushSharedUniforms()
    {
        var ld = LightDir.Normalized();
        foreach (var m in _sharedMats)
        {
            m.SetShaderParameter("light_direction", ld);
            m.SetShaderParameter("sphere_center", SphereCenter);
            m.SetShaderParameter("sphere_radius", SphereRadius);
            m.SetShaderParameter("rim_shadow", 1.0f);
            m.SetShaderParameter("sphere_shadow_on", 1.0f);
        }
        _causticMat.SetShaderParameter("caustic_intensity", _causticIntensity);
        _surfaceMat.SetShaderParameter("ior", _ior);
        _surfaceMat.SetShaderParameter("fresnel_min", _fresnelMin);
    }

    public override void _PhysicsProcess(double delta)
    {
        var solver = _solver;
        if (solver == null || !solver.Ready) { return; }

        Vector4 poke = Vector4.Zero;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            Vector2 uv = MouseUv();
            if (uv.X >= 0.0f) { poke = new Vector4(uv.X * Grid, uv.Y * Grid, _pokeRadius, -_pokeStrength); }
        }
        else if (_auto)
        {
            _pokeT += (float)delta;
            if (_pokeT >= 0.5f)
            {
                _pokeT = 0.0f;
                var uv = Pokes[_pokeI];
                _pokeI = (_pokeI + 1) % Pokes.Length;
                poke = new Vector4(uv.X * Grid, uv.Y * Grid, _pokeRadius, -_pokeStrength);
            }
        }

        _waterTex.TextureRdRid = solver.PackedRid;
        float beta = _dt * _dt * _waveSpeed * _waveSpeed;
        float a = _damping * _dt * 0.5f;
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.Step(poke, beta, a, iters)));

        _fpsAccum += (float)delta;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · MNA → raytraced pool (C#)";
        }
    }

    private Vector2 MouseUv()
    {
        Vector2 mp = GetViewport().GetMousePosition();
        var plane = new Plane(Vector3.Up, 0.0f);
        if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is not Vector3 hit) { return new Vector2(-1, -1); }
        var uv = new Vector2(hit.X * 0.5f + 0.5f, hit.Z * 0.5f + 0.5f);
        if (uv.X < 0.0f || uv.X > 1.0f || uv.Y < 0.0f || uv.Y > 1.0f) { return new Vector2(-1, -1); }
        return uv;
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
        var ui = new DemoUI(this, "24 · MNA → raytraced pool (C#)",
            "Pluggable substrate: the SAME raytraced pool as scene 23, but driven by the MNA implicit "
            + "stamp solver instead of Wallace's sim (a normal-pack kernel bridges them). Auto-pokes; "
            + "LEFT-CLICK to poke. 'Milk' physics, photoreal pool.");
        _readout = ui.AddReadout("— fps");
        ui.AddToggle("Auto-poke", _auto, v => _auto = v);
        ui.AddSlider("Wave speed (c)", 0.2f, 4.0f, _waveSpeed, v => _waveSpeed = v);
        ui.AddSlider("Sim dt (stable at ANY)", 0.05f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Damping", 0.0f, 2.0f, _damping, v => _damping = v);
        ui.AddSlider("Jacobi sweeps / tick", 1, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Poke strength", 0.1f, 4.0f, _pokeStrength, v => _pokeStrength = v);
        ui.AddSlider("Normal (ripple) scale", 1.0f, 120.0f, 16.0f, v => { if (_solver != null) { _solver.NormalScale = v; } });
        ui.AddSlider("Caustics", 0.0f, 1.0f, _causticIntensity, v => _causticMat.SetShaderParameter("caustic_intensity", v));
    }
}
