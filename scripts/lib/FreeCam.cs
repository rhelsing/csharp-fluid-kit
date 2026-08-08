using Godot;

namespace GodotCsharpExperiments.Lib;

// Fly camera — a C# port of ../shorewaves/scripts/free_cam.gd. Hold right mouse to look,
// WASD to move, Q/E down/up, Shift to go fast. Only acts while it's the current camera.
public partial class FreeCam : Camera3D
{
    [Export] public float Speed = 6.0f;
    private bool _looking;
    private bool _enabled = true;

    /// <summary>
    /// Gate the whole rig. Needed because SetProcess(false) does NOT hold: Godot enables
    /// processing for any script that overrides _Process, so a camera parked with
    /// SetProcess(false) before AddChild silently wakes up on tree entry — and then fights
    /// whatever else drives the transform. Defaults true, so scenes that just `new FreeCam`
    /// (Curl3D, RipplePool, Shorewaves, ShoreCurl, ShoreBase) are unchanged.
    /// </summary>
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            // Don't leave a half-finished drag armed for the next time we're switched on.
            if (!value) { _looking = false; }
        }
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!_enabled || !Current) { return; }
        if (e is InputEventMouseButton mb && mb.ButtonIndex == MouseButton.Right)
        {
            _looking = mb.Pressed;
            Input.MouseMode = _looking ? Input.MouseModeEnum.Captured : Input.MouseModeEnum.Visible;
        }
        else if (e is InputEventMouseMotion mm && _looking)
        {
            var r = Rotation;
            r.Y -= mm.Relative.X * 0.0025f;
            r.X = Mathf.Clamp(r.X - mm.Relative.Y * 0.0025f, -1.5f, 1.5f);
            Rotation = r;
        }
    }

    public override void _Process(double delta)
    {
        if (!_enabled || !Current) { return; }
        var dir = Vector3.Zero;
        Basis b = Transform.Basis;
        if (Input.IsKeyPressed(Key.W)) { dir -= b.Z; }
        if (Input.IsKeyPressed(Key.S)) { dir += b.Z; }
        if (Input.IsKeyPressed(Key.A)) { dir -= b.X; }
        if (Input.IsKeyPressed(Key.D)) { dir += b.X; }
        if (Input.IsKeyPressed(Key.E)) { dir += Vector3.Up; }
        if (Input.IsKeyPressed(Key.Q)) { dir -= Vector3.Up; }
        if (dir.LengthSquared() > 0.001f)
        {
            float sp = Speed * (Input.IsKeyPressed(Key.Shift) ? 3.5f : 1.0f);
            Position += dir.Normalized() * sp * (float)delta;
        }
    }
}
