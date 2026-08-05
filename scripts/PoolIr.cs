using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_IR — scene 24, forked. Everything below is RaytracedPoolMna verbatim except three
// things:
//   1. the MNA stamp solver is replaced by the FULL SIM (WebgpuWaterSolver).
//   2. the sim is MEASURED and then replaced by convolution. Three phases:
//        CAPTURE — reset to flat, fire ONE impulse, record the height field every tick into a
//                  3D texture (512 slices, 128 MB). That stack is h(x,y,k), the pool's IR.
//        REPLAY  — solver OFF entirely; the surface is rebuilt as SUM_k s[t-k]*h(x,y,k).
//        SIM     — the live solver, for A/B against the replay.
//      An IR is not a recording of a place, it is a SYSTEM OPERATOR: one measurement, then
//      infinitely many drive signals give infinitely many different outcomes. Change the signal
//      in REPLAY and the water changes with no solver running.
//   3. a RAYTRACE toggle, so render cost can be separated from solver cost on the readout.
//
// Honest limit: exact only while the response is linear. Wallace's heightfield is close enough
// to LTI that replay should track the sim; a nonlinear solver would need drive-indexed kernels.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_ir.tscn 12 1280x900
public partial class PoolIr : Node3D
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
    private IrField? _ir;
    private SubViewport _causticVp = null!;

    // Raytrace A/B — see SetRaytrace(). ON = scene 24 unchanged; OFF = same geometry with
    // ordinary materials and no caustics pass, so render cost stops masking solver cost.
    private MeshInstance3D _poolMi = null!, _sphereMi = null!, _surfaceMi = null!;
    private Material _poolPlain = null!, _spherePlain = null!, _surfacePlain = null!;
    private DirectionalLight3D _plainSun = null!;
    private ArrayMesh _rtPoolMesh = null!, _plainPoolMesh = null!;

    private const int IrDepth = 512;      // recorded ticks — the IR's length

    private enum Phase { Sim, Capture, Replay, Inspect }

    private Phase _phase = Phase.Capture;
    private int _captureSlice;
    private bool _captureFired;
    private bool _haveIr;
    private bool _raytrace = false;   // experiment series starts with the render as cheap/neutral as possible

    // Where the impulse goes in. The IR is measured FROM this point, so replay drives the same
    // point — that is what makes the convolution valid.
    private static readonly Vector2 ImpulseAt = new(-0.25f, -0.2f);
    private const float ImpulseStrength = 0.06f;

    // Drive-signal history: history[k] = s[t-k].
    private readonly float[] _history = new float[IrDepth];
    private int _sigKind;
    private float _sigT;
    private float _sigInterval = 1.0f;
    private float _sigStrength = 1.0f;
    private float _replayGain = 1.0f;
    private float _phaseT;
    private bool _fireOnce;
    private int _inspectSlice = 20;
    private readonly RandomNumberGenerator _rng = new();

    private Label? _readout;
    private Label? _costReadout;
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
        BuildPlainMaterials();
        PushSharedUniforms();
        SetRaytrace(_raytrace);
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _solver = new WebgpuWaterSolver(rd, Grid)
        {
            DropRadius = 0.05f,
            Damping = WebgpuWaterSolver.DampingFor(Grid),
        };
        _ir = new IrField(rd, Grid, IrDepth);
    }

    private void StartCapture()
    {
        _phase = Phase.Capture;
        _captureSlice = 0;
        _captureFired = false;
        _haveIr = false;
        Array.Clear(_history);
        var s = _solver;
        var ir = _ir;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            ir?.ClearIr();
            s?.Reset();   // an IR has to be measured from rest, or leftover state gets baked in
        }));
    }

    // One drive sample this frame — the signal pushed through the measured operator.
    private float NextDrive(float dt)
    {
        if (_fireOnce) { _fireOnce = false; return _sigStrength; }
        _sigT += dt;
        _phaseT += dt;
        switch (_sigKind)
        {
            case 1:   // continuous swell — two incommensurate sines, never repeats
                return _sigStrength * 0.06f *
                       (MathF.Sin(_phaseT * 1.7f) + 0.6f * MathF.Sin(_phaseT * 2.63f));
            case 2:   // noise
                return _sigStrength * 0.10f * (_rng.Randf() * 2.0f - 1.0f);
            case 3:   // manual only
                return 0.0f;
            default:  // impulse train
                if (_sigT < _sigInterval) { return 0.0f; }
                _sigT = 0.0f;
                return _sigStrength;
        }
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

        var ir = _ir;
        if (ir == null || !ir.Ready) { return; }
        float dt = (float)delta;

        switch (_phase)
        {
            case Phase.Capture:
            {
                _waterTex.TextureRdRid = solver.DisplayRid;
                bool fire = !_captureFired;
                _captureFired = true;
                int slice = _captureSlice;
                RenderingServer.CallOnRenderThread(Callable.From(() =>
                {
                    solver.Step(fire, ImpulseAt.X, ImpulseAt.Y, fire ? ImpulseStrength : 0.0f);
                    ir.CaptureSlice(solver.DisplayRid, slice);
                }));
                ++_captureSlice;
                if (_captureSlice >= IrDepth)
                {
                    _haveIr = true;
                    _phase = Phase.Replay;
                    Array.Clear(_history);
                }
                break;
            }

            case Phase.Inspect:
            {
                if (!_haveIr) { break; }
                _waterTex.TextureRdRid = ir.DisplayRid;
                int si = _inspectSlice;
                RenderingServer.CallOnRenderThread(Callable.From(() => ir.InspectSlice(si, 1.0f)));
                break;
            }

            case Phase.Replay:
            {
                if (!_haveIr) { break; }
                float s = NextDrive(dt);
                for (int k = _history.Length - 1; k > 0; --k) { _history[k] = _history[k - 1]; }
                _history[0] = s;

                _waterTex.TextureRdRid = ir.DisplayRid;
                // Units: h_k was recorded as the response to a drop of amplitude ImpulseStrength,
                // and Sim mode drives drops of NextDrive*ImpulseStrength. The two factors of
                // ImpulseStrength cancel, so history (raw NextDrive values) convolved against h_k
                // is already in sim units — gain 1.0 makes REPLAY and SIM directly comparable.
                RenderingServer.CallOnRenderThread(Callable.From(() => ir.Convolve(_history, _replayGain)));
                break;
            }

            default:
            {
                float e = NextDrive(dt) * ImpulseStrength;
                bool drop = MathF.Abs(e) > 1.0e-7f;
                _waterTex.TextureRdRid = solver.DisplayRid;
                RenderingServer.CallOnRenderThread(Callable.From(() =>
                    solver.Step(drop, ImpulseAt.X, ImpulseAt.Y, e)));
                break;
            }
        }

        _fpsAccum += dt;
        if (_readout != null && _fpsAccum >= 0.5f)
        {
            _fpsAccum = 0.0f;
            string ph = _phase switch
            {
                Phase.Capture => $"CAPTURE {_captureSlice}/{IrDepth}",
                Phase.Replay => "REPLAY — convolution only, solver OFF",
                Phase.Inspect => $"INSPECT — raw IR slice {_inspectSlice}/{IrDepth}",
                _ => "SIM — live solver",
            };
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {ph} · raytrace {(_raytrace ? "ON" : "OFF")}";
            if (_costReadout != null)
            {
                _costReadout.Text = _phase == Phase.Replay
                    ? $"active taps {ir.LastTapCount}/{IrDepth} · {(long)Grid * Grid * ir.LastTapCount / 1000} k MACs/frame"
                    : $"sim {Grid}x{Grid} x 4 passes = {(long)Grid * Grid * 4 / 1000} k cell-ops/frame";
            }
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
        var ir = _ir;
        _solver = null;
        _ir = null;
        RenderingServer.CallOnRenderThread(Callable.From(() => { ir?.Free(); s?.Free(); }));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "24_IR · scene 24, measured once then convolved",
            "Scene 24 forked: MNA replaced by the FULL SIM, then the sim itself replaced by its own "
            + "impulse response. CAPTURE fires one impulse and records 512 height fields — h(x,y,k). "
            + "REPLAY switches the solver OFF and rebuilds the surface as SUM_k s[t-k]*h(x,y,k): no "
            + "neighbours, no CFL, every cell independent. Change the drive signal and the water "
            + "changes with no solver running — an IR is an OPERATOR, not a recording. Switch to Sim "
            + "for the A/B, and toggle the raytrace to separate render cost from solver cost.");
        _readout = ui.AddReadout("— fps");
        _costReadout = ui.AddReadout("—");

        ui.AddSection("Phase");
        ui.AddOptions("Mode", new[] { "Replay (IR)", "Sim (live solver)", "Inspect IR slice" }, 0, v =>
        {
            if (v == 1) { _phase = Phase.Sim; }
            else if (v == 2) { _phase = _haveIr ? Phase.Inspect : Phase.Capture; }
            else if (_haveIr) { _phase = Phase.Replay; Array.Clear(_history); }
            else { StartCapture(); }
        });
        ui.AddSlider("Inspect slice k", 0, IrDepth - 1, _inspectSlice, v => _inspectSlice = (int)v);
        ui.AddToggle("Re-capture IR", false, v => { if (v) { StartCapture(); } });

        ui.AddSection("Drive signal (same operator, different input)");
        ui.AddOptions("Signal", new[] { "Impulse train", "Continuous swell", "Noise", "Manual only" }, 0,
            v => { _sigKind = v; Array.Clear(_history); });
        ui.AddSlider("Interval (s)", 0.05f, 3.0f, _sigInterval, v => _sigInterval = v);
        ui.AddSlider("Strength", 0.05f, 3.0f, _sigStrength, v => _sigStrength = v);
        ui.AddToggle("Fire one", false, v => { if (v) { _fireOnce = true; } });
        ui.AddSlider("Replay gain", 0.0f, 3.0f, _replayGain, v => _replayGain = v);

        ui.AddSection("Render (isolate the variable)");
        ui.AddToggle("Raytrace + caustics", _raytrace, SetRaytrace);
        ui.AddSlider("Caustics", 0.0f, 1.0f, _causticIntensity, v => _causticMat.SetShaderParameter("caustic_intensity", v));
    }
}
