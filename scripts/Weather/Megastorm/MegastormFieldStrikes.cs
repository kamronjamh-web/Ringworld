using System;
using System.Collections.Generic;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>One megastorm field strike (docs/megastorm-design.md 5.5.5): an upward discharge from a dome top that ends 50 m below the containment field.</summary>
public readonly record struct MegastormFieldStrike(long Id, long Slot, int Segment, double StartTimeSeconds, double AroundMeters, double AcrossMeters,
    double BaseAltitudeMeters, double ContactAltitudeMeters, double GapMeters, float Energy, float DurationSeconds, int ShapeSeed);

/// <summary>One near-rim contact arc: a 50-500 m, 0.2-0.5 s flash where the wall top presses the field. Visual only: it publishes no disturbance.</summary>
public readonly record struct MegastormContactArc(long Id, long Slot, int Side, double StartTimeSeconds, double AroundMeters, double AcrossMeters,
    double BaseAltitudeMeters, double ContactAltitudeMeters, float Energy, float DurationSeconds, int ShapeSeed);

/// <summary>
/// The field-strike and contact-arc streams of the megastorm (WO13). Pure in (model, ids, t) and in the installed
/// <see cref="MegastormFieldCoupling.FieldUndersideProvider"/>: no nodes, no state, so rewind and save/load reproduce ids, positions and pulses exactly;
/// without a provider nothing exists. Strikes are cells of 0.5 s slots x 64 across segments with a thinned Poisson law
/// (0.05 per minute per km of wall x (1 + 9 exp(-gap / 2 km)), gap = H - ceiling, none under 0.5 km); arcs are cells of 0.25 s slots x 2 rims.
/// </summary>
public static class MegastormFieldStrikes
{
    public const double SlotSeconds = 0.5, ArcSlotSeconds = 0.25;
    public const int Segments = 64;
    /// <summary>Id tags (flash field: plain cell ids, blue stream 1 &lt;&lt; 50, scythes 1 &lt;&lt; 51).</summary>
    public const long IdTag = 1L << 52, ArcIdTag = 1L << 53;
    public const double PerMinutePerKm = 0.05, GapBoost = 9.0, GapScaleMeters = 2000.0, MinGapMeters = 500.0;
    public const double AttachXiMinMeters = 3000.0, AttachXiMaxMeters = 18000.0, ContactBelowFieldMeters = 50.0;
    /// <summary>Rate window of <see cref="FieldStrikeRatePerSecond"/> (the query integrates +-30 km of wall).</summary>
    public const double QueryHalfWindowMeters = 30000.0;
    /// <summary>Contact arcs: about 2 per second per rim.</summary>
    public const double ArcPerSecondPerSide = 2.0;
    public const double ArcMinLengthMeters = 50.0, ArcMaxLengthMeters = 500.0;
    private const double RateMaxPerKmSecond = PerMinutePerKm / 60.0 * (1.0 + GapBoost);

    /// <summary>Typical raw strike rate per second of the whole wall at scale 1 (the law's total, about 18 per minute), used to size the budget compensation.</summary>
    private const double TypicalWallRatePerSecond = 0.3;
    private static float _compensationScale = float.NaN, _compensation = 1.0f;

    /// <summary>
    /// Raw-roll multiplier that cancels the budget rule's average loss: a raw strike survives when fewer than two raw strikes started in the second before it
    /// (probability e^-mu (1 + mu), mu = raw rate per second), so the multiplier b solves b e^-(m b) (1 + m b) = 1 with m the typical wall rate x scale.
    /// About 1.04 at scale 1 and 1.19 at scale 2.
    /// </summary>
    private static double BudgetCompensation(float chanceScale)
    {
        if (chanceScale == _compensationScale) return _compensation;
        double m = TypicalWallRatePerSecond * chanceScale, b = 1.0;
        for (int i = 0; i < 20; i++)
        {
            double mb = m * b, f = b * Math.Exp(-mb) * (1.0 + mb) - 1.0, df = Math.Exp(-mb) * (1.0 + mb) + b * Math.Exp(-mb) * (m - m * (1.0 + mb) - m * 0.0);
            b -= f / Math.Max(df, 1e-6);
            b = Math.Clamp(b, 1.0, 3.0);
        }
        _compensation = (float)b; _compensationScale = chanceScale;
        return b;
    }

    private static readonly MegastormProfile.CeilingFunction CeilingFn = static across => (float)MegastormFieldCoupling.CeilingAt(across);

    public static bool IsFieldStrikeId(long id) => (id & IdTag) != 0 && (id & ArcIdTag) == 0;
    public static bool IsContactArcId(long id) => (id & ArcIdTag) != 0;

    /// <summary>Gap between the field underside and the cloud ceiling at <paramref name="across"/> (metres); NaN without a provider or outside the roots.</summary>
    public static double GapMeters(double across)
    {
        Func<double, double>? provider = MegastormFieldCoupling.FieldUndersideProvider;
        if (provider == null) return double.NaN;
        double h = provider(across);
        return double.IsNaN(h) ? double.NaN : h - MegastormFieldCoupling.CeilingAt(across);
    }

    /// <summary>The rate law in strikes per second per km of wall for a given gap (0 under 0.5 km, NaN gap, or without a field).</summary>
    public static double RatePerKmSecond(double gapMeters)
        => double.IsNaN(gapMeters) || gapMeters < MinGapMeters ? 0.0 : PerMinutePerKm / 60.0 * (1.0 + GapBoost * Math.Exp(-gapMeters / GapScaleMeters));

    /// <summary>The rate law integrated over +-30 km of wall around <paramref name="across"/> (clipped to the wall), in strikes per second, at strike-rate scale 1.</summary>
    public static double FieldStrikeRatePerSecond(double across, double widthMeters)
    {
        if (MegastormFieldCoupling.FieldUndersideProvider == null) return 0.0;
        double lo = Math.Max(0.0, across - QueryHalfWindowMeters), hi = Math.Min(widthMeters, across + QueryHalfWindowMeters), sum = 0.0;
        const double Step = 250.0;
        int n = Math.Max(1, (int)Math.Ceiling((hi - lo) / Step));
        double h = (hi - lo) / n;
        for (int i = 0; i < n; i++) sum += RatePerKmSecond(GapMeters(lo + (i + 0.5) * h)) * h / 1000.0;
        return sum;
    }

    /// <summary>Rate law integrated over [aLo, aHi] (strikes per second).</summary>
    public static double RateBetween(double aLo, double aHi)
    {
        const double Step = 100.0;
        int n = Math.Max(1, (int)Math.Ceiling((aHi - aLo) / Step));
        double h = (aHi - aLo) / n, sum = 0.0;
        for (int i = 0; i < n; i++) sum += RatePerKmSecond(GapMeters(aLo + (i + 0.5) * h)) * h / 1000.0;
        return sum;
    }

    public static long CellId(long slot, int segment) => (slot * Segments + segment) | IdTag;
    private static double H(MegastormModel model, long slot, int seg, int k) => MegastormModel.Hash01(model.State.Seed, 40000 + k * Segments + seg, slot);

    /// <summary>
    /// Start time of the raw (pre-budget) strike of a cell, or NaN when the cell does not roll, lies under the 0.5 km gap, fails the thinning or the storm is not
    /// active at the start. Cheap (hashes and the provider only), so the budget rule can look at neighbours.
    /// </summary>
    private static double RawStart(MegastormModel model, long slot, int seg, float chanceScale, Func<double, double> provider, out double across, out double gap)
    {
        across = 0.0; gap = double.NaN;
        if (slot < 0 || seg < 0 || seg >= Segments) return double.NaN;
        double width = model.Settings.WidthMeters, segWidth = width / Segments;
        if (H(model, slot, seg, 0) >= RateMaxPerKmSecond * (segWidth / 1000.0) * SlotSeconds * chanceScale * BudgetCompensation(chanceScale)) return double.NaN;
        across = (seg + H(model, slot, seg, 1)) * segWidth;
        gap = GapMeters(across);
        if (!(gap >= MinGapMeters) || H(model, slot, seg, 2) * RateMaxPerKmSecond >= RatePerKmSecond(gap)) return double.NaN;
        double start = slot * SlotSeconds + SlotSeconds * H(model, slot, seg, 3);
        return model.Evaluate(start).Active ? start : double.NaN;
    }

    /// <summary>
    /// The field strike of a cell at <paramref name="chanceScale"/> (a scale below 1 keeps a subset of the cells, above 1 a superset), or null. Budget (the field's
    /// FieldStrike spawn cap is 2 per second with 4 reserved live slots): a raw strike is dropped when two raw strikes started earlier within the second before it
    /// (order: start time, then cell id). No accepted triple can fit in one second, so at most 2 start per second and at most 4 are alive (life under 1.85 s) at any
    /// frequency setting, with no state: the rule only reads neighbouring cells' hashes.
    /// </summary>
    public static MegastormFieldStrike? Strike(MegastormModel model, long slot, int seg, float chanceScale)
    {
        Func<double, double>? provider = MegastormFieldCoupling.FieldUndersideProvider;
        if (provider == null || !model.Triggered || chanceScale <= 0.0f || seg < 0 || seg >= Segments || slot < 0) return null;
        double start = RawStart(model, slot, seg, chanceScale, provider, out double across, out double gap);
        if (double.IsNaN(start)) return null;
        int earlier = 0;
        long cell = slot * Segments + seg;
        for (long k = (long)Math.Floor((start - 1.0) / SlotSeconds); k <= slot && earlier < 2; k++)
            for (int j = 0; j < Segments && earlier < 2; j++)
            {
                if (k == slot && j == seg) continue;
                double other = RawStart(model, k, j, chanceScale, provider, out _, out _);
                if (double.IsNaN(other) || other <= start - 1.0 || other > start) continue;
                if (other < start || (other == start && k * Segments + j < cell)) earlier++;
            }
        if (earlier >= 2) return null;
        // The leader leaves the highest of three hashed candidates in the attachment band (the overshooting cell).
        double bestTop = -1.0, bestX = 0.0;
        for (int k = 0; k < 3; k++)
        {
            double x = AttachXiMinMeters + (AttachXiMaxMeters - AttachXiMinMeters) * H(model, slot, seg, 4 + k) + model.LeadOffset(across, start);
            float top = MegastormProfile.Column(model, x, across, start, CeilingFn).BodyTop;
            if (top > bestTop) { bestTop = top; bestX = x; }
        }
        double contact = provider(across) - ContactBelowFieldMeters;
        if (!(bestTop > 0.0) || !(contact - bestTop > 100.0)) return null;
        double around = MegastormModel.WrapPositive(model.AroundAt(start) - model.State.Direction * bestX, model.Settings.CircumferenceMeters);
        long id = CellId(slot, seg);
        return new MegastormFieldStrike(id, slot, seg, start, around, across, bestTop, contact, gap, (float)(0.6 + 0.4 * H(model, slot, seg, 7)),
            (float)(RingLightningEvent.FieldStrikeMinSeconds + (RingLightningEvent.FieldStrikeMaxSeconds - RingLightningEvent.FieldStrikeMinSeconds) * H(model, slot, seg, 8)),
            (int)(MegastormFieldCoupling.Mix64(id, 201, 0, 13) & 0x7fffffff));
    }

    /// <summary>Field strikes whose START time lies in [t0, t1) (slot, then segment; list cleared first). Pure in (model, window, scale, provider).</summary>
    public static int FieldStrikesInWindow(MegastormModel model, double t0, double t1, float chanceScale, List<MegastormFieldStrike> strikes)
    {
        strikes.Clear();
        if (!model.Triggered || t1 <= t0 || chanceScale <= 0.0f || MegastormFieldCoupling.FieldUndersideProvider == null) return 0;
        long first = Math.Max(0, (long)Math.Floor(t0 / SlotSeconds)), last = (long)Math.Floor(t1 / SlotSeconds);
        for (long slot = first; slot <= last; slot++)
            for (int seg = 0; seg < Segments; seg++)
                if (Strike(model, slot, seg, chanceScale) is MegastormFieldStrike s && s.StartTimeSeconds >= t0 && s.StartTimeSeconds < t1) strikes.Add(s);
        return strikes.Count;
    }

    /// <summary>The ordinary lightning event of a field strike: kind 6, <c>FieldStrike</c> set, GroundPosition = contact point, CloudPosition = channel base, own duration.</summary>
    public static RingLightningEvent ToEvent(in MegastormFieldStrike s) => new(s.Id, MegastormModel.WallRecordId, s.StartTimeSeconds,
        new RingCanonicalPosition(s.AroundMeters, s.ContactAltitudeMeters, s.AcrossMeters), new RingCanonicalPosition(s.AroundMeters, s.BaseAltitudeMeters, s.AcrossMeters),
        s.ShapeSeed, (float)(MegastormModel.Hash01(s.ShapeSeed, 9, s.Id) * Math.Tau), s.Energy, 6000.0f, false,
        MegastormModel.KindMegastorm, false, true, s.DurationSeconds);

    // ---- Contact arcs --------------------------------------------------------------------------------------------------------
    private static double HA(MegastormModel model, long slot, int side, int k) => MegastormModel.Hash01(model.State.Seed, 41000 + k * 2 + side, slot);

    /// <summary>The contact arc of one (slot, rim side) cell, or null (the roll fails, the wall top does not press the field there, or no field).</summary>
    public static MegastormContactArc? Arc(MegastormModel model, long slot, int side, float chanceScale)
    {
        Func<double, double>? provider = MegastormFieldCoupling.FieldUndersideProvider;
        if (provider == null || !model.Triggered || chanceScale <= 0.0f || slot < 0 || (side != 0 && side != 1)) return null;
        if (HA(model, slot, side, 0) >= ArcPerSecondPerSide * ArcSlotSeconds * chanceScale) return null;
        double start = slot * ArcSlotSeconds + ArcSlotSeconds * HA(model, slot, side, 1);
        MegastormFrame frame = model.Evaluate(start);
        if (!frame.Active) return null;
        double width = model.Settings.WidthMeters, edge = MegastormFieldCoupling.BandEdgeMeters(width);
        if (edge <= 0.0) return null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            double rim = edge * HA(model, slot, side, 2 + 3 * attempt);
            double across = side == 0 ? rim : width - rim;
            double x = MegastormModel.WallEndXi * HA(model, slot, side, 3 + 3 * attempt) + model.LeadOffset(across, start);
            float top = MegastormProfile.Column(model, x, across, start, CeilingFn).BodyTop;
            double ceiling = MegastormFieldCoupling.CeilingAt(across), h = provider(across);
            if (top <= 0.0f || double.IsNaN(h)) continue;
            double penetration = Math.Clamp((top - (ceiling - 500.0)) / 1000.0, 0.0, 1.0);
            if (penetration <= 0.0) continue;
            double length = Math.Clamp(h - ContactBelowFieldMeters - top, ArcMinLengthMeters, ArcMaxLengthMeters);
            long id = MegastormFieldCoupling.Mix64(side, (long)Math.Floor(x / 500.0), slot, 5) & ~ArcIdTag | ArcIdTag;
            return new MegastormContactArc(id, slot, side, start, MegastormModel.WrapPositive(model.AroundAt(start) - model.State.Direction * x, model.Settings.CircumferenceMeters),
                across, top, top + length, (float)Math.Clamp(frame.Strength * (0.5 + 0.5 * penetration), 0.0, 1.0),
                (float)(0.2 + 0.3 * HA(model, slot, side, 12)), (int)(MegastormFieldCoupling.Mix64(id, 202, 0, 13) & 0x7fffffff));
        }
        return null;
    }

    /// <summary>Contact arcs whose START time lies in [t0, t1) (slot, then side; list cleared first).</summary>
    public static int ContactArcsInWindow(MegastormModel model, double t0, double t1, float chanceScale, List<MegastormContactArc> arcs)
    {
        arcs.Clear();
        if (!model.Triggered || t1 <= t0 || chanceScale <= 0.0f || MegastormFieldCoupling.FieldUndersideProvider == null) return 0;
        long first = Math.Max(0, (long)Math.Floor(t0 / ArcSlotSeconds)), last = (long)Math.Floor(t1 / ArcSlotSeconds);
        for (long slot = first; slot <= last; slot++)
            for (int side = 0; side < 2; side++)
                if (Arc(model, slot, side, chanceScale) is MegastormContactArc a && a.StartTimeSeconds >= t0 && a.StartTimeSeconds < t1) arcs.Add(a);
        return arcs.Count;
    }

    /// <summary>The event the layer draws for a contact arc (FieldStrike set so the shared envelope applies; it is NOT put in ActiveLightningEvents, so the field adapter never sees it).</summary>
    public static RingLightningEvent ToEvent(in MegastormContactArc a) => new(a.Id, MegastormModel.WallRecordId, a.StartTimeSeconds,
        new RingCanonicalPosition(a.AroundMeters, a.ContactAltitudeMeters, a.AcrossMeters), new RingCanonicalPosition(a.AroundMeters, a.BaseAltitudeMeters, a.AcrossMeters),
        a.ShapeSeed, (float)(MegastormModel.Hash01(a.ShapeSeed, 9, a.Id) * Math.Tau), a.Energy, 1500.0f, false, MegastormModel.KindMegastorm, false, true, a.DurationSeconds);

    /// <summary>
    /// Arc flash 0..1 at a fixed point (around, across) at weather time <paramref name="t"/>: the sum over the arcs of the last second of pulse x energy x
    /// exp(-(d / 3 km)^2). This is the FlashLevel the MegastormContact ripples take from the arcs (their only light source).
    /// </summary>
    public static double ContactArcFlash(MegastormModel model, double around, double across, double t, float chanceScale = 1.0f)
    {
        if (!model.Triggered || MegastormFieldCoupling.FieldUndersideProvider == null) return 0.0;
        double c = model.Settings.CircumferenceMeters, sum = 0.0;
        long last = (long)Math.Floor(t / ArcSlotSeconds), first = Math.Max(0, last - 2);
        for (long slot = first; slot <= last; slot++)
            for (int side = 0; side < 2; side++)
            {
                if (Arc(model, slot, side, chanceScale) is not MegastormContactArc a) continue;
                double age = t - a.StartTimeSeconds;
                if (age < 0.0 || age > a.DurationSeconds) continue;
                double pulse = ToEvent(a).PulseAt(t);
                if (pulse <= 0.0) continue;
                double da = MegastormModel.WrapSigned(a.AroundMeters - around, c) / 3000.0, dz = (a.AcrossMeters - across) / 3000.0;
                sum += pulse * a.Energy * Math.Exp(-(da * da + dz * dz));
            }
        return Math.Min(sum, 1.0);
    }
}
