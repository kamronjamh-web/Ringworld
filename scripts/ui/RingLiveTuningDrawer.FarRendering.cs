using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Rendering;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private RingGraphicsQualityController? _farGraphics;
    private RingScaledFarViewport? _farRendering;
    private GameplayFarRenderingDefaults? _farDefaults;
    private readonly Dictionary<string, HSlider> _farRanges = new();
    private readonly Dictionary<string, CheckButton> _farToggles = new();
    private Label? _farStatus;
    private double _farSaveDelay, _farApplyDelay, _farRefreshDelay, _farRetryDelay;
    private bool _farApplyPending;

    private void InitializeFarRenderingTuning()
    {
        _farGraphics = GetNodeOrNull<RingGraphicsQualityController>(GraphicsQualityPath);
        _farRendering = GetNodeOrNull<RingScaledFarViewport>("../ScaledFarViewport");
        _farDefaults = _farGraphics?.FarRenderingDefaults;
    }

    private void ProcessFarRenderingTuning(double delta)
    {
        _farApplyDelay -= delta;
        if (_farApplyPending && _farApplyDelay <= 0) ApplyQueuedFarRendering();
        _farSaveDelay -= delta;
        _farRetryDelay -= delta;
        if (_farDefaults?.Dirty == true && _farSaveDelay <= 0 && _farRetryDelay <= 0 && !FlushFarRenderingDefaults())
            _farRetryDelay = 5;
        if (!_open) return;
        _farRefreshDelay -= delta;
        if (_farRefreshDelay <= 0) { _farRefreshDelay = .5; RefreshFarRenderingStatus(); }
    }

    private void ApplyQueuedFarRendering()
    {
        if (!_farApplyPending) return;
        _farApplyPending = false;
        _farGraphics?.ApplyFarRenderingDefaults();
    }

    private bool FlushFarRenderingDefaults()
    {
        ApplyQueuedFarRendering();
        bool saved = _farDefaults?.Save() ?? true;
        RefreshFarRenderingStatus();
        return saved;
    }

    private void SetFarRenderingValue(string key, Variant value)
    {
        if (_farDefaults?.SetOverride(key, value) != true) return;
        _farApplyPending = true;
        _farApplyDelay = .2;
        _farSaveDelay = .75;
        RefreshFarRenderingValues();
    }

    private void BuildFarRenderingTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Far ring");
        var quality = TuningMenuUi.CreateSection(tab, "Far ring detail", "Default: 100% viewport and Cinematic 4096 × 64 terrain detail. These controls persist independently of the graphics preset.");
        AddFarSlider(quality, "ResolutionScale", "Viewport scale", .25, 1, .05, 1);
        AddFarSlider(quality, "AroundSegments", "Around detail target", 256, 4096, 256, 4096);
        AddFarSlider(quality, "AcrossSegments", "Across detail target", 8, 64, 8, 64);
        var geometry = TuningMenuUi.CreateSection(tab, "Adaptive geometry", "Adapts terrain detail to rendered resolution and projected error. Previous complete geometry stays visible during rebuilds.");
        AddFarToggle(geometry, "AdaptiveGeometryEnabled", "Adaptive far geometry", true);
        AddFarSlider(geometry, "GeometryTargetPixelError", "Pixel error target", .5, 16, .5, 2);
        AddFarSlider(geometry, "GeometryMaxAngularSegments", "Maximum angular segments", 96, 768, 32, 384);
        AddFarSlider(geometry, "GeometrySectorCount", "Ring sectors", 4, 32, 1, 16);
        AddFarSlider(geometry, "GeometryRefinementHysteresis", "Refinement hysteresis", .1, .5, .05, .25);
        AddFarSlider(geometry, "GeometryMaxVertices", "Vertices per terrain set", 100000, 2000000, 50000, 750000);
        AddFarSlider(geometry, "GeometryUploadsPerFrame", "Geometry uploads per frame", 1, 8, 1, 2);
        AddFarSlider(geometry, "GeometryUploadBudgetMilliseconds", "Geometry upload budget (ms)", .25, 8, .25, 2);
        geometry.AddChild(new Label { Text = "Sector visibility uses the camera frustum automatically.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        var tiles = TuningMenuUi.CreateSection(tab, "Cached surface tiles", "Color and normal tiles preserve procedural biome appearance while bounding CPU work, uploads and resident texture memory.");
        AddFarToggle(tiles, "FarSurfaceTilesEnabled", "Cached color and normal tiles", true);
        AddFarSlider(tiles, "FarSurfaceTileResolution", "Tile pixels per side", 32, 256, 32, 128);
        AddFarSlider(tiles, "FarSurfaceTileCpuBudgetMb", "CPU cache budget (MiB)", 8, 512, 8, 64);
        AddFarSlider(tiles, "FarSurfaceTileGpuBudgetMb", "GPU tile budget (MiB)", 8, 512, 8, 64);
        AddFarSlider(tiles, "FarSurfaceTileMaximumJobs", "Tile worker limit", 1, 4, 1, 2);
        AddFarSlider(tiles, "FarSurfaceTileUploadsPerFrame", "Tile uploads per frame", 1, 8, 1, 2);
        AddFarSlider(tiles, "FarSurfaceTileUploadBudgetMilliseconds", "Upload time budget (ms)", .25, 8, .25, 2);
        var streaming = TuningMenuUi.CreateSection(tab, "Native texture streaming", "Engine capability and project enablement are shown below. Mip 0 retains the finest imported detail.");
        AddFarSlider(streaming, "NativeTextureStreamingBudgetMiB", "Texture budget (MiB)", 64, 8192, 64, 512);
        AddFarSlider(streaming, "NativeTextureStreamingInitialMip", "Coarsest resident mip", 0, 13, 1, 3);
        AddFarSlider(streaming, "NativeTextureStreamingFinestMip", "Finest allowed mip", 0, 13, 1, 0);
        foreach (string key in new[] { "NativeTextureStreamingBudgetMiB", "NativeTextureStreamingInitialMip", "NativeTextureStreamingFinestMip" })
            _farRanges[key].Editable = _farDefaults != null && NativeTextureStreaming.Available;
        var row = new HBoxContainer();
        var save = new Button { Name = "FarRenderingSave", Text = "Save far settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        save.Pressed += () => FlushFarRenderingDefaults();
        row.AddChild(save);
        var reset = new Button { Name = "FarRenderingReset", Text = "Reset far settings", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        reset.Pressed += () =>
        {
            _farDefaults?.Reset();
            _farGraphics?.ApplyFarRenderingDefaults();
            _farApplyPending = false;
            RefreshFarRenderingValues();
            FlushFarRenderingDefaults();
        };
        row.AddChild(reset);
        tab.AddChild(row);
        _farStatus = new Label { Name = "FarRenderingRuntimeStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_farStatus);
        RefreshFarRenderingStatus();
    }

    private void AddFarToggle(VBoxContainer parent, string key, string label, bool fallback)
    {
        var toggle = new CheckButton { Name = key, Text = label, Disabled = _farDefaults == null,
            ButtonPressed = _farDefaults?.Value(key, fallback).AsBool() ?? fallback };
        toggle.Toggled += value => SetFarRenderingValue(key, value);
        _farToggles[key] = toggle;
        parent.AddChild(toggle);
    }

    private void AddFarSlider(VBoxContainer parent, string key, string label, double min, double max, double step, double fallback)
    {
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(180, 0) });
        var slider = new HSlider { Name = key, MinValue = min, MaxValue = max, Step = step,
            Value = _farDefaults?.Value(key, fallback).AsDouble() ?? fallback,
            Editable = _farDefaults != null, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var valueLabel = new Label { Text = slider.Value.ToString("0.##"), CustomMinimumSize = new Vector2(55, 0) };
        slider.ValueChanged += value => { valueLabel.Text = value.ToString("0.##"); SetFarRenderingValue(key, value); };
        _farRanges[key] = slider;
        row.AddChild(slider);
        row.AddChild(valueLabel);
        parent.AddChild(row);
    }

    private void RefreshFarRenderingValues()
    {
        if (_farDefaults == null) return;
        foreach (var pair in _farRanges)
        {
            Variant fallback = pair.Key switch
            {
                "ResolutionScale" => 1f, "AroundSegments" => 4096, "AcrossSegments" => 64,
                "GeometryTargetPixelError" => 2f, "GeometryMaxAngularSegments" => 384, "GeometrySectorCount" => 16,
                "GeometryRefinementHysteresis" => .25f, "FarSurfaceTileResolution" => 128,
                "GeometryMaxVertices" => 750000,
                "FarSurfaceTileCpuBudgetMb" or "FarSurfaceTileGpuBudgetMb" => 64,
                "NativeTextureStreamingBudgetMiB" => 512, "NativeTextureStreamingInitialMip" => 3, "NativeTextureStreamingFinestMip" => 0,
                _ => 2f
            };
            // Update labels with the usual signal while avoiding changes to persisted keys.
            double value = _farDefaults.Value(pair.Key, fallback).AsDouble();
            pair.Value.SetValueNoSignal(value);
            if (pair.Value.GetParent().GetChild(2) is Label valueLabel) valueLabel.Text = value.ToString("0.##");
        }
        foreach (var pair in _farToggles) pair.Value.SetPressedNoSignal(_farDefaults.Value(pair.Key, true).AsBool());
    }

    private void RefreshFarRenderingStatus()
    {
        if (_farStatus == null) return;
        if (_farRendering == null || _farDefaults == null) { _farStatus.Text = "Far ring renderer unavailable."; return; }
        string saved = _farDefaults.LastError.Length > 0 ? "Save error: " + _farDefaults.LastError : _farDefaults.Dirty ? "Settings pending save." : "Settings saved.";
        _farStatus.Text = $"Far viewport: {_farRendering.ResolutionScale * 100:0}% | Detail target: {_farRendering.AroundSegments} × {_farRendering.AcrossSegments}\n"
            + $"Effective global mesh: {_farRendering.ActiveGlobalAroundSegments} × {_farRendering.ActiveGlobalAcrossSegments} | Regional angular: {_farRendering.ActiveGeometryAngularSegments}\n"
            + $"Vertices: {_farRendering.ActiveGlobalVertexCount} global, {_farRendering.ActiveRegionalVertexCount} regional | Sectors: {_farRendering.VisibleTerrainSectorCount} submitted, {_farRendering.ActiveTerrainSectorCount} active, {_farRendering.PendingTerrainSectorCount} pending\n"
            + $"Surface tiles: {_farRendering.SurfaceTileResidentCount} resident, {_farRendering.SurfaceTilePendingCount} pending | CPU {_farRendering.SurfaceTileCpuBytes / 1048576.0:0.0} MiB, GPU {_farRendering.SurfaceTileGpuBytes / 1048576.0:0.0} MiB\n"
            + $"Native streaming: {NativeTextureStreaming.Status} | Used {NativeTextureStreaming.UsedBytes / 1048576.0:0.0} MiB / {NativeTextureStreaming.MemoryBudgetMiB} MiB\n{saved}";
    }
}
