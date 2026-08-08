using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 29 — Shore MOANA: hypothesis C. Same solver as 26, no meshes, raymarched.
//
// A fork of scene 26 (ShoreBase) that keeps the physics EXACTLY — same ShallowWaterKp, same
// ShoreScenario bed, same dt/dx/CFL — and throws away the rendering. There is no water plane
// and no terrain mesh; the whole frame is one fullscreen ColorRect running the scene-210
// Moana raymarch, with its analytic swell and analytic ground replaced by tx_state/tx_bottom.
//
// WHY. Hypothesis E carved an SDF barrel on top of a MESH ocean, and every failure it had came
// from that seam: a second water material, the layer repainting the whole sea, discard logic
// to contain it, foam that did not match, depth fights. None of those are about the carve. If
// the ocean IS the raymarch there is no seam — one representation, and a barrel becomes one
// more SDF term instead of a second pass arguing with the first.
//
// HONEST LIMIT: pos.y - surf(pos.xz) is still single-valued, so C on its own does not curl.
// What it buys is that the curl becomes a term rather than an architecture.
//
// Scene 26 stays the reference and is untouched. Shared read-only: ShallowWaterKp,
// ShoreScenario, Bathymetry. Forked because this scene edits it: moana_shore.gdshader.
//
// Verify (render, then Read the PNG):
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/29_shore_moana.tscn 12 1200x800
public partial class ShoreMoana : Node3D
{
    private const float Domain = ShoreScenario.Domain;   // 92.16 m
    private const int N = ShoreScenario.Grid;            // 768
    private const float Dx = ShoreScenario.Dx;           // 0.12 m
    private const float Dt = 0.003f;
    private const string ShaderDir = "res://shaders/shorewaves/";

    private FreeCam _cam = null!;
    private ShaderMaterial _mat = null!;
    private Texture2Drd _texState = null!;
    private Texture2Drd _texBottom = null!;

    private ShallowWaterKp? _solver;
    private byte[] _bottomBytes = System.Array.Empty<byte>();
    private byte[] _stateBytes = System.Array.Empty<byte>();

    private int _parity;
    private int _gparity;
    private float _accum;
    private float _simTime;
    private float _lastSimTime;
    private float _fpsAccum;
    private int _lastSteps;
    private float _simSpeed = 1.0f;
    private bool _running = true;
    private bool _fireSolitary;
    private Label? _readout;

    public override void _Ready()
    {
        float[] corners = ShoreScenario.BuildCorners();
        float[] bottomFloats = ShoreScenario.BuildBottomFloats(corners);
        _bottomBytes = Bathymetry.FloatsToBytes(bottomFloats);
        _stateBytes = Bathymetry.StateBytesFromBottom(bottomFloats);

        BuildCamera();
        BuildRaymarch();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new ShallowWaterKp(RenderingServer.GetRenderingDevice(), _bottomBytes, _stateBytes, N, Dx, Dt)
        {
            WaveMode = 2.0f,
            WaveDepth0 = ShoreScenario.ShelfDepth,
            WaveAmp = 0.585f,
            WavePeriod = 8.005f,
            WaveRamp = 6.0f,
            SolitaryH = 0.9f,
            SolitaryX0 = 8.0f,
            MaxSubsteps = 12,
        };
    }

    // The camera is not used to RENDER — the raymarch builds its own rays from cam_pos/yaw/
    // pitch. FreeCam is here purely to fly, and its transform is pushed to the shader.
    private void BuildCamera()
    {
        _cam = new FreeCam { Fov = 58.0f, Far = 6000.0f, Current = true, Speed = 20.0f };
        AddChild(_cam);
        // Framed to hold the WHOLE 92 m domain: back off along the diagonal and look at the
        // centre. At ~145 m with a 58 deg FOV the visible width is ~160 m against a 130 m
        // domain diagonal, so the entire shore, sandbar and offshore shelf are all in frame.
        _cam.LookAtFromPosition(new Vector3(138.0f, 86.0f, 124.0f), new Vector3(46.0f, 0.0f, 46.0f), Vector3.Up);
    }

    private void BuildRaymarch()
    {
        _texState = new Texture2Drd();
        _texBottom = new Texture2Drd();

        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(ShaderDir + "moana_shore.gdshader") };
        _mat.SetShaderParameter("tx_state", _texState);
        _mat.SetShaderParameter("tx_bottom", _texBottom);
        _mat.SetShaderParameter("DOMAIN_SIZE", Domain);
        _mat.SetShaderParameter("shade_mode", 0);   // cheap first: get the shape, then the look
        _mat.SetShaderParameter("cheap_steps", 144);   // whole-domain view needs more reach

        // canvas_item on a full-rect ColorRect, exactly as water-kit scene 210 mounts it —
        // no mesh, no depth buffer, no camera matrix. layer -1 so the DemoUI panel draws over.
        var rect = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore, Material = _mat };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var layer = new CanvasLayer { Layer = -1 };
        layer.AddChild(rect);
        AddChild(layer);
    }

    public override void _Process(double delta)
    {
        var solver = _solver;
        if (solver is not { Ready: true }) { return; }
        _texBottom.TextureRdRid = solver.BottomRid;

        PushCamera();
        StepSim((float)delta);
        UpdateReadout((float)delta);
    }

    // The oracle's ray basis is CameraView = (sin(yaw)cos(pitch), sin(pitch), cos(yaw)cos(pitch)),
    // so yaw/pitch come straight out of the Godot camera's forward vector.
    private void PushCamera()
    {
        Vector3 fwd = -_cam.GlobalTransform.Basis.Z;
        _mat.SetShaderParameter("cam_pos", _cam.GlobalPosition);
        _mat.SetShaderParameter("cam_pitch", Mathf.Asin(Mathf.Clamp(fwd.Y, -1.0f, 1.0f)));
        _mat.SetShaderParameter("cam_yaw", Mathf.Atan2(fwd.X, fwd.Z));
    }

    private void StepSim(float delta)
    {
        var solver = _solver!;
        if (!_running) { _accum = 0.0f; _lastSteps = 0; return; }

        _accum += Mathf.Min(delta, 0.1f) * _simSpeed;
        int steps = Mathf.Clamp((int)(_accum / solver.Dt), 0, solver.MaxSubsteps);
        _accum -= steps * solver.Dt;
        _lastSteps = steps;
        if (steps == 0) { return; }

        int finalParity = (_parity + steps) % 2;
        int finalGparity = _gparity ^ 1;
        _texState.TextureRdRid = solver.StateRids[finalParity];

        bool solitary = _fireSolitary;
        _fireSolitary = false;
        int parity = _parity, gparity = _gparity;
        float t0 = _simTime;
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.Step(steps, t0, parity, solitary, gparity)));

        _parity = finalParity;
        _gparity = finalGparity;
        _simTime += steps * solver.Dt;
    }

    private void UpdateReadout(float delta)
    {
        _fpsAccum += delta;
        if (_readout == null || _fpsAccum < 0.5f) { return; }
        float simRate = (_simTime - _lastSimTime) / _fpsAccum;
        _lastSimTime = _simTime;
        _fpsAccum = 0.0f;
        string dbg = _solver is { Ready: true }
            ? $"vol {_solver.DbgVolume:0} m³ · maxh {_solver.DbgMaxH:0.00} · nan {_solver.DbgNan:0}"
            : "solver init…";
        _readout.Text = $"{Engine.GetFramesPerSecond():0} fps · sim ×{simRate:0.00} · {_lastSteps} substeps\n"
            + $"{N}² @ dx {Dx} = {Domain:0.0} m · RAYMARCH (no meshes)\n{dbg}";
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Space })
        {
            _fireSolitary = true;
        }
    }

    public override void _ExitTree()
    {
        foreach (var t in new[] { _texState, _texBottom })
        {
            if (t != null) { t.TextureRdRid = default; }
        }
        var s = _solver;
        _solver = null;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(() => s.Free())); }
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "29 · Shore Moana — hypothesis C: raymarch, no meshes",
            "Scene 26's solver, rendered as ONE raymarch instead of a water plane + terrain mesh. "
            + "The scene-210 Moana shader with its analytic swell and ground replaced by KP07's "
            + "real surface and bed. Start in CHEAP shading to get the shape and scale right, "
            + "then switch to Full for the oracle's look. Free cam: RMB look · WASD · Q/E · Shift.");
        _readout = ui.AddReadout("— fps");

        ui.AddSection("Shape first (cheap)");
        ui.AddOptions("Shading", new[] { "Cheap (matte)", "Full (204 oracle)" }, 0,
            i => _mat.SetShaderParameter("shade_mode", i));
        ui.AddSlider("March steps", 16, 256, 144, v => _mat.SetShaderParameter("cheap_steps", (int)v));
        // A heightfield distance overestimates on steep faces, so the march must under-relax or
        // it walks through the wave. This is the knob that decides whether the shape is real.
        ui.AddSlider("Step relax", 0.1f, 1.0f, 0.55f, v => _mat.SetShaderParameter("cheap_relax", v));
        ui.AddToggle("Ground (bed)", true, v => _mat.SetShaderParameter("show_ground", v));
        ui.AddSlider("Sea level offset (m)", -5.0f, 5.0f, 0.0f, v => _mat.SetShaderParameter("sea_level", v));

        ui.AddSection("Sim");
        ui.AddToggle("Run", _running, v => _running = v);
        ui.AddSlider("Sim speed", 0.05f, 1.5f, 1.0f, v => _simSpeed = v);
        ui.AddSlider("Wave amp (m)", 0.0f, 1.0f, 0.585f, v => { if (_solver != null) { _solver.WaveAmp = v; } });
        ui.AddSlider("Wave period (s)", 3.0f, 16.0f, 8.005f, v => { if (_solver != null) { _solver.WavePeriod = v; } });
    }
}
