using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// MNA implicit damped-wave heightfield sim (port of water-kit scene 26's inline solver) → a
// packed RGBA32F texture (R=height, B=normal.x, A=normal.z) for the ww_* raytraced render.
// mna_wave.glsl runs K Jacobi sweeps of the implicit stamped system; mna_pack_normal.glsl
// converts the result into the water_tex layout. Backward-Euler ⇒ unconditionally stable.
// Call Step() from inside RenderingServer.CallOnRenderThread.
public sealed class MnaWaterSolver
{
    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly uint _g;

    private Rid _shader, _pipeline, _packShader, _packPipeline;
    private Rid _hCurr, _hPrev, _iterA, _iterB, _packed;
    private Rid _setHcurr, _setHprev, _set2Curr, _set2A, _set2B, _set3A, _set3B, _packInA, _packInB, _packOut;

    public bool Ready { get; private set; }
    public Rid PackedRid => _packed;   // the water_tex the ww_ render samples
    public int N => _n;

    public float HeightScale = 0.06f;
    public float NormalScale = 16.0f;   // keep normals near-vertical so refract() never hits TIR

    public MnaWaterSolver(RenderingDevice rd, int n)
    {
        _rd = rd;
        _n = n;
        _g = (uint)((n - 1) / 8 + 1);

        _shader = Load("mna_wave");
        _packShader = Load("mna_pack_normal");
        if (!_shader.IsValid || !_packShader.IsValid) { GD.PushError("[MnaWaterSolver] shader load failed"); return; }
        _pipeline = _rd.ComputePipelineCreate(_shader);
        _packPipeline = _rd.ComputePipelineCreate(_packShader);

        var tf = Fmt(RenderingDevice.DataFormat.R32Sfloat);
        _hCurr = MakeTex(tf); _hPrev = MakeTex(tf); _iterA = MakeTex(tf); _iterB = MakeTex(tf);
        _packed = MakeTex(Fmt(RenderingDevice.DataFormat.R32G32B32A32Sfloat));

        _setHcurr = Set(_shader, _hCurr, 0);
        _setHprev = Set(_shader, _hPrev, 1);
        _set2Curr = Set(_shader, _hCurr, 2);
        _set2A = Set(_shader, _iterA, 2);
        _set2B = Set(_shader, _iterB, 2);
        _set3A = Set(_shader, _iterA, 3);
        _set3B = Set(_shader, _iterB, 3);
        _packInA = Set(_packShader, _iterA, 0);
        _packInB = Set(_packShader, _iterB, 0);
        _packOut = Set(_packShader, _packed, 1);
        Ready = true;
        GD.Print($"[MnaWaterSolver] ok ({n}x{n})");
    }

    // poke = (cellX, cellY, radius, strength); beta = dt²c²; a = damping·dt·0.5.
    public void Step(Vector4 poke, float beta, float a, int iters)
    {
        if (!Ready) { return; }
        byte[] wavePc = Pc(_n, _n, beta, a, poke.X, poke.Y, poke.Z, poke.W);
        byte[] packPc = Pc(_n, _n, HeightScale, NormalScale);

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        _rd.ComputeListBindUniformSet(cl, _setHcurr, 0);
        _rd.ComputeListBindUniformSet(cl, _setHprev, 1);
        for (int i = 0; i < iters; i++)
        {
            Rid inSet = i == 0 ? _set2Curr : ((i % 2 == 1) ? _set2A : _set2B);
            Rid outSet = (i % 2 == 0) ? _set3A : _set3B;
            _rd.ComputeListBindUniformSet(cl, inSet, 2);
            _rd.ComputeListBindUniformSet(cl, outSet, 3);
            _rd.ComputeListSetPushConstant(cl, wavePc, (uint)wavePc.Length);
            _rd.ComputeListDispatch(cl, _g, _g, 1);
            _rd.ComputeListAddBarrier(cl);
        }
        bool resultIsA = ((iters - 1) % 2 == 0);
        _rd.ComputeListBindComputePipeline(cl, _packPipeline);
        _rd.ComputeListBindUniformSet(cl, resultIsA ? _packInA : _packInB, 0);
        _rd.ComputeListBindUniformSet(cl, _packOut, 1);
        _rd.ComputeListSetPushConstant(cl, packPc, (uint)packPc.Length);
        _rd.ComputeListDispatch(cl, _g, _g, 1);
        _rd.ComputeListEnd();

        Rid result = resultIsA ? _iterA : _iterB;
        var sz = new Vector3(_n, _n, 1);
        _rd.TextureCopy(_hCurr, _hPrev, Vector3.Zero, Vector3.Zero, sz, 0, 0, 0, 0);   // h_prev <- h_curr
        _rd.TextureCopy(result, _hCurr, Vector3.Zero, Vector3.Zero, sz, 0, 0, 0, 0);   // h_curr <- h_next
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in new[] { _setHcurr, _setHprev, _set2Curr, _set2A, _set2B, _set3A, _set3B, _packInA, _packInB, _packOut }) { FreeIf(s); }
        foreach (var t in new[] { _hCurr, _hPrev, _iterA, _iterB, _packed }) { FreeIf(t); }
        FreeIf(_shader); FreeIf(_packShader);
    }

    private byte[] Pc(params float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private Rid Load(string pass)
    {
        var sf = GD.Load<RDShaderFile>($"res://shaders/{pass}.glsl");
        if (sf == null) { GD.PushError($"[MnaWaterSolver] missing {pass}.glsl"); return default; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[MnaWaterSolver] {pass}: {err}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private RDTextureFormat Fmt(RenderingDevice.DataFormat fmt) => new()
    {
        Format = fmt,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = (uint)_n, Height = (uint)_n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
    };

    private Rid MakeTex(RDTextureFormat tf)
    {
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

    private void FreeIf(Rid r)
    {
        if (r.IsValid) { _rd.FreeRid(r); }
    }
}
