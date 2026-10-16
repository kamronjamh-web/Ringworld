using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Everything the standing-grass layer needs to know about its render style, captured once per step from the layer's
/// properties (grass-cards plan step S5). Blades carries only the tile size, so every Blades code path keeps reading its
/// legacy properties and stays byte-identical. Cards carries the 32 m tiles, the reach-sized coverage radius, the sqrt(2)
/// density ladder, the budgets derived from the reach (never from blade keys), the placement options and the curve.
/// </summary>
internal sealed record RingGrassStyleProfile
{
    /// <summary>The raw layer settings a profile is a pure function of; equal inputs mean the same profile.</summary>
    public readonly record struct Inputs(RingStandingGrassLayer.GrassRenderStyleKind Style, float BladeTileMeters, float CardTileMeters,
        float ReachMeters, float LookaheadMeters, int ResidentTileSetting, float HysteresisOverrideMeters, RingGrassCardTuning Tuning = default);

    public const float MinimumTileMeters = 16f;
    /// <summary>Largest card footprint radius (a card's half width, clamped by the builder); exclusion invalidation margin.</summary>
    public const float CardFootprintMeters = .7f;

    public RingStandingGrassLayer.GrassRenderStyleKind Style { get; init; }
    public bool IsCards => Style == RingStandingGrassLayer.GrassRenderStyleKind.Cards;
    public float TileMeters { get; init; } = 48f;
    public float ReachMeters { get; init; }
    public float HysteresisMeters { get; init; }
    public float LookaheadMeters { get; init; }
    /// <summary>Planning radius of a Cards layer: the reach (cards are fully faded there).</summary>
    public float CoverageRadiusMeters => IsCards ? ReachMeters : 0f;
    public int ResidentTileCap { get; init; }
    public long InstanceBudget { get; init; }
    /// <summary>Per-tile reservation of a tier-0 tile (main cards + low layer); lower tiers reserve proportionally less.</summary>
    public int MainCapPerTile { get; init; }
    public int LowCapPerTile { get; init; }
    public int TierCount { get; init; } = 1;
    /// <summary>Last ladder tier that still carries the low upright layer.</summary>
    public int LowLayerLastTier { get; init; }
    /// <summary>Nearest-point distance at which each ladder tier begins (lookahead included); index = tier.</summary>
    public float[] TierStartMeters { get; init; } = Array.Empty<float>();
    /// <summary>Largest finite tier start: every tile beyond it holds the floor tier.</summary>
    public float LastTierStartMeters
    {
        get { float last = 0f; foreach (float start in TierStartMeters) if (float.IsFinite(start)) last = Math.Max(last, start); return last; }
    }
    public RingGrassCardCurveParameters Curve { get; init; } = RingGrassCardCurveParameters.Default;
    public RingGrassCardPlacement Placement { get; init; } = new();
    public RingGrassCardAtlasParameters Atlas { get; init; } = RingGrassCardAtlasParameters.VariantD;
    /// <summary>Resident tiles the plan wants (nearest point within the planning radius) and the perimeter ring of them.</summary>
    public int DesiredTiles { get; init; }
    public int PerimeterTiles { get; init; }
    /// <summary>Hash of every setting that changes card placement or its tiers (part of the layer fingerprint in Cards).</summary>
    public ulong PlacementFingerprint { get; init; }
    /// <summary>Hash of reach, lookahead, hysteresis, cap and the density curve: these change tiers and membership, never the lattice, so they go through the tier filter / rebuild path instead of a reset.</summary>
    public ulong CoverageFingerprint { get; init; }

    /// <summary>Identical to the pre-profile TileSize expression, so the Blades tile size and fingerprint are unchanged.</summary>
    public static float ClampTile(float meters) => Mathf.Max(MinimumTileMeters, meters);

    public static RingGrassStyleProfile Build(in Inputs inputs)
    {
        if (inputs.Style != RingStandingGrassLayer.GrassRenderStyleKind.Cards)
            return new RingGrassStyleProfile { Style = inputs.Style, TileMeters = ClampTile(inputs.BladeTileMeters) };
        float tile = Mathf.Clamp(float.IsFinite(inputs.CardTileMeters) ? inputs.CardTileMeters : 32f, MinimumTileMeters, 64f);
        float reach = Mathf.Clamp(float.IsFinite(inputs.ReachMeters) ? inputs.ReachMeters : 400f, 100f, 600f);
        float hysteresis = float.IsFinite(inputs.HysteresisOverrideMeters) && inputs.HysteresisOverrideMeters > 0
            ? Mathf.Clamp(inputs.HysteresisOverrideMeters, .5f, 32f) : Mathf.Clamp(tile * .5f, 2f, 8f);
        float lookahead = float.IsFinite(inputs.LookaheadMeters) && inputs.LookaheadMeters > 0 ? Mathf.Clamp(inputs.LookaheadMeters, 0f, 64f) : hysteresis;
        RingGrassCardTuning tuning = inputs.Tuning.IsUnset ? RingGrassCardTuning.Default : inputs.Tuning.Normalized();
        RingGrassCardCurveParameters curve = RingGrassCardCurveParameters.Default with
        {
            ReachMeters = reach, NearDensity = tuning.NearDensity, BaseDensity = tuning.BaseDensity, FloorDensity = tuning.FloorDensity,
            FalloffExponent = tuning.FalloffExponent, NearBoostStartMeters = tuning.NearBoostStartMeters, NearBoostEndMeters = tuning.NearBoostEndMeters,
            CompensationMax = tuning.CompensationMax, NearWidthScale = tuning.NearWidthScale, FadeBandPercent = tuning.FadeBandPercent,
            LowLayerEndMeters = tuning.LowLayerEndMeters,
        };
        int tiers = Math.Max(1, RingGrassCardCurve.LadderTierCount(curve));
        // Tier t begins where the lookahead-shifted keep fraction first drops below f_{t-1}'s step; found by a monotone scan.
        var starts = new float[tiers];
        for (int t = 1; t < tiers; t++) starts[t] = float.PositiveInfinity;
        for (float d = 0; d <= reach + tile * 2f + 64f; d += .25f)
        {
            int t = TierForShiftedDistance(curve, tiers, d);
            for (int k = 1; k <= t; k++) if (float.IsPositiveInfinity(starts[k])) starts[k] = d + lookahead;
        }
        // Tiers the curve never reaches inside the scan keep an infinite start and own no tiles.
        RingGrassCardPlacement placement = new()
        {
            LowLayerDensity = tuning.LowLayerDensity, TallHeight = tuning.TallHeight, ShortHeight = tuning.ShortHeight, Lean = tuning.Lean,
        };
        // No low layer at density 0: no tier carries it.
        int lowLast = placement.LowLayerDensity > 0f ? TierForShiftedDistance(curve, tiers, curve.LowLayerEndMeters) : -1;
        int main = (int)Math.Ceiling(curve.NearDensity * tile * tile * 1.1f);
        int low = (int)Math.Ceiling(placement.LowLayerDensity * tile * tile * 1.1f);
        placement = placement with { NearDensity = curve.NearDensity, LowLayerMaxInstances = low };
        RingGrassCardAtlasParameters atlas = RingGrassCardAtlasParameters.VariantD with { BladesPerVariant = tuning.AtlasBlades };

        // Resident window: tiles whose nearest point lies within the planning radius (reach + reuse margin + hysteresis),
        // plus a perimeter ring. The cap exceeds both with headroom, so a saturated cap cannot flap (invariant 7).
        float planning = reach + tile * .125f + hysteresis;
        double ring = planning / tile + 1.0;
        int desired = (int)Math.Ceiling(Math.PI * ring * ring);
        int perimeter = (int)Math.Ceiling(2.0 * Math.PI * ring);
        int cap = Math.Max(Math.Max(16, inputs.ResidentTileSetting), desired + 2 * perimeter);

        // Instance budget at placement chance 1 (every lattice cell passes): tiles per held tier annulus x rank prefix + low layer.
        double budget = 0;
        for (int t = 0; t < tiers; t++)
        {
            double begin = t == 0 ? 0 : starts[t];
            // A tile keeps tier t until H past the start of tier t+1 (downgrade hysteresis): tiers are evaluated at d - L - H.
            double end = t + 1 < tiers ? starts[t + 1] + hysteresis : planning + tile;
            if (double.IsPositiveInfinity(begin) || begin >= end) continue;
            end = Math.Min(end, planning + tile);
            double tilesInAnnulus = TilesWithin(end, tile) - TilesWithin(begin, tile);
            double perTile = curve.NearDensity * tile * tile * RingGrassCardCurve.LadderFraction(t) + (t <= lowLast ? placement.LowLayerDensity * tile * tile : 0);
            budget += Math.Max(0, tilesInAnnulus) * perTile;
        }
        long instanceBudget = (long)Math.Min(int.MaxValue / 2.0, Math.Ceiling(budget * 1.25));

        return new RingGrassStyleProfile
        {
            Style = inputs.Style,
            TileMeters = tile, ReachMeters = reach, HysteresisMeters = hysteresis, LookaheadMeters = lookahead,
            ResidentTileCap = cap, InstanceBudget = instanceBudget, MainCapPerTile = main, LowCapPerTile = low,
            TierCount = tiers, LowLayerLastTier = lowLast, TierStartMeters = starts, Curve = curve, Placement = placement, Atlas = atlas,
            DesiredTiles = desired, PerimeterTiles = perimeter,
            PlacementFingerprint = HashPlacement(tile, curve, placement, atlas),
            CoverageFingerprint = HashCoverage(reach, lookahead, hysteresis, cap, curve),
        };
    }

    private static double TilesWithin(double radius, double tile)
    {
        // Tiles whose nearest point is within `radius`: centres within roughly radius + 0.6 tile.
        double r = Math.Max(0, radius) + tile * .6;
        return Math.PI * r * r / (tile * tile);
    }

    private static int TierForShiftedDistance(in RingGrassCardCurveParameters curve, int tiers, float shiftedDistance)
    {
        float keep = RingGrassCardCurve.Keep(curve, Math.Max(0f, shiftedDistance));
        if (!(keep < 1f)) return 0;
        // Largest t with 2^(-t/2) >= keep.
        int t = (int)Math.Floor(-2.0 * Math.Log2(keep) + 1e-6);
        return Math.Clamp(t, 0, tiers - 1);
    }

    /// <summary>Ladder tier for a tile whose nearest point is <paramref name="nearestMeters"/> from the focus, evaluated
    /// <see cref="LookaheadMeters"/> closer than that so an upgrade is requested before the camera arrives. Monotone.</summary>
    public byte TierForDistance(double nearestMeters)
        => (byte)TierForShiftedDistance(Curve, TierCount, (float)Math.Max(0.0, nearestMeters - LookaheadMeters));

    public float FractionForTier(int tier) => RingGrassCardCurve.LadderFraction(Math.Clamp(tier, 0, TierCount - 1));

    public (float Fraction, byte Tier) CoverageForTier(byte tier)
    {
        byte clamped = (byte)Math.Clamp((int)tier, 0, TierCount - 1);
        return (FractionForTier(clamped), clamped);
    }

    public (float Fraction, byte Tier) CoverageForDistance(double nearestMeters) => CoverageForTier(TierForDistance(nearestMeters));

    public bool IncludesLowLayer(int tier) => tier <= LowLayerLastTier;

    /// <summary>Worker placement options of a tile at <paramref name="tier"/>.</summary>
    public RingGrassCardPlacement PlacementForTier(int tier) => Placement with { IncludeLowLayer = IncludesLowLayer(tier) };

    /// <summary>Instance reservation of a tile at a tier: the rank prefix of the main lattice plus the low layer when carried.</summary>
    public int ReserveForTier(int tier)
        => (int)Math.Ceiling(MainCapPerTile * (double)FractionForTier(tier)) + (IncludesLowLayer(tier) ? LowCapPerTile : 0);

    /// <summary>Estimated bytes of resident instance data (20 floats each).</summary>
    public static long BytesPerInstance => RingGrassTileData.FloatsPerInstance * sizeof(float);

    /// <summary>Lattice-affecting settings only: a change rebuilds every tile (layer reset).</summary>
    private static ulong HashPlacement(float tile, in RingGrassCardCurveParameters curve, RingGrassCardPlacement p, in RingGrassCardAtlasParameters atlas)
    {
        ulong h = 14695981039346656037UL;
        void M(float v) { h ^= (uint)BitConverter.SingleToInt32Bits(v); h *= 1099511628211UL; }
        M(tile); M(curve.NearDensity);
        M(p.NearDensity); M(p.LowLayerDensity); M(p.LowLayerMaxInstances); M(p.TallHeight); M(p.ShortHeight); M(p.Lean);
        M(p.LowHeightMin); M(p.LowHeightMax); M(p.LowWidthMin); M(p.LowWidthMax); M(p.BiomeGridSpacingMeters);
        M(atlas.BladesPerVariant); // the atlas regenerates for it; the layer rebuilds so no tile keeps a stale look
        return h;
    }

    /// <summary>Membership and tier settings: handled by replanning, tier filtering and tier-upgrade rebuilds.</summary>
    private static ulong HashCoverage(float reach, float lookahead, float hysteresis, int cap, in RingGrassCardCurveParameters curve)
    {
        ulong h = 14695981039346656037UL;
        void M(float v) { h ^= (uint)BitConverter.SingleToInt32Bits(v); h *= 1099511628211UL; }
        M(reach); M(lookahead); M(hysteresis); M(cap);
        M(curve.BaseDensity); M(curve.FloorDensity); M(curve.FalloffExponent); M(curve.FalloffStartMeters);
        M(curve.NearBoostStartMeters); M(curve.NearBoostEndMeters); M(curve.CompensationMax); M(curve.NearWidthScale);
        M(curve.FadeBandPercent); M(curve.LowLayerFullMeters); M(curve.LowLayerEndMeters);
        return h;
    }
}
