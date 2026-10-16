using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>CPU-side surface arrays (buildable on any thread). The ArrayMesh is created lazily on the main thread.</summary>
public sealed class RingGrassMeshData
{
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public Vector2[] Uv { get; init; } = Array.Empty<Vector2>();
    public Vector2[] Uv2 { get; init; } = Array.Empty<Vector2>();
    /// <summary>Optional per-vertex CUSTOM0 (RG float, 2 floats per vertex). Null when the mesh has none.</summary>
    public float[]? Custom0Rg { get; init; }
    public int[] Indices { get; init; } = Array.Empty<int>();
    public Aabb CustomAabb { get; init; }
    public int TriangleCount => Indices.Length / 3;

    ArrayMesh? _mesh;

    /// <summary>Main thread only. Returns the same mesh every call.</summary>
    public ArrayMesh GetMesh()
    {
        if (_mesh != null) return _mesh;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = Normals;
        arrays[(int)Mesh.ArrayType.TexUV] = Uv;
        arrays[(int)Mesh.ArrayType.TexUV2] = Uv2;
        arrays[(int)Mesh.ArrayType.Index] = Indices;
        var mesh = new ArrayMesh();
        if (Custom0Rg != null)
        {
            arrays[(int)Mesh.ArrayType.Custom0] = Custom0Rg;
            var flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
        }
        else mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        mesh.CustomAabb = CustomAabb;
        _mesh = mesh;
        return mesh;
    }
}

/// <summary>
/// Trimmed three-card grass mesh (ported from GrassCardsSpikeAssets.BuildMesh): cards at 0/60/120 degrees, 3 rows each,
/// 18 vertices, 12 triangles. UV is the card-local atlas coordinate, UV2 = (card-local x offset, card index) so the vertex
/// shader can collapse 3 cards into one camera-facing card at distance. Position is only a model-space fallback.
/// </summary>
public static class RingGrassCardMeshFactory
{
    public const int CardCount = 3;
    public const int VertexCount = 18;
    public const int TriangleCount = 12;
    /// <summary>Largest per-instance scale compensation the shader may apply (thinned tiers).</summary>
    public const float ScaleCompensationCap = 2.5f;
    /// <summary>Horizontal width gain when three cards collapse into one.</summary>
    public const float SingleCardWidthGain = 1.6f;

    public static RingGrassMeshData Build()
    {
        float[] rowY = RingGrassCardAtlasGenerator.RowY, rowHalf = RingGrassCardAtlasGenerator.RowHalfWidth;
        var verts = new List<Vector3>(VertexCount); var normals = new List<Vector3>(VertexCount);
        var uv = new List<Vector2>(VertexCount); var uv2 = new List<Vector2>(VertexCount); var idx = new List<int>(TriangleCount * 3);
        for (int k = 0; k < CardCount; k++)
        {
            float a = k * Mathf.Pi / 3f;
            var u = new Vector3(MathF.Cos(a), 0f, -MathF.Sin(a));
            int baseIdx = verts.Count;
            for (int r = 0; r < 3; r++)
                for (int side = 0; side < 2; side++)
                {
                    float lx = (side == 0 ? -1f : 1f) * rowHalf[r];
                    verts.Add(u * lx + new Vector3(0f, rowY[r], 0f));
                    normals.Add(Vector3.Up);
                    uv.Add(new Vector2(0.5f + lx, 1f - rowY[r]));
                    uv2.Add(new Vector2(lx, k));
                }
            for (int r = 0; r < 2; r++)
            {
                int i0 = baseIdx + r * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                idx.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
        }
        // Spike bounds (0.8 half width covers 0.46 * 1.6 collapse gain) widened by the compensation cap, because the
        // shader applies the compensated scale on top of the baked instance transform.
        float half = 0.8f * ScaleCompensationCap, top = 1.5f * ScaleCompensationCap;
        return new RingGrassMeshData
        {
            Vertices = verts.ToArray(), Normals = normals.ToArray(), Uv = uv.ToArray(), Uv2 = uv2.ToArray(), Indices = idx.ToArray(),
            CustomAabb = new Aabb(new Vector3(-half, 0f, -half), new Vector3(half * 2f, top, half * 2f)),
        };
    }
}
