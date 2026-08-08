using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 29 — THE BARREL. A flat mesh that curls, stored so it can never tangle.
//
// A vertical line through a barrel crosses the water THREE times: the lip's top, the lip's
// underside (the barrel ceiling), and the wave face below. That is the whole reason a
// heightfield fails — y = h(x,z) has exactly one crossing, forever.
//
// THE REPRESENTATION. Rather than one sheet folded back on itself (which curls, but tangles the
// instant it does, because vertices end up behind their neighbours), each row of the mesh is a
// CROSS-SECTION traced as a polyline with a FIXED number of samples. The parameter s runs along
// the water's surface — up the wave face, around the lip, back underneath — so the fold lives in
// the GEOMETRY and never in the topology. Connectivity is built once and never changes.
// Three layers where it is breaking, one where it is not, and they merge continuously.
//
// THE LIP IS AN ARC, and this is the part Gerstner cannot do. Gerstner's particles run closed
// circular orbits — no net transport — so high steepness gives a cusped crest and then a
// self-intersecting knot at the peak. A knot is not a lip. Here the crest is thrown forward
// along an arc whose angle GROWS with time:
//     x = xc + R*sin(phi),   y = yc - R*(1 - cos(phi)),   phi in [0, theta]
// theta = 90 deg is overhanging. theta = 180 deg has curled fully back under itself and the
// barrel is closed. theta is the curl.
//
// This is H-W7 from hypotheses.md — lip geometry generated from a breaking front — and it is
// honest about what it is: the SHAPE is authored, the trigger and timing come from the sim.
// H-W6 measured the steepening that says when to fire it. What this does not give is spray or
// entrainment; those need particles, which is what particles are actually good at.
public partial class BarrelMesh : Node3D
{
    private const int NFace = 56;                 // samples up the wave face
    private const int NLip = 72;                  // samples around the lip arc
    private const int NS = NFace + NLip;          // cross-section samples (fixed forever)
    private const int NZ = 64;                    // rows along the crest
    private const float SizeZ = 14.0f;

    private Camera3D _cam = null!;
    private MeshInstance3D _mi = null!;
    private ArrayMesh _mesh = null!;
    private StandardMaterial3D _mat = null!;

    private Vector3[] _pos = new Vector3[NS * NZ];
    private Vector3[] _nrm = new Vector3[NS * NZ];
    private int[] _idx = Array.Empty<int>();
    private Label? _readout;

    // knobs
    private float _theta = 150.0f;      // curl angle, degrees — THE knob
    private float _radius = 1.05f;      // barrel radius
    private float _crestY = 1.7f;       // crest height above still water
    private float _faceLen = 4.2f;      // how far the face runs back from the crest
    private float _faceShape = 2.4f;    // face steepness exponent
    private float _thickness = 0.16f;   // lip thickness — separates layer 2 from layer 3
    private float _trough = -0.35f;
    private float _arcFrac = 0.62f;     // fraction of the lip that is arc; the rest merges
    private float _mergeAhead = 2.2f;   // how far downstream the lip lands
    private float _mergeTension = 1.1f; // Hermite tangent scale — how hard it swings into the water
    private float _zVary = 0.30f;       // per-row variation so it is not a extruded cutout
    private float _zFreq = 1.4f;
    private float _speed = 0.35f;       // crest travel (manual mode only)
    private float _crestX = -7.5f;
    private bool _paused;
    private float _maxOverhang;

    // loop
    private bool _loop = true;
    private float _phase;
    private float _cycleTime = 6.0f;
    private float _breakAt = 0.45f;     // phase at which it starts to throw
    private float _thetaMax = 165.0f;
    private float _crestYMax = 1.9f;

    public override void _Ready()
    {
        BuildEnvironment();
        BuildMesh();
        BuildUi();
    }

    // LOOP: one wave's whole life, on repeat. It enters low and long, shoals (grows while the
    // face steepens), starts to throw at `breakAt`, closes the barrel, then the cycle wraps and
    // the next one comes through. Everything is driven off a single phase so it is seamless —
    // the wrap happens when the wave has run off the far end and the next is still off-screen.
    public override void _PhysicsProcess(double delta)
    {
        if (_paused) { return; }
        if (!_loop)
        {
            _crestX += _speed * (float)delta;
            return;
        }

        _phase += (float)delta / Mathf.Max(0.5f, _cycleTime);
        if (_phase >= 1.0f) { _phase -= 1.0f; }

        _crestX = Mathf.Lerp(-7.5f, 7.5f, _phase);

        // Shoaling: the wave grows through the first part of the run. Amplitude over depth is
        // what drives real steepening (H-W6), so height and curl ramp in that order.
        float grow = Mathf.SmoothStep(0.0f, _breakAt, _phase);
        _crestY = _crestYMax * (0.25f + 0.75f * grow);

        // The throw: nothing until the face is steep, then it curls over hard.
        float curl = Mathf.SmoothStep(_breakAt, 0.97f, _phase);
        _theta = _thetaMax * curl;
    }

    // One cross-section per row: face -> crest -> around the lip. Fixed sample count, so the
    // mesh indices never change no matter how far it curls.
    private void Rebuild()
    {
        float th = Mathf.DegToRad(_theta);
        float over = 0;

        for (int j = 0; j < NZ; j++)
        {
            float z = (j / (float)(NZ - 1) - 0.5f) * SizeZ;
            // Per-row variation: the crest is not a straight extrusion, so the barrel opens and
            // closes along its length the way a real one does.
            float vary = Mathf.Sin(z * _zFreq + _crestX * 0.6f);
            float xc = _crestX + vary * _zVary;
            float yc = _crestY * (1.0f + 0.12f * vary);
            float th_j = th * (1.0f + 0.18f * vary);
            float r_j = _radius * (1.0f + 0.10f * vary);

            float maxLipX = float.MinValue, tipX = 0;

            for (int m = 0; m < NS; m++)
            {
                float x, y;
                if (m < NFace)
                {
                    // The face: still water behind, rising into the crest.
                    float s = m / (float)(NFace - 1);
                    x = xc - _faceLen * (1.0f - s);
                    float shape = Mathf.Pow(s, _faceShape);
                    y = Mathf.Lerp(_trough, yc, shape);
                }
                else
                {
                    // The lip: an arc thrown forward from the crest and curling under, then
                    // MERGED back into the water ahead.
                    //
                    // Without the merge the arc just stops in mid-air and the sheet ends on a
                    // hard edge. The tail is a cubic Hermite from the arc's exit — position AND
                    // tangent — onto the still-water line downstream, so the lip lands into the
                    // surface instead of intersecting it. Topology never changes: this is still
                    // one polyline with a fixed sample count, so the barrel it encloses is
                    // implicit and closing it costs nothing.
                    //
                    // Physically a lip lands with an impact, not a smooth join. The splash is
                    // the foam layer's job (scene 26's splats); the SURFACE wants continuity.
                    float t = (m - NFace) / (float)(NLip - 1);
                    float tArc = Mathf.Clamp(t / Mathf.Max(0.05f, _arcFrac), 0.0f, 1.0f);
                    float phi = th_j * tArc;
                    x = xc + r_j * Mathf.Sin(phi);
                    y = yc - r_j * (1.0f - Mathf.Cos(phi)) - _thickness * tArc;

                    if (t > _arcFrac)
                    {
                        // Arc exit point and its tangent (d/dphi of the arc, scaled).
                        float pe = th_j;
                        var p1 = new Vector2(xc + r_j * Mathf.Sin(pe),
                                             yc - r_j * (1.0f - Mathf.Cos(pe)) - _thickness);
                        var t1 = new Vector2(Mathf.Cos(pe), -Mathf.Sin(pe)) * (r_j * _mergeTension);

                        // Land on still water ahead of the crest, running flat.
                        var p2 = new Vector2(xc + _mergeAhead, _trough);
                        var t2 = new Vector2(1, 0) * (_mergeAhead * _mergeTension);

                        float u = (t - _arcFrac) / (1.0f - _arcFrac);
                        float u2 = u * u, u3 = u2 * u;
                        float h00 = 2 * u3 - 3 * u2 + 1, h10 = u3 - 2 * u2 + u;
                        float h01 = -2 * u3 + 3 * u2, h11 = u3 - u2;
                        Vector2 q = h00 * p1 + h10 * t1 + h01 * p2 + h11 * t2;
                        x = q.X; y = q.Y;
                    }

                    if (x > maxLipX) { maxLipX = x; }
                    if (m == NS - 1) { tipX = x; }
                }
                _pos[m + NS * j] = new Vector3(x, y, z);
            }

            // Overhang: how far the tip has come BACK from the lip's furthest-forward point.
            // The arc reaches maximum x at 90 degrees, so anything past 90 curls back and this
            // goes positive. Measuring tip-vs-LAUNCH instead was wrong — the tip stays forward
            // of the crest until 180, so it read 0.00 through an obvious fold.
            float o = maxLipX - tipX;
            if (o > over) { over = o; }
        }
        _maxOverhang = over;
    }

    public override void _Process(double delta)
    {
        Rebuild();
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
            _readout.Text = $"barrel · curl {_theta:0}° · overhang {_maxOverhang:0.00} "
                + (_maxOverhang > 0.01f ? "— FOLDED" : "— open")
                + $"\n{NS}x{NZ} verts · {Engine.GetFramesPerSecond():0} fps";
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
            AlbedoColor = new Color(0.16f, 0.46f, 0.62f),
            Roughness = 0.12f,
            Metallic = 0.0f,
            // Once it folds you are looking at the BACK of the sheet through the barrel mouth.
            // Cull it and the curl reads as a hole.
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
        };
        _mi = new MeshInstance3D { Mesh = _mesh, MaterialOverride = _mat };
        AddChild(_mi);
    }

    private void BuildEnvironment()
    {
        // Looking down the barrel's axis from outside its open end — the only angle a curl reads
        // from. The crest runs along z, so the camera sits past the end of the crest (z > SizeZ/2)
        // and offset in x to see into the mouth rather than at the sheet's edge.
        _cam = new Camera3D { Fov = 58.0f, Position = new Vector3(4.5f, 1.6f, 12.5f), Far = 200.0f, Current = true };
        AddChild(_cam);
        _cam.LookAt(new Vector3(0.3f, 0.5f, 0), Vector3.Up);

        AddChild(new DirectionalLight3D
        {
            RotationDegrees = new Vector3(-38, -55, 0),
            LightColor = new Color(1.0f, 0.96f, 0.88f),
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

    private void BuildUi()
    {
        var ui = new DemoUI(this, "29 · the barrel — a flat mesh that curls",
            "Each row is a cross-section traced as a polyline with a FIXED sample count: up the "
            + "wave face, around the lip, back underneath. Three layers where it is breaking, one "
            + "where it is not. The fold lives in the geometry and never in the topology, so it "
            + "cannot tangle no matter how far it curls. CURL ANGLE is the knob — 90° overhangs, "
            + "180° closes the barrel. 'Overhang' in the readout is how far the lip tip has "
            + "travelled back past its launch point, so anything above zero is a measured fold.");
        _readout = ui.AddReadout("barrel —");
        ui.AddToggle("Pause", _paused, v => _paused = v);
        ui.AddToggle("LOOP (wave enters, shoals, throws, closes)", _loop, v => { _loop = v; _phase = 0; });
        ui.AddSlider("Cycle time (s)", 1.5f, 20.0f, _cycleTime, v => _cycleTime = v);
        ui.AddSlider("Break at (phase)", 0.05f, 0.9f, _breakAt, v => _breakAt = v);
        ui.AddSlider("Max curl (deg)", 0.0f, 220.0f, _thetaMax, v => _thetaMax = v);
        ui.AddSlider("Max crest height", 0.4f, 3.5f, _crestYMax, v => _crestYMax = v);
        ui.AddSlider("CURL ANGLE (manual, loop off)", 0.0f, 220.0f, _theta, v => _theta = v);
        ui.AddSlider("Barrel radius", 0.3f, 2.5f, _radius, v => _radius = v);
        ui.AddSlider("Crest height (manual, loop off)", 0.4f, 3.5f, _crestY, v => _crestY = v);
        ui.AddSlider("Face length", 1.5f, 8.0f, _faceLen, v => _faceLen = v);
        ui.AddSlider("Face steepness", 1.0f, 5.0f, _faceShape, v => _faceShape = v);
        ui.AddSlider("Lip thickness", 0.0f, 0.6f, _thickness, v => _thickness = v);
        ui.AddSlider("Arc fraction (rest merges)", 0.25f, 1.0f, _arcFrac, v => _arcFrac = v);
        ui.AddSlider("Merge distance ahead", 0.3f, 6.0f, _mergeAhead, v => _mergeAhead = v);
        ui.AddSlider("Merge tension", 0.2f, 3.0f, _mergeTension, v => _mergeTension = v);
        ui.AddSlider("Trough depth", -1.5f, 0.0f, _trough, v => _trough = v);
        ui.AddSlider("Along-crest variation", 0.0f, 1.2f, _zVary, v => _zVary = v);
        ui.AddSlider("Variation frequency", 0.2f, 4.0f, _zFreq, v => _zFreq = v);
        ui.AddSlider("Travel speed", 0.0f, 2.0f, _speed, v => _speed = v);
        ui.AddSlider("Roughness", 0.02f, 0.6f, 0.12f, v => _mat.Roughness = v);
    }
}
