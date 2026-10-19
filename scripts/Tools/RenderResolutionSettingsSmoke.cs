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
            result["pass"] = initialDefault && noOp && changed && saved && saveNoOp && persisted && invalidFallsBack && reset && jitterPersisted;
            result["detail"] = $"default={initialDefault} noop={noOp} changed={changed} saved={saved} atomicNoop={saveNoOp} persisted={persisted} invalid={invalidFallsBack} reset={reset} jitterCompensation={jitterPersisted}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            try { string absolute = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(absolute)) File.Delete(absolute); } catch { }
        }
        return result;
    }
}
