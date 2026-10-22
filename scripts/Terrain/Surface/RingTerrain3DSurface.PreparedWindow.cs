using System;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Main-thread bulk handoff for a prepared 3 by 3 Terrain3D window.</summary>
public partial class RingTerrain3DSurface
{
    private const int PreparedWindowSide = RegionSideSamples * RegionSideCount;
    private const int PreparedWindowOriginSamples = -RegionSideSamples;
    private long _committedPreparedRevision;
    private long _renderPreparedRevision;
    private long _queryPreparedRevision;
    private long _collisionPreparedRevision;
    private bool _preparedDirectRegions;
    private Image[]? _preparedOwnedImages;
    private Image[]? _preparedOwnedBiomeImages;
    private bool _preparedBiomeWeights;
    private double _lastPreparedUploadMilliseconds;
    private double _lastPreparedCollisionMilliseconds;
    private double _lastPreparedValidationMilliseconds;
    private double _lastPreparedNativeSetupMilliseconds;

    /// <summary>
    /// Publishes a complete canonical 384 by 384, 2m window.  The source arrays are copied before
    /// native state is changed, so callers may immediately reuse their job buffers.
    /// </summary>
    public bool CommitPreparedWindow(float[] absoluteHeights, Array<Image> heightImages,
        long anchorAround, int anchorAcross, float frameHeight, int acrossDirection, long revision,
        bool directRegions = true)
    {
        if (_field != null || _activePreparedSurface != null || _stagedPreparedSurface != null)
        {
            _lastError = "Direct prepared commits cannot replace this active owner. Use staging.";
            return false;
        }
        var validation = Stopwatch.StartNew();
        bool valid = ValidatePreparedWindow(absoluteHeights, heightImages, anchorAround, anchorAcross, frameHeight,
                acrossDirection, revision, out string error, out Vector2[] regionBounds);
        validation.Stop();
        _lastPreparedValidationMilliseconds = validation.Elapsed.TotalMilliseconds;
        if (!valid)
        {
            _lastError = error;
            return false;
        }

        // Do all managed copying before touching a previously playable native window.
        var copiedHeights = (float[])absoluteHeights.Clone();
        var copiedImages = new Image[heightImages.Count];
        for (int i = 0; i < copiedImages.Length; i++) copiedImages[i] = (Image)heightImages[i].Duplicate();
        RingSurfaceFrame nextFrame = new(anchorAround, anchorAcross, frameHeight,
            CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, 2, acrossDirection);

        bool published = false;
        try
        {
            _isReady = false;
            var nativeSetup = Stopwatch.StartNew();
            EnsurePreparedNative(nextFrame);
            nativeSetup.Stop();
            _lastPreparedNativeSetupMilliseconds = nativeSetup.Elapsed.TotalMilliseconds;
            var upload = Stopwatch.StartNew();
            _windowHeights = ToWindow(copiedHeights);
            _frame = nextFrame;
            UpdateFrameMaterialParameters();
            ApplyWorldFrameMaterial(); // cull coverage must reflect the new cache before native verification.
            if (directRegions) UploadPreparedDirect(copiedImages, regionBounds);
            else UploadPreparedImport(copiedImages, regionBounds);
            VerifyPreparedNativeUpload();
            upload.Stop(); _lastPreparedUploadMilliseconds = upload.Elapsed.TotalMilliseconds;

            var collision = Stopwatch.StartNew();
            CreateCollision();
            collision.Stop(); _lastPreparedCollisionMilliseconds = collision.Elapsed.TotalMilliseconds;

            _committedPreparedRevision = revision;
            _renderPreparedRevision = revision;
            _queryPreparedRevision = revision;
            _collisionPreparedRevision = revision;
            _preparedDirectRegions = directRegions;
            Image[]? previousImages = _preparedOwnedImages;
            _preparedOwnedImages = copiedImages;
            if (previousImages != null) foreach (Image image in previousImages) image.Dispose();
            _lastError = string.Empty;
            _isReady = true;
            published = true;
            return true;
        }
        catch (Exception exception)
        {
            // Native publication is not transactional; never leave new cache queries playable after a failed upload.
            _isReady = false;
            _lastError = exception.Message;
            GD.PushError("RingTerrain3DSurface prepared window: " + exception.Message);
            return false;
        }
        finally
        {
            if (!published) foreach (Image image in copiedImages) image.Dispose();
        }
    }

    public float SampleCommittedHeight(double around, double across)
    {
        if (_activePreparedSurface != null) return _activePreparedSurface.SampleCommittedHeight(around, across);
        if (!IsReady || _windowHeights == null || !double.IsFinite(around) || !double.IsFinite(across)) return float.NaN;
        double x = _frame.SignedAroundDeltaMeters(around) / WindowSpacingMeters + RegionSideSamples;
        double z = (across - _frame.AnchorAcrossMeters) * _frame.AcrossDirection / WindowSpacingMeters + RegionSideSamples;
        if (x < 0 || z < 0 || x > PreparedWindowSide - 1 || z > PreparedWindowSide - 1) return float.NaN;
        int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
        int x1 = Math.Min(x0 + 1, PreparedWindowSide - 1), z1 = Math.Min(z0 + 1, PreparedWindowSide - 1);
        float tx = (float)(x - x0), tz = (float)(z - z0);
        float a = _windowHeights[x0, z0], b = _windowHeights[x1, z0], c = _windowHeights[x0, z1], d = _windowHeights[x1, z1];
        // Same diagonal as CreateCollision: (a,b,c) then (b,d,c).
        return tx + tz <= 1 ? a + (b - a) * tx + (c - a) * tz : d + (c - d) * (1 - tx) + (b - d) * (1 - tz);
    }

    public Vector3 MapCanonicalPosition(double around, double across, double height) => _activePreparedSurface?.MapCanonicalPosition(around, across, height) ?? _frame.MapPosition(around, across, height);
    public double[] GetCanonicalPosition(Vector3 localPosition)
    {
        if (_activePreparedSurface != null) return _activePreparedSurface.GetCanonicalPosition(localPosition);
        RingSurfaceCoordinates value = _frame.Inverse(localPosition);
        return new[] { value.AroundMeters, value.AcrossMeters, value.Height };
    }

    public Dictionary GetPreparedDiagnostics()
    {
        if (_activePreparedSurface != null)
        {
            Dictionary value = _activePreparedSurface.GetPreparedDiagnostics();
            value["error"] = LastError;
            value["staged_revision"] = _stagedRevision;
            value["staged_ready"] = _stagedPreparedSurface?.IsReady ?? false;
            value["active_resident_count"] = 1;
            value["staged_resident_count"] = _stagedPreparedSurface == null ? 0 : 1;
            value["idle_resident_count"] = _idlePreparedSurface == null ? 0 : 1;
            value["preparation"] = (_stagedPreparedSurface ?? _activePreparedSurface).BudgetStatus();
            return value;
        }
        var diagnostics = new Dictionary
        {
        ["ready"] = IsReady,
        ["error"] = LastError,
        ["revision"] = _committedPreparedRevision,
        ["render_revision"] = _renderPreparedRevision,
        ["query_revision"] = _queryPreparedRevision,
        ["collision_revision"] = _collisionPreparedRevision,
        ["frame_around"] = _frame.AnchorAroundMeters,
        ["frame_across"] = _frame.AnchorAcrossMeters,
        ["frame_height"] = _frame.AnchorHeight,
        ["across_direction"] = _frame.AcrossDirection,
        ["sample_count"] = _windowHeights?.Length ?? 0,
        ["regions"] = RegionCount,
        ["direct_mode"] = _preparedDirectRegions,
        ["prepared_biomes"] = _preparedBiomeWeights,
        ["hole_vertices"] = _windowHoleCount,
        ["upload_ms"] = _lastPreparedUploadMilliseconds,
        ["collision_ms"] = _lastPreparedCollisionMilliseconds,
        ["projection_ms"] = _collisionProjectionMilliseconds,
        ["shape_ms"] = _collisionShapeMilliseconds,
        ["installation_ms"] = _collisionInstallationMilliseconds,
        ["upload_bytes"] = RegionSideCount * RegionSideCount * RegionSideSamples * RegionSideSamples * sizeof(float),
        ["native_setup_ms"] = _lastPreparedNativeSetupMilliseconds,
        ["validation_ms"] = _lastPreparedValidationMilliseconds,
        ["staged_revision"] = _stagedRevision, ["staged_ready"] = _stagedPreparedSurface?.IsReady ?? false,
        ["active_resident_count"] = _isReady ? 1 : 0, ["staged_resident_count"] = _stagedPreparedSurface == null ? 0 : 1
        };
        diagnostics["idle_resident_count"] = _idlePreparedSurface == null ? 0 : 1;
        diagnostics["preparation"] = (_stagedPreparedSurface ?? this).BudgetStatus();
        if (_isBudgetedPreparation) diagnostics["upload_bytes"] = _budgetUploadedBytes;
        return diagnostics;
    }

    private bool ValidatePreparedWindow(float[]? heights, Array<Image>? images, long around, int across, float frameHeight,
        int direction, long revision, out string error, out Vector2[] bounds)
    {
        bounds = System.Array.Empty<Vector2>();
        if (heights == null || heights.Length != PreparedWindowSide * PreparedWindowSide) { error = "Prepared height count must be 384 x 384."; return false; }
        if (images == null || images.Count != RegionSideCount * RegionSideCount) { error = "Prepared window requires nine RF images."; return false; }
        if (direction is not (1 or -1)) { error = "Prepared window direction is invalid."; return false; }
        long firstAcross = (long)across + direction * (long)PreparedWindowOriginSamples * 2;
        long lastAcross = (long)across + direction * (long)(PreparedWindowOriginSamples + PreparedWindowSide - 1) * 2;
        if (!float.IsFinite(frameHeight) || frameHeight < -8000 || frameHeight > 8000 || direction is not (1 or -1) || revision <= _committedPreparedRevision ||
            around % 2 != 0 || across % 2 != 0 || Math.Min(firstAcross, lastAcross) < 0 || Math.Max(firstAcross, lastAcross) >= CertifiedRingScale.RingWidthMeters) { error = "Prepared window frame or revision is invalid."; return false; }
        for (int i = 0; i < heights.Length; i++) if (!float.IsFinite(heights[i]) || heights[i] < -8000 || heights[i] > 8000) { error = "Prepared heights must be finite and inside +/-8000m."; return false; }
        bounds = new Vector2[images.Count];
        for (int i = 0; i < images.Count; i++)
        {
            Image? image = images[i];
            if (image == null || image.GetFormat() != Image.Format.Rf || image.GetWidth() != RegionSideSamples || image.GetHeight() != RegionSideSamples || image.GetData().Length != RegionSideSamples * RegionSideSamples * sizeof(float))
            { error = $"Prepared RF image {i} is invalid."; return false; }
            int regionX = i % RegionSideCount, regionZ = i / RegionSideCount;
            // Verify row-major samples and the z-major region order before any native state changes.
            byte[] bytes = image.GetData();
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            for (int row = 0; row < RegionSideSamples; row++) for (int column = 0; column < RegionSideSamples; column++)
            {
                float expected = heights[(regionZ * RegionSideSamples + row) * PreparedWindowSide + regionX * RegionSideSamples + column] - frameHeight;
                float actual = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, (row * RegionSideSamples + column) * sizeof(float)));
                if (!float.IsFinite(actual) || BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected)) { error = $"Prepared RF image {i} does not match its canonical offset."; return false; }
                minimum = MathF.Min(minimum, actual);
                maximum = MathF.Max(maximum, actual);
            }
            // The pinned addon initializes its maximum with positive FLT_MIN.
            // Supply exact RF bounds, including entirely negative and flat-zero regions.
            bounds[i] = new Vector2(minimum, maximum);
        }
        error = string.Empty; return true;
    }

    private bool ValidateGameplayPreparedWindow(float[]? heights, Array<Image>? heightImages, Array<Image>? biomeImages,
        long around, int across, float frameHeight, int direction, long revision, float minimumHeight, float maximumHeight,
        out string error, out Vector2[] bounds)
    {
        bounds = System.Array.Empty<Vector2>();
        if (!float.IsFinite(minimumHeight) || !float.IsFinite(maximumHeight) || minimumHeight > maximumHeight
            || minimumHeight < CertifiedRingScale.TerrainFloorMeters || maximumHeight > 8000.0f)
        {
            error = "Gameplay surface-height contract is invalid.";
            return false;
        }
        if (heights == null || heights.Length != PreparedWindowSide * PreparedWindowSide || heightImages == null || heightImages.Count != 9
            || biomeImages == null || biomeImages.Count != 9 || direction is not (1 or -1) || revision <= _committedPreparedRevision
            || around % 2 != 0 || across % 2 != 0 || !float.IsFinite(frameHeight) || frameHeight < minimumHeight || frameHeight > maximumHeight)
        {
            error = "Gameplay prepared window frame or payload is invalid.";
            return false;
        }
        for (int i = 0; i < heights.Length; i++)
            if (!float.IsFinite(heights[i]) || heights[i] < minimumHeight || heights[i] > maximumHeight)
            {
                error = "Gameplay prepared heights are outside the declared contract.";
                return false;
            }
        bounds = new Vector2[9];
        for (int i = 0; i < 9; i++)
        {
            Image? height = heightImages[i];
            Image? biome = biomeImages[i];
            if (height == null || height.GetFormat() != Image.Format.Rf || height.GetWidth() != RegionSideSamples || height.GetHeight() != RegionSideSamples || height.GetData().Length != RegionSideSamples * RegionSideSamples * sizeof(float)
                || biome == null || biome.GetFormat() != Image.Format.Rgba8 || biome.GetWidth() != RegionSideSamples || biome.GetHeight() != RegionSideSamples || biome.GetData().Length != RegionSideSamples * RegionSideSamples * 4)
            {
                error = $"Gameplay prepared map {i} is invalid.";
                return false;
            }
            byte[] bytes = height.GetData();
            float minimum = float.PositiveInfinity, maximum = float.NegativeInfinity;
            int regionX = i % RegionSideCount, regionZ = i / RegionSideCount;
            for (int row = 0; row < RegionSideSamples; row++) for (int column = 0; column < RegionSideSamples; column++)
            {
                float expected = heights[(regionZ * RegionSideSamples + row) * PreparedWindowSide + regionX * RegionSideSamples + column] - frameHeight;
                float actual = BitConverter.Int32BitsToSingle(BitConverter.ToInt32(bytes, (row * RegionSideSamples + column) * sizeof(float)));
                if (!float.IsFinite(actual) || BitConverter.SingleToInt32Bits(actual) != BitConverter.SingleToInt32Bits(expected))
                {
                    error = $"Gameplay RF image {i} does not match its canonical offset.";
                    return false;
                }
                minimum = MathF.Min(minimum, actual); maximum = MathF.Max(maximum, actual);
            }
            bounds[i] = new Vector2(minimum, maximum);
        }
        error = string.Empty;
        return true;
    }

    private void EnsurePreparedNative(RingSurfaceFrame frame)
    {
        if (_terrain != null && _terrainData != null) return;
        _frame = frame;
        _terrain = ClassDB.Instantiate("Terrain3D").AsGodotObject() as Node ?? throw new InvalidOperationException("Terrain3D native addon is unavailable.");
        _terrain.Name = "RingTerrain3D"; _terrain.Set("free_editor_textures", false); _terrain.Set("vertex_spacing", 2.0f); _terrain.Set("mesh_size", _preparedMeshSize); EnableCanonicalStandardGridDiagonal(_terrain); _terrain.Set("cull_margin", 1.0f);
        PreconfigureNativeBeforeTreeEntry(true);
        AddChild(_terrain); SilenceMousePickingViewport(); _terrain.Set("collision_mode", 0);
        _terrain.Set("mesh_lods", _preparedMeshLods);
        if (_terrain.Get("collision_mode").AsInt32() != 0) throw new InvalidOperationException("Terrain3D native collision could not be disabled.");
        _terrainData = _terrain.Call("get_data").AsGodotObject() ?? throw new InvalidOperationException("Terrain3D data is unavailable.");
        _terrain.Call("change_region_size", RegionSideSamples); ConfigureMaterial(); CreateMesherCamera();
    }

    private static float[,] ToWindow(float[] values)
    {
        var result = new float[PreparedWindowSide, PreparedWindowSide];
        for (int z = 0; z < PreparedWindowSide; z++) for (int x = 0; x < PreparedWindowSide; x++) result[x, z] = values[z * PreparedWindowSide + x];
        return result;
    }

    private void UploadPreparedImport(Image[] images, Vector2[] bounds)
    {
        if (_terrainData == null) throw new InvalidOperationException("Terrain3D data is unavailable.");
        for (int z = 0; z < RegionSideCount; z++) for (int x = 0; x < RegionSideCount; x++)
        {
            var maps = new Array<Image>(); maps.Resize(3); maps[0] = images[z * RegionSideCount + x];
            GodotObject? existing = _terrainData.Call("get_region", new Vector2I(x - 1, z - 1)).AsGodotObject();
            if (existing != null)
            {
                maps[1] = existing.Call("get_control_map").As<Image>();
                maps[2] = existing.Call("get_color_map").As<Image>();
            }
            _terrainData.Call("import_images", maps, new Vector3((x - 1) * RegionSideSamples * 2, 0, (z - 1) * RegionSideSamples * 2), 0.0f, 1.0f);
            _terrainData.Call("get_region", new Vector2I(x - 1, z - 1)).AsGodotObject()?.Call("set_height_range", bounds[z * RegionSideCount + x]);
        }
        _terrainData.Call("calc_height_range", false);
        _terrainData.Call("emit_signal", "height_maps_changed");
    }

    private void UploadPreparedDirect(Image[] images, Vector2[] bounds)
    {
        if (_terrainData == null) throw new InvalidOperationException("Terrain3D data is unavailable.");
        bool added = false;
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
        {
            Vector2I location = new(x, z);
            GodotObject? region = _terrainData.Call("get_region", location).AsGodotObject();
            if (region == null)
            {
                region = ClassDB.Instantiate("Terrain3DRegion").AsGodotObject() ?? throw new InvalidOperationException("Terrain3DRegion is unavailable.");
                region.Call("set_region_size", RegionSideSamples); region.Call("set_vertex_spacing", 2.0f); region.Call("set_location", location);
                region.Call("set_height_map", images[(z + 1) * RegionSideCount + x + 1]);
                if (_terrainData.Call("add_region", region, false).AsInt32() != 0) throw new InvalidOperationException($"Terrain3D rejected prepared region {location}.");
                added = true;
            }
            else
            {
                Image existingMap = region.Call("get_height_map").As<Image>();
                existingMap.BlitRect(images[(z + 1) * RegionSideCount + x + 1], new Rect2I(Vector2I.Zero, new Vector2I(RegionSideSamples, RegionSideSamples)), Vector2I.Zero);
            }
            region.Call("set_height_range", bounds[(z + 1) * RegionSideCount + x + 1]);
            region.Call("set_edited", true); region.Call("set_modified", true);
        }
        _terrainData.Call("calc_height_range", false);
        _terrainData.Call("update_maps", added ? 3 : 0, added, false);
        for (int z = -1; z <= 1; z++) for (int x = -1; x <= 1; x++)
            _terrainData.Call("get_region", new Vector2I(x, z)).AsGodotObject()?.Call("set_edited", false);
    }

    private void VerifyPreparedNativeUpload()
    {
        if (_terrainData == null || _windowHeights == null) throw new InvalidOperationException("Prepared cache is unavailable.");
        if (_terrainData.Call("get_regions_active").AsGodotArray().Count != 9) throw new InvalidOperationException("Terrain3D did not retain nine prepared regions.");
        int[] points = { 0, RegionSideSamples, PreparedWindowSide - 1 };
        foreach (int z in points) foreach (int x in points)
        {
            // Native get_height returns NaN on hole vertices, and the anchor sample may be one.
            if (_windowHoles != null && _windowHoles[z * PreparedWindowSide + x] != 0) continue;
            float actual = GetNativeHeight(new Vector3((x - RegionSideSamples) * 2, 0, (z - RegionSideSamples) * 2));
            float expected = _windowHeights[x, z] - _frame.AnchorHeight;
            if (!float.IsFinite(actual) || !Mathf.IsEqualApprox(actual, expected)) throw new InvalidOperationException($"Prepared native height mismatch at {x},{z}.");
        }
    }
}
