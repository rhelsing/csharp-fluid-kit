using System;
using System.Diagnostics;
using System.Text;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Solver bench — measures solver cost and convergence on a LOCAL RenderingDevice, with no
// scene, no rendering and no frame loop (solver-ledger.md §9).
//
// WHY THIS EXISTS. The old method drove scene 25 and read its on-screen ms/frame. Every
// number that came out of it was compromised, and all five defects had one cause — the
// benchmark ran inside the render loop of a live scene:
//   * SATURATION. ADI, Schwarz, CG and Spectral all reported exactly 7.5 fps / 133.3 ms at
//     several grids. That is MaxTicksPerFrame clamping, not a measurement. Any solver slower
//     than the frame budget reported the same number, so the interesting half of the table
//     was blank.
//   * CONTENTION. One Jacobi config measured 90, 188, 211 and 231 fps depending on what else
//     was running, because each data point booted its own Godot.
//   * FRAME TIME != SOLVER TIME. ms/tick was frame time / ticks and included render and UI.
//   * BOOT COST. ~15-20 s per data point for ~100 ms of actual work.
//   * UNNORMALIZED RESIDUAL. LastResidual is ||r||_2 = sqrt(sum r^2), which grows as N for
//     the same per-cell error, so comparing grids was invalid.
//
// HOW A LOCAL DEVICE FIXES ALL OF IT. RenderingServer.CreateLocalRenderingDevice() gives a
// device with explicit Submit()/Sync(), isolated from the frame loop. Every solver already
// takes `rd` as its first constructor argument, so they run on it unchanged. That yields:
//   * a real per-step GPU cost with no timestamp API — Sync, start clock, enqueue K steps,
//     Submit+Sync, stop, divide by K. Godot 4.6 exposes no GPU timestamps; a forced sync
//     around a batch is the honest substitute, and batching amortizes the sync itself.
//   * no render, no UI, no tick cap, so nothing saturates.
//   * one process for the whole sweep instead of one per data point.
//
// RESIDUAL IS REPORTED AS RMS (||r|| / sqrt(cells)) so grids are comparable. The L2 figure is
// kept alongside it because every previous measurement in the ledger is in those units.
//
// Run:
//   tools/godot-mono.sh --path . res://tools/bench.tscn -- \
//       solvers=0,1,2,3,4,5,6,7,8 grids=256,512 sweeps=24 reps=40
public partial class SolverBench : Node
{
    private const string StampPath = "res://shaders/stamp/stamp_wave_tank.glslinc";

    // beta at 256^2. beta = g*h*dt^2/dx^2 is the Courant number squared; working scenes sit at
    // 0.1-0.65 (solver-ledger.md §4). Relaxation convergence collapses as beta grows, which is
    // the entire reason the iteration-vs-resolution curve is worth plotting.
    private const float BetaAt256 = 0.30f;

    // 3D. beta at 64^3 — the 3D analog of BetaAt256: same Courant-squared meaning, referenced
    // to the grid the 3D scenes actually run (scene 08 is 48^3).
    private const float BetaAt64 = 0.30f;
    private const string Stamp3DPath = "res://shaders/stamp3d/stamp_blob_3d.glslinc";

    private int[] _solvers = { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
    private int[] _grids = { 256, 512 };
    // The 3D sweep is opt-in: pass grids3d=. Left empty the bench behaves exactly as before.
    private int[] _solvers3d = { 0, 1, 3 };
    private int[] _grids3d = Array.Empty<int>();
    private int _sweeps = 24;
    private int _reps = 40;
    private int _warmup = 8;

    public override void _Ready()
    {
        foreach (string a in OS.GetCmdlineUserArgs())
        {
            if (a.StartsWith("solvers3d=")) { _solvers3d = ParseInts(a.Substring(10)); }
            else if (a.StartsWith("grids3d=")) { _grids3d = ParseInts(a.Substring(8)); }
            else if (a.StartsWith("solvers=")) { _solvers = ParseInts(a.Substring(8)); }
            else if (a.StartsWith("grids=")) { _grids = ParseInts(a.Substring(6)); }
            else if (a.StartsWith("sweeps=")) { _sweeps = a.Substring(7).ToInt(); }
            else if (a.StartsWith("reps=")) { _reps = a.Substring(5).ToInt(); }
            else if (a.StartsWith("warmup=")) { _warmup = a.Substring(7).ToInt(); }
        }

        var rd = RenderingServer.CreateLocalRenderingDevice();
        if (rd == null)
        {
            GD.PrintErr("[SolverBench] no local RenderingDevice (are you running --headless? "
                        + "the dummy driver cannot create one). Run windowed.");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"BENCH2,solver,grid,sweeps,dispatches,ms_per_step,residual_l2,residual_rms");
        foreach (int g in _grids)
        {
            foreach (int m in _solvers)
            {
                RunOne(rd, m, g);
            }
        }
        if (_grids3d.Length > 0)
        {
            GD.Print("BENCH2 NOTE,3D rows use stamp_blob_3d — a DIFFERENT operator from the 2D "
                     + "stamp_wave_tank. Compare 3D rows only to each other.");
        }
        foreach (int g in _grids3d)
        {
            foreach (int m in _solvers3d)
            {
                RunOne3D(rd, m, g);
            }
        }
        GD.Print("BENCH2 DONE");
        rd.Free();
        GetTree().Quit();
    }

    private void RunOne(RenderingDevice rd, int mode, int n) =>
        Measure(rd, Make(rd, mode, new Vector2I(n, n)), $"mode{mode}", n.ToString(),
            (long)n * n, step => Pc(n, step));

    // Same measurement, a volume instead of a plane. Reported cells is n^3 so residual_rms
    // stays per-cell.
    //
    // 3D ROWS ARE ONLY COMPARABLE TO EACH OTHER. stamp_blob_3d is a DIFFERENT OPERATOR from
    // stamp_wave_tank — central-source blob, no Crank-Nicolson term, no bathymetry, no sponge
    // — so a 3D residual read against a 2D one is meaningless no matter how the units line up.
    // Only the within-column ranking (which 3D solver beats which) carries information.
    private void RunOne3D(RenderingDevice rd, int mode, int n) =>
        Measure(rd, Make3D(rd, mode, new Vector3I(n, n, n)), $"mode3d{mode}", $"{n}^3",
            (long)n * n * n, step => Pc3D(n, step));

    private void Measure(RenderingDevice rd, IStampSolver? s, string tag, string gridLabel,
        long cells, Func<int, byte[]> pc)
    {
        if (s == null || !s.Ready)
        {
            // Distinguish the two failures. `null` means the id is not in the factory's switch
            // at all — a typo or, more often, a 3D id borrowed from the 2D numbering (2 is ADI,
            // which has no 3D form, so solvers3d=2 is unmapped, NOT broken). `!Ready` means the
            // solver was constructed and its own init failed, which is a real bug worth chasing.
            string why = s == null ? "UNMAPPED_ID" : "FAILED_INIT";
            GD.Print($"BENCH2,{tag},{gridLabel},{_sweeps},-,{why},-,-");
            s?.Free();
            return;
        }

        try
        {
            // Warm up: build a real field AND let the driver settle. Measuring the first step
            // measures pipeline creation, not the solver.
            for (int k = 0; k < _warmup; k++) { s.Step(pc(k), _sweeps, false); }
            rd.Submit();
            rd.Sync();

            // Timed batch. No residual readback inside — that would force a stall per step and
            // turn a throughput measurement into a latency one.
            long t0 = Stopwatch.GetTimestamp();
            for (int k = 0; k < _reps; k++) { s.Step(pc(_warmup + k), _sweeps, false); }
            rd.Submit();
            rd.Sync();
            long t1 = Stopwatch.GetTimestamp();

            double msPerStep = (t1 - t0) * 1000.0 / Stopwatch.Frequency / _reps;

            // Residual, taken after timing so its readback cannot contaminate the number above.
            //
            // TWO steps, not one, and this is not belt-and-braces. Every solver calls
            // BufferGetData INSIDE Step() — which is correct on the global device, where the
            // work is already in flight, but on a LOCAL device nothing executes until
            // Submit()/Sync(). So the first measured step reads a buffer the GPU has not
            // written yet: stale on a warm buffer, and exactly 0.0 on a cold one. That is what
            // produced the spurious "Schwarz 512^2 residual 0.00e+00" in the first sweep.
            // Stepping, syncing, then stepping again means the second readback observes the
            // first step's completed result.
            s.Step(pc(_warmup + _reps), _sweeps, true);
            rd.Submit();
            rd.Sync();
            s.Step(pc(_warmup + _reps + 1), _sweeps, true);
            rd.Submit();
            rd.Sync();

            float l2 = s.LastResidual;
            double rms = l2 / Math.Sqrt(cells);

            GD.Print($"BENCH2,{s.ModeName},{gridLabel},{_sweeps},{s.PassesPerStep(_sweeps)}," +
                     $"{msPerStep:0.0000},{l2:0.000000e+00},{rms:0.000000e+00}");
        }
        catch (Exception e)
        {
            GD.Print($"BENCH2,{tag},{gridLabel},{_sweeps},-,FAILED_RUN,-,-");
            GD.PrintErr($"[SolverBench] {tag} @ {gridLabel}: {e.Message}");
        }
        finally
        {
            s.Free();
        }
    }

    private static IStampSolver? Make(RenderingDevice rd, int mode, Vector2I grid) => mode switch
    {
        0 => new GpuStampSolver(rd, grid, StampPath, GpuStampSolver.Mode.Jacobi),
        1 => new GpuStampSolver(rd, grid, StampPath, GpuStampSolver.Mode.Rbgs),
        2 => new AdiStampSolver(rd, grid, StampPath),
        3 => new GpuStampSolver(rd, grid, StampPath, GpuStampSolver.Mode.Cg),
        4 => new MgvSolver(rd, grid, StampPath),
        5 => new MgDeepSolver(rd, grid, StampPath),
        6 => new SchwarzSolver(rd, grid, StampPath),
        7 => new SpectralSolver(rd, grid, StampPath, SpectralSolver.Basis.Cosine),
        8 => new SpectralSolver(rd, grid, StampPath, SpectralSolver.Basis.Sine),
        _ => null,
    };

    // The 3D column. Mode numbers deliberately match the 2D ones for Jacobi/RBGS/CG. The
    // multigrid/ADI/Schwarz/spectral hosts have no 3D form — they are Vector2I by construction
    // (restriction stencils, tridiagonal sweeps and DCT axes are all written for a plane).
    private static IStampSolver? Make3D(RenderingDevice rd, int mode, Vector3I grid) => mode switch
    {
        0 => new GpuStampSolver3D(rd, grid, Stamp3DPath, GpuStampSolver3D.Mode.Jacobi),
        1 => new GpuStampSolver3D(rd, grid, Stamp3DPath, GpuStampSolver3D.Mode.Rbgs),
        3 => new GpuStampSolver3D(rd, grid, Stamp3DPath, GpuStampSolver3D.Mode.Cg),
        // 5 keeps MgDeep's 2D id. 9/10 are 3D-only: the same solver with the two competing
        // per-level beta rules, so the bench decides which coarsening is right instead of the
        // comment above BetaCoarsenExp asserting it.
        5 => new MgDeepSolver3D(rd, grid, Stamp3DPath),
        9 => new MgDeepSolver3D(rd, grid, Stamp3DPath, 2),
        10 => new MgDeepSolver3D(rd, grid, Stamp3DPath, 3),
        _ => null,
    };

    // The bench defines its OWN problem rather than borrowing scene 25's UI state, so a run is
    // reproducible from the command line alone. Uniform depth (extra.z = 0) and no sponge
    // (sponge_a = 0) on purpose: those are the conditions under which every solver — including
    // both spectral bases — is inverting the SAME matrix, which is the only way a cross-solver
    // comparison means anything (solver-ledger.md §8b).
    //
    // beta scales as N^2 because beta carries 1/dx^2 and the tank's physical size is fixed.
    // Holding it constant instead would quietly delete the effect the resolution sweep exists
    // to measure.
    private static byte[] Pc(int n, int step)
    {
        float beta = BetaAt256 * (n / 256f) * (n / 256f);
        float phaseOld = step * 0.21f;
        float phaseNew = (step + 1) * 0.21f;

        float[] v =
        {
            n, n, beta, 0.002f,          // size.xy, beta_scale, a (damping)
            0.0f, 1.0f, 0.0f, 0.0f,      // leak, cn (Crank-Nicolson on), sponge_w, sponge_a
            -1.0f, 0.0f, 0.0f, 1.0f,     // floor_base, slope (flat bed), water_level, min_depth
            -0.9f, -0.9f, 0.12f, 0.35f,  // paddle: x_old, x_new, reach, gain
            0.10f, 2.0f, phaseOld, phaseNew,   // macro: amp, lobes, phase_old, phase_new
            0.0f, 0.0f, 0.0f, 0.0f,      // micro: off
            0.0f, 0.0f, 0.0f, 0.0f,      // drop: off
            0.0f, 0.0f, 0.0f, 1.0f,      // extra: segs, edge-mask|dcmode, bathymetry=0, ref depth=1
        };
        var b = new byte[128];
        Buffer.BlockCopy(v, 0, b, 0, 128);
        return b;
    }

    // stamp_blob_3d's layout: vec4 size (xyz = dims) then 10 contiguous scalars, host-padded
    // to 64 B. Uniform conductance (pc.beta) and a uniform diagonal make this a
    // constant-coefficient operator by construction, so both modes invert the SAME matrix —
    // the condition the 2D sweep has to arrange by hand (uniform depth, no sponge).
    //
    // leak = 0 and a moving source: leak would screen the operator and flatter every solver,
    // and a STATIC rhs would let the field converge once and then report the residual of a
    // problem already solved. Moving the source each step keeps the solve honest, exactly as
    // the 2D paddle phase does.
    private static byte[] Pc3D(int n, int step)
    {
        float beta = BetaAt64 * (n / 64f) * (n / 64f);
        float phase = step * 0.21f;
        float half = n * 0.5f;

        float[] v =
        {
            n, n, n, 0f,                    // vec4 size
            beta, 0.002f, 0.0f,             // beta, a (damping), leak
            n * 0.12f, 0.35f,               // src_r, src_w
            half + Mathf.Cos(phase) * n * 0.2f,   // poke x
            half + Mathf.Sin(phase) * n * 0.2f,   // poke y
            half,                                  // poke z
            n * 0.08f, 0.10f,               // poke_r, poke_w
            0f, 0f,
        };
        var b = new byte[64];
        Buffer.BlockCopy(v, 0, b, 0, 64);
        return b;
    }

    private static int[] ParseInts(string csv)
    {
        string[] parts = csv.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var outp = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++) { outp[i] = parts[i].ToInt(); }
        return outp;
    }
}
