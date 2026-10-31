using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Read-only state probe for tools/megastorm_real_path_check.gd: the megastorm chain as the real player path sees it (start menu, Continue, save
/// restore, explorer), one line per call. Nothing here moves the storm or the camera.
/// </summary>
public partial class MegastormRealPathProbe : RefCounted
{
    public string State(Node world)
    {
        try
        {
            var mega = world.GetNodeOrNull<RingMegastorm>("RingMegastorm");
            var clouds = world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
            var explorer = world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
            var bridge = world.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
            var quality = world.GetNodeOrNull<RingGraphicsQualityController>("GraphicsQuality");
            var far = world.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
            if (mega == null) return "no RingMegastorm node";
            double now = mega.Now;
            MegastormFrame f = mega.Model.Evaluate(now);
            string obs = "observer=none";
            if (explorer != null)
            {
                RingCanonicalPosition p = explorer.CanonicalPosition;
                MegastormSample s = mega.Query(p, now);
                Camera3D? cam = explorer.GetViewport()?.GetCamera3D();
                obs = $"observer around={p.AroundMeters / 1000.0:0.000}km vert={p.VerticalMeters:0}m across={p.AcrossMeters / 1000.0:0.000}km xi={s.XiMeters / 1000.0:0.00}km phase={s.Phase} tta={s.TimeToArrivalSeconds:0}s cam={(cam != null ? cam.GetPath().ToString() : "none")}";
            }
            string strip = "strip=none";
            GodotObject? driver = bridge?.GetMegastormStripDriver();
            if (driver != null) strip = $"strip valid={driver.Get("valid").AsBool()} builds={driver.Get("builds_completed").AsInt64()} real={driver.Get("real_texture").AsBool()}";
            return $"t={now:0.0} triggered={mega.Triggered} active={f.Active} level={f.Level:0.00} line={f.AroundMeters / 1000.0:0.000}km dir={f.Direction} v={f.SpeedMetersPerSecond:0} id={mega.Model.State.TriggerId} "
                + $"source={(clouds?.MegastormSource != null)} paused={clouds?.CloudSimulationPaused} {obs} "
                + $"ci={bridge?.CameraIndependentClouds} uploaded={bridge?.UploadedWeatherSystemCount} {strip} preset={quality?.CurrentPresetName ?? "-"} "
                + $"{far?.FarMegastormStatus ?? "far=none"}";
        }
        catch (Exception error) { return "probe error: " + error.Message; }
    }
}
