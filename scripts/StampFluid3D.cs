using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 09 — Phase 4b: 3D pressure-projection fluid, shown as GPU-instanced particles
// (no ray tracing). FluidSim3D runs a full 3D Stam sim on a 48³ grid — the stamper does
// the 3D Poisson pressure projection each tick. The velocity volume is read back and
// used to CPU-advect thousands of MultiMesh billboard particles, colored by speed →
// swirling 3D vortices. A buoyant dye source drives a turbulent plume.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/09_fluid3d.tscn 8 1280x900
public partial class StampFluid3D : Node3D
{
    private static readonly Vector3I Grid = new(48, 48, 48);
    private const float WorldSize = 4.6f;
    private const int NParticles = 12000;

    private Camera3D _cam = null!;
    private MultiMesh _mm = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private volatile float[]? _vel;
    private int _tick;

    private readonly Vector3[] _pos = new Vector3[NParticles];
    private readonly float[] _age = new float[NParticles];
    private readonly System.Random _rng = new(20260801);

    // sim tunables
    private float _dt = 1.0f;
    private int _iters = 30;
    private float _buoy = 4.0f;
    private float _dyeAmt = 0.4f;
    private float _srcRadius = 6.0f;
    private float _particleScale = 1.0f;
    private const float MaxAge = 4.0f;

    private static readonly Vector3 SrcCenter = new(24, 6, 24);   // bottom-centre plume root

    public override void _Ready()
    {
        BuildEnvironment();
        BuildParticles();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid);
        _fluid.EnableMultigrid();   // built up front so the toggle is free at runtime
        ReadCmdline();
    }

    // `mg=1` after the shoot harness's args forces the multigrid pressure path on, so an A/B
    // screenshot is a command-line flag instead of an edit to the committed default. Flipping
    // that default by hand to take a comparison shot is how it ended up committed as `true`.
    private bool _useMg;

    private void ReadCmdline()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a == "mg=1") { _useMg = true; }
        }
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 55.0f, Position = new Vector3(0, 0.4f, 6.6f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, 0.2f, 0), Vector3.Up);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.03f, 0.06f),
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildParticles()
    {
        var quad = new QuadMesh { Size = new Vector2(0.05f, 0.05f) };
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            VertexColorUseAsAlbedo = true,
            DisableReceiveShadows = true,
            AlbedoTexture = MakeDotTexture(),   // soft round puff, not a hard square
        };

        _mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = quad,
            InstanceCount = NParticles,
        };
        for (int i = 0; i < NParticles; i++)
        {
            _pos[i] = Respawn();
            _age[i] = (float)_rng.NextDouble() * MaxAge;
        }
        AddChild(new MultiMeshInstance3D { Multimesh = _mm });
    }

    private static ImageTexture MakeDotTexture()
    {
        const int N = 32;
        var img = Image.CreateEmpty(N, N, false, Image.Format.Rgba8);
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            float dx = (x + 0.5f) / N - 0.5f, dy = (y + 0.5f) / N - 0.5f;
            float d = Mathf.Sqrt(dx * dx + dy * dy) * 2.0f;   // 0 centre → 1 edge
            float a = Mathf.Clamp(1.0f - d, 0f, 1f);
            a = a * a;                                        // soft falloff
            img.SetPixel(x, y, new Color(1, 1, 1, a));
        }
        return ImageTexture.CreateFromImage(img);
    }

    private Vector3 Respawn()
    {
        Vector3 j = new((float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f, (float)_rng.NextDouble() - 0.5f);
        return SrcCenter + j * (2.0f * _srcRadius);
    }

    private Vector3 ToWorld(Vector3 g)
    {
        float s = WorldSize / Grid.X;
        return g * s - new Vector3(WorldSize, WorldSize, WorldSize) * 0.5f;
    }

    public override void _PhysicsProcess(double delta)
    {
        float t = _tick * 0.05f;
        float ivx = 0.5f * Mathf.Sin(t), ivz = 0.5f * Mathf.Cos(t * 1.3f);
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f, _dt, _buoy,
            SrcCenter.X, SrcCenter.Y, SrcCenter.Z, _srcRadius, ivx, 0.6f, ivz, _dyeAmt, 0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.999f, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, 0f, 0f, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.99f, 0f, 0f };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseMultigrid = _useMg;
            _fluid.Step(addB, advVB, simB, advDB, iters, measureResidual: _tick % 30 == 0);
            _vel = _fluid.ReadVelocity();
        }));
        _tick++;
    }

    public override void _Process(double delta)
    {
        var vel = _vel;
        float dt = (float)delta;
        for (int i = 0; i < NParticles; i++)
        {
            Vector3 p = _pos[i];
            Vector3 v = vel != null ? SampleVel(vel, p) : Vector3.Zero;
            p += v * (_dt * 0.6f);
            _age[i] += dt;
            bool oob = p.X < 1 || p.Y < 1 || p.Z < 1 || p.X > Grid.X - 2 || p.Y > Grid.Y - 2 || p.Z > Grid.Z - 2;
            if (oob || _age[i] > MaxAge) { p = Respawn(); _age[i] = 0f; }
            _pos[i] = p;

            // warm plume palette by age: hot young core (base) → cooling embers (top/edges)
            float life = Mathf.Clamp(_age[i] / MaxAge, 0f, 1f);
            var hot = new Color(1.0f, 0.85f, 0.45f);
            var mid = new Color(1.0f, 0.4f, 0.12f);
            var cool = new Color(0.5f, 0.09f, 0.03f);
            Color rgb = life < 0.5f ? hot.Lerp(mid, life * 2f) : mid.Lerp(cool, (life - 0.5f) * 2f);
            var col = new Color(rgb.R, rgb.G, rgb.B, 0.12f * (1.0f - life) + 0.02f);
            _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity.Scaled(new Vector3(_particleScale, _particleScale, _particleScale)), ToWorld(p)));
            _mm.SetInstanceColor(i, col);
        }

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            var f = _fluid;
            string solver = _useMg
                ? $"MgDeep3D ({(f?.MultigridLevels ?? 0)} lvl)"
                : "Jacobi";
            string res = f is { UseMultigrid: true } ? $" · res {f.LastResidual:0.000e+00}" : "";
            _readout.Text = $"3D fluid · {Grid.X}³ · pressure: {solver} · {_iters} iters{res}\n"
                + $"{NParticles} particles · {Engine.GetFramesPerSecond():0} fps";
        }
    }

    private Vector3 SampleVel(float[] vel, Vector3 p)
    {
        int W = Grid.X, H = Grid.Y, D = Grid.Z;
        float x = Mathf.Clamp(p.X, 0, W - 1.001f), y = Mathf.Clamp(p.Y, 0, H - 1.001f), z = Mathf.Clamp(p.Z, 0, D - 1.001f);
        int x0 = (int)x, y0 = (int)y, z0 = (int)z, x1 = x0 + 1, y1 = y0 + 1, z1 = z0 + 1;
        float fx = x - x0, fy = y - y0, fz = z - z0;
        Vector3 C(int xi, int yi, int zi)
        {
            int o = (xi + W * (yi + H * zi)) * 4;
            return new Vector3(vel[o], vel[o + 1], vel[o + 2]);
        }
        Vector3 c00 = C(x0, y0, z0).Lerp(C(x1, y0, z0), fx), c10 = C(x0, y1, z0).Lerp(C(x1, y1, z0), fx);
        Vector3 c01 = C(x0, y0, z1).Lerp(C(x1, y0, z1), fx), c11 = C(x0, y1, z1).Lerp(C(x1, y1, z1), fx);
        return c00.Lerp(c10, fy).Lerp(c01.Lerp(c11, fy), fz);
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    public override void _ExitTree() => RenderingServer.CallOnRenderThread(Callable.From(() => _fluid?.Free()));

    private void BuildUi()
    {
        var ui = new DemoUI(this, "09 · 3D fluid → GPU particles (C#)",
            "Phase 4 — the pressure-projection fluid in a VOLUME, shown as particles (no ray tracing). "
            + "FluidSim3D runs a full 3D Stam sim on 48³; the stamper does the 3D Poisson pressure "
            + "projection each tick. The velocity volume is read back and CPU-advects thousands of "
            + "MultiMesh billboard particles, coloured by speed → swirling 3D vortices from a buoyant "
            + "plume. Same solver as scene 07, one dimension up.");
        _readout = ui.AddReadout("3D fluid —");
        // The whole point of scene 09 today: the pressure projection is the only real linear
        // solve in the fluid, and this flips which solver does it. Jacobi is the original.
        ui.AddToggle("Pressure: MgDeep3D (off = Jacobi)", false, v => _useMg = v);
        ui.AddSlider("Buoyancy", 0.0f, 8.0f, _buoy, v => _buoy = v);
        ui.AddSlider("Dye amount", 0.0f, 1.0f, _dyeAmt, v => _dyeAmt = v);
        ui.AddSlider("Source radius", 3.0f, 14.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pressure iters", 4, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Advect dt", 0.2f, 2.0f, _dt, v => _dt = v);
        ui.AddSlider("Particle size", 0.4f, 3.0f, _particleScale, v => _particleScale = v);
    }
}
