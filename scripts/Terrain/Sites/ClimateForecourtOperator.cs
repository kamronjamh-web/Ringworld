using System;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Pure, deterministic forecourt operator of a Wall Engine (docs/emitter-grid-climate-machines-spec.md sections C1-C3). Immutable,
/// allocation-free, thread-safe. Heights are presentation-space metres.
///
/// Footprint: the forecourt is a half-stadium at the wall foot. The distance s is measured to the footprint segment that runs
/// <see cref="ClimateMachineRecipe.ForecourtLengthM"/> along the wall; the pad radius is <see cref="ClimateMachineRecipe.ForecourtDepthM"/>
/// (3 km onto the floor). The wall itself is the other side of the stadium, so the lookup only ever hits the floor half.
///
/// Stage order mirrors <see cref="EmitterSiteSurfaceOperator"/>: (1) quay raise (max-only, only for machines flagged
/// <see cref="ClimateMachineFlags.Quay"/>), (2) terraced cone clamp, (3) skirt fade back to the (raised) natural height, (4) exact pad.
///
/// Robustness choices for the real seed-1337 ring (about 55% of machines flagged Quay and 85% CutFillConflict, C1 report):
/// - Pad and apron (s &lt;= 3,400 m) are exactly the pad height P regardless of what the natural terrain was, so a wet or rough forecourt becomes a flat quay or
///   terrace platform; the stencil median chooses P so that the average cut and fill is small.
/// - Cuts and fills outside the pad are the emitter mountain-bowl cone clamp (60 degree terrace staircase for cuts, 33 degree embankment for fills): terrain that is
///   merely rough stays untouched, only terrain steeper than the cone is carved. Nothing needs a hard cap; the cone IS the limit.
/// - The quay raise is a PCHIP shoulder (flat deck, gentle apron, 250 m beach, shelf, 30 degree flank) whose weight fades to zero by the influence
///   radius, so even a quay in deep ocean joins the natural height continuously (C0) at the influence boundary.
/// - The skirt fade zone is 2 km wide so the clamp result blends into natural terrain without a step even when a cut is hundreds of metres deep.
/// </summary>
public sealed class ClimateForecourtOperator
{
    /// <summary>Mixed into the v6 terrain definition hash with the climate recipe hash: bump when any constant or formula below changes.</summary>
    public const int OperatorVersion = 1;

    private const double SkirtFadeM = 4_000d;
    // Quay shoulder (metres from the stadium edge, measured past the apron).
    private const double QuayDeckSlopeRunM = 600d;
    private const double QuayShelfWidthM = 300d;
    private const double SoftClampRampM = 40d;
    private const double MaskEdge0 = 2d, MaskEdge1 = 10d;

    private readonly double _sea;
    private readonly double _halfSegment;
    private readonly double _padR, _rhoStart, _influence, _skirtFadeStart;
    private readonly double _riser, _period, _benchFraction;
    private readonly double _fillSlope, _softClamp, _islandK;
    private readonly double _coast, _flank, _shelfDepth, _beachW;
    private readonly double _enclosureR, _shoreR, _shelfR;

    public ClimateMachineRecipe Recipe { get; }
    public double SeaLevel => _sea;
    /// <summary>Radius of the flat pad plus apron: everything inside is exactly the pad height.</summary>
    public double FlatRadiusM => _rhoStart;
    public double PadRadiusM => _padR;
    public double InfluenceRadiusM => _influence;

    public ClimateForecourtOperator(ClimateMachineRecipe recipe, EmitterSiteRecipe emitter, double seaLevel)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (emitter == null) throw new ArgumentNullException(nameof(emitter));
        _sea = seaLevel;
        _halfSegment = recipe.ForecourtLengthM * 0.5d;
        _padR = recipe.ForecourtDepthM;
        _rhoStart = recipe.ForecourtDepthM + recipe.ApronM;
        _influence = recipe.InfluenceRadiusM;
        _skirtFadeStart = _influence - SkirtFadeM;
        // Cone / island constants are the emitter recipe's, so the two terrain operators stay one visual family.
        _riser = emitter.CutTerraceRiserM;
        _period = emitter.CutTerraceBenchM + emitter.CutTerraceRiserM / emitter.CutRiserSlope;
        _benchFraction = emitter.CutTerraceBenchM / _period;
        _fillSlope = emitter.FillSlope;
        _softClamp = emitter.SoftClampM;
        _islandK = emitter.IslandSoftK;
        _coast = emitter.IslandCoastM;
        _flank = emitter.FlankSlope;
        _shelfDepth = emitter.ShelfDepthM;
        _beachW = recipe.BeachM;
        _enclosureR = _rhoStart + QuayDeckSlopeRunM;
        _shoreR = _enclosureR + _beachW;
        _shelfR = _shoreR + QuayShelfWidthM;
    }

    /// <summary>Distance from (du, dw) to the footprint segment.</summary>
    public double FootprintDistance(double du, double dw)
    {
        double sx = Math.Max(0d, Math.Abs(du) - _halfSegment);
        return Math.Sqrt(sx * sx + dw * dw);
    }

    /// <summary>Shaped height at offset (<paramref name="du"/> along the ring, <paramref name="dw"/> across) from the machine.</summary>
    public double Shape(in ClimateMachine machine, double du, double dw, double h0)
        => ShapeCore(in machine, du, dw, h0, false, out _);

    /// <summary>Shaped height plus the surface classification for the same sample.</summary>
    public double Shape(in ClimateMachine machine, double du, double dw, double h0, out SiteSurfaceMask mask)
        => ShapeCore(in machine, du, dw, h0, true, out mask);

    /// <summary>Weight of the machine's influence: 1 on pad and apron, smoothly 0 at the influence radius.</summary>
    public double Weight(double du, double dw)
    {
        double s = FootprintDistance(du, dw);
        double t = Math.Clamp((s - _rhoStart) / Math.Max(1d, _influence - _rhoStart), 0d, 1d);
        return 1d - t * t * (3d - 2d * t);
    }

    /// <summary>Terrace staircase height above the pad at cone distance rho (monotone, C1).</summary>
    public double TerraceHeight(double rho)
    {
        double u = rho / _period;
        double n = Math.Floor(u);
        double f = u - n;
        return _riser * (n + Smooth(_benchFraction, 1d, f));
    }

    /// <summary>Quay shoulder profile at stadium distance s: P on pad and apron, PCHIP through coast, shore and shelf knots, linear flank beyond.</summary>
    public double QuayProfile(double s, double pad)
    {
        if (s <= _rhoStart) return pad;
        if (s >= _shelfR) return _sea - _shelfDepth - _flank * (s - _shelfR);
        double e0 = pad, e1 = _sea + _coast, e2 = _sea - 0.5d, e3 = _sea - _shelfDepth;
        double h0 = _enclosureR - _rhoStart, h1 = _beachW, h2 = _shelfR - _shoreR;
        double m0 = (e1 - e0) / h0, m1 = (e2 - e1) / h1, m2 = (e3 - e2) / h2;
        double d1 = Pchip(m0, m1, h0, h1), d2 = Pchip(m1, m2, h1, h2);
        if (s < _enclosureR) return Hermite(s, _rhoStart, h0, e0, e1, 0d, d1);
        if (s < _shoreR) return Hermite(s, _enclosureR, h1, e1, e2, d1, d2);
        return Hermite(s, _shoreR, h2, e2, e3, d2, m2);
    }

    private double ShapeCore(in ClimateMachine machine, double du, double dw, double h0, bool wantMask, out SiteSurfaceMask mask)
    {
        double s = FootprintDistance(du, dw);
        if (s >= _influence)
        {
            mask = default;
            return h0;
        }
        double pad = machine.PadHeight;
        bool quay = (machine.Flags & ClimateMachineFlags.Quay) != 0;

        // Pad plus apron: exactly P (the cone is degenerate at rho = 0).
        if (s <= _rhoStart)
        {
            mask = wantMask ? PadMask(s) : default;
            return pad;
        }

        // (1) quay raise, max-only, faded out by the influence radius.
        double h1 = h0;
        if (quay)
        {
            double raised = SmoothMax(h0, QuayProfile(s, pad), _islandK);
            double w = 1d - Smooth(_shelfR, _influence, s);
            h1 = h0 + (raised - h0) * w;
        }

        // (2) terraced cone clamp (soft windows shifted by k/2, as in the emitter operator).
        double rho = s - _rhoStart;
        double upper = pad + TerraceHeight(rho);
        double lower = pad - _fillSlope * rho;
        double k = _softClamp * Smooth(0d, SoftClampRampM, rho);
        double h2 = SmoothMax(SmoothMin(h1, upper + 0.5d * k, k), lower - 0.5d * k, k);

        // (3) skirt fade back to the (raised) natural height.
        double h3;
        if (s >= _influence) h3 = h1;
        else if (s <= _skirtFadeStart) h3 = h2;
        else
        {
            double t = Smooth(_skirtFadeStart, _influence, s);
            h3 = h2 + (h1 - h2) * t;
        }

        mask = wantMask ? ClassifySample(s, h0, h1, h3, quay) : default;
        return h3;
    }

    private SiteSurfaceMask PadMask(double s)
    {
        double hard = s <= _padR ? 1d : 1d - Smooth(_padR, _rhoStart, s);
        return new SiteSurfaceMask
        {
            Class = SiteSurfaceClass.Pad,
            Material = SiteMaterialHint.Rock,
            Hardscape = (float)hard,
            VegetationAllowed = false,
        };
    }

    private SiteSurfaceMask ClassifySample(double s, double h0, double h1, double h3, bool quay)
    {
        double rock = Smooth(MaskEdge0, MaskEdge1, h1 - h3);
        double fill = Smooth(MaskEdge0, MaskEdge1, h3 - h1);
        double sand = quay ? (1d - Smooth(_sea + 3d, _sea + 8d, h3)) * Smooth(_enclosureR - 150d, _enclosureR, s) : 0d;
        if (s > _shelfR) sand = 0d;
        SiteSurfaceClass cls;
        if (rock >= 0.5d) cls = SiteSurfaceClass.CutFace;
        else if (fill >= 0.5d) cls = SiteSurfaceClass.Fill;
        else if (sand >= 0.5d) cls = SiteSurfaceClass.Beach;
        else if (quay && h1 - h0 > MaskEdge0) cls = SiteSurfaceClass.IslandFlank;
        else cls = SiteSurfaceClass.None;
        bool vegetation = !(rock > 0.05d || sand > 0.25d);
        SiteMaterialHint material = rock > 0.05d ? SiteMaterialHint.Rock : sand > 0.25d ? SiteMaterialHint.Sand : SiteMaterialHint.None;
        return new SiteSurfaceMask
        {
            Class = cls, Material = material, Hardscape = 0f, Rock = (float)rock, Fill = (float)fill,
            Sand = (float)sand, VegetationAllowed = vegetation,
        };
    }

    // ---- smooth primitives (same definitions as EmitterSiteSurfaceOperator) -------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Smooth(double e0, double e1, double x)
    {
        double t = (x - e0) / (e1 - e0);
        if (t <= 0d) return 0d;
        if (t >= 1d) return 1d;
        return t * t * (3d - 2d * t);
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SmoothMin(double a, double b, double k)
    {
        if (k <= 0d) return Math.Min(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0d) / k;
        return Math.Min(a, b) - h * h * k * 0.25d;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double SmoothMax(double a, double b, double k)
    {
        if (k <= 0d) return Math.Max(a, b);
        double h = Math.Max(k - Math.Abs(a - b), 0d) / k;
        return Math.Max(a, b) + h * h * k * 0.25d;
    }
}
