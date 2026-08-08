using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// CurlAge — the BREAK-AGE memory that the barrel slices live in.
//
// Everything else in the curl thread is instantaneous: steepness and gradient describe the
// wave *now*. "Starts when it crosses a height, then drives into the face over time" cannot be
// expressed that way at all — it needs to know *when* a front started breaking, which means
// state that survives between frames. This is that state.
//
// rgba16f ping-pong: r = age, g = birth steepness, ba = propagation direction (unit xz).
//
// AGE IS ALSO A COORDINATE. The barrel's cross-section is a circle in (age, depth), so age
// doubles as the across-crest axis — oldest where breaking began, ~0 at the leading edge. That
// is what lets a tube follow a curving crest without anyone ever extracting the crest curve,
// and it is why the field is advected rather than aged in place: a fixed column is steep only
// while the front crosses it, so in-place ageing never leaves the blue end of the range and
// nothing is ever inside the tube (see pass_curl_age.glsl's header).
//
// OWNED BY SCENE 28, deliberately not by ShallowWaterKp: adding a pass to the solver would
// change scene 26's per-frame cost and void the A/B that scene 26 exists to provide.
//
// All rd work must run inside RenderingServer.CallOnRenderThread.
public sealed class CurlAge
{
    private const float G = 9.81f;
    private const string PassPath = "res://shaders/shorewaves/pass_curl_age.glsl";

    private readonly RenderingDevice _rd;
    private readonly int _n;
    private readonly int _groups;

    private Rid _shader, _pipeline;
    private readonly Rid[] _age = new Rid[2];
    private readonly Rid[] _sets = new Rid[4];   // index = statePar * 2 + agePar
    private int _agePar;
    private float _dx = 0.05f;

    public bool Ready { get; private set; }

    /// <summary>The most recently written age field — what the shaders sample.</summary>
    public Rid AgeRid => _age[_agePar];

    // ---- tunables ----
    /// <summary>Gradient magnitude that starts a barrel. 0.515 measured on screen — a breaking
    /// front is an order of magnitude steeper than ordinary swell, so a low gate lights the
    /// whole surf zone and carries no information.</summary>
    public float BirthSteep = 0.515f;
    public float MinDepth = 0.05f;
    /// <summary>Seconds from birth to fully crashed.</summary>
    public float CrashSeconds = 0.5f;
    /// <summary>Seconds to forget a front that stopped breaking.</summary>
    public float DecaySeconds = 2.0f;
    /// <summary>Scales sqrt(g·h) in the advection. 0 = drift on flow alone, 1 = full wave speed.</summary>
    public float Celerity = 1.0f;
    /// <summary>
    /// false = WALL-CLOCK: age advances in seconds, crash rate is a slider, directly tunable.
    /// true = PHASE-DRIVEN: age advances with the wave's own period, so a slow swell barrels
    /// slowly and a fast one crashes fast. More correct, but couples the crash to WavePeriod.
    /// Normalised at period 8 s so flipping the toggle does not jump the look.
    /// </summary>
    public bool PhaseDriven;

    // ---- debug stats (main thread reads after CaptureStats) ----
    public float StatMax, StatMean;
    public int StatLive, StatBytes;

    public CurlAge(RenderingDevice rd, int n)
    {
        _rd = rd;
        _n = n;
        _groups = (n + 15) / 16;

        var sf = GD.Load<RDShaderFile>(PassPath);
        if (sf == null) { GD.PushError($"[CurlAge] missing {PassPath}"); return; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[CurlAge] compile error:\n{err}"); return; }
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        _pipeline = _rd.ComputePipelineCreate(_shader);

        var fmt = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)n, Height = (uint)n, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _age[0] = MakeTex(fmt);
        _age[1] = MakeTex(fmt);
        StatBytes = n * n * 8;   // rgba16f = 8 bytes/texel
        Ready = true;
    }

    /// <summary>
    /// Bind the solver's outputs. Separate from the constructor because the sets need BOTH
    /// state parities — the solver ping-pongs its state and hands us whichever is current.
    /// </summary>
    public void Bind(Rid[] stateRids, Rid bottom, Rid derived, float dx)
    {
        if (!Ready) { return; }
        _dx = dx;
        for (int p = 0; p < 2; p++)
        {
            for (int a = 0; a < 2; a++)
            {
                _sets[p * 2 + a] = _rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    Img(0, stateRids[p]), Img(1, bottom), Img(2, derived),
                    Img(3, _age[a]), Img(4, _age[a ^ 1]),
                }, _shader, 0);
            }
        }
    }

    /// <summary>Advance the age field one frame. dt is SIM seconds, so it follows a sim-speed
    /// slider automatically rather than drifting out of step with the water.</summary>
    public void Step(float dt, int statePar, float wavePeriod)
    {
        if (!Ready || dt <= 0.0f) { return; }
        Rid set = _sets[statePar * 2 + _agePar];
        if (!set.IsValid) { return; }

        // Wall-clock: 1/CrashSeconds. Phase-driven: the same, stretched by how long a wave
        // takes relative to the 8 s reference, so the two agree at period 8.
        float crash = Mathf.Max(CrashSeconds, 1e-3f);
        float rate = PhaseDriven
            ? 1.0f / Mathf.Max(crash * (wavePeriod / 8.0f), 1e-3f)
            : 1.0f / crash;

        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _pipeline);
        _rd.ComputeListBindUniformSet(cl, set, 0);
        _rd.ComputeListSetPushConstant(cl, Pc(dt, rate), 32);
        _rd.ComputeListDispatch(cl, (uint)_groups, (uint)_groups, 1);
        _rd.ComputeListEnd();

        _agePar ^= 1;   // the pass wrote age_out; that is now current
    }

    // 8 floats = 32 B, matching pass_curl_age.glsl's Push block exactly.
    private byte[] Pc(float dt, float ageRate)
    {
        float[] v =
        {
            dt, BirthSteep, MinDepth, ageRate,
            1.0f / Mathf.Max(DecaySeconds, 1e-3f), _dx, G, Celerity,
        };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    /// <summary>
    /// Synchronous readback of the age field for the on-screen stats. Throttled by the caller
    /// — this is a full stall on the global device, so it is a debug affordance, not per-frame
    /// telemetry (solver-ledger.md §9a).
    ///
    /// It earns its cost: age is the one part of the system that cannot be judged from a still
    /// frame, and an unverified memory buffer is exactly what put the barrel's cross-section at
    /// age 0.42 in a field that never exceeded 0.05.
    /// </summary>
    public void CaptureStats()
    {
        if (!Ready) { return; }
        byte[] d = _rd.TextureGetData(_age[_agePar], 0);
        int texels = Math.Min(_n * _n, d.Length / 8);
        float max = 0.0f, sum = 0.0f;
        int live = 0;
        for (int i = 0; i < texels; i++)
        {
            float a = (float)BitConverter.ToHalf(d, i * 8);   // r channel of rgba16f
            if (a > max) { max = a; }
            if (a > 0.001f) { live++; sum += a; }
        }
        StatMax = max;
        StatMean = live > 0 ? sum / live : 0.0f;
        StatLive = live;
    }

    /// <summary>One record per slice: a point, a heading and a speed. This is what a line
    /// renderer needs — a screen-space distance test cannot produce it, which is why the
    /// previous debug view came out as contour blobs rather than vectors.</summary>
    public struct Slice
    {
        public Vector2 Pos;    // world XZ (metres)
        public Vector2 Dir;    // unit heading
        public float Age;
        public float Speed;    // |flow| proxy, from birth steepness
    }

    /// <summary>
    /// Read the age field back and reduce it to evenly spaced slice records.
    ///
    /// Binning is on a FIXED WORLD AXIS (z), not on the local crest tangent. That is the whole
    /// point: the tangent is recomputed per sample and rotates, so quantising against it gives
    /// boundaries that wander and a crest that shatters into a sawtooth. A world axis is stable,
    /// so a slice keeps its identity frame to frame — which is what makes the lines (and later
    /// the tubes) sit still instead of flickering.
    ///
    /// Averaging every crest cell in a bin also kills the per-pixel noise that made the
    /// screen-space version fragment: one mean position and one mean heading per bin, period.
    /// </summary>
    public System.Collections.Generic.List<Slice> ExtractSlices(float domain, float spacingM, float minAge)
    {
        var outp = new System.Collections.Generic.List<Slice>();
        if (!Ready) { return outp; }
        byte[] d = _rd.TextureGetData(_age[_agePar], 0);
        int texels = Math.Min(_n * _n, d.Length / 8);
        float cell = domain / _n;
        int bins = Math.Max(1, (int)(domain / Math.Max(spacingM, 0.1f)));

        var sx = new double[bins]; var sz = new double[bins];
        var dx = new double[bins]; var dz = new double[bins];
        var sa = new double[bins]; var ss = new double[bins];
        var cnt = new int[bins];

        for (int i = 0; i < texels; i++)
        {
            float age = (float)BitConverter.ToHalf(d, i * 8);
            if (age <= minAge) { continue; }
            int gx = i % _n, gz = i / _n;
            float wx = (gx + 0.5f) * cell, wz = (gz + 0.5f) * cell;
            int b = Math.Clamp((int)(wz / Math.Max(spacingM, 0.1f)), 0, bins - 1);
            sx[b] += wx; sz[b] += wz;
            dx[b] += (float)BitConverter.ToHalf(d, i * 8 + 4);
            dz[b] += (float)BitConverter.ToHalf(d, i * 8 + 6);
            sa[b] += age;
            ss[b] += (float)BitConverter.ToHalf(d, i * 8 + 2);
            cnt[b]++;
        }

        for (int b = 0; b < bins; b++)
        {
            if (cnt[b] < 4) { continue; }              // ignore specks; a slice needs support
            var dir = new Vector2((float)(dx[b] / cnt[b]), (float)(dz[b] / cnt[b]));
            if (dir.LengthSquared() < 1e-6f) { continue; }
            outp.Add(new Slice
            {
                Pos = new Vector2((float)(sx[b] / cnt[b]), (float)(sz[b] / cnt[b])),
                Dir = dir.Normalized(),
                Age = (float)(sa[b] / cnt[b]),
                Speed = (float)(ss[b] / cnt[b]),
            });
        }
        return outp;
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _sets) { if (s.IsValid) { _rd.FreeRid(s); } }   // sets before textures
        foreach (var t in _age) { if (t.IsValid) { _rd.FreeRid(t); } }
        if (_shader.IsValid) { _rd.FreeRid(_shader); }
    }

    private RDUniform Img(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(rid);
        return u;
    }

    private Rid MakeTex(RDTextureFormat fmt)
    {
        var t = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }
}
