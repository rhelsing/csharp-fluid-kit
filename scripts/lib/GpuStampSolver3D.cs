using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// The matrix-free GPU stamp solver, lifted to a 3D grid. Same idea as GpuStampSolver:
// the PHYSICS is a swappable GLSL stamp (st_diag / st_conductance / st_rhs), here over a
// volume — image3D state, a 7-point stencil, dispatched over 4×4×4 workgroups. Exposes
// ReadField() so a CPU polygonizer (marching tets) can turn the volume into a rasterized
// isosurface. Call every method from inside RenderingServer.CallOnRenderThread.
//
// Solve modes, exactly as 2D:
//   - Jacobi — one relaxation sweep, ping-ponged K times.
//   - Rbgs   — Red-Black Gauss-Seidel, parity injected as a compile-time constant.
//   - Cg     — Conjugate Gradient, GPU-resident scalars, one barrier-chained compute list.
//
// NOTHING HERE IS A 3D FORK OF A SHADER. Both bodies are the SHARED solve_jacobi /
// solve_rbgs includes, compiled against nd_3d.glslinc instead of nd_2d — that one host
// argument is the entire dimension change (solver-ledger.md §7d). RBGS in particular is
// free because ST_PARITY sums every axis, so the checkerboard is still a true bipartite
// split of the 6-neighbour lattice; a 2D-style (x+y) parity would NOT two-colour it.
//
// It implements IStampSolver, so SolverBench measures it on the same terms as the 2D
// family: same residual definition (reduce_residual, which is already dimension-safe down
// to its workgroup index, and whose shared[64] matches 4×4×4 as exactly as it matched 8×8),
// same ms/step, same honest dispatch count.
public sealed class GpuStampSolver3D : IStampSolver
{
    public enum Mode { Jacobi, Rbgs, Cg }

    private const string HeaderBody =
        "layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image3D iter_in;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image3D iter_out;\n";

    private const string ResidualHeader =
        "#version 450\n" +
        "layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image3D iter_in;\n";

    // THE SHARED 2D BODIES. Not a typo: these files have no `ivec2` and no `offs[4]` in them
    // any more — they loop over ST_NB / ST_NEIGHBOUR, which nd_3d.glslinc defines as the
    // 6-neighbour volume (solver-ledger.md §7d). `stamp3d/solve_jacobi_3d.glslinc` was a
    // hand-copy of the first of these; it is deleted.
    private const string JacobiBodyPath = "res://shaders/stamp/solve_jacobi.glslinc";
    private const string RbgsBodyPath = "res://shaders/stamp/solve_rbgs.glslinc";
    private const string ReduceBodyPath = "res://shaders/stamp/reduce_residual.glslinc";
    private const string CgResPath = "res://shaders/stamp/cg_residual.glslinc";
    private const string CgSpmvPath = "res://shaders/stamp/cg_spmv.glslinc";
    private const string CgDotPartialPath = "res://shaders/stamp/cg_dot_partial.glslinc";
    private const string CgDotFinalPath = "res://shaders/stamp/cg_dot_final.glslinc";
    private const string CgCalcPath = "res://shaders/stamp/cg_calc.glslinc";
    private const string CgSaxpyPath = "res://shaders/stamp/cg_saxpybuf.glslinc";

    // CG in 3D was blocked until cg_saxpybuf and cg_dot_partial stopped carrying their own
    // `#version` / `image2D` / `vec2 size` — they never saw the nd macros, so they were the
    // one part of the family pinned to a plane. They are host-headered now, exactly like the
    // solve bodies, and these are the 3D headers. The 2D path supplies its own and is
    // byte-identical to before the lift (verified: CG's residual at 256² is unchanged to every
    // digit the bench prints).
    //
    // The push constant declares explicit padding to a round 32 B: `vec4 size` is 16-byte
    // aligned so the block would otherwise be 24 B, and supplying a size the shader did not
    // declare is exactly the "requires (N) supplied (M)" failure this contract exists to stop.
    private const string SaxpyHeader =
        "#version 450\n" +
        "layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image3D x_img;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image3D y_img;\n" +
        "layout(std430, set = 5, binding = 0) buffer Scalars { float sc[]; };\n" +
        "layout(push_constant, std430) uniform P { vec4 size; uint cy_slot; uint cx_slot; uint _p0; uint _p1; } pc;\n";

    private const string DotPartialHeader =
        "#version 450\n" +
        "layout(local_size_x = 256, local_size_y = 1, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image3D a_img;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image3D b_img;\n" +
        "layout(std430, set = 4, binding = 0) buffer Partials { float partial[]; };\n" +
        "layout(push_constant, std430) uniform P { vec4 size; uint slot; uint _p0; uint _p1; uint _p2; } pc;\n";

    // Stage-1 workgroups for the two-stage dot — same 256 as 2D.
    private const uint DotWg = 256;

    // CG scalar-buffer slots (identical to 2D; see GpuStampSolver for why STrueRes exists).
    private const uint SRsold = 0, SPap = 1, SAlpha = 2, SRsnew = 3, SBeta = 4, SNegAlpha = 5, SOne = 6, SZero = 7;
    private const uint STrueRes = 8;

    private readonly RenderingDevice _rd;
    private readonly Vector3I _grid;
    private readonly uint _gx, _gy, _gz, _numWg;
    private readonly Mode _mode;

    private Rid _shader, _pipeline;
    private Rid _shaderRed, _shaderBlack, _pipeRed, _pipeBlack;
    private Rid _hCurr, _hPrev, _iterA, _iterB;
    private Rid _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B;

    private Rid _resShader, _resPipeline, _ssbo;
    private Rid _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo;
    private bool _resReady;

    // Cg (GPU-resident scalars) — mirrors GpuStampSolver's layout exactly.
    private Rid _cgRes, _cgSpmv, _cgDot, _cgDotFinal, _cgCalc, _cgSaxpy;
    private Rid _cgResPipe, _cgSpmvPipe, _cgDotPipe, _cgDotFinalPipe, _cgCalcPipe, _cgSaxpyPipe;
    private Rid _cgX, _cgR, _cgP, _cgAp, _cgScalars, _cgPartials;
    private Rid _resH0, _resH1, _resX2, _resR3;
    private Rid _spmvH0, _spmvH1, _spmvP2, _spmvAp3;
    private Rid _dotR2, _dotR3, _dotP2, _dotAp3, _dotPart4, _finPart4, _finSc5, _calcSc5;
    private Rid _saxHc2, _saxX3, _saxR2, _saxP3, _saxP2, _saxAp2, _saxR3, _saxSc5;

    public bool Ready { get; private set; }
    public Mode SolveMode => _mode;
    public string ModeName => _mode + "3D";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }

    /// <summary>Kept for scene 08, which predates the IStampSolver surface.</summary>
    public Rid FieldRid => _hCurr;

    public GpuStampSolver3D(RenderingDevice rd, Vector3I grid, string stampPath, Mode mode = Mode.Jacobi)
    {
        _rd = rd;
        _grid = grid;
        _mode = mode;
        _gx = (uint)((grid.X - 1) / 4 + 1);
        _gy = (uint)((grid.Y - 1) / 4 + 1);
        _gz = (uint)((grid.Z - 1) / 4 + 1);
        _numWg = _gx * _gy * _gz;

        // Topology first, then the operator — same order as every 2D host, only the nd file
        // differs. That one argument IS the dimension change.
        string ndText = ReadRes(GpuStampSolver.Nd3DPath);
        string stampText = ndText + "\n" + ReadRes(stampPath);

        if (mode == Mode.Jacobi)
        {
            _shader = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(JacobiBodyPath), "jacobi3d");
            if (!_shader.IsValid) { return; }
            _pipeline = _rd.ComputePipelineCreate(_shader);
        }
        else if (mode == Mode.Rbgs)
        {
            string body = ReadRes(RbgsBodyPath);
            _shaderRed = Compile("#version 450\n#define PARITY 0\n" + HeaderBody + stampText + "\n" + body, "rbgs3d-red");
            _shaderBlack = Compile("#version 450\n#define PARITY 1\n" + HeaderBody + stampText + "\n" + body, "rbgs3d-black");
            if (!_shaderRed.IsValid || !_shaderBlack.IsValid) { return; }
            _pipeRed = _rd.ComputePipelineCreate(_shaderRed);
            _pipeBlack = _rd.ComputePipelineCreate(_shaderBlack);
        }
        else // Cg
        {
            _cgRes = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(CgResPath), "cg3d-res");
            _cgSpmv = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(CgSpmvPath), "cg3d-spmv");
            // header + nd + body: these two are elementwise and never touch the stamp, so they
            // take the nd block only for its ST_ macros.
            _cgDot = Compile(DotPartialHeader + ndText + "\n" + ReadRes(CgDotPartialPath), "cg3d-dot-partial");
            _cgSaxpy = Compile(SaxpyHeader + ndText + "\n" + ReadRes(CgSaxpyPath), "cg3d-saxpy");
            _cgDotFinal = Compile(ReadRes(CgDotFinalPath), "cg3d-dot-final");   // buffers only
            _cgCalc = Compile(ReadRes(CgCalcPath), "cg3d-calc");
            if (!_cgRes.IsValid || !_cgSpmv.IsValid || !_cgDot.IsValid || !_cgDotFinal.IsValid
                || !_cgCalc.IsValid || !_cgSaxpy.IsValid) { return; }
            _cgResPipe = _rd.ComputePipelineCreate(_cgRes);
            _cgSpmvPipe = _rd.ComputePipelineCreate(_cgSpmv);
            _cgDotPipe = _rd.ComputePipelineCreate(_cgDot);
            _cgDotFinalPipe = _rd.ComputePipelineCreate(_cgDotFinal);
            _cgCalcPipe = _rd.ComputePipelineCreate(_cgCalc);
            _cgSaxpyPipe = _rd.ComputePipelineCreate(_cgSaxpy);
        }

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type3D,
            Width = (uint)grid.X,
            Height = (uint)grid.Y,
            Depth = (uint)grid.Z,
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

            var scInit = new byte[64];
            float[] scVals = { 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f };   // ONE @6, ZERO @7
            Buffer.BlockCopy(scVals, 0, scInit, 0, 64);
            _cgScalars = _rd.StorageBufferCreate(64u, scInit);
            _cgPartials = _rd.StorageBufferCreate(DotWg * 4u);

            _resH0 = Set(_hCurr, 0, _cgRes);
            _resH1 = Set(_hPrev, 1, _cgRes);
            _resX2 = Set(_cgX, 2, _cgRes);
            _resR3 = Set(_cgR, 3, _cgRes);

            _spmvP2 = Set(_cgP, 2, _cgSpmv);
            _spmvAp3 = Set(_cgAp, 3, _cgSpmv);
            _spmvH0 = Set(_hCurr, 0, _cgSpmv);    // stamp declares these; must be bound
            _spmvH1 = Set(_hPrev, 1, _cgSpmv);

            _dotR2 = Set(_cgR, 2, _cgDot);
            _dotR3 = Set(_cgR, 3, _cgDot);
            _dotP2 = Set(_cgP, 2, _cgDot);
            _dotAp3 = Set(_cgAp, 3, _cgDot);
            _dotPart4 = SsboSet(_cgPartials, 4, _cgDot);
            _finPart4 = SsboSet(_cgPartials, 4, _cgDotFinal);
            _finSc5 = SsboSet(_cgScalars, 5, _cgDotFinal);
            _calcSc5 = SsboSet(_cgScalars, 5, _cgCalc);

            _saxHc2 = Set(_hCurr, 2, _cgSaxpy);
            _saxX3 = Set(_cgX, 3, _cgSaxpy);
            _saxR2 = Set(_cgR, 2, _cgSaxpy);
            _saxP3 = Set(_cgP, 3, _cgSaxpy);
            _saxP2 = Set(_cgP, 2, _cgSaxpy);
            _saxAp2 = Set(_cgAp, 2, _cgSaxpy);
            _saxR3 = Set(_cgR, 3, _cgSaxpy);
            _saxSc5 = SsboSet(_cgScalars, 5, _cgSaxpy);

            Ready = true;
            return;
        }

        Rid setShader = mode == Mode.Jacobi ? _shader : _shaderRed;
        _setHCurr = Set(_hCurr, 0, setShader);
        _setHPrev = Set(_hPrev, 1, setShader);
        _set2Curr = Set(_hCurr, 2, setShader);
        _set2A = Set(_iterA, 2, setShader);
        _set2B = Set(_iterB, 2, setShader);
        _set3A = Set(_iterA, 3, setShader);
        _set3B = Set(_iterB, 3, setShader);

        _resShader = Compile(ResidualHeader + stampText + "\n" + ReadRes(ReduceBodyPath), "residual3d");
        if (_resShader.IsValid)
        {
            _resPipeline = _rd.ComputePipelineCreate(_resShader);
            _resHCurr = Set(_hCurr, 0, _resShader);
            _resHPrev = Set(_hPrev, 1, _resShader);
            _res2A = Set(_iterA, 2, _resShader);
            _res2B = Set(_iterB, 2, _resShader);
            _setSsbo = SsboSet(_ssbo, 4, _resShader);
            _resReady = true;
        }

        Ready = true;
    }

    public int PassesPerStep(int iters) => _mode == Mode.Rbgs ? 2 * iters : iters;

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
            _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
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
            _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (doRes)
        {
            LastResidual = Mathf.Sqrt(ReadSsbo());
        }

        Rid result = (passes - 1) % 2 == 0 ? _iterA : _iterB;
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    // Conjugate Gradient — one barrier-chained compute list, scalars GPU-resident. Line for
    // line the 2D sequence; only the dispatch shape and the copy extent carry a z.
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

        if (measure)
        {
            // RECOMPUTE ||b − Ax|| from the converged X rather than reporting CG's recursive
            // residual, which drifts orders of magnitude below the true one and would make the
            // 3D column non-comparable the same way it did in 2D (solver-ledger.md §8a).
            DispatchStamp(cl, _cgResPipe, _resH0, _resH1, _resX2, _resR3, stampPc);
            _rd.ComputeListAddBarrier(cl);
            DotBuf(cl, _dotR2, _dotR3, STrueRes);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (measure)
        {
            byte[] d = _rd.BufferGetData(_cgScalars);
            LastResidual = Mathf.Sqrt(Mathf.Max(BitConverter.ToSingle(d, (int)STrueRes * 4), 0f));
        }

        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(_cgX, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    private void SaxpyBuf(long cl, Rid setX2, Rid setY3, uint cySlot, uint cxSlot)
    {
        _rd.ComputeListBindComputePipeline(cl, _cgSaxpyPipe);
        _rd.ComputeListBindUniformSet(cl, setX2, 2);
        _rd.ComputeListBindUniformSet(cl, setY3, 3);
        _rd.ComputeListBindUniformSet(cl, _saxSc5, 5);
        _rd.ComputeListSetPushConstant(cl, Pc32(cySlot, cxSlot), 32);
        _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
    }

    private void DotBuf(long cl, Rid a2, Rid b3, uint slot)
    {
        _rd.ComputeListBindComputePipeline(cl, _cgDotPipe);
        _rd.ComputeListBindUniformSet(cl, a2, 2);
        _rd.ComputeListBindUniformSet(cl, b3, 3);
        _rd.ComputeListBindUniformSet(cl, _dotPart4, 4);
        _rd.ComputeListSetPushConstant(cl, Pc32(slot, 0), 32);
        _rd.ComputeListDispatch(cl, DotWg, 1, 1);
        _rd.ComputeListAddBarrier(cl);

        _rd.ComputeListBindComputePipeline(cl, _cgDotFinalPipe);
        _rd.ComputeListBindUniformSet(cl, _finPart4, 4);
        _rd.ComputeListBindUniformSet(cl, _finSc5, 5);
        _rd.ComputeListSetPushConstant(cl, PcDotFinal(DotWg, slot), 16);
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
        _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
    }

    // vec4 size + two uint slots + two uint pad = 32 B, matching the declared block exactly.
    private byte[] Pc32(uint u0, uint u1)
    {
        var b = new byte[32];
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.X), 0, b, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.Y), 0, b, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.Z), 0, b, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(u0), 0, b, 16, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(u1), 0, b, 20, 4);
        return b;
    }

    private static byte[] PcDotFinal(uint count, uint slot)
    {
        var b = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(count), 0, b, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(slot), 0, b, 4, 4);
        return b;
    }

    private static byte[] PcCalc(uint op)
    {
        var b = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes(op), 0, b, 0, 4);
        return b;
    }

    // Full-volume readback (sync). Returns W*H*D floats in x-fastest, then y, then z order.
    public float[] ReadField()
    {
        if (!Ready) { return Array.Empty<float>(); }
        byte[] data = _rd.TextureGetData(_hCurr, 0);
        int n = _grid.X * _grid.Y * _grid.Z;
        var f = new float[n];
        Buffer.BlockCopy(data, 0, f, 0, Math.Min(data.Length, n * 4));
        return f;
    }

    public void Free()
    {
        Ready = false;
        _resReady = false;
        // Order matters: uniform sets before the textures/buffers they reference.
        foreach (var r in new[]
        {
            _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B,
            _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo,
            _resH0, _resH1, _resX2, _resR3, _spmvH0, _spmvH1, _spmvP2, _spmvAp3,
            _dotR2, _dotR3, _dotP2, _dotAp3, _dotPart4, _finPart4, _finSc5, _calcSc5,
            _saxHc2, _saxX3, _saxR2, _saxP3, _saxP2, _saxAp2, _saxR3, _saxSc5,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        foreach (var buf in new[] { _ssbo, _cgScalars, _cgPartials })
        {
            if (buf.IsValid) { _rd.FreeRid(buf); }
        }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB, _cgX, _cgR, _cgP, _cgAp })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[]
        {
            _shader, _shaderRed, _shaderBlack, _resShader,
            _cgRes, _cgSpmv, _cgDot, _cgDotFinal, _cgCalc, _cgSaxpy,
        })
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
            GD.PushError($"[GpuStampSolver3D] {tag} compile error:\n{err}\n--- source ---\n{src}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[GpuStampSolver3D] could not read {path}"); }
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

    private Rid Set(Rid tex, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }

    private Rid SsboSet(Rid buf, int setIndex, Rid shader)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(buf);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }
}
