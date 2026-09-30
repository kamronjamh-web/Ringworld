using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Renders only the whole-ring analytic tier in an isolated, scaled World3D and
/// composites it as the main world's background canvas. No far-ring vertex ever
/// enters the gameplay camera's frustum or million-metre coordinate domain.
/// </summary>
public partial class RingScaledFarViewport : Node
{
	/// <summary>
	/// The far ring is a canvas background and therefore cannot receive main
	/// viewport volumetric depth. Either supported local-cloud renderer must
	/// keep the matching backdrop mask alive while it owns the cloud image.
	/// </summary>
	public static bool ShouldCompositeCloudBackdrop(
		bool maskEnabled,
		bool legacyLocalVolumeVisible,
		bool sunshineCloudsVisible)
		=> maskEnabled && (legacyLocalVolumeVisible || sunshineCloudsVisible);

	internal static bool HasFastTravelCavityClosure(
		bool buildRoof,
		bool buildLowerHullFloor,
		bool nearFadeEnabled,
		bool nearSpaceEnabled)
		=> buildRoof && buildLowerHullFloor && !nearFadeEnabled && nearSpaceEnabled;
	private sealed record FarRegionalBandData(
		string Name,
		Vector3[] Vertices,
		Vector3[] Normals,
		Color[] Colors,
		Vector2[] MacroShadowUvs,
		int[] Indices,
		double InnerRadius,
		double OuterRadius);

	/// <summary>
	/// A radial regional tier.  Sample spacing is a height-sampling target only:
	/// the polar mesh deliberately keeps its 96 angular columns and bounded radial
	/// row count, so adding a tier never silently multiplies angular geometry.
	/// </summary>
	internal readonly record struct FarRegionalBandSpec(
		string Name,
		double InnerRadius,
		double OuterRadius,
		double SampleSpacing);

	private sealed record FarRegionalBuildResult(
		long AnchorAround,
		int AnchorAcross,
		long TransitGeneration,
		ulong EditVersion,
		long LayoutVersion,
		IReadOnlyList<FarRegionalBandData> Bands);

	[Export] public bool Enabled { get; set; }
	[Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
	[Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
	[Export] public NodePath SourceCameraPath { get; set; } = new("../Explorer/Head/Camera3D");
	[Export] public NodePath MainEnvironmentPath { get; set; } = new("../WorldEnvironment");
	[Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
	[Export] public NodePath PlanetLightPath { get; set; } = new("../PlanetLight");
	[Export] public NodePath LegacyAnalyticPath { get; set; } = new("../AnalyticGlobalRing");
	[Export] public NodePath LegacyCloudPath { get; set; } = new("../RingCloudLayer");
	[Export] public NodePath LocalVolumetricCloudRendererPath { get; set; } = new("../LocalVolumetricCloudRenderer");
	[Export] public NodePath SunshineCloudBridgePath { get; set; } = new("../SunshineCloudBridge");
	[Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");

	[ExportGroup("Scaled space")]
	[Export(PropertyHint.Range, "0.0001,0.01,0.0001")] public float CoordinateScale { get; set; } = 0.001f;
	[Export(PropertyHint.Range, "0.25,1.0,0.05")] public float ResolutionScale { get; set; } = 0.75f;
	[Export(PropertyHint.Range, "1000,1000000,1000,or_greater")]
	public float MinimumWholeRingFarClip { get; set; } = 100000.0f;
	[Export(PropertyHint.Range, "0.001,10,0.001")]
	public float MinimumWholeRingNearClip { get; set; } = 0.1f;
	[Export(PropertyHint.Range, "128,4096,64")] public int AroundSegments { get; set; } = 1024;
	[Export(PropertyHint.Range, "4,128,1")] public int AcrossSegments { get; set; } = 16;
	[Export(PropertyHint.Range, "100,1000000,100")] public int RingWidthMeters { get; set; } = 318000;
	[Export(PropertyHint.Range, "1000,50000,100")] public float StructuralDepthMeters { get; set; } = 22300.0f;
	[Export(PropertyHint.Range, "0,50000,100")] public float StructuralWallTopMeters { get; set; } = 10000.0f;
	[Export] public bool BuildStructuralUnderside { get; set; } = true;
	[Export] public bool BuildTerrainRoofUnderside { get; set; } = true;
	[Export] public bool BuildLowerHullCavityFloor { get; set; } = true;
	[Export(PropertyHint.Range, "100,10000,100")] public float CloudAltitudeMeters { get; set; } = 2200.0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarCloudCoverage { get; set; } = 0.46f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarCloudOpacity { get; set; } = 0.68f;
	[Export(PropertyHint.Range, "0.01,0.4,0.01")] public float FarCloudSoftness { get; set; } = 0.20f;
	[Export(PropertyHint.Range, "1000,50000,500")] public float LocalCloudRadiusMeters { get; set; } = 10000.0f;
	[Export(PropertyHint.Range, "100,10000,100")] public float LocalCloudBaseMeters { get; set; } = 2000.0f;
	[Export(PropertyHint.Range, "200,12000,100")] public float LocalCloudTopMeters { get; set; } = 3200.0f;
	[Export] public bool LocalVolumetricCloudsEnabled { get; set; } = false;
	[ExportGroup("Far atmosphere shroud")]
	[Export(PropertyHint.Range, "100,10000,100")]
	public float FarAtmosphereExitFadeMeters { get; set; } = 3218.688f;
	[ExportGroup("Far cloud clipmap")]
	[Export] public bool FarCloudClipmapEnabled { get; set; } = true;
	[Export(PropertyHint.Range, "30000,500000,5000")] public float FarCloudClipmapRadiusMeters { get; set; } = 180000.0f;
	[Export(PropertyHint.Range, "10000,200000,1000")] public float FarCloudClipmapHandoffStartMeters { get; set; } = 48000.0f;
	[Export(PropertyHint.Range, "0,1,0.01")] public float FarCloudClipmapOpacity { get; set; } = 0.72f;
	[ExportGroup("Far storm lightning")]
	[Export] public bool FarStormLightningEnabled { get; set; } = true;
	[Export(PropertyHint.Range, "1,2,1")] public int FarStormLightningMaximumEvents { get; set; } = 2;
	[Export(PropertyHint.Range, "0.25,4,0.05")] public float FarStormLightningIntensity { get; set; } = 1.15f;
	[Export(PropertyHint.Range, "5000,180000,1000")] public float FarStormLightningRadiusMeters { get; set; } = 42000.0f;
	// Compatibility-only: shared weather lightning owns cadence and lifetime.
	[Export(PropertyHint.Range, "4,40,1")] public float FarStormLightningCycleSeconds { get; set; } = 15.0f;
	[ExportGroup("Shared far weather atlas")]
	// Kept deliberately small: at Certified scale this is macro weather, not
	// near-cloud detail. It is generated once in memory from TerrainSampler.Seed.
	[Export(PropertyHint.Range, "512,8192,512")] public int FarWeatherAtlasWidth { get; set; } = 1024;
	[Export(PropertyHint.Range, "32,512,32")] public int FarWeatherAtlasHeight { get; set; } = 128;
	[Export(PropertyHint.Range, "1,120,1")] public float FarWeatherAtlasRefreshSeconds { get; set; } = 8.0f;
	[Export(PropertyHint.Range, "16,128,16")] public int FarWeatherAtlasWorkBudgetPerFrame { get; set; } = 128;
	[Export(PropertyHint.Range, "10000,2000000,10000")] public float FadeStartMeters { get; set; } = 500000.0f;
	[Export(PropertyHint.Range, "10000,2000000,10000")] public float FadeEndMeters { get; set; } = 750000.0f;
	[Export] public bool KeepLocalTerrainFallbackVisible { get; set; } = true;
	[ExportGroup("Mega regional far LODs")]
	[Export] public bool RegionalFarLodsEnabled { get; set; }
	[Export] public bool IntermediateRegionalLodTiersEnabled { get; set; } = true;
	[ExportGroup("Regional transit guard")]
	// The scaled far terrain is a coverage guard, so it stays visible during
	// high-speed noclip as well.
	// Retained for scene compatibility.  The regional guard must never be
	// hidden from speed alone, even if an older scene serialized this as true.
	[Export] public bool HideRegionalTerrainDuringFastTravel { get; set; }
	[Export(PropertyHint.Range, "1000,1000000000,1000")]
	public float RegionalTransitSpeedThresholdMetersPerSecond { get; set; } = 100000.0f;
	[Export(PropertyHint.Range, "0,1000000000,1000")]
	public float RegionalTransitResumeSpeedMetersPerSecond { get; set; } = 10000.0f;
	[Export(PropertyHint.Range, "0,5,0.05")]
	public float RegionalTransitResumeSeconds { get; set; } = 0.5f;
	[ExportGroup("Altitude-aware terrain fallback")]
	[Export(PropertyHint.Range, "0,20000,100")] public float FullLocalCutoutBelowAltitudeMeters { get; set; } = 2000.0f;
	[Export(PropertyHint.Range, "1000,100000,100")] public float NoLocalCutoutAboveAltitudeMeters { get; set; } = 20000.0f;
	[Export(PropertyHint.Range, "0,250000,1000")] public float MinimumLocalTerrainCutoutMeters { get; set; } = 16000.0f;
	[ExportGroup("Far water handoff")]
	[Export] public bool CoupleFarWaterCutoutToTerrain { get; set; } = false;
	[Export(PropertyHint.Range, "0,100000,250")] public float FarWaterCutoutMeters { get; set; } = 1000.0f;
	[Export] public Color LowTerrainColor { get; set; } = new(0.12f, 0.19f, 0.12f, 1.0f);
	[Export] public Color HighTerrainColor { get; set; } = new(0.29f, 0.27f, 0.20f, 1.0f);
	[Export] public Color HullColor { get; set; } = new(0.20f, 0.24f, 0.29f, 1.0f);
	[ExportGroup("Night readability")]
	[Export] public Color NightTint { get; set; } = new(0.30f, 0.45f, 0.70f, 1.0f);
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float TerrainNightBrightness { get; set; } = 0.72f;
	[Export(PropertyHint.Range, "0,1,0.01")]
	public float HullNightBrightness { get; set; } = 0.75f;
	[ExportGroup("Structural scale cues")]
	[Export] public bool StructuralScaleCuesEnabled { get; set; } = true;
	[Export(PropertyHint.Range, "25000,1000000,5000")]
	public float StructuralLongitudinalSpacingMeters { get; set; } = 250000.0f;
	[Export(PropertyHint.Range, "100,10000,100")]
	public float StructuralLongitudinalHalfWidthMeters { get; set; } = 1200.0f;
	[Export(PropertyHint.Range, "5000,200000,1000")]
	public float StructuralCrossSpacingMeters { get; set; } = 50000.0f;
	[Export(PropertyHint.Range, "50,5000,50")]
	public float StructuralCrossHalfWidthMeters { get; set; } = 450.0f;
	[Export(PropertyHint.Range, "50,5000,50")]
	public float StructuralTopRailHalfWidthMeters { get; set; } = 350.0f;
	[Export] public Color StructuralCueColor { get; set; } = new(0.08f, 0.72f, 0.82f, 1.0f);
	[Export(PropertyHint.Range, "0,2,0.01")]
	public float StructuralCueDayEnergy { get; set; } = 0.16f;
	[Export(PropertyHint.Range, "0,4,0.01")]
	public float StructuralCueNightEnergy { get; set; } = 0.85f;

	[ExportGroup("Cloud backdrop transmittance")]
	// The far ring is a canvas-backed environment background, so local cloud
	// renderers cannot naturally depth-compose over it. Sunshine publishes its
	// exact color/density texture here; the legacy renderer retains a small
	// proxy transmittance pass. Both paths idle when local clouds are hidden.
	[Export] public bool CloudTransmittanceMaskEnabled { get; set; } = false;
	[Export(PropertyHint.Range, "0.25,1.0,0.05")]
	public float CloudMaskResolutionScale { get; set; } = 0.25f;
	[Export(PropertyHint.Range, "5,30,1")]
	public float CloudMaskUpdatesPerSecond { get; set; } = 15.0f;
	[Export(PropertyHint.Range, "0.01,0.25,0.01")]
	public float CloudMaskTemporalBlendSeconds { get; set; } = 0.06f;
	[Export(PropertyHint.Range, "4,24,1")]
	public int CloudMaskRaymarchSteps { get; set; } = 10;
	[Export(PropertyHint.Range, "1,3,1")]
	public int CloudMaskMaximumQualityLevel { get; set; } = 2;
	[Export(PropertyHint.Range, "0.01,4.0,0.01")]
	public float CloudMaskExtinctionPerKilometer { get; set; } = 0.72f;
	[Export(PropertyHint.Range, "0,2,0.01")]
	public float CloudMaskOpacityScale { get; set; } = 1.0f;

	private VoxelChunkGridManager? _grid;
	private RingVoxelExplorerController? _explorer;
	private Camera3D? _sourceCamera;
	private DirectionalLight3D? _sourceSun;
	private DirectionalLight3D? _sourcePlanetLight;
	private global::RingworldTechDemo.RingLocalVolumetricCloudRenderer? _localVolumetricClouds;
	private global::RingworldTechDemo.RingSunshineCloudBridge? _sunshineClouds;
	private global::RingworldTechDemo.RingVolumetricCloudLayer? _weatherSource;
	private WorldEnvironment? _mainWorldEnvironment;
	private SubViewport? _farViewport;
	private Camera3D? _farCamera;
	private DirectionalLight3D? _farSun;
	private DirectionalLight3D? _farPlanetLight;
	// The complete ring is rendered in absolute scaled space. The two regional
	// tiers use their own transparent viewport, whose origin is moved with the
	// player before the doubles are narrowed to GPU floats.
	private SubViewport? _regionalFarViewport;
	private Camera3D? _regionalFarCamera;
	private DirectionalLight3D? _regionalFarSun;
	private DirectionalLight3D? _regionalFarPlanetLight;
	private ShaderMaterial? _terrainMaterial;
	private ShaderMaterial? _waterMaterial;
	private ShaderMaterial? _hullMaterial;
	private ShaderMaterial? _cavityShellMaterial;
	private ShaderMaterial? _cloudMaterial;
	private Texture2D? _farWeatherAtlas;
	private double _farWeatherAtlasSnapshotTime;
	private double _farWeatherAtlasNextRefreshTime;
	private double _lastFarWeatherRawTime = double.NaN;
	public double FarWeatherAtlasLastWorkMilliseconds { get; private set; }
	public double FarWeatherAtlasLastUploadMilliseconds { get; private set; }
	public int FarWeatherAtlasLastWorkItems { get; private set; }
	public ulong FarWeatherAtlasPublicationCount { get; private set; }
	public ulong FarWeatherAtlasContentHash { get; private set; }
	public string FarWeatherAtlasContentHashText => FarWeatherAtlasContentHash.ToString("X16");
	public Godot.Collections.Array<long> SelectedFarLightningEventIds { get; } = new();
	private FarWeatherAtlasBuild? _farWeatherAtlasBuild;
	private MeshInstance3D? _scaledCloudShell;
	private readonly MeshInstance3D?[] _farCloudClipmapLayers = new MeshInstance3D?[3];
	private readonly ShaderMaterial?[] _farCloudClipmapMaterials = new ShaderMaterial?[3];
	// Active meshes remain visible while an asynchronous replacement is uploaded.
	// The candidate set is published only after every band has reached the GPU.
	private readonly List<MeshInstance3D> _farRegionalTerrains = new();
	private readonly List<MeshInstance3D> _farRegionalStagingTerrains = new();
	private ShaderMaterial? _localCloudCanopyMaterial;
	private MeshInstance3D? _localCloudCanopy;
	private ShaderMaterial? _scaledCloudVolumeMaterial;
	private FogVolume? _scaledCloudVolume;
	private Godot.Environment? _farEnvironment;
	// GPU-only physical-distance export of the opaque whole-ring depth.  Sunshine
	// consumes this before its raymarch; never read this texture on the CPU.
	private CompositorEffect? _farDepthCapture;
	private TextureRect? _backdrop;
	private TextureRect? _regionalBackdrop;
	private ShaderMaterial? _backdropCloudCompositeMaterial;
	private readonly SubViewport?[] _cloudMaskViewports = new SubViewport?[2];
	private readonly ShaderMaterial?[] _cloudMaskMaterials = new ShaderMaterial?[2];
	private int _cloudMaskFrontIndex;
	private int _cloudMaskTargetIndex = 1;
	private bool _cloudMaskInitialized;
	private bool _cloudMaskBlending;
	private bool _cloudMaskReadyReported;
	private bool _sunshineBackdropReadyReported;
	private Texture2D? _sunshineBackdropTexture;
	private double _cloudMaskUpdateElapsed = double.PositiveInfinity;
	private double _cloudMaskBlendElapsed;
	private Vector2I _lastRootSize;
	private double _radiusMeters;
	private double _circumferenceMeters;
	private bool _captureDirectView;
	private bool _captureUnderside;
	private bool _captureUndersidePositioned;
	private int _captureFrame;
	private long _lastFallbackAroundCell = long.MinValue;
	private int _lastFallbackAcrossCell = int.MinValue;
	private ulong _lastFallbackEditVersion = ulong.MaxValue;
	private ulong _lastManualWeatherRevision = ulong.MaxValue;
	private float _cachedFallbackSurfaceHeight;
	private ulong _lastSeededSurfaceEditVersion = ulong.MaxValue;
	private long _lastFarRegionalAnchorAround = long.MinValue;
	private int _lastFarRegionalAnchorAcross = int.MinValue;
	private long _requestedFarRegionalAnchorAround = long.MinValue;
	private int _requestedFarRegionalAnchorAcross = int.MinValue;
	private ulong _lastFarRegionalEditVersion = ulong.MaxValue;
	private Task<FarRegionalBuildResult>? _farRegionalBuildTask;
	private FarRegionalBuildResult? _farRegionalUpload;
	private int _farRegionalUploadBand;
	private long _regionalRenderOriginAround = long.MinValue;
	private int _regionalRenderOriginAcross = int.MinValue;
	private Vector3 _regionalRingCenter;
	private Basis _lastValidFarCameraBasis = Basis.Identity;
	private Vector3 _lastValidFarCameraPosition;
	private bool _reportedInvalidFarCameraState;
	private bool _regionalTransitHidden;
	private double _regionalTransitResumeElapsed;
	private long _regionalTransitGeneration;
	private long _farRegionalLayoutVersion;
	private float _farAtmosphereShroudVisibility = 1.0f;
	private Vector3 _lastSharedMaterialCameraPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastSharedMaterialSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private float _lastSharedMaterialHalfWidth = float.NaN;
	private float _lastSharedMaterialShroudVisibility = float.NaN;
	private bool _sharedMaterialRingCenterSet;
	private Vector3 _lastRegionalMaterialCameraPosition = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastRegionalMaterialRingCenter = new(float.PositiveInfinity, 0.0f, 0.0f);
	private Vector3 _lastRegionalMaterialSunDirection = new(float.PositiveInfinity, 0.0f, 0.0f);
	private float _lastRegionalMaterialShroudVisibility = float.NaN;
	private readonly FarCloudClipmapState[] _lastFarCloudClipmapStates = new FarCloudClipmapState[3];
	private readonly bool[] _hasFarCloudClipmapState = new bool[3];
	// Published snapshots carry their own weather epoch; lightning advances
	// from that epoch using the authoritative saveable simulation clock.
	private float _lastTerrainFadeStart = float.NaN;
	private float _lastTerrainFadeEnd = float.NaN;
	private float _lastWaterFadeStart = float.NaN;
	private float _lastWaterFadeEnd = float.NaN;

	private readonly record struct FarCloudClipmapState(
		Transform3D Transform,
		Vector2 CanonicalOriginKm,
		float Coverage,
		float Storm,
		Vector3 SunDirection,
		Vector3 SurfaceUp,
		float ViewerHeight);

	private sealed class FarWeatherAtlasBuild
	{
		public readonly double SnapshotTime;
		public readonly int Width;
		public readonly int Height;
		public readonly Vector2[] Samples;
		public readonly Image Image;
		public int SampleIndex;
		public int TexelIndex;
		public ulong ContentHash = 1469598103934665603UL;

		public FarWeatherAtlasBuild(double snapshotTime, int width, int height)
		{
			SnapshotTime = snapshotTime;
			Width = width;
			Height = height;
			Samples = new Vector2[128 * 32];
			Image = Image.CreateEmpty(width, height, true, Image.Format.Rgba8);
		}
	}

	public float CurrentTerrainFallbackAltitudeMeters { get; private set; }
	public float CurrentTerrainFallbackCutoutMeters { get; private set; }
	public bool CloudMaskActive { get; private set; }
	public bool ExactSunshineBackdropActive { get; private set; }
	public bool FallbackCloudShellVisible => _scaledCloudShell?.Visible == true;
	public double FarWeatherAtlasSnapshotTimeSeconds => _farWeatherAtlasSnapshotTime;
	public bool FarWeatherAtlasRefreshInProgress => _farWeatherAtlasBuild != null;
	public ulong CloudMaskUpdateCount { get; private set; }
	public Vector2I CloudMaskSize { get; private set; }

	/// <summary>Compact Certified-capture evidence for the procedural far canopy.</summary>
	public string GetFarCloudClipmapDiagnostics()
	{
		int built = 0, visible = 0, triangles = 0;
		var layers = new List<string>();
		for (int index = 0; index < _farCloudClipmapLayers.Length; index++)
		{
			MeshInstance3D? layer = _farCloudClipmapLayers[index];
			if (layer == null) { layers.Add($"{index}:null"); continue; }
			built++;
			if (layer.Visible) visible++;
			int layerTriangles = layer.Mesh?.GetFaces().Length / 3 ?? 0;
			triangles += layerTriangles;
			Aabb aabb = layer.GetAabb();
			float cameraDistance = _farCamera == null
				? float.NaN
				: layer.GlobalPosition.DistanceTo(_farCamera.GlobalPosition);
			layers.Add($"{index}:visible={layer.Visible},triangles={layerTriangles},aabb={aabb},origin={layer.GlobalPosition},camera_distance={cameraDistance:0.###}");
		}
		return $"enabled={FarCloudClipmapEnabled},built={built},visible={visible},triangles={triangles},camera={_farCamera?.GlobalPosition},camera_forward={(_farCamera == null ? Vector3.Zero : -_farCamera.GlobalBasis.Z)},layers=[{string.Join(";", layers)}]";
	}

	public override void _Ready()
	{
		if (!Enabled)
		{
			SetProcess(false);
			return;
		}

		_grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
		_weatherSource = GetNodeOrNull<global::RingworldTechDemo.RingVolumetricCloudLayer>(WeatherSourcePath);
		_explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
		_sourceCamera = GetNodeOrNull<Camera3D>(SourceCameraPath);
		_sourceSun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
		_sourcePlanetLight = GetNodeOrNull<DirectionalLight3D>(PlanetLightPath);
		_localVolumetricClouds = GetNodeOrNull<global::RingworldTechDemo.RingLocalVolumetricCloudRenderer>(
			LocalVolumetricCloudRendererPath);
		_sunshineClouds = GetNodeOrNull<global::RingworldTechDemo.RingSunshineCloudBridge>(
			SunshineCloudBridgePath);
		_mainWorldEnvironment = GetNodeOrNull<WorldEnvironment>(MainEnvironmentPath);
		if (_grid?.TerrainSampler == null || _explorer == null || _sourceCamera == null || _mainWorldEnvironment?.Environment == null)
		{
			GD.PushError("RingScaledFarViewport could not resolve its grid, sampler, explorer, camera, or environment.");
			SetProcess(false);
			return;
		}

		_circumferenceMeters = _grid.GetCanonicalRingCircumferenceMeters64();
		_radiusMeters = _circumferenceMeters / Math.Tau;
		BeginFarWeatherAtlasRebuild(ResolveFarWeatherRawTimeSeconds());
		while (_farWeatherAtlasBuild != null) UpdateFarWeatherAtlas(int.MaxValue);
		string[] userArguments = OS.GetCmdlineUserArgs();
		_captureUnderside = Array.Exists(
			userArguments,
			argument => argument == "--capture-ring-underside");
		_captureDirectView = _captureUnderside || Array.Exists(
			userArguments,
			argument => argument == "--capture-scaled-far");
		DisableLegacyWholeRingMeshes();
		CreateFarViewport();
		CreateFarEnvironment();
		if (RegionalFarLodsEnabled)
			CreateRegionalFarViewport();
		CreateMaterials();
		BuildSeededSurface();
		if (RegionalFarLodsEnabled)
			BuildFarRegionalTerrain(force: true, delta: 0.0);
		BuildStructuralShell();
		BuildCloudShell();
		BuildFarCloudClipmap();
		CreateCloudTransmittanceMasks();
		CreateBackdrop();
		ConfigureMainBackground();
		SyncCameraAndLighting();
		_sunshineClouds?.SetFarOpaqueDepthTexture(GetOpaquePhysicalDepthTexture());
		UpdateViewportSize(force: true);
		UpdateCloudTransmittanceMask(0.0);
		GD.Print($"Scaled far viewport ready: scale=1:{1.0f / CoordinateScale:0}, radius={_radiusMeters * CoordinateScale:0.0}, cameraFar={_farCamera?.Far:0.0}, seed={_grid.TerrainSampler.Seed}, localAltitude={CurrentTerrainFallbackAltitudeMeters:0}m, localCutout={CurrentTerrainFallbackCutoutMeters / 1000.0f:0.0}km");
	}

	public override void _Process(double delta)
	{
		RefreshSeededSurfaceForEdits();
		if (_captureUnderside) AimSourceCameraAtRingUnderside();
		else if (_captureDirectView) AimSourceCameraAtOppositeRing();
		BuildFarRegionalTerrain(force: false, delta);
		UpdateFarWeatherAtlas();
		SyncCameraAndLighting();
		_sunshineClouds?.SetFarOpaqueDepthTexture(GetOpaquePhysicalDepthTexture());
		UpdateFarStormLightningMaterials();
		UpdateViewportSize(force: false);
		UpdateCloudTransmittanceMask(delta);
		if (_captureDirectView && ++_captureFrame == (_captureUnderside ? 30 : 8))
			CaptureDirectViewAndQuit();
	}

	private void RefreshSeededSurfaceForEdits()
	{
		if (_farViewport == null || _grid == null) return;
		ulong revision = _grid.LodSurfaceEditVersion;
		if (revision == _lastSeededSurfaceEditVersion) return;
		RemoveFarViewportChild("ScaledSeededRingSurface");
		RemoveFarViewportChild("ScaledWaterSurface");
		BuildSeededSurface();
	}

	public override void _ExitTree()
	{
		// Detach the compositor-owned far texture before its producer is disposed.
		if (_sunshineClouds != null && GodotObject.IsInstanceValid(_sunshineClouds))
			_sunshineClouds.SetFarOpaqueDepthTexture(null);
		_farWeatherAtlasBuild?.Image.Dispose();
		_farWeatherAtlasBuild = null;
		base._ExitTree();
	}

	private void ConfigureFarWeatherMaterial(ShaderMaterial material)
	{
		if (ReferenceEquals(material, _cloudMaterial))
		{
			float outer = GetEffectiveFarCloudClipmapRadiusMeters() * CoordinateScale;
			material.SetShaderParameter("far_clipmap_handoff_start_meters", outer * 0.78f);
			material.SetShaderParameter("far_clipmap_handoff_end_meters", outer);
		}
		if (_farWeatherAtlas == null) return;
		material.SetShaderParameter("weather_atlas", _farWeatherAtlas);
		material.SetShaderParameter("weather_atlas_enabled", true);
		// The texture is an authoritative snapshot. Advect only its age so a
		// lifecycle rebuild cannot make weather jump, while lightning remains
		// on the absolute authoritative clock below.
		material.SetShaderParameter("weather_time_seconds", ResolveFarWeatherAtlasAgeSeconds());
		Vector2 wind = _weatherSource?.WeatherWindMetersPerSecond ?? new Vector2(2.2f, 0.7f);
		material.SetShaderParameter("weather_wind_uv_per_second", new Vector2(
			wind.X / (float)Math.Max(1.0, _circumferenceMeters),
			wind.Y / Math.Max(1.0f, RingWidthMeters)));
		ConfigureFarStormLightningMaterial(material);
	}

	// RingVolumetricCloudLayer owns this saveable clock; the far tier consumes
	// frozen snapshots rather than generating its own weather timeline.
	private double ResolveFarWeatherRawTimeSeconds()
		=> Math.Max(0.0, _weatherSource?.CloudSimulationTimeSeconds ?? 0.0);

	private float ResolveFarWeatherAtlasAgeSeconds()
	{
		// Atlas age is intentionally bounded only for GPU float precision; the
		// snapshot itself and lightning phase stay on the raw saveable clock.
		double age = Math.Max(0.0, ResolveFarWeatherRawTimeSeconds() - _farWeatherAtlasSnapshotTime);
		return (float)Math.Min(age, 1_000_000.0);
	}

	private void ConfigureFarStormLightningMaterial(ShaderMaterial material)
	{
		SelectedFarLightningEventIds.Clear();
		Vector4 first = Vector4.Zero;
		Vector4 second = Vector4.Zero;
		float firstId = 0.0f;
		float secondId = 0.0f;
		float firstWeight = 0.0f;
		float secondWeight = 0.0f;
		double time = ResolveFarWeatherRawTimeSeconds();
		int limit = FarStormLightningEnabled ? Mathf.Clamp(FarStormLightningMaximumEvents, 1, 2) : 0;
		int written = 0;
		foreach (RingLightningEvent lightning in SelectVisibleFarLightningEvents(time, limit))
		{
			float pulse = RingLightningEvent.PulseAtAge(time - lightning.StartTimeSeconds);
			if (pulse <= 0.0001f) continue;
			float around = Mathf.PosMod((float)lightning.CloudPosition.AroundMeters, (float)Math.Max(1.0, _circumferenceMeters));
			float across = Mathf.Clamp((float)lightning.CloudPosition.AcrossMeters, 0.0f, RingWidthMeters);
			var encoded = new Vector4(
				around / (float)Math.Max(1.0, _circumferenceMeters),
				across / Math.Max(1.0f, RingWidthMeters),
				Mathf.Clamp(Mathf.Max(FarStormLightningRadiusMeters, lightning.FlashRadiusMeters) / (float)Math.Max(1.0, _circumferenceMeters), 0.0001f, 0.2f),
				pulse * lightning.Energy * Mathf.Max(0.0f, FarStormLightningIntensity));
			float weight = ResolveFarLightningVisualWeight(lightning, material);
			if (written++ == 0) { first = encoded; firstId = (float)(lightning.EventId & 0x00ffffff); firstWeight = weight; }
			else { second = encoded; secondId = (float)(lightning.EventId & 0x00ffffff); secondWeight = weight; }
		}
		material.SetShaderParameter("far_lightning_event_a", first);
		material.SetShaderParameter("far_lightning_event_b", second);
		material.SetShaderParameter("far_lightning_event_a_id", firstId);
		material.SetShaderParameter("far_lightning_event_b_id", secondId);
		material.SetShaderParameter("far_lightning_event_weights", new Vector2(firstWeight, secondWeight));
	}

	private float ResolveFarLightningVisualWeight(in RingLightningEvent lightning, ShaderMaterial material)
	{
		if (_explorer == null) return 0.0f;
		LightningDistanceHandoff weights = LightningDistanceHandoff.Evaluate(
			_explorer.CanonicalPosition,
			lightning.GroundPosition,
			_circumferenceMeters,
			GetEffectiveFarCloudClipmapRadiusMeters(),
			RingWidthMeters * 0.5f,
			FarCloudClipmapEnabled ? _farCloudClipmapMaterials.Length : 0);
		return ReferenceEquals(material, _cloudMaterial) ? weights.Shell : weights.Regional;
	}

	private IEnumerable<RingLightningEvent> SelectVisibleFarLightningEvents(double time, int limit)
	{
		if (limit <= 0 || _weatherSource == null || _grid == null || _sourceCamera == null) yield break;
		var candidates = new List<(RingLightningEvent Event, float Score)>();
		Vector3 cameraPosition = _sourceCamera.GlobalPosition;
		Vector2 viewportSize = _sourceCamera.GetViewport().GetVisibleRect().Size;
		float aspect = viewportSize.Y > 0.0f ? viewportSize.X / viewportSize.Y : 1.0f;
		float verticalTan = Mathf.Tan(Mathf.DegToRad(_sourceCamera.Fov * 0.5f));
		float horizontalTan = verticalTan * aspect;
		// Camera3D.Fov is vertical when Height is locked and horizontal when
		// Width is locked.  Expand the other axis from the actual viewport so
		// selection matches the visible rectangular frustum rather than a cone.
		if (_sourceCamera.KeepAspect == Camera3D.KeepAspectEnum.Width)
		{
			horizontalTan = verticalTan;
			verticalTan = horizontalTan / Mathf.Max(aspect, 0.001f);
		}
		Basis inverseCameraBasis = _sourceCamera.GlobalBasis.Orthonormalized().Inverse();
		float horizontalPlaneLength = Mathf.Sqrt(1.0f + horizontalTan * horizontalTan);
		float verticalPlaneLength = Mathf.Sqrt(1.0f + verticalTan * verticalTan);
		foreach (RingLightningEvent lightning in _weatherSource.ActiveLightningEvents)
		{
			float pulse = RingLightningEvent.PulseAtAge(time - lightning.StartTimeSeconds);
			if (pulse <= 0.0001f) continue;
			Vector3 world = _grid.CanonicalRingPositionToWorld(lightning.CloudPosition);
			Vector3 delta = world - cameraPosition;
			float distance = Mathf.Max(1.0f, delta.Length());
			float radius = Mathf.Max(FarStormLightningRadiusMeters, lightning.FlashRadiusMeters);
			float angular = Mathf.Atan(radius / distance);
			Vector3 view = inverseCameraBasis * delta;
			float depth = -view.Z;
			LightningDistanceHandoff weights = _explorer == null
				? default
				: LightningDistanceHandoff.Evaluate(
					_explorer.CanonicalPosition,
					lightning.GroundPosition,
					_circumferenceMeters,
					GetEffectiveFarCloudClipmapRadiusMeters(),
					RingWidthMeters * 0.5f,
					FarCloudClipmapEnabled ? _farCloudClipmapMaterials.Length : 0);
			if (weights.Regional + weights.Shell <= 0.0001f || depth + radius <= 0.0f) continue;
			if (Mathf.Abs(view.X) > depth * horizontalTan + radius * horizontalPlaneLength
				|| Mathf.Abs(view.Y) > depth * verticalTan + radius * verticalPlaneLength) continue;
			candidates.Add((lightning, angular * lightning.Energy));
		}
		candidates.Sort(static (left, right) => right.Score != left.Score
			? right.Score.CompareTo(left.Score) : left.Event.EventId.CompareTo(right.Event.EventId));
		SelectedFarLightningEventIds.Clear();
		for (int index = 0; index < Math.Min(limit, candidates.Count); index++)
		{
			SelectedFarLightningEventIds.Add(candidates[index].Event.EventId);
			yield return candidates[index].Event;
		}
	}

	private void UpdateFarStormLightningMaterials()
	{
		if (_cloudMaterial != null) ConfigureFarStormLightningMaterial(_cloudMaterial);
		foreach (ShaderMaterial? material in _farCloudClipmapMaterials)
			if (material != null) ConfigureFarStormLightningMaterial(material);
	}

	private void BeginFarWeatherAtlasRebuild(double snapshotTime)
	{
		if (_farWeatherAtlasBuild != null) return;
		int width = Mathf.Clamp(FarWeatherAtlasWidth, 512, 8192);
		int height = Mathf.Clamp(FarWeatherAtlasHeight, 32, 512);
		_farWeatherAtlasBuild = new FarWeatherAtlasBuild(snapshotTime, width, height);
	}

	private void UpdateFarWeatherAtlas(int? forcedBudget = null)
	{
		double time = ResolveFarWeatherRawTimeSeconds();
		bool manualWeatherChanged = _weatherSource != null && _weatherSource.ManualWeatherRevision != _lastManualWeatherRevision;
		if (_weatherSource != null) _lastManualWeatherRevision = _weatherSource.ManualWeatherRevision;
		bool discontinuity = !double.IsNaN(_lastFarWeatherRawTime)
			&& Math.Abs(time - _lastFarWeatherRawTime) > Math.Max(2.0, FarWeatherAtlasRefreshSeconds * 2.0);
		_lastFarWeatherRawTime = time;
		bool rebuildRequested = false;
		// A load/restore may jump either direction. Discard only unpublished
		// staging data; the previous atlas/systems remain live until replacement.
		if (_farWeatherAtlasBuild != null && (time < _farWeatherAtlasBuild.SnapshotTime || discontinuity || manualWeatherChanged))
		{
			_farWeatherAtlasBuild.Image.Dispose();
			_farWeatherAtlasBuild = null;
			rebuildRequested = true;
		}
		if (_farWeatherAtlasBuild == null && (rebuildRequested || manualWeatherChanged || time >= _farWeatherAtlasNextRefreshTime || time < _farWeatherAtlasSnapshotTime))
			BeginFarWeatherAtlasRebuild(time);
		FarWeatherAtlasBuild? build = _farWeatherAtlasBuild;
		if (build == null || _grid?.TerrainSampler == null) return;
		Stopwatch stopwatch = Stopwatch.StartNew();
		int completedItems = 0;
		int sampleCap = forcedBudget ?? Mathf.Clamp(FarWeatherAtlasWorkBudgetPerFrame, 16, 128);
		const int sampleWidth = 128, sampleHeight = 32;
		int terrainSeed = _grid.TerrainSampler.Seed;
		while (completedItems < sampleCap && build.SampleIndex < build.Samples.Length)
		{
			if (forcedBudget == null && stopwatch.Elapsed.TotalMilliseconds >= 1.5) break;
			int index = build.SampleIndex++;
			int sx = index % sampleWidth;
			int sy = index / sampleWidth;
			double around = sx / (double)sampleWidth * _circumferenceMeters;
			double across = sy / (double)(sampleHeight - 1) * RingWidthMeters;
			global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState weather = _weatherSource?.QueryLocalWeatherAtTime(
				new RingCanonicalPosition(around, 0.0, across), build.SnapshotTime) ?? default;
			float storm = Mathf.Clamp(weather.SystemInfluence * 0.65f + weather.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
			build.Samples[index] = new Vector2(weather.CloudCoverage, storm);
			completedItems++;
		}
		uint seed = unchecked((uint)terrainSeed) ^ 0x9e3779b9u;
		while (completedItems < 8192 && build.SampleIndex >= build.Samples.Length && build.TexelIndex < build.Width * build.Height)
		{
			if (forcedBudget == null && stopwatch.Elapsed.TotalMilliseconds >= 1.5) break;
			int index = build.TexelIndex++;
			int x = index % build.Width;
			int y = index / build.Width;
			float u = x / (float)build.Width;
			float v = y / (float)Math.Max(1, build.Height - 1);
			Vector2 weather = SampleWeatherGrid(build.Samples, sampleWidth, sampleHeight, u, v);
			float coverage = Mathf.Clamp(weather.X, 0.0f, 1.0f);
			float storm = Mathf.Clamp(weather.Y, 0.0f, 1.0f);
			build.Image.SetPixel(x, y, new Color(coverage, storm,
				SeedUnit(seed ^ 0x27d4eb2du, x / 7, y / 5), SeedUnit(seed ^ 0x165667b1u, x / 3, y / 3)));
			build.ContentHash = (build.ContentHash ^ (uint)BitConverter.SingleToInt32Bits(coverage)) * 1099511628211UL;
			build.ContentHash = (build.ContentHash ^ (uint)BitConverter.SingleToInt32Bits(storm)) * 1099511628211UL;
			completedItems++;
		}
		FarWeatherAtlasLastWorkMilliseconds = stopwatch.Elapsed.TotalMilliseconds;
		FarWeatherAtlasLastWorkItems = completedItems;
		if (build.TexelIndex < build.Width * build.Height) return;
		Stopwatch uploadStopwatch = Stopwatch.StartNew();
		build.Image.GenerateMipmaps();
		if (_farWeatherAtlas is ImageTexture reusable && reusable.GetSize() == new Vector2(build.Width, build.Height)) reusable.Update(build.Image);
		else _farWeatherAtlas = ImageTexture.CreateFromImage(build.Image);
		_farWeatherAtlasSnapshotTime = build.SnapshotTime;
		_farWeatherAtlasNextRefreshTime = build.SnapshotTime + Mathf.Max(1.0f, FarWeatherAtlasRefreshSeconds);
		_farWeatherAtlasBuild = null;
		build.Image.Dispose();
		FarWeatherAtlasLastUploadMilliseconds = uploadStopwatch.Elapsed.TotalMilliseconds;
		FarWeatherAtlasPublicationCount++;
		FarWeatherAtlasContentHash = build.ContentHash;
		PublishFarWeatherMaterials();
	}

	private void PublishFarWeatherMaterials()
	{
		if (_cloudMaterial != null) ConfigureFarWeatherMaterial(_cloudMaterial);
		foreach (ShaderMaterial? material in _farCloudClipmapMaterials)
			if (material != null) ConfigureFarWeatherMaterial(material);
	}

	private static Vector2 SampleWeatherGrid(Vector2[] values, int width, int height, float u, float v)
	{
		float x = u * width;
		int x0 = Mathf.PosMod(Mathf.FloorToInt(x), width);
		int x1 = (x0 + 1) % width;
		float y = Mathf.Clamp(v, 0.0f, 1.0f) * (height - 1);
		int y0 = Mathf.FloorToInt(y);
		int y1 = Mathf.Min(y0 + 1, height - 1);
		return values[y0 * width + x0].Lerp(values[y0 * width + x1], x - Mathf.Floor(x)).Lerp(
			values[y1 * width + x0].Lerp(values[y1 * width + x1], x - Mathf.Floor(x)), y - y0);
	}

	private static float SeedUnit(uint seed, int x, int y)
	{
		uint h = seed + 0x9e3779b9u;
		h ^= unchecked((uint)x) * 0x85ebca6bu;
		h ^= unchecked((uint)y) * 0xc2b2ae35u;
		h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
		return (h & 0x00ffffffu) / 16777215.0f;
	}

	private void AimSourceCameraAtOppositeRing()
	{
		if (_grid == null || _explorer == null || _sourceCamera == null) return;
		RingCanonicalPosition canonical = _explorer.CanonicalPosition;
		Vector3 target = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
			canonical.AroundMeters + _circumferenceMeters * 0.5,
			0.0,
			canonical.AcrossMeters));
		_sourceCamera.LookAt(target, Vector3.Up);
	}

	private void AimSourceCameraAtRingUnderside()
	{
		if (_grid == null || _explorer == null || _sourceCamera == null) return;
		double bottom = _grid.GetStructuralBottomGlobalY()
			* (double)VoxelConstants.VoxelSizeMeters;
		if (!_captureUndersidePositioned)
		{
			RingCanonicalPosition current = _explorer.CanonicalPosition;
			_explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(
				current.AroundMeters,
				bottom - 120000.0,
				RingWidthMeters * 0.5));
			_captureUndersidePositioned = true;
		}

		RingCanonicalPosition observer = _explorer.CanonicalPosition;
		Vector3 target = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
			observer.AroundMeters,
			bottom,
			RingWidthMeters * 0.5));
		_sourceCamera.LookAt(target, Vector3.Up);
	}

	private void CaptureDirectViewAndQuit()
	{
		string rootFile = _captureUnderside
			? "ring_underside_root.png"
			: "scaled_far_direct_root.png";
		string farFile = _captureUnderside
			? "ring_underside_far_viewport.png"
			: "scaled_far_direct_viewport.png";
		string rootPath = RingGeneratedOutputPaths.File("captures/scaled_far_viewport", rootFile);
		string farPath = RingGeneratedOutputPaths.File("captures/scaled_far_viewport", farFile);
		Error rootError = GetViewport().GetTexture().GetImage().SavePng(rootPath);
		Error farError = _farViewport?.GetTexture().GetImage().SavePng(farPath) ?? Error.Unconfigured;
		string mode = _captureUnderside ? "underside" : "opposite-ring";
		GD.Print($"Scaled far direct-view capture: mode={mode}, root={rootError}:{rootPath}, far={farError}:{farPath}");
		GetTree().Quit(rootError == Error.Ok && farError == Error.Ok ? 0 : 1);
	}

	private void DisableLegacyWholeRingMeshes()
	{
		if (GetNodeOrNull<Node3D>(LegacyAnalyticPath) is { } analytic) analytic.Visible = false;
		if (GetNodeOrNull<Node3D>(LegacyCloudPath) is { } cloud) cloud.Visible = false;
	}

	private void CreateFarViewport()
	{
		_farViewport = new SubViewport
		{
			Name = "FarRingViewport",
			OwnWorld3D = true,
			TransparentBg = false,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			HandleInputLocally = false,
			Msaa3D = Viewport.Msaa.Disabled,
			ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
			UseTaa = false,
			UseDebanding = true
		};
		AddChild(_farViewport);

		_farCamera = new Camera3D
		{
			Name = "FarRingCamera",
			Current = true,
			Near = 0.1f,
			Far = Mathf.Max(
				100000.0f,
				(float)((2.0 * _radiusMeters + RingWidthMeters + StructuralDepthMeters + StructuralWallTopMeters + 50000.0) * CoordinateScale)),
			Fov = _sourceCamera!.Fov,
			KeepAspect = _sourceCamera.KeepAspect
		};
		_farViewport.AddChild(_farCamera);
	}

	private void CreateFarEnvironment()
	{
		if (_farViewport == null) return;
		var fallbackSkyMaterial = new ProceduralSkyMaterial
		{
			SkyTopColor = new Color(0.02f, 0.03f, 0.06f),
			SkyHorizonColor = new Color(0.01f, 0.02f, 0.03f),
			GroundBottomColor = new Color(0.01f, 0.01f, 0.02f),
			GroundHorizonColor = new Color(0.01f, 0.01f, 0.02f)
		};
		Sky sky = _mainWorldEnvironment?.Environment?.Sky
			?? new Sky { SkyMaterial = fallbackSkyMaterial };
		Godot.Environment? sourceEnvironment = _mainWorldEnvironment?.Environment;
		var environment = new Godot.Environment
		{
			BackgroundMode = Godot.Environment.BGMode.Sky,
			Sky = sky,
			AmbientLightSource = sourceEnvironment?.AmbientLightSource ?? Godot.Environment.AmbientSource.Color,
			AmbientLightColor = sourceEnvironment?.AmbientLightColor ?? new Color(0.53f, 0.58f, 0.65f),
			AmbientLightEnergy = sourceEnvironment?.AmbientLightEnergy ?? 0.4f,
			ReflectedLightSource = sourceEnvironment?.ReflectedLightSource ?? Godot.Environment.ReflectionSource.Sky,
			FogEnabled = false
		};
		// The far ring is composited behind the main viewport, so both views
		// must use the same HDR output curve and ambient split or the handoff
		// appears as a brightness seam.
		if (sourceEnvironment != null)
		{
			environment.Set(
				"ambient_light_sky_contribution",
				sourceEnvironment.Get("ambient_light_sky_contribution"));
			environment.Set("tonemap_mode", sourceEnvironment.Get("tonemap_mode"));
			environment.Set("tonemap_agx_white", sourceEnvironment.Get("tonemap_agx_white"));
			environment.Set("tonemap_agx_contrast", sourceEnvironment.Get("tonemap_agx_contrast"));
			environment.Set("tonemap_exposure", sourceEnvironment.Get("tonemap_exposure"));
		}
		environment.VolumetricFogEnabled = false;
		environment.VolumetricFogDensity = 0.0f;
		environment.VolumetricFogLength = 24.0f;
		environment.VolumetricFogDetailSpread = 2.0f;
		environment.VolumetricFogAmbientInject = 0.35f;
		environment.VolumetricFogSkyAffect = 1.0f;
		environment.VolumetricFogAnisotropy = 0.55f;
		environment.VolumetricFogTemporalReprojectionEnabled = false;
		_farEnvironment = environment;
		var farWorldEnvironment = new WorldEnvironment { Name = "FarWorldEnvironment", Environment = environment };
		_farViewport.AddChild(farWorldEnvironment);
		InstallFarDepthCapture(farWorldEnvironment);

		_farSun = new DirectionalLight3D
		{
			Name = "FarRingSun",
			LightEnergy = _sourceSun?.LightEnergy ?? 3.0f,
			ShadowEnabled = false
		};
		_farViewport.AddChild(_farSun);

		_farPlanetLight = new DirectionalLight3D
		{
			Name = "FarRingPlanetLight",
			LightColor = _sourcePlanetLight?.LightColor ?? new Color(0.62f, 0.72f, 0.90f),
			LightEnergy = _sourcePlanetLight?.LightEnergy ?? 0.0f,
			LightSpecular = _sourcePlanetLight?.LightSpecular ?? 0.45f,
			ShadowEnabled = false
		};
		_farViewport.AddChild(_farPlanetLight);
	}

	private void InstallFarDepthCapture(WorldEnvironment worldEnvironment)
	{
		GDScript? script = GD.Load<GDScript>("res://scripts/render/RingFarDepthCapture.gd");
		_farDepthCapture = script?.New().AsGodotObject() as CompositorEffect;
		if (_farDepthCapture == null)
		{
			GD.PushWarning("Scaled far depth capture unavailable; Sunshine will use main depth only.");
			return;
		}
		_farDepthCapture.Set("coordinate_scale", CoordinateScale);
		worldEnvironment.Compositor ??= new Compositor();
		worldEnvironment.Compositor.CompositorEffects = new Godot.Collections.Array<CompositorEffect> { _farDepthCapture };
	}

	public Texture2D? GetOpaquePhysicalDepthTexture()
		=> _farDepthCapture?.Call("get_depth_texture").AsGodotObject() as Texture2D;

	private void CreateRegionalFarViewport()
	{
		if (_sourceCamera == null) return;

		_regionalFarViewport = new SubViewport
		{
			Name = "FarRingRegionalViewport",
			OwnWorld3D = true,
			// This texture is layered over the whole-ring viewport. Pixels
			// without regional terrain must preserve the coarse backdrop.
			TransparentBg = true,
			RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
			HandleInputLocally = false,
			Msaa3D = Viewport.Msaa.Disabled,
			ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
			UseTaa = false,
			UseDebanding = true
		};
		AddChild(_regionalFarViewport);

		_regionalFarCamera = new Camera3D
		{
			Name = "FarRingRegionalCamera",
			Current = true,
			Near = 0.00001f,
			// LOD11 reaches 8 Mm, which is 0.8 units in Mega scaled space.
			// Keeping this camera close to the origin avoids the precision and
			// clip-range failure mode of the absolute whole-ring camera.
			Far = 1.5f,
			Fov = _sourceCamera.Fov,
			KeepAspect = _sourceCamera.KeepAspect
		};
		_regionalFarViewport.AddChild(_regionalFarCamera);

		Godot.Environment? sourceEnvironment = _mainWorldEnvironment?.Environment;
		var environment = new Godot.Environment
		{
			// TransparentBg only preserves alpha when the world itself does
			// not paint a sky behind the regional terrain.
			BackgroundMode = Godot.Environment.BGMode.Color,
			BackgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.0f),
			AmbientLightSource = sourceEnvironment?.AmbientLightSource ?? Godot.Environment.AmbientSource.Color,
			AmbientLightColor = sourceEnvironment?.AmbientLightColor ?? new Color(0.53f, 0.58f, 0.65f),
			AmbientLightEnergy = sourceEnvironment?.AmbientLightEnergy ?? 0.4f,
			ReflectedLightSource = sourceEnvironment?.ReflectedLightSource ?? Godot.Environment.ReflectionSource.Disabled,
			FogEnabled = false,
			VolumetricFogEnabled = false
		};
		if (sourceEnvironment != null)
		{
			environment.Set("ambient_light_sky_contribution", sourceEnvironment.Get("ambient_light_sky_contribution"));
			environment.Set("tonemap_mode", sourceEnvironment.Get("tonemap_mode"));
			environment.Set("tonemap_agx_white", sourceEnvironment.Get("tonemap_agx_white"));
			environment.Set("tonemap_agx_contrast", sourceEnvironment.Get("tonemap_agx_contrast"));
			environment.Set("tonemap_exposure", sourceEnvironment.Get("tonemap_exposure"));
		}
		_regionalFarViewport.AddChild(new WorldEnvironment
		{
			Name = "FarRingRegionalWorldEnvironment",
			Environment = environment
		});

		_regionalFarSun = new DirectionalLight3D
		{
			Name = "FarRingRegionalSun",
			LightEnergy = _sourceSun?.LightEnergy ?? 3.0f,
			ShadowEnabled = false
		};
		_regionalFarViewport.AddChild(_regionalFarSun);

		_regionalFarPlanetLight = new DirectionalLight3D
		{
			Name = "FarRingRegionalPlanetLight",
			LightColor = _sourcePlanetLight?.LightColor ?? new Color(0.62f, 0.72f, 0.90f),
			LightEnergy = _sourcePlanetLight?.LightEnergy ?? 0.0f,
			LightSpecular = _sourcePlanetLight?.LightSpecular ?? 0.45f,
			ShadowEnabled = false
		};
		_regionalFarViewport.AddChild(_regionalFarPlanetLight);
	}

	private void CreateMaterials()
	{
		// The whole-ring tier uses vertex colours, not the proxy tier's
		// triplanar textures. Cache their representative mip colours before
		// the regional worker meshes consume the palette.
		RingTerrainVisuals.PreloadTexturePalette();
		Shader? terrainShader = GD.Load<Shader>("res://materials/ring_terrain_global.gdshader");
		if (terrainShader != null)
		{
			_terrainMaterial = new ShaderMaterial { Shader = terrainShader };
			_terrainMaterial.SetShaderParameter("far_space_enabled", false);
			_terrainMaterial.SetShaderParameter("far_fade_enabled", false);
			// Begin with the safe ground cutout. SyncCameraAndLighting replaces
			// it immediately with the altitude-dependent radius.
			_terrainMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters * CoordinateScale);
			_terrainMaterial.SetShaderParameter("fade_end_meters", FadeEndMeters * CoordinateScale);
			RingAtmosphereModel.ConfigureMaterial(_terrainMaterial, _radiusMeters, CoordinateScale);
			_terrainMaterial.SetShaderParameter("ring_atmosphere_far_shroud_enabled", true);
			_terrainMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters * CoordinateScale);
			_terrainMaterial.SetShaderParameter("night_brightness", TerrainNightBrightness);
			_terrainMaterial.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
			global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(
				_terrainMaterial,
				CoordinateScale);
			global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
				_terrainMaterial,
				_circumferenceMeters,
				RingWidthMeters,
				_grid?.TerrainSampler?.Seed ?? 1337,
				worldUnitsToKilometres: 1.0f,
				fadeStartWorldUnits: 0.0f,
				fadeEndWorldUnits: 1.0f);
		}

		Shader? waterShader = GD.Load<Shader>("res://materials/ring_ocean_global.gdshader");
		if (waterShader != null)
		{
			_waterMaterial = new ShaderMaterial { Shader = waterShader };
			_waterMaterial.SetShaderParameter("far_fade_enabled", false);
			_waterMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters * CoordinateScale);
			_waterMaterial.SetShaderParameter("fade_end_meters", FadeEndMeters * CoordinateScale);
			_waterMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters * CoordinateScale);
			_waterMaterial.SetShaderParameter("night_brightness", TerrainNightBrightness);
			_waterMaterial.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
			RingWaterModel.ConfigureMaterial(_waterMaterial, CoordinateScale);
			_waterMaterial.SetShaderParameter("wave_geometry_strength", 0.0f);
			RingAtmosphereModel.ConfigureMaterial(_waterMaterial, _radiusMeters, CoordinateScale);
			_waterMaterial.SetShaderParameter("ring_atmosphere_far_shroud_enabled", true);
			global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(_waterMaterial, CoordinateScale);
			global::RingworldTechDemo.RingCloudShadowProjector.ConfigureAnalyticField(
				_waterMaterial,
				_circumferenceMeters,
				RingWidthMeters,
				_grid?.TerrainSampler?.Seed ?? 1337,
				worldUnitsToKilometres: 1.0f,
				fadeStartWorldUnits: 0.0f,
				fadeEndWorldUnits: 1.0f);
		}

		Shader? hullShader = GD.Load<Shader>("res://materials/ring_hull_global.gdshader");
		if (hullShader != null)
		{
			_hullMaterial = new ShaderMaterial { Shader = hullShader };
			_hullMaterial.SetShaderParameter("far_space_enabled", false);
			_hullMaterial.SetShaderParameter("fade_start_meters", FadeStartMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter("fade_end_meters", FadeEndMeters * CoordinateScale);
			RingAtmosphereModel.ConfigureMaterial(_hullMaterial, _radiusMeters, CoordinateScale, 1.35f);
			_hullMaterial.SetShaderParameter("ring_atmosphere_far_shroud_enabled", true);
			_hullMaterial.SetShaderParameter("ring_width_meters", RingWidthMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter("night_brightness", HullNightBrightness);
			_hullMaterial.SetShaderParameter("night_tint", new Vector3(NightTint.R, NightTint.G, NightTint.B));
			_hullMaterial.SetShaderParameter("structural_wall_top_meters", StructuralWallTopMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter("structural_scale_cues_enabled", StructuralScaleCuesEnabled);
			_hullMaterial.SetShaderParameter(
				"structural_longitudinal_spacing_meters",
				StructuralLongitudinalSpacingMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter(
				"structural_longitudinal_half_width_meters",
				StructuralLongitudinalHalfWidthMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter(
				"structural_cross_spacing_meters",
				StructuralCrossSpacingMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter(
				"structural_cross_half_width_meters",
				StructuralCrossHalfWidthMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter(
				"structural_top_rail_half_width_meters",
				StructuralTopRailHalfWidthMeters * CoordinateScale);
			_hullMaterial.SetShaderParameter(
				"structural_cue_color",
				new Vector3(StructuralCueColor.R, StructuralCueColor.G, StructuralCueColor.B));
			_hullMaterial.SetShaderParameter("structural_cue_day_energy", StructuralCueDayEnergy);
			_hullMaterial.SetShaderParameter("structural_cue_night_energy", StructuralCueNightEnergy);
			// The far backdrop normally cuts a large local hole for gameplay
			// terrain.  During fast noclip that hole must not remove the
			// cavity roof/floor after local proxy tiers are hidden.
			_cavityShellMaterial = (ShaderMaterial)_hullMaterial.Duplicate();
			_cavityShellMaterial.SetShaderParameter("near_fade_enabled", false);
			_cavityShellMaterial.SetShaderParameter("near_space_enabled", true);
			_cavityShellMaterial.SetShaderParameter(
				"near_space_render_distance",
				Mathf.Max(0.2f, MinimumWholeRingNearClip * 2.0f));
			_cavityShellMaterial.SetShaderParameter("far_fade_enabled", false);
		}

		Shader? cloudShader = GD.Load<Shader>("res://materials/ring_cloud_layer.gdshader");
		if (cloudShader != null)
		{
			_cloudMaterial = new ShaderMaterial { Shader = cloudShader, RenderPriority = 10 };
			_cloudMaterial.SetShaderParameter("far_space_enabled", false);
			_cloudMaterial.SetShaderParameter("coverage", FarCloudCoverage);
			_cloudMaterial.SetShaderParameter("opacity", FarCloudOpacity);
			_cloudMaterial.SetShaderParameter("softness", FarCloudSoftness);
			_cloudMaterial.SetShaderParameter("local_volumetric_handoff_enabled", true);
			_cloudMaterial.SetShaderParameter("local_handoff_start_meters", LocalCloudRadiusMeters * 0.76f * CoordinateScale);
			_cloudMaterial.SetShaderParameter("local_handoff_end_meters", LocalCloudRadiusMeters * CoordinateScale);
			_cloudMaterial.SetShaderParameter("ring_circumference_km", (float)(_circumferenceMeters * 0.001));
			_cloudMaterial.SetShaderParameter("ring_width_km", RingWidthMeters * 0.001f);
			_cloudMaterial.SetShaderParameter("cloud_seed", _grid?.TerrainSampler?.Seed ?? 1337);
			ConfigureFarWeatherMaterial(_cloudMaterial);
			RingAtmosphereModel.ConfigureMaterial(_cloudMaterial, _radiusMeters, CoordinateScale);
			_cloudMaterial.SetShaderParameter("ring_atmosphere_far_shroud_enabled", true);
		}

		Shader? canopyShader = GD.Load<Shader>("res://materials/ring_cloud_canopy.gdshader");
		if (canopyShader != null)
		{
			_localCloudCanopyMaterial = new ShaderMaterial { Shader = canopyShader, RenderPriority = 11 };
			_localCloudCanopyMaterial.SetShaderParameter("patch_size_km", new Vector2(
				LocalCloudRadiusMeters * 0.002f,
				LocalCloudRadiusMeters * 0.002f));
			_localCloudCanopyMaterial.SetShaderParameter("cloud_base_meters", LocalCloudBaseMeters);
			_localCloudCanopyMaterial.SetShaderParameter("cloud_top_meters", LocalCloudTopMeters);
			_localCloudCanopyMaterial.SetShaderParameter("handoff_start_fraction", 0.76f);
			_localCloudCanopyMaterial.SetShaderParameter("handoff_end_fraction", 1.0f);
		}

		Shader? volumeShader = GD.Load<Shader>("res://materials/ring_volumetric_cloud.gdshader");
		if (volumeShader != null)
		{
			_scaledCloudVolumeMaterial = new ShaderMaterial { Shader = volumeShader };
			_scaledCloudVolumeMaterial.SetShaderParameter("coverage", 0.52f);
			_scaledCloudVolumeMaterial.SetShaderParameter("softness", 0.085f);
			// Godot integrates FogVolume density in froxel space; applying a
			// physical 1/scale multiplier here over-occludes the entire view.
			// Keep the authored density and scale only the noise coordinates.
			_scaledCloudVolumeMaterial.SetShaderParameter("density_scale", 0.065f);
			_scaledCloudVolumeMaterial.SetShaderParameter("quality_level", 1);
			_scaledCloudVolumeMaterial.SetShaderParameter("world_units_to_km", 0.001f / Mathf.Max(0.0001f, CoordinateScale));
		}
	}

	private void BuildSeededSurface()
	{
		if (_farViewport == null || _grid?.TerrainSampler is not RingTerrainSampler sampler) return;
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		int aroundCount = Mathf.Max(128, AroundSegments);
		int acrossCount = Mathf.Max(4, AcrossSegments);
		int row = aroundCount + 1;
		double aroundStep = _circumferenceMeters / aroundCount;
		double acrossStep = RingWidthMeters / (double)acrossCount;
		var terrainVertices = new Vector3[(acrossCount + 1) * row];
		var terrainColors = new Color[terrainVertices.Length];
		var waterVertices = new Vector3[terrainVertices.Length];
		var waterColors = new Color[terrainVertices.Length];
		var displayedWater = new bool[terrainVertices.Length];
		for (int z = 0; z <= acrossCount; z++)
		for (int x = 0; x <= aroundCount; x++)
		{
			double around = x * aroundStep;
			double across = z * acrossStep;
			long sampleAround = (long)Math.Round(around);
			int sampleAcross = (int)Math.Round(across);
			bool usesPresentationSurface = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration;
			TerrainSample sample = usesPresentationSurface
				? edits.GetPresentationTerrainSample(sampler, sampleAround, sampleAcross)
				: sampler.GetTerrainSample(sampleAround, sampleAcross);
			int index = z * row + x;
			float terrainHeight = usesPresentationSurface
				? edits.GetPresentationSurfaceHeight(sampler, sampleAround, sampleAcross)
				: sample.SurfaceHeight;
			float waterHeight = sample.ResolveDisplayedHeight(terrainHeight);
			terrainVertices[index] = ScaledPoint(around, terrainHeight - 150.0, across);
			terrainColors[index] = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(terrainHeight), sampler, LowTerrainColor, HighTerrainColor);
			waterVertices[index] = ScaledPoint(around, waterHeight - 150.0, across);
			waterColors[index] = RingWaterModel.EncodeWaterKind(
				RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(waterHeight), sampler, LowTerrainColor, HighTerrainColor),
				sample.WaterKind);
			displayedWater[index] = sample.HasSurfaceWater && terrainHeight < sample.WaterSurfaceHeight;
		}
		_lastSeededSurfaceEditVersion = edits.Version;

		var terrainSurface = new SurfaceTool();
		var waterSurface = new SurfaceTool();
		terrainSurface.Begin(Mesh.PrimitiveType.Triangles);
		waterSurface.Begin(Mesh.PrimitiveType.Triangles);
		int waterTriangleCount = 0;
		for (int z = 0; z < acrossCount; z++)
		for (int x = 0; x < aroundCount; x++)
		{
			int i00 = z * row + x, i10 = i00 + 1, i01 = i00 + row, i11 = i01 + 1;
			AddTriangle(terrainSurface, terrainVertices[i01], terrainVertices[i10], terrainVertices[i00], terrainColors[i01], terrainColors[i10], terrainColors[i00]);
			AddTriangle(terrainSurface, terrainVertices[i10], terrainVertices[i01], terrainVertices[i11], terrainColors[i10], terrainColors[i01], terrainColors[i11]);
			if (IsWaterTriangle(displayedWater, i01, i10, i00))
			{
				AddTriangle(waterSurface, waterVertices[i01], waterVertices[i10], waterVertices[i00], waterColors[i01], waterColors[i10], waterColors[i00]);
				waterTriangleCount++;
			}
			if (IsWaterTriangle(displayedWater, i10, i01, i11))
			{
				AddTriangle(waterSurface, waterVertices[i10], waterVertices[i01], waterVertices[i11], waterColors[i10], waterColors[i01], waterColors[i11]);
				waterTriangleCount++;
			}
		}
		terrainSurface.GenerateNormals();
		terrainSurface.Index();
		var mesh = new MeshInstance3D
		{
			Name = "ScaledSeededRingSurface",
			Mesh = terrainSurface.Commit(),
			MaterialOverride = _terrainMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			IgnoreOcclusionCulling = true
		};
		_farViewport.AddChild(mesh);
		DisableWholeRingFrustumCulling(mesh);
		if (waterTriangleCount <= 0) return;

		waterSurface.GenerateNormals();
		waterSurface.Index();
		var waterMesh = new MeshInstance3D
		{
			Name = "ScaledWaterSurface",
			Mesh = waterSurface.Commit(),
			MaterialOverride = _waterMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			IgnoreOcclusionCulling = true
		};
		_farViewport.AddChild(waterMesh);
		DisableWholeRingFrustumCulling(waterMesh);
	}

	// The regional far bands are drawn in scaled space so the primary Forward+ camera never
	// needs their multi-million-metre clip range. A polar grid keeps the mesh
	// bounded (and inexpensive) while following the player through canonical
	// space; the complete ring remains the backdrop beyond this regional band.
	private void BuildFarRegionalTerrain(bool force, double delta)
	{
		if (!RegionalFarLodsEnabled
			|| _regionalFarViewport == null
			|| _explorer == null
			|| _grid?.TerrainSampler is not RingTerrainSampler sampler
			|| _terrainMaterial == null)
			return;

		if (UpdateRegionalTransitState(delta))
			return;

		CompleteFarRegionalBuild();
		if (_farRegionalUpload != null && _farRegionalUpload.EditVersion != _grid.LodSurfaceEditVersion)
		{
			_farRegionalUpload = null;
			_farRegionalUploadBand = 0;
			DiscardFarRegionalStaging();
		}
		if (_farRegionalUpload != null)
		{
			// One ArrayMesh upload per frame. Candidate meshes are invisible and use
			// their own anchor until the complete replacement can be published.
			if (_farRegionalUploadBand < _farRegionalUpload.Bands.Count)
			{
				ApplyFarRegionalBand(
						_farRegionalStagingTerrains,
						_farRegionalUpload.Bands[_farRegionalUploadBand],
						_farRegionalUpload.AnchorAround,
						_farRegionalUpload.AnchorAcross,
						visible: false);
				_farRegionalUploadBand++;
				return;
			}

			PublishFarRegionalStaging(_farRegionalUpload);
			return;
		}

		RingCanonicalPosition canonical = _explorer.CanonicalPosition;
		const double ReanchorDistanceMeters = 500000.0;
		// Select the nearest periodic image before snapping. Snapping the raw
		// [0, circumference) coordinate can choose different 500 km cells on
		// either side of the seam when the circumference is not a multiple of
		// the reanchor distance, forcing a redundant regional-band rebuild.
		double signedAround = canonical.AroundMeters;
		if (signedAround > _circumferenceMeters * 0.5)
			signedAround -= _circumferenceMeters;
		long anchorAround = (long)Math.Round(signedAround / ReanchorDistanceMeters)
			* (long)ReanchorDistanceMeters;
		anchorAround = RingCoordMath.WrapAround(
			anchorAround,
			Math.Max(1L, (long)Math.Round(_circumferenceMeters)));
		int anchorAcross = Mathf.RoundToInt((float)(canonical.AcrossMeters / ReanchorDistanceMeters)) * (int)ReanchorDistanceMeters;
		anchorAcross = Mathf.Clamp(anchorAcross, 0, Mathf.Max(1, RingWidthMeters));
		if (_farRegionalBuildTask != null)
			return;
		ulong editVersion = _grid.LodSurfaceEditVersion;
		if (!force
			&& anchorAround == _lastFarRegionalAnchorAround
			&& anchorAcross == _lastFarRegionalAnchorAcross
			&& editVersion == _lastFarRegionalEditVersion)
			return;
		if (!force
			&& anchorAround == _requestedFarRegionalAnchorAround
			&& anchorAcross == _requestedFarRegionalAnchorAcross)
			return;

		_requestedFarRegionalAnchorAround = anchorAround;
		_requestedFarRegionalAnchorAcross = anchorAcross;
		RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
		RingTerrainSampler workerSampler = sampler.CreateIndependentCopy();
		double circumference = _circumferenceMeters;
		double radius = _radiusMeters;
		int width = Mathf.Max(1, RingWidthMeters);
		float scale = CoordinateScale;
		Color low = LowTerrainColor;
		Color high = HighTerrainColor;
		long transitGeneration = _regionalTransitGeneration;
		long layoutVersion = _farRegionalLayoutVersion;
		// Capture the layout on the main thread. The worker only receives immutable
		// value data and an independent terrain sampler.
		FarRegionalBandSpec[] bandSpecs = GetFarRegionalBandSpecs(IntermediateRegionalLodTiersEnabled).ToArray();
		_farRegionalBuildTask = Task.Run(() =>
		{
			try
			{
				var bands = new List<FarRegionalBandData>();
				foreach (FarRegionalBandSpec spec in bandSpecs)
					bands.Add(BuildFarRegionalBandData(
						spec.Name, workerSampler, anchorAround, anchorAcross,
						spec.InnerRadius, spec.OuterRadius, spec.SampleSpacing,
						circumference, radius, width, scale, low, high, edits));
				WeldFarRegionalBandNormals(bands);
				return new FarRegionalBuildResult(anchorAround, anchorAcross, transitGeneration, edits.Version, layoutVersion, bands);
			}
			finally
			{
				workerSampler.Dispose();
			}
		});
	}

	private void CompleteFarRegionalBuild()
	{
		if (_farRegionalBuildTask == null || !_farRegionalBuildTask.IsCompleted) return;
		Task<FarRegionalBuildResult> completed = _farRegionalBuildTask;
		_farRegionalBuildTask = null;
		if (completed.IsFaulted)
		{
			GD.PushError($"Scaled far regional LOD build failed: {completed.Exception?.GetBaseException().Message}");
			return;
		}
		if (completed.IsCanceled) return;
		if (completed.Result.TransitGeneration != _regionalTransitGeneration
				|| completed.Result.LayoutVersion != _farRegionalLayoutVersion
			|| _grid?.LodSurfaceEditVersion != completed.Result.EditVersion)
			return;
		_farRegionalUpload = completed.Result;
		_farRegionalUploadBand = 0;
	}

	private bool UpdateRegionalTransitState(double delta)
	{
		// A previous transit optimization removed the only regional guard at
		// high noclip speeds.  Regional terrain is authoritative coverage and
		// must remain visible regardless of movement speed.
		if (_regionalTransitHidden)
		{
			_regionalTransitHidden = false;
			_regionalTransitResumeElapsed = 0.0;
			_lastFarRegionalAnchorAround = long.MinValue;
			_lastFarRegionalAnchorAcross = int.MinValue;
			_requestedFarRegionalAnchorAround = long.MinValue;
			_requestedFarRegionalAnchorAcross = int.MinValue;
			if (_regionalBackdrop != null) _regionalBackdrop.Visible = true;
			foreach (MeshInstance3D terrain in _farRegionalTerrains) terrain.Visible = true;
			GD.Print("Scaled far regional terrain visibility restored; speed-based hiding is disabled.");
		}
		return false;
	}

	private static FarRegionalBandData BuildFarRegionalBandData(
		string name,
		RingTerrainSampler sampler,
		long anchorAround,
		int anchorAcross,
		double innerRadius,
		double outerRadius,
		double sampleSpacing,
		double circumference,
		double radius,
		int width,
		float scale,
		Color low,
		Color high,
		RingLodSurfaceEditSnapshot edits)
	{
		int radialSteps = Mathf.Clamp(Mathf.CeilToInt((float)((outerRadius - innerRadius) / sampleSpacing)), 8, 256);
		const int angularSteps = 96;
		var vertices = new Vector3[(radialSteps + 1) * (angularSteps + 1)];
		var normals = new Vector3[vertices.Length];
		var colors = new Color[vertices.Length];
		// Keep this coordinate unwrapped while a regional band crosses the
		// circumference seam. The shader applies fract() only at lookup time,
		// so interpolation never blends opposite sides of the shadow texture.
		var macroShadowUvs = new Vector2[vertices.Length];
		double canonicalAnchorAround = anchorAround % circumference;
		if (canonicalAnchorAround < 0.0) canonicalAnchorAround += circumference;
		for (int radial = 0; radial <= radialSteps; radial++)
		for (int angular = 0; angular <= angularSteps; angular++)
		{
			double distance = innerRadius + (outerRadius - innerRadius) * radial / radialSteps;
			double angle = Math.Tau * angular / angularSteps;
			long around = anchorAround + (long)Math.Round(Math.Cos(angle) * distance);
			int across = Mathf.Clamp(anchorAcross + Mathf.RoundToInt((float)(Math.Sin(angle) * distance)), 0, width);
			bool usesPresentationSurface = edits.UsesFloatSurface || sampler.UsesRuntimeSurfaceGeneration;
			TerrainSample sample = usesPresentationSurface
				? edits.GetPresentationTerrainSample(sampler, around, across)
				: sampler.GetTerrainSample(around, across);
			float terrainHeight = usesPresentationSurface
				? edits.GetPresentationSurfaceHeight(sampler, around, across)
				: sample.SurfaceHeight;
			float displayedHeight = sample.ResolveDisplayedHeight(terrainHeight);
			int index = radial * (angularSteps + 1) + angular;
			vertices[index] = ScaledPointRelativeForBuild(
				around, displayedHeight - 150.0, across,
				anchorAround, 0.0, anchorAcross,
				circumference, radius, width, scale);
			colors[index] = RingTerrainVisuals.ResolveMacroTextureColor(sample, Mathf.RoundToInt(displayedHeight), sampler, low, high);
			double unwrappedAround = canonicalAnchorAround + (around - anchorAround);
			macroShadowUvs[index] = new Vector2(
				(float)(unwrappedAround / circumference),
				across / (float)Mathf.Max(1, width));
		}

		int row = angularSteps + 1;
		var indices = new int[radialSteps * angularSteps * 6];
		int cursor = 0;
		for (int radial = 0; radial < radialSteps; radial++)
		for (int angular = 0; angular < angularSteps; angular++)
		{
			int i00 = radial * row + angular;
			int i10 = i00 + 1;
			int i01 = i00 + row;
			int i11 = i01 + 1;
			AddRawTriangle(indices, ref cursor, vertices, normals, i01, i10, i00);
			AddRawTriangle(indices, ref cursor, vertices, normals, i10, i01, i11);
		}
		for (int i = 0; i < normals.Length; i++)
			normals[i] = normals[i].LengthSquared() > 0.0000001f ? normals[i].Normalized() : Vector3.Up;
		return new FarRegionalBandData(name, vertices, normals, colors, macroShadowUvs, indices, innerRadius, outerRadius);
	}

	// Every band samples the shared circumference edge at the same 97 angles.
	// Their triangle fans have different radial row widths, however, so their
	// independently accumulated normal can differ at an otherwise welded edge.
	private static void WeldFarRegionalBandNormals(IReadOnlyList<FarRegionalBandData> bands)
	{
		const int angularRowLength = 97;
		for (int bandIndex = 1; bandIndex < bands.Count; bandIndex++)
		{
			FarRegionalBandData previous = bands[bandIndex - 1];
			FarRegionalBandData next = bands[bandIndex];
			int previousRow = previous.Vertices.Length - angularRowLength;
			for (int angular = 0; angular < angularRowLength; angular++)
			{
				int previousIndex = previousRow + angular;
				if (!previous.Vertices[previousIndex].IsEqualApprox(next.Vertices[angular]))
					throw new InvalidOperationException($"Regional far band boundary mismatch: {previous.Name} -> {next.Name}.");
				Vector3 normal = (previous.Normals[previousIndex] + next.Normals[angular]).Normalized();
				if (normal.LengthSquared() <= 0.0000001f) normal = Vector3.Up;
				previous.Normals[previousIndex] = normal;
				next.Normals[angular] = normal;
			}
		}
	}

	/// <summary>Returns contiguous regional coverage while retaining the legacy reach.</summary>
	internal static IReadOnlyList<FarRegionalBandSpec> GetFarRegionalBandSpecs(bool includeIntermediateTiers)
	{
		if (!includeIntermediateTiers)
			return new[]
			{
				new FarRegionalBandSpec("ScaledFarLod9", 125000.0, 625000.0, 2048.0),
				new FarRegionalBandSpec("ScaledFarLod10", 625000.0, 2250000.0, 8192.0),
				new FarRegionalBandSpec("ScaledFarLod11", 2250000.0, 7960000.0, 32768.0)
			};

		return new[]
		{
			new FarRegionalBandSpec("ScaledFarLod9Bridge512", 125000.0, 187500.0, 512.0),
			new FarRegionalBandSpec("ScaledFarLod9Bridge1024", 187500.0, 250000.0, 1024.0),
			new FarRegionalBandSpec("ScaledFarLod9", 250000.0, 625000.0, 2048.0),
			new FarRegionalBandSpec("ScaledFarLod10Bridge4096", 625000.0, 1125000.0, 4096.0),
			new FarRegionalBandSpec("ScaledFarLod10", 1125000.0, 2250000.0, 8192.0),
			new FarRegionalBandSpec("ScaledFarLod11Bridge16384", 2250000.0, 4500000.0, 16384.0),
			new FarRegionalBandSpec("ScaledFarLod11", 4500000.0, 7960000.0, 32768.0)
		};
	}

	public void SetIntermediateLodTiers(bool enabled)
	{
		if (IntermediateRegionalLodTiersEnabled == enabled) return;
		IntermediateRegionalLodTiersEnabled = enabled;
		_farRegionalLayoutVersion++;
		_farRegionalUpload = null;
		_farRegionalUploadBand = 0;
		DiscardFarRegionalStaging();
		// Invalidate only the publication marker. The active meshes stay visible
		// until the fresh, complete candidate set is atomically published.
		_lastFarRegionalAnchorAround = long.MinValue;
		_lastFarRegionalAnchorAcross = int.MinValue;
		_requestedFarRegionalAnchorAround = long.MinValue;
		_requestedFarRegionalAnchorAcross = int.MinValue;
	}

	private void DiscardFarRegionalStaging()
	{
		foreach (MeshInstance3D mesh in _farRegionalStagingTerrains) mesh.QueueFree();
		_farRegionalStagingTerrains.Clear();
	}

	private void ActivateRegionalRenderOrigin(long around, int across)
	{
		_regionalRenderOriginAround = around;
		_regionalRenderOriginAcross = across;
		_regionalRingCenter = ScaledPointRelativeForBuild(
			0.0, 0.0, 0.0,
			around, 0.0, across,
			_circumferenceMeters, _radiusMeters, Mathf.Max(1, RingWidthMeters), CoordinateScale);

	}

	private void PublishFarRegionalStaging(FarRegionalBuildResult result)
	{
		ActivateRegionalRenderOrigin(result.AnchorAround, result.AnchorAcross);
		foreach (MeshInstance3D mesh in _farRegionalTerrains)
		{
			mesh.Visible = false;
			mesh.QueueFree();
		}
		_farRegionalTerrains.Clear();
		_farRegionalTerrains.AddRange(_farRegionalStagingTerrains);
		_farRegionalStagingTerrains.Clear();
		foreach (MeshInstance3D mesh in _farRegionalTerrains) mesh.Visible = true;
		_lastFarRegionalAnchorAround = result.AnchorAround;
		_lastFarRegionalAnchorAcross = result.AnchorAcross;
		_lastFarRegionalEditVersion = result.EditVersion;
		GD.Print($"Scaled far regional LOD refreshed: around={_lastFarRegionalAnchorAround}, across={_lastFarRegionalAnchorAcross}, bands={_farRegionalTerrains.Count}, async=true.");
		_farRegionalUpload = null;
		_farRegionalUploadBand = 0;
	}

	private void ApplyFarRegionalBand(
		List<MeshInstance3D> destination,
		FarRegionalBandData data,
		long anchorAround,
		int anchorAcross,
		bool visible)
	{
		var instance = new MeshInstance3D { Name = data.Name, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true };
		_regionalFarViewport!.AddChild(instance);
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
		arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
		arrays[(int)Mesh.ArrayType.Color] = data.Colors;
		arrays[(int)Mesh.ArrayType.TexUV] = data.MacroShadowUvs;
		arrays[(int)Mesh.ArrayType.Index] = data.Indices;
		var mesh = new ArrayMesh();
		mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
		instance.Mesh = mesh;
		ShaderMaterial material = (ShaderMaterial)_terrainMaterial!.Duplicate();
		RingAtmosphereModel.ConfigureMaterial(material, _radiusMeters, CoordinateScale);
		global::RingworldTechDemo.RingTerrainMacroShadowProjector.Register(material, CoordinateScale);
		// This band receives a fresh material after an asynchronous rebuild.
		// Force the cached regional camera/ring uniforms onto that new instance
		// during the same process frame.
		InvalidateRegionalMaterialUniformCache();
		float scaledInner = (float)(data.InnerRadius * CoordinateScale);
		float scaledOuter = (float)(data.OuterRadius * CoordinateScale);
		// The regional renderer measures these distances from its own nearby
		// camera, so exact scaled handoffs are stable even for the 75 Gm ring.
		material.SetShaderParameter("fade_start_meters", scaledInner);
		material.SetShaderParameter("fade_end_meters", scaledInner);
		material.SetShaderParameter("far_fade_enabled", true);
		material.SetShaderParameter("fade_out_start_meters", scaledOuter);
		material.SetShaderParameter("fade_out_end_meters", scaledOuter);
		material.SetShaderParameter("camera_relative_region", true);
		// The mesh itself is an exact anchor-relative annulus. Do not apply the
		// shared shader's camera-relative fade here: reanchoring can place the
		// camera hundreds of kilometres from the annulus centre and shift a seam.
		material.SetShaderParameter("regional_band_distance_clip", false);
		material.SetShaderParameter(
			"regional_surface_up",
			SurfaceUpForAround(anchorAround, _circumferenceMeters, _radiusMeters));
		// The detailed atmosphere reconstructs a global cylinder from shader
		// floats, which camera-relative regional meshes cannot retain. The
		// far-shroud branch needs no global reconstruction. Macro terrain
		// shadows instead use the stable canonical UV stored on this mesh.
		material.SetShaderParameter("ring_atmosphere_far_shroud_enabled", true);
		material.SetShaderParameter(
			"ring_atmosphere_half_width_meters",
			RingWidthMeters * CoordinateScale * 0.5f);
		material.SetShaderParameter(
			"ring_atmosphere_far_shroud_visibility",
			_farAtmosphereShroudVisibility);
		material.SetShaderParameter("terrain_macro_shadow_use_uv", true);
		material.SetShaderParameter("analytic_cloud_shadow_enabled", false);
		instance.MaterialOverride = material;
		instance.Visible = visible;
		destination.Add(instance);
	}

	private static void AddRawTriangle(
		int[] indices, ref int cursor, Vector3[] vertices, Vector3[] normals,
		int a, int b, int c)
	{
		indices[cursor++] = a;
		indices[cursor++] = b;
		indices[cursor++] = c;
		Vector3 normal = (vertices[b] - vertices[a]).Cross(vertices[c] - vertices[a]);
		normals[a] += normal;
		normals[b] += normal;
		normals[c] += normal;
	}

	private static Vector3 ScaledPointRelativeForBuild(
		double around, double vertical, double across,
		double originAround, double originVertical, double originAcross,
		double circumference, double radius, int width, float scale)
	{
		GetScaledPointComponents(
			around, vertical, across, circumference, radius, width, scale,
			out double pointX, out double pointY, out double pointZ);
		GetScaledPointComponents(
			originAround, originVertical, originAcross, circumference, radius, width, scale,
			out double originX, out double originY, out double originZ);
		return new Vector3(
			(float)(pointX - originX),
			(float)(pointY - originY),
			(float)(pointZ - originZ));
	}

	private static void GetScaledPointComponents(
		double around, double vertical, double across,
		double circumference, double radius, int width, float scale,
		out double x, out double y, out double z)
	{
		double wrapped = around % circumference;
		if (wrapped < 0.0) wrapped += circumference;
		double angle = wrapped / radius;
		double radial = radius - vertical;
		double acrossBase = -width * 0.5;
		x = Math.Cos(angle) * radial * scale;
		y = (acrossBase + across) * scale;
		z = Math.Sin(angle) * radial * scale;
	}

	private static Vector3 SurfaceUpForAround(double around, double circumference, double radius)
	{
		double wrapped = around % circumference;
		if (wrapped < 0.0) wrapped += circumference;
		double angle = wrapped / radius;
		return new Vector3((float)-Math.Cos(angle), 0.0f, (float)-Math.Sin(angle));
	}

	private void BuildStructuralShell()
	{
		if (_farViewport == null || _cavityShellMaterial == null) return;
		int aroundCount = Mathf.Max(128, AroundSegments / 2);
		double aroundStep = _circumferenceMeters / aroundCount;
		double bottom = _grid != null
			? _grid.GetStructuralBottomGlobalY() * (double)VoxelConstants.VoxelSizeMeters
			: -Math.Max(1.0, StructuralDepthMeters);
		double terrainBottom = _grid?.TerrainBottomGlobalY ?? -8192.0;
		double lowerHullTop = _grid?.GetLowerHullTopGlobalY() ?? bottom;
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);
		for (int x = 0; x < aroundCount; x++)
		{
			double a0 = x * aroundStep, a1 = (x + 1) * aroundStep;
			if (BuildStructuralUnderside)
			{
				AddTriangle(surface, ScaledPoint(a0, bottom, 0.0), ScaledPoint(a1, bottom, RingWidthMeters), ScaledPoint(a1, bottom, 0.0), HullColor);
				AddTriangle(surface, ScaledPoint(a0, bottom, 0.0), ScaledPoint(a0, bottom, RingWidthMeters), ScaledPoint(a1, bottom, RingWidthMeters), HullColor);
			}
			if (BuildTerrainRoofUnderside)
			{
				// Godot clockwise when viewed from the cavity below.
				AddTriangle(surface, ScaledPoint(a0, terrainBottom, 0.0), ScaledPoint(a1, terrainBottom, RingWidthMeters), ScaledPoint(a1, terrainBottom, 0.0), HullColor);
				AddTriangle(surface, ScaledPoint(a0, terrainBottom, 0.0), ScaledPoint(a0, terrainBottom, RingWidthMeters), ScaledPoint(a1, terrainBottom, RingWidthMeters), HullColor);
			}
			if (BuildLowerHullCavityFloor)
			{
				// Reverse the roof: cavity is above the lower-hull top.
				AddTriangle(surface, ScaledPoint(a0, lowerHullTop, 0.0), ScaledPoint(a1, lowerHullTop, 0.0), ScaledPoint(a1, lowerHullTop, RingWidthMeters), HullColor);
				AddTriangle(surface, ScaledPoint(a0, lowerHullTop, 0.0), ScaledPoint(a1, lowerHullTop, RingWidthMeters), ScaledPoint(a0, lowerHullTop, RingWidthMeters), HullColor);
			}
			AddWall(surface, a0, a1, 0.0, bottom, StructuralWallTopMeters, true);
			AddWall(surface, a0, a1, RingWidthMeters, bottom, StructuralWallTopMeters, false);
		}
		surface.GenerateNormals();
		surface.Index();
		var structuralShell = new MeshInstance3D
		{
			Name = "ScaledStructuralShell",
			Mesh = surface.Commit(),
			MaterialOverride = _cavityShellMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			IgnoreOcclusionCulling = true
		};
		_farViewport.AddChild(structuralShell);
		DisableWholeRingFrustumCulling(structuralShell);
	}

	private void BuildCloudShell()
	{
		if (_farViewport == null || _cloudMaterial == null) return;
		int aroundCount = Mathf.Max(64, AroundSegments / 4);
		int acrossCount = Mathf.Max(8, AcrossSegments);
		double aroundStep = _circumferenceMeters / aroundCount;
		double acrossStep = RingWidthMeters / (double)acrossCount;
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);
		for (int z = 0; z < acrossCount; z++)
		for (int x = 0; x < aroundCount; x++)
		{
			AddCloudVertex(surface, x, z + 1, aroundCount, acrossCount, aroundStep, acrossStep);
			AddCloudVertex(surface, x + 1, z, aroundCount, acrossCount, aroundStep, acrossStep);
			AddCloudVertex(surface, x, z, aroundCount, acrossCount, aroundStep, acrossStep);
			AddCloudVertex(surface, x + 1, z, aroundCount, acrossCount, aroundStep, acrossStep);
			AddCloudVertex(surface, x, z + 1, aroundCount, acrossCount, aroundStep, acrossStep);
			AddCloudVertex(surface, x + 1, z + 1, aroundCount, acrossCount, aroundStep, acrossStep);
		}
		surface.Index();
		_scaledCloudShell = new MeshInstance3D
		{
			Name = "ScaledCloudShell",
			Mesh = surface.Commit(),
			MaterialOverride = _cloudMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			IgnoreOcclusionCulling = true,
			// The shell owns the ring beyond the camera-local clipmap. Their
			// shader fades overlap; never make this whole-ring coverage depend
			// on whether the mid-distance tier is enabled.
			Visible = true
		};
		_farViewport.AddChild(_scaledCloudShell);
		DisableWholeRingFrustumCulling(_scaledCloudShell);
	}

	private void BuildFarCloudClipmap()
	{
		if (_farViewport == null || !FarCloudClipmapEnabled) return;
		Shader? shader = GD.Load<Shader>("res://materials/ring_far_cloud_clipmap.gdshader");
		if (shader == null) return;

		// Three independently scrolling layers give the far horizon parallax.
		// Their mesh is an annular, curved canopy rather than a planar quad:
		// the open center is owned by Sunshine and the remaining canopy follows
		// the ring instead of becoming a flat ceiling at night.
		for (int layer = 0; layer < _farCloudClipmapLayers.Length; layer++)
		{
			var material = new ShaderMaterial { Shader = shader, RenderPriority = 12 + layer };
			RingAtmosphereModel.ConfigureMaterial(material, _radiusMeters, CoordinateScale);
			material.SetShaderParameter("layer_index", (float)layer);
			material.SetShaderParameter("opacity", FarCloudClipmapOpacity / (layer == 0 ? 1.0f : 1.45f));
			material.SetShaderParameter("patch_radius_meters", GetEffectiveFarCloudClipmapRadiusMeters());
			material.SetShaderParameter("patch_across_meters", RingWidthMeters);
			material.SetShaderParameter("handoff_start_meters", FarCloudClipmapHandoffStartMeters);
			material.SetShaderParameter("ring_width_km", RingWidthMeters * 0.001f);
			material.SetShaderParameter("ring_circumference_km", (float)(_circumferenceMeters * 0.001));
			ConfigureFarWeatherMaterial(material);
			var instance = new MeshInstance3D
			{
				Name = $"FarCloudClipmapLayer{layer}",
				Mesh = CreateCurvedFarCloudClipmapMesh(layer),
				MaterialOverride = material,
				CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
				IgnoreOcclusionCulling = true,
				Visible = true
			};
			_farViewport.AddChild(instance);
			// The annulus is camera-relative but its curved local AABB spans
			// only a thin chord. Godot can reject it before the shader runs;
			// use the same whole-ring culling exemption as ScaledCloudShell.
			DisableWholeRingFrustumCulling(instance);
			_farCloudClipmapLayers[layer] = instance;
			_farCloudClipmapMaterials[layer] = material;
		}
	}

	private float GetEffectiveFarCloudClipmapRadiusMeters()
	{
		// Do not let a camera-local cloud patch continue into the opposite
		// side of an 80 km ring. The scaled whole-ring tiers own that view.
		return Mathf.Min(
			Mathf.Max(30000.0f, FarCloudClipmapRadiusMeters),
			Mathf.Max(30000.0f, (float)_radiusMeters * 1.35f));
	}

	/// <summary>Actual capped regional outer radius used by lightning handoff consumers.</summary>
	public float EffectiveFarCloudClipmapRadiusMeters => GetEffectiveFarCloudClipmapRadiusMeters();

	private float GetFarCloudClipmapHeightMeters(int layer)
	{
		// A wider distant tower stack gives the far canopy readable vertical
		// extent above the rising ring without adding geometry or passes.
		return LocalCloudBaseMeters + 900.0f + layer * 2400.0f;
	}

	private Mesh CreateCurvedFarCloudClipmapMesh(int layer)
	{
		float outerMeters = GetEffectiveFarCloudClipmapRadiusMeters();
		float innerMeters = Mathf.Clamp(FarCloudClipmapHandoffStartMeters, 1000.0f, outerMeters - 1000.0f);
		float acrossHalfMeters = Mathf.Max(1000.0f, RingWidthMeters * 0.5f);
		float altitudeMeters = GetFarCloudClipmapHeightMeters(layer);
		float curvedRadius = Mathf.Max(1.0f, (float)_radiusMeters - altitudeMeters) * CoordinateScale;
		int angularSegments = Mathf.Clamp(Mathf.CeilToInt(Mathf.Tau * outerMeters / 7000.0f), 64, 128);
		int radialSegments = Mathf.Clamp(Mathf.CeilToInt((outerMeters - innerMeters) / 10000.0f), 6, 16);
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);
		AddCurvedFarCloudClipmapAnnulus(
			surface,
			innerMeters,
			outerMeters,
			acrossHalfMeters,
			curvedRadius,
			angularSegments,
			radialSegments);
		return surface.Commit();
	}

	private void AddCurvedFarCloudClipmapAnnulus(
		SurfaceTool surface,
		float innerMeters,
		float outerMeters,
		float acrossHalfMeters,
		float curvedRadius,
		int angularSegments,
		int radialSegments)
	{
		for (int angularIndex = 0; angularIndex < angularSegments; angularIndex++)
		{
			float angle0 = Mathf.Tau * angularIndex / angularSegments;
			float angle1 = Mathf.Tau * (angularIndex + 1) / angularSegments;
			float aroundDirection0 = Mathf.Cos(angle0);
			float acrossDirection0 = Mathf.Sin(angle0);
			float aroundDirection1 = Mathf.Cos(angle1);
			float acrossDirection1 = Mathf.Sin(angle1);
			float radialLimit0 = GetFarCloudClipmapRadialLimit(
				aroundDirection0,
				acrossDirection0,
				outerMeters,
				acrossHalfMeters);
			float radialLimit1 = GetFarCloudClipmapRadialLimit(
				aroundDirection1,
				acrossDirection1,
				outerMeters,
				acrossHalfMeters);

			for (int radialIndex = 0; radialIndex < radialSegments; radialIndex++)
			{
				float radialFraction0 = radialIndex / (float)radialSegments;
				float radialFraction1 = (radialIndex + 1) / (float)radialSegments;
				float radius00 = Mathf.Lerp(innerMeters, radialLimit0, radialFraction0);
				float radius01 = Mathf.Lerp(innerMeters, radialLimit0, radialFraction1);
				float radius10 = Mathf.Lerp(innerMeters, radialLimit1, radialFraction0);
				float radius11 = Mathf.Lerp(innerMeters, radialLimit1, radialFraction1);
				Vector2 point00 = new(aroundDirection0 * radius00, acrossDirection0 * radius00);
				Vector2 point10 = new(aroundDirection1 * radius10, acrossDirection1 * radius10);
				Vector2 point01 = new(aroundDirection0 * radius01, acrossDirection0 * radius01);
				Vector2 point11 = new(aroundDirection1 * radius11, acrossDirection1 * radius11);
				AddCurvedFarCloudVertex(surface, point00.X, point00.Y, outerMeters, acrossHalfMeters, curvedRadius);
				AddCurvedFarCloudVertex(surface, point10.X, point10.Y, outerMeters, acrossHalfMeters, curvedRadius);
				AddCurvedFarCloudVertex(surface, point01.X, point01.Y, outerMeters, acrossHalfMeters, curvedRadius);
				AddCurvedFarCloudVertex(surface, point10.X, point10.Y, outerMeters, acrossHalfMeters, curvedRadius);
				AddCurvedFarCloudVertex(surface, point11.X, point11.Y, outerMeters, acrossHalfMeters, curvedRadius);
				AddCurvedFarCloudVertex(surface, point01.X, point01.Y, outerMeters, acrossHalfMeters, curvedRadius);
			}
		}
	}

	private static float GetFarCloudClipmapRadialLimit(
		float aroundDirection,
		float acrossDirection,
		float aroundLimitMeters,
		float acrossLimitMeters)
	{
		const float DirectionEpsilon = 0.0001f;
		float aroundRadialLimit = Mathf.Abs(aroundDirection) > DirectionEpsilon
			? aroundLimitMeters / Mathf.Abs(aroundDirection)
			: float.PositiveInfinity;
		float acrossRadialLimit = Mathf.Abs(acrossDirection) > DirectionEpsilon
			? acrossLimitMeters / Mathf.Abs(acrossDirection)
			: float.PositiveInfinity;
		return Mathf.Min(aroundRadialLimit, acrossRadialLimit);
	}

	private void AddCurvedFarCloudVertex(
		SurfaceTool surface,
		float aroundMeters,
		float acrossMeters,
		float outerMeters,
		float acrossHalfMeters,
		float curvedRadius)
	{
		float angle = aroundMeters / Mathf.Max(1.0f, (float)_radiusMeters);
		Vector3 point = new(
			curvedRadius * Mathf.Sin(angle),
			curvedRadius * (1.0f - Mathf.Cos(angle)),
			acrossMeters * CoordinateScale);
		Vector2 uv = new(
			0.5f + 0.5f * aroundMeters / outerMeters,
			0.5f + 0.5f * acrossMeters / acrossHalfMeters);
		AddUvVertex(surface, point, uv);
	}

	private void RebuildFarCloudClipmapMeshes()
	{
		for (int layer = 0; layer < _farCloudClipmapLayers.Length; layer++)
		{
			MeshInstance3D? instance = _farCloudClipmapLayers[layer];
			ShaderMaterial? material = _farCloudClipmapMaterials[layer];
			if (instance == null || material == null) continue;
			instance.Mesh = CreateCurvedFarCloudClipmapMesh(layer);
			material.SetShaderParameter("patch_radius_meters", GetEffectiveFarCloudClipmapRadiusMeters());
			material.SetShaderParameter("patch_across_meters", RingWidthMeters);
			material.SetShaderParameter("handoff_start_meters", FarCloudClipmapHandoffStartMeters);
			_hasFarCloudClipmapState[layer] = false;
		}
	}

	private void SyncFarCloudClipmap(in RingCanonicalPosition canonical, Vector3 sunDirection)
	{
		if (!FarCloudClipmapEnabled || _farCloudClipmapLayers[0] == null) return;
		double wrappedAround = canonical.AroundMeters % _circumferenceMeters;
		if (wrappedAround < 0.0) wrappedAround += _circumferenceMeters;
		double angle = wrappedAround / _radiusMeters;
		Vector3 radialOut = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
		Vector3 tangent = new(-radialOut.Z, 0.0f, radialOut.X);
		Vector3 inward = -radialOut;
		global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState weather = _weatherSource?.QueryLocalWeather(canonical)
			?? default;
		float storm = Mathf.Clamp(weather.SystemInfluence * 0.65f + weather.PrecipitationPotential * 0.55f, 0.0f, 1.0f);
		// The natural clear-cell floor (0.05) drives the clipmap threshold so
		// high that all three distant layers resolve to transparent. Keep a
		// sparse, visible canopy beyond Sunshine's 48 km handoff instead.
		float coverage = Mathf.Clamp(weather.CloudCoverage, 0.36f, 0.98f);
		for (int layer = 0; layer < _farCloudClipmapLayers.Length; layer++)
		{
			MeshInstance3D? instance = _farCloudClipmapLayers[layer];
			ShaderMaterial? material = _farCloudClipmapMaterials[layer];
			if (instance == null || material == null) continue;
			float height = GetFarCloudClipmapHeightMeters(layer);
			Transform3D transform = new(
				new Basis(tangent, inward, Vector3.Up),
				ScaledPoint(canonical.AroundMeters, height, canonical.AcrossMeters));
			var state = new FarCloudClipmapState(
				transform,
				new Vector2((float)(wrappedAround * 0.001), (float)(canonical.AcrossMeters * 0.001)),
				coverage,
				storm,
				sunDirection,
				inward,
				(float)canonical.VerticalMeters);
			if (!_hasFarCloudClipmapState[layer] || state != _lastFarCloudClipmapStates[layer])
			{
				if (instance.GlobalTransform != state.Transform) instance.GlobalTransform = state.Transform;
				material.SetShaderParameter("canonical_origin_km", state.CanonicalOriginKm);
				material.SetShaderParameter("coverage", state.Coverage);
				material.SetShaderParameter("storm_factor", state.Storm);
				material.SetShaderParameter("sun_direction", state.SunDirection);
				material.SetShaderParameter("surface_up", state.SurfaceUp);
				material.SetShaderParameter("viewer_height_meters", state.ViewerHeight);
				_lastFarCloudClipmapStates[layer] = state;
				_hasFarCloudClipmapState[layer] = true;
			}
			material.SetShaderParameter("weather_time_seconds", ResolveFarWeatherAtlasAgeSeconds());
			if (!instance.Visible) instance.Visible = true;
		}
	}

	private static void DisableWholeRingFrustumCulling(MeshInstance3D instance)
	{
		// IgnoreOcclusionCulling does not disable frustum culling. These
		// whole-ring or camera-surrounding meshes can have bounds that do not
		// represent the visible arc, so prevent renderer-level rejection.
		instance.IgnoreOcclusionCulling = true;
		RenderingServer.InstanceSetIgnoreCulling(instance.GetInstance(), true);
	}

	private void BuildLocalCloudCanopy()
	{
		if (_farViewport == null || _localCloudCanopyMaterial == null) return;
		float radius = Mathf.Max(1000.0f, LocalCloudRadiusMeters) * CoordinateScale;
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);
		AddUvVertex(surface, new Vector3(-radius, 0.0f, -radius), new Vector2(0.0f, 0.0f));
		AddUvVertex(surface, new Vector3(radius, 0.0f, -radius), new Vector2(1.0f, 0.0f));
		AddUvVertex(surface, new Vector3(-radius, 0.0f, radius), new Vector2(0.0f, 1.0f));
		AddUvVertex(surface, new Vector3(radius, 0.0f, -radius), new Vector2(1.0f, 0.0f));
		AddUvVertex(surface, new Vector3(radius, 0.0f, radius), new Vector2(1.0f, 1.0f));
		AddUvVertex(surface, new Vector3(-radius, 0.0f, radius), new Vector2(0.0f, 1.0f));
		_localCloudCanopy = new MeshInstance3D
		{
			Name = "ScaledLocalCloudSkyCanopy",
			Mesh = surface.Commit(),
			MaterialOverride = _localCloudCanopyMaterial,
			CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
			IgnoreOcclusionCulling = true
		};
		_farViewport.AddChild(_localCloudCanopy);
	}

	private void BuildScaledCloudVolume()
	{
		if (_farViewport == null || _scaledCloudVolumeMaterial == null) return;
		_scaledCloudVolume = new FogVolume
		{
			Name = "ScaledLocalVolumetricClouds",
			Shape = RenderingServer.FogVolumeShape.Box,
			Size = new Vector3(
				LocalCloudRadiusMeters * 2.0f * CoordinateScale,
				Mathf.Max(100.0f, LocalCloudTopMeters - LocalCloudBaseMeters) * CoordinateScale,
				LocalCloudRadiusMeters * 2.0f * CoordinateScale),
			Material = _scaledCloudVolumeMaterial,
			Visible = LocalVolumetricCloudsEnabled
		};
		_farViewport.AddChild(_scaledCloudVolume);
	}

	private void CreateCloudTransmittanceMasks()
	{
		if (!CloudTransmittanceMaskEnabled
			|| (_localVolumetricClouds == null && _sunshineClouds == null)) return;
		Shader? maskShader = GD.Load<Shader>("res://materials/ring_cloud_transmittance_mask.gdshader");
		Shader? compositeShader = GD.Load<Shader>("res://materials/ring_far_backdrop_cloud_composite.gdshader");
		if (maskShader == null || compositeShader == null)
		{
			GD.PushWarning("Far-ring cloud transmittance mask disabled: mask or composite shader is missing.");
			return;
		}

		_backdropCloudCompositeMaterial = new ShaderMaterial { Shader = compositeShader };
		for (int index = 0; index < _cloudMaskViewports.Length; index++)
		{
			var viewport = new SubViewport
			{
				Name = $"CloudTransmittanceMask{index}",
				Size = new Vector2I(2, 2),
				Disable3D = true,
				TransparentBg = true,
				HandleInputLocally = false,
				RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled
			};
			var material = new ShaderMaterial { Shader = maskShader };
			var rect = new ColorRect
			{
				Name = "CloudMaskRaymarch",
				Color = Colors.White,
				Material = material,
				MouseFilter = Control.MouseFilterEnum.Ignore
			};
			AddChild(viewport);
			viewport.AddChild(rect);
			rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
			_cloudMaskViewports[index] = viewport;
			_cloudMaskMaterials[index] = material;
		}

		_backdropCloudCompositeMaterial.SetShaderParameter(
			"previous_cloud_mask",
			_cloudMaskViewports[0]!.GetTexture());
		_backdropCloudCompositeMaterial.SetShaderParameter(
			"current_cloud_mask",
			_cloudMaskViewports[1]!.GetTexture());
		// Canvas shaders require every sampled texture to have a valid binding
		// even while the exact Sunshine path is disabled. The proxy mask is a
		// stable transparent fallback until Sunshine publishes its RD texture.
		_backdropCloudCompositeMaterial.SetShaderParameter(
			"sunshine_cloud_texture",
			_cloudMaskViewports[0]!.GetTexture());
		_backdropCloudCompositeMaterial.SetShaderParameter("temporal_blend", 1.0f);
		_backdropCloudCompositeMaterial.SetShaderParameter("cloud_mask_enabled", false);
		_backdropCloudCompositeMaterial.SetShaderParameter("exact_sunshine_clouds_enabled", false);
	}

	private void CreateBackdrop()
	{
		if (_farViewport == null) return;
		// Keep the far texture in the normal pre-HUD canvas range. Environment
		// canvas backgrounds consume layers strictly below their maximum layer.
		var layer = new CanvasLayer { Name = "FarRingBackdrop", Layer = -1 };
		AddChild(layer);
		_backdrop = new TextureRect
		{
			Name = "FarRingTexture",
			Texture = _farViewport.GetTexture(),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		layer.AddChild(_backdrop);
		_backdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
		// In Mega Ring, the local cloud-mask compositor is intentionally off.
		// Use the SubViewport texture directly instead of routing it through a
		// no-op canvas shader; on Arc that extra sampled pass produced tiled
		// backdrop quadrants at the local/far handoff.
		if (_backdropCloudCompositeMaterial != null && CloudTransmittanceMaskEnabled)
			_backdrop.Material = _backdropCloudCompositeMaterial;

		if (_regionalFarViewport == null)
			return;

		_regionalBackdrop = new TextureRect
		{
			Name = "FarRingRegionalTexture",
			Texture = _regionalFarViewport.GetTexture(),
			ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
			StretchMode = TextureRect.StretchModeEnum.Scale,
			MouseFilter = Control.MouseFilterEnum.Ignore
		};
		layer.AddChild(_regionalBackdrop);
		_regionalBackdrop.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
	}

	private void ConfigureMainBackground()
	{
		if (_mainWorldEnvironment?.Environment is not { } environment) return;
		environment.BackgroundMode = Godot.Environment.BGMode.Canvas;
		environment.BackgroundCanvasMaxLayer = 0;
	}

	private void SyncCameraAndLighting()
	{
		if (_grid == null || _explorer == null || _sourceCamera == null || _farCamera == null) return;
		RingCanonicalPosition canonical = _explorer.CanonicalPosition;
		UpdateFarAtmosphereShroud(canonical);
		Basis worldToCanonical = _grid.GlobalBasis.Orthonormalized().Inverse();
		if (!IsUsableBasis(worldToCanonical)) worldToCanonical = Basis.Identity;
		SyncTerrainMacroShadowReceiverDirections(worldToCanonical);

		Transform3D sourceTransform = _sourceCamera.GetCameraTransform();
		// The explorer canonical position tracks feet. The render camera can be
		// offset by head bob, vehicle mounts, or a free camera, so derive the
		// ray origin from the actual renderer eye.
		RingCanonicalPosition eyeCanonical = _grid.WorldToCanonicalRingPosition64(sourceTransform.Origin);
		float coordinateScale = IsFinite(CoordinateScale) && CoordinateScale > 0.0f
			? CoordinateScale
			: 0.001f;
		Vector3 scaledPosition = ScaledPoint(canonical.AroundMeters, canonical.VerticalMeters, canonical.AcrossMeters);
		scaledPosition += worldToCanonical * (sourceTransform.Origin - _explorer.GlobalPosition) * coordinateScale;
		Basis farCameraBasis = worldToCanonical * sourceTransform.Basis;
		bool validCameraTransform = IsFinite(scaledPosition) && IsUsableBasis(farCameraBasis);
		if (validCameraTransform)
		{
			farCameraBasis = farCameraBasis.Orthonormalized();
			validCameraTransform = IsUsableBasis(farCameraBasis);
		}

		if (validCameraTransform)
		{
			_lastValidFarCameraBasis = farCameraBasis;
			_lastValidFarCameraPosition = scaledPosition;
			_reportedInvalidFarCameraState = false;
		}
		else
		{
			farCameraBasis = _lastValidFarCameraBasis;
			scaledPosition = _lastValidFarCameraPosition;
			if (!_reportedInvalidFarCameraState)
			{
				GD.PushWarning("Scaled far camera received an invalid transform; retaining its last valid transform.");
				_reportedInvalidFarCameraState = true;
			}
		}

		Transform3D farCameraTransform = new(farCameraBasis, scaledPosition);
		if (_farCamera.GlobalTransform != farCameraTransform) _farCamera.GlobalTransform = farCameraTransform;
		float farCameraFov = Mathf.Clamp(IsFinite(_sourceCamera.Fov) ? _sourceCamera.Fov : 75.0f, 1.0f, 179.0f);
		if (!Mathf.IsEqualApprox(_farCamera.Fov, farCameraFov)) _farCamera.Fov = farCameraFov;
		if (_farCamera.KeepAspect != _sourceCamera.KeepAspect) _farCamera.KeepAspect = _sourceCamera.KeepAspect;
		if (_farCamera.Projection != _sourceCamera.Projection) _farCamera.Projection = _sourceCamera.Projection;
		float farCameraSize = Mathf.Max(0.001f, IsFinite(_sourceCamera.Size) ? _sourceCamera.Size * coordinateScale : 1.0f);
		if (!Mathf.IsEqualApprox(_farCamera.Size, farCameraSize)) _farCamera.Size = farCameraSize;
		Vector2 farCameraFrustumOffset = IsFinite(_sourceCamera.FrustumOffset)
			? _sourceCamera.FrustumOffset * coordinateScale
			: Vector2.Zero;
		if (!_farCamera.FrustumOffset.IsEqualApprox(farCameraFrustumOffset)) _farCamera.FrustumOffset = farCameraFrustumOffset;
		// Keep enough separation between the clip planes for Godot's float
		// frustum math. A 0.001 near plane with the 100,000-unit far plane
		// collapses the far plane and floods RenderingLightCuller with failed
		// three-plane intersections. The whole-ring mesh is already cut out
		// around the viewer, so this does not clip any intended nearby detail.
		float minimumNear = IsFinite(MinimumWholeRingNearClip)
			? Mathf.Max(0.001f, MinimumWholeRingNearClip)
			: 0.1f;
		float near = Mathf.Max(
			minimumNear,
			IsFinite(_sourceCamera.Near) ? _sourceCamera.Near * coordinateScale : minimumNear);
		float sourceFar = IsFinite(_sourceCamera.Far) ? _sourceCamera.Far * coordinateScale : near + 1.0f;
		float ringFar = IsFinite(_farCamera.Far) ? _farCamera.Far : near + 1.0f;
		if (!Mathf.IsEqualApprox(_farCamera.Near, near)) _farCamera.Near = near;
		float farCameraFar = Mathf.Max(
			Mathf.Max(100000.0f, MinimumWholeRingFarClip),
			Mathf.Max(near + 0.001f, Mathf.Max(sourceFar, ringFar)));
		if (!Mathf.IsEqualApprox(_farCamera.Far, farCameraFar)) _farCamera.Far = farCameraFar;
		if (!Mathf.IsZeroApprox(_farCamera.HOffset)) _farCamera.HOffset = 0.0f;
		if (!Mathf.IsZeroApprox(_farCamera.VOffset)) _farCamera.VOffset = 0.0f;

		Vector3 sunDirection = _sourceSun is not null
			? (worldToCanonical * _sourceSun.GlobalTransform.Basis.Z).Normalized()
			: Vector3.Right;
		if (_farSun != null)
		{
			Basis farSunBasis = _sourceSun is not null
				? worldToCanonical * _sourceSun.GlobalBasis
				: Basis.Identity;
			if (_farSun.GlobalBasis != farSunBasis) _farSun.GlobalBasis = farSunBasis;
			float farSunEnergy = _sourceSun?.LightEnergy ?? 3.0f;
			if (!Mathf.IsEqualApprox(_farSun.LightEnergy, farSunEnergy)) _farSun.LightEnergy = farSunEnergy;
		}
		if (_farPlanetLight != null)
		{
			Basis farPlanetBasis = _sourcePlanetLight is not null
				? worldToCanonical * _sourcePlanetLight.GlobalBasis
				: Basis.Identity;
			if (_farPlanetLight.GlobalBasis != farPlanetBasis) _farPlanetLight.GlobalBasis = farPlanetBasis;
			Color farPlanetColor = _sourcePlanetLight?.LightColor
				?? new Color(0.62f, 0.72f, 0.90f);
			if (_farPlanetLight.LightColor != farPlanetColor) _farPlanetLight.LightColor = farPlanetColor;
			float farPlanetEnergy = _sourcePlanetLight?.LightEnergy ?? 0.0f;
			if (!Mathf.IsEqualApprox(_farPlanetLight.LightEnergy, farPlanetEnergy)) _farPlanetLight.LightEnergy = farPlanetEnergy;
			float farPlanetSpecular = _sourcePlanetLight?.LightSpecular ?? 0.45f;
			if (!Mathf.IsEqualApprox(_farPlanetLight.LightSpecular, farPlanetSpecular)) _farPlanetLight.LightSpecular = farPlanetSpecular;
		}
		SyncSharedMaterialUniforms(scaledPosition, sunDirection);
		RingAtmosphereRayFrame wholeRingAtmosphereFrame = ApplyAtmosphereRayFrame(
			eyeCanonical, scaledPosition, sunDirection,
			_terrainMaterial, _waterMaterial, _hullMaterial, _cavityShellMaterial, _cloudMaterial);
		foreach (ShaderMaterial? material in _farCloudClipmapMaterials)
			wholeRingAtmosphereFrame.Apply(material);
		SyncRegionalCameraAndLighting(
			canonical,
			eyeCanonical,
			worldToCanonical,
			sourceTransform,
			farCameraBasis,
			coordinateScale,
			sunDirection);
		UpdateTerrainFallbackCutout(canonical);
		SyncLocalCloudCanopy(canonical, sunDirection);
		SyncFarCloudClipmap(canonical, sunDirection);
		SyncScaledCloudVolume(canonical, sunDirection);
	}

	// The macro masks are generated from main-world light directions, while this
	// viewport shades in canonical scaled-space coordinates. The projector keeps
	// this receiver transform cached, so calling it from the frame sync does not
	// write unchanged uniforms.
	private void SyncTerrainMacroShadowReceiverDirections(Basis worldToCanonical)
	{
		global::RingworldTechDemo.RingTerrainMacroShadowProjector.SetReceiverDirectionTransform(
			_terrainMaterial, worldToCanonical);
		global::RingworldTechDemo.RingTerrainMacroShadowProjector.SetReceiverDirectionTransform(
			_waterMaterial, worldToCanonical);
		foreach (ShaderMaterial material in GetRegionalMaterials())
			global::RingworldTechDemo.RingTerrainMacroShadowProjector.SetReceiverDirectionTransform(material, worldToCanonical);
	}

	private void SyncRegionalCameraAndLighting(
		RingCanonicalPosition canonical,
		RingCanonicalPosition eyeCanonical,
		Basis worldToCanonical,
		Transform3D sourceTransform,
		Basis cameraBasis,
		float coordinateScale,
		Vector3 sunDirection)
	{
		if (_regionalFarCamera == null
			|| _regionalRenderOriginAround == long.MinValue
			|| _regionalRenderOriginAcross == int.MinValue)
			return;

		// Subtract in double precision before creating a Vector3. At the Mega
		// radius, doing this after the float conversion loses kilometres of
		// surface detail and makes the far regional rings visibly swim.
		Vector3 regionalPosition = ScaledPointRelativeForBuild(
			canonical.AroundMeters, canonical.VerticalMeters, canonical.AcrossMeters,
			_regionalRenderOriginAround, 0.0, _regionalRenderOriginAcross,
			_circumferenceMeters, _radiusMeters, Mathf.Max(1, RingWidthMeters), coordinateScale);
		regionalPosition += worldToCanonical * (sourceTransform.Origin - _explorer!.GlobalPosition) * coordinateScale;
		if (!IsFinite(regionalPosition) || !IsUsableBasis(cameraBasis)) return;

		Transform3D regionalCameraTransform = new(cameraBasis, regionalPosition);
		if (_regionalFarCamera.GlobalTransform != regionalCameraTransform) _regionalFarCamera.GlobalTransform = regionalCameraTransform;
		float regionalCameraFov = Mathf.Clamp(IsFinite(_sourceCamera!.Fov) ? _sourceCamera.Fov : 75.0f, 1.0f, 179.0f);
		if (!Mathf.IsEqualApprox(_regionalFarCamera.Fov, regionalCameraFov)) _regionalFarCamera.Fov = regionalCameraFov;
		if (_regionalFarCamera.KeepAspect != _sourceCamera.KeepAspect) _regionalFarCamera.KeepAspect = _sourceCamera.KeepAspect;
		if (_regionalFarCamera.Projection != _sourceCamera.Projection) _regionalFarCamera.Projection = _sourceCamera.Projection;
		float regionalCameraSize = Mathf.Max(0.001f, IsFinite(_sourceCamera.Size) ? _sourceCamera.Size * coordinateScale : 1.0f);
		if (!Mathf.IsEqualApprox(_regionalFarCamera.Size, regionalCameraSize)) _regionalFarCamera.Size = regionalCameraSize;
		Vector2 regionalCameraFrustumOffset = IsFinite(_sourceCamera.FrustumOffset)
			? _sourceCamera.FrustumOffset * coordinateScale
			: Vector2.Zero;
		if (!_regionalFarCamera.FrustumOffset.IsEqualApprox(regionalCameraFrustumOffset)) _regionalFarCamera.FrustumOffset = regionalCameraFrustumOffset;
		float regionalNear = Mathf.Max(0.00001f, IsFinite(_sourceCamera.Near) ? _sourceCamera.Near * coordinateScale : 0.00001f);
		// These tiers begin at 125 km. Raising the scaled near plane close to
		// that handoff dramatically improves depth precision within the 8 Mm
		// regional range without clipping the first far tier.
		regionalNear = Mathf.Max(regionalNear, 125000.0f * coordinateScale * 0.75f);
		if (!Mathf.IsEqualApprox(_regionalFarCamera.Near, regionalNear)) _regionalFarCamera.Near = regionalNear;
		float regionalCameraFar = Mathf.Max(
			7960000.0f * coordinateScale + Mathf.Max(0.01f, 200000.0f * coordinateScale),
			regionalNear + 0.001f);
		if (!Mathf.IsEqualApprox(_regionalFarCamera.Far, regionalCameraFar)) _regionalFarCamera.Far = regionalCameraFar;
		if (!Mathf.IsZeroApprox(_regionalFarCamera.HOffset)) _regionalFarCamera.HOffset = 0.0f;
		if (!Mathf.IsZeroApprox(_regionalFarCamera.VOffset)) _regionalFarCamera.VOffset = 0.0f;

		if (_regionalFarSun != null)
		{
			Basis regionalSunBasis = _sourceSun is not null
				? worldToCanonical * _sourceSun.GlobalBasis
				: Basis.Identity;
			if (_regionalFarSun.GlobalBasis != regionalSunBasis) _regionalFarSun.GlobalBasis = regionalSunBasis;
			float regionalSunEnergy = _sourceSun?.LightEnergy ?? 3.0f;
			if (!Mathf.IsEqualApprox(_regionalFarSun.LightEnergy, regionalSunEnergy)) _regionalFarSun.LightEnergy = regionalSunEnergy;
		}
		if (_regionalFarPlanetLight != null)
		{
			Basis regionalPlanetBasis = _sourcePlanetLight is not null
				? worldToCanonical * _sourcePlanetLight.GlobalBasis
				: Basis.Identity;
			if (_regionalFarPlanetLight.GlobalBasis != regionalPlanetBasis) _regionalFarPlanetLight.GlobalBasis = regionalPlanetBasis;
			Color regionalPlanetColor = _sourcePlanetLight?.LightColor
				?? new Color(0.62f, 0.72f, 0.90f);
			if (_regionalFarPlanetLight.LightColor != regionalPlanetColor) _regionalFarPlanetLight.LightColor = regionalPlanetColor;
			float regionalPlanetEnergy = _sourcePlanetLight?.LightEnergy ?? 0.0f;
			if (!Mathf.IsEqualApprox(_regionalFarPlanetLight.LightEnergy, regionalPlanetEnergy)) _regionalFarPlanetLight.LightEnergy = regionalPlanetEnergy;
			float regionalPlanetSpecular = _sourcePlanetLight?.LightSpecular ?? 0.45f;
			if (!Mathf.IsEqualApprox(_regionalFarPlanetLight.LightSpecular, regionalPlanetSpecular)) _regionalFarPlanetLight.LightSpecular = regionalPlanetSpecular;
		}
		SyncRegionalMaterialUniforms(regionalPosition, sunDirection);
		ApplyAtmosphereRayFrame(eyeCanonical, regionalPosition, sunDirection,
			GetRegionalMaterials().ToArray());
	}

	private RingAtmosphereRayFrame ApplyAtmosphereRayFrame(
		in RingCanonicalPosition canonical,
		Vector3 rendererCameraPosition,
		Vector3 toSun,
		params ShaderMaterial?[] materials)
	{
		if (_radiusMeters <= 0.0 || RingWidthMeters <= 0) return RingAtmosphereRayFrame.Invalid;
		double angle = canonical.AroundMeters / _radiusMeters;
		Vector3 outward = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
		var frame = new RingAtmosphereRayFrame(
			true,
			rendererCameraPosition,
			(float)canonical.VerticalMeters,
			outward,
			Vector3.Up,
			(float)canonical.AcrossMeters,
			(float)((double)RingWidthMeters - canonical.AcrossMeters),
			Mathf.Max(0.000001f, CoordinateScale),
			toSun);
		foreach (ShaderMaterial? material in materials) frame.Apply(material);
		return frame;
	}

	private void SyncSharedMaterialUniforms(Vector3 cameraPosition, Vector3 sunDirection)
	{
		float halfWidth = RingWidthMeters * CoordinateScale * 0.5f;
		if (!_sharedMaterialRingCenterSet)
		{
			SetRingCenter(_terrainMaterial, Vector3.Zero);
			SetRingCenter(_waterMaterial, Vector3.Zero);
			SetRingCenter(_hullMaterial, Vector3.Zero);
			SetRingCenter(_cavityShellMaterial, Vector3.Zero);
			SetRingCenter(_cloudMaterial, Vector3.Zero);
			_sharedMaterialRingCenterSet = true;
		}
		if (!_lastSharedMaterialCameraPosition.IsEqualApprox(cameraPosition))
		{
			SetCameraWorldPosition(_terrainMaterial, cameraPosition);
			SetCameraWorldPosition(_waterMaterial, cameraPosition);
			SetCameraWorldPosition(_hullMaterial, cameraPosition);
			SetCameraWorldPosition(_cavityShellMaterial, cameraPosition);
			SetCameraWorldPosition(_cloudMaterial, cameraPosition);
			_lastSharedMaterialCameraPosition = cameraPosition;
		}
		if (!_lastSharedMaterialSunDirection.IsEqualApprox(sunDirection))
		{
			SetSunDirection(_terrainMaterial, sunDirection);
			SetSunDirection(_waterMaterial, sunDirection);
			SetSunDirection(_hullMaterial, sunDirection);
			SetSunDirection(_cavityShellMaterial, sunDirection);
			SetSunDirection(_cloudMaterial, sunDirection);
			_lastSharedMaterialSunDirection = sunDirection;
		}
		if (!Mathf.IsEqualApprox(_lastSharedMaterialHalfWidth, halfWidth))
		{
			SetAtmosphereHalfWidth(_terrainMaterial, halfWidth);
			SetAtmosphereHalfWidth(_waterMaterial, halfWidth);
			SetAtmosphereHalfWidth(_hullMaterial, halfWidth);
			SetAtmosphereHalfWidth(_cavityShellMaterial, halfWidth);
			SetAtmosphereHalfWidth(_cloudMaterial, halfWidth);
			_lastSharedMaterialHalfWidth = halfWidth;
		}
		if (!Mathf.IsEqualApprox(_lastSharedMaterialShroudVisibility, _farAtmosphereShroudVisibility))
		{
			SetShroudVisibility(_terrainMaterial, _farAtmosphereShroudVisibility);
			SetShroudVisibility(_waterMaterial, _farAtmosphereShroudVisibility);
			SetShroudVisibility(_hullMaterial, _farAtmosphereShroudVisibility);
			SetShroudVisibility(_cavityShellMaterial, _farAtmosphereShroudVisibility);
			SetShroudVisibility(_cloudMaterial, _farAtmosphereShroudVisibility);
			_lastSharedMaterialShroudVisibility = _farAtmosphereShroudVisibility;
		}
		_cloudMaterial?.SetShaderParameter("weather_time_seconds", ResolveFarWeatherAtlasAgeSeconds());
	}

	private void SyncRegionalMaterialUniforms(Vector3 cameraPosition, Vector3 sunDirection)
	{
		if (!_lastRegionalMaterialCameraPosition.IsEqualApprox(cameraPosition))
		{
			foreach (ShaderMaterial material in GetRegionalMaterials()) SetCameraWorldPosition(material, cameraPosition);
			_lastRegionalMaterialCameraPosition = cameraPosition;
		}
		if (!_lastRegionalMaterialRingCenter.IsEqualApprox(_regionalRingCenter))
		{
			foreach (ShaderMaterial material in GetRegionalMaterials()) SetRingCenter(material, _regionalRingCenter);
			_lastRegionalMaterialRingCenter = _regionalRingCenter;
		}
		if (!_lastRegionalMaterialSunDirection.IsEqualApprox(sunDirection))
		{
			foreach (ShaderMaterial material in GetRegionalMaterials()) SetSunDirection(material, sunDirection);
			_lastRegionalMaterialSunDirection = sunDirection;
		}
		if (!Mathf.IsEqualApprox(_lastRegionalMaterialShroudVisibility, _farAtmosphereShroudVisibility))
		{
			foreach (ShaderMaterial material in GetRegionalMaterials()) SetShroudVisibility(material, _farAtmosphereShroudVisibility);
			_lastRegionalMaterialShroudVisibility = _farAtmosphereShroudVisibility;
		}
	}

	private void InvalidateRegionalMaterialUniformCache()
	{
		_lastRegionalMaterialCameraPosition = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
		_lastRegionalMaterialRingCenter = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
		_lastRegionalMaterialSunDirection = new Vector3(float.PositiveInfinity, 0.0f, 0.0f);
		_lastRegionalMaterialShroudVisibility = float.NaN;
	}

	private IEnumerable<ShaderMaterial> GetRegionalMaterials()
	{
		foreach (MeshInstance3D terrain in _farRegionalTerrains)
			if (terrain.MaterialOverride is ShaderMaterial material) yield return material;
		foreach (MeshInstance3D terrain in _farRegionalStagingTerrains)
			if (terrain.MaterialOverride is ShaderMaterial material) yield return material;
	}

	private static void SetCameraWorldPosition(ShaderMaterial? material, Vector3 position) => material?.SetShaderParameter("camera_world_position", position);
	private static void SetRingCenter(ShaderMaterial? material, Vector3 center) => material?.SetShaderParameter("ring_center", center);
	private static void SetSunDirection(ShaderMaterial? material, Vector3 direction) => material?.SetShaderParameter("sun_direction", direction);
	private static void SetAtmosphereHalfWidth(ShaderMaterial? material, float halfWidth) => material?.SetShaderParameter("ring_atmosphere_half_width_meters", halfWidth);
	private static void SetShroudVisibility(ShaderMaterial? material, float visibility) => material?.SetShaderParameter("ring_atmosphere_far_shroud_visibility", visibility);

	private void UpdateFarAtmosphereShroud(in RingCanonicalPosition canonical)
	{
		float atmosphereTop = Mathf.Max(1.0f, RingAtmosphereModel.UpperHeightMeters);
		float fadeLength = Mathf.Clamp(
			FarAtmosphereExitFadeMeters,
			1.0f,
			atmosphereTop);
		float fadeStart = atmosphereTop - fadeLength;
		float altitude = Mathf.Max(0.0f, (float)canonical.VerticalMeters);
		float fadeProgress = Mathf.Clamp(
			(altitude - fadeStart) / fadeLength,
			0.0f,
			1.0f);
		float smoothFade = fadeProgress * fadeProgress * (3.0f - 2.0f * fadeProgress);
		_farAtmosphereShroudVisibility = 1.0f - smoothFade;
	}

	private static bool IsFinite(float value) => float.IsFinite(value);

	private static bool IsFinite(Vector2 value) => IsFinite(value.X) && IsFinite(value.Y);

	private static bool IsFinite(Vector3 value) =>
		IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);

	private static bool IsUsableBasis(Basis basis) =>
		IsFinite(basis.X) && IsFinite(basis.Y) && IsFinite(basis.Z)
		&& Mathf.Abs(basis.Determinant()) > 0.00001f;

	private void UpdateTerrainFallbackCutout(in RingCanonicalPosition canonical)
	{
		if (_terrainMaterial == null || _grid?.TerrainSampler == null) return;
		float baseCutout = (Mathf.Max(0.0f, FadeStartMeters)
			+ Mathf.Max(FadeStartMeters, FadeEndMeters)) * 0.5f;
		float cutoutMeters = baseCutout;
		float altitude = ResolveTerrainAltitude(canonical);
		if (KeepLocalTerrainFallbackVisible)
		{
			float start = Mathf.Max(0.0f, FullLocalCutoutBelowAltitudeMeters);
			float end = Mathf.Max(start + 1.0f, NoLocalCutoutAboveAltitudeMeters);
			float blend = Mathf.Clamp((altitude - start) / (end - start), 0.0f, 1.0f);
			blend = blend * blend * (3.0f - 2.0f * blend);
			cutoutMeters = Mathf.Lerp(baseCutout, 0.0f, blend);
		}
		cutoutMeters = Mathf.Max(
			Mathf.Max(0.0f, MinimumLocalTerrainCutoutMeters),
			cutoutMeters);

		CurrentTerrainFallbackAltitudeMeters = altitude;
		CurrentTerrainFallbackCutoutMeters = cutoutMeters;
		float scaledCutout = cutoutMeters * CoordinateScale;
		float halfTransition = Mathf.Max(0.001f, CoordinateScale);
		float terrainFadeStart = Mathf.Max(0.0f, scaledCutout - halfTransition);
		float terrainFadeEnd = Mathf.Max(halfTransition, scaledCutout + halfTransition);
		if (!Mathf.IsEqualApprox(_lastTerrainFadeStart, terrainFadeStart))
		{
			_terrainMaterial.SetShaderParameter("fade_start_meters", terrainFadeStart);
			_lastTerrainFadeStart = terrainFadeStart;
		}
		if (!Mathf.IsEqualApprox(_lastTerrainFadeEnd, terrainFadeEnd))
		{
			_terrainMaterial.SetShaderParameter("fade_end_meters", terrainFadeEnd);
			_lastTerrainFadeEnd = terrainFadeEnd;
		}
		// Water is safe to keep behind the local world much closer than the
		// terrain fallback. Sharing terrain's large altitude-aware cutout made
		// the ocean visibly terminate at the far-viewport handoff.
		float waterCutout = (CoupleFarWaterCutoutToTerrain
			? cutoutMeters
			: Mathf.Max(0.0f, FarWaterCutoutMeters)) * CoordinateScale;
		float waterFadeStart = Mathf.Max(0.0f, waterCutout - halfTransition);
		float waterFadeEnd = Mathf.Max(halfTransition, waterCutout + halfTransition);
		if (!Mathf.IsEqualApprox(_lastWaterFadeStart, waterFadeStart))
		{
			_waterMaterial?.SetShaderParameter("fade_start_meters", waterFadeStart);
			_lastWaterFadeStart = waterFadeStart;
		}
		if (!Mathf.IsEqualApprox(_lastWaterFadeEnd, waterFadeEnd))
		{
			_waterMaterial?.SetShaderParameter("fade_end_meters", waterFadeEnd);
			_lastWaterFadeEnd = waterFadeEnd;
		}
	}

	private float ResolveTerrainAltitude(in RingCanonicalPosition canonical)
	{
		if (_grid?.TerrainSampler == null) return Mathf.Max(0.0f, (float)canonical.VerticalMeters);
		const int cacheCellMeters = 256;
		long around = (long)Math.Round(canonical.AroundMeters);
		int across = Mathf.Clamp(
			Mathf.RoundToInt((float)canonical.AcrossMeters),
			0,
			Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
		long aroundCell = (long)Math.Floor(around / (double)cacheCellMeters);
		int acrossCell = Mathf.FloorToInt(across / (float)cacheCellMeters);
		ulong editVersion = _grid.LodSurfaceEditVersion;
		if (aroundCell != _lastFallbackAroundCell
			|| acrossCell != _lastFallbackAcrossCell
			|| editVersion != _lastFallbackEditVersion)
		{
			RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
			_cachedFallbackSurfaceHeight = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
				? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
				: edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
			_lastFallbackAroundCell = aroundCell;
			_lastFallbackAcrossCell = acrossCell;
			_lastFallbackEditVersion = editVersion;
		}
		return Mathf.Max(0.0f, (float)(canonical.VerticalMeters - _cachedFallbackSurfaceHeight));
	}

	private void SyncLocalCloudCanopy(in RingCanonicalPosition canonical, Vector3 sunDirection)
	{
		if (_localCloudCanopy == null || _localCloudCanopyMaterial == null) return;
		double wrappedAround = canonical.AroundMeters % _circumferenceMeters;
		if (wrappedAround < 0.0) wrappedAround += _circumferenceMeters;
		double angle = wrappedAround / _radiusMeters;
		Vector3 radialOut = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
		Vector3 tangent = new(-radialOut.Z, 0.0f, radialOut.X);
		Vector3 inward = -radialOut;
		Vector3 across = Vector3.Up;
		double canopyHeight = Mathf.Lerp(LocalCloudBaseMeters, LocalCloudTopMeters, 0.62f);
		_localCloudCanopy.GlobalTransform = new Transform3D(
			new Basis(tangent, inward, across),
			ScaledPoint(canonical.AroundMeters, canopyHeight, canonical.AcrossMeters));
		_localCloudCanopyMaterial.SetShaderParameter("canonical_origin_km", new Vector2(
			(float)(wrappedAround * 0.001),
			(float)(canonical.AcrossMeters * 0.001)));
		_localCloudCanopyMaterial.SetShaderParameter("viewer_height_meters", (float)canonical.VerticalMeters);
		_localCloudCanopyMaterial.SetShaderParameter("surface_up", inward);
		_localCloudCanopyMaterial.SetShaderParameter("sun_direction", sunDirection);
	}

	private void SyncScaledCloudVolume(in RingCanonicalPosition canonical, Vector3 sunDirection)
	{
		if (_scaledCloudVolume == null || _scaledCloudVolumeMaterial == null) return;
		double wrappedAround = canonical.AroundMeters % _circumferenceMeters;
		if (wrappedAround < 0.0) wrappedAround += _circumferenceMeters;
		double angle = wrappedAround / _radiusMeters;
		Vector3 radialOut = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
		Vector3 tangent = new(-radialOut.Z, 0.0f, radialOut.X);
		Vector3 inward = -radialOut;
		Vector3 across = Vector3.Up;
		double centerHeight = (LocalCloudBaseMeters + LocalCloudTopMeters) * 0.5;
		_scaledCloudVolume.GlobalTransform = new Transform3D(
			new Basis(tangent, inward, across),
			ScaledPoint(canonical.AroundMeters, centerHeight, canonical.AcrossMeters));
		_scaledCloudVolumeMaterial.SetShaderParameter("canonical_origin_km", new Vector2(
			(float)(wrappedAround * 0.001),
			(float)(canonical.AcrossMeters * 0.001)));
		_scaledCloudVolumeMaterial.SetShaderParameter("surface_up", inward);
		_scaledCloudVolumeMaterial.SetShaderParameter("sun_direction", sunDirection);
	}

	/// <summary>Stops the canvas backdrop from retaining a compositor-owned Texture2DRD.</summary>
	public void ReleaseSunshineBackdropTexture()
	{
		ExactSunshineBackdropActive = false;
		_sunshineBackdropReadyReported = false;
		_sunshineBackdropTexture = null;
		if (_backdropCloudCompositeMaterial != null)
		{
			_backdropCloudCompositeMaterial.SetShaderParameter("exact_sunshine_clouds_enabled", false);
			if (_cloudMaskViewports[0] != null)
				_backdropCloudCompositeMaterial.SetShaderParameter(
					"sunshine_cloud_texture", _cloudMaskViewports[0]!.GetTexture());
		}
	}

	private void UpdateCloudTransmittanceMask(double delta)
	{
		bool resourcesReady = _backdropCloudCompositeMaterial != null
			&& _cloudMaskViewports[0] != null
			&& _cloudMaskViewports[1] != null
			&& _cloudMaskMaterials[0] != null
			&& _cloudMaskMaterials[1] != null;
		bool legacyVisible = _localVolumetricClouds?.LocalVolumeVisible == true;
		bool sunshineVisible = _sunshineClouds?.RuntimeVisible == true;
		bool active = resourcesReady && ShouldCompositeCloudBackdrop(
			CloudTransmittanceMaskEnabled,
			legacyVisible,
			sunshineVisible);
		CloudMaskActive = active;
		_backdropCloudCompositeMaterial?.SetShaderParameter("cloud_mask_enabled", active);
		if (!active)
		{
			ExactSunshineBackdropActive = false;
			_backdropCloudCompositeMaterial?.SetShaderParameter("exact_sunshine_clouds_enabled", false);
			if (_sunshineBackdropTexture != null && _cloudMaskViewports[0] != null)
			{
				_backdropCloudCompositeMaterial?.SetShaderParameter(
					"sunshine_cloud_texture",
					_cloudMaskViewports[0]!.GetTexture());
				_sunshineBackdropTexture = null;
			}
			_cloudMaskInitialized = false;
			_cloudMaskBlending = false;
			_cloudMaskUpdateElapsed = double.PositiveInfinity;
			return;
		}

		Texture2D? sunshineBackdrop = sunshineVisible
			? _sunshineClouds?.GetCloudBackdropTexture()
			: null;
		if (sunshineBackdrop != null && !GodotObject.IsInstanceValid(sunshineBackdrop))
			sunshineBackdrop = null;
		bool useExactSunshineBackdrop = sunshineBackdrop != null;
		ExactSunshineBackdropActive = useExactSunshineBackdrop;
		_backdropCloudCompositeMaterial!.SetShaderParameter(
			"exact_sunshine_clouds_enabled",
			useExactSunshineBackdrop);
		if (useExactSunshineBackdrop)
		{
			if (_sunshineBackdropTexture != sunshineBackdrop)
			{
				_sunshineBackdropTexture = sunshineBackdrop;
				_backdropCloudCompositeMaterial.SetShaderParameter(
					"sunshine_cloud_texture",
					sunshineBackdrop!);
			}
			// The exact accumulation already contains Sunshine's temporal
			// history. Stop the duplicate proxy raymarch while it is bound.
			_cloudMaskInitialized = false;
			_cloudMaskBlending = false;
			_cloudMaskUpdateElapsed = double.PositiveInfinity;
			if (!_sunshineBackdropReadyReported)
			{
				GD.Print("Far backdrop uses Sunshine's exact cloud color/density texture.");
				_sunshineBackdropReadyReported = true;
			}
			return;
		}

		if (_sunshineBackdropTexture != null)
		{
			_sunshineBackdropTexture = null;
			_backdropCloudCompositeMaterial.SetShaderParameter(
				"sunshine_cloud_texture",
				_cloudMaskViewports[0]!.GetTexture());
		}

		if (!_cloudMaskInitialized)
		{
			for (int index = 0; index < 2; index++)
			{
				UpdateCloudMaskMaterial(_cloudMaskMaterials[index]!);
				_cloudMaskViewports[index]!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
			}
			_cloudMaskFrontIndex = 1;
			_cloudMaskTargetIndex = 0;
			_backdropCloudCompositeMaterial!.SetShaderParameter(
				"previous_cloud_mask",
				_cloudMaskViewports[0]!.GetTexture());
			_backdropCloudCompositeMaterial.SetShaderParameter(
				"current_cloud_mask",
				_cloudMaskViewports[1]!.GetTexture());
			_backdropCloudCompositeMaterial.SetShaderParameter("temporal_blend", 1.0f);
			_cloudMaskInitialized = true;
			_cloudMaskBlending = false;
			_cloudMaskUpdateElapsed = 0.0;
			CloudMaskUpdateCount += 2;
			if (!_cloudMaskReadyReported)
			{
				GD.Print(
					$"Far backdrop cloud mask active: {CloudMaskSize.X}x{CloudMaskSize.Y}, "
					+ $"{CloudMaskUpdatesPerSecond:0}Hz, {CloudMaskRaymarchSteps} steps, "
					+ $"quality<={CloudMaskMaximumQualityLevel}; no second FogVolume.");
				_cloudMaskReadyReported = true;
			}
			return;
		}

		_cloudMaskUpdateElapsed += Math.Max(0.0, delta);
		if (_cloudMaskBlending)
		{
			_cloudMaskBlendElapsed += Math.Max(0.0, delta);
			float duration = Mathf.Max(0.01f, CloudMaskTemporalBlendSeconds);
			float blend = Mathf.Clamp((float)(_cloudMaskBlendElapsed / duration), 0.0f, 1.0f);
			_backdropCloudCompositeMaterial!.SetShaderParameter("temporal_blend", blend);
			if (blend >= 1.0f)
			{
				_cloudMaskFrontIndex = _cloudMaskTargetIndex;
				_cloudMaskBlending = false;
			}
		}

		double updateInterval = 1.0 / Math.Max(1.0f, CloudMaskUpdatesPerSecond);
		if (_cloudMaskBlending || _cloudMaskUpdateElapsed < updateInterval) return;
		_cloudMaskTargetIndex = 1 - _cloudMaskFrontIndex;
		UpdateCloudMaskMaterial(_cloudMaskMaterials[_cloudMaskTargetIndex]!);
		_cloudMaskViewports[_cloudMaskTargetIndex]!.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
		_backdropCloudCompositeMaterial!.SetShaderParameter(
			"previous_cloud_mask",
			_cloudMaskViewports[_cloudMaskFrontIndex]!.GetTexture());
		_backdropCloudCompositeMaterial.SetShaderParameter(
			"current_cloud_mask",
			_cloudMaskViewports[_cloudMaskTargetIndex]!.GetTexture());
		_backdropCloudCompositeMaterial.SetShaderParameter("temporal_blend", 0.0f);
		_cloudMaskBlendElapsed = 0.0;
		_cloudMaskUpdateElapsed = 0.0;
		_cloudMaskBlending = true;
		CloudMaskUpdateCount++;
	}

	private void UpdateCloudMaskMaterial(ShaderMaterial material)
	{
		if (_grid == null
			|| _sourceCamera == null
			|| _sourceSun == null
			|| _explorer == null
			|| _weatherSource == null)
		{
			return;
		}

		// This is a short-lived fallback while Sunshine's exact RD texture is
		// starting or rebuilding. Do not read stale legacy handoff/altitude
		// values when Sunshine owns the local cloud image.
		bool legacyVolumeVisible = _localVolumetricClouds?.LocalVolumeVisible == true;
		bool sunshineVisible = _sunshineClouds?.RuntimeVisible == true;
		RingVolumetricCloudLayer.LocalWeatherState weather;
		Vector3 volumeCenterKm;
		Vector3 volumeSizeMeters;
		float cloudBaseFraction;
		float cloudTopFraction;
		int shaderQuality;
		float visibility;
		float densityScale = 0.048f;
		float edgeSoftness = 0.075f;
		float handoffStart = 36_000.0f;
		float handoffEnd = 48_000.0f;
		Color sunlitColor = new(0.91f, 0.95f, 1.0f);
		Color shadowColor = new(0.24f, 0.31f, 0.42f);
		Color stormColor = new(0.12f, 0.16f, 0.23f);
		Color nightColor = new(0.035f, 0.05f, 0.085f);

		if (legacyVolumeVisible && _localVolumetricClouds != null)
		{
			weather = _localVolumetricClouds.CurrentWeather;
			volumeCenterKm = _localVolumetricClouds.VolumeCanonicalCenterKm;
			volumeSizeMeters = _localVolumetricClouds.VolumeSizeMeters;
			cloudBaseFraction = _localVolumetricClouds.CurrentCloudBaseFraction;
			cloudTopFraction = _localVolumetricClouds.CurrentCloudTopFraction;
			shaderQuality = _localVolumetricClouds.CurrentShaderQualityLevel;
			visibility = _localVolumetricClouds.CurrentVisibility;
			densityScale = _localVolumetricClouds.DensityScale;
			edgeSoftness = _localVolumetricClouds.EdgeSoftness;
			handoffStart = _localVolumetricClouds.FarShellHandoffStartMeters;
			handoffEnd = _localVolumetricClouds.FarShellHandoffEndMeters;
			sunlitColor = _localVolumetricClouds.SunlitColor;
			shadowColor = _localVolumetricClouds.ShadowColor;
			stormColor = _localVolumetricClouds.StormColor;
			nightColor = _localVolumetricClouds.NightColor;
		}
		else if (sunshineVisible && _sunshineClouds != null)
		{
			weather = _sunshineClouds.CurrentWeather;
			Vector2 altitude = _sunshineClouds.CloudTraversalAltitudeMeters;
			float cloudBase = Math.Min(altitude.X, altitude.Y);
			float cloudTop = Math.Max(cloudBase + 500.0f, altitude.Y);
			float volumeHeight = cloudTop - cloudBase;
			handoffStart = _sunshineClouds.HandoffStartMeters;
			handoffEnd = _sunshineClouds.HandoffEndMeters;
			float radius = Math.Max(2_000.0f, handoffEnd);
			volumeCenterKm = new Vector3(
				(float)(_explorer.CanonicalPosition.AroundMeters * 0.001),
				(cloudBase + cloudTop) * 0.0005f,
				(float)(_explorer.CanonicalPosition.AcrossMeters * 0.001));
			volumeSizeMeters = new Vector3(radius * 2.0f, volumeHeight, radius * 2.0f);
			cloudBaseFraction = 0.0f;
			cloudTopFraction = 1.0f;
			shaderQuality = _weatherSource.Quality switch
			{
				RingVolumetricCloudLayer.CloudQuality.Ultra => 3,
				RingVolumetricCloudLayer.CloudQuality.VeryHigh => 2,
				_ => 1
			};
			visibility = 1.0f;
			sunlitColor = _sunshineClouds.DayCloudAmbientColor;
			shadowColor = _sunshineClouds.NightCloudAmbientTint;
			stormColor = _sunshineClouds.StormCloudAmbientColor;
			nightColor = _sunshineClouds.NightCloudAmbientColor;
		}
		else
		{
			// Active policy normally makes this unreachable, but retain a
			// deterministic fallback for editor-time provider changes.
			weather = _weatherSource.QueryLocalWeather(_explorer.CanonicalPosition);
			float cloudBase = weather.CloudBaseAltitudeMeters >= 0.0f
				? weather.CloudBaseAltitudeMeters
				: _weatherSource.CloudBaseMeters;
			float cloudTop = Math.Max(cloudBase + 500.0f, weather.CloudTopAltitudeMeters);
			float volumeTop = Math.Max(20_000.0f, cloudTop + 1_000.0f);
			float radius = Math.Max(2_000.0f, handoffEnd);
			volumeCenterKm = new Vector3(
				(float)(_explorer.CanonicalPosition.AroundMeters * 0.001),
				volumeTop * 0.0005f,
				(float)(_explorer.CanonicalPosition.AcrossMeters * 0.001));
			volumeSizeMeters = new Vector3(radius * 2.0f, volumeTop, radius * 2.0f);
			cloudBaseFraction = Mathf.Clamp(cloudBase / volumeTop, 0.0f, 0.92f);
			cloudTopFraction = Mathf.Clamp(cloudTop / volumeTop, cloudBaseFraction + 0.02f, 1.0f);
			shaderQuality = 1;
			visibility = 1.0f;
		}
		Vector3 volumeCenterMeters = volumeCenterKm * 1000.0f;
		RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
		Vector3 centerWorld = geometry.CanonicalToWorld(
			volumeCenterMeters.X,
			volumeCenterMeters.Y,
			volumeCenterMeters.Z);
		Vector3 aroundAxis = (geometry.CanonicalToWorld(
			volumeCenterMeters.X + 10.0,
			volumeCenterMeters.Y,
			volumeCenterMeters.Z) - centerWorld).Normalized();
		Vector3 verticalAxis = (geometry.CanonicalToWorld(
			volumeCenterMeters.X,
			volumeCenterMeters.Y + 10.0,
			volumeCenterMeters.Z) - centerWorld).Normalized();
		Vector3 acrossAxis = (geometry.CanonicalToWorld(
			volumeCenterMeters.X,
			volumeCenterMeters.Y,
			volumeCenterMeters.Z + 10.0) - centerWorld).Normalized();

		Transform3D cameraTransform = _sourceCamera.GetCameraTransform();
		Vector3 cameraOffsetWorld = cameraTransform.Origin - centerWorld;
		Vector3 cameraCanonicalKm = volumeCenterKm + new Vector3(
			cameraOffsetWorld.Dot(aroundAxis),
			cameraOffsetWorld.Dot(verticalAxis),
			cameraOffsetWorld.Dot(acrossAxis)) * 0.001f;
		Vector3 right = WorldDirectionToCanonical(cameraTransform.Basis.X, aroundAxis, verticalAxis, acrossAxis);
		Vector3 up = WorldDirectionToCanonical(cameraTransform.Basis.Y, aroundAxis, verticalAxis, acrossAxis);
		Vector3 forward = WorldDirectionToCanonical(-cameraTransform.Basis.Z, aroundAxis, verticalAxis, acrossAxis);
		Vector3 sunDirection = WorldDirectionToCanonical(
			_sourceSun.GlobalTransform.Basis.Z,
			aroundAxis,
			verticalAxis,
			acrossAxis);
		Vector2 visibleSize = GetViewport().GetVisibleRect().Size;
		float aspect = visibleSize.Y > 0.0f ? visibleSize.X / visibleSize.Y : 1.0f;
		material.SetShaderParameter("camera_canonical_km", cameraCanonicalKm);
		material.SetShaderParameter("camera_right_canonical", right);
		material.SetShaderParameter("camera_up_canonical", up);
		material.SetShaderParameter("camera_forward_canonical", forward);
		material.SetShaderParameter("camera_tan_half_fov", Mathf.Tan(Mathf.DegToRad(_sourceCamera.Fov * 0.5f)));
		material.SetShaderParameter("viewport_aspect", Mathf.Max(0.1f, aspect));
		material.SetShaderParameter("volume_center_canonical_km", volumeCenterKm);
		material.SetShaderParameter("volume_size_meters", volumeSizeMeters);
		material.SetShaderParameter("ring_circumference_km", (float)(_circumferenceMeters * 0.001));
		material.SetShaderParameter("ring_width_km", (float)(_grid.RingAcrossWidthMeters * 0.001));
		material.SetShaderParameter("cloud_seed", _grid.TerrainSampler?.Seed ?? 1337);
		material.SetShaderParameter("wind_km_per_second", weather.WindMetersPerSecond * 0.001f);
		material.SetShaderParameter("weather_time_seconds", (float)(Time.GetTicksMsec() * 0.001));
		material.SetShaderParameter("coverage", Mathf.Clamp(weather.CloudCoverage, 0.0f, 1.0f));
		material.SetShaderParameter("precipitation", Mathf.Clamp(weather.PrecipitationPotential, 0.0f, 1.0f));
		material.SetShaderParameter("storm_influence", Mathf.Clamp(weather.SystemInfluence, 0.0f, 1.0f));
		material.SetShaderParameter(
			"formation_profile",
			CloudFormationProfiles.ToShaderProfile(weather.DominantFormationProfile));
		material.SetShaderParameter("cloud_base_fraction", cloudBaseFraction);
		material.SetShaderParameter("cloud_top_fraction", cloudTopFraction);
		material.SetShaderParameter("density_scale", Mathf.Max(0.001f, densityScale));
		material.SetShaderParameter("softness", Mathf.Clamp(edgeSoftness, 0.01f, 0.3f));
		material.SetShaderParameter("visibility", visibility);
		material.SetShaderParameter("handoff_start_meters", Mathf.Max(0.0f, handoffStart));
		material.SetShaderParameter("handoff_end_meters", Mathf.Max(
			handoffStart + 1.0f,
			handoffEnd));
		material.SetShaderParameter("quality_level", Mathf.Min(
			Mathf.Clamp(CloudMaskMaximumQualityLevel, 1, 3),
			shaderQuality));
		material.SetShaderParameter("raymarch_steps", Mathf.Clamp(CloudMaskRaymarchSteps, 4, 24));
		material.SetShaderParameter("extinction_per_kilometer", Mathf.Max(0.01f, CloudMaskExtinctionPerKilometer));
		material.SetShaderParameter("opacity_scale", Mathf.Clamp(CloudMaskOpacityScale, 0.0f, 2.0f));
		material.SetShaderParameter("sun_direction_canonical", sunDirection);
		material.SetShaderParameter("sunlit_color", ColorToVector3(sunlitColor));
		material.SetShaderParameter("shadow_color", ColorToVector3(shadowColor));
		material.SetShaderParameter("storm_color", ColorToVector3(stormColor));
		material.SetShaderParameter("night_color", ColorToVector3(nightColor));
	}

	private static Vector3 WorldDirectionToCanonical(
		Vector3 direction,
		Vector3 aroundAxis,
		Vector3 verticalAxis,
		Vector3 acrossAxis)
	{
		Vector3 normalized = direction.Normalized();
		return new Vector3(
			normalized.Dot(aroundAxis),
			normalized.Dot(verticalAxis),
			normalized.Dot(acrossAxis)).Normalized();
	}

	private static Vector3 ColorToVector3(Color color)
		=> new(color.R, color.G, color.B);

	private void UpdateViewportSize(bool force)
	{
		if (_farViewport == null && _regionalFarViewport == null) return;
		Vector2 rootSize = GetViewport().GetVisibleRect().Size;
		var size = new Vector2I(
			Mathf.Max(2, Mathf.RoundToInt(rootSize.X * ResolutionScale)),
			Mathf.Max(2, Mathf.RoundToInt(rootSize.Y * ResolutionScale)));
		if (!force && size == _lastRootSize) return;
		_lastRootSize = size;
		if (_farViewport != null) _farViewport.Size = size;
		if (_regionalFarViewport != null) _regionalFarViewport.Size = size;
		float maskScale = Mathf.Clamp(CloudMaskResolutionScale, 0.25f, 1.0f);
		CloudMaskSize = new Vector2I(
			Mathf.Max(2, Mathf.RoundToInt(rootSize.X * maskScale)),
			Mathf.Max(2, Mathf.RoundToInt(rootSize.Y * maskScale)));
		foreach (SubViewport? maskViewport in _cloudMaskViewports)
		{
			if (maskViewport != null) maskViewport.Size = CloudMaskSize;
		}
		_cloudMaskInitialized = false;
		_cloudMaskUpdateElapsed = double.PositiveInfinity;
	}

	public void ApplyRuntimeRenderQuality(
		float resolutionScale,
		int aroundSegments,
		int acrossSegments)
	{
		float nextScale = Mathf.Clamp(resolutionScale, 0.25f, 1.0f);
		int nextAround = Mathf.Clamp(aroundSegments, 128, 4096);
		int nextAcross = Mathf.Clamp(acrossSegments, 4, 128);
		bool geometryChanged = AroundSegments != nextAround || AcrossSegments != nextAcross;
		ResolutionScale = nextScale;
		AroundSegments = nextAround;
		AcrossSegments = nextAcross;

		if (!Enabled || !IsNodeReady() || _farViewport == null) return;
		UpdateViewportSize(force: true);
		if (!geometryChanged) return;

		RemoveFarViewportChild("ScaledSeededRingSurface");
		RemoveFarViewportChild("ScaledWaterSurface");
		RemoveFarViewportChild("ScaledStructuralShell");
		RemoveFarViewportChild("ScaledCloudShell");
		BuildSeededSurface();
		BuildStructuralShell();
		BuildCloudShell();
		GD.Print(
			$"Scaled far viewport quality rebuilt: {AroundSegments}x{AcrossSegments}, "
			+ $"resolutionScale={ResolutionScale:0.00}.");
	}

	private void RemoveFarViewportChild(string name)
	{
		Node? child = _farViewport?.GetNodeOrNull<Node>(name);
		if (child == null) return;
		_farViewport!.RemoveChild(child);
		child.QueueFree();
	}

	public void SetLocalVolumetricCloudsEnabled(bool enabled)
	{
		// Retained for save/runtime compatibility. The mesh-cloud system owns
		// the local field in the main 3D viewport; scaled space keeps only its
		// seeded far shell, so no duplicated local fog can cover the ring.
		LocalVolumetricCloudsEnabled = false;
		if (_scaledCloudVolume != null) _scaledCloudVolume.Visible = false;
		if (_localCloudCanopy != null) _localCloudCanopy.Visible = false;
		if (_farEnvironment != null) _farEnvironment.VolumetricFogEnabled = false;
	}

	public void SetFallbackCloudShellEnabled(bool enabled)
	{
		if (_scaledCloudShell != null) _scaledCloudShell.Visible = enabled;
		// Sunshine owns only the near volume. The far clipmap and global shell
		// have complementary fades, so both remain present when enabled.
		foreach (MeshInstance3D? layer in _farCloudClipmapLayers)
			if (layer != null) layer.Visible = FarCloudClipmapEnabled;
	}

	public void ConfigureMeshCloudOwnership(
		bool enabled,
		float radiusMeters,
		float cloudBaseMeters,
		float cloudTopMeters,
		float handoffStartMeters = -1.0f)
	{
		LocalCloudRadiusMeters = Mathf.Max(1000.0f, radiusMeters);
		LocalCloudBaseMeters = cloudBaseMeters;
		LocalCloudTopMeters = Mathf.Max(cloudBaseMeters + 100.0f, cloudTopMeters);
		float resolvedHandoffStart = handoffStartMeters > 0.0f
			? handoffStartMeters
			: LocalCloudRadiusMeters * 0.76f;
		resolvedHandoffStart = Mathf.Clamp(
			resolvedHandoffStart,
			1000.0f,
			GetEffectiveFarCloudClipmapRadiusMeters() - 1000.0f);
		if (!Mathf.IsEqualApprox(FarCloudClipmapHandoffStartMeters, resolvedHandoffStart))
		{
			FarCloudClipmapHandoffStartMeters = resolvedHandoffStart;
			RebuildFarCloudClipmapMeshes();
		}
		float scaledRadius = LocalCloudRadiusMeters * CoordinateScale;
		float scaledHandoffStart = Mathf.Min(resolvedHandoffStart, LocalCloudRadiusMeters - 1.0f) * CoordinateScale;
		if (_cloudMaterial != null)
		{
			_cloudMaterial.SetShaderParameter(
				"local_volumetric_handoff_enabled",
				enabled);
			_cloudMaterial.SetShaderParameter(
				"local_handoff_start_meters",
				scaledHandoffStart);
			_cloudMaterial.SetShaderParameter(
				"local_handoff_end_meters",
				scaledRadius);
		}
		SetLocalVolumetricCloudsEnabled(false);
	}

	/// <summary>
	/// Keeps the cloud field rendered with the far ring identical to the local
	/// field. The two viewports cannot share depth, so every cloud that can
	/// overlap the far-ring backdrop must also exist in this viewport.
	/// </summary>
	public void ConfigureCloudOwnership(
		bool enabled,
		float radiusMeters,
		float cloudBaseMeters,
		float cloudTopMeters,
		float coverage,
		float density,
		float softness,
		float canopyCoverage,
		float canopyOpacity,
		int qualityLevel)
	{
		LocalCloudRadiusMeters = Mathf.Max(1000.0f, radiusMeters);
		LocalCloudBaseMeters = cloudBaseMeters;
		LocalCloudTopMeters = Mathf.Max(cloudBaseMeters + 100.0f, cloudTopMeters);

		float scaledRadius = LocalCloudRadiusMeters * CoordinateScale;
		if (_scaledCloudVolume != null)
		{
			_scaledCloudVolume.Size = new Vector3(
				scaledRadius * 2.0f,
				(LocalCloudTopMeters - LocalCloudBaseMeters) * CoordinateScale,
				scaledRadius * 2.0f);
		}
		BuildLocalCloudCanopyMesh(scaledRadius);

		if (_cloudMaterial != null)
		{
			_cloudMaterial.SetShaderParameter("local_handoff_start_meters", scaledRadius * 0.76f);
			_cloudMaterial.SetShaderParameter("local_handoff_end_meters", scaledRadius);
		}
		if (_localCloudCanopyMaterial != null)
		{
			_localCloudCanopyMaterial.SetShaderParameter("patch_size_km", new Vector2(
				LocalCloudRadiusMeters * 0.002f,
				LocalCloudRadiusMeters * 0.002f));
			_localCloudCanopyMaterial.SetShaderParameter("cloud_base_meters", LocalCloudBaseMeters);
			_localCloudCanopyMaterial.SetShaderParameter("cloud_top_meters", LocalCloudTopMeters);
			_localCloudCanopyMaterial.SetShaderParameter("coverage", canopyCoverage);
			_localCloudCanopyMaterial.SetShaderParameter("opacity", canopyOpacity);
		}
		if (_scaledCloudVolumeMaterial != null)
		{
			_scaledCloudVolumeMaterial.SetShaderParameter("coverage", coverage);
			_scaledCloudVolumeMaterial.SetShaderParameter("density_scale", density);
			_scaledCloudVolumeMaterial.SetShaderParameter("softness", softness);
			_scaledCloudVolumeMaterial.SetShaderParameter("quality_level", Mathf.Clamp(qualityLevel, 1, 3));
		}

		if (_farEnvironment != null)
			_farEnvironment.VolumetricFogLength = Mathf.Max(24.0f, LocalCloudRadiusMeters * 1.2f * CoordinateScale);
		SetLocalVolumetricCloudsEnabled(enabled);
	}

	private void BuildLocalCloudCanopyMesh(float scaledRadius)
	{
		if (_localCloudCanopy == null) return;
		var surface = new SurfaceTool();
		surface.Begin(Mesh.PrimitiveType.Triangles);
		AddUvVertex(surface, new Vector3(-scaledRadius, 0.0f, -scaledRadius), new Vector2(0.0f, 0.0f));
		AddUvVertex(surface, new Vector3(scaledRadius, 0.0f, -scaledRadius), new Vector2(1.0f, 0.0f));
		AddUvVertex(surface, new Vector3(-scaledRadius, 0.0f, scaledRadius), new Vector2(0.0f, 1.0f));
		AddUvVertex(surface, new Vector3(scaledRadius, 0.0f, -scaledRadius), new Vector2(1.0f, 0.0f));
		AddUvVertex(surface, new Vector3(scaledRadius, 0.0f, scaledRadius), new Vector2(1.0f, 1.0f));
		AddUvVertex(surface, new Vector3(-scaledRadius, 0.0f, scaledRadius), new Vector2(0.0f, 1.0f));
		_localCloudCanopy.Mesh = surface.Commit();
	}

	private Vector3 ScaledPoint(double around, double vertical, double across)
	{
		double wrapped = around % _circumferenceMeters;
		if (wrapped < 0.0) wrapped += _circumferenceMeters;
		double angle = wrapped / _radiusMeters;
		double radial = _radiusMeters - vertical;
		double acrossBase = -RingWidthMeters * 0.5;
		return new Vector3(
			(float)(Math.Cos(angle) * radial * CoordinateScale),
			(float)((acrossBase + across) * CoordinateScale),
			(float)(Math.Sin(angle) * radial * CoordinateScale));
	}

	private void AddCloudVertex(SurfaceTool surface, int x, int z, int aroundCount, int acrossCount, double aroundStep, double acrossStep)
	{
		surface.SetUV(new Vector2(x / (float)aroundCount, z / (float)acrossCount));
		surface.AddVertex(ScaledPoint(x * aroundStep, CloudAltitudeMeters, z * acrossStep));
	}

	private void AddWall(SurfaceTool surface, double a0, double a1, double across, double bottom, double top, bool reverse)
	{
		Vector3 p0 = ScaledPoint(a0, bottom, across), p1 = ScaledPoint(a1, bottom, across);
		Vector3 p2 = ScaledPoint(a1, top, across), p3 = ScaledPoint(a0, top, across);
		if (reverse)
		{
			AddTriangle(surface, p0, p1, p3, HullColor);
			AddTriangle(surface, p1, p2, p3, HullColor);
		}
		else
		{
			AddTriangle(surface, p3, p1, p0, HullColor);
			AddTriangle(surface, p1, p3, p2, HullColor);
		}
	}

	private static void AddTriangle(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c, Color color)
	{
		surface.SetColor(color); surface.AddVertex(a);
		surface.SetColor(color); surface.AddVertex(b);
		surface.SetColor(color); surface.AddVertex(c);
	}

	private static void AddTriangle(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c, Color ca, Color cb, Color cc)
	{
		surface.SetColor(ca); surface.AddVertex(a);
		surface.SetColor(cb); surface.AddVertex(b);
		surface.SetColor(cc); surface.AddVertex(c);
	}

	private static bool IsWaterTriangle(bool[] waterVertices, int a, int b, int c) =>
		(waterVertices[a] ? 1 : 0) + (waterVertices[b] ? 1 : 0) + (waterVertices[c] ? 1 : 0) >= 2;

	private static void AddUvVertex(SurfaceTool surface, Vector3 position, Vector2 uv)
	{
		surface.SetUV(uv);
		surface.AddVertex(position);
	}

	public void SetBenchmarkCloudLoad(bool enabled)
	{
		if (_cloudMaterial == null) return;
		_cloudMaterial.SetShaderParameter("coverage", enabled ? 0.22f : FarCloudCoverage);
		_cloudMaterial.SetShaderParameter("opacity", enabled ? 0.88f : FarCloudOpacity);
		_cloudMaterial.SetShaderParameter("softness", enabled ? 0.24f : FarCloudSoftness);
	}
}
