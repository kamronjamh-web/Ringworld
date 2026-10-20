using System;
using System.Runtime.CompilerServices;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Pure, deterministic basin operator of the Weather Lung (integration plan D3). Immutable, allocation-free, thread-safe. Heights are presentation-space metres.
///
/// The footprint is the POI's elliptic coordinate r (r = 0 inner lip, r = 1 the 28 x 18 km outer edge); outside r = 1 the metric distance s = (r - 1) |dpt/dr| is
/// measured along the straight elliptic ray. The datum P is the site pad height (sea + 135 m), lowered by the curvature correction du^2 / (2 R) so the pad follows
/// the POI's flat tangent plane (the ring surface rises toward the hub away from the site).
///
/// Stages: (1) r &lt;= RCarve: P - 875 (below the -870 floor underside). (2) RCarve..RPad: C1 smoothstep up to P, entirely inside the solid foundation (r 0.01..0.24),
/// so the shoreline and cliff stay hidden under the rim. (3) RPad..edge plus a 400 m apron: exactly P (Hardscape). (4) Beyond the apron the climate forecourt stages:
/// quay raise (max-only, only when the placement flagged water), terraced cone clamp, 4 km skirt fade to the (raised) natural height, influence ending at s = 6 km.
/// </summary>
public sealed class WeatherLungBasinOperator
{
    /// <summary>Mixed into the v8 terrain definition hash with the placement identity: bump when any constant or formula below changes.</summary>
    public const int OperatorVersion = 1;

    private const double MaskEdge0 = 2d, MaskEdge1 = 10d;
    /// <summary>Upper bound of the elliptic search: s at r = 3 is at least 2 x 3,350 m, beyond the 6 km influence.</summary>
    private const double RSearchMax = 3d;
    private const double RSearchMin = -1d;

    private readonly double _sea, _ringRadius;
    private readonly double _lipX, _lipY, _growX, _growY;
    private readonly double _rCarve, _rPad, _carve;
    private readonly double _apron, _influence, _skirtFadeStart, _boundX, _boundY;
    private readonly double _riser, _period, _benchFraction;
    private readonly double _fillSlope, _softClamp, _softClampRamp, _islandK;
    private readonly double _coast, _flank, _shelfDepth, _beachW;
    private readonly double _enclosureR, _shoreR, _shelfR;

    public WeatherLungRecipe Recipe { get; }
    public double SeaLevel => _sea;
    /// <summary>s at which the exact pad height ends (apron width): everything with r &gt;= RPad and s up to this value is the pad.</summary>
    public double ApronM => _apron;
    public double InfluenceM => _influence;

    public WeatherLungBasinOperator(WeatherLungRecipe recipe, EmitterSiteRecipe emitter, double seaLevel)
    {
        Recipe = recipe ?? throw new ArgumentNullException(nameof(recipe));
        if (emitter == null) throw new ArgumentNullException(nameof(emitter));
        _sea = seaLevel;
        _ringRadius = recipe.RingRadiusM;
        _lipX = recipe.LipSemiAxisXM; _lipY = recipe.LipSemiAxisYM;
        _growX = recipe.RimGrowthXM; _growY = recipe.RimGrowthYM;
        _rCarve = recipe.RCarve; _rPad = recipe.RPad; _carve = recipe.CarveDepthM;
        _apron = recipe.ApronM;
        _influence = recipe.InfluenceM;
        _skirtFadeStart = _influence - recipe.SkirtFadeM;
        _boundX = recipe.BoundHalfXM; _boundY = recipe.BoundHalfYM;
        // Cone / island constants are the emitter recipe's, so all terrain operators stay one visual family.
        _riser = emitter.CutTerraceRiserM;
        _period = emitter.CutTerraceBenchM + emitter.CutTerraceRiserM / emitter.CutRiserSlope;
        _benchFraction = emitter.CutTerraceBenchM / _period;
        _fillSlope = emitter.FillSlope;
        _softClamp = emitter.SoftClampM;
        _softClampRamp = recipe.SoftClampRampM;
        _islandK = emitter.IslandSoftK;
        _coast = emitter.IslandCoastM;
        _flank = emitter.FlankSlope;
        _shelfDepth = emitter.ShelfDepthM;
        _beachW = recipe.BeachM;
        _enclosureR = _apron + recipe.QuayDeckSlopeRunM;
        _shoreR = _enclosureR + _beachW;
        _shelfR = _shoreR + recipe.QuayShelfWidthM;
    }

    /// <summary>Curvature correction du^2 / (2 R): how far the ring surface lies above the POI tangent plane at along-ring offset du.</summary>
    public double Curvature(double du) => du * du / (2d * _ringRadius);

    /// <summary>Pad height at along-ring offset du: the site datum minus the curvature correction.</summary>
    public double PadAt(in WeatherLungSite site, double du) => site.PadHeight - Curvature(du);

    /// <summary>Elliptic semi-axes at r.</summary>
    public void SemiAxes(double r, out double a, out double b)
    {
        a = _lipX + _growX * r;
        b = _lipY + _growY * r;
    }

    /// <summary>
    /// Elliptic coordinate of asset-local (x, y): the r solving x^2 / a(r)^2 + y^2 / b(r)^2 = 1. Returns -1 for points deep inside and 3 (the search limit) for points
    /// beyond it. The function is convex and decreasing, so Newton from the left converges monotonically.
    /// </summary>
    public double SolveR(double x, double y)
    {
        double x2 = x * x, y2 = y * y;
        double r = RSearchMin;
        double a = _lipX + _growX * r, b = _lipY + _growY * r;
        double f = x2 / (a * a) + y2 / (b * b);
        if (f <= 1d) return RSearchMin;
        a = _lipX + _growX * RSearchMax; b = _lipY + _growY * RSearchMax;
        if (x2 / (a * a) + y2 / (b * b) >= 1d) return RSearchMax;
        for (int i = 0; i < 80; i++)
        {
            a = _lipX + _growX * r; b = _lipY + _growY * r;
            f = x2 / (a * a) + y2 / (b * b);
            double slope = -2d * (x2 * _growX / (a * a * a) + y2 * _growY / (b * b * b));
            double step = (f - 1d) / -slope;
            r += step;
            if (step < 1e-13) break;
        }
        return Math.Min(r, RSearchMax);
    }

    /// <summary>Elliptic coordinate r and the metric distance s (negative inside the outer edge) of asset-local (x, y).</summary>
    public void Locate(double x, double y, out double r, out double s)
    {
        r = SolveR(x, y);
        if (r >= RSearchMax) { s = double.MaxValue; return; }
        double a = _lipX + _growX * r, b = _lipY + _growY * r;
        double u = x / a, v = y / b;
        double n = Math.Sqrt(u * u + v * v);
        double d = n < 1e-12 ? _growX : Math.Sqrt((_growX * u) * (_growX * u) + (_growY * v) * (_growY * v)) / n;
        s = (r - 1d) * d;
    }

    /// <summary>Asset-local (x, y) of the canonical offsets for the site yaw.</summary>
    public static void ToAssetLocal(in WeatherLungSite site, double du, double dw, out double x, out double y)
    {
        double c = site.CosYaw, s = site.SinYaw;
        x = du * c + dw * s;
        y = dw * c - du * s;
    }

    /// <summary>Shaped height at canonical offset (<paramref name="du"/> along the ring, <paramref name="dw"/> across) from the site.</summary>
    public double Shape(in WeatherLungSite site, double du, double dw, double h0)
        => ShapeCore(in site, du, dw, h0, false, out _);

    /// <summary>Shaped height plus the surface classification for the same sample.</summary>
    public double Shape(in WeatherLungSite site, double du, double dw, double h0, out SiteSurfaceMask mask)
        => ShapeCore(in site, du, dw, h0, true, out mask);

    /// <summary>Weight of the site's influence: 1 inside the pad and apron, smoothly 0 at the influence distance.</summary>
    public double Weight(in WeatherLungSite site, double du, double dw)
    {
        ToAssetLocal(in site, du, dw, out double x, out double y);
        if (Math.Abs(x) >= _boundX || Math.Abs(y) >= _boundY) return 0d;
        Locate(x, y, out _, out double s);
        if (s >= _influence) return 0d;
        double t = Math.Clamp((s - _apron) / Math.Max(1d, _influence - _apron), 0d, 1d);
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

    /// <summary>Quay shoulder profile at distance s: P on pad and apron, PCHIP through coast, shore and shelf knots, linear flank beyond.</summary>
    public double QuayProfile(double s, double pad)
    {
        if (s <= _apron) return pad;
        if (s >= _shelfR) return _sea - _shelfDepth - _flank * (s - _shelfR);
        double e0 = pad, e1 = _sea + _coast, e2 = _sea - 0.5d, e3 = _sea - _shelfDepth;
        double h0 = _enclosureR - _apron, h1 = _beachW, h2 = _shelfR - _shoreR;
        double m0 = (e1 - e0) / h0, m1 = (e2 - e1) / h1, m2 = (e3 - e2) / h2;
        double d1 = Pchip(m0, m1, h0, h1), d2 = Pchip(m1, m2, h1, h2);
        if (s < _enclosureR) return Hermite(s, _apron, h0, e0, e1, 0d, d1);
        if (s < _shoreR) return Hermite(s, _enclosureR, h1, e1, e2, d1, d2);
        return Hermite(s, _shoreR, h2, e2, e3, d2, m2);
    }

    private double ShapeCore(in WeatherLungSite site, double du, double dw, double h0, bool wantMask, out SiteSurfaceMask mask)
    {
        ToAssetLocal(in site, du, dw, out double x, out double y);
        mask = default;
        if (Math.Abs(x) >= _boundX || Math.Abs(y) >= _boundY) return h0;
        Locate(x, y, out double r, out double s);
        if (s >= _influence) return h0;
        double pad = site.PadHeight - Curvature(du);

        // (1) basin floor, (2) C1 blend up to the pad: both inside the solid foundation.
        if (r <= _rPad)
        {
            if (wantMask) mask = BasinMask();
            if (r <= _rCarve) return pad - _carve;
            double u = (r - _rCarve) / (_rPad - _rCarve);
            return pad - _carve + _carve * (u * u * (3d - 2d * u));
        }

        // (3) pad plus apron: exactly P.
        if (s <= _apron)
        {
            if (wantMask) mask = PadMask(s);
            return pad;
        }

        // (4a) quay raise, max-only, faded out by the influence distance.
        bool quay = site.IsQuay;
        double h1 = h0;
        if (quay)
        {
            double raised = SmoothMax(h0, QuayProfile(s, pad), _islandK);
            double w = 1d - Smooth(_shelfR, _influence, s);
            h1 = h0 + (raised - h0) * w;
        }

        // (4b) terraced cone clamp (soft windows shifted by k/2, as in the climate operator).
        double rho = s - _apron;
        double upper = pad + TerraceHeight(rho);
        double lower = pad - _fillSlope * rho;
        double k = _softClamp * Smooth(0d, _softClampRamp, rho);
        double h2 = SmoothMax(SmoothMin(h1, upper + 0.5d * k, k), lower - 0.5d * k, k);

        // (4c) skirt fade back to the (raised) natural height.
        double h3;
        if (s <= _skirtFadeStart) h3 = h2;
        else
        {
            double t = Smooth(_skirtFadeStart, _influence, s);
            h3 = h2 + (h1 - h2) * t;
        }

        if (wantMask) mask = ClassifySample(s, h0, h1, h3, quay);
        return h3;
    }

    private static SiteSurfaceMask BasinMask() => new()
    {
        Class = SiteSurfaceClass.CutFace,
        Material = SiteMaterialHint.Rock,
        Hardscape = 0f,
        Rock = 1f,
        VegetationAllowed = false,
    };

    private SiteSurfaceMask PadMask(double s)
    {
        double hard = s <= 0d ? 1d : 1d - Smooth(0d, _apron, s);
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
        bool vegetation = !(rock > 0.05d || sand > 0.25d || (quay && fill > 0.05d));
        // A raised plinth over water reads as a deliberate seawall: fill is rock, not vegetation ground.
        bool seawall = quay && fill > 0.05d;
        SiteMaterialHint material = rock > 0.05d || seawall ? SiteMaterialHint.Rock : sand > 0.25d ? SiteMaterialHint.Sand : SiteMaterialHint.None;
        return new SiteSurfaceMask
        {
            Class = cls, Material = material, Hardscape = 0f, Rock = (float)rock, Fill = (float)fill,
            Sand = (float)sand, VegetationAllowed = vegetation,
        };
    }

    // ---- smooth primitives (same definitions as ClimateForecourtOperator) --------------------------------------

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
