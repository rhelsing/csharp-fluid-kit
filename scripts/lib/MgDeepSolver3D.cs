using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Deep geometric multigrid V-cycle over the real stamp operator, in a VOLUME.
//
// The 3D sibling of MgDeepSolver, in the same relationship GpuStampSolver3D has to
// GpuStampSolver: separate HOST, shared SHADERS. Every kernel in the cycle — mg_rhs,
// mgd_smooth, mgd_residual, mg_restrict, mg_prolong, cg_dotbuf — is the same .glslinc the 2D
// host compiles, against nd_3d instead of nd_2d (solver-ledger.md §7d).
//
// WHY MULTIGRID IS THE 3D PORT WORTH DOING. Coarsening removes 8x the cells per level here,
// not 4x, so the pyramid costs 1 + 1/8 + 1/64 + ... = 8/7 of its fine grid against 4/3 in 2D.
// Multigrid's overhead SHRINKS as dimension rises while its convergence advantage does not.
// Set against the measured 3D relaxation trend — RBGS holds its residual as the grid grows
// while Jacobi's nearly doubles, because beta scales as n^2 — every relaxation method is
// expected to fall off at 96^3/128^3, and this is the answer to that.
//
// THE ONE GENUINELY DIMENSION-DEPENDENT LINE is the per-level beta (see BetaCoarsenExp). It
// will converge to a plausible wrong answer rather than fail loudly, so it is a field, not a
// literal, and the bench settles it.
public sealed class MgDeepSolver3D : IStampSolver
{
    private const int Nu1 = 2, Nu2 = 2, NuCoarse = 8;
    private const int CoarsestSize = 4;

    // stamp_blob_3d push-constant offsets. NOT the 2D host's: that one reads stamp_wave_tank,
    // whose `vec2 size` puts beta at byte 8. A 3D stamp leads with `vec4 size` (std430 wants
    // the alignment), so beta sits at 16. Getting this wrong rewrites `leak` as beta and the
    // cycle diverges in a way that looks like a multigrid bug.
    private const int OffSize = 0, OffBeta = 16;

    /// <summary>
    /// Exponent on the per-level beta shrink: beta_L = beta_0 · (1/2^L)^exp.
    ///
    /// The 2D host hardcodes 2 (beta /= 4 per level). Two readings disagree about 3D:
    /// beta = dt²c²/dx² carries 1/dx² and dx doubles per level in ANY dimension, which argues
    /// 2; the Laplacian coarsens by 2^d, which argues 3. Left settable and benched rather than
    /// asserted — a wrong value here still converges, just to the wrong operator.
    /// </summary>
    public int BetaCoarsenExp { get; set; } = 3;

    // ---- external-state mode (a linear solve, not a time step) ------------------
    // Normally this class OWNS h_curr/h_prev and rotates them each Step, which is wave
    // time-stepping. Driven as a plain linear solver — pressure projection being the first
    // case — the caller already owns both textures: h_curr is the previous pressure (a warm
    // start, and the solution lands back in it) and h_prev is the divergence written by
    // someone else's kernel this frame. Two things change:
    //   * the textures are bound, not allocated, and NOT freed here;
    //   * NO copies happen at all. The caller reads the answer from SolutionRid.
    //
    // Skipping the copies is what makes a THREE-field problem expressible. Free-surface
    // pressure projection needs a mask, a divergence and a pressure, but the stamp contract
    // only hands the stamp two images (sets 0/1). Leaving the solution in _x[0] frees both
    // slots for inputs — set 0 = the fluid mask, set 1 = the divergence — and _x[0] surviving
    // between Steps means the previous pressure IS the warm start, for free and with no copy.
    private readonly bool _externalState;

    /// <summary>
    /// Where the answer is. In owned-state mode this is h_curr (copied, as a time step);
    /// in external-state mode it is the fine-level iterate itself, which the caller reads
    /// directly and which warm-starts the next solve.
    /// </summary>
    public Rid SolutionRid => _externalState ? _x[0] : _hCurr;

    private readonly RenderingDevice _rd;
    private readonly Vector3I[] _lvl;
    private readonly uint[] _gx, _gy, _gz;
    private readonly int _n;

    private Rid _shRhs, _shSm, _shRe, _shRs, _shPr, _shDot;
    private Rid _pRhs, _pSm, _pRe, _pRs, _pPr, _pDot;

    private Rid _hCurr, _hPrev, _scalars;
    private readonly Rid[] _x, _xt, _b, _r;

    private Rid _rhsH0, _rhsH1, _rhsB3;
    private Rid _smH0, _smH1, _reH0, _reH1;
    private readonly Rid[] _smX2, _smXt3, _smXt2, _smX3, _smB4;
    private readonly Rid[] _reX2, _reR3, _reB4;
    private readonly Rid[] _rsR2, _rsB3;     // level L -> L+1
    private readonly Rid[] _prC2, _prF3;     // level L+1 -> L
    private Rid _dotR2, _dotR3, _dotSc5;

    public bool Ready { get; private set; }
    public string ModeName => "MultigridDeep3D";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }
    public int Levels => _n;

    /// <summary>
    /// maxLevels caps the pyramid. 1 = fine grid only, i.e. plain smoothing with no
    /// coarse-grid correction at all. That is the control that isolates the coarse levels:
    /// the stamp reads its geometry from bound IMAGES, and this class binds ONE set of stamp
    /// images for every level, so a SAMPLED field (a fluid mask) is read at fine resolution
    /// with coarse coordinates on every level below 0. §7a predicted exactly this — the
    /// no-Galerkin shortcut holds only while the stamp's geometry is analytic in pc.size.
    /// </summary>
    public MgDeepSolver3D(RenderingDevice rd, Vector3I grid, string stampPath, int betaCoarsenExp = 3,
        Rid stateCurr = default, Rid statePrev = default, int maxLevels = int.MaxValue)
    {
        _rd = rd;
        BetaCoarsenExp = betaCoarsenExp;
        _externalState = stateCurr.IsValid && statePrev.IsValid;

        // pyramid: halve until any axis would drop below CoarsestSize or go odd (2^d
        // restriction assumes even dimensions; an odd level silently drops a slab)
        var levels = new System.Collections.Generic.List<Vector3I> { grid };
        var s = grid;
        while (levels.Count < maxLevels
               && s.X / 2 >= CoarsestSize && s.Y / 2 >= CoarsestSize && s.Z / 2 >= CoarsestSize
               && (s.X & 1) == 0 && (s.Y & 1) == 0 && (s.Z & 1) == 0)
        {
            s = new Vector3I(s.X / 2, s.Y / 2, s.Z / 2);
            levels.Add(s);
        }
        _n = levels.Count;
        _lvl = new Vector3I[_n];
        _gx = new uint[_n]; _gy = new uint[_n]; _gz = new uint[_n];
        for (int i = 0; i < _n; i++)
        {
            _lvl[i] = levels[i];
            _gx[i] = (uint)((_lvl[i].X - 1) / 4 + 1);
            _gy[i] = (uint)((_lvl[i].Y - 1) / 4 + 1);
            _gz[i] = (uint)((_lvl[i].Z - 1) / 4 + 1);
        }

        _x = new Rid[_n]; _xt = new Rid[_n]; _b = new Rid[_n]; _r = new Rid[_n];
        _smX2 = new Rid[_n]; _smXt3 = new Rid[_n]; _smXt2 = new Rid[_n]; _smX3 = new Rid[_n]; _smB4 = new Rid[_n];
        _reX2 = new Rid[_n]; _reR3 = new Rid[_n]; _reB4 = new Rid[_n];
        _rsR2 = new Rid[_n]; _rsB3 = new Rid[_n];
        _prC2 = new Rid[_n]; _prF3 = new Rid[_n];

        string nd = ReadRes(GpuStampSolver.Nd3DPath);
        string stamp = nd + "\n" + ReadRes(stampPath);
        const string wg = "#version 450\nlayout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;\n";
        const string mgdSets =
            "layout(r32f, set = 2, binding = 0) uniform image3D u_in;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image3D u_out;\n" +
            "layout(r32f, set = 4, binding = 0) uniform image3D rhs_img;\n";
        const string rhsSets = "layout(r32f, set = 3, binding = 0) uniform image3D b0;\n";
        // vec4 extents, not vec2 — ST_EXTENT swizzles .xyz, and a 32 B block is what the host
        // supplies (Pc32Pair). Declaring vec2 here would read the coarse Y out of the fine X.
        const string rsSets =
            "layout(r32f, set = 2, binding = 0) uniform image3D r_fine;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image3D r_coarse;\n" +
            "layout(push_constant, std430) uniform P { vec4 coarse_size; vec4 fine_size; } pc;\n";
        const string prSets =
            "layout(r32f, set = 2, binding = 0) uniform image3D u_coarse;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image3D u_fine;\n" +
            "layout(push_constant, std430) uniform P { vec4 fine_size; vec4 coarse_size; } pc;\n";
        const string dotHdr =
            "#version 450\nlayout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;\n" +
            "layout(r32f, set = 2, binding = 0) uniform image3D a_img;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image3D b_img;\n" +
            "layout(std430, set = 5, binding = 0) buffer Scalars { float sc[]; };\n" +
            "layout(push_constant, std430) uniform P { vec4 size; uint slot; uint _p0; uint _p1; uint _p2; } pc;\n";

        _shRhs = Compile(wg + rhsSets + stamp + "\n" + ReadRes("res://shaders/stamp/mg_rhs.glslinc"), "mgd3d-rhs");
        _shSm = Compile(wg + mgdSets + stamp + "\n" + ReadRes("res://shaders/stamp/mgd_smooth.glslinc"), "mgd3d-smooth");
        _shRe = Compile(wg + mgdSets + stamp + "\n" + ReadRes("res://shaders/stamp/mgd_residual.glslinc"), "mgd3d-residual");
        _shRs = Compile(wg + rsSets + nd + "\n" + ReadRes("res://shaders/stamp/mg_restrict.glslinc"), "mgd3d-restrict");
        _shPr = Compile(wg + prSets + nd + "\n" + ReadRes("res://shaders/stamp/mg_prolong.glslinc"), "mgd3d-prolong");
        _shDot = Compile(dotHdr + nd + "\n" + ReadRes("res://shaders/stamp/cg_dotbuf.glslinc"), "mgd3d-dot");
        if (!_shRhs.IsValid || !_shSm.IsValid || !_shRe.IsValid || !_shRs.IsValid || !_shPr.IsValid || !_shDot.IsValid)
        {
            return;
        }
        _pRhs = _rd.ComputePipelineCreate(_shRhs);
        _pSm = _rd.ComputePipelineCreate(_shSm);
        _pRe = _rd.ComputePipelineCreate(_shRe);
        _pRs = _rd.ComputePipelineCreate(_shRs);
        _pPr = _rd.ComputePipelineCreate(_shPr);
        _pDot = _rd.ComputePipelineCreate(_shDot);

        _hCurr = _externalState ? stateCurr : Tex(_lvl[0]);
        _hPrev = _externalState ? statePrev : Tex(_lvl[0]);
        for (int L = 0; L < _n; L++)
        {
            _x[L] = Tex(_lvl[L]); _xt[L] = Tex(_lvl[L]); _b[L] = Tex(_lvl[L]); _r[L] = Tex(_lvl[L]);
        }
        _scalars = _rd.StorageBufferCreate(32u);

        _rhsH0 = Img(_hCurr, 0, _shRhs); _rhsH1 = Img(_hPrev, 1, _shRhs); _rhsB3 = Img(_b[0], 3, _shRhs);
        _smH0 = Img(_hCurr, 0, _shSm); _smH1 = Img(_hPrev, 1, _shSm);
        _reH0 = Img(_hCurr, 0, _shRe); _reH1 = Img(_hPrev, 1, _shRe);

        for (int L = 0; L < _n; L++)
        {
            _smX2[L] = Img(_x[L], 2, _shSm); _smXt3[L] = Img(_xt[L], 3, _shSm);
            _smXt2[L] = Img(_xt[L], 2, _shSm); _smX3[L] = Img(_x[L], 3, _shSm);
            _smB4[L] = Img(_b[L], 4, _shSm);
            _reX2[L] = Img(_x[L], 2, _shRe); _reR3[L] = Img(_r[L], 3, _shRe); _reB4[L] = Img(_b[L], 4, _shRe);
        }
        for (int L = 0; L < _n - 1; L++)
        {
            _rsR2[L] = Img(_r[L], 2, _shRs); _rsB3[L] = Img(_b[L + 1], 3, _shRs);
            _prC2[L] = Img(_x[L + 1], 2, _shPr); _prF3[L] = Img(_x[L], 3, _shPr);
        }
        _dotR2 = Img(_r[0], 2, _shDot); _dotR3 = Img(_r[0], 3, _shDot); _dotSc5 = Ssbo(_scalars, 5, _shDot);

        Ready = true;
        GD.Print($"[MgDeepSolver3D] {_n} levels: {LevelsString()} · betaExp={BetaCoarsenExp}");
    }

    private string LevelsString()
    {
        var parts = new string[_n];
        for (int i = 0; i < _n; i++) { parts[i] = $"{_lvl[i].X}³"; }
        return string.Join(" -> ", parts);
    }

    public int PassesPerStep(int iters)
    {
        int vc = Math.Max(1, iters / 12);
        int per = (_n - 1) * (Nu1 + Nu2 + 2) + NuCoarse;
        return 1 + vc * per;
    }

    public void Step(byte[] stampPc, int iters, bool measure)
    {
        if (!Ready) { return; }

        byte[][] lp = new byte[_n][];
        for (int L = 0; L < _n; L++) { lp[L] = LevelPc(stampPc, L); }
        var fsz = new Vector3(_lvl[0].X, _lvl[0].Y, _lvl[0].Z);

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pRhs);
        Bind(cl, _rhsH0, 0); Bind(cl, _rhsH1, 1); Bind(cl, _rhsB3, 3);
        _rd.ComputeListSetPushConstant(cl, stampPc, (uint)stampPc.Length);
        _rd.ComputeListDispatch(cl, _gx[0], _gy[0], _gz[0]);
        _rd.ComputeListEnd();

        // x0 = h_curr is the warm start for a TIME STEP. In external mode h_curr is an input
        // field (the mask), and _x[0] already holds the previous solve's answer — which is the
        // better warm start anyway, so the copy is not just skipped, it is wrong here.
        if (!_externalState)
        {
            _rd.TextureCopy(_hCurr, _x[0], Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
        }

        int vc = Math.Max(1, iters / 12);
        for (int v = 0; v < vc; v++)
        {
            // Coarse iterates are CORRECTIONS: zero every cycle, so the restricted residual is
            // the right rhs for the levels below.
            for (int L = 1; L < _n; L++)
            {
                _rd.TextureClear(_x[L], new Color(0, 0, 0, 0), 0, 1, 0, 1);
            }

            cl = _rd.ComputeListBegin();
            for (int L = 0; L < _n - 1; L++)
            {
                Smooth(cl, L, lp[L], Nu1);
                _rd.ComputeListAddBarrier(cl);
                Residual(cl, L, lp[L]);
                _rd.ComputeListAddBarrier(cl);
                Op2(cl, _pRs, _rsR2[L], _rsB3[L], Pc32Pair(_lvl[L + 1], _lvl[L]),
                    _gx[L + 1], _gy[L + 1], _gz[L + 1]);
                _rd.ComputeListAddBarrier(cl);
            }

            Smooth(cl, _n - 1, lp[_n - 1], NuCoarse);   // coarse solve
            _rd.ComputeListAddBarrier(cl);

            for (int L = _n - 2; L >= 0; L--)
            {
                Op2(cl, _pPr, _prC2[L], _prF3[L], Pc32Pair(_lvl[L], _lvl[L + 1]),
                    _gx[L], _gy[L], _gz[L]);
                _rd.ComputeListAddBarrier(cl);
                Smooth(cl, L, lp[L], Nu2);
                _rd.ComputeListAddBarrier(cl);
            }
            _rd.ComputeListEnd();
        }

        if (measure)
        {
            cl = _rd.ComputeListBegin();
            Residual(cl, 0, lp[0]);
            _rd.ComputeListAddBarrier(cl);
            _rd.ComputeListBindComputePipeline(cl, _pDot);
            Bind(cl, _dotR2, 2); Bind(cl, _dotR3, 3); Bind(cl, _dotSc5, 5);
            _rd.ComputeListSetPushConstant(cl, PcDot(_lvl[0]), 32);
            _rd.ComputeListDispatch(cl, 1, 1, 1);
            _rd.ComputeListEnd();
            byte[] d = _rd.BufferGetData(_scalars);
            LastResidual = Mathf.Sqrt(Mathf.Max(BitConverter.ToSingle(d, 0), 0f));
        }

        // Both copies are time-step bookkeeping. In external mode they would destroy the
        // caller's two input fields; the answer stays in _x[0] (SolutionRid).
        if (!_externalState)
        {
            _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
            _rd.TextureCopy(_x[0], _hCurr, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
        }
    }

    // The stamp's own push constant, retargeted to level L: size -> the level's grid (the
    // stamp re-derives everything positional from it), beta -> scaled by the coarsening rule.
    // stamp_blob_3d has no sponge_w, so unlike the 2D host there is no width to rescale.
    private byte[] LevelPc(byte[] src, int L)
    {
        var pc = (byte[])src.Clone();
        float shrink = 1f / (1 << L);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_lvl[L].X), 0, pc, OffSize, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_lvl[L].Y), 0, pc, OffSize + 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_lvl[L].Z), 0, pc, OffSize + 8, 4);
        float beta0 = BitConverter.ToSingle(src, OffBeta);
        float f = 1f;
        for (int i = 0; i < BetaCoarsenExp; i++) { f *= shrink; }
        Buffer.BlockCopy(BitConverter.GetBytes(beta0 * f), 0, pc, OffBeta, 4);
        return pc;
    }

    private void Smooth(long cl, int L, byte[] pc, int nu)
    {
        for (int i = 0; i < nu; i++)
        {
            Rid inS = (i % 2 == 0) ? _smX2[L] : _smXt2[L];
            Rid outS = (i % 2 == 0) ? _smXt3[L] : _smX3[L];
            _rd.ComputeListBindComputePipeline(cl, _pSm);
            Bind(cl, _smH0, 0); Bind(cl, _smH1, 1);
            Bind(cl, inS, 2); Bind(cl, outS, 3); Bind(cl, _smB4[L], 4);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _gx[L], _gy[L], _gz[L]);
            if (i < nu - 1) { _rd.ComputeListAddBarrier(cl); }
        }
    }

    private void Residual(long cl, int L, byte[] pc)
    {
        _rd.ComputeListBindComputePipeline(cl, _pRe);
        Bind(cl, _reH0, 0); Bind(cl, _reH1, 1);
        Bind(cl, _reX2[L], 2); Bind(cl, _reR3[L], 3); Bind(cl, _reB4[L], 4);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx[L], _gy[L], _gz[L]);
    }

    private void Op2(long cl, Rid pipe, Rid s2, Rid s3, byte[] pc, uint gx, uint gy, uint gz)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        Bind(cl, s2, 2); Bind(cl, s3, 3);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, gx, gy, gz);
    }

    private void Bind(long cl, Rid set, int idx) => _rd.ComputeListBindUniformSet(cl, set, (uint)idx);

    public void Free()
    {
        Ready = false;
        // Sets BEFORE the textures they reference (solver-ledger.md §3c).
        FreeAll(_rhsH0, _rhsH1, _rhsB3, _smH0, _smH1, _reH0, _reH1, _dotR2, _dotR3, _dotSc5);
        FreeArr(_smX2); FreeArr(_smXt3); FreeArr(_smXt2); FreeArr(_smX3); FreeArr(_smB4);
        FreeArr(_reX2); FreeArr(_reR3); FreeArr(_reB4);
        FreeArr(_rsR2); FreeArr(_rsB3); FreeArr(_prC2); FreeArr(_prF3);
        if (_scalars.IsValid) { _rd.FreeRid(_scalars); }
        if (!_externalState) { FreeAll(_hCurr, _hPrev); }   // borrowed textures are the caller's
        FreeArr(_x); FreeArr(_xt); FreeArr(_b); FreeArr(_r);
        FreeAll(_shRhs, _shSm, _shRe, _shRs, _shPr, _shDot);
    }

    private void FreeAll(params Rid[] rids)
    {
        foreach (var r in rids) { if (r.IsValid) { _rd.FreeRid(r); } }
    }

    private void FreeArr(Rid[] rids) => FreeAll(rids);

    // ── helpers ──
    private Rid Compile(string src, string tag)
    {
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[MgDeepSolver3D] {tag} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[MgDeepSolver3D] could not read {path}"); }
        return s;
    }

    private Rid Tex(Vector3I s)
    {
        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type3D,
            Width = (uint)s.X,
            Height = (uint)s.Y,
            Depth = (uint)s.Z,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private Rid Img(Rid tex, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }

    private Rid Ssbo(Rid buf, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(buf);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }

    // two vec4 extents = 32 B, matching the rs/pr blocks declared above
    private static byte[] Pc32Pair(Vector3I dst, Vector3I src)
    {
        float[] v = { dst.X, dst.Y, dst.Z, 0f, src.X, src.Y, src.Z, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    private static byte[] PcDot(Vector3I s)
    {
        float[] v = { s.X, s.Y, s.Z, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 16);   // slot 0 + padding stay zero
        return b;
    }
}
