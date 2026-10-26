using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Pure phase table for PrecipitationAltitude (docs/storm-revamp-s1-design.md section 4): Below / InCloud / Above, factors, cloud bounds, flight-hook default.
/// Run: Godot --headless --path . --script res://tools/precipitation_altitude_test.gd. Prints PRECIP_ALTITUDE=PASS.
/// </summary>
public partial class PrecipitationAltitudeTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            // base 500, top 3000 (front)
            (double alt, PrecipitationPhase want)[] table =
            {
                (0, PrecipitationPhase.Below), (100, PrecipitationPhase.Below), (459.9, PrecipitationPhase.Below),
                (460.0, PrecipitationPhase.InCloud), (500, PrecipitationPhase.InCloud), (2000, PrecipitationPhase.InCloud), (3000, PrecipitationPhase.InCloud),
                (3000.1, PrecipitationPhase.Above), (4200, PrecipitationPhase.Above), (20000, PrecipitationPhase.Above),
            };
            foreach (var (alt, want) in table)
                Check(PrecipitationAltitude.Resolve(alt, 500, 3000) == want, $"phase at {alt} m want {want} got {PrecipitationAltitude.Resolve(alt, 500, 3000)}");
            // Curtain rule (surface vs base, no top): base <= surface + 40 is not Below.
            Check(PrecipitationAltitude.Resolve(460, 500, double.PositiveInfinity) != PrecipitationPhase.Below, "curtain: base == surface+40 not Below");
            Check(PrecipitationAltitude.Resolve(459, 500, double.PositiveInfinity) == PrecipitationPhase.Below, "curtain: base > surface+40 Below");
            Check(PrecipitationAltitude.Resolve(double.NaN, 500, 3000) == PrecipitationPhase.Below, "NaN altitude falls back to Below");
            Check(PrecipitationAltitude.IntensityFactor(PrecipitationPhase.Below) == 1 && PrecipitationAltitude.IntensityFactor(PrecipitationPhase.InCloud) == 1
                && PrecipitationAltitude.IntensityFactor(PrecipitationPhase.Above) == 0, "intensity factors");
            Check(PrecipitationAltitude.StreakFactor(PrecipitationPhase.InCloud) == 0.5f && PrecipitationAltitude.StreakFactor(PrecipitationPhase.Above) == 0, "streak factors");
            Check(PrecipitationAltitude.HazeFactor(PrecipitationPhase.InCloud) == 2.0f && PrecipitationAltitude.HazeFactor(PrecipitationPhase.Above) == 0, "haze factors");
            Check(PrecipitationAltitude.InCloudDepth(PrecipitationPhase.InCloud, 800, 500) == 300, "in-cloud depth");
            Check(PrecipitationAltitude.InCloudDepth(PrecipitationPhase.InCloud, 480, 500) == 0, "in-cloud depth clamps inside the margin");
            Check(PrecipitationAltitude.InCloudDepth(PrecipitationPhase.Below, 800, 500) == 0, "no depth below");
            // Bounds: natural, front, manual, datum-relative manual.
            var natural = PrecipitationAltitude.ResolveCloudBounds(3, false, 0, 0, 100, 2000, 6000, 500);
            Check(natural == (2100, 6100), $"natural bounds {natural}");
            var front = PrecipitationAltitude.ResolveCloudBounds(3, true, 0, 0, 0, 2000, 6000, 500);
            Check(front == (500, 6000), $"front bounds {front}");
            var lowFront = PrecipitationAltitude.ResolveCloudBounds(3, true, 0, 0, 0, 2000, 6000, 20);
            Check(lowFront.Base == 100, "front base floored at 100 m");
            var manual = PrecipitationAltitude.ResolveCloudBounds(-902, false, 400, 5000, 100, 2000, 6000, 500);
            Check(manual == (400, 5000), $"manual bounds {manual}");
            var datumManual = PrecipitationAltitude.ResolveCloudBounds(-1003, false, 400, 5000, 100, 2000, 6000, 500);
            Check(datumManual == (500, 5100), $"datum manual bounds {datumManual}");
            var thin = PrecipitationAltitude.ResolveCloudBounds(-1, false, 400, 0, 0, 2000, 6000, 500);
            Check(thin.Top >= thin.Base + 500, "degenerate manual top widened");
            // Flight hook default and provider plumbing.
            var prior = PrecipitationAltitude.ColumnProvider;
            try
            {
                PrecipitationAltitude.ColumnProvider = null;
                var clear = PrecipitationAltitude.QueryPrecipitationColumn(new RingCanonicalPosition(0, 10, 0));
                Check(clear.Intensity == 0 && clear.Phase == PrecipitationPhase.Below && !clear.Snow, "default column is clear and Below");
                PrecipitationAltitude.ColumnProvider = p => new PrecipitationColumn(0.7f, PrecipitationPhase.InCloud, true, (float)p.VerticalMeters, 0.5f, 2.0f);
                var q = PrecipitationAltitude.QueryPrecipitationColumn(new RingCanonicalPosition(0, 123, 0));
                Check(q.Intensity == 0.7f && q.Snow && q.InCloudDepthMeters == 123, "provider result returned");
            }
            finally { PrecipitationAltitude.ColumnProvider = prior; }
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("PRECIP_ALTITUDE=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("PRECIP_ALTITUDE=PASS");
        return 0;
    }
}
