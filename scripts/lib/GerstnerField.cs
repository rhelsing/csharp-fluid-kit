using Godot;

namespace GodotCsharpExperiments.Lib;

// CPU Gerstner wave sampler — the same math as the shaders, so physics (buoyancy)
// matches the visual surface. C# port of water-kit's scripts/lib/gerstner_field.gd
// (which extends RefCounted). A plain C# class is fine here — nothing needs it to be
// a Godot Node. Share ONE instance between the water shader (via wave_time) and any
// sampling code so they stay in phase.
public class GerstnerField
{
    public float Steepness = 0.2f;
    public float Wavelength = 8.0f;
    public float Speed = 1.0f;
    public float[] Directions = { 0.0f, 0.3f, 0.55f, 0.8f };
    public float Time = 0.0f;

    // One Gerstner contribution for a single direction (0..1, mapped to an angle).
    protected virtual Vector3 One(Vector3 pos, float direction)
    {
        direction = direction * 2.0f - 1.0f;
        Vector2 d = new Vector2(Mathf.Cos(Mathf.Pi * direction), Mathf.Sin(Mathf.Pi * direction)).Normalized();
        float k = Mathf.Tau / Wavelength;
        float a = Steepness / k;
        float f = k * (d.Dot(new Vector2(pos.X, pos.Z)) - Speed * Time);
        return new Vector3(d.X * a * Mathf.Cos(f), a * Mathf.Sin(f), d.Y * a * Mathf.Cos(f));
    }

    public virtual Vector3 Displacement(Vector3 pos)
    {
        var off = Vector3.Zero;
        foreach (var dir in Directions)
        {
            off += One(pos, dir);
        }
        return off;
    }

    public float Height(float x, float z) => Displacement(new Vector3(x, 0.0f, z)).Y;
}
