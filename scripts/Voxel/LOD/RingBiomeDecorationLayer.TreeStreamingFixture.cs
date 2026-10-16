using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    internal readonly record struct VegetationStreamingCounters(long Plans, long Generated, long Evicted, long Reloaded,
        long TranslationRebases, long FullRebases, int Cached, int Desired, int Keep);

    internal VegetationStreamingCounters CaptureVegetationStreamingCounters() => new(VegetationPlanCount, VegetationTilesGenerated,
        VegetationTilesEvicted, VegetationTilesReloadedAfterEviction, VegetationTranslationRebaseCount, VegetationFullRebaseCount,
        _vegetationTiles.Count, _desiredVegetationTiles.Count, _keepVegetationTiles.Count);

    // Headless streaming-contract seam. It runs the production planning, cancellation and keep-band eviction on
    // synthetic tiles: every desired tile that is not cached completes instantly, as an ideal worker would. It
    // touches neither the sampler, the scene, nor the GPU path.
    internal bool StepVegetationStreamingFixture(RingCanonicalPosition focus, double circumference, double ringWidth,
        bool ringCenterMoved = false, bool translationOnlyRebase = false)
    {
        VegetationLayout layout = CaptureVegetationLayout(circumference);
        bool fullRebase = ringCenterMoved && ResolveVegetationRebase(translationOnlyRebase);
        if (!PlanVegetationSets(focus, focus, layout, ringWidth, fullRebase, false, Vector3.Forward, out _)) return false;
        foreach (VegetationTileKey key in _desiredVegetationTiles)
        {
            if (_vegetationTiles.ContainsKey(key)) continue;
            _vegetationTiles[key] = new VegetationTile { Key = key, Trees = new List<CachedTree>(), EditVersion = _vegetationEditVersion, LastUsed = ++_vegetationUseClock };
            if (_evictedVegetationKeys.Remove(key)) VegetationTilesReloadedAfterEviction++;
            VegetationTilesGenerated++;
        }
        EvictVegetationCache();
        return true;
    }
}
