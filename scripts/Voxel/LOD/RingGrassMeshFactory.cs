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
    public static Mesh Create(int segments = 4, bool indexed = true)
    {
        segments = Mathf.Clamp(segments, 2, 12);
        var surface = new SurfaceTool();
        BeginGrassSurface(surface);

        // Density comes from the MultiMesh, so each instance is one narrow
        // blade. Local X remains available for material curvature.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .035f, segments, .50f);

        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>
    /// Creates the inexpensive intermediate tuft used after the near grass
    /// handoff. Two crossed one-section ribbons share one normalized anchor.
    /// This deliberately stays at four triangles (the old two-section ribbon's
    /// cost), but reads as several blades from every ordinary viewing angle.
    /// </summary>
    public static Mesh CreateIntermediate(bool indexed = true)
    {
        var surface = new SurfaceTool();
        BeginGrassSurface(surface);
        // Instance X scale already carries the authored blade width. Keep each
        // crossed card broad enough to replace several nearby single ribbons;
        // tiny prototype widths here would erase the density saving visually.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .055f, 1, .40f);
        AddBlade(surface, Vector3.Forward, -Vector3.Right, -.040f, 1, .34f, sourceFacingAxis: Vector3.Right);
        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>Four offset near sources; four segments cost 32 triangles, two cost 16.</summary>
    public static Mesh CreateClump(int segments = 4, bool indexed = true)
    {
        segments = Mathf.Clamp(segments, 2, 12);
        var surface = new SurfaceTool(); BeginGrassSurface(surface);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .035f, segments, .50f, root);
        }
        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>
    /// Four intermediate sources (16 triangles), optionally one ribbon per source
    /// (8 triangles). The single-ribbon variant requires distant camera facing.
    /// </summary>
    public static Mesh CreateIntermediateClump(bool singleRibbonPerSource = false, bool indexed = true)
    {
        var surface = new SurfaceTool(); BeginGrassSurface(surface);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .055f, 1, .40f, root);
            if (!singleRibbonPerSource)
                AddBlade(surface, outward, -side, -.040f, 1, .34f, root, side);
        }
        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>Four offset copies of the production far source (16 triangles).</summary>
    public static Mesh CreateFarClump(bool indexed = true)
    {
        var surface = new SurfaceTool(); BeginGrassSurface(surface);
        for (int source = 0; source < 4; source++)
        {
            SourceFrame(source, out Vector3 root, out Vector3 side, out Vector3 outward);
            AddBlade(surface, side, outward, .070f, 1, 1.10f, root);
            AddBlade(surface, outward, -side, -.060f, 1, .95f, root, side);
        }
        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>
    /// A deliberately sparse far-distance cluster. It is kept separate from
    /// <see cref="CreateIntermediate"/> so streamers can opt into it without
    /// changing the established near/intermediate handoff.
    /// </summary>
    public static Mesh CreateFarCluster(bool indexed = true)
    {
        var surface = new SurfaceTool();
        BeginGrassSurface(surface);
        // Keep the far prototype at the same four-triangle ceiling as the
        // intermediate tuft. Its wider, more splayed crossed cards trade
        // close silhouette detail for a stable distant coverage read.
        // One far source stands in for roughly sixteen near candidates. Two
        // very broad crossed silhouettes read as a small clump while retaining
        // the four-triangle ceiling and avoiding additional roots/dispatches.
        AddBlade(surface, Vector3.Right, Vector3.Forward, .070f, 1, 1.10f);
        AddBlade(surface, Vector3.Forward, -Vector3.Right, -.060f, 1, .95f, sourceFacingAxis: Vector3.Right);
        return CommitGrassSurface(surface, indexed);
    }

    /// <summary>Compatibility prototype for callers that explicitly need cards.</summary>
    public static Mesh CreateLegacyClump(int segments = 4, bool indexed = true)
    {
        segments = Mathf.Clamp(segments, 2, 12);
        var surface = new SurfaceTool();
        BeginGrassSurface(surface);
        for (int blade = 0; blade < 3; blade++)
        {
            float angle = blade * Mathf.Pi / 3.0f;
            Vector3 sideways = new(Mathf.Cos(angle), 0.0f, Mathf.Sin(angle));
            Vector3 outward = new(-sideways.Z, 0.0f, sideways.X);
            AddBlade(surface, sideways, outward, blade == 0 ? .075f : -.055f, segments, .50f);
        }
        return CommitGrassSurface(surface, indexed);
    }

    private static Mesh CommitGrassSurface(SurfaceTool surface, bool indexed)
    {
        // Deduplicate complete attribute tuples while retaining the authored
        // triangle order. The unindexed reference supports exact regressions
        // and measured comparisons without a second geometry implementation.
        if (indexed) surface.Index();
        return surface.Commit();
    }

    private static void AddBlade(SurfaceTool surface, Vector3 sideways, Vector3 outward, float lean, int segments,
        float rootHalfWidth = .065f, Vector3 rootOffset = default, Vector3? sourceFacingAxis = null)
    {
        // All cards from a source carry the same anchor and rest width axis.
        // In particular, crossed intermediate/far cards must rotate as a tuft
        // when the optional distant camera-facing path is enabled.
        Vector3 facingAxis = sourceFacingAxis ?? sideways;
        Color sourceData = new(rootOffset.X, rootOffset.Z, facingAxis.X, facingAxis.Z);
        const float tipHalfWidth = .018f;
        for (int segment = 0; segment < segments; segment++)
        {
            float t0 = segment / (float)segments;
            float t1 = (segment + 1) / (float)segments;
            BladeSection(t0, sideways, outward, lean, out Vector3 left0, out Vector3 right0, out Vector3 leftNormal0, out Vector3 rightNormal0);
            BladeSection(t1, sideways, outward, lean, out Vector3 left1, out Vector3 right1, out Vector3 leftNormal1, out Vector3 rightNormal1);

            // Godot considers clockwise triangles front-facing. Emit both
            // triangles with supplied, softly upward-facing blade normals.
            AddVertex(surface, left0, leftNormal0, new Vector2(0, t0), sourceData);
            AddVertex(surface, left1, leftNormal1, new Vector2(0, t1), sourceData);
            AddVertex(surface, right1, rightNormal1, new Vector2(1, t1), sourceData);
            AddVertex(surface, left0, leftNormal0, new Vector2(0, t0), sourceData);
            AddVertex(surface, right1, rightNormal1, new Vector2(1, t1), sourceData);
            AddVertex(surface, right0, rightNormal0, new Vector2(1, t0), sourceData);
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

    private static void BeginGrassSurface(SurfaceTool surface)
    {
        // Root offsets reach 2.5m on the production clump; use float custom
        // data rather than normalized RGBA8 so shader anchors stay exact.
        surface.Begin(Mesh.PrimitiveType.Triangles);
        // SurfaceTool requires Begin before its vertex channel format setup.
        // This must precede every SetCustom/AddVertex call.
        surface.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
    }

    private static void AddVertex(SurfaceTool surface, Vector3 position, Vector3 normal, Vector2 uv, Color sourceData)
    {
        surface.SetNormal(normal);
        surface.SetUV(uv);
        surface.SetCustom(0, sourceData);
        surface.AddVertex(position);
    }
}
