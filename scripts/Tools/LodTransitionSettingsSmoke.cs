using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only persistence coverage for the shared intermediate LOD transition preference.</summary>
[GlobalClass]
public partial class LodTransitionSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string absolute = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(absolute)) File.Delete(absolute);

            var defaults = new GameplayLodTransitionDefaults(isolatedPath);
            defaults.Load();
            bool initialDefault = defaults.Settings.IntermediateLodTiersEnabled && !defaults.Dirty;
            bool noOp = !defaults.SetIntermediateLodTiersEnabled(true) && !defaults.Dirty;
            bool changed = defaults.SetIntermediateLodTiersEnabled(false) && defaults.Dirty;
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            byte[] firstSavedBytes = File.ReadAllBytes(absolute);
            bool saveNoOp = defaults.Save() && firstSavedBytes.AsSpan().SequenceEqual(File.ReadAllBytes(absolute));

            var loaded = new GameplayLodTransitionDefaults(isolatedPath);
            loaded.Load();
            bool persisted = !loaded.Settings.IntermediateLodTiersEnabled && !loaded.Dirty;

            result["pass"] = initialDefault && noOp && changed && saved && saveNoOp && persisted;
            result["detail"] = $"default={initialDefault} noop={noOp} changed={changed} saved={saved} atomicNoop={saveNoOp} persisted={persisted}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            try { string absolute = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(absolute)) File.Delete(absolute); } catch { }
        }
        return result;
    }
}
