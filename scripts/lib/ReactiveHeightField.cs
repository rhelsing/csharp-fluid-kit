using Godot;

namespace GodotCsharpExperiments.Lib;

// The boat's height source = the Gerstner swell PLUS the reactive MNA wake, so it bobs on
// the combined surface (its own wake, other wakes). Drops into BoatRider.Field because it
// IS-A GerstnerField; Displacement() adds the sampled wake height on top of the swell.
// Keep its Gerstner params in sync with the visual shader (same field feeds both).
public sealed class ReactiveHeightField : GerstnerField
{
    public ReactiveWaveField Ripples;    // swappable: the scene rebuilds the field on grid change
    public float WakeInfluence = 1.0f;   // how strongly the hull feels the reactive surface

    public ReactiveHeightField(ReactiveWaveField ripples) { Ripples = ripples; }

    public override Vector3 Displacement(Vector3 pos)
    {
        Vector3 d = base.Displacement(pos);
        d.Y += WakeInfluence * Ripples.SampleHeightWorld(new Vector2(pos.X, pos.Z));
        return d;
    }
}
