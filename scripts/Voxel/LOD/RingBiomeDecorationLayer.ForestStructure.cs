using System;
using System.Collections.Generic;
using System.Linq;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

// T6 design A2-A4 (docs/work-orders/terrain-vegetation/T6.md): canopy structure for worlds with vegetation.forest_structure (bit 6).
// Everything here is reached only through the bit-6 gate in TreeStreaming.cs; with the bit off the generation 1 calls and arithmetic are the existing ones.
public partial class RingBiomeDecorationLayer
{
	internal const int ForestStructureBit = 6;

	// True when the open feature must not use the tree disk cache (review worlds always regenerate; after the freeze the version belongs to that generation).
	private static readonly bool ForestStructureFeatureOpen = WorldGenerationLedger.Production.Features.First(feature => feature.Id == ForestStructureSampler.FeatureId).FrozenIn == 0;

	private static bool ForestStructureMask(int worldFeatureMask) => (worldFeatureMask & (1 << ForestStructureBit)) != 0;

	/// <summary>The structured canopy applies: the world has bit 6 and the build uses the regional forest.</summary>
	private static bool ForestStructureActive(in TileBuildRequest request) => request.UseRegionalForest && ForestStructureMask(request.Sampler.WorldFeatureMask);

	private static bool ForestStructureDiskCacheBlocked(int worldFeatureMask) => ForestStructureMask(worldFeatureMask) && ForestStructureFeatureOpen;

	/// <summary>The production ledger's feature IDs whose bit is set in the mask.</summary>
	internal static IReadOnlySet<string> FeatureSetOfMask(int worldFeatureMask)
		=> WorldGenerationLedger.Production.Features.Where(feature => (worldFeatureMask & (1 << feature.Bit)) != 0).Select(feature => feature.Id).ToHashSet(StringComparer.Ordinal);

	internal static string ForestStructureIdentitySuffix(int worldFeatureMask)
		=> ForestStructureMask(worldFeatureMask)
			? "|fs" + ForestStructureSampler.AlgorithmVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|dc" + DerivedTreeCatalog.CatalogVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) // A6a: derived catalog version, only in bit-6 worlds
			: "";

	/// <summary>The vegetation disk-cache identity definition string (hashed into the identity directory); generation 1 (mask 0) strings are exactly the pre-T6 ones.</summary>
	internal static string VegetationIdentityDefinition(int generator, int seed, ulong definitionHash, ulong settings, bool emitterSites, bool climateMachines, bool weatherLung, int worldFeatureMask)
		=> FormattableString.Invariant(
			$"v{VegetationDiskCacheVersion}|g{generator}|seed{seed}|h{definitionHash:X16}|s{settings:X16}{(emitterSites ? "|es1" : "")}{(climateMachines ? "|cm1" : "")}{(weatherLung ? "|wl1" : "")}{ForestStructureIdentitySuffix(worldFeatureMask)}");

	private static ForestStructureSampler ForestStructureFor(in TileBuildRequest request)
		=> ForestStructureSampler.For(request.Seed, (long)Math.Ceiling(request.CircumferenceMeters), (int)Math.Ceiling(request.RingWidth), ForestStructureSampler.ContrastOf(request.Sampler));

	/// <summary>The candidate chance before ApplyGroveDensity: regional density x k(a) x tau_d(T).</summary>
	private static float StructuredChanceBase(ForestStructureSampler structure, long around, int across, in TerrainSample terrain, in ForestRegionSample regional)
		=> regional.Density * ForestStructureSampler.StemFactor(structure.EffectiveAge(around, across, regional, terrain.Temperature))
			* ForestStructureSampler.TreelineDensity(terrain.Temperature);

	/// <summary>Spacing radius of a root: ForestLocalSpacingMeters x (0.75 + 0.55 a), from the root's own position, terrain and regional sample.</summary>
	private static float StructuredSpacingMeters(ForestStructureSampler structure, long around, int across, in TerrainSample terrain, in ForestRegionSample regional)
		=> ForestLocalSpacingMeters(terrain.Moisture, regional.Density)
			* ForestStructureSampler.SpacingFactor(structure.EffectiveAge(around, across, regional, terrain.Temperature));

	/// <summary>
	/// Species by habitat (A3) from the 75 % habitat / 25 % pocket weights; the habitat fields W, R64 and X come lazily from the coarse nodes of the tile's habitat grid (created on the first accepted tree).
	/// </summary>
	private static TreeSpecies SelectStructuredSpecies(in TileBuildRequest request, ref RingHabitatFieldGrid? habitat, RingTerrainSampler sampler,
		long around, int across, in TerrainSample terrain, float slopeDegrees, in ForestRegionSample regional, float random)
	{
		long circumference = (long)Math.Ceiling(request.CircumferenceMeters);
		int width = (int)Math.Ceiling(request.RingWidth);
		habitat ??= new RingHabitatFieldGrid(sampler, request.Edits, request.Seed, circumference, width);
		SampleCoarseHabitat(habitat, circumference, width, around, across, out float wetness, out float relief64, out float rock);
		ForestStructureSampler.SpeciesWeights(terrain.Moisture, terrain.Temperature, wetness, relief64, rock, slopeDegrees, regional, out float maple, out float fir, out _);
		return random < maple ? TreeSpecies.Maple : random < maple + fir ? TreeSpecies.Fir : TreeSpecies.Pine;
	}

	/// <summary>The class from the species' table at effective age (hash salt + 8), then a variant uniformly inside the class (hash salt + 4, the generation 1 variant hash).</summary>
	private static int SelectStructuredVariant(TreeSpecies species, float effectiveAge, float classRandom, float variantRandom)
	{
		TreeSizeClass sizeClass = ForestStructureSampler.SelectSizeClass(species, effectiveAge, classRandom);
		var variants = ImportedTreeSourceTable.CanopyVariants(species, sizeClass);
		return variants[Math.Clamp((int)(variantRandom * variants.Count), 0, variants.Count - 1)];
	}

	/// <summary>Bilinear W (WetnessTotal), R64 and X (RockExposure) at a root from the four surrounding COARSE nodes (around wraps, across clamps, as the grid does).</summary>
	private static void SampleCoarseHabitat(RingHabitatFieldGrid grid, long circumference, int width, double around, double across,
		out float wetness, out float relief64, out float rock)
	{
		const double coarseM = RingHabitatFieldGrid.CoarseM;
		double w = around % circumference;
		if (w < 0.0) w += circumference;
		double acrossMax = ((width - 1) / 32) * coarseM;
		double a = Math.Clamp(across, 0.0, acrossMax);
		long i0 = (long)Math.Floor(w / coarseM);
		int j0 = (int)Math.Floor(a / coarseM);
		double fa = w / coarseM - i0, fc = a / coarseM - j0;
		RingHabitatSample n00 = grid.CoarseNode(i0, j0);
		RingHabitatSample n10 = fa > 0.0 ? grid.CoarseNode(i0 + 1, j0) : n00;
		RingHabitatSample n01 = fc > 0.0 ? grid.CoarseNode(i0, j0 + 1) : n00;
		RingHabitatSample n11 = fa > 0.0 && fc > 0.0 ? grid.CoarseNode(i0 + 1, j0 + 1) : n00;
		double Blend(double v00, double v10, double v01, double v11) => (v00 * (1.0 - fa) + v10 * fa) * (1.0 - fc) + (v01 * (1.0 - fa) + v11 * fa) * fc;
		wetness = (float)Blend(n00.WetnessTotal, n10.WetnessTotal, n01.WetnessTotal, n11.WetnessTotal);
		relief64 = (float)Blend(n00.Relief64, n10.Relief64, n01.Relief64, n11.Relief64);
		rock = (float)Blend(n00.RockExposure, n10.RockExposure, n01.RockExposure, n11.RockExposure);
	}
}
