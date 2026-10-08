using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Vegetation;

/// <summary>Catalog-only foliage attachment data. COLOR and both UV channels stay intact.
/// Nearest searches visit at most 64 nodes per vertex in a deterministic bark tree.
/// Islands without a nearby bark candidate receive no extra flex.</summary>
public static class ImportedTreeWindAttachments
{
    private const int SearchBudget = 64;
    private const float MaximumAttachmentGap = .12f;

    public static ArrayMesh BakeCanopy(ArrayMesh canopy, Mesh bark)
    {
        var points = new List<Vector3>();
        for (int surface = 0; surface < bark.GetSurfaceCount(); surface++)
            points.AddRange(bark.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        Node? tree = BuildBarkTree(points);
        var output = new ArrayMesh();
        for (int surface = 0; surface < canopy.GetSurfaceCount(); surface++)
        {
            var arrays = canopy.SurfaceGetArrays(surface);
            Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                ? Sequential(vertices.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
            arrays[(int)Mesh.ArrayType.Custom0] = BakeMetadata(vertices, indices, tree);
            var flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
            output.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
            output.SurfaceSetMaterial(surface, canopy.SurfaceGetMaterial(surface));
        }
        return output;
    }

    public static ArrayMesh Bake(ArrayMesh source)
    {
        var bark = new List<Vector3>();
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
            if (source.SurfaceGetMaterial(surface) is ShaderMaterial mat
                && mat.GetShaderParameter("tree_foliage_mode").AsInt32() == 0)
                bark.AddRange(source.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array());
        Node? tree = BuildBarkTree(bark);
        var output = new ArrayMesh();
        for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
        {
            var arrays = source.SurfaceGetArrays(surface);
            var mat = source.SurfaceGetMaterial(surface) as ShaderMaterial;
            if (mat?.GetShaderParameter("tree_foliage_mode").AsInt32() == 1)
            {
                Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
                int[] indices = arrays[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                    ? Sequential(vertices.Length) : arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
                arrays[(int)Mesh.ArrayType.Custom0] = BakeMetadata(vertices, indices, tree);
                var flags = (Mesh.ArrayFormat)((ulong)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift);
                output.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, flags);
                mat.SetShaderParameter("tree_foliage_attachments", true);
            }
            else output.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            output.SurfaceSetMaterial(surface, source.SurfaceGetMaterial(surface));
        }
        return output;
    }

    private static float[] BakeMetadata(Vector3[] vertices, int[] indices, Node? tree)
    {
        int[] parent = Sequential(vertices.Length);
        int Find(int i) { while (parent[i] != i) { parent[i] = parent[parent[i]]; i = parent[i]; } return i; }
        void Union(int a, int b) { int x = Find(a), y = Find(b); if (x != y) parent[Math.Max(x, y)] = Math.Min(x, y); }
        var welded = new Dictionary<Vector3I, int>();
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 p = vertices[i] * 100000;
            var key = new Vector3I(Mathf.RoundToInt(p.X), Mathf.RoundToInt(p.Y), Mathf.RoundToInt(p.Z));
            if (welded.TryGetValue(key, out int other)) Union(i, other); else welded[key] = i;
        }
        for (int i = 0; i + 2 < indices.Length; i += 3)
        { Union(indices[i], indices[i + 1]); Union(indices[i], indices[i + 2]); }
        var islands = new Dictionary<int, List<int>>();
        for (int i = 0; i < vertices.Length; i++)
        { int key = Find(i); if (!islands.TryGetValue(key, out var list)) islands[key] = list = new(); list.Add(i); }
        var data = new float[vertices.Length * 4];
        foreach (var island in islands.Values)
        {
            float distance = MaximumAttachmentGap * MaximumAttachmentGap;
            int attachment = -1;
            foreach (int i in island)
            {
                int budget = SearchBudget;
                float candidate = distance;
                Search(tree, vertices[i], ref candidate, ref budget);
                if (candidate < distance) { distance = candidate; attachment = i; }
            }
            if (attachment < 0) continue; // Distant/no bark: common structural motion only.
            Vector3 anchor = vertices[attachment];
            float radius = 0;
            foreach (int i in island) radius = Math.Max(radius, vertices[i].DistanceTo(anchor));
            if (radius < .001f) continue;
            foreach (int i in island)
            { int j = i * 4; data[j] = anchor.X; data[j + 1] = anchor.Y; data[j + 2] = anchor.Z; data[j + 3] = 1 / radius; }
        }
        return data;
    }

    private sealed class Node { public Vector3 Point; public int Axis; public Node? Left, Right; }
    private static Node? BuildBarkTree(List<Vector3> bark)
    {
        // Bound tree construction even for future unusually dense bark assets.
        int step = Math.Max(1, (bark.Count + 16383) / 16384);
        var points = new List<Vector3>((bark.Count + step - 1) / step);
        for (int i = 0; i < bark.Count; i += step) points.Add(bark[i]);
        return Build(points.ToArray(), 0, points.Count, 0);
    }
    private sealed class AxisComparer(int axis) : IComparer<Vector3>
    { public int Compare(Vector3 a, Vector3 b) { int c = a[axis].CompareTo(b[axis]); if (c != 0) return c; c = a.X.CompareTo(b.X); if (c != 0) return c; c = a.Y.CompareTo(b.Y); return c != 0 ? c : a.Z.CompareTo(b.Z); } }
    private static Node? Build(Vector3[] points, int start, int count, int depth)
    {
        if (count == 0) return null;
        int axis = depth % 3;
        Array.Sort(points, start, count, new AxisComparer(axis));
        int middle = start + count / 2;
        return new Node { Point = points[middle], Axis = axis,
            Left = Build(points, start, middle - start, depth + 1),
            Right = Build(points, middle + 1, start + count - middle - 1, depth + 1) };
    }
    private static void Search(Node? node, Vector3 point, ref float distance, ref int budget)
    {
        if (node == null || budget-- <= 0) return;
        distance = Math.Min(distance, point.DistanceSquaredTo(node.Point));
        float split = point[node.Axis] - node.Point[node.Axis];
        Search(split < 0 ? node.Left : node.Right, point, ref distance, ref budget);
        if (split * split < distance) Search(split < 0 ? node.Right : node.Left, point, ref distance, ref budget);
    }
    private static int[] Sequential(int count) { var result = new int[count]; for (int i = 0; i < count; i++) result[i] = i; return result; }
}
