using Godot;
using System.Collections.Generic;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Applies one coherent graphics preset across terrain, clouds, shadows,
/// scaled space, screen-space effects, and streaming budgets.
/// </summary>
public partial class RingGraphicsQualityController : Node
{
    private readonly record struct Tuning(
        int ShadowAtlasSize,
        RenderingServer.ShadowQuality ShadowFilter,
        float ShadowDistanceMeters,
        float SunAngularDistanceDegrees,
        float MacroShadowStrength,
        float MacroFadeStartMeters,
        float MacroFadeEndMeters,
        int MacroTraceSteps,
        float MacroTraceMeters,
        global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality CloudQuality,
        float CloudDetailedRadiusMeters,
        float CloudTransitionRadiusMeters,
        int CloudShadowResolution,
        float ConventionalCloudShadowRadiusMeters,
        float CloudShadowStrength,
        float CloudShadowFarStrength,
        float CloudShadowNearStartMeters,
        float CloudShadowNearEndMeters,
        float CloudShadowFarStartMeters,
        float CloudShadowFarEndMeters,
        int CloudShadowBlurPixels,
        float FarViewportResolutionScale,
        int FarAroundSegments,
        int FarAcrossSegments,
        int SsrMaxSteps,
        float SsaoDetail,
        int VoxelWorkers,
        int VoxelUploadsPerFrame,
        float VoxelUploadBudgetMilliseconds);

    [Export] public RingTerrainLodCoordinator.QualityPreset DefaultPreset { get; set; }
        = RingTerrainLodCoordinator.QualityPreset.Medium;
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath CloudLayerPath { get; set; } = new("../RingVolumetricClouds");
	[Export] public NodePath SunshineCloudBridgePath { get; set; } = new("../SunshineCloudBridge");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GrassLayerPath { get; set; } = new("../StandingGrass");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public bool UseMegaRegionalFarBackdrop { get; set; }

    [ExportGroup("Shadow Transition Overrides")]
    [Export] public bool UseInspectorShadowTransitionOverrides { get; set; }
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float ShadowOverlapDarkening { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "1000,50000,250")]
    public float InspectorRealtimeShadowDistanceMeters { get; set; } = 6000.0f;
    [Export(PropertyHint.Range, "0,0.8,0.01")]
    public float InspectorTerrainMacroShadowStrength { get; set; } = 0.36f;
    [Export(PropertyHint.Range, "0,50000,100")]
    public float InspectorTerrainMacroFadeStartMeters { get; set; } = 3500.0f;
    [Export(PropertyHint.Range, "100,60000,100")]
    public float InspectorTerrainMacroFadeEndMeters { get; set; } = 6500.0f;
    [Export(PropertyHint.Range, "1000,30000,250")]
    public float InspectorConventionalCloudShadowRadiusMeters { get; set; } = 6500.0f;
    [Export(PropertyHint.Range, "0,0.8,0.01")]
    public float InspectorProjectedCloudShadowStrength { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,0.8,0.01")]
    public float InspectorProjectedCloudShadowFarStrength { get; set; } = 0.58f;
    [Export(PropertyHint.Range, "500,30000,100")]
    public float InspectorProjectedCloudNearStartMeters { get; set; } = 3500.0f;
    [Export(PropertyHint.Range, "1000,40000,100")]
    public float InspectorProjectedCloudNearEndMeters { get; set; } = 7000.0f;
    [Export(PropertyHint.Range, "1000,60000,100")]
    public float InspectorProjectedCloudFarStrengthStartMeters { get; set; } = 7000.0f;
    [Export(PropertyHint.Range, "1000,120000,100")]
    public float InspectorProjectedCloudFarStrengthEndMeters { get; set; } = 18000.0f;

    [ExportGroup("Scale Aerial Perspective")]
    [Export] public bool EnableRingAerialPerspective { get; set; } = true;
    [Export(PropertyHint.Range, "0,10000,100")]
    public float AtmosphereFloorDepthMeters { get; set; } = 2500.0f;
    [Export(PropertyHint.Range, "500,10000,100")]
    public float AtmosphereBoundaryHeightMeters { get; set; } = 2500.0f;
    [Export(PropertyHint.Range, "2000,50000,500")]
    public float AtmosphereUpperHeightMeters { get; set; } = 20000.0f;
    [Export(PropertyHint.Range, "5000,200000,1000")]
    public float AtmosphereBoundaryExtinctionMeters { get; set; } = 60000.0f;
    [Export(PropertyHint.Range, "10000,1000000,5000")]
    public float AtmosphereUpperExtinctionMeters { get; set; } = 300000.0f;
    [Export(PropertyHint.Range, "0,0.9,0.01")]
    public float AtmosphereBoundaryMaxOpacity { get; set; } = 0.22f;
    [Export(PropertyHint.Range, "0,0.9,0.01")]
    public float AtmosphereUpperMaxOpacity { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0,0.98,0.01")]
    public float AtmosphereTotalMaxOpacity { get; set; } = 0.80f;
    [Export(PropertyHint.Range, "0,100000,100")]
    public float AtmosphereClearDistanceMeters { get; set; } = 30000.0f;
    [Export(PropertyHint.Range, "1000,300000,250")]
    public float AtmosphereFullDistanceMeters { get; set; } = 220000.0f;
    [Export(PropertyHint.Range, "1000,300000,500")]
    public float AtmosphereHighAltitudeClearDistanceMeters { get; set; } = 200000.0f;
    [Export(PropertyHint.Range, "1000,800000,500")]
    public float AtmosphereHighAltitudeFullDistanceMeters { get; set; } = 600000.0f;
    [Export(PropertyHint.Range, "0,20000,100")]
    public float AtmosphereDistanceAltitudeStartMeters { get; set; } = 1500.0f;
    [Export(PropertyHint.Range, "100,30000,100")]
    public float AtmosphereDistanceAltitudeEndMeters { get; set; } = 12000.0f;
    [Export(PropertyHint.Range, "0,0.98,0.01")]
    public float AtmosphereDistanceHazeMaxOpacity { get; set; } = 0.68f;
    [Export(PropertyHint.Range, "0,1,0.01")]
    public float AtmosphereNightDensityFactor { get; set; } = 0.08f;
    [Export(PropertyHint.Range, "0,2,0.01")]
    public float AtmosphereScatteringEnergy { get; set; } = 0.26f;
    [Export] public Color AtmosphereDayColor { get; set; } = new(0.34f, 0.46f, 0.60f, 1.0f);
    [Export] public Color AtmosphereNightColor { get; set; } = new(0.026f, 0.035f, 0.055f, 1.0f);

    [ExportGroup("Water Shading")]
    [Export(PropertyHint.Range, "0,10,0.01")] public float WaterWaveHeightMeters { get; set; } = 0.32f;
    [Export(PropertyHint.Range, "2,200,1")] public float WaterWaveLengthMeters { get; set; } = 28.0f;
    [Export(PropertyHint.Range, "0,10,0.05")] public float WaterWaveSpeed { get; set; } = 1.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterWaveDetailStrength { get; set; } = 0.42f;
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float WaterWaveDomainWarpStrength { get; set; } = 0.46f;
    [Export(PropertyHint.Range, "0.05,4,0.05")] public float WaterWaveCrestSharpness { get; set; } = 1.35f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterWaveCrestBlend { get; set; } = 0.62f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterWaveNormalStrength { get; set; } = 0.90f;
    [Export(PropertyHint.Range, "0.05,2,0.01")] public float WaterWaveBandlimitStartRadians { get; set; } = 0.35f;
    [Export(PropertyHint.Range, "0.1,3,0.01")] public float WaterWaveBandlimitEndRadians { get; set; } = 1.10f;
    [Export(PropertyHint.Range, "100,50000,100")] public float WaterWaveGeometryFadeMeters { get; set; } = 8000.0f;
    [Export(PropertyHint.Range, "100,50000,100")] public float WaterWaveNormalFadeStartMeters { get; set; } = 500.0f;
    [Export(PropertyHint.Range, "200,100000,100")] public float WaterWaveNormalFadeEndMeters { get; set; } = 3000.0f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float WaterFarRoughness { get; set; } = 0.46f;
    [Export(PropertyHint.Range, "0,0.12,0.001")] public float WaterFresnelF0 { get; set; } = 0.020f;
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float WaterFresnelStrength { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterSpecularAaStrength { get; set; } = 0.85f;
    [Export(PropertyHint.Range, "0,0.08,0.001")] public float WaterRefractionStrength { get; set; } = 0.012f;
    [Export(PropertyHint.Range, "0.001,2,0.001")] public float WaterAbsorptionDensity { get; set; } = 0.11f;
    [Export] public Color WaterAbsorptionCoefficients { get; set; } = new(0.30f, 0.12f, 0.035f, 1.0f);
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterShallowInscatterStrength { get; set; } = 0.58f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterCrestTransmissionStrength { get; set; } = 0.95f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterShallowAlpha { get; set; } = 0.38f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float WaterDeepAlpha { get; set; } = 0.995f;
    [Export] public bool WaterSsrEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterSsrStrength { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "1,32,1")] public int WaterSsrCoarseSteps { get; set; } = 24;
    [Export(PropertyHint.Range, "0,8,1")] public int WaterSsrRefinementSteps { get; set; } = 4;
    [Export(PropertyHint.Range, "10,1000,5")] public float WaterSsrMaxRayDistance { get; set; } = 250f;
    [Export(PropertyHint.Range, "10,1000,5")] public float WaterSsrCameraDistance { get; set; } = 450f;
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float WaterSsrDepthToleranceNear { get; set; } = .35f;
    [Export(PropertyHint.Range, "0.01,1,0.01")] public float WaterSsrRayBias { get; set; } = .12f;
    [Export(PropertyHint.Range, "0.1,8,0.01")] public float WaterSsrDepthToleranceFar { get; set; } = 2f;
    [Export(PropertyHint.Range, "0.01,0.4,0.01")] public float WaterSsrEdgeFade { get; set; } = .12f;
    [Export(PropertyHint.Range, "0.02,1,0.01")] public float WaterSsrRoughnessCutoff { get; set; } = .32f;
    [Export(PropertyHint.Range, "0,3,1")] public int WaterSsrDebugMode { get; set; }
    [Export(PropertyHint.Range, "0.1,20,0.1")] public float WaterShoreFoamDepthMeters { get; set; } = 2.4f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterShoreFoamStrength { get; set; } = 0.82f;
    [Export(PropertyHint.Range, "0.05,1.5,0.01")] public float WaterBreakingFoamSlopeStart { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "0.1,2,0.01")] public float WaterBreakingFoamSlopeEnd { get; set; } = 0.48f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterBreakingFoamStrength { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterSwellVariation { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterFoamPatchiness { get; set; } = 0.65f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float WaterDistantFoamVisibility { get; set; } = 0.20f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float WaterDetailFilteringStrength { get; set; } = 1.0f;
    [Export] public Color WaterShallowTint { get; set; } = new(0.055f, 0.42f, 0.50f, 1.0f);
    [Export] public Color WaterDeepTint { get; set; } = new(0.012f, 0.075f, 0.16f, 1.0f);
    [Export] public Color WaterFoamColor { get; set; } = new(0.72f, 0.88f, 0.91f, 1.0f);

    private RingTerrainLodCoordinator? _coordinator;
    private RingTerrainGlobalLod? _globalLod;
    private global::RingworldTechDemo.RingVolumetricCloudLayer? _clouds;
	private global::RingworldTechDemo.RingSunshineCloudBridge? _sunshineClouds;
    private RingScaledFarViewport? _scaledFarViewport;
    private VoxelChunkGridManager? _grid;
    private RingStandingGrassLayer? _grass;
    private DirectionalLight3D? _sun;
    private WorldEnvironment? _worldEnvironment;
    private bool _skyAtmosphereRegistered;
    private readonly Dictionary<string, Variant> _grassTuningOverrides = new();
    /// <summary>Live drawer target; null when a scene intentionally has no standing grass.</summary>
    public RingStandingGrassLayer? GrassLayer => _grass;

    public RingTerrainLodCoordinator.QualityPreset CurrentPreset { get; private set; }
        = RingTerrainLodCoordinator.QualityPreset.Medium;
    public string CurrentPresetName => CurrentPreset switch
    {
        RingTerrainLodCoordinator.QualityPreset.UltraVideo => "Ultra / Video",
        RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => "Cinematic / RTX 5090",
        _ => _coordinator?.CurrentPresetName ?? CurrentPreset.ToString()
    };

    public override void _Ready()
    {
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _clouds = GetNodeOrNull<global::RingworldTechDemo.RingVolumetricCloudLayer>(CloudLayerPath);
		_sunshineClouds = GetNodeOrNull<global::RingworldTechDemo.RingSunshineCloudBridge>(SunshineCloudBridgePath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _grass = GetNodeOrNull<RingStandingGrassLayer>(GrassLayerPath);
        _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);

        RingTerrainLodCoordinator.QualityPreset startupPreset = ResolveCommandLinePreset(DefaultPreset);
        bool terrainAlreadyConfigured = _coordinator?.CurrentPreset == startupPreset;
        ApplyPreset(startupPreset, applyTerrainPreset: !terrainAlreadyConfigured, userSelected: false);
    }

    public override void _Process(double delta)
    {
        _ = delta;
        if (_skyAtmosphereRegistered || _grid == null) return;
        if (_worldEnvironment?.Environment?.Sky?.SkyMaterial is not ShaderMaterial skyMaterial) return;
        if (skyMaterial.Shader == null
            || !skyMaterial.Shader.Code.Contains("sample_ring_sky_atmosphere")) return;
        RingAtmosphereModel.ConfigureMaterial(
            skyMaterial,
            _grid.GetCanonicalRingCircumferenceMeters64() / System.Math.Tau);
        _skyAtmosphereRegistered = true;
        GD.Print("Curved celestial and exterior ring atmosphere bound to the shared sky.");
    }

    public void ApplyUserPreset(RingTerrainLodCoordinator.QualityPreset preset) =>
        ApplyPreset(preset, applyTerrainPreset: true, userSelected: true);

    public void ApplyPreset(
        RingTerrainLodCoordinator.QualityPreset preset,
        bool applyTerrainPreset = true,
        bool userSelected = false)
    {
        Tuning tuning = ResolveTuning(preset);
        CurrentPreset = preset;
        float shadowDistanceMeters = UseInspectorShadowTransitionOverrides
            ? InspectorRealtimeShadowDistanceMeters
            : tuning.ShadowDistanceMeters;
        float macroShadowStrength = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroShadowStrength
            : tuning.MacroShadowStrength;
        float macroFadeStartMeters = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroFadeStartMeters
            : tuning.MacroFadeStartMeters;
        float macroFadeEndMeters = UseInspectorShadowTransitionOverrides
            ? InspectorTerrainMacroFadeEndMeters
            : tuning.MacroFadeEndMeters;
        float conventionalCloudShadowRadiusMeters = UseInspectorShadowTransitionOverrides
            ? InspectorConventionalCloudShadowRadiusMeters
            : tuning.ConventionalCloudShadowRadiusMeters;
        float cloudShadowStrength = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudShadowStrength
            : tuning.CloudShadowStrength;
        float cloudShadowFarStrength = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudShadowFarStrength
            : tuning.CloudShadowFarStrength;
        float cloudShadowNearStartMeters = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudNearStartMeters
            : tuning.CloudShadowNearStartMeters;
        float cloudShadowNearEndMeters = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudNearEndMeters
            : tuning.CloudShadowNearEndMeters;
        float cloudShadowFarStartMeters = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudFarStrengthStartMeters
            : tuning.CloudShadowFarStartMeters;
        float cloudShadowFarEndMeters = UseInspectorShadowTransitionOverrides
            ? InspectorProjectedCloudFarStrengthEndMeters
            : tuning.CloudShadowFarEndMeters;

        RingAtmosphereModel.ApplyScaleReadabilityTuning(
            EnableRingAerialPerspective,
            AtmosphereFloorDepthMeters,
            AtmosphereBoundaryHeightMeters,
            AtmosphereUpperHeightMeters,
            AtmosphereBoundaryExtinctionMeters,
            AtmosphereUpperExtinctionMeters,
            AtmosphereBoundaryMaxOpacity,
            AtmosphereUpperMaxOpacity,
            AtmosphereTotalMaxOpacity,
            AtmosphereClearDistanceMeters,
            AtmosphereFullDistanceMeters,
            AtmosphereHighAltitudeClearDistanceMeters,
            AtmosphereHighAltitudeFullDistanceMeters,
            AtmosphereDistanceAltitudeStartMeters,
            AtmosphereDistanceAltitudeEndMeters,
            AtmosphereDistanceHazeMaxOpacity,
            AtmosphereNightDensityFactor,
            AtmosphereScatteringEnergy,
            AtmosphereDayColor,
            AtmosphereNightColor);
        ApplyWaterSsrQuality(preset);
        ApplyWaterTuning();
        if (applyTerrainPreset && _coordinator != null)
        {
            if (userSelected) _coordinator.ApplyUserQualityPreset(preset);
            else _coordinator.ApplyQualityPreset(preset);
        }

        if (_sun != null)
        {
            _sun.DirectionalShadowMaxDistance = Mathf.Max(1000.0f, shadowDistanceMeters);
            _sun.DirectionalShadowBlendSplits = true;
            _sun.LightAngularDistance = tuning.SunAngularDistanceDegrees;
            _sun.ShadowBlur = preset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090 ? 1.2f : 1.0f;
        }
        ApplyShadowTuningOverrides();

        if (_coordinator != null)
            _coordinator.TerrainShadowCoverageMeters = shadowDistanceMeters;
        _globalLod?.ApplyRuntimeShadowQuality(
            macroShadowStrength,
            macroFadeStartMeters,
            macroFadeEndMeters,
            tuning.MacroTraceSteps,
            tuning.MacroTraceMeters);
        global::RingworldTechDemo.RingCloudShadowProjector.ConfigureShadowHandoff(
            ShadowOverlapDarkening);

        if (_clouds != null)
        {
            switch (tuning.CloudQuality)
            {
                case global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.Ultra:
                    _clouds.UltraRadiusMeters = tuning.CloudDetailedRadiusMeters;
                    _clouds.UltraTransitionRadiusMeters = tuning.CloudTransitionRadiusMeters;
                    break;
                case global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.VeryHigh:
                    _clouds.VeryHighRadiusMeters = tuning.CloudDetailedRadiusMeters;
                    _clouds.VeryHighTransitionRadiusMeters = tuning.CloudTransitionRadiusMeters;
                    break;
                default:
                    _clouds.HighRadiusMeters = tuning.CloudDetailedRadiusMeters;
                    _clouds.HighTransitionRadiusMeters = tuning.CloudTransitionRadiusMeters;
                    break;
            }
            _clouds.ShadowMapResolution = tuning.CloudShadowResolution;
            _clouds.ConventionalShadowRetentionRadiusMeters = conventionalCloudShadowRadiusMeters;
            _clouds.ProjectedShadowStrength = cloudShadowStrength;
            _clouds.ProjectedShadowFarStrength = cloudShadowFarStrength;
            _clouds.ProjectedShadowNearStartMeters = cloudShadowNearStartMeters;
            _clouds.ProjectedShadowNearEndMeters = cloudShadowNearEndMeters;
            _clouds.ProjectedShadowFarStrengthStartMeters = cloudShadowFarStartMeters;
            _clouds.ProjectedShadowFarStrengthEndMeters = cloudShadowFarEndMeters;
            _clouds.ProjectedShadowBlurRadiusPixels = tuning.CloudShadowBlurPixels;
            _clouds.SetQuality(tuning.CloudQuality);
        }

		ApplySunshineCloudQuality(preset);

        // Mega's whole-ring backdrop needs denser terrain than the normal
        // graphics ladder. Preserve that mesh after startup while retaining
        // its user-selected render-scale progression. Cinematic remains the
        // sole path that can select the 4096x64 far mesh.
        float farViewportResolutionScale = tuning.FarViewportResolutionScale;
        int farAroundSegments = tuning.FarAroundSegments;
        int farAcrossSegments = tuning.FarAcrossSegments;
        if (UseMegaRegionalFarBackdrop)
        {
            switch (preset)
            {
                case RingTerrainLodCoordinator.QualityPreset.Medium:
                    farViewportResolutionScale = 0.5f;
                    farAroundSegments = 2048;
                    farAcrossSegments = 32;
                    break;
                case RingTerrainLodCoordinator.QualityPreset.High:
                    farViewportResolutionScale = 0.75f;
                    farAroundSegments = 2048;
                    farAcrossSegments = 32;
                    break;
                case RingTerrainLodCoordinator.QualityPreset.VeryHigh:
                case RingTerrainLodCoordinator.QualityPreset.UltraVideo:
                    farViewportResolutionScale = 1.0f;
                    farAroundSegments = 2048;
                    farAcrossSegments = 32;
                    break;
            }
        }
        _scaledFarViewport?.ApplyRuntimeRenderQuality(
            farViewportResolutionScale,
            farAroundSegments,
            farAcrossSegments);

        if (_worldEnvironment?.Environment is { } environment)
        {
            environment.SsrMaxSteps = tuning.SsrMaxSteps;
            environment.SsaoDetail = tuning.SsaoDetail;
            environment.SsaoSharpness = preset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090
                ? 1.0f
                : 0.98f;
        }

        if (_grid != null)
        {
            _grid.MaxConcurrentVoxelMeshWorkers = tuning.VoxelWorkers;
            _grid.MaxVoxelMeshUploadsPerFrame = tuning.VoxelUploadsPerFrame;
            _grid.MaxVoxelMeshUploadMillisecondsPerFrame = tuning.VoxelUploadBudgetMilliseconds;
        }
        if (_grass != null)
        {
            string hardwareTier = ProjectSettings.GetSetting(
                "ringworld/runtime/hardware_tier", "laptop_140v_baseline").AsString();
            RingGrassQualityProfile.For(preset, hardwareTier).ApplyTo(_grass);
            ApplyGrassTuningOverrides();
        }

        GD.Print(
            $"Graphics preset applied: {CurrentPresetName}, shadowAtlas={EffectiveDirectionalShadowAtlasSize}, "
            + $"terrainLod={_coordinator?.CurrentPresetName ?? "unavailable"}, "
            + $"cascades={shadowDistanceMeters / 1000.0f:0.#}km, "
            + $"terrainMacro={tuning.MacroTraceMeters / 1000.0f:0}km/{tuning.MacroTraceSteps}, "
            + $"cloudMap={tuning.CloudShadowResolution}px/{tuning.CloudTransitionRadiusMeters / 1000.0f:0}km, "
            + $"handoff={cloudShadowNearStartMeters / 1000.0f:0.#}-{cloudShadowNearEndMeters / 1000.0f:0.#}km, "
            + $"overlapDarkening={ShadowOverlapDarkening:0.00}, "
            + $"farViewport={farViewportResolutionScale:0.00}x, "
            + $"farMesh={farAroundSegments}x{farAcrossSegments}.");
    }

    /// <summary>Reapplies this preset before the sparse player keys, used after loading or resetting grass tuning.</summary>
    public void ResetGrassTuningToCurrentProfile()
    {
        if (_grass == null) return;
        string hardwareTier = ProjectSettings.GetSetting("ringworld/runtime/hardware_tier", "laptop_140v_baseline").AsString();
        RingGrassQualityProfile.For(CurrentPreset, hardwareTier).ApplyTo(_grass);
        _grass.Enabled = true;
        _grass.UseGpuCulling = true;
        _grass.GpuFrustumCulling = true;
        _grass.SkipUnchangedGpuCulling = true;
        _grass.GroupGrassComputePhases = true;
        _grass.CullGrassTiles = true;
        _grass.UseGrassClumps = true;
        _grass.MaximumGpuCullingBatchesPerFrame = 96;
        _grass.GpuGenerationPrototypeTileCount = 4;
        _grass.SurfaceCacheMegabytes = 4;
        _grass.EnableHierarchicalCoverage = true;
        _grass.LodMidDistanceMeters = 250.0f;
        _grass.LodMidSourceDensity = .25f;
        _grass.LodFarSourceDensity = .0625f;
        _grass.EnableGrassInteraction = true;
        _grass.GrassInteractionRadiusMeters = 1.1f;
        _grass.GrassInteractionStrength = 1.0f;
        _grass.GrassInteractionRecoverySeconds = 3.0f;
        _grass.GrassWindStrengthMultiplier = 1.0f;
        _grass.GrassWindGustMultiplier = 1.0f;
        _grass.GrassMaximumWidthScale = 1.35f;
        _grass.GrassDebugMode = RingStandingGrassLayer.GrassDebugView.Off;
        _grass.ShowGrassTileBounds = false;
        _grass.GrassAppearanceProfile = new RingGrassAppearanceProfile();
        _grass.EnableGrassAppearance = true;
    }

    /// <summary>Stores only explicit user choices so later quality-profile changes still control every other grass field.</summary>
    public void SetGrassTuningOverrides(Godot.Collections.Dictionary overrides)
    {
        _grassTuningOverrides.Clear();
        foreach (Variant key in overrides.Keys) if (key.VariantType == Variant.Type.String) _grassTuningOverrides[key.AsString()] = overrides[key];
        ResetGrassTuningToCurrentProfile();
        ApplyGrassTuningOverrides();
    }

    private void ApplyGrassTuningOverrides()
    {
        if (_grass == null) return;
        foreach (var item in _grassTuningOverrides) _grass.Set(item.Key, item.Value);
        // Preserve the scheduler's required near < mid < far ordering when
        // independently tuned values cross. If the player did not explicitly
        // choose a midpoint, retain the coverage system's useful 250 m target
        // and clamp it only for compact quality profiles.
        _grass.LodFarDistanceMeters = Mathf.Max(_grass.LodNearDistanceMeters + 2, _grass.LodFarDistanceMeters);
        float requestedMid = _grassTuningOverrides.ContainsKey("LodMidDistanceMeters")
            ? _grass.LodMidDistanceMeters
            : 250.0f;
        _grass.LodMidDistanceMeters = Mathf.Clamp(requestedMid,
            _grass.LodNearDistanceMeters + 1, _grass.LodFarDistanceMeters - 1);
    }

    /// <summary>Applies only water uniforms; live tuning must not reapply the graphics preset.</summary>
    public void ApplyWaterTuning() => RingWaterModel.ApplyTuning(
        WaterWaveHeightMeters, WaterWaveLengthMeters, WaterWaveSpeed,
        WaterWaveDetailStrength, WaterWaveDomainWarpStrength, WaterWaveCrestSharpness,
        WaterWaveCrestBlend, WaterWaveNormalStrength, WaterWaveBandlimitStartRadians,
        WaterWaveBandlimitEndRadians, WaterWaveGeometryFadeMeters, WaterWaveNormalFadeStartMeters,
        WaterWaveNormalFadeEndMeters, WaterFarRoughness, WaterFresnelF0, WaterFresnelStrength,
        WaterSpecularAaStrength, WaterRefractionStrength, WaterAbsorptionDensity, WaterAbsorptionCoefficients,
        WaterShallowInscatterStrength, WaterCrestTransmissionStrength, WaterShallowAlpha,
        WaterDeepAlpha, WaterSsrEnabled, WaterSsrStrength, WaterSsrCoarseSteps, WaterSsrRefinementSteps,
        WaterSsrMaxRayDistance, WaterSsrCameraDistance, WaterSsrDepthToleranceNear, WaterSsrRayBias, WaterSsrDepthToleranceFar,
        WaterSsrEdgeFade, WaterSsrRoughnessCutoff, WaterSsrDebugMode, WaterShoreFoamDepthMeters, WaterShoreFoamStrength, WaterBreakingFoamSlopeStart,
        WaterBreakingFoamSlopeEnd, WaterBreakingFoamStrength, WaterShallowTint, WaterDeepTint, WaterFoamColor,
        WaterSwellVariation, WaterFoamPatchiness, WaterDistantFoamVisibility, WaterDetailFilteringStrength);

    private void ApplyWaterSsrQuality(RingTerrainLodCoordinator.QualityPreset preset)
    {
        // Defaults are selected before persisted user overrides are loaded.
        if (preset == RingTerrainLodCoordinator.QualityPreset.Low) { WaterSsrEnabled = false; return; }
        if (preset == RingTerrainLodCoordinator.QualityPreset.Medium) { WaterSsrEnabled = true; WaterSsrCoarseSteps = 12; WaterSsrRefinementSteps = 3; WaterSsrMaxRayDistance = 120; WaterSsrCameraDistance = 250; WaterSsrRoughnessCutoff = .25f; WaterSsrStrength = .50f; return; }
        WaterSsrEnabled = true; WaterSsrCoarseSteps = 24; WaterSsrRefinementSteps = 4; WaterSsrMaxRayDistance = 250; WaterSsrCameraDistance = 450; WaterSsrRoughnessCutoff = .32f; WaterSsrStrength = .65f;
    }

	private void ApplySunshineCloudQuality(RingTerrainLodCoordinator.QualityPreset preset)
	{
		if (_sunshineClouds == null) return;
		(int resolutionScale, int raymarchSteps, int nearShadowResolution,
			int farShadowResolution, float nearShadowUpdatesPerSecond,
			float farShadowUpdatesPerSecond, int shadowVerticalSamples) = preset switch
		{
			RingTerrainLodCoordinator.QualityPreset.Low => (0, 128, 256, 128, 2.0f, 0.25f, 2),
			RingTerrainLodCoordinator.QualityPreset.Medium => (0, 128, 1024, 256, 10.0f, 0.5f, 4),
			RingTerrainLodCoordinator.QualityPreset.High => (0, 128, 1024, 256, 10.0f, 0.75f, 4),
			RingTerrainLodCoordinator.QualityPreset.VeryHigh => (0, 160, 1024, 384, 10.0f, 1.0f, 5),
			RingTerrainLodCoordinator.QualityPreset.UltraVideo => (0, 256, 1024, 512, 10.0f, 1.25f, 6),
			RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => (0, 256, 1536, 768, 10.0f, 1.5f, 8),
			_ => (0, 128, 1024, 256, 10.0f, 0.5f, 4)
		};
		_sunshineClouds.CloudResolutionScale = resolutionScale;
		_sunshineClouds.RaymarchSteps = raymarchSteps;
		_sunshineClouds.GpuNearShadowMapResolution = nearShadowResolution;
		_sunshineClouds.GpuFarShadowMapResolution = farShadowResolution;
		_sunshineClouds.GpuNearShadowUpdatesPerSecond = nearShadowUpdatesPerSecond;
		_sunshineClouds.GpuFarShadowUpdatesPerSecond = farShadowUpdatesPerSecond;
		_sunshineClouds.GpuShadowVerticalSamples = shadowVerticalSamples;
		string[] profileKeys =
		{
			nameof(RingSunshineCloudBridge.CloudResolutionScale),
			nameof(RingSunshineCloudBridge.RaymarchSteps),
			nameof(RingSunshineCloudBridge.GpuNearShadowMapResolution),
			nameof(RingSunshineCloudBridge.GpuFarShadowMapResolution),
			nameof(RingSunshineCloudBridge.GpuNearShadowUpdatesPerSecond),
			nameof(RingSunshineCloudBridge.GpuFarShadowUpdatesPerSecond),
			nameof(RingSunshineCloudBridge.GpuShadowVerticalSamples)
		};
		_sunshineClouds.GameplayCloudDefaults.OnGraphicsPresetApplied(_sunshineClouds, profileKeys);
		// The bridge detects actual resolution changes and retires receivers once.
		// Override application may already have updated the compositor above.
		_sunshineClouds.ApplyTemporalQualityTuning();
	}

    private static Tuning ResolveTuning(RingTerrainLodCoordinator.QualityPreset preset) => preset switch
    {
        RingTerrainLodCoordinator.QualityPreset.Low => new(
            4096, RenderingServer.ShadowQuality.Hard, 4000.0f, 0.0f,
            0.30f, 2800.0f, 5500.0f, 8, 64000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.High,
            8000.0f, 40000.0f, 256, 3500.0f, 0.38f, 0.48f,
            2200.0f, 4500.0f, 4500.0f, 12000.0f, 1,
            0.50f, 768, 12, 64, 0.60f, 2, 1, 1.5f),
        RingTerrainLodCoordinator.QualityPreset.Medium => new(
            4096, RenderingServer.ShadowQuality.SoftVeryLow, 6000.0f, 0.06f,
            0.36f, 3500.0f, 6500.0f, 10, 96000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.High,
            10000.0f, 50000.0f, 512, 6500.0f, 0.45f, 0.58f,
            3500.0f, 7000.0f, 7000.0f, 18000.0f, 2,
            0.75f, 1024, 16, 96, 0.75f, 2, 1, 2.0f),
        RingTerrainLodCoordinator.QualityPreset.High => new(
            8192, RenderingServer.ShadowQuality.SoftLow, 12000.0f, 0.08f,
            0.46f, 6500.0f, 11000.0f, 14, 128000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.High,
            12000.0f, 70000.0f, 768, 10000.0f, 0.56f, 0.68f,
            6500.0f, 12500.0f, 12500.0f, 30000.0f, 2,
            0.75f, 1536, 24, 128, 0.85f, 4, 2, 4.0f),
        RingTerrainLodCoordinator.QualityPreset.VeryHigh => new(
            8192, RenderingServer.ShadowQuality.SoftMedium, 14000.0f, 0.12f,
            0.49f, 8000.0f, 13000.0f, 16, 144000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.VeryHigh,
            24000.0f, 120000.0f, 768, 11000.0f, 0.58f, 0.70f,
            7500.0f, 14500.0f, 14500.0f, 32000.0f, 2,
            1.0f, 2048, 32, 160, 0.92f, 6, 3, 5.0f),
        RingTerrainLodCoordinator.QualityPreset.UltraVideo => new(
            8192, RenderingServer.ShadowQuality.SoftHigh, 16000.0f, 0.16f,
            0.52f, 9000.0f, 15000.0f, 16, 160000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.Ultra,
            28000.0f, 140000.0f, 1024, 12000.0f, 0.62f, 0.74f,
            8500.0f, 16500.0f, 16500.0f, 35000.0f, 2,
            1.0f, 2048, 32, 192, 0.96f, 6, 4, 6.0f),
        RingTerrainLodCoordinator.QualityPreset.Cinematic5090 => new(
            16384, RenderingServer.ShadowQuality.SoftUltra, 24000.0f, 0.22f,
            0.58f, 14000.0f, 23000.0f, 24, 240000.0f,
            global::RingworldTechDemo.RingVolumetricCloudLayer.CloudQuality.Ultra,
            40000.0f, 200000.0f, 2048, 20000.0f, 0.68f, 0.78f,
            12000.0f, 25000.0f, 25000.0f, 50000.0f, 3,
            1.0f, 4096, 64, 256, 1.0f, 8, 6, 8.0f),
        _ => ResolveTuning(RingTerrainLodCoordinator.QualityPreset.Medium)
    };

    private static RingTerrainLodCoordinator.QualityPreset ResolveCommandLinePreset(
        RingTerrainLodCoordinator.QualityPreset fallback)
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.Equals("--graphics-preset=ultra-video", System.StringComparison.OrdinalIgnoreCase))
                return RingTerrainLodCoordinator.QualityPreset.UltraVideo;
            if (argument.Equals("--graphics-preset=cinematic-5090", System.StringComparison.OrdinalIgnoreCase))
                return RingTerrainLodCoordinator.QualityPreset.Cinematic5090;
        }
        return fallback;
    }
}
