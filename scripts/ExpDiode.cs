using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_diode — isolated variable: THRESHOLD NONLINEARITY.
//
// The Shockley shape from lib/mna-solver.cmajor, used as a per-cell loss:
//
//     Gd = (Is / Vt) * exp(Vd0 / Vt)
//
// A conductance that is ~zero below a knee and climbs exponentially above it. NO matrix, no
// solve, no stamp assembly — the diode's math, not its solver.
//
// Why a threshold is the cleanest source of genuine interaction: everything linear obeys
// superposition, so two signals can only ADD. A threshold breaks that in the most legible way
// possible. Set the drive so each source is individually just BELOW the knee and neither does
// anything on its own; where their wavefronts CROSS the sum clears the knee and energy dumps.
// Two invisible inputs producing a visible result only where they meet, out of one exponential.
//
// That is what the "Set up the crossing demo" button configures.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_diode.tscn 10 1280x900
public partial class ExpDiode : ExpPoolScene
{
    private bool _on = true;
    private float _knee = 0.030f;
    private float _is = 0.05f;
    private float _vt = 0.010f;

    // Two sources placed so their rings meet near the middle of the pool.
    private int _sources = 2;
    private float _spacing = 0.62f;
    private float _t;

    private Node3D _markers = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_diode · threshold nonlinearity",
        "Isolated variable: a Shockley-shaped THRESHOLD, Gd = (Is/Vt)·exp(Vd0/Vt), applied as a "
        + "per-cell loss — near-zero below the knee, exponential above it. No matrix, no solve: "
        + "the diode's math, not its solver. Set the drive so each source alone stays UNDER the "
        + "knee and neither does anything; where the two wavefronts cross, the sum clears it and "
        + "energy dumps. Interaction out of one exponential. Knee ~= drive strength is the "
        + "interesting region.");

    protected override string StateText() =>
        _on ? $"diode knee={_knee:0.000} Is={_is:0.000} Vt={_vt:0.000}" : "diode off (linear)";

    public override void _Ready()
    {
        DriveStrength = 0.0605f;
        DriveInterval = 1.4f;
        base._Ready();
        _markers = new Node3D();
        AddChild(_markers);
        RebuildMarkers();
    }

    protected override void OnSolverReady() => Push();

    private void Push()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.DiodeEnabled = _on;
        s.DiodeKnee = _knee;
        s.DiodeIs = _is;
        s.DiodeVt = _vt;
    }

    private void Apply()
    {
        if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(Push)); }
    }

    private Vector2 SourcePos(int i)
    {
        if (_sources == 1) { return DefaultSource; }
        float t = i / (float)(_sources - 1) - 0.5f;
        return new Vector2(t * _spacing * 2.0f, -0.05f);
    }

    // Both sources fire together — the crossing region has to be reached by both wavefronts at
    // once for their sum to clear the knee.
    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        if (!AutoDrive) { return; }
        _t += dt;
        if (_t < DriveInterval) { return; }
        _t = 0.0f;
        for (int i = 0; i < _sources; ++i)
        {
            var p = SourcePos(i);
            outDrops.Add(new Vector3(p.X, p.Y, DriveStrength));
        }
    }

    private void RebuildMarkers()
    {
        foreach (var c in _markers.GetChildren()) { c.QueueFree(); }
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(1.0f, 0.85f, 0.25f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        for (int i = 0; i < _sources; ++i)
        {
            var p = SourcePos(i);
            _markers.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.035f, Height = 0.07f, RadialSegments = 12, Rings = 8 },
                MaterialOverride = mat,
                Position = new Vector3(p.X, 0.06f, p.Y),
            });
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — threshold");
        ui.AddToggle("Diode on", _on, v => { _on = v; Apply(); });
        ui.AddSlider("Knee (threshold)", 0.0f, 0.15f, _knee, v => { _knee = v; Apply(); });
        ui.AddSlider("Is (loss scale)", 0.0f, 0.3f, _is, v => { _is = v; Apply(); });
        ui.AddSlider("Vt (sharpness)", 0.002f, 0.08f, _vt, v => { _vt = v; Apply(); });

        ui.AddSection("Crossing demo");
        ui.AddSlider("Sources", 1, 3, _sources, v => { _sources = (int)v; RebuildMarkers(); });
        ui.AddSlider("Spacing", 0.2f, 0.9f, _spacing, v => { _spacing = v; RebuildMarkers(); });
        // One press puts the knee just above what a single source produces, so the crossing
        // region is the only place the threshold is cleared.
        ui.AddToggle("Set up the crossing demo", false, v =>
        {
            if (!v) { return; }
            _sources = 2;
            _spacing = 0.62f;
            DriveStrength = 0.0605f;
            _on = true;
            _knee = DriveStrength * 0.62f;   // one source under, two summed over
            _is = 0.05f;
            _vt = 0.010f;
            RebuildMarkers();
            Apply();
        });
    }
}
