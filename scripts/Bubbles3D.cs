using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 26 — BUBBLES. Air pockets rising through water, in isolation.
//
// The point is to see a bubble at all. A heightfield cannot: h(x,z) is one value per column,
// so "air below water" is unrepresentable (hypotheses.md, THE PRIZE). This is a 3D volume with
// a free surface, so a bubble is simply a hole in the liquid field.
//
// FREE SURFACE, CHEAPLY. FluidSim3D already has the whole fluid loop — advect, divergence,
// project, gradient-subtract, buoyancy. The only missing piece was a surface, and this gets one
// by reinterpreting the existing dye field as LIQUID FRACTION: 1 = water, 0 = air. The 0.5
// isosurface IS the water surface, and it handles holes and detached blobs without knowing
// anything special about them.
//
// GRAVITY IS NEGATIVE BUOYANCY. f3_add_source does `v.y += dt * buoy * d`. With buoy < 0 the
// LIQUID is pulled down and air (d = 0) receives no force, so it rises relative to the water
// around it. That is physically what a bubble is — there is no separate "bubble force".
//
// KNOWN WEAKNESS, stated before you look at it: an advected scalar DIFFUSES, so the interface
// smears over time. That is why level sets need reinitialization and why VOF exists. A few
// seconds of rise should hold; a long run will go milky. If it smears out, that is the honest
// finding that justifies FLIP particles — and FLIP would reuse the pressure solve and the
// rendering unchanged, so it is an upgrade rather than a rewrite.
public partial class Bubbles3D : Node3D
{
    private static readonly Vector3I Grid = new(64, 64, 64);
    private const float WorldSize = 4.0f;

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
    private float _gravity = 9.0f;      // downward pull on the LIQUID (negative buoy)
    private float _iso = 0.5f;
    private float _viscosity = 0.999f;  // velocity dissipation
    private float _liquidKeep = 1.0f;   // dye dissipation — 1.0 = conserve the liquid
    private int _iters = 24;
    private int _bubbleCount = 5;
    private float _bubbleR = 5.0f;
    private float _fillLevel = 0.85f;   // fraction of the box that starts as water
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
        SeedLiquid();
    }

    // Water up to _fillLevel, with _bubbleCount spherical air pockets punched out of it.
    private void SeedLiquid()
    {
        if (_fluid is not { Ready: true }) { return; }
        var f = new float[Grid.X * Grid.Y * Grid.Z];
        var rng = new RandomNumberGenerator { Seed = 12345 };
        float top = Grid.Y * _fillLevel;

        var cx = new float[_bubbleCount];
        var cy = new float[_bubbleCount];
        var cz = new float[_bubbleCount];
        for (int b = 0; b < _bubbleCount; b++)
        {
            cx[b] = rng.RandfRange(Grid.X * 0.25f, Grid.X * 0.75f);
            cy[b] = rng.RandfRange(Grid.Y * 0.10f, Grid.Y * 0.45f);
            cz[b] = rng.RandfRange(Grid.Z * 0.25f, Grid.Z * 0.75f);
        }

        for (int z = 0; z < Grid.Z; z++)
        for (int y = 0; y < Grid.Y; y++)
        for (int x = 0; x < Grid.X; x++)
        {
            float v = y < top ? 1.0f : 0.0f;
            for (int b = 0; b < _bubbleCount; b++)
            {
                float dx = x - cx[b], dy = y - cy[b], dz = z - cz[b];
                if (dx * dx + dy * dy + dz * dz < _bubbleR * _bubbleR) { v = 0.0f; }
            }
            f[x + Grid.X * (y + Grid.Y * z)] = v;
        }
        _fluid.WriteDensity(f);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 50.0f, Position = new Vector3(0, 1.0f, 7.0f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(Vector3.Zero, Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-50, -35, 0),
            LightColor = new Color(1.0f, 0.97f, 0.92f),
            LightEnergy = 1.5f,
        });

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.22f, 0.36f, 0.6f),
            SkyHorizonColor = new Color(0.65f, 0.75f, 0.86f),
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

    // NO ISOSURFACE. Marching tets and raymarching both render the same thing — the isosurface
    // of a coarse volume — and it reads as faceted goo either way, because at 64^3 a bubble is
    // about four cells across. Resolution is the cause, not the renderer.
    //
    // Instead: splat only the cells ON the surface as small overlapping spheres. Overlapping
    // splats merge into a mass without anyone computing a surface, so there is no facet
    // structure to see. This is the front half of screen-space fluid rendering (van der Laan et
    // al. 2009); the back half — blurring depth and rebuilding normals from it — is the upgrade
    // if this still reads as lumpy.
    //
    // Interior cells are skipped: a liquid cell with six liquid neighbours can never be seen, so
    // emitting it costs an instance for nothing. That is what keeps this to a few thousand
    // splats instead of ~200k.
    private void BuildSurface()
    {
        float s = WorldSize / Grid.X;
        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            Mesh = new SphereMesh { Radius = 0.85f, Height = 1.7f, RadialSegments = 6, Rings = 3 },
            InstanceCount = 0,
        };
        // Slightly flat, slightly shiny — not glass, not goo.
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

    // A cell is on the surface if it is liquid and at least one 6-neighbour is not.
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
        // buoy is NEGATIVE: f3_add_source pushes `dt*buoy*d` into +y, and d is the LIQUID, so a
        // negative coefficient is gravity on the water. Air (d = 0) gets nothing and rises.
        // 16 floats, not 14: f3_add_source's block is 56 bytes of members but std430 rounds the
        // struct up to the vec4 alignment, so the pipeline demands 64.
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f,   // size
            dt, -_gravity,                 // dt, buoy
            0f, 0f, 0f, 1f,                // source centre + radius (unused: no injection)
            0f, 0f, 0f,                    // injected velocity
            0f,                            // dye_amt
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

        // Surface cells scale as the AREA of the interface, so a generous fraction of one grid
        // face is plenty of headroom; BuildSplats stops early rather than overrunning.
        int cap = Grid.X * Grid.Y * 6;
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
            _readout.Text = $"bubbles · {_bubbleCount} seeded · {_tris} splats · liquid volume {vol:0} cells\n"
                + $"{Engine.GetFramesPerSecond():0} fps · {Grid.X}³ · {_iters} pressure iters";
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
        var ui = new DemoUI(this, "26 · bubbles — air rising through water (3D free surface)",
            "Air pockets in a tank of water. The dye field is reinterpreted as LIQUID FRACTION "
            + "(1 = water, 0 = air) and its 0.5 isosurface is marching-tets'd into the surface you "
            + "see — so a bubble is just a hole in the field, which is exactly what a heightfield "
            + "cannot express. Gravity is negative buoyancy on the liquid; the air rises because "
            + "nothing pulls it down. Watch for the interface going milky: an advected scalar "
            + "diffuses, and that is the known limit of this approach.");
        _readout = ui.AddReadout("bubbles —");
        ui.AddToggle("Pause", _paused, v => _paused = v);
        ui.AddSlider("Gravity (pull on liquid)", 0.0f, 30.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Isolevel (surface)", 0.15f, 0.85f, _iso, v => _iso = v);
        ui.AddSlider("Pressure iters", 4, 80, _iters, v => _iters = (int)v);
        ui.AddSlider("Viscosity (vel keep)", 0.95f, 1.0f, _viscosity, v => _viscosity = v);
        ui.AddSlider("Liquid keep (1 = conserve)", 0.98f, 1.0f, _liquidKeep, v => _liquidKeep = v);
        ui.AddSlider("Bubble count", 1, 12, _bubbleCount, v => _bubbleCount = (int)v);
        ui.AddSlider("Bubble radius (cells)", 2.0f, 12.0f, _bubbleR, v => _bubbleR = v);
        ui.AddSlider("Fill level", 0.4f, 1.0f, _fillLevel, v => _fillLevel = v);
        // Splat size is the look knob: below ~0.8 the splats separate into beads, above ~1.2
        // they merge into a smooth mass and lose all detail.
        ui.AddSlider("Splat size (cells)", 0.5f, 2.0f, 0.85f,
            v => _mm.Mesh = new SphereMesh { Radius = v, Height = v * 2f, RadialSegments = 6, Rings = 3 });
        ui.AddSlider("Shine (roughness)", 0.02f, 0.6f, 0.14f, v => _mat.SetShaderParameter("rough", v));
        ui.AddSlider("Ripple detail", 0.0f, 1.2f, 0.35f, v => _mat.SetShaderParameter("detail_gain", v));
        ui.AddSlider("Ripple frequency", 2.0f, 24.0f, 9.0f, v => _mat.SetShaderParameter("detail_freq", v));
        ui.AddToggle("RESEED (flip to respawn bubbles)", false,
            _ => RenderingServer.CallOnRenderThread(Callable.From(SeedLiquid)));
    }
}
