using System;
using Godot;
using RingworldTechDemo;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

/// <summary>Capture helpers of tools/megastorm_wo11_capture.gd (WO11 hazard visuals): ground-level poses, the hazard node diagnostics and a test wall.</summary>
public partial class MegastormWo11Capture : RefCounted
{
    public string ErrorText { get; private set; } = "";
    private static RingVoxelExplorerController Explorer(Node world) => world.GetNode<RingVoxelExplorerController>("Explorer");
    private static RingVolumetricCloudLayer Clouds(Node world) => world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");

    /// <summary>Ground height (m) at the camera's around / across plus an offset.</summary>
    public double GroundHeight(Node world, double offsetAround, double offsetAcross)
    {
        RingCanonicalPosition p = Explorer(world).CanonicalPosition;
        Func<double, double, double>? ground = Clouds(world).CreateSurfaceHeightSampler();
        return ground != null ? ground(p.AroundMeters + offsetAround, p.AcrossMeters + offsetAcross) : p.VerticalMeters;
    }

    /// <summary>Puts the camera <paramref name="upMeters"/> above the ground at its current around / across.</summary>
    public bool GroundPose(Node world, double upMeters, double yawRadians, double pitchDegrees, double offsetAround = 0.0, double offsetAcross = 0.0)
    {
        try
        {
            RingCanonicalPosition p = Explorer(world).CanonicalPosition;
            double h = GroundHeight(world, offsetAround, offsetAcross) + upMeters;
            Explorer(world).RestoreSavedState(new RingCanonicalPosition(p.AroundMeters + offsetAround, h, p.AcrossMeters + offsetAcross), Vector3.Zero, (float)yawRadians, (float)pitchDegrees, false, 0);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    public string Fx(Node world)
    {
        var fx = world.GetNodeOrNull<RingMegastormHazardFx>("MegastormHazardFx");
        var tremor = world.GetNodeOrNull<RingGroundTremor>("GroundTremor");
        var node = world.GetNode<RingMegastorm>("RingMegastorm");
        MegastormSample s = node.QueryNow(Explorer(world).CanonicalPosition);
        return $"xi={s.XiMeters:F0} kpa={s.Hazards.OverpressureKPa:F1} env={s.BoomEnvelope:F3} shake={tremor?.ShakeDegrees ?? -1f:F4} tremor={s.TremorLevel:F3} blast_x={fx?.LastBlast.X ?? -1f:F2} front={fx?.LastBlast.Y ?? 0f:F1} burst={fx?.LastBurst ?? -1f:F2} debris={fx?.LastDebrisLevel ?? -1f:F2} shock={fx?.LastShockAmplitude ?? -1f:F2} attached={fx?.ShockAttached} disp={fx?.ShockDispatches ?? -1} wind={MegastormHazardMath.WindAlong(s.XiMeters):F0}";
    }

    public double FxFront(Node world) => world.GetNodeOrNull<RingMegastormHazardFx>("MegastormHazardFx")?.LastBlast.Y ?? 0.0;
    public double Kpa(Node world) => world.GetNode<RingMegastorm>("RingMegastorm").QueryNow(Explorer(world).CanonicalPosition).Hazards.OverpressureKPa;
    public double Shake(Node world) => world.GetNodeOrNull<RingGroundTremor>("GroundTremor")?.ShakeDegrees ?? 0f;
    public double Burst(Node world) => world.GetNodeOrNull<RingMegastormHazardFx>("MegastormHazardFx")?.LastBurst ?? 0f;

    public string Streaks(Node world)
    {
        var effects = world.GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects");
        if (effects == null) return "no effects"; var d = effects.GetRainStreakDiagnostics(); return $"slope={d["slope"]} aligned={d["aligned"]}";
    }

    public string DebrisState(Node world)
    {
        var d = world.GetNodeOrNull<RingGustFrontDebris>("GustFrontDebris");
        if (d == null) return "no debris";
        Godot.Collections.Dictionary diag = d.GetDiagnostics();
        return $"active={d.Active} level={d.Level:F2} smoothed={diag["smoothed_level"]} drift={diag["drift"]} wind={WindNow(world)}";
    }

    public Vector2 WindNow(Node world)
    {
        var effects = world.GetNodeOrNull<RingLocalWeatherEffects>("LocalWeatherEffects");
        return effects != null && effects.HasCurrentWindSnapshot ? effects.CurrentWindSnapshot.InstantWind : Vector2.Zero;
    }

    /// <summary>Adds an unshaded-free grey wall (a box) of the given height / length / thickness standing on the ground <paramref name="aroundOffset"/> metres from the camera, across the wind.</summary>
    public bool AddWall(Node world, double aroundOffset, double height, double length, double thickness)
    {
        try
        {
            RingCanonicalPosition p = Explorer(world).CanonicalPosition;
            var grid = world.GetNode<VoxelChunkGridManager>("ChunkGridManager");
            double ground = GroundHeight(world, aroundOffset, 0.0);
            Vector3 centre = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(p.AroundMeters + aroundOffset, ground + height * 0.5, p.AcrossMeters));
            Vector3 ahead = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(p.AroundMeters + aroundOffset + 10.0, ground + height * 0.5, p.AcrossMeters)) - centre;
            Vector3 side = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(p.AroundMeters + aroundOffset, ground + height * 0.5, p.AcrossMeters + 10.0)) - centre;
            Vector3 up = grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(p.AroundMeters + aroundOffset, ground + height * 0.5 + 10.0, p.AcrossMeters)) - centre;
            var basis = new Basis(ahead.Normalized(), up.Normalized(), side.Normalized());
            var mesh = new MeshInstance3D
            {
                Name = "Wo11TestWall", Mesh = new BoxMesh { Size = new Vector3((float)thickness, (float)height, (float)length) },
                MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.55f, 0.55f, 0.52f), Roughness = 0.9f },
                GlobalTransform = new Transform3D(basis, centre), TopLevel = true
            };
            world.AddChild(mesh);
            mesh.GlobalTransform = new Transform3D(basis, centre);
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    public void RemoveWall(Node world) => world.GetNodeOrNull("Wo11TestWall")?.QueueFree();
    public void SetReducedFlashes(bool reduced) => RingworldTechDemo.Lightning.LightningVisualPolicy.Apply(1.0f, 1.0f, reduced);
}
