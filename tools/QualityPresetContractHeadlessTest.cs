using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Configuration half of the graphics-preset contract (docs/quality-presets-plan.md section 1, C1-C3) for all five menu presets
/// (Low, Medium, High, VeryHigh, UltraVideo) on factory-default atmosphere settings. No GPU needed.
/// Run: Godot --headless --path . --script res://tools/quality_preset_contract_headless_test.gd. Prints QP_CONTRACT_HEADLESS=PASS.
/// </summary>
public partial class QualityPresetContractHeadlessTest : RefCounted
{
    private static readonly string[] Names = { "Low", "Medium", "High", "VeryHigh", "UltraVideo" };
    // Height-shadow preset defaults (shadow-revamp N1): texel (m), near sun map distance (m) for Low..Cinematic5090.
    private static readonly int[] HeightTexel = { 8, 8, 4, 4, 4, 2 };
    private static readonly int[] HeightNearMap = { 300, 300, 400, 400, 500, 500 };

    // WO-3 matrix (Low, Medium, High, VeryHigh, UltraVideo).
    private static readonly int[] AtlasSizes = { 2048, 4096, 4096, 8192, 8192 };
    private static readonly float[] SunDistances = { 4000f, 6000f, 10000f, 14000f, 16000f };
    private static readonly float[] PlanetDistances = { 2000f, 3000f, 4000f, 6000f, 6000f };
    private static readonly int[] PlanetCascades = { 2, 2, 3, 4, 4 };
    private static readonly bool[] SsrEnabled = { false, false, true, true, true };
    private static readonly int[] SsrSteps = { 12, 12, 12, 24, 32 };
    private static readonly float[] SsrRay = { 120f, 120f, 120f, 250f, 400f };
    private static readonly float[] SsrCamera = { 250f, 250f, 250f, 450f, 600f };
    private static readonly float[] SsaoDetail = { 0f, 0.75f, 0.85f, 0.92f, 0.96f };
    private static readonly float[] FarScale = { 0.5f, 0.75f, 1f, 1f, 1f };
    private static readonly int[] RainParticles = { 1024, 2048, 3072, 4096, 6144 };
    private static readonly int[] RainStreaks = { 4000, 8000, 12000, 16000, 20000 };
    private static readonly int[] RippleLayers = { 1, 2, 3, 4, 4 };
    private static readonly int[] DripBudget = { 128, 256, 384, 512, 512 };
    private static readonly bool[] WetHalfSizeSsr = { true, true, true, false, false };
    private static readonly int[] GustDebris = { 128, 256, 512, 768, 1024 };
    private static readonly int[] BoltBranches = { 1, 2, 3, 3, 4 };
    private static readonly int[] BoltDepth = { 0, 1, 1, 1, 2 };
    private static readonly float[] TreeNearEnd = { 25f, 30f, 40f, 40f, 60f };
    private static readonly float[] TreeBillboardStart = { 200f, 250f, 300f, 300f, 550f };
    private static readonly float[] TreeBillboardEnd = { 240f, 300f, 350f, 350f, 650f };
    private static readonly float[] TreeSunShadow = { 160f, 240f, 360f, 480f, 700f };

    private static void CheckMonotonic<T>(Action<bool, string> check, string name, IReadOnlyList<T> values) where T : IComparable<T>
    {
        for (int i = 1; i < values.Count; i++) check(values[i - 1].CompareTo(values[i]) <= 0, $"WO3 {name} not monotonic Low..Ultra at {i}: {values[i - 1]} > {values[i]}");
    }

    public int Run()
    {
        var failures = new List<string>();
        var notes = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        SceneTree? tree = Engine.GetMainLoop() as SceneTree;
        if (tree?.Root == null) { Console.Error.WriteLine("QP_CONTRACT_HEADLESS=FAIL no SceneTree root"); return 1; }

        bool savedEnabled = RingAtmosphereModel.Enabled;
        RingAtmosphereModel.HazeModelKind savedModel = RingAtmosphereModel.HazeModel;
        string atmoPath = $"res://.godot/qp_contract_atmo_{Time.GetTicksUsec()}.tres";
        RingTerrainGlobalLod? globalLod = null;
        RingGraphicsQualityController? controller = null;
        DirectionalLight3D? sun = null;
        WorldEnvironment? environmentNode = null;
        global::RingworldTechDemo.RingLocalWeatherEffects? weather = null;
        try
        {
            controller = new RingGraphicsQualityController();
            sun = new DirectionalLight3D { Name = "Sun", ShadowEnabled = true };
            controller.AddChild(sun);
            controller.SunLightPath = new NodePath("Sun");
            // WO-3: a bare environment (SSAO) and weather node let the preset-driven settings be observed headless.
            environmentNode = new WorldEnvironment { Name = "Env", Environment = new Godot.Environment { SsaoEnabled = true } };
            controller.AddChild(environmentNode);
            controller.WorldEnvironmentPath = new NodePath("Env");
            weather = new global::RingworldTechDemo.RingLocalWeatherEffects { Name = "Weather" };
            controller.AddChild(weather);
            controller.WeatherEffectsPath = new NodePath("Weather");
            globalLod = new RingTerrainGlobalLod { Name = "GlobalTerrainLod3", EnableMacroTerrainShadows = true, EnableMacroTerrainWallShadows = false };
            tree.Root.AddChild(globalLod);
            tree.Root.AddChild(controller);
            // Factory defaults: the path does not exist, so no user preference is loaded.
            controller.InstallAtmosphereDefaults(atmoPath);

            bool atlasFallbackNoted = false;
            for (int p = 0; p <= 4; p++)
            {
                string tag = $"p={p}({Names[p]})";
                controller.ApplyPreset((RingTerrainLodCoordinator.QualityPreset)p, applyTerrainPreset: false);
                // C1 config
                Check(sun.DirectionalShadowMaxDistance >= 250.0f, $"C1 {tag} sun DirectionalShadowMaxDistance={sun.DirectionalShadowMaxDistance} < 250");
                Check(controller.RequestedSunShadowsEnabled, $"C1 {tag} RequestedSunShadowsEnabled=false");
                Check(controller.EffectiveShadowDistanceMeters >= 250.0f, $"C1 {tag} EffectiveShadowDistanceMeters={controller.EffectiveShadowDistanceMeters} < 250");
                int atlas = controller.EffectiveDirectionalShadowAtlasSize;
                string atlasName = "EffectiveDirectionalShadowAtlasSize";
                if (atlas <= 0)
                {
                    atlas = controller.RequestedDirectionalShadowAtlasSize;
                    atlasName = "RequestedDirectionalShadowAtlasSize";
                    if (!atlasFallbackNoted) { notes.Add("effective atlas reported 0 headless (no RD); asserting Requested atlas"); atlasFallbackNoted = true; }
                }
                Check(atlas >= 2048, $"C1 {tag} {atlasName}={atlas} < 2048");
                // C2 config
                Check(controller.EffectiveTerrainMacroShadowsEnabled, $"C2 {tag} EffectiveTerrainMacroShadowsEnabled=false");
                Check(controller.EffectiveTerrainMacroShadowMaxTraceMeters >= 64000.0f, $"C2 {tag} EffectiveTerrainMacroShadowMaxTraceMeters={controller.EffectiveTerrainMacroShadowMaxTraceMeters} < 64000");
                // C3
                Check(RingAtmosphereModel.Enabled, $"C3 {tag} RingAtmosphereModel.Enabled=false");
                Check(RingAtmosphereModel.HazeModel == RingAtmosphereModel.HazeModelKind.Physical, $"C3 {tag} HazeModel={RingAtmosphereModel.HazeModel} (want Physical)");
                // WO-3 matrix
                var preset = (RingTerrainLodCoordinator.QualityPreset)p;
                Check(controller.RequestedDirectionalShadowAtlasSize == AtlasSizes[p], $"WO3 {tag} requested atlas={controller.RequestedDirectionalShadowAtlasSize} want {AtlasSizes[p]}");
                Check(Mathf.IsEqualApprox(sun.DirectionalShadowMaxDistance, SunDistances[p]), $"WO3 {tag} sun distance={sun.DirectionalShadowMaxDistance} want {SunDistances[p]}");
                (float planetDistance, int planetCascades) = RingGraphicsQualityController.PresetPlanetLight(preset);
                Check(planetDistance == PlanetDistances[p] && planetCascades == PlanetCascades[p], $"WO3 {tag} planet light {planetDistance}/{planetCascades} want {PlanetDistances[p]}/{PlanetCascades[p]}");
                Check(controller.WaterSsrEnabled == SsrEnabled[p] && controller.WaterSsrCoarseSteps == SsrSteps[p] && controller.WaterSsrMaxRayDistance == SsrRay[p] && controller.WaterSsrCameraDistance == SsrCamera[p],
                    $"WO3 {tag} SSR enabled={controller.WaterSsrEnabled} steps={controller.WaterSsrCoarseSteps} ray={controller.WaterSsrMaxRayDistance} camera={controller.WaterSsrCameraDistance}");
                Godot.Environment? environment = environmentNode!.Environment;
                Check(environment != null && environment.SsaoEnabled == (SsaoDetail[p] > 0f) && Mathf.IsEqualApprox(environment.SsaoDetail, SsaoDetail[p]), $"WO3 {tag} SSAO enabled={environment?.SsaoEnabled} detail={environment?.SsaoDetail} want {SsaoDetail[p]}");
                Check(Mathf.IsEqualApprox(RingGraphicsQualityController.PresetFarRendering(preset).scale, FarScale[p]), $"WO3 {tag} far viewport scale want {FarScale[p]}");
                Check(weather!.MaximumStreakCount == RainStreaks[p] && RingGraphicsQualityController.PresetWeather(preset).streaks == RainStreaks[p], $"WO5 {tag} rain streak budget={weather.MaximumStreakCount} want {RainStreaks[p]}");
                Check(weather.MaximumParticleCount == RainParticles[p] && weather.MaximumParticleCount >= 512 && weather.EnableLightning && weather.BoltMaximumBranches == BoltBranches[p] && weather.BoltSecondaryBranchDepth == BoltDepth[p],
                    $"WO3 {tag} weather particles={weather.MaximumParticleCount} lightning={weather.EnableLightning} branches={weather.BoltMaximumBranches}/{weather.BoltSecondaryBranchDepth}");
                Check(RingWetWorldSettings.Current.RippleLayers == RippleLayers[p] && RingWetWorldSettings.Current.DripBudget == DripBudget[p] && RingWetWorldSettings.Current.HalfSizeSsr == WetHalfSizeSsr[p]
                    && RingGraphicsQualityController.PresetWetWorld(preset) == (RippleLayers[p], DripBudget[p], WetHalfSizeSsr[p]),
                    $"S2 {tag} wet world ripple layers={RingWetWorldSettings.Current.RippleLayers} drips={RingWetWorldSettings.Current.DripBudget} half-size SSR={RingWetWorldSettings.Current.HalfSizeSsr} want {RippleLayers[p]}/{DripBudget[p]}/{WetHalfSizeSsr[p]}");
                Check(RingGustFrontDebris.PresetBudget == GustDebris[p] && RingGraphicsQualityController.PresetWeather(preset).debris == GustDebris[p], $"S3 {tag} gust debris budget={RingGustFrontDebris.PresetBudget} want {GustDebris[p]}");
                var trees = RingGraphicsQualityController.PresetTrees(preset);
                Check(trees.NearEnd == TreeNearEnd[p] && trees.BillboardStart == TreeBillboardStart[p] && trees.BillboardEnd == TreeBillboardEnd[p] && trees.SunShadow == TreeSunShadow[p] && trees.NearStart < trees.NearEnd && trees.NearEnd <= trees.BillboardStart,
                    $"WO3 {tag} trees near={trees.NearStart}-{trees.NearEnd} billboard={trees.BillboardStart}-{trees.BillboardEnd} shadow={trees.SunShadow}");
                var vegetation = new GameplayVegetationDefaults(null) { Preset = preset };
                Check(vegetation.TreeNearDetailFadeEndMeters == TreeNearEnd[p] && vegetation.TreeBillboardFadeStartMeters == TreeBillboardStart[p] && vegetation.TreeSunShadowDistanceMeters == TreeSunShadow[p], $"WO3 {tag} vegetation defaults do not follow the preset");
            }

            CheckMonotonic(Check, "atlas", AtlasSizes); CheckMonotonic(Check, "sun distance", SunDistances); CheckMonotonic(Check, "planet distance", PlanetDistances); CheckMonotonic(Check, "planet cascades", PlanetCascades);
            CheckMonotonic(Check, "ssr steps", SsrSteps); CheckMonotonic(Check, "ssr ray", SsrRay); CheckMonotonic(Check, "ssr camera", SsrCamera); CheckMonotonic(Check, "ssao", SsaoDetail); CheckMonotonic(Check, "far scale", FarScale);
            CheckMonotonic(Check, "rain particles", RainParticles); CheckMonotonic(Check, "rain streaks", RainStreaks); CheckMonotonic(Check, "bolt branches", BoltBranches); CheckMonotonic(Check, "bolt depth", BoltDepth);
            CheckMonotonic(Check, "tree near end", TreeNearEnd); CheckMonotonic(Check, "tree billboard start", TreeBillboardStart); CheckMonotonic(Check, "tree billboard end", TreeBillboardEnd); CheckMonotonic(Check, "tree sun shadow", TreeSunShadow); CheckMonotonic(Check, "ripple layers", RippleLayers); CheckMonotonic(Check, "drip budget", DripBudget); CheckMonotonic(Check, "gust debris", GustDebris);

            // N1: height shadows + near sun map are preset-owned defaults on every preset (Low..Cinematic5090); an explicit override wins.
            string migrationPath = $"res://.godot/qp_contract_hs_{Time.GetTicksUsec()}.tres";
            for (int p = 0; p <= 5; p++)
            {
                string tag = $"p={p}({(RingTerrainLodCoordinator.QualityPreset)p})";
                controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary());
                controller.ApplyPreset((RingTerrainLodCoordinator.QualityPreset)p, applyTerrainPreset: false);
                Check(controller.EffectiveHeightShadowEnabled, $"N1 {tag} EffectiveHeightShadowEnabled=false");
                Check(controller.HeightShadowPresetDefaultsActive, $"N1 {tag} HeightShadowPresetDefaultsActive=false");
                Check(controller.EffectiveHeightShadowL0TexelMeters == HeightTexel[p], $"N1 {tag} L0 texel={controller.EffectiveHeightShadowL0TexelMeters} want {HeightTexel[p]}");
                Check(controller.EffectiveHeightShadowRangeMeters == HeightTexel[p] * 2000, $"N1 {tag} range={controller.EffectiveHeightShadowRangeMeters}");
                Check(controller.EffectiveHeightShadowNearMapDistanceMeters == HeightNearMap[p], $"N1 {tag} near map={controller.EffectiveHeightShadowNearMapDistanceMeters} want {HeightNearMap[p]}");
                Check(controller.EffectiveHeightShadowLevels == 2, $"N1 {tag} levels={controller.EffectiveHeightShadowLevels} want 2");
                Check(controller.EffectiveHeightShadowNearMapEnabled && controller.EffectiveHeightShadowPlanetNearMapEnabled && controller.EffectiveHeightShadowPlanetNearMapDistanceMeters == 250, $"N1 {tag} near map switches / planet near map changed");
                // Explicit overrides win over the preset values, and a preset change re-resolves the rest.
                int revision = controller.HeightShadowRevision;
                Check(controller.SetShadowTuningOverride("HeightShadowEnabled", false) && !controller.EffectiveHeightShadowEnabled && !controller.HeightShadowPresetDefaultsActive, $"N1 {tag} explicit Enabled=false did not win");
                int otherTexel = HeightTexel[p] == 2 ? 8 : 2, otherNear = HeightNearMap[p] == 300 ? 500 : 300;
                Check(controller.SetShadowTuningOverride("HeightShadowL0TexelMeters", otherTexel) && controller.EffectiveHeightShadowL0TexelMeters == otherTexel, $"N1 {tag} explicit texel did not win");
                Check(controller.SetShadowTuningOverride("HeightShadowNearMapDistanceMeters", otherNear) && controller.EffectiveHeightShadowNearMapDistanceMeters == otherNear, $"N1 {tag} explicit near map did not win");
                Check(controller.HeightShadowRevision > revision, $"N1 {tag} HeightShadowRevision did not advance");
                controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary());
                Check(controller.EffectiveHeightShadowEnabled && controller.EffectiveHeightShadowL0TexelMeters == HeightTexel[p], $"N1 {tag} clearing overrides did not restore preset defaults");
            }
            // A preset change takes effect immediately for keys without an override.
            controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Low, applyTerrainPreset: false);
            int revisionBefore = controller.HeightShadowRevision;
            controller.ApplyPreset(RingTerrainLodCoordinator.QualityPreset.Cinematic5090, applyTerrainPreset: false);
            Check(controller.HeightShadowRevision > revisionBefore && controller.EffectiveHeightShadowL0TexelMeters == 2 && controller.EffectiveHeightShadowNearMapDistanceMeters == 500, "N1 preset change did not re-resolve height shadow defaults");

            // N2: explicit HeightShadowEnabled=false without the marker is migrated once to "no override"; with the marker a later explicit false is honoured.
            string migrationAbs = ProjectSettings.GlobalizePath(migrationPath);
            try
            {
                var legacy = new GameplayShadowSettings();
                legacy.Overrides["HeightShadowEnabled"] = false;
                legacy.Overrides["Split1"] = .18f;
                Check(ResourceSaver.Save(legacy, migrationPath) == Error.Ok, "N2 could not save the legacy fixture");
                var migrated = new GameplayShadowDefaults(migrationPath);
                migrated.Initialize(controller);
                Check(!migrated.Settings.Overrides.ContainsKey("HeightShadowEnabled") && migrated.Settings.Overrides.ContainsKey(RingGraphicsQualityController.HeightShadowMigrationKey) && migrated.Settings.Overrides.ContainsKey("Split1") && migrated.Dirty, "N2 legacy explicit false was not migrated (or touched another key)");
                Check(controller.EffectiveHeightShadowEnabled && controller.HeightShadowPresetDefaultsActive, "N2 migrated file did not leave the preset default on");
                Check(migrated.Save(), "N2 could not save the migrated file");
                var marked = new GameplayShadowDefaults(migrationPath);
                marked.Initialize(controller);
                Check(!marked.Settings.Overrides.ContainsKey("HeightShadowEnabled") && !marked.Dirty, "N2 migrated file was migrated again or left dirty");
                Check(marked.SetOverride(controller, "HeightShadowEnabled", false) && marked.Save(), "N2 could not save an explicit false after migration");
                var honoured = new GameplayShadowDefaults(migrationPath);
                honoured.Initialize(controller);
                Check(honoured.Settings.Overrides.ContainsKey("HeightShadowEnabled") && !honoured.Settings.Overrides["HeightShadowEnabled"].AsBool() && !controller.EffectiveHeightShadowEnabled && !honoured.Dirty, "N2 explicit false with the marker was not honoured");
                honoured.Reset(controller);
                Check(honoured.Save(), "N2 could not save the reset file");
                var afterReset = new GameplayShadowDefaults(migrationPath);
                afterReset.Initialize(controller);
                Check(afterReset.Settings.Overrides.ContainsKey(RingGraphicsQualityController.HeightShadowMigrationKey) && controller.EffectiveHeightShadowEnabled, "N2 reset file lost the marker");
            }
            finally
            {
                controller.SetShadowTuningOverrides(new Godot.Collections.Dictionary());
                if (File.Exists(migrationAbs)) File.Delete(migrationAbs);
            }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); failures.Add("exception " + e.Message); }
        finally
        {
            RingAtmosphereModel.Enabled = savedEnabled;
            RingAtmosphereModel.HazeModel = savedModel;
            if (controller != null && GodotObject.IsInstanceValid(controller)) { tree.Root.RemoveChild(controller); controller.Free(); }
            if (globalLod != null && GodotObject.IsInstanceValid(globalLod)) { tree.Root.RemoveChild(globalLod); globalLod.Free(); }
            string abs = ProjectSettings.GlobalizePath(atmoPath);
            if (File.Exists(abs)) File.Delete(abs);
        }
        foreach (string note in notes) Console.WriteLine("QP_CONTRACT_HEADLESS note: " + note);
        if (failures.Count != 0) { Console.Error.WriteLine("QP_CONTRACT_HEADLESS=FAIL " + string.Join(" | ", failures.Take(40))); return 1; }
        Console.WriteLine("QP_CONTRACT_HEADLESS=PASS");
        return 0;
    }
}
