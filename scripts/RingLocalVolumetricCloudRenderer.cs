using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Camera-local FogVolume cloud tier driven by the deterministic macro-weather
/// field. The curved shell remains responsible for distant and fast-travel
/// clouds; the legacy merged meshes remain available as a reversible fallback.
/// </summary>
public partial class RingLocalVolumetricCloudRenderer : Node3D
{
    [Export] public bool Enabled { get; set; } = false;
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");

    [ExportGroup("Local volume")]
    [Export(PropertyHint.Range, "2000,30000,250")] public float HorizontalRadiusMeters { get; set; } = 12000.0f;
    [Export(PropertyHint.Range, "1000,30000,250")] public float FarShellHandoffStartMeters { get; set; } = 9000.0f;
    [Export(PropertyHint.Range, "2000,40000,250")] public float FarShellHandoffEndMeters { get; set; } = 12000.0f;
    [Export(PropertyHint.Range, "-5000,10000,50")] public float FixedCloudAltitudeDatumMeters { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "0,5000,50")] public float VolumeMinimumAltitudeMeters { get; set; } = 0.0f;
    [Export(PropertyHint.Range, "4000,25000,250")] public float VolumeMaximumAltitudeMeters { get; set; } = 20000.0f;
    [Export(PropertyHint.Range, "64,2048,64")] public float CanonicalSnapMeters { get; set; } = 512.0f;
    [Export(PropertyHint.Range, "0.1,5.0,0.1")] public float WeatherRefreshSeconds { get; set; } = 0.5f;

    [ExportGroup("Cloud density")]
    [Export(PropertyHint.Range, "0.001,0.15,0.001")] public float DensityScale { get; set; } = 0.048f;
    [Export(PropertyHint.Range, "0.01,0.3,0.005")] public float EdgeSoftness { get; set; } = 0.075f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float AmbientLightInjection { get; set; } = 0.18f;
    [Export(PropertyHint.Range, "-0.9,0.9,0.01")] public float ScatteringAnisotropy { get; set; } = 0.58f;
    [Export] public Color SunlitColor { get; set; } = new(0.91f, 0.95f, 1.0f, 1.0f);
    [Export] public Color ShadowColor { get; set; } = new(0.24f, 0.31f, 0.42f, 1.0f);
    [Export] public Color StormColor { get; set; } = new(0.12f, 0.16f, 0.23f, 1.0f);
    [Export] public Color NightColor { get; set; } = new(0.035f, 0.050f, 0.085f, 1.0f);

    [ExportGroup("Volumetric fog quality")]
    [Export(PropertyHint.Range, "2000,40000,250")] public float FogRenderLengthMeters { get; set; } = 18000.0f;
    [Export(PropertyHint.Range, "0.5,4.0,0.05")] public float FogDetailSpread { get; set; } = 1.6f;
    [Export] public bool EnableTemporalReprojection { get; set; } = true;
    [Export(PropertyHint.Range, "0,0.98,0.01")] public float TemporalReprojectionAmount { get; set; } = 0.72f;

    [ExportGroup("Fast travel and space")]
    [Export] public bool SuspendDuringFastTravel { get; set; } = true;
    [Export(PropertyHint.Range, "100,1000000,100")] public float FastTravelSpeedThresholdMetersPerSecond { get; set; } = 750.0f;
    [Export(PropertyHint.Range, "0,10,0.1")] public float ResumeSpeedThresholdMetersPerSecond { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.05")] public float ResumeStoppedSeconds { get; set; } = 0.25f;
    [Export(PropertyHint.Range, "0,30000,250")] public float UpperAtmosphereFadeStartMeters { get; set; } = 8000.0f;
    [Export(PropertyHint.Range, "1000,60000,250")] public float UpperAtmosphereFadeEndMeters { get; set; } = 20000.0f;

    [ExportGroup("Diagnostics")]
    [Export] public bool LogStateChanges { get; set; } = true;

    public bool LocalVolumeVisible { get; private set; }
    public bool FastTravelSuspended { get; private set; }
    public float ViewerAltitudeAboveTerrainMeters { get; private set; }
    public RingVolumetricCloudLayer.LocalWeatherState CurrentWeather { get; private set; }
    public Vector3 VolumeCanonicalCenterKm { get; private set; }
    public Vector3 VolumeSizeMeters { get; private set; } = new(24000.0f, 15900.0f, 24000.0f);
    public float CurrentVisibility { get; private set; }
    public float CurrentCloudBaseFraction { get; private set; } = 0.08f;
    public float CurrentCloudTopFraction { get; private set; } = 0.32f;
    public int CurrentShaderQualityLevel { get; private set; } = 1;

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingVolumetricCloudLayer? _weatherSource;
    private WorldEnvironment? _worldEnvironment;
    private DirectionalLight3D? _sunLight;
    private FogVolume? _fogVolume;
    private ShaderMaterial? _material;
    private double _weatherElapsed = double.PositiveInfinity;
    private double _stoppedElapsed;
    private long _surfaceAroundCell = long.MinValue;
    private int _surfaceAcrossCell = int.MinValue;
    private ulong _surfaceEditVersion = ulong.MaxValue;
	private float _cachedSurfaceHeight;
    private RingTerrainColumnCoordinate _lastAnchorCell = new(long.MinValue, int.MinValue);
    private Vector3 _lastGridOrigin = new(float.PositiveInfinity, 0.0f, 0.0f);
    private bool _lastOwnsClouds;
    private bool _lastReportedVisible;
    private RingVolumetricCloudLayer.CloudQuality _lastQuality = (RingVolumetricCloudLayer.CloudQuality)(-1);
    private bool _environmentCaptured;
    private bool _previousFogEnabled;
    private float _previousFogDensity;
    private float _previousFogLength;
    private float _previousFogDetailSpread;
    private float _previousFogAmbientInject;
    private float _previousFogAnisotropy;
    private bool _previousTemporalEnabled;
    private float _previousTemporalAmount;

    public override void _Ready()
    {
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _weatherSource = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _worldEnvironment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        _sunLight = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
        if (_explorer == null
            || _grid?.TerrainSampler == null
            || _weatherSource == null
            || _worldEnvironment?.Environment == null)
        {
            GD.PushWarning("Local volumetric clouds disabled: explorer, grid, weather source, or environment is missing.");
            return;
        }

        if (!Enabled)
        {
            _weatherSource.SetExternalLocalVolumetricCloudsState(
                ownsLocalClouds: false,
                localVolumeVisible: false,
                FarShellHandoffStartMeters,
                FarShellHandoffEndMeters);
            _worldEnvironment.Environment.VolumetricFogEnabled = false;
            _worldEnvironment.Environment.VolumetricFogDensity = 0.0f;
            SetProcess(false);
            GD.Print("Legacy fog-volume cloud tier disabled; Sunshine Clouds 2 is primary and merged meshes remain fallback-only.");
            return;
        }

        Shader? shader = GD.Load<Shader>("res://materials/ring_local_volumetric_cloud.gdshader");
        if (shader == null)
        {
            GD.PushError("Local volumetric cloud shader could not be loaded.");
            return;
        }

        _material = new ShaderMaterial { Shader = shader };
        _fogVolume = new FogVolume
        {
            Name = "CameraLocalWeatherFogVolume",
            Shape = RenderingServer.FogVolumeShape.Box,
            Material = _material,
            TopLevel = true,
            Visible = false
        };
        AddChild(_fogVolume);
        CaptureEnvironmentSettings();
        RefreshWeather(force: true);
        UpdateVolumeTransform(force: true);
        UpdateRuntimeState(force: true);
        // This renderer is intentionally ordered before the macro-weather node
        // so it can claim ownership before legacy meshes queue. Refresh again
        // on the first process frame, after every sibling has completed Ready.
        _weatherElapsed = double.PositiveInfinity;
        GD.Print(
            $"Local volumetric cloud tier ready: radius={HorizontalRadiusMeters:0}m, "
            + $"vertical={VolumeMinimumAltitudeMeters:0}-{VolumeMaximumAltitudeMeters:0}m datum, "
            + $"handoff={FarShellHandoffStartMeters:0}-{FarShellHandoffEndMeters:0}m.");
    }

    public override void _ExitTree()
    {
        RingTerrainCloudSunVisibility.Instance?.UnregisterRequiredAltitudeRange("local_fog");
        _weatherSource?.SetExternalLocalVolumetricCloudsState(
            ownsLocalClouds: false,
            localVolumeVisible: false,
            FarShellHandoffStartMeters,
            FarShellHandoffEndMeters);
        RestoreEnvironmentSettings();
    }

    public override void _Process(double delta)
    {
        if (_fogVolume == null
            || _material == null
            || _explorer == null
            || _grid == null
            || _weatherSource == null)
        {
            return;
        }

        UpdateFastTravelState(delta);
        _weatherElapsed += Math.Max(0.0, delta);
        if (_weatherElapsed >= Math.Max(0.1, WeatherRefreshSeconds))
            RefreshWeather(force: false);
        UpdateVolumeTransform(force: false);
        UpdateRuntimeState(force: false);
        UpdateMaterialUniforms();
    }

    private void CaptureEnvironmentSettings()
    {
        if (_environmentCaptured || _worldEnvironment?.Environment is not { } environment) return;
        _previousFogEnabled = environment.VolumetricFogEnabled;
        _previousFogDensity = environment.VolumetricFogDensity;
        _previousFogLength = environment.VolumetricFogLength;
        _previousFogDetailSpread = environment.VolumetricFogDetailSpread;
        _previousFogAmbientInject = environment.VolumetricFogAmbientInject;
        _previousFogAnisotropy = environment.VolumetricFogAnisotropy;
        _previousTemporalEnabled = environment.VolumetricFogTemporalReprojectionEnabled;
        _previousTemporalAmount = environment.VolumetricFogTemporalReprojectionAmount;
        _environmentCaptured = true;
    }

    private void ConfigureEnvironment(bool active)
    {
        if (_worldEnvironment?.Environment is not { } environment) return;
        environment.VolumetricFogEnabled = active;
        environment.VolumetricFogDensity = 0.0f;
        environment.VolumetricFogLength = Mathf.Max(1000.0f, FogRenderLengthMeters);
        environment.VolumetricFogDetailSpread = Mathf.Clamp(FogDetailSpread, 0.5f, 4.0f);
        environment.VolumetricFogAmbientInject = Mathf.Clamp(AmbientLightInjection, 0.0f, 1.0f);
        environment.VolumetricFogAnisotropy = Mathf.Clamp(ScatteringAnisotropy, -0.9f, 0.9f);
        environment.VolumetricFogTemporalReprojectionEnabled = EnableTemporalReprojection;
        environment.VolumetricFogTemporalReprojectionAmount = Mathf.Clamp(TemporalReprojectionAmount, 0.0f, 0.98f);
    }

    private void RestoreEnvironmentSettings()
    {
        if (!_environmentCaptured || _worldEnvironment?.Environment is not { } environment) return;
        environment.VolumetricFogEnabled = _previousFogEnabled;
        environment.VolumetricFogDensity = _previousFogDensity;
        environment.VolumetricFogLength = _previousFogLength;
        environment.VolumetricFogDetailSpread = _previousFogDetailSpread;
        environment.VolumetricFogAmbientInject = _previousFogAmbientInject;
        environment.VolumetricFogAnisotropy = _previousFogAnisotropy;
        environment.VolumetricFogTemporalReprojectionEnabled = _previousTemporalEnabled;
        environment.VolumetricFogTemporalReprojectionAmount = _previousTemporalAmount;
    }

    private void UpdateFastTravelState(double delta)
    {
        if (_explorer == null)
        {
            FastTravelSuspended = false;
            return;
        }

        float speed = _explorer.ActualTravelSpeedMetersPerSecond;
        bool eligible = Enabled && SuspendDuringFastTravel && _explorer.NoclipEnabled;
        bool next = FastTravelSuspended;
        if (!eligible)
        {
            next = false;
            _stoppedElapsed = 0.0;
        }
        else if (FastTravelSuspended)
        {
            if (speed <= Mathf.Max(0.0f, ResumeSpeedThresholdMetersPerSecond))
            {
                _stoppedElapsed += Math.Max(0.0, delta);
                next = _stoppedElapsed < Math.Max(0.0f, ResumeStoppedSeconds);
            }
            else
            {
                _stoppedElapsed = 0.0;
            }
        }
        else if (speed >= Mathf.Max(100.0f, FastTravelSpeedThresholdMetersPerSecond))
        {
            next = true;
            _stoppedElapsed = 0.0;
        }

        if (next == FastTravelSuspended) return;
        FastTravelSuspended = next;
        if (!next)
        {
            _lastAnchorCell = new RingTerrainColumnCoordinate(long.MinValue, int.MinValue);
            _weatherElapsed = double.PositiveInfinity;
        }
        if (LogStateChanges)
            GD.Print(next
                ? "Local volumetric clouds suspended for fast travel; far curved clouds remain active."
                : $"Local volumetric clouds resumed after {Mathf.Max(0.0f, ResumeStoppedSeconds):0.00}s stopped.");
    }

    private void RefreshWeather(bool force)
    {
        if (_weatherSource == null || _explorer == null) return;
        if (!force && _weatherElapsed < Math.Max(0.1, WeatherRefreshSeconds)) return;
        _weatherElapsed = 0.0;
        CurrentWeather = _weatherSource.QueryLocalWeather(_explorer.CanonicalPosition);
        _lastQuality = _weatherSource.Quality;
    }

    private void UpdateRuntimeState(bool force)
    {
        if (_weatherSource == null || _fogVolume == null || _explorer == null) return;
        ViewerAltitudeAboveTerrainMeters = ResolveViewerAltitudeAboveTerrain(_explorer.CanonicalPosition);
        float fadeStart = Mathf.Max(0.0f, UpperAtmosphereFadeStartMeters);
        float fadeEnd = Mathf.Max(fadeStart + 1.0f, UpperAtmosphereFadeEndMeters);
        float altitudeOpacity = 1.0f - SmoothUnit(
            (ViewerAltitudeAboveTerrainMeters - fadeStart) / (fadeEnd - fadeStart));
        bool ownsClouds = Enabled && _weatherSource.Quality != RingVolumetricCloudLayer.CloudQuality.Off;
        // A fast-moving camera must not present its old, locally anchored fog
        // volume. Relinquish ownership so the deterministic curved fallback
        // stays visible while this expensive local tier is suspended.
        bool visible = ownsClouds && !FastTravelSuspended && altitudeOpacity > 0.001f;
        LocalVolumeVisible = visible;
        CurrentVisibility = visible ? altitudeOpacity : 0.0f;
        _fogVolume.Visible = visible;
        _material?.SetShaderParameter("visibility", CurrentVisibility);

        bool stateChanged = force
            || visible != _lastReportedVisible
            || ownsClouds != _lastOwnsClouds;
        if (stateChanged)
        {
            ConfigureEnvironment(visible);
            _weatherSource.SetExternalLocalVolumetricCloudsState(
                visible,
                visible,
                Mathf.Min(FarShellHandoffStartMeters, FarShellHandoffEndMeters - 1.0f),
                Mathf.Max(FarShellHandoffStartMeters + 1.0f, FarShellHandoffEndMeters));
        }

        if (stateChanged && LogStateChanges)
        {
            GD.Print(
                $"Local volumetric clouds: {(visible ? "visible" : ownsClouds ? "far-shell only" : "fallback")}, "
                + $"altitude={ViewerAltitudeAboveTerrainMeters:0}m, weather={CurrentWeather.SystemKind}, "
                + $"coverage={CurrentWeather.CloudCoverage:0.00}, precipitation={CurrentWeather.PrecipitationPotential:0.00}.");
        }
        _lastReportedVisible = visible;
        _lastOwnsClouds = ownsClouds;
    }

    private void UpdateVolumeTransform(bool force)
    {
        if (_fogVolume == null || _grid == null || _explorer == null) return;
        float snap = Mathf.Max(64.0f, CanonicalSnapMeters);
        RingCanonicalPosition viewer = _explorer.CanonicalPosition;
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double wrappedAround = viewer.AroundMeters % circumference;
        if (wrappedAround < 0.0) wrappedAround += circumference;
        long aroundCellCount = Math.Max(1L, (long)Math.Ceiling(circumference / snap));
        long aroundCell = RingCoordMath.WrapAround(
            (long)Math.Round(wrappedAround / snap),
            aroundCellCount);
        int acrossCell = Mathf.RoundToInt((float)(viewer.AcrossMeters / snap));
        var anchorCell = new RingTerrainColumnCoordinate(aroundCell, acrossCell);
        bool gridMoved = !_lastGridOrigin.IsFinite()
            || !_lastGridOrigin.IsEqualApprox(_grid.GlobalPosition);
        if (!force && anchorCell == _lastAnchorCell && !gridMoved) return;
        _lastAnchorCell = anchorCell;
        _lastGridOrigin = _grid.GlobalPosition;

        double anchorAround = RingCoordMath.WrapAround(
            (long)Math.Round(aroundCell * (double)snap),
            Math.Max(1L, (long)Math.Round(circumference)));
        double anchorAcross = Math.Clamp(
            acrossCell * (double)snap,
            0.0,
            _grid.RingAcrossWidthMeters);
        float minimumAgl = Mathf.Max(0.0f, VolumeMinimumAltitudeMeters);
        float maximumAgl = Mathf.Max(minimumAgl + 1000.0f, VolumeMaximumAltitudeMeters);
        double centerVertical = FixedCloudAltitudeDatumMeters
            + (minimumAgl + maximumAgl) * 0.5;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 center = geometry.CanonicalToWorld(anchorAround, centerVertical, anchorAcross);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(anchorAround + 10.0, centerVertical, anchorAcross) - center).Normalized();
        Vector3 verticalAxis = (geometry.CanonicalToWorld(anchorAround, centerVertical + 10.0, anchorAcross) - center).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(anchorAround, centerVertical, anchorAcross + 10.0) - center).Normalized();
        VolumeSizeMeters = new Vector3(
            Mathf.Max(2000.0f, HorizontalRadiusMeters) * 2.0f,
            maximumAgl - minimumAgl,
            Mathf.Max(2000.0f, HorizontalRadiusMeters) * 2.0f);
        _fogVolume.Size = VolumeSizeMeters;
        _fogVolume.GlobalTransform = new Transform3D(
            new Basis(aroundAxis, verticalAxis, acrossAxis).Orthonormalized(),
            center);
        VolumeCanonicalCenterKm = new Vector3(
            (float)(anchorAround * 0.001),
            (float)(centerVertical * 0.001),
            (float)(anchorAcross * 0.001));
        _material?.SetShaderParameter("canonical_origin_km", VolumeCanonicalCenterKm);
        _material?.SetShaderParameter("ring_circumference_km", (float)(circumference * 0.001));
        _material?.SetShaderParameter("ring_width_km", (float)(_grid.RingAcrossWidthMeters * 0.001));
        _material?.SetShaderParameter("volume_minimum_agl_meters", minimumAgl);
        _material?.SetShaderParameter("volume_maximum_agl_meters", maximumAgl);
    }

    private void UpdateMaterialUniforms()
    {
        if (_material == null || _weatherSource == null || _grid == null || _sunLight == null) return;
        RingTerrainCloudSunVisibility.Instance?.BindToMaterial(_material);
        float minimumAgl = Mathf.Max(0.0f, VolumeMinimumAltitudeMeters);
        float maximumAgl = Mathf.Max(minimumAgl + 1000.0f, VolumeMaximumAltitudeMeters);
        RingTerrainCloudSunVisibility.Instance?.RegisterRequiredAltitudeRange("local_fog",
            FixedCloudAltitudeDatumMeters + minimumAgl, FixedCloudAltitudeDatumMeters + maximumAgl);
        RingTerrainCloudSunVisibility.Instance?.SetReceiverDemand("local_fog", LocalVolumeVisible);
        float heightRange = maximumAgl - minimumAgl;
        float cloudBase = CurrentWeather.CloudBaseAltitudeMeters >= 0.0f
            ? CurrentWeather.CloudBaseAltitudeMeters
            : _weatherSource.CloudBaseMeters;
        float cloudTop = CurrentWeather.CloudTopAltitudeMeters > cloudBase
            ? CurrentWeather.CloudTopAltitudeMeters
            : _weatherSource.CloudTopMeters;
        float baseFraction = Mathf.Clamp((cloudBase - minimumAgl) / heightRange, 0.0f, 0.92f);
        float topFraction = Mathf.Clamp((cloudTop - minimumAgl) / heightRange, baseFraction + 0.02f, 1.0f);
        int quality = _weatherSource.Quality switch
        {
            RingVolumetricCloudLayer.CloudQuality.Ultra => 3,
            RingVolumetricCloudLayer.CloudQuality.VeryHigh => 2,
            _ => 1
        };
        CurrentCloudBaseFraction = baseFraction;
        CurrentCloudTopFraction = topFraction;
        CurrentShaderQualityLevel = quality;
        _lastQuality = _weatherSource.Quality;
        Vector3 centerWorld = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
            _explorer?.CanonicalPosition.AroundMeters ?? 0.0,
            0.0,
            _explorer?.CanonicalPosition.AcrossMeters ?? 0.0));
        Vector3 surfaceUp = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
            _explorer?.CanonicalPosition.AroundMeters ?? 0.0,
            10.0,
            _explorer?.CanonicalPosition.AcrossMeters ?? 0.0)) - centerWorld).Normalized();
        _material.SetShaderParameter("quality_level", quality);
        _material.SetShaderParameter("coverage", Mathf.Clamp(CurrentWeather.CloudCoverage, 0.0f, 1.0f));
        _material.SetShaderParameter("precipitation", Mathf.Clamp(CurrentWeather.PrecipitationPotential, 0.0f, 1.0f));
        _material.SetShaderParameter("storm_influence", Mathf.Clamp(CurrentWeather.SystemInfluence, 0.0f, 1.0f));
        _material.SetShaderParameter(
            "formation_profile",
            CloudFormationProfiles.ToShaderProfile(CurrentWeather.DominantFormationProfile));
        _material.SetShaderParameter("cloud_base_fraction", baseFraction);
        _material.SetShaderParameter("cloud_top_fraction", topFraction);
        _material.SetShaderParameter("density_scale", Mathf.Max(0.001f, DensityScale));
        _material.SetShaderParameter("softness", Mathf.Clamp(EdgeSoftness, 0.01f, 0.3f));
        _material.SetShaderParameter("handoff_start_meters", Mathf.Max(0.0f, FarShellHandoffStartMeters));
        _material.SetShaderParameter("handoff_end_meters", Mathf.Max(FarShellHandoffStartMeters + 1.0f, FarShellHandoffEndMeters));
        _material.SetShaderParameter("wind_km_per_second", CurrentWeather.WindMetersPerSecond * 0.001f);
        _material.SetShaderParameter("weather_time_seconds", (float)(Time.GetTicksMsec() * 0.001));
        _material.SetShaderParameter("surface_up", surfaceUp);
        _material.SetShaderParameter("sun_direction", _sunLight.GlobalTransform.Basis.Z.Normalized());
        _material.SetShaderParameter("sunlit_color", new Vector3(SunlitColor.R, SunlitColor.G, SunlitColor.B));
        _material.SetShaderParameter("shadow_color", new Vector3(ShadowColor.R, ShadowColor.G, ShadowColor.B));
        _material.SetShaderParameter("storm_color", new Vector3(StormColor.R, StormColor.G, StormColor.B));
        _material.SetShaderParameter("night_color", new Vector3(NightColor.R, NightColor.G, NightColor.B));
        _material.SetShaderParameter("cloud_seed", _grid.TerrainSampler?.Seed ?? 1337);
    }

    private float ResolveViewerAltitudeAboveTerrain(in RingCanonicalPosition position)
        => Mathf.Max(0.0f, (float)(position.VerticalMeters - ResolveSurfaceHeight(position)));

	private float ResolveSurfaceHeight(in RingCanonicalPosition position)
    {
        if (_grid?.TerrainSampler == null) return 0;
        const int cacheCellMeters = 256;
        long around = (long)Math.Round(position.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)position.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        long aroundCell = (long)Math.Floor(around / (double)cacheCellMeters);
        int acrossCell = Mathf.FloorToInt(across / (float)cacheCellMeters);
        ulong editVersion = _grid.LodSurfaceEditVersion;
        if (aroundCell != _surfaceAroundCell
            || acrossCell != _surfaceAcrossCell
            || editVersion != _surfaceEditVersion)
        {
			RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
			_cachedSurfaceHeight = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface
				? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, around, across)
				: edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
            _surfaceAroundCell = aroundCell;
            _surfaceAcrossCell = acrossCell;
            _surfaceEditVersion = editVersion;
        }
        return _cachedSurfaceHeight;
    }

    private static float SmoothUnit(float value)
    {
        float t = Mathf.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
