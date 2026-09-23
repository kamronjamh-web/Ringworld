using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Authoritative, runtime-editable parameters for the curved ring-local air
/// shell. Registered materials keep the same physical result in main and
/// scaled-space coordinates.
/// </summary>
public static class RingAtmosphereModel
{
    private sealed class MaterialBinding
    {
        public MaterialBinding(
            ShaderMaterial material,
            double ringRadiusMeters,
            float coordinateScale,
            float opacityMultiplier)
        {
            Material = new WeakReference<ShaderMaterial>(material);
            RingRadiusMeters = ringRadiusMeters;
            CoordinateScale = coordinateScale;
            OpacityMultiplier = opacityMultiplier;
        }

        public WeakReference<ShaderMaterial> Material { get; }
        public double RingRadiusMeters { get; set; }
        public float CoordinateScale { get; set; }
        public float OpacityMultiplier { get; set; }
    }

    private static readonly List<MaterialBinding> Materials = new();
    private static bool _enabled = true;

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            ApplyAll();
        }
    }

    public static float FloorDepthMeters { get; private set; } = 2500.0f;
    public static float BoundaryHeightMeters { get; private set; } = 2500.0f;
    public static float UpperHeightMeters { get; private set; } = 20000.0f;
    public static float BoundaryExtinctionMeters { get; private set; } = 60000.0f;
    public static float UpperExtinctionMeters { get; private set; } = 300000.0f;
    public static float BoundaryMaxOpacity { get; private set; } = 0.22f;
    public static float UpperMaxOpacity { get; private set; } = 0.08f;
    public static float TotalMaxOpacity { get; private set; } = 0.80f;
    public static float ClearDistanceMeters { get; private set; } = 30000.0f;
    public static float FullDistanceMeters { get; private set; } = 220000.0f;
    public static float HighAltitudeClearDistanceMeters { get; private set; } = 200000.0f;
    public static float HighAltitudeFullDistanceMeters { get; private set; } = 600000.0f;
    public static float DistanceAltitudeStartMeters { get; private set; } = 1500.0f;
    public static float DistanceAltitudeEndMeters { get; private set; } = 12000.0f;
    public static float DistanceHazeMaxOpacity { get; private set; } = 0.68f;
    public static float NightDensityFactor { get; private set; } = 0.08f;
    public static float ScatteringEnergy { get; private set; } = 0.26f;
    public static Color DayColor { get; private set; } = new(0.34f, 0.46f, 0.60f, 1.0f);
    public static Color NightColor { get; private set; } = new(0.026f, 0.035f, 0.055f, 1.0f);

    public static void ApplyScaleReadabilityTuning(
        bool enabled,
        float floorDepthMeters,
        float boundaryHeightMeters,
        float upperHeightMeters,
        float boundaryExtinctionMeters,
        float upperExtinctionMeters,
        float boundaryMaxOpacity,
        float upperMaxOpacity,
        float totalMaxOpacity,
        float clearDistanceMeters,
        float fullDistanceMeters,
        float highAltitudeClearDistanceMeters,
        float highAltitudeFullDistanceMeters,
        float distanceAltitudeStartMeters,
        float distanceAltitudeEndMeters,
        float distanceHazeMaxOpacity,
        float nightDensityFactor,
        float scatteringEnergy,
        Color dayColor,
        Color nightColor)
    {
        _enabled = enabled;
        FloorDepthMeters = Mathf.Max(0.0f, floorDepthMeters);
        BoundaryHeightMeters = Mathf.Max(1.0f, boundaryHeightMeters);
        UpperHeightMeters = Mathf.Max(BoundaryHeightMeters + 1.0f, upperHeightMeters);
        BoundaryExtinctionMeters = Mathf.Max(1.0f, boundaryExtinctionMeters);
        UpperExtinctionMeters = Mathf.Max(1.0f, upperExtinctionMeters);
        BoundaryMaxOpacity = Mathf.Clamp(boundaryMaxOpacity, 0.0f, 0.95f);
        UpperMaxOpacity = Mathf.Clamp(upperMaxOpacity, 0.0f, 0.95f);
        TotalMaxOpacity = Mathf.Clamp(totalMaxOpacity, 0.0f, 0.98f);
        ClearDistanceMeters = Mathf.Max(0.0f, clearDistanceMeters);
        FullDistanceMeters = Mathf.Max(ClearDistanceMeters + 1.0f, fullDistanceMeters);
        HighAltitudeClearDistanceMeters = Mathf.Max(0.0f, highAltitudeClearDistanceMeters);
        HighAltitudeFullDistanceMeters = Mathf.Max(
            HighAltitudeClearDistanceMeters + 1.0f,
            highAltitudeFullDistanceMeters);
        DistanceAltitudeStartMeters = Mathf.Max(0.0f, distanceAltitudeStartMeters);
        DistanceAltitudeEndMeters = Mathf.Max(
            DistanceAltitudeStartMeters + 1.0f,
            distanceAltitudeEndMeters);
        DistanceHazeMaxOpacity = Mathf.Clamp(distanceHazeMaxOpacity, 0.0f, 0.98f);
        NightDensityFactor = Mathf.Clamp(nightDensityFactor, 0.0f, 1.0f);
        ScatteringEnergy = Mathf.Clamp(scatteringEnergy, 0.0f, 2.0f);
        DayColor = dayColor;
        NightColor = nightColor;
        ApplyAll();
    }

    public static void ConfigureMaterial(
        ShaderMaterial? material,
        double ringRadiusMeters,
        float coordinateScale = 1.0f,
        float opacityMultiplier = 1.0f)
    {
        if (material == null) return;
        float scale = Math.Max(0.000001f, coordinateScale);
        float multiplier = Math.Clamp(opacityMultiplier, 0.0f, 2.0f);
        MaterialBinding? existingBinding = null;
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            if (!Materials[index].Material.TryGetTarget(out ShaderMaterial? existing)
                || !GodotObject.IsInstanceValid(existing))
            {
                Materials.RemoveAt(index);
                continue;
            }
            if (ReferenceEquals(existing, material)) existingBinding = Materials[index];
        }

        if (existingBinding == null)
        {
            existingBinding = new MaterialBinding(material, ringRadiusMeters, scale, multiplier);
            Materials.Add(existingBinding);
        }
        else
        {
            existingBinding.RingRadiusMeters = ringRadiusMeters;
            existingBinding.CoordinateScale = scale;
            existingBinding.OpacityMultiplier = multiplier;
        }
        ApplyMaterial(material, ringRadiusMeters, scale, multiplier);
    }

    private static void ApplyAll()
    {
        for (int index = Materials.Count - 1; index >= 0; index--)
        {
            MaterialBinding binding = Materials[index];
            if (!binding.Material.TryGetTarget(out ShaderMaterial? material)
                || !GodotObject.IsInstanceValid(material))
            {
                Materials.RemoveAt(index);
                continue;
            }
            ApplyMaterial(
                material,
                binding.RingRadiusMeters,
                binding.CoordinateScale,
                binding.OpacityMultiplier);
        }
    }

    private static void ApplyMaterial(
        ShaderMaterial material,
        double ringRadiusMeters,
        float coordinateScale,
        float opacityMultiplier)
    {
        material.SetShaderParameter("ring_atmosphere_enabled", _enabled);
        material.SetShaderParameter("ring_radius_meters", (float)(ringRadiusMeters * coordinateScale));
        material.SetShaderParameter("atmosphere_floor_depth_meters", FloorDepthMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_boundary_height_meters", BoundaryHeightMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_upper_height_meters", UpperHeightMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_boundary_extinction_meters", BoundaryExtinctionMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_upper_extinction_meters", UpperExtinctionMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_boundary_max_opacity", BoundaryMaxOpacity);
        material.SetShaderParameter("atmosphere_upper_max_opacity", UpperMaxOpacity);
        material.SetShaderParameter("atmosphere_total_max_opacity", TotalMaxOpacity);
        material.SetShaderParameter("atmosphere_clear_distance_meters", ClearDistanceMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_full_distance_meters", FullDistanceMeters * coordinateScale);
        material.SetShaderParameter(
            "atmosphere_high_altitude_clear_distance_meters",
            HighAltitudeClearDistanceMeters * coordinateScale);
        material.SetShaderParameter(
            "atmosphere_high_altitude_full_distance_meters",
            HighAltitudeFullDistanceMeters * coordinateScale);
        material.SetShaderParameter(
            "atmosphere_distance_altitude_start_meters",
            DistanceAltitudeStartMeters * coordinateScale);
        material.SetShaderParameter(
            "atmosphere_distance_altitude_end_meters",
            DistanceAltitudeEndMeters * coordinateScale);
        material.SetShaderParameter("atmosphere_distance_haze_max_opacity", DistanceHazeMaxOpacity);
        material.SetShaderParameter("atmosphere_opacity_multiplier", opacityMultiplier);
        material.SetShaderParameter("atmosphere_night_density_factor", NightDensityFactor);
        material.SetShaderParameter("atmosphere_scattering_energy", ScatteringEnergy);
        material.SetShaderParameter("atmosphere_day_color", new Vector3(DayColor.R, DayColor.G, DayColor.B));
        material.SetShaderParameter("atmosphere_night_color", new Vector3(NightColor.R, NightColor.G, NightColor.B));
    }

    public static bool RunContractSelfTest(double ringRadiusMeters, out string summary)
    {
        double radius = Math.Max(1.0, ringRadiusMeters);
        // Small integration rings cannot form chords long enough to exercise
        // the configured long-range visibility endpoints. Use a synthetic,
        // otherwise identical radius for those scale-profile assertions while
        // retaining the caller's radius for opposite-side and exterior safety.
        double profileRadius = Math.Max(
            radius,
            Math.Max(
                FullDistanceMeters * 0.5 + 1000.0,
                HighAltitudeFullDistanceMeters * 0.5 + DistanceAltitudeEndMeters + 1000.0));
        double opacity5Km = SurfaceViewOpacity(profileRadius, 5000.0);
        double opacityClearDistance = SurfaceViewOpacity(profileRadius, ClearDistanceMeters);
        double opacityMidDistance = SurfaceViewOpacity(
            profileRadius,
            (ClearDistanceMeters + FullDistanceMeters) * 0.5);
        double opacityFullDistance = SurfaceViewOpacity(profileRadius, FullDistanceMeters);
        double mountainClearDistance = AltitudeViewOpacity(
            profileRadius,
            DistanceAltitudeEndMeters,
            HighAltitudeClearDistanceMeters);
        double mountainFullDistance = AltitudeViewOpacity(
            profileRadius,
            DistanceAltitudeEndMeters,
            HighAltitudeFullDistanceMeters);
        double oppositeOpacity = CalculateOpacity(radius, 0.0, -radius, 0.0, radius);
        double reachableSurfaceFullOpacity = SurfaceViewOpacity(
            radius,
            Math.Min(FullDistanceMeters, radius * 2.0));
        double highAltitudeOpacity = CalculateOpacity(
            radius - UpperHeightMeters - 5000.0,
            0.0,
            0.0,
            radius - UpperHeightMeters - 5000.0,
            radius);
        bool passed = opacity5Km <= 0.000001
            && opacityClearDistance <= 0.0001
            && opacityMidDistance > opacityClearDistance
            && opacityMidDistance < opacityFullDistance
            && opacityFullDistance >= DistanceHazeMaxOpacity * 0.99
            && opacityFullDistance <= TotalMaxOpacity + 0.0001
            && mountainClearDistance <= 0.0001
            && mountainFullDistance >= DistanceHazeMaxOpacity * 0.99
            && mountainFullDistance <= TotalMaxOpacity + 0.0001
            && oppositeOpacity >= reachableSurfaceFullOpacity - 0.0001
            && highAltitudeOpacity <= 0.000001;
        summary = $"profileRadius={profileRadius / 1000.0:0.#}km, surface(5km={opacity5Km:0.000}, clear={opacityClearDistance:0.000}, mid={opacityMidDistance:0.000}, full={opacityFullDistance:0.000}), "
            + $"mountain(clear={mountainClearDistance:0.000}, full={mountainFullDistance:0.000}), "
            + $"opposite={oppositeOpacity:0.000}, aboveAir={highAltitudeOpacity:0.000}";
        return passed;
    }

    private static double SurfaceViewOpacity(double radius, double viewDistanceMeters)
    {
        return AltitudeViewOpacity(radius, 0.0, viewDistanceMeters);
    }

    private static double AltitudeViewOpacity(double radius, double altitudeMeters, double viewDistanceMeters)
    {
        double radialDistance = Math.Max(1.0, radius - Math.Max(0.0, altitudeMeters));
        double maximumChord = radialDistance * 2.0;
        double chord = Math.Clamp(viewDistanceMeters, 0.0, maximumChord);
        double angle = 2.0 * Math.Asin(chord / maximumChord);
        return CalculateOpacity(
            radialDistance,
            0.0,
            Math.Cos(angle) * radialDistance,
            Math.Sin(angle) * radialDistance,
            radius);
    }

    private static double CalculateOpacity(double startX, double startZ, double endX, double endZ, double radius)
    {
        double outer = radius + FloorDepthMeters;
        double boundaryInner = Math.Max(1.0, radius - BoundaryHeightMeters);
        double upperInner = Math.Max(1.0, radius - UpperHeightMeters);
        double insideOuter = SegmentLengthInsideCircle(startX, startZ, endX, endZ, outer);
        double insideBoundaryVacuum = SegmentLengthInsideCircle(startX, startZ, endX, endZ, boundaryInner);
        double boundaryPath = Math.Max(0.0, insideOuter - insideBoundaryVacuum);
        double upperPath = Math.Max(
            0.0,
            insideBoundaryVacuum - SegmentLengthInsideCircle(startX, startZ, endX, endZ, upperInner));
        double boundaryOpacity = (1.0 - Math.Exp(-boundaryPath / BoundaryExtinctionMeters)) * BoundaryMaxOpacity;
        double upperOpacity = (1.0 - Math.Exp(-upperPath / UpperExtinctionMeters)) * UpperMaxOpacity;
        double physicalOpacity = 1.0 - (1.0 - boundaryOpacity) * (1.0 - upperOpacity);
        double viewDistance = Math.Sqrt(
            (endX - startX) * (endX - startX)
            + (endZ - startZ) * (endZ - startZ));
        double cameraRadialDistance = Math.Sqrt(startX * startX + startZ * startZ);
        double cameraAltitude = Math.Max(0.0, radius - cameraRadialDistance);
        double altitudeT = Math.Clamp(
            (cameraAltitude - DistanceAltitudeStartMeters)
                / Math.Max(1.0, DistanceAltitudeEndMeters - DistanceAltitudeStartMeters),
            0.0,
            1.0);
        double altitudeBlend = altitudeT * altitudeT * (3.0 - 2.0 * altitudeT);
        double clearDistance = Lerp(ClearDistanceMeters, HighAltitudeClearDistanceMeters, altitudeBlend);
        double fullDistance = Lerp(FullDistanceMeters, HighAltitudeFullDistanceMeters, altitudeBlend);
        double distanceT = Math.Clamp(
            (viewDistance - clearDistance) / Math.Max(1.0, fullDistance - clearDistance),
            0.0,
            1.0);
        double distanceWeight = distanceT * distanceT * (3.0 - 2.0 * distanceT);
        double airFadeStart = UpperHeightMeters;
        double airFadeEnd = airFadeStart + Math.Max(1000.0, UpperHeightMeters * 0.1);
        double airT = Math.Clamp((cameraAltitude - airFadeStart) / (airFadeEnd - airFadeStart), 0.0, 1.0);
        double cameraAirPresence = 1.0 - airT * airT * (3.0 - 2.0 * airT);
        if (cameraRadialDistance >= radius + FloorDepthMeters) cameraAirPresence = 0.0;
        double distanceHazeOpacity = distanceWeight * DistanceHazeMaxOpacity * cameraAirPresence;
        // Compose the shell extinction and distance haze as independent optical
        // depths. This keeps the transition gradual instead of allowing the
        // distance term to abruptly replace the physical atmosphere.
        double physicalContribution = physicalOpacity * distanceWeight;
        double combinedOpacity = 1.0 - (1.0 - physicalContribution) * (1.0 - distanceHazeOpacity);
        return Math.Min(TotalMaxOpacity, combinedOpacity);
    }

    private static double Lerp(double from, double to, double weight)
    {
        return from + (to - from) * weight;
    }

    private static double SegmentLengthInsideCircle(double startX, double startZ, double endX, double endZ, double radius)
    {
        double dx = endX - startX;
        double dz = endZ - startZ;
        double a = dx * dx + dz * dz;
        double length = Math.Sqrt(a);
        if (a < 1e-12) return startX * startX + startZ * startZ <= radius * radius ? length : 0.0;
        double b = 2.0 * (startX * dx + startZ * dz);
        double c = startX * startX + startZ * startZ - radius * radius;
        double discriminant = b * b - 4.0 * a * c;
        if (discriminant <= 0.0) return 0.0;
        double root = Math.Sqrt(discriminant);
        double t0 = (-b - root) / (2.0 * a);
        double t1 = (-b + root) / (2.0 * a);
        double enter = Math.Clamp(Math.Min(t0, t1), 0.0, 1.0);
        double exit = Math.Clamp(Math.Max(t0, t1), 0.0, 1.0);
        return Math.Max(0.0, exit - enter) * length;
    }
}
