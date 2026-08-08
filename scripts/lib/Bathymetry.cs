using System;
using Godot;

namespace GodotCsharpExperiments.Lib;

// Grid-generic bathymetry plumbing shared by every KP07 scenario. A scenario supplies
// only its bed function (a corner heightfield); everything downstream — the solver's
// bottom texture, the lake-at-rest state, the visual terrain mesh — is the same math
// regardless of grid size, so it lives here once.
//
// Grid convention (identical to ../shorewaves): corner (i,j) at world (i·dx, j·dx),
// row-major corner index j*(grid+1)+i. Cell (i,j) center at ((i+.5)dx, (j+.5)dx) →
// bottom texel (i,j).
//
// Extracted verbatim from BeachScenario so scene 17 is unchanged; ShoreScenario is the
// second caller.
public static class Bathymetry
{
    // GDScript-style smoothstep (clamped Hermite) — reproduced exactly so beds match
    // ../shorewaves cell-for-cell.
    public static float Smoothstep(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>
    /// Sample a bed function onto a (grid+1)² corner heightfield.
    /// </summary>
    public static float[] SampleCorners(int grid, float dx, Func<float, float, float> bed)
    {
        int c = grid + 1;
        var h = new float[c * c];
        for (int j = 0; j < c; j++)
        {
            float z = j * dx;
            int row = j * c;
            for (int i = 0; i < c; i++)
            {
                h[row + i] = bed(i * dx, z);
            }
        }
        return h;
    }

    // Solver bottom texture as RGBAF floats: R = B at cell center, G = B at east face,
    // B = B at north face, A = max of B-center over the 3×3 cell neighborhood.
    public static float[] BuildBottomFloats(float[] corners, int grid)
    {
        int g = grid, c = grid + 1;
        var centers = new float[g * g];
        for (int j = 0; j < g; j++)
        {
            int cRow = j * c, oRow = j * g;
            for (int i = 0; i < g; i++)
            {
                int k = cRow + i;
                centers[oRow + i] = (corners[k] + corners[k + 1] + corners[k + c] + corners[k + c + 1]) * 0.25f;
            }
        }
        // separable 3×3 max (clamped at borders)
        var hmax = new float[g * g];
        for (int j = 0; j < g; j++)
        {
            int row = j * g;
            for (int i = 0; i < g; i++)
            {
                float m = centers[row + i];
                if (i > 0) { m = Mathf.Max(m, centers[row + i - 1]); }
                if (i < g - 1) { m = Mathf.Max(m, centers[row + i + 1]); }
                hmax[row + i] = m;
            }
        }
        var data = new float[g * g * 4];
        for (int j = 0; j < g; j++)
        {
            int cRow = j * c, oRow = j * g;
            for (int i = 0; i < g; i++)
            {
                int k = cRow + i;
                float a = hmax[oRow + i];
                if (j > 0) { a = Mathf.Max(a, hmax[oRow - g + i]); }
                if (j < g - 1) { a = Mathf.Max(a, hmax[oRow + g + i]); }
                int p = (oRow + i) * 4;
                data[p] = centers[oRow + i];
                data[p + 1] = (corners[k + 1] + corners[k + c + 1]) * 0.5f;       // east face
                data[p + 2] = (corners[k + c] + corners[k + c + 1]) * 0.5f;       // north face
                data[p + 3] = a;
            }
        }
        return data;
    }

    // Lake-at-rest initial state from the bottom floats: w = max(B_center, 0), zero
    // momentum/foam. (State texel = bottom R channel, clamped to still-water level.)
    public static byte[] StateBytesFromBottom(float[] bottom)
    {
        var st = new float[bottom.Length];
        for (int o = 0; o < bottom.Length; o += 4)
        {
            st[o] = Mathf.Max(bottom[o], 0.0f);
        }
        return FloatsToBytes(st);
    }

    public static byte[] FloatsToBytes(float[] f)
    {
        var b = new byte[f.Length * sizeof(float)];
        Buffer.BlockCopy(f, 0, b, 0, b.Length);
        return b;
    }

    // Visual terrain: (grid+1)² vertex grid, indexed triangles, analytic smooth normals
    // from the heightfield, UV1 = world_xz / domain (so the sand shader's ground-memory
    // lookup lines up with the sim grid), mikktspace tangents.
    public static ArrayMesh BuildTerrainMesh(float[] corners, int grid, float dx, float domain)
    {
        int c = grid + 1, g = grid;
        var verts = new Vector3[c * c];
        var normals = new Vector3[c * c];
        var uvs = new Vector2[c * c];
        float invDom = 1.0f / domain;
        for (int j = 0; j < c; j++)
        {
            float z = j * dx;
            int row = j * c;
            int jl = Mathf.Max(j - 1, 0), jr = Mathf.Min(j + 1, c - 1);
            float invDz = 1.0f / ((jr - jl) * dx);
            for (int i = 0; i < c; i++)
            {
                float x = i * dx;
                int k = row + i;
                int il = Mathf.Max(i - 1, 0), ir = Mathf.Min(i + 1, c - 1);
                float dbdx = (corners[row + ir] - corners[row + il]) / ((ir - il) * dx);
                float dbdz = (corners[jr * c + i] - corners[jl * c + i]) * invDz;
                verts[k] = new Vector3(x, corners[k], z);
                normals[k] = new Vector3(-dbdx, 1.0f, -dbdz).Normalized();
                uvs[k] = new Vector2(x * invDom, z * invDom);
            }
        }
        var indices = new int[g * g * 6];
        int p = 0;
        for (int j = 0; j < g; j++)
        {
            int a = j * c;
            for (int i = 0; i < g; i++)
            {
                int v00 = a + i, v10 = v00 + 1, v01 = v00 + c, v11 = v01 + 1;
                indices[p] = v00; indices[p + 1] = v10; indices[p + 2] = v01;
                indices[p + 3] = v10; indices[p + 4] = v11; indices[p + 5] = v01;
                p += 6;
            }
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var tmp = new ArrayMesh();
        tmp.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        var st = new SurfaceTool();
        st.CreateFrom(tmp, 0);
        st.GenerateTangents();
        return st.Commit();
    }
}
