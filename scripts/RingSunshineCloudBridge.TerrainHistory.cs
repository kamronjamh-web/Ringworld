using Godot;

namespace RingworldTechDemo;

public partial class RingSunshineCloudBridge
{
    [ExportGroup("Terrain sunlight in clouds")]
    [Export] public bool TerrainCloudShadowEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,1,0.01")] public float TerrainCloudShadowStrength { get; set; } = 1;
    [Export(PropertyHint.Range, "256,4096,256")] public int TerrainCloudShadowGlobalAroundResolution { get; set; } = 1024;
    [Export(PropertyHint.Range, "8,128,8")] public int TerrainCloudShadowGlobalAcrossResolution { get; set; } = 32;
    [Export(PropertyHint.Range, "2,16,1")] public int TerrainCloudShadowAltitudeSlices { get; set; } = 8;
    [Export(PropertyHint.Range, "32,256,32")] public int TerrainCloudShadowRegionalResolution { get; set; } = 64;
    [Export(PropertyHint.Range, "8000,128000,1000")] public float TerrainCloudShadowRegionalExtentMeters { get; set; } = 32000;
    [Export(PropertyHint.Range, "0.1,10,0.1")] public float TerrainCloudShadowRefreshSeconds { get; set; } = .5f;
    [Export(PropertyHint.Range, "8,128,1")] public int TerrainCloudShadowTraceVisitBudget { get; set; } = 32;
    [Export(PropertyHint.Range, "4096,131072,4096")] public int TerrainCloudShadowTexelsPerFrame { get; set; } = 32768;
    [Export(PropertyHint.Range, "0.01,2,0.01")] public float TerrainCloudShadowSunValidityDegrees { get; set; } = .25f;

    [ExportGroup("Cloud history rejection")]
    [Export] public bool DepthHistoryRejectionEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0.25,4,0.05")] public float HistoryDepthToleranceMultiplier { get; set; } = 1;
    [Export] public bool CloudHistoryCompatibilityEnabled { get; set; } = true;
    [Export(PropertyHint.Range, "0,0.95,0.01")] public float CloudHistoryGateAlphaLow { get; set; } = .20f;
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float CloudHistoryGateAlphaHigh { get; set; } = .65f;
    [Export(PropertyHint.Range, "0.25,8,0.05")] public float CloudHistoryContinuityAllowanceScale { get; set; } = 1;
    [Export(PropertyHint.Range, "1.5,32,0.5")] public float CloudHistoryContinuityHardResetRatio { get; set; } = 8;
    [Export] public bool LightningHistoryIsolationEnabled { get; set; } = true;

    private GodotObject? _historyPolicyResource;
    private (bool Depth, float Tolerance, bool Compatibility, bool Lightning, float GateLow, float GateHigh, float Allowance, float ResetRatio)? _historyPolicyApplied;
    private RingTerrainCloudSunVisibility? _terrainCloudSunVisibility;

    public Godot.Collections.Dictionary GetTerrainCloudSunVisibilityDiagnostics()
    {
        var producer = _terrainCloudSunVisibility ?? RingTerrainCloudSunVisibility.Instance;
        return producer != null && GodotObject.IsInstanceValid(producer)
            ? producer.GetDiagnostics()
            : new Godot.Collections.Dictionary { ["status"] = "unavailable" };
    }

    private void ApplyTerrainHistoryTuning()
    {
        ApplyHistoryPolicyTuning();
        EnsureTerrainCloudSunVisibility();
        if (_terrainCloudSunVisibility == null) return;
        _terrainCloudSunVisibility.Configure(new RingTerrainCloudSunVisibilitySettings
        {
            Enabled = TerrainCloudShadowEnabled,
            Strength = TerrainCloudShadowStrength,
            GlobalAroundResolution = TerrainCloudShadowGlobalAroundResolution,
            GlobalAcrossResolution = TerrainCloudShadowGlobalAcrossResolution,
            AltitudeSlices = TerrainCloudShadowAltitudeSlices,
            RegionalResolution = TerrainCloudShadowRegionalResolution,
            RegionalExtentMeters = TerrainCloudShadowRegionalExtentMeters,
            RefreshSeconds = TerrainCloudShadowRefreshSeconds,
            TraceVisitBudget = TerrainCloudShadowTraceVisitBudget,
            TexelsPerFrame = TerrainCloudShadowTexelsPerFrame,
            SunValidityDegrees = TerrainCloudShadowSunValidityDegrees
        });
        PublishTerrainCloudSunVisibility();
    }

    private void PublishTerrainCloudSunVisibility()
    {
        if (_clouds == null || _terrainCloudSunVisibility == null || !GodotObject.IsInstanceValid(_terrainCloudSunVisibility)) return;
        _terrainCloudSunVisibility.SetReceiverDemand("sunshine", Enabled && RuntimeVisible && CloudRenderingEnabled);
        if (!Enabled || !CloudRenderingEnabled)
        {
            _terrainCloudSunVisibility.UnregisterRequiredAltitudeRange("sunshine");
            return;
        }
        _terrainCloudSunVisibility.RegisterRequiredAltitudeRange("sunshine",
            Mathf.Min(Mathf.Min(NaturalCloudBaseAltitudeMeters, StormFrontCloudBaseAltitudeMeters), _clouds.Get("cloud_floor").AsSingle()),
            Mathf.Max(NaturalCloudTopAltitudeMeters, _clouds.Get("cloud_ceiling").AsSingle()));
        _terrainCloudSunVisibility.BindToResource(_clouds);
    }

    private void EnsureTerrainCloudSunVisibility()
    {
        if (!IsInsideTree() || GetParent() == null) return;
        if (_terrainCloudSunVisibility == null || !GodotObject.IsInstanceValid(_terrainCloudSunVisibility))
        {
            _terrainCloudSunVisibility = GetParent().GetNodeOrNull<RingTerrainCloudSunVisibility>("TerrainCloudSunVisibility");
            if (_terrainCloudSunVisibility == null)
            {
                _terrainCloudSunVisibility = new RingTerrainCloudSunVisibility
                {
                    Name = "TerrainCloudSunVisibility",
                    GridPath = new NodePath("../ChunkGridManager"),
                    GlobalLodPath = new NodePath("../GlobalTerrainLod3"),
                    FocusPath = new NodePath("../Explorer"),
                    SunLightPath = new NodePath("../StarLight")
                };
                // Parent may still be assembling its children during _Ready.
                GetParent().CallDeferred(Node.MethodName.AddChild, _terrainCloudSunVisibility);
            }
        }
    }

    private void ApplyHistoryPolicyTuning()
    {
        HistoryDepthToleranceMultiplier = SanitizeFinite(HistoryDepthToleranceMultiplier, 1, .25f, 4);
        CloudHistoryGateAlphaLow = SanitizeFinite(CloudHistoryGateAlphaLow, .20f, 0, .95f);
        CloudHistoryGateAlphaHigh = SanitizeFinite(CloudHistoryGateAlphaHigh, .65f, .05f, 1);
        if (CloudHistoryGateAlphaHigh < CloudHistoryGateAlphaLow + .05f)
            CloudHistoryGateAlphaHigh = Mathf.Min(CloudHistoryGateAlphaLow + .05f, 1);
        CloudHistoryContinuityAllowanceScale = SanitizeFinite(CloudHistoryContinuityAllowanceScale, 1, .25f, 8);
        CloudHistoryContinuityHardResetRatio = SanitizeFinite(CloudHistoryContinuityHardResetRatio, 8, 1.5f, 32);
        if (_clouds == null) return;
        var policy = (DepthHistoryRejectionEnabled, HistoryDepthToleranceMultiplier,
            CloudHistoryCompatibilityEnabled, LightningHistoryIsolationEnabled,
            CloudHistoryGateAlphaLow, CloudHistoryGateAlphaHigh,
            CloudHistoryContinuityAllowanceScale, CloudHistoryContinuityHardResetRatio);
        if (_historyPolicyResource == _clouds && _historyPolicyApplied == policy) return;
        _clouds.Set("depth_history_rejection_enabled", policy.Item1);
        _clouds.Set("history_depth_tolerance_multiplier", policy.Item2);
        _clouds.Set("cloud_history_compatibility_enabled", policy.Item3);
        _clouds.Set("lightning_history_isolation_enabled", policy.Item4);
        _clouds.Set("cloud_history_gate_alpha_low", policy.Item5);
        _clouds.Set("cloud_history_gate_alpha_high", policy.Item6);
        _clouds.Set("history_continuity_allowance_scale", policy.Item7);
        _clouds.Set("history_continuity_hard_reset_ratio", policy.Item8);
        _clouds.Call("request_history_reset");
        _historyPolicyResource = _clouds;
        _historyPolicyApplied = policy;
    }
}
