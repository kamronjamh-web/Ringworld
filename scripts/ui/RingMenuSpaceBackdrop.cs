using System;
using Godot;

namespace RingworldTechDemo.UI;

/// <summary>Isolated, meter-scale menu flyover with bounded terrain and Sunshine clouds.</summary>
public partial class RingMenuSpaceBackdrop : Control
{
    [Signal] public delegate void MenuTuningChangedEventHandler();
    [Export] public int Seed { get; set; } = -1;
    [Export] public bool FreezeMotion { get; set; }
    [Export] public bool UseSavedMenuDefaults { get; set; } = true;
    [Export] public string MenuSettingsPath { get; set; } = "res://resources/menu/menu_flyover_defaults.tres";

    /// <summary>Altitude the flyover uses instead of the tuned one while a live world is expected behind the menu (km above ground).</summary>
    [Export] public float WarmWorldAltitudeKm { get; set; }

    private MenuFlyoverSettings _tuning = new();
    private TextureRect? _output;
    private bool _retired;
    private WorldEnvironment? _worldEnvironment;
    private DirectionalLight3D? _sunLight;
    private Vector3 _sunDirection;
    private SubViewport? _viewport;
    private Camera3D? _camera;
    private MenuFlyoverTerrainCache? _cache;
    private MenuFlyoverFarTerrain? _farTerrain;
    private MenuFlyoverSunshineLayer? _sunshine;
    private MeshInstance3D? _nearTerrain;
    private ShaderMaterial? _nearMaterial;
    private ShaderMaterial? _skyMaterial;
    private int _seed, _meshCount, _triangleCount, _renderUpdates;
    private double _wallSeconds, _animationSeconds, _travelMeters, _renderAccumulator, _presentationAccumulator;
    private float _smoothedGroundKilometers;
    // Screen camera pose: current spring state and the normalized distance at the last pose change.
    private string _cameraPose = "main";
    private float _poseAltitudeKm, _posePitch, _poseFov, _poseAltitudeVel, _posePitchVel, _poseFovVel, _poseStartDistance = 1f;
    private bool _poseInitialized, _poseBlending;
    private bool _terrainRebuildPending;
    private double _terrainRebuildDelay;
    private int _activeFarTerrainDetail, _terrainGeneration;
    private const double SunDriftUpdateSeconds = .25;
    private double _sunDriftAccumulator, _motionEaseSeconds;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        LoadMenuDefaults();
        using var random = new RandomNumberGenerator();
        random.Randomize();
        _seed = Seed >= 0 ? Seed : random.RandiRange(1, int.MaxValue);
        _cache = new MenuFlyoverTerrainCache(_seed, _tuning.TerrainSpacingMeters);
        CreateWorld();
        BuildTerrain();
        ApplyMenuTuningSettings(_tuning);
        _cache.EnsureCoverage(CurrentRow());
    }

    public override void _ExitTree()
    {
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        // Drop receiver references before the adapter retires its GPU textures.
        _nearMaterial?.SetShaderParameter("cloud_shadow_texture", default(Variant));
        if (_farTerrain?.Instance.MaterialOverride is ShaderMaterial farMaterial)
            farMaterial.SetShaderParameter("cloud_shadow_texture", default(Variant));
        _sunshine?.Detach();
        _farTerrain?.Dispose();
        _cache?.Dispose();
        _tuning.Dispose();
        _sunshine = null;
        _farTerrain = null;
        _cache = null;
    }

    public override void _Process(double delta)
    {
        if (_retired) return;
        _wallSeconds += delta;
        if (_terrainRebuildPending)
        {
            _terrainRebuildDelay -= delta;
            if (_terrainRebuildDelay <= 0.0) RebuildTerrain();
        }
        _cache?.UpdateBudgeted();
        _farTerrain?.BudgetedUpdate();
        if (_nearTerrain is not null && _cache is not null) _nearTerrain.Visible = _cache.InitialReady;
        _renderAccumulator += delta;
        _presentationAccumulator += delta;
        if (_renderAccumulator < 1.0 / 60.0) return;
        _renderAccumulator %= 1.0 / 60.0;
        double renderDelta = _presentationAccumulator;
        _presentationAccumulator = 0.0;
        _renderUpdates++;
        bool ready = IsBackdropReady();
        // Restart the ease-in whenever the terrain is not ready (initial build or rebuild).
        if (!ready) _motionEaseSeconds = 0.0;
        else if (!FreezeMotion && !_tuning.Paused)
        {
            _animationSeconds += renderDelta;
            _motionEaseSeconds += renderDelta;
            _travelMeters += _tuning.SpeedKmPerSecond * 1000.0 * renderDelta * MotionEaseFactor();
            UpdateSunDrift(renderDelta);
        }
        UpdatePresentation(renderDelta);
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    public void AdvanceFlyoverForTest(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0.0 || FreezeMotion || _tuning.Paused) return;
        _travelMeters += _tuning.SpeedKmPerSecond * 1000.0 * seconds;
        _cache?.EnsureCoverage(CurrentRow());
    }

    /// <summary>0..1 readiness across the near cache rows and far terrain samples; 1 once ready.</summary>
    public float GetBackdropProgress()
    {
        if (IsBackdropReady()) return 1f;
        if (_cache is null) return 0f;
        double near = Math.Clamp((double)_cache.ResidentRows / Math.Max(1, _cache.RowCount), 0.0, 1.0);
        double far = 1.0;
        if (_farTerrain is not null)
            far = Math.Clamp((double)_farTerrain.SampleCount / Math.Max(1, (long)_farTerrain.AroundSampleCount * _farTerrain.AcrossSampleCount), 0.0, 1.0);
        return (float)Math.Min(.999, (near + far) * .5);
    }

    private double MotionEaseFactor()
    {
        if (_tuning.MotionEaseInSeconds <= 0f) return 1.0;
        double t = Math.Clamp(_motionEaseSeconds / _tuning.MotionEaseInSeconds, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    private float EffectiveSunElevationDegrees()
    {
        float elevation = _tuning.SunElevationDegrees;
        if (_tuning.SunDriftAmplitudeDegrees > 0f)
            elevation += _tuning.SunDriftAmplitudeDegrees * (float)Math.Sin(Math.Tau * _animationSeconds / (_tuning.SunDriftPeriodMinutes * 60.0));
        return Mathf.Clamp(elevation, -90f, 90f);
    }

    private Vector3 ComputeSunDirection()
    {
        float elevation = Mathf.DegToRad(EffectiveSunElevationDegrees());
        float azimuth = Mathf.DegToRad(_tuning.SunAzimuthDegrees);
        return new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation),
            Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation));
    }

    private void ApplySunToLight()
    {
        if (_sunLight is null) return;
        Vector3 referenceUp = Mathf.Abs(_sunDirection.Dot(Vector3.Up)) > .99f ? Vector3.Forward : Vector3.Up;
        _sunLight.LookAt(_sunLight.GlobalPosition - _sunDirection, referenceUp);
    }

    /// <summary>Slow time-of-day drift at ~4 Hz; history-safe (no cloud history reset).</summary>
    private void UpdateSunDrift(double delta)
    {
        if (_tuning.SunDriftAmplitudeDegrees <= 0f) return;
        _sunDriftAccumulator += delta;
        if (_sunDriftAccumulator < SunDriftUpdateSeconds) return;
        _sunDriftAccumulator = 0.0;
        Vector3 direction = ComputeSunDirection();
        if (direction.IsEqualApprox(_sunDirection)) return;
        _sunDirection = direction;
        ApplySunToLight();
        _skyMaterial?.SetShaderParameter("sun_direction", _sunDirection);
        _sunshine?.UpdateSunDirection(_sunDirection);
    }

    public bool IsRetired => _retired;

    /// <summary>Opacity of the whole flyover picture (1 opaque, 0 fully showing what is drawn behind it).</summary>
    public void SetFlyoverAlpha(float alpha)
    {
        if (_output is not null) _output.Modulate = new Color(1f, 1f, 1f, Mathf.Clamp(alpha, 0f, 1f));
    }

    /// <summary>
    /// Permanently stops and frees the flyover (its SubViewport, terrain meshes, caches and Sunshine layer, about 6.5 M triangles).
    /// The control stays so the menu and tuning drawer keep valid references; every accessor is null-safe afterwards.
    /// </summary>
    public void Retire()
    {
        if (_retired) return;
        _retired = true;
        SetProcess(false);
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        _nearMaterial?.SetShaderParameter("cloud_shadow_texture", default(Variant));
        if (_farTerrain?.Instance.MaterialOverride is ShaderMaterial farMaterial)
            farMaterial.SetShaderParameter("cloud_shadow_texture", default(Variant));
        _sunshine?.Detach();
        _sunshine?.QueueFree();
        _output?.QueueFree();
        // Remove the instances from the flyover world before retiring the textures they sample.
        if (_viewport is not null && _nearTerrain is not null)
        {
            _nearTerrain.MaterialOverride = null;
            _viewport.RemoveChild(_nearTerrain);
            Mesh? oldMesh = _nearTerrain.Mesh;
            _nearTerrain.Mesh = null;
            _nearTerrain.QueueFree();
            oldMesh?.Dispose();
        }
        if (_viewport is not null && _farTerrain is not null) _viewport.RemoveChild(_farTerrain.Instance);
        _farTerrain?.Dispose();
        _cache?.Dispose();
        _nearMaterial?.Dispose();
        _viewport?.QueueFree();
        _sunshine = null; _farTerrain = null; _cache = null; _output = null; _viewport = null; _camera = null;
        _nearTerrain = null; _nearMaterial = null; _skyMaterial = null; _sunLight = null; _worldEnvironment = null;
        _terrainRebuildPending = false;
    }

    public bool IsBackdropReady() => _cache is { InitialReady: true } && _farTerrain is { Ready: true };

    public string GetMenuSettingsPath() => OS.HasFeature("template")
        && MenuSettingsPath == "res://resources/menu/menu_flyover_defaults.tres"
            ? "user://menu_flyover_defaults.tres" : MenuSettingsPath;

    public MenuFlyoverSettings GetMenuTuningSettings() => _tuning.CopyValidated();

    /// <summary>Selects a screen camera pose: "main", "worlds" or "detail". Unknown names fall back to "main".</summary>
    public void SetCameraPose(string pose)
    {
        string normalized = pose is "worlds" or "detail" ? pose : "main";
        if (normalized == _cameraPose) return;
        _cameraPose = normalized;
        EnsurePoseInitialized();
        _poseStartDistance = Mathf.Max(PoseDistance(), .001f);
        if (FreezeMotion || _tuning.PoseBlendSeconds <= 0f)
        {
            SnapPoseToTarget();
            UpdatePresentation(0.0);
            if (_viewport is not null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        }
    }

    public string GetCameraPose() => _cameraPose;

    /// <summary>Base flyover altitude: the cruise altitude of the live world when one is expected (never above the tuned altitude).</summary>
    private float EffectiveAltitudeKm() => WarmWorldAltitudeKm > 0f ? Mathf.Min(_tuning.AltitudeKm, WarmWorldAltitudeKm) : _tuning.AltitudeKm;

    private (float Altitude, float Pitch, float Fov) PoseTarget() => _cameraPose switch
    {
        "worlds" => (EffectiveAltitudeKm() * _tuning.WorldsAltitudeScale, _tuning.WorldsPitchDegrees, _tuning.FieldOfView),
        "detail" => (EffectiveAltitudeKm() * .85f, _tuning.PitchDegrees, Mathf.Max(20f, _tuning.FieldOfView + _tuning.DetailFovDeltaDegrees)),
        _ => (EffectiveAltitudeKm(), _tuning.PitchDegrees, _tuning.FieldOfView)
    };

    /// <summary>Unitless distance of the spring state from the active pose target.</summary>
    private float PoseDistance()
    {
        var target = PoseTarget();
        return Mathf.Abs(_poseAltitudeKm - target.Altitude) / Mathf.Max(1f, EffectiveAltitudeKm())
            + Mathf.Abs(_posePitch - target.Pitch) / 30f + Mathf.Abs(_poseFov - target.Fov) / 20f;
    }

    private void EnsurePoseInitialized()
    {
        if (_poseInitialized) return;
        SnapPoseToTarget();
        _poseInitialized = true;
    }

    private void SnapPoseToTarget()
    {
        var target = PoseTarget();
        (_poseAltitudeKm, _posePitch, _poseFov) = target;
        _poseAltitudeVel = _posePitchVel = _poseFovVel = 0f;
        _poseBlending = false;
        _poseInitialized = true;
    }

    private void UpdatePoseBlend(double delta)
    {
        EnsurePoseInitialized();
        if (FreezeMotion || _tuning.PoseBlendSeconds <= 0f) { SnapPoseToTarget(); return; }
        var target = PoseTarget();
        // Critically damped spring; omega = 6 / T settles in roughly PoseBlendSeconds.
        float omega = 6f / _tuning.PoseBlendSeconds;
        float dt = (float)Math.Clamp(delta, 0.0, .1);
        if (dt > 0f)
        {
            Spring(ref _poseAltitudeKm, ref _poseAltitudeVel, target.Altitude, omega, dt);
            Spring(ref _posePitch, ref _posePitchVel, target.Pitch, omega, dt);
            Spring(ref _poseFov, ref _poseFovVel, target.Fov, omega, dt);
        }
        float speed = Mathf.Abs(_poseAltitudeVel) + Mathf.Abs(_posePitchVel) + Mathf.Abs(_poseFovVel);
        if (PoseDistance() < .0005f && speed < .01f) SnapPoseToTarget();
        else _poseBlending = true;
    }

    private static void Spring(ref float value, ref float velocity, float target, float omega, float dt)
    {
        float offset = value - target;
        float temp = (velocity + omega * offset) * dt;
        float decay = Mathf.Exp(-omega * dt);
        velocity = (velocity - omega * temp) * decay;
        value = target + (offset + temp) * decay;
    }

    public string GetFlyoverCaption() => $"SURFACE FLYOVER / {EffectiveAltitudeKm():0.#} KM / {_tuning.SpeedKmPerSecond:0.#} KM/S";

    private void LoadMenuDefaults()
    {
        if (!UseSavedMenuDefaults) return;
        string path = GetMenuSettingsPath();
        // Exported PCK resources are read-only. Their local override is optional;
        // otherwise start with the project preset bundled into the build.
        if (!ResourceLoader.Exists(path)) path = MenuSettingsPath;
        if (!ResourceLoader.Exists(path)) return;
        using var loaded = ResourceLoader.Load<MenuFlyoverSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        if (loaded is null)
        {
            GD.PushWarning($"Could not load menu defaults from {path}; using built-in defaults.");
            return;
        }
        _tuning.Dispose();
        _tuning = loaded.CopyValidated();
    }

    public void ApplyMenuTuningSettings(MenuFlyoverSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        MenuFlyoverSettings validated = settings.CopyValidated();
        bool terrainChoiceChanged = validated.TerrainSpacingMeters != _tuning.TerrainSpacingMeters
            || validated.FarTerrainDetail != _tuning.FarTerrainDetail;
        _tuning.Dispose();
        _tuning = validated;
        if (terrainChoiceChanged && _cache is not null)
        {
            _terrainRebuildPending = _cache.SampleSpacingMeters != _tuning.TerrainSpacingMeters
                || _activeFarTerrainDetail != _tuning.FarTerrainDetail;
            _terrainRebuildDelay = .75;
        }
        // Use the drifted elevation so a drawer edit does not snap the sun back.
        _sunDirection = ComputeSunDirection();
        _sunDriftAccumulator = 0.0;
        if (_sunLight is not null)
        {
            _sunLight.LightEnergy = _tuning.SunEnergy;
            ApplySunToLight();
        }
        if (_worldEnvironment?.Environment is Godot.Environment environment)
        {
            environment.AmbientLightEnergy = _tuning.AmbientEnergy;
            environment.GlowEnabled = _tuning.GlowEnabled;
            environment.GlowIntensity = _tuning.GlowIntensity;
        }
        // Drawer edits apply immediately; the pose spring only animates pose changes.
        SnapPoseToTarget();
        _skyMaterial?.SetShaderParameter("sun_direction", _sunDirection);
        _sunshine?.ApplyTuning(_tuning, _sunDirection);
        UpdatePresentation(0.0);
        if (_viewport is not null) _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        EmitSignal(SignalName.MenuTuningChanged);
    }

    public Godot.Collections.Dictionary GetBackdropDiagnostics() => new()
    {
        ["seed"] = _seed,
        ["ready"] = IsBackdropReady(),
        ["terrain_sample_count"] = _cache?.SampleCount ?? 0,
        ["far_sample_count"] = _farTerrain?.SampleCount ?? 0,
        ["far_ready"] = _farTerrain?.Ready ?? false,
        ["mesh_count"] = _meshCount,
        ["near_triangles"] = _cache is null ? 0 : (_cache.RowCount - 1) * (_cache.ColumnCount - 1) * 2,
        ["far_triangles"] = _farTerrain?.TriangleCount ?? 0,
        ["triangle_count"] = _triangleCount,
        ["camera_position"] = _camera?.Position ?? Vector3.Zero,
        ["viewport_size"] = _viewport?.Size ?? Vector2I.Zero,
        ["elapsed_seconds"] = _wallSeconds,
        ["render_update_count"] = _renderUpdates,
        ["cap_hz"] = 60,
        ["altitude_m"] = (_camera?.Position.Y ?? EffectiveAltitudeKm() * 1000f) - SampleCameraHeightKilometers(CurrentRow()) * 1000f,
        ["travel_m"] = _travelMeters,
        ["speed_mps"] = _tuning.SpeedKmPerSecond * 1000f,
        ["motion_ease"] = MotionEaseFactor(),
        ["backdrop_progress"] = GetBackdropProgress(),
        ["effective_sun_elevation_degrees"] = EffectiveSunElevationDegrees(),
        ["pitch_degrees"] = _poseInitialized ? _posePitch : _tuning.PitchDegrees,
        ["fov_degrees"] = _poseInitialized ? _poseFov : _tuning.FieldOfView,
        ["camera_pose"] = _cameraPose,
        // Residual distance to the pose target: 1 right after a pose change, 0 once settled.
        ["pose_blend"] = _poseBlending ? Mathf.Clamp(PoseDistance() / _poseStartDistance, 0f, 1f) : 0f,
        ["pose_altitude_km"] = _poseInitialized ? _poseAltitudeKm : EffectiveAltitudeKm(),
        ["retired"] = _retired,
        ["paused"] = FreezeMotion || _tuning.Paused,
        ["menu_settings_path"] = GetMenuSettingsPath(),
        ["terrain_cache_rows"] = _cache?.RowCount ?? 0,
        ["terrain_cache_columns"] = _cache?.ColumnCount ?? 0,
        ["terrain_spacing_meters"] = _cache?.SampleSpacingMeters ?? 0,
        ["behind_rows"] = _cache?.BehindRowCount ?? 0,
        ["terrain_generation"] = _terrainGeneration,
        ["terrain_rebuild_pending"] = _terrainRebuildPending,
        ["requested_terrain_spacing_meters"] = _tuning.TerrainSpacingMeters,
        ["far_terrain_detail"] = _activeFarTerrainDetail,
        ["far_atlas_size"] = _farTerrain is null ? Vector2I.Zero : new Vector2I(_farTerrain.AroundSampleCount, _farTerrain.AcrossSampleCount),
        ["texture_upload_count"] = _cache?.TextureUploadCount ?? 0,
        ["committed_rows"] = _cache?.CommittedRows ?? 0,
        ["resident_rows"] = _cache?.ResidentRows ?? 0,
        ["initial_ready"] = _cache?.InitialReady ?? false,
        ["window_ready"] = _cache?.WindowReady ?? false,
        ["queuecount"] = _cache?.QueueCount ?? 0,
        ["cache_miss"] = _cache?.CacheMisses ?? 0,
        ["circumference_m"] = _cache?.CircumferenceMeters ?? 0,
        ["route_start_m"] = _cache?.StartAroundMeters ?? 0,
        ["route_selection_samples"] = _cache?.RouteSelectionSamples ?? 0,
        ["canonicalaround"] = CanonicalAround(),
        ["cloud"] = _sunshine?.GetCloudDiagnostics() ?? new Godot.Collections.Dictionary()
    };

    private void CreateWorld()
    {
        _viewport = new SubViewport {
            Name = "MenuFlyoverViewport", OwnWorld3D = true, TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once, Msaa3D = Viewport.Msaa.Msaa4X,
            UseDebanding = true, Size = BoundedViewportSize()
        };
        AddChild(_viewport);
        _output = new TextureRect { Texture = _viewport.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
        _output.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_output);
        Resized += () => { if (_viewport is not null) _viewport.Size = BoundedViewportSize(); };

        _camera = new Camera3D { Current = true, Near = 50f, Far = 12_000_000f, Fov = _tuning.FieldOfView, Position = new Vector3(0f, EffectiveAltitudeKm() * 1000f, 0f) };
        _camera.RotationDegrees = new Vector3(-_tuning.PitchDegrees, 0f, 0f);
        _viewport.AddChild(_camera);
        Vector3 sunDirection = new(-.4f, .75f, -.5f);
        Shader skyShader = GD.Load<Shader>("res://materials/menu_flyover_sky.gdshader");
        _skyMaterial = new ShaderMaterial { Shader = skyShader };
        _skyMaterial.SetShaderParameter("sun_direction", sunDirection.Normalized());
        _skyMaterial.SetShaderParameter("Rkm", (float)(_cache!.RadiusMeters * .001));
        _skyMaterial.SetShaderParameter("ring_width_km", _cache.RingWidthMeters * .001f);
        var environment = new Godot.Environment {
            BackgroundMode = Godot.Environment.BGMode.Sky, Sky = new Sky { SkyMaterial = _skyMaterial },
            AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(.08f, .11f, .17f), AmbientLightEnergy = .35f,
            GlowEnabled = true, GlowIntensity = .45f, TonemapMode = Godot.Environment.ToneMapper.Filmic
        };
        _worldEnvironment = new WorldEnvironment { Name = "MenuFlyoverEnvironment", Environment = environment };
        _viewport.AddChild(_worldEnvironment);
        _sunLight = new DirectionalLight3D { LightEnergy = _tuning.SunEnergy, ShadowEnabled = false };
        _viewport.AddChild(_sunLight);
        _sunLight.LookAt(-sunDirection, Vector3.Up);
        _sunshine = new MenuFlyoverSunshineLayer { Name = "MenuFlyoverSunshineLayer" };
        AddChild(_sunshine);
        _sunshine.Configure(_worldEnvironment, _seed, _cache.RadiusMeters, _cache.RingWidthMeters, sunDirection);
    }

    private void BuildTerrain()
    {
        if (_viewport is null || _cache is null) return;
        _meshCount = 0;
        _triangleCount = 0;
        _terrainGeneration++;
        _nearMaterial = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/menu_ring_surface.gdshader") };
        ConfigureTerrainMaterial(_nearMaterial, true);
        _nearTerrain = new MeshInstance3D {
            Name = "MenuFlyoverTerrain", Mesh = MenuFlyoverTerrainMesh.BuildGrid(_cache.RowCount, _cache.ColumnCount), MaterialOverride = _nearMaterial,
            Scale = Vector3.One * 1000f, Visible = false, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, IgnoreOcclusionCulling = true,
            CustomAabb = new Aabb(new Vector3(-96f, -10f, -260f), new Vector3(192f, 40f, 340f))
        };
        _viewport.AddChild(_nearTerrain);
        _meshCount++;
        _triangleCount += (_cache.RowCount - 1) * (_cache.ColumnCount - 1) * 2;

        _activeFarTerrainDetail = _tuning.FarTerrainDetail;
        _farTerrain = new MenuFlyoverFarTerrain(_cache, _activeFarTerrainDetail);
        _farTerrain.Instance.Scale = Vector3.One * 1000f;
        _viewport.AddChild(_farTerrain.Instance);
        if (_farTerrain.Instance.MaterialOverride is ShaderMaterial farMaterial) ConfigureTerrainMaterial(farMaterial, false);
        _meshCount++;
        _triangleCount += _farTerrain.TriangleCount;
    }

    private void RebuildTerrain()
    {
        if (_viewport is null || _cache is null) return;
        _terrainRebuildPending = false;
        long routeStart = _cache.StartAroundMeters;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        // Remove the old instances from this world before retiring their owned
        // textures. Reuse the same names and compositor; never reroll the seed.
        if (_nearTerrain is not null)
        {
            _nearTerrain.MaterialOverride = null;
            Mesh? oldMesh = _nearTerrain.Mesh;
            _nearTerrain.Mesh = null;
            _viewport.RemoveChild(_nearTerrain);
            _nearTerrain.QueueFree();
            oldMesh?.Dispose();
        }
        _nearMaterial?.Dispose();
        _nearMaterial = null;
        _nearTerrain = null;
        if (_farTerrain is not null)
        {
            MeshInstance3D oldFar = _farTerrain.Instance;
            _farTerrain.Dispose();
            _viewport.RemoveChild(oldFar);
            _farTerrain = null;
        }
        _cache.Dispose();
        _cache = new MenuFlyoverTerrainCache(_seed, _tuning.TerrainSpacingMeters, routeStart);
        BuildTerrain();
        _cache.EnsureCoverage(CurrentRow());
        UpdatePresentation(0.0);
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
    }

    private void ConfigureTerrainMaterial(ShaderMaterial material, bool useNearCacheTextures)
    {
        if (_cache is null) return;
        if (useNearCacheTextures)
        {
            material.SetShaderParameter("height_texture", _cache.HeightTexture);
            material.SetShaderParameter("color_texture", _cache.ColorTexture);
        }
        material.SetShaderParameter("availability_texture", _cache.AvailabilityTexture);
        material.SetShaderParameter("radius_km", (float)(_cache.RadiusMeters * .001));
        material.SetShaderParameter("ring_width_km", _cache.RingWidthMeters * .001f);
        material.SetShaderParameter("rows", (float)_cache.RowCount);
        material.SetShaderParameter("columns", (float)_cache.ColumnCount);
        material.SetShaderParameter("terrain_info", new Vector4(-48f, -96f, _cache.SampleSpacingMeters * .001f, 0f));
        material.SetShaderParameter("near_ready", false);
    }

    private void UpdatePresentation(double delta)
    {
        if (_cache is null) return;
        long row = CurrentRow();
        _cache.EnsureCoverage(row);
        bool nearReady = IsBackdropReady();
        double around = CanonicalAround();
        double windowStartKm = ((row - _cache.BehindRowCount) * _cache.SampleSpacingMeters - (_cache.StartAroundMeters + _travelMeters)) * .001;
        int rowOffset = _cache.PhysicalSlot(row - _cache.BehindRowCount);
        Vector2 anchor = new((float)(around * .001), _cache.RingWidthMeters * .0005f);
        if (_camera is not null)
        {
            float groundKm = SampleCameraHeightKilometers(row);
            _smoothedGroundKilometers = Mathf.Lerp(_smoothedGroundKilometers, groundKm, 1f - Mathf.Exp(-(float)delta * 4f));
            UpdatePoseBlend(delta);
            _camera.Position = new Vector3(0f, (_poseAltitudeKm + _smoothedGroundKilometers) * 1000f, 0f);
            _camera.Fov = _poseFov;
            _camera.RotationDegrees = new Vector3(-_posePitch, 0f, 0f);
        }
        // Reprojection ignores rotation/FOV change, so cloud history is treated as moving while blending.
        _sunshine?.SetCameraBlending(_poseBlending);
        // Leave Sunshine's noise anchor unwrapped, so weather does not jump at
        // the terrain circumference seam. Receiver map deltas wrap in shader.
        _sunshine?.UpdateFrame(_cache.StartAroundMeters + _travelMeters, _animationSeconds, FreezeMotion || _tuning.Paused || !nearReady);
        ApplyPresentationParameters(_nearMaterial, nearReady, anchor, (float)windowStartKm, rowOffset);
        if (_farTerrain?.Instance.MaterialOverride is ShaderMaterial farMaterial)
            ApplyPresentationParameters(farMaterial, nearReady, anchor, (float)windowStartKm, rowOffset);
        _skyMaterial?.SetShaderParameter("camera_origin_km", (_camera?.Position ?? Vector3.Zero) * .001f);
    }

    private void ApplyPresentationParameters(ShaderMaterial? material, bool nearReady, Vector2 anchor, float windowStart, int rowOffset)
    {
        if (material is null || _sunshine is null) return;
        material.SetShaderParameter("window_start_km", windowStart);
        material.SetShaderParameter("row_offset", rowOffset);
        material.SetShaderParameter("canonical_anchor_km", anchor);
        material.SetShaderParameter("camera_origin_km", (_camera?.Position ?? Vector3.Zero) * .001f);
        if (_sunshine.ShadowTexture is Texture2D shadowTexture)
            material.SetShaderParameter("cloud_shadow_texture", shadowTexture);
        material.SetShaderParameter("shadow_center_km", _sunshine.ShadowCenterCanonicalMeters * .001f);
        material.SetShaderParameter("shadow_extent_km", _sunshine.ShadowSpanMeters * .001f);
        material.SetShaderParameter("shadow_uv_offset", _sunshine.ShadowShaderUvOffset);
        material.SetShaderParameter("shadow_ready", _sunshine.ShadowReady);
        material.SetShaderParameter("shadow_strength", _tuning.ShadowStrength);
        material.SetShaderParameter("haze_amount", _tuning.HazeAmount);
        material.SetShaderParameter("haze_start_km", _tuning.HazeStartKm);
        material.SetShaderParameter("haze_end_km", _tuning.HazeEndKm);
        material.SetShaderParameter("cloud_floor_km", _tuning.CloudFloorKm);
        material.SetShaderParameter("cloud_ceiling_km", _tuning.CloudCeilingKm);
        material.SetShaderParameter("near_ready", nearReady);
    }

    private float SampleCameraHeightKilometers(long row)
    {
        if (_cache is null || !_cache.TryGetHeightKilometers(row, _cache.ColumnCount / 2, out float height)) return _smoothedGroundKilometers;
        if (!_cache.TryGetHeightKilometers(row + 1, _cache.ColumnCount / 2, out float next)) return height;
        float fraction = (float)((_cache.StartAroundMeters + _travelMeters) / _cache.SampleSpacingMeters - row);
        return Mathf.Lerp(height, next, fraction);
    }

    private long CurrentRow() => _cache is null ? 0 : (long)Math.Floor((_cache.StartAroundMeters + _travelMeters) / _cache.SampleSpacingMeters);
    private double CanonicalAround() => _cache is null ? 0.0 : (_cache.StartAroundMeters + _travelMeters) % _cache.CircumferenceMeters;
    private Vector2I BoundedViewportSize()
    {
        Vector2 source = GetViewport().GetVisibleRect().Size;
        float scale = Mathf.Min(1f, Mathf.Min(3840f / Mathf.Max(1f, source.X), 2160f / Mathf.Max(1f, source.Y)));
        return new Vector2I(Mathf.Max(1, Mathf.RoundToInt(source.X * scale)), Mathf.Max(1, Mathf.RoundToInt(source.Y * scale)));
    }
}
