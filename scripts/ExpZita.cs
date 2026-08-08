using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_zita — isolated variable: REVERB TOPOLOGY.
//
// Figure-of-eight (CXM/Dattorro) vs feedback delay network (Zita). Same drive, same clock
// treatment, same tap positions, same pool — only the network in between changes.
//
// The structural difference is the point:
//
//   FIGURE-OF-EIGHT — two cross-coupled chains, `inputA = in + lastB*fb`. Hardwired to 2.
//     Its four raw nodes are what you get; more taps mean re-reading the same two chains.
//
//   FDN — 8 parallel delay lines mixed by an orthogonal Hadamard butterfly, so every channel
//     feeds every other. Channel count is set by the mixing matrix, so it generalizes to N,
//     and it hands out EIGHT genuinely distinct channels natively.
//
// For a reverb that difference is mostly density and smoothness. For water it may matter more,
// because each channel is injected at a PLACE — so the question this scene asks is whether
// N-way orthogonal mixing buys anything spatially that two cross-coupled chains cannot.
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_zita.tscn 12 1280x900
public partial class ExpZita : ExpCxmScene
{
    private bool _useZita = true;
    private ZitaFdn _zita = null!;
    private float _spread = 0.62f;
    private double _zitaAcc;
    private Node3D _markers = null!;

    protected override (string Title, string Hint) SceneInfo => (
        "24_zita · topology — figure-of-eight vs FDN",
        "Isolated variable: the NETWORK. Dattorro's figure-of-eight is two cross-coupled chains, "
        + "hardwired to 2, giving four raw nodes. Zita is 8 parallel delay lines mixed by an "
        + "orthogonal Hadamard butterfly — channel count is set by the mixing matrix, so it "
        + "generalizes to N and hands out 8 distinct channels natively. Same drive, same clock "
        + "treatment, same tap positions. For water each channel is a PLACE, so the question is "
        + "whether N-way mixing buys anything spatially.");

    protected override string StateText() =>
        !TankOn ? "network off"
        : _useZita ? $"ZITA FDN · 8ch Hadamard · {TapCount} taps · out {LastTankOut:0.000}"
        : $"CXM figure-of-eight · 2 chains · {TapCount} taps · out {LastTankOut:0.000}";

    public override void _Ready()
    {
        TapCount = 8;
        base._Ready();
        _zita = new ZitaFdn(TankRate);
        _markers = new Node3D();
        AddChild(_markers);
        RebuildMarkers();
    }

    private Vector2 TapPos(int i)
    {
        float a = i / (float)Math.Max(TapCount, 1) * MathF.Tau;
        return new Vector2(MathF.Cos(a) * _spread, MathF.Sin(a) * _spread);
    }

    // Zita runs on its own clock exactly as the CXM tank does, with the same sample-and-hold
    // decimation — the frame MEAN would cancel an oscillating channel.
    private void RunZita(float dt, float excitation)
    {
        if (!TankOn) { LastTankOut = 0.0f; return; }
        _zita.Rate = TankRate;
        _zitaAcc += dt * TankRate;
        int steps = Math.Min((int)_zitaAcc, 8192);
        _zitaAcc -= steps;
        if (steps <= 0) { return; }
        float[] ch = null!;
        for (int i = 0; i < steps; ++i)
        {
            ch = _zita.Process(i == 0 ? excitation * TankDrive : 0.0f);
        }
        LastTankOut = 0.0f;
        for (int t = 0; t < TapCount && t < ZitaFdn.Channels; ++t)
        {
            TapValues[t] = ch[t] * TankGain;
            LastTankOut += MathF.Abs(TapValues[t]);
        }
    }

    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        float e = outDrops.Count > 0 ? outDrops[0].Z : 0.0f;
        if (_useZita) { RunZita(dt, e); } else { RunTank(dt, e); }
        if (!TankOn) { return; }
        for (int i = 0; i < TapCount; ++i)
        {
            if (MathF.Abs(TapValues[i]) < 1.0e-7f) { continue; }
            var p = TapPos(i);
            outDrops.Add(new Vector3(p.X, p.Y, TapValues[i]));
        }
    }

    private void RebuildMarkers()
    {
        foreach (var c in _markers.GetChildren()) { c.QueueFree(); }
        var mat = new StandardMaterial3D
        {
            AlbedoColor = _useZita ? new Color(1.0f, 0.55f, 0.85f) : new Color(0.35f, 0.95f, 0.75f),
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        for (int i = 0; i < TapCount; ++i)
        {
            var p = TapPos(i);
            _markers.AddChild(new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 0.03f, Height = 0.06f, RadialSegments = 12, Rings = 8 },
                MaterialOverride = mat,
                Position = new Vector3(p.X, 0.05f, p.Y),
            });
        }
    }

    protected override void AddVariableControls(DemoUI ui)
    {
        ui.AddSection("THE VARIABLE — topology");
        ui.AddOptions("Network", new[] { "CXM figure-of-eight", "Zita FDN (8ch Hadamard)" }, 1,
            v => { _useZita = v == 1; Tank.Reset(); _zita.Reset(); RebuildMarkers(); });
        ui.AddSlider("Taps", 4, ZitaFdn.Channels, TapCount, v => { TapCount = (int)v; RebuildMarkers(); });
        ui.AddSlider("Spread", 0.1f, 0.9f, _spread, v => { _spread = v; RebuildMarkers(); });

        ui.AddSection("Zita parameters (its own, not CXM's)");
        ui.AddSlider("Low RT60 (s)", 0.5f, 8.0f, 3.0f, v => _zita.SetRtLow(v));
        ui.AddSlider("Mid RT60 (s)", 0.5f, 8.0f, 2.0f, v => _zita.SetRtMid(v));
        ui.AddSlider("Crossover (Hz)", 50.0f, 1000.0f, 200.0f, v => _zita.SetCrossover(v));
        ui.AddSlider("HF damping (Hz)", 1500.0f, 24000.0f, 6000.0f, v => _zita.SetDamping(v));

        AddTankControls(ui, false);
    }
}
