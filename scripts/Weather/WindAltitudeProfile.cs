using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Pure altitude profile of the mean wind (docs/storm-revamp-s3-storm-arrival-design.md section 8). Vectors are (around, across) in m/s, heights are metres above ground.
/// Up to 10 m the surface wind is unchanged; from 10 m to the boundary-layer top (1 km) it follows a power law (alpha 0.14 open, 0.22 forest) that is blended into
/// the free wind so the speed is continuous at 1 km; above that the free wind has the weather drift direction (the direction that shears the anvils) with a speed
/// that ramps linearly from max(|base|, 4) m/s at 1 km to the jet at the anvil band. The direction veers from the surface (storm-rotated or outflow) to the drift
/// direction across the boundary layer; a ring has no Coriolis veer, so this is the only turning. The surface gust deviation fades out by 300 m.
/// </summary>
public static class WindAltitudeProfile
{
    public const float SurfaceHeightMeters = 10.0f;
    public const float BoundaryLayerTopMeters = 1000.0f;
    public const float GustFadeStartMeters = 10.0f;
    public const float GustFadeEndMeters = 300.0f;
    public const float MinimumFreeSpeed = 4.0f;
    public const float JetSpeed = 25.0f;
    public const float DefaultJetAltitudeMeters = 9000.0f;
    public const float OpenAlpha = 0.14f;
    public const float ForestAlpha = 0.22f;

    /// <summary>Jet multiplier by storm kind: x1.4 Exceptional, x1.8 Superstorm, 1 otherwise.</summary>
    public static float JetKindMultiplier(int kind) => kind >= 5 ? 1.8f : kind == 4 ? 1.4f : 1.0f;

    static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Mean wind (around, across) at a height above ground.</summary>
    public static Vector2 MeanAt(Vector2 surfaceMean, Vector2 drift, float heightMeters, bool forest, float jetMultiplier = 1.0f, float jetAltitudeMeters = DefaultJetAltitudeMeters)
    {
        if (!surfaceMean.IsFinite()) surfaceMean = Vector2.Zero;
        if (!drift.IsFinite()) drift = Vector2.Zero;
        float h = float.IsFinite(heightMeters) ? Mathf.Max(0.0f, heightMeters) : 0.0f;
        if (h <= SurfaceHeightMeters) return surfaceMean;
        float surfaceSpeed = surfaceMean.Length();
        float freeSpeed1km = Mathf.Max(drift.Length(), MinimumFreeSpeed);
        Vector2 freeDirection = drift.LengthSquared() > 1e-6f ? drift.Normalized() : (surfaceSpeed > 1e-3f ? surfaceMean.Normalized() : Vector2.Right);
        Vector2 surfaceDirection = surfaceSpeed > 1e-3f ? surfaceMean / surfaceSpeed : freeDirection;
        float speed;
        float veer = Smooth(0.0f, 1.0f, Mathf.Clamp((h - SurfaceHeightMeters) / (BoundaryLayerTopMeters - SurfaceHeightMeters), 0.0f, 1.0f));
        if (h < BoundaryLayerTopMeters)
        {
            float alpha = forest ? ForestAlpha : OpenAlpha;
            float power = surfaceSpeed * Mathf.Pow(h / SurfaceHeightMeters, alpha);
            float blend = Smooth(0.35f, 1.0f, (h - SurfaceHeightMeters) / (BoundaryLayerTopMeters - SurfaceHeightMeters));
            speed = Mathf.Lerp(power, freeSpeed1km, blend);
        }
        else
        {
            float jet = JetSpeed * Mathf.Max(1.0f, jetMultiplier);
            float span = Mathf.Max(1000.0f, jetAltitudeMeters - BoundaryLayerTopMeters);
            speed = Mathf.Lerp(freeSpeed1km, jet, Mathf.Clamp((h - BoundaryLayerTopMeters) / span, 0.0f, 1.0f));
            veer = 1.0f;
        }
        float a0 = surfaceDirection.Angle(), a1 = freeDirection.Angle();
        float delta = Mathf.Wrap(a1 - a0, -Mathf.Pi, Mathf.Pi);
        return Vector2.FromAngle(a0 + delta * veer) * speed;
    }

    /// <summary>Gust-level wind: the mean plus the surface gust deviation, faded out by 300 m.</summary>
    public static Vector2 GustAt(Vector2 surfaceMean, Vector2 surfaceGust, Vector2 mean, float heightMeters)
    {
        if (!surfaceGust.IsFinite()) surfaceGust = surfaceMean;
        float fade = 1.0f - Smooth(GustFadeStartMeters, GustFadeEndMeters, heightMeters);
        return mean + (surfaceGust - surfaceMean) * fade;
    }

    /// <summary>Magnitude of the vertical shear |dU/dz| (1/s) by a central difference over 40 m.</summary>
    public static float ShearAt(Vector2 surfaceMean, Vector2 drift, float heightMeters, bool forest, float jetMultiplier = 1.0f, float jetAltitudeMeters = DefaultJetAltitudeMeters)
    {
        float lo = Mathf.Max(0.0f, heightMeters - 20.0f), hi = lo + 40.0f;
        return (MeanAt(surfaceMean, drift, hi, forest, jetMultiplier, jetAltitudeMeters) - MeanAt(surfaceMean, drift, lo, forest, jetMultiplier, jetAltitudeMeters)).Length() / (hi - lo);
    }
}
