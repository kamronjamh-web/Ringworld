using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Immutable command format for the replacement natural-terrain edit journal.</summary>
public readonly record struct SmoothTerrainEditCommand(
    int Version,
    ulong CommandId,
    long Sequence,
    bool AddsMatter,
    long Around,
    int Vertical,
    int Across,
    float Radius,
    int Material)
{
    public const int CurrentVersion = 1;
    public bool IsValid => Version == CurrentVersion && Sequence >= 0 && float.IsFinite(Radius) && Radius > 0.0f && Material >= 0;
}

/// <summary>Natural-only material classes. Construction is intentionally absent.</summary>
public enum SmoothTerrainMaterial { Bedrock = 0, Rock = 1, Soil = 2 }
public readonly record struct SmoothTerrainRevision(ulong Baseline, ulong Edits)
{
    public ulong Value => Mix(Baseline, Edits);
    private static ulong Mix(ulong a, ulong b) { unchecked { return (a ^ 0x9E3779B97F4A7C15UL) * 1099511628211UL ^ b; } }
}

/// <summary>Locale-independent FNV-1a identity for a fully transformed mesh result.</summary>
public static class SmoothTerrainMeshHash
{
    public static ulong Compute(SmoothTerrainMeshData mesh)
    {
        const ulong offset = 14695981039346656037UL, prime = 1099511628211UL;
        ulong hash = offset;
        void Add(int value) { unchecked { hash ^= (uint)value; hash *= prime; } }
        foreach (Vector3 v in mesh.Vertices) { Add(BitConverter.SingleToInt32Bits(v.X)); Add(BitConverter.SingleToInt32Bits(v.Y)); Add(BitConverter.SingleToInt32Bits(v.Z)); }
        foreach (Vector3 n in mesh.Normals) { Add(BitConverter.SingleToInt32Bits(n.X)); Add(BitConverter.SingleToInt32Bits(n.Y)); Add(BitConverter.SingleToInt32Bits(n.Z)); }
        foreach (int m in mesh.Materials) Add(m);
        foreach (int i in mesh.Indices) Add(i);
        foreach (Vector3 f in mesh.CollisionFaces) { Add(BitConverter.SingleToInt32Bits(f.X)); Add(BitConverter.SingleToInt32Bits(f.Y)); Add(BitConverter.SingleToInt32Bits(f.Z)); }
        return hash;
    }
}

public readonly record struct SmoothTerrainChunkCoordinate(long AroundChunk, int VerticalChunk, int AcrossChunk)
{
    public const int Cells = 32;
    public RingTerrainSampleCoordinate Origin => new(AroundChunk * Cells, VerticalChunk * Cells, AcrossChunk * Cells);
}

public readonly record struct SmoothTerrainSample(float Density, int Material, ulong Revision);

/// <summary>
/// Immutable 32-cell snapshot. Samples run from -2 through Cells + 2 on every
/// axis, allowing central differences at every lattice point and ghost edge
/// used by the regular-cell mesher.
/// </summary>
public sealed class SmoothTerrainChunkSnapshot
{
    public const int Cells = 32;
    public const int Halo = 2;
    public const int SampleCount = Cells + 1 + Halo * 2;
    private readonly float[] _densities;
    private readonly int[] _materials;

    public SmoothTerrainChunkSnapshot(SmoothTerrainChunkCoordinate coordinate, SmoothTerrainRevision revision, float[] densities, int[] materials)
        : this(coordinate, revision, densities, materials, takeOwnership: false)
    {
    }

    private SmoothTerrainChunkSnapshot(SmoothTerrainChunkCoordinate coordinate, SmoothTerrainRevision revision, float[] densities, int[] materials, bool takeOwnership)
    {
        if (densities.Length != SampleCount * SampleCount * SampleCount || materials.Length != densities.Length)
            throw new ArgumentException("Snapshot arrays must include the complete 37^3 lattice.");
        Coordinate = coordinate;
        Revision = revision;
        _densities = takeOwnership ? densities : (float[])densities.Clone();
        _materials = takeOwnership ? materials : (int[])materials.Clone();
    }
    public SmoothTerrainChunkCoordinate Coordinate { get; }
    public SmoothTerrainRevision Revision { get; }
    public ReadOnlySpan<float> Densities => _densities;
    public ReadOnlySpan<int> Materials => _materials;
    public float Density(int x, int y, int z) => _densities[Index(x, y, z)];
    public int Material(int x, int y, int z) => _materials[Index(x, y, z)];
    public static int Index(int x, int y, int z) => (z + Halo) * SampleCount * SampleCount + (y + Halo) * SampleCount + x + Halo;
    internal static SmoothTerrainChunkSnapshot FromOwnedArrays(SmoothTerrainChunkCoordinate coordinate, SmoothTerrainRevision revision, float[] densities, int[] materials)
        => new(coordinate, revision, densities, materials, takeOwnership: true);
}

/// <summary>Read-only baseline used by snapshot capture. RingTerrainSampler is adapted below.</summary>
public interface ISmoothTerrainBaseline
{
    long AroundLength { get; }
    int AcrossWidth { get; }
    ulong Revision { get; }
    SmoothTerrainSample Sample(long around, int vertical, int across);
}

public sealed class RingTerrainSmoothBaseline : ISmoothTerrainBaseline
{
    private readonly RingTerrainSampler _sampler;
    private readonly int _structuralBottomVertical;
    private readonly int _terrainBottomVertical;
    private readonly int _protectedRoofThickness;
    private readonly int _lowerHullTopVertical;
    private readonly int _structuralWallTopVertical;
    private readonly bool _enableDesignedCaves;
    private readonly ulong _revision;
    private readonly ulong _preDesignedCaveRevision;
    private readonly ulong _legacyRevision;
    private readonly ulong _preCavityLegacyRevision;
    private const int StructuralSidewallThickness = SmoothTerrainChunkSnapshot.Cells;
    public RingTerrainSmoothBaseline(RingTerrainSampler sampler,int structuralBottomVertical=int.MinValue,int terrainBottomVertical=int.MinValue,int protectedRoofThickness=0,int lowerHullTopVertical=int.MinValue,int structuralWallTopVertical=int.MinValue,bool enableDesignedCaves=false)
    {
        _sampler=sampler??throw new ArgumentNullException(nameof(sampler));
        _structuralBottomVertical=structuralBottomVertical;
        _terrainBottomVertical=terrainBottomVertical;
        _protectedRoofThickness=protectedRoofThickness;
        _lowerHullTopVertical=lowerHullTopVertical;
        _enableDesignedCaves=enableDesignedCaves;
        _structuralWallTopVertical=structuralWallTopVertical!=int.MinValue ? structuralWallTopVertical : terrainBottomVertical!=int.MinValue ? checked(terrainBottomVertical+protectedRoofThickness) : lowerHullTopVertical;
        // The structural boundary changes the signed field topology, so it
        // must be part of the snapshot identity rather than an incidental
        // runtime setting.
        unchecked
        {
            _preCavityLegacyRevision=(sampler.TerrainDefinitionHash^(uint)structuralBottomVertical)*1099511628211UL;
            int lowerHullThickness=lowerHullTopVertical==int.MinValue||structuralBottomVertical==int.MinValue ? 0 : lowerHullTopVertical-structuralBottomVertical;
            _legacyRevision=new RingVerticalDomainConfiguration(terrainBottomVertical,protectedRoofThickness,structuralBottomVertical,lowerHullThickness).MixDefinitionHash(_preCavityLegacyRevision);
            // The finite outer walls change the signed-field topology. Keep
            // the immediately preceding cavity identity explicitly migratable,
            // rather than silently treating it as the same terrain definition.
            _preDesignedCaveRevision=MixSidewallDefinition(_legacyRevision, _sampler.RingAcrossWidthMeters, StructuralSidewallThickness, _structuralWallTopVertical);
            _revision=_enableDesignedCaves
                ? MixDesignedCaveDefinition(_preDesignedCaveRevision,_sampler.CaveTopologyDensityComposer.Identity)
                : _preDesignedCaveRevision;
        }
    }
    public long AroundLength => _sampler.AroundRingVoxelLength;
    public int AcrossWidth => _sampler.RingAcrossWidthMeters;
    public ulong Revision => _revision;
    public bool DesignedCavesEnabled => _enableDesignedCaves;
    public ulong PreDesignedCaveRevision => _preDesignedCaveRevision;
    /// <summary>Pre-sidewall cavity baseline identity accepted only to migrate existing smooth journals.</summary>
    public ulong LegacyRevision => _legacyRevision;
    /// <summary>Original pre-cavity identity retained for the already-supported migration path.</summary>
    public ulong PreCavityLegacyRevision => _preCavityLegacyRevision;
    public ulong[] AcceptedLegacyRevisions => _enableDesignedCaves
        ? new[] { _preDesignedCaveRevision, _legacyRevision, _preCavityLegacyRevision }
        : new[] { _legacyRevision, _preCavityLegacyRevision };
    // FastNoiseLite resources are deliberately protected by the sampler's
    // instance lock.  Volumetric workers therefore need an equivalent sampler
    // instance rather than sharing this baseline's sampler and serializing all
    // density queries through one lock.
    internal RingTerrainSmoothBaseline CreateIndependentWorkerCopy() => new(
        _sampler.CreateIndependentCopy(),
        _structuralBottomVertical,
        _terrainBottomVertical,
        _protectedRoofThickness,
        _lowerHullTopVertical,
        _structuralWallTopVertical,
        _enableDesignedCaves);
    internal RingTerrainSampler Sampler => _sampler;
    internal void DisposeIndependentWorkerCopy() => _sampler.Dispose();
    public SmoothTerrainSample Sample(long around, int vertical, int across)
    {
        long canonicalAround = RingCoordMath.WrapAround(around, AroundLength);
        if (across < -StructuralSidewallThickness || across > AcrossWidth + StructuralSidewallThickness - 1)
            return new SmoothTerrainSample(1.0f, (int)SmoothTerrainMaterial.Rock, Revision);
        float surface = _sampler.GetSmoothSurfaceHeight(canonicalAround, across);
        return SampleWithKnownSurface(canonicalAround, vertical, across, surface);
    }
    public float GetSmoothSurfaceHeight(long around, int across) =>
        _sampler.GetSmoothSurfaceHeight(RingCoordMath.WrapAround(around, AroundLength), across);
    public CaveTopologyDensityRegion? CaptureDesignedCaveRegion(
        long around,
        long vertical,
        long across,
        long aroundExtent,
        long verticalExtent,
        long acrossExtent)
    {
        if (!_enableDesignedCaves) return null;
        return _sampler.CaveTopologyDensityComposer.CaptureRegion(
            around,
            vertical,
            across,
            aroundExtent,
            verticalExtent,
            acrossExtent);
    }
    public SmoothTerrainSample SampleWithKnownSurface(long around, int vertical, int across, float surface,CaveTopologyDensityRegion? designedCaves=null,bool regionValidated=false)
    {
        long canonicalAround = RingCoordMath.WrapAround(around, AroundLength);
        if (across < -StructuralSidewallThickness || across > AcrossWidth + StructuralSidewallThickness - 1)
            return new SmoothTerrainSample(1.0f, (int)SmoothTerrainMaterial.Rock, Revision);
        float density = _sampler.SampleNaturalTerrainDensityWithSurface(canonicalAround, vertical, across, surface);
        bool caveRaisedDensity=false;
        if(_enableDesignedCaves&&PermitsDesignedCaveDensity(vertical,across))
        {
            designedCaves??=CaptureDesignedCaveRegion(canonicalAround,vertical,across,1,1,1);
            CaveTopologyDensityResult cave=regionValidated?designedCaves!.SampleTerrainContributionValidated(canonicalAround,vertical,across):designedCaves!.SampleTerrainContribution(canonicalAround,vertical,across);
            if(cave.HasContribution&&cave.FinalDensityMeters>density)
            {
                density=(float)cave.FinalDensityMeters;
                caveRaisedDensity=true;
            }
        }
        // Intersect natural terrain with its real underside. This produces a
        // meshable, opaque roof rather than a streaming cutoff.
        if(_terrainBottomVertical!=int.MinValue)
            density=Math.Max(density,_terrainBottomVertical-vertical-.5f);
        // The lower hull is a finite solid slab. Union it with terrain using
        // SDF min, leaving the cavity between the two volumes as actual air.
        if(_structuralBottomVertical!=int.MinValue&&_lowerHullTopVertical!=int.MinValue)
        {
            float hullDensity=Math.Max(_structuralBottomVertical-vertical-.5f,vertical-_lowerHullTopVertical-.5f);
            density=Math.Min(density,hullDensity);
        }
        // Legacy constructor callers retain the previous finite lower-bound behavior.
        else if(_structuralBottomVertical!=int.MinValue)
            density=Math.Max(density,_structuralBottomVertical-vertical-.5f);
        // Match the legacy/proxy contract: one finite 32 m bedrock wall just
        // outside each habitable across edge. Interior remains its ordinary
        // terrain/cavity field and the exterior beyond the slab is air.
        if (_structuralBottomVertical != int.MinValue
            && vertical >= _structuralBottomVertical
            && vertical <= GetStructuralWallTop())
        {
            float sideDensity = across < 0
                ? Math.Max(-StructuralSidewallThickness - across - .5f, across + .5f)
                : across >= AcrossWidth
                    ? Math.Max(AcrossWidth - across - .5f, across - (AcrossWidth + StructuralSidewallThickness - 1) - .5f)
                    : 1.0f;
            density = Math.Min(density, sideDensity);
        }
        int material = (_structuralBottomVertical!=int.MinValue && vertical >= _structuralBottomVertical && vertical <= _lowerHullTopVertical)
            || IsStructuralSidewall(vertical, across)
            || vertical <= _sampler.BedrockY ? (int)SmoothTerrainMaterial.Bedrock
            : caveRaisedDensity ? (int)SmoothTerrainMaterial.Rock
            : density < 0.0f && vertical >= surface - _sampler.SoilDepthVoxels
                ? (int)SmoothTerrainMaterial.Soil : (int)SmoothTerrainMaterial.Rock;
        return new SmoothTerrainSample(density, material, Revision);
    }
    public bool IsStructuralSidewall(int vertical, int across) =>
        _structuralBottomVertical != int.MinValue
        && vertical >= _structuralBottomVertical && vertical <= GetStructuralWallTop()
        && ((across >= -StructuralSidewallThickness && across < 0)
            || (across >= AcrossWidth && across < AcrossWidth + StructuralSidewallThickness));
    private int GetStructuralWallTop() => _structuralWallTopVertical;
    private bool PermitsDesignedCaveDensity(int vertical,int across)
    {
        if (across < 0 || across >= AcrossWidth) return false;
        // StructuralWallTop is the upper extent of the finite exterior
        // sidewalls and normally sits above playable terrain. It is not a
        // cave exclusion boundary: using it here rejected every underground
        // sample in production. Placement and the final terrain/hull SDF keep
        // the roof, hull, and sidewall exclusions intact.
        return _structuralBottomVertical == int.MinValue
            || vertical > _structuralBottomVertical;
    }
    /// <summary>
    /// Proves and exactly classifies a regular-cell region without building its
    /// 37^3 halo lattice. The fast path is deliberately narrow: it is allowed
    /// only when no compiled designed-cave primitive can touch the request and
    /// no natural 3D feature band intersects any central column.
    /// </summary>
    internal bool TryClassifyUniformRegularRegion(
        SmoothTerrainVolumetricDensityCacheKey key,
        CaveTopologyDensityRegion? designedCaves,
        out SmoothTerrainDensityRange range)
    {
        range = default;
        const int halo = SmoothTerrainChunkSnapshot.Halo;
        const int cells = SmoothTerrainChunkSnapshot.Cells;
        const int centralSamples = cells + 1;
        int spacing = key.SampleSpacingMeters;
        bool hasCaveContributors = designedCaves?.HasTerrainContributors == true;
        // The 1 m tier keeps full sampling whenever a cave primitive can touch it.
        if (hasCaveContributors && spacing <= 1)
            return false;
        int minimumVertical = checked(key.VerticalOrigin + halo * spacing);
        int maximumVertical = checked(minimumVertical + cells * spacing);
        int minimumAcross = checked(key.AcrossOrigin + halo * spacing);
        int maximumAcross = checked(minimumAcross + cells * spacing);
        if (minimumAcross < 0 || maximumAcross >= AcrossWidth)
            return false;
        if (hasCaveContributors)
        {
            // Interval proof over the central 33^3 samples. It only classifies:
            // either no cave candidate can reach the wall-influence band (fall
            // through to the exact natural classifier below), or every sample is
            // provably open cave air. Densities are never filled from the bounds.
            long extent = (long)cells * spacing;
            if (!designedCaves!.TryBoundTerrainTopology(
                    checked(key.AroundOrigin + (long)halo * spacing),
                    minimumVertical,
                    minimumAcross,
                    extent,
                    out double caveLower,
                    out double caveUpper))
                return false;
            if (caveUpper > -CaveTopologyDensityComposer.WallProfile.WallInfluenceMeters)
            {
                // Cave air wins max(natural, cave) everywhere; the roof/bottom
                // terms only raise density, and the lower hull (the sole
                // min() term) stays positive above its top. The designed-cave
                // permit requires the box to sit above the structural bottom.
                bool provenEmpty = caveLower >= CaveTopologyDensityComposer.WallProfile.MaximumDisplacementMeters
                    && (_structuralBottomVertical == int.MinValue || minimumVertical > _structuralBottomVertical)
                    && (_lowerHullTopVertical == int.MinValue || minimumVertical > _lowerHullTopVertical);
                if (!provenEmpty)
                    return false;
                range = new SmoothTerrainDensityRange(
                    (float)(caveLower - CaveTopologyDensityComposer.WallProfile.MaximumDisplacementMeters),
                    float.PositiveInfinity,
                    SmoothTerrainDensityClassification.Empty);
                return true;
            }
        }

        Span<float> surfaces = stackalloc float[centralSamples * centralSamples];
        int surfaceIndex = 0;
        for (int z = 0; z < centralSamples; z++)
        {
            int across = checked(minimumAcross + z * spacing);
            for (int x = 0; x < centralSamples; x++)
            {
                long around = RingCoordMath.WrapAround(
                    checked(key.AroundOrigin + (long)(x + halo) * spacing), AroundLength);
                float surface = GetSmoothSurfaceHeight(around, across);
                if (_sampler.NaturalFeatureBandsCanAffect(surface, minimumVertical, maximumVertical))
                    return false;
                surfaces[surfaceIndex++] = surface;
            }
        }

        float minimum = float.PositiveInfinity;
        float maximum = float.NegativeInfinity;
        surfaceIndex = 0;
        for (int z = 0; z < centralSamples; z++)
        {
            int across = checked(minimumAcross + z * spacing);
            for (int x = 0; x < centralSamples; x++)
            {
                float surface = surfaces[surfaceIndex++];
                for (int y = 0; y < centralSamples; y++)
                {
                    int vertical = checked(minimumVertical + y * spacing);
                    float density = vertical - (surface + 0.5f);
                    if (_terrainBottomVertical != int.MinValue)
                        density = Math.Max(density, _terrainBottomVertical - vertical - 0.5f);
                    if (_structuralBottomVertical != int.MinValue && _lowerHullTopVertical != int.MinValue)
                    {
                        float hullDensity = Math.Max(
                            _structuralBottomVertical - vertical - 0.5f,
                            vertical - _lowerHullTopVertical - 0.5f);
                        density = Math.Min(density, hullDensity);
                    }
                    else if (_structuralBottomVertical != int.MinValue)
                    {
                        density = Math.Max(density, _structuralBottomVertical - vertical - 0.5f);
                    }
                    minimum = MathF.Min(minimum, density);
                    maximum = MathF.Max(maximum, density);
                }
            }
        }

        SmoothTerrainDensityClassification classification = minimum >= 0.0f
            ? SmoothTerrainDensityClassification.Empty
            : maximum < 0.0f ? SmoothTerrainDensityClassification.Solid : SmoothTerrainDensityClassification.Mixed;
        if (classification == SmoothTerrainDensityClassification.Mixed)
            return false;
        range = new SmoothTerrainDensityRange(minimum, maximum, classification);
        return true;
    }
    private static ulong MixSidewallDefinition(ulong definition, int acrossWidth, int thickness, int wallTop)
    {
        unchecked
        {
            ulong hash = definition ^ 0x5349444557414C4CUL; // "SIDEWALL"
            hash = (hash * 1099511628211UL) ^ (uint)acrossWidth;
            hash = (hash * 1099511628211UL) ^ (uint)thickness;
            return (hash * 1099511628211UL) ^ (uint)wallTop;
        }
    }
    private static ulong MixDesignedCaveDefinition(ulong definition,ulong caveIdentity)
    {
        unchecked
        {
            ulong hash=(definition^0x4445534341564531UL)*1099511628211UL;
            return (hash^caveIdentity)*1099511628211UL;
        }
    }
}

/// <summary>Deterministic immutable stamp set with a bounded uniform-grid query.</summary>
public sealed class SmoothTerrainEditSnapshot
{
    private const int BucketSize = 32;
    public const float MaximumStampRadiusMeters = 256.0f;
    private readonly SmoothTerrainEditCommand[] _ordered;
    private readonly Dictionary<(long X, int Y, int Z), int[]> _buckets;
    private readonly long _aroundLength;
    public SmoothTerrainEditSnapshot(IEnumerable<SmoothTerrainEditCommand> commands, ulong revision, long aroundLength)
    {
        if (aroundLength <= 0) throw new ArgumentOutOfRangeException(nameof(aroundLength));
        _aroundLength = aroundLength;
        Revision = revision;
        var sorted = new List<SmoothTerrainEditCommand>();
        var ids = new HashSet<ulong>();
        foreach (SmoothTerrainEditCommand command in commands)
        {
            if (!command.IsValid
                || command.Radius > MaximumStampRadiusMeters
                || command.Radius > aroundLength * 0.5f
                || !Enum.IsDefined((SmoothTerrainMaterial)command.Material))
                throw new ArgumentException("Invalid smooth terrain stamp.", nameof(commands));
            if (!ids.Add(command.CommandId)) throw new ArgumentException("Duplicate smooth terrain command identity.", nameof(commands));
            sorted.Add(command with { Around = RingCoordMath.WrapAround(command.Around, aroundLength) });
        }
        sorted.Sort(static (a, b) => a.Sequence != b.Sequence ? a.Sequence.CompareTo(b.Sequence) : a.CommandId.CompareTo(b.CommandId));
        _ordered = sorted.ToArray();
        var mutable = new Dictionary<(long, int, int), List<int>>();
        for (int i = 0; i < _ordered.Length; i++)
        {
            SmoothTerrainEditCommand s = _ordered[i]; int r = (int)MathF.Ceiling(s.Radius);
            AddToBuckets(s.Around);
            // Mirror only the small edge range so a stamp crossing the ring seam
            // is found from either canonical side without a global scan.
            if (s.Around - r < 0) AddToBuckets(s.Around + aroundLength);
            if (s.Around + r >= aroundLength) AddToBuckets(s.Around - aroundLength);
            void AddToBuckets(long centerAround)
            {
                long minX = FloorDiv(centerAround - r); long maxX = FloorDiv(centerAround + r);
                int minY = FloorDiv(s.Vertical - r); int maxY = FloorDiv(s.Vertical + r);
                int minZ = FloorDiv(s.Across - r); int maxZ = FloorDiv(s.Across + r);
                for (long x = minX; x <= maxX; x++) for (int y = minY; y <= maxY; y++) for (int z = minZ; z <= maxZ; z++)
                    (mutable.TryGetValue((x, y, z), out List<int>? list) ? list : mutable[(x, y, z)] = new List<int>()).Add(i);
            }
        }
        _buckets = new Dictionary<(long, int, int), int[]>(mutable.Count);
        foreach (var pair in mutable)
        {
            // Commands are inserted in sorted index order. Mirroring at the ring
            // seam can insert the same index twice into one bucket, so compact
            // adjacent duplicates once when the immutable snapshot is built.
            var compact = new List<int>(pair.Value.Count);
            int previous = -1;
            foreach (int index in pair.Value)
            {
                if (index == previous) continue;
                compact.Add(index);
                previous = index;
            }
            _buckets.Add(pair.Key, compact.ToArray());
        }
    }
    public ulong Revision { get; }
    public bool IsEmpty => _ordered.Length == 0;
    public ReadOnlySpan<SmoothTerrainEditCommand> Ordered => _ordered;
    public void Query(long around, int vertical, int across, List<SmoothTerrainEditCommand> output)
    {
        output.Clear();
        around = RingCoordMath.WrapAround(around, _aroundLength);
        if (_buckets.TryGetValue((FloorDiv(around), FloorDiv(vertical), FloorDiv(across)), out int[]? indices))
            foreach (int index in indices) output.Add(_ordered[index]);
    }
    internal static bool CommandOccupiesSampleBucket(SmoothTerrainEditCommand command,long around,int vertical,int across,long aroundLength)
    {
        command=command with { Around=RingCoordMath.WrapAround(command.Around,aroundLength) };
        around=RingCoordMath.WrapAround(around,aroundLength);
        int radius=(int)MathF.Ceiling(command.Radius);
        int y=FloorDiv(vertical),z=FloorDiv(across);
        if(y<FloorDiv(command.Vertical-radius)||y>FloorDiv(command.Vertical+radius)
            ||z<FloorDiv(command.Across-radius)||z>FloorDiv(command.Across+radius))return false;
        long x=FloorDiv(around);
        if(InRange(command.Around))return true;
        if(command.Around-radius<0&&InRange(command.Around+aroundLength))return true;
        return command.Around+radius>=aroundLength&&InRange(command.Around-aroundLength);
        bool InRange(long center)=>x>=FloorDiv(center-radius)&&x<=FloorDiv(center+radius);
    }
    /// <summary>Composes this immutable ordered journal at a continuous canonical point.</summary>
    public SmoothTerrainSample SampleContinuous(ISmoothTerrainBaseline baseline,double around,double vertical,double across,List<SmoothTerrainEditCommand> scratch)
    {
        long x0=(long)Math.Floor(around);int y0=(int)Math.Floor(vertical),z0=(int)Math.Floor(across);float fx=(float)(around-x0),fy=(float)(vertical-y0),fz=(float)(across-z0);float density=0;int material=(int)SmoothTerrainMaterial.Rock;float best=float.NegativeInfinity;
        for(int z=0;z<2;z++)for(int y=0;y<2;y++)for(int x=0;x<2;x++){float w=(x==0?1-fx:fx)*(y==0?1-fy:fy)*(z==0?1-fz:fz);var s=baseline.Sample(RingCoordMath.WrapAround(x0+x,baseline.AroundLength),y0+y,z0+z);density+=s.Density*w;if(w>best){best=w;material=s.Material;}}
        Query(RingCoordMath.WrapAround(x0,baseline.AroundLength),y0,z0,scratch);
        foreach(var stamp in scratch){double dx=around-stamp.Around;dx%=baseline.AroundLength;if(dx>baseline.AroundLength*.5)dx-=baseline.AroundLength;if(dx<-baseline.AroundLength*.5)dx+=baseline.AroundLength;float sdf=MathF.Sqrt((float)(dx*dx)+(float)((vertical-stamp.Vertical)*(vertical-stamp.Vertical))+(float)((across-stamp.Across)*(across-stamp.Across)))-stamp.Radius;if(stamp.AddsMatter){if(sdf<density){density=sdf;material=stamp.Material;}}else density=MathF.Max(density,-sdf);}
        return new SmoothTerrainSample(density,material,Revision);
    }
    private static long FloorDiv(long value) => value >= 0 ? value / BucketSize : (value - (BucketSize - 1)) / BucketSize;
    private static int FloorDiv(int value) => value >= 0 ? value / BucketSize : (value - (BucketSize - 1)) / BucketSize;
}

/// <summary>
/// Versioned identity for the single signed-density field consumed by every
/// future volumetric cave LOD. BaselineRevision already includes the terrain
/// generator and vertical-domain/structural-boundary definition, while
/// EditRevision selects the immutable ordered journal composition.
/// </summary>
public readonly record struct SmoothTerrainVolumetricDensityIdentity(
    int ContractVersion,
    ulong BaselineRevision,
    ulong EditRevision,
    long AroundLength,
    int AcrossWidth)
{
    public const int CurrentContractVersion = 1;
    public bool IsValid => ContractVersion == CurrentContractVersion
        && BaselineRevision != 0 && AroundLength > 0 && AcrossWidth > 0;
    public ulong Version => new SmoothTerrainRevision(BaselineRevision, EditRevision).Value;
}

/// <summary>Cache identity for a sampling lattice. LOD consumers must vary
/// only lattice resolution/region, never choose a separate density source.</summary>
public readonly record struct SmoothTerrainVolumetricDensityCacheKey(
    SmoothTerrainVolumetricDensityIdentity Field,
    long AroundOrigin,
    int VerticalOrigin,
    int AcrossOrigin,
    int CellsPerAxis,
    int SampleSpacingMeters)
{
    public const int MaximumCellsPerAxis = 64;
    public bool IsValid => Field.IsValid
        && CellsPerAxis is > 0 and <= MaximumCellsPerAxis
        && SampleSpacingMeters > 0
        && (SampleSpacingMeters & (SampleSpacingMeters - 1)) == 0;
}

/// <summary>Immutable lattice captured from the authoritative volumetric field.</summary>
/// <remarks>Negative density is solid.  A range is deliberately classified
/// from the cell-owning samples only: marching-gradient halo samples are not
/// evidence that the central region itself needs a mesh.</remarks>
public enum SmoothTerrainDensityClassification { Empty, Solid, Mixed }
public readonly record struct SmoothTerrainDensityRange(float Minimum, float Maximum, SmoothTerrainDensityClassification Classification);
internal readonly record struct SmoothTerrainRegularChunkCapture(SmoothTerrainDensityRange Range, SmoothTerrainChunkSnapshot? Snapshot);
public sealed class SmoothTerrainVolumetricDensityLattice
{
    private readonly SmoothTerrainSample[] _samples;
    internal SmoothTerrainVolumetricDensityLattice(SmoothTerrainVolumetricDensityCacheKey key, SmoothTerrainSample[] samples)
    {
        if (!key.IsValid || samples.Length != checked((key.CellsPerAxis + 1) * (key.CellsPerAxis + 1) * (key.CellsPerAxis + 1)))
            throw new ArgumentException("Volumetric lattice dimensions are invalid.");
        Key = key;
        _samples = samples;
    }
    public SmoothTerrainVolumetricDensityCacheKey Key { get; }
    public ReadOnlySpan<SmoothTerrainSample> Samples => _samples;
    public SmoothTerrainSample Sample(int x, int y, int z)
    {
        int side=Key.CellsPerAxis+1;
        if((uint)x>=(uint)side||(uint)y>=(uint)side||(uint)z>=(uint)side)
            throw new ArgumentOutOfRangeException();
        return _samples[(z*side+y)*side+x];
    }
    /// <summary>Classifies an inclusive lattice-sample box for the requested
    /// regular-cell region.  It is immutable and conservative: zero belongs
    /// to empty, while any sign mix remains meshable.</summary>
    public SmoothTerrainDensityRange ClassifyCells(int firstX,int firstY,int firstZ,int cellsX,int cellsY,int cellsZ)
    {
        if(cellsX<=0||cellsY<=0||cellsZ<=0)throw new ArgumentOutOfRangeException(nameof(cellsX));
        int side=Key.CellsPerAxis+1;
        int lastX=checked(firstX+cellsX),lastY=checked(firstY+cellsY),lastZ=checked(firstZ+cellsZ);
        if(firstX<0||firstY<0||firstZ<0||lastX>=side||lastY>=side||lastZ>=side)throw new ArgumentOutOfRangeException();
        float minimum=float.PositiveInfinity,maximum=float.NegativeInfinity;
        for(int z=firstZ;z<=lastZ;z++)for(int y=firstY;y<=lastY;y++)for(int x=firstX;x<=lastX;x++)
        {
            float density=_samples[(z*side+y)*side+x].Density;
            minimum=MathF.Min(minimum,density);maximum=MathF.Max(maximum,density);
        }
        SmoothTerrainDensityClassification classification=minimum>=0
            ? SmoothTerrainDensityClassification.Empty
            : maximum<0 ? SmoothTerrainDensityClassification.Solid : SmoothTerrainDensityClassification.Mixed;
        return new SmoothTerrainDensityRange(minimum,maximum,classification);
    }
}

/// <summary>
/// Immutable, thread-safe sampling view over the exact smooth baseline plus
/// ordered edit snapshot. Near, medium, and coarse cave systems are required
/// to obtain their samples and cache identity from this one contract.
/// </summary>
public sealed class SmoothTerrainVolumetricDensitySnapshot
{
    private readonly ISmoothTerrainBaseline _baseline;
    private readonly SmoothTerrainEditSnapshot _edits;
    private readonly ConcurrentBag<WorkerState> _availableWorkerStates = new();
    private readonly object _workerStateGate = new();
    private readonly TransitionLatticeCache _transitionLatticeCache;
    private readonly RegularChunkCache _regularChunkCache;
    private int _workerStateCount;
    private bool _workerStatesRetired;
    private const int MaximumPooledWorkerStates = 8;
    public SmoothTerrainVolumetricDensitySnapshot(ISmoothTerrainBaseline baseline, SmoothTerrainEditSnapshot edits)
        : this(baseline, edits, new TransitionLatticeCache(), new RegularChunkCache())
    {
    }
    private SmoothTerrainVolumetricDensitySnapshot(
        ISmoothTerrainBaseline baseline,
        SmoothTerrainEditSnapshot edits,
        TransitionLatticeCache transitionLatticeCache,
        RegularChunkCache regularChunkCache)
    {
        _baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
        _edits = edits ?? throw new ArgumentNullException(nameof(edits));
        _transitionLatticeCache = transitionLatticeCache;
        _regularChunkCache = regularChunkCache;
        Identity = new SmoothTerrainVolumetricDensityIdentity(
            SmoothTerrainVolumetricDensityIdentity.CurrentContractVersion,
            baseline.Revision, edits.Revision, baseline.AroundLength, baseline.AcrossWidth);
    }
    public SmoothTerrainVolumetricDensityIdentity Identity { get; }
    /// <summary>
    /// Creates an identity-equivalent sampling view with an independent noise
    /// sampler for one mesh worker.  This preserves the shared immutable edit
    /// and cave contracts while avoiding cross-worker FastNoiseLite locking.
    /// </summary>
    internal SmoothTerrainVolumetricWorkerContext CreateWorkerContext(bool useIndependentSampler = true)
    {
        if (!useIndependentSampler || _baseline is not RingTerrainSmoothBaseline production)
            return new SmoothTerrainVolumetricWorkerContext(this, null);
        lock (_workerStateGate)
        {
            if (!_workerStatesRetired && _availableWorkerStates.TryTake(out WorkerState? available))
                return new SmoothTerrainVolumetricWorkerContext(this, available);
        }
        RingTerrainSmoothBaseline workerBaseline = production.CreateIndependentWorkerCopy();
        Interlocked.Increment(ref _workerStateCount);
        return new SmoothTerrainVolumetricWorkerContext(
            this,
            new WorkerState(new SmoothTerrainVolumetricDensitySnapshot(workerBaseline, _edits, _transitionLatticeCache, _regularChunkCache), workerBaseline));
    }
    internal void ReturnWorkerState(WorkerState state)
    {
        lock (_workerStateGate)
        {
            if (!_workerStatesRetired && Volatile.Read(ref _workerStateCount) <= MaximumPooledWorkerStates)
            {
                _availableWorkerStates.Add(state);
                return;
            }
        }
        Interlocked.Decrement(ref _workerStateCount);
        state.Baseline.DisposeIndependentWorkerCopy();
    }
    internal void DisposePooledWorkerContexts()
    {
        var dispose = new List<WorkerState>();
        lock (_workerStateGate)
        {
            _workerStatesRetired = true;
            while (_availableWorkerStates.TryTake(out WorkerState? state))
            {
                Interlocked.Decrement(ref _workerStateCount);
                dispose.Add(state);
            }
        }
        foreach (WorkerState state in dispose)
            state.Baseline.DisposeIndependentWorkerCopy();
    }
    internal sealed class WorkerState
    {
        public WorkerState(SmoothTerrainVolumetricDensitySnapshot field, RingTerrainSmoothBaseline baseline)
        { Field = field; Baseline = baseline; }
        public SmoothTerrainVolumetricDensitySnapshot Field { get; }
        public RingTerrainSmoothBaseline Baseline { get; }
    }
    internal readonly record struct TransitionLatticeCacheDiagnostics(
        int Count,
        long Hits,
        long Misses,
        long Evictions,
        int Capacity,
        long RetainedBytes);
    internal TransitionLatticeCacheDiagnostics CaptureTransitionLatticeCacheDiagnostics() =>
        _transitionLatticeCache.CaptureDiagnostics();
    internal void ResetTransitionLatticeCacheForDiagnostics() => _transitionLatticeCache.Reset();
    internal readonly record struct RegularChunkCacheDiagnostics(int Count,long Hits,long Misses,long Evictions,int Capacity,long RetainedBytes);
    internal RegularChunkCacheDiagnostics CaptureRegularChunkCacheDiagnostics()=>_regularChunkCache.CaptureDiagnostics();
    internal void ResetRegularChunkCacheForDiagnostics()=>_regularChunkCache.Reset();
    public SmoothTerrainSample Sample(double around, double vertical, double across)
        => Sample(around,vertical,across,new List<SmoothTerrainEditCommand>());
    public SmoothTerrainVolumetricDensityLattice CaptureLattice(SmoothTerrainVolumetricDensityCacheKey key)
    {
        if (key.Field != Identity) throw new ArgumentException("Cache key belongs to another density field.", nameof(key));
        if (!key.IsValid) throw new ArgumentOutOfRangeException(nameof(key));
        int side = key.CellsPerAxis + 1;
        var samples = new SmoothTerrainSample[checked(side * side * side)];
        FillLattice(key, samples, null, null);
        return new SmoothTerrainVolumetricDensityLattice(key, samples);
    }

    /// <summary>
    /// Fills native Transvoxel ABI buffers directly.  Transition workers used
    /// to allocate a managed sample lattice and immediately copy it into these
    /// two arrays, doubling their short-lived capture allocation.
    /// </summary>
    internal void CaptureNativeLattice(SmoothTerrainVolumetricDensityCacheKey key, float[] densities, byte[] materials)
    {
        if (key.Field != Identity) throw new ArgumentException("Cache key belongs to another density field.", nameof(key));
        if (!key.IsValid) throw new ArgumentOutOfRangeException(nameof(key));
        int count=checked((key.CellsPerAxis+1)*(key.CellsPerAxis+1)*(key.CellsPerAxis+1));
        if(densities.Length!=count||materials.Length!=count)throw new ArgumentException("Native lattice buffers do not match cache-key dimensions.");
        FillLattice(key, null, densities, materials);
    }
    /// <summary>Shares immutable coarse transition lattices among sibling fine
    /// sectors. The cache has a strict per-field memory bound.</summary>
    internal void CaptureCachedTransitionLattice(
        SmoothTerrainVolumetricDensityCacheKey key,
        float[] densities,
        byte[] materials) => _transitionLatticeCache.CopyOrCapture(this, key, densities, materials);

    /// <summary>
    /// Captures the 37^3 regular-mesher lattice without first materialising a
    /// <see cref="SmoothTerrainVolumetricDensityLattice"/> and copying it.
    /// The central 33^3 samples own the 32 regular cells; a uniform central
    /// volume does not retain a snapshot. Every sample is evaluated exactly
    /// once, so a mixed sector has no second density/edit evaluation.
    /// </summary>
    internal SmoothTerrainRegularChunkCapture CaptureRegularChunk(
        SmoothTerrainVolumetricDensityCacheKey key, SmoothTerrainChunkCoordinate coordinate)
    {
        if (key.Field != Identity) throw new ArgumentException("Cache key belongs to another density field.", nameof(key));
        if (!key.IsValid || key.CellsPerAxis != SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo * 2)
            throw new ArgumentOutOfRangeException(nameof(key), "Regular chunk capture requires a 36-cell lattice with a two-sample halo.");

        CaveTopologyDensityRegion? designedCaves=CaptureDesignedCaves(key);
        long timingStart = Stopwatch.GetTimestamp();
        if (_edits.IsEmpty
            && _baseline is RingTerrainSmoothBaseline production
            && production.TryClassifyUniformRegularRegion(key, designedCaves, out SmoothTerrainDensityRange uniformRange))
        {
            if (designedCaves != null)
                CaveGenerationPerformanceTelemetry.RecordUniformDensityRejection(
                    designedCaves.Composer.Identity, Stopwatch.GetTimestamp() - timingStart);
            return new SmoothTerrainRegularChunkCapture(uniformRange, null);
        }

        const int halo = SmoothTerrainChunkSnapshot.Halo;
        const int cells = SmoothTerrainChunkSnapshot.Cells;
        int count = SmoothTerrainChunkSnapshot.SampleCount;
        var densities = new float[count * count * count];
        var materials = new int[densities.Length];
        float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
        var scratch = new List<SmoothTerrainEditCommand>();
        for (int z = 0; z < count; z++) for (int x = 0; x < count; x++)
        {
            long around = RingCoordMath.WrapAround(checked(key.AroundOrigin + (long)x * key.SampleSpacingMeters), _baseline.AroundLength);
            int across = checked(key.AcrossOrigin + z * key.SampleSpacingMeters);
            float surface = _baseline is RingTerrainSmoothBaseline ring && across >= 0 && across <= _baseline.AcrossWidth ? ring.GetSmoothSurfaceHeight(around, across) : 0;
            for (int y = 0; y < count; y++)
            {
                int index = (z * count + y) * count + x;
                float density = SampleDensity(around, checked(key.VerticalOrigin + y * key.SampleSpacingMeters), across, surface, designedCaves, scratch, out int material);
                densities[index] = density;
                materials[index] = material;
                if (x >= halo && x <= halo + cells && y >= halo && y <= halo + cells && z >= halo && z <= halo + cells)
                {
                    minimum = MathF.Min(minimum, density); maximum = MathF.Max(maximum, density);
                }
            }
        }
        SmoothTerrainDensityClassification classification = minimum >= 0 ? SmoothTerrainDensityClassification.Empty
            : maximum < 0 ? SmoothTerrainDensityClassification.Solid : SmoothTerrainDensityClassification.Mixed;
        var range = new SmoothTerrainDensityRange(minimum, maximum, classification);
        if (designedCaves != null)
            CaveGenerationPerformanceTelemetry.RecordDensityLattice(designedCaves.Composer.Identity, Stopwatch.GetTimestamp()-timingStart, densities.Length);
        if (classification != SmoothTerrainDensityClassification.Mixed) return new SmoothTerrainRegularChunkCapture(range, null);

        return new SmoothTerrainRegularChunkCapture(range, SmoothTerrainChunkSnapshot.FromOwnedArrays(coordinate,
            new SmoothTerrainRevision(Identity.BaselineRevision, Identity.EditRevision), densities, materials));
    }

    private void FillLattice(SmoothTerrainVolumetricDensityCacheKey key, SmoothTerrainSample[]? samples, float[]? densities, byte[]? materials)
    {
        int side = key.CellsPerAxis + 1;
        // One scratch list belongs to this capture invocation. Concurrent LOD
        // workers still have no shared mutable state. Integer LOD lattices use
        // the same column-wise known-surface path as the production 1 m
        // capture: medium sectors must not recompute macro terrain noise for
        // every vertical sample.
        var scratch=new List<SmoothTerrainEditCommand>();
        CaveTopologyDensityRegion? designedCaves=CaptureDesignedCaves(key);
        long timingStart=Stopwatch.GetTimestamp();
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            long around=RingCoordMath.WrapAround(checked(key.AroundOrigin+(long)x*key.SampleSpacingMeters),_baseline.AroundLength);
            int across=checked(key.AcrossOrigin+z*key.SampleSpacingMeters);
            float surface=_baseline is RingTerrainSmoothBaseline ring&&across>=0&&across<=_baseline.AcrossWidth?ring.GetSmoothSurfaceHeight(around,across):0;
            for(int y=0;y<side;y++)
            {
                int vertical=checked(key.VerticalOrigin+y*key.SampleSpacingMeters);
                float density=SampleDensity(around, vertical, across, surface, designedCaves, scratch, out int material);
                int index=(z*side+y)*side+x;
                if(samples!=null)samples[index]=new SmoothTerrainSample(density,material,_edits.Revision);
                else
                {
                    if((uint)material>byte.MaxValue)throw new InvalidOperationException($"Smooth terrain material id {material} cannot be represented by the native transition ABI.");
                    densities![index]=density;
                    materials![index]=(byte)material;
                }
            }
        }
        if(designedCaves!=null)
            CaveGenerationPerformanceTelemetry.RecordDensityLattice(designedCaves.Composer.Identity,Stopwatch.GetTimestamp()-timingStart,side*side*side);
    }

    private CaveTopologyDensityRegion? CaptureDesignedCaves(SmoothTerrainVolumetricDensityCacheKey key)
    {
        if(_baseline is not RingTerrainSmoothBaseline production||!production.DesignedCavesEnabled)return null;
        long span=checked((long)key.CellsPerAxis*key.SampleSpacingMeters);
        CaveTopologyDensityRegion? region=production.CaptureDesignedCaveRegion(
            key.AroundOrigin,key.VerticalOrigin,key.AcrossOrigin,
            span,span,span);
        // Every lattice sample lies in [origin, origin+span]; prove it once so
        // per-sample cave sampling can skip its containment check.
        region?.RequireContainsLattice(
            key.AroundOrigin,checked(key.AroundOrigin+span),
            key.VerticalOrigin,checked(key.VerticalOrigin+span),
            key.AcrossOrigin,checked(key.AcrossOrigin+span));
        return region;
    }
    private float SampleDensity(long around, int vertical, int across, float surface, CaveTopologyDensityRegion? designedCaves,List<SmoothTerrainEditCommand> scratch, out int material)
    {
        SmoothTerrainSample baseline = _baseline is RingTerrainSmoothBaseline production
            ? production.SampleWithKnownSurface(around, vertical, across, surface,designedCaves,regionValidated:true) : _baseline.Sample(around, vertical, across);
        float density = baseline.Density;
        material = baseline.Material;
        if (_edits.IsEmpty) return density;
        _edits.Query(around, vertical, across, scratch);
        foreach (SmoothTerrainEditCommand stamp in scratch)
        {
            long dx = ShortestDelta(around, stamp.Around, _baseline.AroundLength); float dy = vertical - stamp.Vertical, dz = across - stamp.Across;
            float sdf = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - stamp.Radius;
            if (stamp.AddsMatter) { if (sdf < density) { density = sdf; material = stamp.Material; } }
            else density = MathF.Max(density, -sdf);
        }
        return density;
    }
    /// <summary>Captures the production 1 m, 32-cell parent lattice (including
    /// its marching halo) from this authoritative immutable field.</summary>
    public SmoothTerrainChunkSnapshot CaptureChunk(SmoothTerrainChunkCoordinate coordinate)
    {
        // The immutable field owns the exact baseline and ordered edits. Keep
        // the established production capture implementation so its known-
        // surface optimisation remains intact for the 1 m parent tier.
        return _regularChunkCache.GetOrCapture(this, coordinate);
    }
    internal bool TryCopyCachedFineTransitionLattice(RingTerrainSampleCoordinate origin,float[] densities,byte[] materials)
    {
        const int side=35;
        int expected=side*side*side;
        if(densities.Length!=expected||materials.Length!=expected)
            throw new ArgumentException("Fine transition buffers must contain exactly 35^3 samples.");
        if(origin.AroundVoxel%SmoothTerrainChunkSnapshot.Cells!=0||origin.VerticalVoxel%SmoothTerrainChunkSnapshot.Cells!=0||origin.AcrossVoxel%SmoothTerrainChunkSnapshot.Cells!=0)return false;
        var coordinate=new SmoothTerrainChunkCoordinate(
            origin.AroundVoxel/SmoothTerrainChunkSnapshot.Cells,
            origin.VerticalVoxel/SmoothTerrainChunkSnapshot.Cells,
            origin.AcrossVoxel/SmoothTerrainChunkSnapshot.Cells);
        SmoothTerrainChunkSnapshot snapshot=_regularChunkCache.GetOrCapture(this,coordinate);
        for(int z=0;z<side;z++)for(int y=0;y<side;y++)for(int x=0;x<side;x++)
        {
            int i=(z*side+y)*side+x;
            densities[i]=snapshot.Density(x-1,y-1,z-1);
            int material=snapshot.Material(x-1,y-1,z-1);
            if((uint)material>byte.MaxValue)
                throw new InvalidOperationException($"Smooth terrain material id {material} cannot be represented by the native transition ABI.");
            materials[i]=(byte)material;
        }
        return true;
    }
    private SmoothTerrainSample Sample(double around,double vertical,double across,List<SmoothTerrainEditCommand> scratch)
        => _edits.SampleContinuous(_baseline,around,vertical,across,scratch);
    private static long ShortestDelta(long a,long b,long length)
    {
        long d=a-b;d%=length;if(d>length/2)d-=length;if(d<-length/2)d+=length;return d;
    }

    private sealed class RegularChunkCache
    {
        // Sized above the ~139-164 desired base chunks so near-transition jobs reuse
        // captured lattices. Worst case ~256 x 405 KB (37^3 float+int) ~= 104 MB per live field.
        private const int MaximumEntries=256;
        private readonly object _gate=new();
        private readonly Dictionary<SmoothTerrainChunkCoordinate,Lazy<SmoothTerrainChunkSnapshot>> _entries=new();
        private readonly Queue<(SmoothTerrainChunkCoordinate Coordinate,Lazy<SmoothTerrainChunkSnapshot> Entry)> _order=new();
        private long _hits,_misses,_evictions;
        public SmoothTerrainChunkSnapshot GetOrCapture(SmoothTerrainVolumetricDensitySnapshot field,SmoothTerrainChunkCoordinate coordinate)
        {
            Lazy<SmoothTerrainChunkSnapshot> entry;
            bool inserted=false;
            lock(_gate)
            {
                if(_entries.TryGetValue(coordinate,out entry!))_hits++;
                else
                {
                    _misses++;
                    while(_entries.Count>=MaximumEntries)
                    {
                        var oldest=_order.Dequeue();
                        if(_entries.TryGetValue(oldest.Coordinate,out Lazy<SmoothTerrainChunkSnapshot>? current)
                            &&ReferenceEquals(current,oldest.Entry)
                            &&_entries.Remove(oldest.Coordinate))_evictions++;
                    }
                    entry=new Lazy<SmoothTerrainChunkSnapshot>(
                        ()=>SmoothTerrainSnapshotCapture.Capture(coordinate,field._baseline,field._edits),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    _entries.Add(coordinate,entry);
                    _order.Enqueue((coordinate,entry));
                    inserted=true;
                }
            }
            try{return entry.Value;}
            catch
            {
                if(inserted)lock(_gate)
                {
                    if(_entries.TryGetValue(coordinate,out Lazy<SmoothTerrainChunkSnapshot>? current)
                        &&ReferenceEquals(current,entry))_entries.Remove(coordinate);
                }
                throw;
            }
        }
        public RegularChunkCacheDiagnostics CaptureDiagnostics()
        {
            lock(_gate)
            {
                long bytes=0;
                foreach(Lazy<SmoothTerrainChunkSnapshot> entry in _entries.Values)
                    if(entry.IsValueCreated)
                        bytes+=(long)SmoothTerrainChunkSnapshot.SampleCount*SmoothTerrainChunkSnapshot.SampleCount*SmoothTerrainChunkSnapshot.SampleCount*(sizeof(float)+sizeof(int));
                return new RegularChunkCacheDiagnostics(_entries.Count,_hits,_misses,_evictions,MaximumEntries,bytes);
            }
        }
        public void Reset(){lock(_gate){_entries.Clear();_order.Clear();_hits=_misses=_evictions=0;}}
    }

    private sealed class TransitionLatticeCache
    {
        private const int MaximumEntries = 32;
        private readonly object _gate = new();
        private readonly Dictionary<SmoothTerrainVolumetricDensityCacheKey, Lazy<NativeLattice>> _entries = new();
        private readonly Queue<(SmoothTerrainVolumetricDensityCacheKey Key, Lazy<NativeLattice> Entry)> _insertionOrder = new();
        private long _hits;
        private long _misses;
        private long _evictions;

        public void CopyOrCapture(
            SmoothTerrainVolumetricDensitySnapshot field,
            SmoothTerrainVolumetricDensityCacheKey key,
            float[] densities,
            byte[] materials)
        {
            if (key.Field != field.Identity)
                throw new ArgumentException("Cache key belongs to another density field.", nameof(key));
            if (!key.IsValid)
                throw new ArgumentOutOfRangeException(nameof(key));
            int expectedCount = checked((key.CellsPerAxis + 1) * (key.CellsPerAxis + 1) * (key.CellsPerAxis + 1));
            if (densities.Length != expectedCount || materials.Length != expectedCount)
                throw new ArgumentException("Native lattice buffers do not match cache-key dimensions.");
            Lazy<NativeLattice> entry;
            bool inserted = false;
            lock (_gate)
            {
                if (_entries.TryGetValue(key, out Lazy<NativeLattice>? existing))
                {
                    _hits++;
                    entry = existing;
                }
                else
                {
                    _misses++;
                    while (_entries.Count >= MaximumEntries)
                    {
                        (SmoothTerrainVolumetricDensityCacheKey Key, Lazy<NativeLattice> Entry) oldest =
                            _insertionOrder.Dequeue();
                        if (_entries.TryGetValue(oldest.Key, out Lazy<NativeLattice>? current)
                            && ReferenceEquals(current, oldest.Entry)
                            && _entries.Remove(oldest.Key))
                            _evictions++;
                    }
                    entry = new Lazy<NativeLattice>(
                        () => Capture(field, key),
                        LazyThreadSafetyMode.ExecutionAndPublication);
                    _entries.Add(key, entry);
                    _insertionOrder.Enqueue((key, entry));
                    inserted = true;
                }
            }

            NativeLattice lattice;
            try
            {
                lattice = entry.Value;
            }
            catch
            {
                if (inserted)
                {
                    lock (_gate)
                    {
                        if (_entries.TryGetValue(key, out Lazy<NativeLattice>? current)
                            && ReferenceEquals(current, entry))
                            _entries.Remove(key);
                    }
                }
                throw;
            }
            Array.Copy(lattice.Densities, densities, densities.Length);
            Array.Copy(lattice.Materials, materials, materials.Length);
        }

        public TransitionLatticeCacheDiagnostics CaptureDiagnostics()
        {
            lock (_gate)
            {
                long bytes = 0;
                foreach (Lazy<NativeLattice> entry in _entries.Values)
                {
                    if (!entry.IsValueCreated) continue;
                    bytes += (long)entry.Value.Densities.Length * sizeof(float)
                        + entry.Value.Materials.Length;
                }
                return new TransitionLatticeCacheDiagnostics(
                    _entries.Count,
                    _hits,
                    _misses,
                    _evictions,
                    MaximumEntries,
                    bytes);
            }
        }

        public void Reset()
        {
            lock (_gate)
            {
                _entries.Clear();
                _insertionOrder.Clear();
                _hits = _misses = _evictions = 0;
            }
        }

        private static NativeLattice Capture(
            SmoothTerrainVolumetricDensitySnapshot field,
            SmoothTerrainVolumetricDensityCacheKey key)
        {
            int side = key.CellsPerAxis + 1;
            int count = checked(side * side * side);
            var densities = new float[count];
            var materials = new byte[count];
            field.CaptureNativeLattice(key, densities, materials);
            return new NativeLattice(densities, materials);
        }

        private sealed record NativeLattice(float[] Densities, byte[] Materials);
    }
}

/// <summary>Owns an optional independent production baseline for one worker.</summary>
internal sealed class SmoothTerrainVolumetricWorkerContext : IDisposable
{
    private SmoothTerrainVolumetricDensitySnapshot? _owner;
    private SmoothTerrainVolumetricDensitySnapshot.WorkerState? _state;
    public SmoothTerrainVolumetricWorkerContext(
        SmoothTerrainVolumetricDensitySnapshot field,
        SmoothTerrainVolumetricDensitySnapshot.WorkerState? state)
    {
        _owner = state == null ? null : field;
        _state = state;
        Field = state?.Field ?? field;
    }
    public SmoothTerrainVolumetricDensitySnapshot Field { get; }
    internal RingTerrainSampler? Sampler => _state?.Baseline.Sampler;
    public void Dispose()
    {
        SmoothTerrainVolumetricDensitySnapshot? owner = Interlocked.Exchange(ref _owner, null);
        SmoothTerrainVolumetricDensitySnapshot.WorkerState? state = Interlocked.Exchange(ref _state, null);
        if (owner != null && state != null) owner.ReturnWorkerState(state);
    }
}

public static class SmoothTerrainSnapshotCapture
{
    public static SmoothTerrainChunkSnapshot Capture(SmoothTerrainChunkCoordinate coordinate, ISmoothTerrainBaseline baseline, SmoothTerrainEditSnapshot edits)
    {
        int total = SmoothTerrainChunkSnapshot.SampleCount * SmoothTerrainChunkSnapshot.SampleCount * SmoothTerrainChunkSnapshot.SampleCount;
        var density = new float[total]; var materials = new int[total]; var candidates = new List<SmoothTerrainEditCommand>();
        RingTerrainSampleCoordinate origin = coordinate.Origin;
        CaveTopologyDensityRegion? designedCaves=baseline is RingTerrainSmoothBaseline caveBaseline&&caveBaseline.DesignedCavesEnabled
            ? caveBaseline.CaptureDesignedCaveRegion(
                checked(origin.AroundVoxel-SmoothTerrainChunkSnapshot.Halo),
                checked(origin.VerticalVoxel-SmoothTerrainChunkSnapshot.Halo),
                checked(origin.AcrossVoxel-SmoothTerrainChunkSnapshot.Halo),
                SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo*2,
                SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo*2,
                SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo*2)
            : null;
        // Samples span [origin-Halo, origin+Cells+Halo] on each axis, exactly the captured region.
        designedCaves?.RequireContainsLattice(
            checked(origin.AroundVoxel-SmoothTerrainChunkSnapshot.Halo),checked(origin.AroundVoxel+SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo),
            checked(origin.VerticalVoxel-SmoothTerrainChunkSnapshot.Halo),checked(origin.VerticalVoxel+SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo),
            checked(origin.AcrossVoxel-SmoothTerrainChunkSnapshot.Halo),checked(origin.AcrossVoxel+SmoothTerrainChunkSnapshot.Cells+SmoothTerrainChunkSnapshot.Halo));
        long timingStart=Stopwatch.GetTimestamp();
        // The production baseline's macro surface depends on X/Z, never Y.
        // Walking the snapshot by column avoids recomputing all regional noise
        // 37 times for each column (and avoids a second surface query for soil).
        for (int z = -SmoothTerrainChunkSnapshot.Halo; z <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; z++)
        for (int x = -SmoothTerrainChunkSnapshot.Halo; x <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; x++)
        {
            long around = RingCoordMath.WrapAround(origin.AroundVoxel + x, baseline.AroundLength);
            int across = origin.AcrossVoxel + z;
            float surface = baseline is RingTerrainSmoothBaseline ring && across >= 0 && across <= baseline.AcrossWidth
                ? ring.GetSmoothSurfaceHeight(around, across)
                : 0.0f;
            for (int y = -SmoothTerrainChunkSnapshot.Halo; y <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; y++)
            {
                int vertical = origin.VerticalVoxel + y;
                SmoothTerrainSample sample = baseline is RingTerrainSmoothBaseline production
                    ? production.SampleWithKnownSurface(around, vertical, across, surface,designedCaves,regionValidated:true)
                    : baseline.Sample(around, vertical, across);
                float value = sample.Density;
                int material = sample.Material;
                if (!edits.IsEmpty)
                {
                    edits.Query(around, vertical, across, candidates);
                    foreach (SmoothTerrainEditCommand stamp in candidates)
                    {
                        long dx = ShortestDelta(around, stamp.Around, baseline.AroundLength);
                        float dy = vertical - stamp.Vertical;
                        float dz = across - stamp.Across;
                        float sdf = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - stamp.Radius;
                        if (stamp.AddsMatter) { if (sdf < value) { value = sdf; material = stamp.Material; } }
                        else value = MathF.Max(value, -sdf);
                    }
                }
                int index = SmoothTerrainChunkSnapshot.Index(x, y, z);
                density[index] = value;
                materials[index] = material;
            }
        }
        if(designedCaves!=null)
            CaveGenerationPerformanceTelemetry.RecordDensityLattice(
                designedCaves.Composer.Identity,
                Stopwatch.GetTimestamp()-timingStart,
                total);
        return SmoothTerrainChunkSnapshot.FromOwnedArrays(coordinate, new SmoothTerrainRevision(baseline.Revision, edits.Revision), density, materials);
    }

    /// <summary>
    /// Applies one append-ordered command to an already composed immutable
    /// lattice. This preserves every preceding edit while avoiding another
    /// baseline/noise capture for rapid interactive digging.
    /// </summary>
    public static SmoothTerrainChunkSnapshot ApplyAppendedEdit(
        SmoothTerrainChunkSnapshot previous,
        ISmoothTerrainBaseline baseline,
        SmoothTerrainEditCommand command,
        ulong editRevision)
        => ApplyAppendedEdits(previous,baseline,new[]{command},editRevision);

    /// <summary>Applies one append-ordered process-frame batch with one lattice
    /// clone and one bucket query per sample. Consecutive identical union or
    /// subtraction stamps are idempotent and compacted before evaluation.</summary>
    public static SmoothTerrainChunkSnapshot ApplyAppendedEdits(
        SmoothTerrainChunkSnapshot previous,
        ISmoothTerrainBaseline baseline,
        IReadOnlyList<SmoothTerrainEditCommand> commands,
        ulong editRevision)
    {
        if (previous.Revision.Baseline != baseline.Revision)
            throw new ArgumentException("The cached lattice belongs to a different terrain baseline.", nameof(previous));

        var compact=new List<SmoothTerrainEditCommand>(commands.Count);
        var runKeys=new HashSet<(long Around,int Vertical,int Across,float Radius,int Material)>();
        bool? runAddsMatter=null;
        foreach(SmoothTerrainEditCommand source in commands)
        {
            if(!source.IsValid||source.Radius>SmoothTerrainEditSnapshot.MaximumStampRadiusMeters)
                throw new ArgumentException("Invalid appended smooth terrain stamp.",nameof(commands));
            SmoothTerrainEditCommand command=source with { Around=RingCoordMath.WrapAround(source.Around,baseline.AroundLength) };
            if(runAddsMatter!=command.AddsMatter){runAddsMatter=command.AddsMatter;runKeys.Clear();}
            var key=(command.Around,command.Vertical,command.Across,command.Radius,command.AddsMatter?command.Material:0);
            if(runKeys.Add(key))compact.Add(command);
        }

        float[] density = previous.Densities.ToArray();
        int[] materials = previous.Materials.ToArray();
        RingTerrainSampleCoordinate origin = previous.Coordinate.Origin;
        var pending=new SmoothTerrainEditSnapshot(compact,editRevision,baseline.AroundLength);
        var candidates=new List<SmoothTerrainEditCommand>();
        for (int z = -SmoothTerrainChunkSnapshot.Halo; z <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; z++)
        for (int y = -SmoothTerrainChunkSnapshot.Halo; y <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; y++)
        for (int x = -SmoothTerrainChunkSnapshot.Halo; x <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; x++)
        {
            long around = RingCoordMath.WrapAround(origin.AroundVoxel + x, baseline.AroundLength);
            int vertical = origin.VerticalVoxel + y;
            int across = origin.AcrossVoxel + z;
            int index = SmoothTerrainChunkSnapshot.Index(x, y, z);
            pending.Query(around,vertical,across,candidates);
            foreach(SmoothTerrainEditCommand command in candidates)
            {
                long dx = ShortestDelta(around, command.Around, baseline.AroundLength);
                float dy = vertical - command.Vertical;
                float dz = across - command.Across;
                float sdf = MathF.Sqrt(dx * dx + dy * dy + dz * dz) - command.Radius;
                if (command.AddsMatter)
                {
                    if (sdf < density[index])
                    {
                        density[index] = sdf;
                        materials[index] = command.Material;
                    }
                }
                else density[index] = MathF.Max(density[index], -sdf);
            }
        }

        return SmoothTerrainChunkSnapshot.FromOwnedArrays(
            previous.Coordinate,
            new SmoothTerrainRevision(baseline.Revision, editRevision),
            density,
            materials);
    }

    private static long ShortestDelta(long a, long b, long length) { long d = a - b; if (length <= 0) return d; d %= length; if (d > length / 2) d -= length; if (d < -length / 2) d += length; return d; }
}
