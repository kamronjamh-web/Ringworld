using System;
using Godot;
using RingworldTechDemo.Shadows;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T6-8 (docs/work-orders/terrain-vegetation/T6.md, design A9): the foliage tint globals of materials/ring_foliage_tint.gdshaderinc.
// Published only in worlds whose sampler mask has bit 6 (vegetation.forest_structure); everywhere else the layer never writes them and the defaults (w = 0) keep the tree shaders on their existing statements.
public partial class RingBiomeDecorationLayer
{
	internal const string FoliageFrameGlobal = "ringworld_foliage_frame", FoliageTintGlobal = "ringworld_foliage_tint";
	private const double FoliageAnchorSnapMeters = 4096.0;
	private const long FoliagePatternMeters = 65536;

	/// <summary>Strength of the per-instance and per-stand foliage colour variation, 0-1 (the Vegetation tab's "Foliage colour variation"); published in ringworld_foliage_tint.x.</summary>
	internal float FoliageColourVariation { get; set; } = 1f;
	private bool _foliageTintPublished;

	/// <summary>The vegetation settings apply (T6-8). Read by the next frame's publish; in a world without bit 6 nothing reads it.</summary>
	internal void ApplyFoliageSettings(float colourVariation) => FoliageColourVariation = Math.Clamp(colourVariation, 0f, 1f);

	/// <summary>Per frame: rebuilds the world -> anchor frame for the floating origin (like the canopy frame) and publishes the strength and the anchor. Main thread only.</summary>
	private void PublishFoliageTint(RingTerrainSampler sampler)
	{
		if ((sampler.WorldFeatureMask & (1 << ForestStructureBit)) == 0 || !TryGetCanopyContext(out RingLodGeometrySnapshot geometry, out RingCanonicalPosition camera))
		{
			if (_foliageTintPublished) { RenderingServer.GlobalShaderParameterSet(FoliageTintGlobal, new Vector4(0, 0, 0, 0)); _foliageTintPublished = false; }
			return;
		}
		long circumference = (long)Math.Round(geometry.CircumferenceMeters);
		long anchorAround = RingHeightShadowLattice.PosMod((long)(Math.Round(camera.AroundMeters / FoliageAnchorSnapMeters) * FoliageAnchorSnapMeters), circumference);
		long anchorAcross = (long)(Math.Round(camera.AcrossMeters / FoliageAnchorSnapMeters) * FoliageAnchorSnapMeters);
		double[] m = RingTerrainHeightShadow.BuildFrameFor(geometry, anchorAround, anchorAcross, circumference);
		double floorRadius = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters;
		// The translation column's free w carries the floor radius (as the canopy frame carries its reference altitude).
		var frame = new Projection(new Vector4((float)m[0], (float)m[4], (float)m[8], 0), new Vector4((float)m[1], (float)m[5], (float)m[9], 0),
			new Vector4((float)m[2], (float)m[6], (float)m[10], 0), new Vector4((float)m[3], (float)m[7], (float)m[11], (float)floorRadius));
		RenderingServer.GlobalShaderParameterSet(FoliageFrameGlobal, Variant.From(frame));
		RenderingServer.GlobalShaderParameterSet(FoliageTintGlobal, new Vector4(FoliageColourVariation,
			RingHeightShadowLattice.PosMod(anchorAround, FoliagePatternMeters), RingHeightShadowLattice.PosMod(anchorAcross, FoliagePatternMeters), 1f));
		_foliageTintPublished = true;
	}
}
