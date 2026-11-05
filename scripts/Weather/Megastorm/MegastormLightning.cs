using System;
using System.Collections.Generic;

namespace RingworldTechDemo;

/// <summary>
/// Deterministic flash-slot ids of the far flash field (docs/megastorm-design.md 3.3): 64 segments of 5 km across, 2 s slots, mean interval 10 s per
/// segment, all in the moving frame so they depend only on (seed, segment, slot). WO5 builds the renderer and positions on top of this; WO2 owns the
/// ids so the save/load replay can assert them.
/// </summary>
public static class MegastormLightning
{
    public const int Segments = 64;
    public const double SlotSeconds = 2.0, MeanIntervalSeconds = 10.0;

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
    public const double FlashXiMinMeters = 500.0, FlashXiMaxMeters = 37500.0;
    public const double Chance = SlotSeconds / MeanIntervalSeconds;

    /// <summary>
    /// One far-field flash: the cell (slot, segment) fired. It starts at <see cref="StartTimeSeconds"/> (slot start plus a hashed offset within the slot)
    /// at a point of the MOVING frame: <see cref="XiMeters"/> behind the edge at <see cref="AcrossMeters"/>, <see cref="AltitudeMeters"/> above the floor,
    /// so it travels with the wall. Its brightness is RingLightningEvent.PulseAtAge(t - start) x <see cref="Energy"/>.
    /// </summary>
    public readonly record struct MegastormFlash(long Id, long Slot, int Segment, double StartTimeSeconds, double AcrossMeters, double XiMeters,
        double AltitudeMeters, float Energy);

    /// <summary>The flash of one cell, or null when it does not fire (or the storm is not active at the slot start).</summary>
    public static MegastormFlash? Flash(MegastormModel model, long slot, int segment) => Flash(model, slot, segment, 1.0f);

    /// <summary>
    /// The flash of one cell at a strike-rate multiplier (WO7: "Lightning frequency" x the reduced-flashes share). The roll is the same hash, so a
    /// multiplier below 1 keeps a subset of the cells at 1 and a multiplier above 1 a superset; everything else about a cell is multiplier independent.
    /// </summary>
    public static MegastormFlash? Flash(MegastormModel model, long slot, int segment, float chanceScale)
    {
        if (!model.Triggered || segment < 0 || segment >= Segments || !model.Evaluate(slot * SlotSeconds).Active) return null;
        int seed = model.State.Seed;
        if (MegastormModel.Hash01(seed, 1000 + segment, slot) >= Chance * Math.Max(0.0f, chanceScale)) return null;
        return new MegastormFlash(CellId(slot, segment), slot, segment,
            slot * SlotSeconds + SlotSeconds * MegastormModel.Hash01(seed, 2000 + segment, slot),
            (segment + 0.15 + 0.7 * MegastormModel.Hash01(seed, 3000 + segment, slot)) * SegmentMeters,
            FlashXiMinMeters + (FlashXiMaxMeters - FlashXiMinMeters) * MegastormModel.Hash01(seed, 4000 + segment, slot),
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
    /// The CPU twin of ring_ms_flash_glow (parity test); <paramref name="reduced"/> selects the reduced-flashes envelope and
    /// <paramref name="chanceScale"/> the strike-rate multiplier (the far pass feeds LightningVisualPolicy.NaturalStrikeChanceScale).
    /// </summary>
    public static double GlowAt(MegastormModel model, double x, double across, double altitude, double t, bool reduced, float chanceScale = 1.0f)
    {
        MegastormFrame frame = model.Evaluate(t);
        if (!frame.Active) return 0.0;
        long slot = (long)Math.Floor(t / SlotSeconds);
        int center = (int)Math.Floor(across / SegmentMeters);
        double glow = 0.0;
        for (long k = slot - 1; k <= slot; k++)
            for (int seg = center - 1; seg <= center + 1; seg++)
            {
                if (Flash(model, k, seg, chanceScale) is not MegastormFlash f) continue;
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

    // ---- Near strike stream (WO7) ----
    /// <summary>Near strikes are the far-field cells whose point lies within this horizontal distance of the observer.</summary>
    public const double NearReachMeters = 50000.0;
    /// <summary>"Bolt from the blue": a second, independent stream of this share of the main cell rate, at xi in [-15, -5] km (ahead of the edge).</summary>
    public const double BlueShare = 0.1, BlueXiMinMeters = -15000.0, BlueXiMaxMeters = -5000.0;
    /// <summary>Id tag of the blue stream (the main stream uses plain <see cref="CellId"/>), so the two never collide.</summary>
    public const long BlueIdTag = 1L << 50;

    /// <summary>
    /// A near strike: a cell of the flash field (or of the blue stream) that fires. <see cref="Flash"/> carries the shared id, start time, across,
    /// altitude and energy; the strike point is FIXED at spawn (<see cref="AroundMeters"/>, physical: a channel does not follow the wall).
    /// </summary>
    public readonly record struct NearStrike(MegastormFlash Flash, double AroundMeters, double XiMeters, bool Blue);

    /// <summary>The blue-stream flash of a cell (salts 8000..), or null. Id = CellId | <see cref="BlueIdTag"/>.</summary>
    public static MegastormFlash? BlueFlash(MegastormModel model, long slot, int segment, float chanceScale)
    {
        if (!model.Triggered || segment < 0 || segment >= Segments || !model.Evaluate(slot * SlotSeconds).Active) return null;
        int seed = model.State.Seed;
        if (MegastormModel.Hash01(seed, 8000 + segment, slot) >= Chance * BlueShare * Math.Max(0.0f, chanceScale)) return null;
        return new MegastormFlash(CellId(slot, segment) | BlueIdTag, slot, segment,
            slot * SlotSeconds + SlotSeconds * MegastormModel.Hash01(seed, 9000 + segment, slot),
            (segment + 0.15 + 0.7 * MegastormModel.Hash01(seed, 10000 + segment, slot)) * SegmentMeters,
            BlueXiMinMeters + (BlueXiMaxMeters - BlueXiMinMeters) * MegastormModel.Hash01(seed, 11000 + segment, slot),
            4000.0 + 7000.0 * MegastormModel.Hash01(seed, 12000 + segment, slot),
            (float)(0.6 + 0.4 * MegastormModel.Hash01(seed, 13000 + segment, slot)));
    }

    /// <summary>
    /// Near strikes whose START time lies in [t0, t1) within <see cref="NearReachMeters"/> of the observer (slot, then segment, main stream before blue
    /// within a slot; list cleared first). Pure in (model, window, observer, scale), so rewind and save/load reproduce ids and times exactly.
    /// <paramref name="chanceScale"/> is <c>LightningVisualPolicy.NaturalStrikeChanceScale</c>.
    /// </summary>
    public static int NearStrikesInWindow(MegastormModel model, double t0, double t1, double observerAround, double observerAcross, float chanceScale,
        List<NearStrike> strikes)
    {
        strikes.Clear();
        if (!model.Triggered || t1 <= t0 || chanceScale <= 0.0f) return 0;
        double circumference = model.Settings.CircumferenceMeters;
        int segLo = Math.Max(0, (int)Math.Floor((observerAcross - NearReachMeters) / SegmentMeters) - 1);
        int segHi = Math.Min(Segments - 1, (int)Math.Floor((observerAcross + NearReachMeters) / SegmentMeters) + 1);
        long first = (long)Math.Floor(t0 / SlotSeconds), last = (long)Math.Floor(t1 / SlotSeconds);
        for (long slot = first; slot <= last; slot++)
            for (int pass = 0; pass < 2; pass++)
                for (int seg = segLo; seg <= segHi; seg++)
                {
                    MegastormFlash? cell = pass == 0 ? Flash(model, slot, seg, chanceScale) : BlueFlash(model, slot, seg, chanceScale);
                    if (cell is not MegastormFlash f || f.StartTimeSeconds < t0 || f.StartTimeSeconds >= t1) continue;
                    double s = model.AroundAt(f.StartTimeSeconds);
                    double around = MegastormModel.WrapPositive(s - model.State.Direction * (f.XiMeters + model.LeadOffset(f.AcrossMeters, f.StartTimeSeconds)), circumference);
                    double da = MegastormModel.WrapSigned(around - observerAround, circumference), dz = f.AcrossMeters - observerAcross;
                    if (da * da + dz * dz > NearReachMeters * NearReachMeters) continue;
                    strikes.Add(new NearStrike(f, around, f.XiMeters, pass == 1));
                }
        return strikes.Count;
    }

    // ---- Field strikes (WO13; the stream lives in MegastormFieldStrikes) ----
    /// <summary>Field strikes (upward discharges that end on the containment field) whose START time lies in [t0, t1); empty without an installed field provider.</summary>
    public static int FieldStrikesInWindow(MegastormModel model, double t0, double t1, float chanceScale, List<MegastormFieldStrike> strikes)
        => MegastormFieldStrikes.FieldStrikesInWindow(model, t0, t1, chanceScale, strikes);
}
