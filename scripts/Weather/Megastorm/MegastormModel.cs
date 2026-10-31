using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RingworldTechDemo;

/// <summary>
/// Pure megastorm model (docs/megastorm-design.md sections 2, 4, 6.1, WO1): no nodes, no rendering, no global state. Every output is a
/// deterministic function of (state, settings snapshot, weather-clock time). The runtime node (WO2) owns an instance and the weather
/// injection (WO3) turns <see cref="MegastormModel.BuildBundle"/> records into weather systems; record kinds and profiles are therefore
/// plain integers here, bound to the real enums by the constants below (kind 6 = WeatherSystemKind.Megastorm, profile 12 = CloudFormationProfile.MegastormWall; -1 = the table row the weather injection picks).
/// </summary>
public enum MegastormPhase { Inactive, Forming, Clear, Precursor, Anvil, Approach, GustFront, Wall, Transition, Trailing, Aftermath }

/// <summary>Role of one bundle record (section 3.2): W wall, A leading anvil, T trailing stratiform, P precursor.</summary>
public enum MegastormRecordRole { Wall, Anvil, Trailing, Precursor }

[Flags]
public enum MegastormRecordFlags { None = 0, Sculpt = 1, LowersBase = 2, Front = 4 }

/// <summary>The tunables the model reads, captured at trigger time (the settings snapshot saved with the state).</summary>
public sealed class MegastormSettings
{
    public const float MinimumSpeed = 5.0f, MaximumSpeed = 500.0f;
    public float SpeedMetersPerSecond { get; init; } = 350.0f;
    /// <summary>Trailing length L: the pass is 50 km + L (390 km by default).</summary>
    public double TrailingLengthMeters { get; init; } = 340000.0;
    /// <summary>"Warning reach" R (default 220 km): scales the precursor extent.</summary>
    public double WarningReachMeters { get; init; } = 220000.0;
    /// <summary>Gust front lead (seconds): drives the kind-6 time-based arrival bands.</summary>
    public double GustFrontLeadSeconds { get; init; } = 60.0;
    public double CircumferenceMeters { get; init; } = 31415936.0;
    public double WidthMeters { get; init; } = 318000.0;
    public double BowMeters { get; init; } = 8000.0;
    public double MeanderAmplitudeMeters { get; init; } = 3000.0;
    public double MeanderWavelengthMeters { get; init; } = 40000.0;
    public double MeanderPhaseRadiansPerHour { get; init; } = 1.0;
}

/// <summary>One speed anchor: from <see cref="TimeSeconds"/> the reference line is at <see cref="AroundMeters"/> moving at <see cref="Speed"/>.</summary>
public sealed class MegastormAnchor
{
    public double TimeSeconds { get; set; }
    public double AroundMeters { get; set; }
    public float Speed { get; set; }
}

/// <summary>Persistent state (section 6.2 document body): trigger, anchors, stop, seed and settings snapshot.</summary>
public sealed class MegastormState
{
    public string? TriggerId { get; set; }
    public double TriggerTimeSeconds { get; set; }
    public int Direction { get; set; } = 1;
    public int Seed { get; set; }
    public double FormationSeconds { get; set; } = 1800.0;
    public List<MegastormAnchor> Anchors { get; set; } = new();
    public double StopTimeSeconds { get; set; } = double.NaN;
    public double StopFadeSeconds { get; set; }
    public MegastormSettings Settings { get; set; } = new();
}

public readonly record struct MegastormTrigger(string TriggerId, double LeadingAroundMeters, int Direction,
    double StartTimeSeconds, float SpeedMetersPerSecond = float.NaN /* NaN = setting */, float FormationSeconds = 1800f, int Seed = 0);

/// <summary>The moving frame at one time. <see cref="Level"/> is formation x stop fade (0..1); <see cref="Strength"/> adds the 6-12 h breathing (0.8-1.0).</summary>
public readonly record struct MegastormFrame(bool Active, double AroundMeters, float SpeedMetersPerSecond, int Direction,
    float Formation, float Level, float Strength, float TopMeters, double AgeSeconds);

/// <summary>Point query result (the part of the section 6.1 sample that the pure model owns; audio and hazards come with WO2/WO10).</summary>
public readonly record struct MegastormPointSample(bool Active, MegastormPhase Phase, double XiMeters, double DistanceToEdgeMeters /* > 0 ahead */,
    double TimeToArrivalSeconds, double TimeToClearSeconds, double TimeSincePassageSeconds, float Danger, float Suppression);

/// <summary>One weather-system record of the megastorm bundle (section 3.2). Slab encoding: orientation pi/2, across semi-axis 1e7 m.</summary>
public readonly record struct MegastormRecord(int Id, MegastormRecordRole Role, int Kind, int Profile /* -1 = table row chosen by WO3 */,
    MegastormRecordFlags Flags, double CenterAroundMeters, double CenterAcrossMeters, double SemiAcrossMeters, double SemiAlongMeters,
    double CenterXiMeters, float Density, float Precipitation, float DriftMetersPerSecond /* signed: dir * v around */);

/// <summary>
/// Column of the moving-frame strip (section 3.2): heights above the ring floor plus aux channels, all 0 when absent. BodyCoverage is the wall
/// body's footprint (feathered front edge, transition fade) x strength: the strip stores it so the march can collapse the body interval at
/// its edges and erode by horizontal depth (WO4).
/// </summary>
public readonly record struct MegastormColumn(float BodyBase, float BodyTop, float CanopyUnderside, float CanopyTop,
    float CanopyCoverage, float Mammatus, float TowerCoverage, float RainShaft, float BodyCoverage = 0.0f);

/// <summary>Kind-6 time-based arrival bands (section 5.1): at 350 m/s the distance caps of ordinary storms would be 17 s / 51 s.</summary>
public static class MegastormBands
{
    public const double BandSpeedFactor = 0.75, ReachSpeedFactor = 2.5;
    public const double BandMinMeters = 2000.0, BandMaxMeters = 20000.0, ReachMinMeters = 10000.0, ReachMaxMeters = 60000.0;
    public static double BandWidthMeters(double speed, double leadSeconds)
        => Math.Clamp(speed * BandSpeedFactor * leadSeconds, BandMinMeters, BandMaxMeters);
    public static double ApproachReachMeters(double speed, double leadSeconds)
        => Math.Clamp(speed * ReachSpeedFactor * leadSeconds, ReachMinMeters, ReachMaxMeters);
}

/// <summary>Pure megastorm model: anchors, s(t), ring wrap, xi, formation, stop, zones, suppression, bundle, danger.</summary>
public sealed class MegastormModel : IMegastormWeatherSource
{
    public const int WallRecordId = -30001, AnvilRecordId = -30002, TrailingRecordId = -30003, PrecursorRecordId = -30004;
    public const int KindMegastorm = (int)RingVolumetricCloudLayer.WeatherSystemKind.Megastorm, KindExceptionalFront = (int)RingVolumetricCloudLayer.WeatherSystemKind.ExceptionalFront,
        KindCloudBank = (int)RingVolumetricCloudLayer.WeatherSystemKind.CloudBank, ProfileMegastormWall = (int)CloudFormationProfile.MegastormWall, ProfileTableRow = -1;
    public const int MaxAnchors = 16;
    /// <summary>Across semi-axis of the slab ellipse: q then reduces to (around offset / half depth) with only metres of edge error.</summary>
    public const double SlabSemiAcrossMeters = 1.0e7;
    public const float FullTopMeters = 16500.0f, FormingTopMeters = 6000.0f, CeilingMeters = RingCloudLayers.CeilingMeters;

    // Zone edges (section 2.1), xi in metres.
    public const double AnvilStartXi = -60000.0, ShelfStartXi = -4000.0, ShelfEndXi = 1000.0, WallEndXi = 25000.0, TransitionEndXi = 50000.0;
    public const double AftermathLengthMeters = 600000.0, SuppressionLeadStartXi = -150000.0, SuppressionFullXi = -70000.0;
    public const double PrecursorBaseWarningReachMeters = 220000.0;

    public MegastormState State { get; }
    public MegastormSettings Settings => State.Settings;

    public MegastormModel(MegastormSettings? settings = null) { State = new MegastormState { Settings = settings ?? new MegastormSettings() }; }
    public MegastormModel(MegastormState state) { State = state; }

    public bool Triggered => State.TriggerId != null;

    // ---- JSON (save document body) --------------------------------------------------------------------------------------
    private static readonly JsonSerializerOptions JsonOptions = new() { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    public string ToJson() => JsonSerializer.Serialize(State, JsonOptions);
    public static MegastormModel FromJson(string json)
        => new(JsonSerializer.Deserialize<MegastormState>(json, JsonOptions) ?? throw new JsonException("empty megastorm state"));

    // ---- Lifecycle ------------------------------------------------------------------------------------------------------
    /// <summary>Idempotent per TriggerId; false if one is already triggered.</summary>
    public bool Trigger(in MegastormTrigger t)
    {
        if (Triggered) return false;
        float speed = float.IsNaN(t.SpeedMetersPerSecond) ? Settings.SpeedMetersPerSecond : t.SpeedMetersPerSecond;
        speed = Math.Clamp(speed, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed);
        State.TriggerId = t.TriggerId;
        State.TriggerTimeSeconds = t.StartTimeSeconds;
        State.Direction = t.Direction < 0 ? -1 : 1;
        State.Seed = t.Seed;
        State.FormationSeconds = Math.Max(0.0, t.FormationSeconds);
        State.StopTimeSeconds = double.NaN;
        State.StopFadeSeconds = 0.0;
        State.Anchors.Clear();
        State.Anchors.Add(new MegastormAnchor { TimeSeconds = t.StartTimeSeconds, AroundMeters = WrapPositive(t.LeadingAroundMeters, Settings.CircumferenceMeters), Speed = speed });
        return true;
    }

    /// <summary>
    /// Triggers so the actual edge (xi = 0, including bow and meander) reaches <paramref name="targetAround"/> at <paramref name="targetAcross"/>
    /// at nowSeconds + arrivalSeconds. Refuses (unless forced) when arrivalSeconds &lt; Formation + (reach + 40 km) / v (2,543 s by default).
    /// </summary>
    public bool TriggerToReach(double targetAround, double targetAcross, double arrivalSeconds, int direction, string triggerId,
        double nowSeconds, float speed = float.NaN, int seed = 0, bool force = false)
    {
        if (Triggered) return false;
        float v = float.IsNaN(speed) ? Settings.SpeedMetersPerSecond : Math.Clamp(speed, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed);
        int dir = direction < 0 ? -1 : 1;
        const float formation = 1800f;
        if (!force && arrivalSeconds < formation + (Settings.WarningReachMeters + 40000.0) / v) return false;
        double sAtArrival = targetAround + dir * LeadOffset(Settings, seed, targetAcross, nowSeconds + arrivalSeconds);
        double s0 = sAtArrival - dir * v * arrivalSeconds;
        return Trigger(new MegastormTrigger(triggerId, s0, dir, nowSeconds, v, formation, seed));
    }

    /// <summary>Re-anchors at <paramref name="nowSeconds"/> with a new speed; the position is continuous.</summary>
    public bool SetSpeed(double nowSeconds, float metersPerSecond)
    {
        if (!Triggered) return false;
        MegastormAnchor last = State.Anchors[^1];
        if (nowSeconds < last.TimeSeconds) return false;
        float v = Math.Clamp(metersPerSecond, MegastormSettings.MinimumSpeed, MegastormSettings.MaximumSpeed);
        double s = AroundAt(nowSeconds);
        if (nowSeconds == last.TimeSeconds) { last.AroundMeters = s; last.Speed = v; }
        else
        {
            State.Anchors.Add(new MegastormAnchor { TimeSeconds = nowSeconds, AroundMeters = s, Speed = v });
            if (State.Anchors.Count > MaxAnchors) State.Anchors.RemoveRange(0, State.Anchors.Count - MaxAnchors);
        }
        return true;
    }

    /// <summary>Densities fade and tops sink to 6 km over <paramref name="fadeSeconds"/> from <paramref name="atTimeSeconds"/>, then it is inactive.</summary>
    public bool Stop(double atTimeSeconds, float fadeSeconds)
    {
        if (!Triggered || atTimeSeconds < State.TriggerTimeSeconds) return false;
        State.StopTimeSeconds = atTimeSeconds;
        State.StopFadeSeconds = Math.Max(0.0, fadeSeconds);
        return true;
    }

    // ---- Position -------------------------------------------------------------------------------------------------------
    private MegastormAnchor AnchorAt(double t)
    {
        List<MegastormAnchor> a = State.Anchors;
        int k = 0;
        for (int i = a.Count - 1; i >= 0; i--) if (a[i].TimeSeconds <= t) { k = i; break; }
        return a[k];
    }

    /// <summary>s(t): around coordinate of the reference leading line, in [0, C).</summary>
    public double AroundAt(double t)
    {
        if (!Triggered) return 0.0;
        MegastormAnchor k = AnchorAt(t);
        return WrapPositive(k.AroundMeters + State.Direction * (double)k.Speed * (t - k.TimeSeconds), Settings.CircumferenceMeters);
    }

    public float SpeedAt(double t) => Triggered ? AnchorAt(t).Speed : Settings.SpeedMetersPerSecond;

    public static double WrapPositive(double v, double c) { double r = v % c; return r < 0.0 ? r + c : r; }
    /// <summary>Wraps into (-c/2, c/2].</summary>
    public static double WrapSigned(double v, double c)
    {
        double r = WrapPositive(v, c);
        return r > 0.5 * c ? r - c : r;
    }

    /// <summary>x_lead(across, t) = -Bow (1 - u^2) - Meander: the edge offset from the reference line (negative = ahead).</summary>
    public double LeadOffset(double across, double t) => LeadOffset(Settings, State.Seed, across, t);

    private static double LeadOffset(MegastormSettings s, int seed, double across, double t)
    {
        double u = 2.0 * across / s.WidthMeters - 1.0;
        double phase = MeanderPhase(s, seed, t);
        double meander = s.MeanderAmplitudeMeters * Math.Sin(2.0 * Math.PI * across / s.MeanderWavelengthMeters + phase);
        return -s.BowMeters * (1.0 - u * u) - meander;
    }

    private static double MeanderPhase(MegastormSettings s, int seed, double t)
        => 2.0 * Math.PI * Hash01(seed, 7, 0) + s.MeanderPhaseRadiansPerHour * t / 3600.0;

    /// <summary>The meander phase at t wrapped into [0, 2 pi): what the GPU include (ring_megastorm_lead_offset, p.c.x) receives as a float.</summary>
    public double MeanderPhaseWrapped(double t) => WrapPositive(MeanderPhase(Settings, State.Seed, t), 2.0 * Math.PI);

    /// <summary>
    /// The reference line's unwrapped around position at t: the first retained anchor plus every anchor's travel (no ring wrap), so it is continuous
    /// in time across the 0 / C seam. The renderer derives the wall's per-frame shift and the moving-frame noise offsets from it (WO4).
    /// </summary>
    public double UnwrappedAroundAt(double t)
    {
        List<MegastormAnchor> a = State.Anchors;
        if (!Triggered || a.Count == 0) return 0.0;
        int k = 0;
        for (int i = a.Count - 1; i >= 0; i--) if (a[i].TimeSeconds <= t) { k = i; break; }
        double s = a[0].AroundMeters;
        for (int i = 0; i < k; i++) s += State.Direction * (double)a[i].Speed * (a[i + 1].TimeSeconds - a[i].TimeSeconds);
        return s + State.Direction * (double)a[k].Speed * (t - a[k].TimeSeconds);
    }

    /// <summary>x(p, t) = dir * wrap(s - around): negative ahead of the reference line, positive behind it.</summary>
    public double X(double around, double t) => State.Direction * WrapSigned(AroundAt(t) - around, Settings.CircumferenceMeters);

    /// <summary>xi: distance behind the actual (bowed, meandering) edge.</summary>
    public double Xi(double around, double across, double t) => X(around, t) - LeadOffset(across, t);

    // ---- Frame ----------------------------------------------------------------------------------------------------------
    public MegastormFrame Evaluate(double t)
    {
        if (!Triggered || t < State.TriggerTimeSeconds) return default;
        double age = t - State.TriggerTimeSeconds;
        double stopFade = 1.0;
        if (!double.IsNaN(State.StopTimeSeconds))
        {
            double fs = State.StopFadeSeconds;
            stopFade = t < State.StopTimeSeconds ? 1.0 : fs <= 0.0 ? 0.0 : 1.0 - Smooth((t - State.StopTimeSeconds) / fs);
            if (stopFade <= 0.0) return default;
        }
        double form = State.FormationSeconds <= 0.0 ? 1.0 : Smooth(age / State.FormationSeconds);
        double level = form * stopFade;
        double period = 21600.0 + 21600.0 * Hash01(State.Seed, 1, 0);
        double breathing = 0.9 + 0.1 * Math.Sin(2.0 * Math.PI * t / period + 2.0 * Math.PI * Hash01(State.Seed, 2, 0));
        float top = (float)(FormingTopMeters + (FullTopMeters - FormingTopMeters) * level);
        return new MegastormFrame(true, AroundAt(t), SpeedAt(t), State.Direction, (float)form, (float)level, (float)(level * breathing), top, age);
    }

    // ---- Zones, suppression, danger -------------------------------------------------------------------------------------
    public double TrailingEndXi => TransitionEndXi + Settings.TrailingLengthMeters;
    public double AftermathEndXi => TrailingEndXi + AftermathLengthMeters;
    private double PrecursorStartXi => -(Settings.WarningReachMeters + 40000.0);

    /// <summary>Natural-weather suppression weight S(xi): 0 beyond 150 km ahead, 1 by -70 km, 1 through the pass, 0 again by the end of the aftermath.</summary>
    public float Suppression(double xi)
    {
        double ramp = Smooth((xi - SuppressionLeadStartXi) / (SuppressionFullXi - SuppressionLeadStartXi));
        double decay = 1.0 - Smooth((xi - TrailingEndXi) / AftermathLengthMeters);
        return (float)(xi < 0.0 ? ramp : decay);
    }

    /// <summary>Suppression at a world position; exactly 0 (identity) when the megastorm is not active.</summary>
    public float SuppressionAt(double around, double across, double t)
        => Evaluate(t).Active ? Suppression(Xi(around, across, t)) : 0.0f;

    /// <summary>Observer phase from xi. Forming covers everything ahead of the edge while the formation ramp is still running.</summary>
    public MegastormPhase PhaseAt(double xi, in MegastormFrame frame)
    {
        if (!frame.Active) return MegastormPhase.Inactive;
        double v = frame.SpeedMetersPerSecond, lead = Settings.GustFrontLeadSeconds;
        double band = MegastormBands.BandWidthMeters(v, lead), reach = MegastormBands.ApproachReachMeters(v, lead);
        if (xi < 0.0 && frame.Formation < 1.0) return MegastormPhase.Forming;
        if (xi < -(band + reach))
            return xi < PrecursorStartXi ? MegastormPhase.Clear : xi < AnvilStartXi ? MegastormPhase.Precursor : MegastormPhase.Anvil;
        if (xi < -band) return MegastormPhase.Approach;
        if (xi < 0.0) return MegastormPhase.GustFront;
        if (xi < WallEndXi) return MegastormPhase.Wall;
        if (xi < TransitionEndXi) return MegastormPhase.Transition;
        if (xi < TrailingEndXi) return MegastormPhase.Trailing;
        return xi < AftermathEndXi ? MegastormPhase.Aftermath : MegastormPhase.Clear;
    }

    /// <summary>Danger 0..1 (documented, unused now): max of wall, gust front, transition, trailing, precursor and wake-pressure terms, each x (0.5 + 0.5 x shelterless).</summary>
    public float Danger(double xi, double timeToArrivalSeconds, double speed, float shelterless = 1.0f)
    {
        double band = MegastormBands.BandWidthMeters(speed, Settings.GustFrontLeadSeconds);
        double wall = Smooth((xi + 2000.0) / 4000.0) * (1.0 - Smooth((xi - WallEndXi) / (TransitionEndXi - WallEndXi)));
        double gust = 0.75 * Smooth((xi + band) / band) * (1.0 - Smooth(xi / (0.35 * band)));
        double transition = xi >= WallEndXi && xi < TransitionEndXi ? 0.5 : 0.0;
        double trailing = xi >= TransitionEndXi && xi < TrailingEndXi ? 0.25 : 0.0;
        double precursor = xi < 0.0 ? 0.15 * Smooth((600.0 - timeToArrivalSeconds) / 600.0) : 0.0;
        double wake = MegastormHazardMath.WakeDanger(MegastormHazardMath.PressureDeficitKPa(xi));   // 5.5.3: 0.6 x smoothstep(2, 6, deficit)
        double d = Math.Max(Math.Max(Math.Max(wall, gust), Math.Max(Math.Max(transition, trailing), precursor)), wake);
        return (float)(d * (0.5 + 0.5 * Math.Clamp(shelterless, 0.0f, 1.0f)));
    }

    /// <summary>
    /// Signed time offset dt from t at which xi(around, across, t + dt) reaches <paramref name="targetXi"/>. The edge meanders and the front speed can change,
    /// so -xi/v is only the first guess; a bracket-free secant refinement (deterministic, 6 iterations at most) lands on the true crossing.
    /// </summary>
    private double SolveXiCrossing(double around, double across, double t, double xi0, double targetXi, double v)
    {
        double a = 0.0, fa = xi0 - targetXi;
        double b = -fa / v, fb = Xi(around, across, t + b) - targetXi;
        for (int i = 0; i < 6 && Math.Abs(fb) > 0.01; i++)
        {
            double den = fb - fa;
            double next = Math.Abs(den) < 1e-9 ? b - fb / v : b - fb * (b - a) / den;
            a = b; fa = fb; b = next; fb = Xi(around, across, t + b) - targetXi;
        }
        return b;
    }

    public MegastormPointSample Query(double around, double across, double t, float shelterless = 1.0f)
    {
        MegastormFrame f = Evaluate(t);
        if (!f.Active) return new MegastormPointSample(false, MegastormPhase.Inactive, 0.0, 0.0, double.PositiveInfinity, double.PositiveInfinity, 0.0, 0.0f, 0.0f);
        double xi = Xi(around, across, t);
        double v = f.SpeedMetersPerSecond, c = Settings.CircumferenceMeters;
        double tta = xi < 0.0 ? SolveXiCrossing(around, across, t, xi, 0.0, v) : (c - xi) / v;
        double ttc = xi < TrailingEndXi ? SolveXiCrossing(around, across, t, xi, TrailingEndXi, v) : 0.0;
        double since = xi > 0.0 ? -SolveXiCrossing(around, across, t, xi, 0.0, v) : 0.0;
        return new MegastormPointSample(true, PhaseAt(xi, f), xi, -xi, tta, ttc, since, Danger(xi, tta, v, shelterless), Suppression(xi));
    }

    // ---- Bundle ---------------------------------------------------------------------------------------------------------
    /// <summary>
    /// Fills <paramref name="list"/> (cleared first) with the four bundle records at time t, with their across centre at
    /// <paramref name="queryAcross"/> (section 3.2). Returns the count (0 when inactive).
    /// </summary>
    public int BuildBundle(double t, double queryAcross, List<MegastormRecord> list)
    {
        list.Clear();
        MegastormFrame f = Evaluate(t);
        if (!f.Active) return 0;
        double v = f.SpeedMetersPerSecond, c = Settings.CircumferenceMeters;
        double drift = State.Direction * v;
        double lead = LeadOffset(queryAcross, t);
        double reachScale = Settings.WarningReachMeters / PrecursorBaseWarningReachMeters;
        float strength = f.Strength, level = f.Level;
        double halfT = 0.5 * Settings.TrailingLengthMeters;
        MegastormRecord Make(int id, MegastormRecordRole role, int kind, int profile, MegastormRecordFlags flags, double xiCenter, double half, float dens, float prec)
            => new(id, role, kind, profile, flags, WrapPositive(f.AroundMeters - State.Direction * (xiCenter + lead), c), queryAcross,
                SlabSemiAcrossMeters, half, xiCenter, dens * strength, prec * level, (float)drift);
        list.Add(Make(WallRecordId, MegastormRecordRole.Wall, KindMegastorm, ProfileMegastormWall,
            MegastormRecordFlags.Sculpt | MegastormRecordFlags.LowersBase, 18000.0, 24000.0, 1.0f, 1.0f));
        list.Add(Make(AnvilRecordId, MegastormRecordRole.Anvil, KindMegastorm, ProfileMegastormWall,
            MegastormRecordFlags.Sculpt, -25000.0, 30000.0, 0.5f, 0.0f));
        list.Add(Make(TrailingRecordId, MegastormRecordRole.Trailing, KindExceptionalFront, ProfileTableRow,
            MegastormRecordFlags.Front | MegastormRecordFlags.LowersBase, TransitionEndXi + halfT, halfT / 0.7, 1.0f, 1.0f));
        list.Add(Make(PrecursorRecordId, MegastormRecordRole.Precursor, KindCloudBank, ProfileTableRow,
            MegastormRecordFlags.None, -150000.0 * reachScale, 110000.0 * reachScale, 0.7f, 0.0f));
        return list.Count;
    }

    /// <summary>Unit-ellipse norm q of a slab record at a world position (the GPU's ring_ws_ellipse_normalize for orientation pi/2).</summary>
    public double SlabNorm(in MegastormRecord r, double around, double across)
    {
        double da = WrapSigned(around - r.CenterAroundMeters, Settings.CircumferenceMeters);
        double a = (across - r.CenterAcrossMeters) / r.SemiAcrossMeters, b = da / r.SemiAlongMeters;
        return Math.Sqrt(a * a + b * b);
    }

    // ---- Helpers --------------------------------------------------------------------------------------------------------
    internal static double Smooth(double x) { x = Math.Clamp(x, 0.0, 1.0); return x * x * (3.0 - 2.0 * x); }

    internal static uint Hash(int a, int b, long c)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA6Bu ^ (uint)c * 0xC2B2AE35u ^ (uint)(c >> 32) * 0x27D4EB2Fu;
            h ^= h >> 16; h *= 0x7FEB352Du; h ^= h >> 15; h *= 0x846CA68Bu; h ^= h >> 16;
            return h;
        }
    }
    internal static double Hash01(int a, int b, long c) => (Hash(a, b, c) >> 8) / 16777216.0;
}

/// <summary>C# twin of the shared strip-column include (materials/ring_megastorm_profile.gdshaderinc, WO4): same maths, evaluated in the moving frame.</summary>
public static class MegastormProfile
{
    /// <summary>The include's strip extent along x (reference-line distance): the W/A support (xi -68 .. +53 km) plus the bow and meander.</summary>
    public const double StripMinX = -72000.0, StripMaxX = 56000.0;
    /// <summary>Optional ceiling at an across position (the field doc's single ceiling function, plugged in by WO8); default is the 18 km hard cap.</summary>
    public delegate float CeilingFunction(double acrossMeters);

    private const double TowerScale = 11500.0, ShaftScale = 4000.0, MammatusScale = 2000.0;

    /// <summary>Column at reference-line distance <paramref name="x"/> (positive behind the line) and <paramref name="across"/>.</summary>
    public static MegastormColumn Column(MegastormModel model, double x, double across, double t, CeilingFunction? ceiling = null)
    {
        MegastormFrame f = model.Evaluate(t);
        if (!f.Active) return default;
        MegastormSettings s = model.Settings;
        int seed = model.State.Seed;
        double xi = x - model.LeadOffset(across, t);
        float ceil = Math.Min(MegastormModel.CeilingMeters, ceiling?.Invoke(across) ?? MegastormModel.CeilingMeters);
        double wallEnd = MegastormModel.WallEndXi, transEnd = MegastormModel.TransitionEndXi;

        // Wall body: feathered front edge, full to 25 km, fading out over the transition zone. Towers merge into a buttress near the rims.
        double wallCov = MegastormModel.Smooth((xi + 300.0) / 800.0) * (1.0 - MegastormModel.Smooth((xi - wallEnd) / (transEnd - wallEnd)));
        double rim = Math.Min(across, s.WidthMeters - across);
        double merge = MegastormModel.Smooth((15000.0 - rim) / 15000.0);
        double tower = Math.Max(TowerNoise(seed, across, t), merge);
        double dome = tower > 0.8 ? (tower - 0.8) * 5.0 * 1500.0 * (1.0 - merge) : 0.0;
        double wallTop = 15000.0 + 2000.0 * tower + dome;
        double transition = MegastormModel.Smooth((xi - wallEnd) / (transEnd - wallEnd));
        wallTop += (8500.0 - wallTop) * transition;
        wallTop = MegastormModel.FormingTopMeters + (wallTop - MegastormModel.FormingTopMeters) * f.Level;
        double baseH = xi < MegastormModel.ShelfEndXi ? 300.0 : 300.0 + 200.0 * MegastormModel.Smooth((xi - MegastormModel.ShelfEndXi) / 2000.0);
        float bodyTop = (float)Math.Min(wallTop, ceil);

        // Leading anvil canopy: underside 9 km rising to 12 km ahead, top 16.5 km (formation-scaled), fading behind the wall.
        double canopyCov = MegastormModel.Smooth((xi - MegastormModel.AnvilStartXi) / 10000.0) * (1.0 - MegastormModel.Smooth((xi - wallEnd) / 20000.0));
        float under = (float)(9000.0 + 3000.0 * MegastormModel.Smooth(xi / MegastormModel.AnvilStartXi));
        float canopyTop = (float)Math.Min(MegastormModel.FormingTopMeters + (MegastormModel.FullTopMeters - MegastormModel.FormingTopMeters) * f.Level, ceil);
        double mamm = MegastormModel.Smooth((xi + 25000.0) / 5000.0) * (1.0 - MegastormModel.Smooth((xi + 8000.0) / 5000.0))
            * (0.5 + 0.5 * MammatusNoise(seed, across, t));
        double shaft = wallCov * (0.6 + 0.4 * ShaftNoise(seed, across, t));

        float k = f.Strength;
        bool body = wallCov > 0.0, canopy = canopyCov > 0.0;
        return new MegastormColumn(body ? (float)baseH : 0.0f, body ? bodyTop : 0.0f, canopy ? Math.Min(under, canopyTop) : 0.0f, canopy ? canopyTop : 0.0f,
            (float)(canopyCov * k), (float)(mamm * k), (float)(wallCov * tower * k), (float)(shaft * k), (float)(wallCov * k));
    }

    /// <summary>
    /// The across-only noise terms of the column (ring_megastorm_across_noise / ring_megastorm_shaft_noise in the include): raw tower, mammatus and
    /// rain-shaft value noise 0..1 at <paramref name="across"/>, before the rim merge and the mammatus / shaft scaling. The far wedge pass (WO5) reads
    /// them from a 1D across table built from this.
    /// </summary>
    public static (double Tower, double Mammatus, double Shaft) AcrossTerms(MegastormModel model, double across, double t)
    {
        int seed = model.State.Seed;
        return (TowerNoise(seed, across, t), MammatusNoise(seed, across, t), ShaftNoise(seed, across, t));
    }

    private static double TowerNoise(int seed, double across, double t) => Noise(seed, 11, across / TowerScale, t, 1200.0, 1200.0);
    private static double MammatusNoise(int seed, double across, double t) => Noise(seed, 13, across / MammatusScale, t, 600.0, 600.0);
    private static double ShaftNoise(int seed, double across, double t) => Noise(seed, 12, across / ShaftScale, t, 900.0, 900.0);

    /// <summary>1D value noise whose nodes re-seed on a hashed cycle (cMin + cSpan * h seconds) with a crossfade over the last 20%.</summary>
    private static double Noise(int seed, int salt, double coord, double t, double cMin, double cSpan)
    {
        double fl = Math.Floor(coord);
        long i = (long)fl;
        double f = MegastormModel.Smooth(coord - fl);
        return Cell(seed, salt, i, t, cMin, cSpan) * (1.0 - f) + Cell(seed, salt, i + 1, t, cMin, cSpan) * f;
    }

    private static double Cell(int seed, int salt, long i, double t, double cMin, double cSpan)
    {
        double cycle = cMin + cSpan * MegastormModel.Hash01(seed, salt, i);
        double u = (t + cycle * MegastormModel.Hash01(seed, salt + 100, i)) / cycle;
        double e = Math.Floor(u);
        double fade = MegastormModel.Smooth((u - e - 0.8) / 0.2);
        long ek = (long)e;
        int h = unchecked((int)(i * 31 + salt));
        return MegastormModel.Hash01(seed, h, ek) * (1.0 - fade) + MegastormModel.Hash01(seed, h, ek + 1) * fade;
    }
}
