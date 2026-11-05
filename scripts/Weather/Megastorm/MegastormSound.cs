using System;

namespace RingworldTechDemo;

/// <summary>Airborne sound regime at the observer (docs/megastorm-design.md 5.2): ahead of the edge Silent or Leading, in the boom window Boom, then Inside.</summary>
public enum AirSoundState { Silent, Leading, Boom, Inside }

/// <summary>
/// <see cref="AirGain"/> is the gain of the airborne beds (roar, distant-storm sectors, far thunder): ahead of the edge it is the lead weight
/// (1 below M 0.94, 0 from M 1.0), behind it is 1 except during the boom window, where the pile-up replaces the roar until the last
/// <see cref="MegastormSound.RoarCrossfadeSeconds"/>. <see cref="SwellAllowed"/> is true while the sound outruns the wall inside the gust outflow
/// (v &lt; 343 + outflow); faster than that the swell joins the boom (<see cref="BoomIncludesSwell"/>).
/// </summary>
public readonly record struct AirSoundResult(AirSoundState State, float AirGain, float BoomGain, bool SwellAllowed, bool BoomIncludesSwell);

/// <summary>Rim-wall singing level per wall: Low is the across = 0 wall, High the across = W wall. Retired as a sound (2026-11-04, user decision): nothing plays it, the maths stays.</summary>
public readonly record struct WallSingLevels(float Low, float High)
{
    public float Max => Math.Max(Low, High);
}

/// <summary>The audio outputs of one point query (docs/megastorm-design.md 5.2). All are pure functions of (speed, xi, settings): no stored state.</summary>
public readonly record struct MegastormSoundSample(float HumLevel, float WallSingLevel, AirSoundState AirSound, float AirGain, float BoomEnvelope,
    float GustSwell, float TremorLevel, float WildlifeQuiet)
{
    public static readonly MegastormSoundSample None = default;
}

/// <summary>
/// Pure megastorm sound maths (docs/megastorm-design.md 5.2, WO6b): the structure-borne hum thousands of km ahead, rim-wall singing, the
/// speed-aware airborne regime (the wall is Mach 1.02 at the default speed, so nothing airborne gets ahead of it), the edge boom envelope,
/// tremor and wildlife quiet. No nodes, no audio assets, no state: the audio voices (a later step) only read these values.
/// </summary>
public static class MegastormSound
{
    public const double SpeedOfSoundMetersPerSecond = 343.0;
    /// <summary>Forward gust-front outflow (28 x (1 + 0.3 x 1.5), 40.6 m/s): sound rides it at 384 m/s.</summary>
    public const double GustOutflowMetersPerSecond = 41.0;
    public const double BoomSeconds = 4.0, RoarCrossfadeSeconds = 1.5;
    /// <summary>Menu speed (as M) where the airborne lead starts to fade (below it: full lead, no boom) and where it is gone (from it: Silent, full boom).</summary>
    public const double LeadFullMach = 0.94, LeadNoneMach = 1.0;
    /// <summary>Hum onset at 10 R, stage 2 at 2 R, wildlife fully quiet at 1 R.</summary>
    public const double HumReachFactor = 10.0, StageTwoReachFactor = 2.0, WildlifeFullReachFactor = 1.0;
    public const double TremorHumScale = 0.6, WallSingRimMeters = 20000.0;
    /// <summary>Behind the edge the hum drops from full to half over this distance, then fades to 0 at the end of the trailing rain.</summary>
    public const double HumEdgeBlendMeters = 5000.0, HumEdgeFraction = 0.5;

    private static double Smooth(double x) => MegastormModel.Smooth(x);

    // ---- Hum ----------------------------------------------------------------------------------------------------------
    /// <summary>Hum ahead of the edge: smoothstep(10 R, 0, d)^2 for d &gt; 0 metres to the edge (0 at and beyond 10 R, 1 at the edge).</summary>
    public static float HumAhead(double distanceMeters, double reachMeters)
    {
        double span = HumReachFactor * reachMeters;
        double s = Smooth((span - Math.Max(0.0, distanceMeters)) / span);
        return (float)(s * s);
    }

    /// <summary>Hum at xi (negative = ahead): the build-up ahead, half at the edge (reached over 5 km), 0 by <paramref name="trailingEndXi"/>.</summary>
    public static float HumLevel(double xi, double reachMeters, double trailingEndXi)
    {
        if (xi < 0.0) return HumAhead(-xi, reachMeters);
        double edge = 1.0 - (1.0 - HumEdgeFraction) * Smooth(xi / HumEdgeBlendMeters);
        return (float)(edge * (1.0 - Smooth(xi / trailingEndXi)));
    }

    /// <summary>0 beyond 10 R, 1 inside it, 2 inside 2 R (distance to the edge ahead; behind the edge it stays 2 while the hum is audible).</summary>
    public static int HumStage(double xi, double reachMeters)
    {
        double d = -xi;
        if (d >= HumReachFactor * reachMeters) return 0;
        return d > StageTwoReachFactor * reachMeters ? 1 : 2;
    }

    /// <summary>
    /// The two directional wall-singing voices: the hum level scaled 0.5 .. 1 by each rim wall's proximity (full within 0 m, half beyond
    /// <see cref="WallSingRimMeters"/>), so the nearer wall is louder and it sings loudest within 20 km of it.
    /// </summary>
    public static WallSingLevels WallSing(double xi, double reachMeters, double trailingEndXi, double acrossMeters, double widthMeters)
    {
        float hum = HumLevel(xi, reachMeters, trailingEndXi);
        double low = Math.Max(0.0, acrossMeters), high = Math.Max(0.0, widthMeters - acrossMeters);
        float Voice(double rim) => (float)(hum * (0.5 + 0.5 * (1.0 - Smooth(rim / WallSingRimMeters))));
        return new WallSingLevels(Voice(low), Voice(high));
    }

    // ---- Airborne regime ------------------------------------------------------------------------------------------------
    public static double Mach(double speedMetersPerSecond) => speedMetersPerSecond / SpeedOfSoundMetersPerSecond;

    /// <summary>1 below M 0.94, 0 from M 1.0, linear between (about 0.049 per m/s), so crossing 343 m/s in the menu is never a hard flip.</summary>
    public static float LeadWeight(double speedMetersPerSecond)
        => (float)(1.0 - Math.Clamp((Mach(speedMetersPerSecond) - LeadFullMach) / (LeadNoneMach - LeadFullMach), 0.0, 1.0));

    /// <summary>Boom gain: smoothstep(0.94, 1.0, M).</summary>
    public static float BoomGain(double speedMetersPerSecond)
        => (float)Smooth((Mach(speedMetersPerSecond) - LeadFullMach) / (LeadNoneMach - LeadFullMach));

    public static AirSoundResult AirSound(double speedMetersPerSecond, double xi, double outflowMetersPerSecond = GustOutflowMetersPerSecond,
        double boomSeconds = BoomSeconds)
    {
        double mach = Mach(speedMetersPerSecond);
        float lead = LeadWeight(speedMetersPerSecond), boomGain = BoomGain(speedMetersPerSecond);
        bool swell = speedMetersPerSecond < SpeedOfSoundMetersPerSecond + outflowMetersPerSecond;
        bool includesSwell = !swell;
        if (xi < 0.0) return new AirSoundResult(lead >= 0.5f ? AirSoundState.Leading : AirSoundState.Silent, lead, boomGain, swell, includesSwell);
        double t = xi / speedMetersPerSecond;
        if (mach >= LeadFullMach && t < boomSeconds)
        {
            float roar = (float)Smooth((t - (boomSeconds - RoarCrossfadeSeconds)) / RoarCrossfadeSeconds);
            return new AirSoundResult(AirSoundState.Boom, 1.0f - boomGain * (1.0f - roar), boomGain, swell, includesSwell);
        }
        return new AirSoundResult(AirSoundState.Inside, 1.0f, boomGain, swell, includesSwell);
    }

    /// <summary>
    /// Rolling-thunder envelope of the pile-up: 0 ahead of the edge and from <paramref name="boomSeconds"/> after it, a 0.25 s attack from exactly
    /// xi = 0, a decaying roll underneath, scaled by <see cref="BoomGain"/> (so 0 below M 0.94).
    /// </summary>
    public static float BoomEnvelope(double speedMetersPerSecond, double xi, double boomSeconds = BoomSeconds)
    {
        if (xi < 0.0 || Mach(speedMetersPerSecond) < LeadFullMach) return 0.0f;
        double t = xi / speedMetersPerSecond;
        if (t >= boomSeconds) return 0.0f;
        double attack = Smooth(t / 0.25);
        double body = Math.Exp(-t / (0.45 * boomSeconds));
        double roll = 0.85 + 0.15 * Math.Cos(2.0 * Math.PI * t / 1.1);
        double taper = 1.0 - Smooth((t - 0.6 * boomSeconds) / (0.4 * boomSeconds));
        return (float)(attack * body * roll * taper) * BoomGain(speedMetersPerSecond);
    }

    /// <summary>The brief swell riding the gust outflow: only while the sound outruns the wall (v &lt; 343 + outflow), inside the gust band ahead of the edge.</summary>
    public static float GustSwell(double speedMetersPerSecond, double xi, double bandMeters, double outflowMetersPerSecond = GustOutflowMetersPerSecond)
    {
        if (speedMetersPerSecond >= SpeedOfSoundMetersPerSecond + outflowMetersPerSecond || xi >= 0.0 || xi < -bandMeters) return 0.0f;
        return (float)(Smooth((xi + bandMeters) / (0.3 * bandMeters)) * Smooth(-xi / (0.3 * bandMeters)));
    }

    // ---- Tremor and wildlife --------------------------------------------------------------------------------------------
    /// <summary>The <c>ring_ground_tremor</c> amplitude (a later step wires the readers): HumLevel x 0.6 plus the boom envelope, clamped to 1.</summary>
    public static float TremorLevel(float humLevel, float boomEnvelope) => Math.Min(1.0f, (float)(humLevel * TremorHumScale) + boomEnvelope);

    /// <summary>0 beyond 2 R, 1 at R; stays 1 through the pass and recovers over the aftermath. Any present or future life bed multiplies by 1 - this.</summary>
    public static float WildlifeQuiet(double xi, double reachMeters, double trailingEndXi, double aftermathMeters)
    {
        if (xi < 0.0) return (float)Smooth((StageTwoReachFactor * reachMeters + xi) / ((StageTwoReachFactor - WildlifeFullReachFactor) * reachMeters));
        return (float)(1.0 - Smooth((xi - trailingEndXi) / aftermathMeters));
    }

    // ---- Combined -----------------------------------------------------------------------------------------------------
    public static MegastormSoundSample Evaluate(MegastormModel model, double xi, double acrossMeters, double speedMetersPerSecond)
    {
        MegastormSettings s = model.Settings;
        double trailingEnd = model.TrailingEndXi;
        float hum = HumLevel(xi, s.WarningReachMeters, trailingEnd);
        AirSoundResult air = AirSound(speedMetersPerSecond, xi);
        float boom = BoomEnvelope(speedMetersPerSecond, xi);
        double band = MegastormBands.BandWidthMeters(speedMetersPerSecond, s.GustFrontLeadSeconds);
        return new MegastormSoundSample(hum, WallSing(xi, s.WarningReachMeters, trailingEnd, acrossMeters, s.WidthMeters).Max, air.State, air.AirGain, boom,
            GustSwell(speedMetersPerSecond, xi, band), TremorLevel(hum, boom), WildlifeQuiet(xi, s.WarningReachMeters, trailingEnd, MegastormModel.AftermathLengthMeters));
    }
}

/// <summary>
/// Fires the edge boom once per passage (the hook for the later boom one-shot pool): true on the update that enters the Boom window. A reload
/// has no history, so it returns true only when the observer is still inside the window (the one-shot then seeks by the remaining envelope);
/// behind the window or ahead of the edge it never fires.
/// </summary>
public sealed class MegastormBoomTracker
{
    private bool _inBoom;

    public bool Update(AirSoundState state)
    {
        bool boom = state == AirSoundState.Boom;
        bool fire = boom && !_inBoom;
        _inBoom = boom;
        return fire;
    }

    public void Reset() => _inBoom = false;
}
