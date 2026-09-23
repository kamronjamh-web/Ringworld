using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Switches the local compositor cloud provider without disturbing any other
/// compositor effects. Clouds for Little Planets is deliberately instantiated
/// only while selected so its RenderingDevice resources are freed on exit.
/// </summary>
public partial class RingCloudProviderController : Node
{
    public enum CloudProvider { Sunshine, CloudsLittlePlanets }

    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath SunshineBridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public CloudProvider SelectedProvider { get; private set; } = CloudProvider.Sunshine;
    [Export(PropertyHint.Range, "0,5,1")] public int ClpScaleDownPower { get; set; } = 2;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float ClpDensity { get; set; } = 0.9f;
    [Export(PropertyHint.Range, "0.05,1,0.05")] public float ClpCoverage { get; set; } = 0.5f;
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float ClpWind { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "500,12000,100")] public float ClpCloudBaseMeters { get; set; } = 800.0f;
    [Export(PropertyHint.Range, "1000,20000,100")] public float ClpCloudTopMeters { get; set; } = 3200.0f;
    [Export(PropertyHint.Range, "1000,25000,100")] public float ClpStormCloudTopMeters { get; set; } = 14000.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClpShadowStrength { get; set; } = 0.62f;
    [Export] public bool ClpShadowsEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "5000,80000,1000")] public float ClpShadowRadiusMeters { get; set; } = 30000.0f;
    [Export(PropertyHint.Range, "5000,80000,1000")] public float ClpStormFrontDistanceMeters { get; set; } = 16000.0f;
    [Export(PropertyHint.Range, "1000,30000,500")] public float ClpStormFrontHalfWidthMeters { get; set; } = 8000.0f;
    [Export(PropertyHint.Range, "1,1000,1")] public float ClpStormFrontSpeedMetersPerSecond { get; set; } = 250.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float ClpStormIntensity { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float ClpStormCoverage { get; set; } = 0.94f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float ClpStormDensity { get; set; } = 2.0f;
    [Export(PropertyHint.Range, "0,3,1")] public int ClpQuality { get; set; } = 1;
    [Export] public bool ClpUseRingTangentFrame { get; set; } = true;

    private const string TemplatePath = "res://addons/clouds_lp/CloudsLPTemplate.tres";
    private static readonly string[] QualityPaths =
    {
        "res://addons/clouds_lp/quality_profiles/low.tres",
        "res://addons/clouds_lp/quality_profiles/medium.tres",
        "res://addons/clouds_lp/quality_profiles/high.tres",
        "res://addons/clouds_lp/quality_profiles/very_high.tres"
    };

    private WorldEnvironment? _worldEnvironment;
    private RingSunshineCloudBridge? _sunshine;
    private RingVolumetricCloudLayer? _weather;
    private RingScaledFarViewport? _scaledFarViewport;
    private RingVoxelExplorerController? _explorer;
    private RingLocalWeatherEffects? _weatherEffects;
    private VoxelChunkGridManager? _grid;
    private CompositorEffect? _clpEffect;
    private int _appliedScaleDownPower = -1;
    private int _appliedClpQuality = -1;
    private bool _savedFarCloudClipmapEnabled;
    private bool _savedFallbackCloudShellVisible;
    private bool _savedFarCloudState;
    private ImageTexture? _clpShadowTexture;
    private bool _stormFrontActive;
    private double _stormFrontOffsetMeters;
    private RingCanonicalPosition _stormFrontOrigin;
    private float _preStormCoverage, _preStormDensity, _preStormTopMeters;
    private float _lastAppliedStormInfluence = -1.0f;
    private double _shadowElapsed;

    public bool ClpAvailable => ResourceLoader.Exists(TemplatePath);
    public bool ClpActive => SelectedProvider == CloudProvider.CloudsLittlePlanets && _clpEffect != null;
    public bool ClpStormFrontActive => _stormFrontActive;
    public float ClpStormViewerInfluence { get; private set; }

    public override void _Ready()
    {
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _sunshine = GetNodeOrNull<RingSunshineCloudBridge>(SunshineBridgePath);
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _weatherEffects = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        string[] userArguments = OS.GetCmdlineUserArgs();
        if (Array.IndexOf(userArguments, "--cloud-provider-clp") >= 0)
            SelectedProvider = CloudProvider.CloudsLittlePlanets;
        SetProvider(SelectedProvider);
        if (Array.IndexOf(userArguments, "--clp-storm-demo") >= 0 && ClpActive)
        {
            // A short, deliberately accelerated pass for capture/testing. The
            // grave-menu defaults remain independently adjustable in normal play.
            ClpStormFrontDistanceMeters = 5000.0f;
            ClpStormFrontSpeedMetersPerSecond = 500.0f;
            CallDeferred(nameof(SpawnClpStormFront));
        }
        if (Array.IndexOf(userArguments, "--cloud-provider-smoke") >= 0)
            CallDeferred(nameof(RunProviderSmoke));
    }

    public override void _ExitTree() => UnloadClp();

    public override void _Process(double delta)
    {
        if (!ClpActive) return;
        UpdateClpTangentFrame();
        if (_stormFrontActive) _stormFrontOffsetMeters += delta * Mathf.Max(0.0f, ClpStormFrontSpeedMetersPerSecond);
        _shadowElapsed += delta;
        if (_shadowElapsed >= 0.20)
        {
            _shadowElapsed = 0.0;
            if (_stormFrontActive)
            {
                ResolveStormFrontState(out _, out float influence);
                ApplyStormTuning(influence);
            }
            UpdateClpShadowMask();
        }
        UpdateClpWeatherOverride();
    }

    public void ToggleClpStormFront() { if (_stormFrontActive) ClearClpStormFront(); else SpawnClpStormFront(); }
    public void SpawnClpStormFront()
    {
        if (!ClpActive || _stormFrontActive) return;
        _stormFrontActive = true;
        _stormFrontOffsetMeters = 0.0;
        _stormFrontOrigin = _explorer?.CanonicalPosition ?? default;
        _preStormCoverage = ClpCoverage; _preStormDensity = ClpDensity; _preStormTopMeters = ClpCloudTopMeters;
        _lastAppliedStormInfluence = -1.0f;
        ResolveStormFrontState(out _, out float influence);
        ApplyStormTuning(influence);
        UpdateClpWeatherOverride();
    }
    public void ClearClpStormFront()
    {
        if (!_stormFrontActive) return;
        _stormFrontActive = false;
        ClpCoverage = _preStormCoverage; ClpDensity = _preStormDensity; ClpCloudTopMeters = _preStormTopMeters;
        ClpStormViewerInfluence = 0.0f;
        _lastAppliedStormInfluence = -1.0f;
        ApplyClpTuning();
        _weatherEffects?.SetExternalWeatherOverride(false);
    }

    public void SetProvider(int provider) => SetProvider((CloudProvider)Mathf.Clamp(provider, 0, 1));

    public void SetProvider(CloudProvider provider)
    {
        if (provider == CloudProvider.CloudsLittlePlanets && !ClpAvailable)
        {
            GD.PushWarning("Clouds for Little Planets is unavailable; keeping Sunshine Clouds 2 active.");
            provider = CloudProvider.Sunshine;
        }

        SelectedProvider = provider;
        if (provider == CloudProvider.Sunshine)
        {
            UnloadClp();
            _sunshine?.SetProviderActive(true);
            return;
        }

        _sunshine?.SetProviderActive(false);
        LoadClp();
    }

    public void ApplyClpTuning()
    {
        if (_clpEffect == null) return;
        int scaleDownPower = Mathf.Clamp(ClpScaleDownPower, 0, 5);
        if (_appliedScaleDownPower != scaleDownPower)
        {
            // This setting reallocates CLP's viewport-sized buffers. Avoid
            // assigning it again while dragging unrelated live controls.
            _clpEffect.Set("scale_down_power", scaleDownPower);
            _appliedScaleDownPower = scaleDownPower;
        }
        // CLP's upstream flat mode uses global Y. The ring integration supplies
        // a live canonical tangent frame, so the effect position must be a
        // world-space point for GDScript to transform into local (0, base, 0).
        // Reapply it here because storm tuning can change the base/top at 5 Hz.
        if (_explorer != null && _grid != null) UpdateClpTangentFrame();
        else _clpEffect.Set("position", new Vector3(0.0f, Mathf.Max(0.0f, ClpCloudBaseMeters), 0.0f));

        Resource? profile = _clpEffect.Get("profile").AsGodotObject() as Resource;
        if (profile != null)
        {
            // In CLP's flat shader the volume starts at effect.position and
            // extends profile.atmo_height along Y, even with atmosphere off.
            float thicknessMeters = Mathf.Max(100.0f, ClpCloudTopMeters - ClpCloudBaseMeters);
            profile.Set("cld_s_density_factor", Mathf.Clamp(ClpDensity, 0.1f, 4.0f));
            float coverage = Mathf.Clamp(ClpCoverage, 0.05f, 1.0f);
            float thresholdStart = Mathf.Lerp(0.72f, 0.24f, coverage);
            profile.Set("cld_nsa_threshold", new Vector2(thresholdStart, Mathf.Min(0.96f, thresholdStart + 0.24f)));
            // earth_flat.tres is authored as a 50 m volume with a 50 m
            // global cloud scale. Preserve that 1:1 ratio when stretching the
            // demo to kilometre-scale cloud altitudes, as required by CLP's
            // profile rescaling guide.
            profile.Set("cld_nsa_global_scale", thicknessMeters);
            // The stock profile's saturated blue is meant to light CLP's own
            // atmosphere. This demo keeps the ring sky, so use neutral direct
            // cloud light with a restrained warm terminator tint.
            profile.Set("atmo_s_color_direct", Colors.White);
            profile.Set("atmo_s_color_tangent", new Color(1.0f, 0.55f, 0.28f));
            profile.Set("cld_a_position_rate", new Vector3(Mathf.Max(0.01f, ClpWind), 0.0f, Mathf.Max(0.01f, ClpWind) * 0.35f));
            profile.Set("atmo_height", thicknessMeters);
        }
        ApplyClpQuality();
    }

    private void LoadClp()
    {
        if (_clpEffect != null)
        {
            AttachEffect(_clpEffect);
            ApplyClpTuning();
            return;
        }

        // CLP's resources use two global GDScript classes. Explicitly loading
        // their scripts first avoids a first-run class-cache race in exported
        // .NET projects where the compositor template otherwise parses before
        // those class names are registered.
        ResourceLoader.Load<Script>("res://addons/clouds_lp/scripts/cloud_quality_profile.gd");
        ResourceLoader.Load<Script>("res://addons/clouds_lp/scripts/atmosphere_profile.gd");
        Resource? template = ResourceLoader.Load<Resource>(TemplatePath);
        _clpEffect = template?.Duplicate(true) as CompositorEffect;
        if (_clpEffect == null)
        {
            GD.PushError("Clouds for Little Planets template could not be instantiated.");
            SelectedProvider = CloudProvider.Sunshine;
            _sunshine?.SetProviderActive(true);
            return;
        }

        Resource? flatProfile = ResourceLoader.Load<Resource>("res://addons/clouds_lp/profiles/earth_flat.tres");
        if (flatProfile != null) _clpEffect.Set("profile", flatProfile.Duplicate(true));
        // These setters rebuild CLP's compute pipelines. Configure them once
        // per provider load rather than on every live density/wind update.
        _clpEffect.Set("flat_world", true);
        _clpEffect.Set("atmo_enabled", false);
        _clpEffect.Set("cloud_enabled", true);
        // This lights the clouds through CLP's atmospheric model without
        // drawing CLP's atmosphere over the ring's own sky.
        _clpEffect.Set("cloud_atmo_light_enabled", true);
        _clpEffect.Set("max_distance", 80000.0f);
        _clpEffect.Set("light_source", SunLightPath);
        _appliedScaleDownPower = -1;
        _appliedClpQuality = -1;
        AttachEffect(_clpEffect);
        ApplyClpTuning();
        DisableRingFarCloudsForClp();
        _weather?.SetExternalLocalVolumetricCloudsState(true, true, 36000.0f, 48000.0f);
        SetProcess(true);
        GD.Print("Cloud provider switched to Clouds for Little Planets (flat cloud-only demo).");
    }

    private void AttachEffect(CompositorEffect effect)
    {
        if (_worldEnvironment == null) return;
        _worldEnvironment.Compositor ??= new Compositor();
        Godot.Collections.Array<CompositorEffect> effects = _worldEnvironment.Compositor.CompositorEffects;
        if (!effects.Contains(effect))
        {
            effects.Add(effect);
            _worldEnvironment.Compositor.CompositorEffects = effects;
        }
    }

    private void ApplyClpQuality()
    {
        if (_clpEffect == null) return;
        int quality = Mathf.Clamp(ClpQuality, 0, QualityPaths.Length - 1);
        if (_appliedClpQuality == quality) return;
        Resource? qualityProfile = ResourceLoader.Load<Resource>(QualityPaths[quality]);
        if (qualityProfile == null) return;
        _clpEffect.Set("cloud_quality", qualityProfile.Duplicate(true));
        _appliedClpQuality = quality;
    }

    private void UnloadClp()
    {
        if (_clpEffect == null) return;
        if (_worldEnvironment?.Compositor != null)
        {
            Godot.Collections.Array<CompositorEffect> effects = _worldEnvironment.Compositor.CompositorEffects;
            if (effects.Remove(_clpEffect)) _worldEnvironment.Compositor.CompositorEffects = effects;
        }
        _clpEffect.Set("enabled", false);
        if (_clpEffect.HasMethod("release_runtime_resources"))
            _clpEffect.Call("release_runtime_resources");
        // CompositorEffect is RefCounted, not a Node: releasing its managed
        // wrapper is the correct route to its PREDELETE RID cleanup.
        _clpEffect.Dispose();
        _clpEffect = null;
        _appliedScaleDownPower = -1;
        _appliedClpQuality = -1;
        _weather?.SetExternalLocalVolumetricCloudsState(false, false, 36000.0f, 48000.0f);
        RestoreRingFarClouds();
        ClearClpStormFront();
        RingCloudShadowProjector.Disable();
        SetProcess(false);
    }

    private void UpdateClpWeatherOverride()
    {
        if (!_stormFrontActive || _explorer == null || _grid == null) return;
        ResolveStormFrontState(out double centerAround, out float influence);
        if (influence < 0.015f) { _weatherEffects?.SetExternalWeatherOverride(false); return; }
        float halfWidth = Mathf.Max(1000.0f, ClpStormFrontHalfWidthMeters);
        float targetCoverage = Mathf.Max(_preStormCoverage, ClpStormCoverage);
        float targetTop = Mathf.Max(_preStormTopMeters, ClpStormCloudTopMeters);
        var storm = new RingVolumetricCloudLayer.LocalWeatherState(
            -902, RingVolumetricCloudLayer.WeatherSystemKind.MajorFront, influence,
            halfWidth * 1.75f, new Vector2((float)centerAround, (float)_stormFrontOrigin.AcrossMeters),
            new Vector2(halfWidth * 1.75f, halfWidth), 0.0f,
            Mathf.Lerp(_preStormCoverage, targetCoverage, influence), Mathf.Pow(influence, 1.35f),
            // The front center advances toward decreasing canonical-around values.
            new Vector2(-ClpStormFrontSpeedMetersPerSecond, 0), CloudFormationProfile.AnvilStorm,
            ClpCloudBaseMeters, Mathf.Lerp(_preStormTopMeters, targetTop, influence));
        _weatherEffects?.SetExternalWeatherOverride(true, storm);
    }

    private void ResolveStormFrontState(out double centerAround, out float influence)
    {
        centerAround = 0.0;
        influence = 0.0f;
        if (!_stormFrontActive || _explorer == null || _grid == null) return;

        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        centerAround = (_stormFrontOrigin.AroundMeters + ClpStormFrontDistanceMeters - _stormFrontOffsetMeters) % circumference;
        if (centerAround < 0.0) centerAround += circumference;
        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        double aroundDelta = viewer.AroundMeters - centerAround;
        aroundDelta -= Math.Round(aroundDelta / circumference) * circumference;
        double acrossDelta = viewer.AcrossMeters - _stormFrontOrigin.AcrossMeters;
        float halfWidth = Mathf.Max(1000.0f, ClpStormFrontHalfWidthMeters);
        float normalizedAround = (float)(aroundDelta / halfWidth);
        float normalizedAcross = (float)(acrossDelta / halfWidth);
        influence = Mathf.Exp(-(normalizedAround * normalizedAround + normalizedAcross * normalizedAcross));
        influence = Mathf.Clamp(influence * Mathf.Clamp(ClpStormIntensity, 0.0f, 1.0f), 0.0f, 1.0f);
        ClpStormViewerInfluence = influence;
    }

    private void ApplyStormTuning(float influence)
    {
        if (!_stormFrontActive) return;
        influence = Mathf.Clamp(influence, 0.0f, 1.0f);
        if (Mathf.Abs(influence - _lastAppliedStormInfluence) < 0.0025f) return;
        _lastAppliedStormInfluence = influence;
        ClpCoverage = Mathf.Lerp(_preStormCoverage, Mathf.Max(_preStormCoverage, ClpStormCoverage), influence);
        ClpDensity = Mathf.Lerp(_preStormDensity, Mathf.Max(_preStormDensity, ClpStormDensity), influence);
        ClpCloudTopMeters = Mathf.Lerp(_preStormTopMeters, Mathf.Max(_preStormTopMeters, ClpStormCloudTopMeters), influence);
        ApplyClpTuning();
    }

    private void UpdateClpShadowMask()
    {
        if (!ClpShadowsEnabled || _explorer == null || _grid == null)
        { RingCloudShadowProjector.Disable(); return; }
        const int resolution = 64;
        var bytes = new byte[resolution * resolution];
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double frontCenterAround = 0.0;
        if (_stormFrontActive) ResolveStormFrontState(out frontCenterAround, out _);
        float radius = Mathf.Max(1.0f, ClpShadowRadiusMeters);
        float halfWidth = Mathf.Max(1000.0f, ClpStormFrontHalfWidthMeters);
        float windPhase = Time.GetTicksMsec() * 0.00007f * Mathf.Max(0.01f, ClpWind);
        float baseCoverage = _stormFrontActive ? _preStormCoverage : ClpCoverage;
        float baseDensity = _stormFrontActive ? _preStormDensity : ClpDensity;
        float targetCoverage = Mathf.Max(baseCoverage, ClpStormCoverage);
        float targetDensity = Mathf.Max(baseDensity, ClpStormDensity);
        for (int y = 0; y < resolution; y++) for (int x = 0; x < resolution; x++)
        {
            float nx = (x + 0.5f) / resolution - 0.5f;
            float ny = (y + 0.5f) / resolution - 0.5f;
            double sampleAround = position.AroundMeters + nx * radius * 2.0f;
            double sampleAcross = position.AcrossMeters + ny * radius * 2.0f;
            float frontInfluence = 0.0f;
            if (_stormFrontActive)
            {
                double aroundDelta = sampleAround - frontCenterAround;
                aroundDelta -= Math.Round(aroundDelta / circumference) * circumference;
                double acrossDelta = sampleAcross - _stormFrontOrigin.AcrossMeters;
                float normalizedAround = (float)(aroundDelta / halfWidth);
                float normalizedAcross = (float)(acrossDelta / halfWidth);
                frontInfluence = Mathf.Exp(-(normalizedAround * normalizedAround + normalizedAcross * normalizedAcross))
                    * Mathf.Clamp(ClpStormIntensity, 0.0f, 1.0f);
            }
            float coverage = Mathf.Lerp(baseCoverage, targetCoverage, frontInfluence);
            float density = Mathf.Lerp(baseDensity, targetDensity, frontInfluence);
            float noise = SampleShadowNoise(sampleAround, sampleAcross, windPhase);
            float threshold = Mathf.Lerp(0.74f, 0.28f, Mathf.Clamp(coverage, 0.05f, 1.0f));
            float opacity = SmoothUnit((noise - threshold) / 0.18f) * Mathf.Clamp(density * 0.72f, 0.0f, 1.0f);
            bytes[y * resolution + x] = (byte)Mathf.RoundToInt(opacity * 255.0f);
        }
        var image = Image.CreateFromData(resolution, resolution, false, Image.Format.R8, bytes);
        if (_clpShadowTexture == null) _clpShadowTexture = ImageTexture.CreateFromImage(image); else _clpShadowTexture.Update(image);
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 center = geometry.CanonicalToWorld(position.AroundMeters, 0.0, position.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(position.AroundMeters + 10.0, 0.0, position.AcrossMeters) - center).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(position.AroundMeters, 0.0, position.AcrossMeters + 10.0) - center).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(position.AroundMeters, 10.0, position.AcrossMeters) - center).Normalized();
        RingCloudShadowProjector.Update(
            _clpShadowTexture, center, around, across, ClpShadowRadiusMeters,
            ClpShadowStrength, ClpShadowStrength, 0, 1, 1, 2,
            up,
            (float)Math.Max(1.0, geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters));
    }

    private void UpdateClpTangentFrame()
    {
        if (_clpEffect == null || _explorer == null || _grid == null) return;
        RingCanonicalPosition canonical = _explorer.CanonicalPosition;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(canonical.AroundMeters, 0.0, canonical.AcrossMeters);
        Vector3 around = (geometry.CanonicalToWorld(canonical.AroundMeters + 10.0, 0.0, canonical.AcrossMeters) - origin).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(canonical.AroundMeters, 0.0, canonical.AcrossMeters + 10.0) - origin).Normalized();
        Vector3 up = (geometry.CanonicalToWorld(canonical.AroundMeters, 10.0, canonical.AcrossMeters) - origin).Normalized();
        // Preserve a right-handed local frame: around=X, inward surface-up=Y,
        // across=Z. No setter reloads shader pipelines for these live values.
        _clpEffect.Set("flat_frame_enabled", ClpUseRingTangentFrame);
        _clpEffect.Set("flat_frame_origin", origin);
        _clpEffect.Set("flat_frame_x", around);
        _clpEffect.Set("flat_frame_y", up);
        // Around x up points opposite canonical across on the inner ring
        // surface. Negate across so the shader receives a right-handed frame.
        _clpEffect.Set("flat_frame_z", -across);
        _clpEffect.Set("position", ClpUseRingTangentFrame
            ? origin + up * Mathf.Max(0.0f, ClpCloudBaseMeters)
            : new Vector3(0.0f, Mathf.Max(0.0f, ClpCloudBaseMeters), 0.0f));
    }

    private static float SampleShadowNoise(double aroundMeters, double acrossMeters, float phase)
    {
        float around = (float)(aroundMeters % 1_000_000.0);
        float across = (float)(acrossMeters % 1_000_000.0);
        float noise = Mathf.Sin(around * 0.00022f + across * 0.00007f + phase) * 0.34f
            + Mathf.Sin(around * 0.00061f - across * 0.00037f - phase * 0.63f) * 0.25f
            + Mathf.Sin(around * 0.00131f + across * 0.00091f + phase * 1.37f) * 0.18f
            + Mathf.Sin(around * 0.00270f - across * 0.00190f - phase * 2.10f) * 0.09f;
        return Mathf.Clamp(0.5f + noise * 0.55f, 0.0f, 1.0f);
    }

    private static float SmoothUnit(float value)
    {
        float unit = Mathf.Clamp(value, 0.0f, 1.0f);
        return unit * unit * (3.0f - 2.0f * unit);
    }

    private void DisableRingFarCloudsForClp()
    {
        if (_scaledFarViewport == null || _savedFarCloudState) return;
        _savedFarCloudClipmapEnabled = _scaledFarViewport.FarCloudClipmapEnabled;
        _savedFallbackCloudShellVisible = _scaledFarViewport.FallbackCloudShellVisible;
        _savedFarCloudState = true;
        _scaledFarViewport.FarCloudClipmapEnabled = false;
        _scaledFarViewport.SetFallbackCloudShellEnabled(false);
    }

    private void RestoreRingFarClouds()
    {
        if (_scaledFarViewport == null || !_savedFarCloudState) return;
        _scaledFarViewport.FarCloudClipmapEnabled = _savedFarCloudClipmapEnabled;
        _scaledFarViewport.SetFallbackCloudShellEnabled(_savedFallbackCloudShellVisible);
        _savedFarCloudState = false;
    }

    private async void RunProviderSmoke()
    {
        // A lightweight runtime ownership check for CI/manual command-line
        // validation. It leaves no provider active except Sunshine.
        SetProvider(CloudProvider.CloudsLittlePlanets);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int clpCount = CountOwnedEffect("CloudsLP");
        int sunshineCountWhileClp = CountOwnedEffect("SunshineCloudsGD");
        bool farCloudsWhileClp = _scaledFarViewport?.FarCloudClipmapEnabled == true
            || _scaledFarViewport?.FallbackCloudShellVisible == true;
        SetProvider(CloudProvider.Sunshine);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        int clpCountAfterRestore = CountOwnedEffect("CloudsLP");
        int sunshineCountAfterRestore = CountOwnedEffect("SunshineCloudsGD");
        GD.Print($"Cloud provider smoke: clp={clpCount}, sunshineWhileClp={sunshineCountWhileClp}, farCloudsWhileClp={farCloudsWhileClp}, clpAfterRestore={clpCountAfterRestore}, sunshineAfterRestore={sunshineCountAfterRestore}.");
        GetTree().Quit(clpCount == 1 && sunshineCountWhileClp == 0 && !farCloudsWhileClp && clpCountAfterRestore == 0 && sunshineCountAfterRestore == 1 ? 0 : 1);
    }

    private int CountOwnedEffect(string scriptClass)
    {
        if (_worldEnvironment?.Compositor == null) return 0;
        int count = 0;
        foreach (CompositorEffect effect in _worldEnvironment.Compositor.CompositorEffects)
        {
            if (effect.GetScript().AsGodotObject() is Script script && script.GetGlobalName() == scriptClass)
                count++;
        }
        return count;
    }
}
