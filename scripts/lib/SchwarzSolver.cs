using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Block-dense additive Schwarz (solver-ledger.md §7b).
//
// The grid is cut into TileW x TileH tiles. Each workgroup assembles its tile's dense
// submatrix in shared memory, LU-factors it, and solves it exactly; cells outside the tile
// enter the right-hand side, and the outer ping-pong sweep iterates on that coupling.
// Exact inside, relaxed between.
//
// The LU is a transliteration of mna::solve() from
//   ../neptunely_standalone/js/audio/cmajor/lib/mna-solver.cmajor
// see solve_schwarz.glslinc for the port notes and the two deliberate deviations.
//
// SIZING. mna-solver.cmajor uses MAX_DIM = 32, so 32 unknowns per tile — which is exactly
// what fits comfortably in shared memory as a dense matrix (32*32 floats = 4 KB against a
// typical 32 KB LDS budget, so occupancy is not shared-memory bound). 8x4 makes those 32
// cells a genuine 2D patch rather than a line: a line would just be ADI's Thomas sweep with
// extra steps, and the point is to prove the general dense block works.
//
// Cost per sweep is O(TileN^3) per tile versus Jacobi's O(1) per cell, so a Schwarz sweep
// is far more expensive than a Jacobi sweep. It is only worth it if it buys proportionally
// more convergence — which is precisely what PassesPerStep + the residual readout exist to
// measure, and deliberately not something to guess at before Phase 2.
public sealed class SchwarzSolver : IStampSolver
{
    public const int TileW = 8, TileH = 4;
    public const int TileN = TileW * TileH;   // must stay <= 32 (see LDS note above)

    private const string ResidualHeader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n";

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _tgx, _tgy;        // tile dispatch
    private readonly uint _rgx, _rgy, _numWg;   // residual dispatch (8x8)

    private Rid _shader, _pipeline;
    private Rid _resShader, _resPipeline, _ssbo;
    private Rid _hCurr, _hPrev, _iterA, _iterB;
    private Rid _setH0, _setH1, _set2Curr, _set2A, _set2B, _set3A, _set3B;
    private Rid _resH0, _resH1, _res2A, _res2B, _resSsbo;
    private bool _resReady;

    public bool Ready { get; private set; }
    public string ModeName => "Schwarz";
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }

    public SchwarzSolver(RenderingDevice rd, Vector2I grid, string stampPath)
    {
        _rd = rd;
        _grid = grid;
        _tgx = (uint)((grid.X - 1) / TileW + 1);
        _tgy = (uint)((grid.Y - 1) / TileH + 1);
        _rgx = (uint)((grid.X - 1) / 8 + 1);
        _rgy = (uint)((grid.Y - 1) / 8 + 1);
        _numWg = _rgx * _rgy;

        string stamp = ReadRes(stampPath);

        // One thread per cell in the tile; the LU indexes rows by gl_LocalInvocationIndex.
        string header =
            "#version 450\n" +
            $"layout(local_size_x = {TileN}, local_size_y = 1, local_size_z = 1) in;\n" +
            $"#define TILE_W {TileW}\n#define TILE_H {TileH}\n#define TILE_N {TileN}\n" +
            "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n" +
            "layout(r32f, set = 3, binding = 0) uniform image2D iter_out;\n";

        _shader = Compile(header + stamp + "\n" + ReadRes("res://shaders/stamp/solve_schwarz.glslinc"), "schwarz");
        if (!_shader.IsValid) { return; }
        _pipeline = _rd.ComputePipelineCreate(_shader);

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
        _hCurr = Tex(tf); _hPrev = Tex(tf); _iterA = Tex(tf); _iterB = Tex(tf);
        _ssbo = _rd.StorageBufferCreate(_numWg * 4u);

        _setH0 = Img(_hCurr, 0, _shader);
        _setH1 = Img(_hPrev, 1, _shader);
        _set2Curr = Img(_hCurr, 2, _shader);
        _set2A = Img(_iterA, 2, _shader);
        _set2B = Img(_iterB, 2, _shader);
        _set3A = Img(_iterA, 3, _shader);
        _set3B = Img(_iterB, 3, _shader);

        // Residual runs on the STAMP operator (reduce_residual.glslinc), not on the tile
        // decomposition — so it measures the real global system, and a Schwarz bug shows up
        // as a residual that stalls rather than one that looks fine locally.
        _resShader = Compile(ResidualHeader + stamp + "\n" + ReadRes("res://shaders/stamp/reduce_residual.glslinc"), "schwarz-residual");
        if (_resShader.IsValid)
        {
            _resPipeline = _rd.ComputePipelineCreate(_resShader);
            _resH0 = Img(_hCurr, 0, _resShader);
            _resH1 = Img(_hPrev, 1, _resShader);
            _res2A = Img(_iterA, 2, _resShader);
            _res2B = Img(_iterB, 2, _resShader);
            _resSsbo = Ssbo(_ssbo, 4, _resShader);
            _resReady = true;
        }

        Ready = true;
        GD.Print($"[SchwarzSolver] {TileW}x{TileH} tiles ({TileN} unknowns, {TileN * TileN * 4} B LDS), " +
                 $"{_tgx}x{_tgy} tiles, residual={_resReady}");
    }

    public int PassesPerStep(int iters) => iters;

    public void Step(byte[] pc, int iters, bool measure)
    {
        if (!Ready) { return; }

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        _rd.ComputeListBindUniformSet(cl, _setH0, 0);
        _rd.ComputeListBindUniformSet(cl, _setH1, 1);
        for (int p = 0; p < iters; p++)
        {
            Rid inSet = p == 0 ? _set2Curr : (p % 2 == 1 ? _set2A : _set2B);
            Rid outSet = p % 2 == 0 ? _set3A : _set3B;
            _rd.ComputeListBindComputePipeline(cl, _pipeline);
            _rd.ComputeListBindUniformSet(cl, inSet, 2);
            _rd.ComputeListBindUniformSet(cl, outSet, 3);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _tgx, _tgy, 1);
            _rd.ComputeListAddBarrier(cl);
        }

        bool doRes = measure && _resReady;
        if (doRes)
        {
            Rid resSet2 = (iters - 1) % 2 == 0 ? _res2A : _res2B;
            _rd.ComputeListBindComputePipeline(cl, _resPipeline);
            _rd.ComputeListBindUniformSet(cl, _resH0, 0);
            _rd.ComputeListBindUniformSet(cl, _resH1, 1);
            _rd.ComputeListBindUniformSet(cl, resSet2, 2);
            _rd.ComputeListBindUniformSet(cl, _resSsbo, 4);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _rgx, _rgy, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (doRes) { LastResidual = Mathf.Sqrt(ReadSsbo()); }

        Rid result = (iters - 1) % 2 == 0 ? _iterA : _iterB;
        var size = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    public void Free()
    {
        Ready = false;
        _resReady = false;
        foreach (var r in new[]
        {
            _setH0, _setH1, _set2Curr, _set2A, _set2B, _set3A, _set3B,
            _resH0, _resH1, _res2A, _res2B, _resSsbo,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        if (_ssbo.IsValid) { _rd.FreeRid(_ssbo); }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _shader, _resShader })
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
            GD.PushError($"[SchwarzSolver] {tag} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static string ReadRes(string path)
    {
        string s = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(s)) { GD.PushError($"[SchwarzSolver] could not read {path}"); }
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
