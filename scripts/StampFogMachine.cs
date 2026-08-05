using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 12 — a movable FOG MACHINE. The FluidSim3D smoke is emitted from a little
// machine prop resting on the floor; the machine + its glowing-ember particles + a
// wireframe outline of the sim box all live under one movable "rig" node. The sim runs
// in its fixed 48³ grid; the rig transform just places that volume in the world, so
// dragging the rig carries the whole fog box around the scene. Left-drag on the floor
// moves it; it gently auto-drifts when idle. Bigger world box (8u) than scene 11.
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/12_fog_machine.tscn 9 1280x900
public partial class StampFogMachine : Node3D
{
    private static readonly Vector3I Grid = new(48, 48, 48);
    private const float BoxSize = 8.0f;
    private const int NParticles = 9000;

    private Camera3D _cam = null!;
    private Node3D _rig = null!;
    private MultiMesh _mm = null!;
    private FluidSim3D? _fluid;
    private Label? _readout;
    private volatile float[]? _vel;
    private int _tick;
    private Vector3 _rigTarget = Vector3.Zero;

    private readonly Vector3[] _pos = new Vector3[NParticles];
    private readonly float[] _age = new float[NParticles];
    private readonly System.Random _rng = new(20260801);

    private float _dt = 1.0f;
    private int _iters = 26;
    private float _buoy = 2.0f;
    private float _wind = 0.3f;
    private float _dyeAmt = 0.4f;
    private float _srcRadius = 5.0f;
    private const float MaxAge = 5.0f;

    private static readonly Vector3 SrcCenter = new(24, 3, 24);   // the machine's vent

    public override void _Ready()
    {
        BuildEnvironment();
        BuildRig();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim() => _fluid = new FluidSim3D(RenderingServer.GetRenderingDevice(), Grid);

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 52.0f, Position = new Vector3(8.5f, 4.6f, 11.5f), Far = 400.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0, 2.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-30, 40, 0),
            LightColor = new Color(1.0f, 0.72f, 0.5f),
            LightEnergy = 1.4f,
            ShadowEnabled = true,
        });

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.025f, 0.035f),
            AmbientLightColor = new Color(0.3f, 0.34f, 0.45f),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightEnergy = 0.4f,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        AddChild(new WorldEnvironment { Environment = env });

        AddChild(new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(80, 80) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.085f, 0.1f), Roughness = 0.7f, Metallic = 0.1f },
        });
    }

    private void BuildRig()
    {
        _rig = new Node3D();
        AddChild(_rig);

        // machine body + glowing vent
        _rig.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.8f, 0.34f, 0.55f) },
            Position = new Vector3(0, 0.17f, 0),
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.07f, 0.08f), Roughness = 0.5f, Metallic = 0.4f },
        });
        _rig.AddChild(new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.42f, 0.05f, 0.32f) },
            Position = new Vector3(0, 0.36f, 0),
            MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = new Color(0.6f, 0.85f, 1.0f),
                EmissionEnabled = true, Emission = new Color(0.4f, 0.7f, 1.0f), EmissionEnergyMultiplier = 3.0f,
            },
        });
        _rig.AddChild(new MeshInstance3D { Mesh = BuildBoxLines(), MaterialOverride = LineMat() });

        var quad = new QuadMesh { Size = new Vector2(0.04f, 0.04f) };
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
        _mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = quad, InstanceCount = NParticles };
        for (int i = 0; i < NParticles; i++) { _pos[i] = Respawn(); _age[i] = (float)_rng.NextDouble() * MaxAge; }
        _rig.AddChild(new MultiMeshInstance3D { Multimesh = _mm });
    }

    private ArrayMesh BuildBoxLines()
    {
        float h = BoxSize * 0.5f, b = BoxSize;
        Vector3[] c =
        {
            new(-h, 0, -h), new(h, 0, -h), new(h, 0, h), new(-h, 0, h),
            new(-h, b, -h), new(h, b, -h), new(h, b, h), new(-h, b, h),
        };
        int[,] e = { {0,1},{1,2},{2,3},{3,0}, {4,5},{5,6},{6,7},{7,4}, {0,4},{1,5},{2,6},{3,7} };
        var verts = new Vector3[24];
        for (int i = 0; i < 12; i++) { verts[i * 2] = c[e[i, 0]]; verts[i * 2 + 1] = c[e[i, 1]]; }
        var arr = new Godot.Collections.Array();
        arr.Resize((int)Mesh.ArrayType.Max);
        arr[(int)Mesh.ArrayType.Vertex] = verts;
        var m = new ArrayMesh();
        m.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arr);
        return m;
    }

    private static StandardMaterial3D LineMat() => new()
    {
        ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        AlbedoColor = new Color(0.3f, 0.6f, 0.8f, 0.5f),
        Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
    };

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

    private Vector3 ToLocal(Vector3 g)
    {
        float s = BoxSize / Grid.X;
        return new Vector3(g.X * s - BoxSize * 0.5f, g.Y * s, g.Z * s - BoxSize * 0.5f);
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
        MoveRig((float)delta);

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
            var near = new Color(0.7f, 0.85f, 1.0f);      // cool theatrical fog, bright at the vent
            var far = new Color(0.35f, 0.42f, 0.6f);
            Color rgb = near.Lerp(far, life);
            var col = new Color(rgb.R, rgb.G, rgb.B, 0.11f * (1.0f - life) + 0.02f);
            _mm.SetInstanceTransform(i, new Transform3D(Basis.Identity, ToLocal(p)));
            _mm.SetInstanceColor(i, col);
        }

        _tick++;
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"fog machine · {NParticles} · box {BoxSize:0}u @ ({_rig.Position.X:0.0},{_rig.Position.Z:0.0}) · {Engine.GetFramesPerSecond():0}fps";
        }
    }

    private void MoveRig(float delta)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var mp = GetViewport().GetMousePosition();
            var floor = new Plane(Vector3.Up, 0.0f);
            if (floor.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is Vector3 hit)
            {
                _rigTarget = new Vector3(Mathf.Clamp(hit.X, -12, 12), 0, Mathf.Clamp(hit.Z, -12, 12));
            }
        }
        else
        {
            float t = _tick * 0.012f;                    // gentle idle auto-drift for the shot
            _rigTarget = new Vector3(3.0f * Mathf.Sin(t), 0, 2.0f * Mathf.Cos(t * 0.9f));
        }
        _rig.Position = _rig.Position.Lerp(_rigTarget, Mathf.Clamp(delta * 2.5f, 0f, 1f));
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
        var ui = new DemoUI(this, "12 · Movable fog machine (C#)",
            "A fog machine you can drag around the scene. The FluidSim3D smoke is emitted from a little "
            + "machine on the floor; the machine, its particles, and a wireframe outline of the 8u sim box "
            + "all live under one movable rig. The sim runs in its fixed 48³ grid — the rig transform just "
            + "places that volume in the world, so dragging it carries the whole fog box along. Left-drag "
            + "on the floor to move it; it gently auto-drifts when idle.");
        _readout = ui.AddReadout("fog machine —");
        ui.AddSlider("Buoyancy", 0.0f, 6.0f, _buoy, v => _buoy = v);
        ui.AddSlider("Wind", 0.0f, 1.5f, _wind, v => _wind = v);
        ui.AddSlider("Dye amount", 0.0f, 1.0f, _dyeAmt, v => _dyeAmt = v);
        ui.AddSlider("Source radius", 3.0f, 12.0f, _srcRadius, v => _srcRadius = v);
        ui.AddSlider("Pressure iters", 4, 60, _iters, v => _iters = (int)v);
    }
}
