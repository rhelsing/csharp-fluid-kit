using System;
using Godot;

namespace GodotCsharpExperiments;

// Scene 49 — FHN SPIRALS: FitzHugh–Nagumo excitable media with spiral-tip tracking
// (science_boi chiral_spirals lift, docs/science-boi-lift.md). A bare-plate lab —
// the kit convention: prove the system in isolation, then graduate its tricks into
// the composition scenes. Two-field explicit kernel (no solver: the CN scaffolding's
// stencil with a reaction RHS), the classic cross-field spiral seed, and the third
// use of the 2×2 winding-number primitive (after 51's swell defects + eddy cores):
// tips = phase singularities of the (u,v) limit cycle, sign = chirality.
// LEFT-CLICK excites — poke behind a passing wave's refractory tail to break the
// front and birth new spirals. Tips glow red (CW) / orange (CCW).
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/49_fhn_spirals.tscn 8 1280x960
public partial class FhnSpirals : Node3D
{
    private static readonly Vector2I Grid = new(256, 256);
    private const float PlaneSize = 6.0f;
    private const string StepPath = "res://shaders/stamp/fhn_step.glslinc";
    private const string SeedPath = "res://shaders/stamp/fhn_seed.glslinc";
    private const string TipsPath = "res://shaders/stamp/fhn_tips.glslinc";
    private const string PlateShader = "res://shaders/fhn_plate.gdshader";

    private Camera3D _cam = null!;
    private MeshInstance3D _plate = null!;
    private ShaderMaterial _mat = null!;
    private Texture2Drd _stateTex = null!;
    private Texture2Drd _tipsTex = null!;
    private Label _fpsLabel = null!;
    private float _fpsT;

    private Rid _stepSh, _stepPipe, _seedSh, _seedPipe, _tipsSh, _tipsPipe;
    private Rid _stateA, _stateB, _tips;
    private Rid _sStepA0, _sStepB1, _sStepB0, _sStepA1, _sSeedA, _sTipsA0, _sTipsOut;
    private bool _ready;
    private bool _reseedQueued = true;   // seed the cross-field spiral on first tick

    // FHN parameters (science_boi chiral_spirals defaults)
    private float _du = 1.0f;
    private float _dt = 0.03f;
    private float _eps = 0.06f;
    private float _a = 0.7f;
    private float _b = 0.8f;
    private int _substeps = 30;
    private float _pokeRadius = 5f;
    private float _pokeStrength = 2.0f;
    private float _tipThresh = 4.0f;
    private bool _showTips = true;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildPlate();
        RenderingServer.CallOnRenderThread(Callable.From(InitCompute));
        BuildUi();
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 50f, Position = new Vector3(0f, 7.2f, 3.4f), Current = true };
        AddChild(_cam);
        _cam.LookAt(Vector3.Zero, Vector3.Up);

        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(0.02f, 0.02f, 0.04f),
            TonemapMode = Godot.Environment.ToneMapper.Agx,
            GlowEnabled = true,
            GlowIntensity = 0.6f,
            GlowBloom = 0.1f,
        };
        AddChild(new WorldEnvironment { Environment = env });

        var fpsLayer = new CanvasLayer();
        AddChild(fpsLayer);
        _fpsLabel = new Label
        {
            AnchorTop = 1f,
            AnchorBottom = 1f,
            OffsetLeft = 12f,
            OffsetTop = -36f,
            OffsetBottom = -12f,
            Text = "— fps",
        };
        _fpsLabel.AddThemeFontSizeOverride("font_size", 16);
        fpsLayer.AddChild(_fpsLabel);
    }

    private void BuildPlate()
    {
        _plate = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(PlaneSize, PlaneSize), SubdivideWidth = 2, SubdivideDepth = 2 },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        _stateTex = new Texture2Drd();
        _tipsTex = new Texture2Drd();
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(PlateShader) };
        _mat.SetShaderParameter("state_tex", _stateTex);
        _mat.SetShaderParameter("tips_tex", _tipsTex);
        _plate.MaterialOverride = _mat;
        AddChild(_plate);
    }

    private Rid Compile(RenderingDevice rd, string path, string tag)
    {
        var src = new RDShaderSource
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = FileAccess.GetFileAsString(path),
        };
        var spirv = rd.ShaderCompileSpirVFromSource(src);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[{tag}] compile error:\n{err}");
            return default;
        }
        return rd.ShaderCreateFromSpirV(spirv);
    }

    private Rid MakeSet(RenderingDevice rd, Rid tex, int idx, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)idx);
    }

    private void InitCompute()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _stepSh = Compile(rd, StepPath, "fhn-step");
        _seedSh = Compile(rd, SeedPath, "fhn-seed");
        _tipsSh = Compile(rd, TipsPath, "fhn-tips");
        if (!_stepSh.IsValid || !_seedSh.IsValid || !_tipsSh.IsValid)
        {
            return;
        }
        _stepPipe = rd.ComputePipelineCreate(_stepSh);
        _seedPipe = rd.ComputePipelineCreate(_seedSh);
        _tipsPipe = rd.ComputePipelineCreate(_tipsSh);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16G16Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)Grid.X,
            Height = (uint)Grid.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _stateA = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _stateB = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        tf.Format = RenderingDevice.DataFormat.R16Sfloat;
        _tips = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        foreach (var t in new[] { _stateA, _stateB, _tips })
        {
            rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        }

        _sStepA0 = MakeSet(rd, _stateA, 0, _stepSh);
        _sStepB1 = MakeSet(rd, _stateB, 1, _stepSh);
        _sStepB0 = MakeSet(rd, _stateB, 0, _stepSh);
        _sStepA1 = MakeSet(rd, _stateA, 1, _stepSh);
        _sSeedA = MakeSet(rd, _stateA, 0, _seedSh);
        _sTipsA0 = MakeSet(rd, _stateA, 0, _tipsSh);
        _sTipsOut = MakeSet(rd, _tips, 1, _tipsSh);
        _ready = true;
    }

    public override void _PhysicsProcess(double delta)
    {
        var poke = Vector4.Zero;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var mp = GetViewport().GetMousePosition();
            var plane = new Plane(Vector3.Up, 0f);
            if (plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp)) is Vector3 hit)
            {
                var uv = new Vector2(hit.X / PlaneSize + 0.5f, hit.Z / PlaneSize + 0.5f);
                if (uv.X is >= 0f and <= 1f && uv.Y is >= 0f and <= 1f)
                {
                    poke = new Vector4(uv.X * Grid.X, uv.Y * Grid.Y, _pokeRadius, _pokeStrength);
                }
            }
        }

        _stateTex.TextureRdRid = _ready ? _stateA : default;
        _tipsTex.TextureRdRid = _ready ? _tips : default;

        int substeps = Math.Max(2, _substeps & ~1);   // even: state ends in A
        bool reseed = false;
        if (_ready && _reseedQueued)   // hold the flag until the compute exists
        {
            reseed = true;
            _reseedQueued = false;
        }

        // step push: 12 floats = 48B — poke applied on the FIRST substep only.
        float[] sp = { Grid.X, Grid.Y, _du, _dt, _eps, _a, _b, poke.X, poke.Y, poke.Z, poke.W, 0f };
        var spB = new byte[sp.Length * sizeof(float)];
        Buffer.BlockCopy(sp, 0, spB, 0, spB.Length);
        float[] sp2 = { Grid.X, Grid.Y, _du, _dt, _eps, _a, _b, 0f, 0f, 0f, 0f, 0f };
        var sp2B = new byte[sp2.Length * sizeof(float)];
        Buffer.BlockCopy(sp2, 0, sp2B, 0, sp2B.Length);
        float[] tp = { Grid.X, Grid.Y, -0.2f, 0.0f, _tipThresh, _showTips ? 1f : 0f, 0f, 0f };
        var tpB = new byte[tp.Length * sizeof(float)];
        Buffer.BlockCopy(tp, 0, tpB, 0, tpB.Length);

        RenderingServer.CallOnRenderThread(Callable.From(() => StepRt(spB, sp2B, tpB, substeps, reseed)));

        _fpsT += (float)delta;
        if (_fpsT >= 0.25f)
        {
            _fpsT = 0f;
            _fpsLabel.Text = $"{Engine.GetFramesPerSecond():0} fps";
        }
    }

    private void StepRt(byte[] pokePc, byte[] quietPc, byte[] tipsPc, int substeps, bool reseed)
    {
        if (!_ready)
        {
            return;
        }
        var rd = RenderingServer.GetRenderingDevice();
        uint g = (uint)((Grid.X - 1) / 8 + 1);

        if (reseed)
        {
            float[] seed = { Grid.X, Grid.Y, 1f, 0f };
            var seedB = new byte[16];
            Buffer.BlockCopy(seed, 0, seedB, 0, 16);
            long cs = rd.ComputeListBegin();
            rd.ComputeListBindComputePipeline(cs, _seedPipe);
            rd.ComputeListBindUniformSet(cs, _sSeedA, 0);
            rd.ComputeListSetPushConstant(cs, seedB, 16);
            rd.ComputeListDispatch(cs, g, g, 1);
            rd.ComputeListAddBarrier(cs);
            rd.ComputeListEnd();
        }

        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _stepPipe);
        for (int s = 0; s < substeps; s++)
        {
            rd.ComputeListBindUniformSet(cl, s % 2 == 0 ? _sStepA0 : _sStepB0, 0);
            rd.ComputeListBindUniformSet(cl, s % 2 == 0 ? _sStepB1 : _sStepA1, 1);
            var pc = s == 0 ? pokePc : quietPc;
            rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            rd.ComputeListDispatch(cl, g, g, 1);
            rd.ComputeListAddBarrier(cl);
        }
        rd.ComputeListBindComputePipeline(cl, _tipsPipe);
        rd.ComputeListBindUniformSet(cl, _sTipsA0, 0);
        rd.ComputeListBindUniformSet(cl, _sTipsOut, 1);
        rd.ComputeListSetPushConstant(cl, tipsPc, (uint)tipsPc.Length);
        rd.ComputeListDispatch(cl, g, g, 1);
        rd.ComputeListAddBarrier(cl);
        rd.ComputeListEnd();
    }

    public override void _ExitTree()
    {
        if (_stateTex != null)
        {
            _stateTex.TextureRdRid = default;
        }
        if (_tipsTex != null)
        {
            _tipsTex.TextureRdRid = default;
        }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _ready = false;
            var rd = RenderingServer.GetRenderingDevice();
            foreach (var r in new[]
            {
                _sStepA0, _sStepB1, _sStepB0, _sStepA1, _sSeedA, _sTipsA0, _sTipsOut,
                _stateA, _stateB, _tips, _stepSh, _seedSh, _tipsSh,
            })
            {
                if (r.IsValid)
                {
                    rd.FreeRid(r);
                }
            }
        }));
    }

    private void BuildUi()
    {
        var ui = new Lib.DemoUI(this, "49 · FHN spirals — excitable media + tip tracking",
            "FitzHugh–Nagumo on a 256² plate: waves propagate, leave a refractory tail, and broken fronts curl into SPIRALS. Seeded with one cross-field spiral; LEFT-CLICK excites — poke into a wave's tail to break it and birth more. Tips (winding number) glow red = CW, orange = CCW.");
        ui.AddToggle("Show spiral tips", _showTips, v => _showTips = v);
        ui.AddToggle("Reseed (cross-field spiral)", false, v => _reseedQueued = true);
        ui.AddSlider("Substeps / tick (sim speed)", 2, 80, _substeps, v => _substeps = (int)v);
        ui.AddSlider("dt", 0.005f, 0.06f, _dt, v => _dt = v);
        ui.AddSlider("Diffusion D", 0.2f, 3f, _du, v => _du = v);
        ui.AddSlider("ε (inhibitor rate — low = long tail)", 0.01f, 0.2f, _eps, v => _eps = v);
        ui.AddSlider("a", 0.3f, 1.1f, _a, v => _a = v);
        ui.AddSlider("b", 0.3f, 1.5f, _b, v => _b = v);
        ui.AddSlider("Poke radius (px)", 2f, 20f, _pokeRadius, v => _pokeRadius = v);
        ui.AddSlider("Poke strength", 0.5f, 4f, _pokeStrength, v => _pokeStrength = v);
        ui.AddSlider("Tip winding thresh (rad)", 2f, 6f, _tipThresh, v => _tipThresh = v);
    }
}
