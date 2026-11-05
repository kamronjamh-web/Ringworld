using System;
using System.Collections.Generic;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// A wall-top POI anchor (docs/containment-field-design.md decision 3 and 9, WO8). Canonical metres: around = CueLine * spacing, across = the wall's
/// field root (centre line while the wall is 128 m deep), vertical = the snapshot wall top.
/// </summary>
public readonly record struct RimWallTopAnchor(int Index, int Side, double AroundMeters, double AcrossMeters, double VerticalMeters, int CueLine);

public static class RimWallTopAnchors
{
    /// <summary>
    /// One anchor per longitudinal cue line per wall (RingStructuralCues.LineCount, so 126 per wall and 252 in total), ordered by side then canonical around in
    /// [0, C). Spacing is C / 126 from the snapshot's own circumference, the value the hull shader gets. Across = wall.CentreLine(side, W) (the inner
    /// face while DepthMeters = 0; -64 and W + 64 for the live 128 m wall); vertical = the snapshot wall top.
    /// </summary>
    public static IReadOnlyList<RimWallTopAnchor> Build(in RingLodGeometrySnapshot s, double widthMeters, in RimWallDescription wall)
    {
        int count = RingStructuralCues.LineCount;
        var anchors = new List<RimWallTopAnchor>(count * 2);
        for (int side = 0; side < 2; side++)
        {
            double across = wall.CentreLine(side, widthMeters);
            for (int k = 0; k < count; k++)
                anchors.Add(new RimWallTopAnchor(side * count + k, side, RingStructuralCues.CueLineAround(k, s.CircumferenceMeters), across,
                    s.StructuralWallTopGlobalY, k));
        }
        return anchors;
    }
}
