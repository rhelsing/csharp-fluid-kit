using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// IrField — measure the basin's impulse response once, then replay ANY signal through it.
//
// Capture: the caller resets the sim, fires a single unit impulse, and calls CaptureSlice()
// once per tick for Depth ticks. That stack of height fields IS h(x, y, k).
//
// Replay: the solver is switched off. Convolve() takes the drive-signal history and
// reconstructs the surface as SUM_k s[t-k]*h(x,y,k) in one dispatch. Per-cell independent,
// so there is no stencil, no substep loop and no stability condition — the recurrence has
// been replaced by a function evaluation.
//
// All rd work must run inside RenderingServer.CallOnRenderThread.
public sealed class IrField
{
    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly int _depth;
    private readonly uint _g;

    private Rid _shader, _pipeline;
    private Rid _irTex;       // image3D r32f, the recorded response
    private Rid _fieldTex;    // image2D rgba32f, the reconstruction (R = height)
    private Rid _setIr, _setField;
    private Rid _tapBuf, _setTaps;
    private Rid _stateTexCached, _setState;

    private readonly float[] _tapScratch;
    private readonly byte[] _tapBytes;

    public bool Ready { get; private set; }
    public Rid DisplayRid => _fieldTex;
    public int Depth => _depth;

    /// <summary>Active (non-zero) taps in the last Convolve — the real cost driver.</summary>
    public int LastTapCount { get; private set; }

    public IrField(RenderingDevice rd, int n, int depth)
    {
        _rd = rd;
        _n = n;
        _depth = depth;
        _g = (uint)((n - 1) / 8 + 1);
        _tapScratch = new float[depth * 2];
        _tapBytes = new byte[depth * 2 * sizeof(float)];

        var sf = GD.Load<RDShaderFile>("res://shaders/ir/ir_field.glsl");
        if (sf == null) { GD.PushError("[IrField] missing ir_field.glsl"); return; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[IrField] {err}"); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        var ir = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type3D,
            Width = (uint)n, Height = (uint)n, Depth = (uint)depth, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _irTex = _rd.TextureCreate(ir, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(_irTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        var fld = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _fieldTex = _rd.TextureCreate(fld, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(_fieldTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        _setIr = MakeImageSet(_irTex, 1);
        _setField = MakeImageSet(_fieldTex, 2);

        _tapBuf = _rd.StorageBufferCreate((uint)_tapBytes.Length);
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = 0 };
        u.AddId(_tapBuf);
        _setTaps = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, 3);

        Ready = true;
        GD.Print($"[IrField] ok ({n}x{n} x {depth} slices, {(long)n * n * depth * 4 / (1024 * 1024)} MB)");
    }

    /// <summary>Record the sim's current height field into IR slice k.</summary>
    public void CaptureSlice(Rid stateTex, int slice)
    {
        if (!Ready || slice < 0 || slice >= _depth) { return; }
        if (stateTex != _stateTexCached)
        {
            if (_setState.IsValid) { _rd.FreeRid(_setState); }
            _setState = MakeImageSet(stateTex, 0);
            _stateTexCached = stateTex;
        }
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        _rd.ComputeListBindUniformSet(cl, _setState, 0);
        _rd.ComputeListBindUniformSet(cl, _setIr, 1);
        _rd.ComputeListBindUniformSet(cl, _setField, 2);
        _rd.ComputeListBindUniformSet(cl, _setTaps, 3);
        _rd.ComputeListSetPushConstant(cl, Pc(0.0f, slice, 0.0f, 1.0f), 32);
        _rd.ComputeListDispatch(cl, _g, _g, 1);
        _rd.ComputeListEnd();
    }

    /// <summary>
    /// Reconstruct the surface from the drive-signal history.
    /// history[k] is s[t-k]; only non-zero entries are uploaded.
    /// </summary>
    public void Convolve(float[] history, float gain, float eps = 1.0e-6f)
    {
        if (!Ready) { return; }
        int n = 0;
        int lim = Math.Min(history.Length, _depth);
        for (int k = 0; k < lim; ++k)
        {
            float s = history[k];
            if (MathF.Abs(s) <= eps) { continue; }
            _tapScratch[n * 2] = k;
            _tapScratch[n * 2 + 1] = s;
            ++n;
        }
        LastTapCount = n;
        if (n == 0)
        {
            // Nothing driving — clear so the surface goes flat instead of freezing.
            _rd.TextureClear(_fieldTex, new Color(0, 0, 0, 0), 0, 1, 0, 1);
            return;
        }
        Buffer.BlockCopy(_tapScratch, 0, _tapBytes, 0, n * 2 * sizeof(float));
        _rd.BufferUpdate(_tapBuf, 0, (uint)(n * 2 * sizeof(float)), _tapBytes);

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        if (_setState.IsValid) { _rd.ComputeListBindUniformSet(cl, _setState, 0); }
        _rd.ComputeListBindUniformSet(cl, _setIr, 1);
        _rd.ComputeListBindUniformSet(cl, _setField, 2);
        _rd.ComputeListBindUniformSet(cl, _setTaps, 3);
        _rd.ComputeListSetPushConstant(cl, Pc(1.0f, 0.0f, n, gain), 32);
        _rd.ComputeListDispatch(cl, _g, _g, 1);
        NormalPass(cl);
        _rd.ComputeListEnd();
    }

    /// <summary>Write one raw IR slice into the display field — a look at the measurement itself.</summary>
    public void InspectSlice(int slice, float gain)
    {
        if (!Ready) { return; }
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        if (_setState.IsValid) { _rd.ComputeListBindUniformSet(cl, _setState, 0); }
        _rd.ComputeListBindUniformSet(cl, _setIr, 1);
        _rd.ComputeListBindUniformSet(cl, _setField, 2);
        _rd.ComputeListBindUniformSet(cl, _setTaps, 3);
        _rd.ComputeListSetPushConstant(cl, Pc(2.0f, Math.Clamp(slice, 0, _depth - 1), 0.0f, gain), 32);
        _rd.ComputeListDispatch(cl, _g, _g, 1);
        NormalPass(cl);
        _rd.ComputeListEnd();
    }

    // The ww_* shaders read B/A as the surface normal. Height alone renders as a blob, so every
    // path that writes the field has to follow it with this.
    private void NormalPass(long cl)
    {
        _rd.ComputeListAddBarrier(cl);
        _rd.ComputeListSetPushConstant(cl, Pc(3.0f, 0.0f, 0.0f, 1.0f), 32);
        _rd.ComputeListDispatch(cl, _g, _g, 1);
    }

    public void ClearIr()
    {
        if (Ready) { _rd.TextureClear(_irTex, new Color(0, 0, 0, 0), 0, 1, 0, 1); }
    }

    public void Free()
    {
        Ready = false;
        FreeIf(_setState); FreeIf(_setTaps); FreeIf(_setField); FreeIf(_setIr);
        FreeIf(_tapBuf); FreeIf(_fieldTex); FreeIf(_irTex);
        FreeIf(_shader);
    }

    private byte[] Pc(float mode, float slice, float tapCount, float gain)
    {
        float[] f = { _n, _n, mode, slice, tapCount, gain, 0.0f, 0.0f };
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private Rid MakeImageSet(Rid tex, int setIdx)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _shader, (uint)setIdx);
    }

    private void FreeIf(Rid r)
    {
        if (r.IsValid) { _rd.FreeRid(r); }
    }
}
