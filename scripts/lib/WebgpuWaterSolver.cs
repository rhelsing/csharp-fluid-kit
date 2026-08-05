using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Evan Wallace's WebGL-Water heightfield sim (via water-kit scene 27) ported to C#. One compute
// kernel selected by a `mode` push-constant — drop → update×2 → normal (sphere pass skipped for
// the tank). State RGBA32F: R=height, G=velocity, B=normal.x, A=normal.z (the layout the
// raytraced ww_* shaders sample). Ping-pongs two storage images; the final state is copied into
// a fixed _display texture each tick (what a Texture2Drd samples). Call from CallOnRenderThread.
public sealed class WebgpuWaterSolver
{
    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly uint _g;

    private Rid _shader, _pipeline;
    private readonly Rid[] _tex = new Rid[2];
    private Rid _display;
    private readonly Rid[] _setIn = new Rid[2];    // set 0: read _tex[i]
    private readonly Rid[] _setOut = new Rid[2];   // set 1: write _tex[i]
    private int _cur;

    public bool Ready { get; private set; }
    public Rid DisplayRid => _display;   // fixed; a Texture2Drd samples this
    public int N => _n;

    // Drop footprint in normalized pool units. Small radius + a high-res grid = short-wavelength
    // ripples, i.e. waves that read as small against a big tank.
    public float DropRadius { get; set; } = 0.03f;

    // Velocity damping applied per UPDATE STEP (two per tick). Wallace used 0.995 on a 256 grid;
    // decay compounds per step, so a finer grid or a faster frame rate kills waves proportionally
    // sooner. DampingFor() rescales it to hold the decay-per-distance-travelled constant.
    public float Damping { get; set; } = 0.995f;

    public static float DampingFor(int n) => Mathf.Pow(0.995f, 256.0f / n);

    public WebgpuWaterSolver(RenderingDevice rd, int n)
    {
        _rd = rd;
        _n = n;
        _g = (uint)((n - 1) / 8 + 1);

        var sf = GD.Load<RDShaderFile>("res://shaders/shorewaves/webgpu_sim.glsl");
        if (sf == null) { GD.PushError("[WebgpuWaterSolver] missing webgpu_sim.glsl"); return; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[WebgpuWaterSolver] {err}"); return; }
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
        _setIn[0] = MakeSet(_tex[0], 0);
        _setIn[1] = MakeSet(_tex[1], 0);
        _setOut[0] = MakeSet(_tex[0], 1);
        _setOut[1] = MakeSet(_tex[1], 1);
        Ready = true;
        GD.Print($"[WebgpuWaterSolver] ok ({n}x{n})");
    }

    // drop centre in [-1,1] sim space; dstr>0 raises the surface there. When sphere=true the
    // sphere pass displaces water between sphereOld and sphereNew (Wallace's ball).
    public void Step(bool drop, float dcx, float dcz, float dstr,
        bool sphere = false, Vector3 sphereOld = default, Vector3 sphereNew = default,
        bool paddle = false, float paddleOldX = 0.0f, float paddleNewX = 0.0f,
        float paddleWidth = 0.06f, float paddleGain = 1.0f,
        float curveAmp = 0.0f, float curveLobes = 0.0f,
        float curvePhaseOld = 0.0f, float curvePhaseNew = 0.0f,
        float segCount = 0.0f,
        float microAmp = 0.0f, float microLobes = 0.0f,
        float microPhaseOld = 0.0f, float microPhaseNew = 0.0f,
        float shoalRefDepth = 0.0f, float floorBase = 0.0f,
        float bedSlope = 0.0f, float waterLevel = 0.0f,
        float chopDamping = 0.0f)
    {
        if (!Ready) { return; }
        var passes = new System.Collections.Generic.List<byte[]>
        {
            Pc(0.0f, dcx, dcz, drop ? dstr : 0.0f, sphereOld, sphereNew, DropRadius),   // drop
        };
        if (sphere) { passes.Add(Pc(1.0f, 0.0f, 0.0f, 0.0f, sphereOld, sphereNew, DropRadius)); }
        // paddle: drop_center carries (old x, new x), drop_radius the falloff, strength the gain;
        // the curved face rides in the spare sphere slots — old = (phase, lobes, amp), new = (phase)
        if (paddle)
        {
            passes.Add(Pc(4.0f, paddleOldX, paddleNewX, paddleGain,
                new Vector3(curvePhaseOld, curveLobes, curveAmp),
                new Vector3(curvePhaseNew, segCount, 0.0f), paddleWidth,
                new Vector4(microAmp, microLobes, microPhaseOld, microPhaseNew)));
        }
        // update passes carry the bed ramp; shoalRefDepth > 0 turns depth-dependent speed on
        var bed = new Vector3(floorBase, bedSlope, waterLevel);
        var chop = new Vector4(chopDamping, 0.0f, 0.0f, 0.0f);
        passes.Add(Pc(2.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, bed, shoalRefDepth, chop));   // update
        passes.Add(Pc(2.0f, 0.0f, 0.0f, 0.0f, Vector3.Zero, bed, shoalRefDepth, chop));   // update
        passes.Add(Pc(3.0f, 0.0f, 0.0f, 0.0f, sphereOld, sphereNew, DropRadius));   // normal
        int src = _cur;
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        foreach (var pc in passes)
        {
            int dst = 1 - src;
            _rd.ComputeListBindUniformSet(cl, _setIn[src], 0);
            _rd.ComputeListBindUniformSet(cl, _setOut[dst], 1);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _g, _g, 1);
            _rd.ComputeListAddBarrier(cl);
            src = dst;
        }
        _rd.ComputeListEnd();
        _rd.TextureCopy(_tex[src], _display, Vector3.Zero, Vector3.Zero, new Vector3(_n, _n, 1), 0, 0, 0, 0);
        _cur = src;
    }

    // Flatten the tank back to still water. An impulse response must be measured from rest —
    // any leftover state would be recorded as part of h() and replayed forever after.
    public void Reset()
    {
        if (!Ready) { return; }
        _rd.TextureClear(_tex[0], new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _rd.TextureClear(_tex[1], new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _rd.TextureClear(_display, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _cur = 0;
    }

    // Inject N drops in one go WITHOUT advancing the sim — mode-0 passes only, no update, no
    // normal. The kernel already supports this; Step() just never exposed more than one drop.
    // Used by the CXM fork to stamp the tank's output nodes at their tap positions each tick,
    // which is a forcing term, not a time step. Each drop is (cx, cz, radius, strength) with
    // cx/cz in [-1,1] sim space.
    public void InjectDrops(System.Collections.Generic.IReadOnlyList<Vector4> drops)
    {
        if (!Ready || drops.Count == 0) { return; }
        int src = _cur;
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        foreach (var d in drops)
        {
            if (d.W == 0.0f) { continue; }
            int dst = 1 - src;
            _rd.ComputeListBindUniformSet(cl, _setIn[src], 0);
            _rd.ComputeListBindUniformSet(cl, _setOut[dst], 1);
            var pc = Pc(0.0f, d.X, d.Y, d.W, Vector3.Zero, Vector3.Zero, d.Z);
            _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
            _rd.ComputeListDispatch(cl, _g, _g, 1);
            _rd.ComputeListAddBarrier(cl);
            src = dst;
        }
        _rd.ComputeListEnd();
        if (src != _cur)
        {
            _rd.TextureCopy(_tex[src], _display, Vector3.Zero, Vector3.Zero, new Vector3(_n, _n, 1), 0, 0, 0, 0);
            _cur = src;
        }
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _setIn) { FreeIf(s); }
        foreach (var s in _setOut) { FreeIf(s); }
        FreeIf(_tex[0]); FreeIf(_tex[1]); FreeIf(_display);
        FreeIf(_shader);
    }

    private byte[] Pc(float mode, float dcx, float dcz, float dstr, Vector3 oc, Vector3 nc, float radius,
        Vector4 extra = default)
    {
        // std430, 20 floats / 80 B — matches Params in webgpu_sim.glsl
        float[] f =
        {
            _n, _n, mode, radius,       // size.xy, mode, drop_radius (paddle falloff in mode 4)
            dcx, dcz, dstr, 0.25f,      // drop_center.xy, drop_strength, sphere_radius
            oc.X, oc.Y, oc.Z, Damping,  // sphere old_center (+ per-step damping in .w)
            nc.X, nc.Y, nc.Z, 0.0f,     // sphere new_center (mode 2: bed ramp)
            extra.X, extra.Y, extra.Z, extra.W,   // mode 4: micro layer
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
