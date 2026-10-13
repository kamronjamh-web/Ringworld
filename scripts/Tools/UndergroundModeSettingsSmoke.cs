using System;
using System.IO;
using Godot;
using RingworldTechDemo.Underground;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class UndergroundModeSettingsSmoke : RefCounted
{
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
            result["pass"] = baselineOk && noOp && changed && persisted && untouched && applied && noFileNoApply && sanitized && hysteresis && loadSanitized && reset;
            result["detail"] = $"baseline={baselineOk} noop={noOp} changed={changed} persisted={persisted} untouched={untouched} applied={applied} nofile={noFileNoApply} sanitized={sanitized} hysteresis={hysteresis} loadSanitized={loadSanitized} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally { try { string full = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(full)) File.Delete(full); } catch { } }
        return result;
    }
}
