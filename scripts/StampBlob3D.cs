using System;
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

    private Camera3D _cam = null!;
    private MeshInstance3D _blobMi = null!;
    private ArrayMesh _mesh = null!;
    private GpuStampSolver3D? _solver;
    private Label? _readout;
    private volatile float[]? _field;
    private int _tick;
    private float _fmax;
    private int _tris;

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
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_solver == null) { return; }
            _solver.Step(bytes, iters);
            _field = _solver.ReadField();
        }));
    }

    public override void _Process(double delta)
    {
        var f = _field;
        if (f == null || f.Length != Grid.X * Grid.Y * Grid.Z) { return; }

        var (verts, norms) = IsoSurface.Build(f, Grid, _iso);
        _mesh.ClearSurfaces();
        if (verts.Length >= 3)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        _tris = verts.Length / 3;

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            float m = 0f;
            for (int i = 0; i < f.Length; i++) { if (f[i] > m) { m = f[i]; } }
            _fmax = m;
            _readout.Text = $"3D blob · fmax {_fmax:0.00} · iso {_iso:0.00} · {_tris} tris · {Engine.GetFramesPerSecond():0}fps";
        }
    }

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
