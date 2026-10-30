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

    // ---- Far flash field (WO5; the GPU twin is ring_ms_flash_glow in materials/ring_far_storm_lighting.gdshaderinc) ----
    public const double SegmentMeters = 5000.0, FlashRadiusMeters = 6000.0;
    public const double Chance = SlotSeconds / MeanIntervalSeconds;

    /// <summary>
    /// One far-field flash: the cell (slot, segment) fired. It starts at <see cref="StartTimeSeconds"/> (slot start plus a hashed offset within the slot)
    /// at a point of the MOVING frame: <see cref="XiMeters"/> behind the edge at <see cref="AcrossMeters"/>, <see cref="AltitudeMeters"/> above the floor,
    /// so it travels with the wall. Its brightness is RingLightningEvent.PulseAtAge(t - start) x <see cref="Energy"/>.
    /// </summary>
    public readonly record struct MegastormFlash(long Id, long Slot, int Segment, double StartTimeSeconds, double AcrossMeters, double XiMeters,
        double AltitudeMeters, float Energy);

    /// <summary>The flash of one cell, or null when it does not fire (or the storm is not active at the slot start).</summary>
    public static MegastormFlash? Flash(MegastormModel model, long slot, int segment)
    {
        if (!model.Triggered || segment < 0 || segment >= Segments || !model.Evaluate(slot * SlotSeconds).Active) return null;
        int seed = model.State.Seed;
        if (MegastormModel.Hash01(seed, 1000 + segment, slot) >= Chance) return null;
        return new MegastormFlash(CellId(slot, segment), slot, segment,
            slot * SlotSeconds + SlotSeconds * MegastormModel.Hash01(seed, 2000 + segment, slot),
            (segment + 0.15 + 0.7 * MegastormModel.Hash01(seed, 3000 + segment, slot)) * SegmentMeters,
            2000.0 + 18000.0 * MegastormModel.Hash01(seed, 4000 + segment, slot),
            4000.0 + 7000.0 * MegastormModel.Hash01(seed, 5000 + segment, slot),
            (float)(0.6 + 0.4 * MegastormModel.Hash01(seed, 6000 + segment, slot)));
    }

    /// <summary>
    /// Flashes whose START time lies in [t0, t1), in start order within each slot (slot, then segment; list cleared first). This is the CPU view
    /// of the far field (sprites, rumble density and field ripples read it): exactly the flashes the wedge pass draws.
    /// </summary>
    public static int FlashesInWindow(MegastormModel model, double t0, double t1, List<MegastormFlash> flashes)
    {
        flashes.Clear();
        if (!model.Triggered || t1 <= t0) return 0;
        long first = (long)Math.Floor(t0 / SlotSeconds), last = (long)Math.Floor(t1 / SlotSeconds);
        for (long slot = first; slot <= last; slot++)
            for (int seg = 0; seg < Segments; seg++)
                if (Flash(model, slot, seg) is MegastormFlash f && f.StartTimeSeconds >= t0 && f.StartTimeSeconds < t1) flashes.Add(f);
        return flashes.Count;
    }

    /// <summary>
    /// Flash light at a point of the moving frame (reference-line distance x, across, altitude) at time t: the sum over the cells the shader visits
    /// (the segment of the point and its two neighbours, the current and previous slot) of pulse x energy x exp(-(d / 6 km)^2), times strength.
    /// The CPU twin of ring_ms_flash_glow (parity test); <paramref name="reduced"/> selects the reduced-flashes envelope.
    /// </summary>
    public static double GlowAt(MegastormModel model, double x, double across, double altitude, double t, bool reduced)
    {
        MegastormFrame frame = model.Evaluate(t);
        if (!frame.Active) return 0.0;
        long slot = (long)Math.Floor(t / SlotSeconds);
        int center = (int)Math.Floor(across / SegmentMeters);
        double glow = 0.0;
        for (long k = slot - 1; k <= slot; k++)
            for (int seg = center - 1; seg <= center + 1; seg++)
            {
                if (Flash(model, k, seg) is not MegastormFlash f) continue;
                double age = t - f.StartTimeSeconds;
                double pulse = reduced ? Lightning.RingLightningEvent.ReducedPulseAtAge(age) : NormalPulseAtAge(age);
                if (pulse <= 0.0) continue;
                double fx = f.XiMeters + model.LeadOffset(f.AcrossMeters, t);
                double dx = (x - fx) / FlashRadiusMeters, dy = (altitude - f.AltitudeMeters) / FlashRadiusMeters, dz = (across - f.AcrossMeters) / FlashRadiusMeters;
                glow += pulse * f.Energy * Math.Exp(-(dx * dx + dy * dy + dz * dz));
            }
        return glow * frame.Strength;
    }

    /// <summary>RingLightningEvent.PulseAtAge without the global reduced-flashes switch (the full envelope).</summary>
    private static double NormalPulseAtAge(double age)
    {
        if (age < 0.0 || age > Lightning.RingLightningEvent.LifetimeSeconds) return 0.0;
        static double G(double a, double c, double w) { double v = (a - c) / w; return Math.Exp(-0.5 * v * v); }
        double primary = G(age, 0.035, 0.018) + G(age, 0.095, 0.035) * 0.38;
        return Math.Clamp(primary + G(age, 0.265, 0.016) * 0.62 + G(age, 0.505, 0.022) * 0.35, 0.0, 1.0);
    }
}
