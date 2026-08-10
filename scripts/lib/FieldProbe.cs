using Godot;

namespace GodotCsharpExperiments.Lib;

// FieldProbe — a one-dispatch read-out pass over fields a solver already owns, written into
// its own texture for the artifact view.
//
// Block A's artifact is always a RESIDUAL, and both fluid sims expose one directly. Block B's
// is not: a dissipative scheme leaves no residual, it leaves a shorter tail, and an advection
// scheme leaves no residual either, it leaves a blurrier field. In both cases the honest
// thing to render is a DERIVED quantity — |h − h_prev| for one, |∇f| for the other — and
// neither solver has anywhere to put it.
//
// Both kernels take (in0, in1, out) so one helper drives either; scenes pass the kernel name.
public sealed class FieldProbe
{
    private readonly RenderingDevice _rd;
    private readonly uint _gx, _gy;
    private Rid _shader, _pipe, _set0, _set1, _set2, _tex;

    public bool Ready { get; private set; }
    public Rid Rid => _tex;

    public FieldProbe(RenderingDevice rd, Vector2I grid, string kernel, Rid in0, Rid in1)
    {
        _rd = rd;
        _gx = (uint)((grid.X - 1) / 8 + 1);
        _gy = (uint)((grid.Y - 1) / 8 + 1);

        string src = FileAccess.GetFileAsString($"res://shaders/stamp/{kernel}.glslinc");
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[FieldProbe] cannot read {kernel}"); return; }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[FieldProbe] {kernel}:\n{err}"); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipe = _rd.ComputePipelineCreate(_shader);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)grid.X, Height = (uint)grid.Y, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _tex = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(_tex, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        _set0 = Set(in0, 0); _set1 = Set(in1.IsValid ? in1 : in0, 1); _set2 = Set(_tex, 2);
        Ready = true;
    }

    public void Run(byte[] pc)
    {
        if (!Ready) { return; }
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipe);
        _rd.ComputeListBindUniformSet(cl, _set0, 0);
        _rd.ComputeListBindUniformSet(cl, _set1, 1);
        _rd.ComputeListBindUniformSet(cl, _set2, 2);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
        _rd.ComputeListEnd();
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in new[] { _set0, _set1, _set2 }) { if (s.IsValid) { _rd.FreeRid(s); } }
        if (_tex.IsValid) { _rd.FreeRid(_tex); }
        if (_shader.IsValid) { _rd.FreeRid(_shader); }
    }

    private Rid Set(Rid tex, int idx)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, (uint)idx);
    }
}
