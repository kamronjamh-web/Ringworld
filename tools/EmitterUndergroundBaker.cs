using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M2-2 bake entry point, called by tools/poi/build_emitter_underground.gd. Builds the cavern shell per kind with
/// <see cref="EmitterUndergroundShell"/> and saves assets/poi/underground/CavernShell_{Kind}.res (ArrayMesh, LOD0 plus two
/// index-buffer LODs sharing one vertex array), CavernShell_{Kind}_Collision.res and CavernRock.tres.
/// </summary>
public partial class EmitterUndergroundBaker : RefCounted
{
    public const string Directory = "res://assets/poi/underground";
    public const string MaterialPath = Directory + "/CavernRock.tres";
    /// <summary>Collision is built from this level (cell ~55 m regular / ~80 m main).</summary>
    public const int CollisionLevel = 1;

    public static string MeshPath(SiteKind kind) => $"{Directory}/CavernShell_{kind}.res";
    public static string CollisionPath(SiteKind kind) => $"{Directory}/CavernShell_{kind}_Collision.res";

    /// <summary>Bakes both kinds; returns 0 on success.</summary>
    public int Bake()
    {
        try
        {
            DirAccess.MakeDirRecursiveAbsolute(ProjectSettings.GlobalizePath(Directory));
            StandardMaterial3D rock = new()
            {
                ResourceName = "CavernRock",
                AlbedoColor = new Color(0.17f, 0.15f, 0.135f),
                Roughness = 0.95f,
                Metallic = 0f,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            };
            Error err = ResourceSaver.Save(rock, MaterialPath);
            if (err != Error.Ok) { GD.PushError($"Could not save {MaterialPath}: {err}"); return 1; }
            Material material = ResourceLoader.Load<Material>(MaterialPath, null, ResourceLoader.CacheMode.Ignore);

            foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main })
            {
                EmitterShellLevel[] levels = EmitterUndergroundShell.Build(kind);
                string hash = EmitterUndergroundShell.Hash(levels);
                ArrayMesh mesh = ToMesh(kind, levels, material);
                err = ResourceSaver.Save(mesh, MeshPath(kind));
                if (err != Error.Ok) { GD.PushError($"Could not save {MeshPath(kind)}: {err}"); return 1; }
                ConcavePolygonShape3D shape = ToCollision(kind, levels[CollisionLevel]);
                err = ResourceSaver.Save(shape, CollisionPath(kind));
                if (err != Error.Ok) { GD.PushError($"Could not save {CollisionPath(kind)}: {err}"); return 1; }
                GD.Print($"EMITTER_UNDERGROUND_BAKE {kind}: cells={levels[0].CellSizeM}/{levels[1].CellSizeM}/{levels[2].CellSizeM}"
                    + $" triangles={levels[0].TriangleCount}/{levels[1].TriangleCount}/{levels[2].TriangleCount}"
                    + $" vertices={mesh.SurfaceGetArrayLen(0)} collisionTriangles={shape.GetFaces().Length / 3} hash={hash}");
            }
            return 0;
        }
        catch (Exception exception)
        {
            GD.PushError(exception.ToString());
            return 1;
        }
    }

    /// <summary>One surface: level 0 indices are the surface; levels 1 and 2 are index-only LODs over appended vertices of the same array.</summary>
    public static ArrayMesh ToMesh(SiteKind kind, EmitterShellLevel[] levels, Material? material)
    {
        int total = 0;
        foreach (EmitterShellLevel level in levels) total += level.Vertices.Length;
        Vector3[] vertices = new Vector3[total], normals = new Vector3[total];
        int[][] indices = new int[levels.Length][];
        int offset = 0;
        for (int l = 0; l < levels.Length; l++)
        {
            Array.Copy(levels[l].Vertices, 0, vertices, offset, levels[l].Vertices.Length);
            Array.Copy(levels[l].Normals, 0, normals, offset, levels[l].Normals.Length);
            indices[l] = new int[levels[l].Indices.Length];
            for (int i = 0; i < indices[l].Length; i++) indices[l][i] = levels[l].Indices[i] + offset;
            offset += levels[l].Vertices.Length;
        }
        Godot.Collections.Array arrays = new();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.Index] = indices[0];
        Godot.Collections.Dictionary lods = new();
        for (int l = 1; l < levels.Length; l++) lods[levels[l].CellSizeM] = indices[l];
        ArrayMesh mesh = new() { ResourceName = $"CavernShell_{kind}" };
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, lods);
        if (material != null) mesh.SurfaceSetMaterial(0, material);
        mesh.SetMeta("lod_triangles", new int[] { levels[0].TriangleCount, levels[1].TriangleCount, levels[2].TriangleCount });
        mesh.SetMeta("lod0_vertex_count", levels[0].Vertices.Length);
        return mesh;
    }

    public static ConcavePolygonShape3D ToCollision(SiteKind kind, EmitterShellLevel level)
    {
        Vector3[] faces = new Vector3[level.Indices.Length];
        for (int i = 0; i < faces.Length; i++) faces[i] = level.Vertices[level.Indices[i]];
        return new ConcavePolygonShape3D { ResourceName = $"CavernShell_{kind}_Collision", BackfaceCollision = true, Data = faces };
    }
}
