using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// ColumnField — shared column machinery for 24_col and 24_cols.
//
// Two jobs that have to agree or the scene lies: rasterize the columns into the solver's
// obstacle mask, and build matching visible geometry, so what the water reflects off is what
// you can see. Both are driven from the same list of Column records.
//
// Positions are in the pool's [-1,1] sim space, which is also its world space (the pool is
// 2x2 centred on the origin), so a column's world transform is its sim position directly.
public static class ColumnField
{
    public enum Shape { Cylinder, Square, Blade }

    public readonly record struct Column(Vector2 Pos, float Radius, Shape Kind, float Rotation);

    /// <summary>
    /// Rasterize columns into an n*n mask, 1 = solid. Edges are anti-aliased over ~1 cell so a
    /// column reads as a smooth obstacle rather than a staircase of grid-scale scatterers.
    /// </summary>
    public static float[] Rasterize(int n, System.Collections.Generic.IReadOnlyList<Column> cols)
    {
        var mask = new float[n * n];
        if (cols.Count == 0) { return mask; }
        float cell = 2.0f / n;   // sim units per cell

        foreach (var col in cols)
        {
            // Bound the work to the column's own footprint instead of sweeping the whole grid.
            float reach = col.Radius * 2.0f + cell * 2.0f;
            int x0 = Math.Max(0, (int)((col.Pos.X - reach + 1.0f) / cell));
            int x1 = Math.Min(n - 1, (int)((col.Pos.X + reach + 1.0f) / cell));
            int y0 = Math.Max(0, (int)((col.Pos.Y - reach + 1.0f) / cell));
            int y1 = Math.Min(n - 1, (int)((col.Pos.Y + reach + 1.0f) / cell));

            float ca = MathF.Cos(-col.Rotation), sa = MathF.Sin(-col.Rotation);
            for (int y = y0; y <= y1; ++y)
            {
                float wz = (y + 0.5f) * cell - 1.0f;
                for (int x = x0; x <= x1; ++x)
                {
                    float wx = (x + 0.5f) * cell - 1.0f;
                    float dx = wx - col.Pos.X, dz = wz - col.Pos.Y;
                    float rx = dx * ca - dz * sa, rz = dx * sa + dz * ca;

                    // Signed distance to the shape's edge; negative = inside.
                    float sd = col.Kind switch
                    {
                        Shape.Square => MathF.Max(MathF.Abs(rx), MathF.Abs(rz)) - col.Radius,
                        Shape.Blade => MathF.Max(MathF.Abs(rx) - col.Radius * 0.22f, MathF.Abs(rz) - col.Radius * 1.8f),
                        _ => MathF.Sqrt(rx * rx + rz * rz) - col.Radius,
                    };
                    float v = Mathf.Clamp(0.5f - sd / cell, 0.0f, 1.0f);
                    int i = y * n + x;
                    if (v > mask[i]) { mask[i] = v; }
                }
            }
        }
        return mask;
    }

    /// <summary>Visible geometry matching what Rasterize() put in the mask.</summary>
    public static Mesh MakeMesh(Column col, float height)
    {
        return col.Kind switch
        {
            Shape.Square => new BoxMesh { Size = new Vector3(col.Radius * 2.0f, height, col.Radius * 2.0f) },
            Shape.Blade => new BoxMesh { Size = new Vector3(col.Radius * 0.44f, height, col.Radius * 3.6f) },
            _ => new CylinderMesh { TopRadius = col.Radius, BottomRadius = col.Radius, Height = height, RadialSegments = 24 },
        };
    }

    /// <summary>
    /// Colour cue for the hard/soft slider — solid pale stone at hardness 1, translucent dark at
    /// 0, so the control's position is legible in the frame without reading the panel.
    /// </summary>
    public static StandardMaterial3D MakeMaterial(float hardness)
    {
        float k = Mathf.Clamp(hardness, 0.0f, 1.0f);
        return new StandardMaterial3D
        {
            AlbedoColor = new Color(0.20f + 0.62f * k, 0.24f + 0.58f * k, 0.30f + 0.52f * k, 0.35f + 0.65f * k),
            Transparency = k > 0.98f ? BaseMaterial3D.TransparencyEnum.Disabled : BaseMaterial3D.TransparencyEnum.Alpha,
            Roughness = 0.55f,
        };
    }
}
