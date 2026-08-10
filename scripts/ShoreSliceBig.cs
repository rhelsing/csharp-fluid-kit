using Godot;
using GodotCsharpExperiments.Lib;

namespace GodotCsharpExperiments;

// Scene 290b — SLICE AT SCALE. The same two-phase slice as 290, at beach proportions and
// under REAL gravity, to establish that the size we actually need is affordable.
//
// This is the configuration that boiled. 80 m × 20 m at ~0.08 m/cell, g = 9.81 m/s² —
// twelve times the effective gravity of 290's little box — and every earlier attempt at it
// shredded the interface into froth within seconds. The difference is not more sweeps, a
// smaller step, or a gentler force. It is that the pressure no longer has to be DISCOVERED.
//
// WHY SIZE WAS THE PROBLEM, precisely. Hydrostatic pressure is a domain-scale mode: a ramp
// spanning the entire water column. Relaxation converges domain-scale modes slowest —
// K ∝ N² — so doubling the domain quadruples the work needed for the one mode that must be
// right, while the free-fall it is supposed to arrest gets stronger. Small domains hid this
// by having gravity too weak to reveal it; they were falling too, just slowly.
//
// Seeding the ramp analytically at initialisation removes the scaling entirely. The solve
// starts AT equilibrium and is only ever asked about deviations from it, which are local
// and high-frequency — precisely what relaxation is good at. So the cost stops growing with
// the domain, and the beach proportions become usable.
//
// The Froude argument (docs/shore-slice.md) says the absolute metres never mattered for the
// physics; what mattered was aspect and resolution. This scene is where that claim gets to
// be true in practice rather than in a comment.
public partial class ShoreSliceBig : ShoreSlice
{
    public ShoreSliceBig()
    {
        // 9.81 m/s² in cell units: 9.81 / 0.078 m per cell / 60² ticks ≈ 0.035 cells/tick².
        _gravity = -0.035f;
        _fill = 0.62f;
        _k = 90;              // deviations only, but 833:1 conductance needs more of them
        _speedGain = 900.0f;  // a bigger cell makes the residual velocities smaller still
        _solidWalls = true;   // mandatory with a seeded ramp, not optional — see 290
    }

    protected override string SceneTitle =>
        "290b · slice at scale — 80 m of beach under real gravity";

    protected override string SceneHint =>
        "The configuration that boiled, now holding. 80 m × 20 m at ~0.08 m/cell under real "
        + "gravity — twelve times the effective force of 290's box. What changed is not "
        + "sweeps or step size: hydrostatic pressure is a DOMAIN-SCALE mode and relaxation "
        + "converges those slowest (K ∝ N²), so every earlier attempt was free-falling while "
        + "the solver tried to build the ramp cell by cell. Seeding that ramp in closed form "
        + "at startup leaves the solve only the local deviations, which is what relaxation is "
        + "actually good at — and the cost stops growing with the domain. Toggle solid walls "
        + "off to watch it come apart: a seeded ramp is not a solution of an open boundary.";

    // Beach proportions. Real profiles are more extreme still (80 m of fetch over 4 m of
    // depth is 20:1), but a domain that thin letterboxes to a sliver on screen; 4:1 keeps it
    // readable and lets the SLOPE in 293 supply the shoaling instead of the aspect ratio.
    protected override float DomainAspect => 4.0f;
    protected override float WorldSize => 80.0f;

    // ~0.078 m/cell at 1024 wide, so a 1.5 m wave is ~19 cells through its height — about
    // the minimum to resolve an overturning jet rather than smear it away.
    protected override int[] GridOptions => new[] { 512, 1024, 2048 };
    protected override int GridDefault => 1024;
}
