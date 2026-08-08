using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_scat — isolated variable: OBJECT MOTION, AS A SCATTERER.
//
// The expensive half of the pair, and the most informative scene in the series.
//
// Same paths, same speeds as 24_move. The difference is what the object DOES: here it BLOCKS
// waves rather than pushing them. It lives in the obstacle mask as a no-flux wall, so it
// reflects, diffracts and casts a shadow — and because it moves, that mask has to be
// re-rasterized and re-uploaded every single frame.
//
// Which is the point. A moving scatterer CHANGES THE OPERATOR every step: the geometry is part
// of the system, so moving it breaks the time-INVARIANT half of LTI. A measured impulse
// response cannot represent this at all — not because the response is nonlinear (it is still
// perfectly linear), but because there is no longer a single time-invariant system to measure.
//
// That is the boundary the whole IR/CXM direction runs into, and it is what defines where a
// live solver has to stay.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_scat.tscn 12 1280x900
public partial class ExpScat : ExpPoolScene
{
    private MovingObject.Path _path = MovingObject.Path.Circle;
    private float _speed = 1.1f;
    private float _pathRadius = 0.55f;
    private float _size = 0.15f;
    private float _hardness = 1.0f;
    private bool _moving = true;
    private ColumnField.Shape _shape = ColumnField.Shape.Cylinder;
    private float _t;

    private Vector2 _cur;
    private MeshInstance3D _mi = null!;
    private int _uploads;

    protected override (string Title, string Hint) SceneInfo => (
        "24_scat · object motion AS A SCATTERER",
        "Isolated variable: a moving object that BLOCKS waves instead of pushing them. It lives "
        + "in the obstacle mask as a no-flux wall — reflecting, diffracting, casting a shadow — "
        + "so moving it means re-rasterizing and re-uploading that mask every frame. This CHANGES "
        + "THE OPERATOR every step, which breaks the time-INVARIANT half of LTI. A measured "
        + "impulse response cannot represent it at all, and not because anything is nonlinear: "
        + "there is simply no longer one fixed system to measure. Compare against 24_move.");

    protected override string StateText() =>
        _moving
            ? $"moving SCATTERER · {_path} · {_uploads} mask uploads/s · hard {_hardness:0.00}"
            : $"static scatterer · hard {_hardness:0.00}";

    public override void _Ready()
    {
        base._Ready();
        _cur = MovingObject.At(_path, 0.0f, _pathRadius);
        _mi = new MeshInstance3D();
        AddChild(_mi);
        RebuildVisual();
    }

    protected override void OnSolverReady()
    {
        var s = Solver;
        if (s == null) { return; }
        s.ObstaclesEnabled = true;
        s.ObstacleHardness = _hardness;
        PushMask();
    }

    private ColumnField.Column Col() => new(_cur, _size, _shape, 0.0f);

    private void PushMask()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.ObstacleHardness = _hardness;
        s.UploadObstacles(ColumnField.Rasterize(Grid, new[] { Col() }));
    }

    private void RebuildVisual()
    {
        const float h = 1.1f;   // tall enough to read as solid, short enough to see past
        _mi.Mesh = ColumnField.MakeMesh(Col(), h);
        _mi.MaterialOverride = ColumnField.MakeMaterial(_hardness);
        _mi.Position = new Vector3(_cur.X, -1.0f + h * 0.5f, _cur.Y);
    }

    private float _uploadAccum;
    private int _uploadCount;

    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        if (!_moving) { return; }
        _t += dt * _speed;
        _cur = MovingObject.At(_path, _t, _pathRadius);
        RebuildVisual();
        // The whole cost of a moving boundary: the mask is state, so it has to be rebuilt and
        // re-uploaded every frame it moves. A static scatterer uploads once, ever.
        if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); }
        _uploadCount++;
        _uploadAccum += dt;
        if (_uploadAccum >= 1.0f) { _uploads = _uploadCount; _uploadCount = 0; _uploadAccum = 0.0f; }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — motion as a scatterer");
        ui.AddToggle("Moving", _moving, v => { _moving = v; if (!v) { _uploads = 0; } });
        ui.AddOptions("Path", new[] { "Circle", "Sweep", "Figure 8" }, 0, v => _path = (MovingObject.Path)v);
        ui.AddSlider("Speed", 0.0f, 4.0f, _speed, v => _speed = v);
        ui.AddSlider("Path radius", 0.1f, 0.85f, _pathRadius, v => _pathRadius = v);
        ui.AddSlider("Object size", 0.04f, 0.4f, _size, v => { _size = v; RebuildVisual(); if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); } });
        ui.AddSlider("Hard (1) <-> Soft (0)", 0.0f, 1.0f, _hardness, v => { _hardness = v; RebuildVisual(); if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); } });
        ui.AddOptions("Shape", new[] { "Cylinder", "Square", "Blade" }, 0, v => { _shape = (ColumnField.Shape)v; RebuildVisual(); if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(PushMask)); } });
    }
}
