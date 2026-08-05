using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// ExpPoolScene — the FROZEN BASELINE for the DSP experiment series, shared by reference.
//
// Isolating one variable per scene only means something if everything else is provably
// identical, so the baseline is not copied into each scene — it lives here once and every
// experiment scene inherits it. A subclass adds exactly one thing and nothing else.
//
// Baseline (from scene 24, docs/dsp-experiments.md):
//   * scene 24's camera, cubemap, tiles, ball, pool
//   * grid 256^2, ExpWaterSolver (full sim — NO MNA stamp anywhere in this series)
//   * same drive: amplitude, interval, source position
//   * RAYTRACE OFF by default. The caustics SubViewport is a second 1024^2 render every frame
//     and the ww_* shaders re-trace per pixel; that cost lands on the fps being judged and
//     visually confounds what is being looked at. Kept as a toggle, not deleted.
//   * standard readout: `fps · <state> · <cost>`
//
// Two traps this class already handles, both from scene 24 being built for a raytracer:
//   1. BuildRtPoolMesh() is NOT a pool — its five faces are x=+/-1, z=+/-1 and y=+1, i.e. a LID
//      with no floor. It was a proxy volume for ww_pool.gdshader to raymarch and cannot be
//      rasterized. BuildPlainPoolMesh() is the real one used when raytracing is off.
//   2. water_tex is R=height, G=velocity, B=normal.x, A=normal.z. Anything writing the field
//      must write normals too or the surface renders as a featureless blob.
//
// Subclass hooks: AddVariableControls() for the ONE control, and OnConfigure()/OnStep().
public abstract partial class ExpPoolScene : Node3D
{
    protected const int Grid = 256;
    protected const string ShDir = "res://shaders/webgpu_water/";
    protected const string TexDir = "res://textures/webgpu_water/";
    protected const int WaterDetail = 200;

    protected static readonly Vector3 SphereCenter = new(-0.4f, -0.75f, 0.2f);
    protected const float SphereRadius = 0.25f;
    protected static readonly Vector3 LightDir = new(2.0f, 2.0f, -1.0f);
    private static readonly Vector2I CausticSize = new(1024, 1024);

    protected ExpWaterSolver? Solver;
    protected Camera3D Cam = null!;
    private Texture2D _tileTex = null!;
    private Cubemap _skyTex = null!;
    private Texture2Drd _waterTex = null!;
    private ViewportTexture _causticTex = null!;
    private SubViewport _causticVp = null!;
    private ShaderMaterial _poolMat = null!, _sphereMat = null!, _surfaceMat = null!, _causticMat = null!;
    private readonly System.Collections.Generic.List<ShaderMaterial> _sharedMats = new();
    private Material _poolPlain = null!, _spherePlain = null!, _surfacePlain = null!;
    private ArrayMesh _rtPoolMesh = null!, _plainPoolMesh = null!;
    private MeshInstance3D _poolMi = null!, _sphereMi = null!, _surfaceMi = null!;
    private DirectionalLight3D _plainSun = null!;
    private ShaderMaterial _plainWaterMat = null!;
    private bool _raytrace;

    private float _causticIntensity = 0.35f;
    private Label? _readout;
    private float _fpsAccum;

    // ---- the frozen drive ----
    protected float DriveStrength = 0.06f;
    protected float DriveInterval = 1.2f;
    private float _driveT;
    protected bool AutoDrive = true;

    /// <summary>Title and blurb for the panel.</summary>
    protected abstract (string Title, string Hint) SceneInfo { get; }

    /// <summary>The ONE control this scene adds. Everything else is inherited.</summary>
    protected virtual void AddVariableControls(DemoUI ui) { }

    /// <summary>Called once the solver exists, on the render thread.</summary>
    protected virtual void OnSolverReady() { }

    /// <summary>Extra state for the readout, after `fps · `.</summary>
    protected virtual string StateText() => "baseline";

    /// <summary>Drive points this frame, in [-1,1] sim space. Default: one cycling source.</summary>
    protected virtual void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        _driveT += dt;
        if (!AutoDrive || _driveT < DriveInterval) { return; }
        _driveT = 0.0f;
        outDrops.Add(new Vector3(DefaultSource.X, DefaultSource.Y, DriveStrength));
    }

    protected static readonly Vector2 DefaultSource = new(-0.25f, -0.2f);

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
        BuildPlainMaterials();
        PushSharedUniforms();
        SetRaytrace(false);   // series default
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        Solver = new ExpWaterSolver(RenderingServer.GetRenderingDevice(), Grid)
        {
            DropRadius = 0.05f,
            Damping = ExpWaterSolver.DampingFor(Grid),
        };
        OnSolverReady();
    }

    // ---- scene 24, verbatim ----

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
        Cam = new Camera3D { Fov = 45.0f, Near = 0.02f, Far = 100.0f, Position = new Vector3(1.27f, 1.19f, -3.40f), Current = true };
        AddChild(Cam);
        Cam.LookAt(new Vector3(0.0f, -0.5f, 0.0f), Vector3.Up);
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = new Color(0.55f, 0.62f, 0.72f),
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.6f, 0.65f, 0.7f),
                AmbientLightEnergy = 1.0f,
                TonemapMode = Godot.Environment.ToneMapper.Linear,
            },
        });
    }

    private MeshInstance3D MakeWaterPlane() => new()
    {
        Mesh = new PlaneMesh { Size = new Vector2(2.0f, 2.0f), SubdivideWidth = WaterDetail, SubdivideDepth = WaterDetail },
        CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        CustomAabb = new Aabb(new Vector3(-50, -50, -50), new Vector3(100, 100, 100)),
    };

    private void BuildCaustics()
    {
        _causticVp = new SubViewport
        {
            Size = CausticSize, OwnWorld3D = true, TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            RenderTargetClearMode = SubViewport.ClearMode.Always,
        };
        AddChild(_causticVp);
        var cam = new Camera3D { Position = new Vector3(0, 5, 0), Projection = Camera3D.ProjectionType.Orthogonal, Size = 4.0f, Far = 100.0f, Current = true };
        _causticVp.AddChild(cam);
        cam.LookAt(Vector3.Zero, Vector3.Forward);
        var mi = MakeWaterPlane();
        _causticMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_caustics.gdshader") };
        _causticMat.SetShaderParameter("water_tex", _waterTex);
        mi.MaterialOverride = _causticMat;
        _causticVp.AddChild(mi);
        _sharedMats.Add(_causticMat);
        _causticTex = _causticVp.GetTexture();
    }

    private void BuildPool()
    {
        _poolMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_pool.gdshader") };
        _poolMat.SetShaderParameter("tile_tex", _tileTex);
        _poolMat.SetShaderParameter("water_tex", _waterTex);
        _poolMat.SetShaderParameter("caustic_tex", _causticTex);
        _sharedMats.Add(_poolMat);
        _rtPoolMesh = BuildRtPoolMesh();
        _plainPoolMesh = BuildPlainPoolMesh();
        _poolMi = new MeshInstance3D { Mesh = _rtPoolMesh, MaterialOverride = _poolMat, CustomAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)) };
        AddChild(_poolMi);
    }

    // Scene 24's proxy volume — a lid and no floor. Only valid under ww_pool.gdshader.
    private static ArrayMesh BuildRtPoolMesh()
    {
        Vector3 Octant(int i) => new((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1);
        int[][] faces = { new[] { 0, 4, 2, 6 }, new[] { 1, 3, 5, 7 }, new[] { 2, 6, 3, 7 }, new[] { 0, 2, 1, 3 }, new[] { 4, 5, 6, 7 } };
        var verts = new System.Collections.Generic.List<Vector3>();
        foreach (var f in faces)
        {
            Vector3 v0 = Octant(f[0]), v1 = Octant(f[1]), v2 = Octant(f[2]), v3 = Octant(f[3]);
            verts.AddRange(new[] { v0, v1, v2, v2, v1, v3 });
        }
        return MakeMesh(verts);
    }

    // The real pool: floor + four walls, normals INWARD so default back-face culling drops the
    // near walls and leaves the interior visible from an outside camera.
    private static ArrayMesh BuildPlainPoolMesh()
    {
        const float rim = 0.30f;
        var v = new System.Collections.Generic.List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => v.AddRange(new[] { a, b, c, a, c, d });
        Quad(new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1));
        Quad(new(-1, -1, -1), new(-1, -1, 1), new(-1, rim, 1), new(-1, rim, -1));
        Quad(new(1, -1, 1), new(1, -1, -1), new(1, rim, -1), new(1, rim, 1));
        Quad(new(1, -1, -1), new(-1, -1, -1), new(-1, rim, -1), new(1, rim, -1));
        Quad(new(-1, -1, 1), new(1, -1, 1), new(1, rim, 1), new(-1, rim, 1));
        return MakeMesh(v);
    }

    private static ArrayMesh MakeMesh(System.Collections.Generic.List<Vector3> verts)
    {
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
        _sphereMi = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f, RadialSegments = 32, Rings = 24 },
            MaterialOverride = _sphereMat,
            Transform = new Transform3D(Basis.FromScale(Vector3.One * SphereRadius), SphereCenter),
        };
        AddChild(_sphereMi);
    }

    private void BuildSurface()
    {
        _surfaceMat = new ShaderMaterial { Shader = GD.Load<Shader>(ShDir + "ww_surface.gdshader") };
        _surfaceMat.SetShaderParameter("tile_tex", _tileTex);
        _surfaceMat.SetShaderParameter("water_tex", _waterTex);
        _surfaceMat.SetShaderParameter("caustic_tex", _causticTex);
        _surfaceMat.SetShaderParameter("sky_tex", _skyTex);
        _sharedMats.Add(_surfaceMat);
        _surfaceMi = MakeWaterPlane();
        _surfaceMi.MaterialOverride = _surfaceMat;
        AddChild(_surfaceMi);
    }

    private void BuildPlainMaterials()
    {
        _poolPlain = new StandardMaterial3D
        {
            AlbedoTexture = _tileTex, Uv1Triplanar = true, Uv1Scale = Vector3.One * 2.0f, Roughness = 0.7f,
        };
        _spherePlain = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.87f, 0.9f), Roughness = 0.35f };
        // tank_water.gdshader assumes scene 23's RAMP bed and invents a jagged waterline on a
        // flat box; the neutral render wants no bed model at all.
        var w = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/mna_wave_surface.gdshader") };
        w.SetShaderParameter("height_tex", _waterTex);
        w.SetShaderParameter("tex_size", new Vector2(Grid, Grid));
        w.SetShaderParameter("plane_size", 2.0f);
        w.SetShaderParameter("displacement", 0.25f);
        w.SetShaderParameter("normal_strength", 3.0f);
        w.SetShaderParameter("colormap_gain", 8.0f);
        _surfacePlain = w;
        _plainWaterMat = w;

        _plainSun = new DirectionalLight3D { LightEnergy = 1.3f, Visible = false };
        AddChild(_plainSun);
        _plainSun.LookAtFromPosition(LightDir.Normalized() * 20.0f, Vector3.Zero, Vector3.Up);
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
        _surfaceMat.SetShaderParameter("ior", 1.333f);
        _surfaceMat.SetShaderParameter("fresnel_min", 0.25f);
    }

    private void SetRaytrace(bool on)
    {
        _raytrace = on;
        _poolMi.Mesh = on ? _rtPoolMesh : _plainPoolMesh;
        _poolMi.MaterialOverride = on ? _poolMat : _poolPlain;
        _sphereMi.MaterialOverride = on ? _sphereMat : _spherePlain;
        _surfaceMi.MaterialOverride = on ? _surfaceMat : _surfacePlain;
        _plainSun.Visible = !on;
        _causticVp.RenderTargetUpdateMode = on ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
    }

    public override void _PhysicsProcess(double delta)
    {
        var solver = Solver;
        if (solver == null || !solver.Ready) { return; }
        float dt = (float)delta;

        var drops = new System.Collections.Generic.List<Vector3>();
        CollectDrive(dt, drops);
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            Vector2 s = MouseSim();
            if (s.X > -2.0f) { drops.Add(new Vector3(s.X, s.Y, DriveStrength)); }
        }

        _waterTex.TextureRdRid = solver.DisplayRid;
        var extra = new System.Collections.Generic.List<Vector4>();
        for (int i = 1; i < drops.Count; ++i) { extra.Add(new Vector4(drops[i].X, drops[i].Y, solver.DropRadius, drops[i].Z)); }
        bool hasFirst = drops.Count > 0;
        float fx = hasFirst ? drops[0].X : 0.0f, fz = hasFirst ? drops[0].Y : 0.0f, fs = hasFirst ? drops[0].Z : 0.0f;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (extra.Count > 0) { solver.InjectDrops(extra); }
            solver.Step(hasFirst, fx, fz, fs, true, SphereCenter, SphereCenter);
        }));

        _fpsAccum += dt;
        if (_readout != null && _fpsAccum >= 0.4f)
        {
            _fpsAccum = 0.0f;
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {StateText()} · raytrace {(_raytrace ? "ON" : "OFF")}";
        }
    }

    protected Vector2 MouseSim()
    {
        Vector2 mp = GetViewport().GetMousePosition();
        var plane = new Plane(Vector3.Up, 0.0f);
        if (plane.IntersectsRay(Cam.ProjectRayOrigin(mp), Cam.ProjectRayNormal(mp)) is not Vector3 hit) { return new Vector2(-9, -9); }
        if (MathF.Abs(hit.X) > 1.0f || MathF.Abs(hit.Z) > 1.0f) { return new Vector2(-9, -9); }
        return new Vector2(hit.X, hit.Z);
    }

    public override void _ExitTree()
    {
        if (_waterTex != null) { _waterTex.TextureRdRid = default; }
        var s = Solver;
        Solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var (title, hint) = SceneInfo;
        var ui = new DemoUI(this, title, hint);
        _readout = ui.AddReadout("— fps");

        AddVariableControls(ui);

        ui.AddSection("Baseline (frozen — same in every scene)");
        ui.AddToggle("Auto drive", AutoDrive, v => AutoDrive = v);
        ui.AddSlider("Drive strength", 0.005f, 0.3f, DriveStrength, v => DriveStrength = v);
        ui.AddSlider("Drive interval (s)", 0.1f, 4.0f, DriveInterval, v => DriveInterval = v);
        ui.AddToggle("Raytrace + caustics", false, SetRaytrace);
        // The neutral water shading is deliberately plain, which makes small ripples hard to
        // see. The divergent colormap shows the raw height field instead — same sim, legible.
        ui.AddToggle("Height colormap", false,
            v => _plainWaterMat.SetShaderParameter("show_heightmap", v ? 1.0f : 0.0f));
        ui.AddSlider("Colormap gain", 1.0f, 60.0f, 8.0f,
            v => _plainWaterMat.SetShaderParameter("colormap_gain", v));
        ui.AddSlider("Wave displacement", 0.0f, 1.5f, 0.25f,
            v => _plainWaterMat.SetShaderParameter("displacement", v));
        ui.AddSlider("Caustics", 0.0f, 1.0f, _causticIntensity, v => _causticMat.SetShaderParameter("caustic_intensity", v));
    }
}
