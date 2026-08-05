using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 11 — the scene-09 particle fluid dropped into a real environment so the smoke
// drifts believably in a world: dark dusk sky, a dark ground, a low warm sun for rim,
// and a gentle varying breeze at the source so the plume wafts and curls instead of
// jetting up. FluidSim3D does the sim (the stamper's 3D pressure projection); the
// velocity volume is read back and CPU-advects glowing MultiMesh embers that float
// against the sky. The smoke lives in the fixed 48³ sim box sitting on the ground; the
// box is embedded in a much bigger world → reads as smoke floating in a place.
// (Chose particles over the FogVolume here — froxel volumetrics read too soft/low-
//  contrast in an open lit scene; the particles pop.)
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/11_smoke_world.tscn 8 1280x900
public partial class StampSmokeWorld : Node3D
{
    private static readonly Vector3I Grid = new(48, 48, 48);
    private const float BoxSize = 5.5f;                 // world size of the sim box
    private const int NParticles = 14000;

    private Camera3D _cam = null!;
    private MultiMesh _mm = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private volatile float[]? _vel;
    private int _tick;

    private readonly Vector3[] _pos = new Vector3[NParticles];
    private readonly float[] _age = new float[NParticles];
    private readonly System.Random _rng = new(20260801);

    private float _dt = 1.0f;
    private int _iters = 28;
    private float _buoy = 2.6f;
    private float _wind = 0.4f;
    private float _dyeAmt = 0.4f;
    private float _srcRadius = 5.0f;
    private const float MaxAge = 4.5f;

    private static readonly Vector3 SrcCenter = new(24, 5, 24);   // near the box floor

    public override void _Ready()
    {
        BuildEnvironment();
        BuildParticles();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim() => _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid);

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 52.0f, Position = new Vector3(6.2f, 3.2f, 8.4f), Far = 400.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, 2.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-14, 54, 0),
            LightColor = new Color(1.0f, 0.7f, 0.45f),
            LightEnergy = 1.6f,
            ShadowEnabled = true,
        });

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.03f, 0.05f, 0.11f),       // deep dusk → embers pop
            SkyHorizonColor = new Color(0.30f, 0.20f, 0.22f),
            GroundHorizonColor = new Color(0.12f, 0.10f, 0.12f),
            GroundBottomColor = new Color(0.03f, 0.03f, 0.04f),
        };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Sky { SkyMaterial = skyMat },
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            AmbientLightEnergy = 0.35f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });

        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(80, 80) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.10f, 0.10f, 0.12f), Roughness = 1.0f },
        });
    }

    private void BuildParticles()
    {
        var quad = new QuadMesh { Size = new Vector2(0.055f, 0.055f) };
        quad.Material = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            BlendMode = BaseMaterial3D.BlendModeEnum.Add,
            BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled,
            VertexColorUseAsAlbedo = true,
            DisableReceiveShadows = true,
            AlbedoTexture = MakeDotTexture(),
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
            float a = Mathf.Clamp(1.0f - Mathf.Sqrt(dx * dx + dy * dy) * 2.0f, 0f, 1f);
            img.SetPixel(x, y, new Color(1, 1, 1, a * a));
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
        float s = BoxSize / Grid.X;
        return new Vector3(g.X * s - BoxSize * 0.5f, g.Y * s, g.Z * s - BoxSize * 0.5f);   // bottom on the ground
    }

    public override void _PhysicsProcess(double delta)
    {
        float t = _tick * 0.04f;
        float wx = _wind * (0.7f + 0.3f * Mathf.Sin(t)), wz = _wind * 0.4f * Mathf.Cos(t * 0.8f);
        float[] add =
        {
            Grid.X, Grid.Y, Grid.Z, 0f, _dt, _buoy,
            SrcCenter.X, SrcCenter.Y, SrcCenter.Z, _srcRadius, wx, 0.5f, wz, _dyeAmt, 0f, 0f,
        };
        float[] advV = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.999f, 0f, 0f };
        float[] sim = { Grid.X, Grid.Y, Grid.Z, 0f, 0f, 0f, 0f, 0f };
        float[] advD = { Grid.X, Grid.Y, Grid.Z, 0f, _dt, 0.985f, 0f, 0f };
        byte[] addB = ToBytes(add), advVB = ToBytes(advV), simB = ToBytes(sim), advDB = ToBytes(advD);
        int iters = _iters;
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.Step(addB, advVB, simB, advDB, iters);
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

            float life = Mathf.Clamp(_age[i] / MaxAge, 0f, 1f);
            var hot = new Color(1.0f, 0.85f, 0.5f);
            var mid = new Color(1.0f, 0.4f, 0.14f);
            var cool = new Color(0.35f, 0.09f, 0.06f);
            Color rgb = life < 0.5f ? hot.Lerp(mid, life * 2f) : mid.Lerp(cool, (life - 0.5f) * 2f);
            var col = new Color(rgb.R, rgb.G, rgb.B, 0.14f * (1.0f - life) + 0.02f);
            _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, ToWorld(p)));
            _mm.SetInstanceColor(i, col);
        }

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"smoke-in-world · {NParticles} embers · {Grid.X}³ box on the ground · {Engine.GetFramesPerSecond():0}fps";
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
        var ui = new DemoUI(this, "11 · Smoke drifting in a world (C#)",
            "The scene-09 particle fluid dropped into a real environment so the smoke floats in a place: "
            + "dark dusk sky, dark ground, a low warm sun, and a gentle varying breeze at the source so the "
            + "plume wafts and curls. FluidSim3D runs the sim (the stamper's 3D pressure projection); the "
            + "velocity volume is read back and CPU-advects glowing embers against the sky. The smoke lives "
            + "in the fixed 48³ box on the ground, embedded in a much bigger world. (Particles, not the "
            + "FogVolume — froxel fog reads too soft/low-contrast in an open lit scene.)");
        _readout = ui.AddReadout("smoke —");
        ui.AddSlider("Buoyancy", 0.0f, 6.0f, _buoy, v => _buoy = v);
        ui.AddSlider("Wind", 0.0f, 1.5f, _wind, v => _wind = v);
        ui.AddSlider("Dye amount", 0.0f, 1.0f, _dyeAmt, v => _dyeAmt = v);
        ui.AddSlider("Source radius", 3.0f, 12.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pressure iters", 4, 60, _iters, v => _iters = (int)v);
        ui.AddSlider("Advect dt", 0.2f, 2.0f, _dt, v => _dt = v);
    }
}
