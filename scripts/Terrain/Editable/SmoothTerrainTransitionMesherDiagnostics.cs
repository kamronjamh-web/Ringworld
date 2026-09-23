using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Small deterministic contract suite; invoked by transition lifecycle smoke coverage.</summary>
public static class SmoothTerrainTransitionMesherDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        var baseline = new TransitionBaseline();
        var field = new SmoothTerrainVolumetricDensitySnapshot(baseline,
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 1, baseline.AroundLength));
        // Alignment and foreign-grid rejection do not depend on the optional DLL.
        try { SmoothTerrainTransitionMesher.Validate(new RingTerrainSampleCoordinate(1, 0, 0), 2, new RingTerrainSampleCoordinate(0, 0, 0), SmoothTerrainTransitionFace.NegativeAround, out _, out _, out _); summary = "misaligned fine origin was accepted"; return false; }
        catch (ArgumentException) { }
        try { SmoothTerrainTransitionMesher.Validate(new RingTerrainSampleCoordinate(0, 0, 0), 3, new RingTerrainSampleCoordinate(0, 0, 0), SmoothTerrainTransitionFace.NegativeAround, out _, out _, out _); summary = "non-power-of-two spacing was accepted"; return false; }
        catch (ArgumentOutOfRangeException) { }
        try { SmoothTerrainTransitionMesher.Validate(new RingTerrainSampleCoordinate(0, -64, 0), 2, new RingTerrainSampleCoordinate(256, -64, 0), SmoothTerrainTransitionFace.NegativeAround, out _, out _, out _); summary = "foreign coarse lattice was accepted"; return false; }
        catch (ArgumentException) { }
        try { SmoothTerrainTransitionMesher.BuildFaces(field, new RingTerrainSampleCoordinate(0, 0, 0), 1, new RingTerrainSampleCoordinate(0, 0, 0), 0); summary = "empty face mask was accepted"; return false; }
        catch (ArgumentOutOfRangeException) { }
        try { SmoothTerrainTransitionMesher.BuildFaces(field, new RingTerrainSampleCoordinate(0, 0, 0), 1, new RingTerrainSampleCoordinate(0, 0, 0), 0x40); summary = "unknown face-mask bit was accepted"; return false; }
        catch (ArgumentOutOfRangeException) { }
        if (!SmoothTerrainTransitionMesher.IsNativeAvailable) { summary = "alignment contracts passed; native transition backend unavailable"; return true; }
        int triangles = 0;
        foreach (int spacing in new[] { 1, 2, 32 })
        foreach (SmoothTerrainTransitionFace face in Enum.GetValues<SmoothTerrainTransitionFace>())
        {
            // Core crosses vertical zero, so this also exercises negative-Y
            // sampling while the diagonal plane intersects every orientation.
            var origin = new RingTerrainSampleCoordinate(0, -16 * spacing, 0);
            SmoothTerrainMeshData mesh = SmoothTerrainTransitionMesher.Build(field, origin, spacing, origin, face);
            if (mesh.CollisionFaces.Length != 0 || mesh.Vertices.Length != mesh.Normals.Length || mesh.Vertices.Length != mesh.Materials.Length || mesh.Indices.Length % 3 != 0)
            { summary = $"invalid transition mesh at {spacing}m face {face}"; return false; }
            foreach (int index in mesh.Indices) if ((uint)index >= mesh.Vertices.Length) { summary = $"out-of-range transition index at {spacing}m face {face}"; return false; }
            foreach (int material in mesh.Materials) if (material is < 0 or > byte.MaxValue) { summary = "native material id was not preserved"; return false; }
            triangles += mesh.Indices.Length / 3;
        }
        SmoothTerrainMeshData combined = SmoothTerrainTransitionMesher.BuildFaces(field,
            new RingTerrainSampleCoordinate(0, -16, 0), 1, new RingTerrainSampleCoordinate(0, -16, 0), 0x3f);
        if (combined.CollisionFaces.Length != 0 || combined.Indices.Length == 0)
        { summary = "batched transition face build did not combine exposed faces"; return false; }
        SmoothTerrainMeshData uniform = SmoothTerrainTransitionMesher.BuildFaces(field,
            new RingTerrainSampleCoordinate(0, -4096, 0), 1, new RingTerrainSampleCoordinate(0, -4096, 0), 0x3f);
        if (uniform.Indices.Length != 0 || uniform.CollisionFaces.Length != 0)
        { summary = "uniform transition lattices were not rejected"; return false; }
        var reusedCoordinate = new SmoothTerrainChunkCoordinate(0, -1, 0);
        RingTerrainSampleCoordinate reusedOrigin = reusedCoordinate.Origin;
        var reusedKey = new SmoothTerrainVolumetricDensityCacheKey(
            field.Identity,
            reusedOrigin.AroundVoxel - 2,
            reusedOrigin.VerticalVoxel - 2,
            reusedOrigin.AcrossVoxel - 2,
            SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo * 2,
            1);
        SmoothTerrainRegularChunkCapture reusedCapture = field.CaptureRegularChunk(
            reusedKey,
            reusedCoordinate);
        if (reusedCapture.Snapshot == null)
        { summary = "fine-lattice reuse fixture was unexpectedly uniform"; return false; }
        field.ResetTransitionLatticeCacheForDiagnostics();
        field.ResetRegularChunkCacheForDiagnostics();
        SmoothTerrainMeshData directFine = SmoothTerrainTransitionMesher.BuildFaces(
            field, reusedOrigin, 1, reusedOrigin, 0x3f);
        SmoothTerrainVolumetricDensitySnapshot.RegularChunkCacheDiagnostics regularAfterMiss =
            field.CaptureRegularChunkCacheDiagnostics();
        SmoothTerrainChunkSnapshot coalescedParent = field.CaptureChunk(reusedCoordinate);
        SmoothTerrainVolumetricDensitySnapshot.RegularChunkCacheDiagnostics regularAfterHit =
            field.CaptureRegularChunkCacheDiagnostics();
        SmoothTerrainVolumetricDensitySnapshot.TransitionLatticeCacheDiagnostics afterMiss =
            field.CaptureTransitionLatticeCacheDiagnostics();
        SmoothTerrainMeshData reusedFine = SmoothTerrainTransitionMesher.BuildFaces(
            field, reusedOrigin, 1, reusedOrigin, 0x3f, reusedCapture.Snapshot);
        SmoothTerrainVolumetricDensitySnapshot.TransitionLatticeCacheDiagnostics afterHit =
            field.CaptureTransitionLatticeCacheDiagnostics();
        if (SmoothTerrainMeshHash.Compute(directFine) != SmoothTerrainMeshHash.Compute(reusedFine))
        { summary = "reused fine transition lattice changed the generated mesh"; return false; }
        if (coalescedParent.Coordinate != reusedCoordinate
            || coalescedParent.Revision.Baseline != field.Identity.BaselineRevision
            || coalescedParent.Revision.Edits != field.Identity.EditRevision
            || !coalescedParent.Densities.SequenceEqual(reusedCapture.Snapshot.Densities)
            || !coalescedParent.Materials.SequenceEqual(reusedCapture.Snapshot.Materials))
        { summary = "coalesced 37^3 parent changed the authoritative transition samples"; return false; }
        if (regularAfterMiss.Misses != 1 || regularAfterMiss.Hits != 0 || regularAfterMiss.Count != 1
            || regularAfterHit.Misses != 1 || regularAfterHit.Hits != 1 || regularAfterHit.Count != 1
            || regularAfterHit.RetainedBytes <= 0 || regularAfterHit.Count > regularAfterHit.Capacity)
        { summary = $"regular/transition lattice coalescing contract failed: first={regularAfterMiss}, second={regularAfterHit}"; return false; }
        if (afterMiss.Misses != 1 || afterMiss.Hits != 0 || afterMiss.Count != 1
            || afterHit.Misses != 1 || afterHit.Hits != 1 || afterHit.Count != 1
            || afterHit.RetainedBytes <= 0 || afterHit.Count > afterHit.Capacity)
        { summary = $"coarse transition lattice cache contract failed: first={afterMiss}, second={afterHit}"; return false; }
        var capacityField = new SmoothTerrainVolumetricDensitySnapshot(
            new TransitionBaseline(),
            new SmoothTerrainEditSnapshot(Array.Empty<SmoothTerrainEditCommand>(), 3, baseline.AroundLength));
        int regularCapacity = capacityField.CaptureRegularChunkCacheDiagnostics().Capacity;
        for (int index = 0; index <= regularCapacity; index++)
            capacityField.CaptureChunk(new SmoothTerrainChunkCoordinate(index, -100, 1));
        SmoothTerrainVolumetricDensitySnapshot.RegularChunkCacheDiagnostics bounded =
            capacityField.CaptureRegularChunkCacheDiagnostics();
        long bytesPerParent = (long)SmoothTerrainChunkSnapshot.SampleCount
            * SmoothTerrainChunkSnapshot.SampleCount
            * SmoothTerrainChunkSnapshot.SampleCount
            * (sizeof(float) + sizeof(int));
        if (bounded.Count != regularCapacity || bounded.Misses != regularCapacity + 1
            || bounded.Hits != 0 || bounded.Evictions != 1
            || bounded.RetainedBytes != regularCapacity * bytesPerParent)
        { summary = $"regular parent cache exceeded its strict bound: {bounded}"; return false; }
        capacityField.ResetRegularChunkCacheForDiagnostics();
        if (triangles == 0) { summary = "transition fixture emitted no triangles"; return false; }
        summary = $"1->2, 2->4, and 32->64 Transvoxel faces passed ({triangles} triangles; uniform rejected; parent cache {regularAfterHit.Hits}/{regularAfterHit.Misses}, cap {regularCapacity})";
        return true;
    }

    private sealed class TransitionBaseline : ISmoothTerrainBaseline
    {
        public long AroundLength => 8192;
        public int AcrossWidth => 4096;
        public ulong Revision => 987;
        public SmoothTerrainSample Sample(long around, int vertical, int across)
        {
            long wrapped = ((around % AroundLength) + AroundLength) % AroundLength;
            // Diagonal plane intersects every face while preserving a simple,
            // exact coarse sampling relation.
            float density = vertical + across - (wrapped % 128) - 8.5f;
            return new SmoothTerrainSample(density, density < 0 ? 2 : 1, Revision);
        }
    }
}
