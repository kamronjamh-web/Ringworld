using System.Text.Json;

namespace RingworldTechDemo.Vegetation.Understory;

/// <summary>
/// TEST ONLY (T6-11, tools/landscape_baseline_common.gd scatter_snapshot): the understory compute's byte counters, source rows per budget slot (saplings, shrubs, dead wood, dead-wood caster,
/// sapling caster), caps and colliders. The drawn-instance readback is on the base class (tools/RingScatterLayerBase.Probe.cs). Nothing here changes rendering.
/// </summary>
internal sealed partial class RingUnderstoryLayer
{
    public string ComputeBytesJson() => JsonSerializer.Serialize(new
    {
        estimated_bytes = _compute?.EstimatedBytes ?? 0L, shared_source_bytes = _compute?.SharedSourceBytes ?? 0L, output_bytes = _compute?.OutputBytes ?? 0L,
        batches = BatchCount, draw_outputs = DrawOutputs, renderers = RendererCount, source_rows = SourceRows, base_caps = UnderstoryBaseCaps, colliders = ColliderCount,
        colliders_eligible = ColliderEligibleCount, collider_radius_m = ColliderEffectiveRadiusMeters, sapling_shadows = SaplingShadows, understory_density = UnderstoryDensity, dead_wood_density = DeadWoodDensity
    });
}
