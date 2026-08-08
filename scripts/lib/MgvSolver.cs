using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// 2-level geometric multigrid V-cycle behind the (inlined) wave operator. Fine 256²,
// coarse 128². Per tick: compute the fine RHS, then a few V-cycles of
//   pre-smooth (Jacobi) A x = b  →  residual r = b − Ax  →  restrict r to coarse  →
//   solve the coarse correction A_c e = r  →  prolong e up and correct x  →  post-smooth.
// A constant number of V-cycles converges tight independent of resolution (the O(cells)
// win). Extensible to a deeper pyramid; kept 2-level for clarity. All rd work runs on the
// render thread; scalars for the residual readout come back once per measure tick.
public sealed class MgvSolver : IStampSolver
{
    private const int Nu1 = 2, Nu2 = 2, NuCoarse = 8;   // even → smoother result stays in the primary texture

    private readonly RenderingDevice _rd;
    private readonly Vector2I _f, _c;
    private readonly uint _fgx, _fgy, _cgx, _cgy;

    private Rid _shRhs, _shSmooth, _shResid, _shRestrict, _shProlong, _shDot;
    private Rid _pRhs, _pSmooth, _pResid, _pRestrict, _pProlong, _pDot;

    private Rid _hCurr, _hPrev, _x, _xTmp, _b0, _r0;   // fine
    private Rid _e1, _e1Tmp, _rhs1;                     // coarse
    private Rid _scalars;

    private Rid _rhsH0, _rhsH1, _rhsB03;
    private Rid _sX2, _sX3, _sXt2, _sXt3, _sB04;
    private Rid _sE12, _sE13, _sEt2, _sEt3, _sRhs14;
    private Rid _resX2, _resR03, _resB04;
    private Rid _restR02, _restRhs13;
    private Rid _prolE12, _prolX3;
    private Rid _dotR02, _dotR03, _dotSc5;

    public bool Ready { get; private set; }
    public string ModeName => "Multigrid";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }

    public MgvSolver(RenderingDevice rd, Vector2I grid, string stampPath)
    {
        _rd = rd;
        _f = grid;
        _c = new Vector2I(grid.X / 2, grid.Y / 2);
        _fgx = (uint)((_f.X - 1) / 8 + 1); _fgy = (uint)((_f.Y - 1) / 8 + 1);
        _cgx = (uint)((_c.X - 1) / 8 + 1); _cgy = (uint)((_c.Y - 1) / 8 + 1);

        // mg_rhs is the only MGV kernel that takes the STAMP push constant, so it must be
        // compiled with the stamp; the rest use MgvSolver's own scalar SmoothPc.
        // Topology block ahead of the stamp (solver-ledger.md §7d). This is COMPILE PLUMBING
        // only — mg_smooth/mg_residual are standalone (scalar beta, no stamp) and are still
        // untouched, so this class remains the uniform-beta control it was.
        string stampSrc = FileAccess.GetFileAsString(GpuStampSolver.Nd2DPath) + "\n"
                        + FileAccess.GetFileAsString(stampPath);
        string rhsBody = FileAccess.GetFileAsString("res://shaders/stamp/mg_rhs.glslinc");
        // mg_rhs / mg_restrict / mg_prolong / cg_dotbuf are dimension-generic now
        // (solver-ledger.md §7d) — these 2D headers are byte-for-byte what those files used
        // to declare themselves, so this class is unchanged as the uniform-beta control.
        const string wg8 = "#version 450\nlayout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n";
        string nd = FileAccess.GetFileAsString(GpuStampSolver.Nd2DPath);
        const string rhsSets = "layout(r32f, set = 3, binding = 0) uniform image2D b0;\n";
        const string rsSets =
            "layout(r32f, set = 2, binding = 0) uniform image2D r_fine;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image2D r_coarse;\n" +
            "layout(push_constant, std430) uniform P { vec2 coarse_size; vec2 fine_size; } pc;\n";
        const string prSets =
            "layout(r32f, set = 2, binding = 0) uniform image2D u_coarse;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image2D u_fine;\n" +
            "layout(push_constant, std430) uniform P { vec2 fine_size; vec2 coarse_size; } pc;\n";
        const string dotHdr =
            "#version 450\nlayout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;\n" +
            "layout(r32f, set = 2, binding = 0) uniform image2D a_img;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image2D b_img;\n" +
            "layout(std430, set = 5, binding = 0) buffer Scalars { float sc[]; };\n" +
            "layout(push_constant, std430) uniform P { vec2 size; uint slot; uint _pad; } pc;\n";

        _shRhs = CompileSrc(wg8 + rhsSets + stampSrc + "\n" + rhsBody, "mg-rhs");
        _shSmooth = Compile("res://shaders/stamp/mg_smooth.glslinc", "mg-smooth");
        _shResid = Compile("res://shaders/stamp/mg_residual.glslinc", "mg-resid");
        _shRestrict = CompileSrc(wg8 + rsSets + nd + "\n"
            + FileAccess.GetFileAsString("res://shaders/stamp/mg_restrict.glslinc"), "mg-restrict");
        _shProlong = CompileSrc(wg8 + prSets + nd + "\n"
            + FileAccess.GetFileAsString("res://shaders/stamp/mg_prolong.glslinc"), "mg-prolong");
        _shDot = CompileSrc(dotHdr + nd + "\n"
            + FileAccess.GetFileAsString("res://shaders/stamp/cg_dotbuf.glslinc"), "mg-dot");
        if (!_shRhs.IsValid || !_shSmooth.IsValid || !_shResid.IsValid || !_shRestrict.IsValid || !_shProlong.IsValid || !_shDot.IsValid) { return; }
        _pRhs = _rd.ComputePipelineCreate(_shRhs);
        _pSmooth = _rd.ComputePipelineCreate(_shSmooth);
        _pResid = _rd.ComputePipelineCreate(_shResid);
        _pRestrict = _rd.ComputePipelineCreate(_shRestrict);
        _pProlong = _rd.ComputePipelineCreate(_shProlong);
        _pDot = _rd.ComputePipelineCreate(_shDot);

        _hCurr = Tex(_f); _hPrev = Tex(_f); _x = Tex(_f); _xTmp = Tex(_f); _b0 = Tex(_f); _r0 = Tex(_f);
        _e1 = Tex(_c); _e1Tmp = Tex(_c); _rhs1 = Tex(_c);
        _scalars = _rd.StorageBufferCreate(32u);

        _rhsH0 = Img(_hCurr, 0, _shRhs); _rhsH1 = Img(_hPrev, 1, _shRhs); _rhsB03 = Img(_b0, 3, _shRhs);
        _sX2 = Img(_x, 2, _shSmooth); _sX3 = Img(_x, 3, _shSmooth); _sXt2 = Img(_xTmp, 2, _shSmooth); _sXt3 = Img(_xTmp, 3, _shSmooth); _sB04 = Img(_b0, 4, _shSmooth);
        _sE12 = Img(_e1, 2, _shSmooth); _sE13 = Img(_e1, 3, _shSmooth); _sEt2 = Img(_e1Tmp, 2, _shSmooth); _sEt3 = Img(_e1Tmp, 3, _shSmooth); _sRhs14 = Img(_rhs1, 4, _shSmooth);
        _resX2 = Img(_x, 2, _shResid); _resR03 = Img(_r0, 3, _shResid); _resB04 = Img(_b0, 4, _shResid);
        _restR02 = Img(_r0, 2, _shRestrict); _restRhs13 = Img(_rhs1, 3, _shRestrict);
        _prolE12 = Img(_e1, 2, _shProlong); _prolX3 = Img(_x, 3, _shProlong);
        _dotR02 = Img(_r0, 2, _shDot); _dotR03 = Img(_r0, 3, _shDot); _dotSc5 = Ssbo(_scalars, 5, _shDot);

        Ready = true;
    }

    // per V-cycle: pre + post smooths on the fine level, plus the coarse solve
    public int PassesPerStep(int iters) => iters * (Nu1 + Nu2 + NuCoarse);

    public void Step(byte[] stampPc, int iters, bool measure)
    {
        if (!Ready) { return; }
        // Assumes the stamp header begins: vec2 size(0) | beta(8) | a(12) | leak(16) | cn(20).
        // That matches stamp_wave_multi and stamp_wave_tank. cn was being read from byte 36,
        // which in stamp_wave_tank is `slope` — a silent wrong-operator bug.
        float beta0 = BitConverter.ToSingle(stampPc, 8);
        float a = BitConverter.ToSingle(stampPc, 12);
        float leak = BitConverter.ToSingle(stampPc, 16);
        float cn = stampPc.Length >= 24 ? BitConverter.ToSingle(stampPc, 20) : 0f;
        byte[] smF = SmoothPc(_f, beta0, a, leak, cn);
        byte[] smC = SmoothPc(_c, beta0 * 0.25f, a, leak, cn);
        byte[] restrictPc = Pc16(_c.X, _c.Y, _f.X, _f.Y);
        byte[] prolongPc = Pc16(_f.X, _f.Y, _c.X, _c.Y);
        var fsz = new Vector3(_f.X, _f.Y, 1);

        // b0 = st_rhs (fine)
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pRhs);
        Bind(cl, _rhsH0, 0); Bind(cl, _rhsH1, 1); Bind(cl, _rhsB03, 3);
        _rd.ComputeListSetPushConstant(cl, stampPc, (uint)stampPc.Length);
        _rd.ComputeListDispatch(cl, _fgx, _fgy, 1);
        _rd.ComputeListEnd();

        _rd.TextureCopy(_hCurr, _x, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);   // x = h_curr

        int vc = Math.Max(1, iters / 12);
        for (int i = 0; i < vc; i++)
        {
            cl = _rd.ComputeListBegin();
            Smooth(cl, _sX2, _sX3, _sXt2, _sXt3, _sB04, smF, _fgx, _fgy, Nu1);   // pre-smooth fine
            _rd.ComputeListAddBarrier(cl);
            Op3(cl, _pResid, _resX2, _resR03, _resB04, smF, _fgx, _fgy);          // r0 = b0 − A x
            _rd.ComputeListAddBarrier(cl);
            Op2(cl, _pRestrict, _restR02, _restRhs13, restrictPc, _cgx, _cgy);    // restrict r0 → rhs1
            _rd.ComputeListEnd();

            _rd.TextureClear(_e1, new Color(0, 0, 0, 0), 0, 1, 0, 1);             // e1 = 0

            cl = _rd.ComputeListBegin();
            Smooth(cl, _sE12, _sE13, _sEt2, _sEt3, _sRhs14, smC, _cgx, _cgy, NuCoarse);  // coarse solve
            _rd.ComputeListAddBarrier(cl);
            Op2(cl, _pProlong, _prolE12, _prolX3, prolongPc, _fgx, _fgy);         // x += prolong(e1)
            _rd.ComputeListAddBarrier(cl);
            Smooth(cl, _sX2, _sX3, _sXt2, _sXt3, _sB04, smF, _fgx, _fgy, Nu2);    // post-smooth fine
            _rd.ComputeListEnd();
        }

        if (measure)
        {
            cl = _rd.ComputeListBegin();
            Op3(cl, _pResid, _resX2, _resR03, _resB04, smF, _fgx, _fgy);
            _rd.ComputeListAddBarrier(cl);
            _rd.ComputeListBindComputePipeline(cl, _pDot);
            Bind(cl, _dotR02, 2); Bind(cl, _dotR03, 3); Bind(cl, _dotSc5, 5);
            _rd.ComputeListSetPushConstant(cl, Pc16(_f.X, _f.Y, 0, 0), 16);
            _rd.ComputeListDispatch(cl, 1, 1, 1);
            _rd.ComputeListEnd();
            byte[] d = _rd.BufferGetData(_scalars);
            LastResidual = Mathf.Sqrt(Mathf.Max(BitConverter.ToSingle(d, 0), 0f));
        }

        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
        _rd.TextureCopy(_x, _hCurr, Vector3.Zero, Vector3.Zero, fsz, 0, 0, 0, 0);
    }

    private void Smooth(long cl, Rid u2, Rid u3, Rid ut2, Rid ut3, Rid rhs4, byte[] pc, uint gx, uint gy, int nu)
    {
        for (int i = 0; i < nu; i++)
        {
            Rid inS = (i % 2 == 0) ? u2 : ut2;
            Rid outS = (i % 2 == 0) ? ut3 : u3;
            _rd.ComputeListBindComputePipeline(cl, _pSmooth);
            Bind(cl, inS, 2); Bind(cl, outS, 3); Bind(cl, rhs4, 4);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, gx, gy, 1);
            if (i < nu - 1) { _rd.ComputeListAddBarrier(cl); }
        }
    }

    private void Op3(long cl, Rid pipe, Rid s2, Rid s3, Rid s4, byte[] pc, uint gx, uint gy)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        Bind(cl, s2, 2); Bind(cl, s3, 3); Bind(cl, s4, 4);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, gx, gy, 1);
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
        foreach (var r in new[]
        {
            _rhsH0, _rhsH1, _rhsB03, _sX2, _sX3, _sXt2, _sXt3, _sB04,
            _sE12, _sE13, _sEt2, _sEt3, _sRhs14, _resX2, _resR03, _resB04,
            _restR02, _restRhs13, _prolE12, _prolX3, _dotR02, _dotR03, _dotSc5,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        if (_scalars.IsValid) { _rd.FreeRid(_scalars); }
        foreach (var t in new[] { _hCurr, _hPrev, _x, _xTmp, _b0, _r0, _e1, _e1Tmp, _rhs1 })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _shRhs, _shSmooth, _shResid, _shRestrict, _shProlong, _shDot })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──
    private Rid CompileSrc(string src, string tag)
    {
        var rdSrc0 = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv0 = _rd.ShaderCompileSpirVFromSource(rdSrc0);
        string err0 = spirv0.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err0)) { GD.PushError($"[MgvSolver] {tag} compile error:\n{err0}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv0);
    }

    private Rid Compile(string path, string tag)
    {
        string src = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[MgvSolver] could not read {path}"); return default; }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[MgvSolver] {tag} compile error:\n{err}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv);
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

    private static byte[] SmoothPc(Vector2I s, float beta, float a, float leak, float cn)
    {
        float[] v = { s.X, s.Y, beta, a, leak, cn, 0f, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    private static byte[] Pc16(float f0, float f1, float f2, float f3)
    {
        float[] v = { f0, f1, f2, f3 };
        var b = new byte[16];
        Buffer.BlockCopy(v, 0, b, 0, 16);
        return b;
    }
}
