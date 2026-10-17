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
    public enum ArtDirectionPreset { Natural, Cinematic }
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
    private static ArtDirectionPreset _artPreset = ArtDirectionPreset.Natural;
    private static float _visibilityFullKm = 120.0f, _density = 1.0f, _altitudeFalloff = 0.5f;
    private static float _aerosol = 0.35f, _forwardScattering = 0.22f, _sunWarmth = 0.25f;
    public static ulong Revision { get; private set; }

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Revision++;
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
    public static float Aerosol { get; private set; } = 0.35f;
    public static float ForwardScattering { get; private set; } = 0.22f;
    public static float SunWarmth { get; private set; } = 0.25f;
    public static Color DayColor { get; private set; } = new(0.34f, 0.46f, 0.60f, 1.0f);
    public static Color NightColor { get; private set; } = new(0.026f, 0.035f, 0.055f, 1.0f);
    /// <summary>Eclipse darkness multiplier on the night colour for the CPU-built cloud profiles (Sunshine, far mask). Godot shaders use the ringworld_night_light_scale global instead. Exactly 1 outside an eclipse.</summary>
    public static float EclipseNightScale { get; set; } = 1.0f;

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
        _enabled = enabled && (bool)ProjectSettings.GetSetting("ringworld/runtime/haze_enabled", true);
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
        ApplyProfile();
        Revision++;
        ApplyAll();
    }

    /// <summary>Applies the persisted artistic controls without changing shell geometry.</summary>
    public static ArtDirectionPreset ArtPreset => _artPreset;
    public static float VisibilityFullKm => _visibilityFullKm;
    public static float Density => _density;
    public static float AltitudeFalloff => _altitudeFalloff;

    public static void ApplyArtDirection(ArtDirectionPreset preset, float visibilityFullKm, float density, float altitudeFalloff, float aerosol, float forwardScattering, float sunWarmth)
    {
        _artPreset = preset;
        _visibilityFullKm = FiniteOrDefault(visibilityFullKm, 120.0f);
        _density = FiniteOrDefault(density, 1.0f);
        _altitudeFalloff = FiniteOrDefault(altitudeFalloff, 0.5f);
        _aerosol = FiniteOrDefault(aerosol, 0.35f);
        _forwardScattering = FiniteOrDefault(forwardScattering, 0.22f);
        _sunWarmth = FiniteOrDefault(sunWarmth, 0.25f);
        ApplyProfile();
        Revision++;
        ApplyAll();
    }

    private static void ApplyProfile()
    {
        float full = Mathf.Clamp(_visibilityFullKm, 10.0f, 400.0f) * 1000.0f;
        float cleanDensity = Mathf.Clamp(_density, 0.0f, 2.0f);
        FullDistanceMeters = full;
        float clearFraction = _artPreset == ArtDirectionPreset.Cinematic
            ? 0.004166667f // 250 m at the cinematic 60 km default.
            : 0.008333333f; // 1 km at the natural 120 km default.
        ClearDistanceMeters = Mathf.Max(0.0f, full * clearFraction);
        HighAltitudeFullDistanceMeters = full * Mathf.Lerp(1.5f, 5.0f, Mathf.Clamp(_altitudeFalloff, 0.0f, 1.0f));
        HighAltitudeClearDistanceMeters = ClearDistanceMeters * Mathf.Lerp(2.0f, 8.0f, Mathf.Clamp(_altitudeFalloff, 0.0f, 1.0f));
        DistanceHazeMaxOpacity = Mathf.Clamp(0.45f * cleanDensity, 0.0f, 0.8f);
        BoundaryMaxOpacity = Mathf.Clamp(0.16f * cleanDensity, 0.0f, 0.8f);
        UpperMaxOpacity = Mathf.Clamp(0.06f * cleanDensity, 0.0f, 0.8f);
        Aerosol = Mathf.Clamp(_aerosol, 0.0f, 1.0f);
        ForwardScattering = Mathf.Clamp(_forwardScattering, 0.0f, 0.85f);
        SunWarmth = Mathf.Clamp(_sunWarmth, 0.0f, 1.0f);
    }

    private static float FiniteOrDefault(float value, float fallback) => float.IsFinite(value) ? value : fallback;

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
        material.SetShaderParameter("atmosphere_aerosol", Aerosol);
        material.SetShaderParameter("atmosphere_forward_scattering", ForwardScattering);
        material.SetShaderParameter("atmosphere_sun_warmth", SunWarmth);
        material.SetShaderParameter("atmosphere_day_color", new Vector3(DayColor.R, DayColor.G, DayColor.B));
        material.SetShaderParameter("atmosphere_night_color", new Vector3(NightColor.R, NightColor.G, NightColor.B));
    }

    public static bool RunContractSelfTest(double ringRadiusMeters, out string summary)
    {
        // Ray/finite-cylinder intersection is verified by the GPU fixture.
        // This deliberately tests only the profile transfer function, using
        // explicit connected shell lengths supplied by the caller below.
        _ = ringRadiusMeters;
        double opacityNearDistance = ProfileOpacity(0.0, ClearDistanceMeters * 0.5, ClearDistanceMeters * 0.5, 0.0);
        double opacityClearDistance = ProfileOpacity(0.0, ClearDistanceMeters, ClearDistanceMeters, 0.0);
        double midDistance = (ClearDistanceMeters + FullDistanceMeters) * 0.5;
        double opacityMidDistance = ProfileOpacity(0.0, midDistance, midDistance, 0.0);
        double opacityFullDistance = ProfileOpacity(0.0, FullDistanceMeters, FullDistanceMeters, 0.0);
        double mountainClearDistance = ProfileOpacity(
            DistanceAltitudeEndMeters,
            HighAltitudeClearDistanceMeters, 0.0, HighAltitudeClearDistanceMeters);
        double mountainFullDistance = ProfileOpacity(
            DistanceAltitudeEndMeters,
            HighAltitudeFullDistanceMeters, 0.0, HighAltitudeFullDistanceMeters);
        bool zeroDensity = DistanceHazeMaxOpacity <= 0.000001f
            && BoundaryMaxOpacity <= 0.000001f
            && UpperMaxOpacity <= 0.000001f;
        bool profileShape = opacityNearDistance <= opacityClearDistance + 0.0001
            && opacityClearDistance <= 0.0001
            && opacityMidDistance >= opacityClearDistance - 0.0001
            && opacityFullDistance >= opacityMidDistance - 0.0001
            && opacityFullDistance <= TotalMaxOpacity + 0.0001
            && mountainClearDistance <= 0.0001
            && mountainFullDistance >= mountainClearDistance - 0.0001
            && mountainFullDistance <= TotalMaxOpacity + 0.0001;
        bool densityShape = zeroDensity
            ? opacityFullDistance <= 0.000001 && mountainFullDistance <= 0.000001
            : opacityFullDistance > opacityClearDistance && mountainFullDistance > mountainClearDistance;
        bool passed = Enabled ? profileShape && densityShape : opacityNearDistance <= 0.000001
            && opacityClearDistance <= 0.000001 && opacityMidDistance <= 0.000001
            && opacityFullDistance <= 0.000001 && mountainFullDistance <= 0.000001;
        summary = $"profileTransfer enabled={Enabled}, zeroDensity={zeroDensity}, surface(near={opacityNearDistance:0.000}, clear={opacityClearDistance:0.000}, mid={opacityMidDistance:0.000}, full={opacityFullDistance:0.000}), "
            + $"mountain(clear={mountainClearDistance:0.000}, full={mountainFullDistance:0.000}), "
            + "explicit_connected_lengths";
        return passed;
    }

    /// <summary>Profile transfer function for already-resolved connected shell lengths.</summary>
    private static double ProfileOpacity(double cameraAltitudeMeters, double viewDistanceMeters,
        double boundaryPathMeters = 0.0, double upperPathMeters = 0.0)
    {
        if (!Enabled) return 0.0;
        double connectedPath = Math.Max(0.0, viewDistanceMeters);
        double boundaryPath = Math.Max(0.0, boundaryPathMeters);
        double upperPath = Math.Max(0.0, upperPathMeters);
        double boundaryOpacity = (1.0 - Math.Exp(-boundaryPath / BoundaryExtinctionMeters)) * BoundaryMaxOpacity;
        double upperOpacity = (1.0 - Math.Exp(-upperPath / UpperExtinctionMeters)) * UpperMaxOpacity;
        double physicalOpacity = 1.0 - (1.0 - boundaryOpacity) * (1.0 - upperOpacity);
        double viewDistance = connectedPath;
        double cameraAltitude = Math.Max(0.0, cameraAltitudeMeters);
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

}
