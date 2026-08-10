using Godot;

namespace GodotCsharpExperiments.Lib;

// Scene250Base — the shared scaffold for the artifact series (docs/artifacts-250.md
// step 1; extended by docs/artifacts-270.md). Every 250/270 scene derives from this so
// that the things the series PROMISES are structural, not remembered per scene:
//
//   · the standard panel, in the standard order, every time
//   · a TIME scale that reaches every pass — including per-tick fades, which must be
//     raised to the power of it or slow-mo drowns in stale haze (the scene-32 lesson)
//   · a grid dropdown that live-rebuilds AND carries the similarity table, so a slider
//     tuned at 512² still means the same physics at 256² or 1024²
//   · the reference A/B and the artifact view as first-class controls, not options
//
// A scene supplies: its identity, its sim (BuildSim/FreeSim/SimTick), the Rids to
// display, and two blocks of knobs. It does NOT override _Ready or _PhysicsProcess —
// the template method here owns the order.
//
// 2D scenes get the flat plane + artifact_view material for free. A 3D scene (250b)
// sets UsesFlatPlane false and builds its own raymarch display; everything else —
// panel, TIME, grid, scaling — still applies.
public abstract partial class Scene250Base : Node3D
{
    // ── the palette (docs/artifacts-250.md). Mirrored in artifact_view.gdshader; these
    // are for the C# side of the scene (environment, gizmos, debug draws).
    public static readonly Color Paper = Color.FromHtml("E8E2D5");
    public static readonly Color Ink = Color.FromHtml("16213E");
    public static readonly Color Plus = Color.FromHtml("35C4B5");
    public static readonly Color Minus = Color.FromHtml("E0A458");
    public static readonly Color ArtifactColor = Color.FromHtml("E23D6D");
    public static readonly Color Room = Color.FromHtml("171310");

    protected const string ViewShader = "res://shaders/artifact_view.gdshader";

    /// <summary>
    /// The grid this scene's baked defaults are authored at; the similarity table scales
    /// RELATIVE to it, so tuned Copy-values stay literally correct at the default and only
    /// move when the dropdown does (same trick as ReactiveWaveField.SpeedScale). 512 suits
    /// a 2D scene; a 3D one overrides — 56³ is a completely different budget.
    /// </summary>
    protected virtual int RefGrid => 512;

    /// <summary>Grid dropdown entries. 3D scenes override: 1024³ is not a thing.</summary>
    protected virtual int[] GridOptions => new[] { 256, 512, 1024 };

    /// <summary>How a grid choice is spelled in the panel.</summary>
    protected virtual string GridLabel(int n) => $"{n}²";

    // ── scene identity ────────────────────────────────────────────────────────────
    protected abstract string SceneTitle { get; }
    protected abstract string SceneHint { get; }

    /// <summary>The artifact's name — heads its knob section. One artifact per scene.</summary>
    protected abstract string ArtifactName { get; }

    // ── display geometry (2D default; 250b overrides) ─────────────────────────────
    protected virtual bool UsesFlatPlane => true;
    protected virtual float WorldSize => 6.0f;
    protected virtual float CameraFov => 40.0f;

    /// <summary>
    /// True = the plane is a VERTICAL SLICE seen from the side: the sim's +y axis is
    /// world up, so an in-plane buoyancy term is real buoyancy and the scene lifts
    /// straight to a 3D vessel. False = a horizontal slice seen from above, which is
    /// what a height field wants (lit-height mode assumes it).
    /// Get this wrong and the physics says one thing while the camera says another.
    /// </summary>
    protected virtual bool SideView => false;

    // ── live state the panel owns ─────────────────────────────────────────────────
    public int N { get; private set; } = 512;   // _Ready overwrites from GridDefault
    protected Vector2I Grid => new(N, N);

    /// <summary>Multiplies dt in every pass. Fades must go through <see cref="Fade"/>.</summary>
    public float TimeScale { get; private set; } = 1.0f;

    /// <summary>The accurate solve — the experiment IS this toggle.</summary>
    public bool ReferenceOn { get; private set; }

    public bool ArtifactOn { get; private set; }
    protected float ArtifactGain { get; private set; } = 8.0f;

    /// <summary>Start with the artifact view up. True for scenes whose residual IS the point.</summary>
    protected virtual bool ArtifactViewDefault => false;
    protected virtual float ArtifactGainDefault => 8.0f;

    // Baked-default hooks (house rule: tuned Copy-values become the defaults). A scene
    // overrides these instead of the panel reaching in and setting state after the fact.
    protected virtual int GridDefault => RefGrid;
    protected virtual float TimeScaleDefault => 1.0f;
    protected virtual float FieldGainDefault => 1.0f;
    protected virtual float FieldGammaDefault => 1.0f;

    /// <summary>
    /// Sample the display cell-exactly instead of through the linear filter. True for any
    /// scene whose artifact is CELL-scaled — bilinear averaging erases a two-cell pattern.
    /// </summary>
    protected virtual bool PixelExactDefault => false;

    /// <summary>
    /// Fly camera active: the scene must stop driving the camera while this is true.
    /// A scene with its own camera rig (250b's orbit) checks this before overriding.
    /// </summary>
    protected bool FlyMode { get; private set; } = true;

    /// <summary>Fly camera on at open. On by default — you want to move before you want a rig.</summary>
    protected virtual bool FlyDefault => true;

    protected DemoUI Ui = null!;
    protected Label Readout = null!;
    protected Camera3D Cam = null!;
    private FreeCam _fly = null!;
    protected MeshInstance3D Plane = null!;
    protected ShaderMaterial Mat = null!;

    private Texture2Drd? _fieldTex, _artifactTex, _defectTex;
    private int _tick;

    // Written on the render thread, read on the main one — a plain latch, deliberately
    // not a lock: the worst case is one extra blank frame.
    private volatile bool _rebuilding;

    // ── what a scene must provide ─────────────────────────────────────────────────
    protected abstract void BuildSim();     // on the render thread
    protected abstract void FreeSim();      // on the render thread
    protected abstract void SimTick(double delta);
    protected abstract Rid FieldRid { get; }
    protected abstract Rid ArtifactRid { get; }
    protected virtual Rid DefectRid => default;

    /// <summary>
    /// Build the display when UsesFlatPlane is false (a volume raymarch, say). Runs where
    /// BuildPlane would, i.e. BEFORE the panel — assign <see cref="Mat"/> here and the
    /// artifact toggle + intensity wire themselves up exactly as they do in 2D.
    /// </summary>
    protected virtual void BuildDisplay() { }

    protected abstract void BuildSimKnobs(DemoUI ui);
    protected abstract void BuildArtifactKnobs(DemoUI ui);
    protected virtual void BuildRenderKnobs(DemoUI ui) { }
    protected virtual void OnGridChanged() { }
    protected virtual void OnReferenceToggled(bool on) { }
    protected virtual string ReadoutText() => $"{N}² · t×{TimeScale:0.00} · {(ReferenceOn ? "REFERENCE" : ArtifactName)}";

    // ── the per-tick numbers ──────────────────────────────────────────────────────
    protected virtual float BaseDt => 1.0f;

    /// <summary>dt for this tick — one knob, all passes coherent.</summary>
    protected float Dt => BaseDt * TimeScale;

    /// <summary>
    /// A per-tick fade, corrected to be per SIM-TIME. Slow motion must not mean "the
    /// same decay applied more often" — that is haze. Always route fades through here.
    /// </summary>
    protected float Fade(float perTick) => Mathf.Pow(perTick, TimeScale);

    // ── the similarity table (docs/artifacts-250.md "Grid & similarity") ──────────
    // Kernels run in CELL units, so every raw number changes meaning with N. These keep
    // a slider's WORLD meaning fixed as the dropdown moves. Ratio is 1.0 at RefGrid.

    protected float GridRatio => N / (float)RefGrid;

    /// <summary>Velocity: stir, pour push, advection. v_cells = v_world·N/W ⇒ ×2 per doubling.</summary>
    protected float ScaleVel(float v) => v * GridRatio;

    /// <summary>Body force / buoyancy / drift. Same row as velocity.</summary>
    protected float ScaleForce(float a) => a * GridRatio;

    /// <summary>Source / poke radius, in cells.</summary>
    protected float ScaleRadius(float r) => r * GridRatio;

    /// <summary>Viscosity a = ν·dt. ν_cells = ν_world·(N/W)² ⇒ ×4 per doubling.</summary>
    protected float ScaleVisc(float nu) => nu * GridRatio * GridRatio;

    /// <summary>Jacobi/GS iterations at matched convergence of the largest modes: K ∝ N².</summary>
    protected int ScaleJacobi(int k) => Mathf.Max(1, Mathf.RoundToInt(k * GridRatio * GridRatio));

    /// <summary>SOR at optimal ω converges an order faster: K ∝ N.</summary>
    protected int ScaleSor(int k) => Mathf.Max(1, Mathf.RoundToInt(k * GridRatio));

    /// <summary>ω* ≈ 2/(1+sin(π/N)) — the optimal over-relaxation for this grid.</summary>
    protected float SorOmega => 2.0f / (1.0f + Mathf.Sin(Mathf.Pi / N));

    /// <summary>Multigrid: +1 level per doubling. Character ≈ invariant — its whole point.</summary>
    protected int ScaleMgLevels(int levels) => levels + Mathf.RoundToInt(Mathf.Log(GridRatio) / Mathf.Log(2.0f));

    /// <summary>Vorticity confinement ε: empirical, retune ∝ W/N ⇒ ×0.5 per doubling.</summary>
    protected float ScaleConfine(float eps) => eps / GridRatio;

    // MacCormack is dimensionless and per-tick fades go through Fade() — neither scales.

    /// <summary>
    /// For a knob that is cell-scaled BY NATURE (plaquette width, checkerboard period,
    /// sweep spacing). It cannot be made grid-proof, so the honest move is to say so in
    /// the panel and treat resolution as part of the instrument — design response #2.
    /// </summary>
    protected HSlider AddCellLockedSlider(DemoUI ui, string label, float min, float max, float val,
        System.Action<float> cb) => ui.AddSlider($"{label}  [cell-locked · grid is a knob]", min, max, val, cb);

    /// <summary>
    /// Mouse position on the display plane as UV, or (−1,−1) off-plane. Every 2D scene in
    /// the series poked the same way — it lives here so 251+ stays a formula swap.
    /// </summary>
    protected Vector2 PlaneMouseUv()
    {
        var mp = GetViewport().GetMousePosition();
        // Match the plane built above: the slice faces −Z, the top-down plane faces +Y.
        var face = new Plane(SideView ? Vector3.Forward : Vector3.Up, 0.0f);
        if (face.IntersectsRay(Cam.ProjectRayOrigin(mp), Cam.ProjectRayNormal(mp)) is not Vector3 hit)
        {
            return new Vector2(-1, -1);
        }
        var uv = SideView
            ? new Vector2(hit.X / WorldSize + 0.5f, hit.Y / WorldSize + 0.5f)
            : new Vector2(hit.X / WorldSize + 0.5f, hit.Z / WorldSize + 0.5f);
        return uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1 ? new Vector2(-1, -1) : uv;
    }

    // ── lifecycle. Scenes implement the hooks; the order lives here. ──────────────
    public override void _Ready()
    {
        N = GridDefault;
        TimeScale = TimeScaleDefault;
        BuildEnvironment();
        if (UsesFlatPlane) { BuildPlane(); } else { BuildDisplay(); }
        RenderingServer.CallOnRenderThread(Callable.From(BuildSim));
        BuildUi();
    }

    public override void _PhysicsProcess(double delta)
    {
        // A grid rebuild frees the sim's textures on the RENDER thread while this runs on
        // the main one. Without this guard we keep handing the freed RIDs to Texture2Drd
        // and Godot reports "Attempted to free invalid ID" once per display texture.
        if (_rebuilding) { return; }

        if (_fieldTex != null) { _fieldTex.TextureRdRid = FieldRid; }
        if (_artifactTex != null) { _artifactTex.TextureRdRid = ArtifactRid; }
        if (_defectTex != null) { _defectTex.TextureRdRid = DefectRid; }

        SimTick(delta);

        _tick++;
        if (_tick % 12 == 0) { Readout.Text = ReadoutText(); }
    }

    public override void _ExitTree()
    {
        if (_fieldTex != null) { _fieldTex.TextureRdRid = default; }
        if (_artifactTex != null) { _artifactTex.TextureRdRid = default; }
        if (_defectTex != null) { _defectTex.TextureRdRid = default; }
        RenderingServer.CallOnRenderThread(Callable.From(FreeSim));
    }

    private void BuildEnvironment()
    {
        float d = WorldSize * 0.5f / Mathf.Tan(Mathf.DegToRad(CameraFov * 0.5f)) * 1.06f;

        // Always a FreeCam, gated OFF until the fly toggle turns it on — so every scene in
        // the series can be flown without re-deriving a rig. Enabled, not SetProcess: see
        // FreeCam.Enabled for why the process flag can't be trusted here.
        FlyMode = FlyDefault;
        _fly = new FreeCam
        {
            Speed = WorldSize, Fov = CameraFov, Far = 200.0f, Current = true, Enabled = FlyMode,
        };
        Cam = _fly;
        AddChild(Cam);

        if (SideView)
        {
            // Stand off along −Z and look at the slice face-on, up = world up.
            Cam.Position = new Vector3(0, 0, -d);
            Cam.LookAt(Vector3.Zero, Vector3.Up);
        }
        else
        {
            Cam.Position = new Vector3(0, d, 0.01f);
            Cam.LookAt(Vector3.Zero, Vector3.Forward);
        }

        // LINEAR tonemap on purpose: the palette is designed, and a filmic curve would
        // shift every one of its six colours. What artifact_view writes is what you see.
        var env = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Room.SrgbToLinear(),
            TonemapMode = Godot.Environment.ToneMapper.Linear,
        };
        AddChild(new WorldEnvironment { Environment = env });
    }

    private void BuildPlane()
    {
        Plane = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = new Vector2(WorldSize, WorldSize) },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = 4.0f,
        };
        if (SideView)
        {
            // PlaneMesh lies in XZ with UV u←x, v←z. Rotating −90° about X sends local
            // +Z to world +Y, so v (the sim's y axis) becomes world UP — buoyancy rises
            // on screen — and the face normal turns to −Z, toward the camera.
            Plane.RotationDegrees = new Vector3(-90.0f, 0.0f, 0.0f);
        }
        _fieldTex = new Texture2Drd();
        _artifactTex = new Texture2Drd();
        _defectTex = new Texture2Drd();
        Mat = new ShaderMaterial { Shader = GD.Load<Shader>(ViewShader) };
        Mat.SetShaderParameter("field_tex", _fieldTex);
        Mat.SetShaderParameter("artifact_tex", _artifactTex);
        Mat.SetShaderParameter("defect_tex", _defectTex);
        Mat.SetShaderParameter("texel", 1.0f / N);
        Mat.SetShaderParameter("artifact_gain", 0.0f);
        Mat.SetShaderParameter("field_gain", FieldGainDefault);
        Mat.SetShaderParameter("field_gamma", FieldGammaDefault);
        Mat.SetShaderParameter("pixel_exact", PixelExactDefault);
        Plane.MaterialOverride = Mat;
        AddChild(Plane);
    }

    // The standard panel — docs/artifacts-250.md, same order in every scene.
    private void BuildUi()
    {
        ArtifactOn = ArtifactViewDefault;
        ArtifactGain = ArtifactGainDefault;
        PushArtifactGain();

        // 1. DemoUI's own header: FPS, upscaler, render scale, AA.
        Ui = new DemoUI(this, SceneTitle, SceneHint);
        Readout = Ui.AddReadout(ArtifactName);

        // 2. TIME scale — reaches every pass through Dt and Fade().
        Ui.AddSlider("TIME scale (slow-mo; fades follow)", 0.05f, 1.5f, TimeScale, v => TimeScale = v);

        // 3. Grid — live rebuild, with the similarity table applied to every Scale* knob.
        var choices = GridOptions;
        int sel = System.Array.IndexOf(choices, N);
        var names = new string[choices.Length];
        for (int i = 0; i < choices.Length; i++) { names[i] = GridLabel(choices[i]); }
        Ui.AddOptions("Grid (rebuilds; knobs keep world meaning)", names, sel < 0 ? 0 : sel,
            i => SetGrid(choices[i]));

        // 4. scene sim knobs
        BuildSimKnobs(Ui);

        // 5. THE artifact
        Ui.AddSection($"ARTIFACT — {ArtifactName}");
        BuildArtifactKnobs(Ui);

        // 6. the two toggles that make it a series scene
        Ui.AddSection("Reference & artifact view");
        Ui.AddToggle("Reference solve (accurate A/B)", ReferenceOn, on =>
        {
            ReferenceOn = on;
            OnReferenceToggled(on);
        });
        Ui.AddToggle("Artifact view (the residual itself)", ArtifactOn, on =>
        {
            ArtifactOn = on;
            PushArtifactGain();
        });
        Ui.AddSlider("Artifact intensity", 0.0f, 40.0f, ArtifactGain, v =>
        {
            ArtifactGain = v;
            PushArtifactGain();
        });

        // 7. render
        Ui.AddSection("Render");
        Ui.AddToggle("Camera · FLY (hold RMB look · WASD · Q/E · Shift fast)", FlyMode, on =>
        {
            FlyMode = on;
            _fly.Enabled = on;
            // Leaving fly mode mid-look would otherwise strand the pointer captured.
            if (!on) { Input.MouseMode = Input.MouseModeEnum.Visible; }
        });
        Ui.AddSlider("Camera · fly speed", 0.5f, 40.0f, _fly.Speed, v => _fly.Speed = v);
        if (UsesFlatPlane)
        {
            Ui.AddOptions("Display mode",
                new[] { "Ink scalar", "Signed two-tone", "Lit height", "Artifact solo" }, 0,
                i => Mat.SetShaderParameter("mode", i));
            Ui.AddSlider("Field gain", 0.05f, 8.0f, FieldGainDefault, v => Mat.SetShaderParameter("field_gain", v));
            Ui.AddSlider("Field gamma", 0.15f, 4.0f, FieldGammaDefault, v => Mat.SetShaderParameter("field_gamma", v));
            Ui.AddSlider("Height relief (lit mode)", 0.0f, 200.0f, 40.0f,
                v => Mat.SetShaderParameter("height_scale", v));
            Ui.AddToggle("Pixel-exact sampling (cell-scaled artifacts)", PixelExactDefault,
                on => Mat.SetShaderParameter("pixel_exact", on));
        }
        BuildRenderKnobs(Ui);
    }

    private void PushArtifactGain() =>
        Mat?.SetShaderParameter("artifact_gain", ArtifactOn ? ArtifactGain : 0.0f);

    private void SetGrid(int n)
    {
        if (n == N) { return; }
        N = n;
        Mat?.SetShaderParameter("texel", 1.0f / N);

        // Drop the display's hold on the OLD textures before anything frees them, and
        // stop ticking until the new sim exists. Order matters: clear, then queue.
        _rebuilding = true;
        if (_fieldTex != null) { _fieldTex.TextureRdRid = default; }
        if (_artifactTex != null) { _artifactTex.TextureRdRid = default; }
        if (_defectTex != null) { _defectTex.TextureRdRid = default; }

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            FreeSim();
            BuildSim();
            _rebuilding = false;
        }));
        OnGridChanged();
    }
}
