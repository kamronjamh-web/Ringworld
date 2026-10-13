using System;
using System.Collections.Generic;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>
/// Certifies the interval/Lipschitz uniform-lattice proof used for non-base
/// terrain tiers: per-family Lipschitz constants, brute-force soundness of the
/// proof against full lattice sampling, effectiveness, and edge cases.
/// </summary>
public static class CaveUniformProofDiagnostics
{
	public readonly record struct SeedResult(
		string Summary,
		int Lattices,
		int WithContributors,
		int ProvenEmpty,
		int ProvenFallThrough,
		int ReferenceChecks,
		int BoundSamples,
		int LipschitzPairs,
		int EffectivenessProven,
		int EffectivenessInterior);

	private static readonly int[] Spacings = { 2, 4, 8, 16, 32 };
	private const int Cells = SmoothTerrainChunkSnapshot.Cells;
	private const int Halo = SmoothTerrainChunkSnapshot.Halo;

	private sealed record Entry(string Label, CaveFeatureShape Shape);

	/// <summary>Throws on any mismatch; the proof must be sound, never merely effective.</summary>
	public static SeedResult RunSeed(
		int seed,
		RingTerrainSampler sampler,
		int structuralBottom,
		int terrainBottom,
		int protectedRoofThickness,
		int lowerHullTop,
		int structuralWallTop)
	{
		long aroundLength = sampler.AroundRingVoxelLength;
		var baseline = new RingTerrainSmoothBaseline(
			sampler, structuralBottom, terrainBottom, protectedRoofThickness,
			lowerHullTop, structuralWallTop, enableDesignedCaves: true);
		var noEdits = new SmoothTerrainEditSnapshot(
			Array.Empty<SmoothTerrainEditCommand>(), revision: 31, aroundLength);
		var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, noEdits);

		List<Entry> entries = CollectShapes(sampler, out string missing);
		if (entries.Count == 0)
			throw new InvalidOperationException($"seed {seed}: no generated cave shapes were resolved");

		// 1. Lipschitz constants and 10^4-pair numerical check per shape.
		var maxK = new Dictionary<string, double>();
		int pairs = 0;
		double worstRatio = 0.0;
		foreach (Entry entry in entries)
		{
			double k = CaveTopologyDensityComposer.FamilyLipschitzBound(entry.Shape);
			if (!(k >= 1.0) || k > 2.0)
				throw new InvalidOperationException($"seed {seed}: {entry.Label} needs Lipschitz K={k} (> 2)");
			string family = entry.Shape.Family.ToString();
			maxK[family] = maxK.TryGetValue(family, out double previous) ? Math.Max(previous, k) : k;
			pairs += CheckLipschitz(seed, entry, k, ref worstRatio);
		}

		// 2. Brute-force soundness over neighbourhoods of every anchor.
		int lattices = 0, withContributors = 0, provenEmpty = 0, provenFallThrough = 0;
		int referenceChecks = 0, boundSamples = 0;
		int effectivenessProven = 0, effectivenessInterior = 0;
		SmoothTerrainVolumetricDensityCacheKey? provenKey = null;
		var random = new Random(seed);
		int shapeNumber = 0;
		foreach (Entry entry in entries)
		{
			CaveFeatureShape shape = entry.Shape;
			bool large = shape.Family is CaveFeatureFamily.Megacavern or CaveFeatureFamily.SupermassiveCavern;
			shapeNumber++;
			foreach (int spacing in Spacings)
			{
				long step = (long)Cells * spacing;
				var offsets = new List<(long, long, long, bool)>();
				for (int a = -2; a <= 2; a++)
				for (int v = -2; v <= 2; v++)
				for (int x = -2; x <= 2; x++)
					offsets.Add((a * step, v * step, x * step, true));
				// Half-chunk shifts put box faces across cave walls.
				for (int a = -1; a <= 1; a++)
				for (int v = -1; v <= 1; v++)
				for (int x = -1; x <= 1; x++)
					offsets.Add((a * step + step / 2, v * step + step / 2, x * step + step / 2, false));

				foreach ((long da, long dv, long dx, bool chunkAligned) in offsets)
				{
					long aroundFloor = FloorDiv(shape.Around, step) * step;
					long verticalFloor = FloorDiv(shape.Vertical, step) * step;
					long acrossFloor = FloorDiv(shape.Across, step) * step;
					long aroundOrigin = aroundFloor + da - (long)Halo * spacing;
					long verticalOrigin = verticalFloor + dv - (long)Halo * spacing;
					long acrossOrigin = acrossFloor + dx - (long)Halo * spacing;
					if (verticalOrigin < int.MinValue / 2 || verticalOrigin > int.MaxValue / 2
						|| acrossOrigin < int.MinValue / 2 || acrossOrigin > int.MaxValue / 2)
						continue;
					var key = new SmoothTerrainVolumetricDensityCacheKey(
						field.Identity,
						aroundOrigin,
						(int)verticalOrigin,
						(int)acrossOrigin,
						Cells + Halo * 2,
						spacing);
					lattices++;
					CaveTopologyDensityRegion region = CaptureRegion(baseline, key);
					bool hasContributors = region.HasTerrainContributors;
					bool proven = baseline.TryClassifyUniformRegularRegion(key, region, out SmoothTerrainDensityRange range);
					if (hasContributors)
					{
						withContributors++;
						boundSamples += CheckBounds(seed, entry, region, key, random);
					}
					if (hasContributors && proven)
					{
						bool byCave = range.Maximum == float.PositiveInfinity;
						if (byCave)
						{
							provenEmpty++;
							provenKey ??= key;
						}
						else provenFallThrough++;
						SmoothTerrainDensityRange reference = ReferenceRange(field, key);
						referenceChecks++;
						if (reference.Classification != range.Classification
							|| (byCave && reference.Minimum < 0.0f))
							throw new InvalidOperationException(
								$"seed {seed}: UNSOUND proof for {entry.Label} spacing={spacing} key={key} proof={range} reference={reference}");
					}

					if (large && hasContributors && spacing <= 8 && chunkAligned)
					{
						// Effectiveness is measured over lattices that are genuinely
						// open interior: a cheap screen at the box corners and centre
						// selects candidates, the full lattice then confirms them.
						if (proven && range.Maximum == float.PositiveInfinity)
						{
							effectivenessProven++;
							effectivenessInterior++;
						}
						else if (LooksOpen(region, key))
						{
							SmoothTerrainDensityRange reference = ReferenceRange(field, key);
							referenceChecks++;
							if (reference.Classification == SmoothTerrainDensityClassification.Empty)
								effectivenessInterior++;
						}
					}
				}
			}
		}

		CertifyEdgeCases(seed, baseline, field, entries, provenKey, aroundLength, structuralBottom, terrainBottom, protectedRoofThickness, lowerHullTop, structuralWallTop);

		var kText = new List<string>();
		foreach (KeyValuePair<string, double> pair in maxK)
			kText.Add($"{pair.Key}={pair.Value:0.####}");
		string summary = $"seed={seed} shapes={entries.Count} missing=[{missing}] K[{string.Join(',', kText)}] lipschitzPairs={pairs} worstRatio={worstRatio:0.####} lattices={lattices} withCaves={withContributors} provenEmpty={provenEmpty} provenFallThrough={provenFallThrough} referenceChecks={referenceChecks} boundSamples={boundSamples} mismatches=0 effectiveness={effectivenessProven}/{effectivenessInterior}";
		return new SeedResult(summary, lattices, withContributors, provenEmpty, provenFallThrough,
			referenceChecks, boundSamples, pairs, effectivenessProven, effectivenessInterior);
	}

	private static List<Entry> CollectShapes(RingTerrainSampler sampler, out string missing)
	{
		var result = new List<Entry>();
		var seenFamilies = new HashSet<string>();
		var seenLandmarks = new HashSet<string>();
		var requiredFamilies = new List<string>
		{
			"GrandChamber/None", "GrandChamber/GrandLayered", "FaultCavern/None", "PillarHall/None",
			"ChasmSystem/None", "UndergroundBasin/None", "CathedralCavern/None", "CollapsedRegion/None",
			"EngineeredVoid/MaintenanceTrunk", "EngineeredVoid/Reservoir", "EngineeredVoid/Habitat",
			"EngineeredVoid/TransitGallery", "Megacavern/None", "SupermassiveCavern/None"
		};
		var requiredLandmarks = new List<string>();
		foreach (CaveLandmarkOperation operation in Enum.GetValues<CaveLandmarkOperation>())
			requiredLandmarks.Add(operation.ToString());

		CaveTopologyPartition partition = sampler.CaveTopologyPartition;
		CaveProvinceDistribution distribution = sampler.CaveProvinceDistribution;
		CaveTopologyDensityComposer composer = sampler.CaveTopologyDensityComposer;
		CaveFeaturePlacementPolicy placement = sampler.CavePlacementPolicy;
		long verticalFirst = FloorDiv(
			Math.Max(
				placement.ProtectedRoofTopMeters + CaveFeaturePlacementPolicy.RoofClearanceMeters,
				placement.LowerHullTopMeters + CaveFeaturePlacementPolicy.LowerHullClearanceMeters),
			CaveTopologyPartition.ProvinceVerticalMeters);
		long verticalLast = FloorDiv(
			placement.SurfaceFloorMeters - CaveFeaturePlacementPolicy.SurfaceClearanceMeters - 1,
			CaveTopologyPartition.ProvinceVerticalMeters);
		long acrossFirst = FloorDiv(
			CaveFeaturePlacementPolicy.SidewallClearanceMeters,
			CaveTopologyPartition.ProvinceAcrossMeters);
		long acrossLast = FloorDiv(
			placement.AcrossWidthMeters - CaveFeaturePlacementPolicy.SidewallClearanceMeters - 1,
			CaveTopologyPartition.ProvinceAcrossMeters);
		int examined = 0;
		const int examineLimit = 40_000;
		for (long aroundProvince = 0; aroundProvince < partition.AroundProvinceCount; aroundProvince++)
		for (long verticalProvince = verticalFirst; verticalProvince <= verticalLast; verticalProvince++)
		for (long acrossProvince = acrossFirst; acrossProvince <= acrossLast; acrossProvince++)
		{
			if (seenFamilies.Count == requiredFamilies.Count && seenLandmarks.Count == requiredLandmarks.Count)
				goto done;
			if (++examined > examineLimit) goto done;
			CaveProvincePlan plan = distribution.ResolvePlan(new CaveTopologyProvinceAddress(
				aroundProvince, verticalProvince, acrossProvince));
			if (!plan.Active) continue;
			CaveChamberGraph graph = sampler.CaveChamberGraphGenerator.Resolve(plan);
			for (int anchorIndex = 0; anchorIndex < graph.Anchors.Count; anchorIndex++)
			{
				CaveRegionFeature feature = composer.Features.CreateAnchorFeatureForLocator(graph.Anchors[anchorIndex]);
				CaveFeatureShape shape = composer.Families.ResolveAnchorForLocator(feature);
				string familyKey = $"{shape.Family}/{shape.Variant}";
				string landmarkKey = shape.Landmark.IsDefined ? shape.Landmark.Operation.ToString() : "";
				bool newFamily = seenFamilies.Add(familyKey);
				bool newLandmark = landmarkKey.Length != 0 && seenLandmarks.Add(landmarkKey);
				if (newFamily || newLandmark)
					result.Add(new Entry($"{familyKey}+{(landmarkKey.Length == 0 ? "none" : landmarkKey)}", shape));
			}
		}
		done:
		var absent = new List<string>();
		foreach (string family in requiredFamilies)
			if (!seenFamilies.Contains(family)) absent.Add(family);
		foreach (string landmark in requiredLandmarks)
			if (!seenLandmarks.Contains(landmark)) absent.Add(landmark);
		missing = string.Join(',', absent);
		return result;
	}

	private static CaveTopologyDensityRegion CaptureRegion(
		RingTerrainSmoothBaseline baseline, SmoothTerrainVolumetricDensityCacheKey key)
	{
		long span = checked((long)key.CellsPerAxis * key.SampleSpacingMeters);
		return baseline.CaptureDesignedCaveRegion(
				key.AroundOrigin, key.VerticalOrigin, key.AcrossOrigin, span, span, span)
			?? throw new InvalidOperationException("designed caves are disabled");
	}

	private static SmoothTerrainDensityRange ReferenceRange(
		SmoothTerrainVolumetricDensitySnapshot field, SmoothTerrainVolumetricDensityCacheKey key) =>
		field.CaptureLattice(key).ClassifyCells(Halo, Halo, Halo, Cells, Cells, Cells);

	private static bool LooksOpen(CaveTopologyDensityRegion region, SmoothTerrainVolumetricDensityCacheKey key)
	{
		long extent = (long)Cells * key.SampleSpacingMeters;
		long a0 = key.AroundOrigin + (long)Halo * key.SampleSpacingMeters;
		long v0 = key.VerticalOrigin + (long)Halo * key.SampleSpacingMeters;
		long x0 = key.AcrossOrigin + (long)Halo * key.SampleSpacingMeters;
		for (int corner = 0; corner < 9; corner++)
		{
			bool centre = corner == 8;
			long a = centre ? a0 + extent / 2 : a0 + ((corner & 1) == 0 ? 0 : extent);
			long v = centre ? v0 + extent / 2 : v0 + ((corner & 2) == 0 ? 0 : extent);
			long x = centre ? x0 + extent / 2 : x0 + ((corner & 4) == 0 ? 0 : extent);
			CaveTopologyDensityResult result = region.SampleTerrainContributionValidated(a, v, x);
			if (!result.HasContribution || result.FinalDensityMeters < 0.0) return false;
		}
		return true;
	}

	private static int CheckLipschitz(int seed, Entry entry, double k, ref double worstRatio)
	{
		CaveFeatureShape shape = entry.Shape;
		var random = new Random(unchecked((int)(shape.Key ^ (ulong)seed)));
		double reach = 1.4 * Math.Max(shape.RadiusAround, Math.Max(shape.RadiusVertical, shape.RadiusAcross));
		const int pairs = 10_000;
		for (int index = 0; index < pairs; index++)
		{
			double a = (random.NextDouble() * 2 - 1) * reach;
			double v = (random.NextDouble() * 2 - 1) * reach;
			double x = (random.NextDouble() * 2 - 1) * reach;
			double length = Math.Pow(10.0, random.NextDouble() * 4.5 - 2.5);
			double ua = random.NextDouble() * 2 - 1, uv = random.NextDouble() * 2 - 1, ux = random.NextDouble() * 2 - 1;
			double norm = Math.Sqrt(ua * ua + uv * uv + ux * ux);
			if (norm < 1e-9) continue;
			double da = ua / norm * length, dv = uv / norm * length, dx = ux / norm * length;
			double f0 = CaveTopologyDensityComposer.EvaluateFamilyDensityAtOffset(shape, a, v, x);
			double f1 = CaveTopologyDensityComposer.EvaluateFamilyDensityAtOffset(shape, a + da, v + dv, x + dx);
			double distance = Math.Sqrt(da * da + dv * dv + dx * dx);
			if (Math.Abs(f1 - f0) > k * distance + 1e-9)
				throw new InvalidOperationException(
					$"seed {seed}: Lipschitz bound violated for {entry.Label}: |df|={Math.Abs(f1 - f0)} K*|dp|={k * distance}");
			if (distance > 1e-3) worstRatio = Math.Max(worstRatio, Math.Abs(f1 - f0) / distance / k);
		}
		return pairs;
	}

	private static int CheckBounds(
		int seed, Entry entry, CaveTopologyDensityRegion region,
		SmoothTerrainVolumetricDensityCacheKey key, Random random)
	{
		long extent = (long)Cells * key.SampleSpacingMeters;
		long a0 = key.AroundOrigin + (long)Halo * key.SampleSpacingMeters;
		long v0 = key.VerticalOrigin + (long)Halo * key.SampleSpacingMeters;
		long x0 = key.AcrossOrigin + (long)Halo * key.SampleSpacingMeters;
		if (!region.TryBoundTerrainTopology(a0, v0, x0, extent, out double lower, out double upper))
			return 0;
		const int samples = 96;
		for (int index = 0; index < samples; index++)
		{
			long a, v, x;
			if (index < 8)
			{
				a = a0 + ((index & 1) == 0 ? 0 : extent);
				v = v0 + ((index & 2) == 0 ? 0 : extent);
				x = x0 + ((index & 4) == 0 ? 0 : extent);
			}
			else
			{
				a = a0 + random.NextInt64(extent + 1);
				v = v0 + random.NextInt64(extent + 1);
				x = x0 + random.NextInt64(extent + 1);
			}
			double topology = region.SampleTerrainContributionValidated(a, v, x).TopologyDensityMeters;
			if (topology > upper + 1e-9 || topology < lower - 1e-9)
				throw new InvalidOperationException(
					$"seed {seed}: UNSOUND bound for {entry.Label}: topology={topology} outside [{lower},{upper}] at spacing={key.SampleSpacingMeters}");
		}
		return samples;
	}

	private static void CertifyEdgeCases(
		int seed,
		RingTerrainSmoothBaseline baseline,
		SmoothTerrainVolumetricDensitySnapshot field,
		List<Entry> entries,
		SmoothTerrainVolumetricDensityCacheKey? provenKey,
		long aroundLength,
		int structuralBottom,
		int terrainBottom,
		int protectedRoofThickness,
		int lowerHullTop,
		int structuralWallTop)
	{
		if (provenKey == null)
			throw new InvalidOperationException($"seed {seed}: no cave-proven lattice available for edge-case certification");
		SmoothTerrainVolumetricDensityCacheKey proven = provenKey.Value;
		int spacing = proven.SampleSpacingMeters;

		// Across straddling 0 / AcrossWidth is never classified by the proof.
		foreach (int acrossOrigin in new[] { -Halo * spacing - 1, baseline.AcrossWidth - (Halo + Cells) * spacing })
		{
			SmoothTerrainVolumetricDensityCacheKey shifted = proven with { AcrossOrigin = acrossOrigin };
			if (baseline.TryClassifyUniformRegularRegion(shifted, CaptureRegion(baseline, shifted), out _))
				throw new InvalidOperationException($"seed {seed}: proof classified an across-boundary lattice at {acrossOrigin}");
		}

		// A lower hull top or structural bottom inside the box blocks an Empty claim.
		int midVertical = proven.VerticalOrigin + (Halo + Cells / 2) * spacing;
		var hullStraddle = new RingTerrainSmoothBaseline(
			baseline.Sampler, midVertical - 1024, terrainBottom, protectedRoofThickness,
			midVertical, structuralWallTop, enableDesignedCaves: true);
		var bottomStraddle = new RingTerrainSmoothBaseline(
			baseline.Sampler, midVertical, terrainBottom, protectedRoofThickness,
			int.MinValue, structuralWallTop, enableDesignedCaves: true);
		foreach (RingTerrainSmoothBaseline straddle in new[] { hullStraddle, bottomStraddle })
		{
			if (straddle.TryClassifyUniformRegularRegion(proven, CaptureRegion(straddle, proven), out SmoothTerrainDensityRange straddleRange)
				&& straddleRange.Classification == SmoothTerrainDensityClassification.Empty)
				throw new InvalidOperationException($"seed {seed}: proof claimed Empty across a hull/structural boundary");
		}

		// Around-seam lattices: any proof must still match full sampling.
		foreach (Entry entry in entries)
		foreach (int seamSpacing in Spacings)
		foreach (long seamOrigin in new[] { aroundLength - 16L * seamSpacing, -16L * seamSpacing })
		{
			var seamKey = new SmoothTerrainVolumetricDensityCacheKey(
				field.Identity,
				seamOrigin - Halo * seamSpacing,
				checked((int)(FloorDiv(entry.Shape.Vertical, 32L * seamSpacing) * 32L * seamSpacing - Halo * seamSpacing)),
				checked((int)(FloorDiv(entry.Shape.Across, 32L * seamSpacing) * 32L * seamSpacing - Halo * seamSpacing)),
				Cells + Halo * 2,
				seamSpacing);
			CaveTopologyDensityRegion region = CaptureRegion(baseline, seamKey);
			if (region.HasTerrainContributors
				&& baseline.TryClassifyUniformRegularRegion(seamKey, region, out SmoothTerrainDensityRange seamRange)
				&& ReferenceRange(field, seamKey).Classification != seamRange.Classification)
				throw new InvalidOperationException($"seed {seed}: UNSOUND proof across the around seam at spacing {seamSpacing}");
		}

		// The 1 m tier keeps full sampling whenever a cave primitive can touch it.
		var baseKey = proven with { SampleSpacingMeters = 1 };
		if (baseline.TryClassifyUniformRegularRegion(baseKey, CaptureRegion(baseline, baseKey), out _)
			&& CaptureRegion(baseline, baseKey).HasTerrainContributors)
			throw new InvalidOperationException($"seed {seed}: proof classified a 1 m lattice with cave contributors");

		// Any edit disables the proof: the stamp must show up in the capture.
		long half = (long)(Halo + Cells / 2) * spacing;
		var stamp = new SmoothTerrainEditCommand(
			SmoothTerrainEditCommand.CurrentVersion, CommandId: 1, Sequence: 1, AddsMatter: true,
			proven.AroundOrigin + half, proven.VerticalOrigin + (int)half, proven.AcrossOrigin + (int)half,
			Radius: 24.0f, Material: (int)SmoothTerrainMaterial.Rock);
		var editedField = new SmoothTerrainVolumetricDensitySnapshot(
			baseline, new SmoothTerrainEditSnapshot(new[] { stamp }, revision: 32, aroundLength));
		SmoothTerrainVolumetricDensityCacheKey editedKey = proven with { Field = editedField.Identity };
		SmoothTerrainRegularChunkCapture edited = editedField.CaptureRegularChunk(editedKey, default);
		SmoothTerrainDensityRange editedReference = ReferenceRange(editedField, editedKey);
		if (edited.Range.Classification != editedReference.Classification
			|| edited.Range.Classification != SmoothTerrainDensityClassification.Mixed
			|| edited.Snapshot == null)
			throw new InvalidOperationException($"seed {seed}: edited lattice bypassed full sampling: {edited.Range} vs {editedReference}");
	}

	private static long FloorDiv(long value, long divisor) =>
		value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
}
