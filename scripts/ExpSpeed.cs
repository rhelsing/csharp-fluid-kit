using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_speed — isolated variable: AMPLITUDE-DEPENDENT WAVE SPEED.
//
// The physically real nonlinearity, and the one superposition cannot fake:
//
//     c = sqrt(g (h + eta))
//
// A big wave rides on deeper water and travels faster, so it changes the medium the next wave
// passes through. Two waves stop being independent — which is exactly what "interact" means.
// Coupling 0 is the linear baseline; raise it and crests should steepen and outrun troughs.
//
// ONE HONEST CONSTRAINT, and it is the interesting one. The explicit scheme already sits AT the
// 2D CFL limit (coeff = 2.0 in the kernel), so the coupling may only ever pull the coefficient
// DOWN — troughs slowed rather than crests sped up. Same relative ordering, expressed inside
// the stable range. If this has to be kept small to stay stable, or the asymmetry reads wrong,
// that is precisely the symptom docs/dsp-experiments.md lists as justifying an implicit stamp:
// an implicit solve is unconditionally stable and would let the coupling go both ways.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_speed.tscn 10 1280x900
public partial class ExpSpeed : ExpPoolScene
{
    private float _coupling = 0.6f;

    protected override (string Title, string Hint) SceneInfo => (
        "24_speed · amplitude-dependent speed",
        "Isolated variable: how strongly wave speed depends on wave height, c = √(g(h+η)). A big "
        + "wave rides on deeper water and goes faster, so it changes the medium the next wave "
        + "travels through — the one nonlinearity superposition genuinely cannot fake. Coupling "
        + "0 is the linear baseline. NOTE: the explicit scheme is already at its CFL limit, so "
        + "the coupling can only SLOW troughs, never speed crests past the limit — same relative "
        + "ordering, inside the stable range.");

    protected override string StateText() =>
        _coupling <= 0.0f ? "coupling 0 (linear)" : $"speed coupling {_coupling:0.00}";

    public override void _Ready()
    {
        // A little stronger and slower than the baseline: the effect is amplitude-driven, so it
        // needs waves with some height in them to show at all.
        DriveStrength = 0.11f;
        DriveInterval = 1.0f;
        base._Ready();
    }

    protected override void OnSolverReady() => Push();

    private void Push()
    {
        var s = Solver;
        if (s == null || !s.Ready) { return; }
        s.SpeedCoupling = _coupling;
    }

    private void Apply()
    {
        if (Solver != null) { RenderingServer.CallOnRenderThread(Callable.From(Push)); }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — amplitude ↔ speed coupling");
        ui.AddSlider("Coupling (0 = linear)", 0.0f, 1.0f, _coupling, v => { _coupling = v; Apply(); });
    }
}
