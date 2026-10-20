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
        bool handoffValidation = RingGraphicsQualityController.TryValidateShadowOverride("LocalShadowHandoffEnabled", false, out Variant handoffDisabled) && !handoffDisabled.AsBool()
            && RingGraphicsQualityController.TryValidateShadowOverride("SunShadowOffElevationDegrees", -22f, out Variant handoffOff) && Mathf.IsEqualApprox(handoffOff.AsSingle(), -22f)
            && RingGraphicsQualityController.TryValidateShadowOverride("SunShadowOnElevationDegrees", -20f, out Variant handoffOn) && Mathf.IsEqualApprox(handoffOn.AsSingle(), -20f)
            && !RingGraphicsQualityController.TryValidateShadowOverride("LocalShadowHandoffEnabled", 0, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("SunShadowOffElevationDegrees", float.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("SunShadowOnElevationDegrees", float.PositiveInfinity, out _);
        bool debugModeValidation = true;
        for (int mode = 0; mode <= 5; mode++)
            debugModeValidation &= RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", mode, out Variant debugMode) && debugMode.AsInt32() == mode;
        debugModeValidation = debugModeValidation
            && !RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", -1, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", 6, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", 2.5f, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", float.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("ShadowDebugMode", true, out _);
        bool terrainMacroValidation = RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroEnabled", true, out Variant macroEnabled) && macroEnabled.AsBool()
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroWallShadowsEnabled", true, out Variant wallShadows) && wallShadows.AsBool()
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroStrength", .42f, out Variant macroStrength) && Mathf.IsEqualApprox(macroStrength.AsSingle(), .42f)
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroTraceSteps", 13.6f, out Variant macroSteps) && macroSteps.AsInt32() == 14
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroMaxTraceMeters", 128000f, out Variant macroDistance) && Mathf.IsEqualApprox(macroDistance.AsSingle(), 128000f)
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroFadeStartMeters", 4200f, out Variant macroNear) && Mathf.IsEqualApprox(macroNear.AsSingle(), 4200f)
            && RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroFadeEndMeters", 8200f, out Variant macroFar) && Mathf.IsEqualApprox(macroFar.AsSingle(), 8200f)
            && !RingGraphicsQualityController.TryValidateShadowOverride("TerrainMacroTraceSteps", float.NaN, out _);
        var controller = new RingGraphicsQualityController();
        var sun = new DirectionalLight3D { ShadowEnabled = true, ShadowBias = .17f, ShadowNormalBias = 2.3f };
        controller.AddChild(sun);
        controller.SunLightPath = new NodePath("Sun");
        sun.Name = "Sun";
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) throw new InvalidOperationException("Shadow smoke requires an active SceneTree root.");
        var globalLod = new RingTerrainGlobalLod
        {
            Name = "GlobalTerrainLod3",
            EnableMacroTerrainShadows = true,
            EnableMacroTerrainWallShadows = false
        };
        tree.Root.AddChild(globalLod);
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
        bool set = settings.SetOverride(controller, "AtlasSize", 4096) && settings.SetOverride(controller, "DepthBits", 32) && settings.SetOverride(controller, "Split1", .18f)
            && settings.SetOverride(controller, "LocalShadowHandoffEnabled", false) && settings.SetOverride(controller, "SunShadowOffElevationDegrees", -22f) && settings.SetOverride(controller, "SunShadowOnElevationDegrees", -20f)
            && settings.SetOverride(controller, "TerrainMacroEnabled", false) && settings.SetOverride(controller, "TerrainMacroWallShadowsEnabled", true)
            && settings.SetOverride(controller, "TerrainMacroStrength", .42f) && settings.SetOverride(controller, "TerrainMacroTraceSteps", 14)
            && settings.SetOverride(controller, "TerrainMacroMaxTraceMeters", 128000f) && settings.SetOverride(controller, "TerrainMacroFadeStartMeters", 4200f) && settings.SetOverride(controller, "TerrainMacroFadeEndMeters", 8200f);
        bool debugModeSet = settings.SetOverride(controller, "ShadowDebugMode", 4) && controller.ShadowDebugMode == 4 && !settings.SetOverride(controller, "ShadowDebugMode", 6) && controller.ShadowDebugMode == 4;
        bool legacyRatio = controller.EffectiveDirectionalShadowAtlasSize == 4096 && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .18f);
        bool macroRuntimeApplied = !globalLod.EnableMacroTerrainShadows
            && globalLod.EnableMacroTerrainWallShadows
            && Mathf.IsEqualApprox(globalLod.MacroTerrainShadowStrength, .42f)
            && globalLod.MacroTerrainShadowTraceSteps == 14
            && Mathf.IsEqualApprox(globalLod.MacroTerrainShadowMaxTraceMeters, 128000f)
            && Mathf.IsEqualApprox(globalLod.MacroTerrainShadowFadeStartMeters, 4200f)
            && Mathf.IsEqualApprox(globalLod.MacroTerrainShadowFadeEndMeters, 8200f);
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
            && loaded.Settings.Overrides.ContainsKey("NearCascadeMeters")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroEnabled")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroWallShadowsEnabled")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroStrength")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroTraceSteps")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroMaxTraceMeters")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroFadeStartMeters")
            && loaded.Settings.Overrides.ContainsKey("TerrainMacroFadeEndMeters")
            && loaded.Settings.Overrides.TryGetValue("ShadowDebugMode", out Variant persistedDebugMode) && persistedDebugMode.AsInt32() == 4 && reloaded.ShadowDebugMode == 4
            && loaded.Settings.Overrides.TryGetValue("LocalShadowHandoffEnabled", out Variant persistedHandoff) && !persistedHandoff.AsBool()
            && loaded.Settings.Overrides.TryGetValue("SunShadowOffElevationDegrees", out Variant persistedOff) && Mathf.IsEqualApprox(persistedOff.AsSingle(), -22f)
            && loaded.Settings.Overrides.TryGetValue("SunShadowOnElevationDegrees", out Variant persistedOn) && Mathf.IsEqualApprox(persistedOn.AsSingle(), -20f);
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
            && !legacyLoaded.Settings.Overrides.ContainsKey("ShadowDebugMode") && legacyController.ShadowDebugMode == 0
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
        controller._Process(0);
        bool survivesPreset = controller.EffectiveDirectionalShadowAtlasSize == 4096
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 75f)
            && sun.DirectionalShadowSplit2 >= sun.DirectionalShadowSplit1 + .01f
            && !controller.RequestedSunShadowsEnabled
            && !sun.ShadowEnabled;
        settings.Reset(controller);
        controller._Process(0);
        bool missingInputsKeepsRequestedSun = controller.RequestedSunShadowsEnabled && sun.ShadowEnabled && !controller.LocalSunShadowSuspended;
        bool reset = controller.EffectiveDirectionalShadowAtlasSize == 8192
            && controller.EffectiveDirectionalShadowDepthBits == 16
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, RingGraphicsQualityController.DefaultNearCascadeMeters)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit2, .06f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowSplit3, .28f)
            && Mathf.IsEqualApprox(sun.DirectionalShadowFadeStart, .88f)
            && Mathf.IsEqualApprox(sun.ShadowBias, .17f)
            && Mathf.IsEqualApprox(sun.ShadowNormalBias, 2.3f)
            && sun.ShadowEnabled
            && controller.RequestedSunShadowsEnabled
            && controller.LocalShadowHandoffEnabled
            && controller.ShadowDebugMode == 0
            && missingInputsKeepsRequestedSun
            && globalLod.EnableMacroTerrainShadows
            && !globalLod.EnableMacroTerrainWallShadows;
        // S1: sun map range/cascade count and planet light overrides.
        bool mapKeysValid = RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", 400f, out Variant mapDistance) && Mathf.IsEqualApprox(mapDistance.AsSingle(), 400f)
            && RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", 50f, out Variant mapLow) && Mathf.IsEqualApprox(mapLow.AsSingle(), RingGraphicsQualityController.MinimumShadowDistanceMeters)
            && RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", 99999f, out Variant mapHigh) && Mathf.IsEqualApprox(mapHigh.AsSingle(), RingGraphicsQualityController.MaximumShadowDistanceMeters)
            && RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", 2, out Variant cascades2) && cascades2.AsInt32() == 2
            && RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", 4.0f, out Variant cascades4) && cascades4.AsInt32() == 4
            && RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowsEnabled", false, out Variant planetOff) && !planetOff.AsBool()
            && RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowDistanceMeters", 250f, out Variant planetDistance) && Mathf.IsEqualApprox(planetDistance.AsSingle(), 250f)
            && RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowDistanceMeters", 5f, out Variant planetLow) && Mathf.IsEqualApprox(planetLow.AsSingle(), RingGraphicsQualityController.MinimumPlanetShadowDistanceMeters)
            && RingGraphicsQualityController.TryValidateShadowOverride("PlanetCascadeCount", 2, out Variant planetCascades) && planetCascades.AsInt32() == 2;
        bool mapKeysRejected = !RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", float.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", float.PositiveInfinity, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", true, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("MaxDistanceMeters", "400", out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", 3, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", 2.5f, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", double.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", true, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("CascadeCount", 1, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetCascadeCount", 8, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetCascadeCount", 2.0001d, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowsEnabled", 1, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowsEnabled", "true", out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowDistanceMeters", float.NaN, out _)
            && !RingGraphicsQualityController.TryValidateShadowOverride("PlanetShadowDistanceMeters", false, out _);
        var planetScript = new GDScript { SourceCode = "extends Node\nvar planet_light_cast_shadows := true\nvar planet_light_shadow_distance_meters := 5000.0\nvar planet_light_shadow_cascades := 4\nfunc _update_planet_light() -> void:\n\tpass\n" };
        bool planetScriptOk = planetScript.Reload() == Error.Ok;
        var fakeAtmosphere = new Node { Name = "DaySideAtmosphere" };
        fakeAtmosphere.SetScript(planetScript);
        tree.Root.AddChild(fakeAtmosphere);
        string mapPath = path + ".s1.tres";
        var mapSettings = new GameplayShadowDefaults(mapPath);
        mapSettings.Initialize(controller);
        float presetDistance = sun.DirectionalShadowMaxDistance;
        bool mapDefaultsUnchanged = presetDistance >= 1000f && presetDistance != 400f
            && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits
            && controller.EffectiveSunCascadeCount == 4
            && (bool)fakeAtmosphere.Get("planet_light_cast_shadows") && Mathf.IsEqualApprox((float)fakeAtmosphere.Get("planet_light_shadow_distance_meters"), 5000f) && (int)fakeAtmosphere.Get("planet_light_shadow_cascades") == 4;
        bool mapApplied = mapSettings.SetOverride(controller, "MaxDistanceMeters", 400f) && mapSettings.SetOverride(controller, "CascadeCount", 2)
            && mapSettings.SetOverride(controller, "NearCascadeMeters", 60f)
            && mapSettings.SetOverride(controller, "PlanetShadowsEnabled", false) && mapSettings.SetOverride(controller, "PlanetShadowDistanceMeters", 250f) && mapSettings.SetOverride(controller, "PlanetCascadeCount", 2);
        controller._Process(0);
        bool mapLight = mapApplied
            && Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400f) && Mathf.IsEqualApprox(controller.EffectiveShadowDistanceMeters, 400f)
            && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits && controller.EffectiveSunCascadeCount == 2
            && Mathf.IsEqualApprox(controller.EffectiveNearCascadeMeters, 60f) && Mathf.IsEqualApprox(sun.DirectionalShadowSplit1, .15f)
            && Mathf.IsEqualApprox(RingGraphicsQualityController.ResolveTerrainShadowCasterCoverage(sun, null), 400f)
            && !(bool)fakeAtmosphere.Get("planet_light_cast_shadows") && Mathf.IsEqualApprox((float)fakeAtmosphere.Get("planet_light_shadow_distance_meters"), 250f) && (int)fakeAtmosphere.Get("planet_light_shadow_cascades") == 2
            && !controller.EffectivePlanetShadowsEnabled && controller.EffectivePlanetCascadeCount == 2;
        controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.High, applyTerrainPreset: false);
        bool mapSurvivesPreset = Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400f) && sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits;
        bool mapSaved = mapSettings.Save();
        var mapReloaded = new RingGraphicsQualityController();
        var mapReloadedSun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
        mapReloaded.AddChild(mapReloadedSun);
        mapReloaded.SunLightPath = new NodePath("Sun");
        tree.Root.AddChild(mapReloaded);
        mapReloaded.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.VeryHigh, applyTerrainPreset: false);
        var mapLoaded = new GameplayShadowDefaults(mapPath);
        mapLoaded.Initialize(mapReloaded);
        bool mapRoundTrip = mapSaved && mapLoaded.Settings.Overrides.ContainsKey("MaxDistanceMeters") && mapLoaded.Settings.Overrides.ContainsKey("PlanetCascadeCount")
            && Mathf.IsEqualApprox(mapReloadedSun.DirectionalShadowMaxDistance, 400f) && mapReloadedSun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel2Splits;
        tree.Root.RemoveChild(mapReloaded);
        mapReloaded.Free();
        mapSettings.Reset(controller);
        controller._Process(0);
        bool mapReset = sun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits && controller.EffectiveSunCascadeCount == 4
            && (bool)fakeAtmosphere.Get("planet_light_cast_shadows") && Mathf.IsEqualApprox((float)fakeAtmosphere.Get("planet_light_shadow_distance_meters"), 5000f) && (int)fakeAtmosphere.Get("planet_light_shadow_cascades") == 4;
        // The High preset range (not the 14000 m VeryHigh one) returns after reset, and never below the 1000 m preset floor.
        bool mapResetDistance = sun.DirectionalShadowMaxDistance >= 1000f && !Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, 400f);
        bool legacyKeepsMode = legacySun.DirectionalShadowMode == DirectionalLight3D.ShadowMode.Parallel4Splits && Mathf.IsEqualApprox(legacySun.DirectionalShadowMaxDistance, 14000f);
        tree.Root.RemoveChild(fakeAtmosphere);
        fakeAtmosphere.Free();
        bool shadowMapKeys = planetScriptOk && mapKeysValid && mapKeysRejected && mapDefaultsUnchanged && mapLight && mapSurvivesPreset && mapRoundTrip && mapReset && mapResetDistance && legacyKeepsMode;
        (bool heightShadowKeys, string heightShadowDetail) = RunHeightShadowKeys(path, tree);
        (bool contactShadowKeys, string contactShadowDetail) = RunContactShadowKeys(path, tree);
        var result = new Godot.Collections.Dictionary();
        result["heightShadowKeys"] = heightShadowKeys;
        result["contactShadowKeys"] = contactShadowKeys;
        result["pass"] = heightShadowKeys && contactShadowKeys && supportedAtlas && expandedAtlases && rejectedAtlas && rejectedFractionalAtlas && rejectedPreciseFractionalAtlas && rejectedNanAtlas && rejectedOversizedAtlas && rejectedNegativeAtlas && resolver && memoryEstimates && depthValidation && nearValidation && supportedFilter && rejectedFilter && rejectedDistance && rejectedNaN && stabilizationEnabled && rejectedStabilizationType && stepRange && stepClamped && softnessRange && rejectedSoftness && handoffValidation && terrainMacroValidation && debugModeValidation && debugModeSet && defaultNearFocus && formatToggleSameAtlas && directSwitching && mixedPrecedence && upperNearClamped && directUnsupportedRejected && unsupportedPreferenceRejected && set && macroRuntimeApplied && effective && survivesPreset && reset && missingInputsKeepsRequestedSun && saved && roundTrip && legacyLoadCompatibility && highSaved && portableHighClamped && shadowMapKeys;
        result["unsupportedPreferenceRejected"] = unsupportedPreferenceRejected;
        result["shadowMapKeys"] = shadowMapKeys;
        result["detail"] = $"{heightShadowDetail} {contactShadowDetail} atlas={supportedAtlas}/{expandedAtlases}/{rejectedAtlas}/{rejectedFractionalAtlas}/{rejectedPreciseFractionalAtlas}/{rejectedNanAtlas}/{rejectedOversizedAtlas}/{rejectedNegativeAtlas} resolver={resolver} memory={memoryEstimates} depth={depthValidation}/{formatToggleSameAtlas} near={nearValidation}/{defaultNearFocus}/{directSwitching}/{mixedPrecedence}/{upperNearClamped} filter={supportedFilter}/{rejectedFilter} rangeRejected={rejectedDistance} nan={rejectedNaN} stabilization={stabilizationEnabled}/{rejectedStabilizationType} step={stepRange}/{stepClamped} softness={softnessRange}/{rejectedSoftness} handoff={handoffValidation} terrainMacro={terrainMacroValidation}/{macroRuntimeApplied} debugMode={debugModeValidation}/{debugModeSet} directUnsupportedRejected={directUnsupportedRejected} set={set} effective={effective} survivesPreset={survivesPreset} reset={reset}/{missingInputsKeepsRequestedSun} saved={saved} roundTrip={roundTrip} legacyLoad={legacyLoadCompatibility} highSaved={highSaved} portableHighClamped={portableHighClamped} shadowMapKeys={shadowMapKeys}[{planetScriptOk}/{mapKeysValid}/{mapKeysRejected}/{mapDefaultsUnchanged}/{mapLight}/{mapSurvivesPreset}/{mapRoundTrip}/{mapReset}/{mapResetDistance}/{legacyKeepsMode}]; fixture path={path}";
        tree.Root.RemoveChild(controller);
        controller.Free();
        tree.Root.RemoveChild(reloaded);
        reloaded.Free();
        tree.Root.RemoveChild(legacyController);
        legacyController.Free();
        tree.Root.RemoveChild(globalLod);
        globalLod.Free();
        highReloadedController.Free();
        return result;
    }
}
