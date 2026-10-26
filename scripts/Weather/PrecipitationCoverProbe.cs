namespace RingworldTechDemo.Weather;

/// <summary>
/// Overhead cover at the listener, read from the precipitation occlusion map by a 10 Hz GPU probe with asynchronous readback
/// (docs/storm-revamp-s1-design.md section 1). Consumers (rain sound field, thunder muffling) use these values only while
/// <see cref="IsFresh"/> is true and otherwise fall back to their CPU column resolver (headless runs, startup, in-cloud/above phases, listener outside the map).
/// </summary>
public interface IPrecipitationCoverProbe
{
    /// <summary>Metres from the listener up to the top of the first surface seen from above at the listener's own texel; 0 when that texel is open.</summary>
    float ListenerCoverHeightMeters { get; }
    /// <summary>Fraction (0..1) of the texels within 2 m of the listener that are open to the sky.</summary>
    float ListenerOpenFraction { get; }
    /// <summary>Mean metres from the listener up to the covering surface over the covered texels within 2 m (0 when none): the overburden proxy for thunder muffling.</summary>
    float ListenerCoverDepthMeters { get; }
    /// <summary>True when the newest readback belongs to the currently published map frame, is under 0.5 s old and the listener is inside the map.</summary>
    bool IsFresh { get; }
}

/// <summary>Static accessor for the active probe. Null before a world runs or after the producer leaves the tree; a non-null probe with IsFresh == false means "use the CPU fallback".</summary>
public static class PrecipitationCover
{
    public static IPrecipitationCoverProbe? Probe { get; internal set; }
}
