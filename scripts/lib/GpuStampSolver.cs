using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Reusable matrix-free GPU stamp solver. Owns the RenderingDevice lifecycle and the
// storage images; the PHYSICS is a swappable GLSL stamp (st_diag / st_conductance /
// st_rhs). Shaders are assembled in C# and compiled via ShaderCompileSpirVFromSource.
//
// Solve modes behind the same stamp:
//   - Jacobi — one relaxation sweep, ping-ponged K times.
//   - Rbgs   — Red-Black Gauss-Seidel (parity injected as a compile-time constant).
//   - Cg     — Conjugate Gradient, fully GPU-resident: SpMV + dot-products reduced to
//              a scalar BUFFER, α/β computed by a 1-thread kernel, saxpy reads its
//              coefficients from the buffer. The whole solve is ONE barrier-chained
//              compute list with NO per-iteration readback (a fixed iteration budget →
//              predictable cost). One low-rate readback only for the on-screen residual.
// Call every method from inside RenderingServer.CallOnRenderThread.
public sealed class GpuStampSolver : IStampSolver
{
    public enum Mode { Jacobi, Rbgs, Cg }

    private const string HeaderBody =
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image2D iter_out;\n";

    private const string ResidualHeader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n";

    private const string JacobiBodyPath = "res://shaders/stamp/solve_jacobi.glslinc";
    private const string RbgsBodyPath = "res://shaders/stamp/solve_rbgs.glslinc";
    private const string ReduceBodyPath = "res://shaders/stamp/reduce_residual.glslinc";
    private const string CgResPath = "res://shaders/stamp/cg_residual.glslinc";
    private const string CgSpmvPath = "res://shaders/stamp/cg_spmv.glslinc";
    private const string CgDotPath = "res://shaders/stamp/cg_dotbuf.glslinc";
    private const string CgCalcPath = "res://shaders/stamp/cg_calc.glslinc";
    private const string CgSaxpyPath = "res://shaders/stamp/cg_saxpybuf.glslinc";

    // CG scalar-buffer slots
    private const uint SRsold = 0, SPap = 1, SAlpha = 2, SRsnew = 3, SBeta = 4, SNegAlpha = 5, SOne = 6, SZero = 7;

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _gx, _gy, _numWg;
    private readonly Mode _mode;

    private Rid _shader, _pipeline;
    private Rid _shaderRed, _shaderBlack, _pipeRed, _pipeBlack;
    private Rid _hCurr, _hPrev, _iterA, _iterB;
    private Rid _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B;

    private Rid _resShader, _resPipeline, _ssbo;
    private Rid _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo;
    private bool _resReady;

    // Cg (GPU-resident scalars)
    private Rid _cgRes, _cgSpmv, _cgDot, _cgCalc, _cgSaxpy;
    private Rid _spmvH0, _spmvH1;   // stamp state sets, bound so the declarations are satisfied
    private Rid _cgResPipe, _cgSpmvPipe, _cgDotPipe, _cgCalcPipe, _cgSaxpyPipe;
    private Rid _cgX, _cgR, _cgP, _cgAp, _cgScalars;
    private Rid _resH0, _resH1, _resX2, _resR3;
    private Rid _spmvP2, _spmvAp3;
    private Rid _dotR2, _dotR3, _dotP2, _dotAp3, _dotSc5;
    private Rid _calcSc5;
    private Rid _saxHc2, _saxX3, _saxR2, _saxP3, _saxP2, _saxAp2, _saxR3, _saxSc5;

    public bool Ready { get; private set; }
    public Mode SolveMode => _mode;
    public string ModeName => _mode.ToString();
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;   // h(t-dt) — with HeightRid gives ∂h/∂t consumers a velocity
    public float LastResidual { get; private set; }

    // solveBodyPath (optional) swaps the relaxation body for this instance only —
    // e.g. scene 50's toroidal-wrap RBGS (solve_rbgs_wrap.glslinc). null = the
    // standard clamped bodies; every existing caller is unaffected.
    public GpuStampSolver(RenderingDevice rd, Vector2I grid, string stampPath, Mode mode = Mode.Jacobi, string? solveBodyPath = null)
    {
        _rd = rd;
        _grid = grid;
        _mode = mode;
        _gx = (uint)((grid.X - 1) / 8 + 1);
        _gy = (uint)((grid.Y - 1) / 8 + 1);
        _numWg = _gx * _gy;

        string stampText = ReadRes(stampPath);

        if (mode == Mode.Jacobi)
        {
            _shader = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(solveBodyPath ?? JacobiBodyPath), "jacobi");
            if (!_shader.IsValid) { return; }
            _pipeline = _rd.ComputePipelineCreate(_shader);
        }
        else if (mode == Mode.Rbgs)
        {
            string body = ReadRes(solveBodyPath ?? RbgsBodyPath);
            _shaderRed = Compile("#version 450\n#define PARITY 0\n" + HeaderBody + stampText + "\n" + body, "rbgs-red");
            _shaderBlack = Compile("#version 450\n#define PARITY 1\n" + HeaderBody + stampText + "\n" + body, "rbgs-black");
            if (!_shaderRed.IsValid || !_shaderBlack.IsValid) { return; }
            _pipeRed = _rd.ComputePipelineCreate(_shaderRed);
            _pipeBlack = _rd.ComputePipelineCreate(_shaderBlack);
        }
        else // Cg
        {
            _cgRes = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(CgResPath), "cg-res");
            // stamp-generic now: compiled WITH the stamp so it uses st_diag/st_conductance
            _cgSpmv = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(CgSpmvPath), "cg-spmv");
            _cgDot = Compile(ReadRes(CgDotPath), "cg-dot");       // standalone (own #version + bindings)
            _cgCalc = Compile(ReadRes(CgCalcPath), "cg-calc");
            _cgSaxpy = Compile(ReadRes(CgSaxpyPath), "cg-saxpy");
            if (!_cgRes.IsValid || !_cgSpmv.IsValid || !_cgDot.IsValid || !_cgCalc.IsValid || !_cgSaxpy.IsValid) { return; }
            _cgResPipe = _rd.ComputePipelineCreate(_cgRes);
            _cgSpmvPipe = _rd.ComputePipelineCreate(_cgSpmv);
            _cgDotPipe = _rd.ComputePipelineCreate(_cgDot);
            _cgCalcPipe = _rd.ComputePipelineCreate(_cgCalc);
            _cgSaxpyPipe = _rd.ComputePipelineCreate(_cgSaxpy);
        }

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)grid.X,
            Height = (uint)grid.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _hCurr = MakeTex(tf);
        _hPrev = MakeTex(tf);
        _iterA = MakeTex(tf);
        _iterB = MakeTex(tf);
        _ssbo = _rd.StorageBufferCreate(_numWg * 4u);

        if (mode == Mode.Cg)
        {
            _cgX = MakeTex(tf);
            _cgR = MakeTex(tf);
            _cgP = MakeTex(tf);
            _cgAp = MakeTex(tf);

            var scInit = new byte[32];
            float[] scVals = { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f };   // ONE @6, ZERO @7
            Buffer.BlockCopy(scVals, 0, scInit, 0, 32);
            _cgScalars = _rd.StorageBufferCreate(32u, scInit);

            _resH0 = MakeImageSet(_hCurr, 0, _cgRes);
            _resH1 = MakeImageSet(_hPrev, 1, _cgRes);
            _resX2 = MakeImageSet(_cgX, 2, _cgRes);
            _resR3 = MakeImageSet(_cgR, 3, _cgRes);

            _spmvP2 = MakeImageSet(_cgP, 2, _cgSpmv);
            _spmvAp3 = MakeImageSet(_cgAp, 3, _cgSpmv);
            _spmvH0 = MakeImageSet(_hCurr, 0, _cgSpmv);    // stamp declares these; must be bound
            _spmvH1 = MakeImageSet(_hPrev, 1, _cgSpmv);

            _dotR2 = MakeImageSet(_cgR, 2, _cgDot);
            _dotR3 = MakeImageSet(_cgR, 3, _cgDot);
            _dotP2 = MakeImageSet(_cgP, 2, _cgDot);
            _dotAp3 = MakeImageSet(_cgAp, 3, _cgDot);
            _dotSc5 = MakeSsboSet(_cgScalars, 5, _cgDot);

            _calcSc5 = MakeSsboSet(_cgScalars, 5, _cgCalc);

            _saxHc2 = MakeImageSet(_hCurr, 2, _cgSaxpy);
            _saxX3 = MakeImageSet(_cgX, 3, _cgSaxpy);
            _saxR2 = MakeImageSet(_cgR, 2, _cgSaxpy);
            _saxP3 = MakeImageSet(_cgP, 3, _cgSaxpy);
            _saxP2 = MakeImageSet(_cgP, 2, _cgSaxpy);
            _saxAp2 = MakeImageSet(_cgAp, 2, _cgSaxpy);
            _saxR3 = MakeImageSet(_cgR, 3, _cgSaxpy);
            _saxSc5 = MakeSsboSet(_cgScalars, 5, _cgSaxpy);

            Ready = true;
            return;
        }

        Rid setShader = mode == Mode.Jacobi ? _shader : _shaderRed;
        _setHCurr = MakeImageSet(_hCurr, 0, setShader);
        _setHPrev = MakeImageSet(_hPrev, 1, setShader);
        _set2Curr = MakeImageSet(_hCurr, 2, setShader);
        _set2A = MakeImageSet(_iterA, 2, setShader);
        _set2B = MakeImageSet(_iterB, 2, setShader);
        _set3A = MakeImageSet(_iterA, 3, setShader);
        _set3B = MakeImageSet(_iterB, 3, setShader);

        _resShader = Compile(ResidualHeader + stampText + "\n" + ReadRes(ReduceBodyPath), "residual");
        if (_resShader.IsValid)
        {
            _resPipeline = _rd.ComputePipelineCreate(_resShader);
            _resHCurr = MakeImageSet(_hCurr, 0, _resShader);
            _resHPrev = MakeImageSet(_hPrev, 1, _resShader);
            _res2A = MakeImageSet(_iterA, 2, _resShader);
            _res2B = MakeImageSet(_iterB, 2, _resShader);
            _setSsbo = MakeSsboSet(_ssbo, 4, _resShader);
            _resReady = true;
        }

        Ready = true;
    }

    public int PassesPerStep(int iters) => _mode switch
    {
        Mode.Rbgs => 2 * iters,
        Mode.Cg => iters,
        _ => iters,
    };

    public void Step(byte[] pc, int iters, bool measureResidual = false)
    {
        if (!Ready) { return; }
        if (_mode == Mode.Cg)
        {
            StepCg(pc, iters, measureResidual);
            return;
        }

        int passes = _mode == Mode.Rbgs ? 2 * iters : iters;

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindUniformSet(cl, _setHCurr, 0);
        _rd.ComputeListBindUniformSet(cl, _setHPrev, 1);
        for (int p = 0; p < passes; p++)
        {
            Rid pipe = _mode == Mode.Rbgs ? (p % 2 == 0 ? _pipeRed : _pipeBlack) : _pipeline;
            _rd.ComputeListBindComputePipeline(cl, pipe);
            Rid inSet = p == 0 ? _set2Curr : (p % 2 == 1 ? _set2A : _set2B);
            Rid outSet = p % 2 == 0 ? _set3A : _set3B;
            _rd.ComputeListBindUniformSet(cl, inSet, 2);
            _rd.ComputeListBindUniformSet(cl, outSet, 3);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _gx, _gy, 1);
            _rd.ComputeListAddBarrier(cl);
        }

        bool doRes = measureResidual && _resReady;
        if (doRes)
        {
            Rid resSet2 = (passes - 1) % 2 == 0 ? _res2A : _res2B;
            _rd.ComputeListBindComputePipeline(cl, _resPipeline);
            _rd.ComputeListBindUniformSet(cl, _resHCurr, 0);
            _rd.ComputeListBindUniformSet(cl, _resHPrev, 1);
            _rd.ComputeListBindUniformSet(cl, resSet2, 2);
            _rd.ComputeListBindUniformSet(cl, _setSsbo, 4);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _gx, _gy, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (doRes)
        {
            LastResidual = Mathf.Sqrt(ReadSsbo());
        }

        Rid result = (passes - 1) % 2 == 0 ? _iterA : _iterB;
        var size = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    // Shift the state by whole cells (h_curr AND h_prev together) so a moving window stays
    // WORLD-anchored (clipmap scroll). Newly-exposed edge cells are cleared to rest. Call on
    // the render thread, between Steps (uses _iterA as scratch). No-op for CG mode.
    public void Scroll(int sx, int sy)
    {
        if (!Ready || _mode == Mode.Cg || (sx == 0 && sy == 0)) { return; }
        ShiftTex(_hCurr, sx, sy);
        ShiftTex(_hPrev, sx, sy);
    }

    private void ShiftTex(Rid tex, int sx, int sy)
    {
        var full = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(tex, _iterA, Vector3.Zero, Vector3.Zero, full, 0, 0, 0, 0);   // tex → scratch
        _rd.TextureClear(tex, new Color(0, 0, 0, 0), 0, 1, 0, 1);                      // tex = rest
        int w = _grid.X - Math.Abs(sx);
        int h = _grid.Y - Math.Abs(sy);
        if (w <= 0 || h <= 0) { return; }   // scrolled entirely off → stays cleared
        int fx = sx > 0 ? 0 : -sx, tx = sx > 0 ? sx : 0;
        int fy = sy > 0 ? 0 : -sy, ty = sy > 0 ? sy : 0;
        _rd.TextureCopy(_iterA, tex, new Vector3(fx, fy, 0), new Vector3(tx, ty, 0), new Vector3(w, h, 1), 0, 0, 0, 0);
    }

    // Sync readback of the current height field (x-fastest). For CPU coupling (buoyancy).
    public float[] ReadField()
    {
        if (!Ready) { return Array.Empty<float>(); }
        byte[] data = _rd.TextureGetData(_hCurr, 0);
        int n = _grid.X * _grid.Y;
        var f = new float[n];
        Buffer.BlockCopy(data, 0, f, 0, Math.Min(data.Length, n * 4));
        return f;
    }

    // Conjugate Gradient — one barrier-chained compute list, scalars GPU-resident.
    private void StepCg(byte[] stampPc, int maxIters, bool measure)
    {
        long cl = _rd.ComputeListBegin();
        SaxpyBuf(cl, _saxHc2, _saxX3, SZero, SOne);          // X = h_curr
        _rd.ComputeListAddBarrier(cl);
        DispatchStamp(cl, _cgResPipe, _resH0, _resH1, _resX2, _resR3, stampPc);  // R = b − A X
        _rd.ComputeListAddBarrier(cl);
        SaxpyBuf(cl, _saxR2, _saxP3, SZero, SOne);           // P = R
        _rd.ComputeListAddBarrier(cl);
        DotBuf(cl, _dotR2, _dotR3, SRsold);                  // rsold = R·R
        _rd.ComputeListAddBarrier(cl);

        for (int k = 0; k < maxIters; k++)
        {
            DispatchStamp(cl, _cgSpmvPipe, _spmvH0, _spmvH1, _spmvP2, _spmvAp3, stampPc);  // AP = A P
            _rd.ComputeListAddBarrier(cl);
            DotBuf(cl, _dotP2, _dotAp3, SPap);               // pAp = P·AP
            _rd.ComputeListAddBarrier(cl);
            Calc(cl, 0);                                     // alpha, -alpha
            _rd.ComputeListAddBarrier(cl);
            SaxpyBuf(cl, _saxP2, _saxX3, SOne, SAlpha);      // X += alpha P
            _rd.ComputeListAddBarrier(cl);
            SaxpyBuf(cl, _saxAp2, _saxR3, SOne, SNegAlpha);  // R -= alpha AP
            _rd.ComputeListAddBarrier(cl);
            DotBuf(cl, _dotR2, _dotR3, SRsnew);              // rsnew = R·R
            _rd.ComputeListAddBarrier(cl);
            Calc(cl, 1);                                     // beta, rsold<-rsnew
            _rd.ComputeListAddBarrier(cl);
            SaxpyBuf(cl, _saxR2, _saxP3, SBeta, SOne);       // P = beta P + R
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (measure)
        {
            byte[] d = _rd.BufferGetData(_cgScalars);
            float rsnew = BitConverter.ToSingle(d, (int)SRsnew * 4);
            LastResidual = Mathf.Sqrt(Mathf.Max(rsnew, 0f));
        }

        var size = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(_cgX, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    private void SaxpyBuf(long cl, Rid setX2, Rid setY3, uint cySlot, uint cxSlot)
    {
        _rd.ComputeListBindComputePipeline(cl, _cgSaxpyPipe);
        _rd.ComputeListBindUniformSet(cl, setX2, 2);
        _rd.ComputeListBindUniformSet(cl, setY3, 3);
        _rd.ComputeListBindUniformSet(cl, _saxSc5, 5);
        _rd.ComputeListSetPushConstant(cl, Pc16(cySlot, cxSlot), 16);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    private void DotBuf(long cl, Rid a2, Rid b3, uint slot)
    {
        _rd.ComputeListBindComputePipeline(cl, _cgDotPipe);
        _rd.ComputeListBindUniformSet(cl, a2, 2);
        _rd.ComputeListBindUniformSet(cl, b3, 3);
        _rd.ComputeListBindUniformSet(cl, _dotSc5, 5);
        _rd.ComputeListSetPushConstant(cl, Pc16(slot, 0), 16);
        _rd.ComputeListDispatch(cl, 1, 1, 1);
    }

    private void Calc(long cl, uint op)
    {
        _rd.ComputeListBindComputePipeline(cl, _cgCalcPipe);
        _rd.ComputeListBindUniformSet(cl, _calcSc5, 5);
        _rd.ComputeListSetPushConstant(cl, PcCalc(op), 16);
        _rd.ComputeListDispatch(cl, 1, 1, 1);
    }

    private void DispatchStamp(long cl, Rid pipe, Rid s0, Rid s1, Rid s2, Rid s3, byte[] stampPc)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        _rd.ComputeListBindUniformSet(cl, s0, 0);
        _rd.ComputeListBindUniformSet(cl, s1, 1);
        _rd.ComputeListBindUniformSet(cl, s2, 2);
        _rd.ComputeListBindUniformSet(cl, s3, 3);
        _rd.ComputeListSetPushConstant(cl, stampPc, (uint)stampPc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    private void DispatchOp2(long cl, Rid pipe, Rid s2, Rid s3, byte[] pushC)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        _rd.ComputeListBindUniformSet(cl, s2, 2);
        _rd.ComputeListBindUniformSet(cl, s3, 3);
        _rd.ComputeListSetPushConstant(cl, pushC, (uint)pushC.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    private byte[] Pc16(uint u2, uint u3)
    {
        var b = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.X), 0, b, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.Y), 0, b, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(u2), 0, b, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(u3), 0, b, 12, 4);
        return b;
    }

    private static byte[] PcCalc(uint op)
    {
        var b = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(op), 0, b, 0, 4);
        return b;
    }

    public void Free()
    {
        Ready = false;
        _resReady = false;
        foreach (var r in new[]
        {
            _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B,
            _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo,
            _resH0, _resH1, _resX2, _resR3, _spmvP2, _spmvAp3,
            _dotR2, _dotR3, _dotP2, _dotAp3, _dotSc5, _calcSc5,
            _saxHc2, _saxX3, _saxR2, _saxP3, _saxP2, _saxAp2, _saxR3, _saxSc5,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        foreach (var buf in new[] { _ssbo, _cgScalars })
        {
            if (buf.IsValid) { _rd.FreeRid(buf); }
        }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB, _cgX, _cgR, _cgP, _cgAp })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _shader, _shaderRed, _shaderBlack, _resShader, _cgRes, _cgSpmv, _cgDot, _cgCalc, _cgSaxpy })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private Rid Compile(string src, string tag)
    {
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[GpuStampSolver] {tag} compile error:\n{err}\n--- source ---\n{src}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[GpuStampSolver] could not read {path}"); }
        return s;
    }

    private Rid MakeTex(RDTextureFormat tf)
    {
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private float ReadSsbo()
    {
        byte[] data = _rd.BufferGetData(_ssbo);
        int n = (int)_numWg;
        var f = new float[n];
        Buffer.BlockCopy(data, 0, f, 0, n * 4);
        float s = 0f;
        for (int i = 0; i < n; i++) { s += f[i]; }
        return s;
    }

    private Rid MakeImageSet(Rid tex, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }

    private Rid MakeSsboSet(Rid buf, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(buf);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }
}
