using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Weather anomaly parked by an offline climate machine (docs/emitter-grid-climate-machines-spec.md section C2).</summary>
public enum ClimateAnomalyKind : byte
{
    None = 0,
    Storm = 1,
    Drought = 2,
}

/// <summary>Filter for nearest-machine queries.</summary>
public enum ClimateMachineFilter : byte
{
    Any = 0,
    Offline = 1,
    Storm = 2,
    Drought = 3,
}

/// <summary>
/// Immutable, frozen-per-world recipe for the 200 Wall Engine climate machines (spec section C1/C2). Pure C#, no Godot types.
/// Station pitch and phase come from the seed-derived <see cref="EmitterSiteRecipe"/> so the two grids stay locked together.
/// Every field is mixed into <see cref="Hash"/>.
/// </summary>
public sealed class ClimateMachineRecipe
{
    public const int CurrentAlgorithmVersion = 1;

    public int AlgorithmVersion { get; }
    public long Seed { get; }
    public double CircumferenceM { get; }
    public double WidthM { get; }
    /// <summary>Hash of the emitter recipe whose pitch and phase were adopted.</summary>
    public ulong EmitterRecipeHash { get; }
    public int Stations { get; }
    public int Sides { get; }
    public double RowPitchM { get; }
    public double RowPhaseM { get; }

    // ---- Bay form (informational for later visual phases, hashed) ----
    public double FaceWidthM { get; }
    public double WallHeightM { get; }
    public int IntakeColumns { get; }
    public double IntakeColumnWidthM { get; }
    public double IntakeColumnHeightM { get; }
    public double VentGalleryLowM { get; }
    public double VentGalleryMidM { get; }
    public double VentGalleryHighM { get; }

    // ---- Forecourt / influence ----
    /// <summary>Forecourt depth: how far the flat quay extends from the wall foot (also the pad radius of the stadium footprint).</summary>
    public double ForecourtDepthM { get; }
    /// <summary>Forecourt length along the ring; the footprint segment spans this length centred on the machine.</summary>
    public double ForecourtLengthM { get; }
    public double SkirtM { get; }
    public double ApronM { get; }
    public double MaxPadCutFillM { get; }
    public double BeachM { get; }
    public double MinPadAboveSeaM { get; }

    // ---- Offline selection ----
    public double OfflineFraction { get; }
    public int MinOfflineSpacingStations { get; }
    public int MainExclusionStations { get; }

    // ---- Anomalies ----
    public double StormRadiusAroundM { get; }
    public double StormRadiusAcrossM { get; }
    public double DroughtRadiusAroundM { get; }
    public double DroughtRadiusAcrossM { get; }
    public double StormInsetM { get; }
    public double DroughtInsetM { get; }
    public double AnomalyFeather { get; }

    public ulong Hash { get; }

    /// <summary>Total machine count: stations x sides.</summary>
    public int MachineCount => Stations * Sides;
    /// <summary>Number of offline machines: round(fraction x machine count).</summary>
    public int OfflineCount => (int)Math.Round(OfflineFraction * MachineCount, MidpointRounding.AwayFromZero);
    /// <summary>Radius from the footprint segment outside which a machine never affects terrain.</summary>
    public double InfluenceRadiusM => ForecourtDepthM + ApronM + SkirtM;

    private ClimateMachineRecipe(long seed, EmitterSiteRecipe emitter)
    {
        AlgorithmVersion = CurrentAlgorithmVersion;
        Seed = seed;
        CircumferenceM = emitter.CircumferenceM;
        WidthM = emitter.WidthM;
        EmitterRecipeHash = emitter.Hash;
        Stations = emitter.Rows;
        Sides = 2;
        RowPitchM = emitter.RowPitchM;
        RowPhaseM = emitter.RowPhaseM;
        FaceWidthM = 24_000d;
        WallHeightM = 10_000d;
        IntakeColumns = 8;
        IntakeColumnWidthM = 1_200d;
        IntakeColumnHeightM = 9_000d;
        VentGalleryLowM = 2_000d;
        VentGalleryMidM = 4_000d;
        VentGalleryHighM = 6_000d;
        ForecourtDepthM = 3_000d;
        ForecourtLengthM = 20_000d;
        SkirtM = 5_000d;
        ApronM = 400d;
        MaxPadCutFillM = 150d;
        BeachM = 250d;
        MinPadAboveSeaM = 8d;
        OfflineFraction = 0.03d;
        MinOfflineSpacingStations = 10;
        MainExclusionStations = 2;
        StormRadiusAroundM = 110_000d;
        StormRadiusAcrossM = 70_000d;
        DroughtRadiusAroundM = 160_000d;
        DroughtRadiusAcrossM = 100_000d;
        StormInsetM = 70_000d;
        DroughtInsetM = 90_000d;
        AnomalyFeather = 0.35d;
        Hash = ComputeHash();
    }

    /// <summary>Default recipe for the Certified Ring, derived from the seed like <c>EmitterSiteTerrainShaper.RecipeFor</c>.</summary>
    public static ClimateMachineRecipe Default(long seed) => new(seed, EmitterSiteRecipe.Default(seed));

    /// <summary>Default recipe locked to an explicit emitter recipe (tests with non-certified geometry).</summary>
    public static ClimateMachineRecipe For(EmitterSiteRecipe emitter) => new(emitter.Seed, emitter);

    private ulong ComputeHash()
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, "ClimateMachineRecipe");
        h = EmitterSiteHash.Mix(h, (ulong)AlgorithmVersion);
        h = EmitterSiteHash.Mix(h, (ulong)Seed);
        h = EmitterSiteHash.Mix(h, EmitterRecipeHash);
        foreach (double v in new[]
        {
            CircumferenceM, WidthM, Stations, Sides, RowPitchM, RowPhaseM, FaceWidthM, WallHeightM, IntakeColumns, IntakeColumnWidthM,
            IntakeColumnHeightM, VentGalleryLowM, VentGalleryMidM, VentGalleryHighM, ForecourtDepthM, ForecourtLengthM, SkirtM, ApronM,
            MaxPadCutFillM, BeachM, MinPadAboveSeaM, OfflineFraction, MinOfflineSpacingStations, MainExclusionStations,
            StormRadiusAroundM, StormRadiusAcrossM, DroughtRadiusAroundM, DroughtRadiusAcrossM, StormInsetM, DroughtInsetM, AnomalyFeather
        })
            h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(v));
        return h == 0UL ? 1UL : h;
    }
}
