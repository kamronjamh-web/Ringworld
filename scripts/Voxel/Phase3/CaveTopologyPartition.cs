using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>Immutable ownership partition for future designed cave topology.</summary>
public sealed class CaveTopologyPartition
{
    public const int Version = 1;
    public const long CellAroundMeters = 512, CellVerticalMeters = 256, CellAcrossMeters = 512;
    public const int CellsPerProvinceAxis = 32;
    public const long ProvinceAroundMeters = CellAroundMeters * CellsPerProvinceAxis;
    public const long ProvinceVerticalMeters = CellVerticalMeters * CellsPerProvinceAxis;
    public const long ProvinceAcrossMeters = CellAcrossMeters * CellsPerProvinceAxis;
    private const ulong PartitionSalt = 0x504152544954494FUL; // PARTITIO
    private const ulong CellSalt = 0x43454C4C4B455931UL;
    private const ulong ProvinceSalt = 0x50524F564B455931UL;

    public CaveGenerationContract Contract { get; }
    public long AroundLengthMeters { get; }
    public long AroundCellCount { get; }
    public long AroundProvinceCount { get; }
    public ulong Identity { get; }

    public CaveTopologyPartition(CaveGenerationContract contract, long aroundLengthMeters)
    {
        if (contract.Version != CaveGenerationContract.CurrentVersion
            || contract.Identity != CaveGenerationContract.CalculateIdentity(contract.TerrainSeed, contract.Version))
        {
            throw new ArgumentException("Cave topology requires a valid cave-generation contract.", nameof(contract));
        }
        if (aroundLengthMeters <= 0)
            throw new ArgumentOutOfRangeException(nameof(aroundLengthMeters));

        Contract = contract;
        AroundLengthMeters = aroundLengthMeters;
        AroundCellCount = checked((aroundLengthMeters - 1) / CellAroundMeters + 1);
        AroundProvinceCount = checked((AroundCellCount - 1) / CellsPerProvinceAxis + 1);
        Identity = contract.GetCoordinateSaltKey(aroundLengthMeters, Version, CellAroundMeters, PartitionSalt);
        Identity = Mix(Identity, (ulong)CellVerticalMeters);
        Identity = Mix(Identity, (ulong)CellAcrossMeters);
        Identity = Mix(Identity, CellsPerProvinceAxis);
    }

    public CaveTopologyCell Resolve(long around, long vertical, long across)
    {
        long canonicalAround = Mod(around, AroundLengthMeters);
        long aroundCell = canonicalAround / CellAroundMeters;
        long verticalCell = FloorDiv(vertical, CellVerticalMeters);
        long acrossCell = FloorDiv(across, CellAcrossMeters);
        long aroundOrigin = checked(aroundCell * CellAroundMeters);
        long verticalOrigin = checked(verticalCell * CellVerticalMeters);
        long acrossOrigin = checked(acrossCell * CellAcrossMeters);
        long aroundExtent = Math.Min(CellAroundMeters, AroundLengthMeters - aroundOrigin);
        var province = ResolveProvinceForCells(aroundCell, verticalCell, acrossCell);
        return new CaveTopologyCell(new CaveTopologyCellAddress(aroundCell, verticalCell, acrossCell),
            aroundOrigin, verticalOrigin, acrossOrigin, aroundExtent, CellVerticalMeters, CellAcrossMeters,
            province, checked((int)(aroundCell - province.Address.Around * CellsPerProvinceAxis)),
            LocalIndex(verticalCell), LocalIndex(acrossCell),
            CellKey(aroundCell, verticalCell, acrossCell));
    }

    public CaveTopologyCell ResolveNormalizedCell(long aroundCell, long verticalCell, long acrossCell)
    {
        return Resolve(checked(Mod(aroundCell, AroundCellCount) * CellAroundMeters), checked(verticalCell * CellVerticalMeters), checked(acrossCell * CellAcrossMeters));
    }

    public CaveTopologyProvince ResolveProvince(long around, long vertical, long across) => Resolve(around, vertical, across).Province;

    public CaveTopologyProvince ResolveNormalizedProvince(long aroundProvince, long verticalProvince, long acrossProvince)
    {
        long normalizedAround = Mod(aroundProvince, AroundProvinceCount);
        return ResolveProvince(checked(normalizedAround * ProvinceAroundMeters), checked(verticalProvince * ProvinceVerticalMeters), checked(acrossProvince * ProvinceAcrossMeters));
    }

    private CaveTopologyProvince ResolveProvinceForCells(long aroundCell, long verticalCell, long acrossCell)
    {
        long pa = aroundCell / CellsPerProvinceAxis;
        long pv = FloorDiv(verticalCell, CellsPerProvinceAxis);
        long px = FloorDiv(acrossCell, CellsPerProvinceAxis);
        long originCell = checked(pa * CellsPerProvinceAxis);
        return new CaveTopologyProvince(new CaveTopologyProvinceAddress(pa, pv, px), checked(originCell * CellAroundMeters), checked(pv * ProvinceVerticalMeters), checked(px * ProvinceAcrossMeters),
            Math.Min(ProvinceAroundMeters, AroundLengthMeters - originCell * CellAroundMeters), ProvinceVerticalMeters, ProvinceAcrossMeters,
            ProvinceKey(pa, pv, px));
    }

    private ulong CellKey(long a, long v, long x) => Mix(Contract.GetCoordinateSaltKey(a, v, x, CellSalt), Identity);
    private ulong ProvinceKey(long a, long v, long x) => Mix(Contract.GetCoordinateSaltKey(a, v, x, ProvinceSalt), Identity);
    private static long FloorDiv(long value, long divisor) => value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
    private static long Mod(long value, long divisor) { long r = value % divisor; return r < 0 ? r + divisor : r; }
    private static int LocalIndex(long cell) => (int)Mod(cell, CellsPerProvinceAxis);
    private static ulong Mix(ulong hash, ulong value) { for (int i = 0; i < 8; i++) { hash ^= (byte)(value >> (i * 8)); hash *= 1099511628211UL; } return hash; }
}

public readonly record struct CaveTopologyCellAddress(long Around, long Vertical, long Across);
public readonly record struct CaveTopologyProvinceAddress(long Around, long Vertical, long Across);
public readonly record struct CaveTopologyProvince(
    CaveTopologyProvinceAddress Address,
    long AroundOrigin,
    long VerticalOrigin,
    long AcrossOrigin,
    long AroundExtent,
    long VerticalExtent,
    long AcrossExtent,
    ulong Key);
public readonly record struct CaveTopologyCell(
    CaveTopologyCellAddress Address,
    long AroundOrigin,
    long VerticalOrigin,
    long AcrossOrigin,
    long AroundExtent,
    long VerticalExtent,
    long AcrossExtent,
    CaveTopologyProvince Province,
    int LocalAround,
    int LocalVertical,
    int LocalAcross,
    ulong Key);
