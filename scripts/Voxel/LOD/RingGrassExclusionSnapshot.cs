using System;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>A canonical, worker-safe volume which prevents grass placement.</summary>
public readonly record struct RingGrassExclusionBox(
    double AroundMin, double AroundMax, double VerticalMin, double VerticalMax, double AcrossMin, double AcrossMax);

/// <summary>A canonical region whose grass tiles should be rebuilt after an exclusion change.</summary>
public readonly record struct RingGrassExclusionRegion(
    double AroundMin, double AroundMax, double AcrossMin, double AcrossMax);

/// <summary>
/// Immutable construction and decoration occupancy view. Coordinates are canonical meters and all bounds are half-open.
/// It owns only value data, so it may be retained and queried by background tile builders.
/// </summary>
public sealed class RingGrassExclusionSnapshot
{
    private const double HorizontalMarginMeters = .1;
    private const double RootBuryMeters = .12;
    private readonly RingGrassExclusionBox[] _boxes;
    private readonly IReadOnlyList<RingGrassExclusionBox> _readonlyBoxes;
    private readonly Node? _root;

    private sealed class Node
    {
        public readonly RingGrassExclusionBox Box;
        public readonly Node? Left, Right;
        public readonly double MaxAround;
        public readonly double MinAcross, MaxAcross, MinVertical, MaxVertical;
        public Node(RingGrassExclusionBox box, Node? left, Node? right)
        { Box = box; Left = left; Right = right; MaxAround = Math.Max(box.AroundMax, Math.Max(left?.MaxAround ?? double.NegativeInfinity, right?.MaxAround ?? double.NegativeInfinity)); MinAcross = Math.Min(box.AcrossMin, Math.Min(left?.MinAcross ?? double.PositiveInfinity, right?.MinAcross ?? double.PositiveInfinity)); MaxAcross = Math.Max(box.AcrossMax, Math.Max(left?.MaxAcross ?? double.NegativeInfinity, right?.MaxAcross ?? double.NegativeInfinity)); MinVertical = Math.Min(box.VerticalMin, Math.Min(left?.MinVertical ?? double.PositiveInfinity, right?.MinVertical ?? double.PositiveInfinity)); MaxVertical = Math.Max(box.VerticalMax, Math.Max(left?.MaxVertical ?? double.NegativeInfinity, right?.MaxVertical ?? double.NegativeInfinity)); }
    }

    public ulong Revision { get; }
    public long AroundLength { get; }
    public IReadOnlyList<RingGrassExclusionBox> Boxes => _readonlyBoxes;

    public RingGrassExclusionSnapshot(ulong revision, long aroundLength, IEnumerable<RingGrassExclusionBox>? boxes)
    {
        Revision = revision;
        AroundLength = Math.Max(1, aroundLength);
        List<RingGrassExclusionBox> canonical = new();
        if (boxes != null)
            foreach (RingGrassExclusionBox box in boxes) AddCanonical(canonical, box);
        canonical.Sort(CompareBoxes);
        for (int i = canonical.Count - 1; i > 0; i--) if (canonical[i] == canonical[i - 1]) canonical.RemoveAt(i);
        _boxes = canonical.ToArray();
        _readonlyBoxes = Array.AsReadOnly(_boxes);
        _root = Build(_boxes, 0, _boxes.Length);
    }

    public bool IsExcluded(double around, double height, double across, float bladeHeight, float horizontalRadiusMeters = 0)
    {
        if (!double.IsFinite(around) || !double.IsFinite(height) || !double.IsFinite(across) || !float.IsFinite(bladeHeight)) return false;
        double root = height - RootBuryMeters;
        double tip = height + Math.Max(0f, bladeHeight);
        double canonical = Canonicalize(around);
        double margin = HorizontalMarginMeters + Math.Max(0, float.IsFinite(horizontalRadiusMeters) ? horizontalRadiusMeters : 0);
        if (Intersects(_root, canonical, root, tip, across, margin)) return true;
        if (canonical < margin && Intersects(_root, canonical + AroundLength, root, tip, across, margin)) return true;
        return canonical > AroundLength - margin && Intersects(_root, canonical - AroundLength, root, tip, across, margin);
    }

    /// <summary>Returns conservative canonical tile-space regions changed since the previous immutable view.</summary>
    public RingGrassExclusionRegion[] GetDirtyRegionsSince(RingGrassExclusionSnapshot? previous, float additionalHorizontalMargin = 0)
    {
        if (previous != null && previous.Revision == Revision && previous.AroundLength == AroundLength && SameBoxes(previous._boxes, _boxes)) return Array.Empty<RingGrassExclusionRegion>();
        HashSet<RingGrassExclusionBox> changes = previous == null ? new HashSet<RingGrassExclusionBox>() : new HashSet<RingGrassExclusionBox>(previous._boxes);
        changes.SymmetricExceptWith(_boxes);
        List<RingGrassExclusionRegion> result = new(changes.Count);
        double margin = HorizontalMarginMeters + Math.Max(0, float.IsFinite(additionalHorizontalMargin) ? additionalHorizontalMargin : 0);
        foreach (RingGrassExclusionBox box in changes) AddDirtyRegion(result, box, margin);
        result.Sort(static (a, b) => a.AroundMin.CompareTo(b.AroundMin));
        return result.ToArray();
    }

    private void AddCanonical(List<RingGrassExclusionBox> destination, RingGrassExclusionBox source)
    {
        if (!double.IsFinite(source.AroundMin) || !double.IsFinite(source.AroundMax) || !double.IsFinite(source.VerticalMin) || !double.IsFinite(source.VerticalMax) || !double.IsFinite(source.AcrossMin) || !double.IsFinite(source.AcrossMax) || source.VerticalMax <= source.VerticalMin || source.AcrossMax <= source.AcrossMin || source.AroundMax <= source.AroundMin) return;
        double span = source.AroundMax - source.AroundMin;
        if (span >= AroundLength) { destination.Add(source with { AroundMin = 0, AroundMax = AroundLength }); return; }
        double min = Canonicalize(source.AroundMin), max = min + span;
        if (max <= AroundLength) destination.Add(source with { AroundMin = min, AroundMax = max });
        else { destination.Add(source with { AroundMin = min, AroundMax = AroundLength }); destination.Add(source with { AroundMin = 0, AroundMax = max - AroundLength }); }
    }

    private static Node? Build(RingGrassExclusionBox[] boxes, int start, int end)
    { if (start >= end) return null; int middle = start + (end - start) / 2; return new Node(boxes[middle], Build(boxes, start, middle), Build(boxes, middle + 1, end)); }
    private static bool Intersects(Node? node, double around, double root, double tip, double across, double margin)
    {
        if (node == null || node.MaxAround + margin <= around || node.MinAcross - margin > across || node.MaxAcross + margin <= across || node.MinVertical >= tip || node.MaxVertical <= root) return false;
        RingGrassExclusionBox box = node.Box;
        if (node.Left != null && node.Left.MaxAround + margin > around && Intersects(node.Left, around, root, tip, across, margin)) return true;
        if (box.AroundMin - margin <= around && around < box.AroundMax + margin && box.AcrossMin - margin <= across && across < box.AcrossMax + margin && root < box.VerticalMax && tip > box.VerticalMin) return true;
        return around >= box.AroundMin - margin && Intersects(node.Right, around, root, tip, across, margin);
    }
    private double Canonicalize(double around) { double value = around % AroundLength; return value < 0 ? value + AroundLength : value; }
    private void AddDirtyRegion(List<RingGrassExclusionRegion> destination, RingGrassExclusionBox box, double margin)
    { double min = box.AroundMin - margin, max = box.AroundMax + margin; double acrossMin = box.AcrossMin - margin, acrossMax = box.AcrossMax + margin; if (max - min >= AroundLength) destination.Add(new(0, AroundLength, acrossMin, acrossMax)); else if (min < 0) { destination.Add(new(min + AroundLength, AroundLength, acrossMin, acrossMax)); destination.Add(new(0, max, acrossMin, acrossMax)); } else if (max > AroundLength) { destination.Add(new(min, AroundLength, acrossMin, acrossMax)); destination.Add(new(0, max - AroundLength, acrossMin, acrossMax)); } else destination.Add(new(min, max, acrossMin, acrossMax)); }
    private static int CompareBoxes(RingGrassExclusionBox a, RingGrassExclusionBox b) { int c = a.AroundMin.CompareTo(b.AroundMin); if (c != 0) return c; c = a.AroundMax.CompareTo(b.AroundMax); if (c != 0) return c; c = a.VerticalMin.CompareTo(b.VerticalMin); if (c != 0) return c; c = a.VerticalMax.CompareTo(b.VerticalMax); if (c != 0) return c; c = a.AcrossMin.CompareTo(b.AcrossMin); return c != 0 ? c : a.AcrossMax.CompareTo(b.AcrossMax); }
    private static bool SameBoxes(RingGrassExclusionBox[] a, RingGrassExclusionBox[] b) { if (a.Length != b.Length) return false; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false; return true; }
}
