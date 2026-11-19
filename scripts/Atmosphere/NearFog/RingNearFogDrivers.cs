using System;

namespace RingworldTechDemo.Atmosphere.NearFog;

/// <summary>The inputs of the near ground fog drivers at the camera (docs/sun-flare-shafts-near-fog-plan.md, NF-1 design N3). Plain values, so <see cref="RingNearFogDrivers.Evaluate"/> stays pure.</summary>
public readonly struct NearFogInputs
{
    /// <summary>Climate moisture m, 0 to 1 (TerrainSample.Moisture).</summary>
    public float Moisture { get; init; }
    /// <summary>Climate temperature t, 0 to 1 (TerrainSample.Temperature).</summary>
    public float Temperature { get; init; }
    /// <summary>Wet map soak at the camera, 0 to 1 (RingWetWorld.CameraWetness.Soak).</summary>
    public float Soak { get; init; }
    /// <summary>Instantaneous rain at the camera, 0 to 1 (RingWetWorld.CameraRainNow).</summary>
    public float RainNow { get; init; }
    /// <summary>Shared weather wind speed, m/s.</summary>
    public float WindMetersPerSecond { get; init; }
    /// <summary>Sunshine cloud coverage at the camera, 0 to 1.</summary>
    public float Coverage { get; init; }
    /// <summary>Sun elevation above the local horizon, degrees (CelestialFrame.SunElevationDegrees).</summary>
    public float SunElevationDegrees { get; init; }
    /// <summary>Seconds until the sun next crosses the horizon event elevation upward (CelestialFrame.SecondsUntilSunrise); infinite or NaN when it never does.</summary>
    public double SecondsUntilSunrise { get; init; }
    /// <summary>Seconds until the sun next crosses the horizon event elevation downward (CelestialFrame.SecondsUntilSunset).</summary>
    public double SecondsUntilSunset { get; init; }
    /// <summary>One full ring day, seconds (CelestialConfiguration.RingDaySeconds; RingDayNightCycle.FullCycleSeconds). Both next-event times are in [0, DaySeconds).</summary>
    public double DaySeconds { get; init; }
}

/// <summary>The three driver values of the near ground fog, each 0 to 1.</summary>
public readonly struct NearFogDrive
{
    public readonly float Radiation;
    public readonly float Mist;
    public readonly float Steam;
    /// <summary>The diurnal factor f_rad_time (kept for tests and diagnostics).</summary>
    public readonly float RadiationTime;

    public NearFogDrive(float radiation, float mist, float steam, float radiationTime)
    {
        Radiation = radiation;
        Mist = mist;
        Steam = steam;
        RadiationTime = radiationTime;
    }

    public float Max => MathF.Max(Radiation, MathF.Max(Mist, Steam));
}

/// <summary>
/// N3 drivers D = (D_rad, D_mist, D_steam) of the near ground fog: a pure, allocation-free function of the camera's weather and location inputs.
/// Stateless: a time skip or a save and load needs no history, because the diurnal factor is derived from the next sunrise and sunset times alone.
/// </summary>
public static class RingNearFogDrivers
{
    // Humidity: the soak of the wet map counts as moisture, h = 1 - (1 - m)(1 - SoakHumidityWeight soak).
    public const float SoakHumidityWeight = 0.8f;
    // Cool factor f_T = 1 - smoothstep(...) of temperature: warm climates get no radiation fog.
    public const float CoolStart = 0.7f, CoolEnd = 0.95f;
    // Clear night factor f_clr = 1 - CoverageClearPenalty coverage: cloud blankets the ground and stops the radiative cooling.
    public const float CoverageClearPenalty = 0.8f;
    // Wind cut-offs, m/s: factor = 1 - smoothstep(start, end, wind) per kind.
    public const float RadiationWindStart = 2f, RadiationWindEnd = 5f;
    public const float MistWindStart = 4f, MistWindEnd = 9f;
    public const float SteamWindStart = 3f, SteamWindEnd = 8f;
    // Humidity ramps: radiation smoothstep(0.45, 0.8, h), mist smoothstep(0.65, 0.9, h).
    public const float RadiationHumidityStart = 0.45f, RadiationHumidityEnd = 0.8f;
    public const float MistHumidityStart = 0.65f, MistHumidityEnd = 0.9f;
    // Diurnal, night: f_rad_time = smoothstep(NightRiseStart, NightRiseEnd, p), p = time since sunset / night length (0 at sunset, 1 at sunrise).
    public const float NightRiseStart = 0.1f, NightRiseEnd = 0.7f;
    // Diurnal, morning: f_rad_time = 1 - smoothstep(MorningBurnStartDegrees, MorningBurnEndDegrees, sun elevation) until local noon, then 0 until sunset.
    public const float MorningBurnStartDegrees = 3f, MorningBurnEndDegrees = 20f;
    // Mist keeps a floor all day: D_mist uses (MistTimeFloor + (1 - MistTimeFloor) f_rad_time).
    public const float MistTimeFloor = 0.3f;
    // Rain: radiation fog is washed out by rain now; mist loses MistRainPenalty of itself at full rain.
    public const float MistRainPenalty = 0.5f;
    // Fallback night progress when the day cannot be derived (polar sun that never crosses the horizon event, or an invalid day length).
    public const float UnknownNightProgress = 0.5f;

    public static NearFogDrive Evaluate(in NearFogInputs input)
    {
        float m = Unit(input.Moisture);
        float t = Unit(input.Temperature);
        float soak = Unit(input.Soak);
        float rain = Unit(input.RainNow);
        float coverage = Unit(input.Coverage);
        float wind = float.IsFinite(input.WindMetersPerSecond) ? MathF.Max(0f, input.WindMetersPerSecond) : 0f;

        float h = 1f - (1f - m) * (1f - SoakHumidityWeight * soak);
        float coolFactor = 1f - Smoothstep(CoolStart, CoolEnd, t);
        float clearFactor = 1f - CoverageClearPenalty * coverage;
        float windRadiation = 1f - Smoothstep(RadiationWindStart, RadiationWindEnd, wind);
        float windMist = 1f - Smoothstep(MistWindStart, MistWindEnd, wind);
        float windSteam = 1f - Smoothstep(SteamWindStart, SteamWindEnd, wind);
        float time = RadiationTime(input);

        float radiation = Smoothstep(RadiationHumidityStart, RadiationHumidityEnd, h) * coolFactor * clearFactor * time * windRadiation * (1f - rain);
        float mist = Smoothstep(MistHumidityStart, MistHumidityEnd, h) * (MistTimeFloor + (1f - MistTimeFloor) * time) * windMist * (1f - MistRainPenalty * rain);
        float steam = time * (1f - t) * windSteam;
        return new NearFogDrive(radiation, mist, steam, time);
    }

    /// <summary>f_rad_time: 0 to 1 from the next sunrise and sunset times alone. Night (sunrise comes before sunset): rises over the night, 1 at sunrise. Day: burns off with the sun's elevation until local noon, then 0 until sunset.</summary>
    public static float RadiationTime(in NearFogInputs input)
    {
        double untilRise = input.SecondsUntilSunrise, untilSet = input.SecondsUntilSunset, period = input.DaySeconds;
        bool riseKnown = double.IsFinite(untilRise) && untilRise >= 0.0;
        bool setKnown = double.IsFinite(untilSet) && untilSet >= 0.0;
        bool periodKnown = double.IsFinite(period) && period > 0.0;
        // Night when the sunrise is the next horizon event; with an unknown event the elevation decides.
        bool night = riseKnown && setKnown ? untilRise < untilSet : input.SunElevationDegrees < 0f;
        if (night)
        {
            float p = UnknownNightProgress;
            if (riseKnown && setKnown && periodKnown)
            {
                double nightLength = period - (untilSet - untilRise); // untilSet - untilRise is the coming day
                if (nightLength > 1e-3) p = (float)Math.Clamp((nightLength - untilRise) / nightLength, 0.0, 1.0);
            }
            return Smoothstep(NightRiseStart, NightRiseEnd, p);
        }
        if (!(riseKnown && setKnown && periodKnown)) return 0f;
        // Day: untilRise - untilSet is the coming night, so the day length is the rest of the period and noon is its middle.
        double dayLength = period - (untilRise - untilSet);
        if (dayLength <= 1e-3) return 0f;
        bool morning = untilSet > dayLength * 0.5;
        return morning ? 1f - Smoothstep(MorningBurnStartDegrees, MorningBurnEndDegrees, input.SunElevationDegrees) : 0f;
    }

    public static float Smoothstep(float edge0, float edge1, float x)
    {
        float u = Unit((x - edge0) / (edge1 - edge0));
        return u * u * (3f - 2f * u);
    }

    private static float Unit(float x) => x > 0f ? (x < 1f ? x : 1f) : 0f; // NaN gives 0
}

/// <summary>Exponential smoothing of the drivers (N3: tau 20 s). Feed it the 2 Hz evaluation; <see cref="Reset"/> snaps for teleports, tests and the first evaluation.</summary>
public sealed class NearFogDriverSmoother
{
    public const float TauSeconds = 20f;

    private float _radiation, _mist, _steam;
    private bool _primed;

    public float Radiation => _radiation;
    public float Mist => _mist;
    public float Steam => _steam;
    public bool Primed => _primed;
    public float Max => MathF.Max(_radiation, MathF.Max(_mist, _steam));

    public void Reset(in NearFogDrive target)
    {
        _radiation = target.Radiation;
        _mist = target.Mist;
        _steam = target.Steam;
        _primed = true;
    }

    public void Clear()
    {
        _radiation = _mist = _steam = 0f;
        _primed = false;
    }

    /// <summary>Moves toward <paramref name="target"/> by 1 - exp(-dt / tau); the first call after construction or <see cref="Clear"/> snaps.</summary>
    public void Update(float deltaSeconds, in NearFogDrive target)
    {
        if (!_primed) { Reset(target); return; }
        if (!(deltaSeconds > 0f)) return;
        float a = 1f - MathF.Exp(-deltaSeconds / TauSeconds);
        _radiation += (target.Radiation - _radiation) * a;
        _mist += (target.Mist - _mist) * a;
        _steam += (target.Steam - _steam) * a;
    }
}
