using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

public enum PrecipitationPhase { Below, InCloud, Above }

/// <summary>Precipitation state of one column for one observer altitude. Intensity stays continuous; factors are multipliers for consumers (streaks, haze).</summary>
public readonly record struct PrecipitationColumn(float Intensity, PrecipitationPhase Phase, bool Snow, float InCloudDepthMeters, float StreakFactor, float HazeFactor);

/// <summary>
/// Pure cloud-base / cloud-top phase logic (docs/storm-revamp-s1-design.md section 4), extracted from RingDistantRainCurtains.
/// Below: full precipitation. InCloud: between (base - margin) and top. Above: no local precipitation.
/// </summary>
public static class PrecipitationAltitude
{
    /// <summary>Observers within this distance under the base already count as in cloud; columns whose base is this close to the ground have no curtain.</summary>
    public const float CloudBaseMarginMeters = 40.0f;
    public const float InCloudStreakFactor = 0.5f;
    public const float InCloudHazeFactor = 2.0f;

    public static PrecipitationPhase Resolve(double altitudeMeters, double cloudBaseMeters, double cloudTopMeters)
    {
        if (!double.IsFinite(altitudeMeters) || !double.IsFinite(cloudBaseMeters)) return PrecipitationPhase.Below;
        if (altitudeMeters + CloudBaseMarginMeters < cloudBaseMeters) return PrecipitationPhase.Below;
        if (double.IsNaN(cloudTopMeters) || altitudeMeters <= cloudTopMeters) return PrecipitationPhase.InCloud;
        return PrecipitationPhase.Above;
    }

    /// <summary>Multiplier on local precipitation intensity: only Above removes it.</summary>
    public static float IntensityFactor(PrecipitationPhase phase) => phase == PrecipitationPhase.Above ? 0.0f : 1.0f;
    public static float StreakFactor(PrecipitationPhase phase) => phase switch { PrecipitationPhase.Below => 1.0f, PrecipitationPhase.InCloud => InCloudStreakFactor, _ => 0.0f };
    public static float HazeFactor(PrecipitationPhase phase) => phase switch { PrecipitationPhase.Below => 1.0f, PrecipitationPhase.InCloud => InCloudHazeFactor, _ => 0.0f };

    public static float InCloudDepth(PrecipitationPhase phase, double altitudeMeters, double cloudBaseMeters)
        => phase == PrecipitationPhase.InCloud ? (float)Math.Max(0.0, altitudeMeters - cloudBaseMeters) : 0.0f;

    /// <summary>
    /// Cloud base/top for a weather sample. Manual systems (negative id) carry their own altitudes (-1003 is datum-relative);
    /// natural systems use the bridge's fixed datum plus the natural or front base.
    /// </summary>
    public static (double Base, double Top) ResolveCloudBounds(int systemId, bool naturalFront, float weatherBase, float weatherTop,
        float datum, float naturalBase, float naturalTop, float frontBase)
    {
        if (systemId < 0)
        {
            float shift = systemId == -1003 ? datum : 0.0f;
            double manualBase = weatherBase + shift;
            return (manualBase, Math.Max(weatherTop + shift, manualBase + 500.0));
        }
        double cloudBase = datum + Math.Max(100.0f, naturalFront ? frontBase : naturalBase);
        return (cloudBase, Math.Max(datum + naturalTop, cloudBase + 500.0));
    }

    /// <summary>Provider installed by the active RingLocalWeatherEffects; null before a world is running.</summary>
    public static Func<RingCanonicalPosition, PrecipitationColumn>? ColumnProvider { get; set; }

    /// <summary>Flight hook: precipitation at a canonical position (intensity, phase, snow, in-cloud depth). Default (clear, Below) when no world is running.</summary>
    public static PrecipitationColumn QueryPrecipitationColumn(RingCanonicalPosition position)
        => ColumnProvider?.Invoke(position) ?? new PrecipitationColumn(0.0f, PrecipitationPhase.Below, false, 0.0f, 1.0f, 1.0f);
}
