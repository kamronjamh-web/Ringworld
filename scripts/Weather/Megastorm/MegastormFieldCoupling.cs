using System;
using System.Collections.Generic;
using RingworldTechDemo.ContainmentField;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>
/// The megastorm side of the containment-field seam (docs/megastorm-design.md 6.3, WO8). The field installs <see cref="FieldUndersideProvider"/> and the
/// real <see cref="IContainmentDisturbanceSink"/> later; until then a stub sink and provider drive the tests. Nothing here defines a ceiling formula of
/// its own beyond the <see cref="CeilingFromUnderside"/> stub, which the field replaces with ContainmentFieldGeometry.CloudCeilingFromUnderside.
/// </summary>
public static class MegastormFieldCoupling
{
    /// <summary>
    /// Set by the field at startup: the true underside H(across) in metres (NaN outside the roots). Null = no field: the ceiling is 18 km and nothing is
    /// published.
    /// </summary>
    public static Func<double /*across m*/, double /*field underside m*/>? FieldUndersideProvider { get; set; }

    /// <summary>
    /// Underside to cloud ceiling, min(18 km, H - 300 m). WO8 stub of the field's single rule; the field assigns its own function here and then the
    /// stub and the bake clamp cannot disagree.
    /// </summary>
    public static Func<double, double> CeilingFromUnderside { get; set; } = h => Math.Min((double)RingCloudLayers.CeilingMeters, h - 300.0);

    /// <summary>The cloud ceiling at <paramref name="across"/> (18 km with no provider).</summary>
    public static double CeilingAt(double across)
    {
        Func<double, double>? provider = FieldUndersideProvider;
        if (provider == null) return RingCloudLayers.CeilingMeters;
        double h = provider(across);
        return double.IsNaN(h) ? RingCloudLayers.CeilingMeters : CeilingFromUnderside(h);
    }

    /// <summary>
    /// Distance from a wall where the ceiling drops below 18 km (6.2 km Certified, 5.8 km runtime-surface), by bisection on the provider; 0 when the
    /// field does not press the 18 km cap anywhere near the wall, W/2 when it does everywhere.
    /// </summary>
    public static double BandEdgeMeters(double widthMeters)
    {
        if (FieldUndersideProvider == null) return 0.0;
        const double Cap = RingCloudLayers.CeilingMeters;
        double lo = 0.0, hi = 0.5 * widthMeters;
        if (CeilingAt(lo) >= Cap) return 0.0;
        if (CeilingAt(hi) < Cap) return hi;
        for (int i = 0; i < 40; i++)
        {
            double mid = 0.5 * (lo + hi);
            if (CeilingAt(mid) < Cap) lo = mid; else hi = mid;
        }
        return hi;
    }

    /// <summary>One bit-stable 64-bit mix of three integers and a salt (SourceIds).</summary>
    public static long Mix64(long a, long b, long c, int salt)
    {
        unchecked
        {
            ulong x = (ulong)a * 0x9E3779B97F4A7C15UL ^ (ulong)b * 0xC2B2AE3D27D4EB4FUL ^ (ulong)c * 0x165667B19E3779F9UL ^ (ulong)(long)salt * 0xD6E8FEB86659FD93UL;
            x ^= x >> 30; x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 27; x *= 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (long)x;
        }
    }

    // ---- Upper-atmosphere discharges (sprites and jets) -----------------------------------------------------------------------
    /// <summary>
    /// The natural-layer event of a far flash-field flash: its id IS the flash id, so the sprite layer and the field's sprite adapter see one id per
    /// discharge. Null when the megastorm is not active at the flash start.
    /// </summary>
    public static RingLightningEvent? FlashEvent(MegastormModel model, in MegastormLightning.MegastormFlash flash)
    {
        if (!model.Evaluate(flash.StartTimeSeconds).Active) return null;
        double around = MegastormModel.WrapPositive(model.AroundAt(flash.StartTimeSeconds)
            - model.State.Direction * (flash.XiMeters + model.LeadOffset(flash.AcrossMeters, flash.StartTimeSeconds)), model.Settings.CircumferenceMeters);
        return new RingLightningEvent(flash.Id, MegastormModel.WallRecordId, flash.StartTimeSeconds,
            new RingCanonicalPosition(around, 0.0, flash.AcrossMeters), new RingCanonicalPosition(around, flash.AltitudeMeters, flash.AcrossMeters),
            (int)(MegastormModel.Hash(model.State.Seed, 8000, flash.Id) & 0x7fffffff), 0.0f, flash.Energy, (float)MegastormLightning.FlashRadiusMeters,
            false, MegastormModel.KindMegastorm);
    }

    /// <summary>
    /// The discharge a megastorm flash makes, created through <see cref="UpperAtmosphereLightning.TryDerive"/> exactly as the natural sprite layer does
    /// (same roll, same kinds), with <c>sprite.EventId == flash.Id</c>. The cloud top is the megastorm's current top (unclamped, decision 4).
    /// </summary>
    public static bool TryDeriveSprite(MegastormModel model, in MegastormLightning.MegastormFlash flash, out UpperAtmosphereSprite sprite, float? chanceScale = null)
    {
        sprite = default;
        if (FlashEvent(model, flash) is not RingLightningEvent e) return false;
        return UpperAtmosphereLightning.TryDerive(e, model.Evaluate(flash.StartTimeSeconds).TopMeters, out sprite, chanceScale);
    }

    /// <summary>Sprites of the flashes that START in [t0, t1) (list cleared first), in the order of <see cref="MegastormLightning.FlashesInWindow"/>.</summary>
    public static int SpritesInWindow(MegastormModel model, double t0, double t1, List<UpperAtmosphereSprite> sprites, float? chanceScale = null)
    {
        sprites.Clear();
        var flashes = new List<MegastormLightning.MegastormFlash>();
        MegastormLightning.FlashesInWindow(model, t0, t1, flashes);
        foreach (MegastormLightning.MegastormFlash f in flashes)
            if (TryDeriveSprite(model, f, out UpperAtmosphereSprite s, chanceScale)) sprites.Add(s);
        return sprites.Count;
    }

    // ---- Updraft bursts ---------------------------------------------------------------------------------------------------------
    public const double BurstCellSeconds = 60.0, BurstMinLifeSeconds = 120.0, BurstMaxLifeSeconds = 600.0, BurstPulseSeconds = 1.5;
    public const int BurstSegments = 7;
    /// <summary>Overshooting tops: about 1 per 5 min per 50 km of wall.</summary>
    public const double BurstPerSecondPer50Km = 1.0 / 300.0;

    /// <summary>
    /// One overshooting top. It lives in the REFERENCE-LINE frame: <see cref="XMeters"/> behind the line is fixed, so it drifts with s(t) and a pulse
    /// train advances by v x 1.5 s per pulse.
    /// </summary>
    public readonly record struct UpdraftBurst(long Id, double StartSeconds, double LifeSeconds, double AcrossMeters, double XMeters, float Strength);

    /// <summary>Bursts alive at <paramref name="t"/> (list cleared first), ascending by id.</summary>
    public static int BurstsAt(MegastormModel model, double t, List<UpdraftBurst> bursts)
    {
        bursts.Clear();
        if (!model.Triggered) return 0;
        int seed = model.State.Seed;
        double w = model.Settings.WidthMeters, segWidth = w / BurstSegments;
        double spawn = BurstPerSecondPer50Km * BurstCellSeconds * segWidth / 50000.0;
        long first = (long)Math.Floor((t - BurstMaxLifeSeconds) / BurstCellSeconds), last = (long)Math.Floor(t / BurstCellSeconds);
        for (long k = first; k <= last; k++)
            for (int j = 0; j < BurstSegments; j++)
            {
                long id = k * 8 + j;
                if (MegastormModel.Hash01(seed, 9000, id) >= spawn) continue;
                double start = k * BurstCellSeconds + BurstCellSeconds * MegastormModel.Hash01(seed, 9001, id);
                double life = BurstMinLifeSeconds + (BurstMaxLifeSeconds - BurstMinLifeSeconds) * MegastormModel.Hash01(seed, 9002, id);
                if (t < start || t > start + life || !model.Evaluate(start).Active) continue;
                double across = (j + MegastormModel.Hash01(seed, 9003, id)) * segWidth;
                double xi = 3000.0 + 15000.0 * MegastormModel.Hash01(seed, 9004, id);
                bursts.Add(new UpdraftBurst(id, start, life, across, xi + model.LeadOffset(across, start),
                    (float)(0.6 + 0.4 * MegastormModel.Hash01(seed, 9005, id))));
            }
        return bursts.Count;
    }

    /// <summary>Around position of pulse <paramref name="pulse"/> of a burst: the reference line at the pulse time minus the fixed offset.</summary>
    public static double PulseAround(MegastormModel model, in UpdraftBurst b, int pulse)
        => MegastormModel.WrapPositive(model.AroundAt(b.StartSeconds + BurstPulseSeconds * pulse) - model.State.Direction * b.XMeters, model.Settings.CircumferenceMeters);
}

/// <summary>
/// Publishes the megastorm's contact-band ripples and drifting updraft pulses into an <see cref="IContainmentDisturbanceSink"/> (main thread, weather
/// clock). The budget is a fixed clock: one slot per 0.25 s, so AT MOST 4 ripples per second, both sides and the bursts combined. A slot goes to the
/// first updraft pulse due in it (ascending burst id), else to a contact ripple on the side (slot parity), one per side per 0.5 s. Everything depends
/// only on (seed, slot, model), so it is identical on a paused clock and after a scrub. Strike ripples and sprites are NOT published here: strikes go
/// through the field's LightningDisturbanceAdapter, sprites share the natural layer's ids (<see cref="MegastormFieldCoupling.TryDeriveSprite"/>).
/// </summary>
public sealed class MegastormFieldPublisher
{
    public const double SlotSeconds = 0.25, RangeMeters = 400000.0, MaxCatchUpSeconds = 2.0, ContactDurationSeconds = 5.0, BurstDurationSeconds = 5.0;
    public const float MinBurstEnergy = 0.1f, ContactFlashCoupling = 0.6f, ContactOccluderRadiusMeters = 12000.0f;
    private const double EarlyOutMeters = RangeMeters + 60000.0;
    private const int ContactAttempts = 3;

    private readonly MegastormModel _model;
    private readonly IContainmentDisturbanceSink _sink;
    private readonly List<MegastormFieldCoupling.UpdraftBurst> _bursts = new();
    private long _lastSlot = long.MinValue;

    public int PublishedContact { get; private set; }
    public int PublishedBurst { get; private set; }
    public int Rejected { get; private set; }
    /// <summary>Burst pulses that lost their slot to an earlier burst's pulse.</summary>
    public int DroppedBurstPulses { get; private set; }

    public MegastormFieldPublisher(MegastormModel model, IContainmentDisturbanceSink sink) { _model = model; _sink = sink; }

    /// <summary>
    /// Publishes the slots up to weather time <paramref name="t"/> (a paused clock publishes nothing new; a backwards jump re-arms without publishing;
    /// a jump over 2 s only catches up the last 2 s). Returns the number of ripples accepted by the sink.
    /// </summary>
    public int Update(double t, in RingCanonicalPosition camera)
    {
        long slot = (long)Math.Floor(t / SlotSeconds);
        long maxBack = (long)(MaxCatchUpSeconds / SlotSeconds);
        if (_lastSlot == long.MinValue) _lastSlot = slot - 1;
        else if (slot < _lastSlot) { _lastSlot = slot; return 0; }
        else if (slot - _lastSlot > maxBack) _lastSlot = slot - maxBack;
        int accepted = 0;
        for (long b = _lastSlot + 1; b <= slot; b++)
        {
            if (!TryCollectSlot(b, camera, out ContainmentDisturbance d)) continue;
            if (_sink.Publish(d)) { accepted++; if (d.Kind == ContainmentDisturbanceKind.UpdraftBurst) PublishedBurst++; else PublishedContact++; }
            else Rejected++;
        }
        _lastSlot = slot;
        return accepted;
    }

    /// <summary>The ripple of slot <paramref name="b"/> (start time b x 0.25 s), or false when the slot publishes nothing.</summary>
    public bool TryCollectSlot(long b, in RingCanonicalPosition camera, out ContainmentDisturbance d)
    {
        d = default;
        Func<double, double>? provider = MegastormFieldCoupling.FieldUndersideProvider;
        if (provider == null) return false;
        double slotStart = b * SlotSeconds;
        MegastormFrame frame = _model.Evaluate(slotStart);
        if (!frame.Active) return false;
        double c = _model.Settings.CircumferenceMeters;
        if (Math.Abs(MegastormModel.WrapSigned(frame.AroundMeters - camera.AroundMeters, c)) > EarlyOutMeters) return false;
        return TryBurstPulse(b, slotStart, camera, provider, out d) || TryContact(b, slotStart, frame, camera, provider, out d);
    }

    private bool InRange(double around, double across, in RingCanonicalPosition camera)
    {
        double da = MegastormModel.WrapSigned(around - camera.AroundMeters, _model.Settings.CircumferenceMeters), dz = across - camera.AcrossMeters;
        return da * da + dz * dz <= RangeMeters * RangeMeters;
    }

    private bool TryBurstPulse(long b, double slotStart, in RingCanonicalPosition camera, Func<double, double> provider, out ContainmentDisturbance d)
    {
        d = default;
        double slotEnd = slotStart + SlotSeconds;
        MegastormFieldCoupling.BurstsAt(_model, slotEnd, _bursts);
        bool found = false;
        foreach (MegastormFieldCoupling.UpdraftBurst burst in _bursts)
        {
            int pulse = Math.Max(0, (int)Math.Ceiling((slotStart - burst.StartSeconds) / MegastormFieldCoupling.BurstPulseSeconds));
            double tp = burst.StartSeconds + MegastormFieldCoupling.BurstPulseSeconds * pulse;
            if (tp < slotStart || tp >= slotEnd || tp > burst.StartSeconds + burst.LifeSeconds) continue;
            MegastormFrame frame = _model.Evaluate(tp);
            if (!frame.Active) continue;
            double around = MegastormFieldCoupling.PulseAround(_model, burst, pulse);
            if (!InRange(around, burst.AcrossMeters, camera)) continue;
            double h = provider(burst.AcrossMeters);
            if (double.IsNaN(h)) continue;
            float energy = (float)(burst.Strength * frame.Strength * Math.Exp(-Math.Max(h - RingCloudLayers.CeilingMeters, 0.0) / 2000.0));
            if (energy < MinBurstEnergy) continue;
            if (found) { DroppedBurstPulses++; continue; }
            found = true;
            int seed = _model.State.Seed;
            var position = new RingCanonicalPosition(around, RingCloudLayers.CeilingMeters, burst.AcrossMeters);
            d = new ContainmentDisturbance(MegastormFieldCoupling.Mix64(burst.Id, pulse, 0, 7), ContainmentDisturbanceKind.UpdraftBurst, position, tp, energy,
                (float)(10000.0 + 10000.0 * MegastormModel.Hash01(seed, 9006, burst.Id)), (float)BurstDurationSeconds, ContactFlashCoupling,
                FlashFollower(around, burst.AcrossMeters, RingCloudLayers.CeilingMeters));
        }
        return found;
    }

    private bool TryContact(long b, double slotStart, in MegastormFrame frame, in RingCanonicalPosition camera, Func<double, double> provider,
        out ContainmentDisturbance d)
    {
        d = default;
        double w = _model.Settings.WidthMeters, c = _model.Settings.CircumferenceMeters;
        double edge = MegastormFieldCoupling.BandEdgeMeters(w);
        if (edge <= 0.0) return false;
        int side = (int)(((b % 2) + 2) % 2), seed = _model.State.Seed;
        for (int attempt = 0; attempt < ContactAttempts; attempt++)
        {
            int salt = 100 * (attempt + 1);
            double rim = edge * MegastormModel.Hash01(seed, salt + side, b);
            double across = side == 0 ? rim : w - rim;
            double xi = MegastormModel.WallEndXi * MegastormModel.Hash01(seed, salt + 2 + side, b);
            long xiCell = (long)Math.Floor(xi / 500.0);
            double x = xi + _model.LeadOffset(across, slotStart);
            double ceiling = MegastormFieldCoupling.CeilingAt(across);
            float top = MegastormProfile.Column(_model, x, across, slotStart).BodyTop;
            if (top <= 0.0f) continue;
            float penetration = (float)Math.Clamp((top - (ceiling - 500.0)) / 1000.0, 0.0, 1.0);
            if (penetration <= 0.0f) continue;
            double around = MegastormModel.WrapPositive(frame.AroundMeters - frame.Direction * x, c);
            if (!InRange(around, across, camera)) continue;
            var position = new RingCanonicalPosition(around, ceiling, across);
            d = new ContainmentDisturbance(MegastormFieldCoupling.Mix64(side, xiCell, b, 4), ContainmentDisturbanceKind.MegastormContact, position, slotStart,
                Math.Clamp(frame.Strength * penetration, 0.0f, 1.0f), (float)(10000.0 + 20000.0 * MegastormModel.Hash01(seed, salt + 4, b)),
                (float)ContactDurationSeconds, ContactFlashCoupling, FlashFollower(around, across, ceiling), position, ContactOccluderRadiusMeters, (float)ceiling);
            return true;
        }
        return false;
    }

    /// <summary>Flash 0..1 at a fixed world point: the far flash field's glow at the point's moving-frame position at time tt.</summary>
    private Func<double, float> FlashFollower(double around, double across, double altitude)
    {
        MegastormModel model = _model;
        return tt => (float)Math.Clamp(MegastormLightning.GlowAt(model, model.X(around, tt), across, altitude, tt, LightningVisualPolicy.ReducedFlashes), 0.0, 1.0);
    }
}
