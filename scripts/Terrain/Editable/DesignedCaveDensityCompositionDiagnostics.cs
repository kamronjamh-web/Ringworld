using System;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

public static class DesignedCaveDensityCompositionDiagnostics
{
	public static bool RunSelfTest(out string summary)
	{
		try
		{
			const long fullProvinceCount = 24;
			long aroundLength = CaveTopologyPartition.ProvinceAroundMeters * fullProvinceCount
				+ CaveTopologyPartition.CellAroundMeters * 24;
			VoxelTerrainSettings disabledSettings = CreateSettings(enableDesignedCaves: false);
			VoxelTerrainSettings enabledSettings = CreateSettings(enableDesignedCaves: true);
			using var sampler = new RingTerrainSampler(
				disabledSettings,
				periodicAroundRing: true,
				aroundLength,
				ringRadiusMeters: 64_000.0,
				ringAcrossWidthMeters: 32_768,
				maxBuildGlobalY: 4096);
			using var enabledSettingSampler = new RingTerrainSampler(
				enabledSettings,
				periodicAroundRing: true,
				aroundLength,
				ringRadiusMeters: 64_000.0,
				ringAcrossWidthMeters: 32_768,
				maxBuildGlobalY: 4096);
			using var copy = sampler.CreateIndependentCopy();
			if (sampler.TerrainDefinitionHash != enabledSettingSampler.TerrainDefinitionHash
				|| sampler.CaveTopologyDensityComposer.Identity != copy.CaveTopologyDensityComposer.Identity)
			{
				throw new InvalidOperationException("opt-in cave composition changed the legacy terrain hash or copy identity");
			}

			CaveProvincePlan active = FindActiveInteriorPlan(sampler.CaveProvinceDistribution);
			CaveChamberAnchor anchor = sampler.CaveChamberGraphGenerator.Resolve(active).Anchors[0];
			var defaultBaseline = new RingTerrainSmoothBaseline(sampler);
			var explicitDisabled = new RingTerrainSmoothBaseline(sampler, enableDesignedCaves: false);
			var enabledBaseline = new RingTerrainSmoothBaseline(sampler, enableDesignedCaves: true);
			SmoothTerrainSample defaultSample = defaultBaseline.Sample(
				anchor.Around,
				checked((int)anchor.Vertical),
				checked((int)anchor.Across));
			SmoothTerrainSample disabledSample = explicitDisabled.Sample(
				anchor.Around,
				checked((int)anchor.Vertical),
				checked((int)anchor.Across));
			SmoothTerrainSample caveSample = enabledBaseline.Sample(
				anchor.Around,
				checked((int)anchor.Vertical),
				checked((int)anchor.Across));
			if (defaultBaseline.Revision != explicitDisabled.Revision
				|| defaultSample != disabledSample
				|| enabledBaseline.Revision == explicitDisabled.Revision
				|| enabledBaseline.PreDesignedCaveRevision != explicitDisabled.Revision
				|| !Contains(enabledBaseline.AcceptedLegacyRevisions, explicitDisabled.Revision)
				|| disabledSample.Density >= 0.0f
				|| caveSample.Density != CaveTopologyDensityComposer.ChamberConnectivityRadiusMeters)
			{
				throw new InvalidOperationException("default-off compatibility or enabled chamber composition failed");
			}

			int anchorVertical = checked((int)anchor.Vertical);
			int anchorAcross = checked((int)anchor.Across);
			var addMatter = new SmoothTerrainEditCommand(
				SmoothTerrainEditCommand.CurrentVersion,
				CommandId: 1,
				Sequence: 1,
				AddsMatter: true,
				anchor.Around,
				anchorVertical,
				anchorAcross,
				Radius: 12.0f,
				Material: (int)SmoothTerrainMaterial.Rock);
			var removeMatter = new SmoothTerrainEditCommand(
				SmoothTerrainEditCommand.CurrentVersion,
				CommandId: 2,
				Sequence: 2,
				AddsMatter: false,
				anchor.Around,
				anchorVertical,
				anchorAcross,
				Radius: 4.0f,
				Material: (int)SmoothTerrainMaterial.Rock);
			var edits = new SmoothTerrainEditSnapshot(
				new[] { removeMatter, addMatter },
				revision: 9,
				aroundLength);
			var orderedEdits = new SmoothTerrainEditSnapshot(
				new[] { addMatter, removeMatter },
				revision: 9,
				aroundLength);
			var field = new SmoothTerrainVolumetricDensitySnapshot(enabledBaseline, edits);
			var orderedField = new SmoothTerrainVolumetricDensitySnapshot(enabledBaseline, orderedEdits);
			SmoothTerrainSample edited = field.Sample(anchor.Around, anchor.Vertical, anchor.Across);
			SmoothTerrainSample ordered = orderedField.Sample(anchor.Around, anchor.Vertical, anchor.Across);
			if (edited.Density != 4.0f || edited != ordered)
				throw new InvalidOperationException("ordered edits did not refill and then re-excavate the designed cave");

			var latticeKey = new SmoothTerrainVolumetricDensityCacheKey(
				field.Identity,
				anchor.Around,
				anchorVertical,
				anchorAcross,
				CellsPerAxis: 1,
				SampleSpacingMeters: 1);
			SmoothTerrainVolumetricDensityLattice lattice = field.CaptureLattice(latticeKey);
			if (lattice.Sample(0, 0, 0) != edited)
				throw new InvalidOperationException("volumetric lattice changed cave-before-edit composition");
			using (SmoothTerrainVolumetricWorkerContext worker = field.CreateWorkerContext())
			{
				if (worker.Sampler == null
					|| ReferenceEquals(worker.Sampler, sampler)
					|| worker.Field.Identity != field.Identity
					|| worker.Sampler.CaveTopologyDensityComposer.Identity
						!= sampler.CaveTopologyDensityComposer.Identity)
				{
					throw new InvalidOperationException("worker-local cave sampler identity or ownership changed");
				}
				SmoothTerrainVolumetricDensityLattice workerLattice =
					worker.Field.CaptureLattice(latticeKey);
				for (int z = 0; z <= latticeKey.CellsPerAxis; z++)
				for (int y = 0; y <= latticeKey.CellsPerAxis; y++)
				for (int x = 0; x <= latticeKey.CellsPerAxis; x++)
				{
					if (workerLattice.Sample(x, y, z) != lattice.Sample(x, y, z))
						throw new InvalidOperationException($"worker-local density changed sample {x}/{y}/{z}");
				}
			}
			field.DisposePooledWorkerContexts();

			var chunkCoordinate = new SmoothTerrainChunkCoordinate(
				FloorDiv(anchor.Around, SmoothTerrainChunkSnapshot.Cells),
				checked((int)FloorDiv(anchor.Vertical, SmoothTerrainChunkSnapshot.Cells)),
				checked((int)FloorDiv(anchor.Across, SmoothTerrainChunkSnapshot.Cells)));
			RingTerrainSampleCoordinate chunkOrigin = chunkCoordinate.Origin;
			int localAround = checked((int)(anchor.Around - chunkOrigin.AroundVoxel));
			int localVertical = checked((int)(anchor.Vertical - chunkOrigin.VerticalVoxel));
			int localAcross = checked((int)(anchor.Across - chunkOrigin.AcrossVoxel));
			SmoothTerrainChunkSnapshot chunk = field.CaptureChunk(chunkCoordinate);
			if (chunk.Density(localAround, localVertical, localAcross) != 4.0f
				|| chunk.Revision.Baseline != enabledBaseline.Revision
				|| chunk.Revision.Edits != edits.Revision)
			{
				throw new InvalidOperationException("regular chunk capture bypassed cave-before-edit ordering or revisions");
			}

			int terrainBottom = checked(anchorVertical - 512);
			int structuralBottom = checked(anchorVertical - 2048);
			int lowerHullTop = checked(anchorVertical - 1536);
			var protectedDisabled = new RingTerrainSmoothBaseline(
				sampler,
				structuralBottom,
				terrainBottom,
				protectedRoofThickness: 512,
				lowerHullTopVertical: lowerHullTop,
				structuralWallTopVertical: anchorVertical + 10_000,
				enableDesignedCaves: false);
			var protectedEnabled = new RingTerrainSmoothBaseline(
				sampler,
				structuralBottom,
				terrainBottom,
				protectedRoofThickness: 512,
				lowerHullTopVertical: lowerHullTop,
				structuralWallTopVertical: anchorVertical + 10_000,
				enableDesignedCaves: true);
			SmoothTerrainSample protectedOff = protectedDisabled.Sample(
				anchor.Around,
				anchorVertical,
				anchorAcross);
			SmoothTerrainSample protectedOn = protectedEnabled.Sample(
				anchor.Around,
				anchorVertical,
				anchorAcross);
			// Production structural-wall tops are positive values above the
			// playable terrain. They bound only the exterior wall slab; they must
			// not disable an interior underground cave whose anchor is negative.
			if (SameFieldValue(protectedOn, protectedOff)
				|| protectedOn.Density <= 0.0f
				|| protectedOn.Density <= protectedOff.Density)
			{
				throw new InvalidOperationException("production structural-wall top suppressed an interior designed cave");
			}
			foreach (int across in new[] { -1, sampler.RingAcrossWidthMeters })
			{
				if (!SameFieldValue(
					protectedEnabled.Sample(anchor.Around, anchorVertical, across),
					protectedDisabled.Sample(anchor.Around, anchorVertical, across)))
				{
					throw new InvalidOperationException("designed cave changed a structural sidewall sample");
				}
			}
			if (!SameFieldValue(
				protectedEnabled.Sample(anchor.Around, lowerHullTop, anchorAcross),
				protectedDisabled.Sample(anchor.Around, lowerHullTop, anchorAcross)))
			{
				throw new InvalidOperationException("designed cave changed the lower hull");
			}

			CaveTopologyDensityRegion region = enabledBaseline.CaptureDesignedCaveRegion(
				anchor.Around - 18,
				anchorVertical - 18,
				anchorAcross - 18,
				36,
				36,
				36) ?? throw new InvalidOperationException("enabled baseline omitted regional cave capture");
			if (region.FeatureCount <= 0
				|| region.FeatureCount > CaveRegionFeatureSnapshotGenerator.MaximumFeatures)
			{
				throw new InvalidOperationException("regional cave capture was empty or unbounded");
			}

			(ushort routes, ushort branches, ushort loops, int landmarks) =
				CertifyDesignedTopology(sampler, enabledBaseline, active, anchor);
			CertifyWallNoiseIsSurfaceOnly(sampler, anchor);
			CertifyJournalReplayOverCaveBaseline(
				enabledBaseline,
				aroundLength,
				addMatter,
				removeMatter,
				anchor);
			int uniformRejections = CertifyUniformSectorRejection(
				enabledBaseline,
				sampler,
				aroundLength,
				anchor);

			summary = $"baseline={enabledBaseline.Revision:X16} composer={sampler.CaveTopologyDensityComposer.Identity:X16} features={region.FeatureCount} edited={edited.Density:0.###} routes={routes} branches={branches} loops={loops} landmarks={landmarks} uniformRejections={uniformRejections} journal=cave-replay";
			return true;
		}
		catch (Exception exception)
		{
			summary = exception.Message;
			return false;
		}
	}

	private static VoxelTerrainSettings CreateSettings(bool enableDesignedCaves) => new()
	{
		Seed = 7,
		BaseSurfaceHeightVoxels = 1024,
		HeightAmplitudeVoxels = 0,
		DetailAmplitudeVoxels = 0,
		SoilDepthVoxels = 3,
		BedrockY = -20_000,
		EnableUnified3dDensityTerrain = false,
		DesignedCaveGenerationContractSettings = new CaveGenerationSettings(),
		EnableDesignedCaveDensityComposition = enableDesignedCaves
	};

	private static CaveProvincePlan FindActiveInteriorPlan(CaveProvinceDistribution distribution)
	{
		for (long vertical = -1; vertical >= -32; vertical--)
		for (long around = 0; around < distribution.Partition.AroundProvinceCount - 1; around++)
		for (long across = 0; across <= 1; across++)
		{
			CaveProvincePlan plan = distribution.ResolvePlan(
				new CaveTopologyProvinceAddress(around, vertical, across));
			if (plan.Active) return plan;
		}
		throw new InvalidOperationException("could not find an active interior cave province");
	}

	/// <summary>Exercises the exact graph, region capture, family SDF and baseline
	/// used by meshing. A cave is only certified as designed when all navigable
	/// graph vocabulary and at least one authored landmark reach that field.</summary>
	private static (ushort Routes, ushort Branches, ushort Loops, int Landmarks)
		CertifyDesignedTopology(
			RingTerrainSampler sampler,
			RingTerrainSmoothBaseline baseline,
			CaveProvincePlan active,
			CaveChamberAnchor anchor)
	{
		CaveChamberGraph graph = sampler.CaveChamberGraphGenerator.Resolve(active);
		int branchNodes = 0;
		var degrees = new int[graph.Anchors.Count];
		foreach (CaveChamberGraphEdge edge in graph.AsSpan())
		{
			degrees[edge.LowRank]++;
			degrees[edge.HighRank]++;
		}
		foreach (int degree in degrees)
			if (degree >= CaveChamberGraphGenerator.BranchDegree) branchNodes++;
		if (graph.Count == 0 || graph.TreeEdgeCount == 0 || graph.LoopEdgeCount == 0
			|| branchNodes == 0 || !graph.HasBranch)
		{
			throw new InvalidOperationException("production cave graph lacks routes, branches, or loops");
		}

		const int captureHalfExtent = 2048;
		CaveTopologyDensityRegion capture = sampler.CaveTopologyDensityComposer.CaptureRegion(
			anchor.Around - captureHalfExtent,
			anchor.Vertical - captureHalfExtent,
			anchor.Across - captureHalfExtent,
			captureHalfExtent * 2,
			captureHalfExtent * 2,
			captureHalfExtent * 2);
		int landmarks = 0;
		foreach (CaveFeatureShape shape in capture.FamilyShapes.AsSpan())
		{
			if (!shape.Landmark.IsDefined) continue;
			landmarks++;
			// Family capture deliberately includes a wide halo. Re-capture a tiny
			// region centered on this landmark before sampling: a halo descriptor
			// is allowed to lie outside the original request, but never outside its
			// own production region.
			CaveTopologyDensityRegion landmarkCapture = sampler.CaveTopologyDensityComposer.CaptureRegion(
				shape.Around - 1, shape.Vertical - 1, shape.Across - 1, 2, 2, 2);
			CaveTopologyDensityResult result = landmarkCapture.Sample(shape.Around, shape.Vertical, shape.Across);
			if (!result.HasContribution || result.FinalDensityMeters <= 0.0)
				throw new InvalidOperationException("authored cave landmark did not reach the production density field");
		}
		if (landmarks == 0)
			throw new InvalidOperationException("active cave capture contains no authored landmark");
		SmoothTerrainSample chamber = baseline.Sample(anchor.Around, checked((int)anchor.Vertical), checked((int)anchor.Across));
		if (chamber.Density <= 0.0f)
			throw new InvalidOperationException("graph chamber did not reach the production terrain baseline");
		return (checked((ushort)graph.TreeEdgeCount), checked((ushort)branchNodes), checked((ushort)graph.LoopEdgeCount), landmarks);
	}

	/// <summary>Noise may vary only the topology wall band. This samples the
	/// composed production field, so an accidental independent noise tunnel is
	/// caught even if the standalone applicator remains bounded.</summary>
	private static void CertifyWallNoiseIsSurfaceOnly(
		RingTerrainSampler sampler,
		CaveChamberAnchor anchor)
	{
		const int span = 48;
		CaveTopologyDensityRegion region = sampler.CaveTopologyDensityComposer.CaptureRegion(
			anchor.Around - span, anchor.Vertical - span, anchor.Across - span,
			span * 2, span * 2, span * 2);
		int wallSamples = 0;
		for (int z = -span; z <= span; z += 8)
		for (int y = -span; y <= span; y += 8)
		for (int x = -span; x <= span; x += 8)
		{
			CaveTopologyDensityResult sample = region.Sample(anchor.Around + x, anchor.Vertical + y, anchor.Across + z);
			if (!sample.HasContribution)
			{
				if (sample.AppliedWallDisplacementMeters != 0.0 || sample.FinalDensityMeters != sample.TopologyDensityMeters)
					throw new InvalidOperationException("wall noise modified remote solid space");
				continue;
			}
			if (Math.Abs(sample.AppliedWallDisplacementMeters) > CaveTopologyDensityComposer.WallProfile.MaximumDisplacementMeters + 1e-12)
				throw new InvalidOperationException("wall noise exceeded its declared surface displacement");
			if (sample.TopologyDensityMeters >= CaveTopologyDensityComposer.WallProfile.ProtectedOpenCoreMeters
				&& sample.FinalDensityMeters != sample.TopologyDensityMeters)
				throw new InvalidOperationException("wall noise altered a protected designed-cave core");
			wallSamples++;
		}
		if (wallSamples == 0)
			throw new InvalidOperationException("production cave sample did not exercise wall character");
	}

	private static void CertifyJournalReplayOverCaveBaseline(
		RingTerrainSmoothBaseline baseline,
		long aroundLength,
		SmoothTerrainEditCommand addMatter,
		SmoothTerrainEditCommand removeMatter,
		CaveChamberAnchor anchor)
	{
		var envelope = new SmoothTerrainJournalEnvelope(
			SmoothTerrainEditJournalStore.FormatVersion,
			baseline.Revision,
			aroundLength,
			Revision: 27,
			NextCommandId: 3,
			NextSequence: 3,
			Commands: new[] { removeMatter, addMatter });
		SmoothTerrainEditJournalStore.ValidateEnvelope(envelope, baseline.Revision, aroundLength);
		var replay = new SmoothTerrainEditSnapshot(envelope.Commands, envelope.Revision, aroundLength);
		var canonical = new SmoothTerrainEditSnapshot(new[] { addMatter, removeMatter }, envelope.Revision, aroundLength);
		var replayField = new SmoothTerrainVolumetricDensitySnapshot(baseline, replay);
		var canonicalField = new SmoothTerrainVolumetricDensitySnapshot(baseline, canonical);
		if (replayField.Identity != canonicalField.Identity)
			throw new InvalidOperationException("journal replay changed cave-field identity");
		SmoothTerrainChunkCoordinate coordinate = new(
			FloorDiv(anchor.Around, SmoothTerrainChunkSnapshot.Cells),
			checked((int)FloorDiv(anchor.Vertical, SmoothTerrainChunkSnapshot.Cells)),
			checked((int)FloorDiv(anchor.Across, SmoothTerrainChunkSnapshot.Cells)));
		AssertChunksMatch(replayField.CaptureChunk(coordinate), canonicalField.CaptureChunk(coordinate));
		SmoothTerrainSample replayed = replayField.Sample(anchor.Around, anchor.Vertical, anchor.Across);
		SmoothTerrainSample expected = canonicalField.Sample(anchor.Around, anchor.Vertical, anchor.Across);
		if (replayed != expected || replayed.Density != 4.0f)
			throw new InvalidOperationException("journal replay did not deterministically compose over the procedural cave baseline");
	}

	private static int CertifyUniformSectorRejection(
		RingTerrainSmoothBaseline baseline,
		RingTerrainSampler sampler,
		long aroundLength,
		CaveChamberAnchor anchor)
	{
		var noEdits = new SmoothTerrainEditSnapshot(
			Array.Empty<SmoothTerrainEditCommand>(), revision: 101, aroundLength);
		var field = new SmoothTerrainVolumetricDensitySnapshot(baseline, noEdits);
		var farCoordinate = new SmoothTerrainChunkCoordinate(0, -512, 8);
		const int spacing = 4;
		var farKey = new SmoothTerrainVolumetricDensityCacheKey(
			field.Identity,
			farCoordinate.AroundChunk * SmoothTerrainChunkSnapshot.Cells - SmoothTerrainChunkSnapshot.Halo * spacing,
			farCoordinate.VerticalChunk * SmoothTerrainChunkSnapshot.Cells - SmoothTerrainChunkSnapshot.Halo * spacing,
			farCoordinate.AcrossChunk * SmoothTerrainChunkSnapshot.Cells - SmoothTerrainChunkSnapshot.Halo * spacing,
			SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo * 2,
			spacing);

		ulong composerIdentity = sampler.CaveTopologyDensityComposer.Identity;
		CaveGenerationPerformanceTelemetry.Reset(composerIdentity);
		SmoothTerrainRegularChunkCapture rejected = field.CaptureRegularChunk(farKey, farCoordinate);
		CaveGenerationPerformanceTelemetry.Diagnostics rejectionTelemetry =
			CaveGenerationPerformanceTelemetry.Capture(composerIdentity);
		if (rejected.Snapshot != null
			|| rejected.Range.Classification != SmoothTerrainDensityClassification.Solid
			|| rejectionTelemetry.UniformDensityRejections != 1
			|| rejectionTelemetry.DensitySamples != 0)
		{
			throw new InvalidOperationException(
				$"provably uniform deep sector was not rejected before full sampling: range={rejected.Range}, telemetry={rejectionTelemetry}");
		}

		SmoothTerrainDensityRange reference = field.CaptureLattice(farKey).ClassifyCells(
			SmoothTerrainChunkSnapshot.Halo,
			SmoothTerrainChunkSnapshot.Halo,
			SmoothTerrainChunkSnapshot.Halo,
			SmoothTerrainChunkSnapshot.Cells,
			SmoothTerrainChunkSnapshot.Cells,
			SmoothTerrainChunkSnapshot.Cells);
		if (rejected.Range != reference)
			throw new InvalidOperationException($"uniform rejection range changed the authoritative field: fast={rejected.Range}, full={reference}");

		var caveCoordinate = new SmoothTerrainChunkCoordinate(
			FloorDiv(anchor.Around, SmoothTerrainChunkSnapshot.Cells),
			checked((int)FloorDiv(anchor.Vertical, SmoothTerrainChunkSnapshot.Cells)),
			checked((int)FloorDiv(anchor.Across, SmoothTerrainChunkSnapshot.Cells)));
		RingTerrainSampleCoordinate caveOrigin = caveCoordinate.Origin;
		var caveKey = new SmoothTerrainVolumetricDensityCacheKey(
			field.Identity,
			caveOrigin.AroundVoxel - SmoothTerrainChunkSnapshot.Halo,
			caveOrigin.VerticalVoxel - SmoothTerrainChunkSnapshot.Halo,
			caveOrigin.AcrossVoxel - SmoothTerrainChunkSnapshot.Halo,
			SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo * 2,
			1);
		CaveGenerationPerformanceTelemetry.Reset(composerIdentity);
		field.CaptureRegularChunk(caveKey, caveCoordinate);
		CaveGenerationPerformanceTelemetry.Diagnostics caveTelemetry =
			CaveGenerationPerformanceTelemetry.Capture(composerIdentity);
		int fullSampleCount = SmoothTerrainChunkSnapshot.SampleCount
			* SmoothTerrainChunkSnapshot.SampleCount
			* SmoothTerrainChunkSnapshot.SampleCount;
		if (caveTelemetry.UniformDensityRejections != 0
			|| caveTelemetry.DensitySamples != fullSampleCount)
		{
			throw new InvalidOperationException(
				$"uniform rejection discarded or bypassed an intersecting cave: {caveTelemetry}");
		}
		CaveGenerationPerformanceTelemetry.Reset(composerIdentity);
		return 1;
	}

	private static void AssertChunksMatch(SmoothTerrainChunkSnapshot left, SmoothTerrainChunkSnapshot right)
	{
		if (left.Coordinate != right.Coordinate || left.Revision != right.Revision)
			throw new InvalidOperationException("journal replay changed production chunk identity");
		for (int z = -SmoothTerrainChunkSnapshot.Halo; z <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; z++)
		for (int y = -SmoothTerrainChunkSnapshot.Halo; y <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; y++)
		for (int x = -SmoothTerrainChunkSnapshot.Halo; x <= SmoothTerrainChunkSnapshot.Cells + SmoothTerrainChunkSnapshot.Halo; x++)
			if (left.Density(x, y, z) != right.Density(x, y, z)
				|| left.Material(x, y, z) != right.Material(x, y, z))
				throw new InvalidOperationException("journal replay changed production chunk samples");
	}

	private static bool Contains(ulong[] values, ulong target)
	{
		foreach (ulong value in values)
		{
			if (value == target) return true;
		}
		return false;
	}

	private static bool SameFieldValue(SmoothTerrainSample left,SmoothTerrainSample right) =>
		left.Density==right.Density&&left.Material==right.Material;

	private static long FloorDiv(long value, long divisor) =>
		value / divisor - (value < 0 && value % divisor != 0 ? 1 : 0);
}
