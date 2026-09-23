using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Stateless integer-only chamber-anchor selection for active cave envelopes.</summary>
public sealed class CaveChamberAnchorGenerator
{
    public const int Version = 1, StrataPerAxis = 4, CandidateCount = 64, TargetCellsPerAnchor = 256, MinimumAnchors = 4, MaximumAnchors = 24;
    public const int JitterInsetDivisor = 4;
    private const ulong IdentitySalt = 0x414E43484F525631UL;
    private const ulong AnchorKeySalt = 0x414E43484B455931UL;
    private const ulong PrioritySalt = 0x414E435052494F31UL;
    private const ulong SetSalt = 0x414E434853455431UL;
    private const ulong AroundJitterSalt = 0x41524F554E443031UL;
    private const ulong VerticalJitterSalt = 0x5645525449433031UL;
    private const ulong AcrossJitterSalt = 0x4143524F53533031UL;
    public CaveProvinceDistribution Distribution { get; }
    public ulong Identity { get; }
    public CaveChamberAnchorGenerator(CaveProvinceDistribution distribution)
    {
        Distribution = distribution ?? throw new ArgumentNullException(nameof(distribution));
        ulong identity = MixIdentity(distribution.Identity, IdentitySalt);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, StrataPerAxis);
        identity = MixIdentity(identity, CandidateCount);
        identity = MixIdentity(identity, TargetCellsPerAnchor);
        identity = MixIdentity(identity, MinimumAnchors);
        identity = MixIdentity(identity, MaximumAnchors);
        identity = MixIdentity(identity, JitterInsetDivisor);
        Identity = Avalanche(identity);
    }
    public CaveChamberAnchorSet Resolve(CaveProvincePlan plan)
    {
        CaveProvincePlan expected = Distribution.ResolvePlan(plan.Province.Address);
        if (plan != expected) throw new ArgumentException("Cave province plan does not belong to this distribution.", nameof(plan));
        if (!plan.Active)
            return new CaveChamberAnchorSet(Derive(SetSalt, plan.Key, 0), plan.Province.Address, Array.Empty<CaveChamberAnchor>());

        CaveProvinceLocalCellBounds b = plan.Bounds!.Value;
        int wanted = Math.Clamp((b.CellCount + TargetCellsPerAnchor - 1) / TargetCellsPerAnchor, MinimumAnchors, MaximumAnchors);
        var candidates = new List<Candidate>(CandidateCount);
        for (int slot = 0; slot < CandidateCount; slot++)
        {
            int a = slot % StrataPerAxis;
            int v = slot / StrataPerAxis % StrataPerAxis;
            int x = slot / (StrataPerAxis * StrataPerAxis);
            ulong key = Derive(AnchorKeySalt, plan.Key, (ulong)slot);
            ulong priority = Derive(PrioritySalt, plan.Key, (ulong)slot);
            long around = Position(plan.Province.AroundOrigin, b.AroundStart * CaveTopologyPartition.CellAroundMeters, b.AroundSpan * CaveTopologyPartition.CellAroundMeters, a, Derive(AroundJitterSalt, key, (ulong)slot));
            long vertical = Position(plan.Province.VerticalOrigin, b.VerticalStart * CaveTopologyPartition.CellVerticalMeters, b.VerticalSpan * CaveTopologyPartition.CellVerticalMeters, v, Derive(VerticalJitterSalt, key, (ulong)slot));
            long across = Position(plan.Province.AcrossOrigin, b.AcrossStart * CaveTopologyPartition.CellAcrossMeters, b.AcrossSpan * CaveTopologyPartition.CellAcrossMeters, x, Derive(AcrossJitterSalt, key, (ulong)slot));
            candidates.Add(new Candidate(slot, key, priority, around, vertical, across, Distribution.Partition.Resolve(around, vertical, across)));
        }
        candidates.Sort(static (l, r) => l.Priority != r.Priority ? l.Priority.CompareTo(r.Priority) : l.Slot.CompareTo(r.Slot));
        var anchors = new CaveChamberAnchor[wanted];
        for (int i = 0; i < wanted; i++)
        {
            Candidate c = candidates[i];
            if (!Distribution.PermitsDesignedCaveTopology(c.Owner))
                throw new InvalidOperationException("Generated chamber anchor escaped its eligible province envelope.");
            anchors[i] = new CaveChamberAnchor(i, c.Slot, c.Key, c.Priority, c.Around, c.Vertical, c.Across, plan.Province.Address, c.Owner.Address);
        }
        return new CaveChamberAnchorSet(Derive(SetSalt, plan.Key, (ulong)wanted), plan.Province.Address, anchors);
    }
    private static long Position(long origin, long offset, long length, int stratum, ulong jitter)
    {
        long start = checked(origin + offset + length * stratum / StrataPerAxis);
        long end = checked(origin + offset + length * (stratum + 1) / StrataPerAxis);
        long size = end - start;
        long margin = size / JitterInsetDivisor;
        long central = Math.Max(1, size - margin * 2);
        return checked(start + margin + (long)(jitter % (ulong)central));
    }
    private ulong Derive(ulong salt, ulong first, ulong second)
    {
        ulong hash = MixIdentity(Identity, salt);
        hash = MixIdentity(hash, first);
        hash = MixIdentity(hash, second);
        return Avalanche(hash);
    }
    private static ulong Avalanche(ulong value) { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL; value ^= value >> 27; value *= 0x94d049bb133111ebUL; return value ^ (value >> 31); }
    private static ulong MixIdentity(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (byte)(value >> (i * 8));
            hash *= 1099511628211UL;
        }
        return hash;
    }
    private readonly record struct Candidate(int Slot, ulong Key, ulong Priority, long Around, long Vertical, long Across, CaveTopologyCell Owner);
}
public readonly record struct CaveChamberAnchor(int Rank, int SlotIndex, ulong Key, ulong Priority, long Around, long Vertical, long Across, CaveTopologyProvinceAddress ProvinceAddress, CaveTopologyCellAddress OwningCellAddress);
public sealed class CaveChamberAnchorSet
{
    private readonly CaveChamberAnchor[] _anchors;
    public ulong Identity { get; }
    public CaveTopologyProvinceAddress ProvinceAddress { get; }
    internal CaveChamberAnchorSet(ulong identity, CaveTopologyProvinceAddress provinceAddress, CaveChamberAnchor[] anchors)
    {
        Identity = identity;
        ProvinceAddress = provinceAddress;
        _anchors = (CaveChamberAnchor[])anchors.Clone();
    }
    public int Count => _anchors.Length;
    public CaveChamberAnchor this[int index] => _anchors[index];
    public ReadOnlySpan<CaveChamberAnchor> AsSpan() => _anchors;
}
