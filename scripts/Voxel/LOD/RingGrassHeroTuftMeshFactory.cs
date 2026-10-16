using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Hero tuft mesh (ported from GrassCardsSpikeAssets.BuildHeroTuftMesh): N tapered, curved solid blades, 4 rows + tip
/// (9 vertices / 7 triangles per blade). Heights are unit tuft space (scaled by instance Y); UV2 = (half width metres, per-blade random).
/// The per-vertex blade root offset (metres, XZ) lives in CUSTOM0.xy instead of vertex COLOR, because the MultiMesh
/// instance COLOR (biome weights) multiplies vertex COLOR. The mesh has no COLOR array.
/// </summary>
public static class RingGrassHeroTuftMeshFactory
{
    public const float UnitHeight = 0.45f;
    public const int VerticesPerBlade = 9;
    public const int TrianglesPerBlade = 7;
    public const int DefaultBlades = 8;
    public const int DefaultSeed = 4242;
    public const float DefaultFootprint = 0.15f;
    public const float DefaultHalfWidth = 0.013f;

    public static RingGrassMeshData BuildDefault() => Build(DefaultBlades, DefaultSeed, DefaultFootprint, DefaultHalfWidth);

    public static RingGrassMeshData Build(int blades, int seed, float footprint, float halfWidth)
    {
        var verts = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var uv2 = new List<Vector2>();
        var custom = new List<float>(); var idx = new List<int>();
        var rng = new RingGrassCardAtlasGenerator.Rng(RingGrassCardAtlasGenerator.Hash((uint)seed, 9001u, (uint)blades));
        float[] rowT = { 0f, 0.3f, 0.6f, 0.85f };
        float[] rowW = { 1f, 0.88f, 0.62f, 0.32f };
        for (int b = 0; b < blades; b++)
        {
            float rr = footprint * MathF.Sqrt((b + 0.5f) / blades) * rng.Range(0.8f, 1.1f);
            float ra = b * 2.39996f + rng.Range(-0.4f, 0.4f);
            float rx = rr * MathF.Cos(ra), rz = rr * MathF.Sin(ra);
            float la = Mathf.Lerp(ra, rng.Range(0f, Mathf.Tau), 0.35f);
            var l = new Vector3(MathF.Cos(la), 0f, MathF.Sin(la));
            float hb = rng.Range(0.55f, 1.0f);
            float bend = rng.Range(0.18f, 0.5f) * hb;
            float w0 = halfWidth * rng.Range(0.8f, 1.2f) * (0.7f + 0.3f * hb);
            float r = rng.Next();
            int baseIdx = verts.Count;
            for (int i = 0; i <= rowT.Length; i++)
            {
                bool tip = i == rowT.Length;
                float t = tip ? 1f : rowT[i];
                var c = new Vector3(rx, t * hb, rz) + l * (bend * t * t);
                verts.Add(c); normals.Add(l);
                uv.Add(new Vector2(tip ? 0f : -1f, t));
                uv2.Add(new Vector2(tip ? 0.0003f : w0 * rowW[i], r));
                custom.Add(rx); custom.Add(rz);
                if (!tip)
                {
                    verts.Add(c); normals.Add(l);
                    uv.Add(new Vector2(1f, t));
                    uv2.Add(new Vector2(w0 * rowW[i], r));
                    custom.Add(rx); custom.Add(rz);
                }
            }
            for (int i = 0; i < rowT.Length - 1; i++)
            {
                int i0 = baseIdx + i * 2, i1 = i0 + 1, i2 = i0 + 2, i3 = i0 + 3;
                idx.AddRange(new[] { i0, i2, i1, i1, i2, i3 });
            }
            int last = baseIdx + (rowT.Length - 1) * 2;
            idx.AddRange(new[] { last, last + 2, last + 1 });
        }
        return new RingGrassMeshData
        {
            Vertices = verts.ToArray(), Normals = normals.ToArray(), Uv = uv.ToArray(), Uv2 = uv2.ToArray(),
            Custom0Rg = custom.ToArray(), Indices = idx.ToArray(),
            CustomAabb = new Aabb(new Vector3(-0.7f, 0f, -0.7f), new Vector3(1.4f, 1.3f, 1.4f)),
        };
    }
}
