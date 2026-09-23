using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.UI;

/// <summary>Repeatable camera and edit choreography for engine-level vertical video capture.</summary>
public partial class RingShortsCaptureDirector : Node
{
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath CameraPath { get; set; } = new("../Explorer/Head/Camera3D");
    [Export] public NodePath CloudLayerPath { get; set; } = new("../RingVolumetricClouds");

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private Camera3D? _camera;
    private RingVolumetricCloudLayer? _clouds;
    private string _topic = string.Empty;
    private double _elapsed;
    private double _prerollSeconds;
    private double _baseAround;
    private double _baseAcross;
    private int _baseSurface;
    private bool _initialized;
    private bool _certifiedScale;
    private readonly List<(int Across, int Vertical)> _wallCells = new();
    private int _placedWallCells;
    private bool _hasCloudTarget;
    private RingCanonicalPosition _cloudTarget;
    private Vector2 _cloudTargetRadii;

    public override void _Ready()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--shorts-topic=", StringComparison.Ordinal))
                _topic = argument["--shorts-topic=".Length..].Trim().ToLowerInvariant();
            else if (argument.StartsWith("--shorts-preroll=", StringComparison.Ordinal)
                && double.TryParse(argument["--shorts-preroll=".Length..], out double preroll))
                _prerollSeconds = Math.Clamp(preroll, 0.0, 30.0);
        }
        if (string.IsNullOrEmpty(_topic))
        {
            ProcessMode = ProcessModeEnum.Disabled;
            return;
        }

        ProcessPriority = 100;
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _camera = GetNodeOrNull<Camera3D>(CameraPath);
        _clouds = GetNodeOrNull<RingVolumetricCloudLayer>(CloudLayerPath);
        if (_camera != null) _camera.Fov = _topic == "scale" ? 100.0f : 92.0f;
        Input.MouseMode = Input.MouseModeEnum.Hidden;
        GD.Print($"Vertical short capture armed: topic={_topic}, preroll={_prerollSeconds:0.0}s, intro={RingPresentationHud.CaptureIntroSeconds:0.0}s.");
    }

    public override void _Process(double delta)
    {
        if (!_initialized && !InitializeRoute()) return;
        _elapsed += delta;
        double shotTime = Math.Max(0.0, _elapsed - _prerollSeconds - RingPresentationHud.CaptureIntroSeconds);
        switch (_topic)
        {
            case "clouds":
                ServiceCloudRoute(shotTime);
                break;
            case "voxels":
                ServiceVoxelRoute(shotTime);
                break;
            default:
                ServiceScaleRoute(shotTime);
                break;
        }
    }

    private bool InitializeRoute()
    {
        if (_explorer == null || _grid?.TerrainSampler == null) return false;
        RingCanonicalPosition current = _explorer.CanonicalPosition;
        _baseAcross = Mathf.Clamp(_grid.RingAcrossWidthMeters * 0.5f, 500.0f, Mathf.Max(500.0f, _grid.RingAcrossWidthMeters - 500.0f));
        _certifiedScale = _grid.GetCanonicalRingCircumferenceMeters64() / Math.Tau >= 4_000_000.0;
        FindScenicLandWaypoint(current.AroundMeters);
        BuildWallCellOrder();
        _initialized = true;

        switch (_topic)
        {
            case "clouds":
                SetPose(_baseAround, _baseSurface + 2150.0, _baseAcross, -30.0f, -2.0f);
                break;
            case "voxels":
                SetPose(_baseAround - 16.0, _baseSurface + 5.0, _baseAcross, 0.0f, -7.0f);
                break;
            default:
                SetPose(_baseAround, _baseSurface + 900.0, _baseAcross, -8.0f, -16.0f);
                break;
        }
        return true;
    }

    private void ServiceScaleRoute(double time)
    {
        double progress = SmoothStep(Math.Clamp(time / 8.0, 0.0, 1.0));
        double aroundTravel = _certifiedScale ? 45000.0 : 4200.0;
        double acrossTravel = _certifiedScale ? 12000.0 : 700.0;
        double around = _baseAround + progress * aroundTravel;
        double across = _baseAcross + Math.Sin(progress * Math.PI) * acrossTravel;
        int surface = ResolveSurface(around, across);
        double altitude = _certifiedScale
            ? Mathf.Lerp(600.0f, 950000.0f, Mathf.Pow((float)progress, 1.35f))
            : Mathf.Lerp(900.0f, 26000.0f, (float)progress);
        float yaw = _certifiedScale
            ? Mathf.Lerp(-5.0f, 12.0f, (float)progress)
            : Mathf.Lerp(-8.0f, 18.0f, (float)progress);
        float pitch = _certifiedScale
            ? Mathf.Lerp(-9.0f, -28.0f, (float)progress)
            : Mathf.Lerp(-16.0f, -34.0f, (float)progress);
        SetPose(around, surface + altitude, across, yaw, pitch);
    }

    private void ServiceCloudRoute(double time)
    {
        if (!_hasCloudTarget
            && _clouds?.TryGetPresentationUnionWaypoint(out _cloudTarget, out _cloudTargetRadii) == true)
        {
            _hasCloudTarget = true;
            GD.Print($"Cloud short locked to seamless union: arc={_cloudTarget.AroundMeters / 1000.0:0.0}km, across={_cloudTarget.AcrossMeters / 1000.0:0.0}km, altitude={_cloudTarget.VerticalMeters:0}m, radii={_cloudTargetRadii.X:0}x{_cloudTargetRadii.Y:0}m.");
        }

        if (_hasCloudTarget)
        {
            double targetProgress = Math.Clamp(time / 8.0, 0.0, 1.0);
            double standoff = Math.Max(1000.0, _cloudTargetRadii.X * 2.1);
            double cameraAround = _cloudTarget.AroundMeters - standoff + targetProgress * standoff * 0.34;
            double acrossOrbit = Math.Sin(targetProgress * Math.PI) * Math.Max(180.0, _cloudTargetRadii.Y * 0.32);
            double cameraAcross = _cloudTarget.AcrossMeters + acrossOrbit;
            double cameraVertical = _cloudTarget.VerticalMeters - _cloudTargetRadii.Y * 0.08;
            double deltaAround = _cloudTarget.AroundMeters - cameraAround;
            double deltaAcross = _cloudTarget.AcrossMeters - cameraAcross;
            double horizontalDistance = Math.Sqrt(deltaAround * deltaAround + deltaAcross * deltaAcross);
            float targetYaw = Mathf.RadToDeg((float)Math.Atan2(deltaAcross, deltaAround));
            float targetPitch = Mathf.RadToDeg((float)Math.Atan2(
                _cloudTarget.VerticalMeters - cameraVertical,
                Math.Max(1.0, horizontalDistance)));
            SetPose(cameraAround, cameraVertical, cameraAcross, targetYaw, targetPitch);
            return;
        }

        double progress = Math.Clamp(time / 8.0, 0.0, 1.0);
        double around = _baseAround + progress * 1050.0;
        double across = _baseAcross + Math.Sin(progress * Math.Tau) * 180.0;
        int surface = ResolveSurface(around, across);
        double altitude = 2150.0 + Math.Sin(progress * Math.PI) * 180.0;
        float yaw = Mathf.Lerp(-30.0f, 38.0f, (float)progress);
        float pitch = Mathf.Lerp(-2.0f, 7.0f, (float)Math.Sin(progress * Math.PI));
        SetPose(around, surface + altitude, across, yaw, pitch);
    }

    private void ServiceVoxelRoute(double time)
    {
        SetPose(_baseAround - 16.0, _baseSurface + 5.0, _baseAcross, 0.0f, -7.0f);
        if (time < 0.35 || _grid == null) return;
        int targetCount = Math.Min(_wallCells.Count, Mathf.FloorToInt((float)((time - 0.35) / 0.12)) + 1);
        while (_placedWallCells < targetCount)
        {
            (int acrossOffset, int verticalOffset) = _wallCells[_placedWallCells++];
            var target = new RingCanonicalPosition(
                _baseAround + 8.5,
                _baseSurface + verticalOffset + 0.5,
                _baseAcross + acrossOffset + 0.5);
            Vector3 world = _grid.CanonicalRingPositionToWorld(target);
            _grid.TrySetBlockByWorldPosition(world, BlockType.Concrete, out _, out _, requireTargetAir: true);
        }
    }

    private void BuildWallCellOrder()
    {
        _wallCells.Clear();
        for (int vertical = 1; vertical <= 7; vertical++)
        {
            int halfWidth = Math.Max(1, 5 - vertical / 2);
            for (int across = -halfWidth; across <= halfWidth; across++)
            {
                bool doorway = vertical <= 3 && Math.Abs(across) <= 1;
                if (!doorway) _wallCells.Add((across, vertical));
            }
        }
        // The lintel makes the doorway read before the tapered upper wall fills in.
        _wallCells.Insert(0, (-1, 4));
        _wallCells.Insert(1, (0, 4));
        _wallCells.Insert(2, (1, 4));
    }

    private int ResolveSurface(double around, double across)
    {
        if (_grid?.TerrainSampler == null) return _baseSurface;
        int sampleAcross = Mathf.Clamp(Mathf.RoundToInt((float)across), 0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        return _grid.TerrainSampler.GetSurfaceHeight((int)Math.Round(around), sampleAcross);
    }

    private void FindScenicLandWaypoint(double fallbackAround)
    {
        if (_grid?.TerrainSampler == null)
        {
            _baseAround = fallbackAround;
            _baseSurface = 0;
            return;
        }

        const int sampleCount = 256;
        double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
        int across = Mathf.RoundToInt((float)_baseAcross);
        int highest = int.MinValue;
        int highestAround = (int)Math.Round(fallbackAround);
        for (int index = 0; index < sampleCount; index++)
        {
            int around = (int)Math.Round((index + 0.5) * circumference / sampleCount);
            TerrainSample sample = _grid.TerrainSampler.GetTerrainSample(around, across);
            if (sample.HasSurfaceWater || sample.SurfaceHeight <= highest) continue;
            highest = sample.SurfaceHeight;
            highestAround = around;
        }
        _baseAround = highestAround;
        _baseSurface = highest == int.MinValue
            ? ResolveSurface(_baseAround, _baseAcross)
            : highest;
        GD.Print($"Vertical short scenic waypoint: arc={_baseAround / 1000.0:0.0}km, across={_baseAcross / 1000.0:0.0}km, surface={_baseSurface}m.");
    }

    private void SetPose(double around, double vertical, double across, float yawDegrees, float pitchDegrees)
    {
        if (_explorer == null) return;
        _explorer.SetBenchmarkCanonicalPosition(new RingCanonicalPosition(around, vertical, across));
        _explorer.SetPresentationView(yawDegrees, pitchDegrees);
    }

    private static double SmoothStep(double value) => value * value * (3.0 - 2.0 * value);
}
