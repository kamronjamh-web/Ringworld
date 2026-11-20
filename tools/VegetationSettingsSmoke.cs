using System;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.UI;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Isolated persistence evidence for the saved GPU-tree preference.</summary>
public partial class VegetationSettingsSmoke : Node
{
    public string Result { get; private set; } = "RUNNING";
    public override void _Ready()
    {
        try { Run(); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
    }
    private static void Run()
    {
        string path = "res://.godot/vegetation_settings_smoke.tres";
        if (ResourceSaver.Save(new GameplayVegetationSettings(), path) != Error.Ok) throw new InvalidOperationException("empty vegetation fixture write failed");
        var absent = new GameplayVegetationDefaults(path); absent.Load();
        if (!absent.UseCompactTreeLodProfile || !absent.MergeMidGpuTreeBatches || !absent.PruneEmptyGpuTreeBatches || !absent.SimplifyDistantTreeMaterials || !absent.UseLowDetailTreeShadows || absent.MidTreeBatchTileSpan != 2 || !Mathf.IsEqualApprox(absent.TreeNearDetailFadeStartMeters, 25) || !Mathf.IsEqualApprox(absent.TreeNearDetailFadeEndMeters, 40) || !Mathf.IsEqualApprox(absent.TreeBillboardFadeStartMeters, 300) || !Mathf.IsEqualApprox(absent.TreeBillboardFadeEndMeters, 350)) throw new InvalidOperationException("missing tree rendering preferences did not use defaults");
        var first = new GameplayVegetationDefaults(path); first.Load(); first.Set("UseGpuCulling", false); first.Set("OptimizeGpuTreeDispatches", false); first.Set("UseSharedFarTreeImpostors", false); first.Set("UseCompactTreeLodProfile", false); first.Set("MergeMidGpuTreeBatches", false); first.Set("PruneEmptyGpuTreeBatches", false); first.Set("SimplifyDistantTreeMaterials", false); first.Set("UseLowDetailTreeShadows", false); first.Set("TreeNearDetailFadeStartMeters", 12); first.Set("TreeNearDetailFadeEndMeters", 24); first.Set("TreeBillboardFadeStartMeters", 180); first.Set("TreeBillboardFadeEndMeters", 220); first.Set("MidTreeBatchTileSpan", 3); first.Set("KeepTreeLodFallbacksUntilReady", false); first.Set("MaximumGpuTreeRoots", 2048); first.Set("MaximumGpuTreeBatches", 48); first.Set("StabilizeTreeSunShadowsDuringFastTravel", false); first.Set("TreeSunShadowDistanceMeters", 240); first.Set("MaximumTreeSunShadowBatches", 64); first.Set("TreeStreamingBudgetMilliseconds", 3.25); first.Set("TreeCullingMovementThresholdMeters", 1.25); first.Set("TreeCullingMaximumIntervalSeconds", .2);
        if (!first.Save()) throw new InvalidOperationException("vegetation GPU preference did not save");
        var second = new GameplayVegetationDefaults(path); second.Load();
        if (second.UseGpuCulling || second.OptimizeGpuTreeDispatches || second.UseSharedFarTreeImpostors || second.UseCompactTreeLodProfile || second.MergeMidGpuTreeBatches || second.PruneEmptyGpuTreeBatches || second.SimplifyDistantTreeMaterials || second.UseLowDetailTreeShadows || second.MidTreeBatchTileSpan != 3 || !Mathf.IsEqualApprox(second.TreeNearDetailFadeStartMeters, 12) || !Mathf.IsEqualApprox(second.TreeNearDetailFadeEndMeters, 24) || !Mathf.IsEqualApprox(second.TreeBillboardFadeStartMeters, 180) || !Mathf.IsEqualApprox(second.TreeBillboardFadeEndMeters, 220) || second.KeepTreeLodFallbacksUntilReady || second.MaximumGpuTreeRoots != 2048 || second.MaximumGpuTreeBatches != 48 || second.StabilizeTreeSunShadowsDuringFastTravel || !Mathf.IsEqualApprox(second.TreeSunShadowDistanceMeters, 240) || second.MaximumTreeSunShadowBatches != 64 || !Mathf.IsEqualApprox(second.TreeStreamingBudgetMilliseconds, 3.25f) || !Mathf.IsEqualApprox(second.TreeCullingMovementThresholdMeters, 1.25f) || !Mathf.IsEqualApprox(second.TreeCullingMaximumIntervalSeconds, .2f)) throw new InvalidOperationException("vegetation GPU/shadow/streaming preferences did not round-trip");
        second.Reset(); if (!second.Save()) throw new InvalidOperationException("vegetation reset did not save");
        var reset = new GameplayVegetationDefaults(path); reset.Load();
        if (!reset.UseGpuCulling || !reset.OptimizeGpuTreeDispatches || !reset.UseSharedFarTreeImpostors || !reset.UseCompactTreeLodProfile || !reset.MergeMidGpuTreeBatches || !reset.PruneEmptyGpuTreeBatches || !reset.SimplifyDistantTreeMaterials || !reset.UseLowDetailTreeShadows || reset.MidTreeBatchTileSpan != 2 || !Mathf.IsEqualApprox(reset.TreeNearDetailFadeStartMeters, 25) || !Mathf.IsEqualApprox(reset.TreeNearDetailFadeEndMeters, 40) || !Mathf.IsEqualApprox(reset.TreeBillboardFadeStartMeters, 300) || !Mathf.IsEqualApprox(reset.TreeBillboardFadeEndMeters, 350) || !reset.KeepTreeLodFallbacksUntilReady || reset.MaximumGpuTreeRoots != 65536 || reset.MaximumGpuTreeBatches != 1024 || !reset.StabilizeTreeSunShadowsDuringFastTravel || !Mathf.IsEqualApprox(reset.TreeSunShadowDistanceMeters, 480) || reset.MaximumTreeSunShadowBatches != 128 || !Mathf.IsEqualApprox(reset.TreeStreamingBudgetMilliseconds, 2.0f) || !Mathf.IsEqualApprox(reset.TreeCullingMovementThresholdMeters, .5f) || !Mathf.IsEqualApprox(reset.TreeCullingMaximumIntervalSeconds, .1f)) throw new InvalidOperationException("vegetation reset did not restore defaults");
        string malformed = "res://.godot/vegetation_settings_malformed.tres";
        var bad = new GameplayVegetationSettings(); bad.Overrides["UseGpuCulling"] = 7;
        if (ResourceSaver.Save(bad, malformed) != Error.Ok) throw new InvalidOperationException("malformed fixture write failed");
        var rejected = new GameplayVegetationDefaults(malformed); rejected.Load();
        if (!rejected.UseGpuCulling || rejected.LastError.Length != 0) throw new InvalidOperationException("invalid vegetation payload was accepted");
        if (GameplayVegetationDefaults.TryValidate("MaximumGpuTreeRoots", "bad", out _) || GameplayVegetationDefaults.TryValidate("MaximumGpuTreeBatches", true, out _)) throw new InvalidOperationException("invalid vegetation types were accepted");
        if (!GameplayVegetationDefaults.TryValidate("MaximumGpuTreeRoots", -1, out Variant low) || low.AsInt64() != 256) throw new InvalidOperationException("root range was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("MaximumGpuTreeBatches", 99999, out Variant high) || high.AsInt64() != 2048) throw new InvalidOperationException("batch range was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("MaximumGpuTreeRoots", double.MaxValue, out Variant overflow) || overflow.AsInt64() != 1048576) throw new InvalidOperationException("large root cap overflowed before clamp");
        if (GameplayVegetationDefaults.TryValidate("MaximumGpuTreeRoots", double.NaN, out _)) throw new InvalidOperationException("nonfinite cap accepted");
		if (!GameplayVegetationDefaults.TryValidate("TreeStreamingBudgetMilliseconds", -1, out Variant streamLow) || !Mathf.IsEqualApprox((float)streamLow.AsDouble(), .25f)) throw new InvalidOperationException("streaming budget low range was not clamped");
		if (!GameplayVegetationDefaults.TryValidate("TreeStreamingBudgetMilliseconds", 99, out Variant streamHigh) || !Mathf.IsEqualApprox((float)streamHigh.AsDouble(), 8)) throw new InvalidOperationException("streaming budget high range was not clamped");
		if (GameplayVegetationDefaults.TryValidate("TreeStreamingBudgetMilliseconds", double.NaN, out _)) throw new InvalidOperationException("nonfinite streaming budget accepted");
		if (!GameplayVegetationDefaults.TryValidate("TreeCullingMovementThresholdMeters", 99, out Variant moveHigh) || !Mathf.IsEqualApprox((float)moveHigh.AsDouble(), 4)) throw new InvalidOperationException("movement threshold was not clamped");
		if (!GameplayVegetationDefaults.TryValidate("TreeCullingMaximumIntervalSeconds", -1, out Variant delayLow) || !Mathf.IsEqualApprox((float)delayLow.AsDouble(), 0)) throw new InvalidOperationException("moving delay was not clamped");
		if (GameplayVegetationDefaults.TryValidate("TreeCullingMaximumIntervalSeconds", double.NaN, out _)) throw new InvalidOperationException("nonfinite moving delay accepted");
		if (!GameplayVegetationDefaults.TryValidate("MaximumTreeSunShadowBatches", -1, out Variant shadowLow) || shadowLow.AsInt64() != 0) throw new InvalidOperationException("shadow batch cap was not clamped");
		if (!GameplayVegetationDefaults.TryValidate("TreeSunShadowDistanceMeters", 99999, out Variant shadowDistance) || !Mathf.IsEqualApprox((float)shadowDistance.AsDouble(), 2000)) throw new InvalidOperationException("shadow distance was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("TreeNearDetailFadeStartMeters", 99999, out Variant lodDistance) || !Mathf.IsEqualApprox((float)lodDistance.AsDouble(), 200)) throw new InvalidOperationException("near tree LOD distance was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("TreeBillboardFadeEndMeters", 99999, out lodDistance) || !Mathf.IsEqualApprox((float)lodDistance.AsDouble(), 2000)) throw new InvalidOperationException("billboard tree LOD distance was not clamped");
        if (GameplayVegetationDefaults.TryValidate("TreeBillboardFadeEndMeters", double.NaN, out _)) throw new InvalidOperationException("nonfinite tree LOD distance accepted");
        if (!GameplayVegetationDefaults.TryValidate("TreeQuadStartMeters", 99999, out lodDistance) || !Mathf.IsEqualApprox((float)lodDistance.AsDouble(), 5000)) throw new InvalidOperationException("far billboard start was not clamped");
        if (GameplayVegetationDefaults.TryValidate("TreeQuadStartMeters", double.NaN, out _)) throw new InvalidOperationException("nonfinite far billboard start accepted");
        var quadStart = new GameplayVegetationDefaults(path); quadStart.Load();
        if (!Mathf.IsEqualApprox(quadStart.TreeQuadStartMeters, 900)) throw new InvalidOperationException("far billboard start default is not the preset's 900 m");
        quadStart.Preset = RingTerrainLodCoordinator.QualityPreset.Low; if (!Mathf.IsEqualApprox(quadStart.TreeQuadStartMeters, 600)) throw new InvalidOperationException("far billboard start did not follow the Low preset");
        quadStart.Preset = RingTerrainLodCoordinator.QualityPreset.UltraVideo; if (!Mathf.IsEqualApprox(quadStart.TreeQuadStartMeters, 1200)) throw new InvalidOperationException("far billboard start did not follow the Ultra Video preset");
        quadStart.Set("TreeQuadStartMeters", 1500); if (!Mathf.IsEqualApprox(quadStart.TreeQuadStartMeters, 1500)) throw new InvalidOperationException("far billboard start override did not stick");
        if (!GameplayVegetationDefaults.TryValidate("TreeMidDetailFadeEndMeters", 99999, out lodDistance) || !Mathf.IsEqualApprox((float)lodDistance.AsDouble(), 2000)) throw new InvalidOperationException("reduced-detail fade end was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("TreeMidDetailFadeStartMeters", -5, out lodDistance) || !Mathf.IsEqualApprox((float)lodDistance.AsDouble(), 0)) throw new InvalidOperationException("reduced-detail fade start low range was not clamped");
        if (GameplayVegetationDefaults.TryValidate("TreeMidDetailFadeStartMeters", double.NaN, out _)) throw new InvalidOperationException("nonfinite reduced-detail fade start accepted");
        var midFade = new GameplayVegetationDefaults(path); midFade.Load();
        if (!Mathf.IsEqualApprox(midFade.TreeMidDetailFadeStartMeters, 130) || !Mathf.IsEqualApprox(midFade.TreeMidDetailFadeEndMeters, 150)) throw new InvalidOperationException("reduced-detail fade default is not the preset's 130-150 m");
        midFade.Preset = RingTerrainLodCoordinator.QualityPreset.Low; if (!Mathf.IsEqualApprox(midFade.TreeMidDetailFadeStartMeters, 80) || !Mathf.IsEqualApprox(midFade.TreeMidDetailFadeEndMeters, 100)) throw new InvalidOperationException("reduced-detail fade did not follow the Low preset");
        midFade.Preset = RingTerrainLodCoordinator.QualityPreset.Medium; if (!Mathf.IsEqualApprox(midFade.TreeMidDetailFadeStartMeters, 110) || !Mathf.IsEqualApprox(midFade.TreeMidDetailFadeEndMeters, 130)) throw new InvalidOperationException("reduced-detail fade did not follow the Medium preset");
        midFade.Preset = RingTerrainLodCoordinator.QualityPreset.UltraVideo; if (!Mathf.IsEqualApprox(midFade.TreeMidDetailFadeStartMeters, 200) || !Mathf.IsEqualApprox(midFade.TreeMidDetailFadeEndMeters, 240)) throw new InvalidOperationException("reduced-detail fade did not follow the Ultra Video preset");
        midFade.Set("TreeMidDetailFadeEndMeters", 260); if (!Mathf.IsEqualApprox(midFade.TreeMidDetailFadeEndMeters, 260)) throw new InvalidOperationException("reduced-detail fade override did not stick");
        var render = new RingBiomeDecorationLayer();
        try { if (!render.UseTreeRenderLods || !Mathf.IsEqualApprox(render.TreeMidDetailFadeStartMeters, 130) || !Mathf.IsEqualApprox(render.TreeMidDetailFadeEndMeters, 150)) throw new InvalidOperationException("layer render-LOD defaults differ from the High preset"); }
        finally { render.Free(); }
        if (!GameplayVegetationDefaults.TryValidate("MidTreeBatchTileSpan", 99, out Variant tileSpan) || tileSpan.AsInt64() != 4) throw new InvalidOperationException("mid-tree batch span was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("MidTreeBatchTileSpan", -1, out tileSpan) || tileSpan.AsInt64() != 1) throw new InvalidOperationException("mid-tree batch span low range was not clamped");
        // T5-9 rock scatter keys: clamp, nonfinite, preset fallbacks, save round trip, apply.
        if (!GameplayVegetationDefaults.TryValidate("RockDensity", 7, out Variant density) || !Mathf.IsEqualApprox((float)density.AsDouble(), 1)) throw new InvalidOperationException("rock density high range was not clamped to 1");
        if (!GameplayVegetationDefaults.TryValidate("RockDensity", -3, out density) || !Mathf.IsEqualApprox((float)density.AsDouble(), 0)) throw new InvalidOperationException("rock density low range was not clamped to 0");
        if (GameplayVegetationDefaults.TryValidate("RockDensity", double.NaN, out _) || GameplayVegetationDefaults.TryValidate("RockDensity", "bad", out _)) throw new InvalidOperationException("invalid rock density accepted");
        if (!GameplayVegetationDefaults.TryValidate("ScatterDistanceScale", 9, out Variant scale) || !Mathf.IsEqualApprox((float)scale.AsDouble(), 1.5f)) throw new InvalidOperationException("scatter distance scale high range was not clamped to 1.5");
        if (!GameplayVegetationDefaults.TryValidate("ScatterDistanceScale", 0.1, out scale) || !Mathf.IsEqualApprox((float)scale.AsDouble(), 0.5f)) throw new InvalidOperationException("scatter distance scale low range was not clamped to 0.5");
        if (GameplayVegetationDefaults.TryValidate("ScatterDistanceScale", double.PositiveInfinity, out _)) throw new InvalidOperationException("nonfinite scatter distance scale accepted");
        var scatter = new GameplayVegetationDefaults(path); scatter.Load();
        (RingTerrainLodCoordinator.QualityPreset Preset, float Density, float Scale, float Cap)[] presets =
        {
            (RingTerrainLodCoordinator.QualityPreset.Low, 0.6f, 0.6f, 0.5f), (RingTerrainLodCoordinator.QualityPreset.Medium, 0.8f, 0.8f, 0.75f), (RingTerrainLodCoordinator.QualityPreset.High, 1f, 1f, 1f),
            (RingTerrainLodCoordinator.QualityPreset.VeryHigh, 1f, 1f, 1f), (RingTerrainLodCoordinator.QualityPreset.UltraVideo, 1f, 1.25f, 1.5f), (RingTerrainLodCoordinator.QualityPreset.Cinematic5090, 1f, 1.25f, 1.5f),
        };
        foreach (var row in presets)
        {
            scatter.Preset = row.Preset;
            if (!Mathf.IsEqualApprox(scatter.RockDensity, row.Density) || !Mathf.IsEqualApprox(scatter.ScatterDistanceScale, row.Scale) || !Mathf.IsEqualApprox(scatter.ScatterCapScale, row.Cap))
                throw new InvalidOperationException($"rock scatter defaults did not follow the {row.Preset} preset");
        }
        scatter.Preset = RingTerrainLodCoordinator.QualityPreset.Low;
        scatter.Set("RockDensity", 0.35); scatter.Set("ScatterDistanceScale", 1.4);
        if (!Mathf.IsEqualApprox(scatter.RockDensity, 0.35f) || !Mathf.IsEqualApprox(scatter.ScatterDistanceScale, 1.4f) || !Mathf.IsEqualApprox(scatter.ScatterCapScale, 0.5f)) throw new InvalidOperationException("rock scatter overrides did not win over the preset (the cap scale always follows it)");
        if (!scatter.Save()) throw new InvalidOperationException("rock scatter settings did not save");
        var scatterBack = new GameplayVegetationDefaults(path) { Preset = RingTerrainLodCoordinator.QualityPreset.UltraVideo }; scatterBack.Load();
        if (!Mathf.IsEqualApprox(scatterBack.RockDensity, 0.35f) || !Mathf.IsEqualApprox(scatterBack.ScatterDistanceScale, 1.4f)) throw new InvalidOperationException("rock scatter settings did not round-trip");
        scatterBack.Reset(); scatterBack.Save();
        var layer = new RingBiomeDecorationLayer();
        try
        {
            if (!Mathf.IsEqualApprox(layer.ScatterRockDensity, 1) || !Mathf.IsEqualApprox(layer.ScatterDistanceScale, 1) || !Mathf.IsEqualApprox(layer.ScatterCapScale, 1)) throw new InvalidOperationException("decoration layer scatter defaults differ from the High preset");
            layer.ApplyScatterSettings(0.35f, 1.4f, 0.5f);
            if (!Mathf.IsEqualApprox(layer.ScatterRockDensity, 0.35f) || !Mathf.IsEqualApprox(layer.ScatterDistanceScale, 1.4f) || !Mathf.IsEqualApprox(layer.ScatterCapScale, 0.5f)) throw new InvalidOperationException("scatter settings were not applied to the decoration layer");
        }
        finally { layer.Free(); }
        // T6-8 forest keys: clamp, nonfinite, defaults (no preset dependence), save round trip, apply.
        foreach (string forestKey in new[] { "FoliageColourVariation", "GroundCoverShare" })
        {
            if (!GameplayVegetationDefaults.TryValidate(forestKey, 7, out Variant forestHigh) || !Mathf.IsEqualApprox((float)forestHigh.AsDouble(), 1)) throw new InvalidOperationException(forestKey + " high range was not clamped to 1");
            if (!GameplayVegetationDefaults.TryValidate(forestKey, -3, out Variant forestLow) || !Mathf.IsEqualApprox((float)forestLow.AsDouble(), 0)) throw new InvalidOperationException(forestKey + " low range was not clamped to 0");
            if (GameplayVegetationDefaults.TryValidate(forestKey, double.NaN, out _) || GameplayVegetationDefaults.TryValidate(forestKey, "bad", out _)) throw new InvalidOperationException("invalid " + forestKey + " accepted");
        }
        var forest = new GameplayVegetationDefaults(path); forest.Load();
        foreach (var preset in new[] { RingTerrainLodCoordinator.QualityPreset.Low, RingTerrainLodCoordinator.QualityPreset.Medium, RingTerrainLodCoordinator.QualityPreset.Cinematic5090 })
        {
            forest.Preset = preset;
            if (!Mathf.IsEqualApprox(forest.FoliageColourVariation, 1) || !Mathf.IsEqualApprox(forest.GroundCoverShare, 1)) throw new InvalidOperationException($"forest defaults are not 1 (or follow the {preset} preset)");
        }
        forest.Set("FoliageColourVariation", 0.4); forest.Set("GroundCoverShare", 0.65);
        if (!Mathf.IsEqualApprox(forest.FoliageColourVariation, 0.4f) || !Mathf.IsEqualApprox(forest.GroundCoverShare, 0.65f)) throw new InvalidOperationException("forest overrides did not stick");
        if (!forest.Save()) throw new InvalidOperationException("forest settings did not save");
        var forestBack = new GameplayVegetationDefaults(path); forestBack.Load();
        if (!Mathf.IsEqualApprox(forestBack.FoliageColourVariation, 0.4f) || !Mathf.IsEqualApprox(forestBack.GroundCoverShare, 0.65f)) throw new InvalidOperationException("forest settings did not round-trip");
        forestBack.Reset(); forestBack.Save();
        var forestLayer = new RingBiomeDecorationLayer();
        try
        {
            if (!Mathf.IsEqualApprox(forestLayer.FoliageColourVariation, 1)) throw new InvalidOperationException("decoration layer foliage colour variation default is not 1");
            forestLayer.ApplyFoliageSettings(0.4f);
            if (!Mathf.IsEqualApprox(forestLayer.FoliageColourVariation, 0.4f)) throw new InvalidOperationException("foliage colour variation was not applied to the decoration layer");
            forestLayer.ApplyFoliageSettings(3f);
            if (!Mathf.IsEqualApprox(forestLayer.FoliageColourVariation, 1)) throw new InvalidOperationException("foliage colour variation above 1 was not clamped on apply");
        }
        finally { forestLayer.Free(); }
        // T6-6 dead-wood keys: the rank gates of the understory and dead-wood batches (clamp, nonfinite, default 1 at every preset, round trip, apply).
        foreach (string gateKey in new[] { "UnderstoryDensity", "DeadWoodDensity" })
        {
            if (!GameplayVegetationDefaults.TryValidate(gateKey, 7, out Variant gateHigh) || !Mathf.IsEqualApprox((float)gateHigh.AsDouble(), 1)) throw new InvalidOperationException(gateKey + " high range was not clamped to 1");
            if (!GameplayVegetationDefaults.TryValidate(gateKey, -3, out Variant gateLow) || !Mathf.IsEqualApprox((float)gateLow.AsDouble(), 0)) throw new InvalidOperationException(gateKey + " low range was not clamped to 0");
            if (GameplayVegetationDefaults.TryValidate(gateKey, double.NaN, out _) || GameplayVegetationDefaults.TryValidate(gateKey, "bad", out _)) throw new InvalidOperationException("invalid " + gateKey + " accepted");
        }
        var gates = new GameplayVegetationDefaults(path); gates.Load();
        foreach (var preset in new[] { RingTerrainLodCoordinator.QualityPreset.Low, RingTerrainLodCoordinator.QualityPreset.Medium, RingTerrainLodCoordinator.QualityPreset.Cinematic5090 })
        {
            gates.Preset = preset;
            if (!Mathf.IsEqualApprox(gates.UnderstoryDensity, 1) || !Mathf.IsEqualApprox(gates.DeadWoodDensity, 1)) throw new InvalidOperationException($"understory and dead wood defaults are not 1 at the {preset} preset");
        }
        // T6 sapling shadows follow the preset only: off at Low, Medium and High, on at Very High and up.
        foreach (var preset in new[] { RingTerrainLodCoordinator.QualityPreset.Low, RingTerrainLodCoordinator.QualityPreset.Medium, RingTerrainLodCoordinator.QualityPreset.High, RingTerrainLodCoordinator.QualityPreset.VeryHigh, RingTerrainLodCoordinator.QualityPreset.UltraVideo, RingTerrainLodCoordinator.QualityPreset.Cinematic5090 })
        {
            gates.Preset = preset;
            bool expected = preset >= RingTerrainLodCoordinator.QualityPreset.VeryHigh;
            if (gates.SaplingShadows != expected || RingGraphicsQualityController.PresetSaplingShadows(preset) != expected) throw new InvalidOperationException($"sapling shadows are {(expected ? "off" : "on")} at the {preset} preset");
        }
        gates.Set("UnderstoryDensity", 0.45); gates.Set("DeadWoodDensity", 0.2);
        if (!gates.Save()) throw new InvalidOperationException("understory settings did not save");
        var gatesBack = new GameplayVegetationDefaults(path); gatesBack.Load();
        if (!Mathf.IsEqualApprox(gatesBack.UnderstoryDensity, 0.45f) || !Mathf.IsEqualApprox(gatesBack.DeadWoodDensity, 0.2f)) throw new InvalidOperationException("understory settings did not round-trip");
        gatesBack.Reset(); gatesBack.Save();
        var gateLayer = new RingBiomeDecorationLayer();
        try
        {
            if (!Mathf.IsEqualApprox(gateLayer.UnderstoryDensity, 1) || !Mathf.IsEqualApprox(gateLayer.DeadWoodDensity, 1)) throw new InvalidOperationException("decoration layer understory gate defaults are not 1");
            gateLayer.ApplyUnderstorySettings(0.45f, 0.2f);
            if (!Mathf.IsEqualApprox(gateLayer.UnderstoryDensity, 0.45f) || !Mathf.IsEqualApprox(gateLayer.DeadWoodDensity, 0.2f)) throw new InvalidOperationException("understory settings were not applied to the decoration layer");
            gateLayer.ApplyUnderstorySettings(3f, -1f);
            if (!Mathf.IsEqualApprox(gateLayer.UnderstoryDensity, 1) || !Mathf.IsEqualApprox(gateLayer.DeadWoodDensity, 0)) throw new InvalidOperationException("understory gates were not clamped on apply");
        }
        finally { gateLayer.Free(); }
        var saplingLayer = new RingBiomeDecorationLayer();
        try
        {
            if (saplingLayer.UnderstorySaplingShadows) throw new InvalidOperationException("decoration layer sapling shadows default is not off");
            saplingLayer.ApplyUnderstorySettings(1f, 1f, true);
            if (!saplingLayer.UnderstorySaplingShadows) throw new InvalidOperationException("sapling shadows were not applied to the decoration layer");
            saplingLayer.ApplyUnderstorySettings(1f, 1f, false);
            if (saplingLayer.UnderstorySaplingShadows) throw new InvalidOperationException("sapling shadows were not cleared on apply");
        }
        finally { saplingLayer.Free(); }
        // The static the ground-cover selection reads (the drawer's apply writes it): clamped to 0-1, default 1, Revision counts every change.
        RingGroundCoverSettings.Share = 1f;
        int revision = RingGroundCoverSettings.Revision;
        RingGroundCoverSettings.Share = 1f;
        if (RingGroundCoverSettings.Revision != revision) throw new InvalidOperationException("ground cover revision changed without a change");
        RingGroundCoverSettings.Share = 0.65f;
        if (!Mathf.IsEqualApprox(RingGroundCoverSettings.Share, 0.65f) || RingGroundCoverSettings.Revision != revision + 1) throw new InvalidOperationException("ground cover share was not applied or its revision did not increment");
        RingGroundCoverSettings.Share = 9f;
        if (!Mathf.IsEqualApprox(RingGroundCoverSettings.Share, 1f) || RingGroundCoverSettings.Revision != revision + 2) throw new InvalidOperationException("ground cover share above 1 was not clamped");
        RingGroundCoverSettings.Share = -1f;
        if (!Mathf.IsEqualApprox(RingGroundCoverSettings.Share, 0f)) throw new InvalidOperationException("ground cover share below 0 was not clamped");
        RingGroundCoverSettings.Share = 1f;
        // The shader global: z density and w distance scale, x and y kept from the last world publish; a world publish clears the look again (worlds without the bit never set it).
        RingGroundSurfaceGlobals.PublishWorld(null);
        Vector4 plain = RingGroundSurfaceGlobals.Published;
        if (plain.Z != 0 || plain.W != 0) throw new InvalidOperationException("a world publish did not leave the scatter look at 0");
        RingGroundSurfaceGlobals.PublishScatterLook(0.6f, 0.75f);
        Vector4 lookValue = RingGroundSurfaceGlobals.Published;
        if (lookValue.X != plain.X || lookValue.Y != plain.Y || !Mathf.IsEqualApprox(lookValue.Z, 0.6f) || !Mathf.IsEqualApprox(lookValue.W, 0.75f)) throw new InvalidOperationException("the scatter look did not reach the ground world global (z density, w distance scale)");
        RingGroundSurfaceGlobals.PublishWorld(null);
        if (RingGroundSurfaceGlobals.Published != plain) throw new InvalidOperationException("a world publish did not restore (x, y, 0, 0)");
        var invalidPath = new GameplayVegetationDefaults("res://"); invalidPath.Set("UseGpuCulling", false);
        if (invalidPath.Save() || invalidPath.LastError.Length == 0) throw new InvalidOperationException("invalid vegetation save path did not fail safely");
    }
}
