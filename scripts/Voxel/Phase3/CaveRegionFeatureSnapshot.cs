using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Process-local, allocation-free aggregate timings for one immutable cave identity.</summary>
public static class CaveGenerationPerformanceTelemetry
{
    private sealed class Counter { public long RegionCaptures, RegionTicks, DensityLattices, DensitySamples, DensityTicks, UniformDensityRejections; }
    private static readonly ConcurrentDictionary<ulong, Counter> Counters = new();
    public readonly record struct Diagnostics(long RegionCaptures, double RegionMilliseconds, long DensityLattices, long DensitySamples, double DensityMilliseconds, long UniformDensityRejections);
    public static void RecordRegionCapture(ulong identity, long elapsedTicks)
    { Counter counter=Counters.GetOrAdd(identity, static _=>new Counter()); Interlocked.Increment(ref counter.RegionCaptures); Interlocked.Add(ref counter.RegionTicks,elapsedTicks); }
    public static void RecordDensityLattice(ulong identity, long elapsedTicks, int samples)
    { Counter counter=Counters.GetOrAdd(identity, static _=>new Counter()); Interlocked.Increment(ref counter.DensityLattices); Interlocked.Add(ref counter.DensitySamples,samples); Interlocked.Add(ref counter.DensityTicks,elapsedTicks); }
    public static void RecordUniformDensityRejection(ulong identity, long elapsedTicks)
    { Counter counter=Counters.GetOrAdd(identity, static _=>new Counter()); Interlocked.Increment(ref counter.DensityLattices); Interlocked.Increment(ref counter.UniformDensityRejections); Interlocked.Add(ref counter.DensityTicks,elapsedTicks); }
    public static Diagnostics Capture(ulong identity)
    { if(!Counters.TryGetValue(identity,out Counter? c))return default; double scale=1000.0/Stopwatch.Frequency; return new Diagnostics(Interlocked.Read(ref c.RegionCaptures),Interlocked.Read(ref c.RegionTicks)*scale,Interlocked.Read(ref c.DensityLattices),Interlocked.Read(ref c.DensitySamples),Interlocked.Read(ref c.DensityTicks)*scale,Interlocked.Read(ref c.UniformDensityRejections)); }
    public static void Reset(ulong identity) => Counters.TryRemove(identity, out _);
}

public enum CaveRegionFeatureKind
{
    ChamberAnchor,
    CorridorSegment
}

/// <summary>
/// Immutable topology descriptor captured for one meshed region. Shape families,
/// radii, wall noise, density, and player edits are intentionally not represented.
/// </summary>
public readonly record struct CaveRegionFeature(
    CaveRegionFeatureKind Kind,
    ulong Key,
    ulong SourceKey,
    CaveTopologyProvinceAddress ProvinceAddress,
    int TreeUses,
    int LoopUses,
    long AroundA,
    long VerticalA,
    long AcrossA,
    long AroundB,
    long VerticalB,
    long AcrossB);

/// <summary>
/// Half-open meshed-region bounds plus an explicit feature-influence halo, in meters.
/// Capture canonicalizes Around to the owning ring before storing the request.
/// </summary>
public readonly record struct CaveRegionFeatureRequest(
    long Around,
    long Vertical,
    long Across,
    long AroundExtent,
    long VerticalExtent,
    long AcrossExtent,
    long Halo);

public sealed class CaveRegionFeatureSnapshot
{
    private readonly CaveRegionFeature[] _features;

    internal CaveRegionFeatureSnapshot(
        ulong identity,
        CaveRegionFeatureRequest request,
        int candidateProvinceCount,
        int activeProvinceCount,
        CaveRegionFeature[] features)
    {
        Identity = identity;
        Request = request;
        CandidateProvinceCount = candidateProvinceCount;
        ActiveProvinceCount = activeProvinceCount;
        _features = (CaveRegionFeature[])features.Clone();
    }

    public ulong Identity { get; }
    public CaveRegionFeatureRequest Request { get; }
    public int CandidateProvinceCount { get; }
    public int ActiveProvinceCount { get; }
    public int Count => _features.Length;
    public CaveRegionFeature this[int index] => _features[index];
    public ReadOnlySpan<CaveRegionFeature> AsSpan() => _features;
}

/// <summary>
/// Bounded spatial capture over designed-cave topology. Captures chamber
/// points and unique open-face corridor segments without copying whole provinces.
/// </summary>
public sealed class CaveRegionFeatureSnapshotGenerator
{
    public const int Version = 1;
    public const int CandidateEnumerationVersion = 1;
    public const int SpatialFilterVersion = 1;
    public const int MaximumRegionExtentMeters = 4096;
    public const int MaximumInfluenceHaloMeters = 8192;
    public const int MaximumCandidateProvinces = 64;
    public const int MaximumFeatures = 4096;
    /// <summary>Hard cap for immutable province descriptors retained by one cave world.</summary>
    public const int MaximumCachedProvinceArtifacts = 512;

    private const ulong IdentitySalt = 0x524547494F4E5631UL;
    private const ulong AnchorFeatureSalt = 0x524547414E434831UL;
    private const ulong CorridorFeatureSalt = 0x524547434F525231UL;
    private const ulong SnapshotSalt = 0x524547534E415031UL;

    public CaveCellBoundaryConnectionMapGenerator Maps { get; }
    public ulong Identity { get; }
    private readonly object _provinceCacheGate = new();
    private readonly Dictionary<ProvinceCacheKey, ProvinceTopologyArtifact> _provinceCache = new();
    private readonly Queue<ProvinceCacheKey> _provinceCacheInsertionOrder = new();
    private readonly int _maximumCachedProvinceArtifacts;
    private long _provinceCacheHits;
    private long _provinceCacheMisses;
    private long _provinceCacheEvictions;

    public readonly record struct ProvinceCacheDiagnostics(
        int Count, long Hits, long Misses, long Evictions, int Capacity);

    public CaveRegionFeatureSnapshotGenerator(CaveCellBoundaryConnectionMapGenerator maps)
        : this(maps, MaximumCachedProvinceArtifacts)
    {
    }

    internal CaveRegionFeatureSnapshotGenerator(
        CaveCellBoundaryConnectionMapGenerator maps,
        int maximumCachedProvinceArtifacts)
    {
        Maps = maps ?? throw new ArgumentNullException(nameof(maps));
        if (maximumCachedProvinceArtifacts < 1
            || maximumCachedProvinceArtifacts > MaximumCachedProvinceArtifacts)
            throw new ArgumentOutOfRangeException(nameof(maximumCachedProvinceArtifacts));
        _maximumCachedProvinceArtifacts = maximumCachedProvinceArtifacts;
        ulong identity = MixIdentity(maps.Identity, IdentitySalt);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, CandidateEnumerationVersion);
        identity = MixIdentity(identity, SpatialFilterVersion);
        identity = MixIdentity(identity, MaximumRegionExtentMeters);
        identity = MixIdentity(identity, MaximumInfluenceHaloMeters);
        identity = MixIdentity(identity, MaximumCandidateProvinces);
        identity = MixIdentity(identity, MaximumFeatures);
        Identity = Avalanche(identity);
    }

    public CaveRegionFeatureSnapshot Capture(CaveRegionFeatureRequest request)
    {
        CaveTopologyPartition partition = Maps.Graphs.Anchors.Distribution.Partition;
        CaveRegionFeatureRequest canonicalRequest = ValidateAndCanonicalize(partition, request);
        CaveTopologyProvinceAddress[] addresses = ResolveCandidateProvinceAddressesCanonical(
            partition,
            canonicalRequest);
        var candidates = new CapturedCandidate[addresses.Length];
        var features = new List<CaveRegionFeature>(Math.Min(MaximumFeatures, addresses.Length * 32));
        var bySource = new Dictionary<(CaveRegionFeatureKind Kind, ulong SourceKey), CaveRegionFeature>();
        var byKey = new Dictionary<ulong, CaveRegionFeature>();
        int activeProvinceCount = 0;

        for (int addressIndex = 0; addressIndex < addresses.Length; addressIndex++)
        {
            ProvinceTopologyArtifact artifact = ResolveProvinceArtifact(addresses[addressIndex]);
            CaveProvincePlan plan = artifact.Plan;
            ulong mapIdentity = artifact.MapIdentity;
            if (plan.Active)
            {
                activeProvinceCount++;
                foreach (CaveRegionFeature feature in artifact.Features)
                {
                    if (Overlaps(partition, canonicalRequest, feature))
                        AddUniqueFeature(features, bySource, byKey, feature);
                }
            }

            candidates[addressIndex] = new CapturedCandidate(plan, mapIdentity);
        }

        features.Sort(CompareFeatures);
        ulong snapshotIdentity = CalculateSnapshotIdentity(
            canonicalRequest,
            candidates,
            activeProvinceCount,
            features);
        return new CaveRegionFeatureSnapshot(
            snapshotIdentity,
            canonicalRequest,
            addresses.Length,
            activeProvinceCount,
            features.ToArray());
    }

    internal CaveTopologyProvinceAddress[] ResolveCandidateProvinceAddresses(
        CaveRegionFeatureRequest request)
    {
        CaveTopologyPartition partition = Maps.Graphs.Anchors.Distribution.Partition;
        CaveRegionFeatureRequest canonicalRequest = ValidateAndCanonicalize(partition, request);
        CaveTopologyProvinceAddress[] addresses = ResolveCandidateProvinceAddressesCanonical(
            partition,
            canonicalRequest);
        return (CaveTopologyProvinceAddress[])addresses.Clone();
    }

    /// <summary>Low-overhead cache telemetry for cave viewpoint benchmarks.</summary>
    public ProvinceCacheDiagnostics CaptureProvinceCacheDiagnostics()
    {
        lock (_provinceCacheGate)
            return new ProvinceCacheDiagnostics(
                _provinceCache.Count,
                Interlocked.Read(ref _provinceCacheHits),
                Interlocked.Read(ref _provinceCacheMisses),
                Interlocked.Read(ref _provinceCacheEvictions),
                _maximumCachedProvinceArtifacts);
    }

    internal void ResetProvinceCacheForDiagnostics()
    {
        lock (_provinceCacheGate)
        {
            _provinceCache.Clear();
            _provinceCacheInsertionOrder.Clear();
        }
        Interlocked.Exchange(ref _provinceCacheHits, 0);
        Interlocked.Exchange(ref _provinceCacheMisses, 0);
        Interlocked.Exchange(ref _provinceCacheEvictions, 0);
    }

    private ProvinceTopologyArtifact ResolveProvinceArtifact(CaveTopologyProvinceAddress address)
    {
        CaveTopologyPartition partition = Maps.Graphs.Anchors.Distribution.Partition;
        var key = new ProvinceCacheKey(partition.Contract.Identity, Maps.Identity, address);
        lock (_provinceCacheGate)
        {
            if (_provinceCache.TryGetValue(key, out ProvinceTopologyArtifact? cached))
            {
                Interlocked.Increment(ref _provinceCacheHits);
                return cached;
            }

            Interlocked.Increment(ref _provinceCacheMisses);
            CaveProvincePlan plan = Maps.Graphs.Anchors.Distribution.ResolvePlan(address);
            ulong mapIdentity = 0;
            CaveRegionFeature[] features = Array.Empty<CaveRegionFeature>();
            if (plan.Active)
            {
                CaveCellBoundaryConnectionMap map = Maps.Resolve(plan);
                mapIdentity = map.Identity;
                var generated = new List<CaveRegionFeature>(map.Graph.Anchors.Count + map.Count);
                foreach (CaveChamberAnchor anchor in map.Graph.Anchors.AsSpan())
                    generated.Add(CreateAnchorFeature(anchor));
                foreach (CaveCellBoundaryConnection connection in map.AsSpan())
                    generated.Add(CreateCorridorFeature(partition, plan, connection));
                generated.Sort(CompareFeatures);
                features = generated.ToArray();
            }

            var artifact = new ProvinceTopologyArtifact(plan, mapIdentity, features);
            while (_provinceCache.Count >= _maximumCachedProvinceArtifacts)
            {
                ProvinceCacheKey evicted = _provinceCacheInsertionOrder.Dequeue();
                if (_provinceCache.Remove(evicted)) Interlocked.Increment(ref _provinceCacheEvictions);
            }
            _provinceCache.Add(key, artifact);
            _provinceCacheInsertionOrder.Enqueue(key);
            return artifact;
        }
    }

    private CaveRegionFeature CreateAnchorFeature(CaveChamberAnchor anchor)
    {
        ulong key = MixIdentity(Identity, AnchorFeatureSalt);
        key = MixIdentity(key, anchor.Key);
        key = MixIdentity(key, anchor.ProvinceAddress.Around);
        key = MixIdentity(key, anchor.ProvinceAddress.Vertical);
        key = MixIdentity(key, anchor.ProvinceAddress.Across);
        key = MixIdentity(key, anchor.Around);
        key = MixIdentity(key, anchor.Vertical);
        key = MixIdentity(key, anchor.Across);
        return new CaveRegionFeature(
            CaveRegionFeatureKind.ChamberAnchor,
            Avalanche(key),
            anchor.Key,
            anchor.ProvinceAddress,
            0,
            0,
            anchor.Around,
            anchor.Vertical,
            anchor.Across,
            anchor.Around,
            anchor.Vertical,
            anchor.Across);
    }

    /// <summary>Creates the immutable anchor descriptor for bounded locators and diagnostics.</summary>
    public CaveRegionFeature CreateAnchorFeatureForLocator(CaveChamberAnchor anchor) =>
        CreateAnchorFeature(anchor);

    internal CaveRegionFeature CreateAnchorFeatureForDiagnostics(CaveChamberAnchor anchor) =>
        CreateAnchorFeatureForLocator(anchor);

    private CaveRegionFeature CreateCorridorFeature(
        CaveTopologyPartition partition,
        CaveProvincePlan plan,
        CaveCellBoundaryConnection connection)
    {
        CaveTopologyCell owner = partition.ResolveNormalizedCell(
            connection.Face.Owner.Around,
            connection.Face.Owner.Vertical,
            connection.Face.Owner.Across);
        CaveTopologyCell neighbor = partition.ResolveNormalizedCell(
            connection.Face.Neighbor.Around,
            connection.Face.Neighbor.Vertical,
            connection.Face.Neighbor.Across);
        if (owner.Address != connection.Face.Owner || neighbor.Address != connection.Face.Neighbor)
            throw new InvalidOperationException("Cave corridor face escaped its canonical cell ownership.");

        long aroundA = checked(owner.AroundOrigin + owner.AroundExtent / 2);
        long verticalA = checked(owner.VerticalOrigin + owner.VerticalExtent / 2);
        long acrossA = checked(owner.AcrossOrigin + owner.AcrossExtent / 2);
        long aroundB = checked(neighbor.AroundOrigin + neighbor.AroundExtent / 2);
        long verticalB = checked(neighbor.VerticalOrigin + neighbor.VerticalExtent / 2);
        long acrossB = checked(neighbor.AcrossOrigin + neighbor.AcrossExtent / 2);

        ulong key = MixIdentity(Identity, CorridorFeatureSalt);
        key = MixIdentity(key, connection.Face.Key);
        key = MixIdentity(key, plan.Province.Address.Around);
        key = MixIdentity(key, plan.Province.Address.Vertical);
        key = MixIdentity(key, plan.Province.Address.Across);
        key = MixIdentity(key, connection.TreeUses);
        key = MixIdentity(key, connection.LoopUses);
        key = MixIdentity(key, aroundA);
        key = MixIdentity(key, verticalA);
        key = MixIdentity(key, acrossA);
        key = MixIdentity(key, aroundB);
        key = MixIdentity(key, verticalB);
        key = MixIdentity(key, acrossB);
        return new CaveRegionFeature(
            CaveRegionFeatureKind.CorridorSegment,
            Avalanche(key),
            connection.Face.Key,
            plan.Province.Address,
            connection.TreeUses,
            connection.LoopUses,
            aroundA,
            verticalA,
            acrossA,
            aroundB,
            verticalB,
            acrossB);
    }

    private static void AddUniqueFeature(
        List<CaveRegionFeature> features,
        Dictionary<(CaveRegionFeatureKind Kind, ulong SourceKey), CaveRegionFeature> bySource,
        Dictionary<ulong, CaveRegionFeature> byKey,
        CaveRegionFeature feature)
    {
        var sourceIdentity = (feature.Kind, feature.SourceKey);
        if (bySource.TryGetValue(sourceIdentity, out CaveRegionFeature repeated))
        {
            if (repeated != feature)
                throw new InvalidOperationException("Repeated cave source produced conflicting region features.");
            return;
        }
        if (byKey.TryGetValue(feature.Key, out CaveRegionFeature collision))
        {
            if (collision != feature)
                throw new InvalidOperationException("Cave region feature key collision.");
            return;
        }
        if (features.Count >= MaximumFeatures)
            throw new InvalidOperationException("Cave region feature capture exceeded its bounded feature cap.");
        bySource.Add(sourceIdentity, feature);
        byKey.Add(feature.Key, feature);
        features.Add(feature);
    }

    private ulong CalculateSnapshotIdentity(
        CaveRegionFeatureRequest request,
        CapturedCandidate[] candidates,
        int activeProvinceCount,
        List<CaveRegionFeature> features)
    {
        ulong identity = MixIdentity(Identity, SnapshotSalt);
        identity = MixRequest(identity, request);
        identity = MixIdentity(identity, candidates.Length);
        identity = MixIdentity(identity, activeProvinceCount);
        foreach (CapturedCandidate candidate in candidates)
        {
            CaveProvincePlan plan = candidate.Plan;
            identity = MixIdentity(identity, plan.Province.Address.Around);
            identity = MixIdentity(identity, plan.Province.Address.Vertical);
            identity = MixIdentity(identity, plan.Province.Address.Across);
            identity = MixIdentity(identity, plan.Province.Key);
            identity = MixIdentity(identity, plan.Key);
            identity = MixIdentity(identity, plan.Active ? 1 : 0);
            identity = MixIdentity(identity, candidate.MapIdentity);
            if (plan.Bounds is CaveProvinceLocalCellBounds bounds)
            {
                identity = MixIdentity(identity, 1);
                identity = MixIdentity(identity, bounds.AroundStart);
                identity = MixIdentity(identity, bounds.AroundSpan);
                identity = MixIdentity(identity, bounds.VerticalStart);
                identity = MixIdentity(identity, bounds.VerticalSpan);
                identity = MixIdentity(identity, bounds.AcrossStart);
                identity = MixIdentity(identity, bounds.AcrossSpan);
            }
            else
            {
                identity = MixIdentity(identity, 0);
            }
        }
        identity = MixIdentity(identity, features.Count);
        foreach (CaveRegionFeature feature in features)
        {
            identity = MixIdentity(identity, (int)feature.Kind);
            identity = MixIdentity(identity, feature.Key);
            identity = MixIdentity(identity, feature.SourceKey);
            identity = MixIdentity(identity, feature.ProvinceAddress.Around);
            identity = MixIdentity(identity, feature.ProvinceAddress.Vertical);
            identity = MixIdentity(identity, feature.ProvinceAddress.Across);
            identity = MixIdentity(identity, feature.TreeUses);
            identity = MixIdentity(identity, feature.LoopUses);
            identity = MixIdentity(identity, feature.AroundA);
            identity = MixIdentity(identity, feature.VerticalA);
            identity = MixIdentity(identity, feature.AcrossA);
            identity = MixIdentity(identity, feature.AroundB);
            identity = MixIdentity(identity, feature.VerticalB);
            identity = MixIdentity(identity, feature.AcrossB);
        }
        return Avalanche(identity);
    }

    private static ulong MixRequest(ulong identity, CaveRegionFeatureRequest request)
    {
        identity = MixIdentity(identity, request.Around);
        identity = MixIdentity(identity, request.Vertical);
        identity = MixIdentity(identity, request.Across);
        identity = MixIdentity(identity, request.AroundExtent);
        identity = MixIdentity(identity, request.VerticalExtent);
        identity = MixIdentity(identity, request.AcrossExtent);
        return MixIdentity(identity, request.Halo);
    }

    private static bool Overlaps(
        CaveTopologyPartition partition,
        CaveRegionFeatureRequest request,
        CaveRegionFeature feature)
    {
        Int128 verticalLow = (Int128)request.Vertical - request.Halo;
        Int128 verticalHigh = (Int128)request.Vertical + request.VerticalExtent + request.Halo;
        Int128 acrossLow = (Int128)request.Across - request.Halo;
        Int128 acrossHigh = (Int128)request.Across + request.AcrossExtent + request.Halo;
        if (!SegmentOverlapsHalfOpen(feature.VerticalA, feature.VerticalB, verticalLow, verticalHigh)
            || !SegmentOverlapsHalfOpen(feature.AcrossA, feature.AcrossB, acrossLow, acrossHigh))
        {
            return false;
        }

        Int128 ringLength = partition.AroundLengthMeters;
        Int128 aroundLow = (Int128)request.Around - request.Halo;
        Int128 aroundHigh = (Int128)request.Around + request.AroundExtent + request.Halo;
        if (aroundHigh - aroundLow >= ringLength)
            return true;

        Int128 segmentA = feature.AroundA;
        Int128 segmentB = feature.AroundB;
        Int128 delta = segmentB - segmentA;
        if (delta * 2 > ringLength)
            segmentB -= ringLength;
        else if (delta * 2 < -ringLength)
            segmentB += ringLength;

        for (int shift = -1; shift <= 1; shift++)
        {
            Int128 offset = shift * ringLength;
            if (SegmentOverlapsHalfOpen(
                    segmentA + offset,
                    segmentB + offset,
                    aroundLow,
                    aroundHigh))
            {
                return true;
            }
        }
        return false;
    }

    private static bool SegmentOverlapsHalfOpen(
        Int128 first,
        Int128 second,
        Int128 low,
        Int128 high)
    {
        Int128 segmentLow = Int128.Min(first, second);
        Int128 segmentHigh = Int128.Max(first, second);
        return segmentHigh >= low && segmentLow < high;
    }

    private static CaveTopologyProvinceAddress[] ResolveCandidateProvinceAddressesCanonical(
        CaveTopologyPartition partition,
        CaveRegionFeatureRequest request)
    {
        List<long> aroundProvinces = ResolveAroundProvinceIndices(partition, request);
        long verticalLow = checked(request.Vertical - request.Halo);
        long verticalHighExclusive = checked(request.Vertical + request.VerticalExtent + request.Halo);
        long acrossLow = checked(request.Across - request.Halo);
        long acrossHighExclusive = checked(request.Across + request.AcrossExtent + request.Halo);
        long verticalFirst = FloorDiv(verticalLow, CaveTopologyPartition.ProvinceVerticalMeters);
        long verticalLast = FloorDiv(verticalHighExclusive - 1, CaveTopologyPartition.ProvinceVerticalMeters);
        long acrossFirst = FloorDiv(acrossLow, CaveTopologyPartition.ProvinceAcrossMeters);
        long acrossLast = FloorDiv(acrossHighExclusive - 1, CaveTopologyPartition.ProvinceAcrossMeters);
        Int128 candidateCount = (Int128)aroundProvinces.Count
            * (verticalLast - verticalFirst + 1)
            * (acrossLast - acrossFirst + 1);
        if (candidateCount > MaximumCandidateProvinces)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Cave region request overlaps {candidateCount} provinces; maximum is {MaximumCandidateProvinces}.");
        }

        var addresses = new List<CaveTopologyProvinceAddress>((int)candidateCount);
        foreach (long around in aroundProvinces)
        for (long vertical = verticalFirst; vertical <= verticalLast; vertical++)
        for (long across = acrossFirst; across <= acrossLast; across++)
            addresses.Add(new CaveTopologyProvinceAddress(around, vertical, across));
        addresses.Sort(CompareAddresses);
        return addresses.ToArray();
    }

    private static List<long> ResolveAroundProvinceIndices(
        CaveTopologyPartition partition,
        CaveRegionFeatureRequest request)
    {
        Int128 ringLength = partition.AroundLengthMeters;
        Int128 low = (Int128)request.Around - request.Halo;
        Int128 high = (Int128)request.Around + request.AroundExtent + request.Halo;
        var indices = new HashSet<long>();
        if (high - low >= ringLength)
        {
            if (partition.AroundProvinceCount > MaximumCandidateProvinces)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(request),
                    "Full-ring cave feature request exceeds the candidate-province cap.");
            }
            for (long index = 0; index < partition.AroundProvinceCount; index++)
                indices.Add(index);
        }
        else if (low < 0)
        {
            AddCanonicalAroundInterval(partition, indices, (long)(low + ringLength), partition.AroundLengthMeters);
            AddCanonicalAroundInterval(partition, indices, 0, (long)high);
        }
        else if (high > ringLength)
        {
            AddCanonicalAroundInterval(partition, indices, (long)low, partition.AroundLengthMeters);
            AddCanonicalAroundInterval(partition, indices, 0, (long)(high - ringLength));
        }
        else
        {
            AddCanonicalAroundInterval(partition, indices, (long)low, (long)high);
        }

        var ordered = new List<long>(indices);
        ordered.Sort();
        return ordered;
    }

    private static void AddCanonicalAroundInterval(
        CaveTopologyPartition partition,
        HashSet<long> indices,
        long low,
        long highExclusive)
    {
        if (low >= highExclusive)
            return;
        long first = partition.Resolve(low, 0, 0).Province.Address.Around;
        long last = partition.Resolve(highExclusive - 1, 0, 0).Province.Address.Around;
        for (long index = first; index <= last; index++)
            indices.Add(index);
    }

    private static CaveRegionFeatureRequest ValidateAndCanonicalize(
        CaveTopologyPartition partition,
        CaveRegionFeatureRequest request)
    {
        if (request.AroundExtent <= 0
            || request.VerticalExtent <= 0
            || request.AcrossExtent <= 0
            || request.AroundExtent > MaximumRegionExtentMeters
            || request.VerticalExtent > MaximumRegionExtentMeters
            || request.AcrossExtent > MaximumRegionExtentMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Cave region extents must be in [1, {MaximumRegionExtentMeters}] meters.");
        }
        if (request.Halo < 0 || request.Halo > MaximumInfluenceHaloMeters)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                $"Cave feature halo must be in [0, {MaximumInfluenceHaloMeters}] meters.");
        }

        Int128 verticalLow = (Int128)request.Vertical - request.Halo;
        Int128 verticalHigh = (Int128)request.Vertical + request.VerticalExtent + request.Halo;
        Int128 acrossLow = (Int128)request.Across - request.Halo;
        Int128 acrossHigh = (Int128)request.Across + request.AcrossExtent + request.Halo;
        if (verticalLow < long.MinValue
            || verticalHigh > long.MaxValue
            || acrossLow < long.MinValue
            || acrossHigh > long.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Cave region bounds and halo must fit signed 64-bit topology coordinates.");
        }

        return request with { Around = Mod(request.Around, partition.AroundLengthMeters) };
    }

    private static int CompareAddresses(
        CaveTopologyProvinceAddress left,
        CaveTopologyProvinceAddress right)
    {
        int around = left.Around.CompareTo(right.Around);
        if (around != 0) return around;
        int vertical = left.Vertical.CompareTo(right.Vertical);
        return vertical != 0 ? vertical : left.Across.CompareTo(right.Across);
    }

    private static int CompareFeatures(CaveRegionFeature left, CaveRegionFeature right)
    {
        int key = left.Key.CompareTo(right.Key);
        if (key != 0) return key;
        int kind = left.Kind.CompareTo(right.Kind);
        if (kind != 0) return kind;
        return left.SourceKey.CompareTo(right.SourceKey);
    }

    private static long FloorDiv(long value, long divisor) =>
        value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);

    private static long Mod(long value, long divisor)
    {
        long remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static ulong MixIdentity(ulong hash, long value) =>
        MixIdentity(hash, unchecked((ulong)value));

    private static ulong MixIdentity(ulong hash, int value) =>
        MixIdentity(hash, unchecked((ulong)(long)value));

    private static ulong MixIdentity(ulong hash, ulong value)
    {
        for (int index = 0; index < 8; index++)
        {
            hash ^= (byte)(value >> (index * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static ulong Avalanche(ulong value)
    {
        value ^= value >> 30;
        value *= 0xbf58476d1ce4e5b9UL;
        value ^= value >> 27;
        value *= 0x94d049bb133111ebUL;
        return value ^ (value >> 31);
    }

    private readonly record struct CapturedCandidate(CaveProvincePlan Plan, ulong MapIdentity);
    // Contract identity is intentionally part of the key even though one
    // generator instance serves one contract today. This prevents accidental
    // reuse if ownership is later lifted into a shared cache service.
    private readonly record struct ProvinceCacheKey(
        ulong CaveContractIdentity,
        ulong MapGeneratorIdentity,
        CaveTopologyProvinceAddress Address);
    private sealed class ProvinceTopologyArtifact
    {
        public ProvinceTopologyArtifact(CaveProvincePlan plan, ulong mapIdentity, CaveRegionFeature[] features)
        {
            Plan = plan;
            MapIdentity = mapIdentity;
            Features = features;
        }
        public CaveProvincePlan Plan { get; }
        public ulong MapIdentity { get; }
        public CaveRegionFeature[] Features { get; }
    }
}
