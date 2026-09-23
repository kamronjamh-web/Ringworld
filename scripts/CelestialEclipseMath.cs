using System;
using Godot;

namespace RingworldTechDemo;

public readonly record struct CelestialEclipseSnapshot(
    Vector3 GiantDirection,
    float GiantAngularRadiusRadians,
    float EclipseCoverage);

/// <summary>Allocation-free deterministic geometry used by the runtime celestial clock.</summary>
public static class CelestialEclipseMath
{
    private const double Tiny = 1e-12;
    private const int GiantEventScanSteps = 160;
    private const int MaxGiantEventScanSteps = 4096;
    private const int GiantOrbitSamplesPerCycle = 32;
    private const int GiantEventBisectionSteps = 28;

    /// <summary>
    /// Evaluates all celestial state from absolute time. The authored star vector
    /// supplies only epoch azimuth/sign; configuration owns its declination.
    /// Ring spin and giant orbit are derived independently and never accumulated.
    /// </summary>
    public static CelestialFrame EvaluateFrame(
        double absoluteSeconds,
        Vector3 authoredEpochToSun,
        Vector3 surfaceUp,
        Vector3 referenceAxis,
        CelestialConfiguration configuration,
        Vector3 observerOffsetKm) => EvaluateFrame(
            absoluteSeconds,
            authoredEpochToSun,
            surfaceUp,
            referenceAxis,
            configuration,
            observerOffsetKm,
            calculateGiantEvents: true);

    /// <summary>
    /// Runtime overload. Instantaneous geometry remains exact when giant event
    /// prediction is skipped; callers may cache those two comparatively costly
    /// horizon roots and inject their remaining durations into the returned frame.
    /// </summary>
    public static CelestialFrame EvaluateFrame(
        double absoluteSeconds,
        Vector3 authoredEpochToSun,
        Vector3 surfaceUp,
        Vector3 referenceAxis,
        CelestialConfiguration configuration,
        Vector3 observerOffsetKm,
        bool calculateGiantEvents)
    {
        configuration ??= new CelestialConfiguration();
        double time = double.IsFinite(absoluteSeconds) ? Math.Max(0d, absoluteSeconds) : 0d;
        GasGiantConfiguration giant = configuration.GasGiant ?? new GasGiantConfiguration();
        DVector3 axis = Normalize(DVector3.From(referenceAxis), DVector3.Up);
        DVector3 epochSun = ConfigureEpochSun(
            DVector3.From(authoredEpochToSun),
            axis,
            ClampFinite(configuration.RingTiltDegrees, 15d, -89.9d, 89.9d));
        DVector3 up = Normalize(DVector3.From(surfaceUp), -DVector3.Right);
        DVector3 observer = observerOffsetKm.IsFinite()
            ? DVector3.From(observerOffsetKm)
            : new DVector3(0d, 0d, 0d);

        double daySeconds = PositiveFiniteOr(configuration.RingDaySeconds, CelestialConfiguration.DefaultRingDaySeconds, 1d);
        double ringAngle = RingAngleAt(time, daySeconds);
        double orbitAngle = OrbitAngleAt(time, giant);
        double seasonAngle = SeasonAngleAt(time, giant);
        double signedMissRadians = SeasonalMissRadians(giant, seasonAngle);

        DVector3 sun = Normalize(Rotate(epochSun, axis, ringAngle), DVector3.Forward);
        DVector3 toGiant;
        double giantDistanceKm;
        double giantAngularRadiusRadians;
        double separationRadians;
        double rawCoverage;
        CelestialEclipseType eclipseType;

        if (giant.Enabled)
        {
            DVector3 inertialGiant = EvaluateOrbitDirection(epochSun, axis, orbitAngle, signedMissRadians);
            DVector3 giantCenterDirection = Normalize(Rotate(inertialGiant, axis, ringAngle), -sun);
            double physicalRadiusKm = PositiveFiniteOr(giant.RadiusKm, GasGiantConfiguration.DefaultRadiusKm, 0d);
            double configuredDistanceKm = PositiveFiniteOr(giant.DistanceKm, GasGiantConfiguration.DefaultDistanceKm, .001d);
            double centerDistanceKm = Math.Max(physicalRadiusKm + .001d, configuredDistanceKm);
            DVector3 relative = giantCenterDirection * centerDistanceKm - observer;
            giantDistanceKm = Math.Max(.001d, relative.Length());
            toGiant = Normalize(relative, giantCenterDirection);
            giantAngularRadiusRadians = Math.Asin(Math.Clamp(
                physicalRadiusKm / giantDistanceKm,
                0d,
                .999999999d));
            separationRadians = Math.Acos(Math.Clamp(DVector3.Dot(sun, toGiant), -1d, 1d));
            double starRadiusRadians = DegreesToRadians(
                PositiveFiniteOr(configuration.StarAngularRadiusDegrees, .265d, 0d));
            rawCoverage = DiscOverlap(starRadiusRadians, giantAngularRadiusRadians, separationRadians);
            eclipseType = ClassifyEclipse(starRadiusRadians, giantAngularRadiusRadians, separationRadians);
        }
        else
        {
            toGiant = -sun;
            giantDistanceKm = PositiveFiniteOr(giant.DistanceKm, GasGiantConfiguration.DefaultDistanceKm, .001d);
            giantAngularRadiusRadians = 0d;
            separationRadians = Math.PI;
            rawCoverage = 0d;
            eclipseType = CelestialEclipseType.None;
        }

        double sunDot = Math.Clamp(DVector3.Dot(up, sun), -1d, 1d);
        double giantDot = Math.Clamp(DVector3.Dot(up, toGiant), -1d, 1d);
        double daylightStartDot = ClampFinite(configuration.DaylightStartDot, -.2d, -1d, 1d);
        double daylightFullDot = ClampFinite(configuration.DaylightFullDot, .35d, -1d, 1d);
        daylightFullDot = Math.Max(daylightStartDot + .0001d, daylightFullDot);
        float daylight = (float)SmoothStep(daylightStartDot, daylightFullDot, sunDot);
        double astronomicalElevation = ClampFinite(
            configuration.AstronomicalTwilightElevationDegrees,
            -18d,
            -89.9d,
            0d);
        double nightFullDot = Math.Sin(DegreesToRadians(astronomicalElevation));
        nightFullDot = Math.Min(nightFullDot, daylightStartDot - .0001d);
        float night = (float)(1d - SmoothStep(nightFullDot, daylightStartDot, sunDot));
        float twilight = Math.Clamp(1f - daylight - night, 0f, 1f);

        double horizonDot = Math.Sin(DegreesToRadians(ClampFinite(
            configuration.HorizonEventElevationDegrees,
            -.833d,
            -89.9d,
            89.9d)));
        double untilSunrise = FindSolarCrossingDuration(
            ringAngle, daySeconds, epochSun, axis, up, horizonDot, rising: true);
        double untilSunset = FindSolarCrossingDuration(
            ringAngle, daySeconds, epochSun, axis, up, horizonDot, rising: false);
        double untilGiantRise = double.PositiveInfinity;
        double untilGiantSet = double.PositiveInfinity;
        if (giant.Enabled && calculateGiantEvents)
        {
            FindGiantCrossingDurations(
                time,
                daySeconds,
                epochSun,
                axis,
                up,
                observer,
                giant,
                out untilGiantRise,
                out untilGiantSet);
        }

        double sunDerivative = DVector3.Dot(up, DVector3.Cross(axis, sun));
        float sunElevation = (float)RadiansToDegrees(Math.Asin(sunDot));
        CelestialLightingPhase lightingPhase = ClassifyLightingPhase(
            sunElevation,
            sunDerivative,
            daylight,
            configuration);
        float gameplayDarkness = (float)Math.Min(
            rawCoverage,
            ClampFinite(configuration.GameplayEclipseDarknessCap, .95d, 0d, 1d));
        float giantPhase = (float)Math.Clamp((1d - DVector3.Dot(toGiant, sun)) * .5d, 0d, 1d);

        return new CelestialFrame(
            time,
            ringAngle,
            orbitAngle,
            seasonAngle,
            sun.ToVector3(),
            toGiant.ToVector3(),
            (float)giantDistanceKm,
            (float)RadiansToDegrees(giantAngularRadiusRadians),
            (float)RadiansToDegrees(separationRadians),
            (float)rawCoverage,
            gameplayDarkness,
            eclipseType,
            sunElevation,
            (float)RadiansToDegrees(Math.Asin(giantDot)),
            daylight,
            twilight,
            night,
            giantPhase,
            lightingPhase,
            sunDot > horizonDot,
            // Giant events use the geometric horizon. Solar events use the
            // configurable apparent/refraction threshold above.
            giantDot > 0d,
            untilSunrise,
            untilSunset,
            untilGiantRise,
            untilGiantSet);
    }

    /// <summary>Compatibility helper for centered-observer grazing-eclipse tests.</summary>
    public static CelestialEclipseSnapshot Evaluate(
        Vector3 toSun,
        Vector3 referenceAxis,
        double simulationSeconds,
        double orbitSeconds,
        float giantRadiusKm,
        float giantDistanceKm,
        float starAngularRadiusRadians,
        float grazingMissRadians)
    {
        DVector3 sun = Normalize(DVector3.From(toSun), DVector3.Forward);
        DVector3 axis = Normalize(DVector3.From(referenceAxis), LeastAlignedAxis(sun));
        double phase = Math.Tau * PositiveModulo(simulationSeconds / Math.Max(1d, orbitSeconds), 1d);
        DVector3 giant = EvaluateOrbitDirection(sun, axis, phase, grazingMissRadians);
        double giantAngularRadius = Math.Asin(Math.Clamp(
            giantRadiusKm / Math.Max(giantDistanceKm, giantRadiusKm + .001f),
            0d,
            .999999999d));
        double separation = Math.Acos(Math.Clamp(DVector3.Dot(sun, giant), -1d, 1d));
        return new CelestialEclipseSnapshot(
            giant.ToVector3(),
            (float)giantAngularRadius,
            (float)DiscOverlap(starAngularRadiusRadians, giantAngularRadius, separation));
    }

    /// <summary>Analytic centered-observer totality duration for an authored orbit.</summary>
    public static double CenteredTotalEclipseDurationSeconds(
        float giantRadiusKm, float giantDistanceKm, float starAngularRadiusDegrees,
        float missDegrees, double orbitSeconds)
    {
        double radius = PositiveFiniteOr(giantRadiusKm, 0d, 0d);
        double distance = PositiveFiniteOr(giantDistanceKm, 0d, 0d);
        double orbit = PositiveFiniteOr(orbitSeconds, 0d, 0d);
        if (radius <= 0d || distance <= radius || orbit <= 0d)
            return 0d;
        double alpha = Math.Asin(Math.Clamp(radius / distance, 0d, .999999999d));
        double totalLimit = alpha - DegreesToRadians(PositiveFiniteOr(starAngularRadiusDegrees, 0d, 0d));
        double cosineMiss = Math.Cos(DegreesToRadians(FiniteOr(missDegrees, 0d)));
        if (totalLimit <= 0d || cosineMiss <= 0d)
            return 0d;
        double ratio = Math.Cos(totalLimit) / cosineMiss;
        if (ratio >= 1d)
            return 0d;
        double halfPhase = Math.Acos(Math.Clamp(ratio, -1d, 1d));
        return orbit * halfPhase / Math.PI;
    }

    public static bool DefaultGrazingSelfTest()
    {
        return DefaultTotalEclipseSelfTest() && PartialEclipseFixtureSelfTest();
    }

    public static bool DefaultTotalEclipseSelfTest()
    {
        double duration = CenteredTotalEclipseDurationSeconds(
            GasGiantConfiguration.DefaultRadiusKm, GasGiantConfiguration.DefaultDistanceKm,
            .265f, GasGiantConfiguration.DefaultGrazingMissDegrees,
            GasGiantConfiguration.DefaultOrbitSeconds);
        CelestialEclipseSnapshot center = Evaluate(
            Vector3.Forward, Vector3.Up, 0d, GasGiantConfiguration.DefaultOrbitSeconds,
            GasGiantConfiguration.DefaultRadiusKm, GasGiantConfiguration.DefaultDistanceKm,
            Mathf.DegToRad(.265f), Mathf.DegToRad(GasGiantConfiguration.DefaultGrazingMissDegrees));
        CelestialEclipseSnapshot inside = Evaluate(
            Vector3.Forward, Vector3.Up, duration / 2d - .1d, GasGiantConfiguration.DefaultOrbitSeconds,
            GasGiantConfiguration.DefaultRadiusKm, GasGiantConfiguration.DefaultDistanceKm,
            Mathf.DegToRad(.265f), Mathf.DegToRad(GasGiantConfiguration.DefaultGrazingMissDegrees));
        CelestialEclipseSnapshot outside = Evaluate(
            Vector3.Forward, Vector3.Up, duration / 2d + .1d, GasGiantConfiguration.DefaultOrbitSeconds,
            GasGiantConfiguration.DefaultRadiusKm, GasGiantConfiguration.DefaultDistanceKm,
            Mathf.DegToRad(.265f), Mathf.DegToRad(GasGiantConfiguration.DefaultGrazingMissDegrees));
        return center.EclipseCoverage > .99999f
            && inside.EclipseCoverage > .99999f
            && outside.EclipseCoverage < 1f
            && Math.Abs(duration - GasGiantConfiguration.DefaultCenteredTotalDurationSeconds) < .1d;
    }

    public static bool PartialEclipseFixtureSelfTest()
    {
        float maximum = 0f;
        // Probe only +/- .0005 degrees around conjunction as well as the exact root:
        // this catches a narrow authored grazing miss without a coarse orbit
        // sampler accidentally hiding a total eclipse.
        for (int i = -1; i <= 1; i++)
        {
            CelestialEclipseSnapshot sample = Evaluate(
                Vector3.Forward,
                Vector3.Up,
                GasGiantConfiguration.DefaultOrbitSeconds * i * .0005d / 360d,
                GasGiantConfiguration.DefaultOrbitSeconds,
                GasGiantConfiguration.DefaultRadiusKm,
                GasGiantConfiguration.DefaultDistanceKm,
                Mathf.DegToRad(.265f),
                Mathf.DegToRad(GasGiantConfiguration.PartialFixtureGrazingMissDegrees));
            if (sample.EclipseCoverage >= 1f)
                return false;
            maximum = MathF.Max(maximum, sample.EclipseCoverage);
        }

        CelestialEclipseSnapshot degenerateAxis = Evaluate(
            Vector3.Right,
            Vector3.Right,
            0d,
            GasGiantConfiguration.DefaultOrbitSeconds,
            GasGiantConfiguration.DefaultRadiusKm,
            GasGiantConfiguration.DefaultDistanceKm,
            Mathf.DegToRad(.265f),
            Mathf.DegToRad(GasGiantConfiguration.PartialFixtureGrazingMissDegrees));
        return degenerateAxis.GiantDirection.IsFinite()
            && degenerateAxis.EclipseCoverage > 0f
            && degenerateAxis.EclipseCoverage < 1f
            && MathF.Abs(maximum - .73367293304f) < .0005f;
    }

    private static DVector3 ConfigureEpochSun(DVector3 authored, DVector3 axis, double tiltDegrees)
    {
        DVector3 source = Normalize(authored, DVector3.Forward);
        double authoredAxisDot = DVector3.Dot(source, axis);
        DVector3 projected = source - axis * authoredAxisDot;
        if (projected.LengthSquared() <= Tiny)
        {
            DVector3 fallback = LeastAlignedAxis(axis);
            projected = fallback - axis * DVector3.Dot(fallback, axis);
        }
        projected = Normalize(projected, DVector3.Forward);
        double tilt = DegreesToRadians(ClampFinite(tiltDegrees, 15d, -89.9d, 89.9d));
        double sign = authoredAxisDot < 0d ? -1d : 1d;
        return Normalize(
            projected * Math.Cos(tilt) + axis * (sign * Math.Sin(tilt)),
            projected);
    }

    private static DVector3 EvaluateOrbitDirection(
        DVector3 epochSun,
        DVector3 referenceAxis,
        double orbitalAngle,
        double signedMissRadians)
    {
        DVector3 sun = Normalize(epochSun, DVector3.Forward);
        DVector3 fallback = LeastAlignedAxis(sun);
        DVector3 reference = Normalize(referenceAxis, fallback);
        if (Math.Abs(DVector3.Dot(reference, sun)) > .98d)
            reference = fallback;

        DVector3 perpendicular = Normalize(
            reference - sun * DVector3.Dot(reference, sun),
            fallback);
        double miss = Math.Clamp(
            signedMissRadians,
            -Math.PI * .5d + .000001d,
            Math.PI * .5d - .000001d);
        DVector3 normal = Normalize(
            sun * Math.Sin(miss) + perpendicular * Math.Cos(miss),
            perpendicular);
        DVector3 closest = Normalize(sun - normal * DVector3.Dot(normal, sun), sun);
        DVector3 tangent = Normalize(DVector3.Cross(normal, closest), perpendicular);
        return Normalize(
            closest * Math.Cos(orbitalAngle) + tangent * Math.Sin(orbitalAngle),
            closest);
    }

    private static double FindSolarCrossingDuration(
        double currentRingAngle,
        double period,
        DVector3 epochSun,
        DVector3 axis,
        DVector3 up,
        double targetDot,
        bool rising)
    {
        double parallelAmount = DVector3.Dot(epochSun, axis);
        DVector3 parallel = axis * parallelAmount;
        DVector3 perpendicular = epochSun - parallel;
        DVector3 quarterTurn = DVector3.Cross(axis, perpendicular);
        double a = DVector3.Dot(up, perpendicular);
        double b = DVector3.Dot(up, quarterTurn);
        double c = DVector3.Dot(up, parallel);
        double amplitude = Math.Sqrt(a * a + b * b);
        if (amplitude <= Tiny)
            return double.PositiveInfinity;

        double ratio = (targetDot - c) / amplitude;
        if (ratio < -1d - 1e-10d || ratio > 1d + 1e-10d)
            return double.PositiveInfinity;
        ratio = Math.Clamp(ratio, -1d, 1d);
        double phase = Math.Atan2(b, a);
        double offset = Math.Acos(ratio);
        double rootA = phase + offset;
        double rootB = phase - offset;
        double derivativeA = -a * Math.Sin(rootA) + b * Math.Cos(rootA);
        double derivativeB = -a * Math.Sin(rootB) + b * Math.Cos(rootB);
        double root;
        if (rising)
        {
            if (derivativeA > Tiny) root = rootA;
            else if (derivativeB > Tiny) root = rootB;
            else return double.PositiveInfinity;
        }
        else
        {
            if (derivativeA < -Tiny) root = rootA;
            else if (derivativeB < -Tiny) root = rootB;
            else return double.PositiveInfinity;
        }

        double deltaAngle = PositiveModulo(root - currentRingAngle, Math.Tau);
        return deltaAngle / Math.Tau * period;
    }

    private static void FindGiantCrossingDurations(
        double startTime,
        double ringDaySeconds,
        DVector3 epochSun,
        DVector3 axis,
        DVector3 up,
        DVector3 observer,
        GasGiantConfiguration giant,
        out double riseDuration,
        out double setDuration)
    {
        riseDuration = double.PositiveInfinity;
        setDuration = double.PositiveInfinity;
        double scanDuration = ringDaySeconds * 1.5d;
        double orbitSeconds = PositiveFiniteOr(giant.OrbitSeconds, GasGiantConfiguration.DefaultOrbitSeconds, 1d);
        double targetInterval = Math.Min(
            ringDaySeconds / GiantEventScanSteps,
            orbitSeconds / GiantOrbitSamplesPerCycle);
        double seasonSeconds = PositiveFiniteOr(giant.SeasonSeconds, 0d, 0d);
        if (seasonSeconds > 0d && FiniteOr(giant.SeasonMissAmplitudeDegrees, 0d) != 0d)
            targetInterval = Math.Min(targetInterval, seasonSeconds / GiantOrbitSamplesPerCycle);
        int scanSteps = Math.Clamp(
            (int)Math.Ceiling(scanDuration / Math.Max(.001d, targetInterval)),
            GiantEventScanSteps,
            MaxGiantEventScanSteps);
        double previousTime = startTime;
        double previous = GiantHorizonDotAt(startTime, ringDaySeconds, epochSun, axis, up, observer, giant);

        for (int i = 1; i <= scanSteps; i++)
        {
            double currentTime = startTime + scanDuration * i / scanSteps;
            double current = GiantHorizonDotAt(currentTime, ringDaySeconds, epochSun, axis, up, observer, giant);
            if (!double.IsFinite(riseDuration) && previous <= 0d && current > 0d)
            {
                riseDuration = BisectGiantCrossing(
                    startTime, previousTime, currentTime, ringDaySeconds,
                    epochSun, axis, up, observer, giant, rising: true);
            }
            if (!double.IsFinite(setDuration) && previous >= 0d && current < 0d)
            {
                setDuration = BisectGiantCrossing(
                    startTime, previousTime, currentTime, ringDaySeconds,
                    epochSun, axis, up, observer, giant, rising: false);
            }
            if (double.IsFinite(riseDuration) && double.IsFinite(setDuration))
                return;
            previousTime = currentTime;
            previous = current;
        }
    }

    private static double BisectGiantCrossing(
        double startTime,
        double lower,
        double upper,
        double ringDaySeconds,
        DVector3 epochSun,
        DVector3 axis,
        DVector3 up,
        DVector3 observer,
        GasGiantConfiguration giant,
        bool rising)
    {
        for (int i = 0; i < GiantEventBisectionSteps; i++)
        {
            double middle = (lower + upper) * .5d;
            double value = GiantHorizonDotAt(
                middle, ringDaySeconds, epochSun, axis, up, observer, giant);
            if (rising ? value >= 0d : value < 0d)
                upper = middle;
            else
                lower = middle;
        }
        return Math.Max(0d, (lower + upper) * .5d - startTime);
    }

    private static double GiantHorizonDotAt(
        double time,
        double ringDaySeconds,
        DVector3 epochSun,
        DVector3 axis,
        DVector3 up,
        DVector3 observer,
        GasGiantConfiguration giant)
    {
        double seasonAngle = SeasonAngleAt(time, giant);
        DVector3 inertial = EvaluateOrbitDirection(
            epochSun,
            axis,
            OrbitAngleAt(time, giant),
            SeasonalMissRadians(giant, seasonAngle));
        DVector3 centerDirection = Rotate(inertial, axis, RingAngleAt(time, ringDaySeconds));
        double radius = PositiveFiniteOr(giant.RadiusKm, GasGiantConfiguration.DefaultRadiusKm, 0d);
        double distance = Math.Max(
            radius + .001d,
            PositiveFiniteOr(giant.DistanceKm, GasGiantConfiguration.DefaultDistanceKm, .001d));
        DVector3 relative = centerDirection * distance - observer;
        return DVector3.Dot(up, Normalize(relative, centerDirection));
    }

    private static CelestialLightingPhase ClassifyLightingPhase(
        float elevationDegrees,
        double sunDotDerivative,
        float daylight,
        CelestialConfiguration configuration)
    {
        bool rising = sunDotDerivative >= 0d;
        float astronomical = (float)Math.Min(-.1d, ClampFinite(
            configuration.AstronomicalTwilightElevationDegrees,
            -18d,
            -89.9d,
            0d));
        float band = (float)PositiveFiniteOr(configuration.SunriseBandDegrees, 1.5d, .1d);
        float dawnBoundary = Math.Min(-band, Mathf.RadToDeg(Mathf.Asin(Math.Clamp(
            (float)ClampFinite(configuration.DaylightStartDot, -.2d, -1d, 1d),
            -1f,
            1f))));
        if (elevationDegrees < astronomical)
            return CelestialLightingPhase.Night;
        if (elevationDegrees < dawnBoundary)
            return CelestialLightingPhase.AstronomicalTwilight;
        if (elevationDegrees < -band)
            return rising ? CelestialLightingPhase.Dawn : CelestialLightingPhase.Dusk;
        if (elevationDegrees <= band)
            return rising ? CelestialLightingPhase.Sunrise : CelestialLightingPhase.Sunset;
        if (daylight > .85f && Math.Abs(sunDotDerivative) < .15d)
            return CelestialLightingPhase.Midday;
        return rising ? CelestialLightingPhase.Morning : CelestialLightingPhase.Afternoon;
    }

    private static CelestialEclipseType ClassifyEclipse(
        double starRadius,
        double giantRadius,
        double separation)
    {
        if (starRadius <= 0d || giantRadius <= 0d || separation >= starRadius + giantRadius)
            return CelestialEclipseType.None;
        if (giantRadius >= separation + starRadius)
            return CelestialEclipseType.Total;
        return CelestialEclipseType.Partial;
    }

    private static double RingAngleAt(double time, double period) =>
        Math.Tau * PositiveModulo(
            FiniteOr(time, 0d) / PositiveFiniteOr(period, CelestialConfiguration.DefaultRingDaySeconds, 1d),
            1d);

    private static double OrbitAngleAt(double time, GasGiantConfiguration giant) =>
        Math.Tau * PositiveModulo(
            FiniteOr(time, 0d) / PositiveFiniteOr(giant.OrbitSeconds, GasGiantConfiguration.DefaultOrbitSeconds, 1d)
                + FiniteOr(giant.InitialPhaseDegrees, 180d) / 360d,
            1d);

    private static double SeasonAngleAt(double time, GasGiantConfiguration giant)
    {
        double seasonSeconds = PositiveFiniteOr(giant.SeasonSeconds, 0d, 0d);
        return seasonSeconds > 0d
            ? Math.Tau * PositiveModulo(
                FiniteOr(time, 0d) / seasonSeconds
                    + FiniteOr(giant.SeasonPhaseDegrees, 0d) / 360d,
                1d)
            : 0d;
    }

    private static double SeasonalMissRadians(GasGiantConfiguration giant, double seasonAngle)
    {
        double missDegrees = FiniteOr(giant.GrazingMissDegrees, GasGiantConfiguration.DefaultGrazingMissDegrees);
        double amplitude = FiniteOr(giant.SeasonMissAmplitudeDegrees, 0d);
        double seasonSeconds = PositiveFiniteOr(giant.SeasonSeconds, 0d, 0d);
        if (amplitude != 0d && seasonSeconds > 0d)
            missDegrees += amplitude * Math.Sin(FiniteOr(seasonAngle, 0d));
        return DegreesToRadians(missDegrees);
    }

    private static DVector3 Rotate(DVector3 vector, DVector3 unitAxis, double angle)
    {
        double cosine = Math.Cos(angle);
        double sine = Math.Sin(angle);
        return vector * cosine
            + DVector3.Cross(unitAxis, vector) * sine
            + unitAxis * (DVector3.Dot(unitAxis, vector) * (1d - cosine));
    }

    private static DVector3 LeastAlignedAxis(DVector3 direction)
    {
        double x = Math.Abs(direction.X);
        double y = Math.Abs(direction.Y);
        double z = Math.Abs(direction.Z);
        if (x <= y && x <= z) return DVector3.Right;
        return y <= z ? DVector3.Up : DVector3.Back;
    }

    private static DVector3 Normalize(DVector3 value, DVector3 fallback)
    {
        double length = value.Length();
        if (double.IsFinite(length) && length > Tiny)
            return value / length;
        double fallbackLength = fallback.Length();
        return fallbackLength > Tiny ? fallback / fallbackLength : DVector3.Forward;
    }

    private static double SmoothStep(double from, double to, double value)
    {
        if (to <= from + Tiny)
            return value >= to ? 1d : 0d;
        double amount = Math.Clamp((value - from) / (to - from), 0d, 1d);
        return amount * amount * (3d - 2d * amount);
    }

    private static double PositiveModulo(double value, double modulus)
    {
        if (!double.IsFinite(value) || !double.IsFinite(modulus) || modulus <= 0d)
            return 0d;
        double result = value % modulus;
        return result < 0d ? result + modulus : result;
    }

    private static double FiniteOr(double value, double fallback) =>
        double.IsFinite(value) ? value : fallback;

    private static double PositiveFiniteOr(double value, double fallback, double minimum)
    {
        double finite = double.IsFinite(value) ? value : fallback;
        return Math.Max(minimum, finite);
    }

    private static double ClampFinite(double value, double fallback, double minimum, double maximum) =>
        Math.Clamp(FiniteOr(value, fallback), minimum, maximum);

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180d;
    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;

    private static double DiscOverlap(double star, double giant, double distance)
    {
        if (star <= 0d || giant <= 0d)
            return 0d;
        if (distance >= star + giant)
            return 0d;
        if (giant >= distance + star)
            return 1d;
        if (star >= distance + giant)
            return Math.Clamp((giant * giant) / (star * star), 0d, 1d);

        double d = Math.Max(distance, 1e-12d);
        double a = Math.Acos(Math.Clamp(
            (d * d + star * star - giant * giant) / (2d * d * star),
            -1d,
            1d));
        double b = Math.Acos(Math.Clamp(
            (d * d + giant * giant - star * star) / (2d * d * giant),
            -1d,
            1d));
        double radicand = (-d + star + giant)
            * (d + star - giant)
            * (d - star + giant)
            * (d + star + giant);
        double lens = star * star * a
            + giant * giant * b
            - .5d * Math.Sqrt(Math.Max(0d, radicand));
        return Math.Clamp(lens / (Math.PI * star * star), 0d, 1d);
    }

    private readonly struct DVector3
    {
        public static readonly DVector3 Right = new(1d, 0d, 0d);
        public static readonly DVector3 Up = new(0d, 1d, 0d);
        public static readonly DVector3 Back = new(0d, 0d, 1d);
        public static readonly DVector3 Forward = new(0d, 0d, -1d);

        public readonly double X;
        public readonly double Y;
        public readonly double Z;

        public DVector3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public static DVector3 From(Vector3 value) => new(value.X, value.Y, value.Z);
        public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
        public double LengthSquared() => X * X + Y * Y + Z * Z;
        public double Length() => Math.Sqrt(LengthSquared());

        public static double Dot(DVector3 a, DVector3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        public static DVector3 Cross(DVector3 a, DVector3 b) => new(
            a.Y * b.Z - a.Z * b.Y,
            a.Z * b.X - a.X * b.Z,
            a.X * b.Y - a.Y * b.X);

        public static DVector3 operator +(DVector3 a, DVector3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static DVector3 operator -(DVector3 a, DVector3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static DVector3 operator -(DVector3 value) => new(-value.X, -value.Y, -value.Z);
        public static DVector3 operator *(DVector3 value, double scalar) => new(value.X * scalar, value.Y * scalar, value.Z * scalar);
        public static DVector3 operator /(DVector3 value, double scalar) => new(value.X / scalar, value.Y / scalar, value.Z / scalar);
    }
}
