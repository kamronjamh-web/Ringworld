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

    private MenuFlyoverSettings _tuning = new();
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
    private bool _terrainRebuildPending;
    private double _terrainRebuildDelay;
    private int _activeFarTerrainDetail, _terrainGeneration;

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
        if (!FreezeMotion && !_tuning.Paused && IsBackdropReady())
        {
            _animationSeconds += renderDelta;
            _travelMeters += _tuning.SpeedKmPerSecond * 1000.0 * renderDelta;
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

    public bool IsBackdropReady() => _cache is { InitialReady: true } && _farTerrain is { Ready: true };

    public string GetMenuSettingsPath() => OS.HasFeature("template")
        && MenuSettingsPath == "res://resources/menu/menu_flyover_defaults.tres"
            ? "user://menu_flyover_defaults.tres" : MenuSettingsPath;

    public MenuFlyoverSettings GetMenuTuningSettings() => _tuning.CopyValidated();

    public string GetFlyoverCaption() => $"SURFACE FLYOVER / {_tuning.AltitudeKm:0.#} KM / {_tuning.SpeedKmPerSecond:0.#} KM/S";

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
        float elevation = Mathf.DegToRad(_tuning.SunElevationDegrees);
        float azimuth = Mathf.DegToRad(_tuning.SunAzimuthDegrees);
        _sunDirection = new Vector3(Mathf.Sin(azimuth) * Mathf.Cos(elevation),
            Mathf.Sin(elevation), Mathf.Cos(azimuth) * Mathf.Cos(elevation));
        if (_sunLight is not null)
        {
            _sunLight.LightEnergy = _tuning.SunEnergy;
            Vector3 referenceUp = Mathf.Abs(_sunDirection.Dot(Vector3.Up)) > .99f ? Vector3.Forward : Vector3.Up;
            _sunLight.LookAt(_sunLight.GlobalPosition - _sunDirection, referenceUp);
        }
        if (_worldEnvironment?.Environment is Godot.Environment environment)
        {
            environment.AmbientLightEnergy = _tuning.AmbientEnergy;
            environment.GlowEnabled = _tuning.GlowEnabled;
            environment.GlowIntensity = _tuning.GlowIntensity;
        }
        if (_camera is not null)
        {
            _camera.Fov = _tuning.FieldOfView;
            _camera.RotationDegrees = new Vector3(-_tuning.PitchDegrees, 0f, 0f);
        }
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
        ["altitude_m"] = (_camera?.Position.Y ?? _tuning.AltitudeKm * 1000f) - SampleCameraHeightKilometers(CurrentRow()) * 1000f,
        ["travel_m"] = _travelMeters,
        ["speed_mps"] = _tuning.SpeedKmPerSecond * 1000f,
        ["pitch_degrees"] = _tuning.PitchDegrees,
        ["fov_degrees"] = _tuning.FieldOfView,
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
        var output = new TextureRect { Texture = _viewport.GetTexture(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore };
        output.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(output);
        Resized += () => { if (_viewport is not null) _viewport.Size = BoundedViewportSize(); };

        _camera = new Camera3D { Current = true, Near = 50f, Far = 12_000_000f, Fov = _tuning.FieldOfView, Position = new Vector3(0f, _tuning.AltitudeKm * 1000f, 0f) };
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
            _camera.Position = new Vector3(0f, (_tuning.AltitudeKm + _smoothedGroundKilometers) * 1000f, 0f);
        }
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
