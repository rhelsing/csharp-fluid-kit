using Godot;

namespace GodotCsharpExperiments.Lib;

// Alternating-Direction Implicit line solver for the same stamp GpuStampSolver drives.
//
// Why it exists: Jacobi/RBGS are smoothers. Their iteration eigenvalue for a mode of grid
// angle θ is λ = 2β(cos θx + cos θy)/(1+4β), which tends to 4β/(1+4β) ≈ 1 as θ → 0 — the
// LONG waves never converge, and because the iterate is seeded from h_curr, anything that
// does not converge does not move. Result on screen: ripples propagate, swell dies.
//
// ADI solves each LINE exactly (tridiagonal / Thomas) with the cross-axis neighbours held at
// the current iterate. One pass carries information across the whole row, so the smooth modes
// along that axis are solved rather than smoothed. Alternate x-lines and z-lines per iteration.
//
// Layout: one invocation per LINE (not per cell). Thomas needs O(N) scratch, so c' and d' live
// in two r32f scratch images. Threads on adjacent lines touch adjacent texels at the same
// index, so the walk down a line stays coalesced across the workgroup.
public sealed class AdiStampSolver : IStampSolver
{
    private const string HeaderBody =
        "layout(local_size_x = 64, local_size_y = 1, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image2D iter_out;\n";

    private const string AdiBodyPath = "res://shaders/stamp/solve_adi.glslinc";

    // Residual pipeline, ported from GpuStampSolver so ADI can be RANKED rather than eyeballed.
    // Its own 8x8 header (reduce_residual assumes 64 lanes) — separate shader from the line solve.
    private const string ResidualHeader =
        "#version 450\n" +
        "layout(local_size_x = 8, local_size_y = 8, local_size_z = 1) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image2D iter_in;\n";
    private const string ReduceBodyPath = "res://shaders/stamp/reduce_residual.glslinc";

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _lgx, _lgy;

    private Rid _shX, _shZ, _pipeX, _pipeZ;
    private Rid _hCurr, _hPrev, _iterA, _iterB, _cp, _dp;
    private Rid _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B, _setCp, _setDp;
    private Rid _resShader, _resPipeline, _ssbo, _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo;
    private readonly uint _rgx, _rgy, _numWg;
    private bool _resReady;

    public bool Ready { get; private set; }
    public Rid HeightRid => _hCurr;
    public Rid PrevRid => _hPrev;
    public float LastResidual { get; private set; }
    public string ModeName => "ADI";

    public AdiStampSolver(RenderingDevice rd, Vector2I grid, string stampPath)
    {
        _rd = rd;
        _grid = grid;
        // ONE thread per line, packed 64 to a workgroup. Indexing both axes off .x means no
        // thread duplicates another's line — the first cut ran every line 8x over.
        _lgx = (uint)((grid.Y - 1) / 64 + 1);   // x-pass: one thread per ROW
        _lgy = (uint)((grid.X - 1) / 64 + 1);   // z-pass: one thread per COLUMN
        _rgx = (uint)((grid.X - 1) / 8 + 1);    // residual pass is a normal 8x8 grid walk
        _rgy = (uint)((grid.Y - 1) / 8 + 1);
        _numWg = _rgx * _rgy;

        string stamp = FileAccess.GetFileAsString(stampPath);
        string body = FileAccess.GetFileAsString(AdiBodyPath);
        if (string.IsNullOrEmpty(stamp) || string.IsNullOrEmpty(body))
        {
            GD.PushError("[AdiStampSolver] could not read stamp or solve body");
            return;
        }
        _shX = Compile("#version 450\n#define AXIS 0\n" + HeaderBody + stamp + "\n" + body, "adi-x");
        _shZ = Compile("#version 450\n#define AXIS 1\n" + HeaderBody + stamp + "\n" + body, "adi-z");
        if (!_shX.IsValid || !_shZ.IsValid) { return; }
        _pipeX = _rd.ComputePipelineCreate(_shX);
        _pipeZ = _rd.ComputePipelineCreate(_shZ);

        _hCurr = Tex(); _hPrev = Tex(); _iterA = Tex(); _iterB = Tex(); _cp = Tex(); _dp = Tex();
        _setHCurr = Set(_shX, _hCurr, 0);
        _setHPrev = Set(_shX, _hPrev, 1);
        _set2Curr = Set(_shX, _hCurr, 2);
        _set2A = Set(_shX, _iterA, 2);
        _set2B = Set(_shX, _iterB, 2);
        _set3A = Set(_shX, _iterA, 3);
        _set3B = Set(_shX, _iterB, 3);
        _setCp = Set(_shX, _cp, 4);
        _setDp = Set(_shX, _dp, 5);
        // ---- residual ----
        string reduce = FileAccess.GetFileAsString(ReduceBodyPath);
        if (!string.IsNullOrEmpty(reduce))
        {
            _resShader = Compile(ResidualHeader + stamp + "\n" + reduce, "adi-residual");
            if (_resShader.IsValid)
            {
                _resPipeline = _rd.ComputePipelineCreate(_resShader);
                _ssbo = _rd.StorageBufferCreate(_numWg * 4u);
                _resHCurr = Set(_resShader, _hCurr, 0);
                _resHPrev = Set(_resShader, _hPrev, 1);
                _res2A = Set(_resShader, _iterA, 2);
                _res2B = Set(_resShader, _iterB, 2);
                var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
                u.AddId(_ssbo);
                _setSsbo = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _resShader, 4);
                _resReady = true;
            }
        }

        Ready = true;
        GD.Print($"[AdiStampSolver] ok ({grid.X}x{grid.Y}) residual={_resReady}");
    }

    public int PassesPerStep(int iters) => 2 * Mathf.Max(1, iters);   // x-line + z-line

    // iters = full ADI iterations; each is an x-line pass then a z-line pass.
    public void Step(byte[] pc, int iters, bool measureResidual = false)
    {
        if (!Ready) { return; }
        int passes = 2 * Mathf.Max(1, iters);

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindUniformSet(cl, _setHCurr, 0);
        _rd.ComputeListBindUniformSet(cl, _setHPrev, 1);
        _rd.ComputeListBindUniformSet(cl, _setCp, 4);
        _rd.ComputeListBindUniformSet(cl, _setDp, 5);
        for (int p = 0; p < passes; p++)
        {
            _rd.ComputeListBindComputePipeline(cl, p % 2 == 0 ? _pipeX : _pipeZ);
            Rid inSet = p == 0 ? _set2Curr : (p % 2 == 1 ? _set2A : _set2B);
            Rid outSet = p % 2 == 0 ? _set3A : _set3B;
            _rd.ComputeListBindUniformSet(cl, inSet, 2);
            _rd.ComputeListBindUniformSet(cl, outSet, 3);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, p % 2 == 0 ? _lgx : _lgy, 1, 1);
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
            _rd.ComputeListDispatch(cl, _rgx, _rgy, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        if (doRes)
        {
            byte[] data = _rd.BufferGetData(_ssbo);
            int n = (int)_numWg;
            var f = new float[n];
            System.Buffer.BlockCopy(data, 0, f, 0, n * 4);
            float sum = 0f;
            for (int i = 0; i < n; i++) { sum += f[i]; }
            LastResidual = Mathf.Sqrt(sum);
        }

        Rid result = (passes - 1) % 2 == 0 ? _iterA : _iterB;
        var size = new Vector3(_grid.X, _grid.Y, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    public void Free()
    {
        Ready = false;
        _resReady = false;
        // ORDER MATTERS: uniform sets first. Freeing a texture auto-frees the sets that
        // reference it, so freeing textures early makes the later set-frees invalid.
        foreach (var r in new[]
        {
            _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B, _setCp, _setDp,
            _resHCurr, _resHPrev, _res2A, _res2B, _setSsbo,
        })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        if (_ssbo.IsValid) { _rd.FreeRid(_ssbo); }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB, _cp, _dp })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _shX, _shZ, _resShader })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    private Rid Compile(string src, string tag)
    {
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[AdiStampSolver] {tag} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private Rid Tex()
    {
        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)_grid.X, Height = (uint)_grid.Y, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private Rid Set(Rid shader, Rid tex, int setIdx)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIdx);
    }
}
