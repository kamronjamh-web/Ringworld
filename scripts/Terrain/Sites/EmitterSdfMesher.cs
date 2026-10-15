using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Small self-contained surface-nets mesher for an arbitrary signed-distance delegate over a bounded uniform grid
/// (negative = air). Offline bake helper for the emitter underground shell; deterministic, single threaded, double precision.
/// Triangles use Godot's front-face winding and face toward the negative side (into the air). Vertices are the mean of the
/// cell's edge crossings, projected onto the surface by one clamped Newton step.
/// </summary>
internal static class EmitterSdfMesher
{
    /// <summary>Meshes <paramref name="sd"/> over the grid with lattice origin (ox, oy, oz), <paramref name="cell"/> metres and nx*ny*nz cells.</summary>
    public static void Mesh(Func<double, double, double, double> sd, double ox, double oy, double oz, double cell, int nx, int ny, int nz,
        List<Vector3> vertices, List<int> indices)
    {
        int sx = nx + 1, sy = ny + 1, sz = nz + 1;
        double[] values = new double[sx * sy * sz];
        for (int k = 0; k < sz; k++)
            for (int j = 0; j < sy; j++)
                for (int i = 0; i < sx; i++)
                    values[(k * sy + j) * sx + i] = sd(ox + i * cell, oy + j * cell, oz + k * cell);

        int[] cellVertex = new int[nx * ny * nz];
        Array.Fill(cellVertex, -1);
        // Cube corner offsets and the 12 edges (corner pairs).
        int[,] corner = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
        int[,] edge = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        double[] cv = new double[8];
        for (int k = 0; k < nz; k++)
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int negatives = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        cv[c] = values[((k + corner[c, 2]) * sy + j + corner[c, 1]) * sx + i + corner[c, 0]];
                        if (cv[c] < 0d) negatives++;
                    }
                    if (negatives == 0 || negatives == 8) continue;
                    double px = 0d, py = 0d, pz = 0d;
                    int crossings = 0;
                    for (int e = 0; e < 12; e++)
                    {
                        int a = edge[e, 0], b = edge[e, 1];
                        if ((cv[a] < 0d) == (cv[b] < 0d)) continue;
                        double t = cv[a] / (cv[a] - cv[b]);
                        px += corner[a, 0] + t * (corner[b, 0] - corner[a, 0]);
                        py += corner[a, 1] + t * (corner[b, 1] - corner[a, 1]);
                        pz += corner[a, 2] + t * (corner[b, 2] - corner[a, 2]);
                        crossings++;
                    }
                    double x = ox + (i + px / crossings) * cell, y = oy + (j + py / crossings) * cell, z = oz + (k + pz / crossings) * cell;
                    Project(sd, ref x, ref y, ref z, ox + i * cell, oy + j * cell, oz + k * cell, cell);
                    cellVertex[(k * ny + j) * nx + i] = vertices.Count;
                    vertices.Add(new Vector3((float)x, (float)y, (float)z));
                }

        // One quad per sign-changing lattice edge; the four cells around the edge supply its corners.
        Span<int> q = stackalloc int[4];
        for (int k = 0; k < sz; k++)
            for (int j = 0; j < sy; j++)
                for (int i = 0; i < sx; i++)
                {
                    bool air = values[(k * sy + j) * sx + i] < 0d;
                    // X edge: cells (i, j-1|j, k-1|k) in cyclic (y, z) order (geometric normal +x).
                    if (i < nx && j >= 1 && j < ny && k >= 1 && k < nz && air != (values[(k * sy + j) * sx + i + 1] < 0d))
                    {
                        q[0] = cellVertex[((k - 1) * ny + j - 1) * nx + i]; q[1] = cellVertex[((k - 1) * ny + j) * nx + i];
                        q[2] = cellVertex[(k * ny + j) * nx + i]; q[3] = cellVertex[(k * ny + j - 1) * nx + i];
                        EmitQuad(vertices, indices, q, air);
                    }
                    // Y edge: cells (i-1|i, j, k-1|k) in cyclic (z, x) order (geometric normal +y).
                    if (j < ny && i >= 1 && i < nx && k >= 1 && k < nz && air != (values[(k * sy + j + 1) * sx + i] < 0d))
                    {
                        q[0] = cellVertex[((k - 1) * ny + j) * nx + i - 1]; q[1] = cellVertex[(k * ny + j) * nx + i - 1];
                        q[2] = cellVertex[(k * ny + j) * nx + i]; q[3] = cellVertex[((k - 1) * ny + j) * nx + i];
                        EmitQuad(vertices, indices, q, air);
                    }
                    // Z edge: cells (i-1|i, j-1|j, k) in cyclic (x, y) order (geometric normal +z).
                    if (k < nz && i >= 1 && i < nx && j >= 1 && j < ny && air != (values[((k + 1) * sy + j) * sx + i] < 0d))
                    {
                        q[0] = cellVertex[(k * ny + j - 1) * nx + i - 1]; q[1] = cellVertex[(k * ny + j - 1) * nx + i];
                        q[2] = cellVertex[(k * ny + j) * nx + i]; q[3] = cellVertex[(k * ny + j) * nx + i - 1];
                        EmitQuad(vertices, indices, q, air);
                    }
                }
    }

    private static void Project(Func<double, double, double, double> sd, ref double x, ref double y, ref double z, double cx, double cy, double cz, double cell)
    {
        double h = cell * 0.1d;
        double d = sd(x, y, z);
        double gx = (sd(x + h, y, z) - sd(x - h, y, z)) / (2d * h);
        double gy = (sd(x, y + h, z) - sd(x, y - h, z)) / (2d * h);
        double gz = (sd(x, y, z + h) - sd(x, y, z - h)) / (2d * h);
        double g2 = gx * gx + gy * gy + gz * gz;
        if (g2 < 1e-12d) return;
        double s = d / g2;
        x = Math.Clamp(x - s * gx, cx, cx + cell);
        y = Math.Clamp(y - s * gy, cy, cy + cell);
        z = Math.Clamp(z - s * gz, cz, cz + cell);
    }

    /// <summary>Quad in cyclic order with geometric normal along the edge axis; <paramref name="lowerIsAir"/> keeps the order (air on the low side).</summary>
    private static void EmitQuad(List<Vector3> v, List<int> idx, Span<int> q, bool lowerIsAir)
    {
        int a = q[0], b = q[1], c = q[2], d = q[3];
        if (!lowerIsAir) { b = q[3]; d = q[1]; }
        // Split along the shorter diagonal (ties: a-c).
        if ((v[b] - v[d]).LengthSquared() < (v[a] - v[c]).LengthSquared())
        {
            idx.Add(a); idx.Add(b); idx.Add(d);
            idx.Add(b); idx.Add(c); idx.Add(d);
        }
        else
        {
            idx.Add(a); idx.Add(b); idx.Add(c);
            idx.Add(a); idx.Add(c); idx.Add(d);
        }
    }
}
