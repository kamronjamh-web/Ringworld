using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.Integration;

public partial class RingVoxelExplorerController : CharacterBody3D
{
    private const float CollisionEpsilon = 0.001f;
    private const double CollisionSectorSizeMeters = 32768.0;

    [Export] public NodePath HeadPath { get; set; } = new("Head");
    [Export] public NodePath CameraPath { get; set; } = new("Head/Camera3D");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public bool UseSmoothTerrainPhysics { get; set; }
    [Export] public bool UseTerrain3DSurfacePhysics { get; set; }
    [Export] public NodePath Terrain3DGameplayPath { get; set; } = new("../Terrain3DGameplay");
    [Export(PropertyHint.Range, "0.3,0.9,0.05")] public float BodyWidth { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "1.0,2.5,0.05")] public float BodyHeight { get; set; } = 1.8f;
    [Export(PropertyHint.Range, "0.0,1.0,0.05")] public float StepHeight { get; set; } = 0.6f;
    [Export(PropertyHint.Range, "35,70,1")] public float MaxWalkableSlopeDegrees { get; set; } = 55.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float GravityStrength { get; set; } = 32.0f;
    [ExportGroup("Gravity falloff")]
    [Export] public bool EnableAltitudeGravityFalloff { get; set; } = true;
    [Export(PropertyHint.Range, "1000,50000,100")] public float GravityFadeStartHeightMeters { get; set; } = 10000.0f;
    [Export(PropertyHint.Range, "1000,100000,100")] public float GravityZeroHeightMeters { get; set; } = 20000.0f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float MoveSpeed { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "1,60,0.1")] public float SprintSpeed { get; set; } = 14.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float GroundAcceleration { get; set; } = 45.0f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float AirAcceleration { get; set; } = 10.0f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float JumpVelocity { get; set; } = 8.5f;
    [Export(PropertyHint.Range, "1,100,0.1")] public float MaxFallSpeed { get; set; } = 45.0f;
    [ExportGroup("Curvature horizon correction")]
    [Export] public bool EnableCurvatureHorizonCorrection { get; set; } = true;
    [Export] public bool AlignNoclipInsideAtmosphere { get; set; } = true;
    [Export] public bool UseGravityZeroHeightAsAtmosphereBoundary { get; set; } = true;
    [Export(PropertyHint.Range, "1000,100000,250")]
    public float CurvatureCorrectionAtmosphereHeightMeters { get; set; } = 20000.0f;
    [Export(PropertyHint.Range, "10,10000,10")]
    public float PeriodicCorrectionTravelMeters { get; set; } = 1000.0f;
    [Export(PropertyHint.Range, "0.001,5.0,0.001")]
    public float MinimumHorizonErrorDegrees { get; set; } = 0.005f;
    [Export(PropertyHint.Range, "0.0001,1.0,0.0001")]
    public float CorrectionCompletionDegrees { get; set; } = 0.001f;
    [Export(PropertyHint.Range, "0.1,20.0,0.1")]
    public float PeriodicCorrectionResponse { get; set; } = 2.5f;
    [Export(PropertyHint.Range, "0.1,20.0,0.1")]
    public float AtmosphereEntryCorrectionResponse { get; set; } = 2.0f;
    [Export(PropertyHint.Range, "0.1,20.0,0.1")]
    public float NoclipExitCorrectionResponse { get; set; } = 4.0f;
    [Export(PropertyHint.Range, "1,250,0.5")] public float NoclipSpeed { get; set; } = 24.0f;
    [Export(PropertyHint.Range, "1,1000,0.5")] public float NoclipFastSpeed { get; set; } = 72.0f;
    [Export(PropertyHint.Range, "1,5000,1")] public float NoclipHyperSpeed { get; set; } = 240.0f;
    [Export(PropertyHint.Range, "1,1000000000,1")] public float NoclipWarpSpeed { get; set; } = 1200.0f;
    [Export(PropertyHint.Range, "1,1000000000,1")] public float NoclipTransRingSpeed { get; set; } = 1000000.0f;
    [Export(PropertyHint.Range, "1,10,0.1")] public float NoclipSprintMultiplier { get; set; } = 3.0f;
    [ExportGroup("Noclip speed dial")]
    [Export(PropertyHint.Range, "0.1,100,0.1")] public float NoclipMinimumSpeed { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "1000,1000000000,1000")] public float NoclipMaximumSpeed { get; set; } = 1000000.0f;
    [Export(PropertyHint.Range, "1.05,2.0,0.01")] public float NoclipWheelMultiplier { get; set; } = 1.35f;
    [Export(PropertyHint.Range, "1,30,0.5")] public float NoclipSpeedResponse { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "0.01,1.0,0.01")] public float MouseSensitivity { get; set; } = 0.12f;
    [ExportGroup("Swimming")]
    [Export] public bool EnableSwimming { get; set; } = true;
    [Export(PropertyHint.Range, "1,20,0.1")] public float SwimSpeed { get; set; } = 5.5f;
    [Export(PropertyHint.Range, "1,30,0.1")] public float SwimSprintSpeed { get; set; } = 8.5f;
    [Export(PropertyHint.Range, "1,40,0.5")] public float SwimAcceleration { get; set; } = 12.0f;
    [Export(PropertyHint.Range, "1,15,0.1")] public float SwimVerticalSpeed { get; set; } = 4.5f;
    [Export(PropertyHint.Range, "0,30,0.5")] public float SwimBuoyancyResponse { get; set; } = 8.0f;
    [Export(PropertyHint.Range, "0.1,3,0.05")] public float SwimSurfaceFloatDepthMeters { get; set; } = 1.15f;
    [Export(PropertyHint.Range, "0.05,2,0.05")] public float SwimEnterDepthMeters { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0.01,1,0.05")] public float SwimExitDepthMeters { get; set; } = 0.15f;
    [ExportGroup("Dynamic water tools")]
    [Export] public bool EnableDynamicWaterTools { get; set; } = true;
    [Export] public Key PlaceWaterSourceKey { get; set; } = Key.F;
    [Export] public Key RemoveWaterKey { get; set; } = Key.G;
    [Export(PropertyHint.Range, "1,24,0.5")] public float WaterToolReachMeters { get; set; } = 7.0f;
    [ExportGroup("Cross-ring flight")]
    [Export] public bool EnableCrossRingFlightHandoff { get; set; } = true;
    [Export(PropertyHint.Range, "10000,2000000,10000")] public float CrossRingPreloadHeightMeters { get; set; } = 250000.0f;
    [Export] public bool AutoLandAfterCrossRingFlight { get; set; } = true;
    [Export(PropertyHint.Range, "0.01,10.0,0.01")] public float CrossRingLandingClearanceMeters { get; set; } = 0.05f;
    [ExportGroup("Precision")]
    [Export] public bool UseCameraRelativeTopLevelTransform { get; set; } = true;
    [ExportGroup("Spawn")]
    [Export(PropertyHint.Range, "0.1,10.0,0.1")] public float SpawnTerrainSettleTimeoutSeconds { get; set; } = 3.0f;
    [Export] public bool RecoverSavedPositionBelowTerrain { get; set; } = true;

    private Node3D? _head;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smoothTerrain;
    private CollisionShape3D? _bodyCollisionShape;
    private RingCanonicalPosition _canonicalPosition;
    private RingCanonicalPosition _configuredCanonicalSpawn;
    private bool _hasConfiguredCanonicalSpawn;
    private double _collisionOriginAround;
    private double _collisionOriginAcross;
    private Vector3 _canonicalVelocity;
    private float _yaw;
    private float _pitch;
    private bool _grounded;
    private bool _noclip;
    private int _noclipSpeedTier;
    private float _noclipTargetSpeed;
    private float _noclipCurrentSpeed;
    private bool _noclipSpeedInitialized;
    private bool _benchmarkOverrideActive;
    private bool _crossRingTransitActive;
    private bool _crossRingCenterCrossed;
    private float _crossRingVerticalControlSign = 1.0f;
    private bool _cameraBasisInitialized;
    private bool _pendingPhysicsInterpolationReset;
    private bool _curvatureAtmosphereStateInitialized;
    private bool _wasInsideCurvatureAtmosphere;
    private bool _curvatureCorrectionActive;
    private bool _curvatureCorrectionPending;
    private float _pendingCurvatureCorrectionResponse;
    private float _activeCurvatureCorrectionResponse;
    private double _curvatureCorrectionAnchorAroundMeters;
    private long _lastSurfaceAltitudeAroundCell = long.MinValue;
    private int _lastSurfaceAltitudeAcrossCell = int.MinValue;
    private ulong _lastSurfaceAltitudeEditVersion = ulong.MaxValue;
    private int _cachedSurfaceAltitudeHeight;
    private bool _crossRingSmokeTest;
    private int _crossRingSmokeFrames;
    private bool _swimmingSmokeTest;
    private bool _swimmingSmokeInitialized;
    private int _swimmingSmokeFrames;
    private double _swimmingSmokeStartVertical;
    private bool _swimmingSmokeUnderwaterConfirmed;
    private bool _waterAliasingSmokeTest;
    private bool _waterAliasingSmokeInitialized;
    private int _waterAliasingSmokeFrames;
    private bool _precisionMotionSmokeTest;
    private bool _precisionJumpStarted;
    private bool _precisionJumpAirborne;
    private int _precisionMotionFrames;
    private int _precisionQuantizedFrames;
    private double _precisionMaxTransformError;
    private double _precisionMaxStepError;
    private RingCanonicalPosition _precisionPreviousCanonical;
    private Vector3 _precisionPreviousWorld;
    private Vector3 _precisionPreviousExpectedWorld;
    private bool _precisionHasPreviousSample;
    private bool _spawnTerrainSettling;
    private bool _undergroundSpawnSettling;
    private double _spawnTerrainSettleRemaining;
    private bool _megaSpawnSmokeTest;
    private bool _smoothPhysicalTerrainSmokeTest;
    private int _megaSpawnSmokeFrames;
    private int _megaSpawnStableFrames;
    private RingCanonicalPosition _lastSmoothGroundedPosition;
    private bool _hasLastSmoothGroundedPosition;
    private RingCanonicalPosition? _undergroundCheckpoint;
    private bool _smoothTerrainTraversalSmokeTest;
    private bool _certifiedWalkPrecisionProbe;
    private bool _smoothTerrainTraversalInitialized;
    private int _smoothTerrainTraversalFrames;
    private int _smoothTerrainTraversalStableFrames;
    private double _smoothTerrainTraversalStartAround;
    private int _probeCurrentResidencyFreezes;
    private int _probeTargetResidencyFreezes;
    private int _probeZeroMotionWalkingFrames;
    private int _probeZeroMotionBefore100Meters;
    private int _probeCurrentZeroMotionRun;
    private int _probeLongestZeroMotionRun;
    private bool _probeHasFirstHorizontalBlock;
    private RingCanonicalPosition _probeFirstHorizontalBlock;
    private string _probeFirstBlockCollider = "none";
    private Vector3 _probeFirstBlockNormal;
    private string _probeFirstBlockContacts = "none";
    private string _probeFirstBlockTerrain = "terrain=unavailable";
    private RingCanonicalPosition _probeLongestHorizontalBlock;
    private string _probeLongestBlockCollider = "none";
    private Vector3 _probeLongestBlockNormal;
    private string _probeTerrainSlopeReport = "terrain-slope=unavailable";
    private double _probeMaxWorldTransformCorrection;
    private bool _smoothDeepShaftSmokeTest;
    private bool _smoothDeepShaftInitialized;
    private int _smoothDeepShaftFrames;
    private int _smoothDeepShaftStableFrames;
    private long _smoothDeepShaftAround;
    private int _smoothDeepShaftAcross;
    private int _smoothDeepShaftFloor;
    // Ambient systems consume these cached probes rather than creating one
    // AudioStreamPlayer per water tile.  They are intentionally refreshed at a
    // low cadence: hydrology is deterministic and shore sound does not need a
    // physics-frame update.
    private float _waterAmbienceProbeRemaining;
    private float _waterAmbienceProximity;
    private float _waterAmbienceShoreProximity;
    private SurfaceWaterKind _waterAmbienceKind;

    public RingCanonicalPosition CanonicalPosition => _canonicalPosition;
    public Vector3 CanonicalVelocity => _canonicalVelocity;
    public float SavedYaw => _yaw;
    public float SavedPitch => _pitch;
    public bool NoclipEnabled => _noclip;
    public int NoclipSpeedTier => _noclipSpeedTier;
    public float NoclipTargetSpeedMetersPerSecond => _noclipTargetSpeed;
    public float NoclipCurrentSpeedMetersPerSecond => _noclipCurrentSpeed;
    public float ActualTravelSpeedMetersPerSecond => _canonicalVelocity.Length();
    public float FastTravelSpeedMetersPerSecond =>
        Mathf.Max(_noclipCurrentSpeed, _canonicalVelocity.Length());
    public float CurrentGravityFactor { get; private set; } = 1.0f;
    public float CurrentAltitudeAboveTerrainMeters { get; private set; }
    public float CurrentHorizonErrorDegrees { get; private set; }
    public bool CurvatureCorrectionActive => _curvatureCorrectionActive;
    public bool IsSwimming { get; private set; }
    public bool IsCameraUnderwater { get; private set; }
    public float CurrentWaterSurfaceHeightMeters { get; private set; }
    public float CurrentWaterDepthMeters { get; private set; }
    /// <summary>Last explicit safe cave checkpoint; never inferred from an unverified void.</summary>
    public RingCanonicalPosition? UndergroundCheckpoint => _undergroundCheckpoint;

    public Vector3 GetCanonicalSurfaceUp() => ResolveCanonicalSurfaceUp();

    public void SetMaxWalkableSlopeDegrees(float degrees)
    {
        MaxWalkableSlopeDegrees = float.IsFinite(degrees)
            ? Mathf.Clamp(degrees, 35.0f, 70.0f)
            : 55.0f;
        FloorMaxAngle = Mathf.DegToRad(MaxWalkableSlopeDegrees);
    }

    /// <summary>
    /// Resets the body to the exact local Certified-ring frame at its current
    /// canonical position. Intended for fixed diagnostic captures after a
    /// benchmark teleport, which otherwise preserves the prior presentation
    /// basis and can make the screen's vertical axis drift from local up.
    /// </summary>
    public bool ResetCanonicalOrientationForCapture()
    {
        if (_grid == null) return false;
        Vector3 origin = _grid.CanonicalRingPositionToWorld(_canonicalPosition);
        Vector3 ahead = _grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(
            _canonicalPosition.AroundMeters + 10.0,
            _canonicalPosition.VerticalMeters,
            _canonicalPosition.AcrossMeters));
        Vector3 up = GetCanonicalSurfaceUp().Normalized();
        Vector3 worldForward = ahead - origin;
        worldForward -= up * worldForward.Dot(up);
        if (worldForward.LengthSquared() < 0.000001f || up.LengthSquared() < 0.000001f)
            return false;
        worldForward = worldForward.Normalized();
        Vector3 worldRight = worldForward.Cross(up).Normalized();
        if (worldRight.LengthSquared() < 0.000001f) return false;
        GlobalBasis = new Basis(worldRight, up, -worldForward).Orthonormalized();
        _cameraBasisInitialized = true;
        _pendingPhysicsInterpolationReset = true;
        _curvatureAtmosphereStateInitialized = false;
        _curvatureCorrectionActive = false;
        _curvatureCorrectionPending = false;
        CurrentHorizonErrorDegrees = 0.0f;
        return true;
    }

    public Vector3 GetCanonicalAtmospherePositionMeters() => new(
        0.0f,
        (float)_canonicalPosition.VerticalMeters,
        (float)_canonicalPosition.AcrossMeters);

    public float GetCanonicalAtmosphereHeightMeters() => ResolveAltitudeAboveTerrainMeters();

    /// <summary>
    /// Selects a noclip preset without starting flight. This lets a world
    /// preset prime its intended travel speed while stationary systems still
    /// see a zero travel velocity.
    /// </summary>
    public void SelectNoclipSpeedTier(int tier)
    {
        _noclipSpeedTier = Mathf.Clamp(tier, 0, 4);
        if (_noclip)
            SetNoclipTargetSpeed(GetNoclipTierSpeed(), immediate: false);
    }

    /// <summary>
    /// Returns the explorer to the terrain column nearest its current canonical
    /// ring location. This is deliberately a canonical teleport, so it remains
    /// valid even after a long noclip trip or a floating-origin rebase.
    /// </summary>
    public bool ReturnToNearestSurface()
    {
        if (_grid?.TerrainSampler == null) return false;

        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        RingLodSurfaceEditSnapshot surfaceEdits = _smoothTerrain?.CaptureLodSurfaceEditSnapshot()
            ?? _grid.CaptureLodSurfaceEditSnapshot();
        int surface = surfaceEdits.GetSurfaceHeight(
            _grid.TerrainSampler,
            around,
            across);

        _canonicalPosition = new RingCanonicalPosition(
            around,
            surface + 1.05,
            across).Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _canonicalVelocity = Vector3.Zero;
        _noclip = false;
        _grounded = false;
        EndCrossRingTransit();
        RequestCurvatureCorrection(NoclipExitCorrectionResponse);
        BeginSpawnTerrainSettle();
        RecenterCollisionOrigin(force: true);
        _cameraBasisInitialized = false;
        ApplyWorldTransform();
        ApplyCameraRotation();
        _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
        GD.Print(
            $"Returned explorer to nearest surface: around={_canonicalPosition.AroundMeters / 1000.0:0.000}km, "
            + $"across={_canonicalPosition.AcrossMeters / 1000.0:0.000}km, surface={surface:0}m.");
        return true;
    }

    public bool TrySetUndergroundCheckpoint(out string feedback)
    {
        if (!IsSafeUndergroundCheckpoint(_canonicalPosition, requireResidency: true, out feedback))
            return false;
        _undergroundCheckpoint = _canonicalPosition.Wrap(_grid!.GetCanonicalRingCircumferenceMeters64());
        feedback = "Underground checkpoint set.";
        return true;
    }

    public bool TryRecoverToUndergroundCheckpoint(out string feedback)
    {
        if (_grid == null || _smoothTerrain == null || !_undergroundCheckpoint.HasValue)
        {
            feedback = "No underground checkpoint is available.";
            return false;
        }
        RingCanonicalPosition checkpoint = _undergroundCheckpoint.Value.Wrap(
            _grid.GetCanonicalRingCircumferenceMeters64());
        if (!IsSafeUndergroundCheckpoint(checkpoint, requireResidency: false, out feedback))
        {
            _undergroundCheckpoint = null;
            return false;
        }
        _smoothTerrain.SetCanonicalFocus(checkpoint);
        _grid.SetStreamingCanonicalFocusOverride(checkpoint);
        if (!_smoothTerrain.IsMovementTerrainResident(
                checkpoint.AroundMeters,
                checkpoint.VerticalMeters,
                checkpoint.AcrossMeters,
                BodyWidth * 0.5f,
                BodyHeight))
        {
            feedback = "Checkpoint terrain is streaming; retry recovery when it is resident.";
            return false;
        }
        if (!IsSafeUndergroundCheckpoint(checkpoint, requireResidency: true, out feedback))
            return false;
        _canonicalPosition = checkpoint;
        _canonicalVelocity = Vector3.Zero; _grounded = false; _noclip = false;
        EndCrossRingTransit();
        RecenterCollisionOrigin(force: true); _cameraBasisInitialized = false;
        ApplyWorldTransform(); ApplyCameraRotation();
        feedback = "Recovered to underground checkpoint."; return true;
    }

    private bool IsSafeUndergroundCheckpoint(
        in RingCanonicalPosition position,
        bool requireResidency,
        out string feedback)
    {
        if (_grid?.TerrainSampler == null || _smoothTerrain == null
            || _grid.TerrainSettings?.EnableDesignedCaveDensityComposition != true)
        {
            feedback = "Designed cave terrain is unavailable.";
            return false;
        }
        RingCanonicalPosition checkpoint = position.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        double halfWidth = Math.Max(0.15, BodyWidth * 0.5);
        if (checkpoint.AcrossMeters < halfWidth
            || checkpoint.AcrossMeters > _grid.RingAcrossWidthMeters - halfWidth)
        {
            feedback = "Checkpoint is too close to a structural sidewall.";
            return false;
        }
        int surface = _smoothTerrain.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
            _grid.TerrainSampler,
            (long)Math.Floor(checkpoint.AroundMeters),
            (int)Math.Floor(checkpoint.AcrossMeters));
        if (checkpoint.VerticalMeters >= surface - Math.Max(BodyHeight, 2.0f)
            || checkpoint.VerticalMeters <= _grid.GetLowerHullTopGlobalY() + 0.25)
        {
            feedback = "Checkpoint must be inside the playable underground volume.";
            return false;
        }
        if (requireResidency && !_smoothTerrain.IsMovementTerrainResident(
                checkpoint.AroundMeters,
                checkpoint.VerticalMeters,
                checkpoint.AcrossMeters,
                BodyWidth * 0.5f,
                BodyHeight))
        {
            feedback = "Checkpoint terrain is not fully resident yet.";
            return false;
        }

        double centerVertical = checkpoint.VerticalMeters + BodyHeight * 0.5;
        foreach ((double Around, double Vertical, double Across) probe in new[]
        {
            (checkpoint.AroundMeters, centerVertical, checkpoint.AcrossMeters),
            (checkpoint.AroundMeters - halfWidth, centerVertical, checkpoint.AcrossMeters),
            (checkpoint.AroundMeters + halfWidth, centerVertical, checkpoint.AcrossMeters),
            (checkpoint.AroundMeters, centerVertical, checkpoint.AcrossMeters - halfWidth),
            (checkpoint.AroundMeters, centerVertical, checkpoint.AcrossMeters + halfWidth),
            (checkpoint.AroundMeters, checkpoint.VerticalMeters + BodyHeight, checkpoint.AcrossMeters)
        })
        {
            if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(
                    probe.Around, probe.Vertical, probe.Across, out float clearance)
                || clearance <= 0.0f)
            {
                feedback = "Checkpoint does not have safe body clearance.";
                return false;
            }
        }

        bool hasFloor = false;
        for (double distance = 0.1; distance <= Math.Max(3.0, StepHeight + 0.5); distance += 0.1)
        {
            if (!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(
                    checkpoint.AroundMeters,
                    checkpoint.VerticalMeters - distance,
                    checkpoint.AcrossMeters,
                    out float floorDensity))
                break;
            if (floorDensity <= 0.0f) { hasFloor = true; break; }
        }
        if (!hasFloor)
        {
            feedback = "Checkpoint requires a solid floor within 3 metres.";
            return false;
        }
        feedback = string.Empty;
        return true;
    }

    /// <summary>
    /// Debug move to a deterministic designed-cave chamber near the current
    /// canonical location. Unlike benchmark/spawn APIs this does not replace
    /// the world's configured spawn; it enters noclip while cave LOD streams.
    /// </summary>
    public bool TryTeleportToNearestDesignedCave(out string feedback)
    {
        if (_grid?.TerrainSampler == null || _grid.TerrainSettings?.EnableDesignedCaveDensityComposition != true)
        {
            feedback = "Designed caves are disabled for this world.";
            return false;
        }

        RingTerrainSampler sampler = _grid.TerrainSampler;
        CaveTopologyDensityComposer composer = sampler.CaveTopologyDensityComposer;
        long circumference = (long)Math.Round(_grid.GetCanonicalRingCircumferenceMeters64());
        CaveTopologyPartition partition = sampler.CaveTopologyPartition;
        CaveProvinceDistribution distribution = sampler.CaveProvinceDistribution;
        CaveFeaturePlacementPolicy placement = sampler.CavePlacementPolicy;
        const int provinceSearchRadius = 16;
        const int maximumProvincePlans = 96;
        // The contract, not a hard-coded world profile, owns the usable cave
        // domain. Search every eligible depth band inside a bounded planar
        // neighborhood around the explorer.
        long verticalMinimum = Math.Max(
            placement.ProtectedRoofTopMeters + CaveFeaturePlacementPolicy.RoofClearanceMeters,
            placement.LowerHullTopMeters + CaveFeaturePlacementPolicy.LowerHullClearanceMeters);
        long verticalMaximum = placement.SurfaceFloorMeters - CaveFeaturePlacementPolicy.SurfaceClearanceMeters;
        long acrossMinimum = CaveFeaturePlacementPolicy.SidewallClearanceMeters;
        long acrossMaximum = placement.AcrossWidthMeters - CaveFeaturePlacementPolicy.SidewallClearanceMeters;
        long verticalProvinceMinimum = FloorDiv(verticalMinimum, CaveTopologyPartition.ProvinceVerticalMeters);
        long verticalProvinceMaximum = FloorDiv(verticalMaximum - 1, CaveTopologyPartition.ProvinceVerticalMeters);
        long acrossProvinceMinimum = FloorDiv(acrossMinimum, CaveTopologyPartition.ProvinceAcrossMeters);
        long acrossProvinceMaximum = FloorDiv(acrossMaximum - 1, CaveTopologyPartition.ProvinceAcrossMeters);
        long currentAroundProvince = partition.ResolveProvince(
            (long)Math.Floor(_canonicalPosition.AroundMeters), 0, 0).Address.Around;
        long currentAcrossProvince = partition.ResolveProvince(0, 0, (long)Math.Floor(_canonicalPosition.AcrossMeters)).Address.Across;
        CaveFeatureShape? nearest = null;
        double nearestDistanceSquared = double.PositiveInfinity;
        // Direct province plans avoid region captures altogether, so this
        // locator cannot exceed the snapshot generator's 64-candidate cap.
        // Radius/order/tie-break are fixed, making the result deterministic.
        int examinedPlans = 0;
        var examinedAddresses = new HashSet<CaveTopologyProvinceAddress>();
        for (int ring = 0; ring <= provinceSearchRadius && examinedPlans < maximumProvincePlans; ring++)
        {
            for (int aroundOffset = -ring; aroundOffset <= ring && examinedPlans < maximumProvincePlans; aroundOffset++)
            for (int acrossOffset = -ring; acrossOffset <= ring && examinedPlans < maximumProvincePlans; acrossOffset++)
            {
                if (Math.Max(Math.Abs(aroundOffset), Math.Abs(acrossOffset)) != ring)
                    continue;
                long aroundProvince = currentAroundProvince + aroundOffset;
                long acrossProvince = currentAcrossProvince + acrossOffset;
                if (acrossProvince < acrossProvinceMinimum || acrossProvince > acrossProvinceMaximum)
                    continue;
                for (long verticalProvince = verticalProvinceMinimum;
                     verticalProvince <= verticalProvinceMaximum && examinedPlans < maximumProvincePlans;
                     verticalProvince++)
                {
                    CaveTopologyProvince province = partition.ResolveNormalizedProvince(
                        aroundProvince, verticalProvince, acrossProvince);
                    if (!examinedAddresses.Add(province.Address)) continue;
                    examinedPlans++;
                    CaveProvincePlan plan = distribution.ResolvePlan(province);
                    if (!plan.Active) continue;
                    CaveChamberGraph graph = composer.Features.Maps.Graphs.Resolve(plan);
                    foreach (CaveChamberAnchor anchor in graph.Anchors.AsSpan())
                    {
                        CaveRegionFeature feature = composer.Features.CreateAnchorFeatureForLocator(anchor);
                        CaveFeatureShape shape = composer.Families.ResolveAnchorForLocator(feature);
                        if (!placement.PermitsConnectivity(feature, CaveTopologyDensityComposer.ChamberConnectivityRadiusMeters)) continue;
                        if (!new CaveGameplayIntegrationContract().Evaluate(true, placement, shape).IsEligible) continue;
                        double distanceSquared = CanonicalDistanceSquared(_canonicalPosition, shape, circumference);
                        if (distanceSquared < nearestDistanceSquared
                            || (distanceSquared == nearestDistanceSquared && (!nearest.HasValue || shape.Key < nearest.Value.Key)))
                        {
                            nearest = shape;
                            nearestDistanceSquared = distanceSquared;
                        }
                    }
                }
            }
        }

        if (!nearest.HasValue)
        {
            feedback = "No placement-permitted cave chamber was found in the bounded province search.";
            return false;
        }

        CaveFeatureShape target = nearest.Value;
        _canonicalPosition = new RingCanonicalPosition(target.Around, target.Vertical, target.Across)
            .Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _canonicalVelocity = Vector3.Zero;
        _noclip = true;
        EnsureNoclipSpeedInitialized();
        _grounded = false;
        EndCrossRingTransit();
        RecenterCollisionOrigin(force: true);
        _cameraBasisInitialized = false;
        ApplyWorldTransform();
        ApplyCameraRotation();
        _smoothTerrain?.SetCanonicalFocus(_canonicalPosition);
        _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
        CaveGameplayEligibility gameplayEligibility = new CaveGameplayIntegrationContract().Evaluate(true, placement, target);
        SetMeta("ring_cave_gameplay_handoffs", (int)gameplayEligibility.Handoffs);
        bool completeFamilyIsVisible = placement.Permits(target)
            && composer.Families.IsDepthEligibleForLocator(target);
        string chamberKind = completeFamilyIsVisible ? target.Family.ToString() : "connected";
        feedback = $"Teleported to {chamberKind} chamber ({Math.Sqrt(nearestDistanceSquared):0} m away).";
        GD.Print($"Cave debug teleport: {feedback} key={target.Key:X16}.");
        return true;
    }

    private static double CanonicalDistanceSquared(RingCanonicalPosition origin, CaveFeatureShape shape, long circumference)
    {
        double around = Math.Abs(shape.Around - origin.AroundMeters);
        around = Math.Min(around, Math.Max(0.0, (double)circumference - around));
        double vertical = shape.Vertical - origin.VerticalMeters;
        double across = shape.Across - origin.AcrossMeters;
        return around * around + vertical * vertical + across * across;
    }

    private static long FloorDiv(long value, long divisor) =>
        value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);

    public void SetCanonicalSpawnMeters(double aroundMeters, double verticalMeters, double acrossMeters)
    {
        if (!double.IsFinite(aroundMeters)
            || !double.IsFinite(verticalMeters)
            || !double.IsFinite(acrossMeters))
        {
            throw new ArgumentOutOfRangeException(nameof(aroundMeters));
        }

        _configuredCanonicalSpawn = new RingCanonicalPosition(
            aroundMeters,
            verticalMeters,
            acrossMeters);
        _hasConfiguredCanonicalSpawn = true;

        if (_grid == null)
        {
            return;
        }

        _canonicalPosition = _configuredCanonicalSpawn.Wrap(
            _grid.GetCanonicalRingCircumferenceMeters64());
        BeginSpawnTerrainSettle();
        _canonicalVelocity = Vector3.Zero;
        _grounded = false;
        RecenterCollisionOrigin(force: true);
        _cameraBasisInitialized = false;
        ApplyWorldTransform();
        ApplyCameraRotation();
        _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
    }

    private readonly record struct Box3(Vector3 Min, Vector3 Max);
    private readonly record struct MoveResult(Vector3 Position, Vector3 Applied, bool Grounded, bool HorizontalBlocked);

    public void RestoreSavedState(
        in RingCanonicalPosition position,
        Vector3 velocity,
        float yaw,
        float pitch,
        bool noclip,
        int noclipSpeedTier,
        float noclipSpeed = 0.0f,
        RingCanonicalPosition? undergroundCheckpoint = null)
    {
        if (_grid == null) return;
        _canonicalPosition = position.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _canonicalVelocity = velocity;
        _undergroundCheckpoint = undergroundCheckpoint?.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        bool restoredCaveVoid = IsAuthoritativeCaveVoid(_canonicalPosition);
        if (RecoverSavedPositionBelowTerrain && IsCanonicalPositionBelowTerrain(_canonicalPosition))
        {
            BeginSpawnTerrainSettle();
            _canonicalVelocity = Vector3.Zero;
            noclip = false;
            GD.PushWarning("Recovered saved player position from below deterministic terrain.");
        }
        else if (restoredCaveVoid)
        {
            _undergroundSpawnSettling = true;
            _spawnTerrainSettling = true;
            _spawnTerrainSettleRemaining = Math.Max(.1f, SpawnTerrainSettleTimeoutSeconds);
            _canonicalVelocity = Vector3.Zero;
        }
        _yaw = Mathf.PosMod(yaw, Mathf.Tau);
        _pitch = Mathf.Clamp(pitch, -85.0f, 85.0f);
        _noclip = noclip;
        _noclipSpeedTier = Mathf.Clamp(noclipSpeedTier, 0, 4);
        SetNoclipTargetSpeed(noclipSpeed > 0.0f ? noclipSpeed : GetNoclipTierSpeed(), immediate: true);
        _grounded = false;
        EndCrossRingTransit();
        RecenterCollisionOrigin(force: true);
        _cameraBasisInitialized = false;
        ApplyWorldTransform();
        ApplyCameraRotation();
        _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
    }

    public override void _Ready()
    {
        // A normal child transform would store the explorer near the physical
        // five-million-metre ring radius even after the parent origin shifts.
        // Top-level mode stores the already rebased global transform directly,
        // preserving sub-centimetre jump motion near the camera.
        if (UseCameraRelativeTopLevelTransform) TopLevel = true;
        _head = GetNodeOrNull<Node3D>(HeadPath);
        PhysicsInterpolationMode = PhysicsInterpolationModeEnum.On;
        if (_head != null)
            _head.PhysicsInterpolationMode = PhysicsInterpolationModeEnum.Off;
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        if (_grid == null)
        {
            GD.PushError("RingVoxelExplorerController requires a VoxelChunkGridManager.");
            SetPhysicsProcess(false);
            return;
        }

        _smoothTerrain = UseSmoothTerrainPhysics
            ? GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath)
            : null;
        _bodyCollisionShape = GetNodeOrNull<CollisionShape3D>("CollisionShape3D");
        if (_bodyCollisionShape != null)
        {
            _bodyCollisionShape.Disabled = !UseSmoothTerrainPhysics && !UseTerrain3DSurfacePhysics;
            if(_bodyCollisionShape.Shape is CapsuleShape3D capsule)
            {
                capsule.Radius=Mathf.Max(.1f,BodyWidth*.5f);
                capsule.Height=Mathf.Max(BodyHeight,capsule.Radius*2f);
                _bodyCollisionShape.Position=Vector3.Up*(BodyHeight*.5f);
            }
        }
        MotionMode = MotionModeEnum.Grounded;
        SetMaxWalkableSlopeDegrees(MaxWalkableSlopeDegrees);
        FloorStopOnSlope = true;
        SafeMargin = 0.03f;

        _canonicalPosition = _hasConfiguredCanonicalSpawn
            ? _configuredCanonicalSpawn.Wrap(_grid.GetCanonicalRingCircumferenceMeters64())
            : _grid.WorldToCanonicalRingPosition64(GlobalPosition);
        if (_hasConfiguredCanonicalSpawn) BeginSpawnTerrainSettle();
        EnsureNoclipSpeedInitialized();
        RecenterCollisionOrigin(force: true);
        if (!RunCrossRingHandoffSelfTest())
            GD.PushError("Cross-ring flight coordinate handoff self-test failed.");
        _crossRingSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--cross-ring-flight-smoke-test");
        _swimmingSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--swimming-smoke-test");
        _waterAliasingSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--water-aliasing-smoke-test");
        _precisionMotionSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--precision-motion-smoke-test");
        _smoothPhysicalTerrainSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument =>
            argument == "--smooth-physical-terrain-smoke-test");
        _smoothTerrainTraversalSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument =>
            argument == "--smooth-terrain-traversal-smoke-test");
        _certifiedWalkPrecisionProbe = Array.Exists(OS.GetCmdlineUserArgs(), argument =>
            argument == "--certified-walk-precision-probe");
        if (_certifiedWalkPrecisionProbe)
            _smoothTerrainTraversalSmokeTest = true;
        if (_certifiedWalkPrecisionProbe
            && Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--probe-spawn-side"))
        {
            _configuredCanonicalSpawn = _configuredCanonicalSpawn with { AroundMeters = 0.0 };
            _hasConfiguredCanonicalSpawn = true;
            _canonicalPosition = _configuredCanonicalSpawn;
            BeginSpawnTerrainSettle();
            RecenterCollisionOrigin(force: true);
        }
        _smoothDeepShaftSmokeTest = Array.Exists(OS.GetCmdlineUserArgs(), argument =>
            argument == "--smooth-deep-shaft-smoke-test");
        _megaSpawnSmokeTest = _smoothPhysicalTerrainSmokeTest
            || Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--mega-spawn-smoke-test");
        // This smoke certifies contact with the terrain mesh, not water-state
        // entry at a procedurally selected shoreline/ocean spawn.
        if(_smoothPhysicalTerrainSmokeTest||_smoothTerrainTraversalSmokeTest||_smoothDeepShaftSmokeTest) EnableSwimming=false;
        if(_smoothDeepShaftSmokeTest && (!UseSmoothTerrainPhysics || _smoothTerrain == null))
        {
            GD.PushError("Smooth deep shaft smoke requires active SmoothTerrainGrid physics.");
            GetTree().Quit(1);
            return;
        }
        if(_smoothDeepShaftSmokeTest) BeginSpawnTerrainSettle();
        if (_crossRingSmokeTest) BeginCrossRingSmokeTest();
        if(!UseSmoothTerrainPhysics && !UseTerrain3DSurfacePhysics)
        {
            SnapOutOfSolidOnStartup();
            Callable.From(SnapOutOfSolidOnStartup).CallDeferred();
        }
        ApplyWorldTransform();
        ApplyCameraRotation();
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw = Mathf.PosMod(_yaw - Mathf.DegToRad(motion.Relative.X * MouseSensitivity), Mathf.Tau);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -85.0f, 85.0f);
            ApplyCameraRotation();
        }
        else if (@event is InputEventKey key && key.Pressed && key.Keycode == Key.Escape)
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
        else if (@event is InputEventKey toggle && toggle.Pressed && !toggle.Echo && toggle.Keycode == Key.V)
        {
            _noclip = !_noclip;
            EnsureNoclipSpeedInitialized();
            _canonicalVelocity = Vector3.Zero;
            _grounded = false;
            if (!_noclip)
            {
                EndCrossRingTransit();
                // Do not let a missing destination collider restore an old
                // grounded location on the first physical frame after flight.
                // The current canonical location remains authoritative; the
                // smooth terrain grid will prewarm it below.
                _hasLastSmoothGroundedPosition = false;
                _smoothTerrain?.SetCanonicalFocus(_canonicalPosition);
                RequestCurvatureCorrection(NoclipExitCorrectionResponse);
            }
            GD.Print($"Ring voxel noclip: {(_noclip ? "ON" : "OFF")}");
        }
        else if (@event is InputEventMouseButton wheel
            && wheel.Pressed
            && _noclip
            && (wheel.ButtonIndex == MouseButton.WheelUp || wheel.ButtonIndex == MouseButton.WheelDown))
        {
            EnsureNoclipSpeedInitialized();
            float wheelSteps = Mathf.Max(0.1f, Mathf.Abs(wheel.Factor));
            float multiplier = Mathf.Pow(Mathf.Max(1.01f, NoclipWheelMultiplier), wheelSteps);
            float requestedSpeed = wheel.ButtonIndex == MouseButton.WheelUp
                ? _noclipTargetSpeed * multiplier
                : _noclipTargetSpeed / multiplier;
            SetNoclipTargetSpeed(requestedSpeed, immediate: false);
            GD.Print($"Noclip speed target: {FormatNoclipSpeed(_noclipTargetSpeed)}");
            GetViewport().SetInputAsHandled();
        }
        else if (@event is InputEventKey speedKey && speedKey.Pressed && !speedKey.Echo && _noclip)
        {
            int requestedTier = speedKey.Keycode switch
            {
                Key.Key1 => 0,
                Key.Key2 => 1,
                Key.Key3 => 2,
                Key.Key4 => 3,
                Key.Key5 => 4,
                _ => -1
            };
            if (requestedTier >= 0)
            {
                _noclipSpeedTier = requestedTier;
                SetNoclipTargetSpeed(GetNoclipTierSpeed(), immediate: false);
                GD.Print($"Noclip speed preset {_noclipSpeedTier + 1}: {FormatNoclipSpeed(_noclipTargetSpeed)}");
            }
        }
		else if (@event is InputEventKey waterKey && waterKey.Pressed && !waterKey.Echo
			&& EnableDynamicWaterTools && (waterKey.Keycode == PlaceWaterSourceKey || waterKey.Keycode == RemoveWaterKey))
		{
			TryUseDynamicWaterTool(waterKey.Keycode == PlaceWaterSourceKey);
			GetViewport().SetInputAsHandled();
		}
        else if (@event is InputEventMouseButton button
            && button.Pressed
            && Input.MouseMode != Input.MouseModeEnum.Captured
            && !GetTree().Root.GetMeta(RingLiveTuningDrawer.MouseOwnershipMeta, false).AsBool())
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
        }
    }

	private void TryUseDynamicWaterTool(bool placeSource)
	{
		Camera3D? camera = GetNodeOrNull<Camera3D>(CameraPath);
		if (_grid == null || camera == null) return;
		Vector3 origin = camera.GlobalPosition;
		Vector3 target = origin - camera.GlobalTransform.Basis.Z * Mathf.Max(1.0f, WaterToolReachMeters);
		var query = PhysicsRayQueryParameters3D.Create(origin, target);
		query.CollideWithAreas = false;
		Godot.Collections.Dictionary hit = GetWorld3D().DirectSpaceState.IntersectRay(query);
		if (hit.Count == 0) return;
		Vector3 point = hit["position"].AsVector3();
		Vector3 normal = hit["normal"].AsVector3();
		bool changed = placeSource
			? _grid.TryPlaceWaterSourceByWorldPosition(point + normal * 0.02f)
			: _grid.TryRemoveWaterByWorldPosition(point + normal * 0.02f);
		GD.Print(changed
			? (placeSource ? "Water source placed (F)." : "Dynamic water removed (G).")
			: "Water tool failed: target must be air or glass and inside the streamed world.");
	}

    public override void _PhysicsProcess(double delta)
    {
        if (_grid == null)
        {
            return;
        }
        if (_crossRingSmokeTest)
        {
            ProcessCrossRingSmokeTest(Mathf.Min((float)delta, 0.05f));
            return;
        }
        if (_swimmingSmokeTest)
        {
            if (!_swimmingSmokeInitialized)
            {
                BeginSwimmingSmokeTest();
                if (!_swimmingSmokeInitialized) return;
            }
            ProcessSwimmingSmokeTest(Mathf.Min((float)delta, 0.05f));
            return;
        }
        if (_waterAliasingSmokeTest)
        {
            if (!_waterAliasingSmokeInitialized)
            {
                BeginWaterAliasingSmokeTest();
                if (!_waterAliasingSmokeInitialized) return;
            }
            ProcessWaterAliasingSmokeTest(Mathf.Min((float)delta, 0.05f));
            return;
        }
        if (_benchmarkOverrideActive)
        {
            ApplyWorldTransform();
            return;
        }

        float dt = Mathf.Min((float)delta, 0.05f);
        if (ProcessSpawnTerrainSettle(dt))
        {
            ProcessMegaSpawnSmokeTest();
            ProcessSmoothTerrainTraversalSmokeTest();
            ProcessSmoothDeepShaftSmokeTest();
            return;
        }
        if (_noclip)
        {
            UpdateWaterState();
            IsSwimming = false;
            ProcessNoclip(dt);
            UpdateCurvatureHorizonCorrection(dt);
            return;
        }

        UpdateWaterState();
        if (IsSwimming)
        {
            ProcessSwimming(dt);
            UpdateCurvatureHorizonCorrection(dt);
            return;
        }

        float inputX = (IsPressed(Key.D) ? 1.0f : 0.0f) - (IsPressed(Key.A) ? 1.0f : 0.0f);
        float inputForward = (IsPressed(Key.W) ? 1.0f : 0.0f) - (IsPressed(Key.S) ? 1.0f : 0.0f);
        Vector2 input = new Vector2(inputX, inputForward).LimitLength(1.0f);

        Vector2 forward = new(Mathf.Cos(_yaw), Mathf.Sin(_yaw));
        // With inward radial up, camera-right points toward negative across-ring
        // when facing positive around-ring.
        Vector2 right = new(forward.Y, -forward.X);
        Vector2 desiredPlanar = _smoothTerrainTraversalSmokeTest
            ? Vector2.Right
            : (right * input.X + forward * input.Y).LimitLength(1.0f);
        float speed = _smoothTerrainTraversalSmokeTest||IsPressed(Key.Shift) ? SprintSpeed : MoveSpeed;
        float acceleration = _grounded ? GroundAcceleration : AirAcceleration;
        Vector2 planarVelocity = new(_canonicalVelocity.X, _canonicalVelocity.Z);
        planarVelocity = planarVelocity.MoveToward(desiredPlanar * speed, acceleration * dt);
        _canonicalVelocity.X = planarVelocity.X;
        _canonicalVelocity.Z = planarVelocity.Y;

        bool precisionJumpRequested = _precisionMotionSmokeTest && _grounded && !_precisionJumpStarted;
        if (_grounded && (IsPressed(Key.Space) || precisionJumpRequested))
        {
            _canonicalVelocity.Y = JumpVelocity;
            _grounded = false;
            if (precisionJumpRequested)
            {
                _precisionJumpStarted = true;
                GD.Print("Certified-scale precision motion smoke test jump started.");
            }
        }
        else
        {
            CurrentGravityFactor = ResolveGravityFactor();
            _canonicalVelocity.Y = Mathf.Max(
                _canonicalVelocity.Y - GravityStrength * CurrentGravityFactor * dt,
                -MaxFallSpeed);
        }

        if (UseTerrain3DSurfacePhysics)
        {
            if (!MoveWithTerrain3DSurfacePhysics(swimming: false))
            {
                _canonicalVelocity=Vector3.Zero;
                ApplyWorldTransform();
            }
        }
        else if (UseSmoothTerrainPhysics && _smoothTerrain != null)
        {
            MoveWithSmoothTerrainPhysics(swimming: false);
        }
        else
        {
            Vector3 requested = _canonicalVelocity * dt;
            Vector3 collisionPosition = ToCollisionLocal(_canonicalPosition);
            MoveResult normal = ResolveMovement(collisionPosition, requested);
            MoveResult chosen = normal;

            if (_grounded && StepHeight > 0.0f && normal.HorizontalBlocked && requested.Y <= 0.0f)
            {
                MoveResult stepped = ResolveStepMovement(collisionPosition, requested, StepHeight);
                float normalDistanceSq = new Vector2(normal.Applied.X, normal.Applied.Z).LengthSquared();
                float stepDistanceSq = new Vector2(stepped.Applied.X, stepped.Applied.Z).LengthSquared();
                if (stepDistanceSq > normalDistanceSq + 0.0001f) chosen = stepped;
            }

            _canonicalPosition = FromCollisionLocal(chosen.Position);
            _grounded = chosen.Grounded;
            if (!Mathf.IsEqualApprox(chosen.Applied.X, requested.X)) _canonicalVelocity.X = 0.0f;
            if (!Mathf.IsEqualApprox(chosen.Applied.Y, requested.Y)) _canonicalVelocity.Y = 0.0f;
            if (!Mathf.IsEqualApprox(chosen.Applied.Z, requested.Z)) _canonicalVelocity.Z = 0.0f;
            WrapCanonicalAround();
            RecenterCollisionOrigin(force: false);
            ApplyWorldTransform();
        }
        UpdateCurvatureHorizonCorrection(dt);
        if (_precisionMotionSmokeTest) ProcessPrecisionMotionSmokeTest();
        ProcessMegaSpawnSmokeTest();
        ProcessSmoothTerrainTraversalSmokeTest();
        ProcessSmoothDeepShaftSmokeTest();
    }

    private void UpdateWaterState()
    {
        if (_grid?.TerrainSampler == null
            || _canonicalPosition.AcrossMeters < 0.0
            || _canonicalPosition.AcrossMeters > _grid.RingAcrossWidthMeters)
        {
            _waterAmbienceKind = SurfaceWaterKind.None;
            _waterAmbienceProximity = 0.0f;
            _waterAmbienceShoreProximity = 0.0f;
            ClearWaterState();
            return;
        }

        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
        UpdateWaterAmbienceMetadata(sample, around, across);
        // Ambience is independent from swimming mechanics. A game mode may
        // disable buoyancy while shore, river, and open-water sound remains.
        if (!EnableSwimming)
        {
            ClearWaterState();
            return;
        }
        // Generated hydrology is a finite surface column, not an infinite
        // "everything below the waterline" volume. A tunnel beneath a
        // lake/ocean bed is dry unless dynamic fluid cells actually fill it.
        float generatedWaterSurface = sample.WaterSurfaceHeight + 1.0f;
        // The FFT is an ocean-only surface.  Its offset is relative to the
        // gameplay water top, not the proxy mesh (which is radially underlaid
        // for z-fighting).  Ask before finalizing the finite column so a crest
        // can be entered and a trough can be left at its actual height.
        if (sample.WaterKind == SurfaceWaterKind.Ocean
            && HasGeneratedWaterFloor(sample, _canonicalPosition.VerticalMeters)
            && RingWaterFftController.Instance?.TryGetOceanSurfaceOffset(
                _canonicalPosition, out float fftSurfaceOffset) == true)
        {
            generatedWaterSurface += fftSurfaceOffset;
        }
        bool generatedWater = IsWithinGeneratedWaterColumn(
            sample, _canonicalPosition.VerticalMeters, generatedWaterSurface);
        // A finite hydrology column must still be air at the player. This
        // keeps solid terrain and later cavity edits from masquerading as water.
        if (generatedWater && _smoothTerrain != null
            && _smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(
                _canonicalPosition.AroundMeters,
                _canonicalPosition.VerticalMeters,
                _canonicalPosition.AcrossMeters,
                out float localDensity))
            generatedWater = localDensity > 0.0f;
        bool dynamicWater = _grid.TryGetDynamicWaterSurfaceAtCanonical(
            _canonicalPosition,
            out float dynamicWaterSurface);
        if (!generatedWater && !dynamicWater)
        {
            ClearWaterState();
            return;
        }

        CurrentWaterSurfaceHeightMeters = dynamicWater
            ? dynamicWaterSurface
            : generatedWaterSurface;
        CurrentWaterDepthMeters = Mathf.Max(0.0f, CurrentWaterSurfaceHeightMeters - (float)_canonicalPosition.VerticalMeters);
        float threshold = IsSwimming ? SwimExitDepthMeters : SwimEnterDepthMeters;
        IsSwimming = CurrentWaterDepthMeters > Mathf.Max(0.01f, threshold);
        float cameraHeight = _head?.Position.Y ?? BodyHeight * 0.85f;
        IsCameraUnderwater = _canonicalPosition.VerticalMeters + cameraHeight
            < CurrentWaterSurfaceHeightMeters - 0.05f;
        SetMeta("ring_camera_underwater", IsCameraUnderwater);
        SetMeta("ring_water_depth_meters", Mathf.Max(
            0.0f,
            CurrentWaterSurfaceHeightMeters - (float)(_canonicalPosition.VerticalMeters + cameraHeight)));
    }

    public static bool IsWithinGeneratedWaterColumn(in TerrainSample sample, double verticalMeters)
        => IsWithinGeneratedWaterColumn(sample, verticalMeters, sample.WaterSurfaceHeight + 1.0f);

    /// <summary>Tests a finite generated-water column against an explicit surface top.</summary>
    public static bool IsWithinGeneratedWaterColumn(
        in TerrainSample sample,
        double verticalMeters,
        float surfaceHeightMeters)
    {
        if (!HasGeneratedWaterFloor(sample, verticalMeters)
            || !float.IsFinite(surfaceHeightMeters)) return false;
        // Terrain occupies SurfaceHeight and below; generated water occupies
        // only the declared air column above that floor.
        return verticalMeters > sample.SurfaceHeight
            && verticalMeters <= surfaceHeightMeters;
    }

    private static bool HasGeneratedWaterFloor(in TerrainSample sample, double verticalMeters)
        => sample.HasSurfaceWater
            && sample.WaterDepth > 0
            && double.IsFinite(verticalMeters);

    private void ClearWaterState()
    {
        IsSwimming = false;
        IsCameraUnderwater = false;
        CurrentWaterSurfaceHeightMeters = 0.0f;
        CurrentWaterDepthMeters = 0.0f;
        SetMeta("ring_camera_underwater", false);
        SetMeta("ring_water_depth_meters", 0.0f);
        PublishWaterAmbienceMetadata();
    }

    private void UpdateWaterAmbienceMetadata(in TerrainSample centerSample, long around, int across)
    {
        _waterAmbienceProbeRemaining -= (float)GetPhysicsProcessDeltaTime();
        if (_waterAmbienceProbeRemaining > 0.0f)
        {
            PublishWaterAmbienceMetadata();
            return;
        }

        _waterAmbienceProbeRemaining = 0.4f;
        RingTerrainSampler? sampler = _grid?.TerrainSampler;
        if (sampler == null)
        {
            _waterAmbienceKind = SurfaceWaterKind.None;
            _waterAmbienceProximity = 0.0f;
            _waterAmbienceShoreProximity = 0.0f;
            PublishWaterAmbienceMetadata();
            return;
        }

        bool centerHasWater = centerSample.HasSurfaceWater;
        _waterAmbienceKind = centerHasWater ? centerSample.WaterKind : SurfaceWaterKind.None;
        float nearestOppositeMeters = float.PositiveInfinity;
        float nearestWaterMeters = float.PositiveInfinity;
        SurfaceWaterKind nearestWaterKind = SurfaceWaterKind.None;
        ReadOnlySpan<int> radii = stackalloc[] { 48, 128, 320, 640 };
        ReadOnlySpan<Vector2I> directions = stackalloc[]
        {
            new Vector2I(1, 0), new Vector2I(-1, 0), new Vector2I(0, 1), new Vector2I(0, -1),
            new Vector2I(1, 1), new Vector2I(1, -1), new Vector2I(-1, 1), new Vector2I(-1, -1)
        };
        foreach (int radius in radii)
        {
            foreach (Vector2I direction in directions)
            {
                long probeAround = around + (long)Math.Round(direction.X * radius * (direction.Y == 0 ? 1.0 : 0.70710678));
                int probeAcross = Mathf.Clamp(
                    across + Mathf.RoundToInt(direction.Y * radius * (direction.X == 0 ? 1.0f : 0.70710678f)),
                    0, Mathf.Max(0, Mathf.RoundToInt(_grid!.RingAcrossWidthMeters)));
                TerrainSample probe = sampler.GetTerrainSample(probeAround, probeAcross);
                float distance = radius;
                if (probe.HasSurfaceWater)
                {
                    if (distance < nearestWaterMeters)
                    {
                        nearestWaterMeters = distance;
                        nearestWaterKind = probe.WaterKind;
                    }
                }
                else if (centerHasWater)
                {
                    nearestOppositeMeters = Mathf.Min(nearestOppositeMeters, distance);
                }
            }
        }

        if (centerHasWater)
        {
            _waterAmbienceProximity = 1.0f;
            _waterAmbienceShoreProximity = float.IsFinite(nearestOppositeMeters)
                ? 1.0f - Mathf.Clamp(nearestOppositeMeters / 320.0f, 0.0f, 1.0f)
                : 0.0f;
        }
        else if (float.IsFinite(nearestWaterMeters))
        {
            _waterAmbienceKind = nearestWaterKind;
            _waterAmbienceProximity = 1.0f - Mathf.Clamp(nearestWaterMeters / 640.0f, 0.0f, 1.0f);
            _waterAmbienceShoreProximity = 1.0f - Mathf.Clamp(nearestWaterMeters / 256.0f, 0.0f, 1.0f);
        }
        else
        {
            _waterAmbienceProximity = 0.0f;
            _waterAmbienceShoreProximity = 0.0f;
        }
        PublishWaterAmbienceMetadata();
    }

    private void PublishWaterAmbienceMetadata()
    {
        SetMeta("ring_water_ambience_kind", (int)_waterAmbienceKind);
        SetMeta("ring_water_ambience_proximity", _waterAmbienceProximity);
        SetMeta("ring_water_ambience_shore_proximity", _waterAmbienceShoreProximity);
    }

    private void ProcessSwimming(float dt)
    {
        // Entering water can carry the much larger terrestrial terminal fall
        // speed into the swim motor. Clamp it before acceleration smoothing so
        // the player does not plunge tens of metres through the surface while
        // buoyancy slowly catches up.
        _canonicalVelocity.Y = Mathf.Clamp(
            _canonicalVelocity.Y,
            -Mathf.Max(0.1f, SwimVerticalSpeed),
            Mathf.Max(0.1f, SwimVerticalSpeed));

        float inputX = (IsPressed(Key.D) ? 1.0f : 0.0f) - (IsPressed(Key.A) ? 1.0f : 0.0f);
        float inputForward = (IsPressed(Key.W) ? 1.0f : 0.0f) - (IsPressed(Key.S) ? 1.0f : 0.0f);
        float inputVertical = (IsPressed(Key.Space) || IsPressed(Key.E) ? 1.0f : 0.0f)
            - (IsPressed(Key.C) || IsPressed(Key.Q) ? 1.0f : 0.0f);

        float pitchRadians = Mathf.DegToRad(_pitch);
        Vector3 horizontalForward = new(Mathf.Cos(_yaw), 0.0f, Mathf.Sin(_yaw));
        Vector3 swimForward = horizontalForward * Mathf.Cos(pitchRadians) + Vector3.Up * Mathf.Sin(pitchRadians);
        Vector3 swimRight = new(Mathf.Sin(_yaw), 0.0f, -Mathf.Cos(_yaw));
        float targetVertical;
        if (!Mathf.IsZeroApprox(inputVertical))
        {
            targetVertical = inputVertical * SwimVerticalSpeed;
        }
        else
        {
            float floatTarget = CurrentWaterSurfaceHeightMeters - SwimSurfaceFloatDepthMeters;
            float floatError = floatTarget - (float)_canonicalPosition.VerticalMeters;
            targetVertical = Mathf.Clamp(
                floatError * SwimBuoyancyResponse,
                -SwimVerticalSpeed,
                SwimVerticalSpeed);
        }

        Vector3 desiredDirection = (swimRight * inputX + swimForward * inputForward).LimitLength(1.0f);
        float horizontalSpeed = IsPressed(Key.Shift) ? SwimSprintSpeed : SwimSpeed;
        Vector3 targetVelocity = desiredDirection * horizontalSpeed;
        targetVelocity.Y = Mathf.Clamp(
            targetVelocity.Y + targetVertical,
            -SwimVerticalSpeed,
            SwimVerticalSpeed);
        _canonicalVelocity = _canonicalVelocity.MoveToward(
            targetVelocity,
            Mathf.Max(0.1f, SwimAcceleration) * dt);
        CurrentGravityFactor = 0.0f;
        _grounded = false;

        if (UseTerrain3DSurfacePhysics)
        {
            if (!MoveWithTerrain3DSurfacePhysics(swimming: true))
            {
                _canonicalVelocity = Vector3.Zero;
                ApplyWorldTransform();
            }
        }
        else if (UseSmoothTerrainPhysics && _smoothTerrain != null)
        {
            MoveWithSmoothTerrainPhysics(swimming: true);
        }
        else
        {
            Vector3 requested = _canonicalVelocity * dt;
            MoveResult moved = ResolveMovement(ToCollisionLocal(_canonicalPosition), requested);
            _canonicalPosition = FromCollisionLocal(moved.Position);
            if (!Mathf.IsEqualApprox(moved.Applied.X, requested.X)) _canonicalVelocity.X = 0.0f;
            if (!Mathf.IsEqualApprox(moved.Applied.Y, requested.Y)) _canonicalVelocity.Y = 0.0f;
            if (!Mathf.IsEqualApprox(moved.Applied.Z, requested.Z)) _canonicalVelocity.Z = 0.0f;
            WrapCanonicalAround();
            RecenterCollisionOrigin(force: false);
            ApplyWorldTransform();
        }
        UpdateWaterState();
    }

    private void BeginSwimmingSmokeTest()
    {
        if (_grid?.TerrainSampler == null) return;
        _swimmingSmokeInitialized = true;
        const int aroundSamples = 256;
        const int acrossSamples = 48;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        for (int z = 1; z < acrossSamples - 1; z++)
        for (int x = 0; x < aroundSamples; x++)
        {
            int around = (int)Math.Round((x + 0.5) * circumference / aroundSamples);
            int across = Mathf.RoundToInt((z + 0.5f) * _grid.RingAcrossWidthMeters / acrossSamples);
            TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (!sample.HasSurfaceWater || sample.WaterDepth < 4) continue;

            _canonicalPosition = new RingCanonicalPosition(
                around,
                sample.WaterSurfaceHeight - 2.0,
                across).Wrap(circumference);
            _canonicalVelocity = Vector3.Zero;
            _noclip = false;
            _grounded = false;
            _swimmingSmokeStartVertical = _canonicalPosition.VerticalMeters;
            RecenterCollisionOrigin(force: true);
            ApplyWorldTransform();
            UpdateWaterState();
            _swimmingSmokeUnderwaterConfirmed = IsCameraUnderwater;
            GD.Print($"Swimming smoke test started in {sample.WaterKind}: depth={sample.WaterDepth}m, surface={sample.WaterSurfaceHeight}m.");
            return;
        }

        GD.PushError("Swimming smoke test could not find a generated water sample at least 4m deep.");
        GetTree().Quit(1);
    }

    private void ProcessSwimmingSmokeTest(float dt)
    {
        UpdateWaterState();
        _swimmingSmokeUnderwaterConfirmed |= IsCameraUnderwater;
        if (!IsSwimming)
        {
            GD.PushError("Swimming smoke test left the water before reaching its buoyant surface position.");
            GetTree().Quit(1);
            return;
        }

        ProcessSwimming(dt);
        if (++_swimmingSmokeFrames < 90) return;

        double buoyantRise = _canonicalPosition.VerticalMeters - _swimmingSmokeStartVertical;
        if (buoyantRise < 0.25 || !_swimmingSmokeUnderwaterConfirmed)
        {
            GD.PushError($"Swimming smoke test failed: passiveRise={buoyantRise:0.000}m, underwaterCamera={_swimmingSmokeUnderwaterConfirmed}.");
            GetTree().Quit(1);
            return;
        }

        GD.Print($"Swimming smoke test passed: water state, underwater camera, collision movement, and {buoyantRise:0.00}m passive buoyancy rise.");
        GetTree().Quit(0);
    }

    private void BeginWaterAliasingSmokeTest()
    {
        if (_grid?.TerrainSampler == null) return;
        const int aroundSamples = 256;
        const int acrossSamples = 48;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();

        bool TryBeginAt(int around, int across)
        {
            if (across < 0 || across > _grid.RingAcrossWidthMeters) return false;
            TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (sample.WaterKind != SurfaceWaterKind.Ocean || sample.WaterDepth < 100) return false;

            _canonicalPosition = new RingCanonicalPosition(
                around,
                sample.WaterSurfaceHeight + 1400.0,
                across).Wrap(circumference);
            _canonicalVelocity = Vector3.Zero;
            _noclip = true;
            _grounded = false;
            _yaw = Mathf.DegToRad(22.0f);
            _pitch = -35.0f;
            _waterAliasingSmokeInitialized = true;
            RecenterCollisionOrigin(force: true);
            _cameraBasisInitialized = false;
            ApplyWorldTransform();
            ApplyCameraRotation();
            _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
            GD.Print(
                $"Water aliasing smoke test started: arc={_canonicalPosition.AroundMeters / 1000.0:0.0}km, "
                + $"across={_canonicalPosition.AcrossMeters / 1000.0:0.0}km, altitude=1400m, "
                + $"pitch=-35deg, oceanDepth={sample.WaterDepth}m.");
            return true;
        }

        // Reproduce the supplied view at the same normalized cross-ring position.
        // The source world was wider than this scene's default 20km test habitat,
        // so using 89.4km verbatim could place the camera outside the ring entirely.
        int preferredAround = (int)Math.Round(((529700.0 % circumference) + circumference) % circumference);
        int preferredAcross = Mathf.RoundToInt(_grid.RingAcrossWidthMeters * 0.447f);
        if (TryBeginAt(preferredAround, preferredAcross)) return;

        int deepestAround = -1;
        int deepestAcross = -1;
        int deepestWater = -1;
        // If the normalized source point is not ocean in this seed, keep the
        // fallback well away from the structural sidewalls and use its deepest
        // valid open-water sample.
        for (int z = 1; z < acrossSamples - 1; z++)
        for (int x = 0; x < aroundSamples; x++)
        {
            int around = (int)Math.Round((x + 0.5) * circumference / aroundSamples);
            int across = Mathf.RoundToInt((z + 0.5f) * _grid.RingAcrossWidthMeters / acrossSamples);
            if (across < _grid.RingAcrossWidthMeters * 0.10f
                || across > _grid.RingAcrossWidthMeters * 0.90f) continue;
            TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (sample.WaterKind != SurfaceWaterKind.Ocean || sample.WaterDepth <= deepestWater) continue;
            deepestAround = around;
            deepestAcross = across;
            deepestWater = sample.WaterDepth;
        }

        if (deepestWater >= 100 && TryBeginAt(deepestAround, deepestAcross)) return;

        GD.PushError("Water aliasing smoke test could not find a generated ocean sample at least 100m deep.");
        GetTree().Quit(1);
    }

    private void ProcessWaterAliasingSmokeTest(float dt)
    {
        _waterAliasingSmokeFrames++;
        if (_waterAliasingSmokeFrames <= 180)
        {
            _canonicalVelocity = Vector3.Zero;
            ApplyWorldTransform();
        }
        else
        {
            MoveNoclipInPhysicalRingSpace(Vector3.Right, 409.0f, dt);
            RecenterCollisionOrigin(force: false);
            ApplyWorldTransform();
        }
        if (_waterAliasingSmokeFrames == 180)
            GD.Print("Water aliasing smoke test warm-up complete; beginning the 409m/s aerial pass.");
        if (_waterAliasingSmokeFrames < 360) return;

        GD.Print("Water aliasing smoke test capture completed: 180 warm-up plus 180 flight physics frames.");
        GetTree().Quit(0);
    }

    private void ProcessPrecisionMotionSmokeTest()
    {
        if (_grid == null) return;

        Vector3 expectedWorld = _grid.CanonicalRingPositionToWorld(_canonicalPosition);
        double transformError = GlobalPosition.DistanceTo(expectedWorld);
        _precisionMaxTransformError = Math.Max(_precisionMaxTransformError, transformError);

        if (_precisionHasPreviousSample)
        {
            double canonicalStep = Math.Abs(_canonicalPosition.VerticalMeters - _precisionPreviousCanonical.VerticalMeters);
            double observedStep = GlobalPosition.DistanceTo(_precisionPreviousWorld);
            double expectedStep = expectedWorld.DistanceTo(_precisionPreviousExpectedWorld);
            _precisionMaxStepError = Math.Max(_precisionMaxStepError, Math.Abs(observedStep - expectedStep));
            if (_precisionJumpStarted && canonicalStep > 0.002 && observedStep < 0.001)
                _precisionQuantizedFrames++;
        }

        _precisionPreviousCanonical = _canonicalPosition;
        _precisionPreviousWorld = GlobalPosition;
        _precisionPreviousExpectedWorld = expectedWorld;
        _precisionHasPreviousSample = true;
        _precisionJumpAirborne |= _precisionJumpStarted && !_grounded;

        if (_precisionJumpAirborne && _grounded)
        {
            bool passed = _precisionMaxTransformError <= 0.005
                && _precisionMaxStepError <= 0.005
                && _precisionQuantizedFrames == 0;
            string result = $"Certified-scale precision motion smoke test: maxTransformError={_precisionMaxTransformError:0.000000}m, maxStepError={_precisionMaxStepError:0.000000}m, quantizedFrames={_precisionQuantizedFrames}.";
            if (passed)
            {
                GD.Print(result + " Passed.");
                GetTree().Quit(0);
            }
            else
            {
                GD.PushError(result + " Failed.");
                GetTree().Quit(1);
            }
            return;
        }

        if (++_precisionMotionFrames > 1200)
        {
            GD.PushError("Certified-scale precision motion smoke test timed out before a complete jump and landing.");
            GetTree().Quit(1);
        }
    }

    public void SetBenchmarkCanonicalPosition(
        in RingCanonicalPosition position,
        float simulatedSpeedMetersPerSecond = 0.0f,
        bool simulatedNoclip = false)
    {
        if (_grid == null
            || !double.IsFinite(position.AroundMeters)
            || !double.IsFinite(position.VerticalMeters)
            || !double.IsFinite(position.AcrossMeters))
        {
            return;
        }

        _benchmarkOverrideActive = true;
        _canonicalPosition = position.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        float simulatedSpeed = float.IsFinite(simulatedSpeedMetersPerSecond)
            ? Mathf.Max(0.0f, simulatedSpeedMetersPerSecond)
            : 0.0f;
        // The benchmark owns position directly, but production streaming and
        // fast-travel safeguards still need the route's intended motion state.
        _canonicalVelocity = simulatedSpeed > 0.0f ? Vector3.Right * simulatedSpeed : Vector3.Zero;
        _noclip = simulatedNoclip;
        CurrentGravityFactor = ResolveGravityFactor();
        RecenterCollisionOrigin(force: true);
        _pendingPhysicsInterpolationReset = true;
        ApplyWorldTransform();
    }

    public void ClearBenchmarkOverride()
    {
        _benchmarkOverrideActive = false;
        _canonicalVelocity = Vector3.Zero;
    }

    /// <summary>Applies a stable local view direction for scripted presentation and capture routes.</summary>
    public void SetPresentationView(float yawDegrees, float pitchDegrees)
    {
        if (!float.IsFinite(yawDegrees) || !float.IsFinite(pitchDegrees)) return;
        _yaw = Mathf.PosMod(Mathf.DegToRad(yawDegrees), Mathf.Tau);
        _pitch = Mathf.Clamp(pitchDegrees, -85.0f, 85.0f);
        ApplyCameraRotation();
    }

    private void ProcessNoclip(float dt)
    {
        EnsureNoclipSpeedInitialized();
        UpdateNoclipCurrentSpeed(dt);
        float inputX = (IsPressed(Key.D) ? 1.0f : 0.0f) - (IsPressed(Key.A) ? 1.0f : 0.0f);
        float inputForward = (IsPressed(Key.W) ? 1.0f : 0.0f) - (IsPressed(Key.S) ? 1.0f : 0.0f);
        float inputUp = (IsPressed(Key.Space) || IsPressed(Key.E) ? 1.0f : 0.0f)
            - (IsPressed(Key.C) || IsPressed(Key.Q) ? 1.0f : 0.0f);

        Vector3 direction = ResolveNoclipCanonicalDirection(inputX, inputForward, inputUp);

        float speed = _noclipCurrentSpeed * (IsPressed(Key.Shift) ? NoclipSprintMultiplier : 1.0f);
        RingCanonicalPosition previous = _canonicalPosition;
        MoveNoclipInPhysicalRingSpace(direction, speed, dt);
        HandleCrossRingCenterChange(previous);

        UpdateCrossRingTransit();
        if (_crossRingCenterCrossed && AutoLandAfterCrossRingFlight && TryLandAtDestinationSurface())
            return;

        RecenterCollisionOrigin(force: false);
        ApplyWorldTransform();
    }

    private void BeginCrossRingSmokeTest()
    {
        if (_grid == null) return;
        double radius = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau;
        _canonicalPosition = _canonicalPosition with { VerticalMeters = radius - 100000.0 };
        _canonicalVelocity = Vector3.Up * 1000000.0f;
        _noclip = true;
        _crossRingTransitActive = true;
        UpdateCrossRingTransit();
        RecenterCollisionOrigin(force: true);
        ApplyWorldTransform();
        GD.Print("Cross-ring flight smoke test started 100 km before ring center.");
    }

    private void ProcessCrossRingSmokeTest(float dt)
    {
        RingCanonicalPosition previous = _canonicalPosition;
        MoveNoclipInPhysicalRingSpace(Vector3.Up * _crossRingVerticalControlSign, 1000000.0f, dt);
        HandleCrossRingCenterChange(previous);
        UpdateCrossRingTransit();
        if (_crossRingCenterCrossed && TryLandAtDestinationSurface())
        {
            bool destinationResident = _grid != null
                && _grid.LoadedChunkCount > 0
                && _grid.DesiredCollisionChunkCount > 0;
            if (!destinationResident)
            {
                GD.PushError("Cross-ring flight smoke test reached the surface without resident destination collision chunks.");
                GetTree().Quit(1);
                return;
            }
            GD.Print($"Cross-ring flight smoke test passed: center rebase, destination preload ({_grid!.LoadedChunkCount} chunks), and physical landing completed.");
            GetTree().Quit(0);
            return;
        }

        RecenterCollisionOrigin(force: false);
        ApplyWorldTransform();
        if (++_crossRingSmokeFrames > 900)
        {
            GD.PushError("Cross-ring flight smoke test timed out before physical landing.");
            GetTree().Quit(1);
        }
    }

    private void HandleCrossRingCenterChange(in RingCanonicalPosition previous)
    {
        if (!EnableCrossRingFlightHandoff || !CrossedRingCenter(previous, _canonicalPosition)) return;
        _crossRingTransitActive = true;
        _crossRingCenterCrossed = true;
        _crossRingVerticalControlSign *= -1.0f;
        GD.Print($"Cross-ring center handoff: destinationAround={_canonicalPosition.AroundMeters / 1000.0:0.000}km, destinationHeight={_canonicalPosition.VerticalMeters / 1000.0:0.000}km");
    }

    private void MoveNoclipInPhysicalRingSpace(Vector3 canonicalDirection, float speed, float dt)
    {
        if (_grid == null) return;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double radius = circumference / Math.Tau;
        double theta = CanonicalAngle(_canonicalPosition);
        double radial = radius - _canonicalPosition.VerticalMeters;
        double physicalX = Math.Cos(theta) * radial;
        double physicalY = _grid.RingAcrossBaseOffsetMeters + _canonicalPosition.AcrossMeters;
        double physicalZ = Math.Sin(theta) * radial;

        double tangentX = -Math.Sin(theta), tangentZ = Math.Cos(theta);
        double inwardX = -Math.Cos(theta), inwardZ = -Math.Sin(theta);
        double velocityX = (tangentX * canonicalDirection.X + inwardX * canonicalDirection.Y) * speed;
        double velocityY = canonicalDirection.Z * speed;
        double velocityZ = (tangentZ * canonicalDirection.X + inwardZ * canonicalDirection.Y) * speed;
        physicalX += velocityX * dt;
        physicalY += velocityY * dt;
        physicalZ += velocityZ * dt;

        double nextRadial = Math.Sqrt(physicalX * physicalX + physicalZ * physicalZ);
        double nextTheta = nextRadial > 0.000001 ? Math.Atan2(physicalZ, physicalX) : theta;
        if (nextTheta < 0.0) nextTheta += Math.Tau;
        _canonicalPosition = new RingCanonicalPosition(
            nextTheta * radius,
            radius - nextRadial,
            physicalY - _grid.RingAcrossBaseOffsetMeters).Wrap(circumference);

        double nextTangentX = -Math.Sin(nextTheta), nextTangentZ = Math.Cos(nextTheta);
        double nextInwardX = -Math.Cos(nextTheta), nextInwardZ = -Math.Sin(nextTheta);
        _canonicalVelocity = new Vector3(
            (float)(velocityX * nextTangentX + velocityZ * nextTangentZ),
            (float)(velocityX * nextInwardX + velocityZ * nextInwardZ),
            (float)velocityY);
    }

    private Vector3 ResolveNoclipCanonicalDirection(float inputX, float inputForward, float inputUp)
    {
        if (_grid == null || _head == null) return Vector3.Zero;

        double theta = CanonicalAngle(_canonicalPosition);
        Vector3 tangent = new(-(float)Math.Sin(theta), 0.0f, (float)Math.Cos(theta));
        Vector3 inward = new(-(float)Math.Cos(theta), 0.0f, -(float)Math.Sin(theta));
        Vector3 across = Vector3.Up;

        Basis cameraBasis = _head.GlobalBasis.Orthonormalized();
        Vector3 cameraForward = -cameraBasis.Z;
        Vector3 cameraRight = cameraBasis.X;
        Vector3 cameraUp = cameraBasis.Y;
        Vector3 physicalDirection = (cameraRight * inputX
            + cameraForward * inputForward
            + cameraUp * inputUp).LimitLength(1.0f);

        return new Vector3(
            physicalDirection.Dot(tangent),
            physicalDirection.Dot(inward),
            physicalDirection.Dot(across)).LimitLength(1.0f);
    }

    private void UpdateCrossRingTransit()
    {
        if (_grid?.TerrainSampler == null || !EnableCrossRingFlightHandoff) return;
        double preloadHeight = Math.Max(10000.0, CrossRingPreloadHeightMeters);
        if (!_crossRingTransitActive
            && _canonicalPosition.VerticalMeters >= preloadHeight
            && _canonicalVelocity.Y > 0.0f)
        {
            _crossRingTransitActive = true;
        }

        if (!_crossRingTransitActive) return;
        if (!_crossRingCenterCrossed
            && _canonicalPosition.VerticalMeters < preloadHeight * 0.75
            && _canonicalVelocity.Y < 0.0f)
        {
            EndCrossRingTransit();
            return;
        }

        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double destinationAround = _crossRingCenterCrossed
            ? _canonicalPosition.AroundMeters
            : _canonicalPosition.AroundMeters + circumference * 0.5;
        destinationAround %= circumference;
        long sampleAround = (long)Math.Round(destinationAround);
        int sampleAcross = Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters);
        int surface = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
            _grid.TerrainSampler,
            sampleAround,
            sampleAcross);
        _grid.SetStreamingCanonicalFocusOverride(new RingCanonicalPosition(
            destinationAround,
            surface + CrossRingLandingClearanceMeters,
            _canonicalPosition.AcrossMeters));
    }

    private bool TryLandAtDestinationSurface()
    {
        if (_grid?.TerrainSampler == null || _canonicalVelocity.Y >= 0.0f) return false;
        if (_canonicalPosition.AcrossMeters < 0.0 || _canonicalPosition.AcrossMeters > _grid.RingAcrossWidthMeters)
            return false;

        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        int across = Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters);
        int surface = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(_grid.TerrainSampler, around, across);
        double landingHeight = surface + Math.Max(0.01, CrossRingLandingClearanceMeters);
        if (_canonicalPosition.VerticalMeters > landingHeight) return false;

        _canonicalPosition = _canonicalPosition with { VerticalMeters = landingHeight };
        _canonicalVelocity = Vector3.Zero;
        _noclip = false;
        _grounded = false;
        RequestCurvatureCorrection(NoclipExitCorrectionResponse);
        EndCrossRingTransit();
        RecenterCollisionOrigin(force: true);
        ApplyWorldTransform();
        GD.Print($"Cross-ring physical arrival: around={_canonicalPosition.AroundMeters / 1000.0:0.000}km, surface={surface:0}m; flight mode disengaged.");
        return true;
    }

    private void EndCrossRingTransit()
    {
        _crossRingTransitActive = false;
        _crossRingCenterCrossed = false;
        _crossRingVerticalControlSign = 1.0f;
        _grid?.ClearStreamingCanonicalFocusOverride();
    }

    private double CanonicalAngle(in RingCanonicalPosition position)
    {
        if (_grid == null) return 0.0;
        return position.AroundMeters / (_grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau);
    }

    private bool CrossedRingCenter(in RingCanonicalPosition previous, in RingCanonicalPosition current)
    {
        if (_grid == null) return false;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        double delta = current.AroundMeters - previous.AroundMeters;
        delta -= Math.Round(delta / circumference) * circumference;
        return Math.Abs(delta) > circumference * 0.25;
    }

    private static bool RunCrossRingHandoffSelfTest()
    {
        const double radius = 5000000.0;
        const double theta = 0.37;
        const double overshoot = 123.0;
        double beforeX = Math.Cos(theta) * -overshoot;
        double beforeZ = Math.Sin(theta) * -overshoot;
        double afterTheta = theta + Math.PI;
        double afterX = Math.Cos(afterTheta) * overshoot;
        double afterZ = Math.Sin(afterTheta) * overshoot;
        return Math.Abs(beforeX - afterX) < 0.000001
            && Math.Abs(beforeZ - afterZ) < 0.000001
            && Math.Abs((radius + overshoot) - (2.0 * radius - (radius - overshoot))) < 0.000001;
    }

    private float GetNoclipTierSpeed()
    {
        return _noclipSpeedTier switch
        {
            1 => NoclipFastSpeed,
            2 => NoclipHyperSpeed,
            3 => NoclipWarpSpeed,
            4 => NoclipTransRingSpeed,
            _ => NoclipSpeed
        };
    }

    private void EnsureNoclipSpeedInitialized()
    {
        if (_noclipSpeedInitialized) return;
        SetNoclipTargetSpeed(GetNoclipTierSpeed(), immediate: true);
    }

    private void SetNoclipTargetSpeed(float requestedSpeed, bool immediate)
    {
        float minimum = Mathf.Max(0.1f, NoclipMinimumSpeed);
        float maximum = Mathf.Max(minimum, Mathf.Max(NoclipMaximumSpeed, NoclipTransRingSpeed));
        _noclipTargetSpeed = Mathf.Clamp(requestedSpeed, minimum, maximum);
        if (immediate || !_noclipSpeedInitialized)
            _noclipCurrentSpeed = _noclipTargetSpeed;
        _noclipSpeedInitialized = true;
    }

    private void UpdateNoclipCurrentSpeed(float dt)
    {
        float minimum = Mathf.Max(0.1f, NoclipMinimumSpeed);
        float current = Mathf.Max(minimum, _noclipCurrentSpeed);
        float target = Mathf.Max(minimum, _noclipTargetSpeed);
        double blend = 1.0 - Math.Exp(-Math.Max(0.1f, NoclipSpeedResponse) * dt);
        double logSpeed = Math.Log(current) + (Math.Log(target) - Math.Log(current)) * blend;
        _noclipCurrentSpeed = (float)Math.Exp(logSpeed);
        if (Math.Abs(_noclipCurrentSpeed - target) <= target * 0.001f)
            _noclipCurrentSpeed = target;
    }

    private static string FormatNoclipSpeed(float speed)
    {
        if (speed >= 1000.0f)
            return $"{speed / 1000.0f:0.##} km/s";
        if (speed >= 10.0f)
            return $"{speed:0} m/s";
        return $"{speed:0.0} m/s";
    }

    private void RequestCurvatureCorrection(float response)
    {
        _curvatureCorrectionPending = true;
        _pendingCurvatureCorrectionResponse = Mathf.Max(
            _pendingCurvatureCorrectionResponse,
            Mathf.Max(0.1f, response));
    }

    private void UpdateCurvatureHorizonCorrection(float dt)
    {
        if (_grid == null || !_cameraBasisInitialized) return;

        CurrentAltitudeAboveTerrainMeters = ResolveAltitudeAboveTerrainMeters();
        float atmosphereBoundary = UseGravityZeroHeightAsAtmosphereBoundary
            ? Mathf.Max(1000.0f, GravityZeroHeightMeters)
            : Mathf.Max(1000.0f, CurvatureCorrectionAtmosphereHeightMeters);
        // Altitude alone is not sufficient: beyond either side wall there is
        // no ring-local horizon to follow. Keep the camera inertial until the
        // explorer re-enters the physical across-width of the ring.
        bool insideRingSideWalls = _canonicalPosition.AcrossMeters >= 0.0
            && _canonicalPosition.AcrossMeters <= _grid.RingAcrossWidthMeters;
        bool insideAtmosphere = insideRingSideWalls
            && CurrentAltitudeAboveTerrainMeters <= atmosphereBoundary;
        if (!_curvatureAtmosphereStateInitialized)
        {
            _curvatureAtmosphereStateInitialized = true;
            _wasInsideCurvatureAtmosphere = insideAtmosphere;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
        }

        bool enteredAtmosphere = insideAtmosphere && !_wasInsideCurvatureAtmosphere;
        _wasInsideCurvatureAtmosphere = insideAtmosphere;
        Vector3 targetUp = ResolveCanonicalSurfaceUp();
        CurrentHorizonErrorDegrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(
            GlobalBasis.Orthonormalized().Y.Dot(targetUp),
            -1.0f,
            1.0f)));

        if (!EnableCurvatureHorizonCorrection)
        {
            _curvatureCorrectionActive = false;
            _curvatureCorrectionPending = false;
            _pendingCurvatureCorrectionResponse = 0.0f;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
            return;
        }

        if (!insideAtmosphere)
        {
            // Preserve an inertial camera in space. A pending noclip-exit
            // request remains queued and is applied after atmospheric entry.
            _curvatureCorrectionActive = false;
            return;
        }

        bool maintainLocalHorizon = !_noclip || AlignNoclipInsideAtmosphere;
        if (enteredAtmosphere && maintainLocalHorizon)
            RequestCurvatureCorrection(AtmosphereEntryCorrectionResponse);
        if (!maintainLocalHorizon)
        {
            _curvatureCorrectionActive = false;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
            return;
        }

        if (!_curvatureCorrectionActive && !_curvatureCorrectionPending)
        {
            double circumference = Math.Max(
                1.0,
                _grid.GetCanonicalRingCircumferenceMeters64());
            double aroundDelta = _canonicalPosition.AroundMeters
                - _curvatureCorrectionAnchorAroundMeters;
            aroundDelta -= Math.Round(aroundDelta / circumference) * circumference;
            if (Math.Abs(aroundDelta) >= Math.Max(10.0f, PeriodicCorrectionTravelMeters))
            {
                _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
                if (CurrentHorizonErrorDegrees >= Mathf.Max(0.0001f, MinimumHorizonErrorDegrees))
                    RequestCurvatureCorrection(PeriodicCorrectionResponse);
            }
        }

        if (_curvatureCorrectionPending)
        {
            _curvatureCorrectionActive = true;
            _activeCurvatureCorrectionResponse = Mathf.Max(
                0.1f,
                _pendingCurvatureCorrectionResponse);
            _curvatureCorrectionPending = false;
            _pendingCurvatureCorrectionResponse = 0.0f;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
        }
        if (!_curvatureCorrectionActive) return;

        float completion = Mathf.Max(0.0001f, CorrectionCompletionDegrees);
        if (CurrentHorizonErrorDegrees <= completion)
        {
            _curvatureCorrectionActive = false;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
            return;
        }

        Basis currentBasis = GlobalBasis.Orthonormalized();
        Basis targetBasis = BuildSurfaceAlignedBasis(currentBasis, targetUp);
        float blend = 1.0f - Mathf.Exp(
            -Mathf.Max(0.1f, _activeCurvatureCorrectionResponse)
            * Mathf.Max(0.0f, dt));
        GlobalBasis = currentBasis.Slerp(targetBasis, Mathf.Clamp(blend, 0.0f, 1.0f))
            .Orthonormalized();
        CurrentHorizonErrorDegrees = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(
            GlobalBasis.Orthonormalized().Y.Dot(targetUp),
            -1.0f,
            1.0f)));
    }

    private static Basis BuildSurfaceAlignedBasis(Basis currentBasis, Vector3 targetUp)
    {
        Basis current = currentBasis.Orthonormalized();
        Vector3 fromUp = current.Y.Normalized();
        Vector3 toUp = targetUp.Normalized();
        float dot = Mathf.Clamp(fromUp.Dot(toUp), -1.0f, 1.0f);
        if (dot >= 0.999999f) return current;

        Vector3 axis = fromUp.Cross(toUp);
        if (axis.LengthSquared() <= 0.0000001f)
            axis = current.X.Normalized();
        else
            axis = axis.Normalized();
        float angle = Mathf.Acos(dot);
        return (new Basis(new Quaternion(axis, angle)) * current).Orthonormalized();
    }

    private Vector3 ResolveCanonicalSurfaceUp()
    {
        if (_grid == null) return GlobalBasis.Orthonormalized().Y;
        double angle = (_canonicalPosition.AroundMeters
            / _grid.GetCanonicalRingCircumferenceMeters64()) * Math.Tau;
        return new Vector3(
            -(float)Math.Cos(angle),
            0.0f,
            -(float)Math.Sin(angle)).Normalized();
    }

    private float ResolveAltitudeAboveTerrainMeters()
    {
        if (UseTerrain3DSurfacePhysics && TryGetSurfaceTerrainHeight(_canonicalPosition.AroundMeters,_canonicalPosition.AcrossMeters,out float surfaceHeight))
            return Mathf.Max(0,(float)_canonicalPosition.VerticalMeters-surfaceHeight);
        if (_grid?.TerrainSampler == null)
            return Mathf.Max(0.0f, (float)_canonicalPosition.VerticalMeters);

        const int cacheCellMeters = 256;
        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
            0,
            Mathf.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters)));
        long aroundCell = (long)Math.Floor(around / (double)cacheCellMeters);
        int acrossCell = Mathf.FloorToInt(across / (float)cacheCellMeters);
        ulong editVersion = _grid.LodSurfaceEditVersion;
        if (aroundCell != _lastSurfaceAltitudeAroundCell
            || acrossCell != _lastSurfaceAltitudeAcrossCell
            || editVersion != _lastSurfaceAltitudeEditVersion)
        {
            _cachedSurfaceAltitudeHeight = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
                _grid.TerrainSampler,
                around,
                across);
            _lastSurfaceAltitudeAroundCell = aroundCell;
            _lastSurfaceAltitudeAcrossCell = acrossCell;
            _lastSurfaceAltitudeEditVersion = editVersion;
        }

        return Mathf.Max(
            0.0f,
            (float)(_canonicalPosition.VerticalMeters - _cachedSurfaceAltitudeHeight));
    }

    private float ResolveGravityFactor()
    {
        if (!EnableAltitudeGravityFalloff || _grid?.TerrainSampler == null) return 1.0f;
        double altitude = ResolveAltitudeAboveTerrainMeters();
        double start = Math.Max(0.0, GravityFadeStartHeightMeters);
        double end = Math.Max(start + 1.0, GravityZeroHeightMeters);
        if (altitude <= start) return 1.0f;
        if (altitude >= end) return 0.0f;

        float t = (float)((altitude - start) / (end - start));
        float smooth = t * t * (3.0f - 2.0f * t);
        return 1.0f - smooth;
    }

    private MoveResult ResolveMovement(Vector3 start, Vector3 requested)
    {
        Vector3 position = start;
        float dy = ClipAxis(position, requested.Y, 1);
        position.Y += dy;
        bool grounded = requested.Y < 0.0f && dy > requested.Y + CollisionEpsilon;

        float dx = ClipAxis(position, requested.X, 0);
        position.X += dx;
        float dz = ClipAxis(position, requested.Z, 2);
        position.Z += dz;
        bool blocked = Mathf.Abs(dx - requested.X) > CollisionEpsilon || Mathf.Abs(dz - requested.Z) > CollisionEpsilon;
        return new MoveResult(position, new Vector3(dx, dy, dz), grounded, blocked);
    }

    private void MoveWithSmoothTerrainPhysics(bool swimming)
    {
        if (_grid == null||_smoothTerrain==null) return;
        float physicsDelta=Mathf.Max(.0001f,(float)GetPhysicsProcessDeltaTime());
        double targetAround=_canonicalPosition.AroundMeters+_canonicalVelocity.X*physicsDelta;
        double targetVertical=_canonicalPosition.VerticalMeters+_canonicalVelocity.Y*physicsDelta;
        double targetAcross=_canonicalPosition.AcrossMeters+_canonicalVelocity.Z*physicsDelta;
        // Surface readiness is only valid before entering a cave. Once below
        // the surface, an excavated shaft is expected to have no collider in
        // the player's current chunk; what we need is the depth-following
        // terrain representation, not a collision triangle at head height.
        _smoothTerrain.SetCanonicalFocus(_canonicalPosition);
        bool currentTerrainReady=_smoothTerrain.IsMovementTerrainResident(
            _canonicalPosition.AroundMeters,
            _canonicalPosition.VerticalMeters,
            _canonicalPosition.AcrossMeters,
            BodyWidth*.5f,
            BodyHeight);
        if(!currentTerrainReady && _smoothTerrain.SurfaceResidencyActive)
        {
            if (_certifiedWalkPrecisionProbe) _probeCurrentResidencyFreezes++;
            // Never let an asynchronous streaming miss turn into an unbounded
            // fall. Keep the current canonical position while its local
            // collider streams in. Restoring _lastSmoothGroundedPosition here
            // caused leaving noclip in a perfectly valid cave/space location
            // to teleport the player back to an unrelated stale surface.
            _canonicalVelocity=Vector3.Zero;
            _grounded=false;
            _smoothTerrain.SetCanonicalFocus(_canonicalPosition);
            RecenterCollisionOrigin(force:false);
            ApplyWorldTransform();
            return;
        }
        bool targetTerrainReady = _smoothTerrain.IsMovementTerrainResident(
            targetAround,targetVertical,targetAcross,BodyWidth*.5f,BodyHeight);
        if(_smoothTerrain.SurfaceResidencyActive && !targetTerrainReady)
        {
            if (_certifiedWalkPrecisionProbe) _probeTargetResidencyFreezes++;
            // Do not enter an unready cave chunk in any direction. In
            // particular, leaving Y velocity active here let a fast fall
            // outrun the depth-following hot bubble before its colliders were
            // installed.
            _canonicalVelocity.X=0.0f;
            _canonicalVelocity.Y=0.0f;
            _canonicalVelocity.Z=0.0f;
            _smoothTerrain.SetCanonicalFocus(new RingCanonicalPosition(targetAround,targetVertical,targetAcross));
        }
        double angle = (_canonicalPosition.AroundMeters / _grid.GetCanonicalRingCircumferenceMeters64()) * Math.Tau;
        Vector3 radialOut = new((float)Math.Cos(angle),0.0f,(float)Math.Sin(angle));
        Vector3 surfaceUp = -radialOut;
        Vector3 tangent = new(-radialOut.Z,0.0f,radialOut.X);
        Vector3 across = Vector3.Up;
        UpDirection = surfaceUp;
        FloorSnapLength = swimming ? 0.0f : Mathf.Max(0.1f,StepHeight+SafeMargin);
        bool wasGrounded=_grounded;
        Velocity = tangent*_canonicalVelocity.X + surfaceUp*_canonicalVelocity.Y + across*_canonicalVelocity.Z;
        RingCanonicalPosition probeBeforeMove = _canonicalPosition;
        bool probeWalking = _certifiedWalkPrecisionProbe
            && new Vector2(_canonicalVelocity.X, _canonicalVelocity.Z).LengthSquared() > 0.0001f;
        MoveAndSlide();
        if(!swimming&&wasGrounded&&!IsOnFloor()&&_canonicalVelocity.Y<=0.0f)ApplyFloorSnap();
        Vector3 actualWorldVelocity=Velocity;
        _canonicalVelocity=new Vector3(
            actualWorldVelocity.Dot(tangent),
            actualWorldVelocity.Dot(surfaceUp),
            actualWorldVelocity.Dot(across));
        _canonicalPosition=_grid.WorldToCanonicalRingPosition64(GlobalPosition)
            .Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
        _grounded=!swimming&&IsOnFloor();
        if(_grounded)
        {
            _lastSmoothGroundedPosition=_canonicalPosition;
            _hasLastSmoothGroundedPosition=true;
        }
        RecenterCollisionOrigin(force:false);
        Vector3 worldBeforeCanonicalRebuild = GlobalPosition;
        ApplyWorldTransform();
        if (_certifiedWalkPrecisionProbe)
        {
            _probeMaxWorldTransformCorrection = Math.Max(
                _probeMaxWorldTransformCorrection,
                worldBeforeCanonicalRebuild.DistanceTo(GlobalPosition));
            if (probeWalking)
            {
                double aroundDelta = _canonicalPosition.AroundMeters - probeBeforeMove.AroundMeters;
                double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
                aroundDelta -= Math.Round(aroundDelta / circumference) * circumference;
                double acrossDelta = _canonicalPosition.AcrossMeters - probeBeforeMove.AcrossMeters;
                if (aroundDelta * aroundDelta + acrossDelta * acrossDelta < 0.00000001)
                {
                    _probeZeroMotionWalkingFrames++;
                    _probeCurrentZeroMotionRun++;
                    RecordProbeHorizontalBlock(probeBeforeMove);
                    if (_smoothTerrainTraversalInitialized
                        && Math.Abs(probeBeforeMove.AroundMeters - _smoothTerrainTraversalStartAround) < 100.0)
                        _probeZeroMotionBefore100Meters++;
                }
                else
                {
                    _probeCurrentZeroMotionRun = 0;
                }
            }
        }
    }

    private void RecordProbeHorizontalBlock(in RingCanonicalPosition position)
    {
        string collider = "none";
        Vector3 normal = Vector3.Zero;
        if (GetSlideCollisionCount() > 0)
        {
            KinematicCollision3D collision = GetSlideCollision(0);
            collider = collision.GetCollider()?.GetClass() ?? "none";
            normal = collision.GetNormal();
        }
        if (!_probeHasFirstHorizontalBlock)
        {
            _probeHasFirstHorizontalBlock = true;
            _probeFirstHorizontalBlock = position;
            _probeFirstBlockCollider = collider;
            _probeFirstBlockNormal = normal;
            _probeFirstBlockContacts = CaptureProbeSlideContacts();
            _probeFirstBlockTerrain = CaptureProbeBlockTerrain(position);
        }
        if (_probeCurrentZeroMotionRun > _probeLongestZeroMotionRun)
        {
            _probeLongestZeroMotionRun = _probeCurrentZeroMotionRun;
            _probeLongestHorizontalBlock = position;
            _probeLongestBlockCollider = collider;
            _probeLongestBlockNormal = normal;
        }
    }

    private string CaptureProbeSlideContacts()
    {
        if (GetSlideCollisionCount() == 0) return "none";
        var contacts = new List<string>(GetSlideCollisionCount());
        for (int index = 0; index < GetSlideCollisionCount(); index++)
        {
            KinematicCollision3D collision = GetSlideCollision(index);
            string collider = collision.GetCollider()?.GetClass() ?? "none";
            contacts.Add($"{collider}:{collision.GetNormal()}");
        }
        return string.Join("|", contacts);
    }

    private string CaptureProbeBlockTerrain(in RingCanonicalPosition position)
    {
        if (_grid?.TerrainSampler == null || _smoothTerrain == null) return "terrain=unavailable";
        RingLodSurfaceEditSnapshot edits = _smoothTerrain.CaptureLodSurfaceEditSnapshot();
        long centerAround = (long)Math.Floor(position.AroundMeters);
        int centerAcross = Math.Clamp((int)Math.Floor(position.AcrossMeters), 0,
            (int)Math.Round(_grid.RingAcrossWidthMeters));
        int[,] heights = new int[5, 5];
        int maxAroundGradient = 0;
        int maxAcrossGradient = 0;
        for (int z = -2; z <= 2; z++)
        for (int x = -2; x <= 2; x++)
        {
            int height = edits.GetSurfaceHeight(_grid.TerrainSampler, centerAround + x,
                Math.Clamp(centerAcross + z, 0, (int)Math.Round(_grid.RingAcrossWidthMeters)));
            heights[x + 2, z + 2] = height;
            if (x > -2) maxAroundGradient = Math.Max(maxAroundGradient,
                Math.Abs(height - heights[x + 1, z + 2]));
            if (z > -2) maxAcrossGradient = Math.Max(maxAcrossGradient,
                Math.Abs(height - heights[x + 2, z + 1]));
        }
        double maximumSlopeDegrees = Math.Atan(Math.Sqrt(
            maxAroundGradient * maxAroundGradient + maxAcrossGradient * maxAcrossGradient))
            * 180.0 / Math.PI;
        return $"terrain5x5 center={heights[2, 2]}m,maxGradientAround={maxAroundGradient}m,maxGradientAcross={maxAcrossGradient}m,maxSlopeToCanonicalUp={maximumSlopeDegrees:0.00}deg";
    }

    private void CaptureProbeTerrainSlopeReport()
    {
        if (_grid?.TerrainSampler == null || _smoothTerrain == null) return;
        RingLodSurfaceEditSnapshot edits = _smoothTerrain.CaptureLodSurfaceEditSnapshot();
        int across = Math.Clamp((int)Math.Floor(_canonicalPosition.AcrossMeters), 0,
            (int)Math.Round(_grid.RingAcrossWidthMeters));
        long start = (long)Math.Floor(_smoothTerrainTraversalStartAround);
        int previous = edits.GetSurfaceHeight(_grid.TerrainSampler, start, across);
        int maxRise = int.MinValue;
        int maxFall = int.MaxValue;
        long riseAt = start;
        long fallAt = start;
        for (long around = start + 1; around <= start + 160; around++)
        {
            int height = edits.GetSurfaceHeight(_grid.TerrainSampler, around, across);
            int delta = height - previous;
            if (delta > maxRise) { maxRise = delta; riseAt = around; }
            if (delta < maxFall) { maxFall = delta; fallAt = around; }
            previous = height;
        }
        _probeTerrainSlopeReport = $"terrain-slope maxRise={maxRise}m@around={riseAt}, maxFall={maxFall}m@around={fallAt}, across={across}";
    }

    private string DescribeProbeBlocks() => _probeHasFirstHorizontalBlock
        ? $" firstBlock=around={_probeFirstHorizontalBlock.AroundMeters:0.000},across={_probeFirstHorizontalBlock.AcrossMeters:0.000},collider={_probeFirstBlockCollider},normal={_probeFirstBlockNormal},contacts={_probeFirstBlockContacts},{_probeFirstBlockTerrain}; longestBlockFrames={_probeLongestZeroMotionRun},around={_probeLongestHorizontalBlock.AroundMeters:0.000},across={_probeLongestHorizontalBlock.AcrossMeters:0.000},collider={_probeLongestBlockCollider},normal={_probeLongestBlockNormal};"
        : " firstBlock=none; longestBlockFrames=0;";

    private MoveResult ResolveStepMovement(Vector3 start, Vector3 requested, float height)
    {
        float up = ClipAxis(start, height, 1);
        if (up < height - CollisionEpsilon)
        {
            return ResolveMovement(start, requested);
        }

        Vector3 position = start + Vector3.Up * up;
        float dx = ClipAxis(position, requested.X, 0);
        position.X += dx;
        float dz = ClipAxis(position, requested.Z, 2);
        position.Z += dz;
        float downRequest = requested.Y - up;
        float down = ClipAxis(position, downRequest, 1);
        position.Y += down;
        bool grounded = downRequest < 0.0f && down > downRequest + CollisionEpsilon;
        bool blocked = Mathf.Abs(dx - requested.X) > CollisionEpsilon || Mathf.Abs(dz - requested.Z) > CollisionEpsilon;
        return new MoveResult(position, position - start, grounded, blocked);
    }

    private float ClipAxis(Vector3 position, float delta, int axis)
    {
        if (_grid == null || Mathf.Abs(delta) < CollisionEpsilon)
        {
            return delta;
        }

        Box3 box = GetBodyBox(position);
        Vector3 sweptMin = box.Min;
        Vector3 sweptMax = box.Max;
        if (axis == 0) { if (delta > 0) sweptMax.X += delta; else sweptMin.X += delta; }
        if (axis == 1) { if (delta > 0) sweptMax.Y += delta; else sweptMin.Y += delta; }
        if (axis == 2) { if (delta > 0) sweptMax.Z += delta; else sweptMin.Z += delta; }

        int minX = Mathf.FloorToInt(sweptMin.X + CollisionEpsilon);
        int maxX = Mathf.FloorToInt(sweptMax.X - CollisionEpsilon);
        int minY = Mathf.FloorToInt(sweptMin.Y + CollisionEpsilon);
        int maxY = Mathf.FloorToInt(sweptMax.Y - CollisionEpsilon);
        int minZ = Mathf.FloorToInt(sweptMin.Z + CollisionEpsilon);
        int maxZ = Mathf.FloorToInt(sweptMax.Z - CollisionEpsilon);
        float allowed = delta;

        for (int y = minY; y <= maxY; y++)
        for (int z = minZ; z <= maxZ; z++)
        for (int x = minX; x <= maxX; x++)
        {
            long canonicalX = (long)Math.Floor(_collisionOriginAround + x);
            int canonicalZ = checked((int)Math.Floor(_collisionOriginAcross + z));
            if (!_grid.IsSolidCanonicalVoxel(canonicalX, y, canonicalZ)) continue;
            if (axis != 0 && !RangesOverlap(box.Min.X, box.Max.X, x, x + 1)) continue;
            if (axis != 1 && !RangesOverlap(box.Min.Y, box.Max.Y, y, y + 1)) continue;
            if (axis != 2 && !RangesOverlap(box.Min.Z, box.Max.Z, z, z + 1)) continue;

            float boxMin = Axis(box.Min, axis);
            float boxMax = Axis(box.Max, axis);
            float voxelMin = axis == 0 ? x : axis == 1 ? y : z;
            float voxelMax = voxelMin + 1.0f;
            if (delta > 0.0f && voxelMin >= boxMax - CollisionEpsilon)
                allowed = Mathf.Min(allowed, voxelMin - boxMax);
            else if (delta < 0.0f && voxelMax <= boxMin + CollisionEpsilon)
                allowed = Mathf.Max(allowed, voxelMax - boxMin);
        }

        return allowed;
    }

    private Box3 GetBodyBox(Vector3 feet)
    {
        float half = BodyWidth * 0.5f;
        return new Box3(
            new Vector3(feet.X - half, feet.Y, feet.Z - half),
            new Vector3(feet.X + half, feet.Y + BodyHeight, feet.Z + half));
    }

    private void SnapOutOfSolidOnStartup()
    {
        double initialHeight = _canonicalPosition.VerticalMeters;
        if (_grid?.TerrainSampler != null && BodyOverlapsSolid(ToCollisionLocal(_canonicalPosition)))
        {
            long around = (long)Math.Round(_canonicalPosition.AroundMeters);
            int across = Mathf.Clamp(
                Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
                0,
                Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
            int surface = _grid.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
                _grid.TerrainSampler,
                around,
                across);
            _canonicalPosition = _canonicalPosition with { VerticalMeters = surface + 0.05 };
        }

        for (int i = 0; i < 64 && BodyOverlapsSolid(ToCollisionLocal(_canonicalPosition)); i++)
            _canonicalPosition = _canonicalPosition.AddLocal(Vector3.Up * 0.25f);

        if (_grid != null && !Mathf.IsEqualApprox((float)initialHeight, (float)_canonicalPosition.VerticalMeters))
        {
            RecenterCollisionOrigin(force: true);
            ApplyWorldTransform();
        }
    }

    private void BeginSpawnTerrainSettle()
    {
        if (UseTerrain3DSurfacePhysics && TryGetSurfaceTerrainHeight(_canonicalPosition.AroundMeters,_canonicalPosition.AcrossMeters,out float surfaceHeight))
        {
            _canonicalPosition=_canonicalPosition with { VerticalMeters=surfaceHeight+1.05 };
            _undergroundSpawnSettling=false; _spawnTerrainSettling=true; _spawnTerrainSettleRemaining=Math.Max(.1f,SpawnTerrainSettleTimeoutSeconds); return;
        }
        if (_grid?.TerrainSampler == null) return;
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
            0,
            Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        RingLodSurfaceEditSnapshot edits=_smoothTerrain?.CaptureLodSurfaceEditSnapshot()
            ?? _grid.CaptureLodSurfaceEditSnapshot();
        int surface = edits.GetSurfaceHeight(
            _grid.TerrainSampler,
            around,
            across);
        _canonicalPosition = _canonicalPosition with { VerticalMeters = surface + 1.05 };
        _undergroundSpawnSettling = false;
        _spawnTerrainSettling = true;
        _spawnTerrainSettleRemaining = Math.Max(0.1f, SpawnTerrainSettleTimeoutSeconds);
    }

    private bool IsCanonicalPositionBelowTerrain(in RingCanonicalPosition position)
    {
        if (UseTerrain3DSurfacePhysics && TryGetSurfaceTerrainHeight(position.AroundMeters,position.AcrossMeters,out float terrainHeight))
            return position.VerticalMeters < terrainHeight-Math.Max(BodyHeight,2.0f);
        if (_grid?.TerrainSampler == null) return false;
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)position.AcrossMeters),
            0,
            Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        long around = (long)Math.Round(position.AroundMeters);
        RingLodSurfaceEditSnapshot edits=_smoothTerrain?.CaptureLodSurfaceEditSnapshot()
            ?? _grid.CaptureLodSurfaceEditSnapshot();
        int surface = edits.GetSurfaceHeight(
            _grid.TerrainSampler,
            around,
            across);
        // An in-bounds cave interior is authoritative empty density, not an
        // invalid beneath-surface save. Exterior air below the hull or beyond a
        // sidewall must not use this exception.
        if (IsAuthoritativeCaveVoid(position))
            return false;
        return position.VerticalMeters < surface - Math.Max(BodyHeight, 2.0f);
    }

    private bool IsAuthoritativeCaveVoid(in RingCanonicalPosition position)
    {
        if(_grid?.TerrainSampler==null
            ||_grid.TerrainSettings?.EnableDesignedCaveDensityComposition!=true
            ||_smoothTerrain==null
            ||!double.IsFinite(position.AroundMeters)
            ||!double.IsFinite(position.VerticalMeters)
            ||!double.IsFinite(position.AcrossMeters))return false;
        double halfWidth=Math.Max(.15,BodyWidth*.5);
        if(!UndergroundCheckpointSafetyPolicy.IsWithinStructuralBounds(
                position.AcrossMeters,
                position.VerticalMeters,
                halfWidth,
                _grid.RingAcrossWidthMeters,
                _grid.GetLowerHullTopGlobalY()+.25,
                _grid.GetStructuralWallTopGlobalY()))return false;
        int surface=_smoothTerrain.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(
            _grid.TerrainSampler,(long)Math.Floor(position.AroundMeters),(int)Math.Floor(position.AcrossMeters));
        if(position.VerticalMeters>=surface-Math.Max(BodyHeight,2.0f))return false;
        double centerVertical=position.VerticalMeters+BodyHeight*.5;
        foreach((double Around,double Vertical,double Across) probe in new[]
        {
            (position.AroundMeters,centerVertical,position.AcrossMeters),
            (position.AroundMeters-halfWidth,centerVertical,position.AcrossMeters),
            (position.AroundMeters+halfWidth,centerVertical,position.AcrossMeters),
            (position.AroundMeters,centerVertical,position.AcrossMeters-halfWidth),
            (position.AroundMeters,centerVertical,position.AcrossMeters+halfWidth),
            (position.AroundMeters,position.VerticalMeters+BodyHeight,position.AcrossMeters)
        })
            if(!_smoothTerrain.TryQueryEditableDensityAtCanonicalPosition(
                    probe.Around,probe.Vertical,probe.Across,out float density)
                ||density<=0.0f)return false;
        return true;
    }

    private bool ProcessSpawnTerrainSettle(float delta)
    {
        if (!_spawnTerrainSettling || _grid?.TerrainSampler == null) return false;
        bool collisionReady;
        if (UseTerrain3DSurfacePhysics)
        {
            _terrain3DSurfaceGameplay ??= GetNodeOrNull<RingworldTechDemo.Terrain.Surface.RingTerrain3DGameplay>(Terrain3DGameplayPath);
            if(_terrain3DSurfaceGameplay?.EnsureInitialized()!=true || !_terrain3DSurfaceGameplay.IsReady)
            {
                _canonicalVelocity=Vector3.Zero; _grounded=true; RecenterCollisionOrigin(force:false); ApplyWorldTransform(); return true;
            }
            _terrain3DSurfaceGameplay.EnsureCoverage(_canonicalPosition);
            float exactSurface=_terrain3DSurfaceGameplay.SampleHeight(_canonicalPosition.AroundMeters,_canonicalPosition.AcrossMeters);
            if(float.IsFinite(exactSurface)) _canonicalPosition=_canonicalPosition with { VerticalMeters=exactSurface+1.05 };
            collisionReady=_terrain3DSurfaceGameplay.IsReady;
        }
        else if (UseSmoothTerrainPhysics && _smoothTerrain != null)
        {
            _smoothTerrain.SetCanonicalFocus(_canonicalPosition);
            collisionReady = _undergroundSpawnSettling
                ? _smoothTerrain.IsMovementTerrainResident(_canonicalPosition.AroundMeters, _canonicalPosition.VerticalMeters, _canonicalPosition.AcrossMeters, BodyWidth * .5f, BodyHeight)
                : _smoothTerrain.IsSurfaceCollisionNeighborhoodReady((long)Math.Floor(_canonicalPosition.AroundMeters), (int)Math.Floor(_canonicalPosition.AcrossMeters), radiusChunks:1);
        }
        else
        {
            _grid.SetStreamingCanonicalFocusOverride(_canonicalPosition);
            RingChunkCoordinate surfaceChunk = _grid.CanonicalPositionToChunkCoordinate(_canonicalPosition);
            collisionReady=_grid.TryGetChunk(surfaceChunk,out _);
        }
        if (_undergroundSpawnSettling)
        {
            bool withinBounds = UndergroundCheckpointSafetyPolicy.IsWithinStructuralBounds(
                _canonicalPosition.AcrossMeters,
                _canonicalPosition.VerticalMeters,
                Math.Max(.15,BodyWidth*.5),
                _grid.RingAcrossWidthMeters,
                _grid.GetLowerHullTopGlobalY()+.25,
                _grid.GetStructuralWallTopGlobalY());
            collisionReady = UndergroundCheckpointSafetyPolicy.CanResume(
                _grid.TerrainSettings?.EnableDesignedCaveDensityComposition == true,
                withinBounds,
                IsAuthoritativeCaveVoid(_canonicalPosition),
                collisionReady);
        }
        if (collisionReady)
        {
            _spawnTerrainSettling = false;
            _undergroundSpawnSettling = false;
            if(UseTerrain3DSurfacePhysics || UseSmoothTerrainPhysics)
            {
                _grounded=false;
                GD.Print(UseTerrain3DSurfacePhysics
                    ? "Terrain3D surface physical mesh ready at spawn."
                    : "Smooth terrain physical mesh ready at spawn.");
            }
            else
            {
                _grid.ClearStreamingCanonicalFocusOverride();
                SnapOutOfSolidOnStartup();
            }
            return false;
        }

        _spawnTerrainSettleRemaining -= delta;
        if (_spawnTerrainSettleRemaining > 0.0)
        {
            _canonicalVelocity = Vector3.Zero;
            _grounded = true;
            RecenterCollisionOrigin(force: false);
            ApplyWorldTransform();
            return true;
        }

        if (_undergroundSpawnSettling)
        {
            // Fail closed: a restored cave occupant remains motionless while
            // focus keeps requesting local movement coverage.
            _spawnTerrainSettleRemaining = .25f;
            _canonicalVelocity = Vector3.Zero;
            return true;
        }
        _spawnTerrainSettling = false;
        GD.PushWarning(UseSmoothTerrainPhysics
            ? "Smooth terrain collision did not become resident before the settle timeout."
            : "Spawn terrain collision did not become resident before the settle timeout; using deterministic fallback collision.");
        return false;
    }

    private void ProcessMegaSpawnSmokeTest()
    {
        if (!_megaSpawnSmokeTest || _grid?.TerrainSampler == null) return;
        _megaSpawnSmokeFrames++;
        long around = (long)Math.Round(_canonicalPosition.AroundMeters);
        int across = Mathf.Clamp(
            Mathf.RoundToInt((float)_canonicalPosition.AcrossMeters),
            0,
            Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        RingLodSurfaceEditSnapshot edits=_smoothTerrain?.CaptureLodSurfaceEditSnapshot()
            ?? _grid.CaptureLodSurfaceEditSnapshot();
        int surface = edits.GetSurfaceHeight(
            _grid.TerrainSampler,
            around,
            across);
        double heightAboveSurface = _canonicalPosition.VerticalMeters - surface;
        bool collisionResident=UseSmoothTerrainPhysics&&_smoothTerrain!=null
            ? _smoothTerrain.IsSurfaceCollisionReady(around,across)
            : _grid.LoadedChunkCount>0;
        bool stable = !_spawnTerrainSettling
            && collisionResident
            && heightAboveSurface is >= -0.1 and <= 4.0
            && Math.Abs(_canonicalVelocity.Y) <= 0.1f
            && (!UseSmoothTerrainPhysics||_grounded);
        if(UseSmoothTerrainPhysics&&_megaSpawnSmokeFrames%60==0)
        {
            GD.Print(
                $"Smooth physical terrain smoke progress: frame={_megaSpawnSmokeFrames}, height={heightAboveSurface:0.00}m, "
                + $"velocityY={_canonicalVelocity.Y:0.00}, grounded={_grounded}, floor={IsOnFloor()}, "
                + $"slides={GetSlideCollisionCount()}, collisionResident={collisionResident}, "
                + $"world={GlobalPosition}.");
        }
        _megaSpawnStableFrames = stable ? _megaSpawnStableFrames + 1 : 0;
        if (_megaSpawnStableFrames >= 20)
        {
            GD.Print(
                $"Smooth physical terrain spawn smoke test passed: height={heightAboveSurface:0.00}m, "
                + (UseSmoothTerrainPhysics
                    ? $"smoothCollision={_smoothTerrain!.ReadyCollisionCount}, desired={_smoothTerrain.Metrics.Desired}."
                    : $"loaded={_grid.LoadedChunkCount}, collisionDesired={_grid.DesiredCollisionChunkCount}."));
            _megaSpawnSmokeTest = false;
            GetTree().Quit(0);
        }
        else if (_megaSpawnSmokeFrames >= 600)
        {
            GD.PushError(
                $"Smooth physical terrain spawn smoke test failed: height={heightAboveSurface:0.00}m, "
                + $"velocityY={_canonicalVelocity.Y:0.00}, settling={_spawnTerrainSettling}, "
                + (UseSmoothTerrainPhysics
                    ? $"smoothReady={_smoothTerrain?.ReadyRenderCount??0}, smoothCollision={_smoothTerrain?.ReadyCollisionCount??0}."
                    : $"loaded={_grid.LoadedChunkCount}, pending={_grid.PendingChunkLoadCount}, collisionDesired={_grid.DesiredCollisionChunkCount}."));
            _megaSpawnSmokeTest = false;
            GetTree().Quit(1);
        }
    }

    private void ProcessSmoothTerrainTraversalSmokeTest()
    {
        if(!_smoothTerrainTraversalSmokeTest||_grid?.TerrainSampler==null||_smoothTerrain==null)return;
        _smoothTerrainTraversalFrames++;
        if(_spawnTerrainSettling)return;
        if(!_smoothTerrainTraversalInitialized)
        {
            if(!_grounded)return;
            _smoothTerrainTraversalInitialized=true;
            _smoothTerrainTraversalStartAround=_canonicalPosition.AroundMeters;
            if (_certifiedWalkPrecisionProbe) CaptureProbeTerrainSlopeReport();
            GD.Print("Smooth terrain traversal smoke started.");
        }
        long around=(long)Math.Floor(_canonicalPosition.AroundMeters);
        int across=Math.Clamp((int)Math.Floor(_canonicalPosition.AcrossMeters),0,(int)Math.Round(_grid.RingAcrossWidthMeters));
        int surface=_smoothTerrain.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(_grid.TerrainSampler,around,across);
        double height=_canonicalPosition.VerticalMeters-surface;
        double circumference=_grid.GetCanonicalRingCircumferenceMeters64();
        double traveled=_canonicalPosition.AroundMeters-_smoothTerrainTraversalStartAround;
        traveled-=Math.Round(traveled/circumference)*circumference;
        if(height<-1.0)
        {
            GD.PushError($"Smooth terrain traversal smoke fell through: distance={traveled:0.0}m, height={height:0.00}m.");
            _smoothTerrainTraversalSmokeTest=false;
            GetTree().Quit(1);
            return;
        }
        bool complete=Math.Abs(traveled)>=160.0&&_grounded&&Math.Abs(_canonicalVelocity.Y)<=.1f;
        _smoothTerrainTraversalStableFrames=complete?_smoothTerrainTraversalStableFrames+1:0;
        if(_smoothTerrainTraversalStableFrames>=20)
        {
            string probe = _certifiedWalkPrecisionProbe
                ? $" certified-walk-probe target=160m currentFreezes={_probeCurrentResidencyFreezes}, targetFreezes={_probeTargetResidencyFreezes}, zeroMotionWalkingFrames={_probeZeroMotionWalkingFrames}, zeroMotionBefore100m={_probeZeroMotionBefore100Meters}, maxWorldTransformCorrection={_probeMaxWorldTransformCorrection:0.000000}m;{DescribeProbeBlocks()} {_probeTerrainSlopeReport}."
                : string.Empty;
            GD.Print($"Smooth terrain traversal smoke passed: distance={Math.Abs(traveled):0.0}m, height={height:0.00}m, collision={_smoothTerrain.ReadyCollisionCount}.{probe}");
            _smoothTerrainTraversalSmokeTest=false;
            GetTree().Quit(0);
        }
        else if(_smoothTerrainTraversalFrames>=2400)
        {
            string probe = _certifiedWalkPrecisionProbe
                ? $" certified-walk-probe target=160m currentFreezes={_probeCurrentResidencyFreezes}, targetFreezes={_probeTargetResidencyFreezes}, zeroMotionWalkingFrames={_probeZeroMotionWalkingFrames}, zeroMotionBefore100m={_probeZeroMotionBefore100Meters}, maxWorldTransformCorrection={_probeMaxWorldTransformCorrection:0.000000}m;{DescribeProbeBlocks()} {_probeTerrainSlopeReport}."
                : string.Empty;
            GD.PushError($"Smooth terrain traversal smoke timed out: distance={Math.Abs(traveled):0.0}m, height={height:0.00}m, grounded={_grounded}, pending={_smoothTerrain.Metrics.Pending}.{probe}");
            _smoothTerrainTraversalSmokeTest=false;
            GetTree().Quit(1);
        }
    }

    /// <summary>
    /// Exercises the real CharacterBody3D and streaming grid through the
    /// negative-Y hot-parent handoff.  A grid-only residency check cannot
    /// detect a player falling through a missing collider.
    /// </summary>
    private void ProcessSmoothDeepShaftSmokeTest()
    {
        if(!_smoothDeepShaftSmokeTest)return;
        if(_grid?.TerrainSampler==null||_smoothTerrain==null||!UseSmoothTerrainPhysics)
        {
            GD.PushError("Smooth deep shaft smoke lost SmoothTerrainGrid physics.");
            _smoothDeepShaftSmokeTest=false;
            GetTree().Quit(1);
            return;
        }
        _smoothDeepShaftFrames++;
        if(_spawnTerrainSettling)return;
        if(!_smoothDeepShaftInitialized)
        {
            // Keep this vertical regression away from the independent around-ring
            // and 16 m patch seams so a seam-edge ray cannot masquerade as the
            // negative-Y handoff failure under test.
            long currentAround=(long)Math.Floor(_canonicalPosition.AroundMeters);
            _smoothDeepShaftAround=currentAround/32*32+8;
            int currentAcross=Math.Clamp((int)Math.Floor(_canonicalPosition.AcrossMeters),0,(int)Math.Round(_grid.RingAcrossWidthMeters));
            _smoothDeepShaftAcross=Math.Clamp(RingCoordMath.FloorDiv(currentAcross,32)*32+8,8,Math.Max(8,(int)Math.Round(_grid.RingAcrossWidthMeters)-8));
            int surface=_smoothTerrain.CaptureLodSurfaceEditSnapshot().GetSurfaceHeight(_grid.TerrainSampler,_smoothDeepShaftAround,_smoothDeepShaftAcross);
            // Keep the floor strictly below the observed -55/-64 failure range.
            // Keep the floor in the -96..-64 parent after crossing the -64
            // handoff.  Testing directly on -96 would instead conflate this
            // regression with the following parent transition.
            _smoothDeepShaftFloor=Math.Min(-80,surface-56);
            const float shaftRadius=3.0f;
            ulong commandId=700_000;
            long sequence=700_000;
            var shaftCommands=new System.Collections.Generic.List<SmoothTerrainEditCommand>();
            // Start on the sampled surface so the capsule gets a full-width
            // entrance. Starting several metres below creates only a tangent
            // pinhole at the top even though the deeper journal is correct.
            for(int vertical=surface;vertical>=_smoothDeepShaftFloor+3;vertical-=5)
                shaftCommands.Add(new SmoothTerrainEditCommand(
                    SmoothTerrainEditCommand.CurrentVersion,commandId++,sequence++,false,
                    _smoothDeepShaftAround,vertical,_smoothDeepShaftAcross,shaftRadius,(int)SmoothTerrainMaterial.Rock));
            // The loop step may not land exactly on the intended floor.
            shaftCommands.Add(new SmoothTerrainEditCommand(
                SmoothTerrainEditCommand.CurrentVersion,commandId,sequence,false,
                _smoothDeepShaftAround,_smoothDeepShaftFloor+3,_smoothDeepShaftAcross,shaftRadius,(int)SmoothTerrainMaterial.Rock));
            if(!_smoothTerrain.ReplaceEditCommands(shaftCommands,1))
            {
                GD.PushError("Smooth deep shaft smoke could not install its deterministic edit journal.");
                _smoothDeepShaftSmokeTest=false;
                GetTree().Quit(1);
                return;
            }
            _canonicalPosition=new RingCanonicalPosition(_smoothDeepShaftAround,surface+2.0,_smoothDeepShaftAcross);
            _canonicalVelocity=Vector3.Zero;
            _grounded=false;
            _hasLastSmoothGroundedPosition=false;
            RecenterCollisionOrigin(force:true);
            ApplyWorldTransform();
            _smoothTerrain.SetCanonicalFocus(_canonicalPosition);
            _smoothDeepShaftInitialized=true;
            GD.Print($"Smooth deep shaft smoke started: surface={surface}, floor={_smoothDeepShaftFloor}.");
            return;
        }
        double lateral=Math.Max(Math.Abs(_canonicalPosition.AroundMeters-_smoothDeepShaftAround),Math.Abs(_canonicalPosition.AcrossMeters-_smoothDeepShaftAcross));
        if(lateral>1.5||_canonicalPosition.VerticalMeters<_smoothDeepShaftFloor-4.0)
        {
            GD.PushError($"Smooth deep shaft smoke fell through: y={_canonicalPosition.VerticalMeters:0.00}, floor={_smoothDeepShaftFloor}, lateral={lateral:0.00}; {DescribeDeepShaftFloorState()}.");
            _smoothDeepShaftSmokeTest=false;
            GetTree().Quit(1);
            return;
        }
        bool landed=_grounded&&_canonicalPosition.VerticalMeters<=-64.0&&Math.Abs(_canonicalVelocity.Y)<=.1f;
        _smoothDeepShaftStableFrames=landed?_smoothDeepShaftStableFrames+1:0;
        if(_smoothDeepShaftStableFrames>=20)
        {
            GD.Print($"Smooth deep shaft smoke passed: y={_canonicalPosition.VerticalMeters:0.00}, floor={_smoothDeepShaftFloor}, collision={_smoothTerrain.ReadyCollisionCount}.");
            _smoothDeepShaftSmokeTest=false;
            GetTree().Quit(0);
        }
        else if(_smoothDeepShaftFrames>=2400)
        {
            bool readyHere=_smoothTerrain.IsMovementTerrainResident(_canonicalPosition.AroundMeters,_canonicalPosition.VerticalMeters,_canonicalPosition.AcrossMeters,BodyWidth*.5f,BodyHeight);
            bool readyBelow=_smoothTerrain.IsMovementTerrainResident(_canonicalPosition.AroundMeters,_canonicalPosition.VerticalMeters-1.0,_canonicalPosition.AcrossMeters,BodyWidth*.5f,BodyHeight);
            GD.PushError($"Smooth deep shaft smoke timed out: y={_canonicalPosition.VerticalMeters:0.00}, velocity={_canonicalVelocity.Y:0.00}, grounded={_grounded}, floor={IsOnFloor()}, ready={readyHere}/{readyBelow}, anchor={_smoothTerrain.HotPatchAnchorForTests}, pending={_smoothTerrain.Metrics.Pending}; {DescribeDeepShaftFloorState()}.");
            _smoothDeepShaftSmokeTest=false;
            GetTree().Quit(1);
        }
    }

    private string DescribeDeepShaftFloorState()
    {
        if(_grid==null||_smoothTerrain==null)return "terrain unavailable";
        string DensityAt(int vertical)
        {
            Vector3 world=_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_smoothDeepShaftAround,vertical,_smoothDeepShaftAcross));
            return _smoothTerrain.TryQueryEditableDensityAtWorldPosition(world,out float density)
                ? density.ToString("0.000")
                : "query-failed";
        }
        long aroundChunk=_smoothDeepShaftAround>=0?_smoothDeepShaftAround/32:(_smoothDeepShaftAround-31)/32;
        int acrossChunk=RingCoordMath.FloorDiv(_smoothDeepShaftAcross,32);
        int floorChunk=RingCoordMath.FloorDiv(_smoothDeepShaftFloor,32);
        var diagnostics=_smoothTerrain.CaptureDiagnostics();
        var patchCollisionMasks=_smoothTerrain.CaptureHotPatchCollisionMasksForTests();
        string Describe(SmoothTerrainChunkCoordinate coordinate)
            => diagnostics.TryGetValue(coordinate,out SmoothTerrainGrid.ChunkDiagnostics state)
                ? $"{coordinate}:resident={state.Resident},ready={state.Ready},mesh={state.HasRenderMesh},collision={state.HasCollision},patchCollisionMask={(patchCollisionMasks.TryGetValue(coordinate,out byte mask)?mask:0):x2},edits={state.LastInstalledRevision.Edits}"
                : $"{coordinate}:missing";
        Vector3 rayStart=_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_smoothDeepShaftAround,_smoothDeepShaftFloor+4,_smoothDeepShaftAcross));
        Vector3 rayEnd=_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_smoothDeepShaftAround,_smoothDeepShaftFloor-4,_smoothDeepShaftAcross));
        PhysicsRayQueryParameters3D ray=PhysicsRayQueryParameters3D.Create(rayStart,rayEnd);
        ray.Exclude=new Godot.Collections.Array<Rid>{GetRid()};
        Godot.Collections.Dictionary hit=GetWorld3D().DirectSpaceState.IntersectRay(ray);
        string physicsHit=hit.Count==0?"none":hit["position"].AsVector3().ToString();
        var floorCoordinate=new SmoothTerrainChunkCoordinate(aroundChunk,floorChunk,acrossChunk);
        Vector3 floorWorld=_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(_smoothDeepShaftAround,_smoothDeepShaftFloor,_smoothDeepShaftAcross));
        return $"density[y-1/y/y+1]={DensityAt(_smoothDeepShaftFloor-1)}/{DensityAt(_smoothDeepShaftFloor)}/{DensityAt(_smoothDeepShaftFloor+1)}, physicsRay={physicsHit}, editRevision={_smoothTerrain.EditRevision}, floor={Describe(floorCoordinate)}, patchGeometry={_smoothTerrain.DescribeHotPatchGeometryForTests(floorCoordinate,floorWorld)}, below={Describe(new SmoothTerrainChunkCoordinate(aroundChunk,floorChunk-1,acrossChunk))}";
    }

    private bool BodyOverlapsSolid(Vector3 feet)
    {
        if (_grid == null) return false;
        Box3 box = GetBodyBox(feet);
        for (int y = Mathf.FloorToInt(box.Min.Y + CollisionEpsilon); y <= Mathf.FloorToInt(box.Max.Y - CollisionEpsilon); y++)
        for (int z = Mathf.FloorToInt(box.Min.Z + CollisionEpsilon); z <= Mathf.FloorToInt(box.Max.Z - CollisionEpsilon); z++)
        for (int x = Mathf.FloorToInt(box.Min.X + CollisionEpsilon); x <= Mathf.FloorToInt(box.Max.X - CollisionEpsilon); x++)
        {
            long canonicalX = (long)Math.Floor(_collisionOriginAround + x);
            int canonicalZ = checked((int)Math.Floor(_collisionOriginAcross + z));
            if (_grid.IsSolidCanonicalVoxel(canonicalX, y, canonicalZ)) return true;
        }
        return false;
    }

    private void ApplyWorldTransform()
    {
        if (_grid == null) return;
        GlobalPosition = _grid.CanonicalRingPositionToWorld(_canonicalPosition);
        double angle = (_canonicalPosition.AroundMeters / _grid.GetCanonicalRingCircumferenceMeters64()) * Math.Tau;
        Vector3 radialOut = new((float)Math.Cos(angle), 0.0f, (float)Math.Sin(angle));
        Vector3 up = -radialOut;
        Vector3 tangent = new(-radialOut.Z, 0.0f, radialOut.X);
        Vector3 across = Vector3.Up;
        Vector3 worldForward = tangent.Normalized();
        Vector3 worldRight = worldForward.Cross(up).Normalized();
        if (!_cameraBasisInitialized)
        {
            // Establish an exact local horizon at spawn/load. Subsequent ring
            // traversal uses thresholded, smooth corrections in atmosphere and
            // retains this basis inertially while the player is in space.
            GlobalBasis = new Basis(worldRight, up, -worldForward).Orthonormalized();
            _cameraBasisInitialized = true;
            _pendingPhysicsInterpolationReset = true;
            _curvatureAtmosphereStateInitialized = false;
            _curvatureCorrectionActive = false;
            _curvatureCorrectionPending = false;
            _pendingCurvatureCorrectionResponse = 0.0f;
            _curvatureCorrectionAnchorAroundMeters = _canonicalPosition.AroundMeters;
            CurrentHorizonErrorDegrees = 0.0f;
        }
        if (_pendingPhysicsInterpolationReset)
        {
            ResetPhysicsInterpolation();
            _pendingPhysicsInterpolationReset = false;
        }
    }

    private void ApplyCameraRotation()
    {
        if (_head == null)
        {
            return;
        }

        // Mouse look remains local to the body basis. Curvature correction
        // rotates that basis without altering the player's yaw or pitch input.
        _head.Rotation = new Vector3(Mathf.DegToRad(_pitch), _yaw, 0.0f);
    }

    private void WrapCanonicalAround()
    {
        if (_grid == null) return;
        _canonicalPosition = _canonicalPosition.Wrap(_grid.GetCanonicalRingCircumferenceMeters64());
    }

    private Vector3 ToCollisionLocal(in RingCanonicalPosition position) => new(
        (float)(position.AroundMeters - _collisionOriginAround),
        (float)position.VerticalMeters,
        (float)(position.AcrossMeters - _collisionOriginAcross));

    private RingCanonicalPosition FromCollisionLocal(Vector3 position) => new(
        _collisionOriginAround + position.X,
        position.Y,
        _collisionOriginAcross + position.Z);

    private void RecenterCollisionOrigin(bool force)
    {
        double localAround = _canonicalPosition.AroundMeters - _collisionOriginAround;
        double localAcross = _canonicalPosition.AcrossMeters - _collisionOriginAcross;
        if (!force
            && Math.Abs(localAround) <= CollisionSectorSizeMeters * 0.5
            && Math.Abs(localAcross) <= CollisionSectorSizeMeters * 0.5) return;
        // Select the nearest sector, not the previous sector floor. With a
        // floor-only selection, every physics frame in the outer half of a
        // sector requested another canonical anchor update until the next full
        // boundary, repeatedly rebuilding presentation geometry.
        _collisionOriginAround = Math.Floor(
                (_canonicalPosition.AroundMeters + CollisionSectorSizeMeters * 0.5)
                / CollisionSectorSizeMeters)
            * CollisionSectorSizeMeters;
        _collisionOriginAcross = Math.Floor(
                (_canonicalPosition.AcrossMeters + CollisionSectorSizeMeters * 0.5)
                / CollisionSectorSizeMeters)
            * CollisionSectorSizeMeters;
        _grid?.SetFloatingOriginCanonicalAnchor(_canonicalPosition);
        _pendingPhysicsInterpolationReset = true;
    }

    private static bool RangesOverlap(float aMin, float aMax, float bMin, float bMax) => aMax > bMin + CollisionEpsilon && aMin < bMax - CollisionEpsilon;
    private static float Axis(Vector3 value, int axis) => axis == 0 ? value.X : axis == 1 ? value.Y : value.Z;
    private static bool IsPressed(Key key) => Input.IsKeyPressed(key) || Input.IsPhysicalKeyPressed(key);
}
