using Godot;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingLodDebugHud : CanvasLayer
{
    [Export] public NodePath CoordinatorPath { get; set; } = new("../TerrainLodCoordinator");
    [Export] public NodePath GridManagerPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath GlobalLodPath { get; set; } = new("../GlobalTerrainLod3");
    [Export] public NodePath CertificationMonitorPath { get; set; } = new("../CertificationMonitor");
    [Export] public NodePath VolumetricCloudRendererPath { get; set; } = new("../LocalVolumetricCloudRenderer");
    [Export] public NodePath WeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath ScaledFarViewportPath { get; set; } = new("../ScaledFarViewport");
    [Export] public NodePath PerformanceMonitorPath { get; set; } = new("../RingPerformanceMonitor");
    [Export(PropertyHint.Range, "0.05,2.0,0.05")] public float RefreshSeconds { get; set; } = 0.2f;
    [Export] public bool StartVisible { get; set; } = false;

    private RingTerrainLodCoordinator? _coordinator;
    private VoxelChunkGridManager? _grid;
    private RingTerrainGlobalLod? _globalLod;
    private RingCertificationMonitor? _certificationMonitor;
    private global::RingworldTechDemo.RingLocalVolumetricCloudRenderer? _volumetricClouds;
    private global::RingworldTechDemo.RingLocalWeatherEffects? _weatherEffects;
    private RingScaledFarViewport? _scaledFarViewport;
    private RingPerformanceMonitor? _performanceMonitor;
    private Label? _label;
    private double _elapsed;
    private bool _verbose;

    public override void _Ready()
    {
        _coordinator = GetNodeOrNull<RingTerrainLodCoordinator>(CoordinatorPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        _globalLod = GetNodeOrNull<RingTerrainGlobalLod>(GlobalLodPath);
        _certificationMonitor = GetNodeOrNull<RingCertificationMonitor>(CertificationMonitorPath);
        _volumetricClouds = GetNodeOrNull<global::RingworldTechDemo.RingLocalVolumetricCloudRenderer>(VolumetricCloudRendererPath);
        _weatherEffects = GetNodeOrNull<global::RingworldTechDemo.RingLocalWeatherEffects>(WeatherEffectsPath);
        _scaledFarViewport = GetNodeOrNull<RingScaledFarViewport>(ScaledFarViewportPath);
        _performanceMonitor = GetNodeOrNull<RingPerformanceMonitor>(PerformanceMonitorPath);
        _label = new Label
        {
            OffsetLeft = 16.0f,
            OffsetTop = 128.0f,
            OffsetRight = 820.0f,
            OffsetBottom = 700.0f
        };
        _label.AddThemeColorOverride("font_color", new Color(0.86f, 0.95f, 1.0f));
        _label.AddThemeColorOverride("font_shadow_color", new Color(0, 0, 0, 0.9f));
        _label.AddThemeConstantOverride("shadow_offset_x", 2);
        _label.AddThemeConstantOverride("shadow_offset_y", 2);
        _label.AddThemeFontSizeOverride("font_size", 16);
        AddChild(_label);
        Visible = StartVisible;
        Refresh();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo) return;
        if (key.Keycode == Key.F6 && key.ShiftPressed)
        {
            // A/B switch for the bridge tiers between proxy levels and in the scaled far view. Debug only:
            // not persisted, so every session starts with the tiers on.
            bool enabled = !(_coordinator?.EnableIntermediateLodTiers ?? true);
            _coordinator?.SetIntermediateLodTiersEnabled(enabled);
            _scaledFarViewport?.SetIntermediateLodTiers(enabled);
            Refresh();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.F6)
        {
            Visible = !Visible;
            if (Visible) Refresh();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.F7)
        {
            _verbose = !_verbose;
            Refresh();
            GetViewport().SetInputAsHandled();
        }
    }

    public override void _Process(double delta)
    {
        if (!Visible) return;
        _elapsed += delta;
        if (_elapsed < Mathf.Max(0.05f, _performanceMonitor?.RefreshSeconds ?? RefreshSeconds)) return;
        _elapsed = 0.0;
        Refresh();
    }

    private void Refresh()
    {
        if (_label == null) return;
        string header = $"LOD STREAMING — F6 toggle | F7 details | Shift+F6 intermediate tiers\nFPS: {Engine.GetFramesPerSecond()}";
        string grid = FormatGrid(_grid, _verbose);
        var text = new StringBuilder("PERFORMANCE DIAGNOSTICS - F6 toggle | F7 streaming details | Shift+F6 intermediate LOD tiers");
        if (_coordinator is { EnableIntermediateLodTiers: false })
            text.Append("\nINTERMEDIATE LOD TIERS OFF (debug A/B, Shift+F6)");
        AppendPerformanceSummary(text, _performanceMonitor, _performanceMonitor?.GetLiveSnapshot());
        if (!_verbose)
        {
            _label.Text = text.ToString();
            return;
        }
        text.Append("\n\nSTREAMING\n").Append(grid);
        if (_coordinator == null)
        {
            text.Append("\nLOD stack: unavailable");
        }
        else
        {
            text.Append($"\n{_coordinator.CurrentPresetName} preset | uploads {_coordinator.LastFrameUploadCount}/{_coordinator.TotalSectorUploadsPerFrame} this frame in {_coordinator.LastFrameUploadMilliseconds:0.00}/{_coordinator.MaxUploadMillisecondsPerFrame:0.00}ms | queued {_coordinator.PendingUploadCount} | workers {_coordinator.ActiveWorkerCount}/{_coordinator.MaxConcurrentWorkerBuilds}{(_coordinator.MeshMemoryPressureActive ? " (memory limit)" : string.Empty)}");
            text.Append($"\nProxy memory: {FormatBytes(_coordinator.EstimatedActiveMeshBytes)} active + {FormatBytes(_coordinator.EstimatedPendingMeshBytes)} staged");
            foreach (RingTerrainProxySector level in _coordinator.Levels)
                text.Append('\n').Append(FormatLod(level.LodLabel, level, _verbose));
        }
        if (_globalLod != null)
            text.Append($"\nGlobal HLOD: {_globalLod.ActiveSegmentCount} segments | dirty queue {_globalLod.PendingSegmentRebuildCount} | min/max {_globalLod.MinMaxLevelCount} levels/{_globalLod.MinMaxCellCount} cells | memory {FormatBytes(_globalLod.EstimatedSummaryBytes + _globalLod.EstimatedGlobalMeshBytes)} | last edit {_globalLod.LastDirtyMinMaxCellCount} base cells, {_globalLod.LastSegmentRebuildCount} segments in {_globalLod.LastSegmentRebuildMilliseconds:0.00}ms");
        if (_volumetricClouds?.Enabled == true)
        {
            RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState weather = _volumetricClouds.CurrentWeather;
            text.Append(
                $"\nWeather: {weather.SystemKind}/{weather.DominantFormationProfile} | volume {(_volumetricClouds.LocalVolumeVisible ? "local" : "far shell")}" 
                + $" | coverage {weather.CloudCoverage:0.00} | precipitation {weather.PrecipitationPotential:0.00}"
                + $" | base/top {weather.CloudBaseAltitudeMeters:0}/{weather.CloudTopAltitudeMeters:0}m datum"
                + $" | wind {weather.WindMetersPerSecond.Length():0.0}m/s | viewer {_volumetricClouds.ViewerAltitudeAboveTerrainMeters:0}m AGL");
        }
        if (_weatherEffects?.Enabled == true)
            text.Append($"\nWeather FX: intensity {_weatherEffects.PrecipitationIntensity:0.00} | {(_weatherEffects.Snowing ? "snow" : "rain")} | wetness {_weatherEffects.SurfaceWetness:0.00} | lightning {_weatherEffects.LightningFlashCount}");
        if (_scaledFarViewport?.Enabled == true)
            text.Append(_scaledFarViewport.CloudTransmittanceMaskEnabled
                ? $"\nFar backdrop clouds: {(_scaledFarViewport.ExactSunshineBackdropActive ? "exact Sunshine" : _scaledFarViewport.CloudMaskActive ? "proxy mask" : "idle")} | {_scaledFarViewport.CloudMaskSize.X}x{_scaledFarViewport.CloudMaskSize.Y} | proxy updates {_scaledFarViewport.CloudMaskUpdateCount}"
                : "\nCloud renderer: merged meshes + transition meshes + curved far shell");
        if (_certificationMonitor != null)
            text.Append($"\nCertification: avg {_certificationMonitor.RollingAverageFps:0.0} FPS | 1% low {_certificationMonitor.RollingOnePercentLowFps:0.0} | worst {_certificationMonitor.WorstFrameMilliseconds:0.0}ms | render {_certificationMonitor.CurrentRenderScale * 100.0f:0}% | working set {FormatBytes(_certificationMonitor.CurrentWorkingSetBytes)} / {_certificationMonitor.WorkingSetLimitMb} MiB (peak {FormatBytes(_certificationMonitor.PeakWorkingSetBytes)}) | terrain est {FormatBytes(_certificationMonitor.EstimatedTerrainBytes)}");
        _label.Text = text.ToString();
    }

    public void RefreshNow() => Refresh();

    private static void AppendPerformanceSummary(StringBuilder text, RingPerformanceMonitor? monitor, RingPerformanceSnapshot? snapshot)
    {
        if (snapshot == null) { text.Append("\nMonitor: unavailable"); return; }
        RingPerformanceFrame[] frames = snapshot.History.TakeLast(300).ToArray();
        if (frames.Length == 0) { text.Append("\nCollecting frame history..."); return; }
        double[] ordered = frames.Select(frame => frame.FrameMilliseconds).OrderBy(value => value).ToArray();
        int p95Index = Math.Clamp((int)Math.Ceiling(ordered.Length * .95) - 1, 0, ordered.Length - 1);
        double window = frames[^1].Seconds - frames[0].Seconds;
        text.Append($"\n{(monitor?.Enabled == true ? "Collecting" : "Recording paused; history frozen")} | FPS {Engine.GetFramesPerSecond():0} | frame avg {frames.Average(frame => frame.FrameMilliseconds):0.00} ms | p95 {ordered[p95Index]:0.00} ms | worst {ordered[^1]:0.00} ms (last {window:0.0}s)");
        text.Append($"\nCPU render / GPU render interval: {(frames[^1].ViewportCpuMilliseconds?.ToString("0.00") ?? "n/a")} / {(frames[^1].ViewportGpuMilliseconds?.ToString("0.00") ?? "n/a")} ms | GPU: {RingPerformanceGpu.Status}");
        AppendTopRows(text, "Main CPU worst call", snapshot.Rows.Where(row => !row.Worker && !row.Gpu));
        AppendTopRows(text, "Worker longest job", snapshot.Rows.Where(row => row.Worker));
        AppendTopRows(text, "GPU worst scoped frame", snapshot.Rows.Where(row => row.Gpu));
        text.Append("\nScope rows: recent 5s, sample-capped; full history in slowdown export.");
        text.Append("\nWorker durations and GPU intervals can overlap; do not sum them as frame time.");
    }

    private static void AppendTopRows(StringBuilder text, string group, IEnumerable<RingPerformanceRow> rows)
    {
        RingPerformanceRow[] top = rows.OrderByDescending(row => row.MaxMilliseconds).Take(2).ToArray();
        if (top.Length == 0) return;
        text.Append($"\n{group}: ").Append(string.Join("; ", top.Select(row => $"{row.Name} {row.MaxMilliseconds:0.00}ms")));
    }


    private static string FormatGrid(VoxelChunkGridManager? grid, bool verbose)
    {
        if (grid == null) return "Voxels: unavailable";
        string backend = VoxelChunkMesherNative.IsAvailable ? "native" : "managed";
        string summary = $"Voxels: loaded {grid.LoadedChunkCount} | pooled {grid.PooledChunkCount} | queued {grid.PendingChunkLoadCount}, deferred {grid.DeferredVoxelMeshCount} | mesh {backend} workers {grid.ActiveVoxelMeshWorkerCount}/{grid.MaxConcurrentVoxelMeshWorkers}{(grid.VoxelMeshMemoryPressureActive ? " (memory limit)" : string.Empty)}, ready {grid.CompletedVoxelMeshCount} | water {grid.DynamicFluidCellCount} cells/{grid.PendingFluidUpdateCount} pending | rebuild {grid.PendingChunkRebuildCount} | lead {grid.LastPredictionDistanceMeters:0}m";
        if (!verbose) return summary;
        return summary
            + $"\n        desired visual/collision {grid.DesiredVisualChunkCount}/{grid.DesiredCollisionChunkCount} | virtual air/solid {grid.KnownEmptyStreamingChunkCount}/{grid.KnownSolidStreamingChunkCount} | retained data {grid.RetainedVoxelDataChunkCount}/{grid.LoadedChunkCount} ~{FormatBytes(grid.EstimatedVoxelDataBytes)}"
            + $"\n        async prepare {grid.LastVoxelMeshPrepareMilliseconds:0.00}/{grid.MaxVoxelMeshPrepareMilliseconds:0.00}ms last/max (neighbors {grid.LastVoxelNeighborCaptureMilliseconds:0.00}/{grid.MaxVoxelNeighborCaptureMilliseconds:0.00}) | worker {grid.LastVoxelMeshWorkerMilliseconds:0.00}/{grid.MaxVoxelMeshWorkerMilliseconds:0.00}ms | upload {grid.LastVoxelMeshUploadMilliseconds:0.00}/{grid.MaxVoxelMeshUploadMilliseconds:0.00}ms";
    }

    private static string FormatLod(string name, RingTerrainProxySector? lod, bool verbose)
    {
        if (lod == null) return $"{name}: unavailable";
        string summary = $"{name}: active {lod.ActiveSectorCount} | pool {lod.PooledSectorCount} | upload queue {lod.PendingUploadCount} | lead {lod.LastPredictionDistanceMeters:0}m | inner clip {lod.InnerClipRadiusMeters:0}m\n" +
            $"      last +{lod.LastGeneratedSectorCount} upload {lod.LastStreamingBuildMilliseconds:0.00}ms | worker {lod.LastWorkerBuildMilliseconds:0.00}ms";
        if (!verbose) return summary;
        return summary + $" | disk cache {lod.LoadedSectorCacheCount} hit/{lod.SavedSectorCacheCount} saved\n      avg/max upload {lod.AverageUploadMilliseconds:0.00}/{lod.MaxStreamingBuildMilliseconds:0.00}ms | avg/max worker {lod.AverageWorkerBuildMilliseconds:0.00}/{lod.MaxWorkerBuildMilliseconds:0.00}ms\n" +
            $"      meshes ~{FormatBytes(lod.EstimatedActiveMeshBytes)} active + {FormatBytes(lod.EstimatedStagedMeshBytes)} staged | created/reused/rebuilt {lod.CreatedSectorNodeCount}/{lod.ReusedSectorNodeCount}/{lod.RebuiltSectorMeshCount}";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024.0 * 1024.0):0.00} MiB";
        if (bytes >= 1024L) return $"{bytes / 1024.0:0.0} KiB";
        return $"{bytes} B";
    }
}
