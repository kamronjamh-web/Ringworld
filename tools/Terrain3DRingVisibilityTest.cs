using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Tools;

/// <summary>GPU-only culling regression harness for the curved Terrain3D surface window.</summary>
public partial class Terrain3DRingVisibilityTest : Node3D
{
    private RingTerrain3DSurface? _surface;
    private Camera3D? _camera;
    private RingSurfaceHeightField? _field;
    private RingSurfaceFrame _frame;

    public bool IsSurfaceReady => _surface?.IsReady == true;
    public string LastError => _surface?.LastError ?? "Surface was not initialized.";

    public override void _Ready()
    {
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = new Color(1, 0, 1),
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = Colors.White,
            AmbientLightEnergy = .7f
        };
        AddChild(new WorldEnvironment { Environment = environment });
        AddChild(new DirectionalLight3D { RotationDegrees = new Vector3(-55, 30, 0), LightEnergy = 1.3f });
        _camera = new Camera3D { Current = true, Near = .05f, Far = 2000 };
        AddChild(_camera);
        _field = new RingSurfaceHeightField(0x5EEDu, CertifiedRingScale.AroundVoxelLength, 4096, CertifiedRingScale.TerrainFloorMeters, CertifiedRingScale.TerrainCeilingMeters, (_, _) => 20);
        _frame = new RingSurfaceFrame(0, 2048, 20, CertifiedRingScale.AroundVoxelLength, CertifiedRingScale.EffectiveRadiusMeters, acrossDirection: -1);
        _surface = new RingTerrain3DSurface { Name = "RingVisibilitySurface" };
        AddChild(_surface);
        _surface.Initialize(_field, _frame);
        ConfigureAngle(0);
    }

    public void ConfigureAngle(int quarter)
    {
        if (_surface == null || _camera == null) return;
        int normalized = ((quarter % 4) + 4) % 4;
        double phi = normalized * Math.Tau * .25;
        Vector3 tangent = new((float)-Math.Sin(phi), 0, (float)Math.Cos(phi));
        Vector3 inward = new((float)-Math.Cos(phi), 0, (float)-Math.Sin(phi));
        Transform3D worldFrame = new(new Basis(tangent, inward, Vector3.Down), Vector3.Zero);
        _surface.SetWorldFrame(worldFrame);
        Vector3 cameraWorld = worldFrame * new Vector3(0, 10, 0);
        _camera.GlobalPosition = cameraWorld;
        _camera.LookAt(worldFrame * new Vector3(20, 0, 0), worldFrame.Basis.Y);
        _surface.UpdateCamera(new Vector3(0, 10, 0));
    }
}
