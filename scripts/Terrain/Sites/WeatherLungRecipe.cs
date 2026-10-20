using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Immutable, frozen-per-world recipe of the Weather Lung basin (docs/weather-lung-integration-plan.md D3/D4). Pure C#, no Godot types.
/// Station pitch and phase come from the seed-derived <see cref="EmitterSiteRecipe"/> so the Weather Lung stays locked to the emitter grid.
/// Every field is mixed into <see cref="Hash"/>. Changing any constant or the placement/operator algorithms requires a new generator id.
///
/// Geometry (asset-local metres, elliptic coordinate r; K13 of the integration plan): pt(t, r) = ((9900 + 4100 r) cos t, (5650 + 3350 r) sin t),
/// r = 0 is the inner lip, r = 1 the 28 x 18 km outer edge.
/// </summary>
public sealed class WeatherLungRecipe
{
    public const int CurrentAlgorithmVersion = 2;

    public int AlgorithmVersion { get; }
    public long Seed { get; }
    public double CircumferenceM { get; }
    public double WidthM { get; }
    /// <summary>Hash of the emitter recipe whose pitch, phase, main-row machinery and cone constants were adopted.</summary>
    public ulong EmitterRecipeHash { get; }
    public int Stations { get; }
    public double RowPitchM { get; }
    public double RowPhaseM { get; }
    /// <summary>Across coordinate of the Weather Lung: the ring centre line (same as the main spire).</summary>
    public double AcrossM { get; }
    /// <summary>Ring radius used for the curvature correction x^2 / (2 R).</summary>
    public double RingRadiusM { get; }

    // ---- Datum and basin ----
    /// <summary>POI local Y 0 sits at sea level + this many metres, so the POI waterline (-135) is the game's sea level 0.</summary>
    public double DatumAboveSeaM { get; }
    public double LipSemiAxisXM { get; }
    public double LipSemiAxisYM { get; }
    /// <summary>Growth of the semi-axes per unit r (4100 and 3350 m): the rim is 4.1 km wide along X and 3.35 km along Y.</summary>
    public double RimGrowthXM { get; }
    public double RimGrowthYM { get; }
    /// <summary>Basin floor target ends at r = RCarve (P - CarveDepth); the C1 smoothstep to the pad ends at RPad. Both lie inside the solid foundation (r 0.01..0.24).</summary>
    public double RCarve { get; }
    public double RPad { get; }
    public double CarveDepthM { get; }

    // ---- Forecourt (mirrors ClimateMachineRecipe) ----
    public double ApronM { get; }
    public double InfluenceM { get; }
    public double SkirtFadeM { get; }
    public double BeachM { get; }
    public double QuayDeckSlopeRunM { get; }
    public double QuayShelfWidthM { get; }
    public double SoftClampRampM { get; }
    public double QuayWetMarginM { get; }

    // ---- Placement (D4) ----
    /// <summary>Gaps searched on each side of the main row (MainRow +- this), every gap at several along-gap offsets and across positions.</summary>
    public int GapsEachSide { get; }
    /// <summary>Along-gap offsets from the gap mid-point: -OffsetSteps..+OffsetSteps times OffsetStepM.</summary>
    public int OffsetSteps { get; }
    public double OffsetStepM { get; }
    public double AcrossStepM { get; }
    /// <summary>Clearance kept between the influence box and the ring edge or a Wall Engine influence band.</summary>
    public double EdgeMarginM { get; }
    public int StencilAngles { get; }
    public double StencilR0 { get; }
    public double StencilR1 { get; }
    public double StencilR2 { get; }
    public double StencilOuterR { get; }
    /// <summary>Main-spire knot links (docs: mid-gap across 145.75 and 172.25 km): they run from the main spire to the neighbouring rows; keep the footprint this far from them.</summary>
    public double KnotAcrossLowM { get; }
    public double KnotAcrossHighM { get; }
    public double KnotClearM { get; }
    // ---- Score (minimum wins): cut is the worst outcome, plinth fill where the seabed is deep, a mild distance term, a coastal bonus ----
    public double ScoreCutMeanWeight { get; }
    public double ScoreCutMaxWeight { get; }
    public double ScorePlinthMeanWeight { get; }
    public double ScorePlinthMaxWeight { get; }
    public double ScorePerKmFromMain { get; }
    public double ScoreCoastalBonus { get; }
    public double CoastalLandMin { get; }
    public double CoastalLandMax { get; }
    /// <summary>Water samples at most this deep (below sea level) count as shallow.</summary>
    public double ShallowWaterDepthM { get; }
    public double LandClassFraction { get; }

    public ulong Hash { get; }

    /// <summary>Half extent of the influence bounding box along the asset X axis: outer-edge semi-axis plus the influence band.</summary>
    public double BoundHalfXM => LipSemiAxisXM + RimGrowthXM + InfluenceM;
    /// <summary>Half extent of the influence bounding box along the asset Y axis.</summary>
    public double BoundHalfYM => LipSemiAxisYM + RimGrowthYM + InfluenceM;

    private WeatherLungRecipe(long seed, EmitterSiteRecipe emitter)
    {
        AlgorithmVersion = CurrentAlgorithmVersion;
        Seed = seed;
        CircumferenceM = emitter.CircumferenceM;
        WidthM = emitter.WidthM;
        EmitterRecipeHash = emitter.Hash;
        Stations = emitter.Rows;
        RowPitchM = emitter.RowPitchM;
        RowPhaseM = emitter.RowPhaseM;
        AcrossM = emitter.MainAcrossM;
        RingRadiusM = emitter.CircumferenceM / Math.Tau;
        DatumAboveSeaM = 135d;
        LipSemiAxisXM = 9_900d;
        LipSemiAxisYM = 5_650d;
        RimGrowthXM = 4_100d;
        RimGrowthYM = 3_350d;
        RCarve = 0.05d;
        RPad = 0.15d;
        CarveDepthM = 875d;
        ApronM = 400d;
        InfluenceM = 6_000d;
        SkirtFadeM = 4_000d;
        BeachM = 250d;
        QuayDeckSlopeRunM = 600d;
        QuayShelfWidthM = 300d;
        SoftClampRampM = 40d;
        QuayWetMarginM = 2d;
        GapsEachSide = 4;
        OffsetSteps = 2;
        OffsetStepM = 50_000d;
        AcrossStepM = 20_000d;
        EdgeMarginM = 2_000d;
        StencilAngles = 16;
        StencilR0 = 0.2d;
        StencilR1 = 0.6d;
        StencilR2 = 1.0d;
        StencilOuterR = 1.5d;
        KnotAcrossLowM = 145_750d;
        KnotAcrossHighM = 172_250d;
        KnotClearM = 4_000d;
        ScoreCutMeanWeight = 1d;
        ScoreCutMaxWeight = 0.25d;
        ScorePlinthMeanWeight = 0.5d;
        ScorePlinthMaxWeight = 0.1d;
        ScorePerKmFromMain = 0.1d;
        ScoreCoastalBonus = 40d;
        CoastalLandMin = 0.15d;
        CoastalLandMax = 0.85d;
        ShallowWaterDepthM = 100d;
        LandClassFraction = 0.85d;
        Hash = ComputeHash();
    }

    /// <summary>Default recipe for the Certified Ring, derived from the seed like <c>EmitterSiteTerrainShaper.RecipeFor</c>.</summary>
    public static WeatherLungRecipe Default(long seed) => new(seed, EmitterSiteRecipe.Default(seed));

    /// <summary>Default recipe locked to an explicit emitter recipe (tests with non-certified geometry).</summary>
    public static WeatherLungRecipe For(EmitterSiteRecipe emitter) => new(emitter.Seed, emitter);

    private ulong ComputeHash()
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, "WeatherLungRecipe");
        h = EmitterSiteHash.Mix(h, (ulong)AlgorithmVersion);
        h = EmitterSiteHash.Mix(h, (ulong)Seed);
        h = EmitterSiteHash.Mix(h, EmitterRecipeHash);
        foreach (double v in new[]
        {
            CircumferenceM, WidthM, Stations, RowPitchM, RowPhaseM, AcrossM, RingRadiusM, DatumAboveSeaM, LipSemiAxisXM, LipSemiAxisYM,
            RimGrowthXM, RimGrowthYM, RCarve, RPad, CarveDepthM, ApronM, InfluenceM, SkirtFadeM, BeachM, QuayDeckSlopeRunM, QuayShelfWidthM,
            SoftClampRampM, QuayWetMarginM, GapsEachSide, OffsetSteps, OffsetStepM, AcrossStepM, EdgeMarginM, StencilAngles, StencilR0, StencilR1, StencilR2,
            StencilOuterR, KnotAcrossLowM, KnotAcrossHighM, KnotClearM, ScoreCutMeanWeight, ScoreCutMaxWeight, ScorePlinthMeanWeight, ScorePlinthMaxWeight,
            ScorePerKmFromMain, ScoreCoastalBonus, CoastalLandMin, CoastalLandMax, ShallowWaterDepthM, LandClassFraction
        })
            h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(v));
        return h == 0UL ? 1UL : h;
    }
}
