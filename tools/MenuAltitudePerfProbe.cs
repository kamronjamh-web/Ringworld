using System;
using System.Diagnostics;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Typed helper for tools/menu_altitude_perf_capture.gd (Phase C P0 measurement). It only reads state and flips the
/// documented menu-altitude cost levers at runtime; it never writes to disk.
/// </summary>
public partial class MenuAltitudePerfProbe : RefCounted
{
    private Node? _world;
    private RingVoxelExplorerController? _explorer;
    private RingSunshineCloudBridge? _clouds;
    private RingTerrainLodCoordinator? _lod;
    private RingTerrain3DGameplay? _surface;
    private RingStandingGrassLayer? _grass;
    private RingBiomeDecorationLayer? _trees;
    private int _origResolutionScale, _origRaymarchSteps;
    private bool _origShadowGeneration;
    private RingTerrainLodCoordinator.QualityPreset _origLodPreset;
    private int _origWorkers = 2, _appliedWorkers = 2;
    private RingPerformanceMonitor? _monitor;
    private RingDayNightCycle? _cycle;

    public bool Bind(Node world)
    {
        _world = world;
        _explorer = world.GetNodeOrNull<RingVoxelExplorerController>("Explorer");
        _clouds = world.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        _lod = world.GetNodeOrNull<RingTerrainLodCoordinator>("TerrainLodCoordinator");
        _surface = world.GetNodeOrNull<RingTerrain3DGameplay>("Terrain3DGameplay");
        _grass = world.GetNodeOrNull<RingStandingGrassLayer>("StandingGrass");
        _trees = world.GetNodeOrNull<RingBiomeDecorationLayer>("BiomeDecorations");
        if (_clouds != null)
        {
            _origResolutionScale = _clouds.CloudResolutionScale;
            _origRaymarchSteps = _clouds.RaymarchSteps;
            _origShadowGeneration = _clouds.GpuShadowGenerationEnabled;
        }
        _monitor = world.GetNodeOrNull<RingPerformanceMonitor>("RingPerformanceMonitor");
        _cycle = world.GetNodeOrNull<RingDayNightCycle>("RingDayNightCycle");
        if (_monitor != null) { _monitor.Enabled = true; _monitor.HistorySeconds = 60; _monitor.MemoryBudgetMiB = 16; }
        if (_lod != null) _origLodPreset = _lod.CurrentPreset;
        if (_surface != null)
        {
            var defaults = new GameplaySurfaceTerrainDefaults();
            defaults.Initialize();
            _appliedWorkers = _origWorkers = defaults.Settings.GenerationWorkerCount;
        }
        return _explorer != null && _clouds != null && _lod != null;
    }

    /// <summary>Variant (b): Sunshine half-res raymarch, 64 steps, GPU cloud-shadow generation off.</summary>
    public void ApplyLowCostClouds()
    {
        if (_clouds == null) return;
        _clouds.CloudResolutionScale = 1;
        _clouds.RaymarchSteps = 64;
        _clouds.GpuShadowGenerationEnabled = false;
        _clouds.ApplyTemporalQualityTuning(refreshHistory: true);
    }

    public void RestoreClouds()
    {
        if (_clouds == null) return;
        _clouds.CloudResolutionScale = _origResolutionScale;
        _clouds.RaymarchSteps = _origRaymarchSteps;
        _clouds.GpuShadowGenerationEnabled = _origShadowGeneration;
        _clouds.ApplyTemporalQualityTuning(refreshHistory: true);
    }

    public void ApplyLodLow() => _lod?.ApplyQualityPreset(RingTerrainLodCoordinator.QualityPreset.Low);
    public void RestoreLod() => _lod?.ApplyQualityPreset(_origLodPreset);

    public void SetTerrainWorkers(int workers)
    {
        if (_surface == null) return;
        var defaults = new GameplaySurfaceTerrainDefaults();
        defaults.Initialize();
        defaults.Settings.GenerationWorkerCount = GameplaySurfaceTerrainDefaults.ClampGenerationWorkerCount(workers);
        _surface.ApplySurfaceSettings(defaults.Settings);
        _appliedWorkers = defaults.Settings.GenerationWorkerCount;
    }

    public void RestoreTerrainWorkers() => SetTerrainWorkers(_origWorkers);

    /// <summary>Teleports the Explorer into scripted noclip at the given canonical pose (no terrain snap).</summary>
    public void PlaceNoclip(double around, double vertical, double across, float yawRadians, float speed)
    {
        _explorer?.RestoreSavedState(new RingCanonicalPosition(around, vertical, across), Vector3.Zero,
            yawRadians, 0f, true, 3, speed);
    }

    public Godot.Collections.Dictionary GetPose()
    {
        var result = new Godot.Collections.Dictionary();
        if (_explorer == null) return result;
        RingCanonicalPosition p = _explorer.CanonicalPosition;
        result["around"] = p.AroundMeters;
        result["vertical"] = p.VerticalMeters;
        result["across"] = p.AcrossMeters;
        result["altitude_cached"] = _explorer.CurrentAltitudeAboveTerrainMeters;
        result["altitude_computed"] = _explorer.GetCanonicalAtmosphereHeightMeters();
        result["noclip"] = _explorer.NoclipEnabled;
        result["noclip_speed"] = _explorer.NoclipCurrentSpeedMetersPerSecond;
        result["travel_speed"] = _explorer.ActualTravelSpeedMetersPerSecond;
        return result;
    }

    public Godot.Collections.Dictionary GetState()
    {
        var d = new Godot.Collections.Dictionary();
        if (_clouds != null)
        {
            d["cloud_resolution_scale"] = _clouds.CloudResolutionScale;
            d["cloud_raymarch_steps"] = _clouds.RaymarchSteps;
            d["cloud_lighting_steps"] = _clouds.LightingSteps;
            d["cloud_shadow_generation"] = _clouds.GpuShadowGenerationEnabled;
            d["cloud_shadow_cast"] = _clouds.CastCloudShadows;
            d["cloud_shadow_status"] = _clouds.GpuCloudShadowStatus;
            d["cloud_shadow_near_res"] = _clouds.GpuNearShadowMapResolution;
        }
        if (_lod != null) d["lod_preset"] = _lod.CurrentPresetName;
        var quality = _world?.GetNodeOrNull<RingGraphicsQualityController>("GraphicsQuality");
        if (quality != null) d["graphics_preset"] = quality.CurrentPresetName;
        if (_surface != null) d["terrain3d_workers"] = _appliedWorkers;
        if (_grass != null) d["grass_streaming_suspended"] = _grass.StreamingSuspended;
        if (_trees != null) d["trees_visible"] = _trees.Visible;
        return d;
    }


    public void ResetMonitor() => _monitor?.ClearHistory();

    /// <summary>Per-frame summary from the game's own RingPerformanceMonitor since the last ResetMonitor.</summary>
    public Godot.Collections.Dictionary GetMonitorSummary()
    {
        var result = new Godot.Collections.Dictionary();
        if (_monitor == null) return result;
        var frames = _monitor.GetSnapshot().History;
        result["frames"] = frames.Count;
        void Add(string name, Func<RingPerformanceFrame, double?> select)
        {
            var values = new List<double>();
            foreach (RingPerformanceFrame frame in frames) { double? v = select(frame); if (v.HasValue && double.IsFinite(v.Value)) values.Add(v.Value); }
            if (values.Count == 0) return;
            values.Sort();
            double sum = 0; foreach (double v in values) sum += v;
            result[name] = new Godot.Collections.Dictionary { ["mean"] = sum / values.Count, ["p95"] = values[Math.Min(values.Count - 1, (int)(values.Count * 0.95))], ["max"] = values[^1], ["n"] = values.Count };
        }
        Add("frame_ms", f => f.FrameMilliseconds);
        Add("process_ms", f => f.ProcessMilliseconds);
        Add("physics_ms", f => f.PhysicsMilliseconds);
        Add("setup_ms", f => f.SetupMilliseconds);
        Add("viewport_cpu_ms", f => f.ViewportCpuMilliseconds);
        Add("viewport_gpu_ms", f => f.ViewportGpuMilliseconds);
        Add("far_viewport_cpu_ms", f => f.FarViewportCpuMilliseconds);
        Add("far_viewport_gpu_ms", f => f.FarViewportGpuMilliseconds);
        Add("regional_far_cpu_ms", f => f.RegionalFarViewportCpuMilliseconds);
        Add("regional_far_gpu_ms", f => f.RegionalFarViewportGpuMilliseconds);
        Add("draw_calls", f => f.DrawCalls >= 0 ? f.DrawCalls : null);
        return result;
    }

    /// <summary>Scans the day cycle and leaves it at the fraction with the highest daylight at the Explorer. Returns that fraction.</summary>
    public float SetBrightestCycleFraction()
    {
        if (_cycle == null) return -1f;
        var fractions = new List<float>();
        float bestDaylight = -1f;
        for (int i = 0; i < 40; i++)
        {
            float fraction = i / 40f;
            _cycle.SetCycleFraction(fraction);
            float daylight = _cycle.DaylightFactor;
            if (daylight > bestDaylight + 0.002f) { bestDaylight = daylight; fractions.Clear(); }
            if (daylight >= bestDaylight - 0.002f) fractions.Add(fraction);
        }
        float best = fractions[fractions.Count / 2];
        _cycle.SetCycleFraction(best);
        return best;
    }

    public void SetCycleFraction(float fraction) => _cycle?.SetCycleFraction(fraction);
    public float GetDaylightFactor() => _cycle?.DaylightFactor ?? -1f;

    public Godot.Collections.Dictionary GetProcessMemory()
    {
        using Process process = Process.GetCurrentProcess();
        return new Godot.Collections.Dictionary
        {
            ["working_set_mb"] = process.WorkingSet64 / 1048576.0,
            ["private_mb"] = process.PrivateMemorySize64 / 1048576.0,
            ["managed_heap_mb"] = GC.GetTotalMemory(false) / 1048576.0,
        };
    }
}
