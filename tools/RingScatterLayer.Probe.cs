using System;
using System.Collections.Generic;
using System.Text.Json;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// TEST ONLY (T5-10, tools/landscape_baseline_common.gd): a readback of what the scatter layer draws right now, per output (name, group, LOD, caster): the instance count of the batch's camera list
/// (full list for caster batches). Call DrawnProbeBegin(), poll DrawnProbeTake() until it is not empty. Plus the compute's byte counters. Nothing here changes rendering.
/// </summary>
internal sealed partial class RingScatterLayer
{
    private List<object>? _drawnRows;
    private int _drawnPending;

    public void DrawnProbeBegin()
    {
        var rows = new List<object>();
        _drawnRows = null;
        List<(string Name, int BatchId, int Group, int Lod, bool Caster)> info = OutputInfo();
        if (info.Count == 0 || _compute == null) { _drawnRows = rows; _drawnPending = 0; return; }
        _drawnPending = info.Count;
        foreach ((string name, int id, int group, int lod, bool caster) in info)
        {
            Action<uint[]> done = commands =>
            {
                rows.Add(new { name, group, lod, caster, count = commands.Length >= 2 ? (long)commands[1] : -1L });
                if (--_drawnPending == 0) _drawnRows = rows;
            };
            if (caster) _compute.RequestReadbackForTests(id, (c, _) => done(c));
            else _compute.RequestCameraReadbackForTests(id, (c, _) => done(c));
        }
    }

    /// <summary>"" until every readback has come back, then a JSON array.</summary>
    public string DrawnProbeTake() => _drawnRows == null ? "" : JsonSerializer.Serialize(_drawnRows);

    public string ComputeBytesJson() => JsonSerializer.Serialize(new
    {
        estimated_bytes = _compute?.EstimatedBytes ?? 0L, shared_source_bytes = _compute?.SharedSourceBytes ?? 0L, output_bytes = _compute?.OutputBytes ?? 0L,
        batches = BatchCount, draw_outputs = DrawOutputs, renderers = RendererCount, source_rows = SourceRows, caster_rows = CasterRows, colliders = ColliderCount,
        colliders_eligible = ColliderEligibleCount, collider_radius_m = ColliderEffectiveRadiusMeters
    });
}
