using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 294 — CURL. The whole point: a wave that shoals, steepens, and throws its crest
// forward over its own trough.
//
// Everything before this exists to make this scene possible rather than plausible:
//
//   290  two phases, so the surface is a volumetric field and CAN overturn — a height
//        field h(x) cannot, because a curling wave has three surfaces above one x.
//   290b real gravity at beach scale, once the face-weighted gradient stopped detonating.
//   291  an interface that survives advection instead of dissolving into fog.
//   293  a slope, which is tanβ, without which the Iribarren number is zero and nothing
//        can ever break.
//
// This scene adds the two remaining pieces:
//
//   THE WAVEMAKER. A piston: a column near the offshore wall driven sinusoidally in x, and
//   applied only BELOW the waterline, because a real paddle pushes water rather than air —
//   swinging the atmosphere too would pump a pressure wave instead of a gravity wave.
//
//   BOTTOM FRICTION. This is what makes a wave BREAK rather than merely steepen forever.
//   Drag removes momentum from the base of the wave while the crest keeps its own, so the
//   crest overruns the trough and tips. Without it a shoaling wave sharpens indefinitely
//   and never throws.
//
// WHAT TO CHECK, beyond whether it looks right: the breaker index. A wave breaks when its
// height reaches roughly γ = H/h ≈ 0.78 of the local depth. If it breaks far from that, the
// energy bookkeeping upstream is wrong — most likely the shoaling in 293 — and the picture
// is a coincidence rather than a result.
public partial class ShoreBreaker : ShoreSlice
{
    public ShoreBreaker()
    {
        _fill = 0.33f;
        _k = 90;
        _paddleAmp = 0.10f;    // ≈ 0.47 m/s orbital, now that it is a velocity BC
        // Derived rather than dialled. Cell 0.078 m, 60 ticks/s, offshore depth 0.33×20 m
        // = 6.6 m. A 4.5 s period gives L₀ = gT²/2π ≈ 32 m, so ~2.5 wavelengths fit the 80 m
        // domain and kh ≈ 1.3 — intermediate water, which is where shoaling actually acts.
        // The previous 0.045 rad/tick was a 2.3 s period, L₀ ≈ 8 m, kh ≈ 5: deep-water chop
        // that never feels the bed at all.
        _paddleFreq = 0.023f;  // 2π / (4.5 s × 60 ticks)
        _drag = 0.035f;        // the knob that converts steepening into breaking
        _dragDepth = 40f;      // a real surf-zone boundary layer, not a 4-cell sliver
        _sponge = 0.06f;       // absorb at the offshore wall so nothing reflects back
        // 0.30 flooded the domain to all-water. The sharpening remap is NOT conservative —
        // under violent mixing, cells hovering near φ = 0.5 get snapped to whichever phase
        // is locally ahead, and with more water than air present that cascades until ρ is
        // 1.0 everywhere. Measured: rho [1.000..1.000]. Lower strength keeps the interface
        // without letting it run away; Olsson–Kreiss compression is the real fix.
        _sharpen = 0.20f;
    }

    protected override string SceneTitle => "294 · curl — shoal, steepen, throw";

    protected override string SceneHint =>
        "Swell from the left over a 1:10 bed. The piston pushes only below the waterline — a "
        + "paddle moves water, not air, and driving the atmosphere would make a pressure wave "
        + "instead of a gravity wave. BOTTOM FRICTION is what turns steepening into breaking: "
        + "drag strips momentum from the base while the crest keeps its own, so the crest "
        + "overruns the trough and tips forward. Set drag to 0 and the wave will steepen "
        + "forever without ever throwing. The number to judge it by is the breaker index "
        + "γ = H/h ≈ 0.78 — if it breaks far from that, the shoaling in 293 is wrong and the "
        + "picture is a coincidence.";

    protected override string ArtifactName => "the break (γ = H/h ≈ 0.78)";

    protected override float BedY0 => 6f;
    protected override float BedSlope => 0.10f;

    protected override float DomainAspect => 4.0f;
    protected override float WorldSize => 80.0f;
    protected override int[] GridOptions => new[] { 512, 1024, 2048 };
    protected override int GridDefault => 1024;

    protected override void BuildSimKnobs(DemoUI ui)
    {
        base.BuildSimKnobs(ui);
        ui.AddSlider("Paddle · stroke (cells/tick)", 0.0f, 2.0f, _paddleAmp, v => _paddleAmp = v);
        ui.AddSlider("Paddle · frequency (rad/tick)", 0.005f, 0.2f, _paddleFreq, v => _paddleFreq = v);
        ui.AddSlider("Bottom friction (0 = steepens forever, never breaks)", 0.0f, 0.15f,
            _drag, v => _drag = v);
        ui.AddSlider("Friction depth (cells above bed — 4 is a sliver, 40 is a surf zone)",
            2f, 90f, _dragDepth, v => _dragDepth = v);
        ui.AddSlider("Offshore sponge (0 = walls reflect, waves curl backwards)", 0.0f, 0.3f,
            _sponge, v => _sponge = v);
    }
}
