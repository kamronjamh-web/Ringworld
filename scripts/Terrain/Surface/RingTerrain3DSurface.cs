using System;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Bounded Terrain3D upload and matching curved collision for the certified surface prototype.</summary>
public partial class RingTerrain3DSurface : Node3D
{
    public const int RegionSideSamples = 128;
    public const int RegionSideCount = 3;
    private RingSurfaceHeightField? _field;
    private RingSurfaceFrame _frame;
    private Node? _terrain;
    private GodotObject? _terrainData;
    private Camera3D? _mesherCamera;
    private StaticBody3D? _collisionBody;
    // One canonical sample per native height texel. Reused by the upload and collider.
    private float[,]? _windowHeights;
    private float[,]? _baselineWindowHeights;
    private Color[,]? _biomeWeights;
    private bool _biomeWeightsFusedWithHeightCache;
    private RingTerrainSampler? _biomeSampler;
    private Transform3D _worldFrame = Transform3D.Identity;
    private float _handoffDistanceMeters;
    private Vector3 _lastLocalPhysicalCameraPosition;
    private bool _hasLocalPhysicalCameraPosition;
    private DirectionalLight3D? _atmosphereSun;
    private bool _atmosphereSunResolved;
    private ulong _atmosphereRevision = ulong.MaxValue;
    private ulong _uploadedRevision = ulong.MaxValue;

    private bool _isReady;
    private string _lastError = string.Empty;
    public RingSurfaceFrame Frame => _activePreparedSurface?.Frame ?? _frame;
    public int RegionCount => IsReady ? RegionSideCount * RegionSideCount : 0;
    public bool IsReady => _activePreparedSurface?.IsReady ?? _isReady;
    public string LastError => _stagingError.Length > 0 ? _stagingError :
        _lastError.Length > 0 ? _lastError : _activePreparedSurface?.LastError ?? string.Empty;
    public Node? TerrainNode => _activePreparedSurface?.TerrainNode ?? _terrain;
    public GodotObject? TerrainNativeData => _activePreparedSurface?.TerrainNativeData ?? _terrainData;
    public double LastRebuildMilliseconds { get; private set; }
    private double _collisionProjectionMilliseconds;
    private double _collisionShapeMilliseconds;
    private double _collisionInstallationMilliseconds;
    private double _lastCacheWindowHeightsMilliseconds;
    private double _lastCacheBiomeWeightsMilliseconds;
    private double _lastNativeSetupMilliseconds;
    private double _lastUploadRegionsMilliseconds;
    private double _lastCollisionMilliseconds;
    private long _rebuildCount;
    private string _lastRebuildReason = string.Empty;

    /// <summary>Last synchronous field-window rebuild phase timings, without per-frame logging.</summary>
    public Dictionary GetRebuildDiagnostics()
    {
        if (_activePreparedSurface != null) return _activePreparedSurface.GetRebuildDiagnostics();
        return new Dictionary
        {
            ["rebuild_count"] = _rebuildCount,
            ["reason"] = _lastRebuildReason,
            ["overall_ms"] = LastRebuildMilliseconds,
            ["cache_window_heights_ms"] = _lastCacheWindowHeightsMilliseconds,
            ["cache_biome_weights_ms"] = _lastCacheBiomeWeightsMilliseconds,
            ["native_setup_ms"] = _lastNativeSetupMilliseconds,
            ["upload_regions_ms"] = _lastUploadRegionsMilliseconds,
            ["collision_ms"] = _lastCollisionMilliseconds,
            ["collision_projection_ms"] = _collisionProjectionMilliseconds,
            ["collision_shape_ms"] = _collisionShapeMilliseconds,
            ["collision_installation_ms"] = _collisionInstallationMilliseconds
        };
    }

    /// <summary>Applies the game world basis/origin in the shader and matching custom collider.</summary>
    public void SetWorldFrame(Transform3D worldFrame)
    {
        if (_activePreparedSurface != null) { _activePreparedSurface.SetWorldFrame(worldFrame); _stagedPreparedSurface?.SetWorldFrame(worldFrame); _worldFrame = worldFrame; return; }
        if (_stagedPreparedSurface != null) _stagedPreparedSurface.SetWorldFrame(worldFrame);
        if (_worldFrame == worldFrame) return;
        _worldFrame = worldFrame;
        ApplyWorldFrameMaterial();
        if (_hasLocalPhysicalCameraPosition)
            SetMaterialParameter("ring_viewer_world_position", _worldFrame * _lastLocalPhysicalCameraPosition);
        ApplyAtmosphereRayFrame();
        if (_collisionBody != null) _collisionBody.Transform = _worldFrame;
    }

    /// <summary>Uses the canonical biome sampler to upload packed proxy-compatible COLOR maps.</summary>
    public void SetBiomeSampler(RingTerrainSampler? biomeSampler)
    {
        if (ReferenceEquals(_biomeSampler, biomeSampler)) return;
        _biomeSampler = biomeSampler;
        if (_field != null) RebuildWindow(_frame);
    }

    public void SetHandoffDistance(float meters)
    {
        if (_activePreparedSurface != null) { _activePreparedSurface.SetHandoffDistance(meters); _stagedPreparedSurface?.SetHandoffDistance(meters); _handoffDistanceMeters = Mathf.Max(0, meters); return; }
        if (_stagedPreparedSurface != null) _stagedPreparedSurface.SetHandoffDistance(meters);
        _handoffDistanceMeters = Mathf.Max(0.0f, meters);
        SetMaterialParameter("ring_handoff_distance_meters", _handoffDistanceMeters);
    }

    /// <summary>
    /// Overrides the handoff center with the gameplay Explorer feet position.
    /// The certified 84m radius sits inside the proxy coordinator's 64-104m
    /// band; this keeps Terrain3D and proxy distance ownership on one origin.
    /// </summary>
    public void SetHandoffOriginWorld(Vector3 position)
    {
        _activePreparedSurface?.SetHandoffOriginWorld(position);
        _stagedPreparedSurface?.SetHandoffOriginWorld(position);
        SetMaterialParameter("ring_viewer_world_position", position);
    }

    public void Initialize(RingSurfaceHeightField field, RingSurfaceFrame frame)
    {
        ArgumentNullException.ThrowIfNull(field);
        if (field.CircumferenceMeters != frame.CircumferenceMeters || field.SpacingMeters != frame.SpacingMeters)
            throw new ArgumentException("The height field and surface frame use different lattice definitions.");
        _field = field;
        RebuildWindow(frame);
    }

    public void RebuildWindow(RingSurfaceFrame frame)
    {
        if (_field == null) { Fail("Initialize must be called before rebuilding the Terrain3D window."); return; }
        var clock = Stopwatch.StartNew();
        _lastRebuildReason = _terrain == null ? "initialization" : "window_rebuild";
        _lastCacheWindowHeightsMilliseconds = 0.0;
        _lastCacheBiomeWeightsMilliseconds = 0.0;
        _lastNativeSetupMilliseconds = 0.0;
        _lastUploadRegionsMilliseconds = 0.0;
        _lastCollisionMilliseconds = 0.0;
        _collisionProjectionMilliseconds = 0.0;
        _collisionShapeMilliseconds = 0.0;
        _collisionInstallationMilliseconds = 0.0;
        _rebuildCount++;
        _isReady = false; _lastError = string.Empty; _frame = frame;
        try
        {
            var nativeSetup = Stopwatch.StartNew();
            DestroyNativeNodes();
            _terrain = ClassDB.Instantiate("Terrain3D").AsGodotObject() as Node ?? throw new InvalidOperationException("Terrain3D native addon is unavailable.");
            _terrain.Name = "RingTerrain3D";
            _terrain.Set("free_editor_textures", false);
            _terrain.Set("vertex_spacing", _field.SpacingMeters);
			EnableCanonicalStandardGridDiagonal(_terrain);
            // A 768m window bends less than 2cm at the certified radius; do not inflate by world radius.
            _terrain.Set("cull_margin", 1.0f);
            AddChild(_terrain);
            _terrain.Set("collision_mode", 0);
            if (_terrain.Get("collision_mode").AsInt32() != 0) throw new InvalidOperationException("Terrain3D native collision could not be disabled.");
            _terrainData = _terrain.Call("get_data").AsGodotObject() ?? throw new InvalidOperationException("Terrain3D did not initialize its owned Terrain3DData.");
            // Keep native regions in memory; the canonical surface journal owns persistence.
            // Region changes require the native data object initialized by tree entry.
            _terrain.Call("change_region_size", RegionSideSamples);
            ConfigureMaterial();
            CreateMesherCamera();
            nativeSetup.Stop();
            _lastNativeSetupMilliseconds = nativeSetup.Elapsed.TotalMilliseconds;

            var cacheHeights = Stopwatch.StartNew();
            CacheWindowHeights();
            cacheHeights.Stop();
            _lastCacheWindowHeightsMilliseconds = cacheHeights.Elapsed.TotalMilliseconds;

            var cacheBiome = Stopwatch.StartNew();
            CacheBiomeWeights();
            cacheBiome.Stop();
            _lastCacheBiomeWeightsMilliseconds = cacheBiome.Elapsed.TotalMilliseconds;

            var upload = Stopwatch.StartNew();
            UploadRegions();
            upload.Stop();
            _lastUploadRegionsMilliseconds = upload.Elapsed.TotalMilliseconds;
            VerifyNativeUpload();

            var collision = Stopwatch.StartNew();
            CreateCollision();
            collision.Stop();
            _lastCollisionMilliseconds = collision.Elapsed.TotalMilliseconds;
            _uploadedRevision = _field.Revision;
            _isReady = true;
        }
        catch (Exception exception) { Fail(exception.Message); DestroyNativeNodes(); }
        finally { clock.Stop(); LastRebuildMilliseconds = clock.Elapsed.TotalMilliseconds; }
    }

    public void RefreshEditedSurface()
    {
        if (!IsReady || _field == null || _terrainData == null || _uploadedRevision == _field.Revision) return;
        try
        {
            // HeightField supplies the sparse override enumerator; no procedural baseline is sampled on a brush refresh.
            RefreshCachedEdits();
            ApplyWorldFrameMaterial();
            UploadRegions(); VerifyNativeUpload(); CreateCollision(); _uploadedRevision = _field.Revision;
        }
        catch (Exception exception) { Fail(exception.Message); }
    }

    public void UpdateCamera(Vector3 localPhysicalCameraPosition)
    {
        _lastLocalPhysicalCameraPosition = localPhysicalCameraPosition;
        _hasLocalPhysicalCameraPosition = true;
        if (_stagedPreparedSurface != null && IsReady)
        {
            RingSurfaceCoordinates canonical = Frame.Inverse(localPhysicalCameraPosition);
            _stagedPreparedSurface.UpdateCamera(_stagedPreparedSurface.Frame.MapPosition(
                canonical.AroundMeters, canonical.AcrossMeters, canonical.Height));
        }
        if (_activePreparedSurface != null)
        {
            _activePreparedSurface.UpdateCamera(localPhysicalCameraPosition);
            return;
        }
        SetMaterialParameter("ring_viewer_world_position", _worldFrame * localPhysicalCameraPosition);
        ApplyAtmosphereMaterialParameters();
        ApplyAtmosphereRayFrame();
        if (!IsReady || _mesherCamera == null) return;
        RingSurfaceCoordinates coordinate = _frame.Inverse(localPhysicalCameraPosition);
        _mesherCamera.Position = new Vector3((float)coordinate.AroundOffsetMeters, localPhysicalCameraPosition.Y, localPhysicalCameraPosition.Z);
    }

    public void SetMeshLods(int meshLods)
    {
        _preparedMeshLods = Mathf.Clamp(meshLods, 1, 8);
        if (_activePreparedSurface != null) { _activePreparedSurface.SetMeshLods(meshLods); _stagedPreparedSurface?.SetMeshLods(meshLods); return; }
        if (_stagedPreparedSurface != null) _stagedPreparedSurface.SetMeshLods(meshLods);
        if (_terrain == null) return;
        _terrain.Set("mesh_lods", Mathf.Clamp(meshLods, 1, 8));
    }

    private void ConfigureMaterial()
    {
        // Each Terrain3D rebuild owns a new plugin material object. Static
        // model parameters must therefore publish again even at the same
        // global atmosphere revision.
        _atmosphereRevision = ulong.MaxValue;
        GodotObject material = _terrain!.Call("get_material").AsGodotObject() ?? throw new InvalidOperationException("Terrain3D did not initialize its material.");
        Shader shader = GD.Load<Shader>("res://materials/terrain3d_ring_surface.gdshader") ?? throw new InvalidOperationException("Ring Terrain3D shader is missing.");
        material.Set("world_background", 0);
        material.Set("shader_override", shader);
        material.Set("shader_override_enabled", true);
        // Empty texture assets enable Terrain3D's diagnostic checkerboard by default.
        // The prototype shader supplies its own surface colours.
        material.Set("show_checkered", false);
        UpdateFrameMaterialParameters();
        ApplyWorldFrameMaterial();
        SetMaterialParameter("ring_handoff_distance_meters", _handoffDistanceMeters);
        if (_hasLocalPhysicalCameraPosition)
            SetMaterialParameter("ring_viewer_world_position", _worldFrame * _lastLocalPhysicalCameraPosition);
        // At 0.2 repeats/metre, 1000m is an exact 200 texture repeats.
        SetMaterialParameter("ring_texture_anchor_around_meters", PositiveModulo(_frame.AnchorAroundMeters, 1000));
        SetMaterialParameter("ring_texture_anchor_across_meters", PositiveModulo(_frame.AnchorAcrossMeters, 1000));
        SetMaterialParameter("ring_across_direction", _frame.AcrossDirection);
        SetMaterialParameter("biome_texture_weights_enabled", _biomeSampler != null || _preparedBiomeWeights);
        SetMaterialParameter("biome_texture_scale", 0.20f);
        SetMaterialParameter("biome_grass_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
        SetMaterialParameter("biome_dirt_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
        SetMaterialParameter("biome_desert_albedo", GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
        SetMaterialParameter("biome_forest_albedo", GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
        SetMaterialParameter("biome_tundra_albedo", GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
        SetMaterialParameter("biome_alpine_albedo", GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
        ApplyAtmosphereMaterialParameters();
        ForwardSurfaceAppearance();
    }

    private void ApplyWorldFrameMaterial()
    {
        SetMaterialParameter("ring_frame_basis", _worldFrame.Basis);
        SetMaterialParameter("ring_frame_origin", _worldFrame.Origin);
        if (_terrain != null)
        {
            float spacing = WindowSpacingMeters;
            float halfWindow = RegionSideSamples * RegionSideCount * spacing * .5f;
            float heightExtent = 0.0f;
            if (_windowHeights != null) foreach (float height in _windowHeights)
                heightExtent = Mathf.Max(heightExtent, Mathf.Abs(height - _frame.AnchorHeight));
            // Cover every rotated local window axis and absolute height offset, but never inflate by ring radius.
            float localExtent = Mathf.Sqrt(halfWindow * halfWindow * 2.0f) + heightExtent;
            _terrain.Set("cull_margin", Mathf.Max(1.0f, localExtent + _worldFrame.Origin.Length()));
        }
    }

    private void UpdateFrameMaterialParameters()
    {
        if (_terrain == null) return;
        GodotObject? material = _terrain.Call("get_material").AsGodotObject();
        material?.Call("set_shader_param", "ring_radius_meters", (float)_frame.RadiusMeters);
        material?.Call("set_shader_param", "ring_anchor_height", _frame.AnchorHeight);
        SetMaterialParameter("ring_texture_anchor_around_meters", PositiveModulo(_frame.AnchorAroundMeters, 1000));
        SetMaterialParameter("ring_texture_anchor_across_meters", PositiveModulo(_frame.AnchorAcrossMeters, 1000));
        SetMaterialParameter("ring_across_direction", _frame.AcrossDirection);
        ApplyAtmosphereMaterialParameters();
    }

    private void ApplyAtmosphereMaterialParameters(bool force = false)
    {
        // Terrain3D owns a plugin material object rather than a ShaderMaterial,
        // so it receives the same public model values through its parameter API.
        float radius = (float)_frame.RadiusMeters;
        SetMaterialParameter("ring_atmosphere_camera_world", _hasLocalPhysicalCameraPosition ? _worldFrame * _lastLocalPhysicalCameraPosition : Vector3.Zero);
        if (!force && _atmosphereRevision == RingAtmosphereModel.Revision) return;
        _atmosphereRevision = RingAtmosphereModel.Revision;
        SetMaterialParameter("ring_atmosphere_enabled", RingAtmosphereModel.Enabled);
        SetMaterialParameter("ring_radius_meters", radius);
        SetMaterialParameter("ring_atmosphere_center_world", _worldFrame * new Vector3(0.0f, radius, 0.0f));
        SetMaterialParameter("ring_atmosphere_half_width_meters", _field?.WidthMeters * 0.5f ?? 0.0f);
        SetMaterialParameter("atmosphere_floor_depth_meters", RingAtmosphereModel.FloorDepthMeters);
        SetMaterialParameter("atmosphere_boundary_height_meters", RingAtmosphereModel.BoundaryHeightMeters);
        SetMaterialParameter("atmosphere_upper_height_meters", RingAtmosphereModel.UpperHeightMeters);
        SetMaterialParameter("atmosphere_boundary_extinction_meters", RingAtmosphereModel.BoundaryExtinctionMeters);
        SetMaterialParameter("atmosphere_upper_extinction_meters", RingAtmosphereModel.UpperExtinctionMeters);
        SetMaterialParameter("atmosphere_boundary_max_opacity", RingAtmosphereModel.BoundaryMaxOpacity);
        SetMaterialParameter("atmosphere_upper_max_opacity", RingAtmosphereModel.UpperMaxOpacity);
        SetMaterialParameter("atmosphere_total_max_opacity", RingAtmosphereModel.TotalMaxOpacity);
        SetMaterialParameter("atmosphere_clear_distance_meters", RingAtmosphereModel.ClearDistanceMeters);
        SetMaterialParameter("atmosphere_full_distance_meters", RingAtmosphereModel.FullDistanceMeters);
        SetMaterialParameter("atmosphere_high_altitude_clear_distance_meters", RingAtmosphereModel.HighAltitudeClearDistanceMeters);
        SetMaterialParameter("atmosphere_high_altitude_full_distance_meters", RingAtmosphereModel.HighAltitudeFullDistanceMeters);
        SetMaterialParameter("atmosphere_distance_altitude_start_meters", RingAtmosphereModel.DistanceAltitudeStartMeters);
        SetMaterialParameter("atmosphere_distance_altitude_end_meters", RingAtmosphereModel.DistanceAltitudeEndMeters);
        SetMaterialParameter("atmosphere_distance_haze_max_opacity", RingAtmosphereModel.DistanceHazeMaxOpacity);
        SetMaterialParameter("atmosphere_night_density_factor", RingAtmosphereModel.NightDensityFactor);
        SetMaterialParameter("atmosphere_scattering_energy", RingAtmosphereModel.ScatteringEnergy);
        SetMaterialParameter("atmosphere_aerosol", RingAtmosphereModel.Aerosol);
        SetMaterialParameter("atmosphere_forward_scattering", RingAtmosphereModel.ForwardScattering);
        SetMaterialParameter("atmosphere_sun_warmth", RingAtmosphereModel.SunWarmth);
        SetMaterialParameter("atmosphere_day_color", new Vector3(RingAtmosphereModel.DayColor.R, RingAtmosphereModel.DayColor.G, RingAtmosphereModel.DayColor.B));
        SetMaterialParameter("atmosphere_night_color", new Vector3(RingAtmosphereModel.NightColor.R, RingAtmosphereModel.NightColor.G, RingAtmosphereModel.NightColor.B));
    }

    private void ApplyAtmosphereRayFrame()
    {
        if (!_hasLocalPhysicalCameraPosition) return;
        RingSurfaceCoordinates canonical = _frame.Inverse(_lastLocalPhysicalCameraPosition);
        double width = _field?.WidthMeters
            ?? _biomeSampler?.RingAcrossWidthMeters
            ?? CertifiedRingScale.RingWidthMeters;
        Vector3 outwardLocal = new(
            _lastLocalPhysicalCameraPosition.X,
            -(float)(_frame.RadiusMeters - _frame.AnchorHeight - _lastLocalPhysicalCameraPosition.Y),
            0.0f);
        if (outwardLocal.LengthSquared() <= 0.000001f) return;
        Vector3 outwardWorld = (_worldFrame.Basis * outwardLocal.Normalized()).Normalized();
        Vector3 axisWorld = (_worldFrame.Basis * new Vector3(0.0f, 0.0f, _frame.AcrossDirection)).Normalized();
        DirectionalLight3D? sun = ResolveAtmosphereSun();
        Vector3 toSun = sun?.GlobalTransform.Basis.Z.Normalized() ?? Vector3.Up;
        var frame = new RingAtmosphereRayFrame(
            true,
            _worldFrame * _lastLocalPhysicalCameraPosition,
            (float)canonical.Height,
            outwardWorld,
            axisWorld,
            (float)canonical.AcrossMeters,
            (float)(width - canonical.AcrossMeters),
            1.0f,
            toSun);
        frame.Apply(SetMaterialParameter);
        float inwardDotSun = (-outwardWorld).Dot(toSun);
        SetMaterialParameter("ring_atmosphere_day_factor", Mathf.SmoothStep(-0.2f, 0.35f, inwardDotSun));
    }

    private DirectionalLight3D? ResolveAtmosphereSun()
    {
        if (_atmosphereSunResolved) return _atmosphereSun != null && GodotObject.IsInstanceValid(_atmosphereSun) ? _atmosphereSun : null;
        Node? node = GetTree()?.Root;
        if (node == null) return null;
        _atmosphereSun = FindDirectionalLight(node);
        _atmosphereSunResolved = true;
        return _atmosphereSun;
    }

    private static DirectionalLight3D? FindDirectionalLight(Node node)
    {
        if (node is DirectionalLight3D light && (light.Name == "StarLight" || light.Name == "Sun")) return light;
        foreach (Node child in node.GetChildren())
            if (FindDirectionalLight(child) is { } found) return found;
        return null;
    }

    private void SetMaterialParameter(StringName name, Variant value)
    {
        if (_terrain == null) return;
        GodotObject? material = _terrain.Call("get_material").AsGodotObject();
        material?.Call("set_shader_param", name, value);
    }

    /// <summary>Returns Terrain3D's imported planar height for diagnostics and headless certification.</summary>
    public float GetNativeHeight(Vector3 localPlanarPosition)
    {
        if (_activePreparedSurface != null) return _activePreparedSurface.GetNativeHeight(localPlanarPosition);
        if (_terrainData == null) throw new InvalidOperationException("Terrain3D data is not initialized.");
        return _terrainData.Call("get_height", localPlanarPosition).AsSingle();
    }

    private void UploadRegions()
    {
        if (_field == null || _terrainData == null || _windowHeights == null) return;
        int half = RegionSideCount / 2;
        for (int z = -half; z <= half; z++)
        for (int x = -half; x <= half; x++)
        {
            Image height = Image.CreateEmpty(RegionSideSamples, RegionSideSamples, false, Image.Format.Rf);
            for (int row = 0; row < RegionSideSamples; row++)
            for (int column = 0; column < RegionSideSamples; column++)
            {
                int cacheX = (x + half) * RegionSideSamples + column;
                int cacheZ = (z + half) * RegionSideSamples + row;
                float value = _windowHeights[cacheX, cacheZ] - _frame.AnchorHeight;
                height.SetPixel(column, row, new Color(value, 0, 0, 1));
            }
            var images = new Array<Image>(); images.Resize(3); images[0] = height;
            Image? biome = null;
            if (_biomeWeights != null)
            {
                biome = Image.CreateEmpty(RegionSideSamples, RegionSideSamples, false, Image.Format.Rgba8);
                for (int row = 0; row < RegionSideSamples; row++) for (int column = 0; column < RegionSideSamples; column++)
                    biome.SetPixel(column, row, _biomeWeights[(x + half) * RegionSideSamples + column, (z + half) * RegionSideSamples + row]);
                images[2] = biome;
            }
            _terrainData.Call("import_images", images, new Vector3(x * RegionSideSamples * _field.SpacingMeters, 0, z * RegionSideSamples * _field.SpacingMeters), 0.0f, 1.0f);
            height.Dispose();
            biome?.Dispose();
        }
    }

    private void CreateMesherCamera()
    {
        _mesherCamera = new Camera3D { Name = "RingTerrainMesherCamera", Current = false };
        AddChild(_mesherCamera);
        _terrain!.Call("set_camera", _mesherCamera);
    }

    private void VerifyNativeUpload()
    {
        if (_terrainData == null || _windowHeights == null) return;
        int nativeRegionCount = _terrainData.Call("get_regions_active").AsGodotArray().Count;
        if (nativeRegionCount != RegionSideCount * RegionSideCount)
            throw new InvalidOperationException($"Terrain3D imported {nativeRegionCount} regions; expected {RegionSideCount * RegionSideCount}.");
        float spacing = WindowSpacingMeters;
        float[] regionSamples =
        {
            -RegionSideSamples * spacing * .5f,
            0.0f,
            RegionSideSamples * spacing
        };
        foreach (float z in regionSamples)
        foreach (float x in regionSamples)
        {
            int cacheX = Mathf.RoundToInt(x / spacing) + RegionSideSamples;
            int cacheZ = Mathf.RoundToInt(z / spacing) + RegionSideSamples;
            float expected = _windowHeights[cacheX, cacheZ] - _frame.AnchorHeight;
            float actual = GetNativeHeight(new Vector3(x, 0.0f, z));
            if (!float.IsFinite(actual) || !Mathf.IsEqualApprox(actual, expected))
                throw new InvalidOperationException($"Terrain3D height mismatch at local ({x}, {z}): imported {actual}, expected {expected}.");
        }
    }

    private void CreateCollision()
    {
        if (_windowHeights == null) return;
        if (_collisionBody != null) { _collisionBody.GetParent()?.RemoveChild(_collisionBody); _collisionBody.Free(); }
        _collisionBody = new StaticBody3D
        {
            Name = "RingTerrainSurfaceCollision",
            CollisionLayer = _stagingCandidate ? 0u : 1u,
            CollisionMask = _stagingCandidate ? 0u : 1u
        };
        AddChild(_collisionBody);
        const int side = 193;
        // Preserve the original centered 193x193 collider footprint for both field and prepared uploads.
        const int firstCache = RegionSideSamples - side / 2;
        var projection = Stopwatch.StartNew();
        var triangles = new Vector3[(side - 1) * (side - 1) * 6]; int triangleIndex = 0;
        Vector3[,] points = new Vector3[side, side];
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            int cacheX = firstCache + x, cacheZ = firstCache + z;
            long around = _frame.AnchorAroundMeters + (long)(cacheX - RegionSideSamples) * (long)WindowSpacingMeters;
            int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (cacheZ - RegionSideSamples) * (int)WindowSpacingMeters;
            float height = _windowHeights[cacheX, cacheZ];
            points[x, z] = _frame.MapSample(around, across, height);
        }
        for (int z = 0; z < side - 1; z++) for (int x = 0; x < side - 1; x++)
        {
            Vector3 a = points[x, z], b = points[x + 1, z], c = points[x, z + 1], d = points[x + 1, z + 1];
            // Godot concave faces use clockwise winding when viewed from above.
            triangles[triangleIndex++] = a; triangles[triangleIndex++] = b; triangles[triangleIndex++] = c;
            triangles[triangleIndex++] = b; triangles[triangleIndex++] = d; triangles[triangleIndex++] = c;
        }
        projection.Stop();
        _collisionProjectionMilliseconds = projection.Elapsed.TotalMilliseconds;
        var shapeTiming = Stopwatch.StartNew();
        var shape = new ConcavePolygonShape3D { Data = triangles };
        shapeTiming.Stop();
        _collisionShapeMilliseconds = shapeTiming.Elapsed.TotalMilliseconds;
        var installation = Stopwatch.StartNew();
        _collisionBody.AddChild(new CollisionShape3D { Shape = shape });
        _collisionBody.Transform = _worldFrame;
        installation.Stop();
        _collisionInstallationMilliseconds = installation.Elapsed.TotalMilliseconds;
    }

    private void CacheWindowHeights()
    {
        if (_field == null) return;
        _biomeWeightsFusedWithHeightCache = false;
        int side = RegionSideSamples * RegionSideCount;
        _baselineWindowHeights = new float[side, side];
        _windowHeights = new float[side, side];
        int origin = -RegionSideSamples;
        if (CanFuseRuntimeHeightAndBiomeCache())
        {
            _biomeWeights = new Color[side, side];
            for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
            {
                long around = _frame.AnchorAroundMeters + (long)(x + origin) * (long)_field.SpacingMeters;
                int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (int)((z + origin) * _field.SpacingMeters);
                if (across >= 0 && across < _field.WidthMeters)
                {
                    _biomeSampler!.GetPresentationSurfaceHeightAndBiomeWeights(
                        around, across, out float baseline, out RingTerrainSampler.BiomeTextureWeights weights);
                    _baselineWindowHeights[x, z] = float.IsFinite(baseline)
                        ? Math.Clamp(baseline, _field.MinimumHeight, _field.MaximumHeight)
                        : _field.MinimumHeight;
                    _biomeWeights[x, z] = weights.ToVertexColor();
                }
                else
                {
                    _baselineWindowHeights[x, z] = _field.MinimumHeight;
                    _biomeWeights[x, z] = new Color(0, 0, 0, 0);
                }
            }
            System.Array.Copy(_baselineWindowHeights, _windowHeights, _baselineWindowHeights.Length);
            RefreshCachedEdits();
            _biomeWeightsFusedWithHeightCache = true;
            return;
        }
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            long around = _frame.AnchorAroundMeters + (long)(x + origin) * (long)_field.SpacingMeters;
            int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (int)((z + origin) * _field.SpacingMeters);
            _baselineWindowHeights[x, z] = across >= 0 && across < _field.WidthMeters ? _field.GetBaselineSample(around, across) : _field.MinimumHeight;
        }
        System.Array.Copy(_baselineWindowHeights, _windowHeights, _baselineWindowHeights.Length);
        RefreshCachedEdits();
    }

    private void CacheBiomeWeights()
    {
        if (_biomeWeightsFusedWithHeightCache) return;
        if (_field == null || _biomeSampler == null) { _biomeWeights = null; return; }
        int side = RegionSideSamples * RegionSideCount, origin = -RegionSideSamples;
        _biomeWeights = new Color[side, side];
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            long around = _frame.AnchorAroundMeters + (long)(x + origin) * (long)_field.SpacingMeters;
            int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (int)((z + origin) * _field.SpacingMeters);
            if (across >= 0 && across < _field.WidthMeters)
            {
                // Runtime lattice calls fuse the model diagnostic shared by
                // height and biome evaluation. The field still owns heights,
                // so its custom/editable baseline contract is unchanged here.
                if (_biomeSampler.UsesRuntimeSurfaceGeneration)
                {
                    _biomeSampler.GetPresentationSurfaceHeightAndBiomeWeights(around, across, out _, out RingTerrainSampler.BiomeTextureWeights weights);
                    _biomeWeights[x, z] = weights.ToVertexColor();
                }
                else _biomeWeights[x, z] = _biomeSampler.GetBiomeTextureWeights(around, across).ToVertexColor();
            }
            else _biomeWeights[x, z] = new Color(0, 0, 0, 0);
        }
    }

    private void RefreshCachedEdits()
    {
        if (_field == null || _windowHeights == null || _baselineWindowHeights == null) return;
        System.Array.Copy(_baselineWindowHeights, _windowHeights, _baselineWindowHeights.Length);
        int origin = -RegionSideSamples, side = _windowHeights.GetLength(0);
        int localAcross0 = _frame.AnchorAcrossMeters + _frame.AcrossDirection * origin * (int)_field.SpacingMeters;
        int localAcross1 = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (origin + side - 1) * (int)_field.SpacingMeters;
        int firstAcross = Math.Max(0, Math.Min(localAcross0, localAcross1));
        int lastAcross = Math.Min(_field.WidthMeters - (int)_field.SpacingMeters, Math.Max(localAcross0, localAcross1));
        if (firstAcross > lastAcross) return;
        foreach (RingSurfaceHeightEdit edit in _field.GetEditsInWindow(
            _frame.AnchorAroundMeters + origin * (long)_field.SpacingMeters,
            _frame.AnchorAroundMeters + (origin + side - 1) * (long)_field.SpacingMeters,
            firstAcross, lastAcross))
        {
            long delta = _frame.SignedAroundDeltaMeters(edit.AroundMeters);
            int x = (int)(delta / _field.SpacingMeters) - origin;
            int z = _frame.AcrossDirection * (edit.AcrossMeters - _frame.AnchorAcrossMeters) / (int)_field.SpacingMeters - origin;
            if ((uint)x < (uint)_windowHeights.GetLength(0) && (uint)z < (uint)_windowHeights.GetLength(1)) _windowHeights[x, z] = edit.Height;
        }
    }

    private void DestroyNativeNodes()
    {
        ReleaseBudgetPayload();
        _budgetArraysReady = false;
        if (_collisionBody != null) { _collisionBody.GetParent()?.RemoveChild(_collisionBody); _collisionBody.Free(); _collisionBody = null; }
        if (_mesherCamera != null) { _mesherCamera.GetParent()?.RemoveChild(_mesherCamera); _mesherCamera.Free(); _mesherCamera = null; }
        if (_terrain != null) { _terrain.GetParent()?.RemoveChild(_terrain); _terrain.Free(); _terrain = null; }
        _terrainData = null;
        _windowHeights = null;
        _baselineWindowHeights = null;
        _biomeWeights = null;
        _biomeWeightsFusedWithHeightCache = false;
        if (_preparedOwnedImages != null) { foreach (Image image in _preparedOwnedImages) image.Dispose(); _preparedOwnedImages = null; }
        if (_preparedOwnedBiomeImages != null) { foreach (Image image in _preparedOwnedBiomeImages) image.Dispose(); _preparedOwnedBiomeImages = null; }
        _preparedBiomeWeights = false;
    }

    private bool CanFuseRuntimeHeightAndBiomeCache()
    {
        if (_field == null || _biomeSampler == null || !_biomeSampler.UsesRuntimeSurfaceGeneration)
            return false;
        return _field.SpacingMeters == RingSurfaceHeightField.DefaultSpacingMeters
            && _field.CircumferenceMeters == _biomeSampler.AroundRingVoxelLength
            && _field.WidthMeters == _biomeSampler.RingAcrossWidthMeters
            && _field.TerrainDefinitionHash == _biomeSampler.TerrainDefinitionHash
            && _field.HasBaseline(_biomeSampler.GetPresentationSurfaceHeight);
    }

    private static void EnableCanonicalStandardGridDiagonal(Node terrain)
    {
        const string setter = "set_standard_grid_diagonal_flipped";
        if (terrain.HasMethod(setter))
        {
            terrain.Call(setter, true);
            return;
        }
        GD.PushWarning("RingTerrain3DSurface: Terrain3D native addon lacks standard_grid_diagonal_flipped; using its stock visual diagonal.");
    }

    private void Fail(string error) { _isReady = false; _lastError = error; GD.PushError("RingTerrain3DSurface: " + error); }
    private float WindowSpacingMeters => _field?.SpacingMeters ?? (_frame.SpacingMeters > 0 ? _frame.SpacingMeters : RingSurfaceHeightField.DefaultSpacingMeters);
    private static float PositiveModulo(long value, int modulus) => (float)((value % modulus + modulus) % modulus);
    public override void _ExitTree()
    {
        // Child surfaces receive their own exit notification. Do not keep their
        // disposed managed wrappers alive when this owner leaves the tree.
        _activePreparedSurface = null;
        _stagedPreparedSurface = null;
        _idlePreparedSurface = null;
        _stagedRevision = 0;
        _isReady = false;
        DestroyNativeNodes();
    }
}
