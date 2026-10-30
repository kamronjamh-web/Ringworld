using System;
using System.Collections.Generic;

namespace RingworldTechDemo;

/// <summary>
/// Deterministic flash-slot ids of the far flash field (docs/megastorm-design.md 3.3): 64 segments of 5 km across, 2 s slots, mean interval 20 s per
/// segment, all in the moving frame so they depend only on (seed, segment, slot). WO5 builds the renderer and positions on top of this; WO2 owns the
/// ids so the save/load replay can assert them.
/// </summary>
public static class MegastormLightning
{
    public const int Segments = 64;
    public const double SlotSeconds = 2.0, MeanIntervalSeconds = 20.0;

    /// <summary>Event id of one (slot, segment) cell; unique per cell.</summary>
    public static long CellId(long slot, int segment) => slot * Segments + segment;

    /// <summary>Ids of the flashes whose slot starts in [t0, t1) while the storm is active, in slot-then-segment order (list cleared first).</summary>
    public static int FlashIdsInWindow(MegastormModel model, double t0, double t1, List<long> ids)
    {
        ids.Clear();
        if (!model.Triggered || t1 <= t0) return 0;
        long first = (long)Math.Ceiling(t0 / SlotSeconds), last = (long)Math.Ceiling(t1 / SlotSeconds) - 1;
        double chance = SlotSeconds / MeanIntervalSeconds;
        for (long slot = first; slot <= last; slot++)
        {
            if (!model.Evaluate(slot * SlotSeconds).Active) continue;
            for (int seg = 0; seg < Segments; seg++)
                if (MegastormModel.Hash01(model.State.Seed, 1000 + seg, slot) < chance) ids.Add(CellId(slot, seg));
        }
        return ids.Count;
    }
}
