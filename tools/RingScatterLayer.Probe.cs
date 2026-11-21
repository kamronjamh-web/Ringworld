using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// TEST ONLY (T5-10, tools/landscape_baseline_common.gd): the scatter compute's byte counters and the layer's rows and colliders. The drawn-instance readback (DrawnProbeBegin / DrawnProbeTake)
/// lives on the base class (tools/RingScatterLayerBase.Probe.cs). Nothing here changes rendering.
/// </summary>
internal sealed partial class RingScatterLayer
{
    public string ComputeBytesJson() => JsonSerializer.Serialize(new
    {
        estimated_bytes = _compute?.EstimatedBytes ?? 0L, shared_source_bytes = _compute?.SharedSourceBytes ?? 0L, output_bytes = _compute?.OutputBytes ?? 0L,
        batches = BatchCount, draw_outputs = DrawOutputs, renderers = RendererCount, source_rows = SourceRows, caster_rows = CasterRows, colliders = ColliderCount,
        colliders_eligible = ColliderEligibleCount, collider_radius_m = ColliderEffectiveRadiusMeters
    });

    /// <summary>TEST ONLY (black-rock check, play test 2026-11-20): the ring centre the boulder LOD0 material holds, forwarded from the Terrain3D surface; NaN before the layer has materials.</summary>
    public Vector3 RockRingCenterProbe() => _materials?.Get(RingScatterRenderGroup.Boulders, 0).GetShaderParameter("ring_atmosphere_center_world").AsVector3() ?? new Vector3(float.NaN, float.NaN, float.NaN);
}
