using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// WaveSim3D — free-surface water in a volume, projected by MgDeepSolver3D.
//
// THE POINT. A shallow-water heightfield is y = h(x,z): single-valued, so an overturning lip
// is not merely hard to compute, it is unrepresentable. This carries the water as a level set
// in a 3D velocity field, so the crest can throw forward over the trough. What drives it over
// is incompressibility — the pressure projection — which is the linear solve KP07 does not
// have and MgDeep3D is for (solver-ledger.md §10).
//
// Per step, in order:
//   1. w3_mask       classify FLUID / AIR / SOLID from phi + the static bed
//   2. f3_advect_vel semi-Lagrangian velocity advection (reused VERBATIM from FluidSim3D)
//   3. w3_forces     gravity + wavemaker + solid no-slip — deliberately makes v divergent
//   4. w3_divergence masked div(v)
//   5. MgDeepSolver3D  solve the masked Poisson via stamp_pressure_free_3d
//   6. w3_gradient   v -= grad(p), with p = 0 across the free surface
//   7. w3_advect_phi carry the surface along
//
// Everything after step 1 is standard Chorin projection; the only unusual thing is that the
// solver is a stamp-driven multigrid rather than a hand-written Jacobi loop, which is what
// lets the operator change (free surface, bed, later variable density) without the solver
// changing at all.
//
// Grid convention: x = across-shore (0 = deep, wavemaker edge), y = UP, z = along-shore.
// Units are CELLS, not metres — gravity and velocities are in cells/s, which keeps the
// pressure stamp at beta = 1 and the whole thing scale-free.
public sealed class WaveSim3D
{
    private const string W3 = "res://shaders/wave3d/";
    private const string PressureStamp = "res://shaders/stamp3d/stamp_pressure_free_3d.glslinc";

    private readonly RenderingDevice _rd;
    private readonly Vector3I _grid;
    private readonly uint _gx, _gy, _gz;

    private Rid _sMask, _sAdvV, _sForce, _sDiv, _sGrad, _sAdvP;
    private Rid _pMask, _pAdvV, _pForce, _pDiv, _pGrad, _pAdvP;

    private Rid _velA, _velB, _phiA, _phiB, _bed, _mask, _div;
    private Rid _mkPhi, _mkBed, _mkMask;
    private Rid _avA0, _avA1, _fcV0, _fcM1;
    private Rid _dvV0, _dvD1, _dvM2;
    private Rid _grV0, _grP1, _grM2;
    private Rid _apPhiA0, _apVel1, _apPhiB2;

    private MgDeepSolver3D? _mg;

    public bool Ready { get; private set; }
    public Rid PhiRid => _phiA;
    public Rid MaskRid => _mask;
    public int Levels => _mg?.Levels ?? 0;
    public float LastResidual => _mg?.LastResidual ?? 0.0f;

    public WaveSim3D(RenderingDevice rd, Vector3I grid, int maxLevels = int.MaxValue)
    {
        _rd = rd;
        _grid = grid;
        _gx = (uint)((grid.X - 1) / 4 + 1);
        _gy = (uint)((grid.Y - 1) / 4 + 1);
        _gz = (uint)((grid.Z - 1) / 4 + 1);

        _sMask = Compile(W3 + "w3_mask.glslinc");
        _sAdvV = Compile("res://shaders/fluid3d/f3_advect_vel.glslinc");   // reused verbatim
        _sForce = Compile(W3 + "w3_forces.glslinc");
        _sDiv = Compile(W3 + "w3_divergence.glslinc");
        _sGrad = Compile(W3 + "w3_gradient.glslinc");
        _sAdvP = Compile(W3 + "w3_advect_phi.glslinc");
        if (!(_sMask.IsValid && _sAdvV.IsValid && _sForce.IsValid && _sDiv.IsValid
              && _sGrad.IsValid && _sAdvP.IsValid))
        {
            return;
        }
        _pMask = _rd.ComputePipelineCreate(_sMask);
        _pAdvV = _rd.ComputePipelineCreate(_sAdvV);
        _pForce = _rd.ComputePipelineCreate(_sForce);
        _pDiv = _rd.ComputePipelineCreate(_sDiv);
        _pGrad = _rd.ComputePipelineCreate(_sGrad);
        _pAdvP = _rd.ComputePipelineCreate(_sAdvP);

        var rgba = Fmt(RenderingDevice.DataFormat.R32G32B32A32Sfloat);
        var r = Fmt(RenderingDevice.DataFormat.R32Sfloat);
        _velA = MakeTex(rgba); _velB = MakeTex(rgba);
        _phiA = MakeTex(r); _phiB = MakeTex(r);
        _bed = MakeTex(r); _mask = MakeTex(r); _div = MakeTex(r);

        _mkPhi = Set(_phiA, _sMask, 0); _mkBed = Set(_bed, _sMask, 1); _mkMask = Set(_mask, _sMask, 2);
        _avA0 = Set(_velA, _sAdvV, 0); _avA1 = Set(_velB, _sAdvV, 1);
        _fcV0 = Set(_velA, _sForce, 0); _fcM1 = Set(_mask, _sForce, 1);
        _dvV0 = Set(_velA, _sDiv, 0); _dvD1 = Set(_div, _sDiv, 1); _dvM2 = Set(_mask, _sDiv, 2);
        _apPhiA0 = Set(_phiA, _sAdvP, 0); _apVel1 = Set(_velA, _sAdvP, 1); _apPhiB2 = Set(_phiB, _sAdvP, 2);

        // External-state mode: set 0 = the mask, set 1 = the divergence, and the pressure
        // never leaves the solver — it stays in SolutionRid and warm-starts the next frame.
        _mg = new MgDeepSolver3D(_rd, _grid, PressureStamp, 2, _mask, _div, maxLevels);
        if (!_mg.Ready) { GD.PushError("[WaveSim3D] pressure multigrid failed to init"); return; }

        _grV0 = Set(_velA, _sGrad, 0); _grP1 = Set(_mg.SolutionRid, _sGrad, 1); _grM2 = Set(_mask, _sGrad, 2);

        Ready = true;
        GD.Print($"[WaveSim3D] {grid.X}x{grid.Y}x{grid.Z} · {_mg.Levels} mg levels");
    }

    /// <summary>
    /// Upload the initial water body and the bed. phi > 0.5 is water; bed is a height in
    /// CELLS stored in row y = 0 of a full volume (wasteful, but it keeps every field one
    /// image3D and the mask kernel a single lookup).
    /// </summary>
    public void Init(float[] phi, float[] bedColumn)
    {
        if (!Ready) { return; }
        var pb = new byte[phi.Length * 4];
        Buffer.BlockCopy(phi, 0, pb, 0, pb.Length);
        _rd.TextureUpdate(_phiA, 0, pb);

        int n = _grid.X * _grid.Y * _grid.Z;
        var bed = new float[n];
        for (int z = 0; z < _grid.Z; z++)
        {
            for (int x = 0; x < _grid.X; x++) { bed[Idx(x, 0, z)] = bedColumn[z * _grid.X + x]; }
        }
        var bb = new byte[n * 4];
        Buffer.BlockCopy(bed, 0, bb, 0, bb.Length);
        _rd.TextureUpdate(_bed, 0, bb);
    }

    private int Idx(int x, int y, int z) => (z * _grid.Y + y) * _grid.X + x;

    public void Step(float dt, float gravity, float waveAmp, float waveOmega, float time,
        float paddleW, int iters, bool measureResidual)
    {
        if (!Ready || _mg == null) { return; }
        var size = new Vector3(_grid.X, _grid.Y, _grid.Z);

        Run(_pMask, Pc32(0f), (_mkPhi, 0u), (_mkBed, 1u), (_mkMask, 2u));

        Run(_pAdvV, PcAdvV(dt, 1f), (_avA0, 0u), (_avA1, 1u));
        _rd.TextureCopy(_velB, _velA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);

        Run(_pForce, PcForces(dt, gravity, waveAmp, waveOmega, time, paddleW), (_fcV0, 0u), (_fcM1, 1u));
        Run(_pDiv, Pc32(0f), (_dvV0, 0u), (_dvD1, 1u), (_dvM2, 2u));

        _mg.Step(PressurePc(), iters, measureResidual);

        Run(_pGrad, Pc32(0f), (_grV0, 0u), (_grP1, 1u), (_grM2, 2u));

        Run(_pAdvP, Pc32(dt), (_apPhiA0, 0u), (_apVel1, 1u), (_apPhiB2, 2u));
        _rd.TextureCopy(_phiB, _phiA, Vector3.Zero, Vector3.Zero, size, 0, 0, 0, 0);
    }

    /// <summary>Total water volume, as a fraction of cells with phi > 0.5. The lake-at-rest
    /// check reads this: a correct projection neither gains nor loses water.</summary>
    public float WaterFraction()
    {
        if (!Ready) { return 0f; }
        byte[] d = _rd.TextureGetData(_phiA, 0);
        int n = _grid.X * _grid.Y * _grid.Z;
        var f = new float[n];
        Buffer.BlockCopy(d, 0, f, 0, Math.Min(d.Length, n * 4));
        int w = 0;
        for (int i = 0; i < n; i++) { if (f[i] > 0.5f) { w++; } }
        return (float)w / n;
    }

    private byte[] PressurePc()
    {
        float[] v = { _grid.X, _grid.Y, _grid.Z, 0f, 1f, 0f, 0f, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    // THREE push-constant sizes, because these kernels declare three different blocks and
    // Godot validates the exact byte count: "requires (N) ... supplied (M)" (solver-ledger.md
    // §3a). One shared builder is what got this wrong the first time — 48 B handed to a 32 B
    // block, on four of the six passes.
    //   f3_advect_vel : vec4 size + dt + dissip                       = 32 B (see below)
    //   w3_mask / w3_divergence / w3_gradient : vec4 size + vec4 pad   = 32 B
    //   w3_advect_phi : vec4 size + dt + 3 pad                         = 32 B
    //   w3_forces     : vec4 size + 8 floats                           = 48 B
    // 32, not the 24 the fields add up to: Godot rounds a push-constant block UP to a
    // 16-byte multiple, so `vec4 + 2 floats` reports as 32. Counting the declared fields and
    // trusting the sum is the trap — read the number in the error message, not the struct.
    private byte[] PcAdvV(float dt, float dissip)
    {
        float[] v = { _grid.X, _grid.Y, _grid.Z, 0f, dt, dissip, 0f, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    private byte[] Pc32(float dt)
    {
        float[] v = { _grid.X, _grid.Y, _grid.Z, 0f, dt, 0f, 0f, 0f };
        var b = new byte[32];
        Buffer.BlockCopy(v, 0, b, 0, 32);
        return b;
    }

    private byte[] PcForces(float dt, float g, float amp, float om, float t, float pw)
    {
        float[] v = { _grid.X, _grid.Y, _grid.Z, 0f, dt, g, amp, om, t, pw, 0f, 0f };
        var b = new byte[48];
        Buffer.BlockCopy(v, 0, b, 0, 48);
        return b;
    }

    private void Run(Rid pipe, byte[] pc, params (Rid set, uint idx)[] sets)
    {
        long cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, pipe);
        foreach (var (set, idx) in sets) { _rd.ComputeListBindUniformSet(cl, set, idx); }
        _rd.ComputeListSetPushConstant(cl, pc, (uint)pc.Length);
        _rd.ComputeListDispatch(cl, _gx, _gy, _gz);
        _rd.ComputeListEnd();
    }

    public void Free()
    {
        Ready = false;
        // Sets BEFORE _mg.Free(): _grP1 is built over the solver's SolutionRid, so freeing the
        // solver first auto-frees that set and the explicit free below becomes an invalid ID.
        // Same §3c rule as inside the solvers, one level up.
        foreach (var s in new[]
        {
            _mkPhi, _mkBed, _mkMask, _avA0, _avA1, _fcV0, _fcM1,
            _dvV0, _dvD1, _dvM2, _grV0, _grP1, _grM2, _apPhiA0, _apVel1, _apPhiB2,
        })
        {
            if (s.IsValid) { _rd.FreeRid(s); }
        }
        _mg?.Free();
        _mg = null;
        foreach (var t in new[] { _velA, _velB, _phiA, _phiB, _bed, _mask, _div })
        {
            if (t.IsValid) { _rd.FreeRid(t); }
        }
        foreach (var sh in new[] { _sMask, _sAdvV, _sForce, _sDiv, _sGrad, _sAdvP })
        {
            if (sh.IsValid) { _rd.FreeRid(sh); }
        }
    }

    private Rid Compile(string path)
    {
        string src = FileAccess.GetFileAsString(path);
        if (string.IsNullOrEmpty(src)) { GD.PushError($"[WaveSim3D] could not read {path}"); return default; }
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = _rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err)) { GD.PushError($"[WaveSim3D] {path} compile error:\n{err}"); return default; }
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
            | RenderingDevice.TextureUsageBits.CanCopyToBit
            | RenderingDevice.TextureUsageBits.CanUpdateBit,
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
