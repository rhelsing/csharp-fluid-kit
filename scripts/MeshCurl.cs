using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 28 — CAN A FLAT MESH CURL? The smallest experiment that answers it.
//
// No fluid solver, no particles, no volume. A flat grid of vertices, each carrying a velocity,
// integrated forward. That is the entire simulation.
//
// WHY THE OTHER APPROACHES DO NOT CURL, in one line each:
//   heightfield  — y = h(x,z) is single-valued; a lip is two heights over one spot. Never.
//   Gerstner     — closed circular orbits, so NO net transport. High steepness gives a cusped
//                  crest and then a self-intersecting knot at the peak. A knot is not a lip.
//   particles    — curl fine, but read as foam: discrete scattered things look like foam.
//
// WHAT ACTUALLY MAKES A LIP: the crest must outrun the water beneath it and go ballistic. That
// needs INERTIA — velocity carried forward in time — not a displacement computed from phase.
// The moment a vertex has momentum of its own, folding is just what happens.
//
// THE MECHANISM HERE IS SHEAR, and it is the real one. In a shoaling wave the water near the
// crest moves forward faster than the water below it, because orbital velocity grows with
// height while the base drags on the bed. Any sheet under enough shear rolls over — that is
// the Kelvin-Helmholtz roll-up, and a plunging breaker is exactly that with a free surface.
// So: vx grows with height, and the crest overtakes its own base.
//
// The mesh is a PARAMETER grid, not a height grid: vertex (i,j) outputs a full 3D position, so
// the sheet is free to fold back over itself. Connectivity is fixed and never re-meshed. It
// WILL tangle after the fold — that is expected and is the honest limit of this approach. A
// wave only has to survive its own break, and each wave breaks once.
public partial class MeshCurl : Node3D
{
    private const int NX = 160, NZ = 48;          // parameter grid
    private const float SizeX = 12.0f, SizeZ = 4.0f;

    private Camera3D _cam = null!;
    private MeshInstance3D _mi = null!;
    private ArrayMesh _mesh = null!;
    private StandardMaterial3D _mat = null!;

    private Vector3[] _pos = new Vector3[NX * NZ];
    private Vector3[] _vel = new Vector3[NX * NZ];
    private Vector3[] _nrm = new Vector3[NX * NZ];
    private Vector3[] _rest = new Vector3[NX * NZ];
    private int[] _idx = Array.Empty<int>();
    private Label? _readout;

    // knobs
    private float _shear = 2.6f;        // forward speed gained per unit height — THE curl knob
    private float _gravity = 5.0f;
    private float _stiffness = 6.0f;    // pull back toward the flat rest sheet (makes it a wave)
    private float _damping = 0.02f;
    private float _amp = 0.9f;          // driver hump height
    private float _driverX = -4.0f;     // where the hump is injected
    private float _driverW = 1.2f;
    private float _dt = 0.016f;
    private bool _paused;
    private float _maxOverhang;

    // LOOP. A folded sheet cannot recover — once vertices are behind their neighbours the
    // parameterization is degenerate and no amount of integration untangles it. So the cycle is
    // release, fold, collapse, re-release, rather than a continuous wave train. That IS the
    // limit of the Lagrangian representation, and scene 29 exists because of it.
    private bool _loop = true;
    private float _cycleTime = 5.0f;
    private float _cycleT;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildMesh();
        Reset();
        BuildUi();
    }

    private void Reset()
    {
        for (int j = 0; j < NZ; j++)
        for (int i = 0; i < NX; i++)
        {
            int k = i + NX * j;
            float x = (i / (float)(NX - 1) - 0.5f) * SizeX;
            float z = (j / (float)(NZ - 1) - 0.5f) * SizeZ;
            _rest[k] = new Vector3(x, 0, z);
            _pos[k] = _rest[k];
            _vel[k] = Vector3.Zero;
        }
        // One hump, given forward momentum. Everything after this is integration.
        for (int k = 0; k < _pos.Length; k++)
        {
            float d = (_pos[k].X - _driverX) / _driverW;
            float g = Mathf.Exp(-d * d);
            _pos[k] = _pos[k] with { Y = _amp * g };
            _vel[k] = new Vector3(_shear * _pos[k].Y, 0, 0);
        }
        _maxOverhang = 0;
    }

    public override void _PhysicsProcess(double delta)
    {
        if (_paused) { return; }

        if (_loop)
        {
            _cycleT += (float)delta;
            if (_cycleT >= _cycleTime) { _cycleT = 0; Reset(); }
        }

        float dt = _dt;

        for (int k = 0; k < _pos.Length; k++)
        {
            Vector3 p = _pos[k], v = _vel[k];

            // THE CURL TERM. Forward speed grows with height, so the crest outruns its base and
            // the sheet rolls over. Everything else here is bookkeeping to keep it wave-like.
            v.X += _shear * p.Y * dt;

            v.Y -= _gravity * dt;

            // Spring back to the flat sheet — without this the hump just falls and never
            // returns, and there is no wave to break.
            Vector3 toRest = _rest[k] - p;
            v += new Vector3(0, toRest.Y, 0) * (_stiffness * dt);

            v *= 1.0f - _damping;
            p += v * dt;

            _pos[k] = p;
            _vel[k] = v;
        }

        // Overhang is the instrument: how far a vertex has travelled BACKWARD past the one that
        // used to be in front of it. Zero means no fold. Positive means the sheet is over
        // itself, which is a curl and nothing else.
        float over = 0;
        for (int j = 0; j < NZ; j++)
        for (int i = 1; i < NX; i++)
        {
            float d = _pos[(i - 1) + NX * j].X - _pos[i + NX * j].X;
            if (d > over) { over = d; }
        }
        _maxOverhang = over;
    }

    public override void _Process(double delta)
    {
        ComputeNormals();
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = _pos;
        arrays[(int)Mesh.ArrayType.Normal] = _nrm;
        arrays[(int)Mesh.ArrayType.Index] = _idx;
        _mesh.ClearSurfaces();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

        if (_readout != null)
        {
            _readout.Text = $"mesh curl · overhang {_maxOverhang:0.000} "
                + (_maxOverhang > 0.001f ? "— FOLDED" : "— flat") + $"\n{NX}x{NZ} verts · {Engine.GetFramesPerSecond():0} fps"
                + (_loop ? $" · cycle {_cycleT:0.0}/{_cycleTime:0.0}s" : "");
        }
    }

    private void ComputeNormals()
    {
        for (int j = 0; j < NZ; j++)
        for (int i = 0; i < NX; i++)
        {
            int im = Math.Max(i - 1, 0), ip = Math.Min(i + 1, NX - 1);
            int jm = Math.Max(j - 1, 0), jp = Math.Min(j + 1, NZ - 1);
            Vector3 du = _pos[ip + NX * j] - _pos[im + NX * j];
            Vector3 dv = _pos[i + NX * jp] - _pos[i + NX * jm];
            Vector3 n = dv.Cross(du);
            _nrm[i + NX * j] = n.LengthSquared() > 1e-12f ? n.Normalized() : Vector3.Up;
        }
    }

    private void BuildMesh()
    {
        _mesh = new ArrayMesh();
        var idx = new int[(NX - 1) * (NZ - 1) * 6];
        int t = 0;
        for (int j = 0; j < NZ - 1; j++)
        for (int i = 0; i < NX - 1; i++)
        {
            int a = i + NX * j, b = a + 1, c = a + NX, d = c + 1;
            idx[t++] = a; idx[t++] = c; idx[t++] = b;
            idx[t++] = b; idx[t++] = c; idx[t++] = d;
        }
        _idx = idx;

        _mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.30f, 0.62f, 0.80f),
            Roughness = 0.18f,
            Metallic = 0.0f,
            // Double-sided: once it folds you are looking at the BACK of the sheet through the
            // barrel. Cull it and the curl looks like a hole.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _mi = new MeshInstance3D { Mesh = _mesh, MaterialOverride = _mat };
        AddChild(_mi);
    }

    private void BuildEnvironment()
    {
        _cam = new Camera3D { Fov = 50.0f, Position = new Vector3(-1.0f, 2.2f, 7.5f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0.5f, 0.2f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-45, -40, 0),
            LightColor = new Color(1.0f, 0.97f, 0.92f),
            LightEnergy = 1.5f,
        });

        var sky = new ProceduralSkyMaterial
        {
            SkyTopColor = new Color(0.20f, 0.34f, 0.58f),
            SkyHorizonColor = new Color(0.62f, 0.74f, 0.86f),
            GroundBottomColor = new Color(0.12f, 0.14f, 0.18f),
        };
        AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Sky,
                Sky = new Sky { SkyMaterial = sky },
                AmbientLightSource = Godot.Environment.AmbientSource.Sky,
                AmbientLightEnergy = 1.0f,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
            },
        });
    }

    private void BuildUi()
    {
        var ui = new DemoUI(this, "28 · can a flat mesh curl?",
            "A flat grid of vertices, each carrying a velocity, integrated forward. No fluid "
            + "solver, no particles, no volume. The grid is a PARAMETER domain, not a height "
            + "field, so a vertex outputs a full 3D position and the sheet can fold back over "
            + "itself. SHEAR is the mechanism: forward speed grows with height, so the crest "
            + "outruns its own base — which is what a plunging breaker actually is. Watch "
            + "'overhang' in the readout: it is the distance a vertex has travelled back past "
            + "the one that used to be ahead of it, so anything above zero is a genuine fold.");
        _readout = ui.AddReadout("mesh curl —");
        ui.AddToggle("Pause", _paused, v => _paused = v);
        ui.AddToggle("LOOP (release, fold, collapse, repeat)", _loop, v => { _loop = v; _cycleT = 0; });
        ui.AddSlider("Cycle time (s)", 1.0f, 15.0f, _cycleTime, v => _cycleTime = v);
        ui.AddSlider("SHEAR (the curl knob)", 0.0f, 8.0f, _shear, v => _shear = v);
        ui.AddSlider("Gravity", 0.0f, 20.0f, _gravity, v => _gravity = v);
        ui.AddSlider("Stiffness (back to flat)", 0.0f, 20.0f, _stiffness, v => _stiffness = v);
        ui.AddSlider("Damping", 0.0f, 0.15f, _damping, v => _damping = v);
        ui.AddSlider("Hump height", 0.1f, 2.5f, _amp, v => _amp = v);
        ui.AddSlider("Hump width", 0.3f, 3.0f, _driverW, v => _driverW = v);
        ui.AddSlider("Hump start x", -5.5f, 0.0f, _driverX, v => _driverX = v);
        ui.AddSlider("Timestep", 0.004f, 0.03f, _dt, v => _dt = v);
        ui.AddToggle("RESET (flip to re-release)", false, _ => Reset());
    }
}
