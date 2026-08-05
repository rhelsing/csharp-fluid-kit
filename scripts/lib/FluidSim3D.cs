using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Stam stable-fluids in 3D — FluidSim lifted to a volume. rgba32f velocity + r32f dye,
// pressure, divergence (all image3D), 4×4×4 dispatch. The pressure-projection step is the
// SAME matrix-free Jacobi relaxation the stamp solvers use, now solving ∇²p = div on a
// 7-point 3D stencil. Shared by scene 09 (particles, via ReadVelocity) and scene 10 (fog,
// via DensityRid). Call every method from inside RenderingServer.CallOnRenderThread.
public sealed class FluidSim3D
{
    private const string Dir = "res://shaders/fluid3d/";

    private readonly RenderingDevice _rd;
    private readonly Vector3I _grid;
    private readonly uint _gx, _gy, _gz;

    private Rid _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD;
    private Rid _pAdd, _pAdvV, _pDiv, _pJac, _pGrad, _pAdvD;
    private Rid _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div;

    private Rid _add0, _add1, _advV0, _advV1, _div0, _div1;
    private Rid _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2;
    private Rid _grad0, _grad1, _advD0, _advD1, _advD2;

    public bool Ready { get; private set; }
    public Rid DensityRid => _dyeA;
    public Rid VelocityRid => _velA;

    public FluidSim3D(RenderingDevice rd, Vector3I grid)
    {
        _rd = rd;
        _grid = grid;
        _gx = (uint)((grid.X - 1) / 4 + 1);
        _gy = (uint)((grid.Y - 1) / 4 + 1);
        _gz = (uint)((grid.Z - 1) / 4 + 1);

        _sAdd = Compile("f3_add_source"); _sAdvV = Compile("f3_advect_vel");
        _sDiv = Compile("f3_divergence"); _sJac = Compile("f3_pressure_jacobi");
        _sGrad = Compile("f3_gradient_sub"); _sAdvD = Compile("f3_advect_dye");
        if (!(_sAdd.IsValid && _sAdvV.IsValid && _sDiv.IsValid && _sJac.IsValid && _sGrad.IsValid && _sAdvD.IsValid))
        {
            return;
        }
        _pAdd = _rd.ComputePipelineCreate(_sAdd); _pAdvV = _rd.ComputePipelineCreate(_sAdvV);
        _pDiv = _rd.ComputePipelineCreate(_sDiv); _pJac = _rd.ComputePipelineCreate(_sJac);
        _pGrad = _rd.ComputePipelineCreate(_sGrad); _pAdvD = _rd.ComputePipelineCreate(_sAdvD);

        var rgba = Fmt(RenderingDevice.DataFormat.R32G32B32A32Sfloat);
        var r = Fmt(RenderingDevice.DataFormat.R32Sfloat);
        _velA = MakeTex(rgba); _velB = MakeTex(rgba);
        _dyeA = MakeTex(r); _dyeB = MakeTex(r); _pA = MakeTex(r); _pB = MakeTex(r); _div = MakeTex(r);

        _add0 = Set(_velA, _sAdd, 0); _add1 = Set(_dyeA, _sAdd, 1);
        _advV0 = Set(_velA, _sAdvV, 0); _advV1 = Set(_velB, _sAdvV, 1);
        _div0 = Set(_velA, _sDiv, 0); _div1 = Set(_div, _sDiv, 1);
        _jacAB0 = Set(_pA, _sJac, 0); _jacAB1 = Set(_div, _sJac, 1); _jacAB2 = Set(_pB, _sJac, 2);
        _jacBA0 = Set(_pB, _sJac, 0); _jacBA1 = Set(_div, _sJac, 1); _jacBA2 = Set(_pA, _sJac, 2);
        _grad0 = Set(_velA, _sGrad, 0); _grad1 = Set(_pA, _sGrad, 1);
        _advD0 = Set(_dyeA, _sAdvD, 0); _advD1 = Set(_velA, _sAdvD, 1); _advD2 = Set(_dyeB, _sAdvD, 2);
        Ready = true;
    }

    public void Step(byte[] addPc, byte[] advVPc, byte[] simPc, byte[] advDPc, int iters)
    {
        if (!Ready) { return; }
        if ((iters & 1) == 1) { iters++; }
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);

        RunOne(_pAdd, addPc, (_add0, 0u), (_add1, 1u));
        RunOne(_pAdvV, advVPc, (_advV0, 0u), (_advV1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        long cl = _rd.ComputeListBegin();
        Bind(cl, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
        _rd.ComputeListAddBarrier(cl);
        for (int k = 0; k < iters; k++)
        {
            if (k % 2 == 0) { Bind(cl, _pJac, simPc, (_jacAB0, 0u), (_jacAB1, 1u), (_jacAB2, 2u)); }
            else { Bind(cl, _pJac, simPc, (_jacBA0, 0u), (_jacBA1, 1u), (_jacBA2, 2u)); }
            _rd.ComputeListAddBarrier(cl);
        }
        Bind(cl, _pGrad, simPc, (_grad0, 0u), (_grad1, 1u));
        _rd.ComputeListAddBarrier(cl);
        _rd.ComputeListEnd();

        RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
        _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    // Full velocity readback (sync): 4 floats/cell (vx,vy,vz,0), x-fastest.
    public float[] ReadVelocity()
    {
        if (!Ready) { return Array.Empty<float>(); }
        byte[] data = _rd.TextureGetData(_velA, 0);
        int n = _grid.X * _grid.Y * _grid.Z * 4;
        var f = new float[n];
        Buffer.BlockCopy(data, 0, f, 0, Math.Min(data.Length, n * 4));
        return f;
    }

    private void RunOne(Rid pipe, byte[] pc, params (Rid set, uint idx)[] sets)
    {
        long cl = _rd.ComputeListBegin();
        Bind(cl, pipe, pc, sets);
        _rd.ComputeListEnd();
    }

    private void Bind(long cl, Rid pipe, byte[] pc, params (Rid set, uint idx)[] sets)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        foreach (var (set, idx) in sets) { _rd.ComputeListBindUniformSet(cl, set, idx); }
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in new[]
        {
            _add0, _add1, _advV0, _advV1, _div0, _div1,
            _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2, _grad0, _grad1, _advD0, _advD1, _advD2,
        })
        {
            if (s.IsValid) { _rd.FreeRid(s); }
        }
        foreach (var t in new[] { _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private Rid Compile(string name)
    {
        string src = FileAccess.GetFileAsString(Dir + name + ".glslinc");
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[FluidSim3D] could not read {name}"); return default; }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[FluidSim3D] {name} compile error:\n{err}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private RDTextureFormat Fmt(RenderingDevice.DataFormat format) => new()
    {
        Format = format,
        TextureType = RenderingDevice.TextureType.Type3D,
        Width = (uint)_grid.X, Height = (uint)_grid.Y, Depth = (uint)_grid.Z,
        ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
            | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyFromBit
            | RenderingDevice.TextureUsageBits.CanCopyToBit,
    };

    private Rid MakeTex(RDTextureFormat tf)
    {
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private Rid Set(Rid tex, Rid shader, int setIndex)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, shader, (uint)setIndex);
    }
}
