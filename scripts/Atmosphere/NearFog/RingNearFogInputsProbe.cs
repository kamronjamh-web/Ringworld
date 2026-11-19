using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Atmosphere.NearFog;

/// <summary>
/// Gathers <see cref="NearFogInputs"/> from the live scene for one camera position (NF-1 design N3). Main thread, read-only, no allocation per call.
/// Sources:
/// climate moisture and temperature = <see cref="TerrainSample.Moisture"/> / <see cref="TerrainSample.Temperature"/> from <c>VoxelChunkGridManager.TerrainSampler</c> (the access path RingLocalWeatherEffects.ResolveSnowing uses),
/// re-sampled only when the camera enters another 256 m cell (climate is smooth at that scale);
/// soak and rain-now = <see cref="RingWetWorld.CameraWetness"/>.Soak and <see cref="RingWetWorld.CameraRainNow"/> (the wet world's CPU mirror of the map at the camera texel, shelter applied; not a GPU read-back);
/// wind = <see cref="RingLocalWeatherEffects.ResolvedWindMetersPerSecond"/> length, falling back to <see cref="RingVolumetricCloudLayer.WeatherWindMetersPerSecond"/> exactly as the wet world's wind publication does;
/// coverage = <see cref="RingSunshineCloudBridge.CurrentWeather"/>.CloudCoverage (the bridge's camera-local weather, refreshed by the bridge itself);
/// sun = <see cref="RingDayNightCycle.CurrentFrame"/> and <see cref="RingDayNightCycle.FullCycleSeconds"/>.
/// Any missing source reads as its neutral value (dry, calm, clear, daytime) so a partial scene gives no fog.
/// </summary>
public sealed class RingNearFogInputsProbe
{
    /// <summary>Camera climate cell, metres: the climate is re-sampled when the camera crosses one of these cells.</summary>
    public const int ClimateCellMeters = 256;

    public VoxelChunkGridManager? Grid { get; set; }
    public RingLocalWeatherEffects? Effects { get; set; }
    public RingVolumetricCloudLayer? Weather { get; set; }
    public RingSunshineCloudBridge? Bridge { get; set; }
    public RingDayNightCycle? DayNight { get; set; }
    /// <summary>Null means <see cref="RingWetWorld.Instance"/>.</summary>
    public RingWetWorld? WetWorld { get; set; }

    private long _cellAround = long.MinValue;
    private int _cellAcross = int.MinValue;
    private float _moisture, _temperature;
    private object? _cellSampler;

    /// <summary>Forgets the cached climate cell (teleports and tests).</summary>
    public void Reset()
    {
        _cellAround = long.MinValue;
        _cellAcross = int.MinValue;
        _cellSampler = null;
    }

    public NearFogInputs Gather(in RingCanonicalPosition camera)
    {
        ResolveClimate(camera);
        RingWetWorld? wet = WetWorld ?? RingWetWorld.Instance;
        bool wetValid = wet != null && GodotObject.IsInstanceValid(wet);
        float soak = wetValid ? wet!.CameraWetness.Soak : 0f;
        float rain = wetValid ? wet!.CameraRainNow : 0f;

        float wind = 0f;
        if (Effects != null && GodotObject.IsInstanceValid(Effects))
        {
            Vector2 resolved = Effects.ResolvedWindMetersPerSecond;
            if (resolved.LengthSquared() > 1e-4f) wind = resolved.Length();
            else if (Weather != null && GodotObject.IsInstanceValid(Weather)) wind = Weather.WeatherWindMetersPerSecond.Length();
        }
        else if (Weather != null && GodotObject.IsInstanceValid(Weather)) wind = Weather.WeatherWindMetersPerSecond.Length();

        float coverage = Bridge != null && GodotObject.IsInstanceValid(Bridge) ? Bridge.CurrentWeather.CloudCoverage : 0f;

        float elevation = 90f;
        double untilRise = double.PositiveInfinity, untilSet = double.PositiveInfinity, period = 0.0;
        if (DayNight != null && GodotObject.IsInstanceValid(DayNight))
        {
            CelestialFrame frame = DayNight.CurrentFrame;
            elevation = frame.SunElevationDegrees;
            untilRise = frame.SecondsUntilSunrise;
            untilSet = frame.SecondsUntilSunset;
            period = Math.Max(1f, DayNight.FullCycleSeconds);
        }

        return new NearFogInputs
        {
            Moisture = _moisture,
            Temperature = _temperature,
            Soak = soak,
            RainNow = rain,
            WindMetersPerSecond = wind,
            Coverage = coverage,
            SunElevationDegrees = elevation,
            SecondsUntilSunrise = untilRise,
            SecondsUntilSunset = untilSet,
            DaySeconds = period,
        };
    }

    private void ResolveClimate(in RingCanonicalPosition camera)
    {
        RingTerrainSampler? sampler = Grid != null && GodotObject.IsInstanceValid(Grid) ? Grid.TerrainSampler : null;
        if (sampler == null)
        {
            _moisture = 0f; // a dry climate gives no fog
            _temperature = 0.5f;
            _cellSampler = null;
            return;
        }
        long around = (long)Math.Round(camera.AroundMeters);
        int across = Mathf.Clamp(Mathf.RoundToInt((float)camera.AcrossMeters), 0, Mathf.Max(0, Mathf.RoundToInt(Grid!.RingAcrossWidthMeters)));
        long cellAround = (long)Math.Floor(around / (double)ClimateCellMeters);
        int cellAcross = Mathf.FloorToInt(across / (float)ClimateCellMeters);
        if (cellAround == _cellAround && cellAcross == _cellAcross && ReferenceEquals(sampler, _cellSampler)) return;
        TerrainSample sample = sampler.GetTerrainSample(around, across);
        _moisture = sample.Moisture;
        _temperature = sample.Temperature;
        _cellAround = cellAround;
        _cellAcross = cellAcross;
        _cellSampler = sampler;
    }
}
