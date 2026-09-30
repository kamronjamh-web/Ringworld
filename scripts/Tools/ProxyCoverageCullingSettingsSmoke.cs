using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>Isolated persistence coverage for the proxy partial-coverage culling toggle.</summary>
[GlobalClass]
public partial class ProxyCoverageCullingSettingsSmoke : RefCounted
{
    public Godot.Collections.Dictionary Run(string isolatedPath)
    {
        var result = new Godot.Collections.Dictionary();
        try
        {
            string absolute = ProjectSettings.GlobalizePath(isolatedPath);
            if (File.Exists(absolute)) File.Delete(absolute);
            var defaults = new GameplayProxyCoverageCullingDefaults(isolatedPath);
            defaults.Load();
            bool initialDefault = defaults.Settings.PartialCoverageMeshCullingEnabled && !defaults.Dirty;
            bool noOp = !defaults.Set(true) && !defaults.Dirty;
            bool changed = defaults.Set(false) && defaults.Dirty;
            bool saved = defaults.Save() && !defaults.Dirty && File.Exists(absolute);
            byte[] firstSaved = File.ReadAllBytes(absolute);
            bool saveNoOp = defaults.Save() && firstSaved.AsSpan().SequenceEqual(File.ReadAllBytes(absolute));
            var loaded = new GameplayProxyCoverageCullingDefaults(isolatedPath);
            loaded.Load();
            bool persisted = !loaded.Settings.PartialCoverageMeshCullingEnabled && !loaded.Dirty;
            result["pass"] = initialDefault && noOp && changed && saved && saveNoOp && persisted;
            result["detail"] = $"default={initialDefault} noop={noOp} changed={changed} saved={saved} atomicNoop={saveNoOp} persisted={persisted}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            try
            {
                string absolute = ProjectSettings.GlobalizePath(isolatedPath);
                if (File.Exists(absolute)) File.Delete(absolute);
            }
            catch { }
        }
        return result;
    }
}
