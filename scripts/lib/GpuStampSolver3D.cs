using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// The matrix-free GPU stamp solver, lifted to a 3D grid. Same idea as GpuStampSolver:
// the PHYSICS is a swappable GLSL stamp (st_diag / st_conductance / st_rhs), here over a
// volume — image3D state, a 7-point stencil, dispatched over 4×4×4 workgroups. Jacobi
// relaxation, ping-ponged K sweeps/tick. Exposes ReadField() so a CPU polygonizer
// (marching tets) can turn the volume into a rasterized isosurface. Call every method
// from inside RenderingServer.CallOnRenderThread.
public sealed class GpuStampSolver3D
{
    private const string HeaderBody =
        "layout(local_size_x = 4, local_size_y = 4, local_size_z = 4) in;\n" +
        "layout(r32f, set = 2, binding = 0) uniform image3D iter_in;\n" +
        "layout(r32f, set = 3, binding = 0) uniform image3D iter_out;\n";
    private const string JacobiBodyPath = "res://shaders/stamp3d/solve_jacobi_3d.glslinc";

    private readonly RenderingDevice _rd;
    private readonly Vector3I _grid;
    private readonly uint _gx, _gy, _gz;

    private Rid _shader, _pipeline;
    private Rid _hCurr, _hPrev, _iterA, _iterB;
    private Rid _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B;

    public bool Ready { get; private set; }
    public Rid FieldRid => _hCurr;

    public GpuStampSolver3D(RenderingDevice rd, Vector3I grid, string stampPath)
    {
        _rd = rd;
        _grid = grid;
        _gx = (uint)((grid.X - 1) / 4 + 1);
        _gy = (uint)((grid.Y - 1) / 4 + 1);
        _gz = (uint)((grid.Z - 1) / 4 + 1);

        string stampText = ReadRes(stampPath);
        _shader = Compile("#version 450\n" + HeaderBody + stampText + "\n" + ReadRes(JacobiBodyPath), "jacobi3d");
        if (!_shader.IsValid) { return; }
        _pipeline = _rd.ComputePipelineCreate(_shader);

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

        _setHCurr = Set(_hCurr, 0);
        _setHPrev = Set(_hPrev, 1);
        _set2Curr = Set(_hCurr, 2);
        _set2A = Set(_iterA, 2);
        _set2B = Set(_iterB, 2);
        _set3A = Set(_iterA, 3);
        _set3B = Set(_iterB, 3);
        Ready = true;
    }

    public void Step(byte[] pc, int iters)
    {
        if (!Ready) { return; }

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindUniformSet(cl, _setHCurr, 0);
        _rd.ComputeListBindUniformSet(cl, _setHPrev, 1);
        for (int p = 0; p < iters; p++)
        {
            _rd.ComputeListBindComputePipeline(cl, _pipeline);
            Rid inSet = p == 0 ? _set2Curr : (p % 2 == 1 ? _set2A : _set2B);
            Rid outSet = p % 2 == 0 ? _set3A : _set3B;
            _rd.ComputeListBindUniformSet(cl, inSet, 2);
            _rd.ComputeListBindUniformSet(cl, outSet, 3);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        Rid result = (iters - 1) % 2 == 0 ? _iterA : _iterB;
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
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
        foreach (var r in new[] { _setHCurr, _setHPrev, _set2Curr, _set2A, _set2B, _set3A, _set3B })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        if (_shader.IsValid) { _rd.FreeRid(_shader); }
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

    private Rid Set(Rid tex, int setIndex)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, (uint)setIndex);
    }
}
