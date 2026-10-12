using Godot;

namespace RingworldTechDemo.UI;

public partial class RingLiveTuningDrawer
{
    private void AddTerrainCloudShadowControls(VBoxContainer tab)
    {
        var section = TuningMenuUi.CreateSection(tab, "Terrain sunlight in clouds");
        section.AddChild(new Label { Text = "Terrain blocks sunlight reaching clouds; cloud shadows on terrain remain separate.", AutowrapMode = TextServer.AutowrapMode.WordSmart, Modulate = RingUiStyle.Muted });
        AddPersistentToggle(section, "TerrainCloudShadowEnabled", "Terrain shadows in clouds", _cloudBridge?.TerrainCloudShadowEnabled ?? true);
        AddPersistentSlider(section, "TerrainCloudShadowStrength", "Terrain shadow strength", 0, 1, .01, _cloudBridge?.TerrainCloudShadowStrength ?? 1);
        AddPersistentSlider(section, "TerrainCloudShadowGlobalAroundResolution", "Global around samples", 256, 4096, 256, _cloudBridge?.TerrainCloudShadowGlobalAroundResolution ?? 1024);
        AddPersistentSlider(section, "TerrainCloudShadowGlobalAcrossResolution", "Global across samples", 8, 128, 8, _cloudBridge?.TerrainCloudShadowGlobalAcrossResolution ?? 32);
        AddPersistentSlider(section, "TerrainCloudShadowAltitudeSlices", "Altitude slices", 2, 16, 1, _cloudBridge?.TerrainCloudShadowAltitudeSlices ?? 8);
        AddPersistentSlider(section, "TerrainCloudShadowRegionalResolution", "Regional samples", 32, 256, 32, _cloudBridge?.TerrainCloudShadowRegionalResolution ?? 64);
        AddPersistentSlider(section, "TerrainCloudShadowRegionalExtentMeters", "Regional span (m)", 8000, 128000, 1000, _cloudBridge?.TerrainCloudShadowRegionalExtentMeters ?? 32000);
        AddPersistentSlider(section, "TerrainCloudShadowRefreshSeconds", "Visibility refresh (s)", .1, 10, .1, _cloudBridge?.TerrainCloudShadowRefreshSeconds ?? .5);
        AddPersistentSlider(section, "TerrainCloudShadowTraceVisitBudget", "Trace visits per texel", 8, 128, 1, _cloudBridge?.TerrainCloudShadowTraceVisitBudget ?? 32);
        AddPersistentSlider(section, "TerrainCloudShadowTexelsPerFrame", "Texels per frame", 4096, 131072, 4096, _cloudBridge?.TerrainCloudShadowTexelsPerFrame ?? 32768);
        AddPersistentSlider(section, "TerrainCloudShadowSunValidityDegrees", "Sun freshness angle (deg)", .01, 2, .01, _cloudBridge?.TerrainCloudShadowSunValidityDegrees ?? .25);
    }

    private void AddCloudHistoryPolicyControls(VBoxContainer section)
    {
        AddPersistentToggle(section, "DepthHistoryRejectionEnabled", "Reject history at depth changes", _cloudBridge?.DepthHistoryRejectionEnabled ?? true);
        AddPersistentSlider(section, "HistoryDepthToleranceMultiplier", "Depth history tolerance", .25, 4, .05, _cloudBridge?.HistoryDepthToleranceMultiplier ?? 1);
        AddPersistentToggle(section, "CloudHistoryCompatibilityEnabled", "Reject incompatible cloud history", _cloudBridge?.CloudHistoryCompatibilityEnabled ?? true);
        AddPersistentToggle(section, "LightningHistoryIsolationEnabled", "Isolate lightning from cloud history", _cloudBridge?.LightningHistoryIsolationEnabled ?? true);
    }
}
