using System;

namespace RingworldTechDemo.Voxel.Phase3;

public enum CaveLandmarkOperation { Dome, Terrace, RibbedGallery, Basin, Buttress, Ruin }

/// <summary>Small immutable authored SDF-template descriptor, selected from a family-owned key.</summary>
public readonly record struct CaveLandmarkTemplate(
    string StableId, CaveFeatureFamily Family, CaveLandmarkOperation Operation,
    double BoundAround, double BoundVertical, double BoundAcross)
{
    public bool IsDefined => !string.IsNullOrEmpty(StableId);
}

/// <summary>
/// Versioned catalog which provides recognizable authored silhouettes without
/// turning template selection into mutable world state.  Bounds are fractions
/// of the selected family's conservative descriptor radii.
/// </summary>
public sealed class CaveLandmarkTemplateCatalog
{
    public const int Version = 1;
    private static readonly CaveLandmarkTemplate[] Templates =
    {
        new("grand.terraced_rotunda.v1", CaveFeatureFamily.GrandChamber, CaveLandmarkOperation.Terrace, .70, .55, .70),
        new("grand.buttress_hall.v1", CaveFeatureFamily.GrandChamber, CaveLandmarkOperation.Buttress, .78, .62, .55),
        new("mega.dome_bowl.v1", CaveFeatureFamily.Megacavern, CaveLandmarkOperation.Dome, .72, .68, .72),
        new("super.ancient_ruin.v1", CaveFeatureFamily.SupermassiveCavern, CaveLandmarkOperation.Ruin, .22, .16, .22),
        new("fault.strata_buttress.v1", CaveFeatureFamily.FaultCavern, CaveLandmarkOperation.Buttress, .55, .48, .70),
        new("pillar.processional.v1", CaveFeatureFamily.PillarHall, CaveLandmarkOperation.Buttress, .60, .55, .60),
        new("chasm.bridge_landing.v1", CaveFeatureFamily.ChasmSystem, CaveLandmarkOperation.Terrace, .45, .20, .48),
        new("basin.shore_terrace.v1", CaveFeatureFamily.UndergroundBasin, CaveLandmarkOperation.Basin, .80, .24, .80),
        new("cathedral.apse.v1", CaveFeatureFamily.CathedralCavern, CaveLandmarkOperation.Dome, .58, .72, .58),
        new("collapse.rubble_shrine.v1", CaveFeatureFamily.CollapsedRegion, CaveLandmarkOperation.Ruin, .36, .26, .36),
        new("engineered.ribbed_station.v1", CaveFeatureFamily.EngineeredVoid, CaveLandmarkOperation.RibbedGallery, .62, .55, .62)
    };

    public ulong Identity { get; }
    public CaveLandmarkTemplateCatalog()
    {
        ulong hash = 14695981039346656037UL;
        hash = Mix(hash, Version);
        foreach (CaveLandmarkTemplate item in Templates)
        {
            foreach (char c in item.StableId) hash = Mix(hash, c);
            hash = Mix(hash, (int)item.Family); hash = Mix(hash, (int)item.Operation);
            hash = Mix(hash, BitConverter.DoubleToInt64Bits(item.BoundAround));
            hash = Mix(hash, BitConverter.DoubleToInt64Bits(item.BoundVertical));
            hash = Mix(hash, BitConverter.DoubleToInt64Bits(item.BoundAcross));
        }
        Identity = Avalanche(hash);
    }

    public CaveLandmarkTemplate Resolve(CaveFeatureShape shape) =>
        Resolve(shape.Family, shape.Key);

    public CaveLandmarkTemplate Resolve(CaveFeatureFamily family, ulong key)
    {
        Span<int> matches = stackalloc int[Templates.Length]; int count = 0;
        for (int i = 0; i < Templates.Length; i++) if (Templates[i].Family == family) matches[count++] = i;
        if (count == 0) throw new InvalidOperationException("Family has no authored landmark template.");
        return Templates[(int)(Avalanche(key ^ Identity) % (ulong)count) switch { int pick => matches[pick] }];
    }
    private static ulong Mix(ulong hash, long word) { ulong u = unchecked((ulong)word); for (int i = 0; i < 8; i++) { hash ^= (byte)(u >> (i * 8)); hash *= 1099511628211UL; } return hash; }
    private static ulong Avalanche(ulong value) { value ^= value >> 30; value *= 0xbf58476d1ce4e5b9UL; value ^= value >> 27; value *= 0x94d049bb133111ebUL; return value ^ (value >> 31); }
}
