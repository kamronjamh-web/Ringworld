using System;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Phase3;

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
    private RingTerrainSampler? _biomeSampler;
    private Transform3D _worldFrame = Transform3D.Identity;
    private float _handoffDistanceMeters;
    private Vector3 _lastLocalPhysicalCameraPosition;
    private bool _hasLocalPhysicalCameraPosition;
    private ulong _uploadedRevision = ulong.MaxValue;

    public RingSurfaceFrame Frame => _frame;
    public int RegionCount => IsReady ? RegionSideCount * RegionSideCount : 0;
    public bool IsReady { get; private set; }
    public string LastError { get; private set; } = string.Empty;
    public Node? TerrainNode => _terrain;
    public GodotObject? TerrainNativeData => _terrainData;
    public double LastRebuildMilliseconds { get; private set; }

    /// <summary>Applies the game world basis/origin in the shader and matching custom collider.</summary>
    public void SetWorldFrame(Transform3D worldFrame)
    {
        if (_worldFrame == worldFrame) return;
        _worldFrame = worldFrame;
        ApplyWorldFrameMaterial();
        if (_hasLocalPhysicalCameraPosition)
            SetMaterialParameter("ring_viewer_world_position", _worldFrame * _lastLocalPhysicalCameraPosition);
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
        IsReady = false; LastError = string.Empty; _frame = frame;
        try
        {
            DestroyNativeNodes();
            _terrain = ClassDB.Instantiate("Terrain3D").AsGodotObject() as Node ?? throw new InvalidOperationException("Terrain3D native addon is unavailable.");
            _terrain.Name = "RingTerrain3D";
            _terrain.Set("free_editor_textures", false);
            _terrain.Set("vertex_spacing", _field.SpacingMeters);
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
            CacheWindowHeights();
            CacheBiomeWeights();
            UploadRegions();
            VerifyNativeUpload();
            CreateCollision();
            _uploadedRevision = _field.Revision;
            IsReady = true;
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
        SetMaterialParameter("ring_viewer_world_position", _worldFrame * localPhysicalCameraPosition);
        if (!IsReady || _mesherCamera == null) return;
        RingSurfaceCoordinates coordinate = _frame.Inverse(localPhysicalCameraPosition);
        _mesherCamera.Position = new Vector3((float)coordinate.AroundOffsetMeters, localPhysicalCameraPosition.Y, localPhysicalCameraPosition.Z);
    }

    public void SetMeshLods(int meshLods)
    {
        if (_terrain == null) return;
        _terrain.Set("mesh_lods", Mathf.Clamp(meshLods, 1, 8));
    }

    private void ConfigureMaterial()
    {
        GodotObject material = _terrain!.Call("get_material").AsGodotObject() ?? throw new InvalidOperationException("Terrain3D did not initialize its material.");
        Shader shader = GD.Load<Shader>("res://materials/terrain3d_ring_surface.gdshader") ?? throw new InvalidOperationException("Ring Terrain3D shader is missing.");
        material.Set("world_background", 0);
        material.Set("shader_override", shader);
        material.Set("shader_override_enabled", true);
        // Empty texture assets enable Terrain3D's diagnostic checkerboard by default.
        // The prototype shader supplies its own surface colours.
        material.Set("show_checkered", false);
        material.Call("set_shader_param", "ring_radius_meters", (float)_frame.RadiusMeters);
        material.Call("set_shader_param", "ring_anchor_height", _frame.AnchorHeight);
        ApplyWorldFrameMaterial();
        SetMaterialParameter("ring_handoff_distance_meters", _handoffDistanceMeters);
        if (_hasLocalPhysicalCameraPosition)
            SetMaterialParameter("ring_viewer_world_position", _worldFrame * _lastLocalPhysicalCameraPosition);
        // At 0.2 repeats/metre, 1000m is an exact 200 texture repeats.
        SetMaterialParameter("ring_texture_anchor_around_meters", PositiveModulo(_frame.AnchorAroundMeters, 1000));
        SetMaterialParameter("ring_texture_anchor_across_meters", PositiveModulo(_frame.AnchorAcrossMeters, 1000));
        SetMaterialParameter("ring_across_direction", _frame.AcrossDirection);
        SetMaterialParameter("biome_texture_weights_enabled", _biomeSampler != null);
        SetMaterialParameter("biome_texture_scale", 0.20f);
        SetMaterialParameter("biome_grass_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/grass_albedo.png"));
        SetMaterialParameter("biome_dirt_albedo", GD.Load<Texture2D>("res://materials/third_party/voxelgame/dirt_albedo.png"));
        SetMaterialParameter("biome_desert_albedo", GD.Load<Texture2D>("res://materials/biomes/desert_sand_albedo.png"));
        SetMaterialParameter("biome_forest_albedo", GD.Load<Texture2D>("res://materials/biomes/forest_floor_albedo.png"));
        SetMaterialParameter("biome_tundra_albedo", GD.Load<Texture2D>("res://materials/biomes/tundra_ground_albedo.png"));
        SetMaterialParameter("biome_alpine_albedo", GD.Load<Texture2D>("res://materials/biomes/alpine_rock_albedo.png"));
    }

    private void ApplyWorldFrameMaterial()
    {
        SetMaterialParameter("ring_frame_basis", _worldFrame.Basis);
        SetMaterialParameter("ring_frame_origin", _worldFrame.Origin);
        if (_terrain != null)
        {
            float spacing = _field?.SpacingMeters ?? RingSurfaceHeightField.DefaultSpacingMeters;
            float halfWindow = RegionSideSamples * RegionSideCount * spacing * .5f;
            float heightExtent = 0.0f;
            if (_windowHeights != null) foreach (float height in _windowHeights)
                heightExtent = Mathf.Max(heightExtent, Mathf.Abs(height - _frame.AnchorHeight));
            // Cover every rotated local window axis and absolute height offset, but never inflate by ring radius.
            float localExtent = Mathf.Sqrt(halfWindow * halfWindow * 2.0f) + heightExtent;
            _terrain.Set("cull_margin", Mathf.Max(1.0f, localExtent + _worldFrame.Origin.Length()));
        }
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
        float spacing = _field?.SpacingMeters ?? RingSurfaceHeightField.DefaultSpacingMeters;
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
        if (_field == null || _windowHeights == null) return;
        if (_collisionBody != null) { _collisionBody.GetParent()?.RemoveChild(_collisionBody); _collisionBody.Free(); }
        _collisionBody = new StaticBody3D { Name = "RingTerrainSurfaceCollision" }; AddChild(_collisionBody);
        const int side = 193; const int half = side / 2;
        var triangles = new Vector3[(side - 1) * (side - 1) * 6]; int triangleIndex = 0;
        Vector3[,] points = new Vector3[side, side];
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            long around = _frame.AnchorAroundMeters + (long)(x - half) * (long)_field.SpacingMeters;
            int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (int)((z - half) * _field.SpacingMeters);
            float height = _windowHeights[x - half + RegionSideSamples, z - half + RegionSideSamples];
            points[x, z] = _frame.MapSample(around, across, height);
        }
        for (int z = 0; z < side - 1; z++) for (int x = 0; x < side - 1; x++)
        {
            Vector3 a = points[x, z], b = points[x + 1, z], c = points[x, z + 1], d = points[x + 1, z + 1];
            // Godot concave faces use clockwise winding when viewed from above.
            triangles[triangleIndex++] = a; triangles[triangleIndex++] = b; triangles[triangleIndex++] = c;
            triangles[triangleIndex++] = b; triangles[triangleIndex++] = d; triangles[triangleIndex++] = c;
        }
        var shape = new ConcavePolygonShape3D { Data = triangles };
        _collisionBody.AddChild(new CollisionShape3D { Shape = shape });
        _collisionBody.Transform = _worldFrame;
    }

    private void CacheWindowHeights()
    {
        if (_field == null) return;
        int side = RegionSideSamples * RegionSideCount;
        _baselineWindowHeights = new float[side, side];
        _windowHeights = new float[side, side];
        int origin = -RegionSideSamples;
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
        if (_field == null || _biomeSampler == null) { _biomeWeights = null; return; }
        int side = RegionSideSamples * RegionSideCount, origin = -RegionSideSamples;
        _biomeWeights = new Color[side, side];
        for (int z = 0; z < side; z++) for (int x = 0; x < side; x++)
        {
            long around = _frame.AnchorAroundMeters + (long)(x + origin) * (long)_field.SpacingMeters;
            int across = _frame.AnchorAcrossMeters + _frame.AcrossDirection * (int)((z + origin) * _field.SpacingMeters);
            _biomeWeights[x, z] = across >= 0 && across < _field.WidthMeters
                ? _biomeSampler.GetBiomeTextureWeights(around, across).ToVertexColor()
                : new Color(0, 0, 0, 0);
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
        if (_collisionBody != null) { _collisionBody.GetParent()?.RemoveChild(_collisionBody); _collisionBody.Free(); _collisionBody = null; }
        if (_mesherCamera != null) { _mesherCamera.GetParent()?.RemoveChild(_mesherCamera); _mesherCamera.Free(); _mesherCamera = null; }
        if (_terrain != null) { _terrain.GetParent()?.RemoveChild(_terrain); _terrain.Free(); _terrain = null; }
        _terrainData = null;
        _windowHeights = null;
        _baselineWindowHeights = null;
        _biomeWeights = null;
    }

    private void Fail(string error) { IsReady = false; LastError = error; GD.PushError("RingTerrain3DSurface: " + error); }
    private static float PositiveModulo(long value, int modulus) => (float)((value % modulus + modulus) % modulus);
    public override void _ExitTree() => DestroyNativeNodes();
}
