using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// The living-mist layer for the reactive ocean (docs/mna-next-steps.md §2b): a thin
// FluidSim3D box riding the boat just above the water, coupled to the MNA wave field
// by the f3_ocean_couple kernel — ∂h/∂t becomes updraft + mist source, and a uniform
// Galilean flow keeps the mist world-anchored while the box follows the boat.
// Prepare() on the main thread each tick (builds push constants, tracks the Galilean
// frame), StepRt() inside CallOnRenderThread after the wave field's Step.
public sealed class OceanMist
{
    private const string CouplePath = "res://shaders/fluid3d/f3_ocean_couple.glslinc";

    public Vector3I Grid { get; }
    public float BoxSize;     // world extent, xz (square)
    public float BoxHeight;   // world extent, y
    public float BaseY;       // world y of cell row 0
    public Vector2 Origin;    // world min corner (x, z) — follows the boat

    // tunables (couple kernel + fluid step)
    public float Updraft = 3.0f;
    public float MistGain = 6.0f;
    public float MistThresh = 0.004f;
    public float Ambient = 0.004f;
    public float Buoyancy = 1.2f;
    public float DyeFade = 0.975f;
    public float Dt = 1.0f;
    public int Iters = 18;
    public int SlabCells = 5;

    private FluidSim3D? _fluid;
    private RenderingDevice? _rd;
    private Rid _cShader, _cPipe, _cSetVel, _cSetDye, _cSetH, _cSetHp;
    private bool _coupleReady;
    private bool _waterBound;
    private Vector2 _galApplied;
    private byte[] _couplePc = Array.Empty<byte>();
    private byte[] _addPc = Array.Empty<byte>(), _advVPc = Array.Empty<byte>();
    private byte[] _simPc = Array.Empty<byte>(), _advDPc = Array.Empty<byte>();

    // last velocity readback (reference swap is atomic) — cells/tick, 4 floats/cell
    public float[] Vel = Array.Empty<float>();

    public OceanMist(Vector3I grid, float boxSize, float boxHeight, float baseY)
    {
        Grid = grid;
        BoxSize = boxSize;
        BoxHeight = boxHeight;
        BaseY = baseY;
        Origin = new Vector2(-boxSize * 0.5f, -boxSize * 0.5f);
    }

    public bool Ready => (_fluid?.Ready ?? false) && _coupleReady;
    public float CellXZ => BoxSize / Grid.X;
    public float CellY => BoxHeight / Grid.Y;

    // Call inside CallOnRenderThread, after the wave field is initialised.
    public void Init(RenderingDevice rd, Rid hCurr, Rid hPrev)
    {
        _rd = rd;
        _fluid = new FluidSim3D(rd, Grid);
        if (!_fluid.Ready) { return; }

        string src = FileAccess.GetFileAsString(CouplePath);
        var rdSrc = new RDShaderSource { Language = RenderingDevice.ShaderLanguage.Glsl, SourceCompute = src };
        var spirv = rd.ShaderCompileSpirVFromSource(rdSrc);
        string err = spirv.GetStageCompileError(RenderingDevice.ShaderStage.Compute);
        if (!string.IsNullOrEmpty(err))
        {
            GD.PushError($"[OceanMist] couple kernel compile error:\n{err}");
            return;
        }
        _cShader = rd.ShaderCreateFromSpirV(spirv);
        _cPipe = rd.ComputePipelineCreate(_cShader);
        _cSetVel = MakeSet(rd, _fluid.VelocityRid, 0);
        _cSetDye = MakeSet(rd, _fluid.DensityRid, 1);
        _cSetH = MakeSet(rd, hCurr, 2);
        _cSetHp = MakeSet(rd, hPrev, 3);
        _coupleReady = true;
        _waterBound = true;
    }

    // The water field is rebuilt when its grid density changes; drop the uniform sets that
    // reference its textures BEFORE the old solver frees them, rebind after the new Init.
    public void UnbindWater()
    {
        _waterBound = false;
        if (_rd == null) { return; }
        foreach (var r in new[] { _cSetH, _cSetHp })
        {
            if (r.IsValid) { _rd.FreeRid(r); }
        }
        _cSetH = default;
        _cSetHp = default;
    }

    public void RebindWater(RenderingDevice rd, Rid hCurr, Rid hPrev)
    {
        if (!_coupleReady) { return; }
        _cSetH = MakeSet(rd, hCurr, 2);
        _cSetHp = MakeSet(rd, hPrev, 3);
        _waterBound = true;
    }

    private Rid MakeSet(RenderingDevice rd, Rid tex, int setIdx)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = 0 };
        u.AddId(tex);
        return rd.UniformSetCreate(new Godot.Collections.Array<RDUniform> { u }, _cShader, (uint)setIdx);
    }

    // Main thread, once per tick: re-centre the box on the boat, track the Galilean
    // frame (uniform flow delta = −box velocity in cells/tick), build push constants.
    public void Prepare(Vector2 centerWorld, Vector2 boxDeltaWorld, Vector2 mnaOrigin, float mnaWorldSize, int mnaGridX)
    {
        Origin = centerWorld - new Vector2(BoxSize * 0.5f, BoxSize * 0.5f);

        Vector2 galTarget = -boxDeltaWorld / CellXZ;   // world Δu/tick → cells/tick
        Vector2 galDelta = galTarget - _galApplied;
        _galApplied = galTarget;

        float texPerU = mnaGridX / mnaWorldSize;
        Vector2 mna0 = (Origin - mnaOrigin + new Vector2(CellXZ * 0.5f, CellXZ * 0.5f)) * texPerU;
        float mnaScale = CellXZ * texPerU;

        _couplePc = ToBytes(new[]
        {
            Grid.X, Grid.Y, Grid.Z, SlabCells,
            Updraft, MistGain, MistThresh, Ambient,
            galDelta.X, galDelta.Y, mna0.X, mna0.Y,
            mnaScale, mnaScale, 0f, 0f,
        });
        // add pass: buoyancy only — the point source is parked outside the grid
        _addPc = ToBytes(new[]
        {
            Grid.X, Grid.Y, Grid.Z, 0f, Dt, Buoyancy,
            -100f, -100f, -100f, 0.001f, 0f, 0f, 0f, 0f, 0f, 0f,
        });
        _advVPc = ToBytes(new[] { Grid.X, Grid.Y, Grid.Z, 0f, Dt, 0.999f, 0f, 0f });
        _simPc = ToBytes(new[] { Grid.X, Grid.Y, Grid.Z, 0f, 0f, 0f, 0f, 0f });
        _advDPc = ToBytes(new[] { Grid.X, Grid.Y, Grid.Z, 0f, Dt, DyeFade, 0f, 0f });
    }

    // Render thread: couple → fluid step → (throttled) velocity readback for particles.
    public void StepRt(bool readVel)
    {
        if (!Ready || !_waterBound || _couplePc.Length == 0) { return; }
        var rd = _rd!;
        uint gx = (uint)((Grid.X - 1) / 4 + 1);
        uint gy = (uint)((Grid.Y - 1) / 4 + 1);
        uint gz = (uint)((Grid.Z - 1) / 4 + 1);
        long cl = rd.ComputeListBegin();
        rd.ComputeListBindComputePipeline(cl, _cPipe);
        rd.ComputeListBindUniformSet(cl, _cSetVel, 0);
        rd.ComputeListBindUniformSet(cl, _cSetDye, 1);
        rd.ComputeListBindUniformSet(cl, _cSetH, 2);
        rd.ComputeListBindUniformSet(cl, _cSetHp, 3);
        rd.ComputeListSetPushConstant(cl, _couplePc, (uint)_couplePc.Length);
        rd.ComputeListDispatch(cl, gx, gy, gz);
        rd.ComputeListAddBarrier(cl);
        rd.ComputeListEnd();

        _fluid!.Step(_addPc, _advVPc, _simPc, _advDPc, Iters);
        if (readVel) { Vel = _fluid.ReadVelocity(); }
    }

    // Trilinear velocity sample at a fluid-cell position, from the last readback.
    public Vector3 SampleVel(float[] vel, Vector3 p)
    {
        int W = Grid.X, H = Grid.Y, D = Grid.Z;
        if (vel.Length < W * H * D * 4) { return Vector3.Zero; }
        float x = Mathf.Clamp(p.X, 0, W - 1.001f), y = Mathf.Clamp(p.Y, 0, H - 1.001f), z = Mathf.Clamp(p.Z, 0, D - 1.001f);
        int x0 = (int)x, y0 = (int)y, z0 = (int)z, x1 = x0 + 1, y1 = y0 + 1, z1 = z0 + 1;
        float fx = x - x0, fy = y - y0, fz = z - z0;
        Vector3 C(int xi, int yi, int zi)
        {
            int o = (xi + W * (yi + H * zi)) * 4;
            return new Vector3(vel[o], vel[o + 1], vel[o + 2]);
        }
        Vector3 c00 = C(x0, y0, z0).Lerp(C(x1, y0, z0), fx), c10 = C(x0, y1, z0).Lerp(C(x1, y1, z0), fx);
        Vector3 c01 = C(x0, y0, z1).Lerp(C(x1, y0, z1), fx), c11 = C(x0, y1, z1).Lerp(C(x1, y1, z1), fx);
        return c00.Lerp(c10, fy).Lerp(c01.Lerp(c11, fy), fz);
    }

    public void Free()
    {
        _coupleReady = false;
        if (_rd != null)
        {
            foreach (var r in new[] { _cSetVel, _cSetDye, _cSetH, _cSetHp, _cShader })
            {
                if (r.IsValid) { _rd.FreeRid(r); }
            }
        }
        _fluid?.Free();
    }

    private static byte[] ToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }
}
