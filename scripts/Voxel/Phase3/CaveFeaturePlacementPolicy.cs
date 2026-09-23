using System;

namespace RingworldTechDemo.Voxel.Phase3;

/// <summary>
/// Immutable world-placement contract for designed caves.  This is deliberately
/// separate from an SDF family: it answers whether a complete feature volume is
/// allowed to exist at a coordinate, rather than clipping it after it has
/// already cut a structural boundary.
/// </summary>
public sealed class CaveFeaturePlacementPolicy
{
    public const int Version = 1;
    public const long SurfaceClearanceMeters = 64;
    public const long RoofClearanceMeters = 128;
    public const long SidewallClearanceMeters = 128;
    public const long LowerHullClearanceMeters = 128;
    public const long SupermassiveMacroMeters = 8192;
    public const long SupermassiveMinimumSpacingMeters = SupermassiveMacroMeters * 2;
    public const long DefaultTerrainBottomMeters = -8192;
    public const long DefaultProtectedRoofTopMeters = -7680;
    public const long DefaultLowerHullTopMeters = -21504;
    public const long DefaultStructuralBottomMeters = -22304;

    public int AcrossWidthMeters { get; }
    public long SurfaceFloorMeters { get; }
    public long TerrainBottomMeters { get; }
    public long ProtectedRoofTopMeters { get; }
    public long LowerHullTopMeters { get; }
    public long StructuralBottomMeters { get; }
    public bool IsEnforced { get; }
    public ulong Identity { get; }

    public CaveFeaturePlacementPolicy(
        int acrossWidthMeters,
        long terrainBottomMeters = DefaultTerrainBottomMeters,
        long protectedRoofTopMeters = DefaultProtectedRoofTopMeters,
        long lowerHullTopMeters = DefaultLowerHullTopMeters,
        long structuralBottomMeters = DefaultStructuralBottomMeters,
        long surfaceFloorMeters = 0,
        bool isEnforced = true)
    {
        if (acrossWidthMeters <= 0) throw new ArgumentOutOfRangeException(nameof(acrossWidthMeters));
        if (protectedRoofTopMeters <= terrainBottomMeters
            || lowerHullTopMeters >= terrainBottomMeters
            || structuralBottomMeters >= lowerHullTopMeters
            || surfaceFloorMeters <= protectedRoofTopMeters)
            throw new ArgumentException("Invalid cave structural placement domain.");
        AcrossWidthMeters = acrossWidthMeters;
        SurfaceFloorMeters = surfaceFloorMeters;
        TerrainBottomMeters = terrainBottomMeters;
        ProtectedRoofTopMeters = protectedRoofTopMeters;
        LowerHullTopMeters = lowerHullTopMeters;
        StructuralBottomMeters = structuralBottomMeters;
        IsEnforced = isEnforced;
        ulong hash = 14695981039346656037UL;
        foreach (long word in new[] { (long)Version, acrossWidthMeters, surfaceFloorMeters, terrainBottomMeters, protectedRoofTopMeters, lowerHullTopMeters, structuralBottomMeters, SurfaceClearanceMeters, RoofClearanceMeters, SidewallClearanceMeters, LowerHullClearanceMeters, SupermassiveMacroMeters, SupermassiveMinimumSpacingMeters, isEnforced ? 1L : 0L })
            hash = Mix(hash, word);
        Identity = Avalanche(hash);
    }

    /// <summary>Default only for isolated topology tests; game samplers always supply ring width.</summary>
    public static CaveFeaturePlacementPolicy UnboundedForDiagnostics { get; } = new(
        1_000_000_000, isEnforced: false);

    public bool Permits(CaveFeatureShape shape)
    {
        if (!IsEnforced) return true;
        // Conservatively include the tilted vertical extent and all authored
        // operation protrusions inside the descriptor radius envelope.
        double verticalExtent = Math.Abs(Math.Sin(shape.Tilt)) * shape.RadiusAround
            + Math.Abs(Math.Cos(shape.Tilt)) * shape.RadiusVertical;
        double upper = shape.Vertical + verticalExtent;
        double lower = shape.Vertical - verticalExtent;
        return upper <= SurfaceFloorMeters - SurfaceClearanceMeters
            && lower >= ProtectedRoofTopMeters + RoofClearanceMeters
            && lower >= LowerHullTopMeters + LowerHullClearanceMeters
            && shape.Across - shape.RadiusAcross >= SidewallClearanceMeters
            && shape.Across + shape.RadiusAcross <= AcrossWidthMeters - SidewallClearanceMeters;
    }

    public bool PermitsConnectivity(CaveRegionFeature feature, double radius)
    {
        if (!IsEnforced) return true;
        return EndpointPermitted(feature.VerticalA, feature.AcrossA, radius)
            && EndpointPermitted(feature.VerticalB, feature.AcrossB, radius);
    }

    /// <summary>
    /// Smooth depth preferences for the documented bands.  Depth is measured
    /// down from canonical surface Y=0, with 128 m blend zones at each edge.
    /// The returned weight is intentionally continuous, so crossing a band
    /// cannot abruptly turn a selected family into another one.
    /// </summary>
    public double FamilyWeight(CaveFeatureFamily family, long vertical)
    {
        if (!IsEnforced) return 1.0;
        double depth = -vertical;
        return family switch
        {
            CaveFeatureFamily.CollapsedRegion => Band(depth, 24, 300),
            CaveFeatureFamily.GrandChamber => Math.Max(Band(depth, 24, 300), Band(depth, 300, 2000)),
            CaveFeatureFamily.PillarHall or CaveFeatureFamily.CathedralCavern or CaveFeatureFamily.UndergroundBasin => Band(depth, 300, 2000),
            CaveFeatureFamily.FaultCavern => Math.Max(Band(depth, 2000, 4500), Band(depth, 4500, 7000)),
            CaveFeatureFamily.ChasmSystem or CaveFeatureFamily.EngineeredVoid => Band(depth, 2000, 4500),
            CaveFeatureFamily.Megacavern => Math.Max(Band(depth, 2000, 4500), Band(depth, 4500, 7000)),
            CaveFeatureFamily.SupermassiveCavern => Band(depth, 4500, 7000),
            _ => 0.0
        };
    }

    /// <summary>
    /// Supermassive eligibility is a deterministic horizontal macro lattice,
    /// not a meshing-chunk roll. The usable cave depth is less than one macro
    /// cell, so vertical parity would incorrectly disable this family for half
    /// of all seeds. Horizontal opportunities remain at least 16 km apart.
    /// </summary>
    public bool OwnsSupermassiveMacro(CaveRegionFeature anchor, ulong worldIdentity)
    {
        if (!IsEnforced) return true;
        long a = FloorDiv(anchor.AroundA, SupermassiveMacroMeters);
        long x = FloorDiv(anchor.AcrossA, SupermassiveMacroMeters);
        ulong key = Avalanche(worldIdentity ^ 0x53555045524D4143UL);
        int phaseA = (int)(key & 1UL);
        int phaseX = (int)((key >> 1) & 1UL);
        // A fixed parity lattice has a minimum 16 km separation; phase only
        // changes which macro cells own the opportunity for this world.
        return Mod(a - phaseA, 2) == 0 && Mod(x - phaseX, 2) == 0;
    }

    private bool EndpointPermitted(long vertical, long across, double radius) =>
        vertical + radius <= SurfaceFloorMeters - SurfaceClearanceMeters
        && vertical - radius >= ProtectedRoofTopMeters + RoofClearanceMeters
        && vertical - radius >= LowerHullTopMeters + LowerHullClearanceMeters
        && across - radius >= SidewallClearanceMeters
        && across + radius <= AcrossWidthMeters - SidewallClearanceMeters;

    public static double Band(double depth, double start, double end)
    {
        const double blend = 128.0;
        if (depth <= start - blend || depth >= end + blend) return 0.0;
        double enter = Math.Clamp((depth - (start - blend)) / blend, 0.0, 1.0);
        double leave = Math.Clamp(((end + blend) - depth) / blend, 0.0, 1.0);
        return enter * enter * (3.0 - 2.0 * enter) * leave * leave * (3.0 - 2.0 * leave);
    }

    private static ulong Mix(ulong hash, long value)
    {
        ulong word = unchecked((ulong)value);
        for (int i = 0; i < 8; i++) { hash ^= (byte)(word >> (i * 8)); hash *= 1099511628211UL; }
        return hash;
    }
    private static long FloorDiv(long value, long divisor) => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
    private static long Mod(long value, long divisor) { long r = value % divisor; return r < 0 ? r + divisor : r; }
    private static ulong Avalanche(ulong value) { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL; value ^= value >> 27; value *= 0x94d049bb133111ebUL; return value ^ (value >> 31); }
}
