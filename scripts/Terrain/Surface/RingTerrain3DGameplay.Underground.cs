using System;
using Godot;
using RingworldTechDemo.Poi;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// M2-3 underground gameplay semantics: the single authority for "is this point inside a site's underground", the open-plug
/// ground (the SDF floor below the player, not the old plug top), and the recovery that returns a player who left the volume
/// to the site's arrival overlook. The plug is an open iris; nothing here lifts or clamps a player inside the volume.
/// </summary>
public partial class RingTerrain3DGameplay
{
    public const double RecoveryRockSeconds = 0.5, RecoveryRockDistanceMeters = 2d, RecoveryBelowFloorMeters = 20d, RecoveryRockBelowSurfaceMeters = 3d;

    private double _undergroundRockSeconds;
    private EmitterSiteNavigator? _recoveryNavigator;
    private EmitterSiteRegistry? _recoveryNavigatorRegistry;

    /// <summary>Grave-key "Underground recovery" setting: teleport a player who left the underground volume to the arrival overlook.</summary>
    public bool UndergroundRecoveryEnabled { get; set; } = true;
    public int UndergroundRecoveryCount { get; private set; }
    public int LastUndergroundRecoverySite { get; private set; } = -1;
    public string LastUndergroundRecoveryReason { get; private set; } = "";

    /// <summary>
    /// True when the canonical point is inside a site's underground air (bore, shaft, cavern, bays, tunnel). Respects the
    /// "Bore apertures" switch: when it is off (and no committed hole remains) there is no underground.
    /// </summary>
    public bool IsInsideSiteUnderground(double around, double vertical, double across)
        => TryGetSiteUnderground(around, vertical, across, out _, out _);

    public bool TryGetSiteUnderground(double around, double vertical, double across, out int site, out Vector3 local)
    {
        site = -1; local = default;
        return SurfaceApertureConsumersActive && EmitterSiteUnderground.TryFindSiteUnderground(_sampler?.EmitterSites, around, vertical, across, out site, out local);
    }

    /// <summary>True inside a bore liner column horizontally (any height) while bore consumers are active.</summary>
    public bool IsInsideBoreColumn(double around, double across)
        => SurfaceApertureConsumersActive && EmitterSiteApertures.TryGetBoreFloorAt(_sampler?.EmitterSites, around, across, out _);

    /// <summary>
    /// The zone in which the capsule lift, the spawn-settle snap and the coverage velocity clamp are disabled: the underground
    /// volume plus the bore column above it (a player over the open bore is about to fall into it).
    /// </summary>
    public bool IsInsideSiteUndergroundZone(double around, double vertical, double across)
        => IsInsideBoreColumn(around, across) || IsInsideSiteUnderground(around, vertical, across);

    /// <summary>
    /// Ground height beneath a point that may be underground: the SDF floor below the player inside the volume (or bore column),
    /// else the usual collision height.
    /// </summary>
    public float SampleGroundHeightAt(double around, double vertical, double across)
    {
        if (!_synchronousBenchmark && SurfaceApertureConsumersActive
            && EmitterSiteUnderground.TryFindFloorBelow(_sampler?.EmitterSites, around, vertical, across, out float floor)) return floor;
        return SampleCollisionHeight(around, across);
    }

    /// <summary>Bore-column ground for callers without a vertical: the floor below the bore top, else false.</summary>
    private bool TryGetBoreColumnFloor(double around, double across, out float floor)
    {
        floor = 0f;
        // A vertical far above the bore top starts the march at the top of the column (and requires the point inside the liner).
        return SurfaceApertureConsumersActive
            && EmitterSiteUnderground.TryFindFloorBelow(_sampler?.EmitterSites, around, 1.0e9, across, out floor);
    }

    // ---- Recovery -------------------------------------------------------------------------------------------------------------

    private void ServiceUndergroundRecovery(double delta)
    {
        if (!UndergroundRecoveryEnabled || _explorer == null || _startupPending || !SurfaceApertureConsumersActive) { _undergroundRockSeconds = 0d; return; }
        EmitterSiteRegistry? registry = _sampler?.EmitterSites;
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        if (registry == null || !EmitterSiteUnderground.TryLocateInReach(registry, position.AroundMeters, position.VerticalMeters, position.AcrossMeters, out int site, out Vector3 local))
        {
            _undergroundRockSeconds = 0d;
            return;
        }
        EmitterSiteUndergroundShape shape = EmitterSiteUnderground.Shape(registry.Sites[site].Kind);
        string reason = "";
        if (local.Y < shape.CavernFloorYM - RecoveryBelowFloorMeters) reason = "below the cavern floor";
        // Rock only counts below the terrain surface: a player standing on the ground beside the spire is inside the volume's AABB
        // but not underground.
        if (reason.Length == 0 && !_explorer.NoclipEnabled && local.Y >= shape.LocalBounds.Position.Y && local.Y <= shape.LocalBounds.End.Y
            && position.VerticalMeters < SampleCollisionHeight(position.AroundMeters, position.AcrossMeters) - RecoveryRockBelowSurfaceMeters
            && EmitterSiteUnderground.SignedDistance(registry.Sites[site].Kind, local) > RecoveryRockDistanceMeters)
        {
            _undergroundRockSeconds += Math.Max(0d, delta);
            if (_undergroundRockSeconds > RecoveryRockSeconds) reason = "stuck in rock";
        }
        else if (reason.Length == 0) _undergroundRockSeconds = 0d;
        if (reason.Length == 0) return;
        _undergroundRockSeconds = 0d;
        RecoverToArrivalOverlook(site, shape, reason);
    }

    private void RecoverToArrivalOverlook(int site, EmitterSiteUndergroundShape shape, string reason)
    {
        if (_explorer == null || _grid == null) return;
        EmitterSiteNavigator? navigator = RecoveryNavigator();
        if (navigator == null) return;
        Vector3 o = shape.ArrivalOverlookLocal;
        (double a, double v, double x) = navigator.AssetLocalToCanonical(site, o.X, o.Y, o.Z);
        (double ra, double rv, double rx) = navigator.AssetLocalToCanonical(site, 0d, shape.ChamberCenterYM, 0d);
        navigator.LookAt(a, v + EmitterSiteNavigator.EyeHeightMeters, x, ra, rv, rx, out double yaw, out double pitch);
        // TODO(M2-4): the reactor POI (and its overlook platform) is not loaded yet, so there is nothing to stand on: hover in
        // flight/noclip. Switch this to a grounded settle (noclip: false) once the M2-4 shell/reactor collision exists.
        if (!_explorer.TryTeleportToCanonical(new RingCanonicalPosition(a, v, x), (float)yaw, (float)pitch, true, out _)) return;
        UndergroundRecoveryCount++;
        LastUndergroundRecoverySite = site;
        LastUndergroundRecoveryReason = reason;
        GD.Print($"Underground recovery: site #{site} {reason}; returned to the arrival overlook.");
    }

    private EmitterSiteNavigator? RecoveryNavigator()
    {
        EmitterSiteRegistry? registry = _sampler?.EmitterSites;
        if (registry == null || _grid == null) return null;
        if (_recoveryNavigator != null && ReferenceEquals(registry, _recoveryNavigatorRegistry)) return _recoveryNavigator;
        var snapshot = _grid.CaptureLodGeometrySnapshot();
        _recoveryNavigator = new EmitterSiteNavigator(new EmitterSitePlacementSource(registry),
            snapshot.RingRadiusMeters - snapshot.RingVerticalBaseOffsetMeters, _sampler!.SeaLevelY);
        _recoveryNavigatorRegistry = registry;
        return _recoveryNavigator;
    }
}
