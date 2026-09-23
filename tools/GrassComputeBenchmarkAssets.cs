using System;
using Godot;

namespace RingworldTechDemo.Tests;

/// <summary>
/// Benchmark-only mesh and material variants for comparing vegetation compute
/// grouping. These resources deliberately do not participate in gameplay.
/// </summary>
public static class GrassComputeBenchmarkAssets
{
    /// <summary>
    /// Horizontal half-extent of the solid clump in normalized mesh units.
    /// With the production .045-.082m X/Z instance scale this is .140-.254m
    /// from the root before wind deformation.
    /// </summary>
    public const float SolidClumpNormalizedHalfExtent = 3.1f;

    /// <summary>
    /// Builds independently rooted production-detail ribbons. The default is
    /// four four-segment blades (32 triangles): precisely four times the
    /// production single blade's eight-triangle geometry.
    /// </summary>
    public static Mesh MakeSolidClump(int blades = 4)
    {
        blades = Mathf.Clamp(blades, 1, 16);
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int blade = 0; blade < blades; blade++)
        {
            float angle = Mathf.Tau * blade / blades + Mathf.Pi * .25f;
            Vector3 root = new(Mathf.Cos(angle) * 2.5f, 0, Mathf.Sin(angle) * 2.5f);
            Vector3 sideways = new(Mathf.Cos(angle + Mathf.Pi * .5f), 0, Mathf.Sin(angle + Mathf.Pi * .5f));
            Vector3 outward = new(-sideways.Z, 0, sideways.X);
            // Match RingGrassMeshFactory.Create's normalized .50 root width.
            AddRibbon(surface, root, sideways, outward, blade % 2 == 0 ? .05f : -.05f, 4, .50f);
        }
        return surface.Commit();
    }

    /// <summary>
    /// Four spatially offset copies of the production intermediate source.
    /// Each source has its two one-segment crossed ribbons, so the result is
    /// 16 triangles and preserves one-quarter source coverage in the mid pass.
    /// </summary>
    public static Mesh MakeIntermediateClump()
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int source = 0; source < 4; source++)
        {
            float angle = Mathf.Tau * source / 4.0f + Mathf.Pi * .25f;
            Vector3 root = new(Mathf.Cos(angle) * 2.5f, 0, Mathf.Sin(angle) * 2.5f);
            Vector3 sideways = new(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            Vector3 outward = new(-sideways.Z, 0, sideways.X);
            // Exact RingGrassMeshFactory.CreateIntermediate() ribbon contract.
            AddRibbon(surface, root, sideways, outward, .055f, 1, .40f);
            AddRibbon(surface, root, outward, -sideways, -.040f, 1, .34f);
        }
        return surface.Commit();
    }

    /// <summary>
    /// Builds three crossed, root-anchored cards. Use <see cref="MakeCardMaterial"/>
    /// to apply the matching alpha-scissor blade pattern.
    /// </summary>
    public static Mesh MakeCardClump()
    {
        var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        for (int card = 0; card < 3; card++)
        {
            float angle = card * Mathf.Pi / 3.0f;
            Vector3 sideways = new(Mathf.Cos(angle), 0, Mathf.Sin(angle));
            AddCard(surface, sideways);
        }
        return surface.Commit();
    }

    /// <summary>
    /// Clones production parameters then replaces only the shader with the
    /// benchmark card variant. The shader keeps the production grass wind,
    /// cloud shadow, interaction, debug, LOD, and appearance uniform names.
    /// </summary>
    public static ShaderMaterial MakeCardMaterial(ShaderMaterial productionBase)
    {
        ArgumentNullException.ThrowIfNull(productionBase);
        var material = (ShaderMaterial)productionBase.Duplicate(true);
        material.Shader = GD.Load<Shader>("res://materials/vegetation_gpu/grass_benchmark_cards.gdshader")
            ?? throw new InvalidOperationException("benchmark card shader is unavailable");
        return material;
    }

    /// <summary>Focused non-rendering checks for benchmark smoke tests.</summary>
    public static void ValidateMeshes()
    {
        Validate(MakeSolidClump(), 32, "solid");
        Validate(MakeIntermediateClump(), 16, "intermediate");
        Validate(MakeCardClump(), 6, "card");
    }

    private static void AddRibbon(SurfaceTool surface, Vector3 root, Vector3 sideways, Vector3 outward,
        float lean, int segments, float rootHalfWidth)
    {
        const float tipHalfWidth = .018f;
        for (int segment = 0; segment < segments; segment++)
        {
            float t0 = segment / (float)segments;
            float t1 = (segment + 1) / (float)segments;
            Section(t0, out Vector3 left0, out Vector3 right0, out Vector3 leftNormal0, out Vector3 rightNormal0);
            Section(t1, out Vector3 left1, out Vector3 right1, out Vector3 leftNormal1, out Vector3 rightNormal1);
            Add(surface, left0, leftNormal0, new Vector2(0, t0)); Add(surface, left1, leftNormal1, new Vector2(0, t1)); Add(surface, right1, rightNormal1, new Vector2(1, t1));
            Add(surface, left0, leftNormal0, new Vector2(0, t0)); Add(surface, right1, rightNormal1, new Vector2(1, t1)); Add(surface, right0, rightNormal0, new Vector2(1, t0));
        }

        void Section(float t, out Vector3 left, out Vector3 right, out Vector3 leftNormal, out Vector3 rightNormal)
        {
            float width = Mathf.Lerp(rootHalfWidth, tipHalfWidth, t * t);
            Vector3 center = root + Vector3.Up * t + outward * (lean * t * t) + sideways * (.026f * Mathf.Sin(t * Mathf.Pi));
            left = center - sideways * width; right = center + sideways * width;
            Vector3 tangent = (Vector3.Up + outward * (2 * lean * t) + sideways * (.026f * Mathf.Pi * Mathf.Cos(t * Mathf.Pi))).Normalized();
            Vector3 faceNormal = sideways.Cross(tangent).Normalized();
            leftNormal = (faceNormal - sideways * .34f).Normalized(); rightNormal = (faceNormal + sideways * .34f).Normalized();
        }
    }

    private static void Add(SurfaceTool surface, Vector3 vertex, Vector3 normal, Vector2 uv)
    { surface.SetNormal(normal); surface.SetUV(uv); surface.AddVertex(vertex); }

    // The card mesh stays rectangular from root to tip. The shader's three
    // procedural UV stripes provide the separate tapering blades; tapering the
    // whole quad here would collapse all three tips into one silhouette.
    private static void AddCard(SurfaceTool surface, Vector3 sideways)
    {
        Vector3 normal = sideways.Cross(Vector3.Up).Normalized();
        Vector3 leftRoot = -sideways * 2.5f;
        Vector3 rightRoot = sideways * 2.5f;
        Vector3 leftTip = leftRoot + Vector3.Up;
        Vector3 rightTip = rightRoot + Vector3.Up;
        Add(surface, leftRoot, normal, new Vector2(0, 0)); Add(surface, leftTip, normal, new Vector2(0, 1)); Add(surface, rightTip, normal, new Vector2(1, 1));
        Add(surface, leftRoot, normal, new Vector2(0, 0)); Add(surface, rightTip, normal, new Vector2(1, 1)); Add(surface, rightRoot, normal, new Vector2(1, 0));
    }

    private static void Validate(Mesh mesh, int triangles, string name)
    {
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        if (vertices.Length != triangles * 3 || normals.Length != vertices.Length || uvs.Length != vertices.Length)
            throw new InvalidOperationException($"{name} benchmark mesh has unexpected attributes");
        bool root = false;
        foreach (Vector3 vertex in vertices)
        {
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z)
                || vertex.Y < -.00001f || vertex.Y > 1.00001f)
                throw new InvalidOperationException($"{name} benchmark mesh has invalid vertex data");
            root |= Mathf.IsZeroApprox(vertex.Y);
        }
        for (int i = 0; i < uvs.Length; i++)
        {
            Vector3 normal = normals[i];
            if (!float.IsFinite(normal.X) || !float.IsFinite(normal.Y) || !float.IsFinite(normal.Z)
                || !Mathf.IsEqualApprox(normal.Length(), 1.0f))
                throw new InvalidOperationException($"{name} benchmark mesh has invalid normal data");
            Vector2 uv = uvs[i];
            if (!float.IsFinite(uv.X) || !float.IsFinite(uv.Y) || uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1)
                throw new InvalidOperationException($"{name} benchmark mesh has invalid UV data");
        }
        if (!root || mesh.GetAabb().Size.X <= .1f || mesh.GetAabb().Size.Z <= .1f)
            throw new InvalidOperationException($"{name} benchmark mesh is not root anchored or has insufficient bounds");
    }
}
