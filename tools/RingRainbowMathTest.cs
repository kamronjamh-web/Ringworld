using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-F1 (docs/storm-revamp-s3-storm-arrival-design.md section 12): rainbow geometry and rain illumination. The bow lies at its nominal 42 deg from the antisolar
/// point; nothing is visible above 42.4 deg sun elevation, with the sun hidden at the observer, below the horizon, or with no rain on the azimuth.
/// Run: tools/ring_rainbow_math_test.gd (headless). Prints RING_RAINBOW_MATH=PASS.
/// </summary>
public partial class RingRainbowMathTest : RefCounted
{
    static Vector3 SunAt(float elevationDegrees, float azimuthDegrees = 30.0f)
    {
        float e = Mathf.DegToRad(elevationDegrees), a = Mathf.DegToRad(azimuthDegrees);
        return new Vector3(Mathf.Cos(e) * Mathf.Cos(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Sin(a));
    }

    public int Run(bool verbose)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            Vector3 up = Vector3.Up;
            // Bow radius from the antisolar point, for every azimuth, at several sun elevations.
            double worstRadius = 0;
            foreach (float elevation in new[] { 3f, 12f, 25f, 38f, 42f })
            {
                RingRainbowMath.Bow bow = RingRainbowMath.Compute(SunAt(elevation), up);
                Check(bow.Active, $"bow must be active at sun elevation {elevation}");
                Check(Math.Abs(bow.SunElevationDegrees - elevation) < 1e-3f, "sun elevation");
                Check(bow.Antisolar.DistanceTo(-SunAt(elevation)) < 1e-5f, "antisolar direction");
                for (int i = 0; i < 40; i++)
                {
                    Vector3 d = RingRainbowMath.Direction(bow, i * Mathf.Tau / 40f, RingRainbowMath.BowRadiusDegrees);
                    float angle = Mathf.RadToDeg(Mathf.Acos(Mathf.Clamp(d.Dot(bow.Antisolar), -1f, 1f)));
                    worstRadius = Math.Max(worstRadius, Math.Abs(angle - 42.0));
                }
            }
            Check(worstRadius <= 0.1, $"bow radius off by {worstRadius:0.0000} deg (limit 0.1)");
            Check(RingRainbowMath.PrimaryRedDegrees == 42.4f && RingRainbowMath.PrimaryVioletDegrees == 40.6f && RingRainbowMath.SecondaryRedDegrees == 50.3f && RingRainbowMath.SecondaryVioletDegrees == 53.5f, "band constants");

            // Top of the bow rises to 42 - sun elevation above the horizon; the visible arc ends where the bow reaches -5 deg.
            RingRainbowMath.Bow low = RingRainbowMath.Compute(SunAt(20f), up);
            Vector3 top = RingRainbowMath.Direction(low, 0f, 42.0f);
            float topElevation = Mathf.RadToDeg(Mathf.Asin(top.Dot(up)));
            Check(Math.Abs(topElevation - 22.0f) < 0.05f, $"bow top elevation {topElevation} (expected 22)");
            Vector3 end = RingRainbowMath.Direction(low, low.HalfArcRadians, 42.0f);
            Check(Math.Abs(Mathf.RadToDeg(Mathf.Asin(end.Dot(up))) + 5.0f) < 0.1f, "visible arc ends at -5 deg elevation");
            Check(Math.Abs(RingRainbowMath.Psi(low, 0) + low.HalfArcRadians) < 1e-6f && Math.Abs(RingRainbowMath.Psi(low, 4) - low.HalfArcRadians) < 1e-6f && Math.Abs(RingRainbowMath.Psi(low, 2)) < 1e-6f, "azimuth samples span the arc");
            RingRainbowMath.Bow lower = RingRainbowMath.Compute(SunAt(5f), up);
            Check(lower.HalfArcRadians > low.HalfArcRadians && lower.HalfArcRadians > Mathf.DegToRad(85f), "a lower sun shows a longer arc");

            // Zero cases: sun above 42.4 deg, below the horizon, straight overhead, degenerate input.
            Check(!RingRainbowMath.Compute(SunAt(42.5f), up).Active && !RingRainbowMath.Compute(SunAt(60f), up).Active && !RingRainbowMath.Compute(SunAt(85f), up).Active, "no bow above 42.4 deg");
            Check(!RingRainbowMath.Compute(SunAt(-2f), up).Active && !RingRainbowMath.Compute(SunAt(0f), up).Active, "no bow with the sun at or below the horizon");
            Check(!RingRainbowMath.Compute(Vector3.Up, up).Active && !RingRainbowMath.Compute(Vector3.Zero, up).Active, "degenerate sun directions give no bow");
            Check(RingRainbowMath.SunReach(0f) == 0f && RingRainbowMath.SunReach(0.05f) == 0f && RingRainbowMath.SunReach(1f) == 1f && RingRainbowMath.SunReach(0.35f) > 0.3f && RingRainbowMath.SunReach(0.35f) < 0.7f, "sun reach gate");

            // Rain illumination along an azimuth.
            float[] none = new float[5];
            float clearSigma = 1.2e-5f;
            Check(RingRainbowMath.AzimuthStrength(none, clearSigma, 0.1f, 1500f) == 0f, "no rain on the azimuth gives no bow");
            float near = RingRainbowMath.AzimuthStrength(new float[] { 1f, 0f, 0f, 0f, 0f }, clearSigma, 0.0f, 1500f);
            float mid = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 1f, 0f, 0f }, clearSigma, 0.0f, 1500f);
            float far = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 0f, 0f, 1f }, clearSigma, 0.0f, 1500f);
            Check(near > mid && mid > far && far > 0f && near <= 1f, $"nearer rain must be brighter near={near} mid={mid} far={far}");
            // Light enters from the clear side: rain behind a thick rain wall is dark, and the nearest rain dominates.
            float wall = RingRainbowMath.AzimuthStrength(new float[] { 1f, 1f, 1f, 1f, 1f }, clearSigma, 0.0f, 1500f);
            float rimOnly = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 1f, 1f, 1f }, clearSigma, 0.0f, 1500f);
            Check(wall > near * 0.95f && wall <= 1f && rimOnly > 0f, "rain wall strength");
            float deepOnly = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 0f, 1f, 1f }, clearSigma, 0.0f, 1500f);
            Check(rimOnly > deepOnly, "rain at the rim must be brighter than rain further away");
            // Haze (the post-rain mist): a thick mist hides distant rain but barely touches rain close by.
            float mistSigma = 1.2e-3f;
            float misty = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 1f, 0f, 0f }, mistSigma, 0.0f, 1500f);
            Check(misty < mid * 0.1f, $"mist must dim the bow: {misty} vs {mid}");
            // Rising line of sight: rain above the cloud base does not count.
            float rising = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 0f, 1f, 1f }, clearSigma, Mathf.DegToRad(30f), 1500f);
            float flat = RingRainbowMath.AzimuthStrength(new float[] { 0f, 0f, 0f, 1f, 1f }, clearSigma, 0.0f, 1500f);
            Check(rising == 0f && flat > 0f, $"rain above the cloud base along a rising line of sight must not count (rising={rising})");
            Check(RingRainbowMath.AzimuthStrength(new float[] { float.NaN, 0f, 0f, 0f, 0f }, clearSigma, 0f, 1500f) is var nan && (float.IsNaN(nan) || nan == 0f || nan <= 1f), "NaN wetness stays bounded");

            // Drop size factor and payload.
            var factors = Enumerable.Range(1, 500).Select(RingRainbowMath.DropSizeFactor).ToArray();
            Check(factors.Min() >= 0.4f && factors.Max() <= 1.2f && factors.Max() - factors.Min() > 0.5f, "drop size factor range 0.4-1.2");
            byte[] payload = RingRainbowMath.Payload(low, new float[] { 0.1f, 0.2f, 0.3f, 0.4f, 0.5f }, 0.7f, 0.9f);
            Check(payload.Length == 80, "payload size must be 80 bytes (ring_rainbow.glsl Frame tail)");
            Check(Math.Abs(BitConverter.ToSingle(payload, 12) - low.HalfArcRadians) < 1e-6f && Math.Abs(BitConverter.ToSingle(payload, 28) - 0.7f) < 1e-6f
                && Math.Abs(BitConverter.ToSingle(payload, 44) - 0.9f) < 1e-6f && Math.Abs(BitConverter.ToSingle(payload, 48) - 0.1f) < 1e-6f
                && Math.Abs(BitConverter.ToSingle(payload, 64) - 0.5f) < 1e-6f && Math.Abs(BitConverter.ToSingle(payload, 68) - 30f) < 1e-6f, "payload layout");
            if (verbose) Console.WriteLine($"radius worst={worstRadius:0.00000} deg; top elevation at sun 20 deg = {topElevation:0.00}; half arc {Mathf.RadToDeg(low.HalfArcRadians):0.0} deg; strengths near/mid/far {near:0.000}/{mid:0.000}/{far:0.000} misty {misty:0.0000}");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("RING_RAINBOW_MATH=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("RING_RAINBOW_MATH=PASS");
        return 0;
    }
}
