using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_CXM — scene 24, forked. Everything below is RaytracedPoolMna verbatim except three
// things:
//   1. the MNA stamp solver is replaced by the FULL SIM (WebgpuWaterSolver, Wallace's explicit
//      heightfield). Stamping may earn its way back later; source material first.
//   2. a faithful CXM-1978 tank (scripts/lib/CxmTank.cs — a 1:1 port of cxm-1978.cmajor, same
//      topology, same delay lengths, same coefficients) runs beside the sim. One excitation
//      feeds both; the tank's four raw nodes stamp back at four tap positions.
//   3. a RAYTRACE toggle. The caustics SubViewport is a second 1024^2 render every frame — GPU
//      cost mixed into the thing being measured. Turning it off isolates the variable; it is a
//      toggle rather than a deletion so the cost can be A/B'd on the readout.
// Tank off => plain full sim. Raytrace on => scene 24's render, unchanged.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm.tscn 12 1280x900
public partial class PoolCxm : Node3D
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
    private WebgpuWaterSolver? _solver;
    private SubViewport _causticVp = null!;

    // Raytrace A/B. ww_pool / ww_sphere / ww_surface re-trace walls, floor, ball and sky PER
    // PIXEL, and the caustics SubViewport is a second 1024^2 render every frame. That is render
    // cost sitting on top of the solver cost we are trying to measure, so both material sets are
    // built up front and the toggle swaps them: ON = scene 24 unchanged, OFF = the same geometry
    // with ordinary materials, no caustics pass, surface-only water shader.
    private MeshInstance3D _poolMi = null!, _sphereMi = null!, _surfaceMi = null!;
    private Material _poolPlain = null!, _spherePlain = null!, _surfacePlain = null!;
    private DirectionalLight3D _plainSun = null!;
    private ArrayMesh _rtPoolMesh = null!, _plainPoolMesh = null!;

    private float _pokeStrength = 0.066f;
    private bool _auto = true;
    private float _pokeT;
    private int _pokeI;
    private static readonly Vector2[] Pokes = { new(0.4f, 0.4f), new(0.62f, 0.6f), new(0.5f, 0.5f), new(0.36f, 0.66f) };
    private Label? _readout;
    private float _fpsAccum;

    // ---- CXM tank ----
    private CxmTank _tank = null!;
    // The source runs at 48 kHz, where the longest delay (7188 samples) is 150 ms — that reads
    // as ringing, not water. Lowering the clock stretches every time constant together and
    // leaves the incommensurate RATIOS, the thing that stops the modes phase-locking, exactly
    // as Dattorro wrote them. At 3600 Hz the longest delay is ~2 s.
    private float _tankRate = 3600.0f;
    private bool _tankOn = true;
    private float _tankGain = 20.0f;
    private float _tankDrive = 1.0f;
    private double _tankAcc;
    private float _lastTankOut;
    private bool _raytrace = false;   // experiment series starts with the render as cheap/neutral as possible

    // One tap position per raw tank node. In audio the extra Dattorro taps only buy stereo
    // decorrelation; here each tap is a PLACE, so tap count is the spatial resolution of the
    // tank's contribution to the water.
    private static readonly Vector2[] TapPos =
    {
        new(-0.55f, -0.45f), new(0.50f, -0.55f), new(0.60f, 0.50f), new(-0.45f, 0.58f),
    };
    private const float TapRadius = 0.10f;

    public override void _Ready()
    {
        _tank = new CxmTank(_tankRate);
        _tank.SetType(2);            // Hall — the longest of the source's three presets
        _tank.SetDiffusion(2);       // High
        _tank.SetTankMod(1);         // Med
        _tank.SetDecay(0.85f);
        _tank.SetBass(0.9f);
        _tank.SetPreDelayMs(0.0f);   // the source's pre-delay is a control; at a stretched clock
                                     // its 2016 samples become 0.56 s of dead time up front
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
        SetRaytrace(_raytrace);
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new WebgpuWaterSolver(RenderingServer.GetRenderingDevice(), Grid)
        {
            DropRadius = 0.05f,
            Damping = WebgpuWaterSolver.DampingFor(Grid),
        };
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
        _causticVp = vp;
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
        _rtPoolMesh = BuildPoolMesh();
        _plainPoolMesh = BuildPlainPoolMesh();
        _poolMi = new MeshInstance3D { Mesh = _rtPoolMesh, MaterialOverride = _poolMat, CustomAabb = new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)) };
        AddChild(_poolMi);
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

    // The cheap counterpart to the ww_ raytracing set: the SAME meshes with ordinary materials,
    // plus a real sun (scene 24's environment is ambient-only because the raytracers lit
    // themselves). Built up front so the toggle is a material swap, not a rebuild.

    // BuildPoolMesh() is NOT a pool — decoded, its five faces are x=+/-1, z=+/-1 and y=+1, i.e.
    // a LID and no floor. It was a proxy volume for ww_pool.gdshader to raymarch, never meant to
    // be rasterized, which is why it renders as a broken box with ordinary materials. This is the
    // real thing: floor plus four walls, normals INWARD, so default back-face culling drops the
    // near walls and leaves the interior visible from an outside camera.
    private static ArrayMesh BuildPlainPoolMesh()
    {
        const float rim = 0.30f;
        var v = new System.Collections.Generic.List<Vector3>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) => v.AddRange(new[] { a, b, c, a, c, d });

        Quad(new(-1, -1, -1), new(1, -1, -1), new(1, -1, 1), new(-1, -1, 1));        // floor, up
        Quad(new(-1, -1, -1), new(-1, -1, 1), new(-1, rim, 1), new(-1, rim, -1));    // x=-1, faces +x
        Quad(new(1, -1, 1), new(1, -1, -1), new(1, rim, -1), new(1, rim, 1));        // x=+1, faces -x
        Quad(new(1, -1, -1), new(-1, -1, -1), new(-1, rim, -1), new(1, rim, -1));    // z=-1, faces +z
        Quad(new(-1, -1, 1), new(1, -1, 1), new(1, rim, 1), new(-1, rim, 1));        // z=+1, faces -z

        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = v.ToArray();
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        return m;
    }

    private void BuildPlainMaterials()
    {
        _poolPlain = new StandardMaterial3D
        {
            AlbedoTexture = _tileTex,
            Uv1Triplanar = true,
            Uv1Scale = Vector3.One * 2.0f,
            Roughness = 0.7f,
        };
        _spherePlain = new StandardMaterial3D { AlbedoColor = new Color(0.86f, 0.87f, 0.9f), Roughness = 0.35f };
        // tank_water.gdshader is authored for scene 23's RAMP bed (floor_base + slope) and on
        // scene 24's flat [-1,1] box its analytic depth tint invents a jagged waterline. The
        // neutral render wants no bed model at all, so use the plain height-field surface
        // (scenes 03/06/21) — reads .r, derives its own normals, no ramp, no refraction.
        var w = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/mna_wave_surface.gdshader") };
        w.SetShaderParameter("height_tex", _waterTex);
        w.SetShaderParameter("tex_size", new Vector2(Grid, Grid));
        w.SetShaderParameter("plane_size", 2.0f);
        w.SetShaderParameter("displacement", 0.25f);
        w.SetShaderParameter("normal_strength", 3.0f);
        w.SetShaderParameter("colormap_gain", 8.0f);
        _surfacePlain = w;

        _plainSun = new DirectionalLight3D { LightEnergy = 1.3f, Visible = false };
        AddChild(_plainSun);
        _plainSun.LookAtFromPosition(LightDir.Normalized() * 20.0f, Vector3.Zero, Vector3.Up);
    }

    // ON = scene 24 unchanged. OFF = same geometry, ordinary materials, no caustics pass.
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

        // The excitation is now a scalar signal (it has to feed the tank as well as the water),
        // and drop centres are in [-1,1] sim space rather than the MNA solver's pixel space.
        float dt = (float)delta;
        float e = 0.0f;
        Vector2 src = Vector2.Zero;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            Vector2 uv = MouseUv();
            if (uv.X >= 0.0f) { src = uv * 2.0f - Vector2.One; e = _pokeStrength; }
        }
        else if (_auto)
        {
            _pokeT += dt;
            if (_pokeT >= 0.5f)
            {
                _pokeT = 0.0f;
                var uv = Pokes[_pokeI];
                _pokeI = (_pokeI + 1) % Pokes.Length;
                src = uv * 2.0f - Vector2.One;
                e = _pokeStrength;
            }
        }

        // --- run the tank at ITS clock, consuming this frame's worth of samples ---
        var drops = new System.Collections.Generic.List<Vector4>(TapPos.Length);
        if (_tankOn)
        {
            _tankAcc += dt * _tankRate;
            int steps = Math.Min((int)_tankAcc, 8192);
            _tankAcc -= steps;

            // The excitation is an EVENT, not a level: feed it on the first tank sample of the
            // frame only. Feeding it every sample would turn one impulse into DC held for the
            // whole frame.
            Span<float> last = stackalloc float[4];
            for (int i = 0; i < steps; ++i)
            {
                float x = i == 0 ? e * _tankDrive : 0.0f;
                float[] nodes = _tank.Process(x);
                for (int t = 0; t < 4; ++t) { last[t] = nodes[t]; }
            }
            // Sample-and-hold, NOT the frame mean. The nodes carry an oscillating signal, so
            // averaging 60 tank samples into one number cancels it — measured offline: mean peak
            // 0.0003 against a node peak of 0.0074, a 25x loss that read on screen as "the tank
            // does nothing". Taking the newest sample is the standard decimation.
            if (steps > 0)
            {
                _lastTankOut = 0.0f;
                for (int t = 0; t < TapPos.Length; ++t)
                {
                    float v = last[t] * _tankGain;
                    _lastTankOut += MathF.Abs(v);
                    drops.Add(new Vector4(TapPos[t].X, TapPos[t].Y, TapRadius, v));
                }
            }
        }

        _waterTex.TextureRdRid = solver.DisplayRid;
        bool drop = MathF.Abs(e) > 1.0e-6f;
        float dcx = src.X, dcz = src.Y;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (drops.Count > 0) { solver.InjectDrops(drops); }
            solver.Step(drop, dcx, dcz, e, true, SphereCenter, SphereCenter);
        }));

        _fpsAccum += dt;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            string mode = _tankOn ? $"sim + CXM @ {_tankRate:0} Hz" : "sim only";
            string rt = _raytrace ? "raytrace ON" : "raytrace OFF";
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {mode} · {rt} · tank out {_lastTankOut:0.00000}";
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
        var ui = new DemoUI(this, "24_CXM · scene 24 + full sim + faithful CXM-1978 tank",
            "Scene 24 forked: the MNA stamp is replaced by the FULL SIM (Wallace), and a 1:1 port "
            + "of cxm-1978.cmajor runs beside it — Dattorro figure-of-eight, 4-stage input "
            + "diffuser, exact delays 7188/6005/6807/5106, cross-coupled chains, modulated delays "
            + "90 deg apart. One excitation feeds sim AND tank; the tank's 4 raw nodes stamp back "
            + "at 4 tap positions. Toggle the tank for the A/B, and the raytrace to isolate render "
            + "cost from solver cost. Auto-pokes; LEFT-CLICK to poke.");
        _readout = ui.AddReadout("— fps");
        ui.AddToggle("Auto-poke", _auto, v => _auto = v);
        ui.AddSlider("Poke strength", 0.005f, 1.0f, _pokeStrength, v => _pokeStrength = v);

        ui.AddSection("CXM tank");
        ui.AddToggle("Tank on", _tankOn, v => { _tankOn = v; if (!v) { _tank.Reset(); } });
        ui.AddSlider("Tank clock (Hz)", 300.0f, 48000.0f, _tankRate, v =>
        {
            _tankRate = v;
            _tank.Rate = v;
            _tank.SetDamping(4000.0f * v / 48000.0f);
            _tank.SetCrossover(362.0f * v / 48000.0f);
        });
        ui.AddSlider("Return gain", 0.0f, 200.0f, _tankGain, v => _tankGain = v);
        ui.AddSlider("Drive", 0.0f, 4.0f, _tankDrive, v => _tankDrive = v);
        ui.AddSlider("Pre-delay (ms)", 0.0f, 2000.0f, 0.0f, v => _tank.SetPreDelayMs(v));
        ui.AddOptions("Type", new[] { "Room", "Plate", "Hall" }, 2, v => _tank.SetType(v));
        ui.AddOptions("Diffusion", new[] { "Low", "Med", "High" }, 2, v => _tank.SetDiffusion(v));
        ui.AddOptions("Tank mod", new[] { "Low", "Med", "High" }, 1, v => _tank.SetTankMod(v));
        ui.AddSlider("Decay (mids)", 0.0f, 1.0f, 0.85f, v => _tank.SetDecay(v));
        ui.AddSlider("Bass decay", 0.0f, 1.0f, 0.9f, v => _tank.SetBass(v));

        ui.AddSection("Render (isolate the variable)");
        ui.AddToggle("Raytrace + caustics", _raytrace, SetRaytrace);
        ui.AddSlider("Caustics", 0.0f, 1.0f, _causticIntensity, v => _causticMat.SetShaderParameter("caustic_intensity", v));
    }
}
