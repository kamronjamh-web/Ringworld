using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only persistence coverage for gameplay render-resolution preferences.</summary>
[GlobalClass]
public partial class RenderResolutionSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string absolute = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(absolute)) File.Delete(absolute);

            var defaults = new GameplayRenderResolutionDefaults(isolatedPath);
            defaults.Load();
            bool initialDefault = defaults.Settings.Preset == 0 && !defaults.Settings.AdaptiveResolutionEnabled && !defaults.Dirty;
            bool noOp = !defaults.Set(0, false) && !defaults.Dirty;
            bool changed = defaults.Set(2, true) && defaults.Dirty;
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            byte[] firstSavedBytes = File.ReadAllBytes(absolute);
            bool saveNoOp = defaults.Save() && firstSavedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(absolute));

            var loaded = new GameplayRenderResolutionDefaults(isolatedPath);
            loaded.Load();
            bool persisted = loaded.Settings.Preset == 2 && loaded.Settings.AdaptiveResolutionEnabled && !loaded.Dirty;

            bool jitterDefault = loaded.Settings.CompositorJitterCompensation;
            bool jitterSet = loaded.SetFsrOptions(loaded.Settings.FsrSharpness, loaded.Settings.MipBiasOffset, loaded.Settings.DisableCloudInterleaveUnderFsr,
                loaded.Settings.FarViewportFollowsFsrScale, false) && loaded.Save();
            var jitterReloaded = new GameplayRenderResolutionDefaults(isolatedPath);
            jitterReloaded.Load();
            bool jitterPersisted = jitterDefault && jitterSet && !jitterReloaded.Settings.CompositorJitterCompensation && jitterReloaded.Settings.Preset == 2;

            bool wroteInvalid = ResourceSaver.Save(new GameplayRenderResolutionSettings { Preset = 999, AdaptiveResolutionEnabled = true }, isolatedPath) == Error.Ok;
            var invalid = new GameplayRenderResolutionDefaults(isolatedPath);
            invalid.Load();
            bool invalidFallsBack = wroteInvalid && invalid.Settings.Preset == 0 && invalid.Settings.AdaptiveResolutionEnabled;

            invalid.Reset();
            bool reset = invalid.Settings.Preset == 0 && !invalid.Settings.AdaptiveResolutionEnabled && invalid.Dirty && invalid.Save();
            // Auto (6): persists, validates, is the menu's missing-file fallback; 7 and negatives fall back to Native.
            if (File.Exists(absolute)) File.Delete(absolute); // Load(6) only falls back when no file exists.
            var autoFile = new GameplayRenderResolutionDefaults(isolatedPath);
            autoFile.Load(6);
            bool autoFallback = autoFile.Settings.Preset == 6 && !autoFile.Dirty;
            bool autoSet = autoFile.Set(6, false) == false && autoFile.Set(2, false) && autoFile.Set(6, true) && autoFile.Save();
            var autoReloaded = new GameplayRenderResolutionDefaults(isolatedPath);
            autoReloaded.Load();
            bool autoPersisted = autoSet && autoReloaded.Settings.Preset == 6 && autoReloaded.Settings.AdaptiveResolutionEnabled;
            bool autoValidate = GameplayRenderResolutionDefaults.ValidatePreset(6) == 6 && GameplayRenderResolutionDefaults.ValidatePreset(7) == 0
                && GameplayRenderResolutionDefaults.ValidatePreset(-1) == 0 && GameplayRenderResolutionDefaults.ValidatePreset(5) == 5;
            // A saved explicit Native keeps meaning Native (no migration to Auto).
            bool nativeKept = autoReloaded.Set(0, true) && autoReloaded.Save();
            var nativeReloaded = new GameplayRenderResolutionDefaults(isolatedPath);
            nativeReloaded.Load(6);
            nativeKept = nativeKept && nativeReloaded.Settings.Preset == 0;

            // Pure Auto resolver: every graphics preset at 1080 and 1440 (plus the 1300 threshold).
            var Q = Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.Low;
            bool resolver = true;
            (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset preset, int height, Voxel.LOD.RingLodPauseMenu.UpscalingPreset expected)[] table =
            {
                (Q, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Performance), (Q, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Performance),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.Medium, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Performance),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.Medium, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Performance),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.High, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.High, 1299, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.High, 1300, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Balanced),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.High, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Balanced),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.VeryHigh, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.VeryHigh, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Fsr2Balanced),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.UltraVideo, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.UltraVideo, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.Cinematic5090, 1080, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
                (Voxel.LOD.RingTerrainLodCoordinator.QualityPreset.Cinematic5090, 1440, Voxel.LOD.RingLodPauseMenu.UpscalingPreset.Native),
            };
            foreach (var row in table)
                resolver &= Voxel.LOD.RingLodPauseMenu.ResolveAutoUpscalingPreset(row.preset, row.height) == row.expected;
            bool autoLabel = Voxel.LOD.RingLodPauseMenu.GetRenderResolutionPresetLabel(6) == "Auto (graphics preset)";

            result["pass"] = autoFallback && autoPersisted && autoValidate && nativeKept && resolver && autoLabel && initialDefault && noOp && changed && saved && saveNoOp && persisted && invalidFallsBack && reset && jitterPersisted;
            result["detail"] = $"default={initialDefault} noop={noOp} changed={changed} saved={saved} atomicNoop={saveNoOp} persisted={persisted} invalid={invalidFallsBack} reset={reset} jitterCompensation={jitterPersisted} autoFallback={autoFallback} autoPersisted={autoPersisted} autoValidate={autoValidate} nativeKept={nativeKept} resolver={resolver} autoLabel={autoLabel}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            try { string absolute = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(absolute)) File.Delete(absolute); } catch { }
        }
        return result;
    }
}
