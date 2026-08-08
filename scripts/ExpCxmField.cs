using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cxm_field — isolated variable: HOW THE TANK'S SCALARS BECOME A FIELD.
//
// This is the scene for the question the whole plan rests on: "how do we get it performant like
// a function?"
//
// A reverb tank hands you ~8 SCALARS per tick. Water needs a 2D field. Turning 8 numbers into
// 65k cells is a SEPARATE cost from running the tank, and it is where the budget of every cheap
// method actually lands — the 8-tap tank being nearly free does not help if painting its output
// across the grid is not.
//
// Three modes, same tank, same taps, same drive:
//
//   SIM DROPS   — taps injected as drops, the full solver propagates them. What 24_cxm and
//                 24_cxm_taps do. The expansion is done BY the expensive thing.
//                 Cost: 256^2 x 4 passes per frame, every frame, plus a CFL-bound substep loop.
//
//   MODAL BASIS — solver OFF entirely. h(x,z) = SUM_n a_n * cos(m_n*PI*x) * cos(k_n*PI*z),
//                 the low-order spatial basis from path-a §4, evaluated analytically in-shader
//                 so the basis costs ZERO memory. No time stepping, no neighbour access, no
//                 CFL, no substeps — every cell is an independent weighted sum of N cosines.
//                 Cost: 256^2 x N modes, one pass. That is a function evaluation.
//
//   BOTH        — basis added on top of a live sim, for the hybrid case.
//
// The readout prints both costs so the claim is answerable rather than asserted.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm_field.tscn 12 1280x900
public partial class ExpCxmField : ExpCxmScene
{
    private enum Expand { SimDrops, ModalBasis, Both }

    private Expand _mode = Expand.ModalBasis;
    private float _basisGain = 30.0f;

    private static readonly Vector2[] TapPos =
    {
        new(-0.55f, -0.45f), new(0.50f, -0.55f), new(0.60f, 0.50f), new(-0.45f, 0.58f),
        new(0.00f, -0.62f), new(0.62f, 0.00f), new(0.00f, 0.62f), new(-0.62f, 0.00f),
    };
    private const float TapRadius = 0.10f;

    protected override (string Title, string Hint) SceneInfo => (
        "24_cxm_field · scalars → field",
        "Isolated variable: HOW the tank's ~8 scalars become a 2D field. SIM DROPS injects the "
        + "taps and lets the full solver spread them — the expansion is done by the expensive "
        + "thing. MODAL BASIS switches the solver OFF and evaluates h = Σ aₙ·cos(mₙπx)·cos(kₙπz) "
        + "analytically per cell: no time stepping, no neighbours, no CFL, no substeps, and zero "
        + "memory for the basis. That is a function evaluation rather than a recurrence. Costs "
        + "for both are on the readout.");

    protected override string StateText()
    {
        long sim = (long)Grid * Grid * 4 / 1000;
        long basis = (long)Grid * Grid * TapCount / 1000;
        return _mode switch
        {
            Expand.SimDrops => $"SIM DROPS · solver ON · {sim}k cell-ops/frame",
            Expand.ModalBasis => $"MODAL BASIS · solver OFF · {basis}k MACs/frame ({TapCount} modes)",
            _ => $"BOTH · {sim}k + {basis}k /frame",
        };
    }

    public override void _Ready()
    {
        TapCount = 6;
        base._Ready();
    }

    // In MODAL BASIS the solver must not advance at all — that is the entire point. The base
    // class skips Step() when this is false, but still collects the drive, binds the display
    // and reports fps, so the only thing removed is the time stepping.
    protected override bool StepSolver => _mode != Expand.ModalBasis;

    protected override void AfterStep(float dt)
    {
        if (_mode == Expand.SimDrops) { return; }
        // additive on top of a live sim in Both; the sole contents of the field otherwise.
        PushBasis(_mode == Expand.Both);
    }

    private void PushBasis(bool additive)
    {
        var solver = Solver;
        if (solver == null || !solver.Ready) { return; }
        var amps = new float[CxmTank.MaxTaps];
        Array.Copy(TapValues, amps, CxmTank.MaxTaps);
        int n = TapCount;
        float g = _basisGain;
        RenderingServer.CallOnRenderThread(Callable.From(() => solver.ModalBasis(amps, n, g, additive)));
    }

    // SIM DROPS path: taps become drops, exactly as 24_cxm_taps does.
    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        float e = outDrops.Count > 0 ? outDrops[0].Z : 0.0f;
        RunTank(dt, e);
        if (!TankOn || _mode == Expand.ModalBasis) { return; }
        for (int i = 0; i < TapCount && i < TapPos.Length; ++i)
        {
            if (MathF.Abs(TapValues[i]) < 1.0e-7f) { continue; }
            outDrops.Add(new Vector3(TapPos[i].X, TapPos[i].Y, TapValues[i]));
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — how scalars become a field");
        ui.AddOptions("Expansion", new[] { "Sim drops (solver ON)", "Modal basis (solver OFF)", "Both" }, 1,
            v => _mode = (Expand)v);
        ui.AddSlider("Basis gain", 0.0f, 200.0f, _basisGain, v => _basisGain = v);
        ui.AddSlider("Modes / taps", 4, CxmTank.MaxTaps, TapCount, v => TapCount = (int)v);
        AddTankControls(ui, false);
    }
}
