namespace RingworldTechDemo.Underground;

/// <summary>Transient cave LOD profile applied over the user's terrain streaming settings while underground.</summary>
public readonly record struct CaveLodProfile(int CoarseRadiusAround, int CoarseRadiusAcross, int CoarseRadiusVertical,
    int WorkerBonus, float InstallBudgetMilliseconds, int MaxInstallsPerFrame)
{
    public static CaveLodProfile UndergroundDefault => new(2, 2, 1, 2, 3f, 12);
}

/// <summary>Live cave LOD numbers for the controller status line.</summary>
public readonly record struct CaveLodProfileStatus(bool CoarseActive, int CoarseDesired, int CoarseResident, int CoarseReady,
    int CoarseVisible, long TotalEstimatedBytes, int EffectiveWorkers, int EffectiveRadiusAround, int EffectiveRadiusAcross, int EffectiveRadiusVertical, int CoarseDesiredReady = 0);

/// <summary>Terrain backend seam the underground controller uses to apply and read the cave LOD profile.</summary>
public interface ICaveLodProfileTarget
{
    CaveLodProfile? CaveProfileOverride { get; }
    void SetCaveProfileOverride(CaveLodProfile? profile);
    int CoarseRetainMarginSectors { get; set; }
    float CoarseActivationHysteresisMeters { get; set; }
    float CoarseUndergroundActivationDepthMeters { get; }
    CaveLodProfileStatus CaptureCaveLodProfileStatus();
}
