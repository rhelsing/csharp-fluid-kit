using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_move — isolated variable: OBJECT MOTION, AS A SOURCE.
//
// The cheap half of the moving-object pair. Here the object PUSHES water as it moves: the sim's
// sphere pass raises water where the sphere WAS and lowers it where it IS
// (volume_in_sphere in exp_sim.glsl, mode 1). That is a moving SOURCE, not a boundary — the
// object never blocks anything, it only displaces.
//
// Why that distinction is the whole point: a moving source does not change the operator. The
// medium is untouched; only the forcing moves. Under shift-invariance a moving source is one
// kernel plus a track, which is why boat wakes are tractable to bake and a moving wall is not.
//
// 24_scat is the same motion doing the other thing. Compare them.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_move.tscn 12 1280x900
public partial class ExpMove : ExpPoolScene
{
    private MovingObject.Path _path = MovingObject.Path.Circle;
    private float _speed = 1.1f;
    private float _radius = 0.55f;
    private bool _moving = true;
    private float _t;

    private Vector2 _prev, _cur;
    private MeshInstance3D _mi = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_move · object motion AS A SOURCE",
        "Isolated variable: a moving object that PUSHES water — the sim's sphere pass raises "
        + "water where it was and lowers it where it is. It never blocks anything, it only "
        + "displaces. That means the OPERATOR never changes: the medium is untouched and only "
        + "the forcing moves, which is why a moving source stays cheap and bakeable where a "
        + "moving wall does not. 24_scat is the same motion doing the other thing.");

    protected override string StateText() =>
        _moving ? $"moving SOURCE · {_path} · speed {_speed:0.0}" : "source static";

    public override void _Ready()
    {
        base._Ready();
        _mi = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 1.0f, Height = 2.0f, RadialSegments = 24, Rings = 16 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.95f, 0.75f, 0.35f), Roughness = 0.4f },
        };
        AddChild(_mi);
        _cur = _prev = MovingObject.At(_path, 0.0f, _radius);
        UpdateVisual();
    }

    private void UpdateVisual()
    {
        float r = 0.13f;
        _mi.Transform = new Transform3D(Basis.FromScale(Vector3.One * r), new Vector3(_cur.X, -0.05f, _cur.Y));
    }

    // The sphere pass needs BOTH last frame's and this frame's centre — the displaced volume is
    // the difference between them, so a stationary object displaces exactly nothing.
    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        _prev = _cur;
        if (_moving) { _t += dt * _speed; }
        _cur = MovingObject.At(_path, _t, _radius);
        UpdateVisual();
    }

    protected override void AfterStep(float dt)
    {
        var solver = Solver;
        if (solver == null || !solver.Ready) { return; }
        var o = new Vector3(_prev.X, -0.05f, _prev.Y);
        var n = new Vector3(_cur.X, -0.05f, _cur.Y);
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.SpherePass(o, n)));
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — motion as a source");
        ui.AddToggle("Moving", _moving, v => _moving = v);
        ui.AddOptions("Path", new[] { "Circle", "Sweep", "Figure 8" }, 0, v => _path = (MovingObject.Path)v);
        ui.AddSlider("Speed", 0.0f, 4.0f, _speed, v => _speed = v);
        ui.AddSlider("Path radius", 0.1f, 0.85f, _radius, v => _radius = v);
    }
}
