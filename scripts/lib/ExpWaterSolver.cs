using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// ExpWaterSolver — the solver the DSP experiment series shares.
//
// Same structure as WebgpuWaterSolver (which is deliberately left alone: scenes 23, 24 and
// WaveTankV2 depend on it), driving shaders/exp/exp_sim.glsl instead. The only addition is an
// OBSTACLE MASK on set 2: an r32f image where 1 = solid, 0 = open water, rasterized host-side so
// column shapes stay ordinary C# rather than another shader.
//
// Every experiment uniform defaults to a no-op, so with nothing enabled this produces exactly
// what the stock kernel produces — which is what makes 24_base a real reference.
//
// All rd work must run inside RenderingServer.CallOnRenderThread.
public sealed class ExpWaterSolver
{
    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly uint _g;

    private Rid _shader, _pipeline;
    private readonly Rid[] _tex = new Rid[2];
    private Rid _display, _obstacle;
    private readonly Rid[] _setIn = new Rid[2];
    private readonly Rid[] _setOut = new Rid[2];
    private Rid _setObstacle;
    private int _cur;

    public bool Ready { get; private set; }
    public Rid DisplayRid => _display;
    public int N => _n;

    public float DropRadius { get; set; } = 0.05f;
    public float Damping { get; set; } = 0.995f;
    public static float DampingFor(int n) => Mathf.Pow(0.995f, 256.0f / n);

    /// <summary>1 = perfect reflector (no-flux wall), 0 = pure absorber.</summary>
    public float ObstacleHardness { get; set; } = 1.0f;

    /// <summary>Off by default — the whole series' baseline must be the untouched sim.</summary>
    public bool ObstaclesEnabled { get; set; }

    public ExpWaterSolver(RenderingDevice rd, int n)
    {
        _rd = rd;
        _n = n;
        _g = (uint)((n - 1) / 8 + 1);

        var sf = GD.Load<RDShaderFile>("res://shaders/exp/exp_sim.glsl");
        if (sf == null) { GD.PushError("[ExpWaterSolver] missing exp_sim.glsl"); return; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[ExpWaterSolver] {err}"); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _tex[0] = MakeTex(tf);
        _tex[1] = MakeTex(tf);
        _display = MakeTex(tf);

        var of = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            // CanUpdateBit is required — unlike the state textures, this one is written from the
            // CPU (columns are rasterized host-side), and TextureUpdate refuses without it.
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit | RenderingDevice.TextureUsageBits.CanCopyToBit
                | RenderingDevice.TextureUsageBits.CanUpdateBit,
        };
        _obstacle = MakeTex(of);

        _setIn[0] = MakeSet(_tex[0], 0);
        _setIn[1] = MakeSet(_tex[1], 0);
        _setOut[0] = MakeSet(_tex[0], 1);
        _setOut[1] = MakeSet(_tex[1], 1);
        _setObstacle = MakeSet(_obstacle, 2);
        Ready = true;
        GD.Print($"[ExpWaterSolver] ok ({n}x{n})");
    }

    /// <summary>
    /// Rasterize the obstacle field host-side. mask[y*n + x] in 0..1, 1 = solid.
    /// Called only when the layout changes, not per frame.
    /// </summary>
    public void UploadObstacles(float[] mask)
    {
        if (!Ready || mask.Length < _n * _n) { return; }
        var bytes = new byte[_n * _n * sizeof(float)];
        Buffer.BlockCopy(mask, 0, bytes, 0, bytes.Length);
        _rd.TextureUpdate(_obstacle, 0, bytes);
    }

    public void ClearObstacles()
    {
        if (Ready) { _rd.TextureClear(_obstacle, new Color(0, 0, 0, 0), 0, 1, 0, 1); }
    }

    public void Step(bool drop, float dcx, float dcz, float dstr,
        bool sphere = false, Vector3 sphereOld = default, Vector3 sphereNew = default,
        float chopDamping = 0.0f)
    {
        if (!Ready) { return; }
        var passes = new System.Collections.Generic.List<byte[]> { Pc(0.0f, dcx, dcz, drop ? dstr : 0.0f, sphereOld, sphereNew, DropRadius) };
        if (sphere) { passes.Add(Pc(1.0f, 0.0f, 0.0f, 0.0f, sphereOld, sphereNew, DropRadius)); }
        var chop = new Vector4(chopDamping, 0.0f, 0.0f, 0.0f);
        passes.Add(Pc(2.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, Vector3.Zero, 0.0f, chop));
        passes.Add(Pc(2.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, Vector3.Zero, 0.0f, chop));
        passes.Add(Pc(3.0f, 0.0f, 0.0f, 0.0f, sphereOld, sphereNew, DropRadius));
        Dispatch(passes);
    }

    /// <summary>N drops without advancing the sim — a forcing term, not a time step.</summary>
    public void InjectDrops(System.Collections.Generic.IReadOnlyList<Vector4> drops)
    {
        if (!Ready || drops.Count == 0) { return; }
        var passes = new System.Collections.Generic.List<byte[]>();
        foreach (var d in drops)
        {
            if (d.W == 0.0f) { continue; }
            passes.Add(Pc(0.0f, d.X, d.Y, d.W, Vector3.Zero, Vector3.Zero, d.Z));
        }
        Dispatch(passes);
    }

    public void Reset()
    {
        if (!Ready) { return; }
        _rd.TextureClear(_tex[0], new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _rd.TextureClear(_tex[1], new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _rd.TextureClear(_display, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _cur = 0;
    }

    private void Dispatch(System.Collections.Generic.List<byte[]> passes)
    {
        if (passes.Count == 0) { return; }
        int src = _cur;
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        foreach (var pc in passes)
        {
            int dst = 1 - src;
            _rd.ComputeListBindUniformSet(cl, _setIn[src], 0);
            _rd.ComputeListBindUniformSet(cl, _setOut[dst], 1);
            _rd.ComputeListBindUniformSet(cl, _setObstacle, 2);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _g, _g, 1);
            _rd.ComputeListAddBarrier(cl);
            src = dst;
        }
        _rd.ComputeListEnd();
        _rd.TextureCopy(_tex[src], _display, Vector3.Zero, Vector3.Zero, new Vector3(_n, _n, 1), 0, 0, 0, 0);
        _cur = src;
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _setIn) { FreeIf(s); }
        foreach (var s in _setOut) { FreeIf(s); }
        FreeIf(_setObstacle);
        FreeIf(_tex[0]); FreeIf(_tex[1]); FreeIf(_display); FreeIf(_obstacle);
        FreeIf(_shader);
    }

    private byte[] Pc(float mode, float dcx, float dcz, float dstr, Vector3 oc, Vector3 nc, float radius,
        Vector4 extra = default)
    {
        // std430, 24 floats / 96 B — matches Params in exp_sim.glsl (stock 20 + exp0)
        float[] f =
        {
            _n, _n, mode, radius,
            dcx, dcz, dstr, 0.25f,
            oc.X, oc.Y, oc.Z, Damping,
            nc.X, nc.Y, nc.Z, 0.0f,
            extra.X, extra.Y, extra.Z, extra.W,
            ObstacleHardness, ObstaclesEnabled ? 1.0f : 0.0f, 0.0f, 0.0f,
        };
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private Rid MakeTex(RDTextureFormat tf)
    {
        var t = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private Rid MakeSet(Rid tex, int setIdx)
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
