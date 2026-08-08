using System;
using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 24_cxm_taps — isolated variable: TAP COUNT AND POSITION.
//
// The seam where the audio DSP becomes spatial, and the most on-goal scene in the series.
//
// In a reverb, Dattorro's extra output taps only buy stereo decorrelation — you hear a wider
// image, nothing more. Injected into water, each tap is a PLACE. So tap count stops being a
// stereo nicety and becomes the spatial resolution of everything the tank contributes.
//
// Tank tuning is held fixed (clock, type, diffusion, decay) so the only thing varying is the
// tap geometry: how many, arranged how, spread how far. On ExpPoolScene, so obstacles are
// available in the same scene — tank taps scattering off columns is the combination the whole
// plan is aiming at.
//
// Note on taps 4+: the CXM patch collapses the tank to four raw nodes and says so itself
// ("simplified - full Dattorro uses 7 taps per channel"), so the paper's tap offsets are not in
// the source and are not invented here. See CxmTank.Taps().
//
//   tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/24_cxm_taps.tscn 12 1280x900
public partial class ExpCxmTaps : ExpCxmScene
{
    private int _layout;            // 0 ring · 1 line · 2 scatter
    private float _spread = 0.62f;
    private bool _showTaps = true;

    private Node3D _markers = null!;
    private const float TapRadius = 0.10f;

    protected override (string Title, string Hint) SceneInfo => (
        "24_cxm_taps · tap count + position",
        "Isolated variable: HOW MANY tank taps and WHERE they land, 4 to 8. In a reverb the extra "
        + "Dattorro taps only decorrelate stereo; injected into water each tap is a PLACE, so tap "
        + "count is the spatial resolution of the tank's contribution. Tank tuning is pinned — "
        + "only the tap geometry varies. Taps 0-3 are the source's four raw nodes; 4+ are extra "
        + "reads of the same delay lines (the patch does not carry Dattorro's published offsets).");

    protected override string StateText() =>
        TankOn
            ? $"{TapCount} taps · {(_layout == 0 ? "ring" : _layout == 1 ? "line" : "scatter")} · out {LastTankOut:0.0000}"
            : "tank off";

    public override void _Ready()
    {
        TapCount = 6;
        base._Ready();
        _markers = new Node3D();
        AddChild(_markers);
        RebuildMarkers();
    }

    private static float Hash01(int i)
    {
        float v = MathF.Sin(i * 12.9898f) * 43758.5453f;
        return v - MathF.Floor(v);
    }

    private Vector2 TapPos(int i)
    {
        switch (_layout)
        {
            case 1:   // line across the pool
            {
                float t = TapCount == 1 ? 0.5f : i / (float)(TapCount - 1);
                return new Vector2(Mathf.Lerp(-_spread, _spread, t), -0.1f);
            }
            case 2:   // scatter, deterministic so it holds still
                return new Vector2(
                    (Hash01(i * 2 + 5) * 2.0f - 1.0f) * _spread,
                    (Hash01(i * 2 + 17) * 2.0f - 1.0f) * _spread);
            default:  // ring — spreads the taps evenly around the basin
            {
                float a = i / (float)TapCount * MathF.Tau;
                return new Vector2(MathF.Cos(a) * _spread, MathF.Sin(a) * _spread);
            }
        }
    }

    // The tank's taps are a forcing term, not a time step: inject them as drops and let the
    // solver carry them. 24_cxm_field is the scene that questions whether that is the right
    // way to turn N scalars into a field at all.
    protected override void CollectDrive(float dt, System.Collections.Generic.List<Vector3> outDrops)
    {
        base.CollectDrive(dt, outDrops);
        float e = outDrops.Count > 0 ? outDrops[0].Z : 0.0f;
        RunTank(dt, e);
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
        if (!_showTaps) { return; }
        var mat = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.35f, 0.95f, 0.75f),
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
        ui.AddSection("THE VARIABLE — tap count + position");
        ui.AddSlider("Taps", 4, CxmTank.MaxTaps, TapCount, v => { TapCount = (int)v; RebuildMarkers(); });
        ui.AddOptions("Layout", new[] { "Ring", "Line", "Scatter" }, 0, v => { _layout = v; RebuildMarkers(); });
        ui.AddSlider("Spread", 0.1f, 0.9f, _spread, v => { _spread = v; RebuildMarkers(); });
        ui.AddToggle("Show tap markers", _showTaps, v => { _showTaps = v; RebuildMarkers(); });
        AddTankControls(ui, false);
    }
}
