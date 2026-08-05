using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cols — isolated variable: COLUMN COUNT.
//
// Shape, radius and hardness are held fixed here (unlike 24_col, where they are the variable),
// so the only thing changing is how many obstacles the wave has to get past. Layout is a preset
// rather than free placement for the same reason — arrangement is a confound, so it changes in
// discrete named steps you can hold still.
//
// Still entirely linear. Every extra column multiplies the scattering paths, so this is the
// scene that shows how far pure geometry gets you before any nonlinearity is added.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cols.tscn 10 1280x900
public partial class ExpCols : ExpPoolScene
{
    private const float Radius = 0.11f;
    private const float Hardness = 1.0f;
    private const ColumnField.Shape Kind = ColumnField.Shape.Cylinder;

    private int _count = 4;
    private int _layout;   // 0 grid · 1 arc · 2 scatter

    private Node3D _holder = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_cols · column count",
        "Isolated variable: HOW MANY columns, 1 to 8. Shape, radius and hardness are pinned so "
        + "only the count varies; layout is a preset because arrangement would otherwise be a "
        + "second variable. Still entirely linear — each column just multiplies the scattering "
        + "paths.");

    protected override string StateText() =>
        $"{_count} columns · {(_layout == 0 ? "grid" : _layout == 1 ? "arc" : "scatter")}";

    public override void _Ready()
    {
        base._Ready();
        _holder = new Node3D();
        AddChild(_holder);
        RebuildVisual();
    }

    protected override void OnSolverReady() => PushMask();

    // Deterministic per index so the layout holds still instead of reshuffling on every change.
    private static float Hash01(int i)
    {
        float v = MathF.Sin(i * 12.9898f) * 43758.5453f;
        return v - MathF.Floor(v);
    }

    private ColumnField.Column[] Columns()
    {
        var list = new ColumnField.Column[_count];
        for (int i = 0; i < _count; ++i)
        {
            Vector2 p;
            switch (_layout)
            {
                case 1:   // arc facing the drive source
                {
                    float t = _count == 1 ? 0.5f : i / (float)(_count - 1);
                    float a = Mathf.Lerp(-0.9f, 0.9f, t);
                    p = new Vector2(MathF.Sin(a) * 0.62f, MathF.Cos(a) * 0.55f - 0.05f);
                    break;
                }
                case 2:   // scatter
                    p = new Vector2(Hash01(i * 2 + 3) * 1.5f - 0.75f, Hash01(i * 2 + 11) * 1.5f - 0.75f);
                    break;
                default:  // grid
                {
                    int cols = Math.Max(1, (int)MathF.Ceiling(MathF.Sqrt(_count)));
                    int gx = i % cols, gy = i / cols;
                    int rows = (int)MathF.Ceiling(_count / (float)cols);
                    p = new Vector2(
                        cols == 1 ? 0.0f : Mathf.Lerp(-0.6f, 0.6f, gx / (float)(cols - 1)),
                        rows == 1 ? 0.0f : Mathf.Lerp(-0.5f, 0.5f, gy / (float)(rows - 1)));
                    break;
                }
            }
            list[i] = new ColumnField.Column(p, Radius, Kind, 0.0f);
        }
        return list;
    }

    private void Apply()
    {
        RebuildVisual();
        if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); }
    }

    private void PushMask()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.ObstaclesEnabled = true;
        s.ObstacleHardness = Hardness;
        s.UploadObstacles(ColumnField.Rasterize(Grid, Columns()));
    }

    private void RebuildVisual()
    {
        foreach (var c in _holder.GetChildren()) { c.QueueFree(); }
        const float h = 1.3f;
        var mat = ColumnField.MakeMaterial(Hardness);
        foreach (var col in Columns())
        {
            _holder.AddChild(new MeshInstance3D
            {
                Mesh = ColumnField.MakeMesh(col, h),
                MaterialOverride = mat,
                Position = new Vector3(col.Pos.X, -1.0f + h * 0.5f, col.Pos.Y),
            });
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — column count");
        ui.AddSlider("Columns", 1, 8, _count, v => { _count = (int)v; Apply(); });
        ui.AddOptions("Layout", new[] { "Grid", "Arc", "Scatter" }, 0, v => { _layout = v; Apply(); });
    }
}
