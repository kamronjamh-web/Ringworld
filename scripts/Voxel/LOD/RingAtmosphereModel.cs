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
    /// <summary>Temporary A/B switch (docs/atmosphere-haze-revamp-plan.md P2a, removed in P8). Legacy = smoothstep distance ramp, Physical = ring_haze_eval.</summary>
    public enum HazeModelKind { Legacy = 0, Physical = 1 }
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
    private static HazeModelKind _hazeModel = HazeModelKind.Legacy;
    public static ulong Revision { get; private set; }
    /// <summary>Physical-haze sun irradiance multiplier set from the Atmosphere tab; the graphics controller forwards it to DaySideAtmosphere.haze_brightness.</summary>
    public static float HazeBrightness { get; set; } = 1.0f;

    public static HazeModelKind HazeModel
    {
        get => _hazeModel;
        set
        {
            if (_hazeModel == value) return;
            _hazeModel = value;
            Revision++;
            ApplyAll();
        }
    }

    // ---- Physical haze (P2a). Units are physical (1/m, m) at every coordinate scale; the shader converts positions itself. ----
    // Base values match ring_haze_params_default() in materials/ring_haze_shared.gdshaderinc.
    public static readonly Vector3 BaseSigmaRayleigh = new(5.802e-6f, 13.558e-6f, 33.1e-6f);
    public const float BaseRayleighHeightMeters = 8000.0f, BaseAerosolHeightMeters = 1500.0f, BaseMistHeightMeters = 400.0f;
    public const float BaseSigmaMist = 4.0e-6f, BaseOmegaAerosol = 0.9f, BaseOmegaMist = 0.95f, BaseMieG = 0.6f;
    /// <summary>The legacy "full opacity distance" control (Natural 120 km) maps to Koschmieder visibility with this factor (Natural 120 km -> 180 km).</summary>
    public const float VisibilityScale = 1.5f;
    public const float AerosolControlDefault = 0.35f, ForwardControlDefault = 0.22f, WarmthControlDefault = 0.25f;
    public static Vector3 HazeSigmaRayleigh { get; private set; } = BaseSigmaRayleigh;
    public static float HazeRayleighHeightMeters { get; private set; } = BaseRayleighHeightMeters;
    public static float HazeSigmaAerosol { get; private set; } = 4.175e-6f;
    public static float HazeOmegaAerosol { get; private set; } = BaseOmegaAerosol;
    public static float HazeAerosolHeightMeters { get; private set; } = BaseAerosolHeightMeters;
    public static float HazeSigmaMist { get; private set; } = BaseSigmaMist;
    public static float HazeOmegaMist { get; private set; } = BaseOmegaMist;
    public static float HazeMistHeightMeters { get; private set; } = BaseMistHeightMeters;
    public static float HazeG { get; private set; } = BaseMieG;
    public static Vector3 HazeSunTint { get; private set; } = Vector3.One;
    /// <summary>Gauss-Legendre points of the optical-depth quadrature (4/8/16). Set through <see cref="SetPhysicalControls"/>.</summary>
    public static int HazeQuality { get; private set; } = 8;
    // ---- Physical-only live controls (P4). Defaults reproduce the pre-P4 mapping exactly. Set via SetPhysicalControls, which
    // ApplyArtDirection must follow (AtmosphereVisualDefaults.Apply does both) so the profile, Revision and every material update. ----
    public const float GroundAlbedoControlDefault = 0.1f, MultiscatterGainControlDefault = 1.0f;
    /// <summary>Multiplier on sigma_M0 (after the aerosol-control and density mapping).</summary>
    public static float HazeMistDensityScale { get; private set; } = 1.0f;
    /// <summary>Base mist scale height H_mist (m); the altitude-falloff control still scales it (x2 .. x0.5).</summary>
    public static float HazeMistBaseHeightMeters { get; private set; } = BaseMistHeightMeters;
    /// <summary>Base aerosol scale height H_A (m); the altitude-falloff control still scales it (x2 .. x0.5).</summary>
    public static float HazeAerosolBaseHeightMeters { get; private set; } = BaseAerosolHeightMeters;
    /// <summary>Multiplier on sigma_R (after the density mapping). Does not rebalance sigma_A, so visibility shifts with it.</summary>
    public static float HazeRayleighStrength { get; private set; } = 1.0f;
    /// <summary>Ground bounce albedo of the sky multi-scatter (DaySideAtmosphere.haze_ground_albedo).</summary>
    public static float HazeGroundAlbedo { get; private set; } = GroundAlbedoControlDefault;
    /// <summary>Multi-scatter gain (DaySideAtmosphere.haze_multiscatter_gain).</summary>
    public static float HazeMultiscatterGain { get; private set; } = MultiscatterGainControlDefault;
    /// <summary>Per-channel tint on the haze sun/sky irradiance (DaySideAtmosphere.haze_sky_tint).</summary>
    public static Vector3 HazeSkyTint { get; private set; } = Vector3.One;

    public static void SetPhysicalControls(float mistDensityScale, float mistHeightMeters, float aerosolHeightMeters, float rayleighStrength,
        float groundAlbedo, float multiscatterGain, Vector3 skyTint, int quality)
    {
        HazeMistDensityScale = Mathf.Clamp(FiniteOrDefault(mistDensityScale, 1.0f), 0.0f, 5.0f);
        HazeMistBaseHeightMeters = Mathf.Clamp(FiniteOrDefault(mistHeightMeters, BaseMistHeightMeters), 50.0f, 2000.0f);
        HazeAerosolBaseHeightMeters = Mathf.Clamp(FiniteOrDefault(aerosolHeightMeters, BaseAerosolHeightMeters), 200.0f, 5000.0f);
        HazeRayleighStrength = Mathf.Clamp(FiniteOrDefault(rayleighStrength, 1.0f), 0.0f, 3.0f);
        HazeGroundAlbedo = Mathf.Clamp(FiniteOrDefault(groundAlbedo, GroundAlbedoControlDefault), 0.0f, 0.5f);
        HazeMultiscatterGain = Mathf.Clamp(FiniteOrDefault(multiscatterGain, MultiscatterGainControlDefault), 0.0f, 3.0f);
        HazeSkyTint = new Vector3(Mathf.Clamp(FiniteOrDefault(skyTint.X, 1.0f), 0.0f, 2.0f),
            Mathf.Clamp(FiniteOrDefault(skyTint.Y, 1.0f), 0.0f, 2.0f), Mathf.Clamp(FiniteOrDefault(skyTint.Z, 1.0f), 0.0f, 2.0f));
        HazeQuality = quality <= 6 ? 4 : quality <= 11 ? 8 : 16;
    }
    /// <summary>Top of the integrated air: the controller's upper shell height (AtmosphereUpperHeightMeters export).</summary>
    public static float HazeCapHeightMeters => UpperHeightMeters;
    /// <summary>Physical meteorological visibility (km) implied by the current art controls at density 1.</summary>
    public static float HazeVisibilityKm => Mathf.Clamp(_visibilityFullKm, 10.0f, 400.0f) * VisibilityScale;

    private static readonly StringName[] HazeParameterNameList =
    {
        "atmosphere_haze_model", "ring_haze_sigma_R", "ring_haze_H_R", "ring_haze_sigma_A", "ring_haze_omega_A", "ring_haze_H_A",
        "ring_haze_sigma_M0", "ring_haze_omega_M", "ring_haze_H_mist", "ring_haze_cap_height_meters", "ring_haze_g", "ring_haze_quality",
        "ring_haze_sun_tint",
    };
    /// <summary>Shader uniforms written by <see cref="PushHazeParameters"/> (for follower materials that mirror a surface material).</summary>
    public static System.Collections.Generic.IReadOnlyList<StringName> HazeParameterNames => HazeParameterNameList;

    /// <summary>Publishes the haze-model uniforms through any parameter setter (ShaderMaterial or plugin material).</summary>
    public static void PushHazeParameters(Action<StringName, Variant> setParameter)
    {
        setParameter(HazeParameterNameList[0], (int)_hazeModel);
        setParameter(HazeParameterNameList[1], HazeSigmaRayleigh);
        setParameter(HazeParameterNameList[2], HazeRayleighHeightMeters);
        setParameter(HazeParameterNameList[3], HazeSigmaAerosol);
        setParameter(HazeParameterNameList[4], HazeOmegaAerosol);
        setParameter(HazeParameterNameList[5], HazeAerosolHeightMeters);
        setParameter(HazeParameterNameList[6], HazeSigmaMist);
        setParameter(HazeParameterNameList[7], HazeOmegaMist);
        setParameter(HazeParameterNameList[8], HazeMistHeightMeters);
        setParameter(HazeParameterNameList[9], HazeCapHeightMeters);
        setParameter(HazeParameterNameList[10], HazeG);
        setParameter(HazeParameterNameList[11], HazeQuality);
        setParameter(HazeParameterNameList[12], HazeSunTint);
    }

    // ---- Physical haze lighting for CPU-built compute/mask profiles (P2b). Compute shaders cannot read Godot globals, so
    // day_side_atmosphere.gd hands the exact values it publishes as ringworld_haze_* globals to RingSunshineCloudBridge.SetHazeLighting,
    // which stores them here (same pattern as EclipseNightScale). Defaults match the project.godot global defaults, which are RingHazeSkyIrradiance outputs at the reference pose
    // (Natural preset, sun 60 deg, 300 m, giant light off, haze_brightness 1, albedo 0.1, gain 1): day = A(sun), night = haze_night_floor. ----
    public static Vector3 HazeSunIrradiance { get; set; } = new(12.566f, 12.566f, 12.566f);
    public static Vector3 HazeAmbientDay { get; set; } = new(0.2631f, 0.3464f, 0.4922f);
    public static Vector3 HazeAmbientNight { get; set; } = new(0.004f, 0.005f, 0.008f);

    public const int CloudProfileSlots = 16;

    /// <summary>Camera-local day weight, exactly ringworld_effective_solar_day(smoothstep(-0.2, 0.35, dot(-outward, to_sun))) of ring_haze_sample:
    /// max(geometric * solar_visibility, hub_day). outward and toSun must be in the same space.</summary>
    public static float LocalHazeDay(Vector3 outward, Vector3 toSun, float solarVisibility, float hubDay)
    {
        float x = Mathf.Clamp((-outward.Normalized().Dot(toSun.Normalized()) + 0.2f) / 0.55f, 0.0f, 1.0f);
        float geometric = x * x * (3.0f - 2.0f * x);
        return Mathf.Max(geometric * Mathf.Clamp(solarVisibility, 0.0f, 1.0f), Mathf.Clamp(hubDay, 0.0f, 1.0f));
    }

    /// <summary>
    /// Packs the complete 16-vec4 Sunshine / far-cloud-mask atmosphere profile (the single packer for the Sunshine bridge, the far cloud mask
    /// and the test helpers, so they cannot drift). Slots 0-9 keep their Legacy meaning; 10-15 carry the physical haze (layout documented at
    /// ring_haze_from_profile in materials/ring_haze_shared.gdshaderinc). Directions are in whatever space the consumer ray uses
    /// (world for Sunshine, the canonical mask basis for the far mask); outward and toSun must share it.
    /// Legacy day = geo * max(vis, hub) (slot 6.w, unchanged); physical day = max(geo * vis, hub) (slot 13.z, same as ring_haze_sample).
    /// </summary>
    public static void PackCloudProfile(
        Godot.Collections.Array<Vector4> profile, double preciseRadius, Vector3 outward, Vector3 axis, float cameraHeightMeters,
        float sidewallNegativeMeters, float sidewallPositiveMeters, Vector3 toSun, float solarVisibility, float hubDay,
        float opacityMultiplier = 1.0f)
    {
        if (profile.Count < CloudProfileSlots) profile.Resize(CloudProfileSlots);
        float legacySolarVisibility = Mathf.Max(solarVisibility, hubDay);
        profile[0] = new(1, (float)preciseRadius, FloorDepthMeters, BoundaryHeightMeters);
        profile[1] = new(UpperHeightMeters, BoundaryExtinctionMeters, UpperExtinctionMeters, TotalMaxOpacity);
        profile[2] = new(BoundaryMaxOpacity, UpperMaxOpacity, DistanceHazeMaxOpacity, ScatteringEnergy);
        profile[3] = new(ClearDistanceMeters, FullDistanceMeters, HighAltitudeClearDistanceMeters, HighAltitudeFullDistanceMeters);
        profile[4] = new(DistanceAltitudeStartMeters, DistanceAltitudeEndMeters, Aerosol, ForwardScattering);
        profile[5] = new(DayColor.R, DayColor.G, DayColor.B, SunWarmth);
        profile[6] = new(NightColor.R * EclipseNightScale, NightColor.G * EclipseNightScale, NightColor.B * EclipseNightScale, legacySolarVisibility);
        profile[7] = new(outward.X, outward.Y, outward.Z, cameraHeightMeters);
        profile[8] = new(axis.X, axis.Y, axis.Z, sidewallNegativeMeters);
        profile[9] = new(toSun.X, toSun.Y, toSun.Z, sidewallPositiveMeters);
        Vector3 sun = HazeSunIrradiance * HazeSunTint;
        Vector3 sigmaR = HazeSigmaRayleigh;
        profile[10] = new(_hazeModel == HazeModelKind.Physical ? 1.0f : 0.0f, opacityMultiplier, sigmaR.X, sigmaR.Y);
        profile[11] = new(sigmaR.Z, HazeRayleighHeightMeters, HazeSigmaAerosol, HazeOmegaAerosol);
        profile[12] = new(HazeAerosolHeightMeters, HazeSigmaMist, HazeOmegaMist, HazeMistHeightMeters);
        profile[13] = new(HazeG, HazeQuality, LocalHazeDay(outward, toSun, solarVisibility, hubDay), sun.X);
        profile[14] = new(sun.Y, sun.Z, HazeAmbientDay.X, HazeAmbientDay.Y);
        profile[15] = new(HazeAmbientDay.Z, HazeAmbientNight.X, HazeAmbientNight.Y, HazeAmbientNight.Z);
    }

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
        ApplyPhysicalProfile();
    }

    /// <summary>
    /// Maps the persisted art controls onto the physical haze parameters. The legacy shell/opacity values above are untouched and
    /// still drive the Legacy model; shell geometry and the cap height stay with the controller exports.
    ///   visibility (km)  -> Koschmieder visibility V = 1.5 * km (Natural 120 -> 180 km); sigma_A = max(3.912 / V - sigma_R.g - sigma_M0, 0).
    ///   density          -> multiplies every extinction (Rayleigh, aerosol, mist); 0 is the identity, 1 the nominal air.
    ///   aerosol (0..1)   -> ground-mist load, sigma_M0 = 4e-6 * aerosol / 0.35; sigma_A re-balances so V is unchanged.
    ///   altitude falloff -> scale heights of aerosol and mist, H = H0 * 2^(2 * (0.5 - falloff)) (0 -> x2 taller, 1 -> x0.5),
    ///                       matching the legacy sense (higher falloff = haze thins faster with altitude).
    ///   P4 controls      -> mist density multiplies sigma_M0; mist/aerosol height replace H0 (the falloff factor still multiplies them, so
    ///                       the falloff slider keeps its relative sense); Rayleigh strength multiplies sigma_R. Defaults are the identity.
    ///   forward scatter  -> Mie g = 0.6 + (fs - 0.22) * 0.4545 (Natural 0.22 -> 0.60, Cinematic 0.55 -> 0.75).
    ///   sun warmth       -> tint on the haze sun colour: neutral at 0.25, towards (1, 0.78, 0.55) at 1, towards a cool (0.9, 0.97, 1.08) at 0.
    /// </summary>
    private static void ApplyPhysicalProfile()
    {
        float density = Mathf.Clamp(_density, 0.0f, 2.0f);
        float sigmaMist = BaseSigmaMist * Mathf.Clamp(_aerosol, 0.0f, 1.0f) / AerosolControlDefault;
        // The Koschmieder balance uses the nominal mist and Rayleigh; the P4 mist/Rayleigh multipliers are applied on top (visibility shifts).
        float sigmaAerosol = Mathf.Max(3.912f / (HazeVisibilityKm * 1000.0f) - BaseSigmaRayleigh.Y - sigmaMist, 0.0f);
        float heightScale = Mathf.Pow(2.0f, 2.0f * (0.5f - Mathf.Clamp(_altitudeFalloff, 0.0f, 1.0f)));
        HazeSigmaRayleigh = BaseSigmaRayleigh * (density * HazeRayleighStrength);
        HazeSigmaAerosol = sigmaAerosol * density;
        HazeSigmaMist = sigmaMist * density * HazeMistDensityScale;
        HazeRayleighHeightMeters = BaseRayleighHeightMeters;
        HazeAerosolHeightMeters = HazeAerosolBaseHeightMeters * heightScale;
        HazeMistHeightMeters = HazeMistBaseHeightMeters * heightScale;
        HazeOmegaAerosol = BaseOmegaAerosol;
        HazeOmegaMist = BaseOmegaMist;
        HazeG = Mathf.Clamp(BaseMieG + (_forwardScattering - ForwardControlDefault) * 0.4545f, 0.0f, 0.95f);
        float warmth = Mathf.Clamp(_sunWarmth, 0.0f, 1.0f);
        HazeSunTint = warmth >= WarmthControlDefault
            ? Vector3.One.Lerp(new Vector3(1.0f, 0.78f, 0.55f), (warmth - WarmthControlDefault) / (1.0f - WarmthControlDefault))
            : Vector3.One.Lerp(new Vector3(0.9f, 0.97f, 1.08f), (WarmthControlDefault - warmth) / WarmthControlDefault);
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
        // Physical-model uniforms are in physical units at every coordinate scale (the shader scales positions by the ray frame's
        // coordinate scale), so the far viewport (1/1000) pushes the same values as the main viewport.
        PushHazeParameters(material.SetShaderParameter);
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
        // Physical parameter mapping: Koschmieder closure (when the aerosol term is not clamped away), zero density is the identity,
        // everything finite and positive, and the model switch round-trips.
        float density = Mathf.Clamp(_density, 0.0f, 2.0f);
        float greenExtinction = HazeSigmaRayleigh.Y + HazeSigmaAerosol + HazeSigmaMist;
        bool nominalControls = Mathf.IsEqualApprox(HazeRayleighStrength, 1.0f) && Mathf.IsEqualApprox(HazeMistDensityScale, 1.0f);
        float koschmieder = density > 0.0f ? 3.912f / (HazeVisibilityKm * 1000.0f) * density : 0.0f;
        bool aerosolClamped = HazeSigmaAerosol <= 0.0f;
        bool physicalVisibility = !nominalControls || (aerosolClamped
            ? greenExtinction >= koschmieder - 1e-9f
            : Mathf.Abs(greenExtinction - koschmieder) <= koschmieder * 0.001f + 1e-12f);
        bool physicalZeroDensity = density > 0.0f || (HazeSigmaRayleigh.LengthSquared() <= 0.0f && HazeSigmaAerosol <= 0.0f && HazeSigmaMist <= 0.0f);
        bool physicalFinite = float.IsFinite(HazeSigmaAerosol) && HazeSigmaAerosol >= 0.0f && HazeSigmaMist >= 0.0f
            && HazeAerosolHeightMeters > 0.0f && HazeMistHeightMeters > 0.0f && HazeG >= 0.0f && HazeG <= 0.95f
            && HazeSunTint.X > 0.0f && HazeSunTint.Y > 0.0f && HazeSunTint.Z > 0.0f;
        HazeModelKind originalModel = _hazeModel;
        ulong originalRevision = Revision;
        HazeModel = originalModel == HazeModelKind.Legacy ? HazeModelKind.Physical : HazeModelKind.Legacy;
        bool modelSwitch = _hazeModel != originalModel && Revision == originalRevision + 1;
        HazeModel = originalModel;
        bool physicalPassed = physicalVisibility && physicalZeroDensity && physicalFinite && modelSwitch && _hazeModel == originalModel;
        passed = passed && physicalPassed;
        summary = $"profileTransfer enabled={Enabled}, zeroDensity={zeroDensity}, surface(near={opacityNearDistance:0.000}, clear={opacityClearDistance:0.000}, mid={opacityMidDistance:0.000}, full={opacityFullDistance:0.000}), "
            + $"mountain(clear={mountainClearDistance:0.000}, full={mountainFullDistance:0.000}), "
            + "explicit_connected_lengths, "
            + $"physical(model={_hazeModel}, V={HazeVisibilityKm:0.#}km, sigmaGreen={greenExtinction * 1e6f:0.000}e-6, koschmieder={koschmieder * 1e6f:0.000}e-6, "
            + $"sigmaA={HazeSigmaAerosol * 1e6f:0.000}e-6, sigmaM={HazeSigmaMist * 1e6f:0.000}e-6, g={HazeG:0.000}, ok={physicalPassed})";
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
