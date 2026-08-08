using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// PhysicsProbe — runs the Path S / Path W hypotheses from docs/hypotheses.md.
//
// WHY THIS IS SEPARATE FROM SolverBench. SolverBench answers "am I solving A x = b" — it
// reports residuals. Every hypothesis here answers a different question: *is A the right
// matrix*. Those need a seeded initial condition, field readback, and conserved quantities
// (volume, energy, symmetry) rather than a residual, so they get their own tool. Same local
// RenderingDevice discipline: no scene, no rendering, explicit Submit/Sync.
//
// SEEDING WITHOUT TOUCHING THE SOLVERS. The solver textures are created without
// CAN_UPDATE_BIT, so TextureUpdate on them fails. They DO have CAN_COPY_TO_BIT — so the probe
// allocates its own staging texture with CAN_UPDATE_BIT, writes the initial condition there,
// and TextureCopy's it in. No solver change, no shared-code risk.
//
// Run:
//   tools/godot-mono.sh --path . res://tools/probe.tscn -- probe=symmetry
//   tools/godot-mono.sh --path . res://tools/probe.tscn -- probe=all
public partial class PhysicsProbe : Node
{
    private const string StampPath = "res://shaders/stamp/stamp_wave_tank.glslinc";

    private string _probe = "all";
    private int _grid = 128;
    private int _steps = 220;
    private int _sweeps = 40;

    private RenderingDevice _rd = null!;

    public override void _Ready()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("probe=")) { _probe = a.Substring(6); }
            else if (a.StartsWith("grid=")) { _grid = a.Substring(5).ToInt(); }
            else if (a.StartsWith("steps=")) { _steps = a.Substring(6).ToInt(); }
            else if (a.StartsWith("sweeps=")) { _sweeps = a.Substring(7).ToInt(); }
        }

        _rd = RenderingServer.CreateLocalRenderingDevice();
        if (_rd == null)
        {
            GD.PrintErr("[PhysicsProbe] no local RenderingDevice — run windowed, not --headless.");
            GetTree().Quit(1);
            return;
        }

        bool all = _probe == "all";
        if (all || _probe == "symmetry") { ProbeSymmetry(); }
        if (all || _probe == "energy") { ProbeEnergy(); }
        if (all || _probe == "volume") { ProbeVolume(); }
        if (all || _probe == "floor") { ProbeFloor(); }
        if (all || _probe == "speed") { ProbeSpeed(); }
        if (all || _probe == "pressure") { ProbePressure(); }
        if (all || _probe == "steepen") { ProbeSteepen(); }
        if (all || _probe == "void") { ProbeVoid(); }

        GD.Print("PROBE DONE");
        _rd.Free();
        GetTree().Quit();
    }

    // ── H-S5 · symmetry survives every solver ────────────────────────────────
    // A centred drop on a square domain with symmetric walls must stay 4-fold symmetric to
    // float precision. Asymmetry means a stencil or parity bug and nothing else — RBGS's
    // red/black split is the obvious way to break it.
    private void ProbeSymmetry()
    {
        GD.Print("PROBE,H-S5 symmetry,solver,grid,max|h|,err_x,err_z,rel_err,verdict");
        foreach (int mode in new[] { 0, 1, 2, 3, 4, 5, 6, 7 })
        {
            var s = Make(mode, _grid);
            if (s == null || !s.Ready) { GD.Print($"PROBE,H-S5,mode{mode},{_grid},SKIP"); s?.Free(); continue; }

            SeedCentredDrop(s, _grid, 1.0f, _grid / 12f);
            Run(s, 60, k => Pc(_grid, cn: 1f, a: 0f, leak: 0f, dcMode: 0, dropW: 0f));

            float[] f = Read(s, _grid);
            float mx = 0f, ex = 0f, ez = 0f;
            for (int z = 0; z < _grid; z++)
            for (int x = 0; x < _grid; x++)
            {
                float v = f[x + _grid * z];
                mx = Math.Max(mx, Math.Abs(v));
                ex = Math.Max(ex, Math.Abs(v - f[(_grid - 1 - x) + _grid * z]));
                ez = Math.Max(ez, Math.Abs(v - f[x + _grid * (_grid - 1 - z)]));
            }
            float rel = mx > 0 ? Math.Max(ex, ez) / mx : 0f;
            string verdict = rel < 1e-5f ? "SYMMETRIC" : "BROKEN";
            GD.Print($"PROBE,H-S5,{s.ModeName},{_grid},{mx:0.000000e+00},{ex:0.000000e+00},{ez:0.000000e+00},{rel:0.000000e+00},{verdict}");
            s.Free();
        }
    }

    // ── H-S2 · Crank-Nicolson is non-dissipative, backward-Euler is not ──────
    // mna-next-steps.md §0 calls this "the important one" and §6 owes scene 06 a retrofit
    // BECAUSE of it. It has never been measured. With damping and leak at zero the only loss
    // left is numerical, so tracking energy (sum h^2) isolates exactly the claimed effect.
    private void ProbeEnergy()
    {
        GD.Print("PROBE,H-S2 CN vs BE,cn,step0,step50,step100,step200,retained_%");
        foreach (float cn in new[] { 0f, 0.5f, 1f })
        {
            var s = Make(1, _grid);   // RBGS: best converger, so under-convergence is not the story
            if (s == null || !s.Ready) { s?.Free(); continue; }

            SeedCentredDrop(s, _grid, 1.0f, _grid / 12f);
            double e0 = Energy(Read(s, _grid));
            double e50 = 0, e100 = 0, e200 = 0;
            for (int k = 1; k <= 200; k++)
            {
                Run(s, 1, _ => Pc(_grid, cn: cn, a: 0f, leak: 0f, dcMode: 0, dropW: 0f));
                if (k == 50) { e50 = Energy(Read(s, _grid)); }
                if (k == 100) { e100 = Energy(Read(s, _grid)); }
                if (k == 200) { e200 = Energy(Read(s, _grid)); }
            }
            GD.Print($"PROBE,H-S2,{cn:0.0},{e0:0.0000e+00},{e50:0.0000e+00},{e100:0.0000e+00},{e200:0.0000e+00},{(e0 > 0 ? 100.0 * e200 / e0 : 0):0.00}");
            s.Free();
        }
    }

    // ── H-S3 · the DC modes actually conserve volume ─────────────────────────
    // solver-ledger.md §4 spends pages on "a monopole source pumps net volume into a closed
    // box" and lists six resolutions. NONE has been measured — the choice between them is
    // currently aesthetic. Sum h is a number with a known correct behaviour: flat.
    private void ProbeVolume()
    {
        GD.Print("PROBE,H-S3 volume,dc_mode,sum_h_start,sum_h_end,drift,verdict");
        foreach (int dc in new[] { 0, 1, 2, 3, 4, 5 })
        {
            var s = Make(1, _grid);
            if (s == null || !s.Ready) { s?.Free(); continue; }

            // A monopole paddle drive is the thing §4 says pumps volume.
            //
            // LEAK MUST BE NONZERO. First run of this probe set leak = 0 "so the spring cannot
            // hide the drift", and modes 0-4 returned byte-identical numbers — because
            // st_leak() is `(dc_mode == 3) ? 0.0 : pc.leak`, so leak = 0 collapses five of the
            // six modes onto the same operator. Mode 3 also spends the leak slot on its volume
            // correction (`force -= pc.leak`), so at leak = 0 it is a no-op too. Turning the
            // mechanism off to isolate it removed the thing being compared.
            //
            // 9e-4 is the value ledger §4 measured in scene 25 (kappa = 9e-4 at 197 Hz).
            const float Leak = 9e-4f;
            for (int k = 0; k < _steps; k++)
            {
                Run(s, 1, _ => Pc(_grid, cn: 1f, a: 0.002f, leak: Leak, dcMode: dc, dropW: 0f, paddle: true, step: k));
            }
            float[] f = Read(s, _grid);
            double sum = 0; foreach (float v in f) { sum += v; }
            double mean = sum / f.Length;
            string verdict = Math.Abs(mean) < 1e-5 ? "FLAT" : "DRIFTS";
            GD.Print($"PROBE,H-S3,{dc},0.000000e+00,{sum:0.000000e+00},{mean:0.000000e+00},{verdict}");
            s.Free();
        }
    }

    // ── H-S6 · the residual floor is float32, not stalled convergence ────────
    // §8c ASSERTS the ~5e-08 plateau is the fp32 arithmetic floor, inferred from magnitude
    // alone. If that is true the floor scales with the field; if it is stalled convergence it
    // does not. One variable — the source amplitude — changed by 100x.
    private void ProbeFloor()
    {
        GD.Print("PROBE,H-S6 floor,amplitude,residual_l2,residual/amplitude");
        foreach (float amp in new[] { 0.01f, 1.0f, 100.0f })
        {
            var s = Make(1, _grid);
            if (s == null || !s.Ready) { s?.Free(); continue; }

            SeedCentredDrop(s, _grid, amp, _grid / 12f);
            for (int k = 0; k < 40; k++)
            {
                Run(s, 1, _ => Pc(_grid, cn: 1f, a: 0.002f, leak: 0f, dcMode: 0, dropW: 0f));
            }
            // step-sync-step: Step()'s readback runs before Submit on a local device
            s.Step(Pc(_grid, 1f, 0.002f, 0f, 0, 0f), _sweeps, true); _rd.Submit(); _rd.Sync();
            s.Step(Pc(_grid, 1f, 0.002f, 0f, 0, 0f), _sweeps, true); _rd.Submit(); _rd.Sync();

            float r = s.LastResidual;
            GD.Print($"PROBE,H-S6,{amp:0.####},{r:0.000000e+00},{r / amp:0.000000e+00}");
            s.Free();
        }
    }

    // ── H-W3 · wave speed follows sqrt(beta) ─────────────────────────────────
    // In the stamp, wave speed IS the conductance — which is why a void-fraction (bubble)
    // field is a conductance modulation. Oracle: c = sqrt(g h), so beta x4 must give c x2, so
    // a pulse must cross the tank in half the steps. If this does not hold, the whole
    // bubbles-as-medium-change idea is wrong.
    private void ProbeSpeed()
    {
        GD.Print("PROBE,H-W3 speed,beta,steps_to_reach_r,implied_c,c_ratio_vs_first");
        double first = 0;
        foreach (float beta in new[] { 0.05f, 0.20f, 0.80f })
        {
            var s = Make(1, _grid);
            if (s == null || !s.Ready) { s?.Free(); continue; }

            SeedCentredDrop(s, _grid, 1.0f, 3.0f);
            int probeR = _grid / 4;
            int hit = -1;
            for (int k = 1; k <= 400 && hit < 0; k++)
            {
                Run(s, 1, _ => Pc(_grid, cn: 1f, a: 0f, leak: 0f, dcMode: 0, dropW: 0f, beta: beta));
                float[] f = Read(s, _grid);
                int c0 = _grid / 2;
                float atR = Math.Abs(f[(c0 + probeR) + _grid * c0]);
                if (atR > 1e-3f) { hit = k; }
            }
            double c = hit > 0 ? (double)probeR / hit : 0;
            if (first == 0) { first = c; }
            GD.Print($"PROBE,H-W3,{beta:0.####},{hit},{c:0.0000},{(first > 0 ? c / first : 0):0.000}");
            s.Free();
        }
    }

    // ── H-W1 · projection actually projects (§8b) ────────────────────────────
    // The pressure Poisson's RESIDUAL IS the projection error: if ||b - Ax|| -> 0 with
    // b = -div, then ∇²p = -div exactly, which is all projection needs. So this answers H-W1
    // with no velocity field and no FluidSim3D surgery — the existing residual machinery
    // already measures exactly the right quantity.
    //
    // One Step per solver, because pressure is solved FRESH each frame from the current
    // divergence — there is no time history to advance. The residual is computed inside the
    // compute list before Step()'s state copies, so it sees the correct divergence.
    private void ProbePressure()
    {
        GD.Print("PROBE,H-W1 pressure,solver,iters,dispatches,residual_l2,residual_rms,vs_jacobi");
        foreach (int iters in new[] { 8, 24 })
        {
            double jac = 0;
            foreach (int mode in new[] { 0, 1, 3, 4, 5, 6, 7 })
            {
                var s = MakeWith(mode, _grid, PressureStampPath);
                if (s == null || !s.Ready) { GD.Print($"PROBE,H-W1,mode{mode},{iters},-,FAILED_INIT,-,-"); s?.Free(); continue; }

                SeedDivergence(s, _grid);
                // step-sync-step: Step()'s BufferGetData runs before Submit on a local device
                s.Step(PressurePc(_grid), iters, true); _rd.Submit(); _rd.Sync();
                SeedDivergence(s, _grid);
                s.Step(PressurePc(_grid), iters, true); _rd.Submit(); _rd.Sync();

                double l2 = s.LastResidual;
                double rms = l2 / _grid;
                if (mode == 0) { jac = rms; }
                GD.Print($"PROBE,H-W1,{s.ModeName},{iters},{s.PassesPerStep(iters)}," +
                         $"{l2:0.000000e+00},{rms:0.000000e+00},{(rms > 0 && jac > 0 ? jac / rms : 0):0.00}x");
                s.Free();
            }
        }
    }

    // A zero-MEAN divergence field. Zero mean is required, not cosmetic: the all-Neumann
    // Poisson operator is singular, so a solution exists only if b lies in its range. A field
    // with nonzero mean leaves an irreducible residual that would look like solver failure.
    private void SeedDivergence(IStampSolver s, int n)
    {
        var f = new float[n * n];
        double sum = 0;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            // a few incommensurate lobes — broadband, not a single mode any solver could
            // accidentally be exact on
            float v = MathF.Sin(6.283f * 3f * x / n) * MathF.Cos(6.283f * 2f * z / n)
                    + 0.5f * MathF.Sin(6.283f * 7f * z / n) * MathF.Cos(6.283f * 5f * x / n);
            f[x + n * z] = v;
            sum += v;
        }
        float mean = (float)(sum / f.Length);
        for (int i = 0; i < f.Length; i++) { f[i] -= mean; }
        Upload(s.HeightRid, f, n);
    }

    private static byte[] PressurePc(int n)
    {
        var b = new byte[128];
        float[] v = { n, n, 1.0f, 0.0f };   // size.xy, scale, reg (0 = the true singular operator)
        Buffer.BlockCopy(v, 0, b, 0, 16);
        return b;
    }

    // ── H-W6 · nonlinear depth produces front steepening ─────────────────────
    // THE discriminating test for breaking waves. A linear wave preserves its profile forever
    // and can never break, at any resolution with any solver — that is physics, not numerics.
    // With depth = bed + h, the crest sits in deeper water than the trough, so it outruns it
    // and the front face steepens.
    //
    // Metric: max|dh/dx| over the field. Linear (nl = 0) should hold flat or fall (dispersion
    // spreads the hump). Nonlinear (nl > 0) should GROW, and grow faster with larger nl —
    // which is what makes this a rate measurement rather than a present/absent one.
    //
    // Oracle: Saint-Venant. Crest speed exceeds trough speed by roughly Δc/c ≈ η/(2h), so
    // steepening rate should scale with amplitude and with nl.
    private void ProbeSteepen()
    {
        GD.Print("PROBE,H-W6 steepen,nl,amp,slope_0,slope_40,slope_80,slope_160,growth");
        foreach (float nl in new[] { 0f, 0.5f, 1f })
        {
            var s = MakeWith(1, _grid, NlStampPath);
            if (s == null || !s.Ready) { GD.Print($"PROBE,H-W6,nl={nl},FAILED_INIT"); s?.Free(); continue; }

            const float amp = 0.35f;   // a real fraction of the 1.0 reference depth: nonlinearity
                                       // is an amplitude effect, so a tiny ripple shows nothing

            // A PLANE hump (uniform in z), not a radial drop. First run of this probe used a
            // centred drop and measured no steepening at ANY nl — because a radially spreading
            // hump loses amplitude as ~1/sqrt(r), and nonlinearity is an amplitude effect, so
            // the spreading outran the steepening. Steepening is a 1D phenomenon; a plane wave
            // has no geometric spreading, so the nonlinear term is the only thing acting on the
            // profile. Wrong geometry, not wrong physics.
            SeedPlaneHump(s, _grid, amp, _grid / 10f);
            double s0 = MaxSlope(Read(s, _grid), _grid);
            double s40 = 0, s80 = 0, s160 = 0;
            for (int k = 1; k <= 160; k++)
            {
                Run(s, 1, _ => NlPc(_grid, nl));
                if (k == 40) { s40 = MaxSlope(Read(s, _grid), _grid); }
                if (k == 80) { s80 = MaxSlope(Read(s, _grid), _grid); }
                if (k == 160) { s160 = MaxSlope(Read(s, _grid), _grid); }
            }
            GD.Print($"PROBE,H-W6,{nl:0.0},{amp:0.00},{s0:0.0000e+00},{s40:0.0000e+00}," +
                     $"{s80:0.0000e+00},{s160:0.0000e+00},{(s40 > 0 ? s160 / s40 : 0):0.000}");
            s.Free();
        }
    }

    // Uniform in z, gaussian in x — a plane wave. Equal curr/prev = at rest, so it splits into
    // two counter-propagating halves; each is a clean 1D pulse.
    private void SeedPlaneHump(IStampSolver s, int n, float amp, float radius)
    {
        var f = new float[n * n];
        float x0 = (n - 1) * 0.5f;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - x0;
            f[x + n * z] = amp * MathF.Exp(-(dx * dx) / (radius * radius));
        }
        Upload(s.HeightRid, f, n);
        Upload(s.PrevRid, f, n);
    }

    private static double MaxSlope(float[] f, int n)
    {
        double m = 0;
        for (int z = 1; z < n - 1; z++)
        for (int x = 1; x < n - 1; x++)
        {
            double dx = f[(x + 1) + n * z] - f[(x - 1) + n * z];
            m = Math.Max(m, Math.Abs(dx) * 0.5);
        }
        return m;
    }

    // Uniform reference depth 1.0 (extra.z = 0 -> flat bed), no sponge, no leak, CN on, so the
    // ONLY thing that differs across rows is extra.x = nl.
    private static byte[] NlPc(int n, float nl)
    {
        float[] v =
        {
            n, n, 0.20f, 0f,
            0f, 1f, 0f, 0f,
            -1f, 0f, 0f, 0.05f,     // min_depth 0.05 keeps beta positive under a deep trough
            0f, 0f, 0.12f, 0f,
            0f, 2f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 1f, 0f,
            nl, 0f, 0f, 1f,          // extra.x = NONLINEARITY, extra.w = ref depth 1.0
        };
        var b = new byte[128];
        Buffer.BlockCopy(v, 0, b, 0, 128);
        return b;
    }

    // ── H-B2 · aeration refracts: a void field is a medium ───────────────────
    // H-W3 showed c ∝ sqrt(beta) for a UNIFORM beta. This is the spatial version, and it is the
    // one that matters for bubbles: does an aerated patch behave as a genuine medium?
    //
    // A plane pulse launched along +x meets a domain split in z — clear water in the near half,
    // aerated in the far half. After N steps the front has travelled x1 and x2 in the two
    // halves, and the oracle is
    //     x1 / x2 = c1 / c2 = sqrt(1 / voidMul)
    // The kink between the two fronts IS the refraction; measuring distances rather than
    // fitting a wavefront angle makes it robust.
    private void ProbeVoid()
    {
        GD.Print("PROBE,H-B2 void,void_mul,front_clear,front_aerated,ratio,predicted,err_%");
        foreach (float vm in new[] { 1.0f, 0.5f, 0.25f })
        {
            var s = MakeWith(1, _grid, VoidStampPath);
            if (s == null || !s.Ready) { GD.Print($"PROBE,H-B2,{vm},FAILED_INIT"); s?.Free(); continue; }

            SeedPlaneLine(s, _grid, 1.0f, 8, 3.0f);   // narrow plane pulse near the -x wall
            for (int k = 0; k < 90; k++) { Run(s, 1, _ => VoidPc(_grid, vm)); }

            float[] f = Read(s, _grid);
            double fc = FrontX(f, _grid, _grid / 4);          // clear half
            double fa = FrontX(f, _grid, 3 * _grid / 4);      // aerated half
            double ratio = fa > 0 ? fc / fa : 0;
            double pred = Math.Sqrt(1.0 / vm);
            double err = pred > 0 ? 100.0 * (ratio - pred) / pred : 0;
            GD.Print($"PROBE,H-B2,{vm:0.###},{fc:0.0},{fa:0.0},{ratio:0.0000},{pred:0.0000},{err:0.0}");
            s.Free();
        }
    }

    // Leading edge along one z row: furthest x whose |h| still exceeds a threshold, measured
    // from the launch column.
    private static double FrontX(float[] f, int n, int z)
    {
        const float Thresh = 2e-3f;
        for (int x = n - 2; x > 0; x--)
        {
            if (Math.Abs(f[x + n * z]) > Thresh) { return x - 8; }
        }
        return 0;
    }

    private void SeedPlaneLine(IStampSolver s, int n, float amp, int x0, float radius)
    {
        var f = new float[n * n];
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - x0;
            f[x + n * z] = amp * MathF.Exp(-(dx * dx) / (radius * radius));
        }
        Upload(s.HeightRid, f, n);
        Upload(s.PrevRid, f, n);
    }

    private static byte[] VoidPc(int n, float voidMul)
    {
        float[] v =
        {
            n, n, 0.25f, 0f,
            0f, 1f, 0f, 0f,
            -1f, 0f, 0f, 0.02f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            voidMul, 0f, 0f, 1f,     // extra.x = void multiplier, extra.w = ref depth
        };
        var b = new byte[128];
        Buffer.BlockCopy(v, 0, b, 0, 128);
        return b;
    }

    // ── plumbing ─────────────────────────────────────────────────────────────
    private const string PressureStampPath = "res://shaders/stamp/stamp_pressure.glslinc";
    private const string NlStampPath = "res://shaders/stamp/stamp_wave_nl.glslinc";
    private const string VoidStampPath = "res://shaders/stamp/stamp_wave_void.glslinc";

    private IStampSolver? Make(int mode, int n) => MakeWith(mode, n, StampPath);

    private IStampSolver? MakeWith(int mode, int n, string stamp)
    {
        var g = new Vector2I(n, n);
        return mode switch
        {
            0 => new GpuStampSolver(_rd, g, stamp, GpuStampSolver.Mode.Jacobi),
            1 => new GpuStampSolver(_rd, g, stamp, GpuStampSolver.Mode.Rbgs),
            2 => new AdiStampSolver(_rd, g, stamp),
            3 => new GpuStampSolver(_rd, g, stamp, GpuStampSolver.Mode.Cg),
            4 => new MgvSolver(_rd, g, stamp),
            5 => new MgDeepSolver(_rd, g, stamp),
            6 => new SchwarzSolver(_rd, g, stamp),
            7 => new SpectralSolver(_rd, g, stamp, SpectralSolver.Basis.Cosine),
            _ => null,
        };
    }

    private void Run(IStampSolver s, int steps, Func<int, byte[]> pc)
    {
        for (int k = 0; k < steps; k++) { s.Step(pc(k), _sweeps, false); }
        _rd.Submit();
        _rd.Sync();
    }

    // Staging texture with CAN_UPDATE_BIT -> TextureCopy into the solver's h_curr AND h_prev.
    // Equal curr/prev = zero initial velocity, so the mode starts at rest at full amplitude.
    private void SeedCentredDrop(IStampSolver s, int n, float amp, float radius)
    {
        var f = new float[n * n];
        float c0 = (n - 1) * 0.5f;
        for (int z = 0; z < n; z++)
        for (int x = 0; x < n; x++)
        {
            float dx = x - c0, dz = z - c0;
            f[x + n * z] = amp * MathF.Exp(-(dx * dx + dz * dz) / (radius * radius));
        }
        Upload(s.HeightRid, f, n);
        Upload(s.PrevRid, f, n);
    }

    // Staging texture with CAN_UPDATE_BIT -> TextureCopy into a solver texture. The solvers'
    // own textures lack CAN_UPDATE_BIT so TextureUpdate on them fails, but they DO have
    // CAN_COPY_TO_BIT — so seeding costs one temporary and touches no solver code.
    private void Upload(Rid dst, float[] f, int n)
    {
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.StorageBit,
        };
        Rid stage = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureUpdate(stage, 0, bytes);
        _rd.TextureCopy(stage, dst, Vector3.Zero, Vector3.Zero, new Vector3(n, n, 1), 0, 0, 0, 0);
        _rd.Submit();
        _rd.Sync();
        _rd.FreeRid(stage);
    }

    private float[] Read(IStampSolver s, int n)
    {
        byte[] d = _rd.TextureGetData(s.HeightRid, 0);
        var f = new float[n * n];
        Buffer.BlockCopy(d, 0, f, 0, Math.Min(d.Length, f.Length * 4));
        return f;
    }

    private static double Energy(float[] f)
    {
        double e = 0;
        foreach (float v in f) { e += (double)v * v; }
        return e;
    }

    // Same 32-float stamp layout SolverBench uses. Uniform depth (extra.z = 0) and no sponge
    // throughout: those are the conditions under which every solver inverts the SAME matrix.
    private static byte[] Pc(int n, float cn, float a, float leak, int dcMode, float dropW,
                            bool paddle = false, int step = 0, float beta = 0.30f)
    {
        float edgeMaskAndDc = 0f + 16f * dcMode;   // low 4 bits = sponge edges (none), dc mode << 4
        float[] v =
        {
            n, n, beta, a,
            leak, cn, 0f, 0f,
            -1f, 0f, 0f, 1f,
            paddle ? -0.9f : 0f, paddle ? -0.9f : 0f, 0.12f, paddle ? 0.35f : 0f,
            paddle ? 0.10f : 0f, 2f, step * 0.21f, (step + 1) * 0.21f,
            0f, 0f, 0f, 0f,
            (n - 1) * 0.5f, (n - 1) * 0.5f, n / 12f, dropW,
            0f, edgeMaskAndDc, 0f, 1f,
        };
        var b = new byte[128];
        Buffer.BlockCopy(v, 0, b, 0, 128);
        return b;
    }
}
