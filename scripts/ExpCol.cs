using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_col — isolated variable: ONE COLUMN.
//
// Baseline is inherited wholesale from ExpPoolScene; the only thing this scene adds is a single
// obstacle in the water. Controls: shape, hard<->soft as a continuous slider, radius, position.
//
// Hard/soft is a slider rather than a switch because the interesting region is between the two:
//   1 = perfect reflector — a solid neighbour's height is replaced by the centre's, so there is
//       no gradient across the face. That is a no-flux wall, which is what a column physically
//       is. Waves scatter, diffract round it, and leave a shadow.
//   0 = pure absorber — energy that enters the mask is bled off. Reads as a hole, not a pillar.
//
// Nothing here is nonlinear. Whatever interaction shows up is pure linear scattering, which is
// the point: it calibrates how much you get before adding any nonlinearity at all.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_col.tscn 10 1280x900
public partial class ExpCol : ExpPoolScene
{
    private ColumnField.Shape _shape = ColumnField.Shape.Cylinder;
    private float _hardness = 1.0f;
    private float _radius = 0.16f;
    private Vector2 _pos = new(0.18f, 0.10f);
    private float _rotation;
    private bool _on = true;

    private MeshInstance3D _mi = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_col · one column",
        "Isolated variable: a single obstacle. Shape, radius and position, plus HARD<->SOFT as a "
        + "continuous slider — 1 is a perfect no-flux reflector (scatters, diffracts, casts a "
        + "shadow), 0 is a pure absorber (reads as a hole). Everything else is the frozen "
        + "baseline. Entirely linear: this is how much interaction geometry alone buys you.");

    protected override string StateText() =>
        _on ? $"{_shape} r={_radius:0.00} hard={_hardness:0.00}" : "column off";

    public override void _Ready()
    {
        base._Ready();
        _mi = new MeshInstance3D();
        AddChild(_mi);
        RebuildVisual();
    }

    protected override void OnSolverReady() => PushMask();

    // Both sides of the column are rebuilt from the same state, so what the sim reflects off and
    // what is drawn can never drift apart.
    private void Apply()
    {
        RebuildVisual();
        var s = Solver;
        if (s != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); }
    }

    private void PushMask()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.ObstaclesEnabled = _on;
        s.ObstacleHardness = _hardness;
        if (!_on) { s.ClearObstacles(); return; }
        s.UploadObstacles(ColumnField.Rasterize(Grid, new[] { Col() }));
    }

    private ColumnField.Column Col() => new(_pos, _radius, _shape, _rotation);

    private void RebuildVisual()
    {
        const float h = 1.3f;
        _mi.Visible = _on;
        if (!_on) { return; }
        _mi.Mesh = ColumnField.MakeMesh(Col(), h);
        _mi.MaterialOverride = ColumnField.MakeMaterial(_hardness);
        _mi.Position = new Vector3(_pos.X, -1.0f + h * 0.5f, _pos.Y);
        _mi.Rotation = new Vector3(0.0f, _rotation, 0.0f);
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — one column");
        ui.AddToggle("Column on", _on, v => { _on = v; Apply(); });
        ui.AddOptions("Shape", new[] { "Cylinder", "Square", "Blade" }, 0,
            v => { _shape = (ColumnField.Shape)v; Apply(); });
        ui.AddSlider("Hard (1) <-> Soft (0)", 0.0f, 1.0f, _hardness, v => { _hardness = v; Apply(); });
        ui.AddSlider("Radius", 0.03f, 0.45f, _radius, v => { _radius = v; Apply(); });
        ui.AddSlider("Position X", -0.85f, 0.85f, _pos.X, v => { _pos.X = v; Apply(); });
        ui.AddSlider("Position Z", -0.85f, 0.85f, _pos.Y, v => { _pos.Y = v; Apply(); });
        ui.AddSlider("Rotation", 0.0f, 3.15f, _rotation, v => { _rotation = v; Apply(); });
    }
}
