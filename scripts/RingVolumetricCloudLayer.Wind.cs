using System;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

public partial class RingVolumetricCloudLayer
{
    [Export] public string WindPreferencesPath { get; set; } = "";
    [Export] public string LegacyGrassPreferencesPath { get; set; } = "";
    // Guards the wind partial's shared mutable state (query cache and signature, manual history, the gust atlas seed) so physics-thread
    // RingWindQuery callers are safe against main-thread wind queries. Held only for short sections, never across a weather evaluation.
    private readonly object _windStateLock = new();
    private readonly System.Collections.Generic.List<(string Owner, LocalWeatherState State, double Start, double End)> _manualWindHistory = new();
    private void RecordManualWind(string owner, LocalWeatherState? state)
    {
        double now = CloudSimulationTimeSeconds;
        lock (_windStateLock)
        {
            for (int i = 0; i < _manualWindHistory.Count; i++) if (_manualWindHistory[i].Owner == owner && double.IsPositiveInfinity(_manualWindHistory[i].End))
                {
                    var old = _manualWindHistory[i];
                    _manualWindHistory[i] = (old.Owner, old.State, old.Start, now);
                }
            if (state.HasValue) _manualWindHistory.Add((owner, state.Value, now, double.PositiveInfinity));
            if (_manualWindHistory.Count > 128) _manualWindHistory.RemoveRange(0, _manualWindHistory.Count - 128);
        }
    }
    private (string Owner, LocalWeatherState State, double Start, double End)[] ManualWindHistorySnapshot()
    {
        lock (_windStateLock) return _manualWindHistory.ToArray();
    }
    private System.Collections.Generic.IEnumerable<LocalWeatherState> ManualWindStatesAt(double time)
    {
        foreach (var item in ManualWindHistorySnapshot()) if (time >= item.Start && time < item.End) yield return item.State;
    }
    private int _windQuerySignature;
    private readonly System.Collections.Generic.Dictionary<(RingCanonicalPosition, double), LocalWeatherState> _windQueryCache = new();
    /// <summary>Wind-free variant of <see cref="QueryLocalWeatherAtTime"/>: every field except WindMetersPerSecond is identical (the wind path only overrides that field), without the wind-history quadrature or cache.</summary>
    public LocalWeatherState QueryLocalWeatherCoverageAtTime(RingCanonicalPosition position, double time, bool includeMegastorm = true)
        => QueryRawWeatherCore(position, double.IsFinite(time) ? time : 0, true, false, includeMegastorm);
    public LocalWeatherState QueryLocalWeatherAtTime(RingCanonicalPosition position, double time)
    {
        if (!double.IsFinite(time)) time = 0;
        int signature = HashCode.Combine(_grid?.TerrainSampler?.Seed ?? 1337, HashCode.Combine(WeatherWindMetersPerSecond, WindSettings.BaseWindMultiplier, WindSettings.StormWindSpeed, WindSettings.StormDirectionDegrees, WindSettings.StormRiseSeconds, WindSettings.StormDecaySeconds, WindSettings.Enabled, ManualWeatherRevision), NaturalWeatherLifecyclesEnabled,
            HashCode.Combine(RingStormArrivalSettings.Current.GustFrontStrength, RingStormArrivalSettings.Current.GustFrontLeadSeconds));
        var key = (position, time);
        lock (_windStateLock)
        {
            if (signature != _windQuerySignature)
            {
                _windQuerySignature = signature;
                _windQueryCache.Clear();
            }
            if (_windQueryCache.TryGetValue(key, out var cached)) return cached;
        }
        var current = QueryRawWeatherAtTime(position, time, true);
        if (!current.InheritWind) return current;
        var natural = QueryRawWeatherAtTime(position, time, false, true);
        var target = ResolveSurfaceWind(natural, time);
        // Finite history quadrature: deterministic at arbitrary positions and capture times,
        // including rewind. Eight exponentially weighted samples bound work and state.
        float currentFactor = StormFactor(natural);
        float envelopeBlend = currentFactor * currentFactor * (3 - 2 * currentFactor);
        double tau = Math.Max(.1, Mathf.Lerp(WindSettings.StormDecaySeconds, WindSettings.StormRiseSeconds, envelopeBlend));
        Vector2 mean = Vector2.Zero;
        float total = 0;
        for (int i = 0; i < 8; i++)
        {
            double lag = tau * i * .5;
            float weight = (float)Math.Exp(-lag / tau);
            var past = i == 0 ? target : ResolveSurfaceWind(QueryRawWeatherAtTime(position, time - lag, false, true), time - lag);
            mean += past.WindMetersPerSecond * weight;
            total += weight;
        }
        Vector2 resolved = ResolveManualEnvelope(position, natural, mean / total, time);
        // Storm arrival (B1): applied once, after the history smoothing, so the front edge stays sharp and every consumer agrees. Strength 0 skips it entirely.
        if (current.SystemId == natural.SystemId) resolved = ApplyStormArrivalWind(position, time, resolved, out _, out _);
        var result = current with { WindMetersPerSecond = resolved };
        lock (_windStateLock)
        {
            if (signature == _windQuerySignature)
            {
                if (_windQueryCache.Count >= 128) _windQueryCache.Clear();
                _windQueryCache[key] = result;
            }
        }
        return result;
    }
    /// <summary>The storm-arrival wind modifier at a point and time; also returns the gust-field multipliers (1 and 0 when inactive).</summary>
    private Vector2 ApplyStormArrivalWind(RingCanonicalPosition position, double time, Vector2 mean, out float gustStrengthScale, out float gustBaseAdd)
    {
        gustStrengthScale = 1f; gustBaseAdd = 0f;
        float strength = RingStormArrivalSettings.Current.GustFrontStrength;
        if (!(strength > 0f) || !WindSettings.Enabled) return mean;
        var surface = new RingCanonicalPosition(position.AroundMeters, 0.0, position.AcrossMeters);
        StormArrivalState arrival = QueryStormArrivalCached(surface, time);
        if (!arrival.Valid) return mean;
        Vector2 baseWind = WeatherWindMetersPerSecond * WindSettings.BaseWindMultiplier;
        if (!baseWind.IsFinite()) baseWind = Vector2.Zero;
        var modified = StormArrivalMath.ModifyWind(arrival, mean, baseWind, WindSettings.StormWindSpeed, strength);
        gustStrengthScale = modified.GustStrengthScale; gustBaseAdd = modified.GustBaseAdd;
        return modified.Mean;
    }
    /// <summary>Gust multipliers (strength scale, base add) of the observer's last wind snapshot, for the GPU gust publication.</summary>
    public (float StrengthScale, float BaseAdd) LastArrivalGustModifiers { get; private set; } = (1f, 0f);
    private Vector2 ResolveManualEnvelope(RingCanonicalPosition position, LocalWeatherState natural, Vector2 baseline, double time)
    {
        var events = new System.Collections.Generic.SortedSet<double>();
        var history = ManualWindHistorySnapshot();
        foreach (var item in history)
        {
            if (item.Start > time) continue;
            events.Add(item.Start);
            if (double.IsFinite(item.End) && item.End <= time) events.Add(item.End);
        }
        if (events.Count == 0) return baseline;
        events.Add(time);
        Vector2 mean = baseline;
        double? previous = null;
        foreach (double stamp in events)
        {
            if (previous.HasValue && stamp > previous.Value)
            {
                double midpoint = (previous.Value + stamp) * .5;
                var state = ApplyManualWeather(position, natural, midpoint);
                Vector2 target = state.SystemId == natural.SystemId ? baseline : ResolveSurfaceWind(state, midpoint).WindMetersPerSecond;
                double seconds = target.LengthSquared() > mean.LengthSquared() ? WindSettings.StormRiseSeconds : WindSettings.StormDecaySeconds;
                mean = mean.Lerp(target, (float)(1 - Math.Exp(-(stamp - previous.Value) / Math.Max(.1, seconds))));
            }
            previous = stamp;
        }
        return mean;
    }
    public RingWindSettings WindSettings => RingWindSettings.Current;
    public RingWindField SharedWindField { get; } = new();
    public RingWindSnapshot QueryWindSnapshot(RingCanonicalPosition position) => CaptureWindSnapshot(position, QueryLocalWeather(position), CloudSimulationTimeSeconds);
    public RingWindSnapshot QueryWindSnapshotAtTime(RingCanonicalPosition position, double time)
    {
        time = double.IsFinite(time) ? time : 0;
        var weather = QueryLocalWeatherAtTime(position, time);
        float scale = 1f, add = 0f;
        if (weather.InheritWind && RingStormArrivalSettings.Current.GustFrontStrength > 0f)
            ApplyStormArrivalWind(position, time, weather.WindMetersPerSecond, out scale, out add);
        float gust;
        lock (_windStateLock)
        {
            SharedWindField.SetSeed(_grid?.TerrainSampler?.Seed ?? 1337);
            gust = weather.InheritWind ? SharedWindField.SampleAtTime(position, weather.WindMetersPerSecond, time, _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0, WindSettings, scale, add) : 1;
        }
        bool enabled = Enabled && WindSettings.Enabled;
        return new(enabled ? weather.WindMetersPerSecond : Vector2.Zero, enabled ? (weather.WindMetersPerSecond * gust).LimitLength(80) : Vector2.Zero, StormFactor(weather), time, gust, enabled);
    }
    public RingWindSnapshot CaptureWindSnapshot(RingCanonicalPosition position, LocalWeatherState weather, double time, bool external = false)
    {
        time = double.IsFinite(time) ? time : 0;
        Vector2 mean = weather.WindMetersPerSecond;
        if (!mean.IsFinite()) mean = Vector2.Zero;
        mean = mean.LimitLength(80);
        float scale = 1f, add = 0f;
        if (!external && weather.InheritWind && RingStormArrivalSettings.Current.GustFrontStrength > 0f)
            ApplyStormArrivalWind(position, CloudSimulationTimeSeconds, mean, out scale, out add);   // gust texture multipliers only; the mean already carries the arrival wind
        LastArrivalGustModifiers = (scale, add);
        bool enabled = Enabled && WindSettings.Enabled;
        float gust;
        lock (_windStateLock)
        {
            SharedWindField.SetSeed(_grid?.TerrainSampler?.Seed ?? 1337);
            SharedWindField.Configure(_grid?.GetCanonicalRingCircumferenceMeters64() ?? 0, mean, time, WindSettings);
            gust = external || !weather.InheritWind ? 1 : SharedWindField.Sample(position, WindSettings, scale, add);
        }
        return new(enabled ? mean : Vector2.Zero, enabled ? (mean * gust).LimitLength(80) : Vector2.Zero, StormFactor(weather), time, gust, enabled);
    }
    static float StormFactor(LocalWeatherState weather) => Mathf.Clamp(weather.SystemInfluence * weather.PrecipitationPotential * (weather.SystemKind == WeatherSystemKind.ExceptionalFront ? 1.25f : weather.SystemKind == WeatherSystemKind.MajorFront ? 1f : .3f), 0, 1);
    LocalWeatherState ResolveSurfaceWind(LocalWeatherState weather, double time)
    {
        if (!weather.InheritWind) return weather;
        var settings = WindSettings;
        Vector2 baseWind = WeatherWindMetersPerSecond * settings.BaseWindMultiplier;
        if (!baseWind.IsFinite()) baseWind = Vector2.Zero;
        float factor = StormFactor(weather);
        Vector2 direction = baseWind.LengthSquared() > .001f ? baseWind.Normalized() : Vector2.Right;
        direction = direction.Rotated(Mathf.DegToRad(settings.StormDirectionDegrees) * factor);
        Vector2 mean = direction * Mathf.Lerp(baseWind.Length(), Math.Max(baseWind.Length(), settings.StormWindSpeed), factor);
        return weather with { WindMetersPerSecond = settings.Enabled ? mean.LimitLength(80) : Vector2.Zero };
    }
}
