using System;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Kind of an emitter site: 594 regular spires plus exactly one main spire (docs/emitter-poi-streaming-spec.md MAIN SPIRE DECISIONS).</summary>
public enum SiteKind : byte
{
    Regular = 0,
    Main = 1,
}

/// <summary>
/// Per-kind footprint and shape constants shared by the registry builder and the surface operator. Inputs are recipe
/// fields (hashed); derived radii are computed on demand.
/// </summary>
public sealed class EmitterSiteKindShape
{
    public double PadRadiusM { get; }
    public double ApronWidthM { get; }
    public double SkirtMaxRadiusM { get; }
    public double SkirtFadeM { get; }
    /// <summary>Dry and rock-cover radius: no water and at least <see cref="CoverM"/> of rock above the cavern roof inside it.</summary>
    public double EnclosureRadiusM { get; }
    public double BeachWidthM { get; }
    public double ShelfWidthM { get; }
    /// <summary>Depth of the shallowest part of the underground structure below the pad.</summary>
    public double CavernRoofDepthM { get; }
    public double CoverM { get; }
    /// <summary>Candidate scoring: (max cut + max fill) in metres divided by this is added to a land candidate's score.</summary>
    public double CutFillPenaltyScaleM { get; }

    /// <summary>Radius where the cone clamp starts (pad plus apron); everything inside is exactly the pad height.</summary>
    public double RhoStartM => PadRadiusM + ApronWidthM;
    public double SkirtFadeStartM => SkirtMaxRadiusM - SkirtFadeM;
    public double ShoreRadiusM => EnclosureRadiusM + BeachWidthM;
    public double ShelfRadiusM => ShoreRadiusM + ShelfWidthM;
    /// <summary>Surface must stay at or above pad minus this inside the enclosure radius.</summary>
    public double CoverDepthM => CavernRoofDepthM - CoverM;

    internal EmitterSiteKindShape(double padRadius, double apronWidth, double skirtMax, double skirtFade, double enclosureRadius,
        double beachWidth, double shelfWidth, double cavernRoofDepth, double cover, double cutFillPenaltyScale)
    {
        PadRadiusM = padRadius; ApronWidthM = apronWidth; SkirtMaxRadiusM = skirtMax; SkirtFadeM = skirtFade;
        EnclosureRadiusM = enclosureRadius; BeachWidthM = beachWidth; ShelfWidthM = shelfWidth;
        CavernRoofDepthM = cavernRoofDepth; CoverM = cover; CutFillPenaltyScaleM = cutFillPenaltyScale;
    }
}

/// <summary>
/// Immutable, frozen-per-world recipe for the 595 beam-emitter sites (594 regular + 1 main; docs/emitter-site-terrain-spec.md section 1.1).
/// Pure C#, no Godot types. Every field is mixed into <see cref="Hash"/>. There is no approach road (decision D5).
/// </summary>
public sealed class EmitterSiteRecipe
{
    public const int CurrentAlgorithmVersion = 4;
    public const double CertifiedCircumferenceMeters = 31_415_936d;
    public const double CertifiedWidthMeters = 318_000d;

    public int AlgorithmVersion { get; }
    public long Seed { get; }
    public double CircumferenceM { get; }
    public double WidthM { get; }
    public int Rows { get; }
    public int Columns { get; }
    private readonly double[] _columnOffsetsM;
    public double RowPhaseM { get; }
    public double NudgeRadiusM { get; }
    public double PadRadiusM { get; }
    public double ApronWidthM { get; }
    public double MaxPadCutM { get; }
    public double MaxPadFillM { get; }
    public double SkirtMaxRadiusM { get; }
    public double SkirtFadeM { get; }
    public double CutTerraceRiserM { get; }
    public double CutTerraceBenchM { get; }
    public double CutRiserSlope { get; }
    public double FillSlope { get; }
    public double SoftClampM { get; }
    public double EnclosureRadiusM { get; }
    public double EnclosureCoverM { get; }
    public double LandPadMinM { get; }
    public double IslandPadMinM { get; }
    public double IslandCoastM { get; }
    public double BeachWidthM { get; }
    public double ShelfDepthM { get; }
    public double ShelfWidthM { get; }
    public double FlankSlope { get; }
    public double IslandSoftK { get; }
    public double DryMarginM { get; }
    /// <summary>Pad height above sea level at or above which a land site is flagged Mountain.</summary>
    public double MountainPadHeightM { get; }
    /// <summary>Grade cone used only to rank approach bearings (no road is built).</summary>
    public double ApproachGrade { get; }
    /// <summary>Nudge scoring (D4): (max cut + max fill) in metres divided by this is added to a land candidate's score.</summary>
    public double CutFillPenaltyScaleM { get; }

    /// <summary>Regular-site cavern roof apex depth below the pad (spec 3.2: P - 3000).</summary>
    public double RegularCavernRoofDepthM { get; }

    // ---- Main site (MAIN SPIRE DECISIONS) ----------------------------------------------------------------------
    // Spire: 16 arms, outer feet at R730 + shoes, footprint 1,642.78 m (half 821.4 m), 4,000 m tall, bore R150.
    // Pad flat radius 1,000 m covers the footprint (821 m half-width) and the EntryApproach marker of MainSpirePOI.tscn (R930, 67.5 degrees); the apron is 150 m (smoothstep to natural terrain; the surface stays exactly pad height out to R1,150).
    // Reactor (prototypes/Ringworld_Main_Reactor/IMPORT_NOTES.md): chamber centre 4,000 m below the pad, nominal cavern
    // 6 km wide x 4 km high (R3000, +-2000), architecture bounds X +-2504, Z -2695..+2860 (bbox half diagonal 3,801 m)
    // and vertical -720..+2078 about the centre, so the shallowest structure is the roof interface at P-4000+2078 = P-1922
    // and the architecture floor is at P-4720 (nominal cavern floor P-6000).
    // Enclosure/dry radius 4,000 m = bbox half diagonal + ~200 m: the whole reactor lies under dry rock with the regular
    // 200 m cover above its shallowest point (surface >= P-1722). Island: beach 400 m, shelf 600 m, same 30 degree flank.
    /// <summary>Across coordinate of the main site: the ring centre line (hub line).</summary>
    public double MainAcrossM { get; }
    public double MainPadRadiusM { get; }
    public double MainApronWidthM { get; }
    public double MainSkirtMaxRadiusM { get; }
    public double MainSkirtFadeM { get; }
    public double MainEnclosureRadiusM { get; }
    public double MainBeachWidthM { get; }
    public double MainShelfWidthM { get; }
    public double MainCavernRoofDepthM { get; }
    /// <summary>Informational reactor numbers (hashed): chamber centre depth, nominal cavern radius and half height, bbox half diagonal.</summary>
    public double MainChamberCentreDepthM { get; }
    public double MainCavernRadiusM { get; }
    public double MainCavernHalfHeightM { get; }
    public double MainReactorBoundsHalfDiagonalM { get; }
    /// <summary>Beam crown focus above the pad: BeamCrownFocus marker of MainSpirePOI.tscn (3,989 m on the 4,000 m spire) (no tilt: the main site is on the hub line).</summary>
    public double MainCrownHeightM { get; }
    public double MainCutFillPenaltyScaleM { get; }
    /// <summary>A row's centre-line candidate is acceptable land only if its estimated (max cut + max fill) is at most this.</summary>
    public double MainMaxCutFillM { get; }
    /// <summary>Weight of the per-row seed hash added to a row's score when ranking acceptable rows.</summary>
    public double MainSeedWeight { get; }
    /// <summary>Row used when no row offers acceptable land (seed hash).</summary>
    public int SeedPreferredMainRow { get; }
    public ulong Hash { get; }

    public EmitterSiteKindShape Regular { get; }
    public EmitterSiteKindShape Main { get; }

    public EmitterSiteKindShape ShapeFor(SiteKind kind) => kind == SiteKind.Main ? Main : Regular;

    public double RowPitchM => CircumferenceM / Rows;
    public ReadOnlySpan<double> ColumnOffsetsM => _columnOffsetsM;

    private EmitterSiteRecipe(long seed, double circumference, double width)
    {
        AlgorithmVersion = CurrentAlgorithmVersion;
        Seed = seed;
        CircumferenceM = circumference;
        WidthM = width;
        Rows = 100;
        Columns = 6;
        _columnOffsetsM = new double[Columns];
        for (int j = 0; j < Columns; j++) _columnOffsetsM[j] = 26_500d + 53_000d * j;
        double pitch = circumference / Rows;
        ulong phaseHash = EmitterSiteHash.Derive(seed, "emitter-phase", 0);
        double fraction = (phaseHash % 1_000_000UL) / 1_000_000d;
        RowPhaseM = Math.Min(Snap2(fraction * pitch), Snap2(pitch) - 2d);
        NudgeRadiusM = 4_000d;
        PadRadiusM = 350d;
        ApronWidthM = 60d;
        MaxPadCutM = 150d;
        MaxPadFillM = 150d;
        SkirtMaxRadiusM = 3_000d;
        SkirtFadeM = 600d;
        CutTerraceRiserM = 30d;
        CutTerraceBenchM = 20d;
        CutRiserSlope = Math.Tan(60d * Math.PI / 180d);
        FillSlope = Math.Tan(33d * Math.PI / 180d);
        SoftClampM = 24d;
        EnclosureRadiusM = 2_000d;
        EnclosureCoverM = 200d;
        LandPadMinM = 8d;
        IslandPadMinM = 24d;
        IslandCoastM = 4d;
        BeachWidthM = 250d;
        ShelfDepthM = 20d;
        ShelfWidthM = 300d;
        FlankSlope = Math.Tan(30d * Math.PI / 180d);
        IslandSoftK = 8d;
        DryMarginM = 2d;
        MountainPadHeightM = 800d;
        ApproachGrade = 0.08d;
        CutFillPenaltyScaleM = 200d;
        RegularCavernRoofDepthM = 3_000d;
        MainAcrossM = Snap2(width * 0.5d);
        MainPadRadiusM = 1_000d;
        MainApronWidthM = 150d;
        MainSkirtMaxRadiusM = 6_000d;
        MainSkirtFadeM = 1_200d;
        MainEnclosureRadiusM = 4_000d;
        MainBeachWidthM = 400d;
        MainShelfWidthM = 600d;
        MainCavernRoofDepthM = 1_922d;
        MainChamberCentreDepthM = 4_000d;
        MainCavernRadiusM = 3_000d;
        MainCavernHalfHeightM = 2_000d;
        MainReactorBoundsHalfDiagonalM = 3_801d;
        MainCrownHeightM = 3_989d;
        MainCutFillPenaltyScaleM = 600d;
        MainMaxCutFillM = 900d;
        MainSeedWeight = 2d;
        SeedPreferredMainRow = (int)(EmitterSiteHash.Derive(seed, "emitter-main-row", 0) % (ulong)Rows);
        Regular = new EmitterSiteKindShape(PadRadiusM, ApronWidthM, SkirtMaxRadiusM, SkirtFadeM, EnclosureRadiusM, BeachWidthM,
            ShelfWidthM, RegularCavernRoofDepthM, EnclosureCoverM, CutFillPenaltyScaleM);
        Main = new EmitterSiteKindShape(MainPadRadiusM, MainApronWidthM, MainSkirtMaxRadiusM, MainSkirtFadeM, MainEnclosureRadiusM,
            MainBeachWidthM, MainShelfWidthM, MainCavernRoofDepthM, EnclosureCoverM, MainCutFillPenaltyScaleM);
        Hash = ComputeHash();
    }

    /// <summary>Default recipe for the Certified Ring.</summary>
    public static EmitterSiteRecipe Default(long seed)
        => new(seed, CertifiedCircumferenceMeters, CertifiedWidthMeters);

    public static double Snap2(double value) => Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2d;

    private ulong ComputeHash()
    {
        ulong h = EmitterSiteHash.Offset;
        h = EmitterSiteHash.Mix(h, "EmitterSiteRecipe");
        h = EmitterSiteHash.Mix(h, (ulong)AlgorithmVersion);
        h = EmitterSiteHash.Mix(h, (ulong)Seed);
        foreach (double v in new[]
        {
            CircumferenceM, WidthM, Rows, Columns, RowPhaseM, NudgeRadiusM, PadRadiusM, ApronWidthM,
            MaxPadCutM, MaxPadFillM, SkirtMaxRadiusM, SkirtFadeM, CutTerraceRiserM, CutTerraceBenchM, CutRiserSlope,
            FillSlope, SoftClampM, EnclosureRadiusM, EnclosureCoverM, LandPadMinM, IslandPadMinM, IslandCoastM,
            BeachWidthM, ShelfDepthM, ShelfWidthM, FlankSlope, IslandSoftK, DryMarginM, MountainPadHeightM, ApproachGrade, CutFillPenaltyScaleM,
            RegularCavernRoofDepthM, MainAcrossM, MainPadRadiusM, MainApronWidthM, MainSkirtMaxRadiusM, MainSkirtFadeM, MainEnclosureRadiusM,
            MainBeachWidthM, MainShelfWidthM, MainCavernRoofDepthM, MainChamberCentreDepthM, MainCavernRadiusM, MainCavernHalfHeightM,
            MainReactorBoundsHalfDiagonalM, MainCrownHeightM, MainCutFillPenaltyScaleM, MainMaxCutFillM, MainSeedWeight, SeedPreferredMainRow
        })
            h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(v));
        foreach (double offset in _columnOffsetsM)
            h = EmitterSiteHash.Mix(h, BitConverter.DoubleToUInt64Bits(offset));
        return h == 0UL ? 1UL : h;
    }
}

/// <summary>FNV-1a style 64-bit mixing used for recipe, site, and registry identities.</summary>
internal static class EmitterSiteHash
{
    public const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Mix(ulong hash, ulong value)
    {
        for (int i = 0; i < 8; i++)
        {
            hash ^= (value >> (i * 8)) & 0xFFUL;
            hash *= Prime;
        }
        return hash;
    }

    public static ulong Mix(ulong hash, string text)
    {
        foreach (char c in text) hash = Mix(hash, c);
        return hash;
    }

    public static ulong Mix(ulong hash, ulong value, string label) => Mix(Mix(hash, value), label);

    /// <summary>Well-mixed hash of (seed, label, index) for per-site pseudo-random choices.</summary>
    public static ulong Derive(long seed, string label, int index)
    {
        ulong x = Mix(Mix(Mix(Offset, (ulong)seed), label), (ulong)index);
        x ^= x >> 33; x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33; x *= 0xC4CEB9FE1A85EC53UL;
        x ^= x >> 33;
        return x;
    }

    public static double Unit(ulong hash) => (hash >> 11) * (1.0 / 9007199254740992.0);
}
