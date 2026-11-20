using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Atmosphere.NearFog;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// NF-3 (docs/sun-flare-shafts-near-fog-plan.md, N3): the near ground fog drivers (pure function and smoother) and the near-water field builder with a synthetic sampler. Headless.
/// Checks: monotone in humidity and soak; each kind is 0 above its wind cut-off; radiation is 0 from local noon to sunset, rises through the night, peaks at sunrise, is 0 once the sun is above 20 degrees, and is continuous
/// at sunrise and sunset; no radiation fog under full rain; a dry climate gives no radiation or mist; the smoother converges with tau 20 s and Reset snaps; the water field marks exactly the river strip and lake disc
/// (not the ocean, not a river without an exact level), carries the level, recentres after a 33 m move but not a 31 m move, wraps its frame around the circumference, and reads size 0 when invalid.
/// Prints NEAR_FOG_DRIVERS=PASS|FAIL. Headless: tools/run_tests.ps1 -Tests near_fog_drivers_test.
/// </summary>
public partial class NearFogDriversTest : RefCounted
{
    // Synthetic ring day: period 4500 s, day 2700 s (sun above the horizon event), night 1800 s. tau = 0 is sunrise.
    private const double Period = 4500.0, DayLength = 2700.0, NightLength = 1800.0;
    private const float PeakElevation = 60f, HorizonElevation = -0.833f;
    private readonly List<string> _failures = new();

    private void Fail(string message) { if (_failures.Count < 40) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("NEAR_FOG_DRIVERS_DETAIL " + text);

    private static NearFogInputs AtTime(double tau, float m = 0.9f, float soak = 0f, float t = 0.3f, float rain = 0f, float wind = 0f, float coverage = 0f)
    {
        tau = ((tau % Period) + Period) % Period;
        float elevation;
        double untilRise, untilSet;
        if (tau < DayLength)
        {
            elevation = HorizonElevation + (PeakElevation - HorizonElevation) * MathF.Sin(MathF.PI * (float)(tau / DayLength));
            untilSet = DayLength - tau;
            untilRise = untilSet + NightLength;
        }
        else
        {
            elevation = HorizonElevation - 30f * MathF.Sin(MathF.PI * (float)((tau - DayLength) / NightLength));
            untilRise = Period - tau;
            untilSet = untilRise + DayLength;
        }
        return new NearFogInputs
        {
            Moisture = m, Soak = soak, Temperature = t, RainNow = rain, WindMetersPerSecond = wind, Coverage = coverage,
            SunElevationDegrees = elevation, SecondsUntilSunrise = untilRise, SecondsUntilSunset = untilSet, DaySeconds = Period,
        };
    }

    private void TestMonotone()
    {
        foreach (double tau in new[] { 4000.0, 4400.0, 100.0, 300.0 })
        {
            foreach (float soakFixed in new[] { 0f, 0.5f })
            {
                float lastRad = -1f, lastMist = -1f;
                for (int k = 0; k <= 100; k++)
                {
                    NearFogDrive d = RingNearFogDrivers.Evaluate(AtTime(tau, m: k / 100f, soak: soakFixed));
                    Check(d.Radiation >= lastRad - 1e-6f, $"radiation not monotone in m at tau {tau}, soak {soakFixed}, m {k / 100f}");
                    Check(d.Mist >= lastMist - 1e-6f, $"mist not monotone in m at tau {tau}, soak {soakFixed}, m {k / 100f}");
                    lastRad = d.Radiation; lastMist = d.Mist;
                }
            }
            foreach (float mFixed in new[] { 0.2f, 0.5f, 0.7f })
            {
                float lastRad = -1f, lastMist = -1f;
                for (int k = 0; k <= 100; k++)
                {
                    NearFogDrive d = RingNearFogDrivers.Evaluate(AtTime(tau, m: mFixed, soak: k / 100f));
                    Check(d.Radiation >= lastRad - 1e-6f, $"radiation not monotone in soak at tau {tau}, m {mFixed}, soak {k / 100f}");
                    Check(d.Mist >= lastMist - 1e-6f, $"mist not monotone in soak at tau {tau}, m {mFixed}, soak {k / 100f}");
                    lastRad = d.Radiation; lastMist = d.Mist;
                }
            }
        }
        NearFogDrive wet = RingNearFogDrivers.Evaluate(AtTime(4400, m: 1f, soak: 1f));
        Check(wet.Radiation > 0.5f && wet.Mist > 0.5f, $"saturated calm pre-dawn should fog heavily: rad {wet.Radiation:F3} mist {wet.Mist:F3}");
        Detail($"saturated pre-dawn: rad {wet.Radiation:F3} mist {wet.Mist:F3} steam {wet.Steam:F3}");
    }

    private void TestWind()
    {
        foreach (double tau in new[] { 4000.0, 4400.0, 4499.0 })
        {
            for (float wind = 0f; wind <= 20f; wind += 0.25f)
            {
                NearFogDrive d = RingNearFogDrivers.Evaluate(AtTime(tau, m: 1f, soak: 1f, wind: wind));
                if (wind >= RingNearFogDrivers.RadiationWindEnd) Check(d.Radiation == 0f, $"radiation {d.Radiation} above its cut-off at {wind} m/s");
                if (wind >= RingNearFogDrivers.MistWindEnd) Check(d.Mist == 0f, $"mist {d.Mist} above its cut-off at {wind} m/s");
                if (wind >= RingNearFogDrivers.SteamWindEnd) Check(d.Steam == 0f, $"steam {d.Steam} above its cut-off at {wind} m/s");
            }
        }
        NearFogDrive calm = RingNearFogDrivers.Evaluate(AtTime(4400, m: 1f, soak: 1f, wind: 0f));
        Check(calm.Radiation > 0f && calm.Mist > 0f && calm.Steam > 0f, "calm pre-dawn should have all three kinds");
    }

    private void TestDiurnal()
    {
        const float rich = 1f; // saturated, calm, clear, cool: only the clock limits radiation
        float Rad(double tau) => RingNearFogDrivers.Evaluate(AtTime(tau, m: rich, soak: 1f, t: 0.3f)).Radiation;
        float peak = 0f;
        for (double tau = 0; tau < Period; tau += 1.0) peak = MathF.Max(peak, Rad(tau));
        Check(peak > 0.99f, $"radiation peak {peak} should reach 1 in a rich state");
        // Noon and all afternoon to sunset: zero.
        for (double tau = DayLength * 0.5; tau < DayLength; tau += 1.0)
            if (Rad(tau) != 0f) { Fail($"radiation {Rad(tau)} after noon at tau {tau}"); break; }
        // Gone by 20 degrees: every day second with the sun above 20 deg.
        for (double tau = 0; tau < DayLength; tau += 1.0)
            if (AtTime(tau).SunElevationDegrees >= 20f && Rad(tau) != 0f) { Fail($"radiation {Rad(tau)} with the sun at {AtTime(tau).SunElevationDegrees} deg"); break; }
        // Rises through the night, peaks at sunrise.
        float last = -1f;
        for (double tau = DayLength; tau < Period; tau += 1.0)
        {
            float r = Rad(tau);
            if (r < last - 1e-6f) { Fail($"radiation fell during the night at tau {tau}"); break; }
            last = r;
        }
        Check(MathF.Abs(Rad(Period - 0.01) - peak) < 1e-3f, $"radiation at sunrise {Rad(Period - 0.01)} is not the peak {peak}");
        Check(Rad(DayLength + 1.0) < 1e-3f, "radiation just after sunset should be about 0");
        // Burn-off after sunrise decreases.
        last = 2f;
        for (double tau = 0; tau < DayLength * 0.5; tau += 1.0)
        {
            float r = Rad(tau);
            if (r > last + 1e-6f) { Fail($"radiation rose during the morning at tau {tau}"); break; }
            last = r;
        }
        // Continuity at sunrise and sunset: any 1 s step.
        float riseJump = 0f, setJump = 0f;
        for (double tau = -3.0; tau < 3.0; tau += 0.25)
            riseJump = MathF.Max(riseJump, MathF.Abs(Rad(tau + 1.0) - Rad(tau)));
        for (double tau = DayLength - 3.0; tau < DayLength + 3.0; tau += 0.25)
            setJump = MathF.Max(setJump, MathF.Abs(Rad(tau + 1.0) - Rad(tau)));
        Check(riseJump < 1e-3f, $"radiation jumps {riseJump} within 1 s at sunrise");
        Check(setJump < 1e-3f, $"radiation jumps {setJump} within 1 s at sunset");
        // The diurnal factor itself is continuous too (steam and mist use it).
        float timeJump = 0f;
        foreach (double edge in new[] { 0.0, DayLength })
            for (double tau = edge - 3.0; tau < edge + 3.0; tau += 0.25)
                timeJump = MathF.Max(timeJump, MathF.Abs(RingNearFogDrivers.RadiationTime(AtTime(tau + 1.0)) - RingNearFogDrivers.RadiationTime(AtTime(tau))));
        Check(timeJump < 1e-3f, $"f_rad_time jumps {timeJump} within 1 s at a horizon event");
        // Unknown events (polar day or night) stay finite and inside 0..1.
        var polar = new NearFogInputs { Moisture = 1f, SunElevationDegrees = -10f, SecondsUntilSunrise = double.PositiveInfinity, SecondsUntilSunset = double.PositiveInfinity, DaySeconds = Period };
        float f = RingNearFogDrivers.RadiationTime(polar);
        Check(f >= 0f && f <= 1f, $"polar night factor {f}");
        Detail($"radiation peak {peak:F3}, max 1 s jump at sunrise {riseJump:E2}, at sunset {setJump:E2}");
    }

    private void TestRainAndDry()
    {
        for (double tau = 0; tau < Period; tau += 5.0)
        {
            NearFogDrive rain = RingNearFogDrivers.Evaluate(AtTime(tau, m: 1f, soak: 1f, rain: 1f));
            Check(rain.Radiation == 0f, $"radiation {rain.Radiation} under full rain at tau {tau}");
            Check(rain.Mist <= 0.5f + 1e-6f, $"mist {rain.Mist} under full rain exceeds half");
            NearFogDrive dry = RingNearFogDrivers.Evaluate(AtTime(tau, m: 0.2f, soak: 0f));
            Check(dry.Radiation == 0f && dry.Mist == 0f, $"dry climate gave rad {dry.Radiation} mist {dry.Mist} at tau {tau}");
        }
        // Warm and cloudy kill radiation, not steam's time rule.
        float cool = RingNearFogDrivers.Evaluate(AtTime(4400, m: 1f, soak: 1f, t: 0.3f)).Radiation;
        Check(RingNearFogDrivers.Evaluate(AtTime(4400, m: 1f, soak: 1f, t: 0.99f)).Radiation == 0f, "radiation should be 0 in a hot climate");
        float overcast = RingNearFogDrivers.Evaluate(AtTime(4400, m: 1f, soak: 1f, coverage: 1f)).Radiation;
        Check(MathF.Abs(overcast - cool * 0.2f) < 1e-4f, $"overcast radiation {overcast} should be 0.2 of clear {cool}");
        // NaN and out-of-range inputs stay finite.
        NearFogDrive bad = RingNearFogDrivers.Evaluate(new NearFogInputs { Moisture = float.NaN, Soak = 5f, WindMetersPerSecond = float.NaN, SecondsUntilSunrise = double.NaN, SecondsUntilSunset = double.NaN, DaySeconds = double.NaN });
        Check(float.IsFinite(bad.Radiation) && float.IsFinite(bad.Mist) && float.IsFinite(bad.Steam), "non-finite inputs produced a non-finite drive");
    }

    private void TestSmoother()
    {
        var smoother = new NearFogDriverSmoother();
        var target = new NearFogDrive(1f, 0.5f, 0.2f, 1f);
        smoother.Update(0.5f, target);
        Check(smoother.Radiation == 1f && smoother.Mist == 0.5f && smoother.Steam == 0.2f, "the first update should snap");
        smoother.Reset(default);
        Check(smoother.Radiation == 0f && smoother.Mist == 0f && smoother.Steam == 0f, "Reset(zero) did not snap to zero");
        for (int k = 0; k < 40; k++) smoother.Update(0.5f, target); // 20 s at 2 Hz
        float expected = 1f - MathF.Exp(-1f);
        Check(MathF.Abs(smoother.Radiation - expected) < 5e-3f, $"after one tau radiation {smoother.Radiation} should be {expected}");
        Check(MathF.Abs(smoother.Mist - 0.5f * expected) < 5e-3f && MathF.Abs(smoother.Steam - 0.2f * expected) < 5e-3f, "mist or steam off the 1 - 1/e curve");
        for (int k = 0; k < 200; k++) smoother.Update(0.5f, target); // 120 s more: 6.0 tau total
        Check(smoother.Radiation > 0.995f && MathF.Abs(smoother.Mist - 0.5f) < 5e-3f, $"smoother did not converge: {smoother.Radiation}");
        smoother.Reset(new NearFogDrive(0.25f, 0.25f, 0.25f, 0f));
        Check(smoother.Radiation == 0.25f && smoother.Mist == 0.25f && smoother.Steam == 0.25f, "Reset did not snap");
        // Step size independence: one 20 s step equals 40 half-second steps for the exponential form.
        var a = new NearFogDriverSmoother(); a.Reset(default); a.Update(20f, target);
        Check(MathF.Abs(a.Radiation - expected) < 1e-5f, $"a single 20 s step gives {a.Radiation}");
    }

    private static TerrainSample Sample(SurfaceWaterKind kind, float level, bool exact, int surface = 0)
        => new(SurfaceHeight: surface, Region: default, Continentalness: 0f, Erosion: 0f, RidgeStrength: 0f, Temperature: 0.5f, Moisture: 0.5f, Biome: default,
            WaterKind: kind, WaterSurfaceHeight: (int)MathF.Floor(level), ExactWaterLevel: level, HasExactWaterLevel: exact);

    // The synthetic world: a river strip (across 100..107, all around), a lake disc, and an ocean patch, a river-without-level patch and a river-kind bank patch (bed above the level) that must not be marked.
    private const float RiverLevel = 50.5f, LakeLevel = 12.25f;
    private static readonly (long Around, int Across, int Radius) Lake = (1000, 300, 40);
    private static TerrainSample World(long around, int across)
    {
        if (across >= 100 && across <= 107) return Sample(SurfaceWaterKind.River, RiverLevel, true);
        long da = around - Lake.Around, dz = across - Lake.Across;
        if (da * da + dz * dz <= (long)Lake.Radius * Lake.Radius) return Sample(SurfaceWaterKind.Lake, LakeLevel, true);
        if (around >= 1100 && around < 1140 && across >= 150 && across < 190) return Sample(SurfaceWaterKind.Ocean, 0f, true);
        if (around >= 900 && around < 940 && across >= 150 && across < 190) return Sample(SurfaceWaterKind.River, 7f, false);
        if (around >= 1000 && around < 1040 && across >= 150 && across < 190) return Sample(SurfaceWaterKind.River, 7f, true, surface: 9);
        return Sample(SurfaceWaterKind.None, 0f, false);
    }

    private static void Settle(RingNearFogWaterField field, double around, double across)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        do { field.Update(around, across); if (field.Building) Thread.Sleep(2); }
        while (field.Building && clock.Elapsed.TotalSeconds < 20.0);
    }

    private void TestWaterField()
    {
        const double circumference = 1_000_000.0;
        using var field = new RingNearFogWaterField(World, circumference, 20000);
        Check(!field.HasField && field.FrameFor(0, 0).W == 0f, "a new field should report size 0");
        field.Update(1000.0, 200.0);
        Check(field.Building, "the first update should start a build");
        Settle(field, 1000.0, 200.0);
        Check(field.HasField && field.BuildsCompleted == 1 && field.BuildsStarted == 1, $"build did not land: has {field.HasField}, started {field.BuildsStarted}, completed {field.BuildsCompleted}");
        Check(field.OriginAroundMeters == 1000.0 - 256.0 && field.OriginAcrossMeters == 200.0 - 256.0, $"origin ({field.OriginAroundMeters}, {field.OriginAcrossMeters}) should be the snapped centre - 256");
        int rivers = 0, lakes = 0, mismatches = 0;
        for (int j = 0; j < RingNearFogWaterField.Size; j++)
            for (int i = 0; i < RingNearFogWaterField.Size; i++)
            {
                long around = (long)field.OriginAroundMeters + i * 8L;
                long across = (long)field.OriginAcrossMeters + j * 8L;
                TerrainSample s = World(around, (int)across);
                bool water = across >= 0 && (s.WaterKind == SurfaceWaterKind.River || s.WaterKind == SurfaceWaterKind.Lake) && s.HasExactWaterLevel && s.SurfaceHeight < s.ExactWaterLevel;
                (float mask, float height) = field.Texel(i, j);
                float expectedHeight = water ? s.ExactWaterLevel : 0f;
                if (mask != (water ? 1f : 0f) || height != expectedHeight) mismatches++;
                if (water && s.WaterKind == SurfaceWaterKind.River) rivers++;
                if (water && s.WaterKind == SurfaceWaterKind.Lake) lakes++;
            }
        Check(mismatches == 0, $"{mismatches} texels differ from the synthetic world");
        Check(rivers >= 64 && lakes >= 50, $"expected a river strip and a lake disc in the field, got river {rivers} lake {lakes} texels");
        Detail($"water field: river texels {rivers}, lake texels {lakes}, build {field.LastBuildMilliseconds:F2} ms (synthetic sampler)");

        // Recentre: 31 m no, 33 m yes.
        Settle(field, 1031.0, 200.0);
        Check(field.BuildsStarted == 1, $"a 31 m move started a build ({field.BuildsStarted})");
        Settle(field, 1000.0, 231.0);
        Check(field.BuildsStarted == 1, $"a 31 m cross move started a build ({field.BuildsStarted})");
        field.Update(1033.0, 200.0);
        Check(field.Building && field.BuildsStarted == 2, "a 33 m move should start a build");
        Settle(field, 1033.0, 200.0);
        Check(field.BuildsCompleted == 2 && field.OriginAroundMeters == 1032.0 - 256.0, $"recentre origin {field.OriginAroundMeters}");
        // A running build does not stack: updates while building start nothing.
        field.Update(1200.0, 200.0);
        int started = field.BuildsStarted;
        field.Update(1300.0, 200.0);
        Check(field.BuildsStarted == started, "an update while building started a second build");
        Settle(field, 1300.0, 200.0);

        // The frame around the seam: field at around ~ circumference - 100, anchor at 0.
        Settle(field, circumference - 100.0, 200.0);
        Settle(field, circumference - 100.0, 200.0);
        Vector4 frame = field.FrameFor(0, 0);
        double wrapped = field.OriginAroundMeters - circumference;
        Check(MathF.Abs(frame.X - (float)wrapped) < 0.5f && MathF.Abs(frame.X) < 1000f, $"frame around {frame.X} should be the wrapped {wrapped}");
        Check(frame.Z == 8f && frame.W == 64f, $"frame texel/size {frame.Z}/{frame.W}");
        // And from the other side.
        Vector4 other = field.FrameFor((long)circumference - 4096, 0);
        Check(MathF.Abs(other.X - (frame.X + 4096f)) < 0.5f, $"frame wrap against a high anchor: {other.X} vs {frame.X + 4096f}");

        field.Invalidate();
        Check(!field.HasField && field.FrameFor(0, 0).W == 0f, "Invalidate should give size 0");
    }

    private void TestWaterFieldFailure()
    {
        using var field = new RingNearFogWaterField((a, z) => throw new InvalidOperationException("synthetic sampler failure"), 1_000_000.0, 20000);
        field.Update(0.0, 0.0);
        Settle(field, 0.0, 0.0);
        Check(!field.HasField && field.BuildsFailed == 1, $"a failing sampler should leave no field (has {field.HasField}, failed {field.BuildsFailed})");
    }

    private void TestProbeNeutral()
    {
        var probe = new RingNearFogInputsProbe();
        var camera = new RingCanonicalPosition(1000.0, 0.0, 200.0);
        NearFogInputs inputs = probe.Gather(camera);
        NearFogDrive d = RingNearFogDrivers.Evaluate(inputs);
        Check(d.Radiation == 0f && d.Mist == 0f && d.Steam >= 0f, "a probe with no sources should be dry");
        const int count = 200000;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        float sink = 0f;
        for (int k = 0; k < count; k++) sink += probe.Gather(camera).Moisture + RingNearFogDrivers.Evaluate(inputs).Mist;
        double microseconds = clock.Elapsed.TotalMilliseconds * 1000.0 / count;
        Detail($"probe (no sources) + evaluate: {microseconds:F3} us per call ({sink:F1})");
        Check(microseconds < 50.0, $"probe plus evaluate costs {microseconds:F2} us");
    }

    private void TestActivationRate()
    {
        float threshold = RingNearFog.ActivationThreshold;
        float dry = RingNearFog.ActivationRate(new Vector3(0f, 0f, 1f), false, 1f, 1f);
        float wet = RingNearFog.ActivationRate(new Vector3(0f, 0f, 1f), true, 1f, 1f);
        Check(dry == 0f && dry < threshold, $"steam-only drive without water should not activate (rate {dry})");
        Check(Mathf.IsEqualApprox(wet, 0.01f) && wet >= threshold, $"steam-only drive with water should give 0.01 (rate {wet})");
        foreach (bool near in new[] { false, true })
        {
            Check(Mathf.IsEqualApprox(RingNearFog.ActivationRate(new Vector3(1f, 0f, 0f), near, 1f, 1f), 0.01f), $"radiation term should not depend on the water flag ({near})");
            Check(Mathf.IsEqualApprox(RingNearFog.ActivationRate(new Vector3(0f, 1f, 0f), near, 1f, 1f), 0.002f), $"mist term should not depend on the water flag ({near})");
        }
        Check(Mathf.IsEqualApprox(RingNearFog.ActivationRate(new Vector3(0f, 0f, 1f), true, 0.5f, 0.5f), 0.0025f), "amount and gate should scale the rate linearly");
    }

    public int Run()
    {
        TestMonotone();
        TestWind();
        TestDiurnal();
        TestRainAndDry();
        TestSmoother();
        TestWaterField();
        TestWaterFieldFailure();
        TestProbeNeutral();
        TestActivationRate();
        if (_failures.Count == 0)
        {
            Console.WriteLine("NEAR_FOG_DRIVERS=PASS");
            return 0;
        }
        foreach (string failure in _failures) Console.WriteLine("NEAR_FOG_DRIVERS_FAIL " + failure);
        Console.WriteLine("NEAR_FOG_DRIVERS=FAIL");
        return 1;
    }
}
