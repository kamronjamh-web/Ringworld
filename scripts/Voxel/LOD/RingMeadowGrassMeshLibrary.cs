using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Loads the checked-in Blender export once per geometry setting, on the render thread.</summary>
internal sealed class RingMeadowGrassMeshLibrary : IDisposable
{
    private readonly Dictionary<(int Variant, string Tier), Mesh> _meshes = new();
    private float _spread = float.NaN;

    public Mesh Get(int variant, string tier, float spread)
    {
        spread = float.IsFinite(spread) ? Mathf.Clamp(spread, .25f, 2f) : 1f;
        if (_spread != spread) { Dispose(); _spread = spread; }
        variant = Math.Clamp(variant, 0, 2);
        if (_meshes.TryGetValue((variant, tier), out Mesh? mesh)) return mesh;
        string name = new[] { "compact", "spreading", "leaning" }[variant];
        string path = $"res://resources/grass/meadow/{name}_{tier}.json";
        using var file = Godot.FileAccess.Open(path, Godot.FileAccess.ModeFlags.Read)
            ?? throw new InvalidOperationException($"Cannot load authored meadow grass: {path}");
        mesh = Parse(file.GetAsText(), tier, spread);
        _meshes.Add((variant, tier), mesh);
        return mesh;
    }

    internal static Mesh Parse(string json, string tier, float spread = 1f)
    {
        if (tier != "near" && tier != "near_reduced" && tier != "intermediate" && tier != "far")
            throw new InvalidOperationException("Unknown meadow mesh tier.");
        if (!float.IsFinite(spread) || spread < .25f || spread > 2f)
            throw new InvalidOperationException("Invalid meadow spread.");
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("tier").GetString() != tier)
            throw new InvalidOperationException("Unsupported meadow mesh schema or tier.");
        int blades = root.GetProperty("bladeCount").GetInt32();
        if (blades < 1 || blades > 8) throw new InvalidOperationException("Invalid meadow blade count.");
        float[] positions = ReadFloats(root, "positions"), normals = ReadFloats(root, "normals"),
            uv = ReadFloats(root, "uv"), custom = ReadFloats(root, "custom0");
        int count = positions.Length / 3;
        if (count < 3 || positions.Length != count * 3 || normals.Length != count * 3
            || uv.Length != count * 2 || custom.Length != count * 4)
            throw new InvalidOperationException("Malformed meadow mesh vertex channels.");
        JsonElement indices = root.GetProperty("indices");
        int ceiling = tier == "near" ? 32 : 16;
        if (indices.GetArrayLength() == 0 || indices.GetArrayLength() % 3 != 0 || indices.GetArrayLength() > ceiling * 3)
            throw new InvalidOperationException("Meadow mesh exceeds its triangle budget.");
        using var surface = new SurfaceTool();
        surface.Begin(Mesh.PrimitiveType.Triangles);
        surface.SetCustomFormat(0, SurfaceTool.CustomFormat.RgbaFloat);
        bool hasRoot = false;
        for (int i = 0; i < count; i++)
        {
            Vector3 p = new(positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2]);
            Vector3 n = new(normals[i * 3], normals[i * 3 + 1], normals[i * 3 + 2]);
            Vector2 tex = new(uv[i * 2], uv[i * 2 + 1]);
            Vector2 anchor = new(custom[i * 4], custom[i * 4 + 1]);
            Vector2 side = new(custom[i * 4 + 2], custom[i * 4 + 3]);
            if (p.Y < 0 || p.Y > 1.0001f || tex.Y < 0 || tex.Y > 1.0001f
                || Math.Abs(p.X) > 3.7f || Math.Abs(p.Z) > 3.7f || anchor.Length() > 2.5001f
                || Math.Abs(n.LengthSquared() - 1f) > .02f || Math.Abs(side.LengthSquared() - 1f) > .02f)
                throw new InvalidOperationException("Meadow mesh violates normalized height, footprint or frame contract.");
            hasRoot |= p.Y == 0 && tex.Y == 0;
            surface.SetNormal(new Vector3(n.X / spread, n.Y, n.Z / spread).Normalized());
            surface.SetUV(tex);
            surface.SetCustom(0, new Color(anchor.X * spread, anchor.Y * spread, side.X, side.Y));
            surface.AddVertex(new Vector3(p.X * spread, p.Y, p.Z * spread));
        }
        if (!hasRoot) throw new InvalidOperationException("Meadow mesh has no pinned roots.");
        var triangleIndices = new int[indices.GetArrayLength()];
        int cursor = 0;
        foreach (JsonElement element in indices.EnumerateArray())
        {
            int index = element.GetInt32();
            if (index < 0 || index >= count) throw new InvalidOperationException("Invalid meadow triangle index.");
            triangleIndices[cursor++] = index;
            surface.AddIndex(index);
        }
        for (int i = 0; i < triangleIndices.Length; i += 3)
        {
            Vector3 Position(int index) => new(positions[index * 3], positions[index * 3 + 1], positions[index * 3 + 2]);
            int a = triangleIndices[i];
            Vector3 cross = (Position(triangleIndices[i + 1]) - Position(a)).Cross(Position(triangleIndices[i + 2]) - Position(a));
            Vector3 normal = new(normals[a * 3], normals[a * 3 + 1], normals[a * 3 + 2]);
            if (cross.LengthSquared() < 1e-12f || -cross.Normalized().Dot(normal) < .8f)
                throw new InvalidOperationException("Meadow mesh has degenerate or reversed triangles.");
        }
        return surface.Commit();
    }

    public void Dispose()
    {
        // Drop our owned RefCounted references. MultiMeshes retain their own native
        // references until retired, so an in-flight draw keeps its mesh alive.
        foreach (Mesh mesh in _meshes.Values) mesh.Dispose();
        _meshes.Clear();
    }

    private static float[] ReadFloats(JsonElement root, string name)
    {
        JsonElement array = root.GetProperty(name);
        var values = new float[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement element in array.EnumerateArray())
        {
            float value = element.GetSingle();
            if (!float.IsFinite(value)) throw new InvalidOperationException($"Non-finite meadow channel {name}.");
            values[i++] = value;
        }
        return values;
    }
}
