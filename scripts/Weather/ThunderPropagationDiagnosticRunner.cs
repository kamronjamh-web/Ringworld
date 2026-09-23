using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Weather;

public partial class ThunderPropagationDiagnosticRunner : RefCounted
{
    public string RunAll()
    {
        try
        {
            RunContracts();
            return "Thunder propagation diagnostics: PASS";
        }
        catch (Exception exception)
        {
            return "Thunder propagation diagnostics: FAIL: " + exception.Message;
        }
    }

    private static void RunContracts()
    {
        var context = new ThunderAcousticContext(1_000_000, 20_000, 10_000);
        var mega = new ThunderAcousticContext(1_000_000_000, 20_000, 10_000);
        CheckDistance(new(10_000, 2, 5_000), new(10_000, 2, 5_001), mega, 1, "mega 1m");
        CheckDistance(new(10_000, 2, 5_000), new(10_000, 2, 5_343), mega, 343, "mega 343m");

        RingCanonicalPosition source = new(10_000, 0, 5_000);
        RingCanonicalPosition oneSecond = new(10_000, 2, 5_343);
        RingCanonicalPosition tenSeconds = new(10_000, 2, 8_430);
        RingLightningEvent strike = Strike(1, source);
        CheckTimingAndMotion(context, source, oneSecond, tenSeconds, strike);
        CheckRetentionExpiryAndRewind(context, source, oneSecond, strike);
        CheckCaps(context);
        CheckGeometry(context, source);
        CheckInvalid(context, source);
    }

    private static void CheckTimingAndMotion(ThunderAcousticContext context, RingCanonicalPosition source,
        RingCanonicalPosition oneSecond, RingCanonicalPosition tenSeconds, RingLightningEvent strike)
    {
        var core = new ThunderPropagation();
        var arrivals = new List<ThunderArrival>();
        Require(core.Observe(strike, 0, oneSecond, context), "one-second enqueue");
        core.Advance(.999, oneSecond, context, true, arrivals);
        Require(arrivals.Count == 0, "before one second");
        core.Advance(1, oneSecond, context, true, arrivals);
        Require(arrivals.Count == 1 && Near(arrivals[0].TravelSeconds, 1), "at one second");

        core.Reset(); arrivals.Clear();
        Require(core.Observe(strike, 0, tenSeconds, context), "ten-second enqueue");
        core.Advance(9.999, tenSeconds, context, true, arrivals);
        Require(arrivals.Count == 0, "before ten seconds");
        core.Advance(10, tenSeconds, context, true, arrivals);
        Require(arrivals.Count == 1 && Near(arrivals[0].TravelSeconds, 10), "at ten seconds");

        core.Reset(); arrivals.Clear();
        Require(core.Observe(strike, 0, tenSeconds, context), "moving-toward enqueue");
        core.Advance(5, new RingCanonicalPosition(10_000, 2, 6_715), context, true, arrivals);
        Require(arrivals.Count == 1, "moving toward");

        core.Reset(); arrivals.Clear();
        Require(core.Observe(strike, 0, source, context), "moving-away enqueue");
        core.Advance(1, new RingCanonicalPosition(10_000, 2, 12_000), context, true, arrivals);
        Require(arrivals.Count == 0, "moving away");
        core.Advance(30, new RingCanonicalPosition(10_000, 2, 12_000), context, true, arrivals);
        Require(arrivals.Count == 1 && arrivals[0].LateSeconds > 0, "late moving-away arrival");
    }

    private static void CheckRetentionExpiryAndRewind(ThunderAcousticContext context, RingCanonicalPosition source,
        RingCanonicalPosition oneSecond, RingLightningEvent strike)
    {
        var core = new ThunderPropagation();
        var arrivals = new List<ThunderArrival>();
        Require(core.Observe(strike, 0, oneSecond, context), "flash retention enqueue");
        core.Advance(1.1, oneSecond, context, true, arrivals);
        Require(arrivals.Count == 1 && Near(arrivals[0].SourcePosition.VerticalMeters, 2), "source retained after flash");
        Require(!core.Observe(strike, 2, oneSecond, context), "duplicate id");

        core.Reset(); arrivals.Clear();
        Require(core.Observe(Strike(2, source), 0, oneSecond, context), "muted enqueue");
        core.Advance(2, oneSecond, context, false, arrivals);
        Require(arrivals.Count == 0, "listener cannot hear");

        core.Reset(); arrivals.Clear();
        Require(core.Observe(Strike(3, source), 0, oneSecond, context), "expiry enqueue");
        core.Advance(100, oneSecond, context, true, arrivals);
        Require(arrivals.Count == 0 && core.PendingCount == 0, "expired event emits nothing");

        core.Reset();
        Require(core.Observe(Strike(4, source), 0, oneSecond, context), "rewind enqueue");
        core.Advance(.5, oneSecond, context, true, arrivals);
        core.Advance(-1, oneSecond, context, true, arrivals);
        Require(core.PendingCount == 0, "audio rewind clears pending");
    }

    private static void CheckCaps(ThunderAcousticContext context)
    {
        var core = new ThunderPropagation();
        var listener = new RingCanonicalPosition(0, 2, 0);
        var arrivals = new List<ThunderArrival>();
        for (int index = 0; index < ThunderPropagation.MaxPendingEvents; index++)
            Require(core.Observe(Strike(10 + index, new RingCanonicalPosition(0, 0, 100 + index)), 0, listener, context), "fill pending");
        Require(core.PendingCount == ThunderPropagation.MaxPendingEvents, "pending cap before replacement");
        Require(core.Observe(Strike(999, new RingCanonicalPosition(0, 0, 1)), 0, listener, context), "nearest replacement");
        Require(core.PendingCount == ThunderPropagation.MaxPendingEvents, "pending cap after replacement");
        Require(!core.Observe(Strike(137, new RingCanonicalPosition(0, 0, 227)), 0, listener, context), "evicted id stays deduped");
        for (int batch = 0; batch < 5; batch++)
        {
            core.Advance(batch + 1, listener, context, true, arrivals);
            for (int index = 0; index < ThunderPropagation.MaxPendingEvents; index++)
                core.Observe(Strike(10_000 + batch * 128 + index, new RingCanonicalPosition(0, 0, 1)), batch + 1, listener, context);
        }
        Require(core.HistoryCount == ThunderPropagation.MaxHistoryEntries, "history cap filled");
    }

    private static void CheckGeometry(ThunderAcousticContext context, RingCanonicalPosition source)
    {
        Require(ThunderPropagation.TryDistance(new(999_999, 2, 5_000), new(1, 2, 5_000), context, out double seam) && seam < 3, "canonical seam");
        var vacuum = new ThunderAcousticContext(10_000, 20_000, 1_000, 100_000);
        Require(!ThunderPropagation.TryDistance(new(0, 2, 5_000), new(5_000, 2, 5_000), vacuum, out _), "vacuum chord");
        Require(!ThunderPropagation.TryDistance(new(0, 2, -1), source, context, out _), "outside width");
        Require(!ThunderPropagation.TryDistance(new(0, 10_001, 1), source, context, out _), "outside atmosphere");
    }

    private static void CheckInvalid(ThunderAcousticContext context, RingCanonicalPosition source)
    {
        Require(!new ThunderPropagation().Observe(Strike(0, source), 0, source, context), "invalid id");
        Require(!new ThunderPropagation().Observe(Strike(2, new RingCanonicalPosition(double.NaN, 0, 0)), 0, source, context), "nan source");
        Require(!new ThunderPropagation().Observe(Strike(3, source) with { Energy = -1 }, 0, source, context), "negative energy");
        Require(!ThunderPropagation.TryDistance(source, source, context with { AtmosphereHeightMeters = 1_000_000 }, out _), "invalid atmosphere");
    }

    private static RingLightningEvent Strike(long id, RingCanonicalPosition position) => new(id, 0, 0, position, position with { VerticalMeters = 1_000 }, 0, 0, 1, 1, false);
    private static void CheckDistance(RingCanonicalPosition a, RingCanonicalPosition b, ThunderAcousticContext context, double expected, string name) => Require(ThunderPropagation.TryDistance(a, b, context, out double actual) && Near(actual, expected), name);
    private static bool Near(double first, double second) => Math.Abs(first - second) < .02;
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
}
