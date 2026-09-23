using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private GameplayVegetationDefaults _vegetationDefaults = null!;
    private RingBiomeDecorationLayer? _vegetation;
    private Label? _vegetationStatus;
    private CheckButton? _vegetationToggle;
    private CheckButton? _vegetationSkipUnchanged, _vegetationOptimizeDispatches, _vegetationMergeFar, _vegetationShareFar, _vegetationKeepFallbacks;
    private CheckButton? _treeCompactLodProfile, _treeMergeMid, _treePruneEmpty, _treeSimplifyDistantMaterials, _treeLowDetailShadows;
    private SpinBox? _vegetationRoots, _vegetationBatches;
	private CheckButton? _treeShadowStabilize;
	private SpinBox? _treeShadowSuspend, _treeShadowResume, _treeShadowHold, _treeShadowDistance, _treeShadowBatches;
    private SpinBox? _treeStreamingBudget;
    private SpinBox? _treeCullingMovementThreshold, _treeCullingMaximumInterval;
    private SpinBox? _treeNearFadeStart, _treeNearFadeEnd, _treeBillboardFadeStart, _treeBillboardFadeEnd, _treeMidBatchTileSpan;
    private Label? _treeStreamingStatus;
    private double _vegetationSaveDelay, _vegetationRetryDelay, _vegetationRefreshDelay;
    private string? _vegetationDefaultsPathOverride;

    private void InitializeVegetationTuning()
    {
        _vegetation = FindVegetation(GetTree().CurrentScene) ?? FindVegetation(GetParent()) ?? FindVegetation(GetTree().Root);
        _vegetationDefaults = new GameplayVegetationDefaults(_vegetationDefaultsPathOverride);
        _vegetationDefaults.Load();
        ApplyVegetationGpu();
    }

    private static RingBiomeDecorationLayer? FindVegetation(Node? node)
    {
        if (node is RingBiomeDecorationLayer layer) return layer;
        if (node == null) return null;
        foreach (Node child in node.GetChildren())
        {
            RingBiomeDecorationLayer? found = FindVegetation(child);
            if (found != null) return found;
        }
        return null;
    }

    private void ApplyVegetationGpu()
    {
        if (_vegetation == null) return;
        _vegetation.UseGpuCulling = _vegetationDefaults.UseGpuCulling;
        _vegetation.SkipUnchangedGpuCulling = _vegetationDefaults.SkipUnchangedGpuCulling;
        _vegetation.OptimizeGpuTreeDispatches = _vegetationDefaults.OptimizeGpuTreeDispatches;
        _vegetation.MergeFarGpuTreeBatches = _vegetationDefaults.MergeFarGpuTreeBatches;
        _vegetation.UseSharedFarTreeImpostors = _vegetationDefaults.UseSharedFarTreeImpostors;
        _vegetation.UseCompactTreeLodProfile = _vegetationDefaults.UseCompactTreeLodProfile;
        _vegetation.TreeNearDetailFadeStartMeters = _vegetationDefaults.TreeNearDetailFadeStartMeters;
        _vegetation.TreeNearDetailFadeEndMeters = _vegetationDefaults.TreeNearDetailFadeEndMeters;
        _vegetation.TreeBillboardFadeStartMeters = _vegetationDefaults.TreeBillboardFadeStartMeters;
        _vegetation.TreeBillboardFadeEndMeters = _vegetationDefaults.TreeBillboardFadeEndMeters;
        _vegetation.MergeMidGpuTreeBatches = _vegetationDefaults.MergeMidGpuTreeBatches;
        _vegetation.MidTreeBatchTileSpan = _vegetationDefaults.MidTreeBatchTileSpan;
        _vegetation.PruneEmptyGpuTreeBatches = _vegetationDefaults.PruneEmptyGpuTreeBatches;
        _vegetation.SimplifyDistantTreeMaterials = _vegetationDefaults.SimplifyDistantTreeMaterials;
        _vegetation.UseLowDetailTreeShadows = _vegetationDefaults.UseLowDetailTreeShadows;
        _vegetation.KeepTreeLodFallbacksUntilReady = _vegetationDefaults.KeepTreeLodFallbacksUntilReady;
        _vegetation.MaximumGpuTreeRoots = _vegetationDefaults.MaximumGpuTreeRoots;
        _vegetation.MaximumGpuTreeBatches = _vegetationDefaults.MaximumGpuTreeBatches;
		_vegetation.TreeStreamingBudgetMilliseconds = _vegetationDefaults.TreeStreamingBudgetMilliseconds;
        _vegetation.TreeCullingMovementThresholdMeters = _vegetationDefaults.TreeCullingMovementThresholdMeters;
        _vegetation.TreeCullingMaximumIntervalSeconds = _vegetationDefaults.TreeCullingMaximumIntervalSeconds;
		_vegetation.StabilizeTreeSunShadowsDuringFastTravel = _vegetationDefaults.StabilizeTreeSunShadowsDuringFastTravel;
		_vegetation.TreeSunShadowSuspendSpeedMetersPerSecond = _vegetationDefaults.TreeSunShadowSuspendSpeedMetersPerSecond;
		_vegetation.TreeSunShadowResumeSpeedMetersPerSecond = _vegetationDefaults.TreeSunShadowResumeSpeedMetersPerSecond;
		_vegetation.TreeSunShadowSettleHoldSeconds = _vegetationDefaults.TreeSunShadowSettleHoldSeconds;
		_vegetation.TreeSunShadowDistanceMeters = _vegetationDefaults.TreeSunShadowDistanceMeters;
		_vegetation.MaximumTreeSunShadowBatches = _vegetationDefaults.MaximumTreeSunShadowBatches;
    }

    private void ProcessVegetationTuning(double delta)
    {
        if (_vegetation != null && !GodotObject.IsInstanceValid(_vegetation)) _vegetation = null;
        _vegetationRefreshDelay -= delta;
        if (_vegetationRefreshDelay <= 0)
        {
            _vegetationRefreshDelay = .25;
            if (_vegetation == null)
            {
                _vegetation = FindVegetation(GetTree().CurrentScene) ?? FindVegetation(GetTree().Root);
                if (_vegetation != null) ApplyVegetationGpu();
            }
            if (_open) RefreshVegetationControls();
        }
        if (_vegetationDefaults.Dirty)
        {
            _vegetationSaveDelay -= delta;
            _vegetationRetryDelay -= delta;
            if (_vegetationSaveDelay <= 0 && _vegetationRetryDelay <= 0 && !_vegetationDefaults.Save()) _vegetationRetryDelay = 5;
        }
    }

    private void RefreshVegetationControls()
    {
        bool available = _vegetation != null;
        if (_vegetationToggle != null)
        {
            _vegetationToggle.SetPressedNoSignal(_vegetationDefaults.UseGpuCulling);
            _vegetationToggle.Disabled = !available;
        }
        if (_vegetationSkipUnchanged != null) { _vegetationSkipUnchanged.SetPressedNoSignal(_vegetationDefaults.SkipUnchangedGpuCulling); _vegetationSkipUnchanged.Disabled = !available; }
        if (_vegetationOptimizeDispatches != null) { _vegetationOptimizeDispatches.SetPressedNoSignal(_vegetationDefaults.OptimizeGpuTreeDispatches); _vegetationOptimizeDispatches.Disabled = !available; }
        if (_vegetationMergeFar != null) { _vegetationMergeFar.SetPressedNoSignal(_vegetationDefaults.MergeFarGpuTreeBatches); _vegetationMergeFar.Disabled = !available; }
        if (_vegetationShareFar != null) { _vegetationShareFar.SetPressedNoSignal(_vegetationDefaults.UseSharedFarTreeImpostors); _vegetationShareFar.Disabled = !available; }
        if (_treeCompactLodProfile != null) { _treeCompactLodProfile.SetPressedNoSignal(_vegetationDefaults.UseCompactTreeLodProfile); _treeCompactLodProfile.Disabled = !available; }
        if (_treeMergeMid != null) { _treeMergeMid.SetPressedNoSignal(_vegetationDefaults.MergeMidGpuTreeBatches); _treeMergeMid.Disabled = !available; }
        if (_treePruneEmpty != null) { _treePruneEmpty.SetPressedNoSignal(_vegetationDefaults.PruneEmptyGpuTreeBatches); _treePruneEmpty.Disabled = !available; }
        if (_treeSimplifyDistantMaterials != null) { _treeSimplifyDistantMaterials.SetPressedNoSignal(_vegetationDefaults.SimplifyDistantTreeMaterials); _treeSimplifyDistantMaterials.Disabled = !available; }
        if (_treeLowDetailShadows != null) { _treeLowDetailShadows.SetPressedNoSignal(_vegetationDefaults.UseLowDetailTreeShadows); _treeLowDetailShadows.Disabled = !available; }
        if (_vegetationKeepFallbacks != null) { _vegetationKeepFallbacks.SetPressedNoSignal(_vegetationDefaults.KeepTreeLodFallbacksUntilReady); _vegetationKeepFallbacks.Disabled = !available; }
        if (_vegetationRoots != null)
        {
            if (!_vegetationRoots.GetLineEdit().HasFocus()) _vegetationRoots.SetValueNoSignal(_vegetationDefaults.MaximumGpuTreeRoots);
            _vegetationRoots.Editable = available;
        }
        if (_vegetationBatches != null)
        {
            if (!_vegetationBatches.GetLineEdit().HasFocus()) _vegetationBatches.SetValueNoSignal(_vegetationDefaults.MaximumGpuTreeBatches);
            _vegetationBatches.Editable = available;
        }
		if (_treeShadowStabilize != null) { _treeShadowStabilize.SetPressedNoSignal(_vegetationDefaults.StabilizeTreeSunShadowsDuringFastTravel); _treeShadowStabilize.Disabled = !available; }
		RefreshVegetationSpin(_treeShadowSuspend, _vegetationDefaults.TreeSunShadowSuspendSpeedMetersPerSecond, available);
		RefreshVegetationSpin(_treeShadowResume, _vegetationDefaults.TreeSunShadowResumeSpeedMetersPerSecond, available);
		RefreshVegetationSpin(_treeShadowHold, _vegetationDefaults.TreeSunShadowSettleHoldSeconds, available);
		RefreshVegetationSpin(_treeShadowDistance, _vegetationDefaults.TreeSunShadowDistanceMeters, available);
		RefreshVegetationSpin(_treeShadowBatches, _vegetationDefaults.MaximumTreeSunShadowBatches, available);
        RefreshVegetationSpin(_treeStreamingBudget, _vegetationDefaults.TreeStreamingBudgetMilliseconds, available);
        RefreshVegetationSpin(_treeCullingMovementThreshold, _vegetationDefaults.TreeCullingMovementThresholdMeters, available);
        RefreshVegetationSpin(_treeCullingMaximumInterval, _vegetationDefaults.TreeCullingMaximumIntervalSeconds, available);
        RefreshVegetationSpin(_treeNearFadeStart, _vegetationDefaults.TreeNearDetailFadeStartMeters, available);
        RefreshVegetationSpin(_treeNearFadeEnd, _vegetationDefaults.TreeNearDetailFadeEndMeters, available);
        RefreshVegetationSpin(_treeBillboardFadeStart, _vegetationDefaults.TreeBillboardFadeStartMeters, available);
        RefreshVegetationSpin(_treeBillboardFadeEnd, _vegetationDefaults.TreeBillboardFadeEndMeters, available);
        RefreshVegetationSpin(_treeMidBatchTileSpan, _vegetationDefaults.MidTreeBatchTileSpan, available);
        if (_treeStreamingStatus != null)
            _treeStreamingStatus.Text = _vegetation == null ? "Tree streaming is unavailable."
                : _vegetation.TreePreparationPending ? $"Preparation pending. Last slice: {_vegetation.LastTreePreparationMilliseconds:F2} ms."
                : $"Preparation ready. Last: {_vegetation.LastTreePreparationMilliseconds:F2} ms; reused: {_vegetation.TreePreparationGroupsReused:N0}; rebuilt: {_vegetation.TreePreparationGroupsRebuilt:N0}.";
        if (_vegetationStatus != null)
        {
            string save = _vegetationDefaults.LastError.Length > 0 ? "Save failed; retrying."
                : _vegetationDefaults.Dirty ? "Settings pending." : "Settings saved.";
            _vegetationStatus.Text = _vegetation == null ? "Tree vegetation layer is unavailable."
                : $"{_vegetation.GpuTreeCullingStatus}. Resident roots: {_vegetation.GpuTreeResidentRootCount:N0}. "
				+ $"{_vegetation.GpuTreeBatchCount} current + {_vegetation.GpuTreeFallbackBatchCount} fallback batches, {_vegetation.GpuTreeResidentBytes / 1048576.0:F1} MiB estimated buffers. "
				+ $"Sun shadows: {(_vegetation.TreeSunShadowsSuppressed ? _vegetation.TreeSunShadowSuppressionReason : "active")}, "
				+ $"{_vegetation.ActiveGpuTreeSunShadowRendererCount}/{_vegetation.MaximumTreeSunShadowBatches} GPU shadow batches. {save}";
        }
    }

    public void InstallVegetationDefaults(string path)
    {
        if (!path.StartsWith("res://.godot/", StringComparison.Ordinal)) return;
        _vegetationDefaultsPathOverride = path;
        if (_vegetationDefaults == null) return;
        _vegetationDefaults = new GameplayVegetationDefaults(path);
        _vegetationDefaults.Load();
        ApplyVegetationGpu();
        RefreshVegetationControls();
    }

    private void BuildVegetationTab(TabContainer tabs)
    {
        VBoxContainer tab = CreateTab(tabs, "Vegetation");
        VBoxContainer culling = TuningMenuUi.CreateSection(tab, "Culling");
        _vegetationToggle = new CheckButton { Name = "UseGpuTreeCulling", Text = "Use GPU tree culling",
            ButtonPressed = _vegetationDefaults.UseGpuCulling, Disabled = _vegetation == null };
        _vegetationToggle.Toggled += value => SetVegetationValue("UseGpuCulling", value);
        culling.AddChild(_vegetationToggle);
        _vegetationSkipUnchanged = new CheckButton { Name = "SkipUnchangedGpuTreeCulling", Text = "Skip unchanged tree culling", TooltipText = "Reuses settled tree visibility until camera, source, or culling settings change. Wind animation continues.", ButtonPressed = _vegetationDefaults.SkipUnchangedGpuCulling, Disabled = _vegetation == null };
        _vegetationSkipUnchanged.Toggled += value => SetVegetationValue("SkipUnchangedGpuCulling", value);
        culling.AddChild(_vegetationSkipUnchanged);
        _vegetationOptimizeDispatches = new CheckButton { Name = "OptimizeGpuTreeDispatches", Text = "Optimize tree compute", ButtonPressed = _vegetationDefaults.OptimizeGpuTreeDispatches, Disabled = _vegetation == null };
        _vegetationOptimizeDispatches.Toggled += value => SetVegetationValue("OptimizeGpuTreeDispatches", value);
        culling.AddChild(_vegetationOptimizeDispatches);
        _vegetationMergeFar = new CheckButton { Name = "MergeFarGpuTreeBatches", Text = "Merge far GPU tree batches", ButtonPressed = _vegetationDefaults.MergeFarGpuTreeBatches, Disabled = _vegetation == null };
        _vegetationMergeFar.Toggled += value => SetVegetationValue("MergeFarGpuTreeBatches", value);
        culling.AddChild(_vegetationMergeFar);
        _vegetationShareFar = new CheckButton { Name = "UseSharedFarTreeImpostors", Text = "Share distant tree batches", ButtonPressed = _vegetationDefaults.UseSharedFarTreeImpostors, Disabled = _vegetation == null };
        _vegetationShareFar.Toggled += value => SetVegetationValue("UseSharedFarTreeImpostors", value);
        culling.AddChild(_vegetationShareFar);
        _vegetationKeepFallbacks = new CheckButton { Name = "KeepTreeLodFallbacksUntilReady", Text = "Keep previous tree LOD until replacement is ready", TooltipText = "Prevents tree coverage holes during streamed GPU output replacement. Retained fallbacks do not run culling or cast new shadows.", ButtonPressed = _vegetationDefaults.KeepTreeLodFallbacksUntilReady, Disabled = _vegetation == null };
        _vegetationKeepFallbacks.Toggled += value => SetVegetationValue("KeepTreeLodFallbacksUntilReady", value);
        culling.AddChild(_vegetationKeepFallbacks);
        VBoxContainer lod = TuningMenuUi.CreateSection(tab, "Tree LOD transitions");
        _treeCompactLodProfile = new CheckButton { Name = "UseCompactTreeLodProfile", Text = "Use compact tree LOD profile", TooltipText = "For GPU-rendered imported trees, uses detailed trees close to the camera, then fades to lower-detail meshes and billboards. CPU fallback uses its existing LOD behavior.", ButtonPressed = _vegetationDefaults.UseCompactTreeLodProfile, Disabled = _vegetation == null };
        _treeCompactLodProfile.Toggled += value => SetVegetationValue("UseCompactTreeLodProfile", value); lod.AddChild(_treeCompactLodProfile);
        _treeNearFadeStart = AddVegetationSpin(lod, "TreeNearDetailFadeStartMeters", "Near-detail fade starts (m)", 0, 200, 1, _vegetationDefaults.TreeNearDetailFadeStartMeters);
        _treeNearFadeEnd = AddVegetationSpin(lod, "TreeNearDetailFadeEndMeters", "Near-detail fade ends (m)", 0, 200, 1, _vegetationDefaults.TreeNearDetailFadeEndMeters);
        _treeBillboardFadeStart = AddVegetationSpin(lod, "TreeBillboardFadeStartMeters", "Billboard fade starts (m)", 0, 2000, 1, _vegetationDefaults.TreeBillboardFadeStartMeters);
        _treeBillboardFadeEnd = AddVegetationSpin(lod, "TreeBillboardFadeEndMeters", "Billboard fade ends (m)", 0, 2000, 1, _vegetationDefaults.TreeBillboardFadeEndMeters);
        VBoxContainer batching = TuningMenuUi.CreateSection(tab, "Tree draw batching");
        _treeMergeMid = new CheckButton { Name = "MergeMidGpuTreeBatches", Text = "Merge mid-range tree batches", ButtonPressed = _vegetationDefaults.MergeMidGpuTreeBatches, Disabled = _vegetation == null };
        _treeMergeMid.Toggled += value => SetVegetationValue("MergeMidGpuTreeBatches", value); batching.AddChild(_treeMergeMid);
        _treeMidBatchTileSpan = AddVegetationSpin(batching, "MidTreeBatchTileSpan", "Mid-range batch tile span", 1, 4, 1, _vegetationDefaults.MidTreeBatchTileSpan,
            "Larger groups reduce draw calls but can keep more off-screen trees in a draw.");
        _treePruneEmpty = new CheckButton { Name = "PruneEmptyGpuTreeBatches", Text = "Skip empty tree draw batches", ButtonPressed = _vegetationDefaults.PruneEmptyGpuTreeBatches, Disabled = _vegetation == null };
        _treePruneEmpty.Toggled += value => SetVegetationValue("PruneEmptyGpuTreeBatches", value); batching.AddChild(_treePruneEmpty);
        VBoxContainer materials = TuningMenuUi.CreateSection(tab, "Distant tree rendering");
        _treeSimplifyDistantMaterials = new CheckButton { Name = "SimplifyDistantTreeMaterials", Text = "Simplify distant tree materials", TooltipText = "Reduces shading work for distant trees while keeping their color, cutout, and wind.", ButtonPressed = _vegetationDefaults.SimplifyDistantTreeMaterials, Disabled = _vegetation == null };
        _treeSimplifyDistantMaterials.Toggled += value => SetVegetationValue("SimplifyDistantTreeMaterials", value); materials.AddChild(_treeSimplifyDistantMaterials);
        VBoxContainer budgets = TuningMenuUi.CreateSection(tab, "Budgets");
        budgets.AddChild(new Label { Text = "Lower root and batch limits save memory but can reduce visible forest coverage. Nearby collision trees retain priority.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        _vegetationRoots = AddVegetationCap(budgets, "MaximumGpuTreeRoots", "Resident GPU tree roots", 256, 1048576, 256, _vegetationDefaults.MaximumGpuTreeRoots);
        _vegetationBatches = AddVegetationCap(budgets, "MaximumGpuTreeBatches", "GPU tree draw batches", 16, 2048, 16, _vegetationDefaults.MaximumGpuTreeBatches);
		VBoxContainer streaming = TuningMenuUi.CreateSection(tab, "Vegetation Streaming");
        _treeStreamingBudget = AddVegetationSpin(streaming, "TreeStreamingBudgetMilliseconds", "Streaming CPU budget (ms)", .25, 8, .25, _vegetationDefaults.TreeStreamingBudgetMilliseconds,
            "Soft CPU preparation/install budget. Previous trees remain visible while replacement preparation completes.");
        _treeCullingMovementThreshold = AddVegetationSpin(streaming, "TreeCullingMovementThresholdMeters", "Tree movement refresh distance (m)", 0, 4, .01, _vegetationDefaults.TreeCullingMovementThresholdMeters,
            "0 refreshes on every movement frame. Source and protected-tree changes refresh immediately.");
        _treeCullingMaximumInterval = AddVegetationSpin(streaming, "TreeCullingMaximumIntervalSeconds", "Maximum moving refresh delay (s)", 0, .5, .001, _vegetationDefaults.TreeCullingMaximumIntervalSeconds,
            "0 refreshes on every movement frame. Source and protected-tree changes refresh immediately.");
        _treeStreamingStatus = new Label { Name = "TreeStreamingStatus", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        streaming.AddChild(_treeStreamingStatus);
		VBoxContainer shadows = TuningMenuUi.CreateSection(tab, "Sun shadow stability");
		_treeShadowStabilize = new CheckButton { Name = "StabilizeTreeSunShadowsDuringFastTravel", Text = "Suspend tree sun shadows during fast travel", ButtonPressed = _vegetationDefaults.StabilizeTreeSunShadowsDuringFastTravel, Disabled = _vegetation == null };
		_treeShadowStabilize.Toggled += value => SetVegetationValue("StabilizeTreeSunShadowsDuringFastTravel", value); shadows.AddChild(_treeShadowStabilize);
		_treeLowDetailShadows = new CheckButton { Name = "UseLowDetailTreeShadows", Text = "Use lower-detail tree shadow geometry", TooltipText = "Uses the lower-detail tree mesh for near and mid-range tree shadow casts.", ButtonPressed = _vegetationDefaults.UseLowDetailTreeShadows, Disabled = _vegetation == null };
		_treeLowDetailShadows.Toggled += value => SetVegetationValue("UseLowDetailTreeShadows", value); shadows.AddChild(_treeLowDetailShadows);
		_treeShadowSuspend = AddVegetationSpin(shadows, "TreeSunShadowSuspendSpeedMetersPerSecond", "Suspend speed (m/s)", 0, 2000, 10, _vegetationDefaults.TreeSunShadowSuspendSpeedMetersPerSecond);
		_treeShadowResume = AddVegetationSpin(shadows, "TreeSunShadowResumeSpeedMetersPerSecond", "Resume speed (m/s)", 0, 250, 5, _vegetationDefaults.TreeSunShadowResumeSpeedMetersPerSecond);
		_treeShadowHold = AddVegetationSpin(shadows, "TreeSunShadowSettleHoldSeconds", "Settled hold (seconds)", 0, 5, .1, _vegetationDefaults.TreeSunShadowSettleHoldSeconds);
		_treeShadowDistance = AddVegetationSpin(shadows, "TreeSunShadowDistanceMeters", "Maximum tree sun-shadow distance (m)", 0, 2000, 10, _vegetationDefaults.TreeSunShadowDistanceMeters);
		_treeShadowBatches = AddVegetationSpin(shadows, "MaximumTreeSunShadowBatches", "Maximum GPU tree sun-shadow batches", 0, 1024, 16, _vegetationDefaults.MaximumTreeSunShadowBatches);
        var save = new Button { Name = "VegetationSaveNow", Text = "Save vegetation settings" };
        save.Pressed += () => { _vegetationDefaults.Save(); RefreshVegetationControls(); };
        tab.AddChild(save);
        var reset = new Button { Name = "VegetationResetDefaults", Text = "Reset vegetation defaults" };
        reset.Pressed += () =>
        {
            _vegetationDefaults.Reset();
            ApplyVegetationGpu();
            _vegetationSaveDelay = _vegetationRetryDelay = 0;
            RefreshVegetationControls();
        };
        tab.AddChild(reset);
        _vegetationStatus = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted };
        tab.AddChild(_vegetationStatus);
        RefreshVegetationControls();
    }

    private SpinBox AddVegetationCap(VBoxContainer tab, string key, string label, int min, int max, int step, int value)
    {
        var spin = new SpinBox { Name = key, MinValue = min, MaxValue = max, Step = step, Value = value, Editable = _vegetation != null };
        spin.ValueChanged += number => SetVegetationValue(key, (long)number);
        tab.AddChild(new Label { Text = label });
        tab.AddChild(spin);
        return spin;
    }

	private SpinBox AddVegetationSpin(VBoxContainer tab, string key, string label, double min, double max, double step, double value, string tooltip = "")
	{
		var spin = new SpinBox { Name = key, MinValue = min, MaxValue = max, Step = step, Value = value, Editable = _vegetation != null, TooltipText = tooltip };
		spin.ValueChanged += number => SetVegetationValue(key, number); tab.AddChild(new Label { Text = label, TooltipText = tooltip }); tab.AddChild(spin); return spin;
	}
	private static void RefreshVegetationSpin(SpinBox? spin, double value, bool available)
	{
		if (spin == null) return; if (!spin.GetLineEdit().HasFocus()) spin.SetValueNoSignal(value); spin.Editable = available;
	}

    private void SetVegetationValue(string key, Variant value)
    {
        if (!_vegetationDefaults.Set(key, value)) return;
        ApplyVegetationGpu();
        _vegetationSaveDelay = .5;
        _vegetationRetryDelay = 0;
    }
}
