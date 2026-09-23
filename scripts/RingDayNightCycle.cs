using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Authoritative absolute-time facade for the astronomical simulation. It owns
/// direction, phase and timing state; the atmosphere remains the lighting writer.
/// </summary>
public partial class RingDayNightCycle : Node
{
    private const float DefaultDaySeconds = (float)CelestialConfiguration.DefaultRingDaySeconds;
    private const float DefaultGiantRadiusKm = GasGiantConfiguration.DefaultRadiusKm;
    private const float DefaultGiantDistanceKm = GasGiantConfiguration.DefaultDistanceKm;
    private const double DefaultGiantOrbitSeconds = GasGiantConfiguration.DefaultOrbitSeconds;
    private const float DefaultGrazingMissDegrees = GasGiantConfiguration.DefaultGrazingMissDegrees;
    private const float DefaultStarAngularRadiusDegrees = .265f;
    private const float DefaultInitialGiantPhaseDegrees = 180f;
    private const double GiantEventRefreshIntervalSeconds = .5d;

    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath RingAxisSourcePath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public CelestialConfiguration? Configuration { get; set; }

    // Compatibility facade retained for existing scenes, saves and capture tools.
    [Export(PropertyHint.Range, "60,7200,1")] public float FullCycleSeconds { get; set; } = DefaultDaySeconds;
    [Export(PropertyHint.Range, "1,100,1")]
    public float TimeScale
    {
        get => _timeScale;
        set
        {
            _timeScale = float.IsFinite(value) ? Mathf.Clamp(value, 1f, 100f) : 1f;
            ProjectSettings.SetSetting("ringworld/runtime/day_night_speed", _timeScale);
        }
    }
    [Export(PropertyHint.Range, "0,1,0.001")] public float InitialCycleFraction { get; set; }
    [Export] public Vector3 RingAxis { get; set; } = Vector3.Up;
    [Export] public double InitialSimulationSeconds { get; set; }
    [Export] public bool Paused { get; set; }

    [ExportGroup("Rendered Sun Shadow Stabilization")]
    /// <summary>
    /// Holds the rendered directional-light basis between small celestial steps.
    /// This also holds direct-light direction because Godot couples it to shadows;
    /// it is a shimmer mitigation, not independent shadow caching or a GPU guarantee.
    /// </summary>
    [Export]
    public bool SunShadowStabilizationEnabled
    {
        get => _sunShadowStabilizationEnabled;
        set
        {
            if (_sunShadowStabilizationEnabled == value)
                return;
            _sunShadowStabilizationEnabled = value;
            _forceSunBasisUpdate = true;
        }
    }

    [Export(PropertyHint.Range, "0,0.05,0.0001")]
    public float SunShadowStepDegrees
    {
        get => _sunShadowStepDegrees;
        set
        {
            float sanitized = float.IsFinite(value) ? Mathf.Clamp(value, 0f, .05f) : 0f;
            // Allow float round-trip noise without treating a tiny positive step
            // as zero: zero must always switch back to continuous updates.
            float tolerance = Mathf.Max(_sunShadowStepDegrees, sanitized) * 1e-6f;
            if (Mathf.Abs(_sunShadowStepDegrees - sanitized) <= tolerance)
                return;
            _sunShadowStepDegrees = sanitized;
            _forceSunBasisUpdate = true;
        }
    }

    [Export] public float EffectiveSunShadowStepDegrees { get; private set; }
    [Export] public float SunShadowAngularLagDegrees { get; private set; }

    [ExportGroup("Gas Giant Compatibility")]
    [Export] public bool GiantEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "10000,200000,100")] public float GiantRadiusKm { get; set; } = DefaultGiantRadiusKm;
    [Export(PropertyHint.Range, "100000,5000000,1000")] public float GiantDistanceKm { get; set; } = DefaultGiantDistanceKm;
    [Export(PropertyHint.Range, "60,2000000,1")] public float GiantOrbitSeconds { get; set; } = (float)DefaultGiantOrbitSeconds;
    [Export(PropertyHint.Range, "-89.9,89.9,0.001")] public float GiantGrazingMissDegrees { get; set; } = DefaultGrazingMissDegrees;
    [Export(PropertyHint.Range, "0.05,2,0.001")] public float StarAngularRadiusDegrees { get; set; } = DefaultStarAngularRadiusDegrees;
    [Export(PropertyHint.Range, "0,360,0.001")] public float InitialGiantOrbitalPhaseDegrees { get; set; } = DefaultInitialGiantPhaseDegrees;

    [ExportGroup("Celestial Outputs")]
    [Export] public Vector3 ToSun { get; private set; } = Vector3.Forward;
    [Export] public Vector3 GiantDirection { get; private set; } = Vector3.Zero;
    [Export] public float GiantAngularRadiusDegrees { get; private set; }
    [Export] public float GiantAngularDiameterDegrees { get; private set; }
    [Export] public float GiantEclipseCoverage { get; private set; }
    [Export] public float RawEclipseCoverage { get; private set; }
    [Export] public float GameplayEclipseDarkness { get; private set; }
    [Export] public CelestialEclipseType EclipseType { get; private set; }
    [Export] public CelestialLightingPhase LightingPhase { get; private set; }
    [Export] public string LightingPhaseName { get; private set; } = nameof(CelestialLightingPhase.Night);
    [Export] public string EclipseTypeName { get; private set; } = nameof(CelestialEclipseType.None);
    [Export] public float SunElevationDegrees { get; private set; }
    [Export] public float GiantElevationDegrees { get; private set; }
    [Export] public float DaylightFactor { get; private set; }
    [Export] public float TwilightFactor { get; private set; }
    [Export] public float NightFactor { get; private set; } = 1f;
    [Export] public float GiantLightingPhase { get; private set; }
    [Export] public bool SunAboveHorizon { get; private set; }
    [Export] public bool GiantAboveHorizon { get; private set; }
    [Export] public double RingRotationAngleDegrees { get; private set; }
    [Export] public double OrbitalAngleDegrees { get; private set; }
    [Export] public double SeasonAngleDegrees { get; private set; }
    [Export] public double SecondsUntilSunrise { get; private set; } = double.PositiveInfinity;
    [Export] public double SecondsUntilSunset { get; private set; } = double.PositiveInfinity;
    [Export] public double SecondsUntilGiantRise { get; private set; } = double.PositiveInfinity;
    [Export] public double SecondsUntilGiantSet { get; private set; } = double.PositiveInfinity;
    [Export] public float DaylightStartDot { get; private set; } = -.2f;
    [Export] public float DaylightFullDot { get; private set; } = .35f;
    [Export] public int DeterministicVisualSeed { get; private set; }

    public CelestialFrame CurrentFrame { get; private set; }

    private DirectionalLight3D? _sunLight;
    private Node3D? _ringAxisSource;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private Basis _authoredSunBasis = Basis.Identity;
    private CelestialConfiguration? _effectiveConfiguration;
    private float _timeScale = 1f;
    private bool _sunShadowStabilizationEnabled = true;
    private float _sunShadowStepDegrees = .01f;
    private bool _forceSunBasisUpdate = true;
    private float _cycleFraction;
    private double _simulationSeconds;
    private bool _capturedAuthoredSun;
    private double _giantEventRefreshElapsed = double.PositiveInfinity;
    private double _cachedNextGiantRiseSeconds = double.PositiveInfinity;
    private double _cachedNextGiantSetSeconds = double.PositiveInfinity;

    public override void _Ready()
    {
        ProcessPriority = -100;
        ResolveNodes();
        PrepareEffectiveConfiguration();
        _simulationSeconds = SanitizeSimulationSeconds(InitialSimulationSeconds);
        if (_simulationSeconds == 0d)
            _simulationSeconds = PositiveModulo(InitialCycleFraction, 1f) * Math.Max(1f, FullCycleSeconds);
        SynchronizeCycleFraction();
        TimeScale = ProjectSettings.GetSetting(
            "ringworld/runtime/day_night_speed",
            TimeScale).AsSingle();
        ResolveDeterministicVisualSeed();
        UpdateFrameAndLight(forceGiantEvents: true, forceSunBasis: true);
        if (Array.IndexOf(OS.GetCmdlineUserArgs(), "--celestial-eclipse-smoke-test") >= 0)
            RunEclipseSmokeTest();
    }

    public override void _Process(double delta)
    {
        ResolveNodes();
        ResolveDeterministicVisualSeed();
        double frameDelta = double.IsFinite(delta) ? Math.Max(0d, delta) : 0d;
        _giantEventRefreshElapsed += frameDelta;
        if (!Paused)
            _simulationSeconds += frameDelta * TimeScale;
        SynchronizeCycleFraction();
        UpdateFrameAndLight();
    }

    public void SetSpeedMultiplier(float multiplier) => TimeScale = multiplier;
    public float GetSpeedMultiplier() => TimeScale;

    public void SetCycleFraction(float fraction)
    {
        float daySeconds = Math.Max(1f, FullCycleSeconds);
        _cycleFraction = PositiveModulo(fraction, 1f);
        _simulationSeconds = Math.Floor(_simulationSeconds / daySeconds) * daySeconds
            + _cycleFraction * daySeconds;
        UpdateFrameAndLight(forceGiantEvents: true, forceSunBasis: true);
    }

    public float GetCycleTimeSeconds() => _cycleFraction * FullCycleSeconds;
    public double GetSimulationSeconds() => _simulationSeconds;

    public void SetSimulationSeconds(double seconds)
    {
        _simulationSeconds = SanitizeSimulationSeconds(seconds);
        SynchronizeCycleFraction();
        UpdateFrameAndLight(forceGiantEvents: true, forceSunBasis: true);
    }

    public void EvaluateGiant(Vector3 toSun, Vector3 axis) =>
        UpdateFrameAndLight(forceGiantEvents: true);
    // Retains the established public API name while validating both the default
    // total-eclipse preset and the explicit partial compatibility fixture.
    public bool CelestialEclipseSelfTest() => CelestialEclipseMath.DefaultGrazingSelfTest();

    /// <summary>
    /// Evaluates a future or past celestial frame for the current observer without
    /// changing simulation time, exported outputs, light transforms, or event caches.
    /// Intended for inspection tools that must preview naturally occurring events.
    /// </summary>
    public CelestialFrame EvaluateAtTime(double seconds)
    {
        CelestialConfiguration config = _effectiveConfiguration
            ?? Configuration
            ?? new CelestialConfiguration();
        Vector3 axis = _ringAxisSource != null
            ? SafeNormalized(_ringAxisSource.GlobalBasis.Y, Vector3.Up)
            : SafeNormalized(RingAxis, Vector3.Up);
        Vector3 surfaceUp = -Vector3.Right;
        if (_explorer != null)
        {
            surfaceUp = _explorer.GetCanonicalSurfaceUp();
            if (_grid != null)
                surfaceUp = _grid.GlobalBasis.Orthonormalized() * surfaceUp;
            surfaceUp = SafeNormalized(surfaceUp, -Vector3.Right);
        }
        Vector3 epochSun = SafeNormalized(_authoredSunBasis.Z, Vector3.Forward);
        return CelestialEclipseMath.EvaluateFrame(
            SanitizeSimulationSeconds(seconds),
            epochSun,
            surfaceUp,
            axis,
            config,
            ResolveObserverOffsetKm(),
            calculateGiantEvents: false);
    }

    // Explicit snake-case methods preserve the former GDScript controller API.
    public void set_speed_multiplier(float multiplier) => SetSpeedMultiplier(multiplier);
    public float get_speed_multiplier() => GetSpeedMultiplier();
    public void set_cycle_fraction(float fraction) => SetCycleFraction(fraction);
    public float get_cycle_time_seconds() => GetCycleTimeSeconds();
    public double get_simulation_seconds() => GetSimulationSeconds();
    public void set_simulation_seconds(double seconds) => SetSimulationSeconds(seconds);
    public void evaluate_giant(Vector3 toSun, Vector3 axis) => EvaluateGiant(toSun, axis);
    public bool celestial_eclipse_self_test() => CelestialEclipseSelfTest();
    public float get_daylight_factor() => DaylightFactor;
    public float get_twilight_factor() => TwilightFactor;
    public float get_night_factor() => NightFactor;
    public float get_eclipse_factor() => RawEclipseCoverage;
    public float get_gameplay_eclipse_darkness() => GameplayEclipseDarkness;
    public Vector3 get_to_sun() => ToSun;
    public Vector3 get_giant_direction() => GiantDirection;
    public double get_seconds_until_sunrise() => SecondsUntilSunrise;
    public double get_seconds_until_sunset() => SecondsUntilSunset;
    public double get_seconds_until_giant_rise() => SecondsUntilGiantRise;
    public double get_seconds_until_giant_set() => SecondsUntilGiantSet;

    private void PrepareEffectiveConfiguration()
    {
        CelestialConfiguration source = Configuration
            ?? ResourceLoader.Load<CelestialConfiguration>(
                "res://resources/celestial/celestial_default.tres")
            ?? new CelestialConfiguration();
        GasGiantConfiguration sourceGiant = source.GasGiant ?? new GasGiantConfiguration();
        var effectiveGiant = new GasGiantConfiguration
        {
            Enabled = sourceGiant.Enabled,
            RadiusKm = sourceGiant.RadiusKm,
            DistanceKm = sourceGiant.DistanceKm,
            OrbitSeconds = sourceGiant.OrbitSeconds,
            GrazingMissDegrees = sourceGiant.GrazingMissDegrees,
            InitialPhaseDegrees = sourceGiant.InitialPhaseDegrees,
            SeasonMissAmplitudeDegrees = sourceGiant.SeasonMissAmplitudeDegrees,
            SeasonSeconds = sourceGiant.SeasonSeconds,
            SeasonPhaseDegrees = sourceGiant.SeasonPhaseDegrees,
            MassKg = sourceGiant.MassKg,
            VisualSeedOffset = sourceGiant.VisualSeedOffset,
            AxialTiltDegrees = sourceGiant.AxialTiltDegrees,
            RotationPeriodSeconds = sourceGiant.RotationPeriodSeconds
        };
        var effective = new CelestialConfiguration
        {
            RingDaySeconds = source.RingDaySeconds,
            RingTiltDegrees = source.RingTiltDegrees,
            NominalRingRadiusKm = source.NominalRingRadiusKm,
            StarAngularRadiusDegrees = source.StarAngularRadiusDegrees,
            GameplayEclipseDarknessCap = source.GameplayEclipseDarknessCap,
            DaylightStartDot = source.DaylightStartDot,
            DaylightFullDot = source.DaylightFullDot,
            AstronomicalTwilightElevationDegrees = source.AstronomicalTwilightElevationDegrees,
            HorizonEventElevationDegrees = source.HorizonEventElevationDegrees,
            SunriseBandDegrees = source.SunriseBandDegrees,
            VisualSeed = source.VisualSeed,
            GasGiant = effectiveGiant
        };

        ApplyLegacyFacade(effective, effectiveGiant);
        SanitizeConfiguration(effective, effectiveGiant);
        _effectiveConfiguration = effective;
        Configuration = effective;
        DaylightStartDot = effective.DaylightStartDot;
        DaylightFullDot = effective.DaylightFullDot;
    }

    private void ApplyLegacyFacade(
        CelestialConfiguration effective,
        GasGiantConfiguration giant)
    {
        if (!Mathf.IsEqualApprox(FullCycleSeconds, DefaultDaySeconds))
            effective.RingDaySeconds = FullCycleSeconds;
        else
            FullCycleSeconds = (float)effective.RingDaySeconds;

        if (!Mathf.IsEqualApprox(StarAngularRadiusDegrees, DefaultStarAngularRadiusDegrees))
            effective.StarAngularRadiusDegrees = StarAngularRadiusDegrees;
        else
            StarAngularRadiusDegrees = effective.StarAngularRadiusDegrees;

        if (GiantEnabled != true) giant.Enabled = GiantEnabled;
        else GiantEnabled = giant.Enabled;
        if (!Mathf.IsEqualApprox(GiantRadiusKm, DefaultGiantRadiusKm)) giant.RadiusKm = GiantRadiusKm;
        else GiantRadiusKm = giant.RadiusKm;
        if (!Mathf.IsEqualApprox(GiantDistanceKm, DefaultGiantDistanceKm)) giant.DistanceKm = GiantDistanceKm;
        else GiantDistanceKm = giant.DistanceKm;
        if (!Mathf.IsEqualApprox(GiantOrbitSeconds, (float)DefaultGiantOrbitSeconds)) giant.OrbitSeconds = GiantOrbitSeconds;
        else GiantOrbitSeconds = (float)giant.OrbitSeconds;
        if (!Mathf.IsEqualApprox(GiantGrazingMissDegrees, DefaultGrazingMissDegrees)) giant.GrazingMissDegrees = GiantGrazingMissDegrees;
        else GiantGrazingMissDegrees = giant.GrazingMissDegrees;
        if (!Mathf.IsEqualApprox(InitialGiantOrbitalPhaseDegrees, DefaultInitialGiantPhaseDegrees))
            giant.InitialPhaseDegrees = InitialGiantOrbitalPhaseDegrees;
        else
            InitialGiantOrbitalPhaseDegrees = giant.InitialPhaseDegrees;
    }

    private void SanitizeConfiguration(
        CelestialConfiguration configuration,
        GasGiantConfiguration giant)
    {
        configuration.RingDaySeconds = PositiveFiniteOr(
            configuration.RingDaySeconds,
            DefaultDaySeconds,
            1d);
        configuration.RingTiltDegrees = ClampedFiniteOr(
            configuration.RingTiltDegrees, 15f, -89.9f, 89.9f);
        configuration.NominalRingRadiusKm = PositiveFiniteOr(
            configuration.NominalRingRadiusKm, 5000f, .001f);
        configuration.StarAngularRadiusDegrees = PositiveFiniteOr(
            configuration.StarAngularRadiusDegrees,
            DefaultStarAngularRadiusDegrees,
            0f);
        configuration.GameplayEclipseDarknessCap = ClampedFiniteOr(
            configuration.GameplayEclipseDarknessCap, .95f, 0f, 1f);
        configuration.DaylightStartDot = ClampedFiniteOr(
            configuration.DaylightStartDot, -.2f, -1f, .9999f);
        configuration.DaylightFullDot = Mathf.Clamp(
            MathF.Max(
                configuration.DaylightStartDot + .0001f,
                FiniteOr(configuration.DaylightFullDot, .35f)),
            -1f,
            1f);
        configuration.AstronomicalTwilightElevationDegrees = ClampedFiniteOr(
            configuration.AstronomicalTwilightElevationDegrees, -18f, -89.9f, 0f);
        configuration.HorizonEventElevationDegrees = ClampedFiniteOr(
            configuration.HorizonEventElevationDegrees, -.833f, -89.9f, 89.9f);
        configuration.SunriseBandDegrees = PositiveFiniteOr(
            configuration.SunriseBandDegrees, 1.5f, .1f);

        giant.RadiusKm = PositiveFiniteOr(giant.RadiusKm, DefaultGiantRadiusKm, 0f);
        giant.DistanceKm = MathF.Max(
            giant.RadiusKm + .001f,
            PositiveFiniteOr(giant.DistanceKm, DefaultGiantDistanceKm, .001f));
        giant.OrbitSeconds = PositiveFiniteOr(
            giant.OrbitSeconds,
            DefaultGiantOrbitSeconds,
            1d);
        giant.GrazingMissDegrees = ClampedFiniteOr(
            giant.GrazingMissDegrees, DefaultGrazingMissDegrees, -89.9f, 89.9f);
        giant.InitialPhaseDegrees = FiniteOr(
            giant.InitialPhaseDegrees,
            DefaultInitialGiantPhaseDegrees);
        giant.SeasonMissAmplitudeDegrees = ClampedFiniteOr(
            giant.SeasonMissAmplitudeDegrees, 0f, -89.9f, 89.9f);
        giant.SeasonSeconds = PositiveFiniteOr(giant.SeasonSeconds, 0d, 0d);
        giant.SeasonPhaseDegrees = FiniteOr(giant.SeasonPhaseDegrees, 0f);
        giant.MassKg = PositiveFiniteOr(giant.MassKg, 1.898e27d, 0d);
        giant.AxialTiltDegrees = ClampedFiniteOr(
            giant.AxialTiltDegrees, 3.13f, -180f, 180f);
        giant.RotationPeriodSeconds = PositiveFiniteOr(
            giant.RotationPeriodSeconds, 35730d, 1d);

        FullCycleSeconds = (float)configuration.RingDaySeconds;
        StarAngularRadiusDegrees = configuration.StarAngularRadiusDegrees;
        GiantEnabled = giant.Enabled;
        GiantRadiusKm = giant.RadiusKm;
        GiantDistanceKm = giant.DistanceKm;
        GiantOrbitSeconds = (float)giant.OrbitSeconds;
        GiantGrazingMissDegrees = giant.GrazingMissDegrees;
        InitialGiantOrbitalPhaseDegrees = giant.InitialPhaseDegrees;
    }

    private void UpdateFrameAndLight(bool forceGiantEvents = false, bool forceSunBasis = false)
    {
        CelestialConfiguration config = _effectiveConfiguration
            ?? Configuration
            ?? new CelestialConfiguration();
        Vector3 axis = _ringAxisSource != null
            ? SafeNormalized(_ringAxisSource.GlobalBasis.Y, Vector3.Up)
            : SafeNormalized(RingAxis, Vector3.Up);
        Vector3 epochSun = SafeNormalized(_authoredSunBasis.Z, Vector3.Forward);
        Vector3 surfaceUp = -Vector3.Right;
        if (_explorer != null)
        {
            surfaceUp = _explorer.GetCanonicalSurfaceUp();
            if (_grid != null)
                surfaceUp = _grid.GlobalBasis.Orthonormalized() * surfaceUp;
            surfaceUp = SafeNormalized(surfaceUp, -Vector3.Right);
        }

        bool giantEnabled = config.GasGiant?.Enabled == true;
        bool cachedEventExpired = EventHasArrived(_cachedNextGiantRiseSeconds, _simulationSeconds)
            || EventHasArrived(_cachedNextGiantSetSeconds, _simulationSeconds);
        bool calculateGiantEvents = giantEnabled
            && (forceGiantEvents
                || _giantEventRefreshElapsed >= GiantEventRefreshIntervalSeconds
                || cachedEventExpired);

        CurrentFrame = CelestialEclipseMath.EvaluateFrame(
            _simulationSeconds,
            epochSun,
            surfaceUp,
            axis,
            config,
            ResolveObserverOffsetKm(),
            calculateGiantEvents);
        if (calculateGiantEvents)
        {
            _cachedNextGiantRiseSeconds = CurrentFrame.NextGiantRiseSeconds;
            _cachedNextGiantSetSeconds = CurrentFrame.NextGiantSetSeconds;
            _giantEventRefreshElapsed = 0d;
        }
        else if (!giantEnabled)
        {
            _cachedNextGiantRiseSeconds = double.PositiveInfinity;
            _cachedNextGiantSetSeconds = double.PositiveInfinity;
        }

        CurrentFrame = CurrentFrame.WithGiantEventDurations(
            RemainingEventDuration(_cachedNextGiantRiseSeconds, _simulationSeconds),
            RemainingEventDuration(_cachedNextGiantSetSeconds, _simulationSeconds));
        CopyFrameOutputs();
        ApplySunDirection(axis, forceSunBasis);
    }

    private Vector3 ResolveObserverOffsetKm()
    {
        if (_explorer == null || _grid == null)
            return Vector3.Zero;
        var position = _explorer.CanonicalPosition;
        double radiusMeters = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        double angle = position.AroundMeters / Math.Max(1d, radiusMeters);
        double radialMeters = radiusMeters
            - (_grid.RingVerticalBaseOffsetMeters + position.VerticalMeters);
        double axialMeters = _grid.RingAcrossBaseOffsetMeters + position.AcrossMeters;
        var localMeters = new Vector3(
            (float)(Math.Cos(angle) * radialMeters),
            (float)axialMeters,
            (float)(Math.Sin(angle) * radialMeters));
        return _grid.GlobalBasis.Orthonormalized() * localMeters / 1000f;
    }

    private void CopyFrameOutputs()
    {
        ToSun = CurrentFrame.ToSun;
        GiantDirection = CurrentFrame.ToGiant;
        GiantAngularRadiusDegrees = CurrentFrame.GiantAngularRadiusDegrees;
        GiantAngularDiameterDegrees = CurrentFrame.GiantAngularDiameterDegrees;
        RawEclipseCoverage = CurrentFrame.RawEclipseCoverage;
        GiantEclipseCoverage = RawEclipseCoverage;
        GameplayEclipseDarkness = CurrentFrame.GameplayEclipseDarkness;
        if (EclipseType != CurrentFrame.EclipseType)
        {
            EclipseType = CurrentFrame.EclipseType;
            EclipseTypeName = EclipseType.ToString();
        }
        if (LightingPhase != CurrentFrame.LightingPhase)
        {
            LightingPhase = CurrentFrame.LightingPhase;
            LightingPhaseName = LightingPhase.ToString();
        }
        SunElevationDegrees = CurrentFrame.SunElevationDegrees;
        GiantElevationDegrees = CurrentFrame.GiantElevationDegrees;
        DaylightFactor = CurrentFrame.DaylightFactor;
        TwilightFactor = CurrentFrame.TwilightFactor;
        NightFactor = CurrentFrame.NightFactor;
        GiantLightingPhase = CurrentFrame.GiantLightingPhase;
        SunAboveHorizon = CurrentFrame.SunAboveHorizon;
        GiantAboveHorizon = CurrentFrame.GiantAboveHorizon;
        RingRotationAngleDegrees = CurrentFrame.RingRotationAngleDegrees;
        OrbitalAngleDegrees = CurrentFrame.OrbitalAngleDegrees;
        SeasonAngleDegrees = CurrentFrame.SeasonAngleDegrees;
        SecondsUntilSunrise = CurrentFrame.SecondsUntilSunrise;
        SecondsUntilSunset = CurrentFrame.SecondsUntilSunset;
        SecondsUntilGiantRise = CurrentFrame.SecondsUntilGiantRise;
        SecondsUntilGiantSet = CurrentFrame.SecondsUntilGiantSet;
    }

    private void ApplySunDirection(Vector3 ringAxis, bool force = false)
    {
        if (_sunLight == null)
            return;
        Vector3 z = SafeNormalized(CurrentFrame.ToSun, _authoredSunBasis.Z);
        Vector3 x = ringAxis.Cross(z);
        if (x.LengthSquared() <= .000001f)
        {
            x = _authoredSunBasis.X - z * _authoredSunBasis.X.Dot(z);
            if (x.LengthSquared() <= .000001f)
                x = Vector3.Right - z * Vector3.Right.Dot(z);
        }
        x = SafeNormalized(x, Vector3.Right);
        Vector3 y = SafeNormalized(z.Cross(x), Vector3.Up);
        Basis desired = new Basis(x, y, z).Orthonormalized();
        Basis current = _sunLight.GlobalBasis.Orthonormalized();
        float angularLagDegrees = BasisAngularDifferenceDegrees(current, desired);
        SunShadowAngularLagDegrees = angularLagDegrees;

        float configuredStep = SunShadowStabilizationEnabled ? SunShadowStepDegrees : 0f;
        // Long shadows magnify angular steps. Tighten continuously near the horizon
        // while retaining the configured maximum at high solar elevation.
        float horizonTightening = Mathf.Sin(Mathf.DegToRad(SunElevationDegrees));
        horizonTightening *= horizonTightening;
        EffectiveSunShadowStepDegrees = configuredStep * horizonTightening;

        float squaredDifference = BasisFrobeniusSquaredDifference(current, desired);
        if (squaredDifference <= 1e-14f)
        {
            _forceSunBasisUpdate = false;
            return;
        }
        bool shouldWrite = force || _forceSunBasisUpdate
            || !SunShadowStabilizationEnabled || SunShadowStepDegrees <= 0f
            || squaredDifference >= BasisDifferenceThresholdSquared(EffectiveSunShadowStepDegrees);
        if (!shouldWrite)
            return;

        _sunLight.GlobalBasis = desired;
        SunShadowAngularLagDegrees = 0f;
        _forceSunBasisUpdate = false;
    }

    private void ResolveNodes()
    {
        if (_sunLight == null || !GodotObject.IsInstanceValid(_sunLight))
        {
            _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
            _forceSunBasisUpdate = true;
            if (_sunLight != null && !_capturedAuthoredSun)
            {
                _authoredSunBasis = _sunLight.GlobalBasis.Orthonormalized();
                _capturedAuthoredSun = true;
            }
        }
        if (_ringAxisSource == null || !GodotObject.IsInstanceValid(_ringAxisSource))
            _ringAxisSource = GetNodeOrNull<Node3D>(RingAxisSourcePath);
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer))
            _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        if (_grid == null || !GodotObject.IsInstanceValid(_grid))
            _grid = GetNodeOrNull<VoxelChunkGridManager>(RingAxisSourcePath);
    }

    private void ResolveDeterministicVisualSeed()
    {
        if (_effectiveConfiguration == null)
            return;
        if (_effectiveConfiguration.VisualSeed != 0)
        {
            DeterministicVisualSeed = _effectiveConfiguration.VisualSeed;
            return;
        }
        if (_grid?.TerrainSampler == null)
            return;
        uint value = unchecked((uint)_grid.TerrainSampler.Seed)
            ^ unchecked((uint)(_effectiveConfiguration.GasGiant?.VisualSeedOffset ?? 0))
            ^ 0x9E3779B9u;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        value ^= value >> 16;
        DeterministicVisualSeed = (int)(value & 0x7FFFFFFFu);
        if (DeterministicVisualSeed == 0)
            DeterministicVisualSeed = 1;
    }

    private void SynchronizeCycleFraction()
    {
        _cycleFraction = (float)PositiveModulo(
            _simulationSeconds / Math.Max(1f, FullCycleSeconds),
            1d);
    }

    private void RunEclipseSmokeTest()
    {
        if (CelestialEclipseSelfTest())
            GD.Print("CELESTIAL_ECLIPSE_SMOKE_TEST=PASS");
        else
            GD.PushError("Celestial eclipse smoke test failed.");
    }

    // For orthonormal bases, ||A-B||_F² = 8 sin²(theta/2). This preserves
    // sub-millidegree precision that float acos(dot), trace, and quaternion.w lose.
    private static float BasisFrobeniusSquaredDifference(Basis a, Basis b)
    {
        Vector3 dx = a.X - b.X;
        Vector3 dy = a.Y - b.Y;
        Vector3 dz = a.Z - b.Z;
        return dx.LengthSquared() + dy.LengthSquared() + dz.LengthSquared();
    }

    private static float BasisDifferenceThresholdSquared(float degrees)
    {
        float halfRadians = Mathf.DegToRad(Mathf.Max(0f, degrees)) * .5f;
        float sine = Mathf.Sin(halfRadians);
        return 8f * sine * sine;
    }

    private static float BasisAngularDifferenceDegrees(Basis a, Basis b)
    {
        float sineHalf = Mathf.Sqrt(Mathf.Max(0f, BasisFrobeniusSquaredDifference(a, b) / 8f));
        // atan2 stays well-conditioned for the tiny angles this feature measures.
        return Mathf.RadToDeg(2f * Mathf.Atan2(sineHalf, Mathf.Sqrt(Mathf.Max(0f, 1f - sineHalf * sineHalf))));
    }

    private static double SanitizeSimulationSeconds(double seconds) =>
        double.IsFinite(seconds) ? Math.Max(0d, seconds) : 0d;

    private static bool EventHasArrived(double absoluteEventSeconds, double now) =>
        double.IsFinite(absoluteEventSeconds) && absoluteEventSeconds <= now;

    private static double RemainingEventDuration(double absoluteEventSeconds, double now) =>
        double.IsFinite(absoluteEventSeconds)
            ? Math.Max(0d, absoluteEventSeconds - now)
            : double.PositiveInfinity;

    private static Vector3 SafeNormalized(Vector3 value, Vector3 fallback) =>
        value.IsFinite() && value.LengthSquared() > .000001f
            ? value.Normalized()
            : fallback.Normalized();

    private static float PositiveModulo(float value, float modulus)
    {
        if (!float.IsFinite(value) || !float.IsFinite(modulus) || modulus <= 0f)
            return 0f;
        float result = value % modulus;
        return result < 0f ? result + modulus : result;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        if (!double.IsFinite(value) || !double.IsFinite(modulus) || modulus <= 0d)
            return 0d;
        double result = value % modulus;
        return result < 0d ? result + modulus : result;
    }

    private static float FiniteOr(float value, float fallback) =>
        float.IsFinite(value) ? value : fallback;

    private static float PositiveFiniteOr(float value, float fallback, float minimum) =>
        MathF.Max(minimum, FiniteOr(value, fallback));

    private static double PositiveFiniteOr(double value, double fallback, double minimum) =>
        Math.Max(minimum, double.IsFinite(value) ? value : fallback);

    private static float ClampedFiniteOr(float value, float fallback, float minimum, float maximum) =>
        Mathf.Clamp(FiniteOr(value, fallback), minimum, maximum);
}
