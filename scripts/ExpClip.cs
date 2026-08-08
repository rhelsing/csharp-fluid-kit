using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_clip — isolated variable: THE POSITION OF A NONLINEARITY.
//
// The curve is MangledVerb's softclip, verbatim from fx/eventide/mangledverb.cmajor:
//
//     f(x) = x / (1 + |x| + 0.28 x²)   (their tanh approximation, scaled by 1.2)
//
// IDENTICAL in both positions. The only thing that changes is where it sits:
//
//   AFTER-LOOP — applied downstream of the update, once per tick. This is MangledVerb's own
//     architecture, which is explicitly "distortion AFTER reverb": it mangles the reverberant
//     field but never feeds back into it. Reshapes what you see; does not change what the sim
//     computes next.
//
//   IN-LOOP — the same curve inside the update step, so what it does this tick changes what the
//     next tick sees. This is the position that can actually make signals interact.
//
// That is the whole experiment: same nonlinearity, two positions. Topology never buys you
// interaction — only a nonlinearity inside a feedback path does.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_clip.tscn 10 1280x900
public partial class ExpClip : ExpPoolScene
{
    private int _mode = 2;          // 0 off · 1 after-loop · 2 in-loop
    private float _drive = 6.0f;

    protected override (string Title, string Hint) SceneInfo => (
        "24_clip · nonlinearity POSITION",
        "Isolated variable: WHERE the nonlinearity sits. The curve is MangledVerb's softclip "
        + "x/(1+|x|+0.28x²), identical in both positions. AFTER-LOOP is MangledVerb's own "
        + "architecture (distortion after reverb) — it mangles the field but never feeds back. "
        + "IN-LOOP puts the same curve inside the update, where it changes what the next step "
        + "sees. Same curve, two positions: topology never buys interaction, only a "
        + "nonlinearity inside a feedback path does.");

    protected override string StateText() =>
        _mode == 0 ? "clip off (linear)"
        : _mode == 1 ? $"clip AFTER-loop drive={_drive:0.0}"
        : $"clip IN-loop drive={_drive:0.0}";

    protected override void OnSolverReady() => Push();

    private void Push()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.ClipMode = _mode;
        s.ClipDrive = _drive;
    }

    private void Apply()
    {
        if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(Push)); }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — where the clipper sits");
        ui.AddOptions("Position", new[] { "Off (linear)", "After-loop", "In-loop" }, 2,
            v => { _mode = v; Apply(); });
        ui.AddSlider("Drive", 0.5f, 40.0f, _drive, v => { _drive = v; Apply(); });
    }
}
