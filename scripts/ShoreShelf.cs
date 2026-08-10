using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 293 — SHELF. A sloping seabed, and therefore shoaling: the first scene where the
// beach is a beach rather than a tank.
//
// WHY THE SLOPE IS THE WHOLE POINT. Whether a wave spills, plunges or surges is set by the
// Iribarren number ξ = tanβ / √(H/L₀) — beach slope against wave steepness, pure geometry.
// ξ < 0.5 spills, 0.5–3.3 PLUNGES (the curl), > 3.3 surges. Every scene before this one had
// tanβ = 0, which is why none of them could ever break: with a flat bed there is no
// shoaling, no steepening, and ξ is identically zero.
//
// HOW IT COSTS NOTHING TO ADD. The bed is a sub-grid OPEN FRACTION on each face, multiplying
// the conductance the projection already computes — exactly what stamp_wave_batty.glslinc
// does for island waterlines. One face carries two independent weights:
//
//     conductance = open_fraction × (1 / ρ_face)
//
// The two-phase part and the solid part never need to know about each other. That is the
// payoff of having written the projection in the stamp form rather than hard-coding a
// constant-coefficient Laplacian, and it is why 293 is a parameter change plus a boundary
// term rather than a new solver.
//
// SHOALING HAS AN ANALYTIC CHECK, which makes this the second scene in the whole project
// with a closed-form reference (259's r = c·t was the first). Green's law: as depth falls,
// a non-breaking wave grows as H ∝ h^(−1/4) while its length shortens. Measure the crest
// against that before trusting anything about the break — if the energy bookkeeping is
// wrong here, 294 will break at the wrong place for reasons invisible on screen.
public partial class ShoreShelf : ShoreSlice
{
    public ShoreShelf()
    {
        _fill = 0.33f;
        // Beach-scale gravity. These extend ShoreSlice (the 6 m control), not
        // ShoreSliceBig, so without this they silently inherit -0.012 — a third of real g
        // once the grid ratio is applied. Wave speed is √(gh), so everything runs sluggish
        // and shoals weakly, and nothing on screen says why.
        _gravity = -0.035f;
        _k = 90;
        _drag = 0f;        // no friction yet — 293 is shoaling ALONE, cleanly
        _paddleAmp = 0f;   // and no waves yet either: prove the still bed first
    }

    protected override string SceneTitle => "293 · shelf — a sloping bed, and shoaling";

    protected override string SceneHint =>
        "The first scene with a beach in it. The bed enters as a sub-grid OPEN FRACTION on "
        + "each face, multiplying the 1/ρ_face conductance the projection already computes — "
        + "the same structure scene 52 uses for island waterlines, so a slope costs a "
        + "boundary term rather than a solver. bed_slope IS tanβ, the numerator of the "
        + "Iribarren number that decides spilling vs plunging vs surging; every earlier scene "
        + "had tanβ = 0 and therefore could never break. Wavemaker and friction are OFF here "
        + "on purpose: prove still water sits correctly against a slope first, because a "
        + "wrong equilibrium here breaks 294 in ways that are invisible on screen.";

    protected override string ArtifactName => "shoaling (bed slope tanβ)";

    // 1:10 — steep enough to plunge rather than spill. ξ ≈ 1 at typical swell steepness,
    // which is the middle of the plunging band.
    protected override float BedSlope => 0.10f;

    protected override float DomainAspect => 4.0f;
    protected override float WorldSize => 80.0f;
    protected override int[] GridOptions => new[] { 512, 1024, 2048 };
    protected override int GridDefault => 1024;
}
