using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>CPU-only persistence coverage for the shared intermediate LOD transition preference.</summary>
[GlobalClass]
public partial class LodTransitionSettingsSmoke : RefCounted
{
    private static bool Near(float a, float b, float tolerance = 1e-4f) => Math.Abs(a - b) <= tolerance;

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

            // Proxy detail settings (U-1 / U-2): defaults, set/save/reload, sanitize, older files.
            bool detailDefaults = defaults.Settings.FarProxyErrorScale == 2f;
            bool detailNoOp = !defaults.SetFarProxyErrorScale(2f);
            bool detailSet = defaults.SetFarProxyErrorScale(2.5f) && defaults.Dirty;
            bool detailSaved = defaults.Save();
            var detailLoaded = new GameplayLodTransitionDefaults(isolatedPath);
            detailLoaded.Load();
            bool detailPersisted = detailLoaded.Settings.FarProxyErrorScale == 2.5f
                && !detailLoaded.Settings.IntermediateLodTiersEnabled;
            bool detailNaN = detailLoaded.SetFarProxyErrorScale(float.NaN)
                && detailLoaded.Settings.FarProxyErrorScale == 2f;
            detailLoaded.SetFarProxyErrorScale(9f);
            bool clampHigh = detailLoaded.Settings.FarProxyErrorScale == 4f;
            detailLoaded.SetFarProxyErrorScale(0.2f);
            bool clampLow = detailLoaded.Settings.FarProxyErrorScale == 1f;
            detailLoaded.Reset();
            bool resetOk = detailLoaded.Settings.FarProxyErrorScale == 2f;

            // Older file without the new fields: strip their lines and reload.
            string text = File.ReadAllText(absolute);
            var kept = new System.Collections.Generic.List<string>();
            foreach (string line in text.Split('\n'))
                if (!line.StartsWith("FarProxyErrorScale")) kept.Add(line);
            File.WriteAllText(absolute, string.Join("\n", kept));
            var older = new GameplayLodTransitionDefaults(isolatedPath);
            older.Load();
            bool olderOk = older.LastError.Length == 0 && older.Settings.FarProxyErrorScale == 2f
                && !older.Settings.IntermediateLodTiersEnabled;

            // A retired save that still carries Lod1ReachCapMeters must load quietly.
            File.WriteAllText(absolute, text.TrimEnd() + "\nLod1ReachCapMeters = 300.0\n");
            var retired = new GameplayLodTransitionDefaults(isolatedPath);
            retired.Load();
            bool retiredOk = retired.LastError.Length == 0 && retired.Settings.FarProxyErrorScale == 2.5f;

            // Pure planner math.
            bool mathOk =
                Near(RingTerrainLodChainPlanner.FarLevelErrorScale(16f, 2f), 1f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(32f, 2f), 1.414f, 1e-3f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(64f, 2f), 2f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(256f, 2f), 2f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(256f, 4f), 4f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(64f, 1f), 1f)
                && Near(RingTerrainLodChainPlanner.FarLevelErrorScale(64f, float.NaN), 1f);
            bool orderOk = true;
            foreach (float farScale in new[] { 2f, 4f })
            {
                float previous = 0f;
                for (float spacing = 2f; spacing <= 16384f; spacing *= 2f)
                {
                    float effective = spacing / RingTerrainLodChainPlanner.FarLevelErrorScale(spacing, farScale);
                    if (effective <= previous) orderOk = false;
                    previous = effective;
                }
            }

            bool detailOk = detailDefaults && detailNoOp && detailSet && detailSaved && detailPersisted && detailNaN
                && clampHigh && clampLow && resetOk && olderOk && retiredOk && mathOk && orderOk;
            result["pass"] = initialDefault && noOp && changed && saved && saveNoOp && persisted && detailOk;
            result["detail"] = $"default={initialDefault} noop={noOp} changed={changed} saved={saved} atomicNoop={saveNoOp} persisted={persisted} "
                + $"detailDefaults={detailDefaults} detailNoOp={detailNoOp} detailSet={detailSet} detailSaved={detailSaved} detailPersisted={detailPersisted} "
                + $"detailNaN={detailNaN} clampHigh={clampHigh} clampLow={clampLow} reset={resetOk} older={olderOk} retired={retiredOk} math={mathOk} order={orderOk}";
        }
        catch (Exception error) { result["pass"] = false; result["detail"] = error.Message; }
        finally
        {
            try { string absolute = ProjectSettings.GlobalizePath(isolatedPath); if (File.Exists(absolute)) File.Delete(absolute); } catch { }
        }
        return result;
    }
}
