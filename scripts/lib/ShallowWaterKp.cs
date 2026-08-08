using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// KP07 shallow-water solver (GPU) — a C# port of ../shorewaves/scripts/sim_controller.gd
// (BEACH scenario). State vector Q = (w, hu, hv, hc); the .glsl compute passes are copied
// verbatim into res://shaders/shorewaves/ and loaded as RDShaderFile. Everything here must
// be called from inside RenderingServer.CallOnRenderThread. Ping-pong parity is owned by
// the caller (the scene host), exactly as SimController splits it across threads.
//
// Per substep: fused pass_step (flux+integrate+friction+foam) → pass_boundary (wavemaker /
// walls). Per frame: pass_derived (normals/foam/aeration) → pass_ground (wet-sand memory) →
// pass_debug (throttled reduction, async readback).
public sealed class ShallowWaterKp
{
    private const float G = 9.81f;

    // BEACH defaults (scene 17). A host that wants a different grid passes n/dx/dt to the
    // constructor; every compute pass reads its extent from imageSize(), so the solver
    // itself is resolution-agnostic and nothing but these three numbers changes.
    public const int DefaultN = 608;
    public const float DefaultDx = 0.05f;
    public const float DefaultDt = 0.002f;
    public const float DefaultDomain = DefaultN * DefaultDx;   // 30.4 m
    public const int DefaultMaxSubsteps = 12;

    // grid, fixed at construction
    public readonly int N;
    public readonly float Dx;
    public readonly float Dt;
    public float Domain => N * Dx;
    private readonly int _groups;

    public int MaxSubsteps = DefaultMaxSubsteps;   // per-frame cap; a host may use fewer
    public float WaveMode = 1.0f;     // 1 = west wavemaker, 2 = + absorbing E/N/S (ocean)

    // live-tunable parameters (defaults = shorewaves BEACH). Scene 17 never assigns these,
    // so it keeps the exact defaults; only a host that sets them changes behavior.
    public float Theta = 1.3f;        // minmod limiter (1.0 smooth … 2.0 sharp)
    public float Manning = 0.03f;     // bed friction
    public float Kappa = 0.01f;       // desingularization (kappa^4 goes in the push)
    public float KFoam = 1.5f;        // foam injection scale
    public float WaveDepth0 = 1.2f;   // offshore still-water depth (m)
    public float WaveAmp = 0.18f;     // primary wave amplitude (m)
    public float WavePeriod = 5.0f;   // primary wave period (s)
    public float WaveRamp = 5.0f;     // cold-start ramp (s)
    public float SolitaryH = 0.36f;   // solitary wave height (m)
    public float SolitaryX0 = 3.0f;   // solitary launch x (m)
    public bool Incommensurate = false;  // [exp E] irrational wavemaker component ratios

    // Ground-memory timescales (seconds). These are LOOK controls, not physics: how long the
    // beach stays visibly damp after the swash pulls back, how long stranded foam lace
    // survives on the sand, and how fast returning water erases it. Defaults reproduce the
    // values that were hardcoded in pass_ground.glsl.
    public float DryTau = 45.0f;
    public float StrandTau = 10.0f;
    public float RewetTau = 0.12f;

    private readonly RenderingDevice _rd;

    private readonly Rid[] _state = new Rid[2];
    private Rid _bottom, _r, _derived;
    private readonly Rid[] _ground = new Rid[2];
    private Rid _dbgBuf;

    private Rid _shStep, _shBound, _shDebug, _shSoli, _shDerived, _shGround, _shPoke;
    private Rid _pStep, _pBound, _pDebug, _pSoli, _pDerived, _pGround, _pPoke;

    private readonly Rid[] _setStep = new Rid[2];
    private readonly Rid[] _setBound = new Rid[2];
    private readonly Rid[] _setDebug = new Rid[2];
    private readonly Rid[] _setSoli = new Rid[2];
    private readonly Rid[] _setPoke = new Rid[2];
    private readonly Rid[] _setDerived = new Rid[2];
    private readonly Rid[] _setGround = new Rid[4];   // index = p*2 + gp

    private int _frame;

    public bool Ready { get; private set; }
    public Rid[] StateRids => _state;
    public Rid[] GroundRids => _ground;
    public Rid BottomRid => _bottom;
    public Rid DerivedRid => _derived;

    // latest debug readback (main thread reads)
    public float DbgVolume, DbgMaxH, DbgMaxSpeed, DbgWet, DbgNan, DbgTime;

    public ShallowWaterKp(RenderingDevice rd, byte[] bottomBytes, byte[] stateBytes,
        int n = DefaultN, float dx = DefaultDx, float dt = DefaultDt)
    {
        _rd = rd;
        N = n;
        Dx = dx;
        Dt = dt;
        _groups = (n + 15) / 16;

        var fmt32 = Fmt(RenderingDevice.DataFormat.R32G32B32A32Sfloat);
        _state[0] = MakeTex(fmt32);
        _state[1] = MakeTex(fmt32);
        _bottom = MakeTex(fmt32);
        _r = MakeTex(fmt32);
        var fmt16 = Fmt(RenderingDevice.DataFormat.R16G16B16A16Sfloat);
        _derived = MakeTex(fmt16);
        _ground[0] = MakeTex(fmt16);
        _ground[1] = MakeTex(fmt16);

        _dbgBuf = _rd.StorageBufferCreate(64, new byte[64]);

        _shStep = Load("pass_step");
        _shBound = Load("pass_boundary");
        _shDebug = Load("pass_debug");
        _shSoli = Load("pass_solitary");
        _shDerived = Load("pass_derived");
        _shGround = Load("pass_ground");
        _shPoke = Load("pass_poke");
        if (!(_shStep.IsValid && _shBound.IsValid && _shDebug.IsValid && _shSoli.IsValid
              && _shDerived.IsValid && _shGround.IsValid && _shPoke.IsValid))
        {
            GD.PushError("[ShallowWaterKp] a compute pass failed to load — aborting init");
            return;
        }
        _pStep = _rd.ComputePipelineCreate(_shStep);
        _pBound = _rd.ComputePipelineCreate(_shBound);
        _pDebug = _rd.ComputePipelineCreate(_shDebug);
        _pSoli = _rd.ComputePipelineCreate(_shSoli);
        _pDerived = _rd.ComputePipelineCreate(_shDerived);
        _pGround = _rd.ComputePipelineCreate(_shGround);
        _pPoke = _rd.ComputePipelineCreate(_shPoke);

        for (int p = 0; p < 2; p++)
        {
            _setStep[p] = SetImg(_shStep, (0, _state[p]), (1, _bottom), (2, _r), (3, _state[p ^ 1]));
            _setBound[p] = SetImg(_shBound, (0, _state[p]), (1, _bottom));
            _setSoli[p] = SetImg(_shSoli, (0, _state[p]), (1, _bottom));
            _setPoke[p] = SetImg(_shPoke, (0, _state[p]), (1, _bottom));
            _setDerived[p] = SetImg(_shDerived, (0, _state[p]), (1, _bottom), (2, _derived));
            _setDebug[p] = _rd.UniformSetCreate(
                new Godot.Collections.Array<RDUniform> { Img(0, _state[p]), Img(1, _bottom), Ssbo(2, _dbgBuf) },
                _shDebug, 0);
        }
        for (int p = 0; p < 2; p++)
        {
            for (int gp = 0; gp < 2; gp++)
            {
                _setGround[p * 2 + gp] = SetImg(_shGround,
                    (0, _state[p]), (1, _bottom), (2, _ground[gp]), (3, _ground[gp ^ 1]), (4, _r));
            }
        }

        _rd.TextureUpdate(_bottom, 0, bottomBytes);
        _rd.TextureUpdate(_state[0], 0, stateBytes);
        _rd.TextureUpdate(_state[1], 0, stateBytes);

        Ready = true;
        GD.Print($"[ShallowWaterKp] GPU init ok ({N}x{N}, dx={Dx}, dt={Dt})");
    }

    // Advance `substeps` fixed dt steps starting at sim-time t0, reading parity `parity`
    // and ground parity `gparity`. Mirrors SimController._run_sim exactly.
    public void Step(int substeps, float t0, int parity, bool solitary, int gparity,
        bool poke = false, float pokeX = 0.0f, float pokeZ = 0.0f, float pokeRadius = 0.6f, float pokeStrength = 0.3f)
    {
        if (!Ready) { return; }
        int p = parity;
        long cl = _rd.ComputeListBegin();

        if (poke)
        {
            Dispatch(cl, _pPoke, _setPoke[p], Pc(pokeX, pokeZ, pokeRadius, pokeStrength, Dx, 0.0f, 0.0f, 0.0f));
            _rd.ComputeListAddBarrier(cl);
        }

        if (solitary)
        {
            float d0 = WaveDepth0;
            float ks = Mathf.Sqrt(3.0f * SolitaryH / (4.0f * d0 * d0 * d0));
            float cs = Mathf.Sqrt(G * (SolitaryH + d0));
            Dispatch(cl, _pSoli, _setSoli[p], Pc(SolitaryH, SolitaryX0, ks, cs, Dx, 0, 0, 0));
            _rd.ComputeListAddBarrier(cl);
        }

        for (int k = 0; k < substeps; k++)
        {
            float t = t0 + k * Dt;
            Dispatch(cl, _pStep, _setStep[p], SimPc(t));
            _rd.ComputeListAddBarrier(cl);
            Dispatch(cl, _pBound, _setBound[p ^ 1], BoundPc(t + Dt));
            _rd.ComputeListAddBarrier(cl);
            p ^= 1;
        }

        float tEnd = t0 + substeps * Dt;
        byte[] dcb = SimPc(tEnd);
        Dispatch(cl, _pDerived, _setDerived[p], dcb);
        _rd.ComputeListAddBarrier(cl);

        // ground pass: the dt slot carries the frame delta, not the substep dt
        // 12 floats here, not 8: pass_ground declares the three memory taus on the end of its
        // block. Every other pass still takes the 8-float SimPc.
        Dispatch(cl, _pGround, _setGround[p * 2 + gparity],
            Pc(substeps * Dt, Dx, G, Theta, Mathf.Pow(Kappa, 4.0f), Manning, tEnd, KFoam,
               DryTau, StrandTau, RewetTau, 0.0f));
        _rd.ComputeListAddBarrier(cl);

        _frame++;
        bool doDebug = _frame % 30 == 0;
        if (doDebug)
        {
            Dispatch(cl, _pDebug, _setDebug[p], dcb, single: true);
        }
        _rd.ComputeListEnd();

        if (doDebug)
        {
            _rd.BufferGetDataAsync(_dbgBuf, Callable.From((byte[] data) => OnDebug(data)));
        }
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in _setStep) { FreeIf(s); }
        foreach (var s in _setBound) { FreeIf(s); }
        foreach (var s in _setDebug) { FreeIf(s); }
        foreach (var s in _setSoli) { FreeIf(s); }
        foreach (var s in _setDerived) { FreeIf(s); }
        foreach (var s in _setGround) { FreeIf(s); }
        foreach (var s in _setPoke) { FreeIf(s); }
        foreach (var t in new[] { _state[0], _state[1], _bottom, _r, _derived, _ground[0], _ground[1], _dbgBuf })
        {
            FreeIf(t);
        }
        foreach (var sh in new[] { _shStep, _shBound, _shDebug, _shSoli, _shDerived, _shGround, _shPoke })
        {
            FreeIf(sh);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private void Dispatch(long cl, Rid pipe, Rid set, byte[] pc, bool single = false)
    {
        _rd.ComputeListBindComputePipeline(cl, pipe);
        _rd.ComputeListBindUniformSet(cl, set, 0);
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        if (single) { _rd.ComputeListDispatch(cl, 1, 1, 1); }
        else { _rd.ComputeListDispatch(cl, (uint)_groups, (uint)_groups, 1); }
    }

    private byte[] SimPc(float t) => Pc(Dt, Dx, G, Theta, Mathf.Pow(Kappa, 4.0f), Manning, t, KFoam);
    private byte[] BoundPc(float t) => Pc(t, WaveMode, WaveAmp, WavePeriod, WaveDepth0, WaveRamp, Dx, G, Incommensurate ? 1.0f : 0.0f, 0.0f, 0.0f, 0.0f);

    private static byte[] Pc(params float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];   // 8 floats = 32 B (16-byte aligned)
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    private void OnDebug(byte[] data)
    {
        var f = new float[data.Length / sizeof(float)];
        Buffer.BlockCopy(data, 0, f, 0, data.Length);
        DbgVolume = f[0]; DbgMaxH = f[1]; DbgMaxSpeed = f[2];
        DbgWet = f[3]; DbgNan = f[4]; DbgTime = f[5];
        GD.Print($"[sim] t={f[5]:0.00} vol={f[0]:0.000} m3 max_h={f[1]:0.000} max_u={f[2]:0.00} wet={(int)f[3]} nan={(int)f[4]}");
    }

    private RDUniform Img(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(rid);
        return u;
    }

    private RDUniform Ssbo(int binding, Rid rid)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
        u.AddId(rid);
        return u;
    }

    private Rid SetImg(Rid shader, params (int binding, Rid rid)[] items)
    {
        var arr = new Godot.Collections.Array<RDUniform>();
        foreach (var (b, rid) in items) { arr.Add(Img(b, rid)); }
        return _rd.UniformSetCreate(arr, shader, 0);
    }

    private Rid Load(string pass)
    {
        var sf = GD.Load<RDShaderFile>($"res://shaders/shorewaves/{pass}.glsl");
        if (sf == null) { GD.PushError($"[ShallowWaterKp] missing shader {pass}.glsl"); return default; }
        var spirv = sf.GetSpirV();
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[ShallowWaterKp] {pass} compile error:\n{err}"); return default; }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private RDTextureFormat Fmt(RenderingDevice.DataFormat format) => new()
    {
        Format = format,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = (uint)N, Height = (uint)N, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
            | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyToBit
            | RenderingDevice.TextureUsageBits.CanUpdateBit,
    };

    private Rid MakeTex(RDTextureFormat fmt)
    {
        var t = _rd.TextureCreate(fmt, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureClear(t, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        return t;
    }

    private void FreeIf(Rid r)
    {
        if (r.IsValid) { _rd.FreeRid(r); }
    }
}
