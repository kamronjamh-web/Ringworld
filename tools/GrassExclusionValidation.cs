using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>Headless checks for immutable canonical grass exclusion volumes.</summary>
public partial class GrassExclusionValidation : RefCounted
{
    public bool Run()
    {
        try
        {
            const long length = 1000;
            var occupied = new List<RingGrassExclusionBox> { new(20, 21, 4, 5, 30, 31) };
            RingGrassExclusionSnapshot first = new(1, length, occupied);
            Require(first.IsExcluded(20.5, 4.2, 30.5, .5f), "occupied concrete block did not suppress grass");
            Require(!first.IsExcluded(20.5, 7, 30.5, .5f), "high grass incorrectly hit a lower block");
            Require(first.IsExcluded(20.5, 5.08, 30.5, .02f), "root bury margin did not suppress a nearly touching blade");
            Require(!first.IsExcluded(20.5, 5.13, 30.5, .02f), "vertical clearance was smaller than the root margin");
            Require(!first.IsExcluded(20.5, 4.2, 34, .5f), "unrelated across position was excluded");

            occupied.Clear();
            RingGrassExclusionSnapshot removed = new(2, length, occupied);
            Require(first.IsExcluded(20.5, 4.2, 30.5, .5f), "old snapshot changed when source collection changed");
            Require(!removed.IsExcluded(20.5, 4.2, 30.5, .5f), "removed block remains excluded");
            Require(first.GetDirtyRegionsSince(removed).Length == 1 && removed.GetDirtyRegionsSince(first).Length == 1, "placement/removal dirty region missing");
            RingGrassExclusionSnapshot duplicated = new(3, length, new[] { new RingGrassExclusionBox(30, 31, 0, 1, 30, 31), new RingGrassExclusionBox(30, 31, 0, 1, 30, 31) });
            Require(duplicated.Boxes.Count == 1 && duplicated.GetDirtyRegionsSince(removed).Length == 1, "duplicate additions were not normalized into a dirty occupancy change");

            // Material is deliberately absent from the volume: glass and concrete occupy equally for blades.
            RingGrassExclusionSnapshot mixed = new(3, length, new[] { new RingGrassExclusionBox(40, 41, 2, 3, 10, 11), new RingGrassExclusionBox(42, 43, 2, 3, 10, 11) });
            Require(mixed.IsExcluded(40.5, 2.1, 10.5, .3f) && mixed.IsExcluded(42.5, 2.1, 10.5, .3f), "glass/concrete occupancy is inconsistent");
            Require(!mixed.IsExcluded(40.5, 6, 10.5, .4f), "a high roof with no blade-volume overlap cleared grass");
            RingGrassExclusionSnapshot roof = new(4, length, new[] { new RingGrassExclusionBox(50, 51, 8, 9, 10, 11) });
            Require(!roof.IsExcluded(50.5, 2, 10.5, .4f), "grass below a high roof was excluded");

            RingGrassExclusionSnapshot fractional = new(4, length, new[] { new RingGrassExclusionBox(100.25, 100.75, 1.25, 1.75, 50.25, 50.75) });
            Require(fractional.IsExcluded(100.5, 1.3, 50.5, .2f), "fractional box interior was missed");
            Require(!fractional.IsExcluded(101.0, 1.3, 50.5, .2f), "fractional box exterior was included");

            RingGrassExclusionSnapshot seam = new(5, length, new[] { new RingGrassExclusionBox(length - 1, length + 1, 0, 1, 5, 6) });
            Require(seam.IsExcluded(length - .5, .1, 5.5, .2f) && seam.IsExcluded(.5, .1, 5.5, .2f) && seam.IsExcluded(-.5, .1, 5.5, .2f), "positive or negative seam extent did not wrap");
            RingGrassExclusionSnapshot seamPadding = new(6, length, new[] { new RingGrassExclusionBox(0, 1, 0, 1, 5, 6), new RingGrassExclusionBox(length - 1, length, 0, 1, 7, 8) });
            Require(seamPadding.IsExcluded(length - .05, .1, 5.5, .2f) && seamPadding.IsExcluded(.05, .1, 7.5, .2f), "seam padding did not wrap on both canonical edges");
            RingGrassExclusionRegion[] paddedDirty = seamPadding.GetDirtyRegionsSince(null);
            Require(paddedDirty.Length == 4 && paddedDirty[0].AroundMin == 0, "dirty regions did not include canonical padded seam spans");
            RingGrassExclusionSnapshot far = new(6, length, new[] { new RingGrassExclusionBox(3, 4, 0, 1, 5, 6) });
            Require(far.IsExcluded(length * 12345d + 3.5, .1, 5.5, .2f), "far-around canonical position did not wrap");
            Require(far.GetDirtyRegionsSince(new RingGrassExclusionSnapshot(6, length, new[] { new RingGrassExclusionBox(3, 4, 0, 1, 5, 6) })).Length == 0, "equal snapshots reported dirty regions");
            return true;
        }
        catch (Exception exception) { GD.PushError("GRASS_EXCLUSION=FAIL " + exception.Message); return false; }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
