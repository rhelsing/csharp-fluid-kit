using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 30 — THE BARREL, SIMULATED. Three layers, driven by the MNA stamp solver.
//
// Scene 29 proved the REPRESENTATION works — a fixed-sample cross-section traced up the face,
// around the lip and back underneath cannot tangle no matter how far it curls. But 29 is a
// mockup: the curl angle is a slider and the travel is `x += speed*dt`. Nothing solves anything.
//
// Here nothing is authored. There is no curl-angle knob at all:
//
//   LAYER 1 (the wave)  — GpuStampSolver on stamp_wave_surf. Nonlinear depth, so the crest sits
//                         in deeper water than the trough and outruns it. H-W6 measured that
//                         steepening (growth 1.409 at nl=1 against 1.130 linear). This is the
//                         same solver, same stamp, same measurement — it is what fires the break.
//
//   DETACHMENT          — a column detaches when its front slope exceeds a threshold. That is
//                         the physical breaking criterion, and the slope comes from the solved
//                         field, not from a timer. Mass is moved out of the crest into the lip.
//
//   LAYERS 2 and 3      — the lip: ballistic once detached (gravity only, carrying the forward
//                         momentum it had at detachment), which is the thing Gerstner can never
//                         do because its orbits are closed. Layer 3 is the top, layer 2 the
//                         underside, separated by the lip's own thickness. Neighbours are
//                         coupled so the sheet stays coherent instead of shredding per column.
//
//   LANDING             — when the lip's underside reaches the water it merges back into layer
//                         1, returning its mass. The barrel closes because the water closed it.
//
// The three layers MERGE where nothing is breaking (h1 = h2 = h3) and separate where it is, so
// the mesh is one continuous sheet everywhere. Fixed topology, built once.
public partial class BarrelMna : Node3D
{
    private const int NX = 256, NZ = 64;          // sim grid (x along travel, z along the crest)
    private const int NL = 40;                    // lip samples per row — fixed, degenerate when idle
    private const int NS = NX + NL;               // cross-section samples
    private const float SizeX = 14.0f, SizeZ = 12.0f;

    private Camera3D _cam = null!;
    private MeshInstance3D _mi = null!;
    private ArrayMesh _mesh = null!;
    private StandardMaterial3D _mat = null!;
    private GpuStampSolver? _solver;
    private Label? _readout;
    private volatile float[]? _field;

    private readonly Vector3[] _pos = new Vector3[NS * NZ];
    private readonly Vector3[] _nrm = new Vector3[NS * NZ];
    private int[] _idx = Array.Empty<int>();

    // lip state, per (x,z) column — layers 2 and 3
    private readonly float[] _lipTop = new float[NX * NZ];
    private readonly float[] _lipBot = new float[NX * NZ];
    private readonly float[] _lipVy = new float[NX * NZ];
    private readonly float[] _lipVx = new float[NX * NZ];
    private readonly bool[] _lipOn = new bool[NX * NZ];

    // knobs — note there is NO curl angle here. The curl is an outcome.
    private const float Dx = SizeX / NX;  // cell width, for a real gradient
    private float _breakSlope = 0.35f;    // front slope dh/dx that detaches a column
    private float _gravity = 2.4f;
    private float _throw = 1.9f;          // how much crest speed converts to forward throw
    private float _lipThick = 0.10f;
    private float _cohesion = 0.35f;      // neighbour coupling — holds the sheet together
    private float _nl = 1.0f;             // nonlinearity (H-W6's knob)
    private float _paddle = 0.34f;
    private float _sweeps = 24f;
    private bool _paused;
    private int _tick, _breaking;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildMesh();
        RenderingServer.CallOnRenderThread(Callable.From(InitSolver));
        BuildUi();
    }

    private void InitSolver()
    {
        _solver = new GpuStampSolver(RenderingServer.GetRenderingDevice(), new Vector2I(NX, NZ),
            "res://shaders/stamp/stamp_wave_surf.glslinc", GpuStampSolver.Mode.Rbgs);
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_paused) { return; }
        byte[] pc = Pc(_tick++);
        int sweeps = Mathf.Clamp((int)_sweeps, 1, 64);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_solver is not { Ready: true }) { return; }
            _solver.Step(pc, sweeps, false);
            _field = _solver.ReadField();
        }));
    }

    // The lip: detach on measured slope, then ballistic, then land. No authored trajectory.
    private void StepLip(float[] h, float dt)
    {
        _breaking = 0;
        for (int j = 0; j < NZ; j++)
        for (int i = 2; i < NX - 2; i++)
        {
            int k = i + NX * j;

            if (!_lipOn[k])
            {
                // BREAKING CRITERION, from the solved field: the front face going vertical.
                // Forward slope, so it only fires on the leading edge of a wave, not the back.
                //
                // TRUE slope (dh/dx), not a raw per-cell difference. First version compared
                // h[k]-h[k+1] against a threshold sized for a gradient: at 256 cells over 14
                // units dh is ~1e-3 per cell, so nothing ever detached and the readout sat at
                // "0 columns breaking" while the waves were visibly steep.
                float slope = (h[k] - h[k + 1]) / Dx;
                if (slope > _breakSlope && h[k] > 0.0f)
                {
                    _lipOn[k] = true;
                    _lipTop[k] = h[k];
                    _lipBot[k] = h[k] - _lipThick;
                    _lipVy[k] = 0.0f;
                    // Crest speed converts to forward throw — the crest keeps the momentum it
                    // had, which is exactly why it outruns the water beneath it.
                    _lipVx[k] = _throw * slope;
                }
            }
            else
            {
                _lipVy[k] -= _gravity * dt;
                _lipTop[k] += _lipVy[k] * dt;
                _lipBot[k] += _lipVy[k] * dt;

                // Landed: give the mass back to layer 1 and close the barrel.
                if (_lipBot[k] <= h[k])
                {
                    _lipOn[k] = false;
                    _lipTop[k] = h[k];
                    _lipBot[k] = h[k];
                    _lipVx[k] = 0.0f;
                }
                else
                {
                    _breaking++;
                }
            }
        }

        // Cohesion: pull each lip column toward its along-crest neighbours so the sheet stays a
        // sheet. Without this every column falls independently and it shreds into stripes.
        for (int j = 1; j < NZ - 1; j++)
        for (int i = 1; i < NX - 1; i++)
        {
            int k = i + NX * j;
            if (!_lipOn[k]) { continue; }
            float sum = 0; int n = 0;
            if (_lipOn[k - NX]) { sum += _lipTop[k - NX]; n++; }
            if (_lipOn[k + NX]) { sum += _lipTop[k + NX]; n++; }
            if (n > 0)
            {
                float pull = (sum / n - _lipTop[k]) * _cohesion;
                _lipTop[k] += pull;
                _lipBot[k] += pull;
            }
        }
    }

    // One cross-section per row: layer 1 across x, then the lip traced forward and back under.
    // Where nothing is breaking the lip samples collapse onto the surface, so the three layers
    // merge into one sheet and the extra triangles are degenerate and invisible.
    // Traced IN ORDER: base up to the break column, the lip arc out and back, then base onward.
    //
    // The first version appended the lip AFTER the whole base row, so the polyline walked the
    // water to the domain edge at x=+7 and the next sample jumped back to the lip at x~0. That
    // is a triangle spanning the entire domain, whipping around every time a lip spawned or
    // landed — the flipping below the grid. It also meant the lip was drawn hanging off the end
    // of the row instead of emerging from the wave it came from.
    //
    // Sample COUNT stays fixed at NS; only the split between the two base runs moves, so the
    // index buffer is still built once and never touched.
    private void BuildCrossSections(float[] h)
    {
        const int NBase = NS - NL;

        for (int j = 0; j < NZ; j++)
        {
            float z = (j / (float)(NZ - 1) - 0.5f) * SizeZ;
            int row = NS * j;

            // This row's leading lip column — the furthest-forward active one.
            int lip = -1;
            for (int i = NX - 3; i >= 2; i--)
            {
                if (_lipOn[i + NX * j]) { lip = i; break; }
            }

            if (lip < 0)
            {
                // No break here: the whole cross-section is layer 1, and the lip samples sit
                // coincident with the last base sample — degenerate, continuous, invisible.
                for (int m = 0; m < NBase; m++)
                {
                    _pos[row + m] = BasePoint(h, j, m / (float)(NBase - 1) * (NX - 1), z);
                }
                Vector3 tail = _pos[row + NBase - 1];
                for (int m = NBase; m < NS; m++) { _pos[row + m] = tail; }
                continue;
            }

            // Split the base budget by extent so neither run starves.
            int nPre = Mathf.Clamp((int)(NBase * (lip / (float)(NX - 1))), 2, NBase - 2);
            int nPost = NBase - nPre;

            for (int m = 0; m < nPre; m++)
            {
                _pos[row + m] = BasePoint(h, j, m / (float)(nPre - 1) * lip, z);
            }

            int k = lip + NX * j;
            float xr = (lip / (float)(NX - 1) - 0.5f) * SizeX;
            float reach = _lipVx[k];
            float drop = _lipTop[k] - _lipBot[k];
            for (int m = 0; m < NL; m++)
            {
                // The lip arcs forward and back under. Reach comes from the momentum it carried
                // at detachment and drop from how far it has fallen — both integrated.
                float t = m / (float)(NL - 1);
                float ang = Mathf.Pi * t;
                _pos[row + nPre + m] = new Vector3(
                    xr + reach * Mathf.Sin(ang),
                    _lipTop[k] - drop * t - reach * (1.0f - Mathf.Cos(ang)) * 0.5f,
                    z);
            }

            for (int m = 0; m < nPost; m++)
            {
                float fi = lip + (NX - 1 - lip) * (m / (float)(nPost - 1));
                _pos[row + nPre + NL + m] = BasePoint(h, j, fi, z);
            }
        }
    }

    // Layer 1 sampled at a fractional column, so the two base runs can be resampled to whatever
    // budget the split gives them.
    private static Vector3 BasePoint(float[] h, int j, float fi, float z)
    {
        fi = Math.Clamp(fi, 0, NX - 1);
        int i0 = (int)fi;
        int i1 = Math.Min(i0 + 1, NX - 1);
        float f = fi - i0;
        float y = Mathf.Lerp(h[i0 + NX * j], h[i1 + NX * j], f);
        return new Vector3((fi / (NX - 1) - 0.5f) * SizeX, y, z);
    }

    public override void _Process(double delta)
    {
        var h = _field;
        if (h == null || h.Length != NX * NZ) { return; }

        StepLip(h, (float)Math.Min(delta, 0.033));
        BuildCrossSections(h);
        ComputeNormals();

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _pos;
        arrays[(int)Mesh.ArrayType.Normal] = _nrm;
        arrays[(int)Mesh.ArrayType.Index] = _idx;
        _mesh.ClearSurfaces();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        if (_readout != null && _tick % 6 == 0)
        {
            _readout.Text = $"barrel (MNA) · {_breaking} columns breaking · nl {_nl:0.00}\n"
                + $"{NX}x{NZ} sim · {NS}x{NZ} verts · {Engine.GetFramesPerSecond():0} fps";
        }
    }

    private void ComputeNormals()
    {
        for (int j = 0; j < NZ; j++)
        for (int m = 0; m < NS; m++)
        {
            int mm = Math.Max(m - 1, 0), mp = Math.Min(m + 1, NS - 1);
            int jm = Math.Max(j - 1, 0), jp = Math.Min(j + 1, NZ - 1);
            Vector3 du = _pos[mp + NS * j] - _pos[mm + NS * j];
            Vector3 dv = _pos[m + NS * jp] - _pos[m + NS * jm];
            Vector3 n = du.Cross(dv);
            _nrm[m + NS * j] = n.LengthSquared() > 1e-12f ? n.Normalized() : Vector3.Up;
        }
    }

    private void BuildMesh()
    {
        _mesh = new ArrayMesh();
        var idx = new int[(NS - 1) * (NZ - 1) * 6];
        int t = 0;
        for (int j = 0; j < NZ - 1; j++)
        for (int m = 0; m < NS - 1; m++)
        {
            int a = m + NS * j, b = a + 1, c = a + NS, d = c + 1;
            idx[t++] = a; idx[t++] = c; idx[t++] = b;
            idx[t++] = b; idx[t++] = c; idx[t++] = d;
        }
        _idx = idx;
        _mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.17f, 0.47f, 0.63f),
            Roughness = 0.13f,
            Metallic = 0.0f,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _mi = new MeshInstance3D { Mesh = _mesh, MaterialOverride = _mat };
        AddChild(_mi);
    }

    // stamp_wave_surf layout. extra.x = nonlinearity (H-W6), micro slots = void field (unused).
    private byte[] Pc(int step)
    {
        float ph0 = step * 0.16f, ph1 = (step + 1) * 0.16f;
        float[] v =
        {
            NX, NZ, 0.22f, 0.0015f,
            0f, 1f, 6f, 0.25f,
            -1f, 0.30f, 0f, 0.05f,
            -0.92f, -0.92f, 0.10f, _paddle,
            0.05f, 1f, ph0, ph1,
            0f, 0f, 0f, 0f,
            0f, 0f, 0f, 0f,
            _nl, 2f, 1f, 1f,
        };
        var b = new byte[128];
        Buffer.BlockCopy(v, 0, b, 0, 128);
        return b;
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 56.0f, Position = new Vector3(5.5f, 1.8f, 11.5f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(1.5f, 0.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-40, -52, 0),
            LightColor = new Color(1.0f, 0.96f, 0.89f),
            LightEnergy = 1.8f,
        });
        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.18f, 0.34f, 0.60f),
            SkyHorizonColor = new Color(0.70f, 0.80f, 0.88f),
            GroundBottomColor = new Color(0.10f, 0.13f, 0.17f),
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 1.1f,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
            },
        });
    }

    public override void _ExitTree()
    {
        RenderingServer.CallOnRenderThread(Callable.From(() => _solver?.Free()));
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "30 · the barrel, simulated — three layers driven by MNA",
            "No curl-angle knob. Layer 1 is the MNA stamp solver on stamp_wave_surf, whose "
            + "nonlinear depth makes the crest outrun the trough — the steepening H-W6 measured. "
            + "A column DETACHES when its solved front slope crosses a threshold, and the lip is "
            + "then ballistic, carrying the momentum it had at detachment. It lands when its "
            + "underside meets the water, returning its mass. The three layers merge where "
            + "nothing is breaking and separate where it is, so the sheet is continuous "
            + "everywhere and the topology never changes. The curl is an outcome.");
        _readout = ui.AddReadout("barrel (MNA) —");
        ui.AddToggle("Pause", _paused, v => _paused = v);
        ui.AddSlider("Nonlinearity (H-W6)", 0.0f, 1.5f, _nl, v => _nl = v);
        ui.AddSlider("Break slope (detach, dh/dx)", 0.02f, 2.0f, _breakSlope, v => _breakSlope = v);
        ui.AddSlider("Throw (momentum → reach)", 0.0f, 8.0f, _throw, v => _throw = v);
        ui.AddSlider("Gravity on the lip", 0.2f, 10.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Lip thickness", 0.01f, 0.4f, _lipThick, v => _lipThick = v);
        ui.AddSlider("Sheet cohesion", 0.0f, 0.9f, _cohesion, v => _cohesion = v);
        ui.AddSlider("Paddle gain", 0.0f, 1.5f, _paddle, v => _paddle = v);
        ui.AddSlider("Sweeps / tick", 4, 60, _sweeps, v => _sweeps = v);
        ui.AddSlider("Roughness", 0.02f, 0.6f, 0.13f, v => _mat.Roughness = v);
    }
}
