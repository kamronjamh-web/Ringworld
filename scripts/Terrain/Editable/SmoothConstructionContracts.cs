using System;
using System.Collections.Generic;
using System.Linq;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Construction is deliberately separate from natural density terrain.</summary>
public enum SmoothConstructionMaterial : byte { Concrete = 1, Glass = 2 }

public readonly record struct SmoothConstructionBlock(long Around, int Vertical, int Across)
{
    public SmoothConstructionBlock Canonical(long aroundLength) => this with { Around = RingCoordMath.WrapAround(Around, aroundLength) };
}

public readonly record struct SmoothConstructionCommand(uint Version, ulong CommandId, ulong Sequence, bool Place, SmoothConstructionBlock Block, SmoothConstructionMaterial Material)
{
    public const uint CurrentVersion = 1;
    public bool IsValid => Version == CurrentVersion && CommandId != 0 && Sequence != 0 && Enum.IsDefined(Material);
}

public sealed class SmoothConstructionSnapshot
{
    public long AroundLength { get; }
    public int RingAcrossWidth { get; }
    public ulong Revision { get; }
    public IReadOnlyList<SmoothConstructionCommand> Ordered { get; }
    public SmoothConstructionSnapshot(IEnumerable<SmoothConstructionCommand> commands, ulong revision, long aroundLength, int ringAcrossWidth)
    {
        if (aroundLength <= 0 || ringAcrossWidth <= 0) throw new ArgumentOutOfRangeException(nameof(aroundLength));
        AroundLength = aroundLength; RingAcrossWidth = ringAcrossWidth; Revision = revision;
        var ids = new HashSet<ulong>(); var sequences = new HashSet<ulong>(); var list = new List<SmoothConstructionCommand>();
        foreach (var raw in commands)
        {
            if (!raw.IsValid || !ids.Add(raw.CommandId) || !sequences.Add(raw.Sequence)) throw new ArgumentException("Invalid, duplicate-id, or duplicate-order construction command.");
            var c = raw with { Block = raw.Block.Canonical(aroundLength) };
            if (c.Block.Across < 0 || c.Block.Across >= ringAcrossWidth) throw new ArgumentOutOfRangeException(nameof(commands));
            list.Add(c);
        }
        Ordered = list.OrderBy(c => c.Sequence).ThenBy(c => c.CommandId).ToArray();
    }
}
