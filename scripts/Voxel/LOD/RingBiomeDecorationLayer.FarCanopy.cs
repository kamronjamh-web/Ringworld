using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T6-10 (docs/work-orders/terrain-vegetation/T6.md, design B5): the ringworld_far_canopy global. Published only in worlds whose sampler mask has bit 8 (vegetation.far_canopy); elsewhere the layer never
// writes it, and the default (0, 0, 0, 0) keeps the ground shaders on their existing statements.
public partial class RingBiomeDecorationLayer
{
	private float _farCanopyStart = -1f;

	/// <summary>Per frame (cheap): publishes the canopy albedo and the tree far distance (MaximumLod5DistanceMeters) once, and again when that distance changes. Main thread only.</summary>
	private void PublishFarCanopy(RingTerrainSampler sampler)
	{
		bool on = (sampler.WorldFeatureMask & (1 << RingForestCanopyFold.Bit)) != 0;
		if (!on)
		{
			if (_farCanopyStart >= 0f) { RingFarCanopy.Clear(); _farCanopyStart = -1f; }
			return;
		}
		if (_farCanopyStart == MaximumLod5DistanceMeters) return;
		RingFarCanopy.Publish(MaximumLod5DistanceMeters);
		_farCanopyStart = MaximumLod5DistanceMeters;
	}
}
