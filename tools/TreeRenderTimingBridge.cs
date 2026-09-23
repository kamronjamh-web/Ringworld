using Godot;
using RingworldTechDemo.Voxel.LOD;

/// <summary>Small read-only bridge for render attribution fixture invariants.</summary>
public partial class TreeRenderTimingBridge : Node
{
    [Signal] public delegate void GpuLodCountsReadyEventHandler(Godot.Collections.Dictionary counts);
    public Godot.Collections.Dictionary Capture(Node trees)
    {
        Godot.Collections.Dictionary diagnostics = trees.Call("GetImportedForestRuntimeDiagnostics").AsGodotDictionary();
        return new Godot.Collections.Dictionary
        {
        ["canonical_record_count"] = diagnostics.TryGetValue("canonical_record_count", out Variant records) ? records : 0,
        ["gpu_root_count"] = trees.Get("GpuTreeResidentRootCount"),
        ["gpu_batch_count"] = trees.Get("GpuTreeBatchCount"),
        ["gpu_source_epoch"] = trees.Get("GpuTreeSourceEpoch"),
        ["completed_updates"] = trees.Get("GpuTreeCompletedUpdates"),
        ["shadow_renderer_count"] = trees.Get("ActiveGpuTreeSunShadowRendererCount")
        };
    }

    /// <summary>Schedules one readback per installed tree batch outside timing windows.</summary>
    public void CaptureGpuLodCounts(Node trees)
    {
        if (trees is not RingBiomeDecorationLayer layer) { EmitSignal(SignalName.GpuLodCountsReady, new Godot.Collections.Dictionary()); return; }
        var result = new Godot.Collections.Dictionary();
        int pending = 0;
        for (int lod = 0; lod < 5; lod++) pending += layer.GpuTreeFixtureOutputIds(lod).Length;
        if (pending == 0) { EmitSignal(SignalName.GpuLodCountsReady, result); return; }
        for (int lod = 0; lod < 5; lod++)
        {
            int currentLod = lod;
            int[] ids = layer.GpuTreeFixtureOutputIds(currentLod);
            var entry = new Godot.Collections.Dictionary { ["batches"] = ids.Length, ["visible_instances"] = 0L, ["unreadable_batches"] = 0 };
            result[currentLod] = entry;
            foreach (int batch in ids) layer.GpuTreeFixtureReadbackBatch(batch, (commands, _) =>
            {
                if (commands.Length < 2) entry["unreadable_batches"] = entry["unreadable_batches"].AsInt32() + 1;
                entry["visible_instances"] = entry["visible_instances"].AsInt64() + (commands.Length >= 2 ? commands[1] : 0);
                if (--pending == 0) EmitSignal(SignalName.GpuLodCountsReady, result);
            });
        }
    }
}
