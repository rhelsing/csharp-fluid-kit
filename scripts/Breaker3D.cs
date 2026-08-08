using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

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
public partial class Breaker3D : Node3D
{
    private static readonly Vector3I Grid = new(80, 48, 48);
    private const float WorldSize = 6.0f;

    private Camera3D _cam = null!;
    private MultiMeshInstance3D _mmi = null!;
    private MultiMesh _mm = null!;
    private ShaderMaterial _mat = null!;
    private Texture3Drd _liqTex = null!;
    private float[] _buf = Array.Empty<float>();
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

    // Splats, not an isosurface. Marching tets and raymarching render the same thing — the
    // isosurface of a coarse volume — and both read as faceted goo, because resolution is the
    // cause rather than the renderer. Overlapping splats merge into a mass with no surface
    // computed anywhere, so there is no facet structure to see. Same renderer as scene 26, and
    // the same one FLIP would use, so none of it is throwaway.
    private void BuildSurface()
    {
        float s = WorldSize / Grid.X;
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f, RadialSegments = 6, Rings = 3 },
            InstanceCount = 0,
        };
        // Normals come from the liquid FIELD, not from the splat geometry — see
        // liquid_splat.gdshader. That is what stops it reading as a pile of balls.
        _liqTex = new Texture3Drd();
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/fluid3d/liquid_splat.gdshader") };
        _mat.SetShaderParameter("liquid_tex", _liqTex);
        _mat.SetShaderParameter("grid_dims", new Vector3(Grid.X, Grid.Y, Grid.Z));
        _mat.SetShaderParameter("grid_origin", new Vector3(-Grid.X * s * 0.5f, -Grid.Y * s * 0.5f, -Grid.Z * s * 0.5f));
        _mat.SetShaderParameter("grid_scale", s);
        _mmi = new MultiMeshInstance3D
        {
            Multimesh = _mm,
            MaterialOverride = _mat,
            Scale = new Vector3(s, s, s),
            Position = new Vector3(-Grid.X * s * 0.5f, -Grid.Y * s * 0.5f, -Grid.Z * s * 0.5f),
        };
        AddChild(_mmi);
    }

    // Surface cells only: liquid, with at least one 6-neighbour that is not.
    private int BuildSplats(float[] f, float iso)
    {
        int nx = Grid.X, ny = Grid.Y, nz = Grid.Z;
        int cap = _buf.Length / 12;
        int n = 0;
        for (int z = 1; z < nz - 1; z++)
        for (int y = 1; y < ny - 1; y++)
        for (int x = 1; x < nx - 1; x++)
        {
            int i = x + nx * (y + ny * z);
            if (f[i] <= iso) { continue; }
            if (f[i - 1] > iso && f[i + 1] > iso &&
                f[i - nx] > iso && f[i + nx] > iso &&
                f[i - nx * ny] > iso && f[i + nx * ny] > iso) { continue; }

            if (n >= cap) { return n; }
            int o = n * 12;
            _buf[o + 0] = 1f; _buf[o + 1] = 0f; _buf[o + 2] = 0f; _buf[o + 3] = x;
            _buf[o + 4] = 0f; _buf[o + 5] = 1f; _buf[o + 6] = 0f; _buf[o + 7] = y;
            _buf[o + 8] = 0f; _buf[o + 9] = 0f; _buf[o + 10] = 1f; _buf[o + 11] = z;
            n++;
        }
        return n;
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

        int cap = Grid.X * Grid.Y * 8;
        if (_buf.Length != cap * 12) { _buf = new float[cap * 12]; }

        _tris = BuildSplats(f, _iso);
        if (_mm.InstanceCount != cap) { _mm.InstanceCount = cap; }
        _mm.Buffer = _buf;
        _mm.VisibleInstanceCount = _tris;
        if (_fluid is { Ready: true }) { _liqTex.TextureRdRid = _fluid.DensityRid; }

        if (++_tick % 12 == 0 && _readout != null)
        {
            double vol = 0;
            foreach (float v in f) { vol += v; }
            // Liquid volume is the honest instrument here: an advected scalar leaks, and a
            // free surface that loses mass looks exactly like one that is behaving.
            _readout.Text = $"dam break · {_tris} splats · liquid volume {vol:0} cells\n"
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
        var ui = new DemoUI(this, "27 · the curl — dam break with a free surface (3D)",
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
        ui.AddSlider("Splat size (cells)", 0.5f, 2.0f, 0.85f,
            v => _mm.Mesh = new SphereMesh { Radius = v, Height = v * 2f, RadialSegments = 6, Rings = 3 });
        ui.AddSlider("Shine (roughness)", 0.02f, 0.6f, 0.14f, v => _mat.SetShaderParameter("rough", v));
        ui.AddSlider("Ripple detail", 0.0f, 1.2f, 0.35f, v => _mat.SetShaderParameter("detail_gain", v));
        ui.AddSlider("Ripple frequency", 2.0f, 24.0f, 9.0f, v => _mat.SetShaderParameter("detail_freq", v));
        ui.AddToggle("RESET (flip to re-release the dam)", false,
            _ => RenderingServer.CallOnRenderThread(Callable.From(SeedDam)));
    }
}
