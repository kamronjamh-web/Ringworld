using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Indexed UV grid for the flyover shader; normals are generated analytically in the material.</summary>
public static class MenuFlyoverTerrainMesh
{
    public static ArrayMesh BuildGrid(int rowCount = MenuFlyoverTerrainCache.Rows, int columnCount = MenuFlyoverTerrainCache.Columns)
    {
        bool supported = (rowCount == 2049 && columnCount == 1537)
            || (rowCount == 1025 && columnCount == 769)
            || (rowCount == 513 && columnCount == 385);
        if (!supported) throw new ArgumentOutOfRangeException(nameof(rowCount), "Unsupported menu terrain grid size.");
        int rows = rowCount;
        int columns = columnCount;
        var vertices = new Vector3[rows * columns];
        var uvs = new Vector2[rows * columns];
        var indices = new int[(rows - 1) * (columns - 1) * 6];
        int index = 0;
        for (int y = 0; y < rows; y++)
        for (int x = 0; x < columns; x++)
        {
            vertices[index] = Vector3.Zero;
            uvs[index++] = new Vector2(x / (float)(columns - 1), y / (float)(rows - 1));
        }
        index = 0;
        for (int y = 0; y < rows - 1; y++)
        for (int x = 0; x < columns - 1; x++)
        {
            int a = y * columns + x, b = a + 1, c = a + columns, d = c + 1;
            indices[index++] = c; indices[index++] = b; indices[index++] = a;
            indices[index++] = b; indices[index++] = c; indices[index++] = d;
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)ArrayMesh.ArrayType.Max);
        arrays[(int)ArrayMesh.ArrayType.Vertex] = vertices;
        arrays[(int)ArrayMesh.ArrayType.TexUV] = uvs;
        arrays[(int)ArrayMesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }
}
