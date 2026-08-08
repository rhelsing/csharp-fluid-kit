using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 31 — THE CURL, SURFACED AS A MESH. A FORK of scene 27, which keeps the splats.
//
// WHY BOTH EXIST. "Isosurface looks like goo" came from scene 08's blob and scene 26's
// bubbles, where a feature is ~4 cells across and marching tets has almost nothing to work
// with. A dam break is the opposite case — a large coherent slab many cells thick — so the
// isosurface has real geometry to describe and should read far better here than the same
// technique did there. Generalising the goo complaint to every scale was my error; this fork
// exists so the two can be compared by looking rather than by argument.
//
// Original header follows.
//
// Scene 27 — THE CURL. A dam break: a column of water is released, runs along the floor, hits
// the far wall and the return wave overturns.
//
// Same solver and same surface as scene 26 — only the initial condition differs. That is the
// point: a curl and a bubble are the same machinery, and the thing that makes both possible is
// that the surface is the 0.5 isosurface of a VOLUME rather than a height per column. A curl is
// multivalued (two water heights over the same floor spot), which h(x,z) cannot represent at any
// resolution with any solver — see hypotheses.md H-W6 for the measurement that established the
// heightfield's ceiling is steepening, not breaking.
//
// WHY A DAM BREAK. It is the standard free-surface validation case with published profiles, so
// it has an oracle — unlike "make a wave that looks like it breaks". Get the collapse and the
// run-up right and the overturn on the return is physics rather than tuning.
//
// The known interface-diffusion weakness from scene 26 applies here and bites HARDER: a breaking
// wave runs much longer than a rising bubble, so the surface has more time to go milky. If it
// does, that is the finding, and FLIP particles are the fix.
public partial class BreakerMesh3D : Node3D
{
    private static readonly Vector3I Grid = new(80, 48, 48);
    private const float WorldSize = 6.0f;

    private Camera3D _cam = null!;
    private MeshInstance3D _mi = null!;
    private ArrayMesh _mesh = null!;
    private ShaderMaterial _mat = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private volatile float[]? _field;
    private int _tick, _tris;

    // knobs
    private float _gravity = 12.0f;
    private float _iso = 0.5f;
    private float _viscosity = 0.999f;
    private float _liquidKeep = 1.0f;
    private int _iters = 24;
    private float _colWidth = 0.30f;    // dam column, fraction of the box in x
    private float _colHeight = 0.80f;   // fraction in y
    private float _poolDepth = 0.12f;   // still water the column collapses into
    private float _push;                // initial +x shove, to drive it into the far wall harder
    private bool _paused;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildSurface();
        RenderingServer.CallOnRenderThread(Callable.From(InitFluid));
        BuildUi();
    }

    private void InitFluid()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid);
        SeedDam();
    }

    // A tall column against the -x wall, plus a shallow pool across the floor for it to run into.
    private void SeedDam()
    {
        if (_fluid is not { Ready: true }) { return; }
        var f = new float[Grid.X * Grid.Y * Grid.Z];
        float colX = Grid.X * _colWidth;
        float colY = Grid.Y * _colHeight;
        float poolY = Grid.Y * _poolDepth;

        for (int z = 0; z < Grid.Z; z++)
        for (int y = 0; y < Grid.Y; y++)
        for (int x = 0; x < Grid.X; x++)
        {
            bool inCol = x < colX && y < colY;
            bool inPool = y < poolY;
            f[x + Grid.X * (y + Grid.Y * z)] = (inCol || inPool) ? 1.0f : 0.0f;
        }
        _fluid.WriteDensity(f);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 52.0f, Position = new Vector3(0.5f, 1.2f, 8.5f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, -0.4f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-48, -30, 0),
            LightColor = new Color(1.0f, 0.96f, 0.9f),
            LightEnergy = 1.6f,
        });

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.20f, 0.34f, 0.58f),
            SkyHorizonColor = new Color(0.62f, 0.74f, 0.86f),
            GroundBottomColor = new Color(0.12f, 0.14f, 0.18f),
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 1.0f,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
            },
        });
    }

    // Marching tets into an ArrayMesh, with scene 25's water model on the mesh normals.
    private void BuildSurface()
    {
        _mesh = new ArrayMesh();
        float sc = WorldSize / Grid.X;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fluid3d/liquid_surface.gdshader") };
        _mi = new MeshInstance3D
        {
            Mesh = _mesh,
            Scale = new Vector3(sc, sc, sc),
            Position = new Vector3(-Grid.X * sc * 0.5f, -Grid.Y * sc * 0.5f, -Grid.Z * sc * 0.5f),
            MaterialOverride = _mat,
        };
        AddChild(_mi);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_paused) { return; }
        float dt = 0.9f;
        // Optional +x shove near the column, so the run-up can be driven harder than gravity
        // alone manages — the overturn happens on the REBOUND off the far wall.
        // 16 floats, not 14: f3_add_source's block is 56 bytes of members but std430 rounds the
        // struct up to the vec4 alignment, so the pipeline demands 64.
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f,
            dt, -_gravity,
            Grid.X * 0.15f, Grid.Y * 0.35f, Grid.Z * 0.5f, Grid.Y * 0.30f,
            _push, 0f, 0f,
            0f,
            0f, 0f,                        // std430 tail padding to 64 B
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, dt, _viscosity, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, 0f, 0f, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, Grid.Z, 0f, dt, _liquidKeep, 0f, 0f };

        byte[] a = Pack(add), b = Pack(advV), c = Pack(sim), d = Pack(advD);
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid is not { Ready: true }) { return; }
            _fluid.Step(a, b, c, d, iters);
            _field = _fluid.ReadDensity();
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

        if (++_tick % 12 == 0 && _readout != null)
        {
            double vol = 0;
            foreach (float v in f) { vol += v; }
            // Liquid volume is the honest instrument here: an advected scalar leaks, and a
            // free surface that loses mass looks exactly like one that is behaving.
            _readout.Text = $"dam break · {_tris} tris · liquid volume {vol:0} cells\n"
                + $"{Engine.GetFramesPerSecond():0} fps · {Grid.X}x{Grid.Y}x{Grid.Z} · {_iters} pressure iters";
        }
    }

    private static byte[] Pack(float[] v)
    {
        var b = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    public override void _ExitTree()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "31 · the curl — dam break, MESH surface (fork of 27)",
            "A column of water is released and runs the length of the box; the wave that rebounds "
            + "off the far wall OVERTURNS. Same solver and same surface as scene 26 — only the "
            + "initial condition differs. The curl is possible here and impossible in scene 25 for "
            + "one reason: the surface is the 0.5 isosurface of a volume, so it can be multivalued. "
            + "Dam break specifically because it is the standard validation case with published "
            + "profiles, not a look. Watch liquid volume in the readout — if it drifts, the "
            + "interface is diffusing and the surface is lying to you.");
        _readout = ui.AddReadout("dam break —");
        ui.AddToggle("Pause", _paused, v => _paused = v);
        ui.AddSlider("Gravity", 0.0f, 30.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Isolevel (surface)", 0.15f, 0.85f, _iso, v => _iso = v);
        ui.AddSlider("Pressure iters", 4, 80, _iters, v => _iters = (int)v);
        ui.AddSlider("Extra push (+x)", 0.0f, 3.0f, _push, v => _push = v);
        ui.AddSlider("Viscosity (vel keep)", 0.95f, 1.0f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Liquid keep (1 = conserve)", 0.98f, 1.0f, _liquidKeep, v => _liquidKeep = v);
        ui.AddSlider("Column width", 0.10f, 0.60f, _colWidth, v => _colWidth = v);
        ui.AddSlider("Column height", 0.30f, 1.0f, _colHeight, v => _colHeight = v);
        ui.AddSlider("Pool depth", 0.0f, 0.40f, _poolDepth, v => _poolDepth = v);
        ui.AddSlider("Ripple frequency", 2.0f, 24.0f, 9.0f, v => _mat.SetShaderParameter("detail_freq", v));
        ui.AddToggle("RESET (flip to re-release the dam)", false,
            _ => RenderingServer.CallOnRenderThread(Callable.From(SeedDam)));
    }
}
