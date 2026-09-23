using System;
using System.IO;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Integration;

/// <summary>
/// GPU-only evidence capture for the sealed structural cavity. This intentionally
/// fails in headless/no-frame environments instead of treating black dummy buffers
/// as proof of darkness.
/// </summary>
public partial class RingCavityLightingCaptureDirector : Node
{
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath SmoothTerrainPath { get; set; } = new("../SmoothTerrainGrid");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath UndergroundEnvironmentPath { get; set; } = new("../UndergroundEnvironment");

    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private SmoothTerrainGrid? _smooth;
    private RingTerrainGlobalLod? _globalLod;
    private RingUndergroundEnvironmentController? _underground;
    private Camera3D? _camera;
    private SubViewport? _captureViewport;
    private Camera3D? _captureCamera;
    private MeshInstance3D? _lightProbe;
    private OmniLight3D? _testLight;
    private Transform3D _captureTransform;
    private int _settledFrames;
    private int _frames;
    private bool _active;
    private bool _baselineSaved;
    private ImageMetrics _baseline;

    private const int RequiredSettledFrames = 12;
    private const int TimeoutFrames = 1800;
    // These are deliberately conservative sealed-cavity bounds. Adjust only
    // against retained GPU evidence, never by accepting an all-black buffer.
    private const double MaxSealedCavityMeanLuminance = 0.08;
    private const double MaxSealedCavityEdgeLuminance = 0.06;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _active = Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == "--capture-cavity-lighting");
        if (!_active) { SetProcess(false); return; }
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _smooth = GetNodeOrNull<SmoothTerrainGrid>(SmoothTerrainPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _underground = GetNodeOrNull<RingUndergroundEnvironmentController>(UndergroundEnvironmentPath);
        _camera = GetNodeOrNull<Camera3D>("../Explorer/Head/Camera3D");
        if (_explorer == null || _grid == null || _smooth == null || _camera == null || _underground == null)
        {
            Fail("required Explorer/Grid/SmoothTerrain/UndergroundEnvironment/Camera node is unavailable");
            return;
        }
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
        {
            Fail("headless display server cannot produce GPU evidence (not a passing black frame)");
            return;
        }

        RingVerticalDomainConfiguration domain = _grid.GetVerticalDomainConfiguration();
        double cavityY = (domain.TerrainBottomGlobalY + domain.LowerHullTopGlobalY) * 0.5;
        double across = Math.Min(96.0, Math.Max(32.0, _grid.RingAcrossWidthMeters * 0.02));
        var position = new RingCanonicalPosition(_explorer.CanonicalPosition.AroundMeters, cavityY, across);
        _explorer.SetBenchmarkCanonicalPosition(position);
        Vector3 roof = _grid.CanonicalRingPositionToWorld(position with { VerticalMeters = domain.TerrainBottomGlobalY - 1.0, AcrossMeters = across + 24.0 });
        _camera.LookAt(roof, Vector3.Up);
        // Freeze the evidence pose before the window can receive incidental
        // keyboard or mouse input. Build the rig deferred because this node's
        // parent is still attaching the instantiated world during _Ready().
        _captureTransform = _camera.GlobalTransform;
        Callable.From(CreateOffscreenCaptureRig).CallDeferred();
        GD.Print($"CAVITY_LIGHTING_CAPTURE=START canonical=({position.AroundMeters:0.0},{cavityY:0.0},{across:0.0}) roof={domain.TerrainBottomGlobalY} lowerHullTop={domain.LowerHullTopGlobalY}");
    }

    public override void _Process(double delta)
    {
        if (!_active) return;
        if (++_frames > TimeoutFrames) { Fail("timed out waiting for real terrain/LOD readiness"); return; }
        if (_captureViewport == null || _captureCamera == null || _lightProbe == null) return;
        if (!WorldReady()) { _settledFrames = 0; return; }
        if (Engine.GetFramesDrawn() <= 0) { Fail("renderer produced no drawn frames; refusing dummy capture"); return; }
        if (++_settledFrames < RequiredSettledFrames) return;

        SyncCaptureCamera();

        if (!_baselineSaved)
        {
            if (!TrySaveCapture("intact_cavity_roof_baseline.png", out _baseline)) return;
            if (_baseline.Mean <= 0.002 || _baseline.NonBlackFraction <= 0.001)
            {
                Fail("baseline is all-black or has no rendered geometry; refusing fake pass");
                return;
            }
            _testLight = new OmniLight3D { Name = "CavityCaptureTestLight", LightColor = new Color(1.0f, 0.62f, 0.30f), LightEnergy = 14.0f, OmniRange = 24.0f, ShadowEnabled = true };
            GetParent().AddChild(_testLight);
            Vector3 forward = -_captureTransform.Basis.Z;
            _testLight.GlobalPosition = _captureTransform.Origin + forward * 9.0f + Vector3.Up * 1.0f;
            _baselineSaved = true;
            _settledFrames = 0;
            return;
        }

        if (!TrySaveCapture("localized_test_light_readability.png", out ImageMetrics lit)) return;
        bool localReadable = lit.Mean > _baseline.Mean + 0.006 && lit.CenterMean > _baseline.CenterMean + 0.012;
        bool noFlood = lit.EdgeMean <= _baseline.EdgeMean + 0.045 && lit.EdgeMean < lit.CenterMean * 0.82;
        bool noLeak = _baseline.Mean <= MaxSealedCavityMeanLuminance
            && _baseline.EdgeMean <= MaxSealedCavityEdgeLuminance;
        bool pass = localReadable && noFlood && noLeak && lit.NonBlackFraction > 0.01;
        WriteManifest(pass, lit, localReadable, noFlood, noLeak);
        if (pass)
        {
            GD.Print("CAVITY_LIGHTING_CAPTURE=PASS baseline/intact roof dark, localized test light readable without global flood");
            GetTree().Quit(0);
        }
        else Fail($"metrics rejected: readable={localReadable}, noFlood={noFlood}, noLeak={noLeak}, baseline={_baseline.Mean:0.000}, lit={lit.Mean:0.000}", writeManifest: false);
    }

    private bool WorldReady()
    {
        if (_explorer == null || _grid == null || _smooth == null) return false;
        RingCanonicalPosition p = _explorer.CanonicalPosition;
        // Match the evidence footprint to the scene's two-chunk smooth
        // residency radius. Requiring a wider region would wait forever for
        // chunks that the production streamer deliberately does not own.
        int acrossCenter = (int)Math.Floor(p.AcrossMeters);
        bool smooth = _smooth.IsSurfaceRegionVisualReady(
                (long)Math.Floor(p.AroundMeters) - 32,
                (long)Math.Floor(p.AroundMeters) + 32,
                Math.Max(0, acrossCenter - 32),
                Math.Min((int)_grid.RingAcrossWidthMeters, acrossCenter + 32))
            && _smooth.IsQuiescent;
        bool global = _globalLod == null || (!_globalLod.GlobalSummaryBuildPending && !_globalLod.InitialGlobalMeshBuildPending && _globalLod.ActiveSegmentCount > 0);
        bool sealedCavity = _underground != null && _underground.IsUnderground && _underground.CurrentSkyAccess <= 0.001f;
        return smooth && global && sealedCavity;
    }

    private void CreateOffscreenCaptureRig()
    {
        // A SubViewport is intentionally used instead of the root viewport:
        // HUD/CanvasLayer pixels cannot influence the evidence or its metrics.
        _captureViewport = new SubViewport
        {
            Name = "CavityLightingCaptureViewport",
            Size = new Vector2I(960, 540),
            TransparentBg = false,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always,
            HandleInputLocally = false,
            OwnWorld3D = false
        };
        GetParent().AddChild(_captureViewport);
        _captureCamera = new Camera3D { Name = "CavityLightingCaptureCamera", Current = true, Near = _camera!.Near, Far = _camera.Far, Fov = _camera.Fov };
        _captureViewport.AddChild(_captureCamera);

        // The unlit board is visible in both captures. Its fixed camera-space
        // placement makes the second image measure a real local light receiver,
        // not a sky/UI change or a distant cavity surface.
        var material = new StandardMaterial3D { AlbedoColor = new Color(0.42f, 0.42f, 0.42f), Metallic = 0.0f, Roughness = 1.0f, ShadingMode = BaseMaterial3D.ShadingModeEnum.PerPixel };
        _lightProbe = new MeshInstance3D { Name = "CavityLightingMatteProbe", Mesh = new BoxMesh { Size = new Vector3(14.0f, 9.0f, 0.4f) }, MaterialOverride = material, CastShadow = GeometryInstance3D.ShadowCastingSetting.On };
        GetParent().AddChild(_lightProbe);
        Vector3 forward = -_captureTransform.Basis.Z;
        _lightProbe.GlobalPosition = _captureTransform.Origin + forward * 12.0f;
        _lightProbe.GlobalTransform = _lightProbe.GlobalTransform.LookingAt(_captureTransform.Origin, Vector3.Up);
    }

    private void SyncCaptureCamera()
    {
        if (_captureCamera == null || _camera == null) return;
        _captureCamera.GlobalTransform = _captureTransform;
        _captureCamera.Near = _camera.Near;
        _captureCamera.Far = _camera.Far;
        _captureCamera.Fov = _camera.Fov;
    }

    private bool TrySaveCapture(string filename, out ImageMetrics metrics)
    {
        metrics = default;
        if (_captureViewport == null) { Fail("offscreen 3D capture viewport was not created"); return false; }
        string directory = RingGeneratedOutputPaths.Directory("captures/cavity_lighting_capture");
        Image image = _captureViewport.GetTexture().GetImage();
        Error error = image.SavePng(Path.Combine(directory, filename));
        if (error != Error.Ok) { Fail($"could not save {filename}: {error}"); return false; }
        metrics = Measure(image);
        return true;
    }

    private static ImageMetrics Measure(Image image)
    {
        int width = image.GetWidth(), height = image.GetHeight();
        double total = 0, center = 0, edge = 0; int count = 0, centerCount = 0, edgeCount = 0, nonBlack = 0;
        for (int y = 0; y < height; y += 2)
        for (int x = 0; x < width; x += 2)
        {
            Color c = image.GetPixel(x, y); double l = c.R * 0.2126 + c.G * 0.7152 + c.B * 0.0722;
            total += l; count++; if (l > 0.01) nonBlack++;
            bool middle = x > width / 4 && x < width * 3 / 4 && y > height / 4 && y < height * 3 / 4;
            if (middle) { center += l; centerCount++; } else { edge += l; edgeCount++; }
        }
        return new ImageMetrics(total / Math.Max(1, count), center / Math.Max(1, centerCount), edge / Math.Max(1, edgeCount), (double)nonBlack / Math.Max(1, count));
    }

    private void WriteManifest(bool pass, ImageMetrics lit, bool localReadable, bool noFlood, bool noLeak)
    {
        string directory = RingGeneratedOutputPaths.Directory("captures/cavity_lighting_capture");
        var manifest = new { status = pass ? "PASS" : "FAIL", renderer = RenderingServer.GetCurrentRenderingMethod(), frames_drawn = Engine.GetFramesDrawn(), baseline = _baseline, localized_test_light = lit, local_readable = localReadable, no_global_flood = noFlood, no_obvious_light_leak = noLeak };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void Fail(string reason, bool writeManifest = true)
    {
        if (writeManifest) WriteManifest(false, default, false, false, false);
        GD.PushError($"CAVITY_LIGHTING_CAPTURE=FAIL: {reason}");
        _active = false; GetTree().Quit(1);
    }

    private readonly record struct ImageMetrics(double Mean, double CenterMean, double EdgeMean, double NonBlackFraction);
}
