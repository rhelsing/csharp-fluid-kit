using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 27 — Curl: free-surface water in a volume, pressure-projected by MgDeepSolver3D.
//
// This is the scene the whole 3D solver port was for. A shallow-water heightfield is
// y = h(x,z) — single-valued, so an overturning lip is unrepresentable no matter how good
// the solver. Here the water is a level set in a 3D velocity field and the crest can throw
// forward over the trough. The thing that drives it over is incompressibility, i.e. the
// pressure Poisson solve, which is the linear solve KP07 does not have (solver-ledger.md §10).
//
// THREE TESTS, IN ORDER, ALL VISIBLE FROM ONE SCENE:
//   1. PROJECTION — does the solve work at all? Residual on the readout, and the water does
//      not explode. Verified previously in the bench and in scene 09's plume.
//   2. LAKE AT REST — wavemaker off, flat bed. The water must settle flat and STAY flat, and
//      the water fraction must hold. path-b §6 names well-balancing as one of the two things
//      that cannot be faked; a wrong free-surface BC shows up here as water that either sags
//      forever or slowly inflates.
//   3. SHOALING — wavemaker on, sloped bed. The crest steepens as depth drops. Then it curls.
//
// Verify (render, then Read the PNG):
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/27_curl.tscn 8 1200x800
public partial class Curl3D : Node3D
{
    // x = across-shore (0 = deep, the wavemaker edge), y = up, z = along-shore.
    // Anisotropic on purpose: a wave needs run-up length in x far more than width in z, and
    // every axis must stay even for the multigrid pyramid to coarsen.
    private static readonly Vector3I Grid = new(128, 48, 32);

    private const float BoxW = 12.0f;   // world metres across-shore
    // 1 = fine grid only. Isolates whether the coarse levels are what diverges.
    private const int MaxLevels = 1;
    private const string ShaderDir = "res://shaders/wave3d/";

    private FreeCam _cam = null!;
    private WaveSim3D? _sim;
    private Texture3Drd _phiTex = null!;
    private Texture3Drd _maskTex = null!;
    private ShaderMaterial _mat = null!;
    private Label? _readout;

    // live controls
    private bool _running = true;
    private bool _slope = true;
    private bool _wave = true;
    private float _dt = 0.12f;
    // CELLS/s², and the CFL that matters is |v|·dt in CELLS PER STEP. At dt 0.6 / g -9 the
    // first step already moved phi 3.2 cells, which semi-Lagrangian advection turns to mush in
    // a handful of steps — the water "drained" and the zero residual was just the aftermath.
    // dt 0.12 / g -3 keeps it well under a cell per step.
    private float _gravity = -3.0f;
    private float _waveAmp = 2.0f;
    private float _wavePeriod = 2.2f;
    private int _iters = 24;

    private float _time;
    private int _tick;
    private float _fill;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildBox();
        RenderingServer.CallOnRenderThread(Callable.From(InitSim));
        BuildUi();
    }

    private void InitSim()
    {
        var s = new WaveSim3D(RenderingServer.GetRenderingDevice(), Grid, MaxLevels);
        if (!s.Ready) { return; }
        s.Init(BuildPhi(), BuildBed());
        _sim = s;
    }

    // Bed height in cells per (x,z) column. Flat for the lake-at-rest test; a 1:6 ramp rising
    // toward +x for shoaling, so depth falls from ~24 cells to nothing across the box.
    private float[] BuildBed()
    {
        var bed = new float[Grid.X * Grid.Z];
        for (int z = 0; z < Grid.Z; z++)
        {
            for (int x = 0; x < Grid.X; x++)
            {
                float t = (float)x / (Grid.X - 1);
                bed[z * Grid.X + x] = _slope ? Mathf.Max(0.0f, (t - 0.35f) * Grid.Y * 0.85f) : 0.0f;
            }
        }
        return bed;
    }

    // Still water to a flat level. phi > 0.5 = water. Starting FLAT (not perturbed) is
    // deliberate: test 2 is that it stays that way.
    private float[] BuildPhi()
    {
        float level = Grid.Y * 0.45f;
        var phi = new float[Grid.X * Grid.Y * Grid.Z];
        for (int z = 0; z < Grid.Z; z++)
        {
            for (int y = 0; y < Grid.Y; y++)
            {
                for (int x = 0; x < Grid.X; x++)
                {
                    phi[(z * Grid.Y + y) * Grid.X + x] = y < level ? 1.0f : 0.0f;
                }
            }
        }
        return phi;
    }

    private void BuildEnvironment()
    {
        _cam = new FreeCam { Fov = 60.0f, Far = 500.0f, Current = true, Speed = 6.0f };
        AddChild(_cam);
        // framed for the 12 x 4.5 x 3 box: off the shoulder of the shallow (+x) end, looking
        // back down the tank toward the wavemaker
        _cam.LookAtFromPosition(new Vector3(10.5f, 3.2f, 6.5f), new Vector3(-1.0f, -0.4f, 0.0f), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-45.0f, 135.0f, 0.0f),
            LightEnergy = 1.2f,
        });
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.28f, 0.48f, 0.78f),
            SkyHorizonColor = new Color(0.72f, 0.80f, 0.88f),
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
            },
        });
    }

    private void BuildBox()
    {
        _phiTex = new Texture3Drd();
        _maskTex = new Texture3Drd();
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "w3_surface.gdshader") };
        _mat.SetShaderParameter("phi_tex", _phiTex);
        _mat.SetShaderParameter("mask_tex", _maskTex);

        // A UNIT cube, scaled by the node — NOT a sized BoxMesh. The raymarch maps local
        // space to [0,1]^3 with `VERTEX + 0.5`, which is only true for a unit cube; a sized
        // mesh puts local coords at +-6 and the slab test misses the volume entirely.
        float sy = BoxW * Grid.Y / Grid.X;
        float sz = BoxW * Grid.Z / Grid.X;
        AddChild(new MeshInstance3D
        {
            Name = "WaterBox",
            Mesh = new BoxMesh { Size = Vector3.One },
            Scale = new Vector3(BoxW, sy, sz),
            MaterialOverride = _mat,
        });
    }

    public override void _Process(double delta)
    {
        var sim = _sim;
        if (sim is not { Ready: true }) { return; }
        _phiTex.TextureRdRid = sim.PhiRid;
        _maskTex.TextureRdRid = sim.MaskRid;

        if (_running)
        {
            _time += _dt;   // sim time advances by the step, not by wall-clock
            float omega = Mathf.Tau / Mathf.Max(_wavePeriod, 0.01f);
            float amp = _wave ? _waveAmp : 0.0f;
            float dt = _dt;
            float g = _gravity;
            float t = _time;
            int iters = _iters;
            // Residual readback is a SYNCHRONOUS BufferGetData on the global device — a full
            // GPU stall. Once every 30 ticks, never per frame (that mistake cost scene 09 half
            // its framerate; solver-ledger.md §9a).
            bool measure = _tick % 30 == 0;
            RenderingServer.CallOnRenderThread(Callable.From(() =>
                sim.Step(dt, g, amp, omega, t, 4.0f, iters, measure)));
            _tick++;
        }

        if (_tick % 30 == 1) { _fill = sim.WaterFraction(); }
        if (_readout != null && _tick % 12 == 0)
        {
            _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · {Grid.X}×{Grid.Y}×{Grid.Z} · "
                + $"MgDeep3D {sim.Levels} lvl\nresidual {sim.LastResidual:0.000e+00} · water {_fill * 100.0f:0.0}%";
        }
    }

    public override void _ExitTree()
    {
        if (_phiTex != null) { _phiTex.TextureRdRid = default; }
        if (_maskTex != null) { _maskTex.TextureRdRid = default; }
        var s = _sim;
        _sim = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void Reinit()
    {
        var s = _sim;
        if (s == null) { return; }
        float[] phi = BuildPhi();
        float[] bed = BuildBed();
        RenderingServer.CallOnRenderThread(Callable.From(() => s.Init(phi, bed)));
        _time = 0.0f;
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "27 · Curl — free-surface water, projected by MgDeep3D",
            "The scene the 3D solver port was for. Water as a LEVEL SET in a 3D velocity field, "
            + "not a heightfield — so the crest can overturn. Incompressibility is what throws the "
            + "lip forward, and that is the pressure Poisson solve MgDeep3D does. "
            + "TEST ORDER: turn the wavemaker OFF and the slope OFF — the water must settle flat "
            + "and stay flat, and the water %% must hold (lake at rest / well-balancing). Then "
            + "turn both on for shoaling. Free cam: RMB look · WASD · Q/E · Shift.");
        _readout = ui.AddReadout("— fps");

        ui.AddSection("Tests");
        ui.AddToggle("Run", _running, v => _running = v);
        ui.AddToggle("Wavemaker", _wave, v => _wave = v);
        ui.AddToggle("Sloped bed (off = flat, for lake-at-rest)", _slope, v => { _slope = v; Reinit(); });
        ui.AddToggle("Reset water", false, _ => Reinit());

        ui.AddSection("Sim");
        ui.AddSlider("Timestep", 0.02f, 0.5f, _dt, v => _dt = v);
        ui.AddSlider("Gravity", -12.0f, 0.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Wave amp", 0.0f, 8.0f, _waveAmp, v => _waveAmp = v);
        ui.AddSlider("Wave period (s)", 0.6f, 6.0f, _wavePeriod, v => _wavePeriod = v);
        ui.AddSlider("Pressure iters", 6, 48, _iters, v => _iters = (int)v);

        ui.AddSection("Render");
        ui.AddSlider("Raymarch steps", 24, 224, 96, v => _mat.SetShaderParameter("steps", (int)v));
    }
}
