using System;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>Dominant surface class of one shaped sample (spec 2.8).</summary>
public enum SiteSurfaceClass : byte
{
    None = 0,
    Pad = 1,
    CutFace = 2,
    Fill = 3,
    Beach = 4,
    IslandFlank = 5,
}

/// <summary>Surface material channel a consumer should bias toward.</summary>
public enum SiteMaterialHint : byte
{
    None = 0,
    Rock = 1,
    Sand = 2,
}

/// <summary>
/// Per-sample classification produced by <see cref="EmitterSiteSurfaceOperator"/> (spec 2.8, no road).
/// Weights are 0..1. All zero / <see cref="SiteSurfaceClass.None"/> outside a site's influence disc.
/// </summary>
public readonly record struct SiteSurfaceMask
{
    public SiteSurfaceClass Class { get; init; }
    public SiteMaterialHint Material { get; init; }
    /// <summary>Pad, plaza and apron weight: 1 up to the pad radius, fading to 0 over the apron.</summary>
    public float Hardscape { get; init; }
    /// <summary>Weight of carved cut faces and terrace walls (natural terrain lowered by the operator).</summary>
    public float Rock { get; init; }
    /// <summary>Weight of embankment fill (natural terrain raised by the cone lower bound).</summary>
    public float Fill { get; init; }
    /// <summary>Island beach band weight.</summary>
    public float Sand { get; init; }
    /// <summary>False where grass and trees must not spawn (hardscape, cut faces, beach).</summary>
    public bool VegetationAllowed { get; init; }
    /// <summary>True wherever a site surface class applies; grass and trees must not spawn. Unlike <see cref="VegetationAllowed"/> this is false for default(...).</summary>
    public bool BlocksVegetation => Material != SiteMaterialHint.None;
}

/// <summary>
/// Pure surface-shaping operator (docs/emitter-site-terrain-spec.md section 2, decisions D2/D4/D5/D9: natural-dome
/// islands, terraced bowl, no approach road). Immutable, allocation-free, thread-safe.
/// Heights are presentation-space metres; the caller applies the gen-16 0.5 presentation offset.
///
/// Stage order: (1) island / enclosure raise (max-only), (2) terraced cone clamp, (3) skirt fade, (4) exact pad.
///
/// Continuity: the result is C0 everywhere, including the influence boundary where it equals the natural height.
/// There are no intended jump discontinuities; terrace risers are steep smoothstep ramps, not steps.
/// Intended slope (C1) breaks: the hard cone clamp at the apron edge (r = R_pad + W_apron, where the soft clamp width
/// is zero), the island shelf break at R_shelf (shelf slope to flank slope), and the end of the pad is C1.
/// </summary>
public sealed class EmitterSiteSurfaceOperator
{
    /// <summary>Cavern roof sits this deep below the pad (spec 3.2 apex at P - 3000).</summary>
    public const double CavernRoofDepthM = 3000d;
    private const double SoftClampRampM = 40d;
    private const double MaskEdge0 = 2d, MaskEdge1 = 10d;

    private readonly double _sea;
    private readonly double _riser, _bench, _period, _benchFraction;
    private readonly double _fillSlope, _softClamp, _islandK;
    private readonly double _coast, _flank, _shelfDepth;
    private readonly Kind _regular, _main;

    /// <summary>Per-kind radii (pad, apron, skirt, enclosure, beach, shelf) and cover depth, copied from the recipe shape.</summary>
    private readonly struct Kind
    {
        public readonly double PadR, RhoStart, SkirtMax, SkirtFadeStart, EnclosureR, ShoreR, ShelfR, BeachW, CoverDepth;
        public Kind(EmitterSiteKindShape s)
        {
            PadR = s.PadRadiusM; RhoStart = s.RhoStartM; SkirtMax = s.SkirtMaxRadiusM; SkirtFadeStart = s.SkirtFadeStartM;
            EnclosureR = s.EnclosureRadiusM; ShoreR = s.ShoreRadiusM; ShelfR = s.ShelfRadiusM; BeachW = s.BeachWidthM;
            CoverDepth = s.CavernRoofDepthM - s.CoverM;
        }
    }

    public EmitterSiteRecipe Recipe { get; }
    public double SeaLevel => _sea;

    public EmitterSiteSurfaceOperator(EmitterSiteRecipe recipe, double seaLevel)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        _sea = seaLevel;
        _regular = new Kind(recipe.Regular);
        _main = new Kind(recipe.Main);
        _riser = recipe.CutTerraceRiserM;
        _bench = recipe.CutTerraceBenchM;
        _period = recipe.CutTerraceBenchM + recipe.CutTerraceRiserM / recipe.CutRiserSlope;
        _benchFraction = recipe.CutTerraceBenchM / _period;
        _fillSlope = recipe.FillSlope;
        _softClamp = recipe.SoftClampM;
        _islandK = recipe.IslandSoftK;
        _coast = recipe.IslandCoastM;
        _flank = recipe.FlankSlope;
        _shelfDepth = recipe.ShelfDepthM;
    }

    private Kind For(SiteKind kind) => kind == SiteKind.Main ? _main : _regular;

    /// <summary>Shaped height at offset (<paramref name="du"/>, <paramref name="dw"/>) metres from the site centre.</summary>
    public double Shape(in EmitterSite site, double du, double dw, double h0)
        => ShapeCore(in site, du, dw, h0, false, out _);

    /// <summary>Shaped height plus the surface classification for the same sample.</summary>
    public double Shape(in EmitterSite site, double du, double dw, double h0, out SiteSurfaceMask mask)
        => ShapeCore(in site, du, dw, h0, true, out mask);

    /// <summary>Terrace staircase height above the pad at cone distance rho (monotone, C1, risers 30 m by default).</summary>
    public double TerraceHeight(double rho)
    {
        double u = rho / _period;
        double n = Math.Floor(u);
        double f = u - n;
        return _riser * (n + Smooth(_benchFraction, 1d, f));
    }

    /// <summary>Island/dome profile E(r): P inside the apron, PCHIP through coast, shore and shelf knots, linear flank beyond.</summary>
    public double IslandProfile(double r, double pad) => IslandProfile(r, pad, SiteKind.Regular);

    /// <summary>Island/dome profile with the per-kind pad, enclosure, beach and shelf radii.</summary>
    public double IslandProfile(double r, double pad, SiteKind kind) => IslandProfile(r, pad, For(kind));

    private double IslandProfile(double r, double pad, in Kind k)
    {
        if (r <= k.RhoStart) return pad;
        if (r >= k.ShelfR) return _sea - _shelfDepth - _flank * (r - k.ShelfR);
        double e0 = pad, e1 = _sea + _coast, e2 = _sea - 0.5d, e3 = _sea - _shelfDepth;
        double h0 = k.EnclosureR - k.RhoStart, h1 = k.BeachW, h2 = k.ShelfR - k.ShoreR;
        double m0 = (e1 - e0) / h0, m1 = (e2 - e1) / h1, m2 = (e3 - e2) / h2;
        // Fritsch-Carlson endpoint rules: flat dome top (d0 = 0, C1 with the flat pad), shelf edge keeps the shelf secant.
        double d1 = Pchip(m0, m1, h0, h1), d2 = Pchip(m1, m2, h1, h2);
        if (r < k.EnclosureR) return Hermite(r, k.RhoStart, h0, e0, e1, 0d, d1);
        if (r < k.ShoreR) return Hermite(r, k.EnclosureR, h1, e1, e2, d1, d2);
        return Hermite(r, k.ShoreR, h2, e2, e3, d2, m2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Pchip(double ma, double mb, double ha, double hb)
    {
        if (ma * mb <= 0d) return 0d;
        double w1 = 2d * hb + ha, w2 = hb + 2d * ha;
        return (w1 + w2) / (w1 / ma + w2 / mb);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Hermite(double x, double x0, double h, double y0, double y1, double d0, double d1)
    {
        double t = (x - x0) / h, t2 = t * t, t3 = t2 * t;
        return (2d * t3 - 3d * t2 + 1d) * y0 + (t3 - 2d * t2 + t) * h * d0 + (-2d * t3 + 3d * t2) * y1 + (t3 - t2) * h * d1;
    }

    private double ShapeCore(in EmitterSite site, double du, double dw, double h0, bool wantMask, out SiteSurfaceMask mask)
    {
        double influence = site.InfluenceRadius;
        double r2 = du * du + dw * dw;
        if (r2 >= influence * influence)
        {
            mask = default;
            return h0;
        }
        double r = Math.Sqrt(r2);
        double pad = site.PadHeight;
        bool island = (site.Flags & EmitterSiteFlags.Island) != 0;
        Kind kc = For(site.Kind);

        // Pad plus apron: the cone is degenerate for rho = 0 (U = L = P), so the whole disc is exactly P.
        if (r <= kc.RhoStart)
        {
            mask = wantMask ? PadMask(r, kc) : default;
            return pad;
        }

        // (1) island / enclosure raise, max-only.
        double h1 = h0;
        if (island) h1 = SmoothMax(h1, IslandProfile(r, pad, kc), _islandK);
        if ((site.Flags & EmitterSiteFlags.EnclosureRaise) != 0)
        {
            double cover = pad - kc.CoverDepth - (r > kc.EnclosureR ? _flank * (r - kc.EnclosureR) : 0d);
            h1 = SmoothMax(h1, cover, _islandK);
        }

        // (2) terraced cone clamp. The soft windows are shifted by k/2 away from the bounds so that terrain already
        // inside the cone is left untouched to within k/16 (a plain smin dips k/4 where terrain meets the bound).
        double rho = r - kc.RhoStart;
        double upper = pad + TerraceHeight(rho);
        double lower = pad - _fillSlope * rho;
        double k = _softClamp * Smooth(0d, SoftClampRampM, rho);
        double h2 = SmoothMax(SmoothMin(h1, upper + 0.5d * k, k), lower - 0.5d * k, k);

        // (3) skirt fade back to the (raised) natural height.
        double h3;
        if (r >= kc.SkirtMax) h3 = h1;
        else if (r <= kc.SkirtFadeStart) h3 = h2;
        else
        {
            double t = Smooth(kc.SkirtFadeStart, kc.SkirtMax, r);
            h3 = h2 + (h1 - h2) * t;
        }

        // (4) pad flatten: r > R_pad + W_apron here, so the blend weight is 1 and h3 is the result.
        if (wantMask) mask = ClassifySample(r, h0, h1, h3, island, kc);
        else mask = default;
        return h3;
    }

    private SiteSurfaceMask PadMask(double r, in Kind k)
    {
        double hard = r <= k.PadR ? 1d : 1d - Smooth(k.PadR, k.RhoStart, r);
        return new SiteSurfaceMask
        {
            Class = SiteSurfaceClass.Pad,
            Material = SiteMaterialHint.Rock,
            Hardscape = (float)hard,
            VegetationAllowed = false,
        };
    }

    private SiteSurfaceMask ClassifySample(double r, double h0, double h1, double h5, bool island, in Kind k)
    {
        const double hard = 0d; // r > R_pad + W_apron
        double rock = Smooth(MaskEdge0, MaskEdge1, h1 - h5);
        double fill = Smooth(MaskEdge0, MaskEdge1, h5 - h1);
        double sand = island ? (1d - Smooth(_sea + 3d, _sea + 8d, h5)) * Smooth(k.EnclosureR - 150d, k.EnclosureR, r) : 0d;
        // Sand only on the beach band and the wet skirt directly around it, not on the whole seamount flank.
        if (r > k.ShelfR) sand = 0d;
        SiteSurfaceClass cls;
        if (rock >= 0.5d) cls = SiteSurfaceClass.CutFace;
        else if (fill >= 0.5d) cls = SiteSurfaceClass.Fill;
        else if (sand >= 0.5d) cls = SiteSurfaceClass.Beach;
        else if (island && h1 - h0 > MaskEdge0) cls = SiteSurfaceClass.IslandFlank;
        else cls = SiteSurfaceClass.None;
        bool vegetation = !(rock > 0.05d || sand > 0.25d);
        SiteMaterialHint material = rock > 0.05d ? SiteMaterialHint.Rock : sand > 0.25d ? SiteMaterialHint.Sand : SiteMaterialHint.None;
        return new SiteSurfaceMask
        {
            Class = cls, Material = material, Hardscape = (float)hard, Rock = (float)rock, Fill = (float)fill,
            Sand = (float)sand, VegetationAllowed = vegetation,
        };
    }

    // ---- smooth primitives ------------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smooth(double e0, double e1, double x)
    {
        double t = (x - e0) / (e1 - e0);
        if (t <= 0d) return 0d;
        if (t >= 1d) return 1d;
        return t * t * (3d - 2d * t);
    }

    /// <summary>Polynomial smooth minimum (C1, 1-Lipschitz). Exactly min(a, b) once |a - b| >= k; k &lt;= 0 is a hard min.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SmoothMin(double a, double b, double k)
    {
        if (k <= 0d) return Math.Min(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0d) / k;
        return Math.Min(a, b) - h * h * k * 0.25d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double SmoothMax(double a, double b, double k)
    {
        if (k <= 0d) return Math.Max(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0d) / k;
        return Math.Max(a, b) + h * h * k * 0.25d;
    }
}
