using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M2-2: baked cavern shell. Loads assets/poi/underground/CavernShell_*.res (run tools/poi/build_emitter_underground.gd first) and checks
/// vertex SDF distance, shaft radius, normals into the air, winding, closure (open edges only on the -200 m seam ring), CPU ray
/// containment against the collision shape, seam radius, triangle budgets and bake determinism. Prints EMITTER_UNDERGROUND_BAKE=PASS.
/// </summary>
public partial class EmitterUndergroundBakeTest : Node
{
    bool _started;
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main }) TestKind(kind);
            Complete(true, string.Empty);
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    public bool IsFinished() => Finished;
    public bool IsPassed() => Passed;
    public override void _ExitTree() { if (!Finished) Complete(false, "runner removed before completion"); }

    static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("EMITTER_UNDERGROUND_BAKE check failed: " + label);
    }

    static void TestKind(SiteKind kind)
    {
        EmitterSiteUndergroundShape shape = EmitterSiteUnderground.Shape(kind);
        float cell = EmitterUndergroundShell.CellSizes(kind)[0];
        double roofY = shape.CavernRoofApexYM, seamY = shape.PlugDepthM, liner = EmitterSiteApertures.LinerRadius(kind);

        ArrayMesh mesh = ResourceLoader.Load<ArrayMesh>(EmitterUndergroundBaker.MeshPath(kind), null, ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException("missing baked mesh for " + kind);
        ConcavePolygonShape3D collision = ResourceLoader.Load<ConcavePolygonShape3D>(EmitterUndergroundBaker.CollisionPath(kind), null, ResourceLoader.CacheMode.Ignore)
            ?? throw new InvalidOperationException("missing baked collision for " + kind);
        Check(collision.BackfaceCollision, kind + " collision has backface collision");
        Check(mesh.SurfaceGetMaterial(0) != null, kind + " mesh has a material");

        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        int triangles = indices.Length / 3;

        // ---- budgets and LOD ratios ----------------------------------------------------------------------------------
        int budget = kind == SiteKind.Main ? 80000 : 40000;
        Check(triangles <= budget, $"{kind} LOD0 triangles {triangles} <= {budget}");
        int[] lodTriangles = (int[])mesh.GetMeta("lod_triangles");
        Check(lodTriangles[0] == triangles, $"{kind} LOD0 count matches metadata");
        Check(lodTriangles[1] >= triangles * 0.2 && lodTriangles[1] <= triangles * 0.5, $"{kind} LOD1 about 1/3: {lodTriangles[1]}");
        Check(lodTriangles[2] >= triangles * 0.05 && lodTriangles[2] <= triangles * 0.2, $"{kind} LOD2 about 1/10: {lodTriangles[2]}");
        Check(collision.GetFaces().Length / 3 == lodTriangles[EmitterUndergroundBaker.CollisionLevel], $"{kind} collision triangle count");

        // ---- LOD0 vertices: |SD| <= cell, shaft radius, normals into the air ---------------------------------------------
        HashSet<int> used = new(indices);
        int shaftVertices = 0, normalFailures = 0;
        double maxSd = 0d, maxShaftError = 0d;
        foreach (int i in used)
        {
            Vector3 v = vertices[i], n = normals[i];
            double sd = EmitterSiteUnderground.SignedDistance(kind, v.X, v.Y, v.Z);
            maxSd = Math.Max(maxSd, Math.Abs(sd));
            Check(Math.Abs(sd) <= cell, $"{kind} vertex {i} |SD| {sd:0.00} <= cell {cell}");
            double rho = Math.Sqrt((double)v.X * v.X + (double)v.Z * v.Z);
            if (v.Y > roofY + 1.0 && v.Y <= seamY + 0.01)
            {
                shaftVertices++;
                double error = Math.Min(Math.Abs(rho - shape.ShaftR), Math.Abs(rho - liner));
                maxShaftError = Math.Max(maxShaftError, error);
                Check(error <= 0.5, $"{kind} shaft vertex radius {rho:0.00} vs {shape.ShaftR}");
            }
            Check(Math.Abs(n.LengthSquared() - 1f) < 0.01f, $"{kind} normal {i} is unit length");
            double inward = EmitterSiteUnderground.SignedDistance(kind, v.X + 2d * n.X, v.Y + 2d * n.Y, v.Z + 2d * n.Z);
            double outward = EmitterSiteUnderground.SignedDistance(kind, v.X - 2d * n.X, v.Y - 2d * n.Y, v.Z - 2d * n.Z);
            if (!(inward < outward)) normalFailures++;
        }
        GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: LOD0 vertices={used.Count} max|SD|={maxSd:0.00} (cell {cell}) shaftVertices={shaftVertices} maxShaftRadiusError={maxShaftError:0.000} normalFailures={normalFailures}");
        Check(shaftVertices > 0, kind + " has shaft vertices");
        Check(normalFailures <= used.Count * 0.002, $"{kind} normals point into the air ({normalFailures} of {used.Count} fail)");

        // ---- winding: Godot front normal -(b-a)x(c-a) agrees with the vertex normals ------------------------------------
        int windingFailures = 0;
        for (int t = 0; t < triangles; t++)
        {
            Vector3 a = vertices[indices[3 * t]], b = vertices[indices[3 * t + 1]], c = vertices[indices[3 * t + 2]];
            Vector3 front = -(b - a).Cross(c - a);
            Vector3 n = normals[indices[3 * t]] + normals[indices[3 * t + 1]] + normals[indices[3 * t + 2]];
            if (front.Dot(n) <= 0f) windingFailures++;
        }
        GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: windingFailures={windingFailures}/{triangles}");
        Check(windingFailures <= triangles * 0.002, $"{kind} winding faces the air");

        // ---- closure: welded edge usage, open edges only on the seam ring ---------------------------------------------
        Dictionary<(long, long, long), int> weld = new();
        int[] welded = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            if (!used.Contains(i)) continue;
            (long, long, long) key = ((long)Math.Round(vertices[i].X * 1000.0), (long)Math.Round(vertices[i].Y * 1000.0), (long)Math.Round(vertices[i].Z * 1000.0));
            if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld[key] = id; }
            welded[i] = id;
        }
        Dictionary<(int, int), int> edges = new();
        for (int t = 0; t < triangles; t++)
            for (int e = 0; e < 3; e++)
            {
                int a = welded[indices[3 * t + e]], b = welded[indices[3 * t + (e + 1) % 3]];
                if (a == b) continue;
                (int, int) key = a < b ? (a, b) : (b, a);
                edges[key] = edges.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        Dictionary<int, Vector3> weldedPosition = new();
        foreach (int i in used) weldedPosition[welded[i]] = vertices[i];
        int openEdges = 0, nonManifold = 0, openOffSeam = 0;
        double seamRadiusError = 0d;
        foreach (KeyValuePair<(int, int), int> kv in edges)
        {
            if (kv.Value > 2) nonManifold++;
            if (kv.Value != 1) continue;
            openEdges++;
            foreach (int w in new[] { kv.Key.Item1, kv.Key.Item2 })
            {
                Vector3 p = weldedPosition[w];
                double rho = Math.Sqrt((double)p.X * p.X + (double)p.Z * p.Z);
                seamRadiusError = Math.Max(seamRadiusError, Math.Abs(rho - liner));
                if (Math.Abs(p.Y - seamY) > 0.01) openOffSeam++;
            }
        }
        GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: openEdges={openEdges} openEdgeVerticesOffSeam={openOffSeam} nonManifoldEdges={nonManifold} seamRadiusError={seamRadiusError:0.000}");
        Check(openEdges > 0 && openOffSeam == 0, kind + " open edges only on the -200 seam ring");
        Check(seamRadiusError <= 0.5, $"{kind} seam ring matches the liner radius {liner}");
        Check(nonManifold == 0, kind + " has no non-manifold edges");

        // ---- containment: rays from air points hit the collision shape --------------------------------------------------
        Vector3[] faces = collision.GetFaces();
        Random rng = new(4242 + (int)kind);
        Aabb box = shape.LocalBounds;
        int tested = 0, escaped = 0, shaftRays = 0;
        while (tested < 500)
        {
            Vector3 p;
            if (tested % 10 == 0)
            {
                // Shaft points (a thin volume rejection sampling would rarely hit).
                double ang = rng.NextDouble() * Math.PI * 2d, r = Math.Sqrt(rng.NextDouble()) * (shape.ShaftR - 3d);
                p = new Vector3((float)(r * Math.Cos(ang)), (float)(seamY - 5d - rng.NextDouble() * (seamY - roofY - 10d)), (float)(r * Math.Sin(ang)));
            }
            else
            {
                p = new Vector3((float)(box.Position.X + rng.NextDouble() * box.Size.X), (float)(box.Position.Y + rng.NextDouble() * box.Size.Y),
                    (float)(box.Position.Z + rng.NextDouble() * box.Size.Z));
            }
            if (p.Y > seamY - 1f || EmitterSiteUnderground.SignedDistance(kind, p.X, p.Y, p.Z) > -3d) continue;
            bool horizontal = (tested & 1) == 0;
            double az = rng.NextDouble() * Math.PI * 2d;
            double dy = horizontal ? 0d : -(0.15d + rng.NextDouble() * 0.85d);
            double hl = Math.Sqrt(1d - dy * dy);
            Vector3 dir = new((float)(hl * Math.Cos(az)), (float)dy, (float)(hl * Math.Sin(az)));
            if (tested % 10 == 0) shaftRays++;
            if (!RayHitsAny(faces, p, dir)) escaped++;
            tested++;
        }
        GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: rays={tested} (shaft {shaftRays}) escaped={escaped}");
        Check(escaped == 0, $"{kind} rays from air points hit the shell ({escaped} escaped)");

        // ---- determinism ------------------------------------------------------------------------------------------------
        EmitterShellLevel[] first = EmitterUndergroundShell.Build(kind), second = EmitterUndergroundShell.Build(kind);
        string hash = EmitterUndergroundShell.Hash(first);
        Check(hash == EmitterUndergroundShell.Hash(second), kind + " bake is deterministic");
        Check(first[0].TriangleCount == triangles, $"{kind} baked LOD0 equals a fresh build ({first[0].TriangleCount} vs {triangles})");
        GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: triangles={lodTriangles[0]}/{lodTriangles[1]}/{lodTriangles[2]} hash={hash}");
    }

    /// <summary>Two-sided Moller-Trumbore against every face (brute force).</summary>
    static bool RayHitsAny(Vector3[] faces, Vector3 origin, Vector3 dir)
    {
        double ox = origin.X, oy = origin.Y, oz = origin.Z, dx = dir.X, dy = dir.Y, dz = dir.Z;
        for (int f = 0; f + 2 < faces.Length; f += 3)
        {
            Vector3 a = faces[f], b = faces[f + 1], c = faces[f + 2];
            double e1x = b.X - a.X, e1y = b.Y - a.Y, e1z = b.Z - a.Z, e2x = c.X - a.X, e2y = c.Y - a.Y, e2z = c.Z - a.Z;
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-12) continue;
            double inv = 1d / det, tx = ox - a.X, ty = oy - a.Y, tz = oz - a.Z;
            double u = (tx * px + ty * py + tz * pz) * inv;
            if (u < 0d || u > 1d) continue;
            double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            double v = (dx * qx + dy * qy + dz * qz) * inv;
            if (v < 0d || u + v > 1d) continue;
            if ((e2x * qx + e2y * qy + e2z * qz) * inv > 1e-6) return true;
        }
        return false;
    }

    void Complete(bool passed, string error)
    {
        if (Finished) return;
        Finished = true; Passed = passed;
        if (passed) GD.Print("EMITTER_UNDERGROUND_BAKE=PASS");
        else GD.PushError("EMITTER_UNDERGROUND_BAKE=FAIL " + error);
    }
}
