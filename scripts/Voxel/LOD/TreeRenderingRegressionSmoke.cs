using System;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Headless regression for procedural tree face orientation.</summary>
public partial class TreeRenderingRegressionSmoke : Node
{
    public override void _Ready()
    {
        try
        {
            RingTreeMeshSet tree = RingTreeProceduralMeshFactory.Create(
                seed: 41031,
                radialSegments: 8,
                branchCount: 18,
                addForks: true);
            AssertClockwiseOutward(tree.Trunk, "trunk");
            AssertClockwiseOutward(tree.Canopy, "canopy");
			AssertPlacementHelpers();
		AssertKayKitNormalization();
		AssertImportedPlacementNormalization();
			AssertAuthoredTreeAssets();
            GD.Print("TREE_RENDERING_REGRESSION_SMOKE=PASS");
            GetTree().Quit(0);
        }
        catch (Exception exception)
        {
            GD.PushError($"TREE_RENDERING_REGRESSION_SMOKE=FAIL: {exception.Message}");
            GetTree().Quit(1);
        }
    }

	private static void AssertPlacementHelpers()
	{
		float sparse = RingBiomeDecorationLayer.ApplyGroveDensity(.72f, .15f, 12, -7, 41031);
		float dense = RingBiomeDecorationLayer.ApplyGroveDensity(.72f, .85f, 12, -7, 41031);
		if (!float.IsFinite(sparse) || !float.IsFinite(dense) || sparse < 0 || sparse > 1 || dense < 0 || dense > 1)
			throw new InvalidOperationException("grove density is not clamped and finite");
		if (!Mathf.IsEqualApprox(RingBiomeDecorationLayer.ApplyGroveDensity(.72f, 0, 12, -7, 41031), .72f)
			|| !Mathf.IsEqualApprox(dense, RingBiomeDecorationLayer.ApplyGroveDensity(.72f, .85f, 12, -7, 41031)))
			throw new InvalidOperationException("grove density baseline/determinism regression");
		if (!RingBiomeDecorationLayer.IsSlopeAngleSuitable(1, 1, 2, 45)
			|| RingBiomeDecorationLayer.IsSlopeAngleSuitable(20, 0, 2, 20))
			throw new InvalidOperationException("tree slope boundary regression");
		float humidDenseSpacing = RingBiomeDecorationLayer.ForestLocalSpacingMeters(1.0f, 1.0f);
		float dryOpenSpacing = RingBiomeDecorationLayer.ForestLocalSpacingMeters(0.0f, 0.0f);
		if (!float.IsFinite(humidDenseSpacing) || !float.IsFinite(dryOpenSpacing)
			|| humidDenseSpacing < 2.0f || dryOpenSpacing <= humidDenseSpacing)
			throw new InvalidOperationException("forest humidity spacing regression");
		long tile = RingBiomeDecorationLayer.SpatialTileKey(new Vector3(-1, 0, 441), 440);
		if (tile != RingBiomeDecorationLayer.SpatialTileKey(new Vector3(-1, 99, 441), 440)
			|| tile == RingBiomeDecorationLayer.SpatialTileKey(new Vector3(440, 0, 441), 440))
			throw new InvalidOperationException("tree spatial tile key regression");
		if (!Mathf.IsEqualApprox(RingBiomeDecorationLayer.RootAltitude(100, .16f), 99.84f)
			|| !Mathf.IsEqualApprox(RingBiomeDecorationLayer.RootAltitude(100, 2), 99.75f))
			throw new InvalidOperationException("tree root embed/pivot regression");
		int variant = RingBiomeDecorationLayer.DeterministicVariant(17, -5, 41031, 6);
		if (variant < 0 || variant >= 6 || variant != RingBiomeDecorationLayer.DeterministicVariant(17, -5, 41031, 6))
			throw new InvalidOperationException("tree variant determinism/range regression");
		AssertPersistentTileHelpers();
	}

	private static void AssertImportedPlacementNormalization()
	{
		var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
			1000.0, 0.0, 0.0, Math.Tau * 1000.0, -100.0, 100.0, null);
		Transform3D transform = RingBiomeDecorationLayer.CreateTransformForRegression(geometry, 2.0f, 11.0f);
		if (!Mathf.IsEqualApprox(transform.Basis.X.Length(), 11.0f)
			|| !Mathf.IsEqualApprox(transform.Basis.Y.Length(), 11.0f)
			|| !Mathf.IsEqualApprox(transform.Basis.Z.Length(), 11.0f))
			throw new InvalidOperationException("imported tree placement lost uniform catalog-height normalization");
	}

	private static void AssertPersistentTileHelpers()
	{
		// Tile identity must use canonical around coordinates so a wrap seam never
		// creates two cache entries for the same vegetation tile.
		const long aroundTileCount = 100;
		long seamLeft = RingBiomeDecorationLayer.CanonicalVegetationTileKey(-1, 3, aroundTileCount);
		long seamRight = RingBiomeDecorationLayer.CanonicalVegetationTileKey(99, 3, aroundTileCount);
		if (seamLeft != seamRight
			|| seamRight == RingBiomeDecorationLayer.CanonicalVegetationTileKey(0, 3, aroundTileCount)
			|| seamRight == RingBiomeDecorationLayer.CanonicalVegetationTileKey(99, 4, aroundTileCount))
			throw new InvalidOperationException("canonical vegetation tile seam identity regression");

		// A terrain edit invalidates its owning tile plus the one-tile slope halo,
		// including negative-coordinate floor boundaries, rather than every tile.
		const float tileMeters = 440.0f;
		if (!RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -1, 1320, tileMeters)
			|| !RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -440, 1320, tileMeters)
			|| !RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -441, 1320, tileMeters)
			|| !RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -1, 1760, tileMeters)
			|| RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -881, 1320, tileMeters)
			|| RingBiomeDecorationLayer.IsVegetationTileAffectedByDirtyColumn(-1, 3, -1, 2200, tileMeters))
			throw new InvalidOperationException("vegetation tile dirty-column invalidation regression");

		var rank = RingBiomeDecorationLayer.DeterministicTreeRank(17, -5, 41031, 2);
		if (rank != RingBiomeDecorationLayer.DeterministicTreeRank(17, -5, 41031, 2)
			|| rank == RingBiomeDecorationLayer.DeterministicTreeRank(17, -5, 41031, 3)
			|| rank == RingBiomeDecorationLayer.DeterministicTreeRank(18, -5, 41031, 2)
			|| rank < 0.0f
			|| rank > 1.0f)
			throw new InvalidOperationException("persistent tree rank determinism/isolation regression");

		// The final around-ring tile is allowed to be shorter, but it must end at
		// the canonical cell count. It must never wrap over cell zero and duplicate
		// the first tile's trees at the ring seam.
		RingBiomeDecorationLayer.CanonicalVegetationCellRange(0, 8, 19, out long first0, out long end0);
		RingBiomeDecorationLayer.CanonicalVegetationCellRange(1, 8, 19, out long first1, out long end1);
		RingBiomeDecorationLayer.CanonicalVegetationCellRange(2, 8, 19, out long first2, out long end2);
		RingBiomeDecorationLayer.CanonicalVegetationCellRange(3, 8, 19, out long wrappedFirst, out long wrappedEnd);
		if (first0 != 0 || end0 != 8
			|| first1 != 8 || end1 != 16
			|| first2 != 16 || end2 != 19
			|| wrappedFirst != first0 || wrappedEnd != end0
			|| (end0 - first0) + (end1 - first1) + (end2 - first2) != 19)
			throw new InvalidOperationException("canonical vegetation seam cell partition regression");

		float previousGate = float.PositiveInfinity;
		for (int lod = 1; lod <= 5; lod++)
		{
			float gate = RingBiomeDecorationLayer.VegetationLodRankGate(lod);
			if (!float.IsFinite(gate) || gate < 0.0f || gate > 1.0f || gate > previousGate)
				throw new InvalidOperationException("vegetation LOD population nesting regression");
			previousGate = gate;
		}

		ulong settings = RingBiomeDecorationLayer.VegetationCacheSettingsHash(
			19, 502_656.0, 55.0f, 5_000.0f, 8, 10, .72f, .20f, .70f, 32.0f);
		if (settings != RingBiomeDecorationLayer.VegetationCacheSettingsHash(
				19, 502_656.0, 55.0f, 5_000.0f, 8, 10, .72f, .20f, .70f, 32.0f)
			|| settings == RingBiomeDecorationLayer.VegetationCacheSettingsHash(
				19, 502_656.0, 55.0f, 5_000.0f, 8, 9, .72f, .20f, .70f, 32.0f)
			|| settings == RingBiomeDecorationLayer.VegetationCacheSettingsHash(
				19, 502_656.0, 55.0f, 5_000.0f, 8, 10, .72f, .20f, .60f, 32.0f)
			|| settings == RingBiomeDecorationLayer.VegetationCacheSettingsHash(
				19, 502_656.0, 55.0f, 5_001.0f, 8, 10, .72f, .20f, .70f, 32.0f)
			|| settings == RingBiomeDecorationLayer.VegetationCacheSettingsHash(
				19, 502_657.0, 55.0f, 5_000.0f, 8, 10, .72f, .20f, .70f, 32.0f))
			throw new InvalidOperationException("vegetation disk-cache settings identity regression");

		if (!RingGeneratedLodCacheBudget.FitsWithinBudget(100, 200, 0, 50, 1_000, 100)
			|| !RingGeneratedLodCacheBudget.FitsWithinBudget(180, 200, 40, 30, 1_000, 100)
			|| RingGeneratedLodCacheBudget.FitsWithinBudget(0, 0, 0, 1, 1_000, 100)
			|| RingGeneratedLodCacheBudget.FitsWithinBudget(180, 200, 0, 30, 1_000, 100)
			|| RingGeneratedLodCacheBudget.FitsWithinBudget(100, 200, 0, 30, 120, 100)
			|| RingGeneratedLodCacheBudget.FitsWithinBudget(100, 200, 80, 80, 100, 100))
			throw new InvalidOperationException("generated LOD cache budget/free-space guard regression");

		string reservationRoot = System.IO.Path.Combine(
			System.IO.Path.GetTempPath(),
			$"ringworld_cache_reservation_{Guid.NewGuid():N}",
			"lod_cache");
		string firstPath = System.IO.Path.Combine(reservationRoot, "first.bin");
		string secondPath = System.IO.Path.Combine(reservationRoot, "second.bin");
		if (!RingGeneratedLodCacheBudget.CanWrite(reservationRoot, firstPath, 100, 0, 60)
			|| RingGeneratedLodCacheBudget.CanWrite(reservationRoot, secondPath, 100, 0, 60))
			throw new InvalidOperationException("concurrent generated-cache write reservation regression");
		RingGeneratedLodCacheBudget.CancelWriteReservation(reservationRoot, firstPath);
		if (!RingGeneratedLodCacheBudget.CanWrite(reservationRoot, secondPath, 100, 0, 60))
			throw new InvalidOperationException("generated-cache reservation cancellation regression");
		RingGeneratedLodCacheBudget.CancelWriteReservation(reservationRoot, secondPath);
		RingGeneratedLodCacheBudget.Schedule(reservationRoot, 100, 75, TimeSpan.FromSeconds(10));
		if (!System.Threading.SpinWait.SpinUntil(
				() => RingGeneratedLodCacheBudget.TryGetLatest(reservationRoot, out _),
				TimeSpan.FromSeconds(2))
			|| !RingGeneratedLodCacheBudget.TryGetLatest(
				reservationRoot,
				out RingGeneratedLodCacheBudget.MaintenanceResult maintenance)
			|| !string.IsNullOrEmpty(maintenance.Error)
			|| maintenance.BytesAfter != 0
			|| maintenance.DeletedFileCount != 0)
			throw new InvalidOperationException("generated-cache maintenance scheduling regression");
	}

	private static void AssertKayKitNormalization()
	{
		var arrays = new Godot.Collections.Array();
		arrays.Resize((int)Mesh.ArrayType.Max);
		arrays[(int)Mesh.ArrayType.Vertex] = new[]
		{
			new Vector3(-8, 2, -4),
			new Vector3(4, 6, 8),
			new Vector3(0, 4, 0)
		};
		arrays[(int)Mesh.ArrayType.Normal] = new[] { Vector3.Up, Vector3.Up, Vector3.Up };
		arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 };
		var source = new ArrayMesh();
		source.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);

		Mesh normalized = KayKitTreeMeshLoader.NormalizeMesh(source)
			?? throw new InvalidOperationException("synthetic KayKit mesh did not normalize");
		Vector3[] vertices = normalized.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
		Aabb bounds = normalized.GetAabb();
		if (!Mathf.IsEqualApprox(bounds.Position.Y, 0.0f)
			|| !Mathf.IsEqualApprox(bounds.Size.Y, 1.0f)
			|| !vertices[0].IsEqualApprox(new Vector3(-2, 0, -1))
			|| !vertices[1].IsEqualApprox(new Vector3(1, 1, 2)))
			throw new InvalidOperationException("KayKit uniform normalization/pivot regression");
	}

	private static void AssertAuthoredTreeAssets()
	{
		string[] paths =
		{
			"res://resources/trees/kaykit_forest/Tree_1_A_Color1.gltf",
			"res://resources/trees/kaykit_forest/Tree_1_B_Color1.gltf",
			"res://resources/trees/kaykit_forest/Tree_2_A_Color1.gltf",
			"res://resources/trees/kaykit_forest/Tree_2_C_Color1.gltf",
			"res://resources/trees/kaykit_forest/Tree_3_A_Color1.gltf",
			"res://resources/trees/kaykit_forest/Tree_4_B_Color1.gltf"
		};
		foreach (string path in paths)
		{
			Mesh mesh = KayKitTreeMeshLoader.LoadNormalizedMesh(path)
				?? throw new InvalidOperationException($"authored tree failed to load: {path}");
			Aabb bounds = mesh.GetAabb();
			if (mesh.GetSurfaceCount() == 0
				|| mesh.SurfaceGetMaterial(0) == null
				|| !Mathf.IsEqualApprox(bounds.Position.Y, 0.0f)
				|| !Mathf.IsEqualApprox(bounds.Size.Y, 1.0f))
				throw new InvalidOperationException($"authored tree normalization/material regression: {path}");
		}
	}

    private static void AssertClockwiseOutward(Mesh mesh, string label)
    {
        if (mesh.GetSurfaceCount() != 1)
            throw new InvalidOperationException($"{label} surface count {mesh.GetSurfaceCount()}");
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        if (vertices.Length == 0 || vertices.Length % 3 != 0 || normals.Length != vertices.Length)
            throw new InvalidOperationException($"{label} malformed vertex/normal arrays");
        for (int i = 0; i < vertices.Length; i += 3)
        {
            Vector3 face = (vertices[i + 1] - vertices[i]).Cross(vertices[i + 2] - vertices[i]);
            Vector3 outward = normals[i] + normals[i + 1] + normals[i + 2];
            if (!face.IsFinite() || !outward.IsFinite() || face.LengthSquared() <= 1e-10f
                || face.Dot(outward) >= 0.0f)
                throw new InvalidOperationException($"{label} triangle {i / 3} is not Godot-clockwise with outward normals");
        }
    }
}
