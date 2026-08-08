using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// ExpCxmScene — ExpPoolScene plus a CXM-1978 tank, shared by the CXM experiment scenes.
//
// This exists because 24_cxm was built before ExpPoolScene did, so it carries its own copy of
// the scene 24 setup and its own solver. That left it off the frozen baseline: not comparable
// against 24_base, and not obstacle-capable. Everything CXM from here inherits the same
// baseline as every other scene in the series, which also means columns work in it for free.
//
// The tank itself is scripts/lib/CxmTank.cs — a 1:1 port of cxm-1978.cmajor, exact delay
// lengths and coefficients. Only its CLOCK is adapted (see CxmTank), and that is a control the
// real 224 also had.
public abstract partial class ExpCxmScene : ExpPoolScene
{
    protected CxmTank Tank = null!;

    // 3600 Hz puts the longest delay (7188 samples) at ~2 s. Every delay length and every ratio
    // between them stays exactly as Dattorro wrote them; only the clock moves.
    protected float TankRate = 3600.0f;
    protected bool TankOn = true;
    protected float TankGain = 20.0f;
    protected float TankDrive = 1.0f;
    private double _tankAcc;

    protected int TapCount = 4;
    protected float LastTankOut;

    /// <summary>The newest tap values this frame, length TapCount.</summary>
    protected float[] TapValues = new float[CxmTank.MaxTaps];

    public override void _Ready()
    {
        Tank = new CxmTank(TankRate);
        Tank.SetType(2);           // Hall
        Tank.SetDiffusion(2);      // High
        Tank.SetTankMod(1);        // Med
        Tank.SetDecay(0.85f);
        Tank.SetBass(0.9f);
        Tank.SetPreDelayMs(0.0f);  // at a stretched clock its 2016 samples are dead time
        base._Ready();
    }

    /// <summary>
    /// Run the tank at its own clock for this frame's worth of samples and latch the newest tap
    /// values. Sample-and-hold, NOT the frame mean: the nodes carry an oscillating signal, so
    /// averaging ~60 tank samples into one number cancels it (measured: mean peak 0.0003 against
    /// a node peak of 0.0074, a 25x loss that read on screen as "the tank does nothing").
    /// </summary>
    protected void RunTank(float dt, float excitation)
    {
        if (!TankOn) { LastTankOut = 0.0f; return; }
        Tank.Rate = TankRate;
        _tankAcc += dt * TankRate;
        int steps = Math.Min((int)_tankAcc, 8192);
        _tankAcc -= steps;
        if (steps <= 0) { return; }

        for (int i = 0; i < steps; ++i)
        {
            // The excitation is an EVENT, not a level: feed it on the first sample only, or one
            // impulse becomes DC held for the whole frame.
            Tank.Process(i == 0 ? excitation * TankDrive : 0.0f);
        }
        float[] taps = Tank.Taps(TapCount);
        LastTankOut = 0.0f;
        for (int t = 0; t < TapCount; ++t)
        {
            TapValues[t] = taps[t] * TankGain;
            LastTankOut += MathF.Abs(TapValues[t]);
        }
    }

    /// <summary>Tank controls common to every CXM scene. Subclasses add their own variable.</summary>
    protected void AddTankControls(DemoUI ui, bool includeTapCount)
    {
        ui.AddSection("CXM tank (tuned — same in every CXM scene)");
        ui.AddToggle("Tank on", TankOn, v => { TankOn = v; if (!v) { Tank.Reset(); } });
        ui.AddSlider("Tank clock (Hz)", 300.0f, 48000.0f, TankRate, v =>
        {
            TankRate = v;
            Tank.Rate = v;
            Tank.SetDamping(4000.0f * v / 48000.0f);
            Tank.SetCrossover(362.0f * v / 48000.0f);
        });
        ui.AddSlider("Return gain", 0.0f, 200.0f, TankGain, v => TankGain = v);
        ui.AddSlider("Drive", 0.0f, 4.0f, TankDrive, v => TankDrive = v);
        if (includeTapCount)
        {
            ui.AddSlider("Taps", 4, CxmTank.MaxTaps, TapCount, v => TapCount = (int)v);
        }
        ui.AddOptions("Type", new[] { "Room", "Plate", "Hall" }, 2, v => Tank.SetType(v));
        ui.AddOptions("Diffusion", new[] { "Low", "Med", "High" }, 2, v => Tank.SetDiffusion(v));
        ui.AddOptions("Tank mod", new[] { "Low", "Med", "High" }, 1, v => Tank.SetTankMod(v));
        ui.AddSlider("Decay (mids)", 0.0f, 1.0f, 0.85f, v => Tank.SetDecay(v));
        ui.AddSlider("Bass decay", 0.0f, 1.0f, 0.9f, v => Tank.SetBass(v));
    }
}
