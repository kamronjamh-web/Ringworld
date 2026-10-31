using System;
using System.Collections.Generic;
using RingworldTechDemo.Lightning;

namespace RingworldTechDemo;

/// <summary>One lightning scythe (docs/megastorm-design.md 5.5.4): a strike that attaches at <see cref="AttachAroundMeters"/> and is dragged forward, in the storm's direction, into a ground-skimming plasma sheet.</summary>
public readonly record struct MegastormScythe(long Id, long Slot, int Segment, double StartTimeSeconds, double AttachAroundMeters, double AttachAcrossMeters,
    double XiMeters, int Direction, float HeadingRadians, float LengthMeters, float WidthMeters, float ThicknessMeters, float LifetimeSeconds,
    float TipSpeedMetersPerSecond, float Energy, int PointCount, int StrokeCount, float SkimQuantile);

/// <summary>A polyline point of a scythe: ring ground-plane position, the smoothed ground under it and the sheet's lower edge above that ground (0.5-4 m).</summary>
public readonly record struct ScythePoint(double AroundMeters, double AcrossMeters, float SmoothedGroundMeters, float SkimMeters)
{
    public double LowerEdgeMeters => SmoothedGroundMeters + SkimMeters;
}

/// <summary>One crackle one-shot of a scythe at a listener: the tear of path point <see cref="PointIndex"/> for return stroke <see cref="Stroke"/>, heard <see cref="ArrivalTimeSeconds"/> (weather clock) after its source time plus d / 343.</summary>
public readonly record struct ScytheCrackle(double ArrivalTimeSeconds, int PointIndex, int Stroke, float DistanceMeters, float Amplitude);

/// <summary>State of a scythe at one time: the tip along the path, the stroke pulse and the sheet's fade.</summary>
public readonly record struct ScytheState(bool Visible, double AgeSeconds, float TipDistanceMeters, float Pulse, float Fade, int StrokesFired);

/// <summary>
/// Scythe stream and path maths (docs/megastorm-design.md 5.5.4, WO12). Pure in (model, ids, t): no nodes, no state, so rewind and save/load reproduce
/// ids, paths and pulses exactly. Cells are 0.5 s slots x the 64 flash segments of 5 km; a cell fires with chance 20 per second x slot / 64 x the
/// strike-rate scale, so the whole wall produces 20 scythes per second at scale 1. The attachment xi follows the scythe density profile of
/// <see cref="MegastormHazardMath"/> (xi -15 .. 20 km, peaking over 0-10 km).
/// </summary>
public static class MegastormScythes
{
    public const double SlotSeconds = 0.5, SegmentMeters = MegastormLightning.SegmentMeters;
    public const int Segments = MegastormLightning.Segments;
    /// <summary>Id tag of the scythe stream (the flash field uses plain cell ids, the blue stream 1 &lt;&lt; 50), so ids never collide.</summary>
    public const long IdTag = 1L << 51;
    public const double SmoothingMeters = 100.0, MaxLifetimeSeconds = 3.0;
    public const float MinSkimMeters = 0.5f, MaxSkimMeters = 4.0f;
    public const int MaxPoints = 16, MaxStrokes = 8;

    private const int CdfSteps = 512;
    private static readonly double[] XiCdf = BuildXiCdf();

    private static double[] BuildXiCdf()
    {
        var cdf = new double[CdfSteps + 1];
        double step = (MegastormHazardMath.ScytheZoneEndXi - MegastormHazardMath.ScytheZoneStartXi) / CdfSteps, sum = 0.0;
        for (int i = 0; i < CdfSteps; i++) { sum += MegastormHazardMath.ScytheProfile(MegastormHazardMath.ScytheZoneStartXi + (i + 0.5) * step); cdf[i + 1] = sum; }
        for (int i = 0; i <= CdfSteps; i++) cdf[i] /= sum;
        return cdf;
    }

    /// <summary>Inverse of the attachment-xi distribution (the scythe density profile).</summary>
    public static double XiFromQuantile(double u)
    {
        int lo = 0, hi = CdfSteps;
        while (hi - lo > 1) { int mid = (lo + hi) >> 1; if (XiCdf[mid] <= u) lo = mid; else hi = mid; }
        double span = XiCdf[hi] - XiCdf[lo], f = span > 0.0 ? (u - XiCdf[lo]) / span : 0.0;
        double step = (MegastormHazardMath.ScytheZoneEndXi - MegastormHazardMath.ScytheZoneStartXi) / CdfSteps;
        return MegastormHazardMath.ScytheZoneStartXi + (lo + f) * step;
    }

    public static long CellId(long slot, int segment) => (slot * Segments + segment) | IdTag;
    public static bool IsScytheId(long id) => (id & IdTag) != 0;
    private static void Decode(long id, out long slot, out int segment) { long cell = id & (IdTag - 1); slot = cell / Segments; segment = (int)(cell % Segments); }

    private static double H(MegastormModel model, long slot, int seg, int k) => MegastormModel.Hash01(model.State.Seed, 30000 + k * Segments + seg, slot);

    /// <summary>The inverse of smoothstep on [0, 1]: q in [0, 1] to x with 3x^2 - 2x^3 = q.</summary>
    public static double InverseSmoothstep(double q) => 0.5 - Math.Sin(Math.Asin(1.0 - 2.0 * Math.Clamp(q, 0.0, 1.0)) / 3.0);

    /// <summary>The scythe of a cell with every parameter derived, ignoring the chance roll; null when the storm is not active at the slot start.</summary>
    public static MegastormScythe? FromCell(MegastormModel model, long slot, int seg)
    {
        if (!model.Triggered || seg < 0 || seg >= Segments || slot < 0) return null;
        double slotStart = slot * SlotSeconds;
        if (!model.Evaluate(slotStart).Active) return null;
        double start = slotStart + SlotSeconds * H(model, slot, seg, 0);
        double xi = XiFromQuantile(H(model, slot, seg, 1));
        double across = Math.Min((seg + 0.15 + 0.7 * H(model, slot, seg, 2)) * SegmentMeters, model.Settings.WidthMeters);
        int dir = model.State.Direction;
        double around = MegastormModel.WrapPositive(model.AroundAt(start) - dir * (xi + model.LeadOffset(across, start)), model.Settings.CircumferenceMeters);
        double life = 1.0 + 2.0 * 0.5 * (H(model, slot, seg, 3) + H(model, slot, seg, 4));
        double lMin = Math.Max(2000.0, 1500.0 * life), lMax = Math.Min(4900.0, 2000.0 * life);
        double length = lMin + (lMax - lMin) * 0.5 * (H(model, slot, seg, 5) + H(model, slot, seg, 6));
        double vTip = Math.Clamp(length / life + (2000.0 - length / life) * H(model, slot, seg, 7), 1500.0, 2000.0);
        return new MegastormScythe(CellId(slot, seg), slot, seg, start, around, across, xi, dir,
            (float)((H(model, slot, seg, 8) - 0.5) * 0.30), (float)length, (float)(50.0 + 150.0 * H(model, slot, seg, 9)),
            (float)(2.0 + 6.0 * H(model, slot, seg, 10)), (float)life, (float)vTip, (float)(0.6 + 0.4 * H(model, slot, seg, 11)),
            12 + (int)(5.0 * Math.Min(0.999, H(model, slot, seg, 12))), 3 + (int)(6.0 * Math.Min(0.999, H(model, slot, seg, 13))), (float)H(model, slot, seg, 14));
    }

    /// <summary>The scythe of a cell, or null when it does not fire at this strike-rate scale (a scale below 1 keeps a subset, above 1 a superset).</summary>
    public static MegastormScythe? Scythe(MegastormModel model, long slot, int seg, float chanceScale)
    {
        if (!model.Triggered || chanceScale <= 0.0f || seg < 0 || seg >= Segments) return null;
        if (H(model, slot, seg, 15) >= MegastormHazardMath.ScytheRatePerSecond * SlotSeconds / Segments * chanceScale) return null;
        return FromCell(model, slot, seg);
    }

    /// <summary>Re-derives a scythe from its event id (the cell it fired in), whatever the strike-rate scale.</summary>
    public static MegastormScythe? FromId(MegastormModel model, long id)
    {
        if (!IsScytheId(id)) return null;
        Decode(id, out long slot, out int seg);
        return FromCell(model, slot, seg);
    }

    /// <summary>
    /// Scythes that START in [t0, t1) with the attachment within <paramref name="reachMeters"/> of the observer (slot, then segment; list cleared first).
    /// Pass <c>double.PositiveInfinity</c> for the whole wall. <paramref name="chanceScale"/> is <c>LightningVisualPolicy.NaturalStrikeChanceScale</c>.
    /// </summary>
    public static int ScythesInWindow(MegastormModel model, double t0, double t1, double observerAround, double observerAcross, double reachMeters,
        float chanceScale, List<MegastormScythe> list)
    {
        list.Clear();
        if (!model.Triggered || t1 <= t0 || chanceScale <= 0.0f) return 0;
        double c = model.Settings.CircumferenceMeters;
        int lo = 0, hi = Segments - 1;
        if (double.IsFinite(reachMeters))
        {
            lo = Math.Max(0, (int)Math.Floor((observerAcross - reachMeters) / SegmentMeters) - 1);
            hi = Math.Min(Segments - 1, (int)Math.Floor((observerAcross + reachMeters) / SegmentMeters) + 1);
        }
        long first = (long)Math.Floor(t0 / SlotSeconds), last = (long)Math.Floor(t1 / SlotSeconds);
        for (long slot = Math.Max(0, first); slot <= last; slot++)
            for (int seg = lo; seg <= hi; seg++)
            {
                if (Scythe(model, slot, seg, chanceScale) is not MegastormScythe s || s.StartTimeSeconds < t0 || s.StartTimeSeconds >= t1) continue;
                if (double.IsFinite(reachMeters))
                {
                    double da = MegastormModel.WrapSigned(s.AttachAroundMeters - observerAround, c), dz = s.AttachAcrossMeters - observerAcross;
                    if (da * da + dz * dz > reachMeters * reachMeters) continue;
                }
                list.Add(s);
            }
        return list.Count;
    }

    // ---- Smoothed ground (100 m) ----
    /// <summary>The ground smoothed over 100 m: a box filter of 3 x 3 taps at +-33.3 m (each tap the centre of a third of the box).</summary>
    public static double SmoothedGround(Func<double, double, double> heightAt, double around, double across)
    {
        const double d = SmoothingMeters / 3.0;
        double sum = 0.0;
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++) sum += heightAt(around + i * d, across + j * d);
        return sum / 9.0;
    }

    /// <summary>Depth of the local ground below the smoothed skim surface: smoothed minus local (a trench is positive here; the cut share reads height minus this).</summary>
    public static float GroundBelowSkimMeters(Func<double, double, double> heightAt, double around, double across)
        => (float)(SmoothedGround(heightAt, around, across) - heightAt(around, across));

    // ---- Path ----
    /// <summary>
    /// The full polyline (12-16 points from the attachment to the end of the sheet) with the lower edge 0.5-4 m above the smoothed ground at every point.
    /// The polyline is fixed in the world; the tip moves along it (<see cref="StateAt"/>). <paramref name="smoothedGround"/> is the already smoothed
    /// ground height (null = 0); build it with <see cref="SmoothedGround"/>. Returns the point count.
    /// </summary>
    public static int PathPoints(MegastormModel model, in MegastormScythe s, Func<double, double, double>? smoothedGround, Span<ScythePoint> points)
    {
        int n = Math.Min(s.PointCount, points.Length);
        if (n < 2) return 0;
        double c = model.Settings.CircumferenceMeters, width = model.Settings.WidthMeters;
        double fx = s.Direction * Math.Cos(s.HeadingRadians), fz = Math.Sin(s.HeadingRadians), nx = -fz, nz = s.Direction * Math.Cos(s.HeadingRadians);
        double amp = 30.0 + 90.0 * H(model, s.Slot, s.Segment, 16);
        double l1 = 2000.0 + 1600.0 * H(model, s.Slot, s.Segment, 17), l2 = 800.0 + 600.0 * H(model, s.Slot, s.Segment, 18);
        double p1 = Math.Tau * H(model, s.Slot, s.Segment, 19), p2 = Math.Tau * H(model, s.Slot, s.Segment, 20);
        for (int i = 0; i < n; i++)
        {
            double along = s.LengthMeters * i / (n - 1);
            double ramp = MegastormModel.Smooth(along / (0.2 * s.LengthMeters));
            double lat = amp * ramp * (0.65 * Math.Sin(Math.Tau * along / l1 + p1) + 0.35 * Math.Sin(Math.Tau * along / l2 + p2));
            double around = MegastormModel.WrapPositive(s.AttachAroundMeters + fx * along + nx * lat, c);
            double across = Math.Clamp(s.AttachAcrossMeters + fz * along + nz * lat, 0.0, width);
            double q = Math.Clamp(s.SkimQuantile + 0.3 * (H(model, s.Slot, s.Segment, 24 + i) - 0.5), 0.0, 1.0);
            float skim = (float)(MinSkimMeters + (MaxSkimMeters - MinSkimMeters) * InverseSmoothstep(q));
            points[i] = new ScythePoint(around, across, smoothedGround != null ? (float)smoothedGround(around, across) : 0.0f, skim);
        }
        return n;
    }

    /// <summary>Stroke times (seconds after the start): 3-8 return strokes 0.2-0.5 s apart, all inside the lifetime. Returns the count.</summary>
    public static int StrokeTimes(MegastormModel model, in MegastormScythe s, Span<float> times, Span<float> amplitudes)
    {
        double t = 0.04, maxGap = (s.LifetimeSeconds - 0.12) / 2.0;
        int count = 0;
        for (int i = 0; i < Math.Min(s.StrokeCount, Math.Min(times.Length, amplitudes.Length)); i++)
        {
            if (t > s.LifetimeSeconds - 0.08 + 1e-9) break;
            times[count] = (float)t; amplitudes[count] = i == 0 ? 1.0f : (float)(0.55 + 0.4 * H(model, s.Slot, s.Segment, 40 + i)); count++;
            t += Math.Min(0.2 + 0.3 * H(model, s.Slot, s.Segment, 48 + i), maxGap);
        }
        return count;
    }

    /// <summary>
    /// The scythe at time t: the tip distance along the path (it moves in the storm's direction at 1.5-2 km/s and holds at the end), the stroke pulse
    /// (each return stroke re-brightens the whole sheet over a continuing-current glow) and the end-of-life fade. The reduced envelope is a slow swell
    /// per stroke at <see cref="LightningVisualPolicy.ReducedPeakScale"/> with no step larger than 0.05 per frame at 60 fps.
    /// </summary>
    public static ScytheState StateAt(MegastormModel model, in MegastormScythe s, double t, bool reduced)
    {
        double age = t - s.StartTimeSeconds;
        if (age < 0.0 || age > s.LifetimeSeconds) return new ScytheState(false, age, 0.0f, 0.0f, 0.0f, 0);
        Span<float> times = stackalloc float[MaxStrokes], amps = stackalloc float[MaxStrokes];
        int n = StrokeTimes(model, s, times, amps);
        double fade = 1.0 - MegastormModel.Smooth((age - (s.LifetimeSeconds - 0.4)) / 0.4);
        double rise = reduced ? MegastormModel.Smooth(age / 0.5) : MegastormModel.Smooth(age / 0.15);
        double best = 0.0; int fired = 0;
        for (int i = 0; i < n; i++)
        {
            double d = age - times[i];
            if (d >= 0.0) fired++;
            double v;
            if (reduced) { double g = (age - (times[i] + 0.1)) / 0.14; v = amps[i] * Math.Exp(-0.5 * g * g); }
            else
            {
                double sharp = Math.Exp(-0.5 * Math.Pow((d - 0.012) / 0.012, 2.0)), tail = d > 0.0 ? 0.85 * Math.Exp(-d / 0.09) : 0.0;
                v = amps[i] * Math.Max(sharp, tail);
            }
            best = Math.Max(best, v);
        }
        double pulse;
        if (reduced) pulse = best * LightningVisualPolicy.ReducedPeakScale * rise;
        else
        {
            double lastEnd = n > 0 ? times[n - 1] + 0.15 : 0.15;
            double cc = 0.2 * (age < lastEnd ? 1.0 : Math.Exp(-(age - lastEnd) / 0.15));
            pulse = Math.Clamp(Math.Max(best, cc), 0.0, 1.0);
        }
        float tip = (float)Math.Min(s.LengthMeters, s.TipSpeedMetersPerSecond * age);
        return new ScytheState(true, age, tip, (float)(pulse * fade), (float)fade, fired);
    }

    /// <summary>Doc form: the path and state of the scythe an event id names at time t (false when the id is not a live scythe at t).</summary>
    public static bool ScythePath(MegastormModel model, long eventId, double t, Func<double, double, double>? smoothedGround, Span<ScythePoint> points,
        out int pointCount, out ScytheState state, bool reduced = false)
    {
        pointCount = 0; state = default;
        if (FromId(model, eventId) is not MegastormScythe s) return false;
        state = StateAt(model, s, t, reduced);
        pointCount = PathPoints(model, s, smoothedGround, points);
        return state.Visible;
    }

    /// <summary>Position along the polyline at a distance from the attachment (points are evenly spaced along the length).</summary>
    public static (double Around, double Across) PositionAt(ReadOnlySpan<ScythePoint> points, int count, in MegastormScythe s, float distanceMeters, double circumference)
    {
        double f = Math.Clamp(distanceMeters / Math.Max(1.0, s.LengthMeters), 0.0, 1.0) * (count - 1);
        int i = Math.Min((int)f, count - 2);
        double u = f - i;
        double da = MegastormModel.WrapSigned(points[i + 1].AroundMeters - points[i].AroundMeters, circumference);
        return (MegastormModel.WrapPositive(points[i].AroundMeters + da * u, circumference), points[i].AcrossMeters + (points[i + 1].AcrossMeters - points[i].AcrossMeters) * u);
    }

    /// <summary>
    /// Distance (ground plane) from a point to the live part of the nearest scythe, +Infinity when none is alive within the search. Straight chord from the
    /// attachment to the tip (the meander is under 0.3 km). Pure in (model, point, t, scale): the hazard query's <c>NearestScytheDistanceMeters</c>.
    /// </summary>
    public static float NearestDistanceMeters(MegastormModel model, double around, double across, double t, float chanceScale)
    {
        if (!model.Triggered || chanceScale <= 0.0f) return float.PositiveInfinity;
        double c = model.Settings.CircumferenceMeters, best = double.PositiveInfinity;
        int lo = Math.Max(0, (int)Math.Floor((across - 7000.0) / SegmentMeters)), hi = Math.Min(Segments - 1, (int)Math.Floor((across + 7000.0) / SegmentMeters));
        long first = (long)Math.Floor((t - MaxLifetimeSeconds) / SlotSeconds), last = (long)Math.Floor(t / SlotSeconds);
        for (long slot = Math.Max(0, first); slot <= last; slot++)
            for (int seg = lo; seg <= hi; seg++)
            {
                if (Scythe(model, slot, seg, chanceScale) is not MegastormScythe s) continue;
                double age = t - s.StartTimeSeconds;
                if (age < 0.0 || age > s.LifetimeSeconds) continue;
                double tip = Math.Min(s.LengthMeters, s.TipSpeedMetersPerSecond * age);
                double fx = s.Direction * Math.Cos(s.HeadingRadians), fz = Math.Sin(s.HeadingRadians);
                double px = MegastormModel.WrapSigned(around - s.AttachAroundMeters, c), pz = across - s.AttachAcrossMeters;
                double along = Math.Clamp(px * fx + pz * fz, 0.0, tip);
                double dx = px - fx * along, dz = pz - fz * along;
                best = Math.Min(best, Math.Sqrt(dx * dx + dz * dz));
            }
        return (float)best;
    }

    // ---- Crackle schedule (audio) ----
    public const double SpeedOfSoundMetersPerSecond = 343.0;

    /// <summary>
    /// The crackle one-shots of a scythe for a listener: stroke 0 is the TIP crackle (point i tears when the tip passes it, at i / (n-1) x length / tip
    /// speed), every later return stroke fires a tear at every third path point (and the last) at the stroke time. Each arrives at its own source time
    /// plus its distance / 343, so the rip is smeared along the path. Sorted by arrival; list cleared first.
    /// </summary>
    public static int CrackleSchedule(MegastormModel model, in MegastormScythe s, ReadOnlySpan<ScythePoint> points, int count,
        double listenerAround, double listenerAcross, List<ScytheCrackle> list)
    {
        list.Clear();
        if (count < 2) return 0;
        double c = model.Settings.CircumferenceMeters;
        Span<float> times = stackalloc float[MaxStrokes], amps = stackalloc float[MaxStrokes];
        int strokes = StrokeTimes(model, s, times, amps);
        for (int i = 0; i < count; i++)
        {
            double da = MegastormModel.WrapSigned(points[i].AroundMeters - listenerAround, c), dz = points[i].AcrossMeters - listenerAcross;
            float d = (float)Math.Sqrt(da * da + dz * dz);
            double delay = d / SpeedOfSoundMetersPerSecond;
            double along = s.LengthMeters * i / (count - 1);
            list.Add(new ScytheCrackle(s.StartTimeSeconds + along / s.TipSpeedMetersPerSecond + delay, i, 0, d, amps[0]));
            if (i % 3 == 0 || i == count - 1)
                for (int k = 1; k < strokes; k++) list.Add(new ScytheCrackle(s.StartTimeSeconds + times[k] + delay, i, k, d, amps[k]));
        }
        list.Sort((a, b) => a.ArrivalTimeSeconds.CompareTo(b.ArrivalTimeSeconds));
        return list.Count;
    }
}
