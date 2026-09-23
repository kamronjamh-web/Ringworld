using System;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only contract for bounded directional-shadow overrides and their sparse persistence.</summary>
[GlobalClass]
public partial class ShadowSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string path)
    {
        bool supportedAtlas = RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 8192, out Variant atlas) && atlas.AsInt32() == 8192;
        bool expandedAtlases = RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 32768, out Variant atlas32) && atlas32.AsInt32() == 32768
            && RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 65536, out Variant atlas64) && atlas64.AsInt32() == 65536
            && RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 131072, out Variant atlas128) && atlas128.AsInt32() == 131072;
        bool rejectedAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 6144, out _);
        bool rejectedFractionalAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 32768.5f, out _);
        bool rejectedPreciseFractionalAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 32768.0001d, out _);
        bool rejectedNanAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", float.NaN, out _);
        bool rejectedOversizedAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", 2147483648L, out _);
        bool rejectedNegativeAtlas = !RingGraphicsQualityController.TryValidateShadowOverride("AtlasSize", -32768, out _);
        bool resolver = RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(131072, 32768) == 32768
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(131072, 16384) == 16384
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(131072, 8192) == 8192
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(131072, 24576) == 16384
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(32768, 16384) == 16384
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(2048, 1024) == 1024
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(2048, 0) == 0
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(0, 32768) == 0
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasSize(512, 32768) == 512
            && RingGraphicsQualityController.ResolveDirectionalShadowAtlasHardwareLimit(32768, 16384, 32768) == 16384;
        bool memoryEstimates = RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(32768) == 4UL * 1024UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(65536) == 16UL * 1024UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(131072) == 64UL * 1024UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(8192, 16) == 128UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(8192, 32) == 256UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(16384, 16) == 512UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(32768, 16) == 2UL * 1024UL * 1024UL * 1024UL
            && RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(131072, 16) == 32UL * 1024UL * 1024UL * 1024UL;
        bool depthValidation = RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", 16, out Variant depth16) && depth16.AsInt32() == 16
            && RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", 32, out Variant depth32) && depth32.AsInt32() == 32
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", 24, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", 16.0001d, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", double.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", double.PositiveInfinity, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", long.MaxValue, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("DepthBits", true, out _);
        bool nearValidation = RingGraphicsQualityController.TryValidateShadowOverride("NearCascadeMeters", 60f, out Variant near60) && Mathf.IsEqualApprox(near60.AsSingle(), 60f)
            && RingGraphicsQualityController.TryValidateShadowOverride("NearCascadeMeters", 1f, out Variant nearMin) && Mathf.IsEqualApprox(nearMin.AsSingle(), RingGraphicsQualityController.MinimumNearCascadeMeters)
            && !RingGraphicsQualityController.TryValidateShadowOverride("NearCascadeMeters", float.NaN, out _);
        bool supportedFilter = RingGraphicsQualityController.TryValidateShadowOverride("Filter", 3, out Variant filter) && filter.AsInt32() == 3;
        bool rejectedFilter = !RingGraphicsQualityController.TryValidateShadowOverride("Filter", 6, out _);
        bool rejectedDistance = !RingGraphicsQualityController.TryValidateShadowOverride("DistanceMeters", 99000f, out _);
        bool rejectedNaN = !RingGraphicsQualityController.TryValidateShadowOverride("Split1", float.NaN, out _);
        bool stabilizationEnabled = RingGraphicsQualityController.TryValidateShadowOverride("SunStabilizationEnabled", true, out Variant stabilize) && stabilize.AsBool();
        bool rejectedStabilizationType = !RingGraphicsQualityController.TryValidateShadowOverride("SunStabilizationEnabled", .01f, out _);
        bool stepRange = RingGraphicsQualityController.TryValidateShadowOverride("SunStepDegrees", .01f, out Variant step) && Mathf.IsEqualApprox(step.AsSingle(), .01f);
        bool stepClamped = RingGraphicsQualityController.TryValidateShadowOverride("SunStepDegrees", .051f, out Variant clampedStep) && Mathf.IsEqualApprox(clampedStep.AsSingle(), .05f);
        bool softnessRange = RingGraphicsQualityController.TryValidateShadowOverride("SunAngularDistance", 2.5f, out Variant softness) && Mathf.IsEqualApprox(softness.AsSingle(), 2.5f);
        bool rejectedSoftness = !RingGraphicsQualityController.TryValidateShadowOverride("SunAngularDistance", float.NaN, out _);
        var controller = new RingGraphicsQualityController();
        var sun = new DirectionalLight3D { ShadowEnabled = true, ShadowBias = .17f, ShadowNormalBias = 2.3f };
        controller.AddChild(sun);
        controller.SunLightPath = new NodePath("Sun");
        sun.Name = "Sun";
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) throw new InvalidOperationException("Shadow smoke requires an active SceneTree root.");
        tree.Root.AddChild(controller);
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        bool defaultNearFocus = Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, RingGraphicsQualityController.DefaultNearCascadeMeters)
            && Mathf.IsEqualApprox(controller.EffectiveShadowDistanceMeters, 14000f)
            && controller.EffectiveNearCascadeMeters < controller.EffectiveShadowDistanceMeters;
        bool formatToggleSameAtlas = controller.EffectiveDirectionalShadowDepthBits == 16
            && controller.SetShadowTuningOverride("DepthBits", 32)
            && controller.EffectiveDirectionalShadowAtlasSize == 8192
            && controller.EffectiveDirectionalShadowDepthBits == 32
            && controller.EffectiveDirectionalShadowAtlasMinimumDepthBytes == RingGraphicsQualityController.DirectionalShadowAtlasMinimumDepthBytes(8192, 32)
            && controller.SetShadowTuningOverride("DepthBits", 16)
            && controller.EffectiveDirectionalShadowAtlasSize == 8192
            && controller.EffectiveDirectionalShadowDepthBits == 16;
        bool directSwitching = controller.SetShadowTuningOverride("Split1", .18f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .18f)
            && controller.SetShadowTuningOverride("NearCascadeMeters", 70f)
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 70f)
            && controller.SetShadowTuningOverride("Split1", .19f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .19f)
            && controller.SetShadowTuningOverride("NearCascadeMeters", 70f)
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 70f);
        var mixed = new Godot.Collections.Dictionary { ["Split1"] = .30f, ["NearCascadeMeters"] = 80f };
        controller.SetShadowTuningOverrides(mixed);
        bool mixedPrecedence = Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 80f);
        var reverseMixed = new Godot.Collections.Dictionary { ["NearCascadeMeters"] = 80f, ["Split1"] = .30f };
        controller.SetShadowTuningOverrides(reverseMixed);
        mixedPrecedence &= Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 80f);
        controller.SetShadowTuningOverride("NearCascadeMeters", RingGraphicsQualityController.MaximumNearCascadeMeters);
        bool upperNearClamped = Mathf.IsEqualApprox(controller.EffectiveShadowDistanceMeters, 14000f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .40f)
            && sun.DirectionalShadowSplit2 >= sun.DirectionalShadowSplit1 + .01f
            && sun.DirectionalShadowSplit3 >= sun.DirectionalShadowSplit2 + .01f;
        controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary());
        int directRequestedBeforeUnsupported = controller.RequestedDirectionalShadowAtlasSize;
        bool directUnsupportedRejected = !controller.SetShadowTuningOverride("AtlasSize", 65536)
            && controller.RequestedDirectionalShadowAtlasSize == directRequestedBeforeUnsupported;
        var settings = new GameplayShadowDefaults(path);
        settings.Initialize(controller);
        bool set = settings.SetOverride(controller, "AtlasSize", 4096) && settings.SetOverride(controller, "DepthBits", 32) && settings.SetOverride(controller, "Split1", .18f);
        bool legacyRatio = controller.EffectiveDirectionalShadowAtlasSize == 4096 && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .18f);
        bool nearWins = settings.SetOverride(controller, "NearCascadeMeters", 75f)
            && !settings.Settings.Overrides.ContainsKey("Split1")
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 75f);
        bool settingsSwitching = settings.SetOverride(controller, "Split1", .19f)
            && !settings.Settings.Overrides.ContainsKey("NearCascadeMeters")
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .19f)
            && settings.SetOverride(controller, "NearCascadeMeters", 75f)
            && !settings.Settings.Overrides.ContainsKey("Split1")
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 75f);
        bool effective = legacyRatio && nearWins && settingsSwitching;
        bool saved = settings.Save();
        var reloaded = new RingGraphicsQualityController();
        var reloadedSun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
        reloaded.AddChild(reloadedSun);
        reloaded.SunLightPath = new NodePath("Sun");
        tree.Root.AddChild(reloaded);
        reloaded.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        var loaded = new GameplayShadowDefaults(path);
        loaded.Initialize(reloaded);
        bool roundTrip = reloaded.EffectiveDirectionalShadowAtlasSize == 4096
            && reloaded.EffectiveDirectionalShadowDepthBits == 32
            && Mathf.IsEqualApprox(reloaded.EffectiveNearCascadeMeters, 75f)
            && loaded.Settings.Overrides.ContainsKey("DepthBits")
            && loaded.Settings.Overrides.ContainsKey("NearCascadeMeters");
        bool unsupportedPreferenceRejected = !loaded.SetOverride(reloaded, "AtlasSize", 65536)
            && !loaded.Dirty
            && loaded.Settings.Overrides["AtlasSize"].AsInt32() == 4096
            && reloaded.RequestedDirectionalShadowAtlasSize == 4096;
        var legacyPrefs = new GameplayShadowDefaults(path);
        legacyPrefs.Settings.Overrides["Split1"] = .18f;
        bool legacySaved = ResourceSaver.Save(legacyPrefs.Settings, path) == Error.Ok;
        var legacyController = new RingGraphicsQualityController();
        var legacySun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
        legacyController.AddChild(legacySun);
        legacyController.SunLightPath = new NodePath("Sun");
        tree.Root.AddChild(legacyController);
        legacyController.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        var legacyLoaded = new GameplayShadowDefaults(path);
        legacyLoaded.Initialize(legacyController);
        bool legacyLoadCompatibility = legacySaved
            && legacyLoaded.Settings.Overrides.ContainsKey("Split1")
            && !legacyLoaded.Settings.Overrides.ContainsKey("NearCascadeMeters")
            && Mathf.IsEqualApprox(legacySun.DirectionalShadowSplit1, .18f);
        var portableHigh = new GameplayShadowDefaults(path);
        portableHigh.Settings.Overrides["AtlasSize"] = 131072;
        bool highSaved = ResourceSaver.Save(portableHigh.Settings, path) == Error.Ok;
        var highReloadedController = new RingGraphicsQualityController();
        var highReloaded = new GameplayShadowDefaults(path);
        highReloaded.Initialize(highReloadedController);
        var highOnDisk = ResourceLoader.Load<GameplayShadowSettings>(path, cacheMode: ResourceLoader.CacheMode.Ignore);
        bool portableHighClamped = highReloaded.Settings.Overrides.TryGetValue("AtlasSize", out Variant persistedHigh)
            && persistedHigh.AsInt32() == 131072
            && highOnDisk?.Overrides["AtlasSize"].AsInt32() == 131072
            && highReloadedController.RequestedDirectionalShadowAtlasSize == 131072
            && highReloadedController.EffectiveDirectionalShadowAtlasSize == RingGraphicsQualityController.UnverifiedDirectionalShadowAtlasLimit
            && !highReloaded.Dirty;
        settings.SetOverride(controller, "Bias", .62f);
        settings.SetOverride(controller, "NormalBias", .71f);
        settings.SetOverride(controller, "Enabled", false);
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, applyTerrainPreset: false);
        bool survivesPreset = controller.EffectiveDirectionalShadowAtlasSize == 4096
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 75f)
            && sun.DirectionalShadowSplit2 >= sun.DirectionalShadowSplit1 + .01f
            && !sun.ShadowEnabled;
        settings.Reset(controller);
        bool reset = controller.EffectiveDirectionalShadowAtlasSize == 8192
            && controller.EffectiveDirectionalShadowDepthBits == 16
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, RingGraphicsQualityController.DefaultNearCascadeMeters)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit2, .06f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit3, .28f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .88f)
            && Mathf.IsEqualApprox(sun.ShadowBias, .17f)
            && Mathf.IsEqualApprox(sun.ShadowNormalBias, 2.3f)
            && sun.ShadowEnabled;
        var result = new Godot.Collections.Dictionary();
        result["pass"] = supportedAtlas && expandedAtlases && rejectedAtlas && rejectedFractionalAtlas && rejectedPreciseFractionalAtlas && rejectedNanAtlas && rejectedOversizedAtlas && rejectedNegativeAtlas && resolver && memoryEstimates && depthValidation && nearValidation && supportedFilter && rejectedFilter && rejectedDistance && rejectedNaN && stabilizationEnabled && rejectedStabilizationType && stepRange && stepClamped && softnessRange && rejectedSoftness && defaultNearFocus && formatToggleSameAtlas && directSwitching && mixedPrecedence && upperNearClamped && directUnsupportedRejected && unsupportedPreferenceRejected && set && effective && survivesPreset && reset && saved && roundTrip && legacyLoadCompatibility && highSaved && portableHighClamped;
        result["unsupportedPreferenceRejected"] = unsupportedPreferenceRejected;
        result["detail"] = $"atlas={supportedAtlas}/{expandedAtlases}/{rejectedAtlas}/{rejectedFractionalAtlas}/{rejectedPreciseFractionalAtlas}/{rejectedNanAtlas}/{rejectedOversizedAtlas}/{rejectedNegativeAtlas} resolver={resolver} memory={memoryEstimates} depth={depthValidation}/{formatToggleSameAtlas} near={nearValidation}/{defaultNearFocus}/{directSwitching}/{mixedPrecedence}/{upperNearClamped} filter={supportedFilter}/{rejectedFilter} rangeRejected={rejectedDistance} nan={rejectedNaN} stabilization={stabilizationEnabled}/{rejectedStabilizationType} step={stepRange}/{stepClamped} softness={softnessRange}/{rejectedSoftness} directUnsupportedRejected={directUnsupportedRejected} set={set} effective={effective} survivesPreset={survivesPreset} reset={reset} saved={saved} roundTrip={roundTrip} legacyLoad={legacyLoadCompatibility} highSaved={highSaved} portableHighClamped={portableHighClamped}; fixture path={path}";
        tree.Root.RemoveChild(controller);
        controller.Free();
        tree.Root.RemoveChild(reloaded);
        reloaded.Free();
        tree.Root.RemoveChild(legacyController);
        legacyController.Free();
        highReloadedController.Free();
        return result;
    }
}
