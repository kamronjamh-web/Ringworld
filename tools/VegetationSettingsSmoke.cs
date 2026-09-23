using System;
using Godot;
using RingworldTechDemo.UI;

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
        if (!GameplayVegetationDefaults.TryValidate("MidTreeBatchTileSpan", 99, out Variant tileSpan) || tileSpan.AsInt64() != 4) throw new InvalidOperationException("mid-tree batch span was not clamped");
        if (!GameplayVegetationDefaults.TryValidate("MidTreeBatchTileSpan", -1, out tileSpan) || tileSpan.AsInt64() != 1) throw new InvalidOperationException("mid-tree batch span low range was not clamped");
        var invalidPath = new GameplayVegetationDefaults("res://"); invalidPath.Set("UseGpuCulling", false);
        if (invalidPath.Save() || invalidPath.LastError.Length == 0) throw new InvalidOperationException("invalid vegetation save path did not fail safely");
    }
}
