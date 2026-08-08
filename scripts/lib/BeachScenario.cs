using Godot;

namespace GodotCsharpExperiments.Lib;

// CPU bathymetry + visual terrain for the Shorewaves beach — a C# port of
// ../shorewaves/scripts/scenario.gd (BEACH path). Boulder stamps/props are omitted so
// the scene stays asset-free; what remains is the clean sloped shore. Pure static,
// deterministic math.
//
// Grid: domain 30.4×30.4 m, 608² cells, dx = 0.05. Only the BED is defined here; the
// bottom texture / rest state / terrain mesh are grid-generic and live in Bathymetry.
public static class BeachScenario
{
    public const int Grid = 608;
    public const int Corners = 609;
    public const float Dx = 0.05f;
    public const float Domain = 30.4f;

    // Beach bed elevation (no stamps): a shelf at -1.2 m out to x=6, then a 1:12 ramp up
    // out of the water, with a faint along-shore ripple that fades in over the beach face.
    public static float BeachBase(float x, float z)
    {
        float b = x <= 6.0f ? -1.2f : -1.2f + (x - 6.0f) / 12.0f;
        return b + 0.05f * Mathf.Sin(z * 0.35f + 1.7f) * Bathymetry.Smoothstep(8.0f, 22.0f, x);
    }

    public static float[] BuildCorners() => Bathymetry.SampleCorners(Grid, Dx, BeachBase);

    public static float[] BuildBottomFloats(float[] corners) => Bathymetry.BuildBottomFloats(corners, Grid);

    public static byte[] StateBytesFromBottom(float[] bottom) => Bathymetry.StateBytesFromBottom(bottom);

    public static byte[] FloatsToBytes(float[] f) => Bathymetry.FloatsToBytes(f);

    public static ArrayMesh BuildTerrainMesh(float[] corners) =>
        Bathymetry.BuildTerrainMesh(corners, Grid, Dx, Domain);
}
