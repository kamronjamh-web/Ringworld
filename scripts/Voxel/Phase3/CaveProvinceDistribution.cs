using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Immutable v1 eligibility mask for bounded designed-cave provinces.</summary>
public sealed class CaveProvinceDistribution
{
    public const int Version = 1;
    public const int InsetCells = 2;
    public const int ActivationDenominator = 8;
    public const int MinimumAroundSpan = 8, MaximumAroundSpan = 24;
    public const int MinimumVerticalSpan = 4, MaximumVerticalSpan = 16;
    public const int MinimumAcrossSpan = 8, MaximumAcrossSpan = 24;
    private const ulong IdentitySalt = 0x4D41534B56310001UL;
    public CaveTopologyPartition Partition { get; }
    public ulong Identity { get; }

    public CaveProvinceDistribution(CaveTopologyPartition partition)
    {
        Partition = partition ?? throw new ArgumentNullException(nameof(partition));
        ulong identity = MixIdentity(partition.Identity, IdentitySalt);
        identity = MixIdentity(identity, Version);
        identity = MixIdentity(identity, InsetCells);
        identity = MixIdentity(identity, ActivationDenominator);
        identity = MixIdentity(identity, MinimumAroundSpan);
        identity = MixIdentity(identity, MaximumAroundSpan);
        identity = MixIdentity(identity, MinimumVerticalSpan);
        identity = MixIdentity(identity, MaximumVerticalSpan);
        identity = MixIdentity(identity, MinimumAcrossSpan);
        identity = MixIdentity(identity, MaximumAcrossSpan);
        Identity = Avalanche(identity);
    }

    public CaveProvincePlan ResolvePlan(CaveTopologyProvince province)
    {
        CaveTopologyProvince ownedProvince = Partition.ResolveNormalizedProvince(
            province.Address.Around,
            province.Address.Vertical,
            province.Address.Across);
        if (ownedProvince != province)
            throw new ArgumentException("Cave province belongs to a different topology partition.", nameof(province));

        ulong selection = Avalanche(province.Key ^ Identity);
        int aroundCapacity = checked((int)((province.AroundExtent - 1) / CaveTopologyPartition.CellAroundMeters + 1));
        if (aroundCapacity < InsetCells * 2 + MinimumAroundSpan
            || selection % ActivationDenominator != 0)
            return new CaveProvincePlan(province, selection, false, null);

        int aroundMaximumSpan = Math.Min(MaximumAroundSpan, aroundCapacity - InsetCells * 2);
        int aroundSpan = PickSpan(selection, 0, MinimumAroundSpan, aroundMaximumSpan);
        int verticalSpan = PickSpan(selection, 17, MinimumVerticalSpan, MaximumVerticalSpan);
        int acrossSpan = PickSpan(selection, 34, MinimumAcrossSpan, MaximumAcrossSpan);
        var bounds = new CaveProvinceLocalCellBounds(
            PickStart(selection, 0, aroundSpan, aroundCapacity), aroundSpan,
            PickStart(selection, 17, verticalSpan, CaveTopologyPartition.CellsPerProvinceAxis), verticalSpan,
            PickStart(selection, 34, acrossSpan, CaveTopologyPartition.CellsPerProvinceAxis), acrossSpan);
        return new CaveProvincePlan(province, selection, true, bounds);
    }

    public CaveProvincePlan ResolvePlan(long around, long vertical, long across) => ResolvePlan(Partition.ResolveProvince(around, vertical, across));
    public CaveProvincePlan ResolvePlan(CaveTopologyProvinceAddress address) => ResolvePlan(Partition.ResolveNormalizedProvince(address.Around, address.Vertical, address.Across));
    public bool PermitsDesignedCaveTopology(CaveTopologyCell cell)
    {
        CaveTopologyCell ownedCell = Partition.ResolveNormalizedCell(
            cell.Address.Around,
            cell.Address.Vertical,
            cell.Address.Across);
        if (ownedCell != cell)
            throw new ArgumentException("Cave cell belongs to a different topology partition.", nameof(cell));

        CaveProvincePlan plan = ResolvePlan(cell.Province);
        return plan.Active && plan.Bounds!.Value.Contains(cell.LocalAround, cell.LocalVertical, cell.LocalAcross);
    }
    public bool IsSolidByDefault(CaveTopologyCell cell) => !PermitsDesignedCaveTopology(cell);

    private static int PickSpan(ulong key, int shift, int min, int max) => min + (int)((key >> shift) % (uint)(max - min + 1));
    private static int PickStart(ulong key, int shift, int span, int capacity)
    {
        int positions = capacity - InsetCells * 2 - span + 1;
        return InsetCells + (int)((key >> (shift + 7)) % (uint)positions);
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
}

public readonly record struct CaveProvinceLocalCellBounds(int AroundStart, int AroundSpan, int VerticalStart, int VerticalSpan, int AcrossStart, int AcrossSpan)
{
    public int AroundEndExclusive => AroundStart + AroundSpan;
    public int VerticalEndExclusive => VerticalStart + VerticalSpan;
    public int AcrossEndExclusive => AcrossStart + AcrossSpan;
    public int CellCount => checked(AroundSpan * VerticalSpan * AcrossSpan);
    public bool Contains(int around, int vertical, int across) => around >= AroundStart && around < AroundStart + AroundSpan && vertical >= VerticalStart && vertical < VerticalStart + VerticalSpan && across >= AcrossStart && across < AcrossStart + AcrossSpan;
}
public readonly record struct CaveProvincePlan(CaveTopologyProvince Province, ulong Key, bool Active, CaveProvinceLocalCellBounds? Bounds);
