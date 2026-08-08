using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// CurlFront — the breaking front reduced to a CURVE: one entry per along-shore row.
//
// This is the piece the curl thread kept trying to avoid. `curl-hypotheses.md` §2 claimed E
// only needed a FIELD (steepness + gradient, zero extra passes) and that only A needed a curve.
// That was wrong, and it cost three rounds of debugging: a field can say "breaking here", but
// it cannot say WHERE THE FRONT IS in world space, and placing a tube needs a position.
//
// The specific failure: age is not a distance field. It ramps inside the breaking ribbon and is
// flat zero outside, so any across-crest distance derived from it saturates at the ribbon width
// (~0.6 m) and the tube collapses into a horizontal slab. With front_x known, across-crest is
// simply `pos.x - front_x` — real metres, and the radius becomes independent of the data.
//
// Output texture is N x 1, rgba16f:
//   r = front x (metres)   g = free-surface height there (m)   b = strength   a = valid
//
// One dispatch, N threads, each scanning one row. Cheap enough to run every frame.
public sealed class CurlFront
{
    private const string PassPath = "res://shaders/shorewaves/pass_curl_front.glsl";

    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly int _groups;

    private Rid _shader, _pipeline, _front;
    private readonly Rid[] _sets = new Rid[2];   // one per solver state parity
    private float _dx = 0.05f;

    public bool Ready { get; private set; }
    public Rid FrontRid => _front;

    /// <summary>Steepness gate. Shares the measured 0.515 with CurlAge — the same physical
    /// event defines both, so they must not drift apart.</summary>
    public float BirthSteep = 0.515f;
    public float MinDepth = 0.05f;

    public CurlFront(RenderingDevice rd, int n)
    {
        _rd = rd;
        _n = n;
        _groups = (n + 63) / 64;

        var sf = GD.Load<RDShaderFile>(PassPath);
        if (sf == null) { GD.PushError($"[CurlFront] missing {PassPath}"); return; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[CurlFront] compile error:\n{err}"); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = 1, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _front = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(_front, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        Ready = true;
    }

    public void Bind(Rid[] stateRids, Rid bottom, Rid derived, float dx)
    {
        if (!Ready) { return; }
        _dx = dx;
        for (int p = 0; p < 2; p++)
        {
            _sets[p] = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
            {
                Img(0, stateRids[p]), Img(1, bottom), Img(2, derived), Img(3, _front),
            }, _shader, 0);
        }
    }

    public void Step(int statePar)
    {
        if (!Ready) { return; }
        Rid set = _sets[statePar];
        if (!set.IsValid) { return; }

        float[] v = { BirthSteep, MinDepth, _dx, 0.0f };
        var pc = new byte[16];
        Buffer.BlockCopy(v, 0, pc, 0, 16);

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        _rd.ComputeListBindUniformSet(cl, set, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 16);
        _rd.ComputeListDispatch(cl, (uint)_groups, 1, 1);
        _rd.ComputeListEnd();
    }

    /// <summary>How many rows found a front, and the mean across-shore position. The check that
    /// matters before any geometry trusts this: a curve that is mostly invalid places tubes in
    /// the wrong place silently, which is exactly how the age field wasted three rounds.</summary>
    public void CaptureStats(out int valid, out float meanX)
    {
        valid = 0; meanX = 0.0f;
        if (!Ready) { return; }
        byte[] d = _rd.TextureGetData(_front, 0);
        int rows = Math.Min(_n, d.Length / 8);   // rgba16f = 8 bytes
        float sum = 0.0f;
        for (int i = 0; i < rows; i++)
        {
            float x = (float)BitConverter.ToHalf(d, i * 8);
            float ok = (float)BitConverter.ToHalf(d, i * 8 + 6);
            if (ok > 0.5f) { valid++; sum += x; }
        }
        meanX = valid > 0 ? sum / valid : 0.0f;
    }

    /// <summary>One record per placed slice: where the front is, which way it faces, how hard.</summary>
    public readonly struct Slice
    {
        public readonly Vector2 Pos;    // world xz of the front
        public readonly Vector2 Dir;    // unit, wave-forward (shoreward normal of the curve)
        public readonly float Speed;    // strength at the front
        public Slice(Vector2 pos, Vector2 dir, float speed) { Pos = pos; Dir = dir; Speed = speed; }
    }

    /// <summary>
    /// Sample the curve every `spacing` metres and return the slices that would be placed.
    /// This lives on CurlFront and NOT on CurlAge on purpose: it needs POSITIONS, and age is a
    /// clock — it can say a stretch of front is breaking, never where that front is.
    ///
    /// Direction comes from the curve's own tangent, so it follows the shoreline's meander
    /// instead of assuming waves arrive square to the beach.
    /// </summary>
    public System.Collections.Generic.List<Slice> ExtractSlices(float domain, float spacing, float minStrength)
    {
        var outp = new System.Collections.Generic.List<Slice>();
        if (!Ready) { return outp; }
        byte[] d = _rd.TextureGetData(_front, 0);
        int rows = Math.Min(_n, d.Length / 8);
        float mPerRow = domain / _n;
        int stride = Math.Max(1, (int)Mathf.Round(spacing / Mathf.Max(mPerRow, 1e-4f)));

        for (int i = 0; i < rows; i += stride)
        {
            float ok = (float)BitConverter.ToHalf(d, i * 8 + 6);
            float str = (float)BitConverter.ToHalf(d, i * 8 + 4);
            if (ok < 0.5f || str < minStrength) { continue; }
            float fx = (float)BitConverter.ToHalf(d, i * 8);
            float z = i * mPerRow;

            // tangent from neighbouring rows; shoreward normal is its perpendicular
            int ia = Math.Max(i - stride, 0), ib = Math.Min(i + stride, rows - 1);
            float xa = (float)BitConverter.ToHalf(d, ia * 8);
            float xb = (float)BitConverter.ToHalf(d, ib * 8);
            var tangent = new Vector2(xb - xa, (ib - ia) * mPerRow);
            if (tangent.LengthSquared() < 1e-6f) { tangent = new Vector2(0.0f, 1.0f); }
            tangent = tangent.Normalized();
            var normal = new Vector2(tangent.Y, -tangent.X);   // points shoreward (+x side)
            if (normal.X < 0.0f) { normal = -normal; }

            outp.Add(new Slice(new Vector2(fx, z), normal, str));
        }
        return outp;
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _sets) { if (s.IsValid) { _rd.FreeRid(s); } }   // sets before textures
        if (_front.IsValid) { _rd.FreeRid(_front); }
        if (_shader.IsValid) { _rd.FreeRid(_shader); }
    }

    private RDUniform Img(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(rid);
        return u;
    }
}
