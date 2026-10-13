using System;
using System.IO;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class TerrainStreamingSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string full = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(full)) File.Delete(full);
            var defaults = new GameplayTerrainStreamingDefaults(isolatedPath);
            defaults.Load();
            int automatic = SmoothTerrainGrid.DefaultMaxWorkers;
            bool baseline = defaults.Settings.MaxWorkers == automatic && defaults.Settings.MaxInstallsPerFrame == 8
                && defaults.Settings.InstallBudgetMilliseconds == 1.5f && !defaults.Dirty && !defaults.HasSavedFile
                && automatic >= 2 && automatic <= 8;
            bool noOp = !defaults.Set(automatic, 8, 1.5f) && !defaults.Dirty;
            bool changed = defaults.Set(5, 12, 3f) && defaults.Dirty && defaults.Save() && !defaults.Dirty && defaults.HasSavedFile;
            var reloaded = new GameplayTerrainStreamingDefaults(isolatedPath);
            reloaded.Load();
            bool persisted = reloaded.Settings.MaxWorkers == 5 && reloaded.Settings.MaxInstallsPerFrame == 12
                && reloaded.Settings.InstallBudgetMilliseconds == 3f;
            var grid = new SmoothTerrainGrid();
            reloaded.Apply(grid);
            bool applied = grid.MaxWorkers == 5 && grid.MaxInstallsPerFrame == 12 && grid.InstallBudgetMilliseconds == 3f;
            grid.Free();
            bool finite = GameplayTerrainStreamingDefaults.SanitizeBudget(float.NaN) == 1.5f
                && GameplayTerrainStreamingDefaults.SanitizeWorkers(-3) == 1 && GameplayTerrainStreamingDefaults.SanitizeInstalls(9999) == 32;
            using var malformed = new GameplayTerrainStreamingSettings { MaxWorkers = 999, MaxInstallsPerFrame = 0, InstallBudgetMilliseconds = -10 };
            bool malformedSaved = ResourceSaver.Save(malformed, isolatedPath) == Error.Ok;
            var bounded = new GameplayTerrainStreamingDefaults(isolatedPath);
            bounded.Load();
            bool sanitized = malformedSaved && bounded.Settings.MaxWorkers == 16 && bounded.Settings.MaxInstallsPerFrame == 1
                && bounded.Settings.InstallBudgetMilliseconds == .25f;
            bounded.Reset();
            bool reset = bounded.Settings.MaxWorkers == automatic && bounded.Settings.MaxInstallsPerFrame == 8
                && bounded.Settings.InstallBudgetMilliseconds == 1.5f && bounded.Dirty && bounded.Save();
            result["pass"] = baseline && noOp && changed && persisted && applied && finite && sanitized && reset;
            result["detail"] = $"baseline={baseline} noop={noOp} changed={changed} persisted={persisted} applied={applied} finite={finite} sanitized={sanitized} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally { try { string full = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(full)) File.Delete(full); } catch { } }
        return result;
    }
}
