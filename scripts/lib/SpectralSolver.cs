using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Spectral solver — DCT diagonalization (solver-ledger.md §7c).
//
// Not an iterative method. The constant-coefficient operator is diagonal in the cosine
// basis, so the entire solve is: transform the rhs, divide each mode by its eigenvalue,
// transform back. Six dispatches, fixed, regardless of grid size or "iterations". It is
// exact — the residual should sit at float epsilon, not merely small — and that is the
// datapoint it exists to contribute: the floor that every iterative solver is chasing.
//
// DCT, NOT DST. The ledger sketch said "DST/DCT"; the boundary condition settles it. Every
// solve body uses clamp() for neighbours, i.e. zero flux = NEUMANN, whose eigenvectors are
// the DCT-II basis. DST diagonalizes the Dirichlet operator and would be solving a tank
// with different walls. See dct_1d.glslinc.
//
// WHERE IT IS EXACT: bathymetry coupling (extra.z) = 0 and sponge_a = 0. Both make the
// coefficients spatially varying, which is exactly what a single global transform cannot
// represent. Step() warns once (deduped) when either is violated rather than silently
// returning a plausible wrong field. Outside those conditions the transform is still a
// good approximation, and its real long-term use is as a preconditioner inside CG.
//
// PERFORMANCE: dct_1d is the direct O(N) -per-output form, so each axis pass is O(N^2).
// Deliberate for v1 — correctness first, and the O(N log N) version is a port of a working
// butterfly chain (../water-kit/kit/waves/, solver-ledger.md §7e), not new research.
public sealed class SpectralSolver : IStampSolver
{
    // The basis IS the boundary condition (see dct_1d.glslinc):
    //   Cosine — Neumann walls, which is what clamp() gives, so it matches THIS stamp exactly.
    //   Sine   — Dirichlet (clamped) walls. Wrong BC for the wave tank, on purpose: it is the
    //            exact basis for a clamped plate (scene 06), and a usable approximate inverse
    //            for CG preconditioning where matching the walls does not matter.
    public enum Basis { Cosine, Sine }

    private readonly Basis _basis;
    // stamp push-constant field offsets
    private const int OffBeta = 8, OffA = 12, OffLeak = 16, OffCn = 20, OffSpongeA = 28;
    private const int OffExtraY = 116, OffExtraZ = 120, OffExtraW = 124;

    private const string ResidualHeader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n";

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _gx, _gy, _numWg;

    private Rid _shRhs, _shDct, _shSc, _shRes;
    private Rid _pRhs, _pDct, _pSc, _pRes;

    private Rid _hCurr, _hPrev, _b, _t1, _t2, _x, _ssbo;

    private Rid _rhsH0, _rhsH1, _rhsB3;
    private Rid _dB2, _dT1_2, _dT1_3, _dT2_2, _dT2_3, _dX3;
    private Rid _scT2_2, _scT1_3;
    private Rid _resH0, _resH1, _resX2, _resSsbo;
    private bool _resReady;

    private bool _warned;

    public bool Ready { get; private set; }
    public string ModeName => _basis == Basis.Sine ? "SpectralDST" : "SpectralDCT";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }

    public SpectralSolver(RenderingDevice rd, Vector2I grid, string stampPath, Basis basis = Basis.Cosine)
    {
        _rd = rd;
        _grid = grid;
        _basis = basis;
        _gx = (uint)((grid.X - 1) / 8 + 1);
        _gy = (uint)((grid.Y - 1) / 8 + 1);
        _numWg = _gx * _gy;

        // Topology block ahead of the stamp (solver-ledger.md §7d). The DCT/DST passes
        // themselves are still explicitly 2-axis; a 3D transform is three passes, not two.
        string stamp = ReadRes(GpuStampSolver.Nd2DPath) + "\n" + ReadRes(stampPath);
        const string wg = "#version 450\nlayout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n";

        _shRhs = Compile(wg + "layout(r32f, set = 3, binding = 0) uniform image2D b0;\n"
            + stamp + "\n" + ReadRes("res://shaders/stamp/mg_rhs.glslinc"), "spectral-rhs");
        _shDct = Compile(ReadRes("res://shaders/stamp/dct_1d.glslinc"), "spectral-dct");     // standalone
        _shSc = Compile(ReadRes("res://shaders/stamp/dct_scale.glslinc"), "spectral-scale"); // standalone
        if (!_shRhs.IsValid || !_shDct.IsValid || !_shSc.IsValid) { return; }
        _pRhs = _rd.ComputePipelineCreate(_shRhs);
        _pDct = _rd.ComputePipelineCreate(_shDct);
        _pSc = _rd.ComputePipelineCreate(_shSc);

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
        _hCurr = Tex(tf); _hPrev = Tex(tf); _b = Tex(tf); _t1 = Tex(tf); _t2 = Tex(tf); _x = Tex(tf);
        _ssbo = _rd.StorageBufferCreate(_numWg * 4u);

        _rhsH0 = Img(_hCurr, 0, _shRhs); _rhsH1 = Img(_hPrev, 1, _shRhs); _rhsB3 = Img(_b, 3, _shRhs);

        _dB2 = Img(_b, 2, _shDct);
        _dT1_2 = Img(_t1, 2, _shDct); _dT1_3 = Img(_t1, 3, _shDct);
        _dT2_2 = Img(_t2, 2, _shDct); _dT2_3 = Img(_t2, 3, _shDct);
        _dX3 = Img(_x, 3, _shDct);

        _scT2_2 = Img(_t2, 2, _shSc); _scT1_3 = Img(_t1, 3, _shSc);

        // Residual is measured against the STAMP operator, not against the transform. That is
        // the point: if bathymetry is on, the spectral answer is wrong and this number is what
        // says so out loud instead of the field merely looking a bit off.
        _shRes = Compile(ResidualHeader + stamp + "\n" + ReadRes("res://shaders/stamp/reduce_residual.glslinc"), "spectral-residual");
        if (_shRes.IsValid)
        {
            _pRes = _rd.ComputePipelineCreate(_shRes);
            _resH0 = Img(_hCurr, 0, _shRes); _resH1 = Img(_hPrev, 1, _shRes);
            _resX2 = Img(_x, 2, _shRes); _resSsbo = Ssbo(_ssbo, 4, _shRes);
            _resReady = true;
        }

        Ready = true;
        string kind = _basis == Basis.Sine ? "DST-II/III (Dirichlet walls)" : "DCT-II/III (Neumann walls)";
        GD.Print($"[SpectralSolver] {kind} {grid.X}x{grid.Y}, 6 dispatches/step, residual={_resReady}");
        if (_basis == Basis.Sine)
        {
            GD.PushWarning("[SpectralSolver] SINE basis assumes Dirichlet walls, but the solve bodies " +
                           "use clamp() = Neumann. Expect a boundary-layer error at every wall and a " +
                           "residual well above the cosine basis. This mode is for CG preconditioning " +
                           "and for clamped stamps (scene 06), not for solving the tank.");
        }
    }

    // Fixed cost — no iteration. The `iters` slider does nothing here, which is itself the
    // headline result and the reason PassesPerStep must not pretend otherwise.
    public int PassesPerStep(int iters) => 6;

    public void Step(byte[] pc, int iters, bool measure)
    {
        if (!Ready) { return; }

        float betaScale = BitConverter.ToSingle(pc, OffBeta);
        float a = BitConverter.ToSingle(pc, OffA);
        float leak = BitConverter.ToSingle(pc, OffLeak);
        float cn = BitConverter.ToSingle(pc, OffCn);
        float spongeA = pc.Length > OffSpongeA ? BitConverter.ToSingle(pc, OffSpongeA) : 0f;
        float bathy = pc.Length > OffExtraZ ? BitConverter.ToSingle(pc, OffExtraZ) : 0f;
        float refDepth = pc.Length > OffExtraW ? BitConverter.ToSingle(pc, OffExtraW) : 1f;
        int dcMode = pc.Length > OffExtraY ? ((int)(BitConverter.ToSingle(pc, OffExtraY) + 0.5f) >> 4) : 0;

        if (!_warned && (bathy != 0f || spongeA != 0f))
        {
            _warned = true;
            GD.PushWarning($"[SpectralSolver] operator is not constant-coefficient " +
                           $"(bathymetry={bathy}, sponge_a={spongeA}) — the DCT no longer diagonalizes it, " +
                           $"so this solve is APPROXIMATE. Watch the residual, not the picture.");
        }

        float aMu = 1f - 0.5f * cn;
        float effLeak = dcMode == 3 ? 0f : leak;   // mode 3 reuses the leak slot (stamp does the same)
        float g = aMu * betaScale * refDepth;
        float baseDiag = 1f + a + aMu * effLeak;

        long cl = _rd.ComputeListBegin();

        _rd.ComputeListBindComputePipeline(cl, _pRhs);
        Bind(cl, _rhsH0, 0); Bind(cl, _rhsH1, 1); Bind(cl, _rhsB3, 3);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
        _rd.ComputeListAddBarrier(cl);

        Dct(cl, _dB2, _dT1_3, 0, false);     // b   -> t1   forward along x
        _rd.ComputeListAddBarrier(cl);
        Dct(cl, _dT1_2, _dT2_3, 1, false);   // t1  -> t2   forward along y
        _rd.ComputeListAddBarrier(cl);

        _rd.ComputeListBindComputePipeline(cl, _pSc);
        Bind(cl, _scT2_2, 2); Bind(cl, _scT1_3, 3);
        _rd.ComputeListSetPushConstant(cl, PcScale(baseDiag, g), 32);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
        _rd.ComputeListAddBarrier(cl);

        Dct(cl, _dT1_2, _dT2_3, 1, true);    // t1  -> t2   inverse along y
        _rd.ComputeListAddBarrier(cl);
        Dct(cl, _dT2_2, _dX3, 0, true);      // t2  -> x    inverse along x
        _rd.ComputeListAddBarrier(cl);

        bool doRes = measure && _resReady;
        if (doRes)
        {
            _rd.ComputeListBindComputePipeline(cl, _pRes);
            Bind(cl, _resH0, 0); Bind(cl, _resH1, 1); Bind(cl, _resX2, 2); Bind(cl, _resSsbo, 4);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _gx, _gy, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (doRes) { LastResidual = Mathf.Sqrt(ReadSsbo()); }

        var size = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(_x, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    private void Dct(long cl, Rid s2, Rid s3, uint axis, bool inverse)
    {
        _rd.ComputeListBindComputePipeline(cl, _pDct);
        Bind(cl, s2, 2); Bind(cl, s3, 3);
        _rd.ComputeListSetPushConstant(cl, PcDct(axis, inverse), 16);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    private void Bind(long cl, Rid set, int idx) => _rd.ComputeListBindUniformSet(cl, set, (uint)idx);

    private byte[] PcDct(uint axis, bool inverse)
    {
        uint flags = (inverse ? 1u : 0u) | (_basis == Basis.Sine ? 2u : 0u);
        var b = new byte[16];
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.X), 0, b, 0, 4);
        Buffer.BlockCopy(BitConverter.GetBytes((float)_grid.Y), 0, b, 4, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(axis), 0, b, 8, 4);
        Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, b, 12, 4);
        return b;
    }

    private byte[] PcScale(float baseDiag, float g)
    {
        var b = new byte[32];
        float[] v = { _grid.X, _grid.Y, baseDiag, g };
        Buffer.BlockCopy(v, 0, b, 0, 16);
        Buffer.BlockCopy(BitConverter.GetBytes(_basis == Basis.Sine ? 1u : 0u), 0, b, 16, 4);
        return b;
    }

    public void Free()
    {
        Ready = false;
        _resReady = false;
        foreach (var r in new[]
        {
            _rhsH0, _rhsH1, _rhsB3, _dB2, _dT1_2, _dT1_3, _dT2_2, _dT2_3, _dX3,
            _scT2_2, _scT1_3, _resH0, _resH1, _resX2, _resSsbo,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        if (_ssbo.IsValid) { _rd.FreeRid(_ssbo); }
        foreach (var t in new[] { _hCurr, _hPrev, _b, _t1, _t2, _x })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _shRhs, _shDct, _shSc, _shRes })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──
    private Rid Compile(string src, string tag)
    {
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[SpectralSolver] {tag} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[SpectralSolver] could not read {path}"); }
        return s;
    }

    private Rid Tex(RDTextureFormat tf)
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
}
