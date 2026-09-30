using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Camera-local, deterministic cloud formations instanced from reusable smooth-
/// union implicit surfaces. The scaled-space shell owns the far ring/horizon.
/// </summary>
public partial class RingVolumetricCloudLayer : Node3D
{
    /// <summary>Authoritative persistent simulation clock for every seeded weather consumer.</summary>
    public double CloudSimulationTimeSeconds { get; set; }
    public enum CloudQuality
    {
        Off = 0,
        High = 1,
        VeryHigh = 2,
        Ultra = 3
    }

    public enum WeatherSystemKind
    {
        None = 0,
        CumulusCluster = 1,
        CloudBank = 2,
        MajorFront = 3,
        ExceptionalFront = 4
    }

    public readonly record struct LocalWeatherState(
        int SystemId,
        WeatherSystemKind SystemKind,
        float SystemInfluence,
        float SystemRadiusMeters,
        Vector2 SystemCenterMeters,
        Vector2 SystemRadiiMeters,
        float SystemOrientationRadians,
        float CloudCoverage,
        float PrecipitationPotential,
        Vector2 WindMetersPerSecond,
        CloudFormationProfile DominantFormationProfile,
        float CloudBaseAltitudeMeters,
        float CloudTopAltitudeMeters);

    [Export] public bool Enabled { get; set; } = true;
    [Export] public CloudQuality Quality { get; set; } = CloudQuality.High;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath DistantCloudShellPath { get; set; } = new("../RingCloudLayer");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public bool SunshineCloudsPrimaryRenderer { get; set; } = false;

    [ExportGroup("Fallback cloud band")]
    [Export(PropertyHint.Range, "0,10000,50")] public float CloudBaseMeters { get; set; } = 2000.0f;
    [Export(PropertyHint.Range, "200,25000,50")] public float CloudTopMeters { get; set; } = 6000.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float Coverage { get; set; } = 0.46f;

    [ExportGroup("Merged formation archetypes")]
    [Export(PropertyHint.Range, "3,16,1")] public int ArchetypeCount { get; set; } = 12;
    [Export(PropertyHint.Range, "12,48,2")] public int ArchetypeCellsAround { get; set; } = 26;
    [Export(PropertyHint.Range, "8,32,2")] public int ArchetypeCellsVertical { get; set; } = 14;
    [Export(PropertyHint.Range, "12,48,2")] public int ArchetypeCellsAcross { get; set; } = 22;
    [Export(PropertyHint.Range, "1000,5000,100")] public float FormationCellMeters { get; set; } = 3200.0f;
    [Export(PropertyHint.Range, "300,2500,50")] public float MinimumFormationAroundRadius { get; set; } = 450.0f;
    [Export(PropertyHint.Range, "500,4000,50")] public float MaximumFormationAroundRadius { get; set; } = 850.0f;
    [Export(PropertyHint.Range, "100,1500,25")] public float MinimumFormationHeight { get; set; } = 320.0f;
    [Export(PropertyHint.Range, "200,2500,25")] public float MaximumFormationHeight { get; set; } = 620.0f;
    [Export(PropertyHint.Range, "300,2500,50")] public float MinimumFormationAcrossRadius { get; set; } = 450.0f;
    [Export(PropertyHint.Range, "500,4000,50")] public float MaximumFormationAcrossRadius { get; set; } = 850.0f;
    [Export] public bool CastCloudShadows { get; set; } = true;
    [Export(PropertyHint.Range, "0,0.48,0.01")] public float FormationPositionJitterFraction { get; set; } = 0.42f;
    [Export(PropertyHint.Range, "0,0.9,0.01")] public float WeatherClusteringStrength { get; set; } = 0.72f;
    [Export(PropertyHint.Range, "2,16,1")] public int SmallWeatherClusterCells { get; set; } = 5;
    [Export(PropertyHint.Range, "4,32,1")] public int LargeWeatherClusterCells { get; set; } = 13;

    [ExportGroup("Seamless waterdrop unions")]
    [Export] public bool MergeTouchingFormations { get; set; } = true;
    [Export(PropertyHint.Range, "0,750,25")] public float UnionGapMeters { get; set; } = 200.0f;
    [Export(PropertyHint.Range, "0,1000,25")] public float UnionVerticalGapMeters { get; set; } = 150.0f;
    [Export(PropertyHint.Range, "0,600,25")] public float UnionBlendRadiusMeters { get; set; } = 180.0f;
    [Export(PropertyHint.Range, "2000,30000,500")] public float UnionMaximumRangeMeters { get; set; } = 14000.0f;
    [Export(PropertyHint.Range, "40,300,10")] public float UnionDetailedCellMeters { get; set; } = 140.0f;
    [Export(PropertyHint.Range, "16,72,2")] public int UnionDetailedMaximumCellsPerAxis { get; set; } = 32;
    [Export(PropertyHint.Range, "100,800,25")] public float UnionTransitionCellMeters { get; set; } = 400.0f;
    [Export(PropertyHint.Range, "12,48,2")] public int UnionTransitionMaximumCellsPerAxis { get; set; } = 18;

    [ExportGroup("Macro weather systems")]
    [Export(PropertyHint.Range, "16000,64000,1000")] public float MacroWeatherCellMeters { get; set; } = 32000.0f;
    [Export(PropertyHint.Range, "0.1,0.8,0.01")] public float MacroSystemSpawnChance { get; set; } = 0.34f;
    [Export(PropertyHint.Range, "40000,160000,5000")] public float MaximumMacroSystemRadiusMeters { get; set; } = 110000.0f;
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float MultiCellFormationChance { get; set; } = 0.12f;
    [Export(PropertyHint.Range, "1.5,8.0,0.1")] public float MaximumMultiCellFormationScale { get; set; } = 4.0f;
    [Export] public Vector2 WeatherWindMetersPerSecond { get; set; } = new(2.2f, 0.7f);

    [ExportGroup("Natural weather lifecycles")]
    // Disable for saved-world migration/diagnostics: this retains the static
    // seeded macro-system field while preserving the long-session advection fix.
    [Export] public bool NaturalWeatherLifecyclesEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "30,1800,10")] public float WeatherBirthSeconds { get; set; } = 120.0f;
    [Export(PropertyHint.Range, "30,3600,10")] public float WeatherDevelopSeconds { get; set; } = 300.0f;
    [Export(PropertyHint.Range, "60,14400,10")] public float WeatherMatureSeconds { get; set; } = 900.0f;
    [Export(PropertyHint.Range, "30,3600,10")] public float WeatherDissipateSeconds { get; set; } = 360.0f;
    [Export(PropertyHint.Range, "30,7200,10")] public float WeatherQuietSeconds { get; set; } = 420.0f;
    [Export(PropertyHint.Range, "0,0.45,0.01")] public float WeatherLifecycleVariation { get; set; } = 0.20f;

    [ExportGroup("Mid-range 3D handoff")]
    [Export(PropertyHint.Range, "8,32,2")] public int TransitionCellsAround { get; set; } = 12;
    [Export(PropertyHint.Range, "6,20,2")] public int TransitionCellsVertical { get; set; } = 8;
    [Export(PropertyHint.Range, "8,32,2")] public int TransitionCellsAcross { get; set; } = 10;
    [Export(PropertyHint.Range, "10000,150000,1000")] public float HighTransitionRadiusMeters { get; set; } = 50000.0f;
    [Export(PropertyHint.Range, "10000,200000,1000")] public float VeryHighTransitionRadiusMeters { get; set; } = 90000.0f;
    [Export(PropertyHint.Range, "10000,300000,1000")] public float UltraTransitionRadiusMeters { get; set; } = 140000.0f;
    [Export(PropertyHint.Range, "0.4,0.9,0.01")] public float TransitionFadeInStartFraction { get; set; } = 0.62f;
    [Export(PropertyHint.Range, "0.5,1.0,0.01")] public float TransitionFadeInEndFraction { get; set; } = 0.88f;
    [Export(PropertyHint.Range, "0.5,0.95,0.01")] public float FarShellFadeInStartFraction { get; set; } = 0.76f;

    [ExportGroup("Space cloud handoff")]
    [Export] public bool AltitudeAwareCloudHandoffEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,30000,250")]
    public float SpaceCloudHandoffStartAltitudeMeters { get; set; } = 8000.0f;
    [Export(PropertyHint.Range, "1000,60000,250")]
    public float SpaceCloudHandoffEndAltitudeMeters { get; set; } = 20000.0f;

    [ExportGroup("Conventional cloud shadows")]
    [Export] public bool RetainNearbyConventionalShadowCasters { get; set; } = true;
    [Export(PropertyHint.Range, "1000,30000,250")] public float ConventionalShadowRetentionRadiusMeters { get; set; } = 6500.0f;
    [Export(PropertyHint.Range, "0,4000,100")] public float ConventionalShadowCullMarginMeters { get; set; } = 1500.0f;

    [ExportGroup("Cloud-to-cloud lighting")]
    [Export(PropertyHint.Range, "0,0.7,0.01")] public float CloudSelfShadowStrength { get; set; } = 0.28f;
    [Export(PropertyHint.Range, "0,0.6,0.01")] public float CloudLayerOcclusionStrength { get; set; } = 0.26f;

    [ExportGroup("Projected cloud shadows")]
    [Export] public bool ProjectLongRangeShadows { get; set; } = true;
    [Export(PropertyHint.Range, "128,2048,64")] public int ShadowMapResolution { get; set; } = 512;
    [Export(PropertyHint.Range, "0,0.8,0.01")] public float ProjectedShadowStrength { get; set; } = 0.45f;
    [Export(PropertyHint.Range, "0,0.8,0.01")] public float ProjectedShadowFarStrength { get; set; } = 0.60f;
    [Export(PropertyHint.Range, "500,30000,100")] public float ProjectedShadowNearStartMeters { get; set; } = 1800.0f;
    [Export(PropertyHint.Range, "1000,40000,100")] public float ProjectedShadowNearEndMeters { get; set; } = 3500.0f;
    [Export(PropertyHint.Range, "1000,60000,100")] public float ProjectedShadowFarStrengthStartMeters { get; set; } = 3500.0f;
    [Export(PropertyHint.Range, "1000,120000,100")] public float ProjectedShadowFarStrengthEndMeters { get; set; } = 5000.0f;
    [Export(PropertyHint.Range, "1,4,1")] public int ProjectedShadowBlurRadiusPixels { get; set; } = 2;
    [ExportGroup("Fast Travel")]
    [Export] public bool SuspendLocalCloudRebuildsDuringFastTravel { get; set; } = true;
    [Export(PropertyHint.Range, "100,1000000,100")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750.0f;

    // Preserved for existing inspector/save compatibility. The merged mesh
    // shader maps softness to the firmness of its shared formation lighting.
    [Export(PropertyHint.Range, "0.01,0.2,0.005")] public float Density { get; set; } = 0.065f;
    [Export(PropertyHint.Range, "0.01,0.3,0.005")] public float VolumetricSoftness { get; set; } = 0.085f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float CanopyOpacity { get; set; } = 0.46f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float CanopyCoverage { get; set; } = 0.44f;

    [ExportGroup("Hybrid handoff")]
    [Export(PropertyHint.Range, "1000,50000,500")] public float HighRadiusMeters { get; set; } = 10000.0f;
    [Export(PropertyHint.Range, "1000,50000,500")] public float VeryHighRadiusMeters { get; set; } = 18000.0f;
    [Export(PropertyHint.Range, "1000,75000,500")] public float UltraRadiusMeters { get; set; } = 28000.0f;
    [Export(PropertyHint.Range, "100,2000,50")] public float MaximumCurvatureSagMeters { get; set; } = 250.0f;

    [ExportGroup("Diagnostics")]
    [Export] public Key ToggleKey { get; set; } = Key.K;
    [Export] public bool MeasureGpuFrameTime { get; set; } = false;
    [Export(PropertyHint.Range, "1,10,0.5")] public float TimingReportSeconds { get; set; } = 3.0f;

    public int VisibleFormationCount { get; private set; }
    public int VisibleTransitionFormationCount { get; private set; }
    public int VisiblePuffCount => VisibleFormationCount;
    public int ActiveArchetypeCount => _archetypeInstances.Count;
    public int ActiveTransitionArchetypeCount => _transitionArchetypeInstances.Count;
    public int ArchetypeTriangleCount { get; private set; }
    public int TransitionArchetypeTriangleCount { get; private set; }
    public bool ArchetypesReady => _archetypesReady;
    public string CloudShowcaseLabel { get; private set; } = "No catalog cloud spawned";
    public float ActiveCloudRadiusMeters => _activeRadiusMeters;
    public float TransitionCloudRadiusMeters => _transitionRadiusMeters;
    public int VisibleMultiCellFormationCount { get; private set; }
    public int VisibleWeatherSystemCount { get; private set; }
    public int VisibleLowCloudBankCount { get; private set; }
    public int VisibleStormTowerCount { get; private set; }
    public int VisibleAnvilStormCount { get; private set; }
    public int VisibleUnionClusterCount { get; private set; }
    public int UnionMemberCount { get; private set; }
    public int VisibleCoalescedMassCount => VisibleUnionClusterCount;
    public int AbsorbedFormationCount => Math.Max(0, UnionMemberCount - VisibleUnionClusterCount);
    public int UnionTriangleCount { get; private set; }
    public bool UnionBuildPending => _unionBuildTask != null || _queuedUnionBuild != null;
    public double LastUnionBuildMilliseconds { get; private set; }
    public int VisibleConventionalShadowCasterCount { get; private set; }
    public float CurrentViewerAltitudeAboveTerrainMeters { get; private set; }
    public float LocalMeshCloudOwnership { get; private set; } = 1.0f;
    public double LastCloudPatchBuildMilliseconds { get; private set; }
    public double MaxCloudPatchBuildMilliseconds { get; private set; }
    public double LastWeatherSystemBuildMilliseconds { get; private set; }
    public double LastFormationAssemblyMilliseconds { get; private set; }
    public double LastInstanceInstallMilliseconds { get; private set; }
    public double LastShadowMapBuildMilliseconds { get; private set; }
    public bool FastTravelStreamingActive { get; private set; }
    public int CachedCloudOwnerRebaseCount { get; private set; }
    public int RetainedUnionRefreshCount { get; private set; }

    private RingCanonicalPosition _presentationUnionCenter;
    private Vector2 _presentationUnionRadii;
    private bool _hasPresentationUnion;

    /// <summary>Returns a real generated seamless-union cluster for presentation cameras.</summary>
    public bool TryGetPresentationUnionWaypoint(
        out RingCanonicalPosition center,
        out Vector2 horizontalRadiiMeters)
    {
        center = _presentationUnionCenter;
        horizontalRadiiMeters = _presentationUnionRadii;
        return _hasPresentationUnion && VisibleUnionClusterCount > 0;
    }

    private readonly record struct FormationInstance(
        Transform3D Transform,
        Color CustomData,
        float AroundDeltaMeters,
        float AcrossDeltaMeters,
        float AroundRadiusMeters,
        float AcrossRadiusMeters,
        float AltitudeMeters,
        float RotationRadians,
        int WeatherSystemId,
        float ScaleMultiplier,
        CloudFormationProfile Profile,
        float BaseAltitudeMeters,
        float TopAltitudeMeters);

    private readonly record struct ArchetypedFormation(
        FormationInstance Formation,
        int Archetype,
        int SourceAroundCell,
        int SourceAcrossCell);

    private sealed record FormationUnionGroup(
        ArchetypedFormation[] Members,
        FormationInstance BoundsProxy);

    private sealed record FormationUnionPlan(
        List<ArchetypedFormation> Singles,
        List<FormationUnionGroup> Groups);

    private sealed record UnionInstallState(
        List<FormationInstance>[] DetailedSingles,
        List<FormationInstance>[] TransitionSingles,
        List<FormationInstance>[] ConventionalShadowSingles,
        int DetailedMassCount,
        int TransitionMassCount,
        int ConventionalShadowMassCount,
        int Generation);

    private sealed record UnionBuildRequest(
        CloudUnionClusterInput[] Clusters,
        UnionInstallState InstallState,
        int Generation);

    private sealed record CloudShadowBuildInput(
        FormationInstance[] Formations,
        RingCanonicalPosition Player,
        Basis GridBasis,
        Vector3 SunDirection,
        Vector3 CenterWorld,
        double Circumference,
        float RadiusMeters,
        int Resolution,
        int BlurRadius,
        int Generation);

    private readonly record struct CloudShadowBuildData(
        byte[] PackedData,
        Vector3 CenterWorld,
        Vector3 AxisAround,
        Vector3 AxisAcross,
        float RadiusMeters,
        int Resolution,
        int OccupiedTexels,
        byte MaximumMaskValue,
        double BuildMilliseconds,
        int Generation);

    private readonly record struct FormationVerticalProfile(
        float BaseAltitudeMeters,
        float TopAltitudeMeters,
        float AroundScaleMultiplier,
        float AcrossScaleMultiplier);

    private readonly record struct MacroWeatherSample(
        int SystemId,
        WeatherSystemKind Kind,
        float CombinedInfluence,
        float StrongestInfluence,
        double SourceAroundMeters,
        double SourceAcrossMeters,
        float RadiusMeters,
        float MinorRadiusMeters,
        float OrientationRadians,
        float Density,
        float PrecipitationPotential);

    private readonly record struct WeatherSystemDefinition(
        int SystemId,
        WeatherSystemKind Kind,
        double SourceAroundMeters,
        double SourceAcrossMeters,
        float MajorRadiusMeters,
        float MinorRadiusMeters,
        float OrientationRadians,
        float OrientationCosine,
        float OrientationSine,
        double InverseMajorRadiusSquared,
        double InverseMinorRadiusSquared,
        float Density,
        float PrecipitationPotential);

    private sealed record CloudArchetypeBuildSet(
        CloudMetaballMeshData[] Detailed,
        CloudMetaballMeshData[] Transition,
        CloudFormationProfile[] Profiles);

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private WorldEnvironment? _worldEnvironment;
    private DirectionalLight3D? _sunLight;
    private RingCloudLayer? _distantShell;
    private RingScaledFarViewport? _scaledFarViewport;
    private Node3D? _cloudContainer;
    private Node3D? _showcaseContainer;
    private ShaderMaterial? _material;
    private ShaderMaterial? _showcaseMaterial;
    private ShaderMaterial? _transitionMaterial;
    private ShaderMaterial? _unionMaterial;
    private ShaderMaterial? _unionTransitionMaterial;
    private MeshInstance3D? _unionShadowInstance;
    private MeshInstance3D? _unionDetailedInstance;
    private MeshInstance3D? _unionTransitionInstance;
    private readonly List<MultiMeshInstance3D> _archetypeInstances = new();
    private readonly List<ArrayMesh> _archetypeMeshes = new();
    private readonly List<MultiMeshInstance3D> _transitionArchetypeInstances = new();
    private readonly List<ArrayMesh> _transitionArchetypeMeshes = new();
    private readonly List<MultiMeshInstance3D> _shadowArchetypeInstances = new();
    private CloudFormationProfile[] _archetypeProfiles = Array.Empty<CloudFormationProfile>();
    private MultiMeshInstance3D? _showcaseInstance;
    private CloudShowcaseRequest? _showcaseRequest;
    private CancellationTokenSource? _archetypeCancellation;
    private Task<CloudArchetypeBuildSet>? _archetypeBuildTask;
    private CancellationTokenSource? _unionCancellation;
    private Task<CloudUnionPatchData>? _unionBuildTask;
    private UnionBuildRequest? _activeUnionBuild;
    private UnionBuildRequest? _queuedUnionBuild;
    private int _unionGeneration;
    private Task<CloudShadowBuildData>? _shadowBuildTask;
    private CloudShadowBuildInput? _queuedShadowBuild;
    private int _shadowBuildGeneration;
    private ImageTexture? _shadowTexture;
    private const float CloudOcclusionMinimumAltitudeMeters = -2000.0f;
    private const float CloudOcclusionMaximumAltitudeMeters = 20000.0f;
    private bool _archetypesReady;
    private bool _archetypeFailureReported;
    private Rid _viewportRid;
    private bool _runtimeEnabled;
    private bool _meshCloudsRequestedEnabled = true;
    private bool _externalVolumetricRendererOwnsLocalClouds;
    private bool _externalVolumetricCloudsVisible;
    private float _externalVolumetricHandoffStartMeters = 7000.0f;
    private float _externalVolumetricHandoffEndMeters = 12000.0f;
    private bool _farOwnershipConfigured;
    private bool _localMeshCloudsVisible = true;
    private bool _cloudPatchRebuildRequired;
    private float _activeRadiusMeters;
    private float _handoffStartMeters;
    private float _transitionRadiusMeters;
    private float _transitionFadeInStartMeters;
    private float _transitionFadeInEndMeters;
    private float _farShellHandoffStartMeters;
    private int _lastAroundCell = int.MinValue;
    private int _lastAcrossCell = int.MinValue;
    private Vector3 _lastGridGlobalPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
    private Vector3 _lastCloudOwnerGridGlobalPosition =
        new(float.PositiveInfinity, 0.0f, 0.0f);
    private CloudQuality _lastBuiltQuality = (CloudQuality)(-1);
    private double _timingElapsed;
    private double _gpuFrameTotal;
    private int _gpuFrameSamples;
    private bool _shadowDiagnosticsReported;
    private bool _hasMovingShadowTransform;
    private Vector3 _shadowBaseCenterWorld;
    private Vector3 _shadowBaseAxisAround = Vector3.Forward;
    private Vector3 _shadowBaseAxisAcross = Vector3.Up;
    private Vector3 _lastAppliedShadowOwnerOrigin =
        new(float.PositiveInfinity, 0.0f, 0.0f);
    private long _lastHandoffAroundCell = long.MinValue;
    private int _lastHandoffAcrossCell = int.MinValue;
    private ulong _lastHandoffEditVersion = ulong.MaxValue;
    private float _cachedHandoffSurfaceHeight;

    private readonly record struct CloudShowcaseRequest(
        CloudFormationProfile Profile,
        Vector2 CenterMeters,
        float OrientationRadians,
        float WidthMeters,
        float DepthMeters,
        float BaseAltitudeMeters,
        float TopAltitudeMeters,
        Color CustomData,
        string Label);

    public override void _Ready()
    {
        _externalVolumetricRendererOwnsLocalClouds = SunshineCloudsPrimaryRenderer;
        if (!CloudPhysicalCatalog.Validate(out string catalogSummary))
            GD.PushError(catalogSummary);
        else
            GD.Print(catalogSummary);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        _distantShell = GetNodeOrNull<RingCloudLayer>(DistantCloudShellPath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        if (_explorer == null || _grid == null || _worldEnvironment?.Environment == null)
        {
            GD.PushWarning("Merged mesh clouds disabled: explorer, grid, or environment is missing.");
            return;
        }

        if (_grid.GetCanonicalRingCircumferenceMeters64() > int.MaxValue)
        {
            Visible = false;
            SetProcess(false);
            GD.Print("Merged mesh clouds disabled: Mega Ring uses the scaled far backdrop and local-bubble presentation paths.");
            return;
        }

        Shader? shader = GD.Load<Shader>("res://materials/ring_mesh_cloud.gdshader");
        if (shader == null)
        {
            GD.PushError("Merged mesh cloud shader could not be loaded.");
            return;
        }

        _material = new ShaderMaterial { Shader = shader };
        _showcaseMaterial = new ShaderMaterial { Shader = shader };
        _transitionMaterial = new ShaderMaterial { Shader = shader };
        _unionMaterial = new ShaderMaterial { Shader = shader };
        _unionTransitionMaterial = new ShaderMaterial { Shader = shader };
        RingAtmosphereModel.ConfigureMaterial(
            _material,
            _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        RingAtmosphereModel.ConfigureMaterial(
            _showcaseMaterial,
            _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        RingAtmosphereModel.ConfigureMaterial(
            _transitionMaterial,
            _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        RingAtmosphereModel.ConfigureMaterial(
            _unionMaterial,
            _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        RingAtmosphereModel.ConfigureMaterial(
            _unionTransitionMaterial,
            _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        _unionMaterial.SetShaderParameter("use_vertex_cloud_data", true);
        _unionMaterial.SetShaderParameter("world_space_union_mesh", true);
        _unionTransitionMaterial.SetShaderParameter("use_vertex_cloud_data", true);
        _unionTransitionMaterial.SetShaderParameter("world_space_union_mesh", true);
        _cloudContainer = new Node3D
        {
            Name = "MergedMeshCloudArchetypes",
            TopLevel = true
        };
        AddChild(_cloudContainer);
        _cloudContainer.GlobalTransform = Transform3D.Identity;
        _showcaseContainer = new Node3D
        {
            Name = "PhysicalCloudShowcase",
            TopLevel = true
        };
        AddChild(_showcaseContainer);
        _showcaseContainer.GlobalTransform = Transform3D.Identity;
        _lastCloudOwnerGridGlobalPosition = _grid.GlobalPosition;
        _unionShadowInstance = CreateUnionInstance(
            "SeamlessCloudUnionShadows",
            _unionMaterial,
            GeometryInstance3D.ShadowCastingSetting.On);
        _unionDetailedInstance = CreateUnionInstance(
            "SeamlessCloudUnions",
            _unionMaterial,
            GeometryInstance3D.ShadowCastingSetting.Off);
        _unionTransitionInstance = CreateUnionInstance(
            "SeamlessCloudUnionTransitions",
            _unionTransitionMaterial,
            GeometryInstance3D.ShadowCastingSetting.Off);

        // The merged-mesh path does not require volumetric fog. A separate
        // renderer can claim local cloud ownership after this node is ready.
        if (!_externalVolumetricRendererOwnsLocalClouds)
        {
            _worldEnvironment.Environment.VolumetricFogEnabled = false;
            _worldEnvironment.Environment.VolumetricFogDensity = 0.0f;
        }
        _scaledFarViewport?.SetLocalVolumetricCloudsEnabled(false);

        _viewportRid = GetViewport().GetViewportRid();
        if (MeasureGpuFrameTime)
            RenderingServer.ViewportSetMeasureRenderTime(_viewportRid, true);

        ApplyQualityConfiguration();
        SetCloudsEnabled(Enabled && Quality != CloudQuality.Off);
        UpdateMaterialUniforms();
        if (_externalVolumetricRendererOwnsLocalClouds)
        {
            GD.Print("Cloud macro-weather source ready; legacy mesh fallback is idle.");
        }
        else
        {
            StartArchetypeBuild();
            GD.Print(
                $"Merged mesh clouds queued: quality={Quality}, archetypes={Mathf.Clamp(ArchetypeCount, 3, 16)}, "
                + $"grid={ArchetypeCellsAround}x{ArchetypeCellsVertical}x{ArchetypeCellsAcross}, "
                + $"band={CloudBaseMeters:0}-{CloudTopMeters:0}m, radius={_activeRadiusMeters:0}m.");
        }
    }

    private MeshInstance3D CreateUnionInstance(
        string name,
        ShaderMaterial material,
        GeometryInstance3D.ShadowCastingSetting shadowMode)
    {
        var instance = new MeshInstance3D
        {
            Name = name,
            MaterialOverride = material,
            CastShadow = shadowMode,
            IgnoreOcclusionCulling = true,
            ExtraCullMargin = shadowMode
                == GeometryInstance3D.ShadowCastingSetting.On
                ? Mathf.Max(0.0f, ConventionalShadowCullMarginMeters)
                : 0.0f,
            Visible = false
        };
        _cloudContainer!.AddChild(instance);
        RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
        return instance;
    }

    public override void _ExitTree()
    {
        RingCloudShadowProjector.Disable();
        _archetypeCancellation?.Cancel();
        _archetypeCancellation?.Dispose();
        _archetypeCancellation = null;
        _unionCancellation?.Cancel();
        _unionCancellation?.Dispose();
        _unionCancellation = null;
        _shadowBuildGeneration++;
        _queuedShadowBuild = null;
        _shadowBuildTask = null;
    }

    public override void _Process(double delta)
    {
        // Advance before the Sunshine early-out: Sunshine, the far atlas and
        // mesh fallback must all query the same continuous weather timeline.
        CloudSimulationTimeSeconds += Math.Max(0.0, delta);
        ProcessSharedLightning();
        if (_grid == null || _explorer == null || _cloudContainer == null) return;
        // Sunshine queries this node's deterministic weather field directly.
        // When it owns local rendering and no physical showcase was requested,
        // the legacy mesh renderer has no state to advance: no archetypes are
        // resident, no mesh/shadow build should run, and shader animation is
        // entirely owned by Sunshine. Keep the far-shell ownership handshake
        // available for runtime handoffs, but skip the legacy presentation work.
        if (IsLegacyMeshFallbackIdle())
        {
            if (!_farOwnershipConfigured) SyncFarCloudOwnership();
            return;
        }
        UpdateFastTravelStreamingState();
        UpdateAltitudeAwareCloudOwnership(force: false);
        PreserveCachedCloudAlignmentDuringFastTravel();
        AdvanceCloudWind(delta);
        ServiceArchetypeBuild();
        UpdateCloudShowcaseTransform();
        ServiceUnionBuild();
        ServiceProjectedShadowBuild();
        if (!_farOwnershipConfigured) SyncFarCloudOwnership();
        if (!FastTravelStreamingActive && _localMeshCloudsVisible)
        {
            RebuildCloudPatch(force: _cloudPatchRebuildRequired);
            _cloudPatchRebuildRequired = false;
        }
        UpdateMovingProjectedShadowTransform();
        UpdateMaterialUniforms();
        UpdateGpuTiming(delta);
    }

    private bool IsLegacyMeshFallbackIdle() =>
        _externalVolumetricRendererOwnsLocalClouds
        && _showcaseRequest == null
        && _showcaseInstance == null
        && _archetypeBuildTask == null
        && _unionBuildTask == null
        && _shadowBuildTask == null;

    private void UpdateAltitudeAwareCloudOwnership(bool force)
    {
        if (_grid == null || _explorer == null || _cloudContainer == null) return;

        CurrentViewerAltitudeAboveTerrainMeters = ResolveViewerAltitudeAboveTerrain(
            _explorer.CanonicalPosition);
        float nextOwnership = 1.0f;
        if (AltitudeAwareCloudHandoffEnabled)
        {
            float start = Mathf.Max(0.0f, SpaceCloudHandoffStartAltitudeMeters);
            float end = Mathf.Max(start + 1.0f, SpaceCloudHandoffEndAltitudeMeters);
            float altitudeBlend = Mathf.Clamp(
                (CurrentViewerAltitudeAboveTerrainMeters - start) / (end - start),
                0.0f,
                1.0f);
            altitudeBlend = altitudeBlend * altitudeBlend * (3.0f - 2.0f * altitudeBlend);
            nextOwnership = 1.0f - altitudeBlend;
        }

        bool ownershipChanged = force
            || Mathf.Abs(nextOwnership - LocalMeshCloudOwnership) > 0.0005f;
        LocalMeshCloudOwnership = nextOwnership;
        bool nextVisible = _runtimeEnabled && LocalMeshCloudOwnership > 0.001f;
        bool visibilityChanged = force || nextVisible != _localMeshCloudsVisible;
        if (visibilityChanged)
        {
            bool wasVisible = _localMeshCloudsVisible;
            _localMeshCloudsVisible = nextVisible;
            _cloudContainer.Visible = nextVisible;
            if (!nextVisible)
            {
                CancelUnionBuild(clearQueued: true, clearInstalled: false);
                _shadowBuildGeneration++;
                _queuedShadowBuild = null;
                _hasMovingShadowTransform = false;
                SetCloudLayerOcclusionEnabled(false);
                RingCloudShadowProjector.Disable();
            }
            else if (!wasVisible)
            {
                ResetCloudOwnerForCurrentGeometry();
                _lastAroundCell = int.MinValue;
                _lastAcrossCell = int.MinValue;
                _lastGridGlobalPosition = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
                _cloudPatchRebuildRequired = true;
            }
        }

        if (ownershipChanged || visibilityChanged)
        {
            _farOwnershipConfigured = false;
            SyncFarCloudOwnership();
        }
    }

    private float ResolveViewerAltitudeAboveTerrain(in RingCanonicalPosition canonical)
    {
        if (_grid?.TerrainSampler == null)
            return Mathf.Max(0.0f, (float)canonical.VerticalMeters);

        const int cacheCellMeters = 256;
        long around = (long)Math.Round(WrapAround(canonical.AroundMeters));
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)canonical.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        long aroundCell = (long)Math.Floor(around / (double)cacheCellMeters);
        int acrossCell = Mathf.FloorToInt(across / (float)cacheCellMeters);
        ulong editVersion = _grid.LodSurfaceEditVersion;
        if (aroundCell != _lastHandoffAroundCell
            || acrossCell != _lastHandoffAcrossCell
            || editVersion != _lastHandoffEditVersion)
        {
			RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
			_cachedHandoffSurfaceHeight = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
				? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
				: edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
            _lastHandoffAroundCell = aroundCell;
            _lastHandoffAcrossCell = acrossCell;
            _lastHandoffEditVersion = editVersion;
        }

        return Mathf.Max(
            0.0f,
            (float)(canonical.VerticalMeters - _cachedHandoffSurfaceHeight));
    }

    private void UpdateFastTravelStreamingState()
    {
        if (_explorer == null) return;
        bool next = SuspendLocalCloudRebuildsDuringFastTravel
            && _explorer.NoclipEnabled
            && _explorer.FastTravelSpeedMetersPerSecond
                >= Mathf.Max(100.0f, FastTravelSpeedThresholdMetersPerSecond);
        if (next == FastTravelStreamingActive) return;
        FastTravelStreamingActive = next;
        if (next)
        {
            CancelUnionBuild(clearQueued: true, clearInstalled: false);
            _shadowBuildGeneration++;
            _queuedShadowBuild = null;
            GD.Print(
                "Fast-travel local cloud patch rebuilds suspended; "
                + "the scaled/far cloud tier remains active.");
        }
        else
        {
            ResetCloudOwnerForCurrentGeometry();
            GD.Print(
                "Fast-travel local cloud patch updates resumed; "
                + "the normal cell/origin guard will retain or refresh the cached patch "
                + $"after {CachedCloudOwnerRebaseCount} cached owner rebases.");
        }
    }

    private void PreserveCachedCloudAlignmentDuringFastTravel()
    {
        if (_grid == null || _cloudContainer == null) return;
        Vector3 currentGridPosition = _grid.GlobalPosition;
        if (!_lastCloudOwnerGridGlobalPosition.IsFinite())
        {
            _lastCloudOwnerGridGlobalPosition = currentGridPosition;
            return;
        }

        Vector3 gridDelta =
            currentGridPosition - _lastCloudOwnerGridGlobalPosition;
        _lastCloudOwnerGridGlobalPosition = currentGridPosition;
        if (!FastTravelStreamingActive || gridDelta.IsZeroApprox()) return;

        // The cloud owner is top-level because its instance transforms are
        // camera-relative world transforms. During suspended fast travel it
        // therefore does not inherit the root's floating-origin shift. Move
        // the cached owner by the exact same delta so the existing formations
        // remain stationary in canonical space without rebuilding the patch.
        Transform3D transform = _cloudContainer.GlobalTransform;
        transform.Origin += gridDelta;
        _cloudContainer.GlobalTransform = transform;
        CachedCloudOwnerRebaseCount++;
    }

    private void ResetCloudOwnerForCurrentGeometry()
    {
        if (_cloudContainer != null)
            _cloudContainer.GlobalTransform = Transform3D.Identity;
        if (_grid != null)
            _lastCloudOwnerGridGlobalPosition = _grid.GlobalPosition;
    }

    private void AdvanceCloudWind(double delta)
    {
        if (_cloudContainer == null
            || _grid == null
            || _explorer == null
            || !_archetypesReady
            || !_runtimeEnabled
            || !_localMeshCloudsVisible)
        {
            return;
        }

        float dt = (float)Math.Clamp(delta, 0.0, 0.25);
        if (dt <= 0.0f || WeatherWindMetersPerSecond.IsZeroApprox()) return;
        double circumference = Math.Max(
            1.0,
            _grid.GetCanonicalRingCircumferenceMeters64());
        double angle = WrapAround(
            _explorer.CanonicalPosition.AroundMeters,
            circumference) / circumference * Math.Tau;
        Vector3 tangentLocal = new(
            -(float)Math.Sin(angle),
            0.0f,
            (float)Math.Cos(angle));
        Basis gridBasis = _grid.GlobalBasis.Orthonormalized();
        Vector3 aroundAxis = (gridBasis * tangentLocal).Normalized();
        Vector3 acrossAxis = (gridBasis * Vector3.Up).Normalized();
        Transform3D transform = _cloudContainer.GlobalTransform;
        transform.Origin += aroundAxis
                * (WeatherWindMetersPerSecond.X * dt)
            + acrossAxis * (WeatherWindMetersPerSecond.Y * dt);
        _cloudContainer.GlobalTransform = transform;
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent
            || !keyEvent.Pressed
            || keyEvent.Echo
            || keyEvent.Keycode != ToggleKey)
        {
            return;
        }
        SetCloudsEnabled(!_runtimeEnabled);
        GetViewport().SetInputAsHandled();
        GD.Print($"Merged mesh cloud A/B: {(_runtimeEnabled ? "ON" : "OFF")} ({Quality}, key {ToggleKey}).");
    }

    public void SetQuality(CloudQuality quality)
    {
        Quality = quality;
        ApplyQualityConfiguration();
        SetCloudsEnabled(Enabled && quality != CloudQuality.Off);
        RebuildCloudPatch(force: true);
    }

    public void SetCloudsEnabled(bool enabled)
    {
        _meshCloudsRequestedEnabled = enabled;
        _runtimeEnabled = enabled
            && Quality != CloudQuality.Off
            && _cloudContainer != null
            && !_externalVolumetricRendererOwnsLocalClouds;
        UpdateAltitudeAwareCloudOwnership(force: true);
        if (!_externalVolumetricRendererOwnsLocalClouds
            && _worldEnvironment?.Environment is { } environment)
        {
            environment.VolumetricFogEnabled = false;
            environment.VolumetricFogDensity = 0.0f;
        }
        _scaledFarViewport?.SetLocalVolumetricCloudsEnabled(false);
        _farOwnershipConfigured = false;
        SyncFarCloudOwnership();
        if (_runtimeEnabled && _localMeshCloudsVisible)
        {
            RebuildCloudPatch(force: true);
        }
        else
        {
            RingCloudShadowProjector.Disable();
        }
        ResetGpuTiming();
    }

    /// <summary>
    /// Lets a camera-local density renderer replace only the detailed mesh
    /// tier while this class remains the deterministic macro-weather source.
    /// The far curved shell is handed back automatically when the local volume
    /// is suspended for fast travel or the upper-atmosphere transition.
    /// </summary>
    public void SetExternalLocalVolumetricCloudsState(
        bool ownsLocalClouds,
        bool localVolumeVisible,
        float handoffStartMeters,
        float handoffEndMeters)
    {
        bool meshFallbackWasSuppressed = _externalVolumetricRendererOwnsLocalClouds;
        _externalVolumetricRendererOwnsLocalClouds = ownsLocalClouds;
        _externalVolumetricCloudsVisible = ownsLocalClouds && localVolumeVisible;
        _externalVolumetricHandoffStartMeters = Mathf.Max(0.0f, handoffStartMeters);
        _externalVolumetricHandoffEndMeters = Mathf.Max(
            _externalVolumetricHandoffStartMeters + 1.0f,
            handoffEndMeters);
        if (ownsLocalClouds)
            ReleaseFallbackMeshArchetypes();
        SetCloudsEnabled(_meshCloudsRequestedEnabled);
        if (meshFallbackWasSuppressed
            && !ownsLocalClouds
            && _cloudContainer != null)
        {
            StartArchetypeBuild();
        }
    }

    private void ReleaseFallbackMeshArchetypes()
    {
        _archetypeCancellation?.Cancel();
        CancelUnionBuild(clearQueued: true, clearInstalled: true);
        ClearPhysicalCloudShowcase();
        foreach (MultiMeshInstance3D instance in _archetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        foreach (MultiMeshInstance3D instance in _transitionArchetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        foreach (MultiMeshInstance3D instance in _shadowArchetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        _archetypeInstances.Clear();
        _archetypeMeshes.Clear();
        _transitionArchetypeInstances.Clear();
        _transitionArchetypeMeshes.Clear();
        _shadowArchetypeInstances.Clear();
        _archetypeProfiles = Array.Empty<CloudFormationProfile>();
        _archetypesReady = false;
        ArchetypeTriangleCount = 0;
        TransitionArchetypeTriangleCount = 0;
    }

    /// <summary>
    /// Spawns one catalog cloud at its representative physical dimensions.
    /// The preview is independent of normal local-cloud ownership so it remains
    /// available while Sunshine renders the procedural cloud volume.
    /// </summary>
    public void SpawnPhysicalCloudShowcase(
        CloudFormationProfile profile,
        Vector2 centerMeters,
        float orientationRadians)
    {
        CloudPhysicalDefinition physical = CloudPhysicalCatalog.Get(profile);
        float widthMeters = GeometricMean(
            physical.MinimumWidthMeters,
            physical.MaximumWidthMeters);
        float depthFraction = profile switch
        {
            CloudFormationProfile.Cirrus => 0.14f,
            CloudFormationProfile.Cirrostratus
                or CloudFormationProfile.Altostratus
                or CloudFormationProfile.Stratus
                or CloudFormationProfile.Nimbostratus => 0.76f,
            _ => 0.68f
        };
        float thicknessMeters = GeometricMean(
            physical.MinimumThicknessMeters,
            physical.MaximumThicknessMeters);
        float availableAltitude = Math.Max(
            100.0f,
            physical.MaximumAltitudeMeters - physical.MinimumAltitudeMeters);
        thicknessMeters = Mathf.Clamp(thicknessMeters, 100.0f, availableAltitude);
        float maximumBase = Math.Max(
            physical.MinimumAltitudeMeters,
            physical.MaximumAltitudeMeters - thicknessMeters);
        float baseAltitude = profile == CloudFormationProfile.Cumulonimbus
            ? physical.MinimumAltitudeMeters
            : (physical.MinimumAltitudeMeters + maximumBase) * 0.5f;
        float topAltitude = Math.Min(
            physical.MaximumAltitudeMeters,
            baseAltitude + thicknessMeters);
        float precipitation = profile switch
        {
            CloudFormationProfile.Nimbostratus => 0.78f,
            CloudFormationProfile.Cumulonimbus => 0.88f,
            _ => 0.0f
        };
        float brightness = physical.Density switch
        {
            CloudDensityClass.Thin => 1.02f,
            CloudDensityClass.Medium => 0.95f,
            CloudDensityClass.MediumToDense => 0.90f,
            CloudDensityClass.VeryDense => 0.80f,
            CloudDensityClass.ExtremelyDense => 0.74f,
            _ => 0.95f
        };
        string label = $"{physical.Name} ({physical.Abbreviation}) — "
            + $"{widthMeters / 1000.0f:0.0} km wide, "
            + $"{(topAltitude - baseAltitude) / 1000.0f:0.0} km thick, "
            + $"altitude {baseAltitude / 1000.0f:0.0}-{topAltitude / 1000.0f:0.0} km";
        _showcaseRequest = new CloudShowcaseRequest(
            profile,
            centerMeters,
            orientationRadians,
            widthMeters,
            widthMeters * depthFraction,
            baseAltitude,
            topAltitude,
            new Color(
                brightness,
                0.37f,
                precipitation,
                CloudFormationProfiles.ToShaderProfile(profile) / 3.0f),
            label);
        CloudShowcaseLabel = $"Loading {label}";

        if (!_archetypesReady) StartArchetypeBuild();
        TryInstallCloudShowcase();
    }

    public void ClearPhysicalCloudShowcase()
    {
        _showcaseRequest = null;
        CloudShowcaseLabel = "No catalog cloud spawned";
        if (_showcaseInstance != null && GodotObject.IsInstanceValid(_showcaseInstance))
            _showcaseInstance.QueueFree();
        _showcaseInstance = null;
    }

    private void TryInstallCloudShowcase()
    {
        if (!_archetypesReady
            || _showcaseRequest is not { } request
            || _showcaseContainer == null
            || _showcaseMaterial == null)
        {
            return;
        }

        int archetype = ResolveArchetypeIndex(request.Profile, 0.5f);
        if (archetype < 0 || archetype >= _archetypeMeshes.Count) return;
        if (_showcaseInstance != null && GodotObject.IsInstanceValid(_showcaseInstance))
            _showcaseInstance.QueueFree();

        var multiMesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseCustomData = true,
            Mesh = _archetypeMeshes[archetype],
            InstanceCount = 1,
            VisibleInstanceCount = 1
        };
        multiMesh.SetInstanceCustomData(0, request.CustomData);
        _showcaseInstance = new MultiMeshInstance3D
        {
            Name = $"CatalogCloud_{request.Profile}",
            Multimesh = multiMesh,
            MaterialOverride = _showcaseMaterial,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            IgnoreOcclusionCulling = true
        };
        _showcaseContainer.AddChild(_showcaseInstance);
        RenderingServer.InstanceSetIgnoreCulling(_showcaseInstance.GetInstance(), true);
        CloudShowcaseLabel = request.Label;
        UpdateCloudShowcaseTransform();
        GD.Print($"Physical cloud showcase spawned: {request.Label}.");
    }

    private void UpdateCloudShowcaseTransform()
    {
        if (_showcaseRequest is not { } request
            || _showcaseContainer == null
            || _showcaseInstance?.Multimesh == null
            || _grid == null)
        {
            return;
        }

        const float archetypeMinimumY = -0.72f;
        const float archetypeMaximumY = 1.02f;
        const float archetypeWidth = 3.10f;
        const float archetypeDepth = 2.60f;
        float verticalScale = Math.Max(
            100.0f,
            request.TopAltitudeMeters - request.BaseAltitudeMeters)
            / (archetypeMaximumY - archetypeMinimumY);
        float altitude = request.BaseAltitudeMeters - archetypeMinimumY * verticalScale;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double around = WrapAround(request.CenterMeters.X, circumference);
        double angle = around / circumference * Math.Tau;
        Basis gridBasis = _grid.GlobalBasis.Orthonormalized();
        Vector3 radialOutLocal = new(
            (float)Math.Cos(angle),
            0.0f,
            (float)Math.Sin(angle));
        Vector3 tangent = (gridBasis * new Vector3(
            -radialOutLocal.Z,
            0.0f,
            radialOutLocal.X)).Normalized();
        Vector3 surfaceUp = (gridBasis * -radialOutLocal).Normalized();
        Vector3 acrossAxis = (gridBasis * Vector3.Up).Normalized();
        Basis orientation = new Basis(surfaceUp, request.OrientationRadians)
            * new Basis(tangent, surfaceUp, acrossAxis);
        Basis scaled = new(
            orientation.X * (request.WidthMeters / archetypeWidth),
            orientation.Y * verticalScale,
            orientation.Z * (request.DepthMeters / archetypeDepth));
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 position = geometry.CanonicalToWorld(
            around,
            altitude,
            request.CenterMeters.Y);
        Transform3D transform = new(scaled, position);

        _showcaseContainer.GlobalTransform = Transform3D.Identity;
        _showcaseInstance.Multimesh.SetInstanceTransform(0, transform);
        Vector3 extent = scaled.X.Abs() * 1.55f
            + scaled.Y.Abs() * 1.02f
            + scaled.Z.Abs() * 1.30f;
        _showcaseInstance.CustomAabb = new Aabb(position - extent, extent * 2.0f);
    }

    private static float GeometricMean(float minimum, float maximum)
    {
        return MathF.Sqrt(Math.Max(1.0f, minimum) * Math.Max(1.0f, maximum));
    }

    private void StartArchetypeBuild()
    {
        if (_grid == null || _archetypeBuildTask != null || _archetypesReady) return;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        int count = Mathf.Clamp(ArchetypeCount, 3, 16);
        int cellsX = Math.Clamp(ArchetypeCellsAround, 12, 48);
        int cellsY = Math.Clamp(ArchetypeCellsVertical, 8, 32);
        int cellsZ = Math.Clamp(ArchetypeCellsAcross, 12, 48);
        int transitionCellsX = Math.Clamp(TransitionCellsAround, 8, 32);
        int transitionCellsY = Math.Clamp(TransitionCellsVertical, 6, 20);
        int transitionCellsZ = Math.Clamp(TransitionCellsAcross, 8, 32);
        CloudFormationProfile[] profiles = BuildArchetypeProfiles(count);
        _archetypeCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _archetypeCancellation.Token;
        _archetypeBuildTask = Task.Run(() =>
        {
            var detailed = new CloudMetaballMeshData[count];
            var transition = new CloudMetaballMeshData[count];
            for (int index = 0; index < count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                detailed[index] = CloudMetaballMeshGenerator.BuildArchetype(
                    seed,
                    index,
                    profiles[index],
                    cellsX,
                    cellsY,
                    cellsZ,
                    cancellation);
                transition[index] = CloudMetaballMeshGenerator.BuildArchetype(
                    seed,
                    index,
                    profiles[index],
                    transitionCellsX,
                    transitionCellsY,
                    transitionCellsZ,
                    cancellation);
            }
            return new CloudArchetypeBuildSet(detailed, transition, profiles);
        }, cancellation);
    }

    private static CloudFormationProfile[] BuildArchetypeProfiles(int count)
    {
        count = Math.Max(3, count);
        var profiles = new CloudFormationProfile[count];
        CloudFormationProfile[] physical = CloudFormationProfiles.PhysicalArchetypes;
        for (int index = 0; index < count; index++)
        {
            profiles[index] = index < physical.Length
                ? physical[index]
                : index == physical.Length
                    ? CloudFormationProfile.StormTower
                    : physical[(index - physical.Length - 1) % physical.Length];
        }
        return profiles;
    }

    private void ServiceArchetypeBuild()
    {
        if (_archetypeBuildTask == null || !_archetypeBuildTask.IsCompleted) return;
        Task<CloudArchetypeBuildSet> completed = _archetypeBuildTask;
        _archetypeBuildTask = null;
        try
        {
            if (completed.IsCanceled) return;
            InstallArchetypes(completed.Result);
        }
        catch (Exception exception)
        {
            if (!_archetypeFailureReported)
            {
                _archetypeFailureReported = true;
                GD.PushError($"Merged cloud archetype build failed: {exception}");
            }
        }
        finally
        {
            _archetypeCancellation?.Dispose();
            _archetypeCancellation = null;
            if (!_externalVolumetricRendererOwnsLocalClouds && !_archetypesReady)
                StartArchetypeBuild();
        }
    }

    private void InstallArchetypes(CloudArchetypeBuildSet buildSet)
    {
        if (_externalVolumetricRendererOwnsLocalClouds
            || _cloudContainer == null
            || _material == null
            || _transitionMaterial == null)
        {
            return;
        }
        foreach (MultiMeshInstance3D instance in _archetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        foreach (MultiMeshInstance3D instance in _transitionArchetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        foreach (MultiMeshInstance3D instance in _shadowArchetypeInstances)
        {
            if (GodotObject.IsInstanceValid(instance)) instance.QueueFree();
        }
        _archetypeInstances.Clear();
        _archetypeMeshes.Clear();
        _transitionArchetypeInstances.Clear();
        _transitionArchetypeMeshes.Clear();
        _shadowArchetypeInstances.Clear();
        _archetypeProfiles = Array.Empty<CloudFormationProfile>();
        ArchetypeTriangleCount = 0;
        TransitionArchetypeTriangleCount = 0;

        int detailedTriangleCount = 0;
        int transitionTriangleCount = 0;
        InstallArchetypeSet(
            buildSet.Detailed,
            _material,
            _archetypeInstances,
            _archetypeMeshes,
            "MergedCloudArchetype",
            CastCloudShadows && !RetainNearbyConventionalShadowCasters,
            ref detailedTriangleCount);
        InstallArchetypeSet(
            buildSet.Transition,
            _transitionMaterial,
            _transitionArchetypeInstances,
            _transitionArchetypeMeshes,
            "TransitionCloudArchetype",
            false,
            ref transitionTriangleCount);
        if (CastCloudShadows && RetainNearbyConventionalShadowCasters)
        {
            InstallShadowArchetypeSet(
                _archetypeMeshes,
                _material,
                _shadowArchetypeInstances);
        }
        ArchetypeTriangleCount = detailedTriangleCount;
        TransitionArchetypeTriangleCount = transitionTriangleCount;
        _archetypeProfiles = buildSet.Profiles;

        _archetypesReady = _archetypeInstances.Count > 0
            && _transitionArchetypeInstances.Count == _archetypeInstances.Count
            && (!CastCloudShadows
                || !RetainNearbyConventionalShadowCasters
                || _shadowArchetypeInstances.Count == _archetypeInstances.Count)
            && _archetypeProfiles.Length == _archetypeInstances.Count;
        _lastAroundCell = int.MinValue;
        _lastAcrossCell = int.MinValue;
        RebuildCloudPatch(force: true);
        TryInstallCloudShowcase();
        RunContractSelfTest();
    }

    private void InstallArchetypeSet(
        CloudMetaballMeshData[] data,
        ShaderMaterial material,
        List<MultiMeshInstance3D> instances,
        List<ArrayMesh> meshes,
        string namePrefix,
        bool castShadows,
        ref int triangleCount)
    {
        if (_cloudContainer == null) return;
        for (int index = 0; index < data.Length; index++)
        {
            CloudMetaballMeshData source = data[index];
            if (source.Vertices.Length == 0) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = source.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = source.Normals;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            meshes.Add(mesh);
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = mesh,
                InstanceCount = 0,
                VisibleInstanceCount = 0
            };
            var instance = new MultiMeshInstance3D
            {
                Name = $"{namePrefix}{index + 1}",
                Multimesh = multiMesh,
                MaterialOverride = material,
                CastShadow = castShadows
                    ? GeometryInstance3D.ShadowCastingSetting.On
                    : GeometryInstance3D.ShadowCastingSetting.Off,
                IgnoreOcclusionCulling = true
            };
            _cloudContainer.AddChild(instance);
            RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
            instances.Add(instance);
            triangleCount += source.TriangleCount;
        }
    }

    private void InstallShadowArchetypeSet(
        IReadOnlyList<ArrayMesh> meshes,
        ShaderMaterial material,
        List<MultiMeshInstance3D> instances)
    {
        if (_cloudContainer == null) return;
        for (int index = 0; index < meshes.Count; index++)
        {
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = meshes[index],
                InstanceCount = 0,
                VisibleInstanceCount = 0
            };
            var instance = new MultiMeshInstance3D
            {
                Name = $"RetainedCloudShadowArchetype{index + 1}",
                Multimesh = multiMesh,
                MaterialOverride = material,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.On,
                IgnoreOcclusionCulling = true,
                ExtraCullMargin = Mathf.Max(0.0f, ConventionalShadowCullMarginMeters)
            };
            _cloudContainer.AddChild(instance);
            RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
            instances.Add(instance);
        }
    }

    private void ApplyQualityConfiguration()
    {
        if (_grid == null) return;
        float requestedRadius = Quality switch
        {
            CloudQuality.VeryHigh => VeryHighRadiusMeters,
            CloudQuality.Ultra => UltraRadiusMeters,
            _ => HighRadiusMeters
        };
        double ringRadius = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
        float curvatureSafeRadius = (float)Math.Sqrt(
            2.0 * ringRadius * Math.Max(1.0f, MaximumCurvatureSagMeters));
        _activeRadiusMeters = Mathf.Max(2000.0f, Mathf.Min(requestedRadius, curvatureSafeRadius));
        _handoffStartMeters = _activeRadiusMeters * 0.82f;
        float requestedTransitionRadius = Quality switch
        {
            CloudQuality.VeryHigh => VeryHighTransitionRadiusMeters,
            CloudQuality.Ultra => UltraTransitionRadiusMeters,
            _ => HighTransitionRadiusMeters
        };
        _transitionRadiusMeters = Mathf.Max(_activeRadiusMeters + FormationCellMeters * 2.0f, requestedTransitionRadius);
        _transitionFadeInStartMeters = _activeRadiusMeters
            * Mathf.Clamp(TransitionFadeInStartFraction, 0.4f, 0.9f);
        _transitionFadeInEndMeters = _activeRadiusMeters
            * Mathf.Clamp(
                Mathf.Max(TransitionFadeInStartFraction + 0.05f, TransitionFadeInEndFraction),
                0.5f,
                1.0f);
        _farShellHandoffStartMeters = _transitionRadiusMeters
            * Mathf.Clamp(FarShellFadeInStartFraction, 0.5f, 0.95f);
        _lastBuiltQuality = (CloudQuality)(-1);
        if (_material != null)
        {
            _material.SetShaderParameter("handoff_in_start_meters", 0.0f);
            _material.SetShaderParameter("handoff_in_end_meters", 1.0f);
            _material.SetShaderParameter("handoff_start_meters", _handoffStartMeters);
            _material.SetShaderParameter("handoff_end_meters", _activeRadiusMeters);
            _material.SetShaderParameter("edge_firmness", Mathf.Clamp(
                1.0f - VolumetricSoftness * 2.0f,
                0.55f,
                0.98f));
        }
        if (_showcaseMaterial != null)
        {
            _showcaseMaterial.SetShaderParameter("handoff_in_start_meters", 0.0f);
            _showcaseMaterial.SetShaderParameter("handoff_in_end_meters", 1.0f);
            _showcaseMaterial.SetShaderParameter("handoff_start_meters", 1000000.0f);
            _showcaseMaterial.SetShaderParameter("handoff_end_meters", 1000001.0f);
            _showcaseMaterial.SetShaderParameter("edge_firmness", Mathf.Clamp(
                1.0f - VolumetricSoftness * 2.0f,
                0.55f,
                0.98f));
        }
        if (_transitionMaterial != null)
        {
            _transitionMaterial.SetShaderParameter("handoff_in_start_meters", _transitionFadeInStartMeters);
            _transitionMaterial.SetShaderParameter("handoff_in_end_meters", _transitionFadeInEndMeters);
            _transitionMaterial.SetShaderParameter("handoff_start_meters", _farShellHandoffStartMeters);
            _transitionMaterial.SetShaderParameter("handoff_end_meters", _transitionRadiusMeters);
            _transitionMaterial.SetShaderParameter("edge_firmness", Mathf.Clamp(
                1.0f - VolumetricSoftness * 1.5f,
                0.62f,
                0.98f));
        }
        if (_unionMaterial != null)
        {
            _unionMaterial.SetShaderParameter("handoff_in_start_meters", 0.0f);
            _unionMaterial.SetShaderParameter("handoff_in_end_meters", 1.0f);
            _unionMaterial.SetShaderParameter("handoff_start_meters", _handoffStartMeters);
            _unionMaterial.SetShaderParameter("handoff_end_meters", _activeRadiusMeters);
            _unionMaterial.SetShaderParameter("edge_firmness", Mathf.Clamp(
                1.0f - VolumetricSoftness * 2.0f,
                0.55f,
                0.98f));
        }
        if (_unionTransitionMaterial != null)
        {
            _unionTransitionMaterial.SetShaderParameter(
                "handoff_in_start_meters",
                _transitionFadeInStartMeters);
            _unionTransitionMaterial.SetShaderParameter(
                "handoff_in_end_meters",
                _transitionFadeInEndMeters);
            _unionTransitionMaterial.SetShaderParameter(
                "handoff_start_meters",
                _farShellHandoffStartMeters);
            _unionTransitionMaterial.SetShaderParameter(
                "handoff_end_meters",
                _transitionRadiusMeters);
            _unionTransitionMaterial.SetShaderParameter("edge_firmness", Mathf.Clamp(
                1.0f - VolumetricSoftness * 1.5f,
                0.62f,
                0.98f));
        }
        _distantShell?.SetLocalVolumetricHandoff(
            _runtimeEnabled,
            _farShellHandoffStartMeters,
            _transitionRadiusMeters);
        _farOwnershipConfigured = false;
        SyncFarCloudOwnership();
    }

    private void SyncFarCloudOwnership()
    {
        bool meshOwnershipEnabled = _runtimeEnabled && _localMeshCloudsVisible;
        bool localOwnershipEnabled = meshOwnershipEnabled || _externalVolumetricCloudsVisible;
        bool sunshineOwnsCloudRendering = _externalVolumetricRendererOwnsLocalClouds;
        float ownership = meshOwnershipEnabled
            ? Mathf.Clamp(LocalMeshCloudOwnership, 0.0f, 1.0f)
            : 1.0f;
        float effectiveTransitionRadius = _externalVolumetricCloudsVisible
            ? _externalVolumetricHandoffEndMeters
            : Mathf.Max(1.0f, _transitionRadiusMeters * ownership);
        float effectiveFarHandoffStart = _externalVolumetricCloudsVisible
            ? Mathf.Min(
                effectiveTransitionRadius - 0.5f,
                _externalVolumetricHandoffStartMeters)
            : Mathf.Min(
                effectiveTransitionRadius - 0.5f,
                Mathf.Max(0.0f, _farShellHandoffStartMeters * ownership));

        if (_distantShell != null)
        {
            _distantShell.Visible = !sunshineOwnsCloudRendering;
            if (!sunshineOwnsCloudRendering)
            {
                _distantShell.SetLocalVolumetricHandoff(
                    localOwnershipEnabled,
                    effectiveFarHandoffStart,
                    effectiveTransitionRadius);
            }
        }
        if (_scaledFarViewport == null || !_scaledFarViewport.IsNodeReady()) return;
        // Sunshine owns the near volume only. The scaled shell remains the
        // sole owner of macro weather beyond the clipmap handoff.
        _scaledFarViewport.SetFallbackCloudShellEnabled(true);
        _scaledFarViewport.ConfigureMeshCloudOwnership(
            localOwnershipEnabled,
            effectiveTransitionRadius,
            CloudBaseMeters,
            CloudTopMeters,
            effectiveFarHandoffStart);
        _farOwnershipConfigured = true;
    }

    private void RebuildCloudPatch(bool force)
    {
        if (FastTravelStreamingActive
            || !_runtimeEnabled
            || !_localMeshCloudsVisible
            || !_archetypesReady
            || _grid == null
            || _explorer == null
            || _archetypeInstances.Count == 0
            || _transitionArchetypeInstances.Count != _archetypeInstances.Count)
        {
            return;
        }

        // Newly assembled formation transforms already include the current
        // floating-origin offset. Any temporary owner translation used to
        // preserve a suspended cached patch must not be applied twice.
        ResetCloudOwnerForCurrentGeometry();

        float cellSize = Mathf.Max(1000.0f, FormationCellMeters);
        RingCanonicalPosition player = _explorer.CanonicalPosition;
        double weatherTimeSeconds = CloudSimulationTimeSeconds;
        double windAroundMeters = WeatherWindMetersPerSecond.X * weatherTimeSeconds;
        double windAcrossMeters = WeatherWindMetersPerSecond.Y * weatherTimeSeconds;
        int aroundCell = Mathf.FloorToInt(
            (float)(WrapAround(player.AroundMeters - windAroundMeters) / cellSize));
        int acrossCell = Mathf.FloorToInt(
            (float)((player.AcrossMeters - windAcrossMeters) / cellSize));
        bool originShifted = !_lastGridGlobalPosition.IsFinite()
            || _lastGridGlobalPosition.DistanceSquaredTo(_grid.GlobalPosition) > 0.25f;
        if (!force
            && aroundCell == _lastAroundCell
            && acrossCell == _lastAcrossCell
            && _lastBuiltQuality == Quality
            && !originShifted)
        {
            return;
        }

        _lastAroundCell = aroundCell;
        _lastAcrossCell = acrossCell;
        _lastGridGlobalPosition = _grid.GlobalPosition;
        _lastBuiltQuality = Quality;
        ulong patchBuildStart = Time.GetTicksUsec();
        int formationLimit = Quality switch
        {
            CloudQuality.VeryHigh => 3072,
            CloudQuality.Ultra => 6144,
            _ => 1024
        };
        int cellRadius = Mathf.CeilToInt(_transitionRadiusMeters / cellSize) + 1;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        double circumference = Math.Max(1.0, geometry.CircumferenceMeters);
        Basis gridBasis = geometry.GridGlobalTransform.Basis.Orthonormalized();
        int aroundCellCount = Math.Max(
            1,
            Mathf.CeilToInt((float)(circumference / cellSize)));
        ulong stageStart = Time.GetTicksUsec();
        List<WeatherSystemDefinition> nearbyWeatherSystems = BuildNearbyWeatherSystems(
            player.AroundMeters,
            player.AcrossMeters,
            weatherTimeSeconds,
            seed,
            _transitionRadiusMeters);
        LastWeatherSystemBuildMilliseconds = (Time.GetTicksUsec() - stageStart) / 1000.0;
        stageStart = Time.GetTicksUsec();
        var detailedByArchetype = new List<FormationInstance>[_archetypeInstances.Count];
        var transitionByArchetype = new List<FormationInstance>[_archetypeInstances.Count];
        var conventionalShadowByArchetype = new List<FormationInstance>[_archetypeInstances.Count];
        var shadowFormations = new List<FormationInstance>();
        var rawCandidates = new List<ArchetypedFormation>();
        var visibleWeatherSystems = new HashSet<int>();
        for (int i = 0; i < detailedByArchetype.Length; i++)
        {
            detailedByArchetype[i] = new List<FormationInstance>();
            transitionByArchetype[i] = new List<FormationInstance>();
            conventionalShadowByArchetype[i] = new List<FormationInstance>();
        }
        int formationCount = 0;
        int detailedCount = 0;
        int transitionCount = 0;
        int multiCellCount = 0;
        int lowBankCount = 0;
        int stormTowerCount = 0;
        int anvilStormCount = 0;

        for (int dz = -cellRadius; dz <= cellRadius && formationCount < formationLimit; dz++)
        for (int dx = -cellRadius; dx <= cellRadius && formationCount < formationLimit; dx++)
        {
            int candidateAroundCell = PositiveModulo(aroundCell + dx, aroundCellCount);
            int candidateAcrossCell = acrossCell + dz;
            float smallWeather = SampleWeatherNoise(
                candidateAroundCell,
                candidateAcrossCell,
                seed,
                Mathf.Max(2, SmallWeatherClusterCells),
                30);
            float largeWeather = SampleWeatherNoise(
                candidateAroundCell,
                candidateAcrossCell,
                seed,
                Mathf.Max(4, LargeWeatherClusterCells),
                40);
            float weather = smallWeather * 0.62f + largeWeather * 0.38f;
            float jitterFraction = Mathf.Clamp(FormationPositionJitterFraction, 0.0f, 0.48f);
            double aroundJitter = (Hash01(candidateAroundCell, candidateAcrossCell, seed, 9) - 0.5)
                * 2.0
                * jitterFraction
                * cellSize;
            double acrossJitter = (Hash01(candidateAroundCell, candidateAcrossCell, seed, 10) - 0.5)
                * 2.0
                * jitterFraction
                * cellSize;
            double around = WrapAround(
                (candidateAroundCell + 0.5) * cellSize
                + aroundJitter
                + windAroundMeters);
            double across = (candidateAcrossCell + 0.5) * cellSize
                + acrossJitter
                + windAcrossMeters;
            MacroWeatherSample macroWeather = EvaluateMacroWeather(
                around,
                across,
                weatherTimeSeconds,
                nearbyWeatherSystems);
            float localCoverage = ResolveLocalCloudCoverage(weather, macroWeather);
            if (Hash01(candidateAroundCell, candidateAcrossCell, seed, 0) > localCoverage)
                continue;

            double aroundDelta = ShortestAroundDelta(
                player.AroundMeters,
                around,
                circumference);
            double acrossDelta = across - player.AcrossMeters;
            if (aroundDelta * aroundDelta + acrossDelta * acrossDelta
                > _transitionRadiusMeters * _transitionRadiusMeters)
            {
                continue;
            }
            if (across < 0.0 || across >= _grid.RingAcrossWidthMeters)
                continue;

            CloudFormationProfile profile = ResolveFormationProfile(
                candidateAroundCell,
                candidateAcrossCell,
                seed,
                macroWeather);
            if (!ShouldPlacePhysicalCloud(
                    candidateAroundCell,
                    candidateAcrossCell,
                    seed,
                    cellSize,
                    profile))
            {
                continue;
            }
            int archetype = ResolveArchetypeIndex(
                profile,
                Hash01(candidateAroundCell, candidateAcrossCell, seed, 1));
            FormationInstance formation = CreateFormationInstance(
                candidateAroundCell,
                candidateAcrossCell,
                around,
                across,
                (float)aroundDelta,
                (float)acrossDelta,
                seed,
                macroWeather,
                profile,
                geometry,
                gridBasis,
                circumference);
            rawCandidates.Add(new ArchetypedFormation(
                formation,
                archetype,
                candidateAroundCell,
                candidateAcrossCell));
            formationCount++;
        }

        FormationUnionPlan unionPlan = BuildFormationUnionPlan(
            rawCandidates,
            player,
            geometry,
            gridBasis,
            circumference,
            seed);
        if (unionPlan.Groups.Count > 0)
        {
            FormationUnionGroup presentationGroup = unionPlan.Groups[0];
            foreach (FormationUnionGroup candidate in unionPlan.Groups)
                if (candidate.Members.Length > presentationGroup.Members.Length)
                    presentationGroup = candidate;
            FormationInstance proxy = presentationGroup.BoundsProxy;
            _presentationUnionCenter = new RingCanonicalPosition(
                player.AroundMeters + proxy.AroundDeltaMeters,
                proxy.AltitudeMeters,
                player.AcrossMeters + proxy.AcrossDeltaMeters).Wrap(circumference);
            _presentationUnionRadii = new Vector2(
                proxy.AroundRadiusMeters,
                proxy.AcrossRadiusMeters);
            _hasPresentationUnion = true;
        }
        float conventionalShadowRadius = Mathf.Clamp(
            ConventionalShadowRetentionRadiusMeters,
            1000.0f,
            _activeRadiusMeters);
        foreach (ArchetypedFormation candidate in rawCandidates)
        {
            FormationInstance formation = candidate.Formation;
            if (formation.ScaleMultiplier > 1.45f) multiCellCount++;
            if (formation.Profile == CloudFormationProfile.LowCloudBank) lowBankCount++;
            if (formation.Profile == CloudFormationProfile.StormTower) stormTowerCount++;
            if (formation.Profile == CloudFormationProfile.AnvilStorm) anvilStormCount++;
            if (formation.WeatherSystemId != 0)
                visibleWeatherSystems.Add(formation.WeatherSystemId);
            shadowFormations.Add(formation);
            AddFormationToRenderBuckets(
                candidate,
                detailedByArchetype,
                transitionByArchetype,
                conventionalShadowByArchetype,
                conventionalShadowRadius,
                ref detailedCount,
                ref transitionCount);
        }
        foreach (FormationUnionGroup group in unionPlan.Groups)
            shadowFormations.Add(group.BoundsProxy);

        // Keep the original instanced formations visible while their seamless
        // union surface is extracted on a worker. Once a union exists, retain
        // it until an event-driven replacement is ready instead of reverting
        // to visibly intersecting member meshes.
        LastFormationAssemblyMilliseconds = (Time.GetTicksUsec() - stageStart) / 1000.0;
        stageStart = Time.GetTicksUsec();
        bool retainInstalledUnion = unionPlan.Groups.Count > 0
            && HasInstalledUnionSurface();
        if (retainInstalledUnion) RetainedUnionRefreshCount++;
        if (!retainInstalledUnion)
        {
            ClearUnionMeshes();
            InstallFormationInstances(
                _archetypeInstances,
                _archetypeMeshes,
                detailedByArchetype,
                _activeRadiusMeters);
            InstallFormationInstances(
                _transitionArchetypeInstances,
                _transitionArchetypeMeshes,
                transitionByArchetype,
                _transitionRadiusMeters);
            VisibleConventionalShadowCasterCount = InstallRetainedShadowInstances(
                _shadowArchetypeInstances,
                _archetypeMeshes,
                conventionalShadowByArchetype,
                _explorer.GlobalPosition,
                Mathf.Max(0.0f, ConventionalShadowCullMarginMeters));
        }
        LastInstanceInstallMilliseconds = (Time.GetTicksUsec() - stageStart) / 1000.0;
        VisibleFormationCount = detailedCount;
        VisibleTransitionFormationCount = transitionCount;
        VisibleMultiCellFormationCount = multiCellCount;
        VisibleWeatherSystemCount = visibleWeatherSystems.Count;
        VisibleLowCloudBankCount = lowBankCount;
        VisibleStormTowerCount = stormTowerCount;
        VisibleAnvilStormCount = anvilStormCount;

        if (unionPlan.Groups.Count > 0)
        {
            var finalDetailed = CreateFormationBuckets();
            var finalTransition = CreateFormationBuckets();
            var finalShadows = CreateFormationBuckets();
            int finalDetailedCount = 0;
            int finalTransitionCount = 0;
            foreach (ArchetypedFormation single in unionPlan.Singles)
            {
                AddFormationToRenderBuckets(
                    single,
                    finalDetailed,
                    finalTransition,
                    finalShadows,
                    conventionalShadowRadius,
                    ref finalDetailedCount,
                    ref finalTransitionCount);
            }

            var clusterInputs = new List<CloudUnionClusterInput>(unionPlan.Groups.Count);
            int unionDetailedCount = 0;
            int unionTransitionCount = 0;
            int unionShadowCount = 0;
            foreach (FormationUnionGroup group in unionPlan.Groups)
            {
                FormationInstance proxy = group.BoundsProxy;
                float nearestAround = Mathf.Max(
                    0.0f,
                    Mathf.Abs(proxy.AroundDeltaMeters)
                        - proxy.AroundRadiusMeters);
                float nearestAcross = Mathf.Max(
                    0.0f,
                    Mathf.Abs(proxy.AcrossDeltaMeters)
                        - proxy.AcrossRadiusMeters);
                float nearestDistanceSquared =
                    nearestAround * nearestAround
                    + nearestAcross * nearestAcross;
                float centerDistanceSquared =
                    proxy.AroundDeltaMeters * proxy.AroundDeltaMeters
                    + proxy.AcrossDeltaMeters * proxy.AcrossDeltaMeters;
                CloudUnionRenderBands bands = CloudUnionRenderBands.None;
                if (nearestDistanceSquared
                    <= _activeRadiusMeters * _activeRadiusMeters)
                {
                    if (CastCloudShadows
                        && RetainNearbyConventionalShadowCasters
                        && nearestDistanceSquared
                            <= conventionalShadowRadius * conventionalShadowRadius)
                    {
                        bands |= CloudUnionRenderBands.DetailedShadow;
                        unionShadowCount++;
                    }
                    else
                    {
                        bands |= CloudUnionRenderBands.Detailed;
                    }
                    unionDetailedCount++;
                }
                if (centerDistanceSquared
                    >= _transitionFadeInStartMeters * _transitionFadeInStartMeters)
                {
                    bands |= CloudUnionRenderBands.Transition;
                    unionTransitionCount++;
                }
                if (bands == CloudUnionRenderBands.None) continue;

                var members = new CloudUnionMember[group.Members.Length];
                for (int index = 0; index < group.Members.Length; index++)
                {
                    ArchetypedFormation member = group.Members[index];
                    members[index] = new CloudUnionMember(
                        seed,
                        member.Archetype,
                        member.Formation.Profile,
                        member.Formation.Transform,
                        member.Formation.CustomData);
                }
                clusterInputs.Add(new CloudUnionClusterInput(members, bands));
            }

            int generation = ++_unionGeneration;
            if (retainInstalledUnion)
            {
                InstallFormationInstances(
                    _archetypeInstances,
                    _archetypeMeshes,
                    finalDetailed,
                    _activeRadiusMeters);
                InstallFormationInstances(
                    _transitionArchetypeInstances,
                    _transitionArchetypeMeshes,
                    finalTransition,
                    _transitionRadiusMeters);
                VisibleConventionalShadowCasterCount =
                    InstallRetainedShadowInstances(
                        _shadowArchetypeInstances,
                        _archetypeMeshes,
                        finalShadows,
                        _explorer.GlobalPosition,
                        Mathf.Max(
                            0.0f,
                            ConventionalShadowCullMarginMeters));
                VisibleFormationCount =
                    finalDetailedCount + unionDetailedCount;
                VisibleTransitionFormationCount =
                    finalTransitionCount + unionTransitionCount;
            }
            ScheduleUnionBuild(new UnionBuildRequest(
                clusterInputs.ToArray(),
                new UnionInstallState(
                    finalDetailed,
                    finalTransition,
                    finalShadows,
                    finalDetailedCount + unionDetailedCount,
                    finalTransitionCount + unionTransitionCount,
                    CountBucketEntries(finalShadows) + unionShadowCount,
                    generation),
                generation));
        }
        else
        {
            CancelUnionBuild(clearQueued: true);
            UnionTriangleCount = 0;
            LastUnionBuildMilliseconds = 0.0;
        }

        UpdateProjectedShadowMap(player, shadowFormations);
        LastCloudPatchBuildMilliseconds = (Time.GetTicksUsec() - patchBuildStart) / 1000.0;
        MaxCloudPatchBuildMilliseconds = Math.Max(
            MaxCloudPatchBuildMilliseconds,
            LastCloudPatchBuildMilliseconds);
    }

    private List<FormationInstance>[] CreateFormationBuckets()
    {
        var buckets = new List<FormationInstance>[_archetypeInstances.Count];
        for (int index = 0; index < buckets.Length; index++)
            buckets[index] = new List<FormationInstance>();
        return buckets;
    }

    private void AddFormationToRenderBuckets(
        in ArchetypedFormation candidate,
        List<FormationInstance>[] detailed,
        List<FormationInstance>[] transition,
        List<FormationInstance>[] conventionalShadows,
        float conventionalShadowRadius,
        ref int detailedCount,
        ref int transitionCount)
    {
        FormationInstance formation = candidate.Formation;
        int archetype = candidate.Archetype;
        if (archetype < 0 || archetype >= detailed.Length) return;
        float distanceSquared =
            formation.AroundDeltaMeters * formation.AroundDeltaMeters
            + formation.AcrossDeltaMeters * formation.AcrossDeltaMeters;
        if (distanceSquared <= _activeRadiusMeters * _activeRadiusMeters)
        {
            if (_shadowArchetypeInstances.Count == _archetypeInstances.Count
                && distanceSquared
                    <= conventionalShadowRadius * conventionalShadowRadius)
            {
                conventionalShadows[archetype].Add(formation);
            }
            else
            {
                detailed[archetype].Add(formation);
            }
            detailedCount++;
        }
        if (distanceSquared
            >= _transitionFadeInStartMeters * _transitionFadeInStartMeters)
        {
            transition[archetype].Add(formation);
            transitionCount++;
        }
    }

    private static int CountBucketEntries(List<FormationInstance>[] buckets)
    {
        int count = 0;
        foreach (List<FormationInstance> bucket in buckets) count += bucket.Count;
        return count;
    }

    private void ClearUnionMeshes()
    {
        ClearUnionMesh(_unionShadowInstance);
        ClearUnionMesh(_unionDetailedInstance);
        ClearUnionMesh(_unionTransitionInstance);
    }

    private bool HasInstalledUnionSurface() =>
        HasInstalledUnionSurface(_unionShadowInstance)
        || HasInstalledUnionSurface(_unionDetailedInstance)
        || HasInstalledUnionSurface(_unionTransitionInstance);

    private static bool HasInstalledUnionSurface(MeshInstance3D? instance) =>
        instance is { Visible: true, Mesh: not null };

    private static void ClearUnionMesh(MeshInstance3D? instance)
    {
        if (instance == null) return;
        instance.Visible = false;
        instance.Mesh = null;
    }

    private void ScheduleUnionBuild(UnionBuildRequest request)
    {
        if (request.Clusters.Length == 0)
        {
            CancelUnionBuild(clearQueued: true);
            return;
        }
        if (_unionBuildTask != null)
        {
            _unionCancellation?.Cancel();
            _queuedUnionBuild = request;
            return;
        }
        StartUnionBuild(request);
    }

    private void StartUnionBuild(UnionBuildRequest request)
    {
        _activeUnionBuild = request;
        _unionCancellation = new CancellationTokenSource();
        CancellationToken cancellation = _unionCancellation.Token;
        float detailedCell = UnionDetailedCellMeters;
        int detailedCap = UnionDetailedMaximumCellsPerAxis;
        float transitionCell = UnionTransitionCellMeters;
        int transitionCap = UnionTransitionMaximumCellsPerAxis;
        float blendRadius = UnionBlendRadiusMeters;
        _unionBuildTask = Task.Run(() =>
            CloudFormationUnionMeshGenerator.BuildPatch(
                request.Clusters,
                detailedCell,
                detailedCap,
                transitionCell,
                transitionCap,
                blendRadius,
                cancellation),
            cancellation);
    }

    private void CancelUnionBuild(
        bool clearQueued,
        bool clearInstalled = true)
    {
        _unionGeneration++;
        _unionCancellation?.Cancel();
        if (clearQueued) _queuedUnionBuild = null;
        if (clearInstalled) ClearUnionMeshes();
    }

    private void ServiceUnionBuild()
    {
        if (_unionBuildTask == null || !_unionBuildTask.IsCompleted) return;
        Task<CloudUnionPatchData> completed = _unionBuildTask;
        UnionBuildRequest? request = _activeUnionBuild;
        _unionBuildTask = null;
        _activeUnionBuild = null;
        try
        {
            if (!completed.IsCanceled
                && request != null
                && request.Generation == _unionGeneration)
            {
                InstallUnionPatch(completed.Result, request.InstallState);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer canonical patch superseded this worker result.
        }
        catch (Exception exception)
        {
            GD.PushError($"Seamless cloud union build failed: {exception}");
        }
        finally
        {
            _unionCancellation?.Dispose();
            _unionCancellation = null;
        }

        if (_queuedUnionBuild is { } queued)
        {
            _queuedUnionBuild = null;
            StartUnionBuild(queued);
        }
    }

    private void InstallUnionPatch(
        in CloudUnionPatchData patch,
        UnionInstallState install)
    {
        if (_explorer == null || install.Generation != _unionGeneration) return;
        ulong started = Time.GetTicksUsec();
        InstallFormationInstances(
            _archetypeInstances,
            _archetypeMeshes,
            install.DetailedSingles,
            _activeRadiusMeters);
        InstallFormationInstances(
            _transitionArchetypeInstances,
            _transitionArchetypeMeshes,
            install.TransitionSingles,
            _transitionRadiusMeters);
        InstallRetainedShadowInstances(
            _shadowArchetypeInstances,
            _archetypeMeshes,
            install.ConventionalShadowSingles,
            _explorer.GlobalPosition,
            Mathf.Max(0.0f, ConventionalShadowCullMarginMeters));
        InstallUnionSurface(_unionShadowInstance, patch.DetailedShadow);
        InstallUnionSurface(_unionDetailedInstance, patch.Detailed);
        InstallUnionSurface(_unionTransitionInstance, patch.Transition);
        VisibleFormationCount = install.DetailedMassCount;
        VisibleTransitionFormationCount = install.TransitionMassCount;
        VisibleConventionalShadowCasterCount = install.ConventionalShadowMassCount;
        UnionTriangleCount = patch.DetailedShadow.TriangleCount
            + patch.Detailed.TriangleCount
            + patch.Transition.TriangleCount;
        LastUnionBuildMilliseconds = patch.BuildMilliseconds;
        LastInstanceInstallMilliseconds =
            (Time.GetTicksUsec() - started) / 1000.0;
        GD.Print(
            $"Seamless cloud unions installed: clusters={patch.ClusterCount}, "
            + $"members={patch.MemberCount}, triangles={UnionTriangleCount}, "
            + $"retainedRefreshes={RetainedUnionRefreshCount}, "
            + $"workerMs={LastUnionBuildMilliseconds:0.00}, "
            + $"installMs={LastInstanceInstallMilliseconds:0.00}.");
    }

    private static void InstallUnionSurface(
        MeshInstance3D? instance,
        in CloudUnionSurfaceData surface)
    {
        if (instance == null) return;
        if (surface.Vertices.Length == 0)
        {
            ClearUnionMesh(instance);
            return;
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = surface.Vertices;
        arrays[(int)Mesh.ArrayType.Normal] = surface.Normals;
        arrays[(int)Mesh.ArrayType.Color] = surface.Colors;
        arrays[(int)Mesh.ArrayType.TexUV2] = surface.Uv2;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        instance.Mesh = mesh;
        instance.Visible = true;
    }

    private FormationInstance CreateFormationInstance(
        int aroundCell,
        int acrossCell,
        double around,
        double across,
        float aroundDelta,
        float acrossDelta,
        int seed,
        in MacroWeatherSample macroWeather,
        CloudFormationProfile profile,
        in RingLodGeometrySnapshot geometry,
        in Basis gridBasis,
        double circumference)
    {
        FormationVerticalProfile verticalProfile = ResolveFormationVerticalProfile(
            aroundCell,
            acrossCell,
            seed,
            macroWeather,
            profile);
        const float archetypeMinimumY = -0.72f;
        const float archetypeMaximumY = 1.02f;
        const float archetypeHeight = archetypeMaximumY - archetypeMinimumY;
        float verticalScale = Mathf.Max(
            100.0f,
            verticalProfile.TopAltitudeMeters - verticalProfile.BaseAltitudeMeters)
            / archetypeHeight;
        float baseAltitude = verticalProfile.BaseAltitudeMeters;
        float topAltitude = verticalProfile.TopAltitudeMeters;
        float altitude = baseAltitude - archetypeMinimumY * verticalScale;
        double angle = WrapAround(around, circumference) / circumference * Math.Tau;
        Vector3 radialOutLocal = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
        Vector3 tangentLocal = new(-radialOutLocal.Z, 0.0f, radialOutLocal.X);
        Vector3 surfaceUpLocal = -radialOutLocal;
        Vector3 tangent = (gridBasis * tangentLocal).Normalized();
        Vector3 surfaceUp = (gridBasis * surfaceUpLocal).Normalized();
        Vector3 acrossAxis = (gridBasis * Vector3.Up).Normalized();
        float randomRotation = (Hash01(aroundCell, acrossCell, seed, 3) - 0.5f) * 1.35f;
        float rotation = macroWeather.SystemId != 0
            ? macroWeather.OrientationRadians
                + randomRotation * (1.0f - macroWeather.StrongestInfluence * 0.72f)
            : randomRotation;
        Basis orientation = new Basis(surfaceUp, rotation)
            * new Basis(tangent, surfaceUp, acrossAxis);
        CloudPhysicalDefinition physical = CloudPhysicalCatalog.Get(profile);
        float widthRoll = Hash01(aroundCell, acrossCell, seed, 4);
        float widthMeters = MathF.Exp(Mathf.Lerp(
            MathF.Log(Math.Max(1.0f, physical.MinimumWidthMeters)),
            MathF.Log(Math.Max(physical.MinimumWidthMeters, physical.MaximumWidthMeters)),
            widthRoll));
        float depthFraction = profile switch
        {
            CloudFormationProfile.Cirrus => Mathf.Lerp(0.08f, 0.20f, Hash01(aroundCell, acrossCell, seed, 6)),
            CloudFormationProfile.Cirrostratus
                or CloudFormationProfile.Altostratus
                or CloudFormationProfile.Stratus
                or CloudFormationProfile.Nimbostratus =>
                Mathf.Lerp(0.58f, 0.95f, Hash01(aroundCell, acrossCell, seed, 6)),
            _ => Mathf.Lerp(0.55f, 0.90f, Hash01(aroundCell, acrossCell, seed, 6))
        };
        float depthMeters = widthMeters * depthFraction;
        const float archetypeWidth = 3.10f;
        const float archetypeDepth = 2.60f;
        float aroundScale = widthMeters / archetypeWidth;
        float acrossScale = depthMeters / archetypeDepth;
        float scaleMultiplier = widthMeters / Math.Max(1.0f, physical.MinimumWidthMeters);
        Basis scaled = new(
            orientation.X * aroundScale,
            orientation.Y * verticalScale,
            orientation.Z * acrossScale);
        Vector3 position = geometry.CanonicalToWorld(around, altitude, across);
        float brightness = Mathf.Lerp(
            0.86f,
            1.06f,
            Hash01(aroundCell, acrossCell, seed, 7));
        float phase = Hash01(aroundCell, acrossCell, seed, 8);
        float precipitation = macroWeather.PrecipitationPotential
            * macroWeather.StrongestInfluence;
        if (profile is CloudFormationProfile.StormTower or CloudFormationProfile.Cumulonimbus)
            precipitation = Mathf.Max(precipitation, macroWeather.StrongestInfluence * 0.48f);
        if (profile == CloudFormationProfile.Nimbostratus)
            precipitation = Mathf.Max(precipitation, 0.68f);
        if (profile == CloudFormationProfile.Stratocumulus)
            precipitation *= 0.82f;
        float densityDarkening = physical.Density switch
        {
            CloudDensityClass.Thin => 0.02f,
            CloudDensityClass.Medium => 0.07f,
            CloudDensityClass.MediumToDense => 0.12f,
            CloudDensityClass.VeryDense => 0.22f,
            CloudDensityClass.ExtremelyDense => 0.28f,
            _ => 0.0f
        };
        brightness *= 1.0f - densityDarkening;
        brightness *= Mathf.Lerp(1.0f, 0.82f, precipitation);
        return new FormationInstance(
            new Transform3D(scaled, position),
            new Color(
                brightness,
                phase,
                precipitation,
                CloudFormationProfiles.ToShaderProfile(profile) / 3.0f),
            aroundDelta,
            acrossDelta,
            widthMeters * 0.5f,
            depthMeters * 0.5f,
            altitude,
            rotation,
            macroWeather.SystemId,
            scaleMultiplier,
            profile,
            baseAltitude,
            topAltitude);
    }

    private FormationUnionPlan BuildFormationUnionPlan(
        List<ArchetypedFormation> candidates,
        in RingCanonicalPosition player,
        in RingLodGeometrySnapshot geometry,
        in Basis gridBasis,
        double circumference,
        int seed)
    {
        VisibleUnionClusterCount = 0;
        UnionMemberCount = 0;
        if (!MergeTouchingFormations || candidates.Count < 2)
            return new FormationUnionPlan(candidates, new List<FormationUnionGroup>());

        int count = candidates.Count;
        var parents = new int[count];
        var ranks = new byte[count];
        float maximumRadius = 1.0f;
        for (int index = 0; index < count; index++)
        {
            parents[index] = index;
            FormationInstance formation = candidates[index].Formation;
            maximumRadius = Mathf.Max(
                maximumRadius,
                Mathf.Max(formation.AroundRadiusMeters, formation.AcrossRadiusMeters));
        }

        float gap = Mathf.Max(0.0f, UnionGapMeters);
        float binSize = Mathf.Max(2000.0f, FormationCellMeters * 2.0f);
        int binReach = Mathf.Max(
            1,
            Mathf.CeilToInt((maximumRadius * 2.0f + gap) / binSize));
        var spatialBins = new Dictionary<Vector2I, List<int>>();
        for (int firstIndex = 0; firstIndex < count; firstIndex++)
        {
            FormationInstance first = candidates[firstIndex].Formation;
            var bin = new Vector2I(
                Mathf.FloorToInt(first.AroundDeltaMeters / binSize),
                Mathf.FloorToInt(first.AcrossDeltaMeters / binSize));
            for (int binY = bin.Y - binReach; binY <= bin.Y + binReach; binY++)
            for (int binX = bin.X - binReach; binX <= bin.X + binReach; binX++)
            {
                if (!spatialBins.TryGetValue(new Vector2I(binX, binY), out List<int>? nearby))
                    continue;
                foreach (int secondIndex in nearby)
                {
                    if (CanCoalesce(
                            first,
                            candidates[secondIndex].Formation,
                            gap,
                            Mathf.Max(0.0f, UnionVerticalGapMeters)))
                    {
                        UnionRoots(parents, ranks, firstIndex, secondIndex);
                    }
                }
            }

            if (!spatialBins.TryGetValue(bin, out List<int>? currentBin))
            {
                currentBin = new List<int>();
                spatialBins.Add(bin, currentBin);
            }
            currentBin.Add(firstIndex);
        }

        var groups = new Dictionary<int, List<ArchetypedFormation>>();
        for (int index = 0; index < count; index++)
        {
            int root = FindRoot(parents, index);
            if (!groups.TryGetValue(root, out List<ArchetypedFormation>? group))
            {
                group = new List<ArchetypedFormation>();
                groups.Add(root, group);
            }
            group.Add(candidates[index]);
        }

        var singles = new List<ArchetypedFormation>(groups.Count);
        var unionGroups = new List<FormationUnionGroup>();
        foreach (List<ArchetypedFormation> group in groups.Values)
        {
            if (group.Count == 1)
            {
                singles.Add(group[0]);
                continue;
            }

            FormationInstance boundsProxy = CreateUnionBoundsProxy(
                group,
                player,
                geometry,
                gridBasis,
                circumference,
                seed).Formation;
            float unionRange = Mathf.Max(
                _activeRadiusMeters,
                UnionMaximumRangeMeters);
            float proxyDistanceSquared =
                boundsProxy.AroundDeltaMeters * boundsProxy.AroundDeltaMeters
                + boundsProxy.AcrossDeltaMeters * boundsProxy.AcrossDeltaMeters;
            if (proxyDistanceSquared > unionRange * unionRange)
            {
                singles.AddRange(group);
                continue;
            }
            unionGroups.Add(new FormationUnionGroup(group.ToArray(), boundsProxy));
            VisibleUnionClusterCount++;
            UnionMemberCount += group.Count;
        }
        return new FormationUnionPlan(singles, unionGroups);
    }

    private static bool CanCoalesce(
        in FormationInstance first,
        in FormationInstance second,
        float maximumGap,
        float maximumVerticalGap)
    {
        if (CloudFormationProfiles.IsLayered(first.Profile)
            || CloudFormationProfiles.IsLayered(second.Profile)
            || first.AroundRadiusMeters > 5000.0f
            || first.AcrossRadiusMeters > 5000.0f
            || second.AroundRadiusMeters > 5000.0f
            || second.AcrossRadiusMeters > 5000.0f)
        {
            return false;
        }
        float verticalGap = Mathf.Max(
            0.0f,
            Mathf.Max(first.BaseAltitudeMeters, second.BaseAltitudeMeters)
                - Mathf.Min(first.TopAltitudeMeters, second.TopAltitudeMeters));
        if (verticalGap > maximumVerticalGap) return false;

        var separation = new Vector2(
            second.AroundDeltaMeters - first.AroundDeltaMeters,
            second.AcrossDeltaMeters - first.AcrossDeltaMeters);
        float distance = separation.Length();
        if (distance < 1.0f) return true;
        float direction = Mathf.Atan2(separation.Y, separation.X);
        float firstRadius = FormationRadiusAlong(first, direction);
        float secondRadius = FormationRadiusAlong(second, direction);
        return distance <= firstRadius + secondRadius + maximumGap;
    }

    private ArchetypedFormation CreateUnionBoundsProxy(
        IReadOnlyList<ArchetypedFormation> group,
        in RingCanonicalPosition player,
        in RingLodGeometrySnapshot geometry,
        in Basis gridBasis,
        double circumference,
        int seed)
    {
        float minimumAround = float.PositiveInfinity;
        float maximumAround = float.NegativeInfinity;
        float minimumAcross = float.PositiveInfinity;
        float maximumAcross = float.NegativeInfinity;
        float minimumBase = float.PositiveInfinity;
        float maximumTop = float.NegativeInfinity;
        float brightnessTotal = 0.0f;
        float precipitationTotal = 0.0f;
        float maximumPrecipitation = 0.0f;
        float maximumScale = 1.0f;
        int sharedWeatherSystem = group[0].Formation.WeatherSystemId;
        int sourceAroundCell = int.MaxValue;
        int sourceAcrossCell = int.MaxValue;
        CloudFormationProfile profile = group[0].Formation.Profile;

        foreach (ArchetypedFormation candidate in group)
        {
            FormationInstance formation = candidate.Formation;
            float cosine = Mathf.Cos(formation.RotationRadians);
            float sine = Mathf.Sin(formation.RotationRadians);
            float aroundExtent = Mathf.Sqrt(
                Mathf.Pow(formation.AroundRadiusMeters * cosine, 2.0f)
                + Mathf.Pow(formation.AcrossRadiusMeters * sine, 2.0f));
            float acrossExtent = Mathf.Sqrt(
                Mathf.Pow(formation.AroundRadiusMeters * sine, 2.0f)
                + Mathf.Pow(formation.AcrossRadiusMeters * cosine, 2.0f));
            minimumAround = Mathf.Min(
                minimumAround,
                formation.AroundDeltaMeters - aroundExtent);
            maximumAround = Mathf.Max(
                maximumAround,
                formation.AroundDeltaMeters + aroundExtent);
            minimumAcross = Mathf.Min(
                minimumAcross,
                formation.AcrossDeltaMeters - acrossExtent);
            maximumAcross = Mathf.Max(
                maximumAcross,
                formation.AcrossDeltaMeters + acrossExtent);
            minimumBase = Mathf.Min(minimumBase, formation.BaseAltitudeMeters);
            maximumTop = Mathf.Max(maximumTop, formation.TopAltitudeMeters);
            brightnessTotal += formation.CustomData.R;
            precipitationTotal += formation.CustomData.B;
            maximumPrecipitation = Mathf.Max(maximumPrecipitation, formation.CustomData.B);
            maximumScale = Mathf.Max(maximumScale, formation.ScaleMultiplier);
            if (formation.WeatherSystemId != sharedWeatherSystem)
                sharedWeatherSystem = 0;
            sourceAroundCell = Math.Min(sourceAroundCell, candidate.SourceAroundCell);
            sourceAcrossCell = Math.Min(sourceAcrossCell, candidate.SourceAcrossCell);
            if (formation.Profile == CloudFormationProfile.AnvilStorm)
                profile = CloudFormationProfile.AnvilStorm;
            else if (formation.Profile == CloudFormationProfile.StormTower
                     && profile != CloudFormationProfile.AnvilStorm)
                profile = CloudFormationProfile.StormTower;
        }

        float aroundDelta = (minimumAround + maximumAround) * 0.5f;
        float acrossDelta = (minimumAcross + maximumAcross) * 0.5f;
        float aroundScale = Mathf.Max(180.0f, (maximumAround - minimumAround) * 0.5f);
        float acrossScale = Mathf.Max(180.0f, (maximumAcross - minimumAcross) * 0.5f);
        const float archetypeMinimumY = -0.72f;
        const float archetypeMaximumY = 1.02f;
        float verticalScale = Mathf.Max(100.0f, maximumTop - minimumBase)
            / (archetypeMaximumY - archetypeMinimumY);
        float altitude = minimumBase - archetypeMinimumY * verticalScale;
        double around = WrapAround(player.AroundMeters + aroundDelta, circumference);
        double across = player.AcrossMeters + acrossDelta;
        double angle = around / circumference * Math.Tau;
        Vector3 radialOutLocal = new(
            (float)Math.Cos(angle),
            0.0f,
            (float)Math.Sin(angle));
        Vector3 tangentLocal = new(-radialOutLocal.Z, 0.0f, radialOutLocal.X);
        Vector3 tangent = (gridBasis * tangentLocal).Normalized();
        Vector3 surfaceUp = (gridBasis * -radialOutLocal).Normalized();
        Vector3 acrossAxis = (gridBasis * Vector3.Up).Normalized();
        Basis scaled = new(
            tangent * aroundScale,
            surfaceUp * verticalScale,
            acrossAxis * acrossScale);
        Vector3 position = geometry.CanonicalToWorld(around, altitude, across);
        float selector = Hash01(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            181 + group.Count);
        int archetype = ResolveArchetypeIndex(profile, selector);
        float brightness = brightnessTotal / group.Count;
        float precipitation = Mathf.Lerp(
            precipitationTotal / group.Count,
            maximumPrecipitation,
            0.62f);
        float phase = Hash01(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            211 + group.Count);
        float scaleMultiplier = Mathf.Max(maximumScale, Mathf.Sqrt(group.Count));
        var mergedFormation = new FormationInstance(
            new Transform3D(scaled, position),
            new Color(
                brightness,
                phase,
                precipitation,
                CloudFormationProfiles.ToShaderProfile(profile) / 3.0f),
            aroundDelta,
            acrossDelta,
            aroundScale,
            acrossScale,
            altitude,
            0.0f,
            sharedWeatherSystem,
            scaleMultiplier,
            profile,
            minimumBase,
            maximumTop);
        return new ArchetypedFormation(
            mergedFormation,
            archetype,
            sourceAroundCell,
            sourceAcrossCell);
    }

    private static float FormationRadiusAlong(
        in FormationInstance formation,
        float worldDirectionRadians)
    {
        float localDirection = worldDirectionRadians - formation.RotationRadians;
        float cosine = Mathf.Cos(localDirection);
        float sine = Mathf.Sin(localDirection);
        float around = Mathf.Max(1.0f, formation.AroundRadiusMeters);
        float across = Mathf.Max(1.0f, formation.AcrossRadiusMeters);
        float denominator = Mathf.Sqrt(
            cosine * cosine / (around * around)
            + sine * sine / (across * across));
        return denominator > 0.000001f ? 1.0f / denominator : Mathf.Max(around, across);
    }

    private static int FindRoot(int[] parents, int index)
    {
        int root = index;
        while (parents[root] != root) root = parents[root];
        while (parents[index] != index)
        {
            int next = parents[index];
            parents[index] = root;
            index = next;
        }
        return root;
    }

    private static void UnionRoots(
        int[] parents,
        byte[] ranks,
        int first,
        int second)
    {
        int firstRoot = FindRoot(parents, first);
        int secondRoot = FindRoot(parents, second);
        if (firstRoot == secondRoot) return;
        if (ranks[firstRoot] < ranks[secondRoot])
        {
            parents[firstRoot] = secondRoot;
        }
        else
        {
            parents[secondRoot] = firstRoot;
            if (ranks[firstRoot] == ranks[secondRoot]) ranks[firstRoot]++;
        }
    }


    private static void InstallFormationInstances(
        List<MultiMeshInstance3D> owners,
        List<ArrayMesh> meshes,
        List<FormationInstance>[] formations,
        float visibleRadiusMeters)
    {
        for (int archetype = 0; archetype < owners.Count; archetype++)
        {
            MultiMeshInstance3D owner = owners[archetype];
            List<FormationInstance> source = formations[archetype];
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = meshes[archetype],
                InstanceCount = source.Count,
                VisibleInstanceCount = source.Count
            };
            for (int index = 0; index < source.Count; index++)
            {
                multiMesh.SetInstanceTransform(index, source[index].Transform);
                multiMesh.SetInstanceCustomData(index, source[index].CustomData);
            }
            owner.Multimesh = multiMesh;
            float bound = visibleRadiusMeters * 1.08f;
            owner.CustomAabb = new Aabb(
                new Vector3(-bound, -bound, -bound),
                Vector3.One * bound * 2.0f);
        }
    }

    private static int InstallRetainedShadowInstances(
        List<MultiMeshInstance3D> owners,
        List<ArrayMesh> meshes,
        List<FormationInstance>[] formations,
        Vector3 playerWorldPosition,
        float cullMarginMeters)
    {
        if (owners.Count != formations.Length || meshes.Count != formations.Length)
            return 0;

        int casterCount = 0;
        for (int archetype = 0; archetype < owners.Count; archetype++)
        {
            MultiMeshInstance3D owner = owners[archetype];
            List<FormationInstance> source = formations[archetype];
            var multiMesh = new MultiMesh
            {
                TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
                UseCustomData = true,
                Mesh = meshes[archetype],
                InstanceCount = source.Count,
                VisibleInstanceCount = source.Count
            };
            for (int index = 0; index < source.Count; index++)
            {
                multiMesh.SetInstanceTransform(index, source[index].Transform);
                multiMesh.SetInstanceCustomData(index, source[index].CustomData);
            }
            owner.Multimesh = multiMesh;
            owner.CustomAabb = CalculateRetainedShadowBounds(
                source,
                playerWorldPosition,
                cullMarginMeters);
            casterCount += source.Count;
        }
        return casterCount;
    }

    private static Aabb CalculateRetainedShadowBounds(
        IReadOnlyList<FormationInstance> formations,
        Vector3 playerWorldPosition,
        float marginMeters)
    {
        float cameraAnchor = Mathf.Max(16.0f, marginMeters);
        Vector3 minimum = playerWorldPosition - Vector3.One * cameraAnchor;
        Vector3 maximum = playerWorldPosition + Vector3.One * cameraAnchor;
        foreach (FormationInstance formation in formations)
        {
            Basis basis = formation.Transform.Basis;
            Vector3 extent = basis.X.Abs() * 1.55f
                + basis.Y.Abs() * 1.02f
                + basis.Z.Abs() * 1.30f
                + Vector3.One * marginMeters;
            Vector3 position = formation.Transform.Origin;
            minimum = ComponentMinimum(minimum, position - extent);
            maximum = ComponentMaximum(maximum, position + extent);
        }
        return new Aabb(minimum, maximum - minimum);
    }

    private static Vector3 ComponentMinimum(Vector3 a, Vector3 b) => new(
        Mathf.Min(a.X, b.X),
        Mathf.Min(a.Y, b.Y),
        Mathf.Min(a.Z, b.Z));

    private static Vector3 ComponentMaximum(Vector3 a, Vector3 b) => new(
        Mathf.Max(a.X, b.X),
        Mathf.Max(a.Y, b.Y),
        Mathf.Max(a.Z, b.Z));

    private void UpdateProjectedShadowMap(
        in RingCanonicalPosition player,
        List<FormationInstance> formations)
    {
        if (!ProjectLongRangeShadows
            || !_runtimeEnabled
            || _grid == null
            || _explorer == null
            || formations.Count == 0)
        {
            _shadowBuildGeneration++;
            _queuedShadowBuild = null;
            _hasMovingShadowTransform = false;
            RingCloudShadowProjector.Disable();
            SetCloudLayerOcclusionEnabled(false);
            return;
        }

        int resolution = Math.Clamp(
            Mathf.RoundToInt(ShadowMapResolution / 64.0f) * 64,
            128,
            2048);
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        Basis gridBasis = _grid.GlobalBasis.Orthonormalized();
        Vector3 sun = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Vector3 centerWorld = _grid.CanonicalRingPositionToWorld(
            new RingCanonicalPosition(player.AroundMeters, 0.0, player.AcrossMeters));
        int generation = ++_shadowBuildGeneration;
        var input = new CloudShadowBuildInput(
            formations.ToArray(),
            player,
            gridBasis,
            sun,
            centerWorld,
            circumference,
            _transitionRadiusMeters,
            resolution,
            Math.Clamp(ProjectedShadowBlurRadiusPixels, 1, 4),
            generation);
        if (_shadowBuildTask != null)
        {
            _queuedShadowBuild = input;
            return;
        }
        StartProjectedShadowBuild(input);
    }

    private void StartProjectedShadowBuild(CloudShadowBuildInput input)
    {
        _shadowBuildTask = Task.Run(() => BuildProjectedShadowData(input));
    }

    private static CloudShadowBuildData BuildProjectedShadowData(
        CloudShadowBuildInput input)
    {
        var timer = Stopwatch.StartNew();
        int resolution = input.Resolution;
        var mask = new byte[resolution * resolution];
        var topAltitudeMask = new byte[resolution * resolution];
        float diameterMeters = input.RadiusMeters * 2.0f;
        float pixelsPerMeter = resolution / Mathf.Max(1.0f, diameterMeters);
        double angle = WrapAround(
            input.Player.AroundMeters,
            input.Circumference) / input.Circumference * Math.Tau;
        Vector3 radialOutLocal = new(
            (float)Math.Cos(angle),
            0.0f,
            (float)Math.Sin(angle));
        Vector3 tangentLocal = new(-radialOutLocal.Z, 0.0f, radialOutLocal.X);
        Vector3 axisAround = (input.GridBasis * tangentLocal).Normalized();
        Vector3 surfaceUp = (input.GridBasis * -radialOutLocal).Normalized();
        Vector3 axisAcross = (input.GridBasis * Vector3.Up).Normalized();
        float sunVertical = Mathf.Max(
            0.16f,
            input.SunDirection.Dot(surfaceUp));
        Vector2 sunPlanar = new(
            input.SunDirection.Dot(axisAround),
            input.SunDirection.Dot(axisAcross));

        foreach (FormationInstance formation in input.Formations)
        {
            Vector2 projected = new(
                formation.AroundDeltaMeters,
                formation.AcrossDeltaMeters);
            Vector2 shift = -sunPlanar
                * (formation.AltitudeMeters / sunVertical);
            float maximumShift = input.RadiusMeters * 0.30f;
            if (shift.LengthSquared() > maximumShift * maximumShift)
                shift = shift.Normalized() * maximumShift;
            projected += shift;

            float centerX = (projected.X / diameterMeters + 0.5f)
                * resolution;
            float centerY = (projected.Y / diameterMeters + 0.5f)
                * resolution;
            float profile = Mathf.Clamp(
                formation.CustomData.A * 3.0f,
                0.0f,
                3.0f);
            float stormFactor = Mathf.SmoothStep(
                0.0f,
                1.0f,
                Mathf.InverseLerp(1.45f, 2.95f, profile));
            float sizeFactor = Mathf.SmoothStep(
                0.0f,
                1.0f,
                Mathf.InverseLerp(
                    1.35f,
                    4.0f,
                    formation.ScaleMultiplier));
            float footprintInflation =
                1.15f + stormFactor * 0.12f + sizeFactor * 0.10f;
            float radiusX = Mathf.Max(
                1.25f,
                formation.AroundRadiusMeters
                    * footprintInflation
                    * pixelsPerMeter);
            float radiusY = Mathf.Max(
                1.25f,
                formation.AcrossRadiusMeters
                    * footprintInflation
                    * pixelsPerMeter);
            float shadowDensity = Mathf.Max(
                formation.CustomData.B,
                Mathf.Max(stormFactor * 0.90f, sizeFactor * 0.72f));
            byte peakShadow = (byte)Math.Clamp(
                Mathf.RoundToInt(Mathf.Lerp(230.0f, 252.0f, shadowDensity)),
                1,
                255);
            float normalizedTopAltitude = Mathf.InverseLerp(
                CloudOcclusionMinimumAltitudeMeters,
                CloudOcclusionMaximumAltitudeMeters,
                formation.TopAltitudeMeters);
            byte encodedTopAltitude = (byte)Math.Clamp(
                1 + Mathf.RoundToInt(normalizedTopAltitude * 254.0f),
                1,
                255);
            RasterizeShadowEllipse(
                mask,
                topAltitudeMask,
                resolution,
                centerX,
                centerY,
                radiusX,
                radiusY,
                formation.RotationRadians,
                encodedTopAltitude,
                peakShadow);
        }

        byte[] softened = BoxBlur(mask, resolution, input.BlurRadius);
        var packedShadowData = new byte[softened.Length * 2];
        int occupiedTexels = 0;
        byte maximumMaskValue = 0;
        for (int index = 0; index < softened.Length; index++)
        {
            byte value = softened[index];
            if (value > 0) occupiedTexels++;
            if (value > maximumMaskValue) maximumMaskValue = value;
            packedShadowData[index * 2] = value;
            packedShadowData[index * 2 + 1] = topAltitudeMask[index];
        }
        timer.Stop();
        return new CloudShadowBuildData(
            packedShadowData,
            input.CenterWorld,
            axisAround,
            axisAcross,
            input.RadiusMeters,
            resolution,
            occupiedTexels,
            maximumMaskValue,
            timer.Elapsed.TotalMilliseconds,
            input.Generation);
    }

    private void ServiceProjectedShadowBuild()
    {
        if (_shadowBuildTask == null || !_shadowBuildTask.IsCompleted) return;
        Task<CloudShadowBuildData> completed = _shadowBuildTask;
        _shadowBuildTask = null;
        try
        {
            if (completed.IsCompletedSuccessfully)
            {
                CloudShadowBuildData data = completed.Result;
                if (data.Generation == _shadowBuildGeneration)
                    InstallProjectedShadowData(data);
            }
            else if (completed.IsFaulted)
            {
                GD.PushError(
                    $"Cloud shadow worker failed: "
                    + $"{completed.Exception?.GetBaseException()}");
            }
        }
        finally
        {
            if (_queuedShadowBuild is { } queued)
            {
                _queuedShadowBuild = null;
                StartProjectedShadowBuild(queued);
            }
        }
    }

    private void InstallProjectedShadowData(in CloudShadowBuildData data)
    {
        Image image = Image.CreateFromData(
            data.Resolution,
            data.Resolution,
            false,
            Image.Format.Rg8,
            data.PackedData);
        if (_shadowTexture == null
            || _shadowTexture.GetWidth() != data.Resolution
            || _shadowTexture.GetHeight() != data.Resolution)
        {
            _shadowTexture = ImageTexture.CreateFromImage(image);
        }
        else
        {
            _shadowTexture.Update(image);
        }
        LastShadowMapBuildMilliseconds = data.BuildMilliseconds;
        _hasMovingShadowTransform = true;
        _shadowBaseCenterWorld = data.CenterWorld;
        _shadowBaseAxisAround = data.AxisAround;
        _shadowBaseAxisAcross = data.AxisAcross;
        Transform3D ownerTransform =
            _cloudContainer?.GlobalTransform ?? Transform3D.Identity;
        Vector3 transformedCenter =
            ownerTransform * _shadowBaseCenterWorld;
        Vector3 transformedAround =
            (ownerTransform.Basis * _shadowBaseAxisAround).Normalized();
        Vector3 transformedAcross =
            (ownerTransform.Basis * _shadowBaseAxisAcross).Normalized();
        _lastAppliedShadowOwnerOrigin = ownerTransform.Origin;
        UpdateCloudLayerOcclusionBindings(
            transformedCenter,
            transformedAround,
            transformedAcross,
            data.RadiusMeters);
        RingCloudShadowProjector.Update(
            _shadowTexture,
            transformedCenter,
            transformedAround,
            transformedAcross,
            data.RadiusMeters,
            ProjectedShadowStrength,
            ProjectedShadowFarStrength,
            ProjectedShadowNearStartMeters,
            ProjectedShadowNearEndMeters,
            ProjectedShadowFarStrengthStartMeters,
            ProjectedShadowFarStrengthEndMeters,
            transformedAcross.Cross(transformedAround).Normalized(),
            _grid == null
                ? 0.0f
                : (float)Math.Max(
                    1.0,
                    _grid.CaptureLodGeometrySnapshot().RingRadiusMeters
                        - _grid.CaptureLodGeometrySnapshot().RingVerticalBaseOffsetMeters));
        if (_shadowDiagnosticsReported) return;
        _shadowDiagnosticsReported = true;
        GD.Print(
            $"Cloud shadow projector ready: receivers={RingCloudShadowProjector.ActiveReceiverCount}, "
            + $"map={data.Resolution}px, occupied={data.OccupiedTexels}/{data.Resolution * data.Resolution}, "
            + $"peak={data.MaximumMaskValue}, strength={ProjectedShadowStrength:0.00}->{ProjectedShadowFarStrength:0.00}, "
            + $"blur={ProjectedShadowBlurRadiusPixels}px, "
            + $"workerMs={data.BuildMilliseconds:0.00}, "
            + $"range={ProjectedShadowNearStartMeters:0}-{data.RadiusMeters:0}m.");
    }

    private void UpdateMovingProjectedShadowTransform()
    {
        if (!_hasMovingShadowTransform
            || _cloudContainer == null
            || _shadowTexture == null)
        {
            return;
        }

        Transform3D ownerTransform = _cloudContainer.GlobalTransform;
        if (_lastAppliedShadowOwnerOrigin.IsFinite()
            && _lastAppliedShadowOwnerOrigin.DistanceSquaredTo(
                ownerTransform.Origin) < 0.25f * 0.25f)
        {
            return;
        }

        _lastAppliedShadowOwnerOrigin = ownerTransform.Origin;
        Vector3 center = ownerTransform * _shadowBaseCenterWorld;
        Vector3 around =
            (ownerTransform.Basis * _shadowBaseAxisAround).Normalized();
        Vector3 across =
            (ownerTransform.Basis * _shadowBaseAxisAcross).Normalized();
        foreach (ShaderMaterial? material in new[]
                 {
                     _material,
                     _showcaseMaterial,
                     _transitionMaterial,
                     _unionMaterial,
                     _unionTransitionMaterial
                 })
        {
            if (material == null) continue;
            material.SetShaderParameter(
                "cloud_layer_occlusion_center_world",
                center);
            material.SetShaderParameter(
                "cloud_layer_occlusion_axis_around",
                around);
            material.SetShaderParameter(
                "cloud_layer_occlusion_axis_across",
                across);
        }
        RingCloudShadowProjector.UpdateTransform(center, around, across);
    }

    private void SetCloudLayerOcclusionEnabled(bool enabled)
    {
        foreach (ShaderMaterial? material in new[]
                 {
                     _material,
                     _transitionMaterial,
                     _unionMaterial,
                     _unionTransitionMaterial
                 })
        {
            material?.SetShaderParameter(
                "cloud_layer_occlusion_enabled",
                enabled && _shadowTexture != null);
        }
    }

    private void UpdateCloudLayerOcclusionBindings(
        Vector3 centerWorld,
        Vector3 axisAround,
        Vector3 axisAcross,
        float radiusMeters)
    {
        ImageTexture? shadowTexture = _shadowTexture;
        bool enabled = _runtimeEnabled
            && ProjectLongRangeShadows
            && shadowTexture != null;
        foreach (ShaderMaterial? material in new[]
                 {
                     _material,
                     _transitionMaterial,
                     _unionMaterial,
                     _unionTransitionMaterial
                 })
        {
            if (material == null) continue;
            material.SetShaderParameter("cloud_layer_occlusion_enabled", enabled);
            if (!enabled || shadowTexture == null) continue;
            material.SetShaderParameter("cloud_layer_occlusion_texture", shadowTexture);
            material.SetShaderParameter("cloud_layer_occlusion_center_world", centerWorld);
            material.SetShaderParameter("cloud_layer_occlusion_axis_around", axisAround);
            material.SetShaderParameter("cloud_layer_occlusion_axis_across", axisAcross);
            material.SetShaderParameter(
                "cloud_layer_occlusion_radius_meters",
                Mathf.Max(1.0f, radiusMeters));
            material.SetShaderParameter(
                "cloud_layer_occlusion_altitude_min_meters",
                CloudOcclusionMinimumAltitudeMeters);
            material.SetShaderParameter(
                "cloud_layer_occlusion_altitude_max_meters",
                CloudOcclusionMaximumAltitudeMeters);
        }
    }

    private static void RasterizeShadowEllipse(
        byte[] mask,
        byte[] topAltitudeMask,
        int resolution,
        float centerX,
        float centerY,
        float radiusX,
        float radiusY,
        float rotation,
        byte encodedTopAltitude,
        byte peakShadow)
    {
        int minX = Math.Max(0, Mathf.FloorToInt(centerX - radiusX - 2.0f));
        int maxX = Math.Min(resolution - 1, Mathf.CeilToInt(centerX + radiusX + 2.0f));
        int minY = Math.Max(0, Mathf.FloorToInt(centerY - radiusY - 2.0f));
        int maxY = Math.Min(resolution - 1, Mathf.CeilToInt(centerY + radiusY + 2.0f));
        float cosine = Mathf.Cos(rotation);
        float sine = Mathf.Sin(rotation);
        float inverseRadiusXSquared = 1.0f / (radiusX * radiusX);
        float inverseRadiusYSquared = 1.0f / (radiusY * radiusY);
        for (int y = minY; y <= maxY; y++)
        for (int x = minX; x <= maxX; x++)
        {
            float dx = x + 0.5f - centerX;
            float dy = y + 0.5f - centerY;
            float localX = cosine * dx + sine * dy;
            float localY = -sine * dx + cosine * dy;
            float normalizedSquared = localX * localX * inverseRadiusXSquared
                + localY * localY * inverseRadiusYSquared;
            if (normalizedSquared >= 1.0f) continue;
            float body = 1.0f - Mathf.SmoothStep(0.2304f, 1.0f, normalizedSquared);
            byte value = (byte)(body * peakShadow + 0.5f);
            int index = y * resolution + x;
            if (value > mask[index]) mask[index] = value;
            if (value > 0 && encodedTopAltitude > topAltitudeMask[index])
                topAltitudeMask[index] = encodedTopAltitude;
        }
    }

    private static byte[] BoxBlur(byte[] source, int resolution, int radius)
    {
        if (radius == 2)
        {
            var first = new byte[source.Length];
            var second = new byte[source.Length];
            BlurRadiusOneHorizontal(source, first, resolution);
            BlurRadiusOneHorizontal(first, second, resolution);
            BlurRadiusOneVertical(second, first, resolution);
            BlurRadiusOneVertical(first, second, resolution);
            return second;
        }

        var horizontal = new byte[source.Length];
        var result = new byte[source.Length];
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            int total = 0;
            int totalWeight = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                int sampleX = Math.Clamp(x + offset, 0, resolution - 1);
                int weight = radius + 1 - Math.Abs(offset);
                total += source[y * resolution + sampleX] * weight;
                totalWeight += weight;
            }
            horizontal[y * resolution + x] = (byte)(total / totalWeight);
        }
        for (int y = 0; y < resolution; y++)
        for (int x = 0; x < resolution; x++)
        {
            int total = 0;
            int totalWeight = 0;
            for (int offset = -radius; offset <= radius; offset++)
            {
                int sampleY = Math.Clamp(y + offset, 0, resolution - 1);
                int weight = radius + 1 - Math.Abs(offset);
                total += horizontal[sampleY * resolution + x] * weight;
                totalWeight += weight;
            }
            result[y * resolution + x] = (byte)(total / totalWeight);
        }
        return result;
    }

    private static void BlurRadiusOneHorizontal(byte[] source, byte[] destination, int resolution)
    {
        for (int y = 0; y < resolution; y++)
        {
            int row = y * resolution;
            for (int x = 0; x < resolution; x++)
            {
                int left = x > 0 ? x - 1 : 0;
                int right = x + 1 < resolution ? x + 1 : resolution - 1;
                destination[row + x] = (byte)(
                    (source[row + left] + source[row + x] + source[row + right]) / 3);
            }
        }
    }

    private static void BlurRadiusOneVertical(byte[] source, byte[] destination, int resolution)
    {
        for (int y = 0; y < resolution; y++)
        {
            int upperRow = (y > 0 ? y - 1 : 0) * resolution;
            int row = y * resolution;
            int lowerRow = (y + 1 < resolution ? y + 1 : resolution - 1) * resolution;
            for (int x = 0; x < resolution; x++)
            {
                destination[row + x] = (byte)(
                    (source[upperRow + x] + source[row + x] + source[lowerRow + x]) / 3);
            }
        }
    }

    private void UpdateMaterialUniforms()
    {
        if (_material == null
            || _transitionMaterial == null
            || _unionMaterial == null
            || _unionTransitionMaterial == null
            || _grid == null
            || _explorer == null)
        {
            return;
        }
        RingCanonicalPosition player = _explorer.CanonicalPosition;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double angle = WrapAround(player.AroundMeters) / circumference * Math.Tau;
        Vector3 radialOutLocal = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
        Vector3 surfaceUp = (_grid.GlobalBasis.Orthonormalized() * -radialOutLocal).Normalized();
        Vector3 sunDirection = _sunLight?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Right;
        Camera3D? activeCamera = GetViewport()?.GetCamera3D();
        RingAtmosphereRayFrame atmosphereFrame = activeCamera == null
            ? RingAtmosphereRayFrame.Invalid
            : RingAtmosphereRayFrame.FromMainCamera(_grid, activeCamera, sunDirection);
        foreach (ShaderMaterial material in new[]
                 {
                     _material,
                     _transitionMaterial,
                     _unionMaterial,
                     _unionTransitionMaterial
                 })
        {
			atmosphereFrame.Apply(material);
            material.SetShaderParameter("camera_world_position", _explorer.GlobalPosition);
            material.SetShaderParameter("ring_center", _grid.GetCanonicalRingCenterWorld());
            material.SetShaderParameter("surface_up", surfaceUp);
            material.SetShaderParameter("sun_direction", sunDirection);
            material.SetShaderParameter("cloud_self_shadow_strength", CloudSelfShadowStrength);
            material.SetShaderParameter(
                "cloud_layer_occlusion_strength",
                CloudLayerOcclusionStrength);
        }
        atmosphereFrame.Apply(_showcaseMaterial);
        float ownership = Mathf.Clamp(LocalMeshCloudOwnership, 0.0f, 1.0f);
        float effectiveDetailedRadius = Mathf.Max(1.0f, _activeRadiusMeters * ownership);
        float effectiveDetailedHandoffStart = Mathf.Min(
            effectiveDetailedRadius - 0.5f,
            Mathf.Max(0.0f, _handoffStartMeters * ownership));
        float effectiveTransitionFadeInStart = Mathf.Max(
            0.0f,
            _transitionFadeInStartMeters * ownership);
        float effectiveTransitionFadeInEnd = Mathf.Max(
            effectiveTransitionFadeInStart + 1.0f,
            _transitionFadeInEndMeters * ownership);
        float effectiveTransitionRadius = Mathf.Max(
            1.0f,
            _transitionRadiusMeters * ownership);
        float effectiveFarShellHandoffStart = Mathf.Min(
            effectiveTransitionRadius - 0.5f,
            Mathf.Max(0.0f, _farShellHandoffStartMeters * ownership));
        _material.SetShaderParameter("handoff_in_start_meters", 0.0f);
        _material.SetShaderParameter("handoff_in_end_meters", 1.0f);
        _material.SetShaderParameter("handoff_start_meters", effectiveDetailedHandoffStart);
        _material.SetShaderParameter("handoff_end_meters", effectiveDetailedRadius);
        _showcaseMaterial?.SetShaderParameter("handoff_in_start_meters", 0.0f);
        _showcaseMaterial?.SetShaderParameter("handoff_in_end_meters", 1.0f);
        _showcaseMaterial?.SetShaderParameter("handoff_start_meters", 1000000.0f);
        _showcaseMaterial?.SetShaderParameter("handoff_end_meters", 1000001.0f);
        _transitionMaterial.SetShaderParameter("handoff_in_start_meters", effectiveTransitionFadeInStart);
        _transitionMaterial.SetShaderParameter("handoff_in_end_meters", effectiveTransitionFadeInEnd);
        _transitionMaterial.SetShaderParameter("handoff_start_meters", effectiveFarShellHandoffStart);
        _transitionMaterial.SetShaderParameter("handoff_end_meters", effectiveTransitionRadius);
        _unionMaterial.SetShaderParameter("handoff_in_start_meters", 0.0f);
        _unionMaterial.SetShaderParameter("handoff_in_end_meters", 1.0f);
        _unionMaterial.SetShaderParameter("handoff_start_meters", effectiveDetailedHandoffStart);
        _unionMaterial.SetShaderParameter("handoff_end_meters", effectiveDetailedRadius);
        _unionTransitionMaterial.SetShaderParameter(
            "handoff_in_start_meters",
            effectiveTransitionFadeInStart);
        _unionTransitionMaterial.SetShaderParameter(
            "handoff_in_end_meters",
            effectiveTransitionFadeInEnd);
        _unionTransitionMaterial.SetShaderParameter(
            "handoff_start_meters",
            effectiveFarShellHandoffStart);
        _unionTransitionMaterial.SetShaderParameter(
            "handoff_end_meters",
            effectiveTransitionRadius);
    }

    public LocalWeatherState QueryLocalWeather(in RingCanonicalPosition position)
        => QueryLocalWeatherAtTime(position, CloudSimulationTimeSeconds);

    /// <summary>
    /// Evaluates the saved-clock deterministic weather field without mutating it.
    /// Far atlas snapshots and restored sessions must use this instead of an
    /// elapsed-frame approximation.
    /// </summary>
    public LocalWeatherState QueryLocalWeatherAtTime(
        in RingCanonicalPosition position,
        double timeSeconds)
    {
        if (_grid == null) return default;
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        float cellSize = Mathf.Max(1000.0f, FormationCellMeters);
        int sourceAroundCell = Mathf.FloorToInt((float)(
            WrapAround(position.AroundMeters - WeatherWindMetersPerSecond.X * timeSeconds)
            / cellSize));
        int sourceAcrossCell = Mathf.FloorToInt((float)(
            (position.AcrossMeters - WeatherWindMetersPerSecond.Y * timeSeconds)
            / cellSize));
        float smallWeather = SampleWeatherNoise(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            Mathf.Max(2, SmallWeatherClusterCells),
            30);
        float largeWeather = SampleWeatherNoise(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            Mathf.Max(4, LargeWeatherClusterCells),
            40);
        float weather = smallWeather * 0.62f + largeWeather * 0.38f;
        List<WeatherSystemDefinition> nearbyWeatherSystems = BuildNearbyWeatherSystems(
            position.AroundMeters,
            position.AcrossMeters,
            timeSeconds,
            seed,
            0.0f);
        MacroWeatherSample macro = EvaluateMacroWeather(
            position.AroundMeters,
            position.AcrossMeters,
            timeSeconds,
            nearbyWeatherSystems);
        float cloudCoverage = ResolveLocalCloudCoverage(weather, macro);
        float precipitation = Mathf.Clamp(
            macro.PrecipitationPotential
            * macro.CombinedInfluence
            * SmoothUnit((cloudCoverage - 0.35f) / 0.55f),
            0.0f,
            1.0f);
        CloudFormationProfile dominantProfile = ResolveFormationProfile(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            macro);
        FormationVerticalProfile verticalProfile = ResolveFormationVerticalProfile(
            sourceAroundCell,
            sourceAcrossCell,
            seed,
            macro,
            dominantProfile);
        bool hasWeatherSystem = macro.Kind != WeatherSystemKind.None
            && macro.RadiusMeters > 0.0f
            && macro.MinorRadiusMeters > 0.0f;
        Vector2 systemCenter = hasWeatherSystem
            ? new Vector2(
                (float)WrapAround(
                    macro.SourceAroundMeters + WeatherWindMetersPerSecond.X * timeSeconds),
                (float)(macro.SourceAcrossMeters + WeatherWindMetersPerSecond.Y * timeSeconds))
            : Vector2.Zero;
        Vector2 systemRadii = hasWeatherSystem
            ? new Vector2(macro.RadiusMeters, macro.MinorRadiusMeters)
            : Vector2.Zero;
        return ApplyManualWeather(position, new LocalWeatherState(
            macro.SystemId,
            macro.Kind,
            macro.CombinedInfluence,
            macro.RadiusMeters,
            systemCenter,
            systemRadii,
            macro.OrientationRadians,
            cloudCoverage,
            precipitation,
            WeatherWindMetersPerSecond,
            dominantProfile,
            verticalProfile.BaseAltitudeMeters,
            verticalProfile.TopAltitudeMeters));
    }

    /// <summary>Godot-friendly deterministic sample for smoke diagnostics.</summary>
    public Godot.Collections.Dictionary GetNaturalWeatherDiagnosticAtTime(
        double aroundMeters,
        double acrossMeters,
        double timeSeconds)
    {
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        LocalWeatherState sample = QueryLocalWeatherAtTime(
            new RingCanonicalPosition(aroundMeters, 0.0, acrossMeters), timeSeconds);
        return new Godot.Collections.Dictionary
        {
            ["time_seconds"] = timeSeconds,
            ["system_id"] = sample.SystemId,
            ["system_kind"] = (int)sample.SystemKind,
            ["influence"] = sample.SystemInfluence,
            ["radius_meters"] = sample.SystemRadiusMeters,
            ["center_meters"] = sample.SystemCenterMeters,
            ["radii_meters"] = sample.SystemRadiiMeters,
            ["cloud_coverage"] = sample.CloudCoverage,
            ["precipitation"] = sample.PrecipitationPotential,
            ["lifecycle_enabled"] = NaturalWeatherLifecyclesEnabled
        };
    }

    /// <summary>Production lifecycle state for deterministic boundary smoke tests.</summary>
    public Godot.Collections.Dictionary GetNaturalWeatherLifecycleDiagnostic(
        int aroundCell, int acrossCell, double timeSeconds)
    {
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        int seed = _grid?.TerrainSampler?.Seed ?? 1337;
        WeatherLifecycle lifecycle = EvaluateWeatherLifecycle(aroundCell, acrossCell, seed, timeSeconds);
        int id = WeatherSystemId(aroundCell, acrossCell,
            unchecked(seed ^ lifecycle.Generation * 0x45D9F3B));
        return new Godot.Collections.Dictionary {
            ["generation"] = lifecycle.Generation,
            ["envelope"] = lifecycle.Envelope,
            ["stage"] = lifecycle.Stage.ToString(),
            ["cycle_seconds"] = lifecycle.CycleSeconds,
            ["offset_seconds"] = lifecycle.OffsetSeconds,
            ["system_id"] = id
        };
    }

    /// <summary>
    /// Chooses an active precipitation cell belonging to a currently active storm
    /// system. The returned position is inside that formation cell, rather than
    /// being camera-local, so consumers such as lightning can cover the full
    /// storm front.
    /// </summary>
    public bool TryGetRandomActiveStormCell(
        in LocalWeatherState storm,
        float precipitationThreshold,
        RandomNumberGenerator random,
        out RingCanonicalPosition position,
        out LocalWeatherState cellWeather)
    {
        position = default;
        cellWeather = default;
        if (_grid == null
            || storm.SystemId == 0
            || storm.SystemKind is not (WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront)
            || storm.SystemRadiiMeters.X <= 0.0f
            || storm.SystemRadiiMeters.Y <= 0.0f)
            return false;

        float cos = Mathf.Cos(storm.SystemOrientationRadians);
        float sin = Mathf.Sin(storm.SystemOrientationRadians);
        for (int attempt = 0; attempt < 48; attempt++)
        {
            float radius = Mathf.Sqrt(random.Randf());
            float angle = random.RandfRange(0.0f, Mathf.Tau);
            Vector2 local = new(
                Mathf.Cos(angle) * storm.SystemRadiiMeters.X * radius,
                Mathf.Sin(angle) * storm.SystemRadiiMeters.Y * radius);
            if (!IsWithinStormEllipse(local, storm.SystemRadiiMeters))
                continue;
            Vector2 offset = new(
                local.X * cos - local.Y * sin,
                local.X * sin + local.Y * cos);
            double around = WrapAround(storm.SystemCenterMeters.X + offset.X);
            double across = storm.SystemCenterMeters.Y + offset.Y;
            if (across < 0.0 || across > _grid.RingAcrossWidthMeters)
                continue;

            LocalWeatherState candidate = QueryLocalWeather(new RingCanonicalPosition(around, 0.0, across));
            // The sunshine bridge can expose a synthesized negative system ID.
            // Those IDs do not exist in QueryLocalWeather, but the sampled point
            // is still uniformly inside its active system ellipse.
            if (storm.SystemId < 0)
            {
                position = new RingCanonicalPosition(around, 0.0, across);
                cellWeather = storm;
                return true;
            }
            if (candidate.SystemId != storm.SystemId
                || candidate.SystemKind is not (WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront)
                || candidate.PrecipitationPotential < precipitationThreshold)
                continue;

            position = new RingCanonicalPosition(around, 0.0, across);
            cellWeather = candidate;
            return true;
        }

        return false;
    }

    // Kept separate so the uniform sampler's ellipse contract is explicit and
    // independently checkable without relying on canonical-position wrapping.
    private static bool IsWithinStormEllipse(Vector2 localOffset, Vector2 radii)
    {
        float normalizedX = localOffset.X / radii.X;
        float normalizedY = localOffset.Y / radii.Y;
        return normalizedX * normalizedX + normalizedY * normalizedY <= 1.0001f;
    }

    private float ResolveLocalCloudCoverage(float weather, in MacroWeatherSample macro)
    {
        float macroBias = (macro.CombinedInfluence - 0.28f) * 0.46f;
        float densityBias = macro.StrongestInfluence * (macro.Density - 0.5f) * 0.24f;
        return Mathf.Clamp(
            Coverage
            + (weather - 0.5f) * WeatherClusteringStrength
            + macroBias
            + densityBias,
            0.015f,
            0.985f);
    }

    private CloudFormationProfile ResolveFormationProfile(
        int aroundCell,
        int acrossCell,
        int seed,
        in MacroWeatherSample macro)
    {
        float roll = Hash01(aroundCell, acrossCell, seed, 13);
        float precipitation = macro.PrecipitationPotential * macro.StrongestInfluence;
        return macro.Kind switch
        {
            WeatherSystemKind.ExceptionalFront when macro.StrongestInfluence >= 0.24f =>
                roll < 0.28f
                    ? CloudFormationProfile.Cumulonimbus
                    : roll < 0.62f
                        ? CloudFormationProfile.Nimbostratus
                        : roll < 0.78f
                            ? CloudFormationProfile.Altostratus
                            : roll < 0.90f
                                ? CloudFormationProfile.Cirrostratus
                                : CloudFormationProfile.Stratocumulus,
            WeatherSystemKind.MajorFront when macro.StrongestInfluence >= 0.22f =>
                roll < 0.12f && precipitation >= 0.35f
                    ? CloudFormationProfile.Cumulonimbus
                    : roll < 0.42f
                        ? CloudFormationProfile.Nimbostratus
                        : roll < 0.67f
                            ? CloudFormationProfile.Altostratus
                            : roll < 0.88f
                                ? CloudFormationProfile.Stratocumulus
                                : CloudFormationProfile.Altocumulus,
            WeatherSystemKind.CloudBank =>
                precipitation >= 0.32f && roll < 0.20f
                    ? CloudFormationProfile.Nimbostratus
                    : roll < 0.42f
                        ? CloudFormationProfile.Stratus
                        : roll < 0.72f
                            ? CloudFormationProfile.Stratocumulus
                            : roll < 0.90f
                                ? CloudFormationProfile.Altostratus
                                : CloudFormationProfile.Cirrostratus,
            WeatherSystemKind.CumulusCluster =>
                precipitation >= 0.18f && roll < 0.10f
                    ? CloudFormationProfile.Cumulonimbus
                    : roll < 0.58f
                        ? CloudFormationProfile.Cumulus
                        : roll < 0.84f
                            ? CloudFormationProfile.Altocumulus
                            : CloudFormationProfile.Cirrocumulus,
            _ => roll < 0.30f
                ? CloudFormationProfile.Cumulus
                : roll < 0.48f
                    ? CloudFormationProfile.Stratocumulus
                    : roll < 0.64f
                        ? CloudFormationProfile.Altocumulus
                        : roll < 0.74f
                            ? CloudFormationProfile.Stratus
                            : roll < 0.84f
                                ? CloudFormationProfile.Cirrus
                                : roll < 0.90f
                                    ? CloudFormationProfile.Altostratus
                                    : roll < 0.94f
                                        ? CloudFormationProfile.Cirrostratus
                                        : roll < 0.97f
                                            ? CloudFormationProfile.Cirrocumulus
                                            : roll < 0.99f
                                                ? CloudFormationProfile.Nimbostratus
                                                : CloudFormationProfile.Cumulonimbus
        };
    }

    private static bool ShouldPlacePhysicalCloud(
        int aroundCell,
        int acrossCell,
        int seed,
        float cellSize,
        CloudFormationProfile profile)
    {
        CloudPhysicalDefinition physical = CloudPhysicalCatalog.Get(profile);
        int spacingCells = Mathf.Clamp(
            Mathf.CeilToInt(physical.MinimumWidthMeters / Math.Max(1000.0f, cellSize) * 0.45f),
            1,
            8);
        int profileOffset = (int)profile * 17 + seed;
        if (PositiveModulo(aroundCell + profileOffset, spacingCells) != 0
            || PositiveModulo(acrossCell - profileOffset, spacingCells) != 0)
        {
            return false;
        }

        float occurrenceChance = physical.Occurrence switch
        {
            CloudOccurrence.Common => 1.0f,
            CloudOccurrence.Uncommon => 0.55f,
            CloudOccurrence.Rare => 0.24f,
            _ => 1.0f
        };
        return Hash01(aroundCell, acrossCell, seed, 117 + (int)profile) <= occurrenceChance;
    }

    private FormationVerticalProfile ResolveFormationVerticalProfile(
        int aroundCell,
        int acrossCell,
        int seed,
        in MacroWeatherSample macro,
        CloudFormationProfile profile)
    {
        float baseRoll = Hash01(aroundCell, acrossCell, seed, 14);
        float topRoll = Hash01(aroundCell, acrossCell, seed, 15);
        CloudPhysicalDefinition physical = CloudPhysicalCatalog.Get(profile);
        float maximumAltitude = profile == CloudFormationProfile.Cumulonimbus
            && macro.Kind != WeatherSystemKind.ExceptionalFront
            ? Math.Min(18000.0f, physical.MaximumAltitudeMeters)
            : physical.MaximumAltitudeMeters;
        float availableDepth = Math.Max(
            100.0f,
            maximumAltitude - physical.MinimumAltitudeMeters);
        float thickness = Mathf.Lerp(
            physical.MinimumThicknessMeters,
            physical.MaximumThicknessMeters,
            topRoll);
        thickness = Mathf.Clamp(thickness, 100.0f, availableDepth);
        float cloudBase;
        if (profile == CloudFormationProfile.Cumulonimbus)
        {
            cloudBase = physical.MinimumAltitudeMeters;
        }
        else
        {
            float maximumBase = Math.Max(
                physical.MinimumAltitudeMeters,
                maximumAltitude - thickness);
            cloudBase = Mathf.Lerp(physical.MinimumAltitudeMeters, maximumBase, baseRoll);
        }
        float cloudTop = Math.Min(maximumAltitude, cloudBase + thickness);
        return new FormationVerticalProfile(cloudBase, cloudTop, 1.0f, 1.0f);
    }

    private int ResolveArchetypeIndex(CloudFormationProfile requestedProfile, float selector)
    {
        CloudFormationProfile profile = requestedProfile;
        int matchingCount = CountArchetypes(profile);
        if (matchingCount == 0 && profile == CloudFormationProfile.AnvilStorm)
        {
            profile = CloudFormationProfile.StormTower;
            matchingCount = CountArchetypes(profile);
        }
        if (matchingCount == 0)
            return Math.Clamp((int)(selector * _archetypeProfiles.Length), 0, _archetypeProfiles.Length - 1);

        int selectedMatch = Math.Min(matchingCount - 1, (int)(selector * matchingCount));
        for (int index = 0; index < _archetypeProfiles.Length; index++)
        {
            if (_archetypeProfiles[index] != profile) continue;
            if (selectedMatch-- == 0) return index;
        }
        return 0;
    }

    private int CountArchetypes(CloudFormationProfile profile)
    {
        int count = 0;
        foreach (CloudFormationProfile candidate in _archetypeProfiles)
        {
            if (candidate == profile) count++;
        }
        return count;
    }

    private float ResolveFormationScale(
        int aroundCell,
        int acrossCell,
        int seed,
        in MacroWeatherSample macro)
    {
        float chance = Mathf.Clamp(MultiCellFormationChance, 0.0f, 0.5f)
            * (0.32f + macro.StrongestInfluence * 1.18f);
        chance += macro.Kind switch
        {
            WeatherSystemKind.MajorFront => 0.10f,
            WeatherSystemKind.ExceptionalFront => 0.22f,
            _ => 0.0f
        };
        if (Hash01(aroundCell, acrossCell, seed, 11) > Mathf.Clamp(chance, 0.0f, 0.75f))
            return 1.0f;

        float maximum = Mathf.Clamp(MaximumMultiCellFormationScale, 1.5f, 8.0f);
        float tail = Hash01(aroundCell, acrossCell, seed, 12);
        float exponent = macro.Kind == WeatherSystemKind.ExceptionalFront ? 1.8f : 3.1f;
        return Mathf.Lerp(1.55f, maximum, Mathf.Pow(tail, exponent));
    }

    private enum WeatherLifecycleStage { Static, Birth, Develop, Mature, Dissipate, Quiet }
    private readonly record struct WeatherLifecycle(
        int Generation, float Envelope, WeatherLifecycleStage Stage,
        double CycleSeconds, double OffsetSeconds);

    private WeatherLifecycle EvaluateWeatherLifecycle(
        int aroundCell,
        int acrossCell,
        int seed,
        double timeSeconds)
    {
        if (!NaturalWeatherLifecyclesEnabled)
            return new WeatherLifecycle(0, 1.0f, WeatherLifecycleStage.Static, 0.0, 0.0);

        double cycle = Math.Max(1.0,
            WeatherBirthSeconds + WeatherDevelopSeconds + WeatherMatureSeconds
            + WeatherDissipateSeconds + WeatherQuietSeconds);
        double offset = Hash01(aroundCell, acrossCell, seed, 109) * cycle;
        double shifted = timeSeconds + offset;
        int generation = (int)Math.Floor(shifted / cycle);
        float phase = (float)(shifted - Math.Floor(shifted / cycle) * cycle);
        float variation = Mathf.Clamp(WeatherLifecycleVariation, 0.0f, 0.45f);
        float birth = VariedLifecycleDuration(WeatherBirthSeconds, aroundCell, acrossCell, seed, generation, 110, variation);
        float develop = VariedLifecycleDuration(WeatherDevelopSeconds, aroundCell, acrossCell, seed, generation, 111, variation);
        float mature = VariedLifecycleDuration(WeatherMatureSeconds, aroundCell, acrossCell, seed, generation, 112, variation);
        float dissipate = VariedLifecycleDuration(WeatherDissipateSeconds, aroundCell, acrossCell, seed, generation, 113, variation);
        float quiet = VariedLifecycleDuration(WeatherQuietSeconds, aroundCell, acrossCell, seed, generation, 114, variation);
        float normalization = (float)cycle / Mathf.Max(0.001f, birth + develop + mature + dissipate + quiet);
        birth *= normalization;
        develop *= normalization;
        mature *= normalization;
        dissipate *= normalization;
        if (phase < birth) return new WeatherLifecycle(generation, SmoothUnit(phase / Mathf.Max(0.001f, birth)) * 0.30f, WeatherLifecycleStage.Birth, cycle, offset);
        phase -= birth;
        if (phase < develop) return new WeatherLifecycle(generation, Mathf.Lerp(0.30f, 1.0f, SmoothUnit(phase / Mathf.Max(0.001f, develop))), WeatherLifecycleStage.Develop, cycle, offset);
        phase -= develop;
        if (phase < mature) return new WeatherLifecycle(generation, 1.0f, WeatherLifecycleStage.Mature, cycle, offset);
        phase -= mature;
        if (phase < dissipate) return new WeatherLifecycle(generation, 1.0f - SmoothUnit(phase / Mathf.Max(0.001f, dissipate)), WeatherLifecycleStage.Dissipate, cycle, offset);
        return new WeatherLifecycle(generation, 0.0f, WeatherLifecycleStage.Quiet, cycle, offset);
    }

    private static float VariedLifecycleDuration(float baseSeconds, int aroundCell, int acrossCell, int seed, int generation, int salt, float variation)
        => Mathf.Max(0.001f, baseSeconds * Mathf.Lerp(1.0f - variation, 1.0f + variation,
            LifecycleHash01(aroundCell, acrossCell, seed, generation, salt)));

    private static float LifecycleHash01(int aroundCell, int acrossCell, int seed, int generation, int salt)
        => Hash01(aroundCell, acrossCell, unchecked(seed ^ generation * 0x45D9F3B), salt);

    private float WeatherHash01(int aroundCell, int acrossCell, int seed, int generation, int salt)
        => NaturalWeatherLifecyclesEnabled
            ? LifecycleHash01(aroundCell, acrossCell, seed, generation, salt)
            : Hash01(aroundCell, acrossCell, seed, salt);

    private List<WeatherSystemDefinition> BuildNearbyWeatherSystems(
        double centerAroundMeters,
        double centerAcrossMeters,
        double timeSeconds,
        int seed,
        float queryRadiusMeters)
    {
        var systems = new List<WeatherSystemDefinition>();
        if (_grid == null) return systems;
        float macroCell = Mathf.Clamp(MacroWeatherCellMeters, 16000.0f, 64000.0f);
        float maximumRadius = Mathf.Clamp(
            MaximumMacroSystemRadiusMeters,
            40000.0f,
            160000.0f);
        double sourceAround = WrapAround(
            centerAroundMeters - WeatherWindMetersPerSecond.X * timeSeconds);
        double sourceAcross = centerAcrossMeters - WeatherWindMetersPerSecond.Y * timeSeconds;
        int aroundCellCount = Math.Max(
            1,
            Mathf.CeilToInt((float)(_grid.GetCanonicalRingCircumferenceMeters64() / macroCell)));
        int centerCellAround = Mathf.FloorToInt((float)(sourceAround / macroCell));
        int centerCellAcross = Mathf.FloorToInt((float)(sourceAcross / macroCell));
        int searchRadius = Math.Clamp(
            Mathf.CeilToInt((Mathf.Max(0.0f, queryRadiusMeters) + maximumRadius) / macroCell) + 1,
            2,
            12);

        for (int dz = -searchRadius; dz <= searchRadius; dz++)
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            int rawAroundCell = centerCellAround + dx;
            int systemAroundCell = PositiveModulo(rawAroundCell, aroundCellCount);
            int systemAcrossCell = centerCellAcross + dz;
            if (TryBuildWeatherSystem(systemAroundCell, systemAcrossCell, seed, timeSeconds, out WeatherSystemDefinition system))
                systems.Add(system);
        }

        return systems;
    }

    /// <summary>Single authoritative seeded macro-cell constructor shared by weather queries and lightning.</summary>
    private bool TryBuildWeatherSystem(int systemAroundCell, int systemAcrossCell, int seed, double timeSeconds,
        out WeatherSystemDefinition system)
    {
        system = default;
        if (_grid == null) return false;
        float macroCell = Mathf.Clamp(MacroWeatherCellMeters, 16000.0f, 64000.0f);
        float maximumRadius = Mathf.Clamp(MaximumMacroSystemRadiusMeters, 40000.0f, 160000.0f);
        WeatherLifecycle lifecycle = EvaluateWeatherLifecycle(systemAroundCell, systemAcrossCell, seed, timeSeconds);
            if (lifecycle.Envelope <= 0.0001f
                || WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 100)
                > Mathf.Clamp(MacroSystemSpawnChance, 0.1f, 0.8f))
            {
                return false;
            }

            float typeRoll = WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 101);
            WeatherSystemKind kind = typeRoll switch
            {
                < 0.64f => WeatherSystemKind.CumulusCluster,
                < 0.89f => WeatherSystemKind.CloudBank,
                < 0.988f => WeatherSystemKind.MajorFront,
                _ => WeatherSystemKind.ExceptionalFront
            };
            float radiusRandom = WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 102);
            float majorRadius = kind switch
            {
                WeatherSystemKind.CumulusCluster => Mathf.Lerp(8000.0f, 17000.0f, radiusRandom),
                WeatherSystemKind.CloudBank => Mathf.Lerp(18000.0f, 36000.0f, radiusRandom),
                WeatherSystemKind.MajorFront => Mathf.Lerp(36000.0f, 70000.0f, radiusRandom),
                WeatherSystemKind.ExceptionalFront => Mathf.Lerp(70000.0f, maximumRadius, radiusRandom),
                _ => 8000.0f
            };
            float aspect = kind switch
            {
                WeatherSystemKind.CumulusCluster => Mathf.Lerp(
                    0.85f,
                    1.45f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 103)),
                WeatherSystemKind.CloudBank => Mathf.Lerp(
                    1.35f,
                    2.25f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 103)),
                WeatherSystemKind.MajorFront => Mathf.Lerp(
                    2.0f,
                    3.4f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 103)),
                WeatherSystemKind.ExceptionalFront => Mathf.Lerp(
                    2.8f,
                    4.5f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 103)),
                _ => 1.0f
            };
            float minorRadius = majorRadius / Mathf.Max(0.8f, aspect);
            float orientation = (WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 104) - 0.5f)
                * Mathf.Pi;
            double centerAround = WrapAround(
                (systemAroundCell + 0.5) * macroCell
                + (WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 105) - 0.5)
                    * macroCell
                    * 0.82);
            double centerAcross = (systemAcrossCell + 0.5) * macroCell
                + (WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 106) - 0.5)
                    * macroCell
                    * 0.82;
            // Source coordinates can be far outside the finite ring after a
            // long cross-wind run. Cull their *advected* centre, not the ancient
            // source, or all weather eventually drains from the ring.
            double advectedAcross = centerAcross + WeatherWindMetersPerSecond.Y * timeSeconds;
            if (advectedAcross < -majorRadius
                || advectedAcross > _grid.RingAcrossWidthMeters + majorRadius)
            {
                return false;
            }
            float density = kind switch
            {
                WeatherSystemKind.CumulusCluster => Mathf.Lerp(
                    0.46f,
                    0.66f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 107)),
                WeatherSystemKind.CloudBank => Mathf.Lerp(
                    0.60f,
                    0.80f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 107)),
                WeatherSystemKind.MajorFront => Mathf.Lerp(
                    0.75f,
                    0.94f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 107)),
                WeatherSystemKind.ExceptionalFront => Mathf.Lerp(
                    0.88f,
                    1.0f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 107)),
                _ => 0.5f
            };
            float precipitation = kind switch
            {
                WeatherSystemKind.CumulusCluster => Mathf.Lerp(
                    0.02f,
                    0.28f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 108)),
                WeatherSystemKind.CloudBank => Mathf.Lerp(
                    0.20f,
                    0.58f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 108)),
                WeatherSystemKind.MajorFront => Mathf.Lerp(
                    0.52f,
                    0.88f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 108)),
                WeatherSystemKind.ExceptionalFront => Mathf.Lerp(
                    0.78f,
                    1.0f,
                    WeatherHash01(systemAroundCell, systemAcrossCell, seed, lifecycle.Generation, 108)),
                _ => 0.0f
            };
            system = new WeatherSystemDefinition(
                WeatherSystemId(systemAroundCell, systemAcrossCell, unchecked(seed ^ lifecycle.Generation * 0x45D9F3B)),
                kind,
                centerAround,
                centerAcross,
                majorRadius * lifecycle.Envelope,
                minorRadius * lifecycle.Envelope,
                orientation,
                Mathf.Cos(orientation),
                Mathf.Sin(orientation),
                1.0 / Math.Max(1.0, majorRadius * lifecycle.Envelope * majorRadius * lifecycle.Envelope),
                1.0 / Math.Max(1.0, minorRadius * lifecycle.Envelope * minorRadius * lifecycle.Envelope),
                density * lifecycle.Envelope,
                precipitation * lifecycle.Envelope);
        return true;
    }

    private MacroWeatherSample EvaluateMacroWeather(
        double aroundMeters,
        double acrossMeters,
        double timeSeconds,
        IReadOnlyList<WeatherSystemDefinition> systems)
    {
        double circumference = Math.Max(
            1.0,
            _grid?.GetCanonicalRingCircumferenceMeters64() ?? 1.0);
        double sourceAround = WrapAround(
            aroundMeters - WeatherWindMetersPerSecond.X * timeSeconds,
            circumference);
        double sourceAcross = acrossMeters - WeatherWindMetersPerSecond.Y * timeSeconds;
        float combinedInfluence = 0.0f;
        float strongest = 0.0f;
        WeatherSystemDefinition strongestSystem = default;

        foreach (WeatherSystemDefinition system in systems)
        {
            double deltaAround = ShortestAroundDelta(
                sourceAround,
                system.SourceAroundMeters,
                circumference);
            double deltaAcross = system.SourceAcrossMeters - sourceAcross;
            double maximumDistance = system.MajorRadiusMeters;
            if (deltaAround * deltaAround + deltaAcross * deltaAcross
                >= maximumDistance * maximumDistance)
            {
                continue;
            }
            double along = system.OrientationCosine * deltaAround
                + system.OrientationSine * deltaAcross;
            double cross = -system.OrientationSine * deltaAround
                + system.OrientationCosine * deltaAcross;
            float normalizedSquared = (float)(
                along * along * system.InverseMajorRadiusSquared
                + cross * cross * system.InverseMinorRadiusSquared);
            if (normalizedSquared >= 1.0f) continue;
            float normalized = Mathf.Sqrt(normalizedSquared);
            float influence = 1.0f - SmoothUnit((normalized - 0.32f) / 0.68f);
            float weightedInfluence = influence * system.Density;
            combinedInfluence = 1.0f
                - (1.0f - combinedInfluence) * (1.0f - weightedInfluence);
            if (weightedInfluence <= strongest) continue;
            strongest = weightedInfluence;
            strongestSystem = system;
        }

        return new MacroWeatherSample(
            strongestSystem.SystemId,
            strongestSystem.Kind,
            Mathf.Clamp(combinedInfluence, 0.0f, 1.0f),
            Mathf.Clamp(strongest, 0.0f, 1.0f),
            strongestSystem.SourceAroundMeters,
            strongestSystem.SourceAcrossMeters,
            strongestSystem.MajorRadiusMeters,
            strongestSystem.MinorRadiusMeters,
            strongestSystem.OrientationRadians,
            strongestSystem.Density,
            strongestSystem.PrecipitationPotential);
    }

    private static int WeatherSystemId(int aroundCell, int acrossCell, int seed)
    {
        unchecked
        {
            uint value = (uint)seed ^ 0xA511E9B3u;
            value ^= (uint)aroundCell * 0x9E3779B9u;
            value ^= (uint)acrossCell * 0x85EBCA6Bu;
            value ^= value >> 16;
            value *= 0x7FEB352Du;
            value ^= value >> 15;
            int id = (int)(value & 0x7FFFFFFF);
            return id == 0 ? 1 : id;
        }
    }

    private static float SmoothUnit(float value)
    {
        float t = Mathf.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    private double WrapAround(double around)
    {
        if (_grid == null) return around;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        return WrapAround(around, circumference);
    }

    private static double WrapAround(double around, double circumference)
    {
        double wrapped = around % circumference;
        return wrapped < 0.0 ? wrapped + circumference : wrapped;
    }

    private double ShortestAroundDelta(double from, double to)
    {
        if (_grid == null) return to - from;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        return ShortestAroundDelta(from, to, circumference);
    }

    private static double ShortestAroundDelta(double from, double to, double circumference)
    {
        double delta = WrapAround(to, circumference) - WrapAround(from, circumference);
        if (delta > circumference * 0.5) delta -= circumference;
        if (delta < -circumference * 0.5) delta += circumference;
        return delta;
    }

    private static int PositiveModulo(int value, int divisor)
    {
        int result = value % divisor;
        return result < 0 ? result + divisor : result;
    }

    private static float Hash01(int x, int z, int seed, int salt)
    {
        unchecked
        {
            uint hash = (uint)seed ^ 0x9E3779B9u;
            hash ^= (uint)x * 0x85EBCA6Bu;
            hash = (hash << 13) | (hash >> 19);
            hash ^= (uint)z * 0xC2B2AE35u;
            hash ^= (uint)salt * 0x27D4EB2Fu;
            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return (hash & 0x00FFFFFFu) / 16777215.0f;
        }
    }

    private static float SampleWeatherNoise(
        int cellX,
        int cellZ,
        int seed,
        int scaleCells,
        int salt)
    {
        float x = cellX / (float)Math.Max(1, scaleCells);
        float z = cellZ / (float)Math.Max(1, scaleCells);
        int x0 = Mathf.FloorToInt(x);
        int z0 = Mathf.FloorToInt(z);
        float fx = x - x0;
        float fz = z - z0;
        fx = fx * fx * (3.0f - 2.0f * fx);
        fz = fz * fz * (3.0f - 2.0f * fz);
        float a = Mathf.Lerp(
            Hash01(x0, z0, seed, salt),
            Hash01(x0 + 1, z0, seed, salt),
            fx);
        float b = Mathf.Lerp(
            Hash01(x0, z0 + 1, seed, salt),
            Hash01(x0 + 1, z0 + 1, seed, salt),
            fx);
        return Mathf.Lerp(a, b, fz);
    }

    private void UpdateGpuTiming(double delta)
    {
        if (!MeasureGpuFrameTime || !_viewportRid.IsValid) return;
        double gpuMilliseconds = RenderingServer.ViewportGetMeasuredRenderTimeGpu(_viewportRid);
        if (gpuMilliseconds > 0.0 && double.IsFinite(gpuMilliseconds))
        {
            _gpuFrameTotal += gpuMilliseconds;
            _gpuFrameSamples++;
        }
        _timingElapsed += delta;
        if (_timingElapsed < Math.Max(1.0, TimingReportSeconds)) return;
        if (_gpuFrameSamples > 0)
        {
            double average = _gpuFrameTotal / _gpuFrameSamples;
            GD.Print(
                $"Cloud A/B GPU [{(_runtimeEnabled ? "ON" : "OFF")}, {Quality}]: "
                + $"total frame {average:0.00} ms over {_gpuFrameSamples} samples, "
                + $"{VisibleFormationCount} detailed + {VisibleTransitionFormationCount} transition formations, "
                + $"{VisibleUnionClusterCount} seamless unions containing {UnionMemberCount} formations "
                + $"({UnionTriangleCount} triangles, {LastUnionBuildMilliseconds:0.00}ms worker), "
                + $"{VisibleMultiCellFormationCount} multi-cell across {VisibleWeatherSystemCount} systems, "
                + $"profiles bank/tower/anvil="
                + $"{VisibleLowCloudBankCount}/{VisibleStormTowerCount}/{VisibleAnvilStormCount}, "
                + $"retained casters={VisibleConventionalShadowCasterCount}, "
                + $"patch {LastCloudPatchBuildMilliseconds:0.00}ms (max {MaxCloudPatchBuildMilliseconds:0.00}), "
                + $"stages weather/formation/install/shadow="
                + $"{LastWeatherSystemBuildMilliseconds:0.00}/{LastFormationAssemblyMilliseconds:0.00}/"
                + $"{LastInstanceInstallMilliseconds:0.00}/{LastShadowMapBuildMilliseconds:0.00}ms, "
                + $"{ActiveArchetypeCount + ActiveTransitionArchetypeCount} archetype owners.");
        }
        ResetGpuTiming();
    }

    private void ResetGpuTiming()
    {
        _timingElapsed = 0.0;
        _gpuFrameTotal = 0.0;
        _gpuFrameSamples = 0;
    }

    private void RunContractSelfTest()
    {
        LocalWeatherState localWeather = _explorer != null
            ? QueryLocalWeather(_explorer.CanonicalPosition)
            : default;
        bool valid = CloudBaseMeters < CloudTopMeters
            && _activeRadiusMeters >= 2000.0f
            && _transitionRadiusMeters > _activeRadiusMeters
            && _archetypesReady
            && _archetypeInstances.Count >= 3
            && _transitionArchetypeInstances.Count == _archetypeInstances.Count
            && ArchetypeTriangleCount > 0
            && TransitionArchetypeTriangleCount > 0
            && localWeather.CloudCoverage is >= 0.0f and <= 1.0f
            && localWeather.PrecipitationPotential is >= 0.0f and <= 1.0f
            && localWeather.CloudBaseAltitudeMeters >= 0.0f
            && localWeather.CloudTopAltitudeMeters > localWeather.CloudBaseAltitudeMeters
            && localWeather.CloudTopAltitudeMeters <= 20000.0f
            && (!CastCloudShadows
                || !RetainNearbyConventionalShadowCasters
                || _shadowArchetypeInstances.Count == _archetypeInstances.Count)
            && (_externalVolumetricRendererOwnsLocalClouds
                || _worldEnvironment?.Environment?.VolumetricFogEnabled == false);
        if (!valid)
        {
            GD.PushError("Merged mesh cloud contract failed: invalid band, archetypes, geometry, or fog state.");
            return;
        }
        GD.Print(
            $"Merged mesh clouds ready: quality={Quality}, band={CloudBaseMeters:0}-{CloudTopMeters:0}m, "
            + $"detailRadius={_activeRadiusMeters:0}m, transitionRadius={_transitionRadiusMeters:0}m, "
            + $"archetypes={ActiveArchetypeCount}+{ActiveTransitionArchetypeCount}, "
            + $"archetypeTriangles={ArchetypeTriangleCount}+{TransitionArchetypeTriangleCount}, "
            + $"formations={VisibleFormationCount}+{VisibleTransitionFormationCount}, "
            + $"unionClusters={VisibleUnionClusterCount}, unionMembers={UnionMemberCount}, "
            + $"unionTriangles={UnionTriangleCount}, unionMs={LastUnionBuildMilliseconds:0.00}, "
            + $"unionGap={UnionGapMeters:0}m/{UnionVerticalGapMeters:0}m vertical, "
            + $"multiCell={VisibleMultiCellFormationCount}, weatherSystems={VisibleWeatherSystemCount}, "
            + $"profiles=bank:{VisibleLowCloudBankCount}/tower:{VisibleStormTowerCount}/anvil:{VisibleAnvilStormCount}, "
            + $"retainedShadowCasters={VisibleConventionalShadowCasterCount}@"
            + $"{Mathf.Min(ConventionalShadowRetentionRadiusMeters, _activeRadiusMeters):0}m, "
            + $"localWeather={localWeather.SystemKind}/{localWeather.DominantFormationProfile}/"
            + $"{localWeather.CloudCoverage:0.00}/rainPotential={localWeather.PrecipitationPotential:0.00}/"
            + $"base-topDatum={localWeather.CloudBaseAltitudeMeters:0}-{localWeather.CloudTopAltitudeMeters:0}m, "
            + $"patchMs={LastCloudPatchBuildMilliseconds:0.00} "
            + $"(weather/formation/install/shadow="
            + $"{LastWeatherSystemBuildMilliseconds:0.00}/{LastFormationAssemblyMilliseconds:0.00}/"
            + $"{LastInstanceInstallMilliseconds:0.00}/{LastShadowMapBuildMilliseconds:0.00}), "
            + $"projectedShadows={(ProjectLongRangeShadows ? $"{ShadowMapResolution}px" : "off")}, "
            + $"farShell=scaled-space, A/B={ToggleKey}.");
    }
}
