using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Stam-style stable-fluids solver (GPU, matrix-free) — a sibling to GpuStampSolver.
// Phase 3c is where the single-scalar stamp solver stops dropping in: a fluid needs a
// velocity field, advection, and a projection, not one A x = b. So this is a purpose-
// built pipeline — BUT the pressure-projection step is the very same matrix-free Jacobi
// relaxation the stamp solvers use, here solving ∇²p = div. Per tick:
//   add force+dye (+buoyancy) → advect velocity (semi-Lagrangian) → divergence →
//   K Jacobi pressure iters (warm-started) → subtract ∇p (divergence-free) → advect dye.
// All persistent state lives in the "A" textures; "B"/div/pressure are scratch. Call
// every method from inside RenderingServer.CallOnRenderThread.
public sealed class FluidSim
{
    private const string Dir = "res://shaders/fluid/";

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _gx, _gy;

    private Rid _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD;
    private Rid _pAdd, _pAdvV, _pDiv, _pJac, _pGrad, _pAdvD;
    private Rid _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div;

    private Rid _add0, _add1;
    private Rid _advV0, _advV1;
    private Rid _div0, _div1;
    private Rid _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2;
    private Rid _grad0, _grad1;
    private Rid _advD0, _advD1, _advD2;

    // MacCormack (optional): second-order dye advection — pass 1 is the standard
    // semi-Lagrangian, pass 2 corrects with the reverse round-trip error (limited).
    private Rid _sMc, _pMc, _dyeC;
    private Rid _mc0, _mc1, _mc2, _mc3;
    private bool _mcReady;

    // Red-black Gauss-Seidel + SOR pressure path (optional): scene 251 bounce's solver,
    // and ~2× Jacobi's convergence per sweep at ω = 1. PARITY is compiled in, so this is
    // two shaders, not one with a branch — see fs_pressure_rbgs.glslinc.
    private Rid _sSorR, _sSorB, _pSorR, _pSorB;
    private Rid _sorR0, _sorR1, _sorR2, _sorB0, _sorB1, _sorB2;
    private bool _sorReady;

    // Warm-start scale p₀ ← μ·p_prev (optional): scene 253 memory's whole mechanism.
    private Rid _sWarm, _pWarm, _warm0;
    private bool _warmReady;

    // Spectral pressure path (optional): scene 257 eq. Separable cosine transform in, one
    // divide by the Poisson eigenvalue, shaped by W(k), transform out. No iteration at all,
    // so there is no truncation to be honest or dishonest about — see fs_dct_1d.
    // Crossfade (optional): scene 252. Holds the first solve in _pC so the second can be
    // blended onto it. Works across ANY pair of the paths above.
    private Rid _sBlend, _pBlend, _pC, _blend0, _blend1;
    private bool _blendReady;

    // Two-phase / variable-density path (optional): the shore-slice series. The dye field
    // is reinterpreted as ρ, and the projection becomes a WEIGHTED Laplacian — same stamp
    // contract, conductance 1/ρ_face instead of a flat 1.
    private Rid _sRhoR, _sRhoB, _pRhoR, _pRhoB, _sGradRho, _pGradRho, _sTwoAdd, _pTwoAdd;
    private Rid _sDivSolid, _pDivSolid, _divS0, _divS1;
    private Rid _sSharp, _pSharp, _sharp0;
    private Rid _rhoR0, _rhoR1, _rhoR2, _rhoR3, _rhoB0, _rhoB1, _rhoB2, _rhoB3;
    private Rid _gradRho0, _gradRho1, _gradRho2, _twoAdd0, _twoAdd1, _twoAdd2;
    private bool _twoPhaseReady;

    // Prescribed solid-body rotation (optional): scene 260's advection-only harness.
    private Rid _sRot, _pRot, _rot0;
    private bool _rotReady;

    // ADI line-relaxation path (optional): scene 255. Two pipelines (AXIS 0/1), a Thomas
    // c' scratch texture, and a ONE-DIMENSIONAL dispatch — one thread per line, not per cell.
    private Rid _sAdiX, _sAdiY, _pAdiX, _pAdiY, _cScratch;
    private Rid _adiX0, _adiX1, _adiX2, _adiX3;        // x half-sweep: pA → pB
    private Rid _adiY1, _adiYb0, _adiYb2, _adiYb3;     // y half-sweep: pB → pA
    private bool _adiReady;

    private Rid _sDctFx, _sDctFy, _sDctIy, _sDctIx, _sSpec;
    private Rid _pDctFx, _pDctFy, _pDctIy, _pDctIx, _pSpec;
    private Rid _specA, _specB;
    private Rid _dctFx0, _dctFx1, _dctFy0, _dctFy1, _dctIy0, _dctIy1, _dctIx0, _dctIx1, _spec0;
    private bool _spectralReady;

    // Viscosity (optional): implicit Jacobi diffusion of velocity, ping-ponged with
    // the pre-diffusion field held fixed in _velC.
    private Rid _sVisc, _pVisc, _velC;
    private Rid _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2;
    private bool _viscReady;

    public bool Ready { get; private set; }
    public Rid DyeRid => _dyeA;
    public Rid VelRid => _velA;

    // The divergence texture — scene 250's artifact view (docs/artifacts-250.md).
    // NOTE what this holds depends on MeasureResidual: normally it is the divergence of
    // the PRE-projection velocity (the solve's right-hand side). Set MeasureResidual to
    // re-run the divergence pass after the gradient subtract so it holds the true
    // post-projection RESIDUAL — the compressibility truncated Jacobi left behind.
    public Rid DivRid => _div;

    // Opt-in extra divergence dispatch at the end of the projection (see DivRid).
    // Off by default: scenes 07/18 keep the original pipeline, byte-identical.
    public bool MeasureResidual { get; set; }

    /// <summary>
    /// Solve the pressure with red-black Gauss-Seidel + SOR instead of Jacobi (needs
    /// ctor sor: true). ω rides in the sim push constant's third float. Both solvers stay
    /// reachable forever — that A/B is the point of scene 251.
    /// </summary>
    public bool UseSor { get; set; }

    public bool SorReady => _sorReady;

    /// <summary>
    /// Scale the warm-started pressure by μ (sim push constant's 4th float) before each
    /// solve — scene 253. Needs ctor warm: true. Off = the standard warm start, untouched.
    /// </summary>
    public bool UseWarmScale { get; set; }

    public bool WarmReady => _warmReady;

    /// <summary>
    /// Solve the pressure spectrally (needs ctor spectral: true) — scene 257. Replaces the
    /// whole iteration loop, so `iters` is ignored while this is on. Requires a specPc.
    /// </summary>
    public bool UseSpectral { get; set; }

    public bool SpectralReady => _spectralReady;

    /// <summary>
    /// Run the truncated solve AND the spectral solve each tick and blend them (needs ctor
    /// crossfade + spectral). α = 1 is the exact solve, 0 the truncated one — scene 252.
    /// Takes precedence over UseSpectral alone.
    /// </summary>
    public bool UseCrossfade { get; set; }

    public bool CrossfadeReady => _blendReady && _spectralReady;

    /// <summary>ADI alternating-line relaxation instead of Jacobi (ctor adi: true) — scene 255.</summary>
    public bool UseAdi { get; set; }

    public bool AdiReady => _adiReady;

    public bool RotationReady => _rotReady;

    public bool TwoPhaseReady => _twoPhaseReady;

    /// <summary>
    /// Pull a field back to the CPU. Slow (a full stall) and meant for DIAGNOSIS ONLY —
    /// call it at ~1 Hz from a debug readout, never per frame. Exists because two-phase
    /// went wrong twice in ways that looked identical on screen and were not.
    /// </summary>
    public float[] ReadField(Rid tex, int components = 1)
    {
        var bytes = _rd.TextureGetData(tex, 0);
        var outv = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, outv, 0, bytes.Length);
        return outv;
    }

    public Rid PressureRid => _pA;

    /// <summary>
    /// Treat the domain border as SOLID (v·n = 0) inside the projection rather than
    /// zero-gradient. Correct — without it uniform gravity yields a divergence-free field,
    /// the solve returns p = const and nothing balances gravity — but not sufficient on its
    /// own, so it stays selectable. See fs_divergence_solid.
    /// </summary>
    public bool UseSolidDivergence { get; set; }

    /// <summary>
    /// One two-phase tick: uniform gravity → advect velocity → variable-density projection
    /// → ρ-weighted gradient subtract → advect ρ. Deliberately its own method rather than a
    /// flag on Step: the operator pair (fs_pressure_rho + fs_gradient_rho) must ALWAYS be
    /// used together — projecting with one and subtracting with the other leaves a field
    /// that is not divergence-free while every individual solve looks converged.
    /// </summary>
    public void StepTwoPhase(byte[] addPc, byte[] advVPc, byte[] rhoPc, byte[] advDPc,
        int iters, bool macCormack, byte[]? mcPc, byte[]? sharpenPc = null)
    {
        if (!Ready || !_twoPhaseReady) { return; }
        var size = new Vector3(_grid.X, _grid.Y, 1);

        RunOne(_pTwoAdd, addPc, (_twoAdd0, 0u), (_twoAdd1, 1u), (_twoAdd2, 2u));
        RunOne(_pAdvV, advVPc, (_advV0, 0u), (_advV1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        long cl = _rd.ComputeListBegin();
        // SOLID-wall divergence, not the clamped one — see fs_divergence_solid. Without it
        // the projection cannot produce hydrostatic pressure at all.
        if (UseSolidDivergence) { Bind(cl, _pDivSolid, rhoPc, (_divS0, 0u), (_divS1, 1u)); }
        else { Bind(cl, _pDiv, rhoPc, (_div0, 0u), (_div1, 1u)); }
        _rd.ComputeListAddBarrier(cl);
        for (int k = 0; k < iters; k++)
        {
            Bind(cl, _pRhoR, rhoPc, (_rhoR0, 0u), (_rhoR1, 1u), (_rhoR2, 2u), (_rhoR3, 3u));
            _rd.ComputeListAddBarrier(cl);
            Bind(cl, _pRhoB, rhoPc, (_rhoB0, 0u), (_rhoB1, 1u), (_rhoB2, 2u), (_rhoB3, 3u));
            _rd.ComputeListAddBarrier(cl);
        }
        Bind(cl, _pGradRho, rhoPc, (_gradRho0, 0u), (_gradRho1, 1u), (_gradRho2, 2u));
        _rd.ComputeListAddBarrier(cl);
        if (MeasureResidual)
        {
            if (UseSolidDivergence) { Bind(cl, _pDivSolid, rhoPc, (_divS0, 0u), (_divS1, 1u)); }
            else { Bind(cl, _pDiv, rhoPc, (_div0, 0u), (_div1, 1u)); }
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (_mcReady && macCormack && mcPc != null)
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            RunOne(_pMc, mcPc, (_mc0, 0u), (_mc1, 1u), (_mc2, 2u), (_mc3, 3u));
            _rd.TextureCopy(_dyeC, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
        else
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Advection-only tick for scene 260: impose a rigid rotation on the velocity field and
    /// advect the dye through it. No forces, no projection — solid-body rotation is exactly
    /// divergence-free, so there is nothing for a pressure solve to remove, and leaving it
    /// out is what makes the advection scheme the ONLY thing acting on the field.
    /// </summary>
    public void StepAdvectOnly(byte[] rotPc, byte[] advDPc, bool macCormack, byte[]? mcPc)
    {
        if (!Ready || !_rotReady) { return; }
        var size = new Vector3(_grid.X, _grid.Y, 1);
        RunOne(_pRot, rotPc, (_rot0, 0u));
        if (_mcReady && macCormack && mcPc != null)
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            RunOne(_pMc, mcPc, (_mc0, 0u), (_mc1, 1u), (_mc2, 2u), (_mc3, 3u));
            _rd.TextureCopy(_dyeC, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
        else
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
    }

    /// <summary>Stamp a dye blob directly (scene 260 lays its test pattern down once).</summary>
    public void Seed(byte[] addPc) { if (Ready) { RunOne(_pAdd, addPc, (_add0, 0u), (_add1, 1u)); } }

    // addKernel swaps the source stage for this instance (its push constants are
    // opaque to Step) — e.g. scene 18's fs_add_milk. extras compiles the MacCormack
    // + viscosity stages; default false = the original pipeline, byte-identical.
    public FluidSim(RenderingDevice rd, Vector2I grid, string addKernel = "fs_add_source",
        bool extras = false, bool sor = false, bool warm = false, bool spectral = false,
        bool crossfade = false, bool adi = false, bool rotation = false, bool twoPhase = false)
    {
        _rd = rd;
        _grid = grid;
        _gx = (uint)((grid.X - 1) / 8 + 1);
        _gy = (uint)((grid.Y - 1) / 8 + 1);

        _sAdd = Compile(addKernel);
        _sAdvV = Compile("fs_advect_vel");
        _sDiv = Compile("fs_divergence");
        _sJac = Compile("fs_pressure_jacobi");
        _sGrad = Compile("fs_gradient_sub");
        _sAdvD = Compile("fs_advect_dye");
        if (!(_sAdd.IsValid && _sAdvV.IsValid && _sDiv.IsValid && _sJac.IsValid && _sGrad.IsValid && _sAdvD.IsValid))
        {
            return;
        }
        _pAdd = _rd.ComputePipelineCreate(_sAdd);
        _pAdvV = _rd.ComputePipelineCreate(_sAdvV);
        _pDiv = _rd.ComputePipelineCreate(_sDiv);
        _pJac = _rd.ComputePipelineCreate(_sJac);
        _pGrad = _rd.ComputePipelineCreate(_sGrad);
        _pAdvD = _rd.ComputePipelineCreate(_sAdvD);

        var rg = Fmt(RenderingDevice.DataFormat.R32G32Sfloat);
        var r = Fmt(RenderingDevice.DataFormat.R32Sfloat);
        _velA = MakeTex(rg); _velB = MakeTex(rg);
        _dyeA = MakeTex(r); _dyeB = MakeTex(r);
        _pA = MakeTex(r); _pB = MakeTex(r); _div = MakeTex(r);

        _add0 = Set(_velA, _sAdd, 0); _add1 = Set(_dyeA, _sAdd, 1);
        _advV0 = Set(_velA, _sAdvV, 0); _advV1 = Set(_velB, _sAdvV, 1);
        _div0 = Set(_velA, _sDiv, 0); _div1 = Set(_div, _sDiv, 1);
        _jacAB0 = Set(_pA, _sJac, 0); _jacAB1 = Set(_div, _sJac, 1); _jacAB2 = Set(_pB, _sJac, 2);
        _jacBA0 = Set(_pB, _sJac, 0); _jacBA1 = Set(_div, _sJac, 1); _jacBA2 = Set(_pA, _sJac, 2);
        _grad0 = Set(_velA, _sGrad, 0); _grad1 = Set(_pA, _sGrad, 1);
        _advD0 = Set(_dyeA, _sAdvD, 0); _advD1 = Set(_velA, _sAdvD, 1); _advD2 = Set(_dyeB, _sAdvD, 2);

        if (sor)
        {
            // Two pipelines, PARITY baked in. Red reads pA → writes pB; black then reads pB
            // (where red is already fresh) → writes pA, so a full sweep lands back in _pA
            // exactly where gradient-subtract and next frame's warm start expect it.
            _sSorR = Compile("fs_pressure_rbgs", "#define PARITY 0");
            _sSorB = Compile("fs_pressure_rbgs", "#define PARITY 1");
            if (_sSorR.IsValid && _sSorB.IsValid)
            {
                _pSorR = _rd.ComputePipelineCreate(_sSorR);
                _pSorB = _rd.ComputePipelineCreate(_sSorB);
                _sorR0 = Set(_pA, _sSorR, 0); _sorR1 = Set(_div, _sSorR, 1); _sorR2 = Set(_pB, _sSorR, 2);
                _sorB0 = Set(_pB, _sSorB, 0); _sorB1 = Set(_div, _sSorB, 1); _sorB2 = Set(_pA, _sSorB, 2);
                _sorReady = true;
            }
        }

        if (warm)
        {
            _sWarm = Compile("fs_pressure_warm");
            if (_sWarm.IsValid)
            {
                _pWarm = _rd.ComputePipelineCreate(_sWarm);
                _warm0 = Set(_pA, _sWarm, 0);   // in place on the warm-started iterate
                _warmReady = true;
            }
        }

        if (spectral)
        {
            // Four transform pipelines from one source. The chain is
            //   _div --Fx--> specA --Fy--> specB --filter--> specB --Iy--> specA --Ix--> _pA
            // landing the pressure exactly where gradient-subtract already reads it.
            _sDctFx = Compile("fs_dct_1d", "#define AXIS 0\n#define INVERSE 0");
            _sDctFy = Compile("fs_dct_1d", "#define AXIS 1\n#define INVERSE 0");
            _sDctIy = Compile("fs_dct_1d", "#define AXIS 1\n#define INVERSE 1");
            _sDctIx = Compile("fs_dct_1d", "#define AXIS 0\n#define INVERSE 1");
            _sSpec = Compile("fs_spectral_project");
            if (_sDctFx.IsValid && _sDctFy.IsValid && _sDctIy.IsValid && _sDctIx.IsValid && _sSpec.IsValid)
            {
                _pDctFx = _rd.ComputePipelineCreate(_sDctFx);
                _pDctFy = _rd.ComputePipelineCreate(_sDctFy);
                _pDctIy = _rd.ComputePipelineCreate(_sDctIy);
                _pDctIx = _rd.ComputePipelineCreate(_sDctIx);
                _pSpec = _rd.ComputePipelineCreate(_sSpec);
                _specA = MakeTex(r); _specB = MakeTex(r);
                _dctFx0 = Set(_div, _sDctFx, 0); _dctFx1 = Set(_specA, _sDctFx, 1);
                _dctFy0 = Set(_specA, _sDctFy, 0); _dctFy1 = Set(_specB, _sDctFy, 1);
                _spec0 = Set(_specB, _sSpec, 0);
                _dctIy0 = Set(_specB, _sDctIy, 0); _dctIy1 = Set(_specA, _sDctIy, 1);
                _dctIx0 = Set(_specA, _sDctIx, 0); _dctIx1 = Set(_pA, _sDctIx, 1);
                _spectralReady = true;
            }
        }

        if (twoPhase)
        {
            _sRhoR = Compile("fs_pressure_rho", "#define PARITY 0");
            _sRhoB = Compile("fs_pressure_rho", "#define PARITY 1");
            _sGradRho = Compile("fs_gradient_rho");
            _sTwoAdd = Compile("fs_two_phase_add");
            _sDivSolid = Compile("fs_divergence_solid");
            _sSharp = Compile("fs_rho_sharpen");
            if (_sRhoR.IsValid && _sRhoB.IsValid && _sGradRho.IsValid && _sTwoAdd.IsValid
                && _sDivSolid.IsValid)
            {
                _pDivSolid = _rd.ComputePipelineCreate(_sDivSolid);
                if (_sSharp.IsValid) { _pSharp = _rd.ComputePipelineCreate(_sSharp); _sharp0 = Set(_dyeA, _sSharp, 0); }
                _divS0 = Set(_velA, _sDivSolid, 0); _divS1 = Set(_div, _sDivSolid, 1);
                _pRhoR = _rd.ComputePipelineCreate(_sRhoR);
                _pRhoB = _rd.ComputePipelineCreate(_sRhoB);
                _pGradRho = _rd.ComputePipelineCreate(_sGradRho);
                _pTwoAdd = _rd.ComputePipelineCreate(_sTwoAdd);
                // _dyeA IS ρ in this mode — the dye field reinterpreted, not a new texture.
                _rhoR0 = Set(_pA, _sRhoR, 0); _rhoR1 = Set(_div, _sRhoR, 1);
                _rhoR2 = Set(_pB, _sRhoR, 2); _rhoR3 = Set(_dyeA, _sRhoR, 3);
                _rhoB0 = Set(_pB, _sRhoB, 0); _rhoB1 = Set(_div, _sRhoB, 1);
                _rhoB2 = Set(_pA, _sRhoB, 2); _rhoB3 = Set(_dyeA, _sRhoB, 3);
                _gradRho0 = Set(_velA, _sGradRho, 0); _gradRho1 = Set(_pA, _sGradRho, 1);
                _gradRho2 = Set(_dyeA, _sGradRho, 2);
                _twoAdd0 = Set(_velA, _sTwoAdd, 0); _twoAdd1 = Set(_dyeA, _sTwoAdd, 1);
                _twoAdd2 = Set(_pA, _sTwoAdd, 2);   // the seed writes hydrostatic p directly
                _twoPhaseReady = true;
            }
        }

        if (rotation)
        {
            _sRot = Compile("fs_solid_rotation");
            if (_sRot.IsValid)
            {
                _pRot = _rd.ComputePipelineCreate(_sRot);
                _rot0 = Set(_velA, _sRot, 0);
                _rotReady = true;
            }
        }

        if (adi)
        {
            _sAdiX = Compile("fs_pressure_adi", "#define AXIS 0");
            _sAdiY = Compile("fs_pressure_adi", "#define AXIS 1");
            if (_sAdiX.IsValid && _sAdiY.IsValid)
            {
                _pAdiX = _rd.ComputePipelineCreate(_sAdiX);
                _pAdiY = _rd.ComputePipelineCreate(_sAdiY);
                _cScratch = MakeTex(r);
                // A→B and B→A bindings for each axis, so half-sweeps ping-pong.
                // x sweeps pA → pB, y sweeps pB → pA, so ONE alternation is a full round
                // trip and any count of them lands back in _pA — where gradient-subtract
                // and next frame's warm start expect it.
                _adiX0 = Set(_pA, _sAdiX, 0); _adiX1 = Set(_div, _sAdiX, 1);
                _adiX2 = Set(_cScratch, _sAdiX, 2); _adiX3 = Set(_pB, _sAdiX, 3);
                _adiY1 = Set(_div, _sAdiY, 1);
                _adiYb0 = Set(_pB, _sAdiY, 0); _adiYb2 = Set(_cScratch, _sAdiY, 2); _adiYb3 = Set(_pA, _sAdiY, 3);
                _adiReady = true;
            }
        }

        if (crossfade)
        {
            _sBlend = Compile("fs_pressure_blend");
            if (_sBlend.IsValid)
            {
                _pBlend = _rd.ComputePipelineCreate(_sBlend);
                _pC = MakeTex(r);
                _blend0 = Set(_pA, _sBlend, 0); _blend1 = Set(_pC, _sBlend, 1);
                _blendReady = true;
            }
        }

        if (extras)
        {
            _sMc = Compile("fs_maccormack");
            _sVisc = Compile("fs_diffuse_vel");
            if (_sMc.IsValid)
            {
                _pMc = _rd.ComputePipelineCreate(_sMc);
                _dyeC = MakeTex(r);
                _mc0 = Set(_dyeA, _sMc, 0);   // φⁿ
                _mc1 = Set(_dyeB, _sMc, 1);   // forward result
                _mc2 = Set(_velA, _sMc, 2);
                _mc3 = Set(_dyeC, _sMc, 3);
                _mcReady = true;
            }
            if (_sVisc.IsValid)
            {
                _pVisc = _rd.ComputePipelineCreate(_sVisc);
                _velC = MakeTex(rg);
                _visc0 = Set(_velC, _sVisc, 0);                          // v₀ (fixed)
                _viscAB1 = Set(_velA, _sVisc, 1); _viscAB2 = Set(_velB, _sVisc, 2);
                _viscBA1 = Set(_velB, _sVisc, 1); _viscBA2 = Set(_velA, _sVisc, 2);
                _viscReady = true;
            }
        }

        Ready = true;
    }

    // iters is forced even so the ping-ponged pressure ends back in _pA (which gradient
    // subtract reads and which warm-starts next frame). viscIters/viscPc and macCormack
    // engage the optional stages (ctor extras: true); defaults = the original pipeline.
    public void Step(byte[] addPc, byte[] advVPc, byte[] simPc, byte[] advDPc, int iters,
        int viscIters = 0, byte[]? viscPc = null, bool macCormack = false, byte[]? mcPc = null,
        byte[]? specPc = null, byte[]? blendPc = null)
    {
        if (!Ready) { return; }
        if ((iters & 1) == 1) { iters++; }
        var size = new Vector3(_grid.X, _grid.Y, 1);

        // 1. inject force + dye + buoyancy (in place on velA/dyeA)
        RunOne(_pAdd, addPc, (_add0, 0u), (_add1, 1u));

        // 2. self-advect velocity velA -> velB, then copy back so velA stays "current"
        RunOne(_pAdvV, advVPc, (_advV0, 0u), (_advV1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        // 2b. (optional) implicit viscosity: v₀ → _velC, Jacobi ping-pong, ends in velA
        if (_viscReady && viscIters > 0 && viscPc != null)
        {
            if ((viscIters & 1) == 1) { viscIters++; }
            _rd.TextureCopy(_velA, _velC, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
            long vcl = _rd.ComputeListBegin();
            for (int k = 0; k < viscIters; k++)
            {
                if (k % 2 == 0) { Bind(vcl, _pVisc, viscPc, (_visc0, 0u), (_viscAB1, 1u), (_viscAB2, 2u)); }
                else { Bind(vcl, _pVisc, viscPc, (_visc0, 0u), (_viscBA1, 1u), (_viscBA2, 2u)); }
                _rd.ComputeListAddBarrier(vcl);
            }
            _rd.ComputeListEnd();
        }

        // 3a. (optional) CROSSFADE — scene 252. Two solves of the SAME divergence, blended.
        // Split into two lists because TextureCopy cannot sit inside one. Order matters:
        // the truncated solve runs FIRST so it still warm-starts from last frame's blended
        // pressure; the spectral solve is direct, so running it second cannot contaminate it.
        if (_blendReady && _spectralReady && UseCrossfade && specPc != null && blendPc != null)
        {
            long xl = _rd.ComputeListBegin();
            Bind(xl, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
            _rd.ComputeListAddBarrier(xl);
            int ji = iters;
            if ((ji & 1) == 1) { ji++; }
            for (int k = 0; k < ji; k++)
            {
                if (k % 2 == 0) { Bind(xl, _pJac, simPc, (_jacAB0, 0u), (_jacAB1, 1u), (_jacAB2, 2u)); }
                else { Bind(xl, _pJac, simPc, (_jacBA0, 0u), (_jacBA1, 1u), (_jacBA2, 2u)); }
                _rd.ComputeListAddBarrier(xl);
            }
            _rd.ComputeListEnd();

            _rd.TextureCopy(_pA, _pC, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

            long xl2 = _rd.ComputeListBegin();
            Bind(xl2, _pDctFx, simPc, (_dctFx0, 0u), (_dctFx1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pDctFy, simPc, (_dctFy0, 0u), (_dctFy1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pSpec, specPc, (_spec0, 0u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pDctIy, simPc, (_dctIy0, 0u), (_dctIy1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pDctIx, simPc, (_dctIx0, 0u), (_dctIx1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pBlend, blendPc, (_blend0, 0u), (_blend1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            Bind(xl2, _pGrad, simPc, (_grad0, 0u), (_grad1, 1u));
            _rd.ComputeListAddBarrier(xl2);
            if (MeasureResidual)
            {
                Bind(xl2, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
                _rd.ComputeListAddBarrier(xl2);
            }
            _rd.ComputeListEnd();
        }
        else
        {
        // 3. divergence -> K Jacobi pressure iters -> subtract gradient (one compute list)
        long cl = _rd.ComputeListBegin();
        Bind(cl, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
        _rd.ComputeListAddBarrier(cl);
        // p₀ ← μ·p_prev, applied to the warm-started iterate BEFORE any relaxation, so μ
        // scales what last frame remembered rather than what this frame just computed.
        if (_warmReady && UseWarmScale)
        {
            Bind(cl, _pWarm, simPc, (_warm0, 0u));
            _rd.ComputeListAddBarrier(cl);
        }
        if (_spectralReady && UseSpectral && specPc != null)
        {
            // No iteration: forward transform, one divide per cell, inverse transform. The
            // solve is EXACT (up to the shaping curve), so `iters` means nothing here.
            Bind(cl, _pDctFx, simPc, (_dctFx0, 0u), (_dctFx1, 1u));
            _rd.ComputeListAddBarrier(cl);
            Bind(cl, _pDctFy, simPc, (_dctFy0, 0u), (_dctFy1, 1u));
            _rd.ComputeListAddBarrier(cl);
            Bind(cl, _pSpec, specPc, (_spec0, 0u));
            _rd.ComputeListAddBarrier(cl);
            Bind(cl, _pDctIy, simPc, (_dctIy0, 0u), (_dctIy1, 1u));
            _rd.ComputeListAddBarrier(cl);
            Bind(cl, _pDctIx, simPc, (_dctIx0, 0u), (_dctIx1, 1u));
            _rd.ComputeListAddBarrier(cl);
        }
        else if (_adiReady && UseAdi)
        {
            // iters = alternations: an exact line solve along x, then along y. Each
            // alternation is a full A→B→A round trip, so any count ends in _pA.
            int alts = (iters < 1) ? 1 : iters;
            for (int k = 0; k < alts; k++)
            {
                BindLines(cl, _pAdiX, simPc, _grid.Y, (_adiX0, 0u), (_adiX1, 1u), (_adiX2, 2u), (_adiX3, 3u));
                _rd.ComputeListAddBarrier(cl);
                BindLines(cl, _pAdiY, simPc, _grid.X, (_adiYb0, 0u), (_adiY1, 1u), (_adiYb2, 2u), (_adiYb3, 3u));
                _rd.ComputeListAddBarrier(cl);
            }
        }
        else if (_sorReady && UseSor)
        {
            // iters = FULL sweeps here; each is red + black, so 2 dispatches. Half the
            // threads no-op per dispatch, so per-sweep cost ≈ Jacobi's — the convergence
            // gain is free. Always ends in _pA regardless of parity of the count.
            for (int k = 0; k < iters; k++)
            {
                Bind(cl, _pSorR, simPc, (_sorR0, 0u), (_sorR1, 1u), (_sorR2, 2u));
                _rd.ComputeListAddBarrier(cl);
                Bind(cl, _pSorB, simPc, (_sorB0, 0u), (_sorB1, 1u), (_sorB2, 2u));
                _rd.ComputeListAddBarrier(cl);
            }
        }
        else
        {
            for (int k = 0; k < iters; k++)
            {
                if (k % 2 == 0) { Bind(cl, _pJac, simPc, (_jacAB0, 0u), (_jacAB1, 1u), (_jacAB2, 2u)); }
                else { Bind(cl, _pJac, simPc, (_jacBA0, 0u), (_jacBA1, 1u), (_jacBA2, 2u)); }
                _rd.ComputeListAddBarrier(cl);
            }
        }
        Bind(cl, _pGrad, simPc, (_grad0, 0u), (_grad1, 1u));
        _rd.ComputeListAddBarrier(cl);
        // 3b. (optional) re-measure divergence of the NOW-projected velocity, overwriting
        // _div with the residual. Same pass, same sets — the solve is finished with _div,
        // so this is safe to clobber. One dispatch; only paid when the flag is on.
        if (MeasureResidual)
        {
            Bind(cl, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();
        }

        // 4. advect dye by the divergence-free velocity, copy back
        if (_mcReady && macCormack && mcPc != null)
        {
            // pass 1: standard SL forward (dissip 1 — real dissip applied in pass 2)
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            // pass 2: reverse round trip, limited half-error correction → dyeC → dyeA
            RunOne(_pMc, mcPc, (_mc0, 0u), (_mc1, 1u), (_mc2, 2u), (_mc3, 3u));
            _rd.TextureCopy(_dyeC, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
        else
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
    }

    private void RunOne(Rid pipe, byte[] pc, params (Rid set, uint idx)[] sets)
    {
        long cl = _rd.ComputeListBegin();
        Bind(cl, pipe, pc, sets);
        _rd.ComputeListEnd();
    }

    // One thread per LINE, not per cell — the ADI kernel's dispatch shape (local_size_x 64).
    private void BindLines(long cl, Rid pipe, byte[] pc, int lines, params (Rid set, uint idx)[] sets)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        foreach (var (set, idx) in sets) { _rd.ComputeListBindUniformSet(cl, set, idx); }
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, (uint)((lines - 1) / 64 + 1), 1, 1);
    }

    private void Bind(long cl, Rid pipe, byte[] pc, params (Rid set, uint idx)[] sets)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        foreach (var (set, idx) in sets) { _rd.ComputeListBindUniformSet(cl, set, idx); }
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in new[]
        {
            _add0, _add1, _advV0, _advV1, _div0, _div1,
            _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2,
            _grad0, _grad1, _advD0, _advD1, _advD2,
            _mc0, _mc1, _mc2, _mc3, _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2,
            _sorR0, _sorR1, _sorR2, _sorB0, _sorB1, _sorB2, _warm0,
            _dctFx0, _dctFx1, _dctFy0, _dctFy1, _dctIy0, _dctIy1, _dctIx0, _dctIx1, _spec0,
            _blend0, _blend1,
            _adiX0, _adiX1, _adiX2, _adiX3, _adiY1, _adiYb0, _adiYb2, _adiYb3, _rot0,
            _rhoR0, _rhoR1, _rhoR2, _rhoR3, _rhoB0, _rhoB1, _rhoB2, _rhoB3,
            _gradRho0, _gradRho1, _gradRho2, _twoAdd0, _twoAdd1, _twoAdd2, _divS0, _divS1, _sharp0,
        })
        {
            if (s.IsValid) { _rd.FreeRid(s); }
        }
        foreach (var t in new[] { _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div, _dyeC, _velC, _specA, _specB, _pC, _cScratch })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD, _sMc, _sVisc, _sSorR, _sSorB, _sWarm,
            _sDctFx, _sDctFy, _sDctIy, _sDctIx, _sSpec, _sBlend, _sAdiX, _sAdiY, _sRot,
            _sRhoR, _sRhoB, _sGradRho, _sTwoAdd, _sDivSolid, _sSharp })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    // define lets one source compile to several pipelines (PARITY for red-black), the same
    // compile-time-constant trick the stamp solvers use — no branch, no push-constant churn.
    private Rid Compile(string name, string? define = null)
    {
        string src = FileAccess.GetFileAsString(Dir + name + ".glslinc");
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[FluidSim] could not read {name}"); return default; }
        if (define != null) { src = src.Replace("#version 450", "#version 450\n" + define); }
        // Host-side #include. Godot's ShaderCompileSpirVFromSource has no includer, so the
        // text is spliced here — the same "assemble the shader in C#" approach the stamp
        // solvers use, and what lets the shore kernels share one boundary description
        // instead of three copies that can silently disagree.
        for (int guard = 0; guard < 8 && src.Contains("#include \""); guard++)
        {
            int a = src.IndexOf("#include \"", System.StringComparison.Ordinal);
            int b = src.IndexOf('"', a + 10);
            string inc = src.Substring(a + 10, b - a - 10);
            string body = FileAccess.GetFileAsString(Dir + inc);
            if (string.IsNullOrEmpty(body)) { GD.PushError($"[FluidSim] {name}: cannot include {inc}"); return default; }
            int lineEnd = src.IndexOf('\n', b);
            src = src.Substring(0, a) + body + src.Substring(lineEnd < 0 ? src.Length : lineEnd);
        }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[FluidSim] {name} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static RDTextureFormat Fmt(RenderingDevice.DataFormat format) => new()
    {
        Format = format,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = 0, Height = 0, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
            | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyFromBit
            | RenderingDevice.TextureUsageBits.CanCopyToBit,
    };

    private Rid MakeTex(RDTextureFormat tf)
    {
        tf.Width = (uint)_grid.X;
        tf.Height = (uint)_grid.Y;
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private Rid Set(Rid tex, Rid shader, int setIndex)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }
}
