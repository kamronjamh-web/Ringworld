using System;
using System.Globalization;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

public enum EmitterTeleportPhase { Idle, Holding, Arrived, TimedOut, Failed }

/// <summary>
/// One site teleport from request to arrival: computes the viewpoint pose, jumps the explorer, then holds the streaming focus on the
/// destination until the player has collision (and, near the spire, its loaded POI slot) or 60 s pass.
/// </summary>
public sealed class EmitterSiteTeleport
{
    public const double TimeoutSeconds = 60d;

    private RingVoxelExplorerController? _explorer;
    private RingTerrain3DGameplay? _gameplay;
    private EmitterPoiStreamer? _streamer;
    private VoxelChunkGridManager? _grid;
    private bool _requireCoverage, _requireSlot, _requireCollision;
    private string _label = "";
    private Terrain.Sites.SiteKind _siteKind;

    public EmitterTeleportPhase State { get; private set; } = EmitterTeleportPhase.Idle;
    public string Message { get; private set; } = "";
    public EmitterViewpointPose Pose { get; private set; }
    public double ElapsedSeconds { get; private set; }

    public bool Begin(RingVoxelExplorerController? explorer, RingTerrain3DGameplay? gameplay, EmitterPoiStreamer? streamer, VoxelChunkGridManager? grid,
        EmitterSiteNavigator navigator, int site, EmitterViewpoint viewpoint, double farKm, double highKm)
    {
        ReleaseFocus();
        if (explorer == null || !GodotObject.IsInstanceValid(explorer) || grid == null) return Fail("Teleport unavailable: no player.");
        if (gameplay == null || !GodotObject.IsInstanceValid(gameplay) || !gameplay.IsReady) return Fail("Teleport unavailable: Terrain3D gameplay is not ready.");
        if (!navigator.TryGetSite(site, out _)) return Fail($"No site #{site} (0..{navigator.Count - 1}).");
        _explorer = explorer; _gameplay = gameplay; _streamer = streamer; _grid = grid;
        _siteKind = navigator.Placements.GetPlacement(site).Kind;
        EmitterViewpointPose pose = navigator.GetPose(site, viewpoint, (around, across) =>
        {
            float height = gameplay.SampleCollisionHeight(around, across);
            return float.IsFinite(height) ? height : (float)navigator.Placements.GetPlacement(site).PadHeightMeters;
        }, farKm, highKm);
        Pose = pose;
        _label = viewpoint.ToString().ToLowerInvariant();
        if (!explorer.TryTeleportToCanonical(new RingCanonicalPosition(pose.Around, pose.Vertical, pose.Across), (float)pose.YawDeg, (float)pose.PitchDeg, pose.Flight, out string feedback))
            return Fail(feedback);
        // Grounded arrivals and the gallery need collision under the player; high and hovering views only need the settle to be done.
        _requireCoverage = !pose.Flight || viewpoint == EmitterViewpoint.Gallery;
        _requireSlot = viewpoint == EmitterViewpoint.Entry || viewpoint == EmitterViewpoint.Gallery;
        _requireCollision = viewpoint == EmitterViewpoint.Gallery;
        ElapsedSeconds = 0d;
        State = EmitterTeleportPhase.Holding;
        Message = $"Site #{site} {_label}: moving ({pose.Detail}), waiting for terrain.";
        return true;
    }

    public EmitterTeleportPhase Poll(double delta)
    {
        if (State != EmitterTeleportPhase.Holding) return State;
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer) || _gameplay == null || !GodotObject.IsInstanceValid(_gameplay))
        {
            Fail("Teleport interrupted: player or terrain was removed.");
            return State;
        }
        ElapsedSeconds += Math.Max(0d, delta);
        RingCanonicalPosition position = _explorer.CanonicalPosition;
        if (_requireCoverage) _gameplay.EnsureCoverage(position);
        bool settled = !_explorer.IsSpawnTerrainSettling;
        bool covered = !_requireCoverage || _gameplay.HasCollisionCoverage(position);
        int slot = -1;
        bool slotOk = true, collisionOk = true;
        if (_requireSlot && _streamer != null && GodotObject.IsInstanceValid(_streamer) && _streamer.IsBound && _streamer.Enabled && !_streamer.Unavailable
            && !System.Linq.Enumerable.Contains(_streamer.FailedKinds, _siteKind))
        {
            slot = _streamer.FindSlotForSite(Pose.Site);
            slotOk = slot >= 0;
            collisionOk = !_requireCollision || (slot >= 0 && _streamer.IsSlotCollisionEnabled(slot));
        }
        if (settled && covered && slotOk && collisionOk)
        {
            State = EmitterTeleportPhase.Arrived;
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Site #{Pose.Site} {_label}: arrived {ElapsedSeconds:0.0} s{(slot >= 0 ? $", slot {slot}" : "")}{(_requireCollision && slot >= 0 ? ", collision on" : "")}.");
            ReleaseFocus();
        }
        else if (ElapsedSeconds >= TimeoutSeconds)
        {
            State = EmitterTeleportPhase.TimedOut;
            Message = string.Create(CultureInfo.InvariantCulture,
                $"Site #{Pose.Site} {_label}: timed out after {TimeoutSeconds:0} s (settled {settled}, coverage {covered}, slot {(slotOk ? "ok" : "missing")}, collision {(collisionOk ? "ok" : "off")}).");
            ReleaseFocus();
        }
        return State;
    }


    private bool Fail(string message)
    {
        State = EmitterTeleportPhase.Failed;
        Message = message;
        return false;
    }

    private void ReleaseFocus()
    {
        if (_grid != null && GodotObject.IsInstanceValid(_grid)) _grid.ClearStreamingCanonicalFocusOverride();
    }
}
