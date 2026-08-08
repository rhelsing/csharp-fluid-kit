using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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

    // Parallel over Z-SLABS. The cube loop is embarrassingly parallel — each cube reads the
    // field (never writes it) and emits triangles independently, so the only shared state was
    // the output lists and the cv/cp scratch. Giving every slab its own list + scratch removes
    // all of it; no locks, no atomics.
    //
    // Slab boundaries are safe: a cube at z owns corners z and z+1, so slabs overlap by one
    // PLANE of reads and zero cubes. Triangle order does not matter (normals come from the
    // field gradient, not winding — see the header), so the merge is a plain concatenation.
    public static (Vector3[] verts, Vector3[] normals) Build(float[] f, Vector3I dim, float iso)
    {
        int W = dim.X, H = dim.Y, D = dim.Z;
        // System.Environment, not Godot.Environment — both are in scope here.
        int slabs = Math.Clamp(System.Environment.ProcessorCount, 1, Math.Max(1, D - 1));
        var partV = new List<Vector3>[slabs];
        var partN = new List<Vector3>[slabs];

        Parallel.For(0, slabs, s =>
        {
            var verts = new List<Vector3>(4096);
            var norms = new List<Vector3>(4096);
            var cv = new float[8];      // per-thread: these were shared scratch
            var cp = new Vector3[8];

            int zBegin = (D - 1) * s / slabs;
            int zEnd = (D - 1) * (s + 1) / slabs;

            for (int z = zBegin; z < zEnd; z++)
            for (int y = 0; y < H - 1; y++)
            for (int x = 0; x < W - 1; x++)
            {
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                float v = f[(x + CornerOff[i, 0]) + W * ((y + CornerOff[i, 1]) + H * (z + CornerOff[i, 2]))];
                cv[i] = v;
                if (v < lo) { lo = v; }
                if (v > hi) { hi = v; }
            }
            if (lo >= iso || hi < iso) { continue; }   // whole cube on one side → skip

            // Corner POSITIONS are only needed once the cube is known to straddle. Filling
            // them in the loop above cost 8 Vector3 stores for all ~104k cubes when only a
            // few percent survive the test — pure waste on the overwhelming majority.
            for (int i = 0; i < 8; i++)
            {
                cp[i] = new Vector3(x + CornerOff[i, 0], y + CornerOff[i, 1], z + CornerOff[i, 2]);
            }

            for (int t = 0; t < 6; t++)
            {
                MarchTet(cv, cp, Tets[t, 0], Tets[t, 1], Tets[t, 2], Tets[t, 3], iso, f, dim, verts, norms);
            }
            }

            partV[s] = verts;
            partN[s] = norms;
        });

        int total = 0;
        for (int s = 0; s < slabs; s++) { total += partV[s].Count; }
        var outV = new Vector3[total];
        var outN = new Vector3[total];
        int at = 0;
        for (int s = 0; s < slabs; s++)
        {
            partV[s].CopyTo(outV, at);
            partN[s].CopyTo(outN, at);
            at += partV[s].Count;
        }
        return (outV, outN);
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

    // Central-difference gradient on the GRID, by direct integer index.
    //
    // This used to trilinearly Sample() the field at p +/- 1 on each axis: 6 samples x 8 reads
    // = 48 scattered reads per normal, x3 normals per triangle = 144 reads per triangle. At
    // ~22k triangles that is 3.2M reads/frame, and it measured as 54 ms of a 58 ms frame
    // (readout: "solve 0.02ms . readback 1.92ms . isosurface 54.47ms . mesh 1.75ms"). The
    // marching itself was never the cost — the cube-skip early-out keeps it to surface cells.
    //
    // Direct indexing gives 6 reads instead of 48, with no per-sample clamp or lerp chain.
    // What it gives up: the gradient is now evaluated at the nearest grid point rather than
    // exactly at the vertex, so shading is very slightly flatter on a coarse field. On a
    // smooth 48^3 blob that is not visible; if it ever is, the fix is a finer grid, not a
    // costlier normal.
    private static Vector3 NormalAt(float[] f, Vector3I dim, Vector3 p)
    {
        int W = dim.X, H = dim.Y, D = dim.Z;
        int x = Math.Clamp((int)MathF.Round(p.X), 1, W - 2);
        int y = Math.Clamp((int)MathF.Round(p.Y), 1, H - 2);
        int z = Math.Clamp((int)MathF.Round(p.Z), 1, D - 2);

        int i = x + W * (y + H * z);
        int sy = W, sz = W * H;
        float dx = f[i + 1] - f[i - 1];
        float dy = f[i + sy] - f[i - sy];
        float dz = f[i + sz] - f[i - sz];

        var g = new Vector3(dx, dy, dz);
        // field is high inside, low outside → the outward normal is -gradient
        return g.LengthSquared() < 1e-10f ? Vector3.Up : (-g).Normalized();
    }
}
