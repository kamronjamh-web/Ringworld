using System;
using Godot;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>
/// Named depths and dimensions of one kind's underground air volume, in site-local metres (y relative to the pad datum).
/// </summary>
public sealed class EmitterSiteUndergroundShape
{
    public SiteKind Kind { get; }
    /// <summary>Bore liner radius from the surface down to <see cref="PlugDepthM"/>.</summary>
    public float BoreRadiusM { get; }
    public float BoreTopYM { get; }
    /// <summary>Depth of the (open) plug iris: the bore ends and the shaft takes over here.</summary>
    public float PlugDepthM => -EmitterSiteApertures.PlugDepthM;
    public float ShaftRadiusM { get; }
    /// <summary>Top of the shaft primitive; overlaps the bore by 1 m so the column has no seam.</summary>
    public float ShaftTopYM { get; }
    /// <summary>Lowest point of the shaft primitive; it overlaps the cavern roof.</summary>
    public float ShaftBottomYM { get; }
    /// <summary>Reactor <c>ExhaustTop</c> marker height in site-local y: the shaft/cavern roof interface the spire's ShaftBottom aligns with.</summary>
    public float ExhaustTopYM { get; }
    public float ChamberCenterYM { get; }
    public float CavernRoofApexYM { get; }
    public float CavernFloorYM { get; }
    public float CoreWallRadiusM { get; }
    /// <summary><c>ArrivalOverlook</c> marker in site-local metres (asset frame: +Z is the arrival side).</summary>
    public Vector3 ArrivalOverlookLocal { get; }
    public Aabb LocalBounds { get; }
    /// <summary>Largest horizontal reach from the bore axis (AABB circumscribed radius), for O(1) pre-rejects.</summary>
    public float MaxHorizontalReachM { get; }

    // Precomputed primitive constants (double: the SDF evaluates in double and narrows only the result).
    internal readonly double BoreR, BoreTop, BoreBottom, ShaftR, ShaftTop, ShaftBottom;
    internal readonly double Yc, CoreInnerR, CoreHalfCore, CoreEdge, SmoothK;
    internal readonly double Bay0X, Bay0Z, Bay0R, Bay1X, Bay1Z, Bay1R, Bay2X, Bay2Z, Bay2R;
    internal readonly double TunnelY, TunnelZ0, TunnelZ1, TunnelR;
    internal readonly double BoxMinX, BoxMaxX, BoxMinY, BoxMaxY, BoxMinZ, BoxMaxZ;

    internal EmitterSiteUndergroundShape(SiteKind kind)
    {
        Kind = kind;
        bool main = kind == SiteKind.Main;
        // Spec 3.2 is authored for the regular reactor. MainReactorPOI is the regular one scaled 2x (assets/poi/README.md),
        // so the cavern, bays and tunnel scale by 2. The shaft radius is the exception: 2x85 would cover the main exhaust
        // roof ring (clear radius 150), so the main shaft uses the bore liner radius (151, exhaust clear radius 152.01).
        double s = main ? 2d : 1d;
        BoreR = EmitterSiteApertures.LinerRadius(kind);
        BoreTop = 16d;
        // The bore primitive runs 60 m below the plug depth so the signed distance on the axis keeps clearance at the seam; below
        // the plug the shaft (radius >= bore) already covers it, so the air region is exactly "bore to -200, shaft below".
        BoreBottom = -EmitterSiteApertures.PlugDepthM - 60d;
        ShaftR = main ? EmitterSiteApertures.MainLinerRadiusM : 85d;
        ShaftTop = -EmitterSiteApertures.PlugDepthM + 1d;
        Yc = -4000d;
        ShaftBottom = Yc + 990d * s;
        CoreInnerR = 1240d * s - 600d * s;
        CoreHalfCore = 400d * s;
        CoreEdge = 600d * s;
        SmoothK = 120d * s;
        double bayRing = 1050d * s;
        Bay0X = 0d; Bay0Z = bayRing; Bay0R = 420d * s; // arrival bay, Godot azimuth 90 degrees (+Z)
        double c = Math.Cos(210d * Math.PI / 180d), sn = Math.Sin(210d * Math.PI / 180d);
        Bay1X = bayRing * c; Bay1Z = bayRing * sn; Bay1R = 500d * s;
        c = Math.Cos(330d * Math.PI / 180d); sn = Math.Sin(330d * Math.PI / 180d);
        Bay2X = bayRing * c; Bay2Z = bayRing * sn; Bay2R = 500d * s;
        TunnelY = Yc + 80d * s; TunnelZ0 = 1470d * s; TunnelZ1 = TunnelZ0 + 300d * s; TunnelR = 14d * s;

        // AABB: union of the primitive extents plus the smooth-union bulge (k/4) and 1 m.
        double pad = SmoothK * 0.25d + 1d;
        double coreR = 1240d * s;
        double maxX = Math.Max(coreR, Math.Max(Math.Max(Math.Abs(Bay0X) + Bay0R, Math.Abs(Bay1X) + Bay1R), Math.Abs(Bay2X) + Bay2R));
        double maxZ = Math.Max(coreR, Math.Max(Math.Max(Bay0Z + Bay0R, TunnelZ1 + TunnelR), Math.Max(Bay1Z + Bay1R, Bay2Z + Bay2R)));
        double minZ = Math.Min(-coreR, Math.Min(Math.Min(Bay0Z - Bay0R, Bay1Z - Bay1R), Bay2Z - Bay2R));
        BoxMinX = -maxX - pad; BoxMaxX = maxX + pad;
        BoxMinZ = minZ - pad; BoxMaxZ = maxZ + pad;
        BoxMinY = Yc - 1000d * s - pad; BoxMaxY = BoreTop + 1d;
        LocalBounds = new Aabb(new Vector3((float)BoxMinX, (float)BoxMinY, (float)BoxMinZ),
            new Vector3((float)(BoxMaxX - BoxMinX), (float)(BoxMaxY - BoxMinY), (float)(BoxMaxZ - BoxMinZ)));
        MaxHorizontalReachM = (float)Math.Sqrt(Math.Max(BoxMinX * BoxMinX, BoxMaxX * BoxMaxX) + Math.Max(BoxMinZ * BoxMinZ, BoxMaxZ * BoxMaxZ));

        BoreRadiusM = (float)BoreR; BoreTopYM = (float)BoreTop;
        ShaftRadiusM = (float)ShaftR; ShaftTopYM = (float)ShaftTop; ShaftBottomYM = (float)ShaftBottom;
        ExhaustTopYM = (float)(Yc + (main ? 2078d : 1039d)); // ExhaustTop marker of ReactorPOI / MainReactorPOI
        ChamberCenterYM = (float)Yc;
        CavernRoofApexYM = (float)(Yc + 1000d * s);
        CavernFloorYM = (float)(Yc - 1000d * s);
        CoreWallRadiusM = (float)coreR;
        ArrivalOverlookLocal = main ? new Vector3(0f, (float)Yc + 160f, 2770f) : new Vector3(0f, (float)Yc + 80f, 1385f);
    }

    /// <summary>Air-negative signed distance (metres, double) of a point in the site-local asset frame.</summary>
    internal double Evaluate(double x, double y, double z)
    {
        double rho = Math.Sqrt(x * x + z * z);

        // Bore + shaft: finite cylinders, air positive (min of half-spaces), unioned with max.
        double air = Math.Min(BoreR - rho, Math.Min(y - BoreBottom, BoreTop - y));
        double shaft = Math.Min(ShaftR - rho, Math.Min(y - ShaftBottom, ShaftTop - y));
        if (shaft > air) air = shaft;

        double cavern = CavernAir(x, y, z, rho);
        double tunnel = TunnelAir(x, y, z);
        if (cavern > air) air = cavern;
        if (tunnel > air) air = tunnel;
        return -air;
    }

    /// <summary>Air-positive distance of the core + bay union alone (no bore, shaft or tunnel); used by the offline shell bake.</summary>
    internal double CavernAir(double x, double y, double z)
        => CavernAir(x, y, z, Math.Sqrt(x * x + z * z));

    private double CavernAir(double x, double y, double z, double rho)
    {
        // Core cavern: rounded cylinder.
        double qx = rho - CoreInnerR, qy = Math.Abs(y - Yc) - CoreHalfCore;
        double qxp = qx > 0d ? qx : 0d, qyp = qy > 0d ? qy : 0d;
        double core = CoreEdge - (Math.Sqrt(qxp * qxp + qyp * qyp) + Math.Min(Math.Max(qx, qy), 0d));

        // Bays: spheres, smooth-max'd with the core (polynomial smax, raises air by at most k/4 near the seam).
        double dy = y - Yc;
        double bay0 = Bay0R - Math.Sqrt(x * x + dy * dy + (z - Bay0Z) * (z - Bay0Z));
        double bx = x - Bay1X, bz = z - Bay1Z;
        double bay1 = Bay1R - Math.Sqrt(bx * bx + dy * dy + bz * bz);
        bx = x - Bay2X; bz = z - Bay2Z;
        double bay2 = Bay2R - Math.Sqrt(bx * bx + dy * dy + bz * bz);
        return SmoothMax(SmoothMax(SmoothMax(core, bay0), bay1), bay2);
    }

    /// <summary>Air-positive distance of the arrival tunnel stub capsule alone.</summary>
    internal double TunnelAir(double x, double y, double z)
    {
        // Arrival tunnel stub: capsule along +Z from the arrival bay wall.
        double tz = z < TunnelZ0 ? TunnelZ0 : (z > TunnelZ1 ? TunnelZ1 : z);
        double ty = y - TunnelY, tzd = z - tz;
        return TunnelR - Math.Sqrt(x * x + ty * ty + tzd * tzd);
    }

    private double SmoothMax(double a, double b)
    {
        double h = SmoothK - Math.Abs(a - b);
        if (h <= 0d) return a > b ? a : b;
        return (a > b ? a : b) + h * h * (0.25d / SmoothK);
    }
}

/// <summary>
/// M2-1: the single answer to "is this point inside a site's underground air?". Pure C#, immutable and thread-safe; per-kind
/// constants are precomputed. Built from docs/emitter-site-terrain-spec.md section 3.2 (bore, shaft, rounded-cylinder core,
/// three bay spheres smooth-unioned with k = 120, arrival tunnel stub) with the user decision that the plug opens: the bore
/// (liner radius, surface to -200 m) and the shaft (-199 m down into the cavern roof) overlap, so the air column is continuous.
/// The Main variant is the regular shape scaled 2x about the bore axis / chamber centre (MainReactorPOI is the 2x reactor).
///
/// Site-local frame: origin on the bore axis at the pad datum (P + the placement pad clearance, as
/// <c>EmitterSiteNavigator.AssetLocalToCanonical</c>), +Y inward up, X/Z as in the POI asset frame. Canonical (around,
/// vertical, across) maps in via the placement yaw theta exactly as that navigator and <c>EmitterSiteFrameContext.SiteTransform</c>
/// do. Signed distance: negative = air, positive = rock. Every primitive is 1-Lipschitz; unions are min/max/smooth-max.
/// </summary>
public static class EmitterSiteUnderground
{
    private static readonly EmitterSiteUndergroundShape RegularShape = new(SiteKind.Regular);
    private static readonly EmitterSiteUndergroundShape MainShape = new(SiteKind.Main);

    public static EmitterSiteUndergroundShape Shape(SiteKind kind) => kind == SiteKind.Main ? MainShape : RegularShape;

    /// <summary>Signed distance in metres; negative = air.</summary>
    public static float SignedDistance(SiteKind kind, Vector3 local)
        => (float)Shape(kind).Evaluate(local.X, local.Y, local.Z);

    /// <summary>Double-precision overload for callers that already hold the local point in double.</summary>
    public static double SignedDistance(SiteKind kind, double x, double y, double z) => Shape(kind).Evaluate(x, y, z);

    /// <summary>True when the point is air with at least <paramref name="margin"/> metres of clearance to rock.</summary>
    public static bool Contains(SiteKind kind, Vector3 local, float margin = 0f)
        => Shape(kind).Evaluate(local.X, local.Y, local.Z) < -(double)margin;

    public static Aabb LocalBounds(SiteKind kind) => Shape(kind).LocalBounds;

    /// <summary>
    /// Site-local point of a canonical point. The around delta wraps via <paramref name="circumferenceMeters"/>; the yaw maps
    /// like <c>AssetLocalToCanonical</c> (local z points toward -across). Flat-tangent approximation (curvature error is
    /// below 1.2 m at R1500 and 4 km depth, spec 3.1).
    /// </summary>
    public static Vector3 ToLocal(in EmitterSitePlacement placement, double circumferenceMeters, double around, double vertical, double across)
    {
        double sin = Math.Sin(placement.YawRadians), cos = Math.Cos(placement.YawRadians);
        ToLocalD(placement, circumferenceMeters, around, vertical, across, sin, cos, out double x, out double y, out double z);
        return new Vector3((float)x, (float)y, (float)z);
    }

    private static void ToLocalD(in EmitterSitePlacement p, double circumference, double around, double vertical, double across,
        double sin, double cos, out double x, out double y, out double z)
    {
        double fx = WrapDelta(around - p.AroundMeters, circumference);
        double fz = -(across - p.AcrossMeters);
        x = cos * fx - sin * fz;
        z = sin * fx + cos * fz;
        y = vertical - (p.PadHeightMeters + EmitterSiteFrameMath.DefaultPadClearanceMeters);
    }

    public static bool ContainsCanonical(in EmitterSitePlacement placement, double circumferenceMeters, double around, double vertical, double across, float margin = 0f)
    {
        EmitterSiteUndergroundShape shape = Shape(placement.Kind);
        double sin = Math.Sin(placement.YawRadians), cos = Math.Cos(placement.YawRadians);
        ToLocalD(placement, circumferenceMeters, around, vertical, across, sin, cos, out double x, out double y, out double z);
        return shape.Evaluate(x, y, z) < -(double)margin;
    }

    /// <summary>
    /// Conservative bound for the later density carve hook (P6): the ball of radius <paramref name="boundingRadius"/> about
    /// <paramref name="localCenter"/> (a box's centre and half diagonal). Returns false when the ball cannot touch the site AABB
    /// (the point is rock: signed distance &gt; 0 throughout). Otherwise <paramref name="lower"/> and <paramref name="upper"/>
    /// bound the signed distance over the ball (centre value -/+ radius, widened by a 1e-3 margin); <c>upper &lt; 0</c> means
    /// the whole ball is air.
    /// </summary>
    public static bool TryBoundCarve(SiteKind kind, Vector3 localCenter, float boundingRadius, out float lower, out float upper)
    {
        EmitterSiteUndergroundShape s = Shape(kind);
        double r = boundingRadius;
        if (localCenter.X + r < s.BoxMinX || localCenter.X - r > s.BoxMaxX || localCenter.Y + r < s.BoxMinY || localCenter.Y - r > s.BoxMaxY
            || localCenter.Z + r < s.BoxMinZ || localCenter.Z - r > s.BoxMaxZ)
        {
            lower = float.PositiveInfinity; upper = float.PositiveInfinity;
            return false;
        }
        double d = s.Evaluate(localCenter.X, localCenter.Y, localCenter.Z);
        lower = (float)(d - r - 1e-3d);
        upper = (float)(d + r + 1e-3d);
        return true;
    }

    /// <summary>Canonical box form of <see cref="TryBoundCarve(SiteKind, Vector3, float, out float, out float)"/> (centre plus half diagonal; rotation invariant).</summary>
    public static bool TryBoundCarve(in EmitterSitePlacement placement, double circumferenceMeters, double around, double vertical, double across,
        float boundingRadius, out float lower, out float upper)
        => TryBoundCarve(placement.Kind, ToLocal(placement, circumferenceMeters, around, vertical, across), boundingRadius, out lower, out upper);

    /// <summary>
    /// O(1): the site whose underground contains the canonical point, via the nearest-cell lookup then a Contains test. The
    /// influence flag is ignored on purpose (as the aperture lookup): the nearest-cell site is the only candidate.
    /// </summary>
    public static bool TryFindSiteUnderground(EmitterSiteRegistry? registry, double around, double vertical, double across, out int site, out Vector3 local)
    {
        site = -1; local = default;
        if (registry == null) return false;
        registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
        ref readonly EmitterSite s = ref registry.Sites[index];
        EmitterSiteUndergroundShape shape = Shape(s.Kind);
        double y = vertical - ((double)s.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters);
        if (y < shape.BoxMinY || y > shape.BoxMaxY) return false;
        double reach = shape.MaxHorizontalReachM;
        if (du * du + dw * dw > reach * reach) return false;
        double yaw = (EmitterSitePlacementSource.AssetEntryBearingDegrees + (double)s.BetaDegrees) * (Math.PI / 180d);
        double sin = Math.Sin(yaw), cos = Math.Cos(yaw);
        double fz = -dw;
        double x = cos * du - sin * fz, z = sin * du + cos * fz;
        if (shape.Evaluate(x, y, z) >= 0d) return false;
        site = index;
        local = new Vector3((float)x, (float)y, (float)z);
        return true;
    }

    /// <summary>
    /// Site-local point of the canonical point for the nearest-cell site when it lies inside that site's horizontal reach (the local
    /// XZ box of the volume); the vertical is not tested and the signed distance is not evaluated. Used by recovery.
    /// </summary>
    public static bool TryLocateInReach(EmitterSiteRegistry? registry, double around, double vertical, double across, out int site, out Vector3 local)
    {
        site = -1; local = default;
        if (registry == null) return false;
        registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
        ref readonly EmitterSite s = ref registry.Sites[index];
        EmitterSiteUndergroundShape shape = Shape(s.Kind);
        double yaw = (EmitterSitePlacementSource.AssetEntryBearingDegrees + (double)s.BetaDegrees) * (Math.PI / 180d);
        double sin = Math.Sin(yaw), cos = Math.Cos(yaw);
        double fz = -dw;
        double x = cos * du - sin * fz, z = sin * du + cos * fz;
        if (x < shape.BoxMinX || x > shape.BoxMaxX || z < shape.BoxMinZ || z > shape.BoxMaxZ) return false;
        site = index;
        local = new Vector3((float)x, (float)(vertical - ((double)s.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters)), (float)z);
        return true;
    }

    /// <summary>First coarse step (metres) of the downward floor march before the bisection refine.</summary>
    public const double FloorMarchStepMeters = 8d;

    /// <summary>
    /// M2-3 open-plug "ground": the vertical of the first rock below the point, marching down the site-local -Y with
    /// steps of max(8 m, clearance) (sphere tracing, safe because the SDF is 1-Lipschitz) and bisecting the bracket.
    /// The point must be inside the volume, or inside the bore column above it (then the march starts at the bore top).
    /// With no rock before <c>CavernFloorYM - 10</c> the cavern floor depth is returned. False when the point is outside.
    /// </summary>
    public static bool TryFindFloorBelow(EmitterSiteRegistry? registry, double around, double vertical, double across, out float floorVertical)
    {
        floorVertical = 0f;
        if (registry == null) return false;
        registry.TryGetInfluencingSite(around, across, out int index, out double du, out double dw);
        ref readonly EmitterSite s = ref registry.Sites[index];
        EmitterSiteUndergroundShape shape = Shape(s.Kind);
        double datum = (double)s.PadHeight + EmitterSiteFrameMath.DefaultPadClearanceMeters;
        double reach = shape.MaxHorizontalReachM;
        if (du * du + dw * dw > reach * reach) return false;
        double yaw = (EmitterSitePlacementSource.AssetEntryBearingDegrees + (double)s.BetaDegrees) * (Math.PI / 180d);
        double sin = Math.Sin(yaw), cos = Math.Cos(yaw);
        double fz = -dw;
        double x = cos * du - sin * fz, z = sin * du + cos * fz;
        double y = vertical - datum;
        if (y > shape.BoxMaxY - 1d)
        {
            // Above the bore top: only the bore column (inside the liner radius) continues into the volume.
            if (x * x + z * z >= shape.BoreR * shape.BoreR) return false;
            y = shape.BoxMaxY - 1.5d;
        }
        if (y < shape.BoxMinY || shape.Evaluate(x, y, z) >= 0d) return false;
        double cap = (double)shape.CavernFloorYM - 10d;
        double top = y; // air
        while (true)
        {
            double sd = shape.Evaluate(x, top, z);
            double step = Math.Max(FloorMarchStepMeters, -sd - 1d);
            double next = top - step;
            if (next <= cap)
            {
                if (shape.Evaluate(x, cap, z) < 0d) { floorVertical = (float)(datum + shape.CavernFloorYM); return true; }
                next = cap;
            }
            if (shape.Evaluate(x, next, z) >= 0d)
            {
                double lo = next, hi = top; // lo rock, hi air
                for (int i = 0; i < 24; i++)
                {
                    double mid = 0.5d * (lo + hi);
                    if (shape.Evaluate(x, mid, z) >= 0d) lo = mid; else hi = mid;
                }
                floorVertical = (float)(datum + hi);
                return true;
            }
            top = next;
            if (top <= cap) { floorVertical = (float)(datum + shape.CavernFloorYM); return true; }
        }
    }

    private static double WrapDelta(double delta, double circumference)
    {
        if (!(circumference > 0d)) return delta;
        double half = circumference * 0.5d;
        if (delta > half || delta < -half)
        {
            delta %= circumference;
            if (delta > half) delta -= circumference; else if (delta < -half) delta += circumference;
        }
        return delta;
    }
}
