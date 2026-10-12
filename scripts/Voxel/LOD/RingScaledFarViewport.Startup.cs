using System.Collections.Generic;
using System;
using System.Diagnostics;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    [Export] public bool UseStagedStartup { get; set; }
    private IEnumerator<bool>? _startupStructure;
    private IEnumerator<bool>? _startupClouds;
    private IEnumerator<bool>? _startupClipmap;
    public string StartupError { get; private set; } = string.Empty;
    // There is no prior atlas to preserve during staged startup. Spend a small
    // additional main-thread slice on that first publication; later refreshes
    // keep the existing gameplay budget and sampling/texture work limits.
    private double FarWeatherAtlasWorkBudgetMilliseconds => UseStagedStartup && _farWeatherAtlas == null ? 4.0 : 1.5;
    // A generated fallback and the first weather atlas are mandatory before
    // releasing the loading view. Subsequent adaptive refinements stream normally.
    public bool StartupComplete => StartupError.Length == 0 && (!Enabled || (!UseStagedStartup ||
        (_farViewport?.GetNodeOrNull<MeshInstance3D>("ScaledStructuralShell")?.Mesh != null
        && _scaledCloudShell != null && GodotObject.IsInstanceValid(_scaledCloudShell) && _scaledCloudShell.Mesh != null
        && _globalSurfaceActive.Count > 0 && _farWeatherAtlas != null && StartupClipmapReady()
        && _startupStructure == null && _startupClouds == null && _startupClipmap == null)));
    public Godot.Collections.Dictionary GetLoadingStatus() => new()
    {
        ["ready"] = StartupComplete, ["error"] = StartupError,
        ["weather_work_budget_ms"] = FarWeatherAtlasWorkBudgetMilliseconds,
        ["weather_samples_completed"] = _farWeatherAtlasBuild?.SampleIndex ?? 0,
        ["weather_texels_completed"] = _farWeatherAtlasBuild?.TexelIndex ?? 0,
        ["phase"] = _startupStructure != null ? "far structure" : _startupClouds != null ? "far cloud shell"
            : _startupClipmap != null ? "far cloud canopy" : _farWeatherAtlas == null ? "far weather" : "far terrain"
    };

    private void UpdateStartupScene()
    {
        if (!UseStagedStartup || StartupError.Length > 0) return;
        try
        {
            long start = Stopwatch.GetTimestamp();
            for (int units = 0; units < 4; units++)
            {
                if (_startupStructure != null)
                {
                    AdvanceStartupBuilder(ref _startupStructure);
                    if (_startupStructure == null && _farViewport?.GetNodeOrNull<MeshInstance3D>("ScaledStructuralShell")?.Mesh == null)
                        FailStartup("The far structure did not produce a mesh.");
                }
                else if (_startupClouds != null)
                {
                    AdvanceStartupBuilder(ref _startupClouds);
                    if (_startupClouds == null && (_scaledCloudShell == null || _scaledCloudShell.Mesh == null))
                        FailStartup("The far cloud shell did not produce a mesh.");
                }
                else if (_startupClipmap != null)
                {
                    AdvanceStartupBuilder(ref _startupClipmap);
                    if (_startupClipmap == null && !StartupClipmapReady())
                        FailStartup("The far cloud canopy did not produce its meshes.");
                }
                else break;
                // Native indexing and mesh commits cannot be preempted.
                if (Stopwatch.GetElapsedTime(start).TotalMilliseconds >= 3) break;
            }
        }
        catch (Exception error) { FailStartup(error.Message); }
    }
    private void FailStartup(string error)
    {
        if (!UseStagedStartup || StartupError.Length > 0) return;
        StartupError = "Far ring startup failed: " + error;
        DisposeStartupScene();
        SetProcess(false);
    }

    private bool StartupClipmapReady()
    {
        if (!FarCloudClipmapEnabled) return true;
        foreach (MeshInstance3D? layer in _farCloudClipmapLayers)
            if (layer == null || !GodotObject.IsInstanceValid(layer) || layer.Mesh == null) return false;
        return true;
    }

    private static void AdvanceStartupBuilder(ref IEnumerator<bool>? builder)
    {
        if (builder == null || builder.MoveNext()) return;
        builder.Dispose();
        builder = null;
    }

    private void DisposeStartupScene()
    {
        _startupStructure?.Dispose(); _startupStructure = null;
        _startupClouds?.Dispose(); _startupClouds = null;
        _startupClipmap?.Dispose(); _startupClipmap = null;
    }
}
