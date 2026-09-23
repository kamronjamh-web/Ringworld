using System;
using System.Text;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase7;

namespace RingworldTechDemo.Voxel.Phase5;

public partial class VoxelPhase5DebugHud : CanvasLayer
{
    [Export] public NodePath GridManagerPath { get; set; } = new NodePath("../ChunkGridManager");
    [Export(PropertyHint.Range, "0.05,2.0,0.05")] public float RefreshIntervalSeconds { get; set; } = 0.2f;
    [Export] public bool StartVisible { get; set; } = true;
    [Export] public Key ToggleVisibilityKey { get; set; } = Key.F7;
    [Export] public Key PrintMetricsReportKey { get; set; } = Key.F8;
    [Export] public Key CaptureReplicationSnapshotKey { get; set; } = Key.F9;
    [Export] public Key ReapplyReplicationSnapshotKey { get; set; } = Key.F10;
    [Export] public Key ToggleAuthorityMismatchSimulationKey { get; set; } = Key.F11;
    [Export] public Key CaptureReplayBaselineKey { get; set; } = Key.F12;
    [Export] public Key ValidateReplayJournalKey { get; set; } = Key.Home;
    [Export] public Key ClearReplayJournalKey { get; set; } = Key.End;
    [Export] public bool LogRejectedAuthoritativeEdits { get; set; } = false;
    [Export] public Color PanelBackgroundColor { get; set; } = new Color(0.05f, 0.07f, 0.10f, 0.84f);
    [Export] public Color TextColor { get; set; } = new Color(0.92f, 0.95f, 0.98f, 1.0f);

    private VoxelChunkGridManager? _gridManager;
    private Label? _label;
    private double _refreshAccumulator;

    private ulong _processedEditCount;
    private ulong _appliedEditCount;
    private ulong _rejectedEditCount;
    private ulong _navInvalidationEvents;
    private ulong _chunkRevisionEvents;

    private string _lastEditReason = "n/a";
    private string _lastSnapshotStatus = "none";
    private string _lastSimulationStatus = "off";
    private string _lastReplayValidationStatus = "not_run";
    private string _lastReplayValidationHashes = "-";
    private uint _lastChunkRevision;
    private uint _lastGlobalRevision;
    private VoxelReplicationSnapshot? _capturedSnapshot;

    public override void _Ready()
    {
        BuildUi();
        ResolveManager();
        Visible = StartVisible;
        RefreshNow();
    }

    public override void _ExitTree()
    {
        UnsubscribeManagerEvents();
    }

    public override void _Process(double delta)
    {
        if (!Visible)
        {
            return;
        }

        _refreshAccumulator += delta;
        double interval = Math.Max(0.05, RefreshIntervalSeconds);
        if (_refreshAccumulator >= interval)
        {
            _refreshAccumulator = 0.0;
            RefreshNow();
        }
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey keyEvent || !keyEvent.Pressed || keyEvent.Echo)
        {
            return;
        }

        if (keyEvent.Keycode == ToggleVisibilityKey)
        {
            Visible = !Visible;
            if (Visible)
            {
                RefreshNow();
            }

            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == PrintMetricsReportKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            GD.Print(_gridManager.BuildRuntimeMetricsReport());
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == CaptureReplicationSnapshotKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            _capturedSnapshot = _gridManager.BuildReplicationSnapshot(minChunkRevisionExclusive: 0, includeChunkOverrides: true);
            _lastSnapshotStatus = $"captured global={_capturedSnapshot.GlobalRevision} chunks={_capturedSnapshot.ChunkRevisions.Count}";
            GD.Print($"Phase6 snapshot captured: {_lastSnapshotStatus}");
            RefreshNow();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == ReapplyReplicationSnapshotKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            if (_capturedSnapshot == null)
            {
                _lastSnapshotStatus = "reapply failed: no captured snapshot";
            }
            else
            {
                _gridManager.ApplyReplicationSnapshot(_capturedSnapshot, flushRebuildQueueAfterApply: true);
                _lastSnapshotStatus = $"reapplied global={_capturedSnapshot.GlobalRevision} chunks={_capturedSnapshot.ChunkRevisions.Count}";
                GD.Print($"Phase6 snapshot reapplied: {_lastSnapshotStatus}");
            }

            RefreshNow();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == ToggleAuthorityMismatchSimulationKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            _gridManager.SimulateLocalAuthorityMismatchForDebug = !_gridManager.SimulateLocalAuthorityMismatchForDebug;
            _lastSimulationStatus = _gridManager.SimulateLocalAuthorityMismatchForDebug ? "on" : "off";
            GD.Print($"Phase6 authority mismatch simulation: {_lastSimulationStatus}");
            RefreshNow();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == CaptureReplayBaselineKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            _gridManager.CapturePhase7ReplayBaseline();
            _gridManager.ClearPhase7CommandJournal(keepBaseline: true);
            _lastReplayValidationStatus = "baseline_captured_journal_cleared";
            _lastReplayValidationHashes = "-";
            GD.Print("Phase7 baseline captured and replay journal cleared.");
            RefreshNow();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == ValidateReplayJournalKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            VoxelReplayValidationReport report = _gridManager.ValidatePhase7DeterministicReplay(restoreWorldStateAfterValidation: true);
            _lastReplayValidationStatus = $"{report.Message} success={report.Success}";

            if (string.IsNullOrEmpty(report.ExpectedStateHash) && string.IsNullOrEmpty(report.ReplayedStateHash))
            {
                _lastReplayValidationHashes = "-";
            }
            else
            {
                _lastReplayValidationHashes = $"{report.ExpectedStateHash}/{report.ReplayedStateHash}";
            }

            GD.Print($"Phase7 replay validation: success={report.Success} journal={report.JournalCommandCount} replayed={report.ReplayedCommandCount} mismatches={report.ResultMismatchCount} stateMatch={report.FinalStateMatches} message={report.Message}");
            RefreshNow();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (keyEvent.Keycode == ClearReplayJournalKey)
        {
            ResolveManager();
            if (_gridManager == null)
            {
                return;
            }

            _gridManager.ClearPhase7CommandJournal(keepBaseline: false);
            _lastReplayValidationStatus = "journal_and_baseline_cleared";
            _lastReplayValidationHashes = "-";
            GD.Print("Phase7 replay journal and baseline cleared.");
            RefreshNow();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        var panel = new PanelContainer();
        panel.SetAnchorsPreset(Control.LayoutPreset.TopLeft);
        panel.Position = new Vector2(14, 14);
        panel.CustomMinimumSize = new Vector2(520, 290);

        var panelStyle = new StyleBoxFlat();
        panelStyle.BgColor = PanelBackgroundColor;
        panelStyle.CornerRadiusTopLeft = 6;
        panelStyle.CornerRadiusTopRight = 6;
        panelStyle.CornerRadiusBottomLeft = 6;
        panelStyle.CornerRadiusBottomRight = 6;
        panel.AddThemeStyleboxOverride("panel", panelStyle);

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_top", 8);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_bottom", 8);
        panel.AddChild(margin);

        _label = new Label();
        _label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _label.HorizontalAlignment = HorizontalAlignment.Left;
        _label.VerticalAlignment = VerticalAlignment.Top;
        _label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        _label.SizeFlagsVertical = Control.SizeFlags.ExpandFill;
        _label.AddThemeColorOverride("font_color", TextColor);
        _label.Text = "Phase 5 HUD waiting for manager...";
        margin.AddChild(_label);

        AddChild(panel);
    }

    private void ResolveManager()
    {
        if (_gridManager != null && IsInstanceValid(_gridManager))
        {
            return;
        }

        UnsubscribeManagerEvents();

        if (GridManagerPath.IsEmpty)
        {
            return;
        }

        _gridManager = GetNodeOrNull<VoxelChunkGridManager>(GridManagerPath);
        SubscribeManagerEvents();
    }

    private void SubscribeManagerEvents()
    {
        if (_gridManager == null)
        {
            return;
        }

        _gridManager.ChunkNavigationInvalidated += OnChunkNavigationInvalidated;
        _gridManager.ChunkRevisionChanged += OnChunkRevisionChanged;
        _gridManager.AuthoritativeEditProcessed += OnAuthoritativeEditProcessed;
    }

    private void UnsubscribeManagerEvents()
    {
        if (_gridManager == null)
        {
            return;
        }

        _gridManager.ChunkNavigationInvalidated -= OnChunkNavigationInvalidated;
        _gridManager.ChunkRevisionChanged -= OnChunkRevisionChanged;
        _gridManager.AuthoritativeEditProcessed -= OnAuthoritativeEditProcessed;
        _gridManager = null;
    }

    private void OnChunkNavigationInvalidated(RingChunkCoordinate _)
    {
        _navInvalidationEvents++;
    }

    private void OnChunkRevisionChanged(RingChunkCoordinate _, uint newRevision)
    {
        _chunkRevisionEvents++;
        _lastChunkRevision = newRevision;
    }

    private void OnAuthoritativeEditProcessed(VoxelBlockEditResult result)
    {
        _processedEditCount++;
        _lastEditReason = result.Reason;
        _lastChunkRevision = result.ChunkRevision;
        _lastGlobalRevision = result.GlobalRevision;

        if (result.Applied)
        {
            _appliedEditCount++;
            return;
        }

        _rejectedEditCount++;

        if (LogRejectedAuthoritativeEdits)
        {
            GD.Print($"Authoritative edit rejected reason={result.Reason}");
        }
    }

    private void RefreshNow()
    {
        ResolveManager();

        if (_label == null)
        {
            return;
        }

        if (_gridManager == null)
        {
            _label.Text = "Phase 5 HUD\nGrid manager not found.";
            return;
        }

        VoxelRuntimeMetricsSnapshot metrics = _gridManager.BuildRuntimeMetricsSnapshot();
        double toMb = 1.0 / (1024.0 * 1024.0);

        var builder = new StringBuilder();
        builder.AppendLine("PHASE 5 DEBUG HUD");
        builder.AppendLine($"Toggle {ToggleVisibilityKey} | Print report {PrintMetricsReportKey}");
        builder.AppendLine($"Snapshot capture {CaptureReplicationSnapshotKey} | Reapply {ReapplyReplicationSnapshotKey}");
        builder.AppendLine($"Toggle authority mismatch sim {ToggleAuthorityMismatchSimulationKey}");
        builder.AppendLine($"Capture replay baseline {CaptureReplayBaselineKey} | Validate replay {ValidateReplayJournalKey} | Clear replay {ClearReplayJournalKey}");
        builder.AppendLine($"Authoritative path: {_gridManager.EnableServerAuthoritativeEditPath} | LocalAuthorityId: {_gridManager.LocalAuthorityId}");
        builder.AppendLine($"Authority mismatch sim: {_gridManager.SimulateLocalAuthorityMismatchForDebug}");
        builder.AppendLine($"Nav invalidation: {_gridManager.EnableChunkLocalNavInvalidation} | Delay: {_gridManager.NavInvalidationDelaySeconds:0.00}s");
        builder.AppendLine($"Nav region bridge: {_gridManager.EnableNavigationRegionBridge} | Regions: {_gridManager.GetRegisteredNavigationRegionCount()} | Updates: {_gridManager.NavigationRegionBridgeUpdateCount}");
        builder.AppendLine($"Phase7 baseline captured: {_gridManager.HasPhase7ReplayBaseline()} | Journal entries: {_gridManager.GetPhase7JournalCount()}");
        builder.AppendLine($"Anchored runtime: {_gridManager.EnableAnchoredBuildObjects}");
        builder.AppendLine();
        builder.AppendLine($"Chunks loaded: {metrics.LoadedChunkCount}");
        builder.AppendLine($"Queued rebuilds: {metrics.QueuedRebuildCount}");
        builder.AppendLine($"Queued nav invalidations: {metrics.QueuedNavInvalidationCount}");
        builder.AppendLine($"Override chunks: {metrics.OverrideChunkCount}");
        builder.AppendLine($"Override voxels: {metrics.OverrideVoxelCount}");
        builder.AppendLine($"Anchored objects: {metrics.AnchoredObjectCount}");
        builder.AppendLine($"Estimated memory: {metrics.EstimatedTotalBytes * toMb:0.00} MB / {metrics.SoftMemoryBudgetMb} MB");
        builder.AppendLine($"Budget exceeded: {metrics.ExceedsSoftMemoryBudget}");
        builder.AppendLine();
        builder.AppendLine($"Authoritative edits: {_processedEditCount} total | {_appliedEditCount} applied | {_rejectedEditCount} rejected");
        builder.AppendLine($"Last reason: {_lastEditReason}");
        builder.AppendLine($"Snapshot: {_lastSnapshotStatus}");
        builder.AppendLine($"Mismatch sim state: {_lastSimulationStatus}");
        builder.AppendLine($"Phase7 replay: {_lastReplayValidationStatus}");
        builder.AppendLine($"Phase7 hashes expected/replayed: {_lastReplayValidationHashes}");
        builder.AppendLine($"Last revision chunk/global: {_lastChunkRevision}/{_lastGlobalRevision}");
        builder.AppendLine($"Event counters nav/revision: {_navInvalidationEvents}/{_chunkRevisionEvents}");

        _label.Text = builder.ToString();
    }
}
