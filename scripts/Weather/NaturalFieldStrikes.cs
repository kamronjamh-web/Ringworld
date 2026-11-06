using System;
using RingworldTechDemo.Lightning;

namespace RingworldTechDemo;

/// <summary>A natural (or registered) storm as the field-strike stream sees it, evaluated at the start of a 20 s bucket.</summary>
public readonly record struct NaturalFieldStorm(int SystemId, int Kind, double CenterAroundMeters, double CenterAcrossMeters, float MajorRadiusMeters, float MinorRadiusMeters,
    float OrientationCosine, float OrientationSine, float Precipitation, float TopMeters /* StormCeilingMeters, uncapped by the field */, bool Sculpts, bool Mature);

/// <summary>The field the stream reads: the live underside provider, the two roots (d_w is measured from them) and the habitat extent.</summary>
public readonly record struct NaturalFieldGeometry(Func<double, double> Underside, double Root0, double RootW, double WidthMeters, double CircumferenceMeters);

/// <summary>One natural near-wall field strike (docs/containment-field-design.md 3.10.6, WO14).</summary>
public readonly record struct NaturalFieldStrike(long Id, int SystemId, int Kind, long Bucket, int Attempt, double StartTimeSeconds, double AroundMeters, double AcrossMeters,
    double BaseAltitudeMeters, double ContactAltitudeMeters, double GapMeters, double WallDistanceMeters, float Energy, float DurationSeconds, float FlashRadiusMeters, int ShapeSeed);

/// <summary>
/// The natural field-strike stream (WO14). Pure in (seed, storm at the bucket start, bucket, field, Lightning frequency x Reduced flashes): no nodes and no state, so
/// a rewind, a pause or a reload reproduces ids, times and positions. It is a SEPARATE stream from the hashed natural lightning schedule (that schedule, and the
/// natural weather fingerprint, are untouched); the layer only adds its strikes to <c>ActiveLightningEvents</c> as <c>FieldStrike</c>-flagged events of the natural kind.
/// Time is cut into 20 s buckets; per (system, bucket) there are <see cref="BaseAttempts"/> candidate "parent strikes" (about 0.2 per second, the natural rate of a
/// mature front), the first one that passes the doc's roll becomes the bucket's single field strike (the per-system 20 s cooldown). The storm's centre must be within 12 km of
/// the nearer wall; the strike's across is hashed inside that 12 km zone and its around inside the storm's tower core (0.6 of the radii). The roll is
/// p = 0.04 x strength x exp(-(g - 0.3 km) / 0.75 km) x (1 - smoothstep(6 km, 12 km, d_w)) with g = H(across) - top and strength = clamp(precipitation) x (kind >= 4 ? 1 : 0.6).
/// </summary>
public static class NaturalFieldStrikes
{
    public const double BucketSeconds = 20.0;
    public const int BaseAttempts = 4, MaxAttempts = 8;
    /// <summary>Id tag (flash field: plain cell ids, blue 1 &lt;&lt; 50, scythes 1 &lt;&lt; 51, megastorm field strikes 1 &lt;&lt; 52, contact arcs 1 &lt;&lt; 53).</summary>
    public const long IdTag = 1L << 54;
    public const double MaxProbability = 0.04, GapScaleMeters = 750.0, MinGapMeters = 300.0, MaxGapMeters = 3000.0, MinTopMeters = 15000.0;
    public const double WallFullMeters = 6000.0, WallCutoffMeters = 12000.0, FootprintScale = 0.6;
    public const double ContactBelowFieldMeters = 50.0, BaseBelowTopMeters = 100.0, TopBelowFieldMeters = 300.0;
    public const float StrongPrecipitation = 0.6f, WeakStrength = 0.6f;
    public const int ExceptionalKind = 4, MegastormKind = 6;

    public static bool IsNaturalFieldStrikeId(long id) => (id & IdTag) != 0 && (id & ((1L << 53) | (1L << 52))) == 0;
    public static long BucketOf(double now) => (long)Math.Floor(Math.Max(0.0, now) / BucketSeconds);

    private static ulong Mix(ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }
    }
    private static ulong Hash(int seed, long key, long bucket, int attempt, int salt)
        => Mix(Mix(Mix(Mix((ulong)seed ^ 0x5F1E0DA7UL) ^ (ulong)key) ^ (ulong)bucket) ^ ((ulong)(uint)attempt << 32 | (uint)salt));
    private static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));
    private static double Unit(int seed, long key, long bucket, int attempt, int salt) => Unit(Hash(seed, key, bucket, attempt, salt));

    private static double Smoothstep(double a, double b, double x)
    {
        double t = Math.Clamp((x - a) / (b - a), 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Candidate parent strikes per bucket at a strike-rate scale (Lightning frequency x the reduced-flashes share): 4 up to scale 1, more above it (up to 8).</summary>
    public static int AttemptCount(float chanceScale) => chanceScale <= 1.0f ? BaseAttempts : Math.Min(MaxAttempts, (int)Math.Ceiling(BaseAttempts * chanceScale));

    /// <summary>Start time of attempt <paramref name="attempt"/>: hashed inside its equal share of the bucket, so attempts are ordered in time.</summary>
    private static double AttemptStart(int seed, long key, long bucket, int attempt, int count)
        => bucket * BucketSeconds + (attempt + Unit(seed, key, bucket, attempt, 2)) * (BucketSeconds / count);

    /// <summary>
    /// The roll value of an attempt that survives the strike-rate thinning and the cheapest bound (every roll threshold is at most <see cref="MaxProbability"/>), or 2.
    /// This is the one-hash check the layer makes before it builds a storm.
    /// </summary>
    private static double RollOf(int seed, long key, long bucket, int attempt, float chanceScale)
    {
        if (Unit(seed, key, bucket, attempt, 1) >= Math.Min(1.0f, chanceScale)) return 2.0;
        double roll = Unit(seed, key, bucket, attempt, 0x5F1E);
        return roll < MaxProbability ? roll : 2.0;
    }

    /// <summary>False when no attempt of this (system key, bucket) can possibly strike (about 85 percent of them), so no storm has to be built.</summary>
    public static bool MayRoll(int seed, long key, long bucket, float chanceScale)
    {
        if (chanceScale <= 0.0f) return false;
        int n = AttemptCount(chanceScale);
        for (int j = 0; j < n; j++) if (RollOf(seed, key, bucket, j, chanceScale) < 2.0) return true;
        return false;
    }

    /// <summary>The roll threshold p of a strike at <paramref name="gapMeters"/> and <paramref name="wallMeters"/> from the wall for a given strength (3.10.6 item 5).</summary>
    public static double Probability(double strength, double gapMeters, double wallMeters)
        => MaxProbability * strength * Math.Exp(-(gapMeters - MinGapMeters) / GapScaleMeters) * (1.0 - Smoothstep(WallFullMeters, WallCutoffMeters, wallMeters));

    public static float Strength(in NaturalFieldStorm storm) => Math.Clamp(storm.Precipitation, 0.0f, 1.0f) * (storm.Kind >= ExceptionalKind ? 1.0f : WeakStrength);

    /// <summary>Gates 2 and 3 on the storm alone: a front (kind 3 or 4, never the megastorm), a tower (Cb or StormTower), strong, and tall enough.</summary>
    public static bool IsEligibleStorm(in NaturalFieldStorm s)
        => s.SystemId != 0 && (s.Kind == 3 || s.Kind == ExceptionalKind) && s.Sculpts && s.TopMeters >= MinTopMeters
           && (s.Kind >= ExceptionalKind || (s.Precipitation >= StrongPrecipitation && s.Mature))
           && s.MajorRadiusMeters > 1.0f && s.MinorRadiusMeters > 1.0f;

    /// <summary>
    /// The field strike of (storm, bucket): the first attempt whose position passes gates 1-4 and whose roll is under p, or false. <paramref name="key"/> is the stable key of
    /// the storm's source (macro cell, or the id of a registered storm) so the roll does not depend on anything but (seed, source, bucket, attempt).
    /// </summary>
    public static bool TryStrike(int seed, long key, in NaturalFieldGeometry field, in NaturalFieldStorm storm, long bucket, float chanceScale, out NaturalFieldStrike strike)
    {
        strike = default;
        if (chanceScale <= 0.0f || bucket < 0 || !IsEligibleStorm(storm) || field.Underside == null) return false;
        double toWall0 = storm.CenterAcrossMeters - field.Root0, toWallW = field.RootW - storm.CenterAcrossMeters;
        bool nearSide0 = toWall0 <= toWallW;
        // "Close to a wall": the storm itself (its centre) is within 12 km of the nearer wall; a storm further out never strikes, whatever its size.
        if (Math.Min(toWall0, toWallW) > WallCutoffMeters) return false;
        float strength = Strength(storm);
        int n = AttemptCount(chanceScale);
        for (int j = 0; j < n; j++)
        {
            double roll = RollOf(seed, key, bucket, j, chanceScale);
            if (roll >= 2.0) continue;
            double wall = WallCutoffMeters * Unit(seed, key, bucket, j, 3);
            double across = nearSide0 ? field.Root0 + wall : field.RootW - wall;
            if (!(across >= 0.0 && across <= field.WidthMeters)) continue;
            double h = field.Underside(across);
            if (double.IsNaN(h)) continue;
            double top = Math.Min(storm.TopMeters, h - TopBelowFieldMeters), gap = h - top;
            if (gap < MinGapMeters - 1e-6 || gap > MaxGapMeters) continue;
            if (!(roll < Probability(strength, gap, wall))) continue;
            // Position: inside the tower core (0.6 of the radii, like the natural strikes) at that across; up to 4 hashed tries.
            double dz = across - storm.CenterAcrossMeters;
            double a = FootprintScale * storm.MajorRadiusMeters, b = FootprintScale * storm.MinorRadiusMeters, c = storm.OrientationCosine, s = storm.OrientationSine;
            double extent = Math.Sqrt(a * c * (a * c) + b * s * (b * s));
            bool placed = false;
            double da = 0.0;
            for (int k = 0; k < 4 && !placed; k++)
            {
                da = (2.0 * Unit(seed, key, bucket, j, 10 + k) - 1.0) * extent;
                double x = da * c + dz * s, z = -da * s + dz * c;
                placed = (x / a) * (x / a) + (z / b) * (z / b) <= 1.0;
            }
            if (!placed) continue;
            double around = storm.CenterAroundMeters + da;
            if (field.CircumferenceMeters > 1.0) { around %= field.CircumferenceMeters; if (around < 0.0) around += field.CircumferenceMeters; }
            double start = AttemptStart(seed, key, bucket, j, n);
            ulong ih = Hash(seed, key, bucket, j, 4);
            long id = (long)(ih & 0x000FFFFFFFFFFFFFUL) | IdTag;
            strike = new NaturalFieldStrike(id, storm.SystemId, storm.Kind, bucket, j, start, around, across, top - BaseBelowTopMeters, h - ContactBelowFieldMeters, gap, wall,
                (float)Math.Clamp(0.45 + 0.55 * strength, 0.0, 1.0),
                (float)(RingLightningEvent.FieldStrikeMinSeconds + (RingLightningEvent.FieldStrikeMaxSeconds - RingLightningEvent.FieldStrikeMinSeconds) * Unit(seed, key, bucket, j, 5)),
                (float)(2500.0 + 9000.0 * Unit(seed, key, bucket, j, 6)), (int)(Hash(seed, key, bucket, j, 7) & 0x7fffffff));
            return true;   // one per bucket: the per-system 20 s cooldown
        }
        return false;
    }

    /// <summary>The ordinary lightning event of a natural field strike: the natural kind, <c>FieldStrike</c> set, GroundPosition = contact point, CloudPosition = channel base, own duration.</summary>
    public static RingLightningEvent ToEvent(in NaturalFieldStrike s) => new(s.Id, s.SystemId, s.StartTimeSeconds,
        new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(s.AroundMeters, s.ContactAltitudeMeters, s.AcrossMeters),
        new RingworldTechDemo.Voxel.Core.RingCanonicalPosition(s.AroundMeters, s.BaseAltitudeMeters, s.AcrossMeters),
        s.ShapeSeed, (float)(Unit(unchecked((ulong)s.Id * 0x9E3779B97F4A7C15UL)) * Math.Tau), s.Energy, s.FlashRadiusMeters, false, s.Kind, false, true, s.DurationSeconds);
}
