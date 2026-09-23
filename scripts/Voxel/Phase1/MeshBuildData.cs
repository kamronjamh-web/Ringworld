using Godot;

namespace RingworldTechDemo.Voxel.Phase1;

public readonly struct MeshBuildData
{
    public readonly Vector3[] Vertices;
    public readonly Vector3[] Normals;
    public readonly Color[] Colors;
    public readonly int[] Indices;
    public readonly Vector3[] CollisionFaces;
    public bool HasGeometry => Vertices.Length > 0;

    public MeshBuildData(
        Vector3[] vertices,
        Vector3[] normals,
        Color[] colors,
        int[] indices,
        Vector3[] collisionFaces)
    {
        Vertices = vertices;
        Normals = normals;
        Colors = colors;
        Indices = indices;
        CollisionFaces = collisionFaces;
    }

    public Godot.Collections.Array CreateGodotMeshArrays()
    {
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Normals;
        arrays[(int)Mesh.ArrayType.Color] = Colors;
        arrays[(int)Mesh.ArrayType.Index] = Indices;
        return arrays;
    }
}
