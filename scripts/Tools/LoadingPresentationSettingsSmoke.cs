using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

[GlobalClass]
public partial class LoadingPresentationSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string full = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(full)) File.Delete(full);
            var defaults = new GameplayLoadingPresentationDefaults(isolatedPath);
            defaults.Load();
            bool baseline = defaults.Settings.AnimationSpeed == .18f && defaults.Settings.MotionEnabled
                && defaults.Settings.HologramBrightness == 1 && !defaults.Dirty;
            bool noOp = !defaults.Set(.18f, true, 1) && !defaults.Dirty;
            bool changed = defaults.Set(.32f, false, .75f) && defaults.Dirty && defaults.Save() && !defaults.Dirty;
            var reloaded = new GameplayLoadingPresentationDefaults(isolatedPath);
            reloaded.Load();
            bool persisted = reloaded.Settings.AnimationSpeed == .32f && !reloaded.Settings.MotionEnabled
                && reloaded.Settings.HologramBrightness == .75f;
            bool finite = GameplayLoadingPresentationDefaults.SanitizeSpeed(float.NaN) == .18f
                && GameplayLoadingPresentationDefaults.SanitizeBrightness(float.PositiveInfinity) == 1;
            using var malformed = new GameplayLoadingPresentationSettings { AnimationSpeed = 99, MotionEnabled = false, HologramBrightness = -10 };
            bool malformedSaved = ResourceSaver.Save(malformed, isolatedPath) == Error.Ok;
            var bounded = new GameplayLoadingPresentationDefaults(isolatedPath);
            bounded.Load();
            bool sanitized = malformedSaved && bounded.Settings.AnimationSpeed == .6f && !bounded.Settings.MotionEnabled
                && bounded.Settings.HologramBrightness == .35f;
            bounded.Reset();
            bool reset = bounded.Settings.AnimationSpeed == .18f && bounded.Settings.MotionEnabled
                && bounded.Settings.HologramBrightness == 1 && bounded.Dirty && bounded.Save();
            result["pass"] = baseline && noOp && changed && persisted && finite && sanitized && reset;
            result["detail"] = $"baseline={baseline} noop={noOp} changed={changed} persisted={persisted} finite={finite} sanitized={sanitized} reset={reset}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.ToString(); }
        finally { try { string full = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(full)) File.Delete(full); } catch { } }
        return result;
    }
}
