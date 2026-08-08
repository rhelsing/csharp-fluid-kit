using System;
using System.Collections.Generic;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 50 — Boat + MNA reactive window. Fork of scene 16 (BoatComposite: boat +
// composite-Gerstner sea + ameye look + seabed/shadow/islands + chase camera), the
// base for the docs/mna-next-steps.md build: a camera-following CN-MNA sim window
// (§2a micro layer + §2.5 toroidal window, sponge edges, edge fade) layered on the
// composite macro — `h = gerstner + mna` — poked by the boat's wake, foam on top.
// Starts byte-identical to 16 so every MNA stage lands as a small verifiable diff.
//
// CompositeGerstner (CPU, extends GerstnerField -> drops into the boat) and
// shaders/ameye_water_composite.gdshader (the ameye water shader with an N-band wave loop)
// sum the SAME bands + a shared wave_time, so the boat rides the exact crests you see.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/50_boat_mna.tscn 5 1600x1200
public partial class BoatMna : Node3D
{
    private const string WaterShader = "res://shaders/ameye_water_mna.gdshader";
    private const string FloorShader = "res://shaders/caustics_floor.gdshader";
    private const string FoamTex = "res://textures/ameye/Foam 5.png";
    private const string NormalTex = "res://textures/ameye/Normals 1.png";
    private const float BedDepth = -3.2f;

    // Scene 17's proven render-mesh density: 200 m at 400 subdivisions = 0.5 m vertices
    // (fine enough to draw the 512² ripples). The plane follows the boat snapped by ITS
    // OWN vertex spacing, so vertices land on the same world lattice every snap and the
    // Gerstner resample is bit-identical — no shimmer. (Snapping by the 15.6 cm sim cell
    // moved the vertex lattice ~60×/s while driving — that was the jank.)
    private const float WaterPlaneSize = 200f;
    private const int WaterPlaneSubdiv = 400;

    // Composite bands: [wavelength, steepness, speed, direction(0..1)] — swell -> chop.
    private static readonly float[][] Bands =
    {
        new[] { 34f, 0.13f, 1.10f, 0.12f },
        new[] { 26f, 0.10f, 1.00f, 0.28f },
        new[] { 14f, 0.10f, 1.05f, 0.52f },
        new[] { 9f, 0.09f, 1.15f, 0.66f },
        new[] { 5f, 0.07f, 1.35f, 0.82f },
        new[] { 3f, 0.05f, 1.60f, 0.40f },
    };

    private SumField _field = new();
    private ShaderMaterial _mat = null!;
    private ShaderMaterial _floorMat = null!;
    private MeshInstance3D _waterMi = null!;
    private MeshInstance3D _bedMi = null!;
    private BoatRider _boat = null!;
    private Camera3D _cam = null!;
    private DirectionalLight3D _sun = null!;

    // Ryan's live-tuned preset (Copy values, baked): calmer sea, faster phase.
    private float _waveAmp = 0.62f;
    private float _waveSpeedScale = 1.5375f;

    // Chase-camera smoothing (from scene 15): snappy XZ, damped Y so it doesn't pop.
    private Vector3 _camFocus;
    private bool _camFocusInit;
    private float _camFollowXz = 8f;
    private float _camSmoothY = 1.5f;
    private float _camPosRate = 6f;

    // --- MNA reactive window (docs/mna-next-steps.md §2a) — a world-anchored CN sim
    // box under the boat spawn; toroidal camera-follow comes in a later stage.
    private static readonly Vector2I MnaGrid = new(512, 512);
    private const float MnaWindow = 80f;   // scene 17's 80 m reach, at 512² = 15.6 cm cells (finer than both)
    private const string StampPath = "res://shaders/stamp/stamp_wave_multi.glslinc";

    private const string SolveBodyPath = "res://shaders/stamp/solve_rbgs_wrap.glslinc";

    private IStampSolver? _mnaSolver;
    private Texture2Drd _mnaTex = null!;
    private Vector2 _mnaOrigin = new(-MnaWindow * 0.5f, -MnaWindow * 0.5f);
    private Vector2I _mnaTexOff;   // toroidal scroll offset (cells) — the window rides the boat

    // Scene 03's solver constants rescaled for ocean cells (1 cell = 18.75 cm here vs
    // 2.3 cm on 03's plate): slower c ≈ 3 m/s so rings linger at ripple speed.
    private float _mnaC = 0.39f;
    private float _mnaDt = 0.84f;
    private float _mnaDamping = 0f;
    private float _mnaLeak = 0.001f;
    private float _mnaCn = 0.375f;
    private int _mnaIters = 13;
    private float _mnaPokeRadius = 4.53f;
    private float _mnaPokeStrength = 0.35f;
    private int _mnaSubsteps = 2;          // oversampling: S sub-ticks of dt/S — less dispersion, same speed

    private bool _mnaAutoPoke = false;
    private float _mnaPokeT;
    private int _mnaPokeI;
    private int _mnaTick;
    private Label? _mnaReadout;
    private Label _fpsLabel = null!;
    private float _fpsT;

    private float _wakeStrength = 0.0096f; // per-tick hull forcing at full speed (60 Hz integrates this)
    private float _wakeRadius = 2.7f;      // px
    private float _wakeSideOffset = 0f;    // boat-local right(+)/left(-) shift of both pokes (m)
    private bool _mousePoke = false;       // click-to-poke — OFF: clicks must not touch the water
    // Per-poke placement (boat-local polar) — full manual control of the wake shape.
    private bool _bowPokeOn = true;        // TWIN bow pokes (the V-wake split), spread-tunable
    private float _bowAngle = 0f;          // deg off dead-AHEAD (+ = starboard)
    private float _bowDist = 1.44f;        // m from hull centre
    private float _bowScale = -0.68f;      // × wake strength per poke (+ ridge / − trough)
    private float _bowSpread = 2.25f;      // m between the two bow pokes
    private float _sternAngle = 0f;        // deg off dead-ASTERN (+ = starboard)
    private float _sternDist = 4.2f;
    private float _sternScale = 0.92f;
    private float _wakeInfluence = 1.0f;   // buoyancy feels the reactive wake (scene 17 parity; 0 = off)
    private float _mnaDisplacement = 2.61f;
    private float _mnaEdgeFade = 0.15f;
    // Async-readback smoothing: arrivals are step-functions at irregular 2–4 frame
    // cadence, which the buoyancy spring feels as kicks. Cross-fade the last two
    // readbacks so the field the hull feels evolves continuously.
    private volatile float[]? _mnaCurr;    // newest published field
    private volatile float[]? _mnaPrev;    // previous published field (lerp source)
    private float[]? _hbA, _hbB, _hbC;     // rotating write targets — writer never touches published refs
    private float _mnaBlend = 1f;          // 0 -> 1 from prev to curr after each arrival
    private float _mnaBlendTime = 0.104f;  // ≈ readback cadence (slider)
    private bool _autoCruise = false;      // demo drive for harness shots — OFF by default (toggle in panel)
    private bool _meshFollow = true;       // A/B: freeze the render meshes to isolate snap artifacts
    private float _spongeWidth = 16f;      // absorbing-edge ramp width (px)
    private float _spongeDamp = 0.35f;     // extra damping at the very border

    // --- Reactive foam (§2a): |∇²h| -> accumulate+decay buffer, same toroidal window ---
    private const string FoamKernelPath = "res://shaders/stamp/foam_update.glslinc";
    private Rid _foamShader, _foamPipe, _foamState, _foamTmp;
    private Rid _foamSetIn, _foamSetOut, _foamSetH, _foamSetHp;
    private bool _foamReady;
    private Texture2Drd _foamDisplayTex = null!;
    private float _foamDecay = 0.96f;
    private float _foamGain = 3f;
    private float _foamThresh = 0.015f;

    // --- Foam v2 (docs/scene50-foam.md): ADDITIVE layer — own kernel/buffers/knobs,
    // v1 untouched. Both kernels step every tick; the toggles gate DISPLAY only.
    private const string Foam2KernelPath = "res://shaders/stamp/foam_update_v2.glslinc";
    private Rid _foam2Shader, _foam2Pipe, _foam2State, _foam2Tmp;
    private Rid _foam2SetIn, _foam2SetOut, _foam2SetH;
    private bool _foam2Ready;
    private Texture2Drd _foam2DisplayTex = null!;
    private bool _foam1On = true;
    private bool _foam2On = true;
    private float _f2DecayW = 0.94f;
    private float _f2DecayL = 0.996f;
    private float _f2DecayS = 0.99f;
    private float _f2XferWL = 0.7f;
    private float _f2XferLS = 0.5f;
    private float _f2Thresh = 0.008f;   // v2's own threshold (dropped below v1's — wake foam fires while cruising)
    private float _f2Drift = 1.0f;      // v3a: swell-drift strength (0 = static v2 behaviour)

    // --- Foam v3b: SWIRL layer (docs/scene50-foam.md) — a 128² incompressible Stam
    // fluid on its own toroidal boat-following window. Propwash jet at the stern (the
    // vortex pair emerges from the pressure projection, as behind a real transom) +
    // a lateral shed on turns; its velocity advects the foam = milk-in-coffee wakes.
    private const int SwirlN = 128;
    private const string SwAdvPath = "res://shaders/stamp/swirl_advect.glslinc";
    private const string SwDivPath = "res://shaders/stamp/swirl_divergence.glslinc";
    private const string SwJacPath = "res://shaders/stamp/swirl_jacobi.glslinc";
    private const string SwGradPath = "res://shaders/stamp/swirl_gradient.glslinc";
    private Rid _swAdvSh, _swAdvPipe, _swDivSh, _swDivPipe, _swJacSh, _swJacPipe, _swGradSh, _swGradPipe;
    private Rid _swVelA, _swVelB, _swDivTex, _swPrA, _swPrB;
    private Rid _sAdvIn, _sAdvOut, _sDivV, _sDivOut, _sJacA0, _sJacB1, _sJacB0, _sJacA1, _sJacDiv, _sGradV, _sGradOut, _sGradP;
    private Rid _foam2SetSwirl;
    private bool _swReady;
    private Vector2 _swOrigin = new(-MnaWindow * 0.5f, -MnaWindow * 0.5f);
    private Vector2I _swTexOff;
    private float _prevYawScene;
    private bool _swirlOn = true;
    private float _swJet = 2.5f;        // propwash strength (m/s at full throttle)
    private float _swJetRadius = 1.6f;  // m
    private float _swShed = 2.0f;       // lateral vortex shed per rad/s of turn
    private float _swDissip = 0.995f;   // velocity retention/tick
    private int _swIters = 22;          // pressure Jacobi sweeps (kept even; warm-started)
    private float _swAdvectScale = 1.0f;
    private float _swEdge = 10f;        // window edge falloff (texels)
    private const float MnaPokeInterval = 0.45f;
    private static readonly Vector2[] MnaPokes =
    {
        new(0.35f, 0.4f), new(0.65f, 0.6f), new(0.5f, 0.5f),
        new(0.4f, 0.68f), new(0.66f, 0.34f),
    };

    public override void _Ready()
    {
        _field.Extra = SampleMnaWorld;   // boat bobs on swell + its own wake
        RebuildField();
        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildBoat();
        BuildIslands();
        RenderingServer.CallOnRenderThread(Callable.From(InitMnaSolver));
        // Ryan's preset: MetalFX spatial at 0.4 render scale (sim cost is resolution-
        // independent — fullscreen fps is pure per-pixel work, so scale exactly that).
        GetViewport().Scaling3DScale = 0.4f;
        GetViewport().Scaling3DMode = (Viewport.Scaling3DModeEnum)3;   // MetalFX spatial
        BuildUi();
    }

    // Arrow keys belong to the boat ALONE — eat the events before the GUI layer sees
    // them (sliders/scroll reacting to throttle keys is unusable). BoatRider polls
    // Input.GetAxis, which reads raw key state and is unaffected by handled events.
    public override void _Input(InputEvent ev)
    {
        if (ev.IsAction("ui_left") || ev.IsAction("ui_right") || ev.IsAction("ui_up") || ev.IsAction("ui_down"))
        {
            GetViewport().SetInputAsHandled();
        }
    }

    private void InitMnaSolver()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _mnaSolver = new GpuStampSolver(rd, MnaGrid, StampPath, GpuStampSolver.Mode.Rbgs, SolveBodyPath);
        InitSwirl(rd);   // before InitFoam: the foam kernel binds the swirl velocity
        InitFoam(rd);
    }

    private Rid CompileKernel(RenderingDevice rd, string path, string tag)
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
            GD.PushError($"[{tag}] kernel compile error:\n{err}");
            return default;
        }
        return rd.ShaderCreateFromSpirV(spirv);
    }

    private void InitSwirl(RenderingDevice rd)
    {
        _swAdvSh = CompileKernel(rd, SwAdvPath, "swirl-advect");
        _swDivSh = CompileKernel(rd, SwDivPath, "swirl-div");
        _swJacSh = CompileKernel(rd, SwJacPath, "swirl-jacobi");
        _swGradSh = CompileKernel(rd, SwGradPath, "swirl-grad");
        if (!_swAdvSh.IsValid || !_swDivSh.IsValid || !_swJacSh.IsValid || !_swGradSh.IsValid)
        {
            return;
        }
        _swAdvPipe = rd.ComputePipelineCreate(_swAdvSh);
        _swDivPipe = rd.ComputePipelineCreate(_swDivSh);
        _swJacPipe = rd.ComputePipelineCreate(_swJacSh);
        _swGradPipe = rd.ComputePipelineCreate(_swGradSh);

        var tfv = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16G16Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = SwirlN,
            Height = SwirlN,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        // RDTextureFormat is a reference type — create the RG16F velocity textures
        // BEFORE mutating Format for the scalar (div/pressure) ones.
        _swVelA = rd.TextureCreate(tfv, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _swVelB = rd.TextureCreate(tfv, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        tfv.Format = RenderingDevice.DataFormat.R16Sfloat;
        _swDivTex = rd.TextureCreate(tfv, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _swPrA = rd.TextureCreate(tfv, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _swPrB = rd.TextureCreate(tfv, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        foreach (var t in new[] { _swVelA, _swVelB, _swDivTex, _swPrA, _swPrB })
        {
            rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        }

        _sAdvIn = MakeFoamSet(rd, _swVelA, 0, _swAdvSh);
        _sAdvOut = MakeFoamSet(rd, _swVelB, 1, _swAdvSh);
        _sDivV = MakeFoamSet(rd, _swVelB, 0, _swDivSh);
        _sDivOut = MakeFoamSet(rd, _swDivTex, 1, _swDivSh);
        _sJacA0 = MakeFoamSet(rd, _swPrA, 0, _swJacSh);
        _sJacB1 = MakeFoamSet(rd, _swPrB, 1, _swJacSh);
        _sJacB0 = MakeFoamSet(rd, _swPrB, 0, _swJacSh);
        _sJacA1 = MakeFoamSet(rd, _swPrA, 1, _swJacSh);
        _sJacDiv = MakeFoamSet(rd, _swDivTex, 2, _swJacSh);
        _sGradV = MakeFoamSet(rd, _swVelB, 0, _swGradSh);
        _sGradOut = MakeFoamSet(rd, _swVelA, 1, _swGradSh);
        _sGradP = MakeFoamSet(rd, _swPrA, 2, _swGradSh);
        _swReady = true;
    }

    // Render thread: one full Stam step — advect+force -> divergence -> K Jacobi
    // pressure sweeps (even count, warm-started from last tick) -> gradient subtract.
    // Velocity state starts and ends in _swVelA (what the foam kernel binds).
    private void StepSwirlRt(byte[] advPc, int iters)
    {
        if (!_swReady)
        {
            return;
        }
        var rd = RenderingServer.GetRenderingDevice();
        uint g = (uint)((SwirlN - 1) / 8 + 1);
        float[] small = { SwirlN, SwirlN, 0f, 0f };
        var smallPc = new byte[16];
        Buffer.BlockCopy(small, 0, smallPc, 0, 16);

        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _swAdvPipe);
        rd.ComputeListBindUniformSet(cl, _sAdvIn, 0);
        rd.ComputeListBindUniformSet(cl, _sAdvOut, 1);
        rd.ComputeListSetPushConstant(cl, advPc, (uint)advPc.Length);
        rd.ComputeListDispatch(cl, g, g, 1);
        rd.ComputeListAddBarrier(cl);

        rd.ComputeListBindComputePipeline(cl, _swDivPipe);
        rd.ComputeListBindUniformSet(cl, _sDivV, 0);
        rd.ComputeListBindUniformSet(cl, _sDivOut, 1);
        rd.ComputeListSetPushConstant(cl, smallPc, 16);
        rd.ComputeListDispatch(cl, g, g, 1);
        rd.ComputeListAddBarrier(cl);

        rd.ComputeListBindComputePipeline(cl, _swJacPipe);
        for (int i = 0; i < iters; i++)
        {
            rd.ComputeListBindUniformSet(cl, i % 2 == 0 ? _sJacA0 : _sJacB0, 0);
            rd.ComputeListBindUniformSet(cl, i % 2 == 0 ? _sJacB1 : _sJacA1, 1);
            rd.ComputeListBindUniformSet(cl, _sJacDiv, 2);
            rd.ComputeListSetPushConstant(cl, smallPc, 16);
            rd.ComputeListDispatch(cl, g, g, 1);
            rd.ComputeListAddBarrier(cl);
        }

        rd.ComputeListBindComputePipeline(cl, _swGradPipe);
        rd.ComputeListBindUniformSet(cl, _sGradV, 0);
        rd.ComputeListBindUniformSet(cl, _sGradOut, 1);
        rd.ComputeListBindUniformSet(cl, _sGradP, 2);
        rd.ComputeListSetPushConstant(cl, smallPc, 16);
        rd.ComputeListDispatch(cl, g, g, 1);
        rd.ComputeListAddBarrier(cl);
        rd.ComputeListEnd();
    }

    private void InitFoam(RenderingDevice rd)
    {
        string src = FileAccess.GetFileAsString(FoamKernelPath);
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[foam] kernel compile error:\n{err}");
            return;
        }
        _foamShader = rd.ShaderCreateFromSpirV(spirv);
        _foamPipe = rd.ComputePipelineCreate(_foamShader);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)MnaGrid.X,
            Height = (uint)MnaGrid.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _foamState = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _foamTmp = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        rd.TextureClear(_foamState, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        rd.TextureClear(_foamTmp, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        _foamSetIn = MakeFoamSet(rd, _foamState, 0, _foamShader);
        _foamSetOut = MakeFoamSet(rd, _foamTmp, 1, _foamShader);
        _foamSetH = MakeFoamSet(rd, _mnaSolver!.HeightRid, 2, _foamShader);
        _foamSetHp = MakeFoamSet(rd, _mnaSolver.PrevRid, 3, _foamShader);   // v1 kernel now takes h_prev (drift stage; gain 0 = unchanged)
        _foamReady = true;

        // v2: same shape, RGBA16F cascade buffers + its own kernel.
        string src2 = FileAccess.GetFileAsString(Foam2KernelPath);
        var rdSrc2 = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src2 };
        var spirv2 = rd.ShaderCompileSpirVFromSource(rdSrc2);
        string err2 = spirv2.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err2))
        {
            GD.PushError($"[foam2] kernel compile error:\n{err2}");
            return;
        }
        _foam2Shader = rd.ShaderCreateFromSpirV(spirv2);
        _foam2Pipe = rd.ComputePipelineCreate(_foam2Shader);
        var tf2 = tf;
        tf2.Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat;
        _foam2State = rd.TextureCreate(tf2, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _foam2Tmp = rd.TextureCreate(tf2, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        rd.TextureClear(_foam2State, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        rd.TextureClear(_foam2Tmp, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _foam2SetIn = MakeFoamSet(rd, _foam2State, 0, _foam2Shader);
        _foam2SetOut = MakeFoamSet(rd, _foam2Tmp, 1, _foam2Shader);
        _foam2SetH = MakeFoamSet(rd, _mnaSolver!.HeightRid, 2, _foam2Shader);
        if (!_swReady)
        {
            GD.PushError("[foam2] swirl layer failed to init — foam v2 kernel needs its velocity binding");
            return;
        }
        _foam2SetSwirl = MakeFoamSet(rd, _swVelA, 3, _foam2Shader);
        _foam2Ready = true;
    }

    private Rid MakeFoamSet(RenderingDevice rd, Rid tex, int setIdx, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIdx);
    }

    // Render thread: the foam passes (v1 + v2) reading the freshly-solved height.
    private void StepFoamRt(byte[] fpc, byte[] fpc2)
    {
        if (!_foamReady)
        {
            return;
        }
        var rd = RenderingServer.GetRenderingDevice();
        uint gx = (uint)((MnaGrid.X - 1) / 8 + 1);
        uint gy = (uint)((MnaGrid.Y - 1) / 8 + 1);
        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _foamPipe);
        rd.ComputeListBindUniformSet(cl, _foamSetIn, 0);
        rd.ComputeListBindUniformSet(cl, _foamSetOut, 1);
        rd.ComputeListBindUniformSet(cl, _foamSetH, 2);
        rd.ComputeListBindUniformSet(cl, _foamSetHp, 3);
        rd.ComputeListSetPushConstant(cl, fpc, (uint)fpc.Length);
        rd.ComputeListDispatch(cl, gx, gy, 1);
        rd.ComputeListAddBarrier(cl);
        if (_foam2Ready)
        {
            rd.ComputeListBindComputePipeline(cl, _foam2Pipe);
            rd.ComputeListBindUniformSet(cl, _foam2SetIn, 0);
            rd.ComputeListBindUniformSet(cl, _foam2SetOut, 1);
            rd.ComputeListBindUniformSet(cl, _foam2SetH, 2);
            rd.ComputeListBindUniformSet(cl, _foam2SetSwirl, 3);
            rd.ComputeListSetPushConstant(cl, fpc2, (uint)fpc2.Length);
            rd.ComputeListDispatch(cl, gx, gy, 1);
            rd.ComputeListAddBarrier(cl);
        }
        rd.ComputeListEnd();
        var full = new Vector3(MnaGrid.X, MnaGrid.Y, 1);
        rd.TextureCopy(_foamTmp, _foamState, Vector3.Zero, Vector3.Zero, full, 0, 0, 0, 0);
        if (_foam2Ready)
        {
            rd.TextureCopy(_foam2Tmp, _foam2State, Vector3.Zero, Vector3.Zero, full, 0, 0, 0, 0);
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        UpdateFloorShadow();
        if (_autoCruise && Input.GetAxis("ui_down", "ui_up") == 0f && Input.GetAxis("ui_left", "ui_right") == 0f)
        {
            _boat.CurrentSpeed = Mathf.MoveToward(_boat.CurrentSpeed, 6f, 3f * (float)delta);
            _boat.Yaw += 0.12f * (float)delta;   // wide arc — the window follows, so range is free   // wide arc — the window follows, so range is free
        }
        StepMna((float)delta);
    }

    // One MNA tick: gather this tick's pokes (mouse + auto + boat wake, up to 4),
    // push the stamp constants, dispatch the solve on the render thread.
    private void StepMna(float delta)
    {
        _mnaBlend = Mathf.Min(1f, _mnaBlend + delta / Mathf.Max(_mnaBlendTime, 0.005f));

        // Toroidal camera-follow (§2.5): the window rides with the boat, cell-snapped.
        // The field is WORLD-anchored — on motion we bump the origin + texture offset
        // (never move the data); the strip that wraps in was just absorbed by the
        // sponge on the far side, so it arrives near-zero and needs no re-init.
        float cell = MnaWindow / MnaGrid.X;
        Vector3 boatPos = _boat.GlobalPosition;
        var desired = new Vector2(boatPos.X - MnaWindow * 0.5f, boatPos.Z - MnaWindow * 0.5f);
        int di = Mathf.FloorToInt((desired.X - _mnaOrigin.X) / cell);
        int dj = Mathf.FloorToInt((desired.Y - _mnaOrigin.Y) / cell);
        if (di != 0 || dj != 0)
        {
            _mnaOrigin += new Vector2(di * cell, dj * cell);
            _mnaTexOff = new Vector2I(
                ((_mnaTexOff.X + di) % MnaGrid.X + MnaGrid.X) % MnaGrid.X,
                ((_mnaTexOff.Y + dj) % MnaGrid.Y + MnaGrid.Y) % MnaGrid.Y);
            _mat.SetShaderParameter("mna_origin", _mnaOrigin);
            _mat.SetShaderParameter("mna_tex_offset", new Vector2(
                (float)_mnaTexOff.X / MnaGrid.X, (float)_mnaTexOff.Y / MnaGrid.Y));
        }

        // The render surfaces follow too — a long drive must never run off the mesh.
        // Every shader on them is world-space, so moving them never moves the waves.
        // Water snaps by its OWN vertex spacing (lattice-invariant — see WaterPlaneSize);
        // the bed is flat with fragment-space shading, so it can follow smoothly.
        if (_meshFollow)
        {
            const float meshCell = WaterPlaneSize / (WaterPlaneSubdiv + 1);
            _waterMi.Position = new Vector3(
                Mathf.Round(boatPos.X / meshCell) * meshCell, 0f,
                Mathf.Round(boatPos.Z / meshCell) * meshCell);
            _bedMi.Position = new Vector3(boatPos.X, BedDepth, boatPos.Z);
        }

        // v3b swirl window: same toroidal follow, its own (coarser) cell granularity.
        float swCell = MnaWindow / SwirlN;
        int sdi = Mathf.FloorToInt((desired.X - _swOrigin.X) / swCell);
        int sdj = Mathf.FloorToInt((desired.Y - _swOrigin.Y) / swCell);
        if (sdi != 0 || sdj != 0)
        {
            _swOrigin += new Vector2(sdi * swCell, sdj * swCell);
            _swTexOff = new Vector2I(
                ((_swTexOff.X + sdi) % SwirlN + SwirlN) % SwirlN,
                ((_swTexOff.Y + sdj) % SwirlN + SwirlN) % SwirlN);
        }

        // v3b forcing: propwash jet at the stern (throttle-scaled) + lateral shed ∝ yaw
        // rate — the projection turns both into vortices.
        float dyawRate = Mathf.Wrap(_boat.Yaw - _prevYawScene, -Mathf.Pi, Mathf.Pi) / Mathf.Max(delta, 1e-5f);
        _prevYawScene = _boat.Yaw;
        Vector3 sfwd = -_boat.GlobalTransform.Basis.Z;
        sfwd.Y = 0f;
        sfwd = sfwd.Normalized();
        Vector3 sright = new(-sfwd.Z, 0f, sfwd.X);
        float thr = Mathf.Abs(_boat.CurrentSpeed) / Mathf.Max(_boat.MaxSpeed, 0.01f);
        Vector3 sternW = boatPos - sfwd * 2.4f;
        var jetCell = new Vector2(sternW.X - _swOrigin.X, sternW.Z - _swOrigin.Y) / swCell;
        var jetVel = new Vector2(-sfwd.X, -sfwd.Z) * (_swJet * thr);
        var shedVel = new Vector2(sright.X, sright.Z) * (-dyawRate * _swShed * thr);
        float[] swPcF =
        {
            SwirlN, SwirlN, 1f / 60f, _swDissip, swCell, _swTexOff.X, _swTexOff.Y, _swEdge,
            jetCell.X, jetCell.Y, jetVel.X, jetVel.Y, _swJetRadius / swCell,
            jetCell.X, jetCell.Y, shedVel.X, shedVel.Y, _swJetRadius * 1.4f / swCell,
            0f, 0f,
        };
        var swBytes = new byte[swPcF.Length * sizeof(float)];
        Buffer.BlockCopy(swPcF, 0, swBytes, 0, swBytes.Length);
        int swIters = Math.Max(4, _swIters & ~1);   // even count: pressure ends in _swPrA

        var pokes = new List<Vector4>();
        if (_mousePoke && Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (MouseWaterHit() is Vector3 hit)
            {
                AddWorldPoke(pokes, hit, _mnaPokeRadius, -_mnaPokeStrength);
            }
        }
        else if (_mnaAutoPoke)
        {
            _mnaPokeT += delta;
            if (_mnaPokeT >= MnaPokeInterval)
            {
                _mnaPokeT = 0f;
                var uv = MnaPokes[_mnaPokeI];
                _mnaPokeI = (_mnaPokeI + 1) % MnaPokes.Length;
                pokes.Add(new Vector4(uv.X * MnaGrid.X, uv.Y * MnaGrid.Y, _mnaPokeRadius, -_mnaPokeStrength));
            }
        }

        // Boat wake — the §2a interactor: a BALANCED dipole (bow ridge + equal stern
        // trough) so the continuous 60 Hz forcing injects zero net volume — one-signed
        // forcing integrates into a standing mound the leak can't drain fast enough.
        float speedRatio = Mathf.Abs(_boat.CurrentSpeed) / Mathf.Max(_boat.MaxSpeed, 0.01f);
        if (speedRatio > 0.02f)
        {
            // Flattened to XZ: the tilted hull basis would smear the poke line laterally
            // as the boat rolls. Each poke is boat-local polar (angle off its axis +
            // distance + signed strength share) so the wake shape is fully hand-tunable.
            Vector3 fwd = -_boat.GlobalTransform.Basis.Z;
            fwd.Y = 0f;
            fwd = fwd.Normalized();
            Vector3 right = new(-fwd.Z, 0f, fwd.X);
            Vector3 bp = _boat.GlobalPosition + right * _wakeSideOffset;
            float w = _wakeStrength * speedRatio * speedRatio;   // wake energy ~ v²: no pile-up while crawling
            if (_bowPokeOn)
            {
                Vector3 bow = bp + PokeDir(fwd, right, _bowAngle) * _bowDist;
                Vector3 half = right * (_bowSpread * 0.5f);
                AddWorldPoke(pokes, bow - half, _wakeRadius, w * _bowScale);
                AddWorldPoke(pokes, bow + half, _wakeRadius, w * _bowScale);
            }
            AddWorldPoke(pokes, bp + PokeDir(-fwd, right, _sternAngle) * _sternDist, _wakeRadius, w * _sternScale);
        }

        _mnaTex.TextureRdRid = _mnaSolver?.HeightRid ?? default;
        _foamDisplayTex.TextureRdRid = _foamReady ? _foamState : default;
        _foam2DisplayTex.TextureRdRid = _foam2Ready ? _foam2State : default;

        // Oversampling (§2a): S sub-ticks of dt/S per frame — same wave speed, less
        // numerical dispersion. Forcing is injected on the FIRST sub-tick only.
        int substeps = _mnaSubsteps;
        float sdt = _mnaDt / substeps;
        float beta = sdt * sdt * _mnaC * _mnaC;
        float a = _mnaDamping * sdt * 0.5f;
        int iters = _mnaIters;
        _mnaTick++;
        bool measure = _mnaTick % 30 == 0;
        // Push layout = stamp_wave_multi.glslinc Params: 12-float header + 4 vec4 drops = 112B.
        var pc = new float[28];
        pc[0] = MnaGrid.X;
        pc[1] = MnaGrid.Y;
        pc[2] = beta;
        pc[3] = a;
        pc[4] = _mnaLeak;
        pc[5] = _mnaCn;
        pc[6] = _spongeWidth;
        pc[7] = _spongeDamp;
        pc[8] = _mnaTexOff.X;
        pc[9] = _mnaTexOff.Y;
        for (int i = 0; i < pokes.Count && i < 4; i++)
        {
            pc[12 + i * 4] = pokes[i].X;
            pc[13 + i * 4] = pokes[i].Y;
            pc[14 + i * 4] = pokes[i].Z;
            pc[15 + i * 4] = pokes[i].W;
        }
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
        byte[]? bytesNoPoke = null;
        if (substeps > 1)
        {
            var pc2 = (float[])pc.Clone();
            Array.Clear(pc2, 12, 16);   // sub-ticks 2..S run force-free
            bytesNoPoke = new byte[pc2.Length * sizeof(float)];
            Buffer.BlockCopy(pc2, 0, bytesNoPoke, 0, bytesNoPoke.Length);
        }
        // v1 foam kernel pc grew to 12 floats (deposit + drift stages); zeros = original behaviour
        float[] fpcF = { MnaGrid.X, MnaGrid.Y, _foamDecay, _foamGain, _foamThresh, 0f, 0f, 0f, 0f, 0f, 0f, 0f };
        var fbytes = new byte[fpcF.Length * sizeof(float)];
        Buffer.BlockCopy(fpcF, 0, fbytes, 0, fbytes.Length);
        // v2 push: cascade + v3a swell-drift band data (bands 0/1 = the big orbital
        // movers, amp/speed multipliers already applied by RebuildField).
        var (bd0, bk0, bs0, bst0) = BandParams(0);
        var (bd1, bk1, bs1, bst1) = BandParams(1);
        float[] fpc2F =
        {
            MnaGrid.X, MnaGrid.Y,
            _f2DecayW, _f2DecayL, _f2DecayS, _f2XferWL, _f2XferLS,
            _foamGain, _f2Thresh,
            _field.Time, _f2Drift, cell,
            _mnaOrigin.X, _mnaOrigin.Y, _mnaTexOff.X, _mnaTexOff.Y,
            _swirlOn && _swReady ? _swAdvectScale : 0f, swCell, _swOrigin.X, _swOrigin.Y, _swTexOff.X, _swTexOff.Y,
            bd0.X, bd0.Y, bk0, bs0, bst0,
            bd1.X, bd1.Y, bk1, bs1, bst1,
        };
        var f2bytes = new byte[fpc2F.Length * sizeof(float)];
        Buffer.BlockCopy(fpc2F, 0, f2bytes, 0, f2bytes.Length);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            _mnaSolver?.Step(bytes, iters, measure);
            for (int s = 1; s < substeps; s++)
            {
                _mnaSolver?.Step(bytesNoPoke!, iters, false);
            }
            if (_swirlOn)
            {
                StepSwirlRt(swBytes, swIters);   // fresh velocity before the foam samples it
            }
            StepFoamRt(fbytes, f2bytes);
            ReadbackHeightsRt();
        }));

        if (measure && _mnaReadout != null)
        {
            _mnaReadout.Text = $"MNA ‖b−Ax‖ {(_mnaSolver?.LastResidual ?? 0f):E3} · {_mnaIters} sw · {Engine.GetFramesPerSecond():0}fps";
        }
    }

    // Boat height source = composite swell + the reactive wake (scene 17's
    // ReactiveHeightField idea on our toroidal window): the hull bobs on the sum.
    private sealed class SumField : CompositeGerstner
    {
        public Func<float, float, float>? Extra;

        public override Vector3 Displacement(Vector3 pos)
        {
            Vector3 d = base.Displacement(pos);
            if (Extra != null)
            {
                d.Y += Extra(pos.X, pos.Z);
            }
            return d;
        }
    }

    // Render thread: cache the height field for buoyancy. ASYNC readback — the sync
    // TextureGetData flush cost 60 -> 27 fps at 512²; async delivers a couple frames
    // late, which the buoyancy spring can't feel.
    private void ReadbackHeightsRt()
    {
        var rid = _mnaSolver?.HeightRid ?? default;
        if (!rid.IsValid)
        {
            return;
        }
        var rd = RenderingServer.GetRenderingDevice();
        rd.TextureGetDataAsync(rid, 0, Callable.From((byte[] data) =>
        {
            var target = NextHeightsBuffer();
            Buffer.BlockCopy(data, 0, target, 0, Math.Min(data.Length, target.Length * sizeof(float)));
            _mnaPrev = _mnaCurr;
            _mnaCurr = target;
            _mnaBlend = 0f;
        }));
    }

    // The buffer that is neither of the two published refs — safe to overwrite.
    private float[] NextHeightsBuffer()
    {
        _hbA ??= new float[MnaGrid.X * MnaGrid.Y];
        _hbB ??= new float[MnaGrid.X * MnaGrid.Y];
        _hbC ??= new float[MnaGrid.X * MnaGrid.Y];
        if (_hbA != _mnaCurr && _hbA != _mnaPrev)
        {
            return _hbA;
        }
        if (_hbB != _mnaCurr && _hbB != _mnaPrev)
        {
            return _hbB;
        }
        return _hbC;
    }

    // Bilinear toroidal sample of one cached grid.
    private float SampleGrid(float[] h, float gx, float gz)
    {
        int n = MnaGrid.X;
        int x0 = Mathf.FloorToInt(gx);
        int z0 = Mathf.FloorToInt(gz);
        float fx = gx - x0;
        float fz = gz - z0;
        int xa = ((x0 % n) + n) % n, xb = (xa + 1) % n;
        int za = ((z0 % n) + n) % n, zb = (za + 1) % n;
        float h0 = Mathf.Lerp(h[za * n + xa], h[za * n + xb], fx);
        float h1 = Mathf.Lerp(h[zb * n + xa], h[zb * n + xb], fx);
        return Mathf.Lerp(h0, h1, fz);
    }

    // Physics thread: bilinear toroidal sample of the cached field, cross-faded
    // between the last two readbacks, in world metres (× displacement × edge fade),
    // scaled by the influence knob.
    private float SampleMnaWorld(float x, float z)
    {
        var curr = _mnaCurr;
        if (curr == null || _wakeInfluence == 0f)
        {
            return 0f;
        }
        float u = (x - _mnaOrigin.X) / MnaWindow;
        float v = (z - _mnaOrigin.Y) / MnaWindow;
        if (u < 0f || u > 1f || v < 0f || v > 1f)
        {
            return 0f;
        }
        float edge = Mathf.Min(Mathf.Min(u, 1f - u), Mathf.Min(v, 1f - v));
        float fade = Mathf.SmoothStep(0f, _mnaEdgeFade, edge);
        float gx = u * MnaGrid.X + _mnaTexOff.X;
        float gz = v * MnaGrid.Y + _mnaTexOff.Y;
        float hh = SampleGrid(curr, gx, gz);
        var prev = _mnaPrev;
        float blend = _mnaBlend;
        if (prev != null && blend < 1f)
        {
            hh = Mathf.Lerp(SampleGrid(prev, gx, gz), hh, blend);
        }
        return hh * _mnaDisplacement * fade * _wakeInfluence;
    }

    // One composite band's drift parameters for the foam kernel (same angle map as
    // CompositeGerstner.Wave).
    private (Vector2 d, float k, float s, float st) BandParams(int i)
    {
        float direction = _field.Dirs[i] * 2f - 1f;
        var d = new Vector2(Mathf.Cos(Mathf.Pi * direction), Mathf.Sin(Mathf.Pi * direction)).Normalized();
        return (d, Mathf.Tau / _field.Wavelengths[i], _field.Speeds[i], _field.Steepnesses[i]);
    }

    // Boat-local polar: rotate the poke's base axis (ahead or astern) toward starboard.
    private static Vector3 PokeDir(Vector3 baseDir, Vector3 right, float angleDeg)
    {
        float a = Mathf.DegToRad(angleDeg);
        return (baseDir * Mathf.Cos(a) + right * Mathf.Sin(a)).Normalized();
    }

    // World XZ -> a poke in sim cells. Skipped outside the window MINUS the sponge
    // margin: forcing inside the absorbing band would pile into a static mound
    // faster than the sponge's losses can flatten it.
    private void AddWorldPoke(List<Vector4> pokes, Vector3 world, float radius, float w)
    {
        if (pokes.Count >= 4)
        {
            return;
        }
        float m = (_spongeWidth + radius) / MnaGrid.X;
        var uv = (new Vector2(world.X, world.Z) - _mnaOrigin) / MnaWindow;
        if (uv.X < m || uv.X > 1f - m || uv.Y < m || uv.Y > 1f - m)
        {
            return;
        }
        pokes.Add(new Vector4(uv.X * MnaGrid.X, uv.Y * MnaGrid.Y, radius, w));
    }

    // Mouse ray -> the y=0 water datum, or null off-screen.
    private Vector3? MouseWaterHit()
    {
        var mp = GetViewport().GetMousePosition();
        var plane = new Plane(Vector3.Up, 0f);
        return plane.IntersectsRay(_cam.ProjectRayOrigin(mp), _cam.ProjectRayNormal(mp));
    }

    public override void _ExitTree()
    {
        if (_mnaTex != null)
        {
            _mnaTex.TextureRdRid = default;
        }
        if (_foamDisplayTex != null)
        {
            _foamDisplayTex.TextureRdRid = default;
        }
        if (_foam2DisplayTex != null)
        {
            _foam2DisplayTex.TextureRdRid = default;
        }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            // Foam first: _foamSetH references the solver's height texture, so freeing
            // the solver before it auto-invalidates the set -> "free invalid ID".
            _foamReady = false;
            _foam2Ready = false;
            var rd = RenderingServer.GetRenderingDevice();
            _swReady = false;
            foreach (var r in new[]
            {
                _foamSetIn, _foamSetOut, _foamSetH, _foamSetHp, _foamState, _foamTmp, _foamShader,
                _foam2SetIn, _foam2SetOut, _foam2SetH, _foam2SetSwirl, _foam2State, _foam2Tmp, _foam2Shader,
                _sAdvIn, _sAdvOut, _sDivV, _sDivOut, _sJacA0, _sJacB1, _sJacB0, _sJacA1, _sJacDiv,
                _sGradV, _sGradOut, _sGradP,
                _swVelA, _swVelB, _swDivTex, _swPrA, _swPrB,
                _swAdvSh, _swDivSh, _swJacSh, _swGradSh,
            })
            {
                if (r.IsValid)
                {
                    rd.FreeRid(r);
                }
            }
            _mnaSolver?.Free();
        }));
    }

    public override void _Process(double delta)
    {
        // Wave time advances at RENDER rate — per physics tick it beats against any
        // fps that isn't a clean multiple of 60 (the 55 fps fullscreen stutter).
        _field.Time += (float)delta;
        _mat.SetShaderParameter("wave_time", _field.Time);
        UpdateCamera((float)delta);
        _fpsT += (float)delta;
        if (_fpsT >= 0.25f)
        {
            _fpsT = 0f;
            _fpsLabel.Text = $"{Engine.GetFramesPerSecond():0} fps";
        }
    }

    private void UpdateFloorShadow()
    {
        if (_floorMat == null || _boat == null)
        {
            return;
        }
        _floorMat.SetShaderParameter("boat_pos", _boat.GlobalPosition);
        _floorMat.SetShaderParameter("boat_yaw", _boat.Yaw);
        _floorMat.SetShaderParameter("wave_time", _field.Time * _field.Speed);
        _floorMat.SetShaderParameter("steepness", _field.Steepness);
        _floorMat.SetShaderParameter("wl", _field.Wavelength);
        _floorMat.SetShaderParameter("wave_dirs", new Vector4(
            _field.Directions[0], _field.Directions[1], _field.Directions[2], _field.Directions[3]));
    }

    // Fill the composite bands (× live amp/speed) and mirror them to the shader. Also set the
    // base GerstnerField members to the dominant swell so the caustic-floor hull shadow (which
    // reads steepness/wavelength/directions) still ripples sensibly.
    private void RebuildField()
    {
        _field.ClearWaves();
        foreach (var b in Bands)
        {
            _field.AddWave(b[0], b[1] * _waveAmp, b[2] * _waveSpeedScale, b[3]);
        }
        _field.Wavelength = Bands[0][0];
        _field.Steepness = Bands[0][1] * _waveAmp;
        _field.Speed = Bands[0][2] * _waveSpeedScale;
        _field.Directions = new[] { Bands[0][3], Bands[1][3], Bands[2][3], Bands[3][3] };
        if (_mat != null)
        {
            SyncShaderWaves();
        }
    }

    private void SyncShaderWaves()
    {
        _mat.SetShaderParameter("wave_count", _field.Wavelengths.Count);
        _mat.SetShaderParameter("wavelengths", _field.WavelengthsArr);
        _mat.SetShaderParameter("steepnesses", _field.SteepnessesArr);
        _mat.SetShaderParameter("speeds", _field.SpeedsArr);
        _mat.SetShaderParameter("dirs", _field.DirsArr);
        _mat.SetShaderParameter("wave_time", _field.Time);
    }

    private void BuildEnvironment()
    {
        _sun = new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-42f, -120f, 0f),
            LightEnergy = 1.35f,
        };
        AddChild(_sun);

        var skyMat = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.30f, 0.48f, 0.80f),
            SkyHorizonColor = new Color(0.74f, 0.82f, 0.88f),
            GroundBottomColor = new Color(0.30f, 0.34f, 0.36f),
        };
        var sky = new Sky { SkyMaterial = skyMat };
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = sky,
            AmbientLightSource = Godot.Environment.AmbientSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Agx,
        };
        var we = new WorldEnvironment { Environment = env };
        AddChild(we);

        // Always-visible fps, bottom-left.
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

        _cam = new Camera3D
        {
            Far = 3000f,
            Current = true,
            Position = new Vector3(0f, 3.5f, 8f),
            // driven at render rate in _Process — physics interpolation would re-smooth
            // it from stale physics snapshots (engine warns; same fix as scene 17)
            PhysicsInterpolationMode = Node.PhysicsInterpolationModeEnum.Off,
        };
        AddChild(_cam);
    }

    private void BuildSeabed()
    {
        var mi = _bedMi = new MeshInstance3D();
        var plane = new PlaneMesh
        {
            Size = new Vector2(800, 800),
            SubdivideWidth = 32,
            SubdivideDepth = 32,
        };
        mi.Mesh = plane;
        mi.Position = new Vector3(0f, BedDepth, 0f);
        mi.ExtraCullMargin = 80f;

        _floorMat = new ShaderMaterial { Shader = GD.Load<Shader>(FloorShader) };
        _floorMat.SetShaderParameter("albedo", new Color(0.76f, 0.68f, 0.50f));
        _floorMat.SetShaderParameter("caustic_scale", 1.0f);
        _floorMat.SetShaderParameter("caustic_strength", 1.3f);
        _floorMat.SetShaderParameter("caustic_speed", 0.6f);
        _floorMat.SetShaderParameter("hull_half", new Vector2(1.0f, 2.6f));
        _floorMat.SetShaderParameter("sun_dir", -_sun.GlobalTransform.Basis.Z);
        _floorMat.SetShaderParameter("shadow_darkness", 0.75f);
        _floorMat.SetShaderParameter("shadow_softness", 0.4f);
        _floorMat.SetShaderParameter("shadow_size", 1.5f);
        _floorMat.SetShaderParameter("shadow_refracted", true);
        _floorMat.SetShaderParameter("shadow_refract_amount", 1.0f);
        mi.MaterialOverride = _floorMat;
        AddChild(mi);
    }

    private void BuildWater()
    {
        var mi = _waterMi = new MeshInstance3D();
        var plane = new PlaneMesh
        {
            Size = new Vector2(WaterPlaneSize, WaterPlaneSize),
            SubdivideWidth = WaterPlaneSubdiv,
            SubdivideDepth = WaterPlaneSubdiv,
        };
        mi.Mesh = plane;
        mi.ExtraCullMargin = 40f;
        _mat = new ShaderMaterial { Shader = GD.Load<Shader>(WaterShader) };

        // Scene 15's STAMPED scene-08 (ameye) preset — the water LOOK (unchanged). The
        // composite shader carries no single steepness/wavelength/wave_speed (the band
        // arrays replace them), so only the look params are set here.
        _mat.SetShaderParameter("water_roughness", 0.035f);
        _mat.SetShaderParameter("foam_distance", 1.575f);
        _mat.SetShaderParameter("foam_crest", 0.900f);
        _mat.SetShaderParameter("normal_strength", 0.54f);
        _mat.SetShaderParameter("refraction_strength", 1.14f);
        _mat.SetShaderParameter("specular_smoothness", 0.485f);

        _mat.SetShaderParameter("depth_fade_distance", 11.0f);
        _mat.SetShaderParameter("water_color", new Color(0.09f, 0.52f, 0.62f));
        _mat.SetShaderParameter("shallow_color", new Color(0.46f, 0.82f, 0.80f));
        SyncShaderWaves();
        _mat.SetShaderParameter("foam_tex", GD.Load<Texture2D>(FoamTex));
        _mat.SetShaderParameter("normal_tex", GD.Load<Texture2D>(NormalTex));
        _mat.SetShaderParameter("sun_direction", _sun.GlobalTransform.Basis.Z);

        _mat.SetShaderParameter("enable_depth_fade", true);
        _mat.SetShaderParameter("enable_shore_color", true);
        _mat.SetShaderParameter("enable_foam", false);
        _mat.SetShaderParameter("enable_normal_maps", true);
        _mat.SetShaderParameter("enable_refraction", true);
        _mat.SetShaderParameter("enable_lighting", false);

        _mnaTex = new Texture2Drd();
        _foamDisplayTex = new Texture2Drd();
        _foam2DisplayTex = new Texture2Drd();
        _mat.SetShaderParameter("mna_tex", _mnaTex);
        _mat.SetShaderParameter("mna_foam_tex", _foamDisplayTex);
        _mat.SetShaderParameter("mna_foam2_tex", _foam2DisplayTex);
        _mat.SetShaderParameter("mna_tex_size", new Vector2(MnaGrid.X, MnaGrid.Y));
        _mat.SetShaderParameter("mna_origin", _mnaOrigin);
        _mat.SetShaderParameter("mna_window", MnaWindow);
        _mat.SetShaderParameter("mna_displacement", _mnaDisplacement);
        _mat.SetShaderParameter("mna_normal_strength", 16.0f);
        _mat.SetShaderParameter("mna_foam_intensity", 0.59f);

        mi.MaterialOverride = _mat;
        AddChild(mi);
    }

    private void BuildBoat()
    {
        // Ryan's live-tuned feel (Copy values, baked): stiffer buoyancy, always-on
        // gravity, full conform — the boat presses into the sea instead of skating.
        _boat = new BoatRider
        {
            Field = _field,
            KBuoy = 51.9f,
            CLin = 7.35f,
            GravityAlways = true,
            Conform = 0.835f,
            TurnBank = 0.564f,  // lean into turns
            Grip = 8.495f,      // course chases heading — lower = nose leads, drifty
            TrimAngle = 0.245f, // bow-up at top speed (~14°)
            SpeedLift = 0.004f, // ride-height gain ~off (Ryan's tune)
            TrimPivot = -1.725f, // hinge aft of centre: bow kicks up, transom stays planted
            TurnPivot = -2.5f,  // turns pivot at the stern: the nose sweeps onto the heading
        };

        var hullMat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.55f, 0.16f, 0.14f),
            Roughness = 0.65f,
        };

        var hull = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 5.0f) },
            MaterialOverride = hullMat,
        };
        _boat.AddChild(hull);

        var prow = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(2.0f, 0.7f, 1.4f) },
            MaterialOverride = hullMat,
            Position = new Vector3(0f, 0f, -2.9f),
            Scale = new Vector3(0.25f, 1.0f, 1.0f),
        };
        _boat.AddChild(prow);

        var deck = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.7f, 0.12f, 4.4f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.88f, 0.83f, 0.70f),
                Roughness = 0.8f,
            },
            Position = new Vector3(0f, 0.40f, 0.2f),
        };
        _boat.AddChild(deck);

        var cabin = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(1.3f, 0.9f, 1.6f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.92f, 0.94f, 0.96f),
                Roughness = 0.7f,
            },
            Position = new Vector3(0f, 0.85f, 1.2f),
        };
        _boat.AddChild(cabin);

        var col = new CollisionShape3D
        {
            Shape = new BoxShape3D { Size = new Vector3(2.0f, 0.7f, 5.0f) },
        };
        _boat.AddChild(col);

        AddChild(_boat);
        _boat.GlobalPosition = Vector3.Zero;
    }

    private void BuildIslands()
    {
        var sand = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.80f, 0.72f, 0.52f),
            Roughness = 0.95f,
        };
        // [px, pz, radius, height, cap] per island.
        float[][] specs =
        {
            new[] { 70f, -95f, 40f, 24f, 3.5f },
            new[] { -90f, -120f, 34f, 20f, 2.5f },
            new[] { 60f, 140f, 50f, 26f, 4.0f },
            new[] { -140f, 50f, 30f, 18f, 2.0f },
            new[] { 150f, 90f, 38f, 22f, 3.0f },
            new[] { -55f, 120f, 28f, 16f, 2.0f },
        };
        foreach (var s in specs)
        {
            float px = s[0];
            float pz = s[1];
            float r = s[2];
            float h = s[3];
            float cap = s[4];
            float centerY = cap - h * 0.5f;

            var mi = new MeshInstance3D
            {
                Mesh = new SphereMesh
                {
                    Radius = r,
                    Height = h,
                    RadialSegments = 24,
                    Rings = 12,
                },
                MaterialOverride = sand,
                Position = new Vector3(px, centerY, pz),
            };
            AddChild(mi);

            float b = h * 0.5f;
            float ratio = Mathf.Clamp(Mathf.Abs(centerY) / b, 0f, 0.999f);
            float foot = r * Mathf.Sqrt(1.0f - ratio * ratio);
            var body = new StaticBody3D { Position = new Vector3(px, 0f, pz) };
            var ccol = new CollisionShape3D
            {
                Shape = new CylinderShape3D { Radius = foot, Height = 40.0f },
            };
            body.AddChild(ccol);
            AddChild(body);
        }
    }

    private void UpdateCamera(float delta)
    {
        if (_boat == null)
        {
            return;
        }
        Vector3 bp = _boat.GlobalPosition;
        if (!_camFocusInit)
        {
            _camFocus = bp;
            _camFocusInit = true;
        }
        float kxz = 1.0f - Mathf.Exp(-_camFollowXz * delta);
        float ky = 1.0f - Mathf.Exp(-_camSmoothY * delta);
        _camFocus.X = Mathf.Lerp(_camFocus.X, bp.X, kxz);
        _camFocus.Z = Mathf.Lerp(_camFocus.Z, bp.Z, kxz);
        _camFocus.Y = Mathf.Lerp(_camFocus.Y, bp.Y, ky);

        Vector3 back = _boat.GlobalTransform.Basis.Z;
        back.Y = 0f;
        back = back.Normalized();
        Vector3 targetPos = _camFocus + back * 8.0f + Vector3.Up * 3.5f;
        float k = 1.0f - Mathf.Exp(-_camPosRate * delta);
        _cam.GlobalPosition = _cam.GlobalPosition.Lerp(targetPos, k);
        _cam.LookAt(_camFocus + Vector3.Up * 0.6f, Vector3.Up);
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "50 · Boat + MNA reactive window",
            "Arrow keys: throttle + steer. Scene 16's composite Gerstner sea (all its knobs kept), plus a 48 m CN-MNA sim window riding the swell — h = gerstner + mna. The wake is two steerable pokes (bow ridge + stern trough), each with angle / distance / strength knobs. MNA knobs at the bottom.");

        ui.AddSlider("Wave amplitude x", 0f, 2f, _waveAmp, v =>
        {
            _waveAmp = v;
            RebuildField();
        });
        ui.AddSlider("Wave speed x", 0f, 2.5f, _waveSpeedScale, v =>
        {
            _waveSpeedScale = v;
            RebuildField();
        });
        ui.AddSlider("Cam vertical smooth (low = steadier)", 0.3f, 12f, _camSmoothY, v => _camSmoothY = v);
        ui.AddSlider("Cam follow XZ", 1f, 20f, _camFollowXz, v => _camFollowXz = v);
        ui.AddSlider("Max speed", 4f, 30f, _boat.MaxSpeed, v => _boat.MaxSpeed = v);
        ui.AddSlider("Max turn speed", 0.5f, 4f, _boat.MaxTurnSpeed, v => _boat.MaxTurnSpeed = v);
        ui.AddSlider("Hull offset (sit depth)", -0.4f, 1f, _boat.HullOffset, v => _boat.HullOffset = v);

        ui.AddSlider("Buoyancy k", 0f, 60f, _boat.KBuoy, v => _boat.KBuoy = v);
        ui.AddSlider("Linear damp", 0f, 30f, _boat.CLin, v => _boat.CLin = v);
        ui.AddSlider("Slam (quadratic)", 0f, 5f, _boat.CSlam, v => _boat.CSlam = v);
        ui.AddSlider("Launch kick", 0f, 5f, _boat.KWave, v => _boat.KWave = v);
        ui.AddSlider("Gravity", 0f, 20f, _boat.Gravity, v => _boat.Gravity = v);
        ui.AddToggle("Gravity always (else airborne only)", _boat.GravityAlways, on => _boat.GravityAlways = on);

        ui.AddSlider("Conform (upright bias)", 0f, 1f, _boat.Conform, v => _boat.Conform = v);
        ui.AddSlider("Lookahead", 0f, 8f, _boat.LookaheadBase, v => _boat.LookaheadBase = v);
        ui.AddSlider("Ang stiffness", 0f, 100f, _boat.AngStiffness, v => _boat.AngStiffness = v);
        ui.AddSlider("Ang damp", 0f, 30f, _boat.AngDamp, v => _boat.AngDamp = v);
        ui.AddSlider("Ang slam", 0f, 5f, _boat.AngSlam, v => _boat.AngSlam = v);
        ui.AddSlider("Turn bank (lean into turns; − = out)", -1.2f, 1.2f, _boat.TurnBank, v => _boat.TurnBank = v);
        ui.AddSlider("Grip (course chases heading; low = drifty)", 0.5f, 20.0f, _boat.Grip, v => _boat.Grip = v);
        ui.AddSlider("Trim · bow-up @ top speed (rad)", 0.0f, 0.35f, _boat.TrimAngle, v => _boat.TrimAngle = v);
        ui.AddSlider("Trim · lift @ top speed (m)", 0.0f, 0.8f, _boat.SpeedLift, v => _boat.SpeedLift = v);
        ui.AddSlider("Trim · hinge along hull (− stern, + bow)", -2.5f, 2.5f, _boat.TrimPivot, v => _boat.TrimPivot = v);
        ui.AddSlider("Turn pivot along hull (− stern, + bow)", -2.5f, 2.5f, _boat.TurnPivot, v => _boat.TurnPivot = v);

        ui.AddSlider("Caustic strength", 0f, 2f, 1.3f, v => _floorMat.SetShaderParameter("caustic_strength", v));
        ui.AddSlider("Water clarity (depth fade)", 3f, 20f, 11.0f, v => _mat.SetShaderParameter("depth_fade_distance", v));
        ui.AddSlider("Shadow darkness", 0f, 1f, 0.75f, v => _floorMat.SetShaderParameter("shadow_darkness", v));
        ui.AddSlider("Shadow size", 0.5f, 4f, 1.5f, v => _floorMat.SetShaderParameter("shadow_size", v));
        ui.AddToggle("Shadow refracted (ripple)", true, on => _floorMat.SetShaderParameter("shadow_refracted", on));

        // --- Water look: scene 13's stage toggles + tuners (defaults = BuildWater's stamp) ---
        ui.AddToggle("2 · Depth fade", true, p => _mat.SetShaderParameter("enable_depth_fade", p));
        ui.AddToggle("3 · HSV shore color", true, p => _mat.SetShaderParameter("enable_shore_color", p));
        ui.AddToggle("4 · Foam (shore/crest)", false, p => _mat.SetShaderParameter("enable_foam", p));
        ui.AddToggle("5 · Normal maps", true, p => _mat.SetShaderParameter("enable_normal_maps", p));
        ui.AddToggle("5 · Refraction", true, p => _mat.SetShaderParameter("enable_refraction", p));
        ui.AddToggle("6 · Toon lighting", false, p => _mat.SetShaderParameter("enable_lighting", p));
        ui.AddSlider("Roughness", 0f, 1f, 0.035f, v => _mat.SetShaderParameter("water_roughness", v));
        ui.AddSlider("Foam distance", 0f, 5f, 1.575f, v => _mat.SetShaderParameter("foam_distance", v));
        ui.AddSlider("Foam crest", 0f, 2f, 0.900f, v => _mat.SetShaderParameter("foam_crest", v));
        ui.AddSlider("Normal map strength", 0f, 2f, 0.54f, v => _mat.SetShaderParameter("normal_strength", v));
        ui.AddSlider("Refraction", 0f, 4f, 1.14f, v => _mat.SetShaderParameter("refraction_strength", v));
        ui.AddSlider("Specular smooth", 0f, 1f, 0.485f, v => _mat.SetShaderParameter("specular_smoothness", v));

        // --- MNA reactive layer ---
        _mnaReadout = ui.AddReadout("MNA residual —");
        ui.AddToggle("Auto-cruise (demo; arrows override)", _autoCruise, v => _autoCruise = v);
        ui.AddToggle("Mesh follows boat (A/B jank test)", _meshFollow, v => _meshFollow = v);

        // 3D render-resolution scaling — the sim is fixed-cost, so fullscreen fps is
        // pure per-pixel work; upscaling attacks exactly that term. MetalFX needs the
        // native Metal driver (falls back with a console warning on MoltenVK — use FSR 2).
        ui.AddSlider("Render scale (3D)", 0.4f, 1.0f, 0.4f, v => GetViewport().Scaling3DScale = v);
        ui.AddOptions("Upscaler (3D)", new[] { "Bilinear", "FSR 1", "FSR 2", "MetalFX spatial", "MetalFX temporal" }, 3,
            i => GetViewport().Scaling3DMode = (Viewport.Scaling3DModeEnum)i);
        ui.AddSlider("Wake strength", 0f, 0.02f, _wakeStrength, v => _wakeStrength = v);
        ui.AddSlider("Wake radius (px)", 2f, 16f, _wakeRadius, v => _wakeRadius = v);
        ui.AddSlider("Wake side offset (m, + right)", -3f, 3f, _wakeSideOffset, v => _wakeSideOffset = v);
        ui.AddToggle("Bow poke", _bowPokeOn, v => _bowPokeOn = v);
        ui.AddSlider("Bow angle (° off ahead, + right)", -120f, 120f, _bowAngle, v => _bowAngle = v);
        ui.AddSlider("Bow distance (m)", 0f, 8f, _bowDist, v => _bowDist = v);
        ui.AddSlider("Bow strength × (+up/−down)", -2f, 2f, _bowScale, v => _bowScale = v);
        ui.AddSlider("Bow spread (m, between the pair)", 0f, 6f, _bowSpread, v => _bowSpread = v);
        ui.AddSlider("Stern angle (° off astern, + right)", -120f, 120f, _sternAngle, v => _sternAngle = v);
        ui.AddSlider("Stern distance (m)", 0f, 8f, _sternDist, v => _sternDist = v);
        ui.AddSlider("Stern strength × (+up/−down)", -2f, 2f, _sternScale, v => _sternScale = v);
        ui.AddToggle("Mouse click poke", _mousePoke, v => _mousePoke = v);
        ui.AddToggle("MNA auto-poke", _mnaAutoPoke, v => _mnaAutoPoke = v);
        ui.AddToggle("MNA debug colormap", false, v => _mat.SetShaderParameter("mna_debug", v ? 1.0f : 0.0f));
        ui.AddSlider("MNA debug gain", 1f, 60f, 10f, v => _mat.SetShaderParameter("mna_debug_gain", v));
        ui.AddSlider("MNA displacement", 0f, 6f, _mnaDisplacement, v =>
        {
            _mnaDisplacement = v;
            _mat.SetShaderParameter("mna_displacement", v);
        });
        ui.AddSlider("Buoyancy feels wake × (0 = off)", 0f, 2f, _wakeInfluence, v => _wakeInfluence = v);
        ui.AddSlider("Bob smoothing (s)", 0f, 0.25f, _mnaBlendTime, v => _mnaBlendTime = v);
        ui.AddSlider("MNA normal strength", 0f, 24f, 16.0f, v => _mat.SetShaderParameter("mna_normal_strength", v));
        ui.AddSlider("MNA scheme  BE 0 → 1 CN (rings)", 0f, 1f, _mnaCn, v => _mnaCn = v);
        ui.AddSlider("MNA wave speed (c)", 0.2f, 4f, _mnaC, v => _mnaC = v);
        ui.AddSlider("MNA sim dt", 0.05f, 2f, _mnaDt, v => _mnaDt = v);
        ui.AddSlider("MNA damping", 0f, 2f, _mnaDamping, v => _mnaDamping = v);
        ui.AddSlider("MNA rest leak κ", 0f, 0.2f, _mnaLeak, v => _mnaLeak = v);
        ui.AddSlider("MNA sweeps / tick", 1, 60, _mnaIters, v => _mnaIters = (int)v);
        ui.AddSlider("MNA substeps (oversampling)", 1, 4, _mnaSubsteps, v => _mnaSubsteps = (int)v);
        ui.AddSlider("MNA poke radius (px)", 2f, 24f, _mnaPokeRadius, v => _mnaPokeRadius = v);
        ui.AddSlider("MNA poke strength", 0.05f, 4f, _mnaPokeStrength, v => _mnaPokeStrength = v);
        ui.AddSlider("MNA edge fade", 0.02f, 0.5f, _mnaEdgeFade, v =>
        {
            _mnaEdgeFade = v;
            _mat.SetShaderParameter("mna_edge_fade", v);
        });
        ui.AddSlider("Sponge width (px)", 4f, 48f, _spongeWidth, v => _spongeWidth = v);
        ui.AddSlider("Sponge damping", 0f, 1f, _spongeDamp, v => _spongeDamp = v);
        ui.AddToggle("Foam v1 (flat white)", _foam1On, v =>
        {
            _foam1On = v;
            _mat.SetShaderParameter("foam1_enabled", v);
        });
        ui.AddToggle("Foam v2 (material + cascade)", _foam2On, v =>
        {
            _foam2On = v;
            _mat.SetShaderParameter("foam2_enabled", v);
        });
        ui.AddSlider("Foam gain (curvature, v1+v2)", 0f, 200f, _foamGain, v => _foamGain = v);
        ui.AddSlider("v1 threshold", 0f, 0.08f, _foamThresh, v => _foamThresh = v);
        ui.AddSlider("v2 threshold", 0f, 0.08f, _f2Thresh, v => _f2Thresh = v);
        ui.AddSlider("v3 swell drift ×", 0f, 3f, _f2Drift, v => _f2Drift = v);
        ui.AddToggle("v3 swirl (milk-in-coffee)", _swirlOn, v => _swirlOn = v);
        ui.AddSlider("Swirl carries foam ×", 0f, 3f, _swAdvectScale, v => _swAdvectScale = v);
        ui.AddSlider("Swirl jet (propwash m/s)", 0f, 8f, _swJet, v => _swJet = v);
        ui.AddSlider("Swirl jet radius (m)", 0.5f, 4f, _swJetRadius, v => _swJetRadius = v);
        ui.AddSlider("Swirl turn shed", 0f, 6f, _swShed, v => _swShed = v);
        ui.AddSlider("Swirl dissipation", 0.97f, 1f, _swDissip, v => _swDissip = v);
        ui.AddSlider("Swirl pressure iters", 4, 40, _swIters, v => _swIters = (int)v);
        ui.AddSlider("v1 decay", 0.9f, 0.999f, _foamDecay, v => _foamDecay = v);
        ui.AddSlider("v1 intensity", 0f, 2f, 0.59f, v => _mat.SetShaderParameter("mna_foam_intensity", v));
        ui.AddSlider("v2 whitecap decay", 0.85f, 0.995f, _f2DecayW, v => _f2DecayW = v);
        ui.AddSlider("v2 lace decay", 0.97f, 0.9995f, _f2DecayL, v => _f2DecayL = v);
        ui.AddSlider("v2 milk decay", 0.95f, 0.999f, _f2DecayS, v => _f2DecayS = v);
        ui.AddSlider("v2 whitecap → lace", 0f, 1f, _f2XferWL, v => _f2XferWL = v);
        ui.AddSlider("v2 lace → milk", 0f, 1f, _f2XferLS, v => _f2XferLS = v);
        ui.AddSlider("v2 intensity", 0f, 2f, 1.0f, v => _mat.SetShaderParameter("foam2_intensity", v));
        ui.AddSlider("v2 erosion", 0f, 1.5f, 0.65f, v => _mat.SetShaderParameter("foam2_erosion", v));
        ui.AddSlider("v2 erosion scale", 0.05f, 1.5f, 0.35f, v => _mat.SetShaderParameter("foam2_erosion_scale", v));
        ui.AddSlider("v2 foam roughness", 0f, 1f, 0.7f, v => _mat.SetShaderParameter("foam2_roughness", v));
        ui.AddSlider("v2 puff (m)", 0f, 0.3f, 0.06f, v => _mat.SetShaderParameter("foam2_puff", v));
        ui.AddSlider("v2 milk strength", 0f, 2f, 0.5f, v => _mat.SetShaderParameter("foam2_milk", v));
    }
}
