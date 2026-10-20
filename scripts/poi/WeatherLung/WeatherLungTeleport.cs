using System;
using System.Globalization;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Poi;

public enum WeatherLungViewpoint { DamCrest, IntakeOverlook, Far40Km }

public enum WeatherLungTeleportPhase { Idle, Holding, Arrived, TimedOut, Failed }

/// <summary>Destination in canonical metres (around, vertical, across) plus the view direction. All three viewpoints arrive in noclip (hover).</summary>
public readonly record struct WeatherLungViewpointPose(WeatherLungViewpoint Viewpoint, double Around, double Vertical, double Across, double YawDeg, double PitchDeg, string Detail);

/// <summary>
/// Teleport to the ACTUAL Weather Lung site (the poses are computed from the placement, so they follow it wherever the search put it): the dam crest,
/// the intake overlook, or 40 km out on the dam side. Same shape as <see cref="EmitterSiteTeleport"/>: jump the explorer, then hold the streaming focus
/// on the destination until the terrain has settled and (for the close views) the detail scene is attached, or 60 s pass.
/// </summary>
public sealed class WeatherLungTeleport
{
    public const double TimeoutSeconds = 60d, EyeAboveMarkerMeters = 3d, FarMeters = 40_000d, FarHoverMeters = 1_500d;
    /// <summary>Station-local (Godot) aim points: the intake mouth at the arch, and the arch centre for the far view.</summary>
    public static readonly Vector3 IntakeAim = new(-11250f, 1500f, 0f), StationAim = new(0f, 800f, 0f);

    private RingVoxelExplorerController? _explorer;
    private WeatherLungStreamer? _streamer;
    private VoxelChunkGridManager? _grid;
    private bool _requireDetail;
    private string _label = "";

    public WeatherLungTeleportPhase State { get; private set; } = WeatherLungTeleportPhase.Idle;
    public string Message { get; private set; } = "";
    public WeatherLungViewpointPose Pose { get; private set; }
    public double ElapsedSeconds { get; private set; }

    /// <summary>Pure pose computation (unit-testable): <paramref name="ringRadius"/> is the ring radius minus the vertical base offset.</summary>
    public static WeatherLungViewpointPose ComputePose(WeatherLungPlacement placement, WeatherLungViewpoint viewpoint, Vector3 damCrestLocal, Vector3 overlookLocal, double ringRadius)
    {
        WeatherLungSite site = placement.Site;
        Vector3 local, aim;
        string detail;
        switch (viewpoint)
        {
            case WeatherLungViewpoint.DamCrest:
                local = damCrestLocal + new Vector3(0f, (float)EyeAboveMarkerMeters, 0f);
                aim = IntakeAim;
                detail = "dam crest";
                break;
            case WeatherLungViewpoint.IntakeOverlook:
                local = overlookLocal;
                aim = IntakeAim;
                detail = "intake overlook";
                break;
            default:
                local = new Vector3((float)FarMeters, (float)FarHoverMeters, 0f);
                aim = StationAim;
                detail = "40 km out on the dam side";
                break;
        }
        ToCanonical(placement, local, out double a, out double v, out double x);
        ToCanonical(placement, aim, out double aa, out double av, out double ax);
        LookAt(ringRadius, placement.Recipe.CircumferenceM / Math.Tau, a, v, x, aa, av, ax, out double yaw, out double pitch);
        return new WeatherLungViewpointPose(viewpoint, a, v, x, yaw, pitch, detail);
    }

    /// <summary>Station-local (Godot x, y up, z) to canonical (around, vertical, across): asset local Z maps to (sin yaw, -cos yaw), so Blender y = -z.</summary>
    public static void ToCanonical(WeatherLungPlacement placement, Vector3 local, out double around, out double vertical, out double across)
    {
        placement.FromAssetLocal(local.X, -local.Z, out double du, out double dw);
        around = placement.Site.Around + du;
        across = placement.Site.Across + dw;
        vertical = placement.Site.PadHeight + WeatherLungStreamer.PadClearanceMeters + local.Y;
    }

    /// <summary>Exact yaw / pitch (degrees) from an observer to a target on the ring cylinder; yaw = atan2(across, tangent) like the explorer, positive pitch looks up.</summary>
    public static void LookAt(double ringRadius, double angleRadius, double fromAround, double fromVertical, double fromAcross,
        double toAround, double toVertical, double toAcross, out double yawDeg, out double pitchDeg)
    {
        double phi0 = fromAround / angleRadius, phi1 = toAround / angleRadius;
        double r0 = ringRadius - fromVertical, r1 = ringRadius - toVertical;
        double dx = Math.Cos(phi1) * r1 - Math.Cos(phi0) * r0, dz = Math.Sin(phi1) * r1 - Math.Sin(phi0) * r0, dy = toAcross - fromAcross;
        double tangent = -Math.Sin(phi0) * dx + Math.Cos(phi0) * dz;
        double up = -Math.Cos(phi0) * dx - Math.Sin(phi0) * dz;
        yawDeg = Math.Atan2(dy, tangent) * (180d / Math.PI);
        pitchDeg = Math.Atan2(up, Math.Sqrt(tangent * tangent + dy * dy)) * (180d / Math.PI);
    }

    public bool Begin(RingVoxelExplorerController? explorer, WeatherLungStreamer? streamer, VoxelChunkGridManager? grid, WeatherLungViewpoint viewpoint)
    {
        ReleaseFocus();
        if (explorer == null || !GodotObject.IsInstanceValid(explorer) || grid == null) return Fail("Teleport unavailable: no player.");
        if (streamer == null || !GodotObject.IsInstanceValid(streamer) || streamer.Placement == null) return Fail("The Weather Lung is unavailable (needs a v8 world).");
        _explorer = explorer; _streamer = streamer; _grid = grid;
        Vector3 dam = streamer.GetMarker("DamCrest", new Vector3(7854f, 655f, 3439f));
        Vector3 overlook = streamer.GetMarker("IntakeOverlook", new Vector3(-7000f, 150f, 0f));
        var snapshot = grid.CaptureLodGeometrySnapshot();
        WeatherLungViewpointPose pose = ComputePose(streamer.Placement, viewpoint, dam, overlook, snapshot.RingRadiusMeters - snapshot.RingVerticalBaseOffsetMeters);
        Pose = pose;
        _label = pose.Detail;
        if (!explorer.TryTeleportToCanonical(new RingCanonicalPosition(pose.Around, pose.Vertical, pose.Across), (float)pose.YawDeg, (float)pose.PitchDeg, true, out string feedback))
            return Fail(feedback);
        _requireDetail = viewpoint != WeatherLungViewpoint.Far40Km;
        ElapsedSeconds = 0d;
        State = WeatherLungTeleportPhase.Holding;
        Message = $"Weather Lung {_label}: moving, waiting for terrain.";
        return true;
    }

    public WeatherLungTeleportPhase Poll(double delta)
    {
        if (State != WeatherLungTeleportPhase.Holding) return State;
        if (_explorer == null || !GodotObject.IsInstanceValid(_explorer) || _streamer == null || !GodotObject.IsInstanceValid(_streamer))
        {
            Fail("Teleport interrupted: player or station was removed.");
            return State;
        }
        ElapsedSeconds += Math.Max(0d, delta);
        bool settled = !_explorer.IsSpawnTerrainSettling;
        bool detailOk = !_requireDetail || _streamer.DetailState is WeatherLungDetailState.Attached or WeatherLungDetailState.Failed || !_streamer.Enabled;
        if (settled && detailOk)
        {
            State = WeatherLungTeleportPhase.Arrived;
            Message = string.Create(CultureInfo.InvariantCulture, $"Weather Lung {_label}: arrived {ElapsedSeconds:0.0} s, detail {_streamer.DetailState}.");
            ReleaseFocus();
        }
        else if (ElapsedSeconds >= TimeoutSeconds)
        {
            State = WeatherLungTeleportPhase.TimedOut;
            Message = string.Create(CultureInfo.InvariantCulture, $"Weather Lung {_label}: timed out after {TimeoutSeconds:0} s (settled {settled}, detail {_streamer.DetailState}).");
            ReleaseFocus();
        }
        return State;
    }

    private bool Fail(string message)
    {
        State = WeatherLungTeleportPhase.Failed;
        Message = message;
        return false;
    }

    private void ReleaseFocus()
    {
        if (_grid != null && GodotObject.IsInstanceValid(_grid)) _grid.ClearStreamingCanonicalFocusOverride();
    }
}
