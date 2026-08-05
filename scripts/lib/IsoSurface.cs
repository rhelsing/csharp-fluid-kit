using System;
using System.Collections.Generic;
using Godot;

namespace GodotCsharpExperiments.Lib;

// CPU isosurface polygonizer (marching TETRAHEDRA) — turns a 3D scalar field into a
// rasterizable triangle mesh, no ray tracing. Tets over the 256-case marching-cubes
// table on purpose: the per-tet logic is tiny and unambiguous (watertight), which is
// exactly the kind of big CPU-side job C# is here for. Each cube → 6 tets sharing the
// 0–6 diagonal; per-vertex normals come from the field gradient, so triangle winding is
// irrelevant (pair with a double-sided material). Field is x-fastest: idx = x + W*(y + H*z).
public static class IsoSurface
{
    private static readonly int[,] CornerOff =
    {
        {0,0,0}, {1,0,0}, {1,1,0}, {0,1,0}, {0,0,1}, {1,0,1}, {1,1,1}, {0,1,1},
    };

    // 6 tetrahedra (cube-corner indices) sharing the 0–6 main diagonal.
    private static readonly int[,] Tets =
    {
        {0,1,2,6}, {0,2,3,6}, {0,3,7,6}, {0,7,4,6}, {0,4,5,6}, {0,5,1,6},
    };

    public static (Vector3[] verts, Vector3[] normals) Build(float[] f, Vector3I dim, float iso)
    {
        int W = dim.X, H = dim.Y, D = dim.Z;
        var verts = new List<Vector3>(4096);
        var norms = new List<Vector3>(4096);

        var cv = new float[8];
        var cp = new Vector3[8];

        for (int z = 0; z < D - 1; z++)
        for (int y = 0; y < H - 1; y++)
        for (int x = 0; x < W - 1; x++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                int cx = x + CornerOff[i, 0], cyy = y + CornerOff[i, 1], cz = z + CornerOff[i, 2];
                float v = f[cx + W * (cyy + H * cz)];
                cv[i] = v;
                cp[i] = new Vector3(cx, cyy, cz);
                if (v < lo) { lo = v; }
                if (v > hi) { hi = v; }
            }
            if (lo >= iso || hi < iso) { continue; }   // whole cube on one side → skip

            for (int t = 0; t < 6; t++)
            {
                MarchTet(cv, cp, Tets[t, 0], Tets[t, 1], Tets[t, 2], Tets[t, 3], iso, f, dim, verts, norms);
            }
        }
        return (verts.ToArray(), norms.ToArray());
    }

    private static void MarchTet(float[] cv, Vector3[] cp, int a, int b, int c, int d,
        float iso, float[] f, Vector3I dim, List<Vector3> verts, List<Vector3> norms)
    {
        // "inside" = value >= iso. Collect inside/outside local indices (into a,b,c,d).
        Span<int> ti = stackalloc int[] { a, b, c, d };
        Span<int> inside = stackalloc int[4];
        Span<int> outside = stackalloc int[4];
        int ni = 0, no = 0;
        for (int i = 0; i < 4; i++)
        {
            if (cv[ti[i]] >= iso) { inside[ni++] = ti[i]; }
            else { outside[no++] = ti[i]; }
        }
        if (ni == 0 || ni == 4) { return; }

        if (ni == 1)
        {
            Emit(cv, cp, iso, f, dim, verts, norms,
                inside[0], outside[0], inside[0], outside[1], inside[0], outside[2]);
        }
        else if (ni == 3)
        {
            Emit(cv, cp, iso, f, dim, verts, norms,
                outside[0], inside[0], outside[0], inside[1], outside[0], inside[2]);
        }
        else // ni == 2 → quad (two triangles)
        {
            // walk the four crossing edges in order to avoid a bowtie
            Vector3 pa = Interp(cv, cp, inside[0], outside[0], iso);
            Vector3 pb = Interp(cv, cp, inside[1], outside[0], iso);
            Vector3 pc = Interp(cv, cp, inside[1], outside[1], iso);
            Vector3 pd = Interp(cv, cp, inside[0], outside[1], iso);
            Tri(f, dim, verts, norms, pa, pb, pc);
            Tri(f, dim, verts, norms, pa, pc, pd);
        }
    }

    private static void Emit(float[] cv, Vector3[] cp, float iso, float[] f, Vector3I dim,
        List<Vector3> verts, List<Vector3> norms, int a0, int a1, int b0, int b1, int c0, int c1)
    {
        Tri(f, dim, verts, norms,
            Interp(cv, cp, a0, a1, iso), Interp(cv, cp, b0, b1, iso), Interp(cv, cp, c0, c1, iso));
    }

    private static Vector3 Interp(float[] cv, Vector3[] cp, int i, int j, float iso)
    {
        float da = cv[i], db = cv[j];
        float denom = db - da;
        float t = Mathf.Abs(denom) < 1e-8f ? 0.5f : (iso - da) / denom;
        t = Mathf.Clamp(t, 0.0f, 1.0f);
        return cp[i].Lerp(cp[j], t);
    }

    private static void Tri(float[] f, Vector3I dim, List<Vector3> verts, List<Vector3> norms,
        Vector3 p0, Vector3 p1, Vector3 p2)
    {
        verts.Add(p0); verts.Add(p1); verts.Add(p2);
        norms.Add(NormalAt(f, dim, p0));
        norms.Add(NormalAt(f, dim, p1));
        norms.Add(NormalAt(f, dim, p2));
    }

    private static float Sample(float[] f, Vector3I dim, float x, float y, float z)
    {
        int W = dim.X, H = dim.Y, D = dim.Z;
        x = Mathf.Clamp(x, 0, W - 1.001f);
        y = Mathf.Clamp(y, 0, H - 1.001f);
        z = Mathf.Clamp(z, 0, D - 1.001f);
        int x0 = (int)x, y0 = (int)y, z0 = (int)z;
        int x1 = x0 + 1, y1 = y0 + 1, z1 = z0 + 1;
        float fx = x - x0, fy = y - y0, fz = z - z0;
        float c000 = f[x0 + W * (y0 + H * z0)], c100 = f[x1 + W * (y0 + H * z0)];
        float c010 = f[x0 + W * (y1 + H * z0)], c110 = f[x1 + W * (y1 + H * z0)];
        float c001 = f[x0 + W * (y0 + H * z1)], c101 = f[x1 + W * (y0 + H * z1)];
        float c011 = f[x0 + W * (y1 + H * z1)], c111 = f[x1 + W * (y1 + H * z1)];
        float c00 = Mathf.Lerp(c000, c100, fx), c10 = Mathf.Lerp(c010, c110, fx);
        float c01 = Mathf.Lerp(c001, c101, fx), c11 = Mathf.Lerp(c011, c111, fx);
        return Mathf.Lerp(Mathf.Lerp(c00, c10, fy), Mathf.Lerp(c01, c11, fy), fz);
    }

    private static Vector3 NormalAt(float[] f, Vector3I dim, Vector3 p)
    {
        const float e = 1.0f;
        float dx = Sample(f, dim, p.X + e, p.Y, p.Z) - Sample(f, dim, p.X - e, p.Y, p.Z);
        float dy = Sample(f, dim, p.X, p.Y + e, p.Z) - Sample(f, dim, p.X, p.Y - e, p.Z);
        float dz = Sample(f, dim, p.X, p.Y, p.Z + e) - Sample(f, dim, p.X, p.Y, p.Z - e);
        var g = new Vector3(dx, dy, dz);
        // field is high inside, low outside → the outward normal is -gradient
        return g.LengthSquared() < 1e-10f ? Vector3.Up : (-g).Normalized();
    }
}
