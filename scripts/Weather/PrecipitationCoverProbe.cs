using System;
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

/// <summary>
/// Wind-aligned cover for bullet rain (docs/megastorm-design.md 5.5.2): the top-down occlusion map cannot shelter the lee of a wall from near-horizontal rain,
/// so eight rays (four body heights x two lateral angles) march upwind along the drop path (rising by fall speed / wind speed per metre) and test solid matter.
/// Pure of the world: the caller supplies the solid test. Exposure is the share of rays that reach 48 m upwind unobstructed.
/// </summary>
public static class WindAlignedCoverProbe
{
    public const int RayCount = 8;
    public const float LateralAngleRadians = 0.12f, MaxSlope = 2.0f, FallSpeedMetersPerSecond = 9.0f;
    private static readonly float[] Distances = { 1.0f, 2.0f, 3.0f, 4.5f, 6.0f, 8.0f, 11.0f, 15.0f, 20.0f, 28.0f, 40.0f };
    private static readonly float[] HeightOffsets = { -1.4f, -0.9f, -0.4f, 0.1f };

    /// <param name="windAroundAcross">Horizontal wind (around, across), m/s: the drops travel along it, so the rays go the opposite way.</param>
    /// <param name="isSolid">(around, vertical, across) in ring space: true when solid matter occupies the point.</param>
    public static float Exposure(double around, double vertical, double across, Godot.Vector2 windAroundAcross, Func<double, double, double, bool> isSolid)
    {
        float speed = windAroundAcross.Length();
        if (!(speed > 0.5f)) return 1.0f;           // no wind-driven rain: the overhead cover (occlusion map, shelter class) decides
        Godot.Vector2 upwind = -windAroundAcross / speed;
        float slope = Math.Min(MaxSlope, FallSpeedMetersPerSecond / speed);
        int open = 0;
        for (int lateral = -1; lateral <= 1; lateral += 2)
        {
            Godot.Vector2 dir = upwind.Rotated(lateral * LateralAngleRadians);
            for (int h = 0; h < HeightOffsets.Length; h++)
            {
                bool blocked = false;
                for (int i = 0; i < Distances.Length && !blocked; i++)
                {
                    float d = Distances[i];
                    blocked = isSolid(around + dir.X * d, vertical + HeightOffsets[h] + d * slope, across + dir.Y * d);
                }
                if (!blocked) open++;
            }
        }
        return open / (float)RayCount;
    }
}
