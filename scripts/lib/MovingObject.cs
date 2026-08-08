using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Shared path machinery for 24_move and 24_scat, so the ONLY difference between those two
// scenes is what the moving object DOES to the water, not how it moves.
public static class MovingObject
{
    public enum Path { Circle, Sweep, Figure8 }

    /// <summary>Object position in [-1,1] sim space at time t.</summary>
    public static Vector2 At(Path path, float t, float radius)
    {
        return path switch
        {
            Path.Sweep => new Vector2(MathF.Sin(t) * radius, MathF.Sin(t * 0.37f) * radius * 0.35f),
            Path.Figure8 => new Vector2(MathF.Sin(t) * radius, MathF.Sin(t * 2.0f) * radius * 0.5f),
            _ => new Vector2(MathF.Cos(t) * radius, MathF.Sin(t) * radius),
        };
    }
}
