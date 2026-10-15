using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Shared per-kind bore resources (liner, plug, inlay ring, rim cap, collision), in asset-local coordinates: the origin is
/// the bore axis at the pad datum and +Y points inward (up). The liner runs from y = -200 to y = -0.5, the open iris rim sits
/// at y = -200 (no floor, no plug collision). Pure data: building it needs no scene nodes.
/// </summary>
public sealed class EmitterBoreKindResources
{
    public SiteKind Kind { get; init; }
    public float LinerRadius { get; init; }
    public float DiscardRadius { get; init; }
    public ArrayMesh Mesh { get; init; } = null!;
    public ConcavePolygonShape3D LinerShape { get; init; } = null!;
    public Material Dark { get; init; } = null!;
    public Material Inlay { get; init; } = null!;
    public Color InlayEmission { get; init; }
    public float LightRange { get; init; }
}

public static class EmitterBoreGeometry
{
    public const int RadialSegments = 64, VerticalSegments = 8;
    public const float PlugY = -EmitterSiteApertures.PlugDepthM;
    public const float PlugClearY = -EmitterSiteApertures.PlugDepthM + 0.02f;
    // The teal inlay is the iris rim: an annulus at -200 whose outer edge is the liner radius (the plug itself is open since M2-3).
    public const float RegularInlayInner = 70f, RegularInlayOuter = 76f, MainInlayInner = 140f, MainInlayOuter = 151f;
    public const float LightLocalY = -185f, RegularLightRange = 260f, MainLightRange = 420f;
    public static readonly Color FallbackTeal = new(0.2f, 0.9f, 0.85f);
    public const string RegularDarkPath = "res://assets/poi/beam_emitter/merged/materials/Obsidian_Alloy.tres";
    public const string RegularInlayPath = "res://assets/poi/beam_emitter/merged/materials/Reactor_Teal_Inlay.tres";
    public const string MainDarkPath = "res://assets/poi/main_spire/merged/materials/Obsidian_Alloy.tres";
    public const string MainInlayPath = "res://assets/poi/main_spire/merged/materials/Reactor_Teal_Inlay.tres";

    private static readonly EmitterBoreKindResources?[] Cache = new EmitterBoreKindResources?[2];

    public static float InlayInner(SiteKind kind) => kind == SiteKind.Main ? MainInlayInner : RegularInlayInner;
    public static float InlayOuter(SiteKind kind) => kind == SiteKind.Main ? MainInlayOuter : RegularInlayOuter;
    public static float LightRange(SiteKind kind) => kind == SiteKind.Main ? MainLightRange : RegularLightRange;

    /// <summary>Shared resources for a kind (built once, then reused by every slot).</summary>
    public static EmitterBoreKindResources Get(SiteKind kind)
    {
        int index = kind == SiteKind.Main ? 1 : 0;
        if (Cache[index] is { } cached && GodotObject.IsInstanceValid(cached.Mesh)) return cached;
        return Cache[index] = Build(kind);
    }

    public static void ClearCache() => Array.Clear(Cache);

    public static EmitterBoreKindResources Build(SiteKind kind)
    {
        float liner = EmitterSiteApertures.LinerRadius(kind), discard = EmitterSiteApertures.DiscardRadius(kind);
        (Material dark, Material inlay, Color emission) = LoadMaterials(kind);

        BuildLiner(liner, out Vector3[] linerVertices, out Vector3[] linerNormals, out int[] linerIndices);
        BuildCap(liner, discard, out Vector3[] capVertices, out int[] capIndices);
        BuildInlay(InlayInner(kind), InlayOuter(kind), out Vector3[] inlayVertices, out int[] inlayIndices);

        var mesh = new ArrayMesh();
        AddSurface(mesh, Join(linerVertices, capVertices), JoinNormals(linerNormals, capVertices.Length),
            Join(linerIndices, capIndices, linerVertices.Length), dark);
        AddSurface(mesh, inlayVertices, UpNormals(inlayVertices.Length), inlayIndices, inlay);

        // Collision faces: liner + rim cap (double sided through BackfaceCollision). There is no plug collision: the bore is open.
        var faces = new Vector3[linerIndices.Length + capIndices.Length];
        for (int i = 0; i < linerIndices.Length; i++) faces[i] = linerVertices[linerIndices[i]];
        for (int i = 0; i < capIndices.Length; i++) faces[linerIndices.Length + i] = capVertices[capIndices[i]];
        var linerShape = new ConcavePolygonShape3D { BackfaceCollision = true };
        linerShape.SetFaces(faces);

        return new EmitterBoreKindResources
        {
            Kind = kind, LinerRadius = liner, DiscardRadius = discard, Mesh = mesh, LinerShape = linerShape,
            Dark = dark, Inlay = inlay, InlayEmission = emission, LightRange = LightRange(kind)
        };
    }

    // ---- Pure geometry (arrays), used by the mesh builder and the tests -------------------------------------------------

    /// <summary>Inward-facing wall: 64 radial x 8 vertical quads from y = -200 to y = LinerTopM at exactly <paramref name="radius"/>.</summary>
    public static void BuildLiner(float radius, out Vector3[] vertices, out Vector3[] normals, out int[] indices)
    {
        int columns = RadialSegments, rows = VerticalSegments;
        vertices = new Vector3[columns * (rows + 1)];
        normals = new Vector3[vertices.Length];
        indices = new int[columns * rows * 6];
        for (int r = 0; r <= rows; r++)
        {
            float y = Mathf.Lerp(PlugY, EmitterSiteApertures.LinerTopM, r / (float)rows);
            for (int c = 0; c < columns; c++)
            {
                float angle = c * Mathf.Tau / columns;
                float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                vertices[r * columns + c] = new Vector3(cos * radius, y, sin * radius);
                normals[r * columns + c] = new Vector3(-cos, 0f, -sin);
            }
        }
        int k = 0;
        for (int r = 0; r < rows; r++)
        for (int c = 0; c < columns; c++)
        {
            int c1 = (c + 1) % columns;
            int a = r * columns + c, b = r * columns + c1, d = (r + 1) * columns + c, e = (r + 1) * columns + c1;
            // Normals are explicit and the materials are double sided, so winding only needs to be consistent.
            indices[k++] = a; indices[k++] = d; indices[k++] = b;
            indices[k++] = b; indices[k++] = d; indices[k++] = e;
        }
    }

    /// <summary>Annulus at y = LinerTopM from the liner radius to a polygon that circumscribes the discard circle.</summary>
    public static void BuildCap(float innerRadius, float discardRadius, out Vector3[] vertices, out int[] indices)
    {
        float outer = CapOuterVertexRadius(discardRadius);
        BuildRing(innerRadius, outer, EmitterSiteApertures.LinerTopM, out vertices, out indices);
    }

    /// <summary>Outer cap vertices sit at R / cos(pi / 64) so the polygon edges are tangent to the discard circle.</summary>
    public static float CapOuterVertexRadius(float discardRadius) => discardRadius / Mathf.Cos(Mathf.Pi / RadialSegments);

    /// <summary>Teal inlay annulus (the iris rim) lying 2 cm above y = -200, inside the liner radius.</summary>
    public static void BuildInlay(float inner, float outer, out Vector3[] vertices, out int[] indices)
        => BuildRing(inner, outer, PlugClearY, out vertices, out indices);

    private static void BuildRing(float inner, float outer, float y, out Vector3[] vertices, out int[] indices)
    {
        int n = RadialSegments;
        vertices = new Vector3[n * 2];
        indices = new int[n * 6];
        for (int c = 0; c < n; c++)
        {
            float angle = c * Mathf.Tau / n;
            float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
            vertices[c] = new Vector3(cos * inner, y, sin * inner);
            vertices[n + c] = new Vector3(cos * outer, y, sin * outer);
        }
        int k = 0;
        for (int c = 0; c < n; c++)
        {
            int c1 = (c + 1) % n;
            // Materials are double sided; normals are explicit (+Y).
            indices[k++] = c; indices[k++] = c1; indices[k++] = n + c;
            indices[k++] = c1; indices[k++] = n + c1; indices[k++] = n + c;
        }
    }

    // ---- Mesh assembly ----------------------------------------------------------------------------------------------------

    private static Vector3[] Join(Vector3[] a, Vector3[] b)
    {
        var all = new Vector3[a.Length + b.Length];
        a.CopyTo(all, 0); b.CopyTo(all, a.Length);
        return all;
    }

    private static Vector3[] JoinNormals(Vector3[] liner, int capCount)
    {
        var all = new Vector3[liner.Length + capCount];
        liner.CopyTo(all, 0);
        for (int i = liner.Length; i < all.Length; i++) all[i] = Vector3.Up;
        return all;
    }

    private static Vector3[] UpNormals(int count)
    {
        var normals = new Vector3[count];
        Array.Fill(normals, Vector3.Up);
        return normals;
    }

    private static int[] Join(int[] liner, int[] cap, int linerVertexCount)
    {
        var all = new int[liner.Length + cap.Length];
        liner.CopyTo(all, 0);
        for (int i = 0; i < cap.Length; i++) all[liner.Length + i] = cap[i] + linerVertexCount;
        return all;
    }

    private static void AddSurface(ArrayMesh mesh, Vector3[] vertices, Vector3[] normals, int[] indices, Material material)
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, material);
    }

    // ---- Materials ----------------------------------------------------------------------------------------------------------

    private static (Material Dark, Material Inlay, Color Emission) LoadMaterials(SiteKind kind)
    {
        string darkPath = kind == SiteKind.Main ? MainDarkPath : RegularDarkPath;
        string inlayPath = kind == SiteKind.Main ? MainInlayPath : RegularInlayPath;
        Material? dark = TryLoad(darkPath);
        if (dark is StandardMaterial3D darkStandard) darkStandard.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
        else dark = new StandardMaterial3D { AlbedoColor = new Color(0.08f, 0.08f, 0.08f), Metallic = 0.5f, Roughness = 0.4f, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        Material? inlay = TryLoad(inlayPath);
        Color emission = FallbackTeal;
        if (inlay is StandardMaterial3D inlayStandard)
        {
            inlayStandard.CullMode = BaseMaterial3D.CullModeEnum.Disabled;
            if (inlayStandard.EmissionEnabled) emission = inlayStandard.Emission;
        }
        else
        {
            inlay = new StandardMaterial3D
            {
                AlbedoColor = FallbackTeal, EmissionEnabled = true, Emission = FallbackTeal, EmissionEnergyMultiplier = 1.8f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled
            };
        }
        return (dark, inlay, emission);
    }

    /// <summary>Loads a material and returns a private duplicate, so the shared POI resource is never modified.</summary>
    private static Material? TryLoad(string path)
    {
        try
        {
            if (!ResourceLoader.Exists(path)) return null;
            return ResourceLoader.Load<Material>(path)?.Duplicate() as Material;
        }
        catch { return null; }
    }
}
