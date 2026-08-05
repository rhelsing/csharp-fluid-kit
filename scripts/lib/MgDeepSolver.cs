using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Deep geometric multigrid V-cycle over the REAL stamp operator.
//
// This is a FORK of MgvSolver, not a replacement (solver-ledger.md §7a). MgvSolver stays
// exactly as it was and stays in the dropdown, because it is the control: it inverts a
// scalar-beta (uniform-depth) approximation while every other solver inverts the stamp.
// Running both side by side is what makes that divergence visible — turn bathymetry
// coupling (extra.z) up from 0 and watch "Multigrid (uniform B)" peel away from CG while
// "Multigrid (deep)" tracks it.
//
// Two differences from MgvSolver:
//
//  1. OPERATOR. mgd_smooth / mgd_residual are compiled against the stamp and call
//     st_diag / st_conductance. There is no SmoothPc; every level is driven by the
//     stamp's OWN push constant with three fields rewritten (see LevelPc).
//
//  2. DEPTH. A full pyramid (N -> N/2 -> ... -> <=8) instead of one coarse level. This is
//     what makes the cycle count independent of resolution — the O(cells) claim in
//     mna-next-steps.md §4 that has never actually been plotted. A 2-level cycle still
//     leaves the longest wavelengths under-resolved, which is the exact failure mode
//     solver-ledger.md §4 describes (relaxation is a smoother, not a solver).
//
// Why no Galerkin coarsening: st_depth() derives depth from st_x(c) and pc.size, so handing
// a coarse level its own `size` re-derives correct per-face conductances for free. The stamp
// coarsens itself. That only holds while the bed is ANALYTIC — a sampled/painted bathymetry
// texture would need real Galerkin coarsening and this shortcut would silently be wrong.
//
// All rd work runs on the render thread; the residual scalar comes back once per measure tick.
public sealed class MgDeepSolver : IStampSolver
{
    // even nu1/nu2 => after `nu` smooths the result is back in the primary texture
    private const int Nu1 = 2, Nu2 = 2, NuCoarse = 8;
    private const int CoarsestSize = 8;

    // stamp push-constant field offsets (stamp_wave_tank / stamp_wave_multi share this header)
    private const int OffSize = 0, OffBeta = 8, OffA = 12, OffLeak = 16, OffCn = 20, OffSpongeW = 24;

    private readonly RenderingDevice _rd;
    private readonly Vector2I[] _lvl;
    private readonly uint[] _gx, _gy;
    private readonly int _n;

    private Rid _shRhs, _shSm, _shRe, _shRs, _shPr, _shDot;
    private Rid _pRhs, _pSm, _pRe, _pRs, _pPr, _pDot;

    private Rid _hCurr, _hPrev, _scalars;
    private readonly Rid[] _x, _xt, _b, _r;

    // uniform sets, per level where noted
    private Rid _rhsH0, _rhsH1, _rhsB3;
    private Rid _smH0, _smH1, _reH0, _reH1;
    private readonly Rid[] _smX2, _smXt3, _smXt2, _smX3, _smB4;
    private readonly Rid[] _reX2, _reR3, _reB4;
    private readonly Rid[] _rsR2, _rsB3;     // level L -> L+1
    private readonly Rid[] _prC2, _prF3;     // level L+1 -> L
    private Rid _dotR2, _dotR3, _dotSc5;

    public bool Ready { get; private set; }
    public string ModeName => "MultigridDeep";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }
    public int Levels => _n;

    public MgDeepSolver(RenderingDevice rd, Vector2I grid, string stampPath)
    {
        _rd = rd;

        // pyramid: halve until either axis would drop below CoarsestSize, or would go odd
        // (2x2 restriction assumes even dimensions; an odd level would silently drop a row)
        var levels = new System.Collections.Generic.List<Vector2I> { grid };
        var s = grid;
        while (s.X / 2 >= CoarsestSize && s.Y / 2 >= CoarsestSize && (s.X & 1) == 0 && (s.Y & 1) == 0)
        {
            s = new Vector2I(s.X / 2, s.Y / 2);
            levels.Add(s);
        }
        _n = levels.Count;
        _lvl = new Vector2I[_n];
        _gx = new uint[_n];
        _gy = new uint[_n];
        for (int i = 0; i < _n; i++)
        {
            _lvl[i] = levels[i];
            _gx[i] = (uint)((_lvl[i].X - 1) / 8 + 1);
            _gy[i] = (uint)((_lvl[i].Y - 1) / 8 + 1);
        }

        _x = new Rid[_n]; _xt = new Rid[_n]; _b = new Rid[_n]; _r = new Rid[_n];
        _smX2 = new Rid[_n]; _smXt3 = new Rid[_n]; _smXt2 = new Rid[_n]; _smX3 = new Rid[_n]; _smB4 = new Rid[_n];
        _reX2 = new Rid[_n]; _reR3 = new Rid[_n]; _reB4 = new Rid[_n];
        _rsR2 = new Rid[_n]; _rsB3 = new Rid[_n];
        _prC2 = new Rid[_n]; _prF3 = new Rid[_n];

        string stamp = ReadRes(stampPath);
        const string wg = "#version 450\nlayout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n";
        // mgd_* need the level images; the stamp brings h_curr(0)/h_prev(1) and `pc`.
        const string mgdSets =
            "layout(r32f, set = 2, binding = 0) uniform image2D u_in;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image2D u_out;\n" +
            "layout(r32f, set = 4, binding = 0) uniform image2D rhs_img;\n";

        _shRhs = Compile(wg + stamp + "\n" + ReadRes("res://shaders/stamp/mg_rhs.glslinc"), "mgd-rhs");
        _shSm = Compile(wg + mgdSets + stamp + "\n" + ReadRes("res://shaders/stamp/mgd_smooth.glslinc"), "mgd-smooth");
        _shRe = Compile(wg + mgdSets + stamp + "\n" + ReadRes("res://shaders/stamp/mgd_residual.glslinc"), "mgd-residual");
        _shRs = Compile(ReadRes("res://shaders/stamp/mg_restrict.glslinc"), "mgd-restrict");   // standalone, reused as-is
        _shPr = Compile(ReadRes("res://shaders/stamp/mg_prolong.glslinc"), "mgd-prolong");     // standalone, reused as-is
        _shDot = Compile(ReadRes("res://shaders/stamp/cg_dotbuf.glslinc"), "mgd-dot");
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

        _hCurr = Tex(_lvl[0]);
        _hPrev = Tex(_lvl[0]);
        for (int L = 0; L < _n; L++)
        {
            _x[L] = Tex(_lvl[L]); _xt[L] = Tex(_lvl[L]); _b[L] = Tex(_lvl[L]); _r[L] = Tex(_lvl[L]);
        }
        _scalars = _rd.StorageBufferCreate(32u);

        _rhsH0 = Img(_hCurr, 0, _shRhs); _rhsH1 = Img(_hPrev, 1, _shRhs); _rhsB3 = Img(_b[0], 3, _shRhs);
        // st_diag/st_conductance never read the state images, but the stamp declares them.
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
        GD.Print($"[MgDeepSolver] {_n} levels: {LevelsString()}");
    }

    private string LevelsString()
    {
        var parts = new string[_n];
        for (int i = 0; i < _n; i++) { parts[i] = $"{_lvl[i].X}x{_lvl[i].Y}"; }
        return string.Join(" -> ", parts);
    }

    // Honest dispatch count: smooths + residual + restrict on the way down, prolong +
    // smooths on the way up, plus the coarse solve. Constant-per-cycle like MgvSolver's
    // would understate a deep pyramid by (levels-1)x and make MG look free.
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
        var fsz = new Vector3(_lvl[0].X, _lvl[0].Y, 1);

        // b0 = st_rhs on the fine level (the only place the state images are read)
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pRhs);
        Bind(cl, _rhsH0, 0); Bind(cl, _rhsH1, 1); Bind(cl, _rhsB3, 3);
        _rd.ComputeListSetPushConstant(cl, stampPc, (uint)stampPc.Length);
        _rd.ComputeListDispatch(cl, _gx[0], _gy[0], 1);
        _rd.ComputeListEnd();

        _rd.TextureCopy(_hCurr, _x[0], Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);   // x0 = h_curr

        int vc = Math.Max(1, iters / 12);
        for (int v = 0; v < vc; v++)
        {
            // Coarse iterates are CORRECTIONS: they must start at zero every cycle.
            // Clearing here (before the list) also means the down-pass smooths on levels
            // >=1 see a zero initial guess, which is what makes the restricted residual
            // the right rhs for them.
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
                Op2(cl, _pRs, _rsR2[L], _rsB3[L], Pc16(_lvl[L + 1], _lvl[L]), _gx[L + 1], _gy[L + 1]);
                _rd.ComputeListAddBarrier(cl);
            }

            Smooth(cl, _n - 1, lp[_n - 1], NuCoarse);   // coarse solve
            _rd.ComputeListAddBarrier(cl);

            for (int L = _n - 2; L >= 0; L--)
            {
                Op2(cl, _pPr, _prC2[L], _prF3[L], Pc16(_lvl[L], _lvl[L + 1]), _gx[L], _gy[L]);
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
            _rd.ComputeListSetPushConstant(cl, PcDot(_lvl[0]), 16);
            _rd.ComputeListDispatch(cl, 1, 1, 1);
            _rd.ComputeListEnd();
            byte[] d = _rd.BufferGetData(_scalars);
            LastResidual = Mathf.Sqrt(Mathf.Max(BitConverter.ToSingle(d, 0), 0f));
        }

        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
        _rd.TextureCopy(_x[0], _hCurr, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
    }

    // The stamp's own push constant, retargeted to level L. Three fields move:
    //   size        -> the level's grid  (st_x/st_z/st_depth re-derive the bed from it)
    //   beta_scale  /= 4^L               (it carries 1/dx^2, and dx doubles per level)
    //   sponge_w    /= 2^L               (a width in CELLS; keep the PHYSICAL width fixed)
    // Everything else — paddle, macro/micro, drop, damping, DC mode — rides through
    // untouched, which is the whole reason this is a push-constant rewrite and not a
    // reimplementation of the operator.
    private byte[] LevelPc(byte[] src, int L)
    {
        var pc = (byte[])src.Clone();
        float shrink = 1f / (1 << L);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_lvl[L].X), 0, pc, OffSize, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_lvl[L].Y), 0, pc, OffSize + 4, 4);
        float beta0 = BitConverter.ToSingle(src, OffBeta);
        Buffer.BlockCopy(BitConverter.GetBytes(beta0 * shrink * shrink), 0, pc, OffBeta, 4);
        if (src.Length >= OffSpongeW + 4)
        {
            float sw0 = BitConverter.ToSingle(src, OffSpongeW);
            Buffer.BlockCopy(BitConverter.GetBytes(sw0 * shrink), 0, pc, OffSpongeW, 4);
        }
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
            _rd.ComputeListDispatch(cl, _gx[L], _gy[L], 1);
            if (i < nu - 1) { _rd.ComputeListAddBarrier(cl); }
        }
    }

    private void Residual(long cl, int L, byte[] pc)
    {
        _rd.ComputeListBindComputePipeline(cl, _pRe);
        Bind(cl, _reH0, 0); Bind(cl, _reH1, 1);
        Bind(cl, _reX2[L], 2); Bind(cl, _reR3[L], 3); Bind(cl, _reB4[L], 4);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx[L], _gy[L], 1);
    }

    private void Op2(long cl, Rid pipe, Rid s2, Rid s3, byte[] pc, uint gx, uint gy)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        Bind(cl, s2, 2); Bind(cl, s3, 3);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, gx, gy, 1);
    }

    private void Bind(long cl, Rid set, int idx) => _rd.ComputeListBindUniformSet(cl, set, (uint)idx);

    public void Free()
    {
        Ready = false;
        // Sets BEFORE the textures they reference — Godot auto-frees dependent sets, so
        // freeing textures first makes these frees invalid (solver-ledger.md §3c).
        FreeAll(_rhsH0, _rhsH1, _rhsB3, _smH0, _smH1, _reH0, _reH1, _dotR2, _dotR3, _dotSc5);
        FreeArr(_smX2); FreeArr(_smXt3); FreeArr(_smXt2); FreeArr(_smX3); FreeArr(_smB4);
        FreeArr(_reX2); FreeArr(_reR3); FreeArr(_reB4);
        FreeArr(_rsR2); FreeArr(_rsB3); FreeArr(_prC2); FreeArr(_prF3);
        if (_scalars.IsValid) { _rd.FreeRid(_scalars); }
        FreeAll(_hCurr, _hPrev);
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
            GD.PushError($"[MgDeepSolver] {tag} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[MgDeepSolver] could not read {path}"); }
        return s;
    }

    private Rid Tex(Vector2I s)
    {
        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)s.X,
            Height = (uint)s.Y,
            Depth = 1,
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

    private static byte[] Pc16(Vector2I dst, Vector2I src)
    {
        float[] v = { dst.X, dst.Y, src.X, src.Y };
        var b = new byte[16];
        Buffer.BlockCopy(v, 0, b, 0, 16);
        return b;
    }

    private static byte[] PcDot(Vector2I s)
    {
        float[] v = { s.X, s.Y, 0f, 0f };
        var b = new byte[16];
        Buffer.BlockCopy(v, 0, b, 0, 16);
        return b;
    }
}
