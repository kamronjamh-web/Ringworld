using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase1;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Faces of a fine 32-cell core which can meet a 2:1 coarse lattice.</summary>
public enum SmoothTerrainTransitionFace { NegativeAround, PositiveAround, NegativeVertical, PositiveVertical, NegativeAcross, PositiveAcross }

/// <summary>
/// Builds one real Transvoxel transition face from the same immutable density
/// field used by all smooth-terrain LODs. The native tables operate in fine
/// cell units, so this adapter supports every power-of-two physical spacing
/// without duplicating the table implementation.
/// </summary>
public static class SmoothTerrainTransitionMesher
{
    public const int FineCells = 32;
    public const int FineSampleCount = 35; // -1 through +33 relative to core
    public const int CoarseSampleCount = 33;
    // Transition jobs are worker-bound. Four reusable inputs cover the
    // scheduler's normal concurrent work while preventing a fast cave route
    // from retaining an unbounded number of 35^3/33^3 native ABI arrays.
    private const int MaximumPooledInputs = 4;
    private static readonly object InputPoolGate = new();
    private static readonly Stack<TransitionInputBuffers> InputPool = new(MaximumPooledInputs);
    private sealed class TransitionInputBuffers
    {
        public readonly float[] FineDensity = new float[FineSampleCount * FineSampleCount * FineSampleCount];
        public readonly byte[] FineMaterials = new byte[FineSampleCount * FineSampleCount * FineSampleCount];
        public readonly float[] CoarseDensity = new float[CoarseSampleCount * CoarseSampleCount * CoarseSampleCount];
        public readonly byte[] CoarseMaterials = new byte[CoarseSampleCount * CoarseSampleCount * CoarseSampleCount];
    }

    public static bool IsNativeAvailable => VoxelChunkMesherNative.IsSmoothTransitionAvailable;

    public static SmoothTerrainMeshData Build(
        SmoothTerrainVolumetricDensitySnapshot field,
        RingTerrainSampleCoordinate fineCoreOrigin,
        int fineSpacingMeters,
        RingTerrainSampleCoordinate coarseLatticeOrigin,
        SmoothTerrainTransitionFace face)
    {
        if ((uint)face > (uint)SmoothTerrainTransitionFace.PositiveAcross) throw new ArgumentOutOfRangeException(nameof(face));
        return BuildFaces(field, fineCoreOrigin, fineSpacingMeters, coarseLatticeOrigin, (byte)(1 << (int)face));
    }

    /// <summary>
    /// Builds every requested exposed face from one pair of immutable,
    /// authoritative lattices. Bit order is <see cref="SmoothTerrainTransitionFace"/>.
    /// This is the bridge-lifecycle entry point: it avoids recapturing the SDF
    /// six times for a sector with several exposed faces.
    /// </summary>
    public static SmoothTerrainMeshData BuildFaces(
        SmoothTerrainVolumetricDensitySnapshot field,
        RingTerrainSampleCoordinate fineCoreOrigin,
        int fineSpacingMeters,
        RingTerrainSampleCoordinate coarseLatticeOrigin,
        byte faceMask,
        SmoothTerrainChunkSnapshot? capturedFineLattice = null)
    {
        ArgumentNullException.ThrowIfNull(field);
        if ((faceMask & ~0x3f) != 0 || faceMask == 0)
            throw new ArgumentOutOfRangeException(nameof(faceMask), "Face mask must contain one or more of the six transition faces.");
        // Validate all selected enum values before allocating/capturing.
        Validate(fineCoreOrigin, fineSpacingMeters, coarseLatticeOrigin, SmoothTerrainTransitionFace.NegativeAround, out int dx, out int dy, out int dz);
        TransitionInputBuffers input = RentInput();
        try
        {
            CaptureFine(field, fineCoreOrigin, fineSpacingMeters, input.FineDensity, input.FineMaterials, capturedFineLattice);
            CaptureCoarse(field, coarseLatticeOrigin, checked(fineSpacingMeters * 2), input.CoarseDensity, input.CoarseMaterials);
            if (!CanContainSurface(input.FineDensity, input.CoarseDensity))
                return new SmoothTerrainMeshData { Vertices = Array.Empty<Vector3>(), Normals = Array.Empty<Vector3>(), Materials = Array.Empty<int>(), Indices = Array.Empty<int>(), CollisionFaces = Array.Empty<Vector3>() };
            var allVertices = new List<Vector3>();
            var allNormals = new List<Vector3>();
            var allMaterials = new List<int>();
            var allIndices = new List<int>();
            for (int face = 0; face < 6; face++) if ((faceMask & (1 << face)) != 0)
            {
                if (!VoxelChunkMesherNative.TryBuildSmoothTransitionRaw(input.FineDensity, input.FineMaterials, input.CoarseDensity, input.CoarseMaterials,
                        dx, dy, dz, face, out Vector3[] vertices, out Vector3[] normals, out byte[] materials, out int[] indices))
                    throw new InvalidOperationException($"Native Transvoxel transition backend is unavailable: {VoxelChunkMesherNative.Status}");
                int vertexOffset = allVertices.Count;
                for (int i = 0; i < vertices.Length; i++) { allVertices.Add(vertices[i] * fineSpacingMeters); allNormals.Add(normals[i]); allMaterials.Add(materials[i]); }
                for (int i = 0; i < indices.Length; i++) allIndices.Add(checked(indices[i] + vertexOffset));
            }
            return new SmoothTerrainMeshData { Vertices = allVertices.ToArray(), Normals = allNormals.ToArray(), Materials = allMaterials.ToArray(), Indices = allIndices.ToArray(), CollisionFaces = Array.Empty<Vector3>() };
        }
        finally { ReturnInput(input); }
    }

    /// <summary>Validates grid alignment independently of native availability.</summary>
    public static void Validate(RingTerrainSampleCoordinate fineCoreOrigin, int fineSpacingMeters,
        RingTerrainSampleCoordinate coarseLatticeOrigin, SmoothTerrainTransitionFace face,
        out int haloOriginMinusCoarseX, out int haloOriginMinusCoarseY, out int haloOriginMinusCoarseZ)
    {
        if (fineSpacingMeters <= 0 || (fineSpacingMeters & (fineSpacingMeters - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(fineSpacingMeters), "Fine spacing must be a positive power of two.");
        if ((uint)face > (uint)SmoothTerrainTransitionFace.PositiveAcross)
            throw new ArgumentOutOfRangeException(nameof(face));
        int coarseSpacing = checked(fineSpacingMeters * 2);
        ValidateAligned(fineCoreOrigin.AroundVoxel, fineSpacingMeters, "fine around");
        ValidateAligned(fineCoreOrigin.VerticalVoxel, fineSpacingMeters, "fine vertical");
        ValidateAligned(fineCoreOrigin.AcrossVoxel, fineSpacingMeters, "fine across");
        ValidateAligned(coarseLatticeOrigin.AroundVoxel, coarseSpacing, "coarse around");
        ValidateAligned(coarseLatticeOrigin.VerticalVoxel, coarseSpacing, "coarse vertical");
        ValidateAligned(coarseLatticeOrigin.AcrossVoxel, coarseSpacing, "coarse across");
        haloOriginMinusCoarseX = FineUnits(checked(fineCoreOrigin.AroundVoxel - (long)fineSpacingMeters - coarseLatticeOrigin.AroundVoxel), fineSpacingMeters);
        haloOriginMinusCoarseY = FineUnits(checked((long)fineCoreOrigin.VerticalVoxel - fineSpacingMeters - coarseLatticeOrigin.VerticalVoxel), fineSpacingMeters);
        haloOriginMinusCoarseZ = FineUnits(checked((long)fineCoreOrigin.AcrossVoxel - fineSpacingMeters - coarseLatticeOrigin.AcrossVoxel), fineSpacingMeters);
        // The native table reads points 1..33 from each fine axis. Keeping all
        // of those points inside the supplied 33^3 coarse lattice makes the
        // C++ index calculation safe even for negative Y and wrapped around X.
        ValidateCoarseReach(haloOriginMinusCoarseX, "around");
        ValidateCoarseReach(haloOriginMinusCoarseY, "vertical");
        ValidateCoarseReach(haloOriginMinusCoarseZ, "across");
    }

    private static void CaptureFine(
        SmoothTerrainVolumetricDensitySnapshot field,
        RingTerrainSampleCoordinate origin,
        int spacing,
        float[] density,
        byte[] materials,
        SmoothTerrainChunkSnapshot? captured)
    {
        if (captured != null)
        {
            RingTerrainSampleCoordinate capturedOrigin = captured.Coordinate.Origin;
            if (capturedOrigin != origin
                || captured.Revision.Baseline != field.Identity.BaselineRevision
                || captured.Revision.Edits != field.Identity.EditRevision)
                throw new ArgumentException("Captured fine transition lattice belongs to another sector or density field.", nameof(captured));
            for (int z = 0; z < FineSampleCount; z++)
            for (int y = 0; y < FineSampleCount; y++)
            for (int x = 0; x < FineSampleCount; x++)
            {
                int index = (z * FineSampleCount + y) * FineSampleCount + x;
                density[index] = captured.Density(x - 1, y - 1, z - 1);
                int material = captured.Material(x - 1, y - 1, z - 1);
                if ((uint)material > byte.MaxValue)
                    throw new InvalidOperationException($"Smooth terrain material id {material} cannot be represented by the native transition ABI.");
                materials[index] = (byte)material;
            }
            return;
        }
        // An aligned 1 m transition covers a strict subset of the owning
        // parent's 37^3 lattice. The field coalescer either reuses that
        // immutable capture or lets the first transition create it once.
        if (spacing == 1 && field.TryCopyCachedFineTransitionLattice(origin, density, materials))
            return;
        var key=new SmoothTerrainVolumetricDensityCacheKey(field.Identity,
            checked(origin.AroundVoxel - spacing), checked(origin.VerticalVoxel - spacing), checked(origin.AcrossVoxel - spacing), 34, spacing);
        field.CaptureNativeLattice(key,density,materials);
    }
    private static void CaptureCoarse(SmoothTerrainVolumetricDensitySnapshot field, RingTerrainSampleCoordinate origin, int spacing, float[] density, byte[] materials)
    {
        var key=new SmoothTerrainVolumetricDensityCacheKey(field.Identity,
            origin.AroundVoxel, origin.VerticalVoxel, origin.AcrossVoxel, 32, spacing);
        field.CaptureCachedTransitionLattice(key,density,materials);
    }
    private static TransitionInputBuffers RentInput()
    {
        lock (InputPoolGate) return InputPool.Count > 0 ? InputPool.Pop() : new TransitionInputBuffers();
    }
    private static void ReturnInput(TransitionInputBuffers input)
    {
        lock (InputPoolGate) if (InputPool.Count < MaximumPooledInputs) InputPool.Push(input);
    }
    private static bool CanContainSurface(float[] fineDensity, float[] coarseDensity)
    {
        bool negative=false,positive=false;
        foreach(float density in fineDensity){negative|=density<0;positive|=density>=0;if(negative&&positive)return true;}
        foreach(float density in coarseDensity){negative|=density<0;positive|=density>=0;if(negative&&positive)return true;}
        return false;
    }
    private static void ValidateAligned(long value, int spacing, string axis) { if (value % spacing != 0) throw new ArgumentException($"{axis} origin must align to its sample spacing."); }
    private static int FineUnits(long meters, int spacing)
    {
        if (meters % spacing != 0) throw new ArgumentException("Fine and coarse origins do not share a fine-cell grid.");
        long units = meters / spacing;
        if (units is < int.MinValue or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(meters));
        return (int)units;
    }
    private static void ValidateCoarseReach(int delta, string axis)
    {
        if (delta < -1 || delta > 31) throw new ArgumentException($"Coarse {axis} lattice does not cover the fine transition face.");
    }
}
