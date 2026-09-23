using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Focused non-GPU geometry and exclusion checks for grass clumps.</summary>
public static class GrassClumpValidation
{
    public static void Verify()
    {
        VerifyMesh(RingGrassMeshFactory.CreateClump(), 32, "near");
        VerifyMesh(RingGrassMeshFactory.CreateIntermediateClump(), 16, "intermediate");
        VerifyMesh(RingGrassMeshFactory.CreateFarClump(), 16, "far");
        VerifyDenseClumpPlacement();
        var seam = new RingGrassExclusionSnapshot(1, 100, new[] { new RingGrassExclusionBox(99.8, 100, 0, 2, 4, 6) });
        if (seam.IsExcluded(.15, .1, 5, .5f) || !seam.IsExcluded(.15, .1, 5, .5f, .3f))
            throw new InvalidOperationException("clump exclusion root footprint missed seam");
        RingGrassExclusionRegion[] dirty = seam.GetDirtyRegionsSince(null, .3f);
        if (dirty.Length < 2) throw new InvalidOperationException("clump dirty margin did not wrap at seam");
    }

    private static void VerifyMesh(Mesh mesh, int triangles, string name)
    {
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        if (vertices.Length != triangles * 3 || normals.Length != vertices.Length || uvs.Length != vertices.Length)
            throw new InvalidOperationException($"{name} clump triangle contract failed");
        foreach (Vector3 vertex in vertices)
            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || !float.IsFinite(vertex.Z) || vertex.Y < 0 || vertex.Y > 1)
                throw new InvalidOperationException($"{name} clump vertex contract failed");
        foreach (Vector3 normal in normals) if (!Mathf.IsEqualApprox(normal.Length(), 1)) throw new InvalidOperationException($"{name} clump normal contract failed");
        foreach (Vector2 uv in uvs) if (uv.X < 0 || uv.X > 1 || uv.Y < 0 || uv.Y > 1) throw new InvalidOperationException($"{name} clump UV contract failed");
        if (mesh.GetAabb().Size.X < .2f || mesh.GetAabb().Size.Z < .2f) throw new InvalidOperationException($"{name} clump bounds contract failed");
    }

    private static void VerifyDenseClumpPlacement()
    {
        const long circumference = 981_748L * 32L;
        const int width = 318_000;
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain settings missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5_000_000d, width, 8000);
        var focus = new RingCanonicalPosition(186_000, 0, 141_000);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero, circumference / Math.Tau,
            0, 0, circumference, -1000, 1000, focus);
        var edits = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        var bounds = new RingGrassTileBounds(focus.AroundMeters - 16, focus.AroundMeters + 16, focus.AcrossMeters - 16, focus.AcrossMeters + 16);
        var key = new RingGrassTileKey((long)(focus.AroundMeters / 32), (long)(focus.AcrossMeters / 32));

        RingGrassTileData Build(float fraction, int cap, bool clumps) => RingGrassTileBuilder.Build(key, bounds, 4, 4, 16, cap, 60,
            sampler, edits, geometry, CancellationToken.None, sourceFraction: fraction, useGrassClumps: clumps);

        RingGrassTileData baseline = Build(1, 16_384, false);
        RingGrassTileData dense = Build(1, 16_384, true);
        RingGrassTileData repeated = Build(1, 16_384, true);
        if (baseline.InstanceCount < 100 || dense.InstanceCount < 20)
            throw new InvalidOperationException($"dense clump fixture underfilled: baseline={baseline.InstanceCount}, clumps={dense.InstanceCount}");
        float ratio = dense.InstanceCount / (float)baseline.InstanceCount;
        if (ratio < .18f || ratio > .32f)
            throw new InvalidOperationException($"dense clump source ratio was {ratio:F3}, expected approximately one quarter");
        if (dense.BladesPerSource != 4 || !dense.Buffer.AsSpan().SequenceEqual(repeated.Buffer) || dense.PlacementHash != repeated.PlacementHash)
            throw new InvalidOperationException("dense clump placement was not deterministic or blade-equivalent");

        RingGrassTileData near = Build(1, 16_384, true);
        RingGrassTileData middle = Build(.5f, 16_384, true);
        RingGrassTileData far = Build(.25f, 16_384, true);
        var nearRows = Rows(near.Buffer);
        var middleRows = Rows(middle.Buffer);
        if (!middleRows.All(nearRows.Contains) || !Rows(far.Buffer).All(middleRows.Contains))
            throw new InvalidOperationException("dense LOD source tiers are not nested near/middle/far subsets");

        RingGrassTileData capped = Build(1, 37, true);
        if (capped.InstanceCount > 37 || capped.BladesPerSource != 4)
            throw new InvalidOperationException("dense clump source cap exceeded its root budget");
        VerifyBounds(dense, RingGrassMeshFactory.CreateClump(), "near");
        VerifyBounds(dense, RingGrassMeshFactory.CreateIntermediateClump(), "intermediate");
        VerifyBounds(dense, RingGrassMeshFactory.CreateFarClump(), "far");
    }

    private static HashSet<string> Rows(float[] buffer)
    {
        var rows = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; offset < buffer.Length; offset += RingGrassTileData.FloatsPerInstance)
            rows.Add(string.Join(",", buffer.AsSpan(offset, RingGrassTileData.FloatsPerInstance).ToArray().Select(value => BitConverter.SingleToInt32Bits(value))));
        return rows;
    }

    private static void VerifyBounds(RingGrassTileData data, Mesh mesh, string name)
    {
        Vector3[] vertices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        for (int offset = 0; offset < data.Buffer.Length; offset += RingGrassTileData.FloatsPerInstance)
        {
            Transform3D transform = Transform(data.Buffer, offset);
            foreach (Vector3 vertex in vertices)
                if (!data.LocalAabb.HasPoint(transform * vertex))
                    throw new InvalidOperationException($"{name} clump mesh escaped tile LocalAabb");
        }
    }

    private static Transform3D Transform(float[] buffer, int offset) => new(
        new Basis(new Vector3(buffer[offset], buffer[offset + 4], buffer[offset + 8]),
            new Vector3(buffer[offset + 1], buffer[offset + 5], buffer[offset + 9]),
            new Vector3(buffer[offset + 2], buffer[offset + 6], buffer[offset + 10])),
        new Vector3(buffer[offset + 3], buffer[offset + 7], buffer[offset + 11]));
}
