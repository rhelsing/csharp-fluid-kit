using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using GodotCsharpExperiments.Lib;

// Scene 53 — UNDERWATER LAB. Fork of scene 50 (BoatMna), which stays untouched.
//
// The test bed for a MODULAR underwater mode. Everything scene 50 has that makes a
// surface — composite Gerstner + the CN-MNA reactive window, the ameye look, the caustic
// seabed and islands — MINUS the boat, and with a free camera in place of the chase cam.
//
// WHY STRIPPED. The thing being proven is a BOUNDARY: the camera partially submerged, part
// of the near plane under the surface and part above it. To see that you have to park the
// camera exactly at the waterline and hold it there, which a bobbing boat can never do.
// Nothing here is about boats, so the boat is noise.
//
// WAVES. `Wave amplitude x` is a MULTIPLIER over every band's steepness, so at 0 the six
// per-band sliders are all multiplied by zero and appear broken. It shipped at 0 during
// proof 1 (a flat sea makes a boundary verifiable by eye); it is 1 now that the composite
// is the thing being looked at.
//
// PORT EDITS from 50, all forced by removing the boat:
//   · the toroidal MNA window follows the CAMERA. 50 calls it a "camera-following window"
//     but actually follows the boat; with no boat, the camera is the anchor it always meant.
//   · boat wake pokes and the v3b propwash jet are gone — they were hull forcing. The MNA
//     solver, foam and swirl all still step, so the surface is still a live sim; it just has
//     nothing stirring it but the mouse (`_mousePoke`) until we add something.
//   · chase camera -> FreeCam, plus a camera-Y slider for parking on the waterline.
//
// See it: tools/godot-mono.sh --path . res://scenes/53_underwater_lab.tscn
public partial class UnderwaterLab : Node3D
{
    // PORT EDIT: scene-53 fork, so scene 50 keeps its shader byte-for-byte. The fork adds
    // only what the uwkit underside chunk requires.
    private const string WaterShader = "res://shaders/ameye_water_uw.gdshader";
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
    private static readonly float[][] BandDefaults =
    {
        new[] { 34f, 0.13f, 1.10f, 0.12f },
        new[] { 26f, 0.10f, 1.00f, 0.28f },
        new[] { 14f, 0.10f, 1.05f, 0.52f },
        new[] { 9f, 0.09f, 1.15f, 0.66f },
        new[] { 5f, 0.07f, 1.35f, 0.82f },
        new[] { 3f, 0.05f, 1.60f, 0.40f },
    };

    // Live, editable copy of the composite. Scene 50 bakes these as constants because its
    // sea is a fixed look; this is a LAB, so every band is a slider — and the kit reads the
    // same numbers, so the fog and the waterline follow whatever you dial.
    private readonly float[][] _bands = BandDefaults.Select(b => (float[])b.Clone()).ToArray();

    private SumField _field = new();
    private ShaderMaterial _mat = null!;
    private ShaderMaterial _floorMat = null!;
    private MeshInstance3D _waterMi = null!;
    private MeshInstance3D _bedMi = null!;
    // PORT EDIT: the MNA window's anchor, and the parked-camera controls.
    private Vector3 _camAnchor = Vector3.Zero;
    private bool _parkY;
    private float _camParkY = 0.0f;
    private int _uwProvider;

    // ---- uwkit: the modular underwater kit under test ----------------------
    // The effect is GDSCRIPT living in res://uwkit/, deliberately: it has to drop into
    // water-kit (GDScript) unchanged, and the proof that it is modular is that the file is
    // byte-identical in both repos. Only this host scene is C#.
    //
    // Instantiating it from C# is itself part of proof 1 — GDScript is proven to RUN in this
    // .NET project (tools/shoot.gd), but a GDScript CompositorEffect attached to a C#
    // scene's camera was not, and that is the load-bearing unknown.
    // ---- MASK VOLUME (the construction proven in water-kit scene 213) ----
    // A SubViewport renders a box whose top face is displaced by uw53_displace() — the SAME
    // include the water shader uses — and a 2D overlay paints where its back faces are.
    // Four invariants, and breaking any one makes the mask silently stop matching:
    //   1. one wave function (the shared include)
    //   2. one data push (SyncMask below feeds both materials)
    //   3. matching vertex grids (same footprint AND subdivisions as the water plane)
    //   4. same clock, same frame
    private SubViewport _maskVp = null!;
    private Camera3D _maskCam = null!;
    private ShaderMaterial _maskMat = null!;
    private ColorRect _overlay = null!;
    private float _underFade = 0.35f;   // metres over which the underside look eases in
    private ShaderMaterial _overlayMat = null!;

    private GodotObject? _uwBoundary;
    private const string UwBoundaryPath = "res://uwkit/uw_boundary_effect.gd";
    private FreeCam _cam = null!;
    private DirectionalLight3D _sun = null!;

    // Ryan's live-tuned preset (Copy values, baked): calmer sea, faster phase.
    // WAS 0 ("flat until you raise it") which was right for proof 1 and wrong now: every
    // band's steepness is multiplied by this, so at 0 the whole per-band panel is dead
    // controls multiplied by zero. Proof 1 is done; the composite should be visible.
    private float _waveAmp = 1f;
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
        _field.Extra = SampleMnaWorld;
        RebuildField();
        BuildEnvironment();
        BuildSeabed();
        BuildWater();
        BuildIslands();
        BuildTestCube();
        RenderingServer.CallOnRenderThread(Callable.From(InitMnaSolver));
        // Ryan's preset: MetalFX spatial at 0.4 render scale (sim cost is resolution-
        // independent — fullscreen fps is pure per-pixel work, so scale exactly that).
        GetViewport().Scaling3DScale = 0.4f;
        GetViewport().Scaling3DMode = (Viewport.Scaling3DModeEnum)3;   // MetalFX spatial
        BuildUwKit();
        BuildMaskVolume();
        BuildUi();
    }

    private void BuildMaskVolume()
    {
        _maskVp = new SubViewport
        {
            Size = (Vector2I)GetViewport().GetVisibleRect().Size,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            OwnWorld3D = true,
            World3D = new World3D(),
        };
        AddChild(_maskVp);

        // No interpolation: this camera must be byte-identical to the main one, and an
        // interpolated one is smoothed toward a DIFFERENT transform.
        _maskCam = new Camera3D { Current = true, PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off };
        _maskVp.AddChild(_maskCam);

        // A fresh World3D has no Environment, so the viewport would clear to the project
        // default — and a non-black clear reads as "masked everywhere".
        _maskVp.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = Colors.Black,
            },
        });

        // SAME footprint and SAME subdivisions as the water plane. A coarser mask
        // straight-lines across short waves; that error grows with every band.
        const float depth = 400f;
        var mi = new MeshInstance3D
        {
            Mesh = new BoxMesh
            {
                Size = new Vector3(WaterPlaneSize, depth, WaterPlaneSize),
                SubdivideWidth = WaterPlaneSubdiv,
                SubdivideDepth = WaterPlaneSubdiv,
            },
            Position = new Vector3(0f, -depth * 0.5f, 0f),
            ExtraCullMargin = 40f,
        };
        _maskMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/uwkit_mask_53.gdshader") };
        _maskMat.SetShaderParameter("box_top", depth * 0.5f);
        mi.MaterialOverride = _maskMat;
        _maskVp.AddChild(mi);

        var layer = new CanvasLayer { Layer = 10 };
        AddChild(layer);
        _overlay = new ColorRect { MouseFilter = Control.MouseFilterEnum.Ignore };
        _overlay.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _overlayMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/uwkit_overlay.gdshader") };
        _overlayMat.SetShaderParameter("mask_tex", _maskVp.GetTexture());
        _overlayMat.SetShaderParameter("fill_color", new Color(1f, 1f, 1f, 0.5f));
        _overlay.Material = _overlayMat;
        // The overlay stays VISIBLE — it now carries the meniscus. The white debug fill
        // inside it is what defaults off.
        _overlayMat.SetShaderParameter("fill_enabled", false);
        layer.AddChild(_overlay);
        GD.Print("[mask] volume + overlay built");
    }

    // ONE data push, every frame, to BOTH materials. If this ever feeds only one of them the
    // mask stops matching the water in a way that looks like a tuning problem and is not.
    // Analytic surface height at the camera, from the same composite the field uses.
    private float SeaLevelAtCamera()
    {
        var p = _cam.GlobalPosition;
        return _field.Height(p.X, p.Z);
    }

    private void SyncMask()
    {
        if (_maskMat == null) return;
        _maskVp.Size = (Vector2I)GetViewport().GetVisibleRect().Size;
        _maskCam.GlobalTransform = _cam.GlobalTransform;
        _maskCam.Fov = _cam.Fov;
        _maskCam.Near = _cam.Near;
        _maskCam.Far = _cam.Far;

        // The water material samples the mask directly — nothing is gated, the mask decides
        // per pixel how much underside shows.
        _mat.SetShaderParameter("uw_mask_tex", _maskVp.GetTexture());

        foreach (string k in new[]
        {
            "wave_time", "wave_count", "wavelengths", "steepnesses", "speeds", "dirs",
            "mna_tex", "mna_origin", "mna_tex_offset", "mna_window", "mna_displacement",
            "mna_edge_fade", "foam2_enabled", "mna_foam2_tex", "foam2_puff",
        })
        {
            _maskMat.SetShaderParameter(k, _mat.GetShaderParameter(k));
        }

        // THE MASK DRIVES THE FOG. Hand the mask viewport to the compositor as an RD texture
        // and switch it off the analytic eye-height fade entirely. That fade was an estimate
        // of where the water is; this is where the water actually IS, rasterised from the
        // same geometry, so `above_fade`, `sample_offset`, `sample_amplitude`, `mask_bias`
        // and `mask_gain` all stop mattering — they only ever compensated for the estimate.
        if (_uwBoundary != null)
        {
            _uwBoundary.Set("mask_texture", RenderingServer.TextureGetRdTexture(_maskVp.GetTexture().GetRid()));
            _uwBoundary.Set("use_mask", true);
        }
    }

    // Attach the GDScript boundary effect to this camera's compositor.
    private void BuildUwKit()
    {
        var script = GD.Load<GDScript>(UwBoundaryPath);
        if (script == null)
        {
            GD.PushError($"[uwkit] could not load {UwBoundaryPath}");
            return;
        }
        _uwBoundary = script.New().AsGodotObject();
        if (_uwBoundary is not CompositorEffect fx)
        {
            GD.PushError("[uwkit] script did not instantiate as a CompositorEffect");
            return;
        }
        fx.Set("sea_level", 0.0f);
        fx.Set("test_amplitude", 0.0f);   // flat first — see the shader header
        fx.Set("debug_mode", 0);   // FOG — the mask view is opt-in from the dropdown
        // ONE source of truth for the water's colour: the kit owns it and the underside
        // material is told, so the surface and the volume can no longer disagree. This is
        // what the UBO bought — the value used to be copy-pasted into both shaders.
        var near = (Color)fx.Get("fog_near_color");
        _mat.SetShaderParameter("uw_tir_color", near);

        // The panel's provider dropdown defaults to index 0 (composite gerstner) but its
        // callback only fires on CHANGE, and the kit's own default is the generic flat
        // plane. Say it out loud, or the scene opens claiming gerstner while running flat —
        // which it did.
        fx.Call("set_provider", "res://uwkit/providers/uw_gerstner.glslinc");
        // RebuildField ran during _Ready, before the effect existed, so its push was a
        // no-op. Push again now that there is something to push to.
        PushBandsToKit();

        var comp = new Compositor();
        comp.CompositorEffects = new Godot.Collections.Array<CompositorEffect> { fx };
        _cam.Compositor = comp;
        GD.Print("[uwkit] boundary effect attached to the camera compositor");
    }

    // PORT EDIT: 50 ate the arrow keys so the boat owned them. Nothing is driven here, so
    // they go back to the GUI.
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
        Vector3 boatPos = _camAnchor;   // PORT EDIT: the CAMERA anchors the window now
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

        // PORT EDIT: the propwash jet and lateral shed were HULL forcing (throttle and
        // yaw rate). With no boat there is nothing to shed, so the swirl field still steps
        // and advects but is driven by zero velocity injection.
        float dyawRate = 0f;
        Vector3 sfwd = Vector3.Forward, sright = Vector3.Right;
        float thr = 0f;
        Vector3 sternW = boatPos;
        var jetCell = new Vector2(sternW.X - _swOrigin.X, sternW.Z - _swOrigin.Y) / swCell;
        var jetVel = Vector2.Zero;
        var shedVel = Vector2.Zero;
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

        // PORT EDIT: the boat wake dipole is gone with the boat. Mouse poke still works.

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
        // uwkit: the boundary effect is a GDScript-created RefCounted held from C#. Left to
        // the GC it is finalized on a BACKGROUND thread after the engine has torn down, and
        // Variant's finalizer then calls into the node tree — "the caller thread can't call
        // propagate_notification()", then SIGSEGV on exit. Release it here, on the main
        // thread, while there is still an engine to release it into.
        if (_cam != null)
        {
            _cam.Compositor = null;
        }
        _uwBoundary?.Call("cleanup");   // frees the compute shader; Dispose() alone leaks it
        _uwBoundary?.Dispose();
        _uwBoundary = null;

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
        SyncMask();   // same clock, same frame
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
        if (_floorMat == null)
        {
            return;
        }
        // PORT EDIT: no hull, so no hull shadow; the caustics still animate.
        _floorMat.SetShaderParameter("shadow_darkness", 0f);
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
        foreach (var b in _bands)
        {
            _field.AddWave(b[0], b[1] * _waveAmp, b[2] * _waveSpeedScale, b[3]);
        }
        _field.Wavelength = _bands[0][0];
        _field.Steepness = _bands[0][1] * _waveAmp;
        _field.Speed = _bands[0][2] * _waveSpeedScale;
        _field.Directions = new[] { _bands[0][3], _bands[1][3], _bands[2][3], _bands[3][3] };
        if (_mat != null)
        {
            SyncShaderWaves();
        }
        PushBandsToKit();
    }

    // The kit sums these itself in uw_gerstner.glslinc, so the boundary and the fog see the
    // SAME sea the vertex shader displaces. Amplitude and speed multipliers are folded in
    // here rather than passed separately — the kit should receive the final waves, not the
    // recipe for them.
    private void PushBandsToKit()
    {
        if (_uwBoundary == null) return;
        var flat = new float[32];
        int n = Math.Min(_bands.Length, 8);
        for (int i = 0; i < n; i++)
        {
            flat[i * 4 + 0] = _bands[i][0];
            flat[i * 4 + 1] = _bands[i][1] * _waveAmp;
            flat[i * 4 + 2] = _bands[i][2] * _waveSpeedScale;
            flat[i * 4 + 3] = _bands[i][3];
        }
        _uwBoundary.Call("set_bands", flat, n);
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

        _cam = new FreeCam
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
        _mat.SetShaderParameter("uw_underside_on", true);   // the lab exists to look at it
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

    // A big, solid, unmistakable object ABOVE the water — the test target for looking up
    // from below. Snell's window is hard to judge against empty sky: you cannot tell a
    // window that is working from one that is simply showing you nothing. A saturated cube
    // directly overhead makes it obvious — it should be visible through the cone straight
    // up, and gone once you pan past the critical angle.
    //
    // It also gives the depth buffer and the fog something with a KNOWN size and position
    // to be checked against, which the water-column bands need.
    private MeshInstance3D _testCube = null!;

    private void BuildTestCube()
    {
        _testCube = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(24f, 24f, 24f) },
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.95f, 0.42f, 0.08f),
                Roughness = 0.55f,
            },
            Position = new Vector3(0f, 20f, 0f),
        };
        AddChild(_testCube);
    }

    // PORT EDIT: BuildBoat deleted — see the header. Nothing else referenced it.

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

    // PORT EDIT: 50's chase cam followed the hull. Here the camera is the thing being
    // tested — it has to sit STILL, half-submerged, so it is a FreeCam plus an explicit Y.
    // _camAnchor is what the MNA window follows, so the sim window rides with you.
    private void UpdateCamera(float delta)
    {
        _camAnchor = _cam.GlobalPosition;

        // Publish the MNA window to the kit. This is the whole integration surface for a
        // texture-backed water: a height RID, a world origin and a window size.
        if (_uwBoundary != null && _uwProvider == 2)
        {
            _uwBoundary.Set("height_texture", _mnaSolver?.HeightRid ?? default);
            _uwBoundary.Set("window_origin", _mnaOrigin);
            _uwBoundary.Set("window_size", MnaWindow);
        }
        if (_parkY)
        {
            var p = _cam.GlobalPosition;
            p.Y = _camParkY;
            _cam.GlobalPosition = p;
        }
    }


    private void BuildUi()
    {
        var ui = new DemoUI(this, "50 · Boat + MNA reactive window",
            "Arrow keys: throttle + steer. Scene 16's composite Gerstner sea (all its knobs kept), plus a 48 m CN-MNA sim window riding the swell — h = gerstner + mna. The wake is two steerable pokes (bow ridge + stern trough), each with angle / distance / strength knobs. MNA knobs at the bottom.");

        // THE COMPOSITE, band by band. This is what "composite Gerstner" actually means —
        // six independent waves — and until now only their global amplitude and speed were
        // reachable. The kit reads the same array, so moving any of these moves the fog and
        // the waterline with the water.
        for (int bi = 0; bi < _bands.Length; bi++)
        {
            int b = bi;   // capture
            ui.AddSlider($"B{b} wavelength (m)", 1f, 60f, _bands[b][0],
                v => { _bands[b][0] = v; RebuildField(); });
            ui.AddSlider($"B{b} steepness", 0f, 0.35f, _bands[b][1],
                v => { _bands[b][1] = v; RebuildField(); });
            ui.AddSlider($"B{b} speed", 0f, 3f, _bands[b][2],
                v => { _bands[b][2] = v; RebuildField(); });
            ui.AddSlider($"B{b} direction (0-1)", 0f, 1f, _bands[b][3],
                v => { _bands[b][3] = v; RebuildField(); });
        }

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
        // Parking the camera ON the waterline is the whole point of this scene: the
        // boundary case is a camera that is PARTLY under, and you cannot hold that by hand.
        ui.AddToggle("Park camera Y (hold it on the waterline)", false, on => _parkY = on);
        ui.AddSlider("Camera Y", -6f, 6f, 0f, v => _camParkY = v);

        // PROOF 1 controls. The test ripple is NOT the real wave field on purpose —
        // see the shader header. Flat must give a straight line; raising the ripple
        // must bend it, and that is the whole pass criterion.
        ui.AddToggle("uwkit: show per-pixel boundary (red under / blue above)", true,
            on => _uwBoundary?.Set("show_boundary", on));
        ui.AddSlider("uwkit: sea level", -3f, 3f, 0f,
            v => _uwBoundary?.Set("sea_level", v));
        ui.AddSlider("uwkit: TEST ripple amplitude (0 = flat)", 0f, 2f, 0f,
            v => _uwBoundary?.Set("test_amplitude", v));
        ui.AddSlider("uwkit: TEST ripple frequency", 0.05f, 2f, 0.35f,
            v => _uwBoundary?.Set("test_frequency", v));

        // PROOF 3 — the modularity claim, reduced to one control. Swapping the
        // provider is the ONLY edit: the kit's template, effect and host wiring are
        // identical across all three. FLAT must give a straight line, RIPPLE must bend
        // it analytically, and MNA TEXTURE must bend it to the actual sim.
        ui.AddOptions("uwkit: view", new[] { "FOG", "depth probe", "water column (5 m bands)", "MASK + line" }, 0,
            i => _uwBoundary?.Set("debug_mode", i));
        // Two brightnesses because they are two different sets of pixels: the SURFACE
        // seen from below (the chunk) and the fogged VOLUME at distance (the
        // compositor). The volume one is scaled by water column, so it is inert when
        // you are looking up from just under the surface — which is why it is not the
        // one you want there.
        ui.AddSlider("uwkit: FOG brightness (distance)", 0.2f, 3f, 0.97f,
            v => _uwBoundary?.Set("underwater_brightness", v));
        ui.AddSlider("uwkit: fog fades above surface (m)", 0.05f, 8f, 0.129f,
            v => _uwBoundary?.Set("above_fade", v));
        // The mask is provider-agnostic: uw_signed_at() calls uw_surface_height(), which IS
        // whatever provider is loaded. Same two controls here on composite Gerstner as on
        // water-kit's FFT cascades — that is the contract doing its job, not a coincidence.
        ui.AddSlider("uwkit: LENS span (m, vertical reach)", 0.05f, 20f, 2f,
            v => _uwBoundary?.Set("line_distance", v));
        ui.AddSlider("uwkit: SAMPLE offset (m, phase)", -20f, 20f, 0f,
            v => _uwBoundary?.Set("sample_offset", v));
        ui.AddSlider("uwkit: SAMPLE amplitude x", 0f, 4f, 1f,
            v => _uwBoundary?.Set("sample_amplitude", v));
        ui.AddSlider("uwkit: MASK bias (m, aligns the line)", -3f, 3f, 0f,
            v => _uwBoundary?.Set("mask_bias", v));
        ui.AddSlider("uwkit: MASK gain (transition sharpness)", 0.05f, 8f, 1f,
            v => _uwBoundary?.Set("mask_gain", v));
        ui.AddSlider("uwkit: MASK softness (m)", 0f, 0.5f, 0.03f,
            v => _uwBoundary?.Set("line_softness", v));
        ui.AddSlider("uwkit: fog density (per metre)", 0f, 0.4f, 0.146f,
            v => _uwBoundary?.Set("fog_density", v));
        ui.AddSlider("uwkit: fog colour transition (m)", 5f, 200f, 148.3f,
            v => _uwBoundary?.Set("fog_transition", v));
        ui.AddToggle("TEST cube above the water", true, on => _testCube.Visible = on);
        ui.AddSlider("TEST cube height", 6f, 60f, 20f,
            v => _testCube.Position = new Vector3(0f, v, 0f));
        ui.AddSlider("TEST cube size", 4f, 60f, 24f,
            v => ((BoxMesh)_testCube.Mesh).Size = new Vector3(v, v, v));
        // The meniscus: the line where the water meets the LENS. Drawn from the mask's edge,
        // so it lands exactly on the waterline rather than near it.
        ui.AddToggle("Meniscus", true,
            on => _overlayMat.SetShaderParameter("meniscus_enabled", on));
        ui.AddSlider("Meniscus thickness (px)", 1f, 64f, 12f,
            v => _overlayMat.SetShaderParameter("meniscus_px", v));
        ui.AddSlider("Meniscus opacity", 0f, 1f, 0.9f,
            v => _overlayMat.SetShaderParameter("meniscus_color", new Color(1f, 1f, 1f, v)));
        ui.AddToggle("MASK debug fill (white)", false,
            on => _overlayMat.SetShaderParameter("fill_enabled", on));
        ui.AddToggle("uwkit: underside (see the surface from below)", true,
            on => _mat.SetShaderParameter("uw_underside_on", on));
        // Renamed with its meaning: this is now TIR strength at grazing angles, NOT a
        // distance tint. Distance colouring belongs entirely to the fog.
        ui.AddSlider("uwkit: TIR strength (grazing mirror)", 0f, 1f, 0.21f,
            v => _mat.SetShaderParameter("uw_tir_strength", v));
        ui.AddSlider("uwkit: UNDERSIDE brightness (looking up)", 0.1f, 8f, 3.82f,
            v => _mat.SetShaderParameter("uw_underside_brightness", v));
        ui.AddSlider("uwkit: mirror lift (grazing only)", 0f, 4f, 2.09f,
            v => _mat.SetShaderParameter("uw_tir_lift", v));
        ui.AddSlider("uwkit: underside warp", 0f, 0.25f, 0.0475f,
            v => _mat.SetShaderParameter("uw_underside_warp", v));
        ui.AddSlider("uwkit: Snell window softness", 0f, 0.5f, 0.1575f,
            v => _mat.SetShaderParameter("uw_snell_softness", v));
        ui.AddOptions("uwkit: height provider",
            new[] { "composite gerstner", "flat", "ripple", "MNA texture" }, 0, i =>
        {
            string[] paths =
            {
                "res://uwkit/providers/uw_gerstner.glslinc",
                "res://uwkit/providers/uw_flat.glslinc",
                "res://uwkit/providers/uw_ripple.glslinc",
                "res://uwkit/providers/uw_texture.glslinc",
            };
            _uwProvider = i == 3 ? 2 : -1;   // only the texture provider needs the MNA window fed
            _uwBoundary?.Call("set_provider", paths[i]);
        });



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
