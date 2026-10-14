using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class LodSchedulingSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string full = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(full)) File.Delete(full);
            var defaults = new GameplayLodSchedulingDefaults(isolatedPath);
            defaults.Load();
            bool baseline = defaults.Settings.MaxBuildStartsPerFrame == 1
                && defaults.Settings.BuildStartBudgetMilliseconds == 2f
                && defaults.Settings.StarvationFrames == 30 && !defaults.Dirty && !defaults.HasSavedFile;
            bool noOp = !defaults.Set(1, 2f, 30) && !defaults.Dirty;
            bool changed = defaults.Set(3, 4.5f, 60) && defaults.Dirty && defaults.Save() && !defaults.Dirty && defaults.HasSavedFile;
            bool atomicNoOp = defaults.Save() && !defaults.Dirty;
            var reloaded = new GameplayLodSchedulingDefaults(isolatedPath);
            reloaded.Load();
            bool persisted = reloaded.Settings.MaxBuildStartsPerFrame == 3
                && reloaded.Settings.BuildStartBudgetMilliseconds == 4.5f && reloaded.Settings.StarvationFrames == 60;
            var coordinator = new RingTerrainLodCoordinator();
            reloaded.Apply(coordinator);
            bool applied = coordinator.MaxBuildStartsPerFrame == 3
                && coordinator.MaxBuildStartMillisecondsPerFrame == 4.5f && coordinator.BuildStartStarvationFrames == 60;
            coordinator.Free();
            bool finite = GameplayLodSchedulingDefaults.SanitizeBudget(float.NaN) == 2f
                && GameplayLodSchedulingDefaults.SanitizeStarts(-3) == 1
                && GameplayLodSchedulingDefaults.SanitizeStarvationFrames(9999) == 300;
            using var malformed = new GameplayLodSchedulingSettings { MaxBuildStartsPerFrame = 99, BuildStartBudgetMilliseconds = -10, StarvationFrames = 0 };
            bool malformedSaved = ResourceSaver.Save(malformed, isolatedPath) == Error.Ok;
            var bounded = new GameplayLodSchedulingDefaults(isolatedPath);
            bounded.Load();
            bool sanitized = malformedSaved && bounded.Settings.MaxBuildStartsPerFrame == 8
                && bounded.Settings.BuildStartBudgetMilliseconds == .25f && bounded.Settings.StarvationFrames == 5;
            bounded.Reset();
            bool reset = bounded.Settings.MaxBuildStartsPerFrame == 1 && bounded.Settings.BuildStartBudgetMilliseconds == 2f
                && bounded.Settings.StarvationFrames == 30 && bounded.Dirty && bounded.Save();
            result["pass"] = baseline && noOp && changed && atomicNoOp && persisted && applied && finite && sanitized && reset;
            result["detail"] = $"baseline={baseline} noop={noOp} changed={changed} atomicNoOp={atomicNoOp} persisted={persisted} applied={applied} finite={finite} sanitized={sanitized} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally { try { string full = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(full)) File.Delete(full); } catch { } }
        return result;
    }
}
