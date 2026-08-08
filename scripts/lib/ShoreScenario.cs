using Godot;

namespace GodotCsharpExperiments.Lib;

// CPU bathymetry for the SHORE scenario — the bigger, curved-shoreline bed that the
// solver-equivalence series (scene 26+) runs on. Same grid conventions and the same
// downstream plumbing as BeachScenario (see Bathymetry); only the bed differs.
//
//   BeachScenario:  30.4 m,  608² @ dx 0.05  — a straight 1:12 ramp, one wavelength wide.
//   ShoreScenario:  92.2 m,  768² @ dx 0.12  — a real shoreline: it meanders, so the surf
//                                              zone is oblique in places and the far field
//                                              actually has somewhere to go.
//
// Cost is deliberately held ~constant against scene 17: 2.6× the wavelengths for 1.6× the
// cells, paid for by the coarser dx (which also buys a proportionally bigger dt at the same
// CFL number — 0.003·√(g·3)/0.12 = 0.136, vs 17's 0.002·√(g·1.2)/0.05 = 0.137).
//
// The bed, seaward to landward:
//   • flat shelf at -3 m beyond 38 m offshore — the wavemaker's analytic incident wave
//     assumes a uniform depth0, so the whole west relaxation zone must sit on the shelf;
//   • a Dean equilibrium profile h = A·d^(2/3) in from there (the concave-up nearshore
//     real beaches settle into, not a straight ramp);
//   • a longshore sandbar ~16 m out, so swell shoals and breaks OFFSHORE, reforms in the
//     trough, and breaks again at the beach — two break lines instead of one;
//   • a 1:15 beach face above the waterline with a faint along-shore ripple.
public static class ShoreScenario
{
    public const int Grid = 768;
    public const int Corners = 769;
    public const float Dx = 0.12f;
    public const float Domain = Grid * Dx;         // 92.16 m

    /// <summary>Offshore still-water depth. The wavemaker is driven with this same value.</summary>
    public const float ShelfDepth = 3.0f;

    /// <summary>Distance offshore at which the Dean profile reaches ShelfDepth and flattens.</summary>
    private const float ShelfDist = 38.0f;

    private static readonly float DeanA = ShelfDepth / Mathf.Pow(ShelfDist, 2.0f / 3.0f);

    /// <summary>
    /// Across-shore position of the waterline (bed = 0) at along-shore coordinate z: two
    /// incommensurate meanders give a broad bay and a headland without repeating over the
    /// domain. Kept ≥ ~50 m so the west relaxation zone is always on the flat shelf.
    /// </summary>
    public static float ShoreX(float z) =>
        64.0f
        + 9.0f * Mathf.Sin(z * (Mathf.Tau / 74.0f) + 0.6f)
        + 4.5f * Mathf.Sin(z * (Mathf.Tau / 27.0f) + 2.2f);

    public static float ShoreBase(float x, float z)
    {
        float d = x - ShoreX(z);          // >0 landward (dry), <0 seaward
        if (d >= 0.0f)
        {
            // beach face: 1:15, with an along-shore ripple that fades in up the berm
            return d / 15.0f + 0.06f * Mathf.Sin(z * 0.55f + 1.7f) * Bathymetry.Smoothstep(1.0f, 12.0f, d);
        }
        float dist = -d;                  // metres offshore of the waterline
        float h = Mathf.Min(ShelfDepth, DeanA * Mathf.Pow(dist, 2.0f / 3.0f));
        float bar = 0.62f * Mathf.Exp(-Sq((dist - 16.0f) / 5.5f))
                    * (0.65f + 0.35f * Mathf.Sin(z * 0.11f + 0.4f));
        return -h + bar;
    }

    private static float Sq(float v) => v * v;

    public static float[] BuildCorners() => Bathymetry.SampleCorners(Grid, Dx, ShoreBase);

    public static float[] BuildBottomFloats(float[] corners) => Bathymetry.BuildBottomFloats(corners, Grid);

    public static ArrayMesh BuildTerrainMesh(float[] corners) =>
        Bathymetry.BuildTerrainMesh(corners, Grid, Dx, Domain);

    /// <summary>
    /// Self-check: the wavemaker injects an analytic wave for a uniform depth0 across the
    /// west relaxation zone, so the bed there must actually BE flat at -ShelfDepth. A bed
    /// that slopes under the relaxation zone is an impedance mismatch — it reflects, and the
    /// reflection is invisible until it has bounced around for a few seconds. Prints the
    /// worst deviation over the zone; anything above a millimetre wants a wider shelf.
    /// </summary>
    public static float ShelfFlatnessError(float[] corners, int relaxCells)
    {
        int c = Corners;
        int xMax = Mathf.Min(relaxCells + 2, c - 1);
        float worst = 0.0f;
        for (int j = 0; j < c; j++)
        {
            int row = j * c;
            for (int i = 0; i <= xMax; i++)
            {
                worst = Mathf.Max(worst, Mathf.Abs(corners[row + i] + ShelfDepth));
            }
        }
        return worst;
    }
}
