using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// The reactive MNA "micro" layer for the ocean: a GPU Crank-Nicolson 2D wave grid
// (wraps GpuStampSolver + stamp_wave, cn=1 so ripples RING instead of numerically
// damping) mapped onto a square WORLD window. Interactors (the boat) stamp a poke at a
// world position; the solved height (HeightRid) is sampled by the water shader over the
// same window and added to the Gerstner swell → real wakes on the sea.
//
// Stage 1: a fixed window. Stage 2 (Ocean-F) makes Origin follow the camera via toroidal
// addressing. All rd work runs inside RenderingServer.CallOnRenderThread.
public sealed class ReactiveWaveField
{
    private const string StampPath = "res://shaders/stamp/stamp_wave.glslinc";
    private const string FoamKernelPath = "res://shaders/stamp/foam_update.glslinc";

    private GpuStampSolver? _solver;
    private RenderingDevice? _rd;

    // reactive foam (scene 50's foam_update kernel): |∇²h| of the solved height →
    // accumulate+decay buffer, scrolled with the window so foam stays world-anchored.
    // Optional layer — disabled = no dispatch, no cost.
    private Rid _foamShader, _foamPipe, _foamState, _foamTmp;
    private Rid _foamSetIn, _foamSetOut, _foamSetH, _foamSetHp;
    private bool _foamReady;
    private Vector4 _foamDep;       // trail deposit: grid px x, y, radius px, amount (consumed per Step)
    public Vector2I Grid { get; }
    public float WorldSize;      // window extent, world units (square)
    public Vector2 Origin;       // window min corner (world x, z)

    // wave tunables → the stamp push constant
    public float WaveSpeed = 2.2f;  // tuned in CELL units at the reference grid density
    public float SpeedScale = 1f;   // grid/refGrid — keeps the WORLD wave speed constant when cell density changes
    public float Dt = 0.4f;
    public float Damping = 0.12f;   // enough loss that a stationary/circling poke can't pump CN to blow-up
    public float Leak = 0.008f;
    public float Cn = 1.0f;         // Crank-Nicolson → ripples ring
    public bool DipolePoke;         // zero-net-volume poke → no mean pump-up at a standstill
    public int Substeps = 1;        // oversampling: K solver ticks/frame at Dt/K — less dispersion, truthful wave speed
    public int Iters = 20;
    public float Gain = 2.5f;       // sim height → world (mirror the shader's ripple_gain)
    public bool Follow = true;      // camera-follow window (world-anchored via cell-snapped scroll)

    // foam tunables → the foam_update push constant
    public bool FoamEnabled;
    public float FoamDecay = 0.96f;
    public float FoamGain = 3.0f;
    public float FoamThresh = 0.015f;   // NOTE: the dipole poke is a curvature-shaped source — scenes using it need a much higher threshold
    public float FoamAdvect;            // wave-drift advection gain (0 = foam decays in place, original behaviour)

    private Vector4 _poke;          // grid px: x, y, radius px, strength (w=0 → no poke)
    private float[] _cached = System.Array.Empty<float>();   // last readback, for buoyancy

    public ReactiveWaveField(Vector2I grid, float worldSize, Vector2 origin)
    {
        Grid = grid;
        WorldSize = worldSize;
        Origin = origin;
    }

    public bool Ready => _solver?.Ready ?? false;
    public Rid HeightRid => _solver?.HeightRid ?? default;
    public Rid PrevRid => _solver?.PrevRid ?? default;
    public Rid FoamRid => _foamReady ? _foamState : default;
    public float CellSize => WorldSize / Grid.X;

    // Call inside CallOnRenderThread.
    public void Init(RenderingDevice rd)
    {
        _rd = rd;
        _solver = new GpuStampSolver(rd, Grid, StampPath, GpuStampSolver.Mode.Rbgs);
        if (_solver.Ready) { InitFoam(rd); }
    }

    private void InitFoam(RenderingDevice rd)
    {
        string src = FileAccess.GetFileAsString(FoamKernelPath);
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[ReactiveWaveField] foam kernel compile error:\n{err}");
            return;
        }
        _foamShader = rd.ShaderCreateFromSpirV(spirv);
        _foamPipe = rd.ComputePipelineCreate(_foamShader);

        var tf = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32Sfloat,
            TextureType = RenderingDevice.TextureType.Type2D,
            Width = (uint)Grid.X,
            Height = (uint)Grid.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            UsageBits = RenderingDevice.TextureUsageBits.SamplingBit
                | RenderingDevice.TextureUsageBits.StorageBit
                | RenderingDevice.TextureUsageBits.CanCopyFromBit
                | RenderingDevice.TextureUsageBits.CanCopyToBit,
        };
        _foamState = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        _foamTmp = rd.TextureCreate(tf, new RDTextureView(), new Godot.Collections.Array<byte[]>());
        rd.TextureClear(_foamState, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        rd.TextureClear(_foamTmp, new Color(0, 0, 0, 0), 0, 1, 0, 1);

        _foamSetIn = MakeFoamSet(rd, _foamState, 0);
        _foamSetOut = MakeFoamSet(rd, _foamTmp, 1);
        _foamSetH = MakeFoamSet(rd, _solver!.HeightRid, 2);
        _foamSetHp = MakeFoamSet(rd, _solver.PrevRid, 3);
        _foamReady = true;
    }

    // Queue a foam trail deposit at a world position (consumed by the next Step's foam
    // pass) — the "speedboat trail" source: foam stamped where the hull is, so trail
    // length is governed purely by decay, independent of the curvature source.
    public void DepositFoamWorld(Vector2 worldXz, float radiusWorld, float amount)
    {
        Vector2 g = (worldXz - Origin) / WorldSize * Grid.X;
        _foamDep = new Vector4(g.X, g.Y, Mathf.Max(1.0f, radiusWorld / CellSize), amount);
    }

    private Rid MakeFoamSet(RenderingDevice rd, Rid tex, int setIdx)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _foamShader, (uint)setIdx);
    }

    // Queue a poke at a world position (call before Step; consumed by that Step).
    public void PokeWorld(Vector2 worldXz, float radiusWorld, float strength)
    {
        Vector2 g = (worldXz - Origin) / WorldSize * Grid.X;   // square window → grid px
        _poke = new Vector4(g.X, g.Y, Mathf.Max(1.0f, radiusWorld / CellSize), strength);
    }

    public void ClearPoke() => _poke = Vector4.Zero;

    // Advance one tick (render thread). Consumes the queued poke. With Substeps > 1 the
    // tick is split into K solves at Dt/K (audio-style oversampling): numerical dispersion
    // shrinks so ripples travel at the true wave speed instead of smearing. Poke force and
    // leak are split across the substeps so the per-tick totals match; damping scales with
    // dt naturally. Substeps = 1 → byte-identical to the original single solve.
    public void Step()
    {
        if (_solver == null || !_solver.Ready) { return; }
        int k = Math.Max(1, Substeps);
        float dt = Dt / k;
        float c = WaveSpeed * SpeedScale;   // cell-units speed, world-invariant across grid densities
        float beta = dt * dt * c * c;
        float a = Damping * dt * 0.5f;
        float leak = Leak / k;
        float pokeW = _poke.W / k;
        for (int s = 0; s < k; s++)
        {
            // stamp_wave pc: size(2), beta, a, leak, drop x/y/r/w, cn, poke_mode, pad  (12 floats)
            float[] pc = { Grid.X, Grid.Y, beta, a, leak, _poke.X, _poke.Y, _poke.Z, pokeW, Cn, DipolePoke ? 1f : 0f, 0f };
            var bytes = new byte[pc.Length * sizeof(float)];
            Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
            _solver.Step(bytes, Iters, false);
        }
        if (FoamEnabled && _foamReady) { StepFoam(); }
    }

    // One foam accumulate+decay pass reading the freshly-solved height (render thread).
    private void StepFoam()
    {
        var rd = _rd!;
        float[] pc =
        {
            Grid.X, Grid.Y, FoamDecay, FoamGain, FoamThresh,
            _foamDep.X, _foamDep.Y, _foamDep.Z, _foamDep.W, FoamAdvect, 0f, 0f,
        };
        _foamDep = Vector4.Zero;   // consumed
        var bytes = new byte[pc.Length * sizeof(float)];
        Buffer.BlockCopy(pc, 0, bytes, 0, bytes.Length);
        uint gx = (uint)((Grid.X - 1) / 8 + 1);
        uint gy = (uint)((Grid.Y - 1) / 8 + 1);
        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _foamPipe);
        rd.ComputeListBindUniformSet(cl, _foamSetIn, 0);
        rd.ComputeListBindUniformSet(cl, _foamSetOut, 1);
        rd.ComputeListBindUniformSet(cl, _foamSetH, 2);
        rd.ComputeListBindUniformSet(cl, _foamSetHp, 3);
        rd.ComputeListSetPushConstant(cl, bytes, (uint)bytes.Length);
        rd.ComputeListDispatch(cl, gx, gy, 1);
        rd.ComputeListAddBarrier(cl);
        rd.ComputeListEnd();
        rd.TextureCopy(_foamTmp, _foamState, Vector3.Zero, Vector3.Zero, new Vector3(Grid.X, Grid.Y, 1), 0, 0, 0, 0);
    }

    // Re-centre the window on a world point (cell-snapped) and scroll the field so ripples
    // stay WORLD-anchored as the camera/boat moves. Render thread, before Poke/Step.
    public void SetCenter(Vector2 centerWorld)
    {
        if (_solver == null || !_solver.Ready || !Follow) { return; }
        float cs = CellSize;
        Vector2 desired = centerWorld - new Vector2(WorldSize * 0.5f, WorldSize * 0.5f);
        var snapped = new Vector2(Mathf.Floor(desired.X / cs) * cs, Mathf.Floor(desired.Y / cs) * cs);
        int sx = -(int)Mathf.Round((snapped.X - Origin.X) / cs);
        int sy = -(int)Mathf.Round((snapped.Y - Origin.Y) / cs);
        if (sx != 0 || sy != 0)
        {
            _solver.Scroll(sx, sy);
            if (FoamEnabled && _foamReady) { ScrollFoam(sx, sy); }
            Origin = snapped;
        }
    }

    // Shift the foam buffer by the same whole-cell offset as the height field so foam
    // stays world-anchored. _foamTmp doubles as scratch (fully rewritten every StepFoam).
    private void ScrollFoam(int sx, int sy)
    {
        var rd = _rd!;
        var full = new Vector3(Grid.X, Grid.Y, 1);
        rd.TextureCopy(_foamState, _foamTmp, Vector3.Zero, Vector3.Zero, full, 0, 0, 0, 0);
        rd.TextureClear(_foamState, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        int w = Grid.X - Math.Abs(sx);
        int h = Grid.Y - Math.Abs(sy);
        if (w <= 0 || h <= 0) { return; }
        int fx = sx > 0 ? 0 : -sx, tx = sx > 0 ? sx : 0;
        int fy = sy > 0 ? 0 : -sy, ty = sy > 0 ? sy : 0;
        rd.TextureCopy(_foamTmp, _foamState, new Vector3(fx, fy, 0), new Vector3(tx, ty, 0), new Vector3(w, h, 1), 0, 0, 0, 0);
    }

    // Wipe accumulated foam (render thread) — used when the layer is toggled off.
    public void ClearFoam()
    {
        if (!_foamReady) { return; }
        _rd!.TextureClear(_foamState, new Color(0, 0, 0, 0), 0, 1, 0, 1);
        _rd.TextureClear(_foamTmp, new Color(0, 0, 0, 0), 0, 1, 0, 1);
    }

    // Cache the field for CPU buoyancy sampling (render thread; call at whatever rate).
    public void ReadBack()
    {
        if (_solver != null && _solver.Ready) { _cached = _solver.ReadField(); }
    }

    // Mean height of the last readback (sim units) — diagnostic: a healthy field hovers
    // near 0; a pumping poke drags it negative (the stop-sink signature).
    public float MeanHeight()
    {
        var f = _cached;
        if (f.Length == 0) { return 0f; }
        float s = 0f;
        for (int i = 0; i < f.Length; i++) { s += f[i]; }
        return s / f.Length;
    }

    // World-space wake height at a world point (bilinear × Gain). Main-thread safe (reads the
    // last cached readback; a frame of latency is fine for buoyancy).
    public float SampleHeightWorld(Vector2 worldXz)
    {
        var f = _cached;
        if (f.Length != Grid.X * Grid.Y) { return 0f; }
        Vector2 g = (worldXz - Origin) / WorldSize * Grid.X;
        if (g.X < 0f || g.Y < 0f || g.X >= Grid.X - 1 || g.Y >= Grid.Y - 1) { return 0f; }
        int x0 = (int)g.X, y0 = (int)g.Y, W = Grid.X;
        float fx = g.X - x0, fy = g.Y - y0;
        float h0 = Mathf.Lerp(f[x0 + W * y0], f[x0 + 1 + W * y0], fx);
        float h1 = Mathf.Lerp(f[x0 + W * (y0 + 1)], f[x0 + 1 + W * (y0 + 1)], fx);
        return Mathf.Lerp(h0, h1, fy) * Gain;
    }

    public void Free()
    {
        // foam first: _foamSetH references the solver's height texture
        if (_foamReady && _rd != null)
        {
            _foamReady = false;
            foreach (var r in new[] { _foamSetIn, _foamSetOut, _foamSetH, _foamSetHp, _foamState, _foamTmp, _foamShader })
            {
                if (r.IsValid) { _rd.FreeRid(r); }
            }
        }
        _solver?.Free();
    }
}
