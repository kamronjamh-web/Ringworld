using System;
using System.Collections.Generic;
using System.Linq;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
	internal string RunImportedForestCollisionRegression()
	{
		try
		{
			const double circumference = 1_000;
			const double width = 360;
			CellSizeMeters = 30;
			SpatialTileCells = 4;
			VegetationLayout layout = CaptureVegetationLayout(circumference);
			_vegetationTiles.Clear();
			_desiredVegetationTiles.Clear();
			AddCollisionRegressionTile(0, 0, 1, (2, 8), (116, 10), (998, 12));
			AddCollisionRegressionTile(layout.AroundTileCount - 1, 0, 20, (990, 16)); // partial final around tile / seam.
			AddCollisionRegressionTile(0, 1, 40, (5, 130), (50, 150));
			AddCollisionRegressionTile(2, 0, 60, (250, 10));
			var seamFocus = new RingCanonicalPosition(3, 0, 10);
			VerifyCollisionSelection(seamFocus, layout, width, 40, 16, "seam");
			VerifyCollisionSelection(seamFocus, layout, width, 140, 3, "radius/cap");
			var emptyFocus = new RingCanonicalPosition(500, 0, 300);
			VerifyCollisionSelection(emptyFocus, layout, width, 20, 16, "empty");
			if (ImportedForestCollisionResultCount != 0) return "FAIL: empty selection was not cached as empty";
			AddCollisionRegressionTile(4, 2, 80, (500, 300));
			VerifyCollisionSelection(emptyFocus, layout, width, 20, 16, "empty invalidation");
			return "PASS: bounded local collision selection matches exhaustive seam, partial-tile, radius/cap, and empty invalidation cases";
		}
		catch (Exception error) { return "FAIL: " + error.GetType().Name + " " + error.Message; }
	}

	private void AddCollisionRegressionTile(long around, long across, ulong firstId, params (long Around, int Across)[] roots)
	{
		var key = new VegetationTileKey(around, across);
		var trees = new List<CachedTree>(roots.Length);
		for (int i = 0; i < roots.Length; i++)
			trees.Add(new CachedTree(firstId + (ulong)i, roots[i].Around, roots[i].Across, 0, 2, 8, 0, i, 0, default, 0));
		_vegetationTiles[key] = new VegetationTile { Key = key, Trees = trees };
		_desiredVegetationTiles.Add(key);
	}

	private void VerifyCollisionSelection(RingCanonicalPosition focus, in VegetationLayout layout, double width, float radius, int maximum, string name)
	{
		SelectImportedForestCollisionTrees(focus, layout, width, radius, maximum);
		List<ulong> actual = _importedForestCollisionCandidates.Select(candidate => candidate.Tree.Id).ToList();
		var expected = new List<(ulong Id, double DistanceSquared)>();
		foreach (VegetationTile tile in _vegetationTiles.Values)
		{
			if (!_desiredVegetationTiles.Contains(tile.Key)) continue;
			foreach (CachedTree tree in tile.Trees)
			{
				double da = WrappedVegetationDelta(tree.Around - focus.AroundMeters, layout.CircumferenceMeters);
				double dz = tree.Across - focus.AcrossMeters;
				double distanceSquared = da * da + dz * dz;
				if (distanceSquared <= radius * radius) expected.Add((tree.Id, distanceSquared));
			}
		}
		expected.Sort(static (left, right) => { int distance = left.DistanceSquared.CompareTo(right.DistanceSquared); return distance != 0 ? distance : left.Id.CompareTo(right.Id); });
		if (expected.Count > maximum) expected.RemoveRange(maximum, expected.Count - maximum);
		if (!actual.SequenceEqual(expected.Select(candidate => candidate.Id)))
			throw new InvalidOperationException(name + " selected IDs differ from exhaustive reference");
	}
}
