using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Versioned, fail-closed handoff from permitted designed-cave volumes to gameplay owners.</summary>
[Flags]
public enum CaveGameplayHandoff { None = 0, Water = 1 << 0, Navigation = 1 << 1, Resources = 1 << 2, PlayerSpawn = 1 << 3, AnchoredObjects = 1 << 4, SafeCheckpoint = 1 << 5 }

public readonly record struct CaveGameplayEligibility(bool IsEligible, CaveGameplayHandoff Handoffs, string Reason)
{
    public static CaveGameplayEligibility Rejected(string reason) => new(false, CaveGameplayHandoff.None, reason);
}

/// <summary>Stable downstream record. Consumers must use this instead of rerolling cave features.</summary>
public readonly record struct CaveGameplayFeatureRecord(CaveFeatureShape Shape, CaveGameplayEligibility Eligibility);

/// <summary>Immutable, bounded handoff snapshot. The source identity is the
/// authoritative cave field identity; mismatched fields are never diffed.</summary>
public sealed class CaveGameplayHandoffSnapshot
{
    public const int CurrentVersion = 1;
    public const int MaximumRecords = CaveRegionFeatureSnapshotGenerator.MaximumFeatures;
    private readonly CaveGameplayFeatureRecord[] _records;

    private CaveGameplayHandoffSnapshot(ulong sourceIdentity, CaveGameplayFeatureRecord[] records)
    {
        SourceIdentity = sourceIdentity;
        _records = records;
    }

    public int Version => CurrentVersion;
    public ulong SourceIdentity { get; }
    public ReadOnlySpan<CaveGameplayFeatureRecord> Records => _records;
    public bool IsValid => SourceIdentity != 0 && _records.Length <= MaximumRecords && IsOrderedAndValid(_records);

    public static bool TryCreate(ulong sourceIdentity, IEnumerable<CaveGameplayFeatureRecord> records, out CaveGameplayHandoffSnapshot snapshot)
    {
        snapshot = new CaveGameplayHandoffSnapshot(0, Array.Empty<CaveGameplayFeatureRecord>());
        if (sourceIdentity == 0 || records is null) return false;
        var sorted = new List<CaveGameplayFeatureRecord>();
        foreach (CaveGameplayFeatureRecord record in records)
        {
            if (sorted.Count >= MaximumRecords || !IsRecordValid(record)) return false;
            sorted.Add(record);
        }
        sorted.Sort(static (left, right) => left.Shape.Key.CompareTo(right.Shape.Key));
        if (!IsOrderedAndValid(sorted)) return false;
        snapshot = new CaveGameplayHandoffSnapshot(sourceIdentity, sorted.ToArray());
        return true;
    }

    private static bool IsOrderedAndValid(IReadOnlyList<CaveGameplayFeatureRecord> records)
    {
        ulong previous = 0;
        for (int index = 0; index < records.Count; index++)
        {
            if (!IsRecordValid(records[index]) || (index > 0 && records[index].Shape.Key <= previous)) return false;
            previous = records[index].Shape.Key;
        }
        return true;
    }

    private static bool IsRecordValid(CaveGameplayFeatureRecord record) => record.Shape.Key != 0
        && record.Eligibility.IsEligible
        && record.Eligibility.Handoffs != CaveGameplayHandoff.None
        && (record.Eligibility.Handoffs & ~KnownHandoffs) == CaveGameplayHandoff.None;

    internal const CaveGameplayHandoff ConsumerHandoffs = CaveGameplayHandoff.Water
        | CaveGameplayHandoff.Navigation | CaveGameplayHandoff.Resources
        | CaveGameplayHandoff.PlayerSpawn | CaveGameplayHandoff.AnchoredObjects;
    private const CaveGameplayHandoff KnownHandoffs = ConsumerHandoffs | CaveGameplayHandoff.SafeCheckpoint;
}

/// <summary>Immutable per-owner change list. Added and removed records are key sorted.</summary>
public sealed class CaveGameplayHandoffChangeSet
{
    private readonly CaveGameplayFeatureRecord[] _added;
    private readonly CaveGameplayFeatureRecord[] _removed;
    internal CaveGameplayHandoffChangeSet(CaveGameplayHandoff handoff, CaveGameplayFeatureRecord[] added, CaveGameplayFeatureRecord[] removed)
    {
        Handoff = handoff;
        _added = (CaveGameplayFeatureRecord[])added.Clone();
        _removed = (CaveGameplayFeatureRecord[])removed.Clone();
    }
    public CaveGameplayHandoff Handoff { get; }
    public ReadOnlySpan<CaveGameplayFeatureRecord> Added => _added;
    public ReadOnlySpan<CaveGameplayFeatureRecord> Removed => _removed;
}

/// <summary>Bounded delta for one deterministic handoff. An invalid delta is
/// intentionally empty so downstream owners retain their last safe state.</summary>
public sealed class CaveGameplayHandoffDelta
{
    private readonly CaveGameplayHandoffChangeSet[] _changes;
    internal CaveGameplayHandoffDelta(bool isValid, CaveGameplayHandoffChangeSet[] changes)
    {
        IsValid = isValid;
        _changes = changes;
    }
    public bool IsValid { get; }
    public ReadOnlySpan<CaveGameplayHandoffChangeSet> Changes => _changes;
    public bool IsEmpty
    {
        get
        {
            foreach (CaveGameplayHandoffChangeSet change in _changes)
                if (change.Added.Length != 0 || change.Removed.Length != 0) return false;
            return true;
        }
    }
}

public sealed class CaveGameplayIntegrationContract
{
    public const int Version = 1;
    public CaveGameplayEligibility Evaluate(bool designedCavesEnabled, CaveFeaturePlacementPolicy placement, CaveFeatureShape shape)
    {
        if (!designedCavesEnabled) return CaveGameplayEligibility.Rejected("designed cave density is disabled");
        if (!placement.Permits(shape)) return CaveGameplayEligibility.Rejected("feature is outside the protected cave domain");
        if (placement.FamilyWeight(shape.Family, shape.Vertical) <= 0.0) return CaveGameplayEligibility.Rejected("feature is outside its depth band");
        CaveGameplayHandoff handoffs = CaveGameplayHandoff.Navigation | CaveGameplayHandoff.Resources | CaveGameplayHandoff.AnchoredObjects;
        if (shape.Family is CaveFeatureFamily.GrandChamber or CaveFeatureFamily.Megacavern or CaveFeatureFamily.SupermassiveCavern or CaveFeatureFamily.CathedralCavern or CaveFeatureFamily.PillarHall)
            handoffs |= CaveGameplayHandoff.PlayerSpawn | CaveGameplayHandoff.SafeCheckpoint;
        if (shape.Family == CaveFeatureFamily.UndergroundBasin || shape.Variant == CaveFeatureVariant.Reservoir) handoffs |= CaveGameplayHandoff.Water;
        if (shape.Family == CaveFeatureFamily.CollapsedRegion) handoffs &= ~(CaveGameplayHandoff.PlayerSpawn | CaveGameplayHandoff.SafeCheckpoint | CaveGameplayHandoff.AnchoredObjects);
        return new(true, handoffs, "permitted cave feature");
    }

    public IReadOnlyList<CaveGameplayFeatureRecord> ResolveEligibleFeatures(
        RingTerrainSampler sampler, bool designedCavesEnabled, CaveRegionFeatureRequest request)
    {
        if (sampler is null) throw new ArgumentNullException(nameof(sampler));
        if (!designedCavesEnabled) return Array.Empty<CaveGameplayFeatureRecord>();
        CaveFeatureFamilySnapshot shapes = sampler.CaveTopologyDensityComposer.Families.Resolve(
            sampler.CaveRegionFeatureSnapshotGenerator.Capture(request));
        var result = new List<CaveGameplayFeatureRecord>(shapes.Count);
        foreach (CaveFeatureShape shape in shapes.AsSpan())
        {
            CaveGameplayEligibility eligibility = Evaluate(true, sampler.CavePlacementPolicy, shape);
            if (eligibility.IsEligible) result.Add(new CaveGameplayFeatureRecord(shape, eligibility));
        }
        result.Sort(static (left, right) => left.Shape.Key.CompareTo(right.Shape.Key));
        return result;
    }

    public CaveGameplayHandoffSnapshot CaptureHandoffSnapshot(
        RingTerrainSampler sampler, bool designedCavesEnabled, CaveRegionFeatureRequest request)
    {
        if (sampler is null) throw new ArgumentNullException(nameof(sampler));
        IReadOnlyList<CaveGameplayFeatureRecord> records = ResolveEligibleFeatures(sampler, designedCavesEnabled, request);
        if (!CaveGameplayHandoffSnapshot.TryCreate(sampler.CaveTopologyDensityComposer.Identity, records, out CaveGameplayHandoffSnapshot snapshot))
            throw new InvalidOperationException("Cave gameplay handoff snapshot exceeded its fail-closed contract.");
        return snapshot;
    }

    public bool TryCreateIncrementalHandoff(
        CaveGameplayHandoffSnapshot previous,
        CaveGameplayHandoffSnapshot current,
        out CaveGameplayHandoffDelta delta)
    {
        delta = new CaveGameplayHandoffDelta(false, Array.Empty<CaveGameplayHandoffChangeSet>());
        if (previous is null || current is null || !previous.IsValid || !current.IsValid
            || previous.Version != CaveGameplayHandoffSnapshot.CurrentVersion
            || current.Version != CaveGameplayHandoffSnapshot.CurrentVersion
            || previous.SourceIdentity != current.SourceIdentity)
            return false;

        var changes = new List<CaveGameplayHandoffChangeSet>(5);
        foreach (CaveGameplayHandoff handoff in new[]
        {
            CaveGameplayHandoff.Water, CaveGameplayHandoff.Navigation,
            CaveGameplayHandoff.Resources, CaveGameplayHandoff.PlayerSpawn,
            CaveGameplayHandoff.AnchoredObjects
        })
        {
            var added = new List<CaveGameplayFeatureRecord>();
            var removed = new List<CaveGameplayFeatureRecord>();
            int oldIndex = 0, newIndex = 0;
            ReadOnlySpan<CaveGameplayFeatureRecord> oldRecords = previous.Records;
            ReadOnlySpan<CaveGameplayFeatureRecord> newRecords = current.Records;
            while (oldIndex < oldRecords.Length || newIndex < newRecords.Length)
            {
                if (newIndex >= newRecords.Length || (oldIndex < oldRecords.Length && oldRecords[oldIndex].Shape.Key < newRecords[newIndex].Shape.Key))
                {
                    AddIfHandoff(removed, oldRecords[oldIndex++], handoff);
                }
                else if (oldIndex >= oldRecords.Length || newRecords[newIndex].Shape.Key < oldRecords[oldIndex].Shape.Key)
                {
                    AddIfHandoff(added, newRecords[newIndex++], handoff);
                }
                else
                {
                    CaveGameplayFeatureRecord oldRecord = oldRecords[oldIndex++];
                    CaveGameplayFeatureRecord newRecord = newRecords[newIndex++];
                    if (oldRecord != newRecord)
                    {
                        AddIfHandoff(removed, oldRecord, handoff);
                        AddIfHandoff(added, newRecord, handoff);
                    }
                }
            }
            changes.Add(new CaveGameplayHandoffChangeSet(handoff, added.ToArray(), removed.ToArray()));
        }
        delta = new CaveGameplayHandoffDelta(true, changes.ToArray());
        return true;
    }

    private static void AddIfHandoff(List<CaveGameplayFeatureRecord> output, CaveGameplayFeatureRecord record, CaveGameplayHandoff handoff)
    {
        if (record.Eligibility.Handoffs.HasFlag(handoff)) output.Add(record);
    }
}

public static class CaveGameplayIntegrationDiagnostics
{
    public static bool RunSelfTest(out string summary)
    {
        try
        {
            var policy = new CaveFeaturePlacementPolicy(50_000); var contract = new CaveGameplayIntegrationContract();
            CaveGameplayEligibility enabled = contract.Evaluate(true, policy, Shape(CaveFeatureFamily.GrandChamber, CaveFeatureVariant.None, -900));
            if (!enabled.IsEligible || !enabled.Handoffs.HasFlag(CaveGameplayHandoff.Navigation) || !enabled.Handoffs.HasFlag(CaveGameplayHandoff.PlayerSpawn) || !enabled.Handoffs.HasFlag(CaveGameplayHandoff.SafeCheckpoint)) throw new InvalidOperationException("permitted grand chamber handoff failed");
            if (contract.Evaluate(false, policy, Shape(CaveFeatureFamily.GrandChamber, CaveFeatureVariant.None, -900)).IsEligible) throw new InvalidOperationException("disabled cave layer leaked gameplay eligibility");
            if (!contract.Evaluate(true, policy, Shape(CaveFeatureFamily.UndergroundBasin, CaveFeatureVariant.None, -1200)).Handoffs.HasFlag(CaveGameplayHandoff.Water)) throw new InvalidOperationException("basin water handoff missing");
            if (contract.Evaluate(true, policy, Shape(CaveFeatureFamily.CollapsedRegion, CaveFeatureVariant.None, -180)).Handoffs.HasFlag(CaveGameplayHandoff.SafeCheckpoint)) throw new InvalidOperationException("collapsed feature exposed recovery checkpoint");
            CertifyIncrementalHandoff(contract, policy);
            summary = "version=1 water/nav/resources/spawn/anchors/checkpoints are fail-closed, family-scoped, bounded, and incremental"; return true;
        }
        catch (Exception error) { summary = error.Message; return false; }
    }

    private static void CertifyIncrementalHandoff(CaveGameplayIntegrationContract contract, CaveFeaturePlacementPolicy policy)
    {
        CaveGameplayFeatureRecord basin = new(Shape(CaveFeatureFamily.UndergroundBasin, CaveFeatureVariant.None, -1200, 30), contract.Evaluate(true, policy, Shape(CaveFeatureFamily.UndergroundBasin, CaveFeatureVariant.None, -1200, 30)));
        CaveGameplayFeatureRecord grand = new(Shape(CaveFeatureFamily.GrandChamber, CaveFeatureVariant.None, -900, 10), contract.Evaluate(true, policy, Shape(CaveFeatureFamily.GrandChamber, CaveFeatureVariant.None, -900, 10)));
        CaveGameplayFeatureRecord collapsed = new(Shape(CaveFeatureFamily.CollapsedRegion, CaveFeatureVariant.None, -180, 20), contract.Evaluate(true, policy, Shape(CaveFeatureFamily.CollapsedRegion, CaveFeatureVariant.None, -180, 20)));
        if (!CaveGameplayHandoffSnapshot.TryCreate(0xABCDUL, Array.Empty<CaveGameplayFeatureRecord>(), out CaveGameplayHandoffSnapshot empty)
            || !CaveGameplayHandoffSnapshot.TryCreate(0xABCDUL, new[] { basin, collapsed, grand }, out CaveGameplayHandoffSnapshot populated))
            throw new InvalidOperationException("valid bounded gameplay handoff snapshot was rejected");
        if (!contract.TryCreateIncrementalHandoff(empty, populated, out CaveGameplayHandoffDelta added) || !added.IsValid)
            throw new InvalidOperationException("initial gameplay handoff delta was rejected");
        AssertChange(added, CaveGameplayHandoff.Water, added: new[] { 30UL }, removed: Array.Empty<ulong>());
        AssertChange(added, CaveGameplayHandoff.Navigation, added: new[] { 10UL, 20UL, 30UL }, removed: Array.Empty<ulong>());
        AssertChange(added, CaveGameplayHandoff.Resources, added: new[] { 10UL, 20UL, 30UL }, removed: Array.Empty<ulong>());
        AssertChange(added, CaveGameplayHandoff.PlayerSpawn, added: new[] { 10UL }, removed: Array.Empty<ulong>());
        AssertChange(added, CaveGameplayHandoff.AnchoredObjects, added: new[] { 10UL, 30UL }, removed: Array.Empty<ulong>());
        if (!contract.TryCreateIncrementalHandoff(populated, populated, out CaveGameplayHandoffDelta unchanged) || !unchanged.IsValid || !unchanged.IsEmpty)
            throw new InvalidOperationException("unchanged gameplay snapshots produced a non-empty delta");
        if (!CaveGameplayHandoffSnapshot.TryCreate(0xABCDUL, new[] { grand }, out CaveGameplayHandoffSnapshot reduced)
            || !contract.TryCreateIncrementalHandoff(populated, reduced, out CaveGameplayHandoffDelta removed))
            throw new InvalidOperationException("reduced gameplay handoff delta was rejected");
        AssertChange(removed, CaveGameplayHandoff.Water, added: Array.Empty<ulong>(), removed: new[] { 30UL });
        AssertChange(removed, CaveGameplayHandoff.Navigation, added: Array.Empty<ulong>(), removed: new[] { 20UL, 30UL });
        AssertChange(removed, CaveGameplayHandoff.Resources, added: Array.Empty<ulong>(), removed: new[] { 20UL, 30UL });
        AssertChange(removed, CaveGameplayHandoff.PlayerSpawn, added: Array.Empty<ulong>(), removed: Array.Empty<ulong>());
        AssertChange(removed, CaveGameplayHandoff.AnchoredObjects, added: Array.Empty<ulong>(), removed: new[] { 30UL });
        if (CaveGameplayHandoffSnapshot.TryCreate(0xABCDUL, new[] { grand, grand }, out _)
            || !CaveGameplayHandoffSnapshot.TryCreate(0xDCBAUL, new[] { grand }, out CaveGameplayHandoffSnapshot foreign)
            || contract.TryCreateIncrementalHandoff(populated, foreign, out CaveGameplayHandoffDelta rejected)
            || rejected.IsValid || !rejected.IsEmpty)
            throw new InvalidOperationException("invalid gameplay handoff input was accepted");
    }

    private static void AssertChange(CaveGameplayHandoffDelta delta, CaveGameplayHandoff handoff, ulong[] added, ulong[] removed)
    {
        foreach (CaveGameplayHandoffChangeSet change in delta.Changes)
        {
            if (change.Handoff != handoff) continue;
            if (!KeysEqual(change.Added, added) || !KeysEqual(change.Removed, removed))
                throw new InvalidOperationException($"{handoff} handoff delta was not stable or complete");
            return;
        }
        throw new InvalidOperationException($"{handoff} handoff delta was missing");
    }

    private static bool KeysEqual(ReadOnlySpan<CaveGameplayFeatureRecord> records, ulong[] keys)
    {
        if (records.Length != keys.Length) return false;
        for (int index = 0; index < keys.Length; index++)
            if (records[index].Shape.Key != keys[index]) return false;
        return true;
    }

    private static CaveFeatureShape Shape(CaveFeatureFamily family, CaveFeatureVariant variant, long vertical, ulong key = 1) => new(family, variant, key, key, new CaveTopologyProvinceAddress(0, 0, 0), 25_000, vertical, 25_000, 100, 80, 100, 0, 1, default);
}
