using System;
using Godot;

namespace RingworldTechDemo;

/// <summary>Focused, headless-friendly assertions for the pure celestial evaluator.</summary>
public partial class CelestialCoreSmokeRunner : Node
{
    public string LastFailure { get; private set; } = string.Empty;

    public bool RunAll()
    {
        try
        {
            AssertClockRatiosAreNotSynchronous();
            AssertPhysicalPreset();
            AssertIndependentMotionsAndConfiguredTilt();
            AssertDeterminismAndFiniteFrame();
            AssertInvalidConfigurationFallsBackSafely();
            AssertPredictedHorizonCrossings();
            AssertGiantEventPredictionCanBeCached();
            AssertObserverParallax();
            AssertDaylightSurfaceParallaxRemainsPartial();
            AssertDefaultTotalEclipseDuration();
            AssertEclipseClassesAndSafetyCap();
            AssertSeasonWindows();
            GD.Print("CELESTIAL_CORE_MATH_TEST=PASS");
            return true;
        }
        catch (Exception exception)
        {
            LastFailure = exception.Message;
            GD.PushError($"CELESTIAL_CORE_MATH_TEST=FAIL {LastFailure}");
            return false;
        }
    }

    private static void AssertIndependentMotionsAndConfiguredTilt()
    {
        CelestialConfiguration config = MakeConfiguration();
        CelestialFrame start = Evaluate(config, 0d, Vector3.Zero);
        CelestialFrame oneRingDay = Evaluate(config, config.RingDaySeconds, Vector3.Zero);
        float expectedAxisDot = MathF.Sin(Mathf.DegToRad(config.RingTiltDegrees));
        Assert(Math.Abs(start.ToSun.Dot(Vector3.Up) - expectedAxisDot) < .00001f,
            "Configured ring tilt was not applied as the star's epoch declination.");
        Assert(start.ToSun.DistanceTo(oneRingDay.ToSun) < .00001f,
            "The star did not return after one exact ring rotation.");
        Assert(start.ToGiant.DistanceTo(oneRingDay.ToGiant) > .005f,
            "The independently orbiting giant repeated with the ring day.");
        Assert(Math.Abs(oneRingDay.OrbitalAngleRadians - start.OrbitalAngleRadians) > .005d,
            "Orbital angle did not advance independently from ring rotation.");
    }

    private static void AssertClockRatiosAreNotSynchronous()
    {
        const double orbit = GasGiantConfiguration.DefaultOrbitSeconds;
        const double day = CelestialConfiguration.DefaultRingDaySeconds;
        double ratio = orbit / day;
        Assert(Math.Abs(ratio - Math.Round(ratio)) > .001d,
            "The 75-minute ring day must not be synchronized to the giant orbit.");
        Assert(Fraction(orbit / day) > .001d && Fraction(orbit / 6180d) > .001d,
            "A ring day must not be synchronized to the giant orbit.");
    }

    private static void AssertPhysicalPreset()
    {
        var defaults = new CelestialConfiguration();
        GasGiantConfiguration giant = defaults.GasGiant;
        Assert(Math.Abs(defaults.RingDaySeconds - CelestialConfiguration.DefaultRingDaySeconds) < .000001d,
            "Default ring day is not the authored 75-minute cycle.");
        Assert(Math.Abs(giant.DistanceKm - 136610.390625f) < .001f
            && Math.Abs(giant.RadiusKm - 70000f) < .01f
            && Math.Abs(giant.GrazingMissDegrees - 10.87182510946549f) < .0001f
            && Math.Abs(giant.MassKg - 1.898e27d) / 1.898e27d < 1e-12d,
            "Gas-giant physical preset values changed unexpectedly.");
        const double gravitationalConstant = 6.67430e-11d;
        double keplerSeconds = Math.Tau * Math.Sqrt(Math.Pow(giant.DistanceKm * 1000d, 3d)
            / (gravitationalConstant * giant.MassKg));
        Assert(Math.Abs(giant.OrbitSeconds - keplerSeconds) < .001d,
            "Gas-giant orbit period is not consistent with its Kepler distance and mass preset.");
        Assert(Math.Abs(giant.OrbitSeconds - 28187.36649295002d) < .001d,
            "Gas-giant physical orbit period changed unexpectedly.");

        double alpha = Math.Asin(giant.RadiusKm / giant.DistanceKm);
        double width = Math.Tan(alpha) / (GasGiantConfiguration.NormalViewAspectRatio
            * Math.Tan(Mathf.DegToRad(GasGiantConfiguration.NormalViewVerticalFovDegrees) / 2d));
        Assert(Math.Abs(width - GasGiantConfiguration.NormalViewCenteredWidthFraction) < .000001d,
            "The documented normal-view physical giant no longer projects to 40% screen width.");
        double fartherAlpha = Math.Asin(giant.RadiusKm / (giant.DistanceKm * 1.25d));
        double fartherWidth = Math.Tan(fartherAlpha) / (GasGiantConfiguration.NormalViewAspectRatio
            * Math.Tan(Mathf.DegToRad(GasGiantConfiguration.NormalViewVerticalFovDegrees) / 2d));
        Assert(fartherAlpha < alpha && fartherWidth < width,
            "Projected giant size did not continue to derive from physical distance.");
    }

    private static void AssertDeterminismAndFiniteFrame()
    {
        CelestialConfiguration config = MakeConfiguration();
        CelestialFrame a = Evaluate(config, 123456.789d, Vector3.Zero);
        CelestialFrame b = Evaluate(config, 123456.789d, Vector3.Zero);
        Assert(a.ToGiant.DistanceTo(b.ToGiant) < .000001f && Math.Abs(a.RawEclipseCoverage - b.RawEclipseCoverage) < .000001f,
            "Same absolute time did not evaluate deterministically.");
        Assert(double.IsFinite(a.AbsoluteSeconds) && float.IsFinite(a.GiantDistanceKm)
            && float.IsFinite(a.GiantAngularRadiusDegrees) && float.IsFinite(a.SunElevationDegrees)
            && float.IsFinite(a.GiantElevationDegrees) && a.ToGiant.IsFinite(),
            "Celestial frame contains non-finite geometry.");
        Assert(Math.Abs(a.DaylightFactor + a.TwilightFactor + a.NightFactor - 1f) < .00001f,
            "Daylight, twilight and night factors must form a coherent normalized blend.");
        Assert(a.GiantAngularDiameterDegrees > 61.6f && a.GiantAngularDiameterDegrees < 61.7f,
            "Gas-giant apparent diameter is not derived from physical radius and distance.");
        Assert(a.NextSunriseSeconds >= a.AbsoluteSeconds && a.NextSunsetSeconds >= a.AbsoluteSeconds,
            "Sunrise/sunset estimates must be future absolute times.");
    }

    private static void AssertPredictedHorizonCrossings()
    {
        CelestialConfiguration config = MakeConfiguration();
        const double time = 137.25d;
        CelestialFrame frame = Evaluate(config, time, Vector3.Zero);
        Assert(double.IsFinite(frame.SecondsUntilSunrise)
            && frame.SecondsUntilSunrise >= 0d
            && frame.SecondsUntilSunrise <= config.RingDaySeconds,
            "Analytic sunrise prediction is missing or outside one ring day.");
        Assert(double.IsFinite(frame.SecondsUntilSunset)
            && frame.SecondsUntilSunset >= 0d
            && frame.SecondsUntilSunset <= config.RingDaySeconds,
            "Analytic sunset prediction is missing or outside one ring day.");
        double horizonDot = Math.Sin(Mathf.DegToRad(config.HorizonEventElevationDegrees));
        const double epsilon = .05d;
        CelestialFrame beforeRise = Evaluate(config, time + Math.Max(0d, frame.SecondsUntilSunrise - epsilon), Vector3.Zero);
        CelestialFrame afterRise = Evaluate(config, time + frame.SecondsUntilSunrise + epsilon, Vector3.Zero);
        Assert(beforeRise.ToSun.Dot(-Vector3.Right) <= horizonDot + .001d
            && afterRise.ToSun.Dot(-Vector3.Right) >= horizonDot - .001d,
            "Predicted sunrise is not an actual rising horizon root.");
        CelestialFrame beforeSet = Evaluate(config, time + Math.Max(0d, frame.SecondsUntilSunset - epsilon), Vector3.Zero);
        CelestialFrame afterSet = Evaluate(config, time + frame.SecondsUntilSunset + epsilon, Vector3.Zero);
        Assert(beforeSet.ToSun.Dot(-Vector3.Right) >= horizonDot - .001d
            && afterSet.ToSun.Dot(-Vector3.Right) <= horizonDot + .001d,
            "Predicted sunset is not an actual falling horizon root.");

        Assert(double.IsFinite(frame.SecondsUntilGiantRise)
            && double.IsFinite(frame.SecondsUntilGiantSet)
            && frame.SecondsUntilGiantRise <= config.RingDaySeconds * 1.5d
            && frame.SecondsUntilGiantSet <= config.RingDaySeconds * 1.5d,
            "Default giant rise/set predictions were not found by the bounded solver.");
        CelestialFrame beforeGiantRise = Evaluate(config, time + Math.Max(0d, frame.SecondsUntilGiantRise - epsilon), Vector3.Zero);
        CelestialFrame afterGiantRise = Evaluate(config, time + frame.SecondsUntilGiantRise + epsilon, Vector3.Zero);
        Assert(beforeGiantRise.ToGiant.Dot(-Vector3.Right) <= .001f
            && afterGiantRise.ToGiant.Dot(-Vector3.Right) >= -.001f,
            "Predicted giant rise is not a rising horizon root.");
    }

    private static void AssertGiantEventPredictionCanBeCached()
    {
        CelestialConfiguration config = MakeConfiguration();
        const double time = 137.25d;
        CelestialFrame predicted = Evaluate(config, time, Vector3.Zero);
        CelestialFrame instantaneous = CelestialEclipseMath.EvaluateFrame(
            time + 1d,
            Vector3.Forward,
            -Vector3.Right,
            Vector3.Up,
            config,
            Vector3.Zero,
            calculateGiantEvents: false);
        Assert(double.IsPositiveInfinity(instantaneous.SecondsUntilGiantRise)
            && double.IsPositiveInfinity(instantaneous.SecondsUntilGiantSet),
            "Skipping giant event prediction should leave explicit infinite placeholders.");
        Assert(instantaneous.ToGiant.IsFinite() && float.IsFinite(instantaneous.RawEclipseCoverage),
            "Skipping event prediction must not skip instantaneous giant geometry.");

        CelestialFrame cached = instantaneous.WithGiantEventDurations(
            Math.Max(0d, predicted.NextGiantRiseSeconds - instantaneous.AbsoluteSeconds),
            Math.Max(0d, predicted.NextGiantSetSeconds - instantaneous.AbsoluteSeconds));
        Assert(double.IsFinite(cached.SecondsUntilGiantRise)
            && double.IsFinite(cached.SecondsUntilGiantSet)
            && cached.ToGiant == instantaneous.ToGiant,
            "Cached giant event roots could not be reattached to an immutable frame.");
    }

    private static void AssertInvalidConfigurationFallsBackSafely()
    {
        CelestialConfiguration config = MakeConfiguration();
        config.RingDaySeconds = double.NaN;
        config.RingTiltDegrees = float.NaN;
        config.StarAngularRadiusDegrees = float.NaN;
        config.DaylightStartDot = float.NaN;
        config.DaylightFullDot = float.NaN;
        config.AstronomicalTwilightElevationDegrees = float.NaN;
        config.HorizonEventElevationDegrees = float.NaN;
        config.SunriseBandDegrees = float.NaN;
        config.GameplayEclipseDarknessCap = float.NaN;
        config.GasGiant.RadiusKm = float.NaN;
        config.GasGiant.DistanceKm = float.NaN;
        config.GasGiant.OrbitSeconds = double.NaN;
        config.GasGiant.GrazingMissDegrees = float.NaN;
        config.GasGiant.InitialPhaseDegrees = float.NaN;
        config.GasGiant.SeasonMissAmplitudeDegrees = float.NaN;
        config.GasGiant.SeasonSeconds = double.NaN;
        config.GasGiant.SeasonPhaseDegrees = float.NaN;

        CelestialFrame frame = CelestialEclipseMath.EvaluateFrame(
            double.NaN,
            new Vector3(float.NaN, 0f, 0f),
            new Vector3(0f, float.NaN, 0f),
            new Vector3(0f, 0f, float.NaN),
            config,
            new Vector3(float.NaN, float.NaN, float.NaN));
        Assert(double.IsFinite(frame.RingRotationAngleRadians)
            && double.IsFinite(frame.OrbitalAngleRadians)
            && frame.ToSun.IsFinite()
            && frame.ToGiant.IsFinite()
            && float.IsFinite(frame.GiantDistanceKm)
            && float.IsFinite(frame.GiantAngularRadiusDegrees)
            && float.IsFinite(frame.RawEclipseCoverage)
            && Math.Abs(frame.DaylightFactor + frame.TwilightFactor + frame.NightFactor - 1f) < .00001f,
            "Invalid resource values propagated non-finite celestial state.");
    }

    private static void AssertObserverParallax()
    {
        CelestialConfiguration config = MakeConfiguration(GasGiantConfiguration.PartialFixtureGrazingMissDegrees);
        config.GasGiant.InitialPhaseDegrees = 0f;
        CelestialFrame center = Evaluate(config, 0d, Vector3.Zero);
        Vector3 sideways = center.ToGiant.Cross(Vector3.Up);
        if (sideways.LengthSquared() < .00001f) sideways = center.ToGiant.Cross(Vector3.Right);
        CelestialFrame rim = Evaluate(config, 0d, sideways.Normalized() * 5000f);
        Assert(center.ToGiant.DistanceTo(rim.ToGiant) > .001f,
            "A 5,000 km observer offset did not produce giant parallax.");
        Assert(Math.Abs(center.RawEclipseCoverage - rim.RawEclipseCoverage) > .00001f,
            "Player-relative parallax did not affect eclipse overlap.");
    }

    private static void AssertDaylightSurfaceParallaxRemainsPartial()
    {
        // Deliberately pin this geometry to the authored partial fixture; a
        // future total-eclipse presentation preset must not weaken this scan.
        CelestialConfiguration config = MakeConfiguration(30.724235534668f);
        config.GasGiant.InitialPhaseDegrees = 180f;
        int[] daylightEclipseCounts = { 0, 0 };
        float[] maxVisibleCoverage = { 0f, 0f };
        float[] radiiKm = { 80f, 5000f };
        float[][] axialOffsetsKm =
        {
            new[] { -10f, 10f },
            new[] { -159f, 159f }
        };
        for (double delta = -1800d; delta <= 1800d; delta += 10d)
        for (int longitudeDegrees = 0; longitudeDegrees < 360; longitudeDegrees += 10)
        for (int radiusIndex = 0; radiusIndex < radiiKm.Length; radiusIndex++)
        for (int axialIndex = 0; axialIndex < axialOffsetsKm[radiusIndex].Length; axialIndex++)
        {
            float longitude = Mathf.DegToRad(longitudeDegrees);
            float radius = radiiKm[radiusIndex];
            Vector3 surfaceUp = new(-Mathf.Cos(longitude), 0f, -Mathf.Sin(longitude));
            Vector3 observerOffset = new(
                radius * Mathf.Cos(longitude), 0f, radius * Mathf.Sin(longitude));
            observerOffset.Y = axialOffsetsKm[radiusIndex][axialIndex];
            CelestialFrame surface = CelestialEclipseMath.EvaluateFrame(
                config.GasGiant.OrbitSeconds * .5d + delta,
                Vector3.Forward, surfaceUp, Vector3.Up, config, observerOffset,
                calculateGiantEvents: false);
            if (surface.SunElevationDegrees <= 0f || surface.RawEclipseCoverage <= 0f)
                continue;
            daylightEclipseCounts[radiusIndex]++;
            maxVisibleCoverage[radiusIndex] = MathF.Max(maxVisibleCoverage[radiusIndex], surface.RawEclipseCoverage);
            Assert(surface.RawEclipseCoverage < 1f
                && surface.EclipseType == CelestialEclipseType.Partial
                && surface.GameplayEclipseDarkness <= .950001f,
                "A daylight ground-observer eclipse was not a capped partial eclipse.");
        }
        Assert(daylightEclipseCounts[0] > 0 && daylightEclipseCounts[1] > 0,
            "No daylight partial eclipses were found for one or both representative ring radii.");
        GD.Print($"CELESTIAL_DAYLIGHT_PARTIAL_SCAN=PASS max80={maxVisibleCoverage[0]:0.00000} max5000={maxVisibleCoverage[1]:0.00000}");
    }

    private static void AssertEclipseClassesAndSafetyCap()
    {
        CelestialConfiguration noneConfig = MakeConfiguration(45f);
        CelestialFrame none = Evaluate(noneConfig, 0d, Vector3.Zero);
        Assert(none.RawEclipseCoverage < .000001f && none.EclipseType == CelestialEclipseType.None,
            "Wide miss should classify as no eclipse.");

        CelestialConfiguration partialConfig = MakeConfiguration(GasGiantConfiguration.PartialFixtureGrazingMissDegrees);
        CelestialFrame partial = Evaluate(partialConfig, 0d, Vector3.Zero);
        Assert(Math.Abs(partial.RawEclipseCoverage - .73367293f) < .0005f
            && partial.EclipseType == CelestialEclipseType.Partial,
            "Explicit partial fixture should classify as a partial eclipse.");

        CelestialConfiguration totalConfig = MakeConfiguration(0f);
        CelestialFrame total = Evaluate(totalConfig, 0d, Vector3.Zero);
        Assert(total.RawEclipseCoverage > .99999f && total.EclipseType == CelestialEclipseType.Total,
            "Zero miss should classify as total raw coverage.");
        Assert(Math.Abs(total.GameplayEclipseDarkness - .95f) < .00001f,
            "Gameplay eclipse darkness cap must remain independent from raw totality.");
    }

    private static void AssertDefaultTotalEclipseDuration()
    {
        CelestialConfiguration config = MakeConfiguration();
        double duration = CelestialEclipseMath.CenteredTotalEclipseDurationSeconds(
            config.GasGiant.RadiusKm, config.GasGiant.DistanceKm,
            config.StarAngularRadiusDegrees, config.GasGiant.GrazingMissDegrees,
            config.GasGiant.OrbitSeconds);
        Assert(Math.Abs(duration - GasGiantConfiguration.DefaultCenteredTotalDurationSeconds) < .1d,
            "Default total-eclipse duration is not the authored 4,500 seconds.");
        CelestialFrame center = Evaluate(config, 0d, Vector3.Zero);
        CelestialFrame inside = Evaluate(config, duration / 2d - .1d, Vector3.Zero);
        CelestialFrame outside = Evaluate(config, duration / 2d + .1d, Vector3.Zero);
        Assert(center.RawEclipseCoverage > .99999f && center.EclipseType == CelestialEclipseType.Total
            && inside.EclipseType == CelestialEclipseType.Total
            && outside.EclipseType == CelestialEclipseType.Partial,
            "Default total eclipse contact boundaries do not enclose the authored 4,500-second interval.");
        Assert(center.GameplayEclipseDarkness <= .950001f,
            "The default total eclipse bypassed the gameplay darkness safety cap.");
    }

    private static void AssertSeasonWindows()
    {
        CelestialConfiguration config = MakeConfiguration(45f);
        config.GasGiant.SeasonMissAmplitudeDegrees = 45f;
        config.GasGiant.SeasonSeconds = 1000d;
        CelestialFrame noEclipseSeason = Evaluate(config, 250d, Vector3.Zero);
        CelestialFrame eclipseSeason = Evaluate(config, 750d, Vector3.Zero);
        Assert(noEclipseSeason.RawEclipseCoverage < .000001f,
            "Seasonal high-miss window should have no eclipse.");
        Assert(eclipseSeason.RawEclipseCoverage > .99999f,
            "Seasonal low-miss window should permit raw totality.");
        Assert(eclipseSeason.GameplayEclipseDarkness <= .950001f,
            "Seasonal totality must still honor the gameplay safety cap.");
    }

    private static CelestialFrame Evaluate(CelestialConfiguration config, double time, Vector3 observerOffsetKm) =>
        CelestialEclipseMath.EvaluateFrame(time, Vector3.Forward, -Vector3.Right, Vector3.Up, config, observerOffsetKm);

    private static CelestialConfiguration MakeConfiguration(float miss = GasGiantConfiguration.DefaultGrazingMissDegrees) => new()
    {
        RingDaySeconds = CelestialConfiguration.DefaultRingDaySeconds,
        StarAngularRadiusDegrees = .265f,
        GameplayEclipseDarknessCap = .95f,
        GasGiant = new GasGiantConfiguration
        {
            Enabled = true, RadiusKm = GasGiantConfiguration.DefaultRadiusKm,
            DistanceKm = GasGiantConfiguration.DefaultDistanceKm,
            OrbitSeconds = GasGiantConfiguration.DefaultOrbitSeconds,
            GrazingMissDegrees = miss, InitialPhaseDegrees = 0f
        }
    };

    private static double Fraction(double value) => value - Math.Floor(value);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
