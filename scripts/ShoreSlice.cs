using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 290 — SLICE (docs/shore-slice.md). The first step toward a wave that can actually
// curl: two phases, water and air, in a vertical cross-section.
//
// WHY THIS IS A DEPARTURE FROM EVERY SHORE SCENE BEFORE IT. Scenes 26–33 and 50–52 are all
// height fields — KP07, Boussinesq, the MNA wave stamp — and every one of them is
// depth-averaged and single-valued. h(x) can steepen without limit and can NEVER overturn,
// because a curling wave has three water surfaces above one x. Curl is not a resolution
// problem or a tuning problem for those solvers; it is outside what they can represent. So
// the interface has to become a volumetric field, and that is what this scene establishes.
//
// ρ is the dye field, reinterpreted. Water and air are ONE scalar with a large ratio — no
// per-phase branching anywhere, which is the same observation f3_add_gravity makes for the
// 3D liquid ("water, oil and fog are one field holding rho"). Gravity is uniform on every
// cell including air, because the variable-density momentum equation carries no density
// factor on g; the density difference enters entirely through 1/ρ on the pressure gradient
// (fs_two_phase_add explains why weighting g by ρ as well would double-count it).
//
// THE ARTIFACT: SPURIOUS HYDROSTATIC CURRENTS.
// Still water under still air has an exact answer — nothing moves, ever, and ∇p = ρg
// balances gravity precisely. On a discrete grid that balance is only approximate at the
// interface, and the imbalance shows up as a thin sheet of velocity crawling along the
// waterline that has no physical cause whatsoever. That is the residual, and it renders in
// #E23D6D like every other artifact in the series.
//
// AND IT BEHAVES UNLIKE ANY BLOCK A ARTIFACT, which is the reason to build it first: it
// does NOT go away when you converge. Squish, bounce, plaid and grain all shrink toward
// nothing as the solve is run longer — they are truncation. This one is a DISCRETISATION
// INCONSISTENCY between how gravity is applied and how the pressure gradient is taken, so a
// perfectly converged solve still produces it. Turn the reference on and watch it survive.
// If it vanishes under convergence, the diagnosis here is wrong.
//
//   artifact  |v| where v must be 0     reference  the same solve, converged hard
public partial class ShoreSlice : Scene250Base
{
    private FluidSim? _fluid;
    private FieldProbe? _speed;
    private bool _seeded;
    private float _t;

    // medium
    private float _dt = 1.0f;
    protected float _gravity = -0.012f;   // cells/tick², down
    protected float _fill = 0.45f;        // waterline as a fraction of the domain height
    private float _rhoWater = 1.0f;
    // 833:1 — REAL water:air, measured rather than hedged. A sweep at 100 / 833 / 2000
    // showed hydrostatic pressure staying correct (11.15 against an analytic 11.18) and
    // interface thickness essentially unchanged (1.8% → 1.9%). What degrades is |div|max,
    // 8× worse, which is conditioning: the conductance now spans 1:833 and RBGS converges a
    // badly-conditioned weighted Laplacian slowly. That is a convergence cost, not an
    // instability — multigrid is what buys it back. Past ~2000:1 it starts to run away.
    protected float _rhoAir = 0.0012f;
    private float _stir;                // 0 = leave the equilibrium alone
    private bool _macCormack = true;

    // ON now, and it has to be: a seeded hydrostatic ramp has ∂p/∂y ≠ 0 at the floor, which
    // is simply not a solution of the CLAMPED (zero-gradient, i.e. open) boundary problem —
    // the solve would spend every frame destroying the ramp it was handed. Solid walls and
    // hydrostatic seeding are one change, not two. Still a toggle, so the A/B stays
    // reachable and the failure mode is reproducible.
    protected bool _solidWalls = true;

    // ── INTERFACE SHARPNESS (291). Nothing in the pipeline opposes advective smearing, so
    // ρ drifts from "two phases" toward "a concentration" and the scene stops reading as
    // water at all. Strength balances against that diffusion each frame; power sets how
    // hard the edge is.
    protected float _sharpen = 0.25f;
    protected float _sharpPower = 2.4f;

    // Display the field as a PHASE with a boundary rather than a continuous concentration.
    protected float _surfaceLevel = 0.5f;
    protected float _surfaceSoft = 0.06f;

    // ── BOUNDARIES. A sealed box is not an ocean: with solid walls all round, both fluids
    // are incompressible and the total volume is fixed, so the interface cannot rise
    // anywhere without falling somewhere else and every disturbance becomes a circulation.
    // An open top (pressure Dirichlet p = 0) lets the atmosphere leave and enter; free-slip
    // sides stop the surface being pinned where it meets a wall.
    protected bool _openTop = true;
    protected bool _freeSlip = true;

    // Seabed: height in cells at x=0, and rise per cell of run — bed_slope IS tanβ, the
    // numerator of the Iribarren number that decides spilling vs plunging vs surging.
    protected virtual float BedSlope => 0f;

    /// <summary>
    /// Where the still waterline meets the bed, as a fraction of domain length. Derived
    /// rather than set, because bed height and slope are NOT independent: raising the slope
    /// while holding the intercept drags the shoreline offshore and shrinks the sea to a
    /// corner. My first Iribarren sweep did exactly that, so it varied tanβ AND the surf-zone
    /// length together and could not be read. Holding the shoreline fixed makes slope the
    /// only variable, which is what the sweep is for.
    /// </summary>
    protected virtual float ShoreAt => 0.75f;

    protected float BedY0 => _fill * Grid.Y - BedSlope * (ShoreAt * Grid.X);

    // Piston wavemaker + bottom friction (294).
    protected float _paddleAmp;
    protected float _paddleFreq = 0.06f;
    protected float _paddleT;
    protected float _drag;
    protected float _dragDepth = 0.12f;   // FRACTION of local depth, not a cell count
    protected float _sponge;

    // ── SUBSTEPS: the CFL fix, and the reason energetic runs destroyed the interface.
    // Semi-Lagrangian is unconditionally STABLE but only accurate near CFL ≈ 1. At |v|max
    // 8–12 cells/tick with dt = 1 the fluid crosses ten cells per step, the backtrace lands
    // nowhere near where it started, and the density interface is shredded in a few steps —
    // measured as 76% of the domain going mid-phase and ρ_min climbing from 0.0012 to 0.25,
    // i.e. no cell left pure air. Sharpening cannot keep up with that and was never the
    // problem. Lowering dt alone does not work either: TimeScale multiplies it straight
    // back. Splitting the tick into N steps of dt/N keeps sim-time per frame the same and
    // brings CFL back to ~1, which is the only thing that preserves an interface.
    protected int _substeps = 4;

    /// <summary>
    /// Substeps needed at the CURRENT grid. CFL is |v|·dt in CELLS, so halving the cell size
    /// doubles the Courant number for the same physical speed — a substep count tuned at
    /// 1024 is half what 2048 needs, and the interface shreds for a reason that has nothing
    /// to do with the physics. Scaling with the grid keeps the accuracy constant and makes
    /// the resolution dropdown safe to touch.
    /// </summary>
    protected int SubstepsForGrid => Mathf.Max(1, Mathf.RoundToInt(_substeps * (N / 1024f)));

    // Measured peak speed, fed back from the readback. Drives adaptive substepping.
    private float _vPeak;

    /// <summary>
    /// Substeps chosen from the MEASURED peak velocity so that CFL stays near 1.
    ///
    /// A fixed count cannot work here, and the time series shows exactly why: at rest the
    /// domain needs ~5, but a breaking wave spikes |v|max from 4 to 11.5 for a few frames.
    /// Those few frames run at CFL > 2, the density interface is shredded, and it never
    /// comes back — the sharpening remap MAINTAINS an interface, it cannot reconstruct one
    /// that has been mixed away. With no density contrast there is no buoyancy, velocities
    /// decay, and the whole domain settles into uniform mush. Measured: interface 0.4% →
    /// 75% over ninety seconds, with |v|max peaking at 11.5 and then falling to 1.1.
    ///
    /// So the breaking event destroys the very thing that makes breaking possible. Spending
    /// the substeps only when the wave is actually breaking is both cheaper and the only
    /// thing that survives one.
    /// </summary>
    protected int AdaptiveSubsteps()
    {
        int floorN = SubstepsForGrid;
        if (_vPeak <= 0f) { return floorN; }
        int need = Mathf.CeilToInt(_vPeak * Dt / Mathf.Max(_targetCfl, 0.05f));
        return Mathf.Clamp(Mathf.Max(floorN, need), 1, 48);
    }

    protected float _targetCfl = 0.9f;
    private float _statT;
    private string _lastStat = "";
    protected float _speedGain = 240.0f;  // spurious currents are TINY; this is why
    protected int _k = 40;

    private const int ReferenceSweeps = 400;

    protected override string SceneTitle => "290 · slice — two-phase water/air, and does still water stay still?";

    protected override string SceneHint =>
        "Water and air as ONE density field in a vertical slice — the departure from every "
        + "height-field shore scene, because h(x) can steepen but can never overturn. The "
        + "test is hydrostatic equilibrium: still water under still air must sit perfectly "
        + "still, so every velocity you see at the waterline is discretisation error. Unlike "
        + "any Block A artifact this one does NOT converge away — it is an inconsistency "
        + "between how gravity is applied and how the pressure gradient is taken, so the "
        + "reference solve shows it surviving. Press SPACE to re-seed a flat waterline; "
        + "left-click to drop a blob and check the splash still behaves.";

    protected override string ArtifactName => "spurious currents (|v| where v must be 0)";

    protected override bool SideView => true;         // a cross-section, obviously

    // ── SCALE: back to the configuration that visually held (6 m box, 256²).
    //
    // IMPORTANT, so nobody re-learns it: that version was NOT in hydrostatic balance. It
    // simply had gravity weak enough not to show — ScaleForce(-0.012) at N=256 against
    // RefGrid 512 is -0.006 cells/tick², where the 80 m / 1024-cell version reached -0.07,
    // twelve times stronger. The water was falling in both; only one of them fell slowly
    // enough to look still over the seconds anyone watched.
    //
    // The beach proportions (DomainAspect 4–8, ~0.08 m/cell) and the reasoning behind them
    // are correct and are kept in docs/shore-slice.md. They are not usable until the
    // projection can actually converge the hydrostatic mode, because that mode is
    // DOMAIN-SCALE and relaxation resolves it slowest — the K ∝ N² wall from scene 250,
    // landing on the one mode that has to be right.

    protected override bool ArtifactViewDefault => false;
    protected override float ArtifactGainDefault => 6.0f;
    protected override float TimeScaleDefault => 1.0f;
    protected override float FieldGainDefault => 1.0f;
    protected override int[] GridOptions => new[] { 128, 256, 512 };
    protected override int GridDefault => 256;

    protected override float BaseDt => _dt;

    protected override Rid FieldRid => _fluid?.DyeRid ?? default;      // ρ
    protected override Rid ArtifactRid => _speed?.Rid ?? default;      // |v|

    protected override void BuildSim()
    {
        var rd = RenderingServer.GetRenderingDevice();
        _fluid = new FluidSim(rd, Grid, "fs_add_milk", extras: true, twoPhase: true);
        _fluid.MeasureResidual = true;
        if (!_fluid.TwoPhaseReady) { GD.PushError("[290] two-phase path failed to compile"); return; }
        _speed = new FieldProbe(rd, Grid, "stamp_speed", _fluid.VelRid, default);
        if (!_speed.Ready) { GD.PushError("[290] speed probe failed to compile"); }
        _seeded = false;
        _t = 0f;
    }

    protected override void FreeSim()
    {
        _speed?.Free();
        _speed = null;
        _fluid?.Free();
        _fluid = null;
    }

    protected override string ReadoutText() =>
        $"slice · {Grid.X}×{Grid.Y} · {PlaneSize.X:0}×{PlaneSize.Y:0} m ({PlaneSize.X / N:0.000} m/cell) · ρ ratio {_rhoWater / Mathf.Max(_rhoAir, 1e-5f):0} : 1 · "
        + $"{(ReferenceOn ? $"K {ReferenceSweeps} (REFERENCE — the artifact should SURVIVE this)" : $"K {_k}")}"
        + $" · t×{TimeScale:0.00} · {Engine.GetFramesPerSecond():0}fps";

    protected override void SimTick(double delta)
    {
        _t += (float)delta * TimeScale;
        _paddleT += 60f * (float)delta * TimeScale;   // sim TICKS, which is what the kernel's phase counts

        bool seed = !_seeded || Input.IsKeyPressed(Key.Space);
        if (seed) { _seeded = true; _t = 0f; }

        int sub = AdaptiveSubsteps();
        float dt = Dt / sub;
        // Each substep warm-starts from the last, so the solve does not need K sweeps from
        // scratch every time — fewer sweeps × more steps costs about the same and converges
        // better, because the pressure is never far from where it was.
        int iters = ReferenceOn ? ReferenceSweeps : Mathf.Max(20, _k / sub);

        // EVERY push constant takes the REAL grid, not (N, N). With a non-square domain the
        // two differ by the aspect, and the failure is silent and total: the seed kernel
        // computes y = c.y / size.y, so passing 1024 where the height is 128 keeps y below
        // 0.125 for every cell, every cell tests below the waterline, and the domain fills
        // 100% with water. Nothing errors — it just floods.
        var g = Grid;
        float[] add =
        {
            g.X, g.Y, dt, ScaleForce(_gravity),
            seed ? 1f : 0f, _fill, _rhoWater, _rhoAir, _stir,
            _paddleAmp, _paddleFreq, _paddleT, _drag, BedY0, BedSlope,
            _dragDepth, _sponge,
            0f, 0f, 0f,   // pad to 20 floats = 80 B
        };
        float[] advV = { g.X, g.Y, dt, 1.0f };   // no velocity fade: it would mask the artifact
        // 3rd float is ω for the RBGS pass, 4th the ρ floor guarding the divide
        float[] rhoPc =
        {
            g.X, g.Y, 1.0f, Mathf.Max(_rhoAir, 1e-5f) * 0.5f,
            _openTop ? 1f : 0f, _freeSlip ? 1f : 0f, BedY0, BedSlope,
            0f, 0f, 0f, 0f,   // 12 floats = 48 B
        };
        float[] advD = { g.X, g.Y, dt, 1.0f };   // ρ is conserved — never faded
        float[] mc = { g.X, g.Y, dt, 1.0f };
        float[] sp = { g.X, g.Y, _speedGain, 0f };
        float[] sharpen = { g.X, g.Y, _sharpen, _sharpPower, _rhoWater, _rhoAir, 0f, 0f };

        byte[] addB = ToBytes(add), advVB = ToBytes(advV), rhoB = ToBytes(rhoPc);
        byte[] advDB = ToBytes(advD), mcB = ToBytes(mc), spB = ToBytes(sp);
        byte[] shB = ToBytes(sharpen);
        bool mcOn = _macCormack;
        bool solidW = _solidWalls;

        // The seed must happen ONCE, not on every substep.
        byte[] addQuiet = addB;
        if (seed)
        {
            var quiet = (float[])add.Clone();
            quiet[4] = 0f;
            addQuiet = ToBytes(quiet);
        }
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_fluid == null) { return; }
            _fluid.UseSolidDivergence = solidW;
            for (int i = 0; i < sub; i++)
            {
                _fluid.StepTwoPhase(i == 0 ? addB : addQuiet, advVB, rhoB, advDB, iters, mcOn, mcB, shB);
            }
            _speed?.Run(spB);
        }));

        // ── DIAGNOSTIC READBACK, ~1 Hz. Deliberately measured rather than reasoned about:
        // two separate hypotheses about why this boils (missing wall BC, then unconverged
        // hydrostatic mode) both looked right and were both wrong, and both failures look
        // identical on screen. Numbers or nothing.
        _statT += (float)delta;
        if (_statT > 1.0f)
        {
            _statT = 0f;
            RenderingServer.CallOnRenderThread(Callable.From(LogStats));
        }
    }

    protected override void BuildSimKnobs(DemoUI ui)
    {
        ui.AddSlider("Gravity (cells/tick², down)", -0.05f, 0.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Waterline (fraction of height)", 0.1f, 0.9f, _fill, v => _fill = v);
        ui.AddSlider("ρ air (water 1.0 — real air is 0.0012)", 0.0004f, 0.5f, _rhoAir, v => _rhoAir = v);
        ui.AddToggle("MacCormack advection of ρ", _macCormack, on => _macCormack = on);
        ui.AddToggle("Solid-wall divergence (lets hydrostatic pressure exist at all)",
            _solidWalls, on => _solidWalls = on);
        ui.AddToggle("Open top (atmosphere can leave — off = sealed box, drifts)",
            _openTop, on => _openTop = on);
        ui.AddToggle("Free-slip walls (off = no-slip, pins the surface at the edges)",
            _freeSlip, on => _freeSlip = on);
        ui.AddSlider("Deliberate stir (0 = leave it alone)", 0.0f, 0.05f, _stir, v => _stir = v);
        ui.AddSlider("|v| readout gain (currents are tiny)", 10.0f, 2000.0f, _speedGain,
            v => _speedGain = v);
        ui.AddSlider("Substeps floor (adaptive above this)", 1, 12, _substeps, v => _substeps = (int)v);
        ui.AddSlider("Target CFL (lower = safer through a break, slower)", 0.2f, 2.0f,
            _targetCfl, v => _targetCfl = v);
        ui.AddSlider("Interface sharpening (0 = off, and it WILL fog)", 0.0f, 1.0f, _sharpen,
            v => _sharpen = v);
        ui.AddSlider("Sharpening power (harder edge)", 1.0f, 8.0f, _sharpPower,
            v => _sharpPower = v);
    }

    protected override void BuildArtifactKnobs(DemoUI ui)
    {
        // K is the usual truncation dial — but the POINT of this scene is that turning it
        // up does not remove the artifact, only the part of it that was ever truncation.
        AddCellLockedSlider(ui, "K — RBGS sweeps (won't converge the artifact away)", 4, 200,
            _k, v => _k = (int)v);
        ui.AddSlider("dt", 0.25f, 2.0f, _dt, v => _dt = v);
    }

    // The domain's floor and walls are already no-slip in fs_gradient_rho — they have simply
    // never been drawn. Without them the slice is ink under paper floating in the void, with
    // no cue for which way is down, which is exactly how a side view comes to read as a
    // top-down one. This draws the boundary that the physics already enforces.
    protected override void BuildDisplay()
    {
        var bed = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(PlaneSize.X * 1.02f, PlaneSize.Y * 0.06f, 0.4f) },
            Position = new Vector3(0f, -PlaneSize.Y * 0.5f - PlaneSize.Y * 0.03f, 0f),
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = Ink.SrgbToLinear(),
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(bed);
    }

    protected override void BuildRenderKnobs(DemoUI ui)
    {
        Mat.SetShaderParameter("mode", 0);
        Mat.SetShaderParameter("surface_level", _surfaceLevel);
        Mat.SetShaderParameter("bed_y0", BedY0 / Grid.Y);
        Mat.SetShaderParameter("bed_slope", BedSlope * Grid.X / Grid.Y);
        Mat.SetShaderParameter("surface_soft", _surfaceSoft);
        // Surface OFF shows the raw ρ, which is the honest view of how smeared it really is;
        // surface ON shows where the phase boundary sits. Keeping both reachable is the
        // difference between fixing the smearing and hiding it.
        ui.AddSlider("Surface threshold (0 = raw ρ, shows the true smear)", 0.0f, 1.0f,
            _surfaceLevel, v => Mat.SetShaderParameter("surface_level", v));
        ui.AddSlider("Surface softness (band width in ρ)", 0.002f, 0.5f, _surfaceSoft,
            v => Mat.SetShaderParameter("surface_soft", v));
    }

    // Flat, deduped, one line per CHANGE of character — not per frame.
    private void LogStats()
    {
        if (_fluid == null) { return; }
        var g = Grid;
        var vel = _fluid.ReadField(_fluid.VelRid);
        var rho = _fluid.ReadField(_fluid.DyeRid);
        var prs = _fluid.ReadField(_fluid.PressureRid);
        var dvg = _fluid.ReadField(_fluid.DivRid);

        // SPLIT BY PHASE. The projection gives air a mobility of 1/ρ_air ≈ 833 against
        // water's 1, so the same pressure error moves air ~833× harder. If |v|max is being
        // set by the air phase then the CFL limit is the air's, substeps tuned on water are
        // far too few, and the density ratio is buying realism at the cost of a timestep
        // nobody can afford. One number cannot tell those apart; two can.
        float vmax = 0f, vAir = 0f, vWat = 0f, dmax = 0f, rmin = 1e9f, rmax = -1e9f;
        float phaseMid = 0.5f * (_rhoWater + _rhoAir);
        for (int i = 0; i + 1 < vel.Length; i += 2)
        {
            float sp = Mathf.Sqrt(vel[i] * vel[i] + vel[i + 1] * vel[i + 1]);
            if (sp > vmax) { vmax = sp; }
            if (rho[i / 2] > phaseMid) { if (sp > vWat) { vWat = sp; } }
            else if (sp > vAir) { vAir = sp; }
        }
        foreach (var d in dvg) { float a = Mathf.Abs(d); if (a > dmax) { dmax = a; } }
        foreach (var r in rho) { if (r < rmin) { rmin = r; } if (r > rmax) { rmax = r; } }

        // Pressure down the middle column: hydrostatic means a straight ramp, so three
        // samples say whether a ramp exists at all and whether it has the right sign.
        int cx = g.X / 2;
        float pBot = prs[0 * g.X + cx], pMid = prs[(g.Y / 2) * g.X + cx], pTop = prs[(g.Y - 1) * g.X + cx];

        // ρ OUT OF RANGE is the tell for advection overshoot: ρ can only ever lie between
        // air and water, so anything outside means the scheme invented mass.
        // Interface thickness, as a FRACTION of cells sitting mid-phase. This is the
        // number behind "it reads as fog": a sharp interface is a thin line of transitional
        // cells; a smeared one has them everywhere.
        int mid = 0;
        float lo = _rhoAir + 0.15f * (_rhoWater - _rhoAir);
        float hi = _rhoAir + 0.85f * (_rhoWater - _rhoAir);
        foreach (var r in rho) { if (r > lo && r < hi) { mid++; } }
        float midPct = 100f * mid / rho.Length;
        float idealPct = 100f * g.X / rho.Length;   // one cell thick everywhere along x

        // ── BREAKER INDEX γ = H/h. For each column: the highest cell that is still water
        // is the surface; H is its rise above the still-water line and h is the still depth
        // over the bed there. A wave breaks at γ ≈ 0.78, so the MAXIMUM of H/h across the
        // profile — and where it occurs — says whether this is a real breaker or a picture.
        float still = _fill * g.Y;
        float gMax = 0f; int gx = 0; float hAtMax = 0f, HAtMax = 0f;
        for (int x = 0; x < g.X; x++)
        {
            int top = -1;
            for (int y = g.Y - 1; y >= 0; y--)
            {
                if (rho[y * g.X + x] > 0.5f) { top = y; break; }
            }
            if (top < 0) { continue; }
            float bedY = BedY0 + BedSlope * (x + 0.5f);
            float h = still - bedY;
            if (h < 4f) { continue; }                 // inside the swash, γ is meaningless
            float H = top - still;
            if (H <= 0f) { continue; }
            float r = H / h;
            if (r > gMax) { gMax = r; gx = x; hAtMax = h; HAtMax = H; }
        }

        string stat = $"[290] γ {gMax:0.00} @x{gx} (H {HAtMax * (PlaneSize.Y / g.Y):0.00}m / "
            + $"h {hAtMax * (PlaneSize.Y / g.Y):0.00}m) · interface {midPct:0.00}% mid-phase (1-cell ideal {idealPct:0.00}%) · "
            + $"|v| air {vAir:0.0}/water {vWat:0.0} · |v|max {vmax:0.0000} · |div|max {dmax:0.0000} · "
            + $"rho [{rmin:0.000}..{rmax:0.000}] · p bot/mid/top {pBot:0.000}/{pMid:0.000}/{pTop:0.000}";
        _vPeak = vmax;   // feeds AdaptiveSubsteps on the next tick
        if (stat != _lastStat) { GD.Print(stat); _lastStat = stat; }
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
