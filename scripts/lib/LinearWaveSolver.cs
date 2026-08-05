using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Explicit leapfrog LINEAR wave-equation heightfield solver (d2h/dt2 = c^2 laplacian h), on a
// GPU compute ping-pong. The cheap, predictable "linear core" family (Hugo-Elias / Wallace /
// MNA cousin). Optional spatially-varying c^2 = g·depth from a bathymetry texture gives
// shoaling. Fully self-contained — does not touch ShallowWaterKp or the frozen scenes.
// Call Step()/UploadBathy() from inside RenderingServer.CallOnRenderThread.
public sealed class LinearWaveSolver
{
    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly float _dx;
    private readonly bool _useBathy;
    private readonly int _groups;

    private readonly Rid[] _h = new Rid[2];   // 2-buffer leapfrog (curr + prev/out)
    private Rid _bathy;
    private Rid _shWave, _pWave;
    private readonly Rid[] _setWave = new Rid[2];   // index = current-buffer parity
    private int _cur;

    // tunables (host sets these on the main thread; read on the render thread — benign race)
    public float Dt = 0.004f;
    public float Damp = 0.002f;
    public float ConstC2 = 9.81f;       // c^2 for the flat pool (= g·depth)
    public float WaveAmp = 0.0f;
    public float WavePeriod = 4.0f;
    public bool UseWavemaker = false;

    public bool Ready { get; private set; }
    public Rid HeightRid => _h[_cur];
    public Rid BathyRid => _bathy;
    public int N => _n;

    public LinearWaveSolver(RenderingDevice rd, int n, float dx, bool useBathy)
    {
        _rd = rd;
        _n = n;
        _dx = dx;
        _useBathy = useBathy;
        _groups = (n + 15) / 16;

        var fmt = Fmt();
        _h[0] = MakeTex(fmt);
        _h[1] = MakeTex(fmt);
        _bathy = MakeTex(fmt);   // always created (bound + used for the render bed even if c is constant)

        _shWave = Load("pass_wave");
        if (!_shWave.IsValid)
        {
            GD.PushError("[LinearWaveSolver] pass_wave failed to load");
            return;
        }
        _pWave = _rd.ComputePipelineCreate(_shWave);
        for (int c = 0; c < 2; c++)
        {
            _setWave[c] = SetImg(_shWave, (0, _h[c]), (1, _h[c ^ 1]), (2, _bathy));
        }
        Ready = true;
        GD.Print($"[LinearWaveSolver] ok ({n}x{n}, dx={dx}, bathy={useBathy})");
    }

    public void UploadBathy(byte[] r32) => _rd.TextureUpdate(_bathy, 0, r32);

    public void Step(int substeps, float t0, bool poke, float px, float pz, float pr, float ps)
    {
        if (!Ready) { return; }
        long cl = _rd.ComputeListBegin();
        for (int k = 0; k < substeps; k++)
        {
            float t = t0 + k * Dt;
            float pstr = (poke && k == 0) ? ps : 0.0f;   // apply the poke once
            byte[] pcb = Pc(Dt, _dx, 9.81f, Damp, ConstC2, _useBathy ? 1.0f : 0.0f,
                px, pz, pr, pstr, WaveAmp, WavePeriod, t, UseWavemaker ? 1.0f : 0.0f, 0.0f, 0.0f);
            _rd.ComputeListBindComputePipeline(cl, _pWave);
            _rd.ComputeListBindUniformSet(cl, _setWave[_cur], 0);
            _rd.ComputeListSetPushConstant(cl, pcb, (uint)pcb.Length);
            _rd.ComputeListDispatch(cl, (uint)_groups, (uint)_groups, 1);
            _rd.ComputeListAddBarrier(cl);
            _cur ^= 1;   // next becomes current
        }
        _rd.ComputeListEnd();
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _setWave) { FreeIf(s); }
        FreeIf(_h[0]); FreeIf(_h[1]); FreeIf(_bathy);
        FreeIf(_shWave);
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static byte[] Pc(params float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];   // 16 floats = 64 B
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private RDUniform Img(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(rid);
        return u;
    }

    private Rid SetImg(Rid shader, params (int b, Rid r)[] items)
    {
        var a = new Godot.Collections.Array<RDUniform>();
        foreach (var (b, r) in items) { a.Add(Img(b, r)); }
        return _rd.UniformSetCreate(a, shader, 0);
    }

    private Rid Load(string pass)
    {
        var sf = GD.Load<RDShaderFile>($"res://shaders/shorewaves/{pass}.glsl");
        if (sf == null) { GD.PushError($"[LinearWaveSolver] missing {pass}.glsl"); return default; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[LinearWaveSolver] {pass}: {err}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private RDTextureFormat Fmt() => new()
    {
        Format = RenderingDevice.DataFormat.R32Sfloat,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = (uint)_n, Height = (uint)_n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
            | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyToBit
            | RenderingDevice.TextureUsageBits.CanUpdateBit,
    };

    private Rid MakeTex(RDTextureFormat f)
    {
        var t = _rd.TextureCreate(f, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private void FreeIf(Rid r)
    {
        if (r.IsValid) { _rd.FreeRid(r); }
    }
}
