using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // Immutable tile records can keep their variant partitions until the tile is
    // replaced. The planner stages a new dictionary and publishes it at commit.
    private sealed class GpuTilePartition
    {
        public required VegetationTile Tile;
        public required Dictionary<int, CachedTree[]> Groups;
        public required Dictionary<ulong, CachedTree> RootsById;
    }

    private Dictionary<VegetationTileKey, GpuTilePartition> _gpuTreeTilePartitions = new();

    private IEnumerable<bool> PrepareGpuTilePartitions(
        VegetationTile[] tiles,
        Dictionary<VegetationTileKey, GpuTilePartition> destination)
    {
        foreach (VegetationTile tile in tiles)
        {
            if (_gpuTreeTilePartitions.TryGetValue(tile.Key, out GpuTilePartition? cached)
                && ReferenceEquals(cached.Tile, tile))
            {
                destination[tile.Key] = cached;
                continue;
            }

            var grouped = new Dictionary<int, List<CachedTree>>();
            var rootsById = new Dictionary<ulong, CachedTree>(tile.Trees.Count);
            int scannedRoots = 0;
            foreach (CachedTree tree in tile.Trees)
            {
                rootsById[tree.Id] = tree;
                if (!grouped.TryGetValue(tree.Variant, out List<CachedTree>? roots))
                    grouped[tree.Variant] = roots = new List<CachedTree>();
                roots.Add(tree);
                if (++scannedRoots % 128 == 0) yield return false;
            }

            var groups = new Dictionary<int, CachedTree[]>(grouped.Count);
            foreach ((int variant, List<CachedTree> roots) in grouped)
            {
                roots.Sort(static (left, right) =>
                {
                    int rank = left.Rank.CompareTo(right.Rank);
                    return rank != 0 ? rank : left.Id.CompareTo(right.Id);
                });
                groups[variant] = roots.ToArray();
                yield return false;
            }
            destination[tile.Key] = new GpuTilePartition
            {
                Tile = tile,
                Groups = groups,
                RootsById = rootsById
            };
        }
    }
}
