using System;

namespace RingworldTechDemo.Vegetation;

/// <summary>The plant communities of T6 design A5, in table order. Index 8 of the ground-cover tables is the grassland remainder (no community).</summary>
internal enum RingPlantCommunity { ClosedBroadleaf = 0, ConiferStand = 1, OpenWoodland = 2, ForestEdge = 3, Clearing = 4, DryMeadow = 5, WetMeadow = 6, RockyFell = 7 }

/// <summary>Soft memberships in [0, 1] of the eight A5 communities at a point. A plain value type, so a patch node holds one per lattice node.</summary>
internal readonly struct RingCommunityMemberships
{
    public const int Count = 8;
    public readonly float ClosedBroadleaf, ConiferStand, OpenWoodland, ForestEdge, Clearing, DryMeadow, WetMeadow, RockyFell;

    public RingCommunityMemberships(float closedBroadleaf, float coniferStand, float openWoodland, float forestEdge, float clearing, float dryMeadow, float wetMeadow, float rockyFell)
    {
        ClosedBroadleaf = closedBroadleaf; ConiferStand = coniferStand; OpenWoodland = openWoodland; ForestEdge = forestEdge;
        Clearing = clearing; DryMeadow = dryMeadow; WetMeadow = wetMeadow; RockyFell = rockyFell;
    }

    public float this[int index] => index switch
    {
        0 => ClosedBroadleaf, 1 => ConiferStand, 2 => OpenWoodland, 3 => ForestEdge, 4 => Clearing, 5 => DryMeadow, 6 => WetMeadow, 7 => RockyFell, _ => 0f
    };

    public float Sum => ClosedBroadleaf + ConiferStand + OpenWoodland + ForestEdge + Clearing + DryMeadow + WetMeadow + RockyFell;

    /// <summary>Grassland: 1 minus the sum of the memberships, clamped at 0.</summary>
    public float Grassland => MathF.Max(0f, 1f - Sum);
}

/// <summary>
/// T6 design A5 (memberships) and B3 (ground-cover shares and splits per community). Pure and stateless: the membership formulas, the share table and the blend by membership weight, with
/// "grassland" = 1 - sum of the memberships standing in where no community holds. Kind columns are fern / flower / tall grass (reed is T6-12); flower variants are white / yellow / violet / pink.
/// </summary>
internal static class RingPlantCommunities
{
    public const int GrasslandIndex = 8;
    public const int FernKind = 0, FlowerKind = 1, TallKind = 2;

    /// <summary>The largest row share of the table: a hash at or above it can never become a cover card, whatever the memberships.</summary>
    public const float MaximumCoverShare = 0.45f;

    // Rows: the eight communities in enum order, then grassland.
    private static readonly float[] CoverShares = { 0.45f, 0.25f, 0.30f, 0.35f, 0.35f, 0.12f, 0.40f, 0.05f, 0.10f };
    private static readonly float[,] KindSplits =
    {
        { .75f, .10f, .15f }, { .60f, .10f, .30f }, { .20f, .30f, .50f }, { .20f, .35f, .45f }, { .10f, .50f, .40f },
        { 0f, .80f, .20f }, { 0f, .30f, .70f }, { 0f, 1f, 0f }, { 0f, .60f, .40f }
    };
    private static readonly float[,] VariantSplits =
    {
        { .7f, .2f, .1f, 0f }, { .5f, .3f, .2f, 0f }, { .35f, .30f, .20f, .15f }, { .35f, .30f, .20f, .15f }, { .2f, .2f, .1f, .5f },
        { .2f, .4f, .4f, 0f }, { .1f, .5f, .1f, .3f }, { .3f, .4f, .3f, 0f }, { .3f, .4f, .2f, .1f }
    };

    /// <summary>A5 membership formulas. d is the regional canopy density, cl the clearing field, c the conifer share (fir + pine weights), W wetness, R64 relief, X rock exposure, M moisture.</summary>
    public static RingCommunityMemberships Memberships(float canopy, float clearing, float coniferShare, float wetness, float relief64, float rock, float moisture, float scree)
    {
        float d = canopy;
        float closed = SmoothStep(.50f, .56f, d);
        float treeless = 1f - SmoothStep(.05f, .12f, d);
        return new RingCommunityMemberships(
            closed * (1f - coniferShare),
            closed * coniferShare,
            SmoothStep(.25f, .31f, d) * (1f - closed),
            SmoothStep(.10f, .16f, d) * (1f - SmoothStep(.25f, .31f, d)),
            SmoothStep(.10f, .30f, clearing) * (1f - SmoothStep(.10f, .16f, d)),
            treeless * SmoothStep(.1f, .5f, relief64) * (1f - SmoothStep(.35f, .60f, moisture)),
            treeless * SmoothStep(.55f, .80f, wetness),
            MathF.Max(SmoothStep(.30f, .70f, rock), scree) * (1f - SmoothStep(.30f, .40f, d)));
    }

    /// <summary>A row with a raw weight at or below this does not take part in the blend, so a cover kind appears only where a community that carries it has membership above 0.05 (the B3 proof).</summary>
    public const float MinimumWeight = 0.05f;

    private static float Raw(in RingCommunityMemberships m, int row) => row == GrasslandIndex ? m.Grassland : m[row];

    /// <summary>Weight of row i (the eight communities, then grassland) in a membership blend: the memberships and the grassland remainder, rows at or below <see cref="MinimumWeight"/> dropped, normalised
    /// over the rows that remain (so overlapping memberships past 1 also normalise).</summary>
    private static float Weight(in RingCommunityMemberships m, int row, float total) => Raw(m, row) > MinimumWeight ? Raw(m, row) / total : 0f;

    private static float Total(in RingCommunityMemberships m)
    {
        float total = 0f;
        for (int row = 0; row <= GrasslandIndex; row++) if (Raw(m, row) > MinimumWeight) total += Raw(m, row);
        return total > 0f ? total : 1f;
    }

    /// <summary>B3: the cover share (probability that a main-lattice card is a cover card, before the menu share), blended by membership weight.</summary>
    public static float CoverShare(in RingCommunityMemberships m)
    {
        float total = Total(m), share = 0f;
        for (int row = 0; row <= GrasslandIndex; row++) share += Weight(m, row, total) * CoverShares[row];
        return share;
    }

    /// <summary>B3: the blended fern / flower / tall-grass split (sums to 1).</summary>
    public static void KindSplit(in RingCommunityMemberships m, out float fern, out float flower, out float tall)
    {
        float total = Total(m);
        fern = flower = tall = 0f;
        for (int row = 0; row <= GrasslandIndex; row++)
        {
            float w = Weight(m, row, total);
            fern += w * KindSplits[row, FernKind]; flower += w * KindSplits[row, FlowerKind]; tall += w * KindSplits[row, TallKind];
        }
    }

    /// <summary>B3: the largest raw membership (grassland included) among the rows that carry one cover kind column (0 fern, 1 flower, 2 tall grass): the kind can appear only above <see cref="MinimumWeight"/>.</summary>
    public static float KindSupport(in RingCommunityMemberships m, int kindColumn)
    {
        float best = 0f;
        for (int row = 0; row <= GrasslandIndex; row++) if (KindSplits[row, kindColumn] > 0f) best = MathF.Max(best, Raw(m, row));
        return best;
    }

    /// <summary>B3: the blended flower variant weights (white / yellow / violet / pink), summing to 1.</summary>
    public static void VariantWeights(in RingCommunityMemberships m, out float white, out float yellow, out float violet, out float pink)
    {
        float total = Total(m);
        white = yellow = violet = pink = 0f;
        for (int row = 0; row <= GrasslandIndex; row++)
        {
            float w = Weight(m, row, total);
            white += w * VariantSplits[row, 0]; yellow += w * VariantSplits[row, 1]; violet += w * VariantSplits[row, 2]; pink += w * VariantSplits[row, 3];
        }
    }

    /// <summary>The share of one community row (0-7) or grassland (8), for tests and the sheet.</summary>
    public static float RowCoverShare(int row) => CoverShares[row];

    private static float Saturate(float value) => float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;

    private static float SmoothStep(float edge0, float edge1, float value)
    {
        float t = Saturate((value - edge0) / (edge1 - edge0));
        return t * t * (3f - 2f * t);
    }
}
