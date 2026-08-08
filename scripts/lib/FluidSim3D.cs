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

    // optional extras (ctor extras: true): MacCormack dye advection + implicit viscosity
    private Rid _sMc, _pMc, _dyeC;
    private Rid _mc0, _mc1, _mc2, _mc3;
    private bool _mcReady;
    private Rid _sVisc, _pVisc, _velC;
    private Rid _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2;
    private bool _viscReady;
    private Rid _sConf, _pConf, _conf0, _conf1;
    private bool _confReady;

    // ---- pressure solver swap (solver-ledger.md §10) ----------------------------
    // The projection step ∇²p = div is the ONLY genuine linear solve in the 3D fluid, and
    // until now it was plain Jacobi — the solver the 3D bench ranks worst above 64³
    // (8.75e-04 at 96³ against multigrid's 1.49e-06 at HALF the cost). MgDeepSolver3D drives
    // the identical operator via stamp_pressure_3d, which reproduces f3_pressure_jacobi
    // exactly at beta = 1, so this is a swap and not a reformulation.
    private MgDeepSolver3D? _mg;
    private const string PressureStamp = "res://shaders/stamp3d/stamp_pressure_3d.glslinc";

    /// <summary>Off = the original Jacobi loop. The A/B is the point; both stay reachable.</summary>
    public bool UseMultigrid { get; set; }
    public bool MultigridReady => _mg is { Ready: true };
    public int MultigridLevels => _mg?.Levels ?? 0;
    public float LastResidual => _mg?.LastResidual ?? 0.0f;

    public bool Ready { get; private set; }
    public Rid DensityRid => _dyeA;
    public Rid VelocityRid => _velA;

    // Scene 250b's artifact view: the divergence volume, raymarched as a second channel
    // in #E23D6D (docs/artifacts-250.md). Holds the solve's right-hand side normally, or
    // the true post-projection RESIDUAL when MeasureDivergence is on — see below.
    public Rid DivRid => _div;

    // Opt-in: re-run the divergence pass AFTER the gradient subtract, so DivRid holds the
    // compressibility the truncated solve left behind rather than the divergence it was
    // handed. Distinct from Step's measureResidual arg, which is the multigrid's scalar
    // residual NORM. Off by default: scenes 09/32 keep the original pipeline.
    public bool MeasureDivergence { get; set; }

    /// <summary>
    /// Build the multigrid pressure path over the EXISTING pressure/divergence textures —
    /// external-state mode, so p is warm-started from last frame and the solution lands back
    /// in _pA where f3_gradient_sub already reads it. betaExp 2, not the wave stamp's 3: here
    /// the whole operator scales as 1/dx² uniformly, with no unscaled identity term to compete
    /// with (see stamp_pressure_3d and solver-ledger.md §10c).
    /// </summary>
    public void EnableMultigrid(int betaExp = 2)
    {
        if (!Ready || _mg != null) { return; }
        _mg = new MgDeepSolver3D(_rd, _grid, PressureStamp, betaExp, _pA, _div);
        if (!_mg.Ready) { GD.PushError("[FluidSim3D] multigrid pressure path failed to init"); }
    }

    public FluidSim3D(RenderingDevice rd, Vector3I grid, bool extras = false)
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

        if (extras)
        {
            _sMc = Compile("f3_maccormack");
            _sVisc = Compile("f3_diffuse_vel");
            if (_sMc.IsValid)
            {
                _pMc = _rd.ComputePipelineCreate(_sMc);
                _dyeC = MakeTex(r);
                _mc0 = Set(_dyeA, _sMc, 0);
                _mc1 = Set(_dyeB, _sMc, 1);
                _mc2 = Set(_velA, _sMc, 2);
                _mc3 = Set(_dyeC, _sMc, 3);
                _mcReady = true;
            }
            if (_sVisc.IsValid)
            {
                _pVisc = _rd.ComputePipelineCreate(_sVisc);
                _velC = MakeTex(rgba);
                _visc0 = Set(_velC, _sVisc, 0);
                _viscAB1 = Set(_velA, _sVisc, 1); _viscAB2 = Set(_velB, _sVisc, 2);
                _viscBA1 = Set(_velB, _sVisc, 1); _viscBA2 = Set(_velA, _sVisc, 2);
                _viscReady = true;
            }
            _sConf = Compile("f3_confine");
            if (_sConf.IsValid)
            {
                _pConf = _rd.ComputePipelineCreate(_sConf);
                _conf0 = Set(_velA, _sConf, 0);
                _conf1 = Set(_velB, _sConf, 1);
                _confReady = true;
            }
        }

        Ready = true;
    }

    public void Step(byte[] addPc, byte[] advVPc, byte[] simPc, byte[] advDPc, int iters,
        bool measureResidual = false,
        int viscIters = 0, byte[]? viscPc = null, bool macCormack = false, byte[]? mcPc = null,
        byte[][]? extraAdds = null, byte[]? confinePc = null)
    {
        if (!Ready) { return; }
        if ((iters & 1) == 1) { iters++; }
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);

        RunOne(_pAdd, addPc, (_add0, 0u), (_add1, 1u));
        if (extraAdds != null)
        {
            // extra source passes (same kernel, scene-supplied pcs) — e.g. scene 32's
            // orbiting spoon stir and whole-glass jostle
            foreach (var e in extraAdds) { RunOne(_pAdd, e, (_add0, 0u), (_add1, 1u)); }
        }
        RunOne(_pAdvV, advVPc, (_advV0, 0u), (_advV1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        // (optional) implicit viscosity: v₀ → _velC, Jacobi ping-pong, ends in velA
        if (_viscReady && viscIters > 0 && viscPc != null)
        {
            if ((viscIters & 1) == 1) { viscIters++; }
            _rd.TextureCopy(_velA, _velC, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
            long vcl = _rd.ComputeListBegin();
            for (int k = 0; k < viscIters; k++)
            {
                if (k % 2 == 0) { Bind(vcl, _pVisc, viscPc, (_visc0, 0u), (_viscAB1, 1u), (_viscAB2, 2u)); }
                else { Bind(vcl, _pVisc, viscPc, (_visc0, 0u), (_viscBA1, 1u), (_viscBA2, 2u)); }
                _rd.ComputeListAddBarrier(vcl);
            }
            _rd.ComputeListEnd();
        }

        if (UseMultigrid && _mg is { Ready: true })
        {
            // MgDeepSolver3D opens its own compute lists, so the projection splits into three
            // submissions instead of one. Same three stages, same textures, same order.
            RunOne(_pDiv, simPc, (_div0, 0u), (_div1, 1u));
            _mg.Step(PressurePc(), iters, measureResidual);
            RunOne(_pGrad, simPc, (_grad0, 0u), (_grad1, 1u));
        }
        else
        {
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
        }

        // (optional) re-measure divergence of the now-projected velocity → _div becomes the
        // residual. Safe to clobber: the solve is done with it and next tick rewrites it
        // first. One dispatch, and only when the flag is on. Same for both solver paths.
        if (MeasureDivergence) { RunOne(_pDiv, simPc, (_div0, 0u), (_div1, 1u)); }

        if (_mcReady && macCormack && mcPc != null)
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));   // pass 1 (dissip 1)
            RunOne(_pMc, mcPc, (_mc0, 0u), (_mc1, 1u), (_mc2, 2u), (_mc3, 3u)); // pass 2 → dyeC
            _rd.TextureCopy(_dyeC, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
        else
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
    }

    // stamp_pressure_3d's push constant: vec4 size + beta + 3 pad = 32 B. beta = 1 at the fine
    // level is what makes this operator identical to f3_pressure_jacobi.
    private byte[] PressurePc()
    {
        float[] v = { _grid.X, _grid.Y, _grid.Z, 0f, 1f, 0f, 0f, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    // Full velocity readback (sync): 4 floats/cell (vx,vy,vz,0), x-fastest.
    // Additive only — no existing caller changes. The free-surface scenes treat the dye field
    // as LIQUID FRACTION (1 = water, 0 = air) and need it on the CPU to polygonize the 0.5
    // isosurface into a mesh.
    public float[] ReadDensity()
    {
        if (!Ready) { return Array.Empty<float>(); }
        byte[] data = _rd.TextureGetData(_dyeA, 0);
        int n = _grid.X * _grid.Y * _grid.Z;
        var f = new float[n];
        Buffer.BlockCopy(data, 0, f, 0, Math.Min(data.Length, n * 4));
        return f;
    }

    // Seed the liquid-fraction field directly (staging texture -> TextureCopy, since the sim's
    // own textures lack CAN_UPDATE_BIT but do have CAN_COPY_TO_BIT).
    public void WriteDensity(float[] f)
    {
        if (!Ready) { return; }
        var bytes = new byte[f.Length * 4];
        Buffer.BlockCopy(f, 0, bytes, 0, bytes.Length);
        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type3D,
            Width = (uint)_grid.X, Height = (uint)_grid.Y, Depth = (uint)_grid.Z,
            ArrayLayers = 1, Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.CanUpdateBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.StorageBit,
        };
        Rid stage = _rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _rd.TextureUpdate(stage, 0, bytes);
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);
        _rd.TextureCopy(stage, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.TextureCopy(stage, _dyeB, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        _rd.FreeRid(stage);
    }

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
        _mg?.Free();      // borrows _pA/_div; frees its own pyramid only
        _mg = null;
        foreach (var s in new[]
        {
            _add0, _add1, _advV0, _advV1, _div0, _div1,
            _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2, _grad0, _grad1, _advD0, _advD1, _advD2,
            _mc0, _mc1, _mc2, _mc3, _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2, _conf0, _conf1,
        })
        {
            if (s.IsValid) { _rd.FreeRid(s); }
        }
        foreach (var t in new[] { _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div, _dyeC, _velC })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD, _sMc, _sVisc, _sConf })
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
