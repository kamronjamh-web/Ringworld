using System;
using Godot;

namespace RingworldTechDemo.Poi;

/// <summary>
/// Plume geometry in the current world frame: where the nozzle ring and the hood crest are, the exhaust direction (the POI's up) and a
/// 0..1 strength (the exhale envelope).
/// </summary>
public readonly record struct WeatherLungPlumeState(Vector3 NozzleOrigin, Vector3 CrestOrigin, Vector3 CrestDirection, float Strength);

/// <summary>
/// Weather API of the Weather Lung (data only, in the style of <see cref="IEmitterBeamSource"/>). A future cloud layer reads the breath
/// and plume from here; nothing in this milestone writes weather or cloud state.
///
/// Constraints for the future SunshineClouds hook (plan P14): changing <c>point_effector_data</c> forces a cloud-history hard reset
/// (<c>_continuity_hard("effectors")</c> in SunshineClouds.gd), so a per-frame breathing plume needs a fix on the Sunshine side first;
/// <c>RegisterManualWeather</c> is additive (Math.Max), unsaved and session-only.
/// </summary>
public interface IWeatherLungWeatherSource
{
    /// <summary>True while the station exists in this world and is presented.</summary>
    bool IsPresent { get; }
    ulong Identity { get; }
    /// <summary>Increments whenever the world frame / placement is rebuilt, so cached geometry can be invalidated.</summary>
    int GeometryRevision { get; }
    double CentreAround { get; }
    double CentreAcross { get; }
    /// <summary>Outer basin half-extents in metres (along the ring, across the ring): 14 km x 9 km.</summary>
    Vector2 BasinRadii { get; }
    /// <summary>Breath phase in [0, 1): inhale 0..0.5, exhale 0.5..1.</summary>
    double BreathPhase { get; }
    /// <summary>0..1 vane opening, 0.5 - 0.5 cos 2pi p.</summary>
    double InhaleLevel { get; }
    /// <summary>max(0, -sin 2pi p)^2.</summary>
    double ExhaleEnvelope { get; }
    /// <summary>Smoothed sun availability, 0 night .. 1 day (LocalHazeDay in Auto mode).</summary>
    float SunAvailable { get; }
    bool TryGetPlume(out WeatherLungPlumeState plume);
    event Action? Changed;
}
