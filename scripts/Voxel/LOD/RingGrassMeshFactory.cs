using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Creates the small normalized prototype shared by standing-grass MultiMeshes.
/// Its roots are on y=0 and its height is one unit, so instance transforms own
/// terrain placement and blade height without changing the wind coordinate
/// convention.
/// </summary>
public static class RingGrassMeshFactory
{
    /// <summary>Creates one tapered, normalized meadow-blade ribbon.</summary>
    public static Mesh Create(int segments = 4)
    {
        segments = Mathf.Clamp(segments, 2, 12);
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);

        // Density comes from the MultiMesh, so each instance is one narrow
        // blade. Local X remains available for material curvature.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .035f, segments, .50f);

        return surface.Commit();
    }

    /// <summary>
    /// Creates the inexpensive intermediate tuft used after the near grass
    /// handoff. Two crossed one-section ribbons share one normalized anchor.
    /// This deliberately stays at four triangles (the old two-section ribbon's
    /// cost), but reads as several blades from every ordinary viewing angle.
    /// </summary>
    public static Mesh CreateIntermediate()
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        // Instance X scale already carries the authored blade width. Keep each
        // crossed card broad enough to replace several nearby single ribbons;
        // tiny prototype widths here would erase the density saving visually.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .055f, 1, .40f);
        AddBlade(surface, Vector3.Forward, -Vector3.Right, -.040f, 1, .34f);
        return surface.Commit();
    }

    /// <summary>Four offset near sources grouped under one instance (32 triangles).</summary>
    public static Mesh CreateClump()
    {
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .035f, 4, .50f, root);
        }
        return surface.Commit();
    }

    /// <summary>Four offset copies of the production intermediate source (16 triangles).</summary>
    public static Mesh CreateIntermediateClump()
    {
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .055f, 1, .40f, root);
            AddBlade(surface, outward, -side, -.040f, 1, .34f, root);
        }
        return surface.Commit();
    }

    /// <summary>Four offset copies of the production far source (16 triangles).</summary>
    public static Mesh CreateFarClump()
    {
        var surface = new SurfaceTool(); surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .070f, 1, 1.10f, root);
            AddBlade(surface, outward, -side, -.060f, 1, .95f, root);
        }
        return surface.Commit();
    }

    /// <summary>
    /// A deliberately sparse far-distance cluster. It is kept separate from
    /// <see cref="CreateIntermediate"/> so streamers can opt into it without
    /// changing the established near/intermediate handoff.
    /// </summary>
    public static Mesh CreateFarCluster()
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        // Keep the far prototype at the same four-triangle ceiling as the
        // intermediate tuft. Its wider, more splayed crossed cards trade
        // close silhouette detail for a stable distant coverage read.
        // One far source stands in for roughly sixteen near candidates. Two
        // very broad crossed silhouettes read as a small clump while retaining
        // the four-triangle ceiling and avoiding additional roots/dispatches.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .070f, 1, 1.10f);
        AddBlade(surface, Vector3.Forward, -Vector3.Right, -.060f, 1, .95f);
        return surface.Commit();
    }

    /// <summary>Compatibility prototype for callers that explicitly need cards.</summary>
    public static Mesh CreateLegacyClump(int segments = 4)
    {
        segments = Mathf.Clamp(segments, 2, 12);
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int blade = 0; blade < 3; blade++)
        {
            float angle = blade * Mathf.Pi / 3.0f;
            Vector3 sideways = new(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
            Vector3 outward = new(-sideways.Z, 0.0f, sideways.X);
            AddBlade(surface, sideways, outward, blade == 0 ? .075f : -.055f, segments, .50f);
        }
        return surface.Commit();
    }

    private static void AddBlade(SurfaceTool surface, Vector3 sideways, Vector3 outward, float lean, int segments,
        float rootHalfWidth = .065f, Vector3 rootOffset = default)
    {
        const float tipHalfWidth = .018f;
        for (int segment = 0; segment < segments; segment++)
        {
            float t0 = segment / (float)segments;
            float t1 = (segment + 1) / (float)segments;
            BladeSection(t0, sideways, outward, lean, out Vector3 left0, out Vector3 right0, out Vector3 leftNormal0, out Vector3 rightNormal0);
            BladeSection(t1, sideways, outward, lean, out Vector3 left1, out Vector3 right1, out Vector3 leftNormal1, out Vector3 rightNormal1);

            // Godot considers clockwise triangles front-facing. Emit both
            // triangles with supplied, softly upward-facing blade normals.
            AddVertex(surface, left0, leftNormal0, new Vector2(0, t0));
            AddVertex(surface, left1, leftNormal1, new Vector2(0, t1));
            AddVertex(surface, right1, rightNormal1, new Vector2(1, t1));
            AddVertex(surface, left0, leftNormal0, new Vector2(0, t0));
            AddVertex(surface, right1, rightNormal1, new Vector2(1, t1));
            AddVertex(surface, right0, rightNormal0, new Vector2(1, t0));
        }

        void BladeSection(float t, Vector3 side, Vector3 outwards, float bladeLean,
            out Vector3 left, out Vector3 right, out Vector3 leftNormal, out Vector3 rightNormal)
        {
            float width = Mathf.Lerp(rootHalfWidth, tipHalfWidth, t * t);
            // Keep only a subtle rest arc. The material supplies the visible
            // per-instance curve and all root vertices remain exact at y=0.
            Vector3 center = rootOffset + Vector3.Up * t + outwards * (bladeLean * t * t)
                + side * (.026f * Mathf.Sin(t * Mathf.Pi));
            left = center - side * width;
            right = center + side * width;
            Vector3 tangent = (Vector3.Up + outwards * (2.0f * bladeLean * t)
                + side * (.026f * Mathf.Pi * Mathf.Cos(t * Mathf.Pi))).Normalized();
            Vector3 faceNormal = side.Cross(tangent).Normalized();
            // A small roll towards the two strip edges gives the cards a
            // rounded response under directional lighting without extra faces.
            leftNormal = (faceNormal - side * .34f).Normalized();
            rightNormal = (faceNormal + side * .34f).Normalized();
        }
    }

    private static void SourceFrame(int source, out Vector3 root, out Vector3 side, out Vector3 outward)
    {
        float angle = Mathf.Tau * source / 4.0f + Mathf.Pi * .25f;
        root = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * 2.5f;
        side = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));
        outward = new Vector3(-side.Z, 0, side.X);
    }

    private static void AddVertex(SurfaceTool surface, Vector3 position, Vector3 normal, Vector2 uv)
    {
        surface.SetNormal(normal);
        surface.SetUV(uv);
        surface.AddVertex(position);
    }
}
