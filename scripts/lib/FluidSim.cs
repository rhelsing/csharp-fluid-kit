using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Stam-style stable-fluids solver (GPU, matrix-free) — a sibling to GpuStampSolver.
// Phase 3c is where the single-scalar stamp solver stops dropping in: a fluid needs a
// velocity field, advection, and a projection, not one A x = b. So this is a purpose-
// built pipeline — BUT the pressure-projection step is the very same matrix-free Jacobi
// relaxation the stamp solvers use, here solving ∇²p = div. Per tick:
//   add force+dye (+buoyancy) → advect velocity (semi-Lagrangian) → divergence →
//   K Jacobi pressure iters (warm-started) → subtract ∇p (divergence-free) → advect dye.
// All persistent state lives in the "A" textures; "B"/div/pressure are scratch. Call
// every method from inside RenderingServer.CallOnRenderThread.
public sealed class FluidSim
{
    private const string Dir = "res://shaders/fluid/";

    private readonly RenderingDevice _rd;
    private readonly Vector2I _grid;
    private readonly uint _gx, _gy;

    private Rid _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD;
    private Rid _pAdd, _pAdvV, _pDiv, _pJac, _pGrad, _pAdvD;
    private Rid _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div;

    private Rid _add0, _add1;
    private Rid _advV0, _advV1;
    private Rid _div0, _div1;
    private Rid _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2;
    private Rid _grad0, _grad1;
    private Rid _advD0, _advD1, _advD2;

    // MacCormack (optional): second-order dye advection — pass 1 is the standard
    // semi-Lagrangian, pass 2 corrects with the reverse round-trip error (limited).
    private Rid _sMc, _pMc, _dyeC;
    private Rid _mc0, _mc1, _mc2, _mc3;
    private bool _mcReady;

    // Viscosity (optional): implicit Jacobi diffusion of velocity, ping-ponged with
    // the pre-diffusion field held fixed in _velC.
    private Rid _sVisc, _pVisc, _velC;
    private Rid _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2;
    private bool _viscReady;

    public bool Ready { get; private set; }
    public Rid DyeRid => _dyeA;
    public Rid VelRid => _velA;

    // The divergence texture — scene 250's artifact view (docs/artifacts-250.md).
    // NOTE what this holds depends on MeasureResidual: normally it is the divergence of
    // the PRE-projection velocity (the solve's right-hand side). Set MeasureResidual to
    // re-run the divergence pass after the gradient subtract so it holds the true
    // post-projection RESIDUAL — the compressibility truncated Jacobi left behind.
    public Rid DivRid => _div;

    // Opt-in extra divergence dispatch at the end of the projection (see DivRid).
    // Off by default: scenes 07/18 keep the original pipeline, byte-identical.
    public bool MeasureResidual { get; set; }

    // addKernel swaps the source stage for this instance (its push constants are
    // opaque to Step) — e.g. scene 18's fs_add_milk. extras compiles the MacCormack
    // + viscosity stages; default false = the original pipeline, byte-identical.
    public FluidSim(RenderingDevice rd, Vector2I grid, string addKernel = "fs_add_source", bool extras = false)
    {
        _rd = rd;
        _grid = grid;
        _gx = (uint)((grid.X - 1) / 8 + 1);
        _gy = (uint)((grid.Y - 1) / 8 + 1);

        _sAdd = Compile(addKernel);
        _sAdvV = Compile("fs_advect_vel");
        _sDiv = Compile("fs_divergence");
        _sJac = Compile("fs_pressure_jacobi");
        _sGrad = Compile("fs_gradient_sub");
        _sAdvD = Compile("fs_advect_dye");
        if (!(_sAdd.IsValid && _sAdvV.IsValid && _sDiv.IsValid && _sJac.IsValid && _sGrad.IsValid && _sAdvD.IsValid))
        {
            return;
        }
        _pAdd = _rd.ComputePipelineCreate(_sAdd);
        _pAdvV = _rd.ComputePipelineCreate(_sAdvV);
        _pDiv = _rd.ComputePipelineCreate(_sDiv);
        _pJac = _rd.ComputePipelineCreate(_sJac);
        _pGrad = _rd.ComputePipelineCreate(_sGrad);
        _pAdvD = _rd.ComputePipelineCreate(_sAdvD);

        var rg = Fmt(RenderingDevice.DataFormat.R32G32Sfloat);
        var r = Fmt(RenderingDevice.DataFormat.R32Sfloat);
        _velA = MakeTex(rg); _velB = MakeTex(rg);
        _dyeA = MakeTex(r); _dyeB = MakeTex(r);
        _pA = MakeTex(r); _pB = MakeTex(r); _div = MakeTex(r);

        _add0 = Set(_velA, _sAdd, 0); _add1 = Set(_dyeA, _sAdd, 1);
        _advV0 = Set(_velA, _sAdvV, 0); _advV1 = Set(_velB, _sAdvV, 1);
        _div0 = Set(_velA, _sDiv, 0); _div1 = Set(_div, _sDiv, 1);
        _jacAB0 = Set(_pA, _sJac, 0); _jacAB1 = Set(_div, _sJac, 1); _jacAB2 = Set(_pB, _sJac, 2);
        _jacBA0 = Set(_pB, _sJac, 0); _jacBA1 = Set(_div, _sJac, 1); _jacBA2 = Set(_pA, _sJac, 2);
        _grad0 = Set(_velA, _sGrad, 0); _grad1 = Set(_pA, _sGrad, 1);
        _advD0 = Set(_dyeA, _sAdvD, 0); _advD1 = Set(_velA, _sAdvD, 1); _advD2 = Set(_dyeB, _sAdvD, 2);

        if (extras)
        {
            _sMc = Compile("fs_maccormack");
            _sVisc = Compile("fs_diffuse_vel");
            if (_sMc.IsValid)
            {
                _pMc = _rd.ComputePipelineCreate(_sMc);
                _dyeC = MakeTex(r);
                _mc0 = Set(_dyeA, _sMc, 0);   // φⁿ
                _mc1 = Set(_dyeB, _sMc, 1);   // forward result
                _mc2 = Set(_velA, _sMc, 2);
                _mc3 = Set(_dyeC, _sMc, 3);
                _mcReady = true;
            }
            if (_sVisc.IsValid)
            {
                _pVisc = _rd.ComputePipelineCreate(_sVisc);
                _velC = MakeTex(rg);
                _visc0 = Set(_velC, _sVisc, 0);                          // v₀ (fixed)
                _viscAB1 = Set(_velA, _sVisc, 1); _viscAB2 = Set(_velB, _sVisc, 2);
                _viscBA1 = Set(_velB, _sVisc, 1); _viscBA2 = Set(_velA, _sVisc, 2);
                _viscReady = true;
            }
        }

        Ready = true;
    }

    // iters is forced even so the ping-ponged pressure ends back in _pA (which gradient
    // subtract reads and which warm-starts next frame). viscIters/viscPc and macCormack
    // engage the optional stages (ctor extras: true); defaults = the original pipeline.
    public void Step(byte[] addPc, byte[] advVPc, byte[] simPc, byte[] advDPc, int iters,
        int viscIters = 0, byte[]? viscPc = null, bool macCormack = false, byte[]? mcPc = null)
    {
        if (!Ready) { return; }
        if ((iters & 1) == 1) { iters++; }
        var size = new Vector3(_grid.X, _grid.Y, 1);

        // 1. inject force + dye + buoyancy (in place on velA/dyeA)
        RunOne(_pAdd, addPc, (_add0, 0u), (_add1, 1u));

        // 2. self-advect velocity velA -> velB, then copy back so velA stays "current"
        RunOne(_pAdvV, advVPc, (_advV0, 0u), (_advV1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        // 2b. (optional) implicit viscosity: v₀ → _velC, Jacobi ping-pong, ends in velA
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

        // 3. divergence -> K Jacobi pressure iters -> subtract gradient (one compute list)
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
        // 3b. (optional) re-measure divergence of the NOW-projected velocity, overwriting
        // _div with the residual. Same pass, same sets — the solve is finished with _div,
        // so this is safe to clobber. One dispatch; only paid when the flag is on.
        if (MeasureResidual)
        {
            Bind(cl, _pDiv, simPc, (_div0, 0u), (_div1, 1u));
            _rd.ComputeListAddBarrier(cl);
        }
        _rd.ComputeListEnd();

        // 4. advect dye by the divergence-free velocity, copy back
        if (_mcReady && macCormack && mcPc != null)
        {
            // pass 1: standard SL forward (dissip 1 — real dissip applied in pass 2)
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            // pass 2: reverse round trip, limited half-error correction → dyeC → dyeA
            RunOne(_pMc, mcPc, (_mc0, 0u), (_mc1, 1u), (_mc2, 2u), (_mc3, 3u));
            _rd.TextureCopy(_dyeC, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
        else
        {
            RunOne(_pAdvD, advDPc, (_advD0, 0u), (_advD1, 1u), (_advD2, 2u));
            _rd.TextureCopy(_dyeB, _dyeA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
        }
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
        _rd.ComputeListDispatch(cl, _gx, _gy, 1);
    }

    public void Free()
    {
        Ready = false;
        foreach (var s in new[]
        {
            _add0, _add1, _advV0, _advV1, _div0, _div1,
            _jacAB0, _jacAB1, _jacAB2, _jacBA0, _jacBA1, _jacBA2,
            _grad0, _grad1, _advD0, _advD1, _advD2,
            _mc0, _mc1, _mc2, _mc3, _visc0, _viscAB1, _viscAB2, _viscBA1, _viscBA2,
        })
        {
            if (s.IsValid) { _rd.FreeRid(s); }
        }
        foreach (var t in new[] { _velA, _velB, _dyeA, _dyeB, _pA, _pB, _div, _dyeC, _velC })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _sAdd, _sAdvV, _sDiv, _sJac, _sGrad, _sAdvD, _sMc, _sVisc })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private Rid Compile(string name)
    {
        string src = FileAccess.GetFileAsString(Dir + name + ".glslinc");
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[FluidSim] could not read {name}"); return default; }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[FluidSim] {name} compile error:\n{err}");
            return default;
        }
        return _rd.ShaderCreateFromSpirV(spirv);
    }

    private static RDTextureFormat Fmt(RenderingDevice.DataFormat format) => new()
    {
        Format = format,
        TextureType = RenderingDevice.TextureType.Type2D,
        Width = 0, Height = 0, Depth = 1, ArrayLayers = 1, Mipmaps = 1,
        UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
            | RenderingDevice.TextureUsageBits.StorageBit
            | RenderingDevice.TextureUsageBits.CanCopyFromBit
            | RenderingDevice.TextureUsageBits.CanCopyToBit,
    };

    private Rid MakeTex(RDTextureFormat tf)
    {
        tf.Width = (uint)_grid.X;
        tf.Height = (uint)_grid.Y;
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
