using System;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

/// <summary>One wind sample in the ring frame (around, up, across), m/s. Flight-ready; no gameplay consumer yet.</summary>
public readonly record struct RingWindSample(Vector3 MeanWind, Vector3 GustWind, Vector3 Turbulence, float TurbulenceSigma,
    float TurbulenceLengthMeters, float UpdraftMetersPerSecond, float VerticalShear, float HeightAboveGroundMeters,
    StormArrivalPhase Phase, float StormWeight, PrecipitationPhase Precipitation, float InCloudDepthMeters, bool Valid)
{
    public static RingWindSample Calm => new(Vector3.Zero, Vector3.Zero, Vector3.Zero, 0.0f, 50.0f, 0.0f, 0.0f, 0.0f, StormArrivalPhase.Clear, 0.0f,
        PrecipitationPhase.Below, 0.0f, false);
}

/// <summary>
/// Everything about one (around, across) column at one time that does not depend on altitude, so a column of altitudes is cheap.
/// Surface vectors are (around, across). Cloud base and top are altitudes in the canonical vertical frame.
/// </summary>
public readonly record struct RingWindColumn(
    Vector2 SurfaceMean, Vector2 SurfaceGust, Vector2 Drift, bool Forest, float GroundAltitudeMeters,
    StormArrivalState Arrival, float CloudBaseMeters, float CloudTopMeters, int Seed, bool Valid);

/// <summary>
/// Flight-ready wind and turbulence query (docs/storm-revamp-s3-storm-arrival-design.md section 8). Pure maths over a column, with the world wired in through
/// <see cref="ColumnProvider"/> / <see cref="Provider"/> by the weather layer. Returns calm and Valid = false with no world. No gameplay consumer: the only readers are
/// tests and the debug wind-column overlay. Threading: the pure maths here is allocation-free and thread-safe; see the weather layer for what the world side guarantees.
/// </summary>
public static class RingWindQuery
{
    public static Func<RingCanonicalPosition, double, RingWindSample>? Provider { get; set; }
    public static Func<RingCanonicalPosition, double, RingWindColumn>? ColumnProvider { get; set; }

    public static RingWindSample Sample(RingCanonicalPosition position, double time)
    {
        if (Provider != null) return Provider(position, time);
        if (ColumnProvider != null) return Compose(ColumnProvider(position, time), position.AroundMeters, position.VerticalMeters, position.AcrossMeters, time);
        return RingWindSample.Calm;
    }

    /// <summary>Samples one column at several altitudes (canonical vertical metres); returns the number written (min of both lengths).</summary>
    public static int SampleColumn(RingCanonicalPosition position, double time, ReadOnlySpan<float> altitudes, Span<RingWindSample> results)
    {
        int count = Math.Min(altitudes.Length, results.Length);
        if (count <= 0) return 0;
        if (ColumnProvider != null)
        {
            RingWindColumn column = ColumnProvider(position, time);
            for (int i = 0; i < count; i++) results[i] = Compose(column, position.AroundMeters, altitudes[i], position.AcrossMeters, time);
            return count;
        }
        for (int i = 0; i < count; i++) results[i] = Sample(position with { VerticalMeters = altitudes[i] }, time);
        return count;
    }

    static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    static float Hash01(int a, int b, int seed)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u ^ (uint)seed * 0xC2B2AE3Du;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return (h & 0xFFFFFF) / 16777216.0f;
        }
    }

    /// <summary>Core updraft peak by storm kind (m/s): 15 Major, 30 Exceptional, 40 Superstorm.</summary>
    public static float CoreUpdraftPeak(int kind) => kind >= 5 ? 40.0f : kind == 4 ? 30.0f : kind == 3 ? 15.0f : 0.0f;
    public const float UpdraftCellMeters = 5000.0f;
    public const float UpdraftFill = 0.3f;

    /// <summary>
    /// The storm updraft at a point: W_max sin(pi (z - base) / (top - base)) inside a statistical lattice of 5 km cells with 30% fill. v1 only; a v2 should
    /// mirror the sculpt bake's cell hash so the updrafts sit in the visible towers.
    /// </summary>
    public static float StormUpdraft(in RingWindColumn column, double around, double across, float altitudeMeters)
    {
        StormArrivalState a = column.Arrival;
        if (!a.Valid || a.Kind < 3 || a.Core <= 0.001f) return 0.0f;
        float depth = column.CloudTopMeters - column.CloudBaseMeters;
        if (depth < 200.0f || altitudeMeters <= column.CloudBaseMeters || altitudeMeters >= column.CloudTopMeters) return 0.0f;
        int ci = (int)Math.Floor(around / UpdraftCellMeters), cj = (int)Math.Floor(across / UpdraftCellMeters);
        if (Hash01(ci, cj, a.SystemId) >= UpdraftFill) return 0.0f;
        float cx = (ci + 0.2f + 0.6f * Hash01(ci, cj, a.SystemId + 1)) * UpdraftCellMeters;
        float cz = (cj + 0.2f + 0.6f * Hash01(ci, cj, a.SystemId + 2)) * UpdraftCellMeters;
        float dx = (float)(around - cx), dz = (float)(across - cz);
        float profile = 1.0f - Smooth(0.35f, 1.0f, Mathf.Sqrt(dx * dx + dz * dz) / (0.5f * UpdraftCellMeters));
        float vertical = Mathf.Sin(Mathf.Pi * (altitudeMeters - column.CloudBaseMeters) / depth);
        return CoreUpdraftPeak(a.Kind) * Mathf.Clamp(a.Core, 0.0f, 1.0f) * profile * vertical;
    }

    /// <summary>Precipitation downdraft (-5 to -12 m/s) from cloud base down to 300 m AGL, where it turns into the surface outflow carried by the mean wind.</summary>
    public static float PrecipitationDownDraft(in RingWindColumn column, float altitudeMeters, float heightAboveGround)
    {
        StormArrivalState a = column.Arrival;
        if (!a.Valid || a.Kind < 3) return 0.0f;
        float weight = Mathf.Clamp(a.Core * 0.8f + a.FatDrop * 0.4f, 0.0f, 1.0f);
        if (weight <= 0.001f) return 0.0f;
        float strength = Mathf.Lerp(5.0f, 12.0f, Mathf.Clamp(a.Severity, 0.0f, 1.0f));
        float belowBase = 1.0f - Smooth(column.CloudBaseMeters - 200.0f, column.CloudBaseMeters + 500.0f, altitudeMeters);
        return -strength * weight * belowBase * Smooth(300.0f, 600.0f, heightAboveGround);
    }

    /// <summary>Composes the sample at one altitude from a column. Pure, allocation-free, finite for any input.</summary>
    public static RingWindSample Compose(in RingWindColumn column, double around, double altitudeMeters, double across, double time)
    {
        if (!column.Valid) return RingWindSample.Calm;
        if (!double.IsFinite(altitudeMeters)) altitudeMeters = column.GroundAltitudeMeters;
        float altitude = (float)Math.Clamp(altitudeMeters, -1.0e6, 1.0e7);
        float agl = Mathf.Max(0.0f, altitude - column.GroundAltitudeMeters);
        StormArrivalState a = column.Arrival;
        float jetMultiplier = 1.0f + (WindAltitudeProfile.JetKindMultiplier(a.Kind) - 1.0f) * Mathf.Clamp(Mathf.Max(a.Core, a.GustFront), 0.0f, 1.0f);
        Vector2 mean = WindAltitudeProfile.MeanAt(column.SurfaceMean, column.Drift, agl, column.Forest, jetMultiplier);
        Vector2 gust = WindAltitudeProfile.GustAt(column.SurfaceMean, column.SurfaceGust, mean, agl);
        float shear = WindAltitudeProfile.ShearAt(column.SurfaceMean, column.Drift, agl, column.Forest, jetMultiplier);

        float cloudBase = column.CloudBaseMeters, cloudTop = column.CloudTopMeters;
        bool hasCloud = float.IsFinite(cloudBase) && float.IsFinite(cloudTop) && cloudTop > cloudBase;
        PrecipitationPhase phase = hasCloud ? PrecipitationAltitude.Resolve(altitude, cloudBase, cloudTop) : PrecipitationPhase.Below;
        float inCloudDepth = hasCloud ? PrecipitationAltitude.InCloudDepth(phase, altitude, cloudBase) : 0.0f;
        var input = new StormTurbulenceInput(agl, column.SurfaceMean.Length(), a.GustFront, a.Core, a.Severity, a.Kind, phase == PrecipitationPhase.InCloud && a.Valid && a.Severity > 0.05f,
            altitude, hasCloud ? cloudBase : 0.0f, hasCloud ? cloudTop : 1.0f, hasCloud ? cloudTop : WindAltitudeProfile.DefaultJetAltitudeMeters);
        float sigma = StormTurbulence.Sigma(input);
        float length = StormTurbulence.LengthMeters(agl, a.Core);
        var meanWind = new Vector3(mean.X, 0.0f, mean.Y);
        Vector3 turbulence = StormTurbulence.Realize(around, altitude, across, time, meanWind, sigma, length, column.Seed);
        float updraft = StormUpdraft(column, around, across, altitude) + PrecipitationDownDraft(column, altitude, agl);
        float stormWeight = Mathf.Clamp(Mathf.Max(Mathf.Max(a.Approach, a.GustFront), Mathf.Max(Mathf.Max(a.FatDrop, a.Core), a.Trailing)), 0.0f, 1.0f);
        return new RingWindSample(meanWind, new Vector3(gust.X, 0.0f, gust.Y), turbulence, sigma, length, updraft, shear, agl,
            a.Valid ? a.Phase : StormArrivalPhase.Clear, a.Valid ? stormWeight : 0.0f, phase, inCloudDepth, true);
    }
}
