using System;
using System.Diagnostics;
using Godot;
using Godot.Collections;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>Main-thread publication units over private, worker-prepared geometry.</summary>
public partial class RingTerrain3DSurface
{
    private enum PreparationPhase { Idle, Setup, Regions, Maps, Collision, Trim, Finish, Ready, Cancelled, Error }
    private PreparationPhase _budgetPhase;
    private RingTerrain3DCollisionGeometry? _budgetGeometry;
    private bool _ownsBudgetGeometry;
    private Image[]? _budgetImages;
    private Image[]? _budgetBiomeImages;
    // Emitter-site hole payload (null when the window has no hole). The control images are referenced by their regions
    // after install, so they are kept alive in _preparedOwnedControlImages like the height and biome images.
    private Image[]? _budgetControlImages;
    private Image[]? _preparedOwnedControlImages;
    // Per resident region: true while its native control map carries hole bits that a later window must clear.
    private readonly bool[] _regionControlHoles = new bool[RegionSideCount * RegionSideCount];
    private byte[]? _windowHoles;
    private int _windowHoleCount;
    private Vector2[]? _budgetBounds;
    private long _budgetRevision;
    private int _budgetUploadIndex;
    private int _budgetTileIndex;
    private int _budgetTileCount;
    private int _budgetTileCells;
    private string _budgetError = string.Empty;
    // Resident native array state survives cancelled requests.
    private bool _budgetArraysReady;
    private double _budgetStepMaxMilliseconds;
    private int _budgetUploadOverruns;
    private int _budgetCollisionOverruns;
    private int _budgetUnitsLast;
    private int _budgetUnitsMax;
    private long _budgetUploadedBytes;
    private double _budgetBeginMilliseconds;

    public bool BeginBudgetedPreparedWindow(float[] heights, Array<Image> images,
        RingTerrain3DCollisionGeometry geometry, long around, int across, float frameHeight,
        int direction, long revision)
        => BeginBudgetedPreparedWindowInternal(heights, images, null, geometry, around, across, frameHeight,
            direction, revision, -8000.0f, 8000.0f, false);

    /// <summary>Begins a gameplay replacement with height and packed biome maps copied from worker-owned payloads.</summary>
    public bool BeginGameplayPreparedWindow(float[] heights, Array<Image> heightImages, Array<Image> biomeImages,
        RingTerrain3DCollisionGeometry geometry, long around, int across, float frameHeight, int direction,
        long revision, float minimumHeight, float maximumHeight)
        => BeginBudgetedPreparedWindowInternal(heights, heightImages, biomeImages, geometry, around, across, frameHeight,
            direction, revision, minimumHeight, maximumHeight, true, null, null);

    /// <summary>
    /// Gameplay replacement carrying the emitter-site hole payload: nine RF control images (hole bit 2) and the matching
    /// 384 x 384 mask the geometry was prepared with. Both null means no hole.
    /// </summary>
    public bool BeginGameplayPreparedWindowWithHoles(float[] heights, Array<Image> heightImages, Array<Image> biomeImages,
        RingTerrain3DCollisionGeometry geometry, long around, int across, float frameHeight, int direction,
        long revision, float minimumHeight, float maximumHeight, Array<Image>? controlImages, byte[]? holeMask)
        => BeginBudgetedPreparedWindowInternal(heights, heightImages, biomeImages, geometry, around, across, frameHeight,
            direction, revision, minimumHeight, maximumHeight, true, controlImages, holeMask);

    private bool BeginBudgetedPreparedWindowInternal(float[] heights, Array<Image> images, Array<Image>? biomeImages,
        RingTerrain3DCollisionGeometry geometry, long around, int across, float frameHeight, int direction,
        long revision, float minimumHeight, float maximumHeight, bool gameplay, Array<Image>? controlImages = null, byte[]? holeMask = null)
    {
        var begin = Stopwatch.StartNew();
        long live = _activePreparedSurface?._committedPreparedRevision ?? _committedPreparedRevision;
        if (_field != null || _stagingCandidate || geometry == null || revision <= live ||
            (_stagedPreparedSurface != null && revision <= _stagedRevision))
        {
            _stagingError = "Budgeted preparation owner or revision is invalid.";
            return false;
        }
        var validation = Stopwatch.StartNew();
        string error;
        Vector2[] bounds;
        bool valid = gameplay
            ? ValidateGameplayPreparedWindow(heights, images, biomeImages, around, across, frameHeight, direction,
                revision, minimumHeight, maximumHeight, out error, out bounds)
            : ValidatePreparedWindow(heights, images, around, across, frameHeight, direction,
                revision, out error, out bounds);
        if (!valid)
        {
            _stagingError = error;
            return false;
        }
        if (!geometry.Matches(heights, around, across, frameHeight, direction))
        {
            _stagingError = "Collision geometry does not match the prepared heights and frame.";
            return false;
        }
        if ((controlImages == null) != (holeMask == null) || (holeMask != null && !gameplay) ||
            (holeMask != null && holeMask.Length != PreparedWindowSide * PreparedWindowSide) || !ValidateControlImages(controlImages))
        {
            _stagingError = "Prepared hole payload is invalid.";
            return false;
        }
        if (!geometry.MatchesHoles(holeMask))
        {
            _stagingError = "Collision geometry does not match the prepared hole mask.";
            return false;
        }
        validation.Stop();

        // Validate and copy before retiring even an incomplete previous candidate.
        var copies = new Image[images.Count];
        Image[]? biomeCopies = biomeImages == null ? null : new Image[biomeImages.Count];
        Image[]? controlCopies = controlImages == null ? null : new Image[controlImages.Count];
        RingTerrain3DSurface? candidate = null;
        try
        {
            for (int i = 0; i < copies.Length; i++) copies[i] = (Image)images[i].Duplicate();
            if (biomeCopies != null) for (int i = 0; i < biomeCopies.Length; i++) biomeCopies[i] = (Image)biomeImages![i].Duplicate();
            if (controlCopies != null) for (int i = 0; i < controlCopies.Length; i++) controlCopies[i] = (Image)controlImages![i].Duplicate();
            float[,] window = ToWindow(heights);
            DiscardStagedWindow();
            candidate = _idlePreparedSurface ?? new RingTerrain3DSurface { Name = "RingTerrain3DBudgetedStage" };
            _idlePreparedSurface = null;
            candidate._stagingCandidate = true;
            candidate._isBudgetedPreparation = true;
            candidate.Visible = false;
            if (candidate.GetParent() == null) AddChild(candidate);
            candidate.ReleaseBudgetPayload();
            candidate._frame = new RingSurfaceFrame(around, across, frameHeight,
                CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, 2, direction);
            candidate._windowHeights = window;
            candidate._worldFrame = _worldFrame;
            candidate._handoffDistanceMeters = _handoffDistanceMeters;
            candidate._preparedMeshLods = _preparedMeshLods;
            candidate._preparedMeshSize = _preparedMeshSize;
            candidate.SetSmoothTerrainShading(_smoothTerrainShading);
            candidate.SetSurfaceAppearance(_surfaceDryRoughness, _surfaceWetRoughness, _rainWetnessStrength);
            candidate.SetSurfaceWetness(_surfaceWetness);
            candidate._budgetGeometry = geometry;
            candidate._budgetImages = copies;
            candidate._budgetBiomeImages = biomeCopies;
            candidate._budgetControlImages = controlCopies;
            candidate._windowHoles = holeMask == null ? null : (byte[])holeMask.Clone();
            candidate._windowHoleCount = RingTerrain3DApertureMask.CountHoles(holeMask);
            candidate._budgetBounds = bounds;
            candidate._budgetRevision = revision;
            candidate._budgetTileCount = geometry.TileCount;
            candidate._budgetTileCells = geometry.TileCells;
            candidate._budgetUploadIndex = candidate._budgetTileIndex = 0;
            candidate._budgetUnitsLast = candidate._budgetUnitsMax = 0;
            candidate._budgetUploadOverruns = candidate._budgetCollisionOverruns = 0;
            candidate._budgetStepMaxMilliseconds = candidate._lastPreparedUploadMilliseconds = 0;
            candidate._collisionShapeMilliseconds = candidate._collisionInstallationMilliseconds = 0;
            candidate._lastPreparedCollisionMilliseconds = candidate._lastPreparedNativeSetupMilliseconds = 0;
            candidate._budgetUploadedBytes = 0;
            candidate._preparedBiomeWeights = biomeCopies != null;
            candidate._lastPreparedValidationMilliseconds = validation.Elapsed.TotalMilliseconds;
            candidate._collisionProjectionMilliseconds = geometry.GetDiagnostics()["projection_ms"].AsDouble();
            candidate._budgetError = candidate._lastError = string.Empty;
            candidate._isReady = false;
            candidate._budgetPhase = PreparationPhase.Setup;
            if (candidate._collisionBody != null)
            {
                candidate._collisionBody.CollisionLayer = 0;
                candidate._collisionBody.CollisionMask = 0;
            }
            _stagedPreparedSurface = candidate;
            _stagedRevision = revision;
            _stagingError = _lastError = string.Empty;
            candidate._budgetBeginMilliseconds = begin.Elapsed.TotalMilliseconds;
            // Preview callers retain their geometry; gameplay explicitly transfers
            // its private worker payload after a successful begin.
            candidate._ownsBudgetGeometry = gameplay;
            return true;
        }
        catch (Exception exception)
        {
            foreach (Image? image in copies) image?.Dispose();
            if (biomeCopies != null) foreach (Image? image in biomeCopies) image?.Dispose();
            if (controlCopies != null) foreach (Image? image in controlCopies) image?.Dispose();
            if (candidate != null)
            {
                candidate._budgetImages = null;
                candidate._budgetBiomeImages = null;
                candidate._budgetControlImages = null;
                candidate._windowHoles = null;
                candidate.Free();
            }
            _stagingError = exception.Message;
            return false;
        }
    }

    public Dictionary AdvanceBudgetedPreparation(double uploadBudgetMs, double collisionBudgetMs)
    {
        if (!_stagingCandidate)
        {
            return _stagedPreparedSurface?.AdvanceBudgetedPreparation(uploadBudgetMs, collisionBudgetMs)
                ?? new Dictionary { ["ok"] = false, ["ready"] = false, ["error"] = "No pending preparation.", ["revision"] = 0 };
        }
        _budgetUnitsLast = 0;
        if (_budgetPhase is PreparationPhase.Ready or PreparationPhase.Error) return BudgetStatus();
        if (!double.IsFinite(uploadBudgetMs) || !double.IsFinite(collisionBudgetMs) || uploadBudgetMs < 0 || collisionBudgetMs < 0)
            return new Dictionary { ["ok"] = false, ["ready"] = false, ["error"] = "Budgets must be finite and nonnegative.", ["revision"] = _budgetRevision };
        if (_budgetGeometry == null || _budgetImages == null || _budgetBounds == null)
            return new Dictionary { ["ok"] = false, ["ready"] = false, ["error"] = "Preparation has no payload.", ["revision"] = _budgetRevision };
        var step = Stopwatch.StartNew();
        try
        {
            var upload = Stopwatch.StartNew();
            int uploadUnits = 0;
            while (uploadBudgetMs > 0 && IsUploadPhase() &&
                (uploadUnits == 0 || upload.Elapsed.TotalMilliseconds < uploadBudgetMs))
            {
                AdvanceUploadUnit();
                uploadUnits++;
            }
            upload.Stop();
            if (uploadUnits > 0 && upload.Elapsed.TotalMilliseconds > uploadBudgetMs) _budgetUploadOverruns++;
            _budgetUnitsLast += uploadUnits;

            var collision = Stopwatch.StartNew();
            int collisionUnits = 0;
            while (collisionBudgetMs > 0 && IsCollisionPhase() &&
                (collisionUnits == 0 || collision.Elapsed.TotalMilliseconds < collisionBudgetMs))
            {
                AdvanceCollisionUnit();
                collisionUnits++;
            }
            collision.Stop();
            if (collisionUnits > 0 && collision.Elapsed.TotalMilliseconds > collisionBudgetMs) _budgetCollisionOverruns++;
            _budgetUnitsLast += collisionUnits;
            if (collisionUnits > 0) _lastPreparedCollisionMilliseconds += collision.Elapsed.TotalMilliseconds;
        }
        catch (Exception exception)
        {
            _budgetError = _lastError = exception.Message;
            _budgetPhase = PreparationPhase.Error;
            _isReady = false;
            ReleaseBudgetPayload();
        }
        step.Stop();
        _budgetStepMaxMilliseconds = Math.Max(_budgetStepMaxMilliseconds, step.Elapsed.TotalMilliseconds);
        _budgetUnitsMax = Math.Max(_budgetUnitsMax, _budgetUnitsLast);
        return BudgetStatus();
    }

    private bool IsUploadPhase() => _budgetPhase is PreparationPhase.Setup or PreparationPhase.Regions or PreparationPhase.Maps;
    private bool IsCollisionPhase() => _budgetPhase is PreparationPhase.Collision or PreparationPhase.Trim or PreparationPhase.Finish;

    private void AdvanceUploadUnit()
    {
        var clock = Stopwatch.StartNew();
        if (_budgetPhase == PreparationPhase.Setup)
        {
            EnsurePreparedNative(_frame);
            // The Terrain3D material does not exist until native setup. Push
            // the cached facade appearance before this owner can be activated.
            ForwardSurfaceAppearance();
            SetMeshLods(_preparedMeshLods);
            SetMeshSize(_preparedMeshSize);
            SetHandoffDistance(_handoffDistanceMeters);
            UpdateFrameMaterialParameters();
            ApplyWorldFrameMaterial();
            _lastPreparedNativeSetupMilliseconds += clock.Elapsed.TotalMilliseconds;
            _budgetPhase = PreparationPhase.Regions;
            return;
        }
        if (_budgetPhase == PreparationPhase.Regions)
        {
            InstallBudgetRegion(_budgetUploadIndex++);
            if (_budgetUploadIndex == 9) _budgetPhase = PreparationPhase.Maps;
        }
        else
        {
            _terrainData!.Call("calc_height_range", false);
            if (!_budgetArraysReady)
            {
                // Native array creation is indivisible. Reused windows upload one
                // resident layer at a time without reconstructing texture arrays.
                _terrainData.Call("update_maps", 3, true, false);
                _budgetArraysReady = true;
                _budgetUploadedBytes += 9L * RegionSideSamples * RegionSideSamples * sizeof(float);
                if (_budgetBiomeImages != null) _budgetUploadedBytes += 9L * RegionSideSamples * RegionSideSamples * 4L;
            }
            _budgetPhase = PreparationPhase.Collision;
        }
        _lastPreparedUploadMilliseconds += clock.Elapsed.TotalMilliseconds;
    }

    private void InstallBudgetRegion(int index)
    {
        Vector2I location = new(index % 3 - 1, index / 3 - 1);
        GodotObject? region = _terrainData!.Call("get_region", location).AsGodotObject();
        bool controlTouched = false;
        if (region == null)
        {
            region = ClassDB.Instantiate("Terrain3DRegion").AsGodotObject()
                ?? throw new InvalidOperationException("Terrain3DRegion is unavailable.");
            region.Call("set_region_size", RegionSideSamples);
            region.Call("set_vertex_spacing", 2.0f);
            region.Call("set_location", location);
            region.Call("set_height_map", _budgetImages![index]);
            if (_budgetBiomeImages != null) region.Call("set_color_map", _budgetBiomeImages[index]);
            if (_budgetControlImages != null) region.Call("set_control_map", _budgetControlImages[index]);
            if (_terrainData.Call("add_region", region, false).AsInt32() != 0)
                throw new InvalidOperationException("Terrain3D rejected a prepared region.");
            _budgetArraysReady = false;
        }
        else
        {
            // A hole written by an earlier window must never persist into a window that has none there:
            // reused regions are overwritten with this window's control image or the default texel.
            if (_budgetControlImages != null || _regionControlHoles[index])
            {
                Image control = region.Call("get_control_map").As<Image>();
                Image? blank = _budgetControlImages == null ? RingTerrain3DApertureMask.CreateBlankControlImage() : null;
                try
                {
                    control.BlitRect(_budgetControlImages != null ? _budgetControlImages[index] : blank!,
                        new Rect2I(Vector2I.Zero, new Vector2I(RegionSideSamples, RegionSideSamples)), Vector2I.Zero);
                }
                finally { blank?.Dispose(); }
                controlTouched = true;
            }
            Image target = region.Call("get_height_map").As<Image>();
            target.BlitRect(_budgetImages![index], new Rect2I(Vector2I.Zero,
                new Vector2I(RegionSideSamples, RegionSideSamples)), Vector2I.Zero);
            if (_budgetBiomeImages != null)
            {
                Image color = region.Call("get_color_map").As<Image>();
                color.BlitRect(_budgetBiomeImages[index], new Rect2I(Vector2I.Zero,
                    new Vector2I(RegionSideSamples, RegionSideSamples)), Vector2I.Zero);
            }
        }
        region.Call("set_height_range", _budgetBounds![index]);
        region.Call("set_modified", true);
        region.Call("set_edited", _budgetArraysReady);
        if (_budgetArraysReady)
        {
            _terrainData.Call("update_maps", 0, false, false);
            _budgetUploadedBytes += RegionSideSamples * RegionSideSamples * sizeof(float);
            if (_budgetBiomeImages != null)
            {
                region.Call("set_edited", true);
                _terrainData.Call("update_maps", 2, false, true);
                _budgetUploadedBytes += RegionSideSamples * RegionSideSamples * 4L;
            }
            if (controlTouched)
            {
                region.Call("set_edited", true);
                _terrainData.Call("update_maps", 1, false, false);
                _budgetUploadedBytes += RegionSideSamples * RegionSideSamples * sizeof(float);
            }
        }
        _regionControlHoles[index] = _budgetControlImages != null;
        region.Call("set_edited", false);
    }

    private static bool ValidateControlImages(Array<Image>? controlImages)
    {
        if (controlImages == null) return true;
        if (controlImages.Count != RegionSideCount * RegionSideCount) return false;
        for (int i = 0; i < controlImages.Count; i++)
        {
            Image? image = controlImages[i];
            if (image == null || image.GetFormat() != Image.Format.Rf || image.GetWidth() != RegionSideSamples || image.GetHeight() != RegionSideSamples
                || image.GetData().Length != RegionSideSamples * RegionSideSamples * sizeof(float)) return false;
        }
        return true;
    }

    private void AdvanceCollisionUnit()
    {
        if (_budgetPhase == PreparationPhase.Collision)
        {
            InstallBudgetTile(_budgetTileIndex, _budgetGeometry!.GetTile(_budgetTileIndex));
            _budgetTileIndex++;
            if (_budgetTileIndex == _budgetTileCount) _budgetPhase = PreparationPhase.Trim;
        }
        else if (_budgetPhase == PreparationPhase.Trim)
        {
            if (_collisionBody!.GetChildCount() > _budgetTileCount)
            {
                var clock = Stopwatch.StartNew();
                var node = (CollisionShape3D)_collisionBody.GetChild(_collisionBody.GetChildCount() - 1);
                Shape3D? shape = node.Shape;
                node.Shape = null;
                node.Free();
                shape?.Dispose();
                _collisionInstallationMilliseconds += clock.Elapsed.TotalMilliseconds;
            }
            else _budgetPhase = PreparationPhase.Finish;
        }
        else
        {
            VerifyPreparedNativeUpload();
            _committedPreparedRevision = _renderPreparedRevision = _queryPreparedRevision = _collisionPreparedRevision = _budgetRevision;
            _preparedDirectRegions = true;
            _isReady = true;
            _budgetPhase = PreparationPhase.Ready;
            if (_preparedOwnedImages != null) foreach (Image image in _preparedOwnedImages) image.Dispose();
            _preparedOwnedImages = _budgetImages;
            _budgetImages = null;
            if (_preparedOwnedBiomeImages != null) foreach (Image image in _preparedOwnedBiomeImages) image.Dispose();
            _preparedOwnedBiomeImages = _budgetBiomeImages;
            _budgetBiomeImages = null;
            if (_preparedOwnedControlImages != null) foreach (Image image in _preparedOwnedControlImages) image.Dispose();
            _preparedOwnedControlImages = _budgetControlImages;
            _budgetControlImages = null;
            _budgetBounds = null;
            if (_ownsBudgetGeometry) _budgetGeometry?.Dispose();
            _budgetGeometry = null;
            _ownsBudgetGeometry = false;
        }
    }

    private void InstallBudgetTile(int index, Vector3[] vertices)
    {
        var shapeClock = Stopwatch.StartNew();
        // A fully emptied tile (every triangle touches a hole) cannot back a concave shape: park its node disabled.
        bool empty = vertices.Length == 0;
        ConcavePolygonShape3D? shape = empty ? null : new ConcavePolygonShape3D { Data = vertices };
        _collisionShapeMilliseconds += shapeClock.Elapsed.TotalMilliseconds;
        var installation = Stopwatch.StartNew();
        if (_collisionBody == null)
        {
            _collisionBody = new StaticBody3D { Name = "RingTerrainSurfaceCollision", CollisionLayer = 0, CollisionMask = 0 };
            AddChild(_collisionBody);
        }
        if (_collisionBody.Transform != _worldFrame) _collisionBody.Transform = _worldFrame;
        if (index < _collisionBody.GetChildCount())
        {
            var node = (CollisionShape3D)_collisionBody.GetChild(index);
            Shape3D? previous = node.Shape;
            node.Shape = shape;
            node.Disabled = empty;
            previous?.Dispose();
        }
        else _collisionBody.AddChild(new CollisionShape3D { Shape = shape, Disabled = empty });
        _collisionInstallationMilliseconds += installation.Elapsed.TotalMilliseconds;
    }

    private void ReleaseBudgetPayload()
    {
        if (_budgetImages != null) foreach (Image image in _budgetImages) image.Dispose();
        _budgetImages = null;
        if (_budgetBiomeImages != null) foreach (Image image in _budgetBiomeImages) image.Dispose();
        _budgetBiomeImages = null;
        if (_budgetControlImages != null) foreach (Image image in _budgetControlImages) image.Dispose();
        _budgetControlImages = null;
        _budgetBounds = null;
        if (_ownsBudgetGeometry) _budgetGeometry?.Dispose();
        _budgetGeometry = null;
        _ownsBudgetGeometry = false;
    }

    private void CancelBudgetPreparation()
    {
        ReleaseBudgetPayload();
        _isReady = false;
        _budgetPhase = PreparationPhase.Cancelled;
        _budgetUnitsLast = 0;
    }

    private Dictionary BudgetStatus() => new()
    {
        ["ok"] = _budgetError.Length == 0, ["ready"] = _budgetPhase == PreparationPhase.Ready,
        ["error"] = _budgetError, ["revision"] = _budgetRevision,
        ["phase"] = _budgetPhase.ToString().ToLowerInvariant(),
        ["tiles_done"] = _budgetTileIndex, ["tiles_total"] = _budgetTileCount, ["tile_cells"] = _budgetTileCells,
        ["regions_done"] = _budgetUploadIndex, ["units_last"] = _budgetUnitsLast, ["units_max"] = _budgetUnitsMax,
        ["step_max_ms"] = _budgetStepMaxMilliseconds, ["begin_ms"] = _budgetBeginMilliseconds,
        ["upload_budget_overruns"] = _budgetUploadOverruns, ["collision_budget_overruns"] = _budgetCollisionOverruns,
        ["validation_ms"] = _lastPreparedValidationMilliseconds, ["native_setup_ms"] = _lastPreparedNativeSetupMilliseconds,
        ["upload_ms"] = _lastPreparedUploadMilliseconds, ["projection_ms"] = _collisionProjectionMilliseconds,
        ["shape_ms"] = _collisionShapeMilliseconds, ["installation_ms"] = _collisionInstallationMilliseconds,
        ["upload_bytes"] = _budgetUploadedBytes,
        ["pending_geometry_bytes"] = _budgetGeometry?.GetDiagnostics()["geometry_bytes"].AsInt64() ?? 0,
    };
}
