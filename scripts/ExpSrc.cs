using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_src — isolated variable: SOURCE COUNT.
//
// No obstacles, nothing nonlinear: this is the pure superposition scene. Per-source amplitude
// and interval are identical and inherited from the baseline, so the only thing that varies is
// how many places the water is being driven from.
//
// What crossing wavefronts do here — interference fringes, standing patterns where the sources
// beat against each other — is produced entirely by ADDITION. That is not an approximation; at
// small amplitude it is what water does. Worth seeing on its own before any nonlinearity is
// added, because it sets the bar those experiments have to beat.
//
// Fire mode matters: simultaneous makes the fields symmetric and the fringes obvious;
// staggered offsets each source in phase so the pattern drifts instead of locking.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_src.tscn 10 1280x900
public partial class ExpSrc : ExpPoolScene
{
    private int _count = 2;
    private float _spacing = 0.55f;
    private bool _stagger;
    private float _t;
    private int _next;

    private Node3D _markers = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_src · source count",
        "Isolated variable: how many DRIVE POINTS, 1 to 3, plus their spacing. No obstacles, "
        + "nothing nonlinear — the fields simply cross and add. The interference fringes you see "
        + "are produced entirely by addition, which at small amplitude is exactly what water "
        + "does. Staggered fires the sources out of phase so the pattern drifts instead of "
        + "locking.");

    protected override string StateText() =>
        $"{_count} source{(_count == 1 ? "" : "s")} · {(_stagger ? "staggered" : "simultaneous")}";

    public override void _Ready()
    {
        base._Ready();
        _markers = new Node3D();
        AddChild(_markers);
        RebuildMarkers();
    }

    private Vector2 SourcePos(int i)
    {
        if (_count == 1) { return DefaultSource; }
        // Spread along a line through the pool centre, so spacing is the only geometry knob.
        float t = i / (float)(_count - 1) - 0.5f;
        return new Vector2(t * _spacing * 2.0f, -0.15f + (i % 2 == 0 ? -0.12f : 0.12f));
    }

    // Replaces the baseline's single cycling source. Same amplitude and interval per source —
    // only the number of them changes.
    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        if (!AutoDrive) { return; }
        _t += dt;
        if (_stagger)
        {
            // one source per sub-interval, round-robin
            float step = DriveInterval / Math.Max(1, _count);
            if (_t < step) { return; }
            _t = 0.0f;
            var p = SourcePos(_next);
            _next = (_next + 1) % _count;
            outDrops.Add(new Vector3(p.X, p.Y, DriveStrength));
        }
        else
        {
            if (_t < DriveInterval) { return; }
            _t = 0.0f;
            for (int i = 0; i < _count; ++i)
            {
                var p = SourcePos(i);
                outDrops.Add(new Vector3(p.X, p.Y, DriveStrength));
            }
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
        for (int i = 0; i < _count; ++i)
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
        ui.AddSection("THE VARIABLE — source count");
        ui.AddSlider("Sources", 1, 3, _count, v => { _count = (int)v; _next = 0; RebuildMarkers(); });
        ui.AddSlider("Spacing", 0.1f, 0.9f, _spacing, v => { _spacing = v; RebuildMarkers(); });
        ui.AddToggle("Stagger (out of phase)", _stagger, v => { _stagger = v; _t = 0.0f; _next = 0; });
    }
}
