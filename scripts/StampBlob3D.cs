using System;
using System.Diagnostics;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 08 — Phase 4a: the stamp solver in 3D, shown WITHOUT ray tracing. GpuStampSolver3D
// relaxes a 3D implicit damped-wave "blob" field (a central source holds a stable blob;
// pokes ripple it) on a 48³ grid; each frame the volume is read back and marching-TETS'd
// (scripts/lib/IsoSurface.cs) into a triangle mesh that Godot rasterizes and lights as a
// slimy blob. The heavy per-frame polygonization is exactly what the C# track is for.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/08_blob3d.tscn 6 1280x900
public partial class StampBlob3D : Node3D
{
    private static readonly Vector3I Grid = new(48, 48, 48);
    private const float WorldSize = 4.0f;
    private const string StampPath = "res://shaders/stamp3d/stamp_blob_3d.glslinc";

    private const string RaymarchShaderPath = "res://shaders/stamp3d/blob_raymarch.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _blobMi = null!;
    private ArrayMesh _mesh = null!;

    // Raymarched path: the field is already an image3D on the GPU, so a Texture3DRD + a
    // fragment-shader march renders the isosurface with NO readback, NO polygonization and NO
    // mesh upload — the three costs that made this scene 15 fps. The CPU marching-tets path is
    // kept behind a toggle because it is what the scene was originally built to demonstrate,
    // and because having both makes the cost difference something you can see rather than
    // something I claim.
    private MeshInstance3D _volMi = null!;
    private ShaderMaterial _volMat = null!;
    private Texture3Drd _fieldTex = null!;
    private bool _raymarch = true;
    private GpuStampSolver3D? _solver;
    private Label? _readout;
    private volatile float[]? _field;
    private int _tick;
    private float _fmax;
    private int _tris;

    // TEMPORARY instrumentation — "it's slow" needs a stage name, not a theory. Four numbers,
    // each an EMA so the readout does not flicker, printed on the existing 12-tick readout so
    // nothing spams. Strip once the bottleneck is fixed.
    private volatile float _msSolve, _msRead, _msIso, _msMesh;

    private float _waveSpeed = 1.6f;
    private float _dt = 0.4f;
    private float _damping = 0.12f;    // low → poke ripples persist and the blob keeps wobbling
    private float _leak = 0.03f;
    private float _srcRadius = 9.0f;
    private float _srcStrength = 0.12f;
    private int _iters = 18;
    private float _iso = 0.55f;         // tighter surface: fewer tris (faster) + sits where pokes act

    private float _pokeRadius = 5.0f;
    private float _pokeStrength = 1.6f;
    private float _pokeT;
    private const float PokeInterval = 0.4f;
    private int _pokeI;
    private static readonly Vector3[] Pokes =
    {
        new(38, 24, 24), new(10, 24, 24), new(24, 38, 24),
        new(24, 10, 24), new(24, 24, 38), new(24, 24, 10),
    };

    public override void _Ready()
    {
        BuildEnvironment();
        BuildBlob();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new GpuStampSolver3D(RenderingServer.GetRenderingDevice(), Grid, StampPath);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 55.0f, Position = new Vector3(0, 1.4f, 6.0f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(Vector3.Zero, Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48, -40, 0),
            LightColor = new Color(1.0f, 0.96f, 0.9f),
            LightEnergy = 1.6f,
        });

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.2f, 0.34f, 0.6f),
            SkyHorizonColor = new Color(0.6f, 0.72f, 0.85f),
            GroundBottomColor = new Color(0.14f, 0.16f, 0.2f),
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

    private void BuildBlob()
    {
        _mesh = new ArrayMesh();
        float s = WorldSize / Grid.X;
        _blobMi = new MeshInstance3D
        {
            Mesh = _mesh,
            Scale = new Vector3(s, s, s),
            Position = new Vector3(-Grid.X * s * 0.5f, -Grid.Y * s * 0.5f, -Grid.Z * s * 0.5f),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.35f, 0.85f, 0.45f),
                Metallic = 0.0f,
                Roughness = 0.22f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                RimEnabled = true,
                Rim = 0.5f,
                ClearcoatEnabled = true,
                Clearcoat = 0.6f,
            },
        };
        AddChild(_blobMi);
        _blobMi.Visible = !_raymarch;

        // Bounding box for the march. Size 4 centred at the origin matches the world extent
        // the CPU mesh occupies (48 cells x WorldSize/48), so toggling between the two paths
        // does not move the blob.
        _fieldTex = new Texture3Drd();
        _volMat = new ShaderMaterial { Shader = GD.Load<Shader>(RaymarchShaderPath) };
        _volMat.SetShaderParameter("field", _fieldTex);
        _volMat.SetShaderParameter("iso", _iso);
        _volMi = new MeshInstance3D
        {
            // UNIT box + Scale, not BoxMesh{Size=4}. BoxMesh puts VERTEX at +/-Size/2, so a
            // size-4 box hands the shader VERTEX in [-2,2] and its [0,1] field-space mapping
            // silently samples a quarter of the volume — the blob renders 4x too small.
            // A unit box keeps VERTEX in [-0.5,0.5] and the world size lives in MODEL_MATRIX,
            // where the ray transform already accounts for it.
            Mesh = new BoxMesh { Size = Vector3.One },
            Scale = new Vector3(WorldSize, WorldSize, WorldSize),
            MaterialOverride = _volMat,
            Visible = false,   // until the solver's RD texture exists (else Godot logs an invalid-texture error)
            // The march writes DEPTH from the hit point, but Godot still culls against the
            // BOX's bounds; without this the blob pops out when the box centre leaves frame.
            ExtraCullMargin = WorldSize,
        };
        AddChild(_volMi);
    }

    public override void _PhysicsProcess(double delta)
    {
        var poke = Vector4.Zero;
        _pokeT += (float)delta;
        if (_pokeT >= PokeInterval)
        {
            _pokeT = 0.0f;
            var pk = Pokes[_pokeI];
            _pokeI = (_pokeI + 1) % Pokes.Length;
            poke = new Vector4(pk.X, pk.Y, pk.Z, 1.0f);
        }

        float beta = _dt * _dt * _waveSpeed * _waveSpeed;
        float a = _damping * _dt * 0.5f;
        float pr = _pokeRadius, pw = poke.W != 0 ? _pokeStrength : 0f;
        // vec4 size (xyz + pad) + beta,a,leak,src_r,src_w + poke(x,y,z,r,w) = 14 floats, pad to 16 (64B)
        float[] pc =
        {
            Grid.X, Grid.Y, Grid.Z, 0f,
            beta, a, _leak, _srcRadius, _srcStrength,
            poke.X, poke.Y, poke.Z, pr, pw,
            0f, 0f,
        };
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
        int iters = _iters;
        bool ray = _raymarch;
        // In raymarch mode the field never leaves the GPU. The one exception is a low-rate
        // readback for the fmax readout — the "if it vanishes, set iso below fmax" hint is the
        // scene's only debugging affordance and it is worth 2.5ms every 30th tick to keep.
        bool wantField = !ray || _tick % 30 == 0;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_solver == null) { return; }
            long t0 = Stopwatch.GetTimestamp();
            _solver.Step(bytes, iters);
            long t1 = Stopwatch.GetTimestamp();
            if (wantField) { _field = _solver.ReadField(); }
            long t2 = Stopwatch.GetTimestamp();
            // Step() only ENQUEUES; ReadField()'s TextureGetData is what forces the GPU to
            // finish, so the solve's real cost lands in the read number. That is the point —
            // it measures the stall, which is the thing worth knowing.
            _msSolve = Ema(_msSolve, Ms(t0, t1));
            _msRead = Ema(_msRead, Ms(t1, t2));
        }));
    }

    public override void _Process(double delta)
    {
        if (_raymarch)
        {
            // The whole per-frame cost is now: hand the shader the live RD texture. No
            // readback, no polygonize, no upload.
            if (_solver is { Ready: true })
            {
                _fieldTex.TextureRdRid = _solver.FieldRid;
                _volMi.Visible = true;
            }
            _volMat.SetShaderParameter("iso", _iso);
            _msIso = 0f;
            _msMesh = 0f;
            _tris = 0;
            UpdateReadout();
            return;
        }

        var f = _field;
        if (f == null || f.Length != Grid.X * Grid.Y * Grid.Z) { return; }

        long i0 = Stopwatch.GetTimestamp();
        var (verts, norms) = IsoSurface.Build(f, Grid, _iso);
        long i1 = Stopwatch.GetTimestamp();
        _mesh.ClearSurfaces();
        if (verts.Length >= 3)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        long i2 = Stopwatch.GetTimestamp();
        _msIso = Ema(_msIso, Ms(i0, i1));
        _msMesh = Ema(_msMesh, Ms(i1, i2));
        _tris = verts.Length / 3;
        UpdateReadout();
    }

    private void UpdateReadout()
    {
        _tick++;
        if (_readout == null || _tick % 12 != 0) { return; }

        var f = _field;
        if (f != null && f.Length == Grid.X * Grid.Y * Grid.Z)
        {
            float m = 0f;
            for (int i = 0; i < f.Length; i++) { if (f[i] > m) { m = f[i]; } }
            _fmax = m;
        }
        string mode = _raymarch ? "raymarch" : $"marching-tets · {_tris} tris";
        _readout.Text = $"3D blob · {mode} · fmax {_fmax:0.00} · iso {_iso:0.00} · {Engine.GetFramesPerSecond():0}fps\n"
            + $"solve {_msSolve:0.00}ms · readback {_msRead:0.00}ms · isosurface {_msIso:0.00}ms · mesh {_msMesh:0.00}ms";
    }

    private static float Ms(long a, long b) => (float)((b - a) * 1000.0 / Stopwatch.Frequency);
    private static float Ema(float prev, float now) => prev <= 0f ? now : prev * 0.9f + now * 0.1f;

    public override void _ExitTree()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() => _solver?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "08 · 3D stamp solver → marching-tets blob (C#)",
            "Phase 4 — the MNA stamper in a VOLUME, shown without ray tracing. GpuStampSolver3D relaxes a "
            + "3D implicit damped-wave field (7-point stencil, 48³) with a central source holding a stable "
            + "blob; the field is read back each frame and marching-TETS'd into a triangle mesh that Godot "
            + "rasterizes and lights. Same matrix-free stamp idea as the 2D scenes, one dimension up. "
            + "Auto-pokes wobble it like jelly. If it vanishes, set the isolevel below fmax in the readout.");
        _readout = ui.AddReadout("3D blob —");
        ui.AddToggle("Raymarch (off = CPU marching tets)", _raymarch, v =>
        {
            _raymarch = v;
            _volMi.Visible = v;
            _blobMi.Visible = !v;
        });
        ui.AddSlider("Isolevel", 0.02f, 1.5f, _iso, v => _iso = v);
        ui.AddSlider("Wave speed (c)", 0.4f, 3.0f, _waveSpeed, v => _waveSpeed = v);
        ui.AddSlider("Damping", 0.0f, 1.0f, _damping, v => _damping = v);
        ui.AddSlider("Rest leak κ", 0.0f, 0.2f, _leak, v => _leak = v);
        ui.AddSlider("Source strength", 0.0f, 0.4f, _srcStrength, v => _srcStrength = v);
        ui.AddSlider("Source radius", 3.0f, 18.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Poke strength", 0.0f, 2.0f, _pokeStrength, v => _pokeStrength = v);
        ui.AddSlider("Sweeps / tick", 1, 40, _iters, v => _iters = (int)v);
    }
}
