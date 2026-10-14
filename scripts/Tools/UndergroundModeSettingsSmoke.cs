using System;
using System.IO;
using Godot;
using RingworldTechDemo.Underground;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class UndergroundModeSettingsSmoke : RefCounted
{
    private static bool baselineGroups(GameplayUndergroundModeSettings s) => s.AudioWeatherEnabled && s.VegetationEnabled && s.CloudsEnabled && s.WaterEnabled && s.FarTerrainEnabled && s.ShadowsEnabled;

    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string full = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(full)) File.Delete(full);
            var defaults = new GameplayUndergroundModeDefaults(isolatedPath);
            defaults.Load();
            var baseline = new UndergroundEnclosureParameters();
            bool baselineOk = defaults.Settings.Enabled && defaults.Settings.EnterDepthMeters == baseline.EnterDepthMeters
                && defaults.Settings.ExitDepthMeters == baseline.ExitDepthMeters && defaults.Settings.EnterOpennessMax == baseline.EnterOpennessMax
                && defaults.Settings.ExitOpennessMin == baseline.ExitOpennessMin && defaults.Settings.EnterHoldSeconds == baseline.EnterHoldSeconds
                && defaults.Settings.ExitHoldSeconds == baseline.ExitHoldSeconds && defaults.Settings.BlendSeconds == baseline.BlendSeconds
                && !defaults.Dirty && !defaults.HasSavedFile;
            using var same = new GameplayUndergroundModeSettings();
            bool noOp = !defaults.Set(same) && !defaults.Dirty;
            using var custom = new GameplayUndergroundModeSettings { Enabled = false, EnterDepthMeters = 40, ExitDepthMeters = 20, EnterOpennessMax = .1f, ExitOpennessMin = .3f, EnterHoldSeconds = 2, ExitHoldSeconds = .5f, BlendSeconds = 2.5f };
            bool changed = defaults.Set(custom) && defaults.Dirty && defaults.Save() && !defaults.Dirty && defaults.HasSavedFile;
            var reloaded = new GameplayUndergroundModeDefaults(isolatedPath);
            reloaded.Load();
            bool persisted = !reloaded.Settings.Enabled && reloaded.Settings.EnterDepthMeters == 40 && reloaded.Settings.ExitDepthMeters == 20
                && reloaded.Settings.EnterOpennessMax == .1f && reloaded.Settings.ExitOpennessMin == .3f && reloaded.Settings.EnterHoldSeconds == 2
                && reloaded.Settings.ExitHoldSeconds == .5f && reloaded.Settings.BlendSeconds == 2.5f;
            var controller = new RingUndergroundModeController();
            bool untouched = controller.Enabled && controller.Parameters == new UndergroundEnclosureParameters();
            // The drawer only applies once a saved file exists.
            if (reloaded.HasSavedFile) reloaded.Apply(controller);
            bool applied = !controller.Enabled && controller.Parameters.EnterDepthMeters == 40 && controller.Parameters.BlendSeconds == 2.5f && controller.Parameters.ExitOpennessMin == .3f;
            controller.Free();
            var fresh = new GameplayUndergroundModeDefaults(isolatedPath + ".missing");
            fresh.Load();
            bool noFileNoApply = !fresh.HasSavedFile;
            using var wild = new GameplayUndergroundModeSettings { EnterDepthMeters = float.NaN, ExitDepthMeters = 999, EnterOpennessMax = 5, ExitOpennessMin = -1, EnterHoldSeconds = -3, ExitHoldSeconds = 99, BlendSeconds = 0 };
            using var clean = GameplayUndergroundModeDefaults.Sanitize(wild);
            bool sanitized = clean.EnterDepthMeters == 24 && clean.ExitDepthMeters == 24 && clean.EnterOpennessMax == 1 && clean.ExitOpennessMin == 1
                && clean.EnterHoldSeconds == 0 && clean.ExitHoldSeconds == 2 && clean.BlendSeconds == GameplayUndergroundModeDefaults.MinimumBlend;
            using var inverted = new GameplayUndergroundModeSettings { EnterDepthMeters = 30, ExitDepthMeters = 50, EnterOpennessMax = .4f, ExitOpennessMin = .1f };
            using var ordered = GameplayUndergroundModeDefaults.Sanitize(inverted);
            bool hysteresis = ordered.ExitDepthMeters == 30 && ordered.ExitOpennessMin == .4f;
            using var malformed = new GameplayUndergroundModeSettings { EnterDepthMeters = 9999, BlendSeconds = -10, ExitHoldSeconds = 50 };
            bool malformedSaved = ResourceSaver.Save(malformed, isolatedPath) == Error.Ok;
            var bounded = new GameplayUndergroundModeDefaults(isolatedPath);
            bounded.Load();
            bool loadSanitized = malformedSaved && bounded.Settings.EnterDepthMeters == 256 && bounded.Settings.BlendSeconds == GameplayUndergroundModeDefaults.MinimumBlend
                && bounded.Settings.ExitHoldSeconds == 2 && bounded.Settings.ExitDepthMeters <= bounded.Settings.EnterDepthMeters;
            bounded.Reset();
            bool reset = bounded.Settings.Enabled && bounded.Settings.EnterDepthMeters == 24 && bounded.Dirty && bounded.Save();
            // Per-group suspension toggles, cave lighting (D1/D2) and the deep-hide depth.
            bool groupDefaults = baselineGroups(defaults.Settings) && defaults.EnabledGroups() == UndergroundGroups.All
                && defaults.Settings.DeepHideDepthMeters == 128 && defaults.Settings.EnclosureLightingWeight == 1
                && defaults.Settings.CaveAmbientEnergy == .035f && defaults.Settings.CaveAmbientColor == new Color(.16f, .20f, .30f, 1f)
                && defaults.Settings.CaveFogColor == new Color(.05f, .07f, .11f, 1f);
            var lightingPath = isolatedPath + ".lighting.tres";
            var lighting = new GameplayUndergroundModeDefaults(lightingPath);
            lighting.Load();
            using var lightingCustom = new GameplayUndergroundModeSettings
            {
                AudioWeatherEnabled = false, VegetationEnabled = false, CloudsEnabled = true, WaterEnabled = false, FarTerrainEnabled = true, ShadowsEnabled = false,
                DeepHideDepthMeters = 200, EnclosureLightingWeight = .5f, CaveAmbientEnergy = .08f,
                CaveAmbientColor = new Color(.3f, .2f, .1f, 1f), CaveFogColor = new Color(.1f, .2f, .3f, 1f)
            };
            bool lightingSaved = lighting.Set(lightingCustom) && lighting.Save();
            var lightingReloaded = new GameplayUndergroundModeDefaults(lightingPath);
            lightingReloaded.Load();
            var lightingController = new RingUndergroundModeController();
            bool controllerDefaults = lightingController.EnabledGroups == UndergroundGroups.All && lightingController.DeepHideDepthMeters == 128 && lightingController.EnclosureLightingWeight == 1
                && lightingController.CaveAmbientEnergy == .035f;
            lightingReloaded.Apply(lightingController);
            bool lightingApplied = lightingController.EnabledGroups == (UndergroundGroups.Clouds | UndergroundGroups.FarTerrain)
                && lightingController.DeepHideDepthMeters == 200 && lightingController.EnclosureLightingWeight == .5f && lightingController.CaveAmbientEnergy == .08f
                && lightingController.CaveAmbientColor == new Color(.3f, .2f, .1f, 1f) && lightingController.CaveFogColor == new Color(.1f, .2f, .3f, 1f);
            lightingController.Free();
            bool lightingPersisted = lightingSaved && !lightingReloaded.Settings.AudioWeatherEnabled && !lightingReloaded.Settings.VegetationEnabled && lightingReloaded.Settings.CloudsEnabled
                && !lightingReloaded.Settings.WaterEnabled && lightingReloaded.Settings.FarTerrainEnabled && !lightingReloaded.Settings.ShadowsEnabled
                && lightingReloaded.Settings.DeepHideDepthMeters == 200 && lightingReloaded.Settings.EnclosureLightingWeight == .5f;
            using var wildLighting = new GameplayUndergroundModeSettings
            {
                DeepHideDepthMeters = 1, EnclosureLightingWeight = 5, CaveAmbientEnergy = 99, CaveAmbientColor = new Color(float.NaN, 2f, -1f, .2f), CaveFogColor = new Color(2f, -1f, .5f, 0f)
            };
            using var cleanLighting = GameplayUndergroundModeDefaults.Sanitize(wildLighting);
            bool lightingSanitized = cleanLighting.DeepHideDepthMeters == GameplayUndergroundModeDefaults.MinimumDeepHideDepth && cleanLighting.EnclosureLightingWeight == 1
                && cleanLighting.CaveAmbientEnergy == GameplayUndergroundModeDefaults.MaximumCaveAmbientEnergy
                && cleanLighting.CaveAmbientColor == new Color(.16f, .20f, .30f, 1f) && cleanLighting.CaveFogColor == new Color(1f, 0f, .5f, 1f);
            using var wildDepth = new GameplayUndergroundModeSettings { DeepHideDepthMeters = 99999, EnclosureLightingWeight = -2, CaveAmbientEnergy = float.NaN };
            using var cleanDepth = GameplayUndergroundModeDefaults.Sanitize(wildDepth);
            bool depthSanitized = cleanDepth.DeepHideDepthMeters == GameplayUndergroundModeDefaults.MaximumDeepHideDepth && cleanDepth.EnclosureLightingWeight == 0 && cleanDepth.CaveAmbientEnergy == .035f;
            // Cave LOD profile: defaults match the controller, round trip, apply, and sanitize clamps.
            var profileDefaults = new GameplayUndergroundModeDefaults(isolatedPath + ".profile.missing");
            profileDefaults.Load();
            var profileController = new RingUndergroundModeController();
            var ps = profileDefaults.Settings;
            bool profileDefaultsMatch = profileController.UndergroundCaveProfile == CaveLodProfile.UndergroundDefault
                && ps.CaveCoarseRadiusAround == profileController.UndergroundCaveProfile.CoarseRadiusAround && ps.CaveCoarseRadiusAcross == profileController.UndergroundCaveProfile.CoarseRadiusAcross
                && ps.CaveCoarseRadiusVertical == profileController.UndergroundCaveProfile.CoarseRadiusVertical && ps.CaveWorkerBonus == profileController.UndergroundCaveProfile.WorkerBonus
                && ps.CaveInstallBudgetMilliseconds == profileController.UndergroundCaveProfile.InstallBudgetMilliseconds && ps.CaveMaxInstallsPerFrame == profileController.UndergroundCaveProfile.MaxInstallsPerFrame
                && ps.CaveCoarseRetainMarginSectors == profileController.CoarseRetainMarginSectors && ps.CaveProfileShrinkDelaySeconds == profileController.CaveProfileShrinkDelaySeconds
                && ps.CaveCoarseActivationHysteresisMeters == profileController.CaveCoarseActivationHysteresisMeters;
            var profilePath = isolatedPath + ".profile.tres";
            var profileSaved = new GameplayUndergroundModeDefaults(profilePath);
            profileSaved.Load();
            using var profileCustom = new GameplayUndergroundModeSettings
            {
                CaveCoarseRadiusAround = 3, CaveCoarseRadiusAcross = 1, CaveCoarseRadiusVertical = 2, CaveWorkerBonus = 4, CaveInstallBudgetMilliseconds = 5.5f,
                CaveMaxInstallsPerFrame = 20, CaveCoarseRetainMarginSectors = 2, CaveProfileShrinkDelaySeconds = 25f, CaveCoarseActivationHysteresisMeters = 128f
            };
            bool profileWritten = profileSaved.Set(profileCustom) && profileSaved.Save();
            var profileReloaded = new GameplayUndergroundModeDefaults(profilePath);
            profileReloaded.Load();
            var rs = profileReloaded.Settings;
            bool profilePersisted = profileWritten && rs.CaveCoarseRadiusAround == 3 && rs.CaveCoarseRadiusAcross == 1 && rs.CaveCoarseRadiusVertical == 2 && rs.CaveWorkerBonus == 4
                && rs.CaveInstallBudgetMilliseconds == 5.5f && rs.CaveMaxInstallsPerFrame == 20 && rs.CaveCoarseRetainMarginSectors == 2
                && rs.CaveProfileShrinkDelaySeconds == 25f && rs.CaveCoarseActivationHysteresisMeters == 128f;
            profileReloaded.Apply(profileController);
            bool profileApplied = profileController.UndergroundCaveProfile == new CaveLodProfile(3, 1, 2, 4, 5.5f, 20) && profileController.CoarseRetainMarginSectors == 2
                && profileController.CaveProfileShrinkDelaySeconds == 25f && profileController.CaveCoarseActivationHysteresisMeters == 128f;
            profileController.Free();
            try { File.Delete(ProjectSettings.GlobalizePath(profilePath)); } catch { }
            using var wildProfile = new GameplayUndergroundModeSettings
            {
                CaveCoarseRadiusAround = 9, CaveCoarseRadiusAcross = -2, CaveCoarseRadiusVertical = 9, CaveWorkerBonus = -1, CaveInstallBudgetMilliseconds = float.NaN,
                CaveMaxInstallsPerFrame = 0, CaveCoarseRetainMarginSectors = 5, CaveProfileShrinkDelaySeconds = -1f, CaveCoarseActivationHysteresisMeters = 999f
            };
            using var cleanProfile = GameplayUndergroundModeDefaults.Sanitize(wildProfile);
            using var wildBudget = new GameplayUndergroundModeSettings { CaveInstallBudgetMilliseconds = 99f };
            using var cleanBudget = GameplayUndergroundModeDefaults.Sanitize(wildBudget);
            bool profileSanitized = cleanProfile.CaveCoarseRadiusAround == 4 && cleanProfile.CaveCoarseRadiusAcross == 0 && cleanProfile.CaveCoarseRadiusVertical == 3 && cleanProfile.CaveWorkerBonus == 0
                && cleanProfile.CaveInstallBudgetMilliseconds == 3f && cleanProfile.CaveMaxInstallsPerFrame == 1 && cleanProfile.CaveCoarseRetainMarginSectors == 2
                && cleanProfile.CaveProfileShrinkDelaySeconds == 0 && cleanProfile.CaveCoarseActivationHysteresisMeters == 256f
                && cleanBudget.CaveInstallBudgetMilliseconds == GameplayUndergroundModeDefaults.MaximumCaveInstallBudget;
            bool caveProfile = profileDefaultsMatch && profilePersisted && profileApplied && profileSanitized;
            // Cave fog coupling: defaults match the controller, round trip, apply, and sanitize clamps.
            var fogController = new RingUndergroundModeController();
            var fs = profileDefaults.Settings;
            bool fogDefaultsMatch = fs.CoupleFogToCaveDistance && fs.CoupleFogToCaveDistance == fogController.CoupleFogToCaveDistance
                && fs.FogEndFraction == fogController.FogEndFraction && fs.UnfilledCaveFogReachMeters == fogController.UnfilledCaveFogReachMeters;
            var fogPath = isolatedPath + ".fog.tres";
            var fogSaved = new GameplayUndergroundModeDefaults(fogPath);
            fogSaved.Load();
            using var fogCustom = new GameplayUndergroundModeSettings { CoupleFogToCaveDistance = false, FogEndFraction = .7f, UnfilledCaveFogReachMeters = 2048f };
            bool fogWritten = fogSaved.Set(fogCustom) && fogSaved.Save();
            var fogReloaded = new GameplayUndergroundModeDefaults(fogPath);
            fogReloaded.Load();
            bool fogPersisted = fogWritten && !fogReloaded.Settings.CoupleFogToCaveDistance && fogReloaded.Settings.FogEndFraction == .7f && fogReloaded.Settings.UnfilledCaveFogReachMeters == 2048f;
            fogReloaded.Apply(fogController);
            bool fogApplied = !fogController.CoupleFogToCaveDistance && fogController.FogEndFraction == .7f && fogController.UnfilledCaveFogReachMeters == 2048f;
            fogController.Free();
            try { File.Delete(ProjectSettings.GlobalizePath(fogPath)); } catch { }
            using var wildFogLow = new GameplayUndergroundModeSettings { FogEndFraction = .2f, UnfilledCaveFogReachMeters = 5f };
            using var wildFogHigh = new GameplayUndergroundModeSettings { FogEndFraction = 3f, UnfilledCaveFogReachMeters = 99999f };
            using var wildFogNaN = new GameplayUndergroundModeSettings { FogEndFraction = float.NaN, UnfilledCaveFogReachMeters = float.NaN };
            using var cleanFogLow = GameplayUndergroundModeDefaults.Sanitize(wildFogLow);
            using var cleanFogHigh = GameplayUndergroundModeDefaults.Sanitize(wildFogHigh);
            using var cleanFogNaN = GameplayUndergroundModeDefaults.Sanitize(wildFogNaN);
            bool fogSanitized = cleanFogLow.FogEndFraction == .5f && cleanFogLow.UnfilledCaveFogReachMeters == 256f
                && cleanFogHigh.FogEndFraction == 1f && cleanFogHigh.UnfilledCaveFogReachMeters == 4096f
                && cleanFogNaN.FogEndFraction == .9f && cleanFogNaN.UnfilledCaveFogReachMeters == 1024f;
            bool caveFog = fogDefaultsMatch && fogPersisted && fogApplied && fogSanitized;
            lighting.Reset();
            bool lightingReset = lighting.Settings.AudioWeatherEnabled && lighting.Settings.ShadowsEnabled && lighting.Settings.EnclosureLightingWeight == 1 && lighting.Dirty && lighting.Save();
            try { File.Delete(ProjectSettings.GlobalizePath(lightingPath)); } catch { }
            bool groupsAndLighting = caveProfile && caveFog && groupDefaults && controllerDefaults && lightingApplied && lightingPersisted && lightingSanitized && depthSanitized && lightingReset;
            result["pass"] = groupsAndLighting && baselineOk && noOp && changed && persisted && untouched && applied && noFileNoApply && sanitized && hysteresis && loadSanitized && reset;
            result["detail"] = $"groupsAndLighting={groupsAndLighting} (caveFog={caveFog} [defaults={fogDefaultsMatch} persisted={fogPersisted} applied={fogApplied} sanitized={fogSanitized}] caveProfile={caveProfile} [defaults={profileDefaultsMatch} persisted={profilePersisted} applied={profileApplied} sanitized={profileSanitized}] defaults={groupDefaults} controller={controllerDefaults} applied={lightingApplied} persisted={lightingPersisted} sanitized={lightingSanitized}/{depthSanitized} reset={lightingReset}) baseline={baselineOk} noop={noOp} changed={changed} persisted={persisted} untouched={untouched} applied={applied} nofile={noFileNoApply} sanitized={sanitized} hysteresis={hysteresis} loadSanitized={loadSanitized} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally { try { string full = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(full)) File.Delete(full); } catch { } }
        return result;
    }
}
