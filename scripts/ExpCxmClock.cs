using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cxm_clock — isolated variable: THE TANK CLOCK.
//
// The single adaptation made to cxm-1978.cmajor, swept end to end. Every delay length and every
// ratio between them is the source's; only the rate they are clocked at moves.
//
//   48000 Hz — the audio patch bit-for-bit. Longest tank delay 7188 samples = 150 ms.
//    3600 Hz — the same 7188 samples = ~2 s.
//     300 Hz — ~24 s.
//
// The incommensurate ratios (7188/6005/6807/5106) are what stop the modes phase-locking, and
// they survive the stretch untouched — that is why this is a clock change and not a retune.
// It is also not an invention: the real 224 had exactly this control (`clock`, HiFi/Standard/
// LoFi), which is why the source patch has one too.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm_clock.tscn 12 1280x900
public partial class ExpCxmClock : ExpCxmScene
{
    private static readonly Vector2[] TapPos =
    {
        new(-0.55f, -0.45f), new(0.50f, -0.55f), new(0.60f, 0.50f), new(-0.45f, 0.58f),
        new(0.00f, -0.62f), new(0.62f, 0.00f), new(0.00f, 0.62f), new(-0.62f, 0.00f),
    };

    // The source's longest tank delay, in samples. Its real duration is this / clock.
    private const float LongestDelaySamples = 7188.0f;

    protected override (string Title, string Hint) SceneInfo => (
        "24_cxm_clock · tank clock",
        "Isolated variable: the CLOCK the tank runs at, 300 Hz to 48000 Hz — the one adaptation "
        + "made to cxm-1978.cmajor. At 48000 it is the audio patch bit-for-bit (longest delay "
        + "7188 samples = 150 ms, which reads as ringing); lowering the clock stretches every "
        + "delay together while the incommensurate ratios that stop the modes phase-locking stay "
        + "exactly as Dattorro wrote them. The real 224 had this control too.");

    protected override string StateText() =>
        TankOn
            ? $"clock {TankRate:0} Hz · longest delay {LongestDelaySamples / TankRate:0.00} s · out {LastTankOut:0.000}"
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
        ui.AddSection("THE VARIABLE — tank clock");
        ui.AddSlider("Clock (Hz)", 300.0f, 48000.0f, TankRate, v =>
        {
            TankRate = v;
            Tank.Rate = v;
            // Re-derive the filter corners so the FREQUENCIES track the clock rather than
            // drifting — that is what keeps this a rescale instead of a retune.
            Tank.SetDamping(4000.0f * v / 48000.0f);
            Tank.SetCrossover(362.0f * v / 48000.0f);
        });
        AddTankControls(ui, true);
    }
}
