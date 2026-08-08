using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cxm_type — isolated variable: THE SOURCE'S OWN TUNED PRESETS.
//
// Not knobs I invented — these are the tables inside cxm-1978.cmajor, applied verbatim by
// CxmTank.SetType / SetDiffusion / SetTankMod:
//
//   Type       Room  decayScale 0.7, gDecay 0.60, gDecay2 0.45
//              Plate decayScale 1.0, gDecay 0.70, gDecay2 0.50
//              Hall  decayScale 1.3, gDecay 0.75, gDecay2 0.55
//   Diffusion  Low 0.4 / Med 0.7 / High 0.9, scaling the 4-stage input diffuser's
//              0.75/0.75/0.625/0.625 coefficients
//   Tank mod   Low  depth 4, rate 0.5 Hz
//              Med  depth 12, rate 1.0 Hz
//              High depth 24, rate 2.5 Hz  (the "Leslie-like" setting)
//
// Type and Diffusion shape the density and decay; Tank mod is the one that matters most for
// water, because modulating the delay lengths is what stops the pattern freezing into a single
// standing wave. Clock, taps and drive are pinned so only the preset varies.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm_type.tscn 12 1280x900
public partial class ExpCxmType : ExpCxmScene
{
    private static readonly string[] TypeNames = { "Room", "Plate", "Hall" };
    private static readonly string[] Levels = { "Low", "Med", "High" };
    private int _type = 2, _diffusion = 2, _mod = 1;

    private static readonly Vector2[] TapPos =
    {
        new(-0.55f, -0.45f), new(0.50f, -0.55f), new(0.60f, 0.50f), new(-0.45f, 0.58f),
        new(0.00f, -0.62f), new(0.62f, 0.00f), new(0.00f, 0.62f), new(-0.62f, 0.00f),
    };

    protected override (string Title, string Hint) SceneInfo => (
        "24_cxm_type · the tuned presets",
        "Isolated variable: the source's OWN preset tables — Type (Room/Plate/Hall), Diffusion "
        + "and Tank mod, applied exactly as cxm-1978.cmajor defines them. Type sets decayScale "
        + "and the decay-diffuser coefficients; Diffusion scales the 4-stage input diffuser; "
        + "Tank mod sets the modulated-delay depth and rate — the one that matters most here, "
        + "since modulating delay length is what stops the pattern freezing into one standing "
        + "wave. Clock, taps and drive pinned.");

    protected override string StateText() =>
        TankOn
            ? $"{TypeNames[_type]} · diff {Levels[_diffusion]} · mod {Levels[_mod]} · out {LastTankOut:0.000}"
            : "tank off";

    public override void _Ready()
    {
        TapCount = 4;
        base._Ready();
    }

    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        float e = outDrops.Count > 0 ? outDrops[0].Z : 0.0f;
        RunTank(dt, e);
        if (!TankOn) { return; }
        for (int i = 0; i < TapCount && i < TapPos.Length; ++i)
        {
            if (MathF.Abs(TapValues[i]) < 1.0e-7f) { continue; }
            outDrops.Add(new Vector3(TapPos[i].X, TapPos[i].Y, TapValues[i]));
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — the source's preset tables");
        ui.AddOptions("Type", TypeNames, 2, v => { _type = v; Tank.SetType(v); });
        ui.AddOptions("Diffusion", Levels, 2, v => { _diffusion = v; Tank.SetDiffusion(v); });
        ui.AddOptions("Tank mod", Levels, 1, v => { _mod = v; Tank.SetTankMod(v); });
        AddTankControls(ui, true);
    }
}
