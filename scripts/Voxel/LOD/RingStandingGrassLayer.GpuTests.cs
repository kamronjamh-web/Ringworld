using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    /// <summary>CPU-only regression coverage for the Godot outward frustum-plane convention.</summary>
    internal static bool ValidateTileFrustumCullingForTests()
    {
        // With this representative camera plane, x &lt;= 0 is inside and x &gt; 0
        // is outside. The coarse test must retain visible/intersecting boxes
        // and reject a box wholly behind the camera. A translated tile covers
        // the rebase/world-transform path used by production presentation.
        Plane plane = new(Vector3.Right, 0);
        Plane[] planes = new[] { plane };
        Aabb visible = new(new Vector3(-40, -2, -2), new Vector3(4, 4, 4));
        Aabb behindCamera = new(new Vector3(20, -2, -2), new Vector3(4, 4, 4));
        Aabb intersecting = new(new Vector3(-2, -2, -2), new Vector3(4, 4, 4));
        Aabb rebased = new Transform3D(Basis.Identity, new Vector3(-120, 11, 37))
            * new Aabb(new Vector3(100, -1, -1), new Vector3(4, 2, 2));
        return AabbIntersectsFrustum(visible, planes)
            && !AabbIntersectsFrustum(behindCamera, planes)
            && AabbIntersectsFrustum(intersecting, planes)
            && AabbIntersectsFrustum(rebased, planes);
    }

    internal void ConfigureGpuRegistrationFixture(int emptyTiles, int populatedTiles)
    {
        EnsureResources(); ClearTiles(); _tiles.Clear(); _uploads.Clear(); _uploadQueued.Clear();
        EnableDistanceLod = false;
        for (int i = 0; i < emptyTiles + populatedTiles; i++)
        {
            bool populated = i >= emptyTiles;
            var key = new RingGrassTileKey(i, 0);
            float[] rows = populated ? FixtureRows(100 + i) : Array.Empty<float>();
            var data = new RingGrassTileData(key, new RingGrassTileBounds(i * 16, i * 16 + 16, 0, 16), 0, rows,
                new Aabb(new Vector3(-1, -1, -1), new Vector3(2, 2, 2)), (ulong)(i + 1));
            _tiles.Add(key, new Resident { Data = data, NearWanted = true });
            _wanted.Add(key);
            ResidentGrassInstanceCount += data.InstanceCount;
            _uploads.Enqueue(key); _uploadQueued.Add(key);
        }
        int uploadLimit = MaximumTileUploadsPerFrame;
        float uploadMilliseconds = MaximumTileUploadMilliseconds;
        MaximumTileUploadsPerFrame = emptyTiles + populatedTiles;
        MaximumTileUploadMilliseconds = 1000;
        Install();
        MaximumTileUploadsPerFrame = uploadLimit;
        MaximumTileUploadMilliseconds = uploadMilliseconds;
    }
    internal void StepGpuRegistrationFixture() => RequestGpuCulling();
    internal void EnableGpuRegistrationFixtureIntermediate()
    {
        EnableDistanceLod = true; EnsureResources();
        foreach (var pair in _tiles)
        {
            Resident resident = pair.Value;
            if (resident.Data.InstanceCount == 0 || resident.IntermediateRenderer != null || _intermediateMesh == null || _intermediateMaterial == null) continue;
            MultiMeshInstance3D renderer = resident.IntermediateRenderer = CreateRenderer(pair.Key, _intermediateMesh, _intermediateMaterial, "Intermediate");
            renderer.Multimesh!.InstanceCount = resident.Data.InstanceCount; renderer.Multimesh.VisibleInstanceCount = resident.Data.InstanceCount;
            renderer.Multimesh.Buffer = resident.Data.Buffer; renderer.Multimesh.CustomAabb = LodAabb(resident.Data.LocalAabb); resident.IntermediateWanted = true;
        }
    }
    internal int GpuRegistrationFixtureBatchId(long around, bool intermediate)
        => _tiles.TryGetValue(new RingGrassTileKey(around, 0), out Resident? resident) ? intermediate ? resident.GpuIntermediateBatch : resident.GpuNearBatch : -1;
    private static float[] FixtureRows(float marker)
    {
        var rows = new float[RingGrassTileData.FloatsPerInstance];
        rows[0] = rows[5] = rows[10] = 1; rows[3] = marker;
        return rows;
    }
    internal bool TryGetGpuGrassSourceForTests(long around, long across, out float[] rows)
    {
        rows = Array.Empty<float>();
        if (!_tiles.TryGetValue(new RingGrassTileKey(around, across), out Resident? resident)) return false;
        rows = (float[])resident.Data.Buffer.Clone();
        return true;
    }
    internal bool IsGpuGrassBatchReadyForTests(long around, long across, bool intermediate)
    {
        if (_gpuCompute == null || !_tiles.TryGetValue(new RingGrassTileKey(around, across), out Resident? resident)) return false;
        int id = intermediate ? resident.GpuIntermediateBatch : resident.GpuNearBatch;
        return id >= 0 && _gpuCompute.IsBatchReady(id);
    }

    internal bool RequestGpuGrassReadbackForTests(long around, long across, bool intermediate, Action<uint[], float[]> callback)
    {
        if (_gpuCompute == null || !_tiles.TryGetValue(new RingGrassTileKey(around, across), out Resident? resident)) return false;
        int id = intermediate ? resident.GpuIntermediateBatch : resident.GpuNearBatch;
        if (id < 0) return false;
        _gpuCompute.RequestReadbackForTests(id, callback);
        return true;
    }
}
