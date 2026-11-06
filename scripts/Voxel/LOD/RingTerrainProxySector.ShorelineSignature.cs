using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector
{
    private enum ShorelineFieldAction { Skip, OriginOnly, Rebuild }

    /// <summary>
    /// Everything the shared FFT shoreline field reads besides the 32 m centre cell: the ordered set of
    /// (coordinate, tile reference identity) for tiles that are active, visible and not warm. Tiles are immutable once
    /// installed, so equal ordered signatures produce byte-identical fields. Order is part of the signature because
    /// overlapping edge texels are last-writer-wins.
    /// </summary>
    private sealed class ShorelineFieldSignature
    {
        private struct Entry
        {
            public RingTerrainSectorCoordinate Coordinate;
            public object Tile;
        }

        private List<Entry> _published = new();
        private List<Entry> _scratch = new();
        private object? _publishedOwner;
        private object? _scratchOwner;

        public bool HasPublished { get; private set; }

        public void Begin(object owner)
        {
            _scratch.Clear();
            _scratchOwner = owner;
        }

        public void Add(RingTerrainSectorCoordinate coordinate, object tile) =>
            _scratch.Add(new Entry { Coordinate = coordinate, Tile = tile });

        /// <summary>Records the scratch signature as the one the published field was built from.</summary>
        public void Commit()
        {
            (_published, _scratch) = (_scratch, _published);
            _publishedOwner = _scratchOwner;
            HasPublished = true;
        }

        public void Invalidate() => HasPublished = false;

        public bool ScratchMatchesPublished()
        {
            if (_scratch.Count != _published.Count) return false;
            if (_scratch.Count == 0) return true;
            if (!ReferenceEquals(_scratchOwner, _publishedOwner)) return false;
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (!_scratch[i].Coordinate.Equals(_published[i].Coordinate)
                    || !ReferenceEquals(_scratch[i].Tile, _published[i].Tile)) return false;
            }
            return true;
        }

        /// <summary>
        /// Decides what to do once the old gate (coverage revision or centre cell changed) has passed and the scratch
        /// signature is built. An empty field that stays empty is all zeros whatever the origin, so only the origin moves.
        /// </summary>
        public ShorelineFieldAction Decide(bool fieldValid, bool centerChanged)
        {
            if (!fieldValid || !HasPublished || !ScratchMatchesPublished()) return ShorelineFieldAction.Rebuild;
            if (!centerChanged) return ShorelineFieldAction.Skip;
            return _scratch.Count == 0 ? ShorelineFieldAction.OriginOnly : ShorelineFieldAction.Rebuild;
        }
    }

    private static readonly ShorelineFieldSignature _shorelineFieldSignature = new();

    private void BuildShorelineFieldSignature()
    {
        _shorelineFieldSignature.Begin(this);
        foreach ((RingTerrainSectorCoordinate coordinate, FftShorelineTile tile) in _activeFftShorelineTiles)
        {
            if (!_active.TryGetValue(coordinate, out MeshInstance3D? node) || !node.Visible || _warmUntil.ContainsKey(coordinate)) continue;
            _shorelineFieldSignature.Add(coordinate, tile);
        }
    }

    /// <summary>
    /// Contract: for random sequences of sector install, departure, visibility/warm flips, coverage-revision bumps
    /// and centre moves, the field published under the signature gate is byte-identical (modelled as the exact input
    /// to the copy) to the field the old revision-or-centre gate publishes, at every frame.
    /// </summary>
    private static bool RunFftShorelineSignatureGateContract()
    {
        var rng = new Random(20261105);
        const int tileCount = 6;
        for (int trial = 0; trial < 40; trial++)
        {
            var tiles = new ContractTile?[tileCount];      // installed tile identity per slot (null = none)
            var visible = new bool[tileCount];
            var warm = new bool[tileCount];
            ulong revision = 0;
            long centerX = 0; int centerZ = 0;
            int nextTileId = 0;
            var gate = new ShorelineFieldSignature();
            object owner = new();
            // Old gate state.
            ulong oldRevision = ulong.MaxValue; long oldCx = long.MinValue; int oldCz = int.MinValue; string oldBytes = "";
            // New gate state.
            ulong newRevision = ulong.MaxValue; long newCx = long.MinValue; int newCz = int.MinValue; string newBytes = "";
            int skippedRebuilds = 0;
            for (int frame = 0; frame < 400; frame++)
            {
                int ops = rng.Next(0, 3);
                for (int op = 0; op < ops; op++)
                {
                    int slot = rng.Next(tileCount);
                    switch (rng.Next(7))
                    {
                        case 0: tiles[slot] = new ContractTile(++nextTileId); visible[slot] = rng.Next(2) == 0; warm[slot] = false; revision++; break; // install shoreline sector
                        case 1: tiles[slot] = null; visible[slot] = false; warm[slot] = false; revision++; break;                      // departure
                        case 2: visible[slot] = !visible[slot]; if (rng.Next(2) == 0) revision++; break;                              // visibility flip, maybe unbumped
                        case 3: warm[slot] = !warm[slot]; if (rng.Next(2) == 0) revision++; break;
                        case 4: case 5: revision++; break;                                                                           // sector without shoreline installs/leaves
                        default: centerX += rng.Next(-2, 3); centerZ += rng.Next(-2, 3); break;                                       // centre move
                    }
                }

                // Old gate.
                if (oldRevision != revision || oldCx != centerX || oldCz != centerZ)
                {
                    oldBytes = ModelBytes(tiles, visible, warm, centerX, centerZ);
                    oldRevision = revision; oldCx = centerX; oldCz = centerZ;
                }

                // New gate: the old gate first, then the signature.
                bool fieldValid = newRevision != ulong.MaxValue;
                if (newRevision != revision || newCx != centerX || newCz != centerZ)
                {
                    bool centerChanged = newCx != centerX || newCz != centerZ;
                    gate.Begin(owner);
                    for (int i = 0; i < tileCount; i++)
                        if (tiles[i] != null && visible[i] && !warm[i]) gate.Add(new RingTerrainSectorCoordinate(i, 0), tiles[i]!);
                    ShorelineFieldAction action = gate.Decide(fieldValid, centerChanged);
                    if (action == ShorelineFieldAction.Rebuild)
                    {
                        newBytes = ModelBytes(tiles, visible, warm, centerX, centerZ);
                        gate.Commit();
                    }
                    else skippedRebuilds++;
                    newRevision = revision; newCx = centerX; newCz = centerZ;
                }
                // The published field, as consumers see it, must equal what the old gate would have published.
                if (!FieldEquivalent(newBytes, oldBytes)) return false;
            }
            if (skippedRebuilds == 0) return false; // the gate must actually skip something in this model
        }
        return true;
    }

    // Model of the field bytes: the ordered contributing tiles (identity) plus the origin when anything contributes.
    // An empty field is all zeros whatever the origin.
    private static string ModelBytes(ContractTile?[] tiles, bool[] visible, bool[] warm, long centerX, int centerZ)
    {
        var parts = new List<string>();
        for (int i = 0; i < tiles.Length; i++)
            if (tiles[i] != null && visible[i] && !warm[i])
                parts.Add(i + ":" + tiles[i]!.Id);
        return parts.Count == 0 ? "" : centerX + "," + centerZ + "|" + string.Join(";", parts);
    }

    private sealed record ContractTile(int Id)
    {
        public bool Equals(ContractTile? other) => ReferenceEquals(this, other);
        public override int GetHashCode() => Id;
    }

    private static bool FieldEquivalent(string a, string b) => a == b;
}
