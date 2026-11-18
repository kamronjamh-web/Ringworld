using Godot;

/// <summary>
/// R1-6c: decides when the ring sky's radiance map should refresh. The sky's per-frame inputs live in global shader parameters (R1-6b), so
/// only a write to the sky material dirties the map; DaySideAtmosphere writes radiance_epoch when <see cref="Step"/> returns true.
/// Pure logic (no node, no engine state): a refresh is due on a discrete event, when a tracked input moved past its threshold since the
/// last refresh, or when <see cref="MaxAgeSeconds"/> have passed. Thresholds are estimates, tuned in R1-6e (docs/work-orders/reflections/R1-6-design.md 4.2).
/// GDScript calls <see cref="Update"/> with a packed array (layout below); tests call <see cref="Step"/> with an <see cref="Inputs"/>.
/// </summary>
public partial class RingSkyRadianceCadence : RefCounted
{
    public const double AngleThresholdDegrees = 0.02;
    public const double SpinThresholdDegrees = 0.2;
    public const double HeightAbsoluteMeters = 1.0;
    public const double HeightRelative = 0.002;
    public const double AcrossMeters = 100.0;
    public const double TeleportMeters = 1000.0;
    public const double LuminanceRelative = 0.004;
    public const double ColorRelative = 0.004;
    public const double AbsoluteThreshold = 0.003;
    public const double MaxAgeSeconds = 0.5;

    public struct Inputs
    {
        public Vector3 Sun, Hub, Giant, Up;
        public double Height, Across;
        public Vector3 CameraPosition;
        public Vector3 HazeSun, HazeAmbientDay, HazeAmbientNight;
        public double AtmosphereFactor, RingInteriorFactor, EclipseCoverage, EclipseTransmittance, HubLevel, StarVisibility;
        public Vector4 HazeStorm;
        public double StormLightStrength;
        public Vector3 StormLightMega;
        public Vector3 SkyTop, SkyHorizon, SunColor, HubColor;
        public double SpinRadians;
        public ulong Revision, StormRevision;
        public bool LutValid;
    }

    /// <summary>Why the last <see cref="Step"/> refreshed (empty when it did not).</summary>
    public string LastReason { get; private set; } = "";

    private bool _have;
    private Inputs _at;          // inputs at the last refresh
    private Vector3 _lastPosition;
    private double _lastRefreshSeconds;

    /// <summary>Forget the last refresh: the next Step refreshes (first frame, scene or sky material reload).</summary>
    public void Reset() { _have = false; }

    public bool Step(in Inputs now, double nowSeconds, bool externalRefresh = false)
    {
        string reason = Evaluate(now, nowSeconds);
        if (externalRefresh && reason.Length == 0) reason = "external";
        _lastPosition = now.CameraPosition;
        if (reason.Length == 0)
        {
            LastReason = "";
            return false;
        }
        LastReason = reason;
        _at = now;
        _lastRefreshSeconds = nowSeconds;
        _have = true;
        return true;
    }

    private string Evaluate(in Inputs n, double nowSeconds)
    {
        if (!_have) return "first";
        var a = _at;
        if (n.Revision != a.Revision || n.StormRevision != a.StormRevision) return "revision";
        if (n.LutValid != a.LutValid) return "lut";
        if (n.CameraPosition.DistanceTo(_lastPosition) > TeleportMeters) return "teleport";
        if (AngleDegrees(n.Sun, a.Sun) > AngleThresholdDegrees) return "sun";
        if (AngleDegrees(n.Hub, a.Hub) > AngleThresholdDegrees) return "hub";
        if (AngleDegrees(n.Giant, a.Giant) > AngleThresholdDegrees) return "giant";
        if (AngleDegrees(n.Up, a.Up) > AngleThresholdDegrees) return "up";
        if (System.Math.Abs(n.Height - a.Height) > System.Math.Max(HeightAbsoluteMeters, HeightRelative * System.Math.Abs(a.Height))) return "height";
        if (System.Math.Abs(n.Across - a.Across) > AcrossMeters) return "across";
        if (LuminanceChange(n.HazeSun, a.HazeSun) > LuminanceRelative) return "haze_sun";
        if (LuminanceChange(n.HazeAmbientDay, a.HazeAmbientDay) > LuminanceRelative) return "haze_ambient_day";
        if (LuminanceChange(n.HazeAmbientNight, a.HazeAmbientNight) > LuminanceRelative) return "haze_ambient_night";
        if (Moved(n.AtmosphereFactor, a.AtmosphereFactor)) return "atmosphere";
        if (Moved(n.RingInteriorFactor, a.RingInteriorFactor)) return "interior";
        if (Moved(n.EclipseCoverage, a.EclipseCoverage)) return "eclipse_coverage";
        if (Moved(n.EclipseTransmittance, a.EclipseTransmittance)) return "eclipse_transmittance";
        if (Moved(n.HubLevel, a.HubLevel)) return "hub_level";
        if (Moved(n.StarVisibility, a.StarVisibility)) return "stars";
        if (Moved(n.HazeStorm.X, a.HazeStorm.X) || Moved(n.HazeStorm.Y, a.HazeStorm.Y)
            || Moved(n.HazeStorm.Z, a.HazeStorm.Z) || Moved(n.HazeStorm.W, a.HazeStorm.W)) return "haze_storm";
        if (Moved(n.StormLightStrength, a.StormLightStrength)) return "storm_light";
        if (Moved(n.StormLightMega.X, a.StormLightMega.X) || Moved(n.StormLightMega.Y, a.StormLightMega.Y)) return "storm_mega";
        if (n.StormLightMega.Z != a.StormLightMega.Z || n.StormLightMega.Z > 0.0f) return "flash";
        if (ColorChange(n.SkyTop, a.SkyTop) > ColorRelative) return "sky_top";
        if (ColorChange(n.SkyHorizon, a.SkyHorizon) > ColorRelative) return "sky_horizon";
        if (ColorChange(n.SunColor, a.SunColor) > ColorRelative) return "sun_color";
        if (ColorChange(n.HubColor, a.HubColor) > ColorRelative) return "hub_color";
        if (System.Math.Abs(Mathf.RadToDeg(WrapRadians(n.SpinRadians - a.SpinRadians))) > SpinThresholdDegrees) return "spin";
        if (nowSeconds - _lastRefreshSeconds >= MaxAgeSeconds) return "age";
        return "";
    }

    private static bool Moved(double a, double b) => System.Math.Abs(a - b) > AbsoluteThreshold;

    private static double WrapRadians(double r)
    {
        r %= System.Math.Tau;
        if (r > System.Math.PI) r -= System.Math.Tau;
        else if (r < -System.Math.PI) r += System.Math.Tau;
        return r;
    }

    // atan2(|a x b|, a . b) stays exact at the 0.02 degree scale, where acos(dot) would not.
    public static double AngleDegrees(Vector3 a, Vector3 b)
    {
        var an = a.Normalized();
        var bn = b.Normalized();
        return Mathf.RadToDeg(System.Math.Atan2(an.Cross(bn).Length(), an.Dot(bn)));
    }

    private static double Luminance(Vector3 v) => 0.2126 * v.X + 0.7152 * v.Y + 0.0722 * v.Z;

    private static double LuminanceChange(Vector3 a, Vector3 b)
    {
        double lb = Luminance(b);
        return System.Math.Abs(Luminance(a) - lb) / System.Math.Max(System.Math.Abs(lb), 1e-5);
    }

    private static double ColorChange(Vector3 a, Vector3 b) => (a - b).Length() / System.Math.Max(b.Length(), 1e-4);

    // ---- GDScript adapter. Packed layout (doubles):
    // 0-2 sun, 3-5 hub, 6-8 giant, 9-11 up, 12 height, 13 across, 14-16 camera position, 17-19 haze sun, 20-22 haze ambient day,
    // 23-25 haze ambient night, 26 atmosphere factor, 27 ring interior, 28 eclipse coverage, 29 eclipse transmittance, 30 hub level,
    // 31 star visibility, 32-35 haze storm, 36 storm light strength, 37-39 storm light mega (x, y, flash), 40-42 sky top, 43-45 sky horizon,
    // 46-48 sun colour, 49-51 hub colour, 52 spin radians.
    public const int PackedSize = 53;

    private static Vector3 V(double[] p, int i) => new((float)p[i], (float)p[i + 1], (float)p[i + 2]);

    public static Inputs Unpack(double[] p, long revision, long stormRevision, bool lutValid) => new()
    {
        Sun = V(p, 0), Hub = V(p, 3), Giant = V(p, 6), Up = V(p, 9),
        Height = p[12], Across = p[13], CameraPosition = V(p, 14),
        HazeSun = V(p, 17), HazeAmbientDay = V(p, 20), HazeAmbientNight = V(p, 23),
        AtmosphereFactor = p[26], RingInteriorFactor = p[27], EclipseCoverage = p[28], EclipseTransmittance = p[29],
        HubLevel = p[30], StarVisibility = p[31],
        HazeStorm = new Vector4((float)p[32], (float)p[33], (float)p[34], (float)p[35]),
        StormLightStrength = p[36], StormLightMega = V(p, 37),
        SkyTop = V(p, 40), SkyHorizon = V(p, 43), SunColor = V(p, 46), HubColor = V(p, 49),
        SpinRadians = p[52], Revision = (ulong)revision, StormRevision = (ulong)stormRevision, LutValid = lutValid,
    };

    /// <summary>True when the radiance map should refresh this frame. externalRefresh: a static sky material write already dirtied the map this frame.</summary>
    public bool Update(double[] packed, long revision, long stormRevision, bool lutValid, double nowSeconds, bool externalRefresh)
    {
        if (packed == null || packed.Length < PackedSize) return true;
        return Step(Unpack(packed, revision, stormRevision, lutValid), nowSeconds, externalRefresh);
    }

    public string GetLastReason() => LastReason;
}
