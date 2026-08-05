using System.Collections.Generic;
using Godot;

namespace GodotCsharpExperiments.Lib;

// A MULTI-BAND Gerstner field: a flat list of N waves, each with its OWN wavelength,
// steepness, speed and direction — long swell + mid waves + fine chop. Sums to a much
// richer surface than the base GerstnerField (which shares one wavelength across 4
// directions). C# port of water-kit's scripts/lib/composite_gerstner.gd. The scene
// passes the SAME parallel arrays to ameye_water_composite.gdshader (+ the shared
// Time as wave_time), so the visible crests match what the field samples.
public class CompositeGerstner : GerstnerField
{
    // one entry per wave (parallel lists, mirrored to the shader uniforms)
    public readonly List<float> Wavelengths = new();
    public readonly List<float> Steepnesses = new();
    public readonly List<float> Speeds = new();
    public readonly List<float> Dirs = new();   // 0..1, mapped to an angle like the base field

    public void AddWave(float wavelength, float steepness, float speed, float direction)
    {
        Wavelengths.Add(wavelength);
        Steepnesses.Add(steepness);
        Speeds.Add(speed);
        Dirs.Add(direction);
    }

    public void ClearWaves()
    {
        Wavelengths.Clear();
        Steepnesses.Clear();
        Speeds.Clear();
        Dirs.Clear();
    }

    private Vector3 Wave(Vector3 pos, int i)
    {
        float direction = Dirs[i] * 2.0f - 1.0f;
        Vector2 d = new Vector2(Mathf.Cos(Mathf.Pi * direction), Mathf.Sin(Mathf.Pi * direction)).Normalized();
        float k = Mathf.Tau / Wavelengths[i];
        float a = Steepnesses[i] / k;
        float f = k * (d.Dot(new Vector2(pos.X, pos.Z)) - Speeds[i] * Time);
        return new Vector3(d.X * a * Mathf.Cos(f), a * Mathf.Sin(f), d.Y * a * Mathf.Cos(f));
    }

    public override Vector3 Displacement(Vector3 pos)
    {
        var off = Vector3.Zero;
        for (int i = 0; i < Wavelengths.Count; i++)
        {
            off += Wave(pos, i);
        }
        return off;
    }

    // Convenience getters for pushing the parallel arrays to the shader uniforms.
    public float[] WavelengthsArr => Wavelengths.ToArray();
    public float[] SteepnessesArr => Steepnesses.ToArray();
    public float[] SpeedsArr => Speeds.ToArray();
    public float[] DirsArr => Dirs.ToArray();
}
