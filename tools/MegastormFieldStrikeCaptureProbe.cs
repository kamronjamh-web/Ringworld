using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

/// <summary>
/// Helper for tools/megastorm_field_strike_capture.gd (WO13 captures, debug only): installs the stub field underside the containment field will provide
/// (the field is not built, so the real game has no provider and therefore no field strikes), sets the strike-rate scale, and reports the live field strikes
/// (world points for the camera test, pulse, leader progress, which channel set carries them).
/// </summary>
public partial class MegastormFieldStrikeCaptureProbe : RefCounted
{
    public void InstallStubField(double wallTopMeters, double apexMeters, double widthMeters)
    {
        double a = 0.5 * widthMeters, b = apexMeters - wallTopMeters;
        MegastormFieldCoupling.FieldUndersideProvider = across =>
        {
            if (across < 0.0 || across > widthMeters) return double.NaN;
            double x = (across - a) / a;
            return wallTopMeters + b * Math.Sqrt(Math.Max(0.0, 1.0 - x * x));
        };
    }

    public void SetLayerEnabled(bool on) { if (RingFieldStrikeLayer.Instance != null) RingFieldStrikeLayer.Instance.Enabled = on; }
    public void ClearField() => MegastormFieldCoupling.FieldUndersideProvider = null;
    public void SetStrikeScale(float frequency, bool reduced) => LightningVisualPolicy.Apply(frequency, 1.0f, reduced);

    /// <summary>The live field strike nearest to the camera (a Dictionary; "count" 0 when none), with its world endpoints and handoff set.</summary>
    public Godot.Collections.Dictionary Live(Node world)
    {
        var result = new Godot.Collections.Dictionary { ["count"] = 0 };
        var clouds = world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
        var grid = world.GetNodeOrNull<RingworldTechDemo.Voxel.Phase2.VoxelChunkGridManager>("ChunkGridManager");
        var explorer = world.GetNodeOrNull<RingworldTechDemo.Voxel.Integration.RingVoxelExplorerController>("Explorer");
        if (clouds == null || grid == null || explorer == null) return result;
        double now = clouds.CloudSimulationTimeSeconds, circumference = grid.GetCanonicalRingCircumferenceMeters64();
        RingCanonicalPosition o = explorer.CanonicalPosition;
        int count = 0; double best = double.MaxValue;
        foreach (RingLightningEvent e in clouds.ActiveLightningEvents)
        {
            if (!e.FieldStrike) continue;
            count++;
            double da = MegastormModel.WrapSigned(e.GroundPosition.AroundMeters - o.AroundMeters, circumference), dz = e.GroundPosition.AcrossMeters - o.AcrossMeters;
            double d = Math.Sqrt(da * da + dz * dz);
            if (d >= best) continue;
            best = d;
            result["id"] = e.EventId; result["distance"] = d; result["pulse"] = e.PulseAt(now) * e.Energy; result["progress"] = e.FieldLeaderProgress(now);
            result["age"] = now - e.StartTimeSeconds; result["duration"] = e.LifeSeconds; result["kind"] = e.SystemKind;
            result["world_base"] = grid.CanonicalRingPositionToWorld(e.CloudPosition); result["world_contact"] = grid.CanonicalRingPositionToWorld(e.GroundPosition);
            result["length"] = e.GroundPosition.VerticalMeters - e.CloudPosition.VerticalMeters;
            result["main_weight"] = RingFieldStrikeLayer.MainWeight(d, RingFieldStrikeLayer.FarSetBound);
        }
        result["count"] = count;
        result["arcs"] = clouds.ActiveContactArcs.Count;
        result["far_bound"] = RingFieldStrikeLayer.FarSetBound;
        result["far_live"] = RingFieldStrikeLayer.Instance?.FarLiveCount ?? 0;
        var fx = world.GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects");
        if (fx != null) { result["channel_dbg"] = fx.LightningChannelDebug(); result["channel_bolts"] = fx.LightningChannelBoltCount; result["channel_segments"] = fx.LightningChannelSegmentCount; result["channel_us"] = fx.LightningChannelMicros; }
        return result;
    }

    public string Catalogue(Node world, double seconds)
    {
        var clouds = world.GetNodeOrNull<RingVolumetricCloudLayer>("RingVolumetricClouds");
        return clouds == null ? "no clouds" : $"t={clouds.CloudSimulationTimeSeconds:0.0}";
    }
}
