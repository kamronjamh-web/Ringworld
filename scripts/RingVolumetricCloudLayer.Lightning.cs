using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo;

/// <summary>World-weather lightning scheduler. It intentionally owns no rendering or camera policy.</summary>
public partial class RingVolumetricCloudLayer
{
    [ExportGroup("Shared lightning")]
    [Export] public bool EnableSharedLightning { get; set; } = true;
    [Export(PropertyHint.Range, "0.35,0.95,0.01")] public float SharedLightningPrecipitationThreshold { get; set; } = 0.62f;
    [Export(PropertyHint.Range, "1,64,1")] public int SharedLightningScanCellsPerFrame { get; set; } = 64;

    private readonly List<RingLightningEvent> _activeLightningEvents = new();
    private readonly Dictionary<(int Around, int Across), WeatherSystemDefinition> _lightningCandidateCache = new();
    private readonly Dictionary<string, LocalWeatherState> _manualWeather = new(StringComparer.Ordinal);
    private long _lightningSweepCursor;
    private double _lastLightningProcessedTime = double.NegativeInfinity;
    private double _nextLightningLocalPriorityRefresh;
    private double _lightningScanMilliseconds;
    private int _lightningCandidatesScannedLastUpdate;
    private int _lightningGlobalCellsScannedLastUpdate;
    private int _lightningLocalCellsScannedLastUpdate;
    private int _lightningCandidatesEvaluatedLastUpdate;
    public ulong ManualWeatherRevision { get; private set; }

    public IReadOnlyList<RingLightningEvent> ActiveLightningEvents => _activeLightningEvents;

    /// <summary>Registers a stationary canonical storm override; its ellipse also feeds far weather queries.</summary>
    public void RegisterManualWeather(string ownerKey, LocalWeatherState weather)
    {
        if (string.IsNullOrWhiteSpace(ownerKey)) return;
        if (_manualWeather.TryGetValue(ownerKey, out LocalWeatherState existing) && existing.Equals(weather)) return;
        RecordManualWind(ownerKey, weather);
        _manualWeather[ownerKey] = weather;
        ManualWeatherRevision++;
        _nextLightningLocalPriorityRefresh = 0.0;
    }

    public void UnregisterManualWeather(string ownerKey)
    {
        if (string.IsNullOrWhiteSpace(ownerKey)) return;
        if (_manualWeather.Remove(ownerKey)) { RecordManualWind(ownerKey, null); ManualWeatherRevision++; _nextLightningLocalPriorityRefresh = 0.0; }
    }
    public void unregister_manual_weather(string owner_key) => UnregisterManualWeather(owner_key);

    public RingLightningEvent? TriggerSharedLightningPreview(RingCanonicalPosition target, LocalWeatherState weather)
    {
        if (!Enabled || !EnableSharedLightning || _grid == null || !double.IsFinite(target.AroundMeters) || !double.IsFinite(target.AcrossMeters) || weather.SystemId == 0 || weather.PrecipitationPotential < SharedLightningPrecipitationThreshold)
            return null;
        int previews = 0; foreach (RingLightningEvent active in _activeLightningEvents) if (active.IsPreview) previews++;
        if (previews >= 16) return null;
        RingLightningEvent result = CreateLightningEvent(weather.SystemId, CloudSimulationTimeSeconds, target, weather, true, 0);
        if (!_activeLightningEvents.Exists(e => e.EventId == result.EventId)) _activeLightningEvents.Add(result);
        return result;
    }

    public Godot.Collections.Dictionary GetSharedLightningDiagnostics()
    {
        return new Godot.Collections.Dictionary {
            ["active_count"] = _activeLightningEvents.Count,
            ["candidate_count"] = _lightningCandidateCache.Count + _manualWeather.Count,
            ["scanned_last_update"] = _lightningCandidatesScannedLastUpdate,
            ["global_cells_scanned_last_update"] = _lightningGlobalCellsScannedLastUpdate,
            ["local_cells_scanned_last_update"] = _lightningLocalCellsScannedLastUpdate,
            ["candidates_evaluated_last_update"] = _lightningCandidatesEvaluatedLastUpdate,
            ["scan_cells_per_frame"] = Math.Clamp(SharedLightningScanCellsPerFrame, 1, 64),
            ["manual_weather_count"] = _manualWeather.Count,
            ["scan_milliseconds"] = _lightningScanMilliseconds
        };
    }

    // Godot's C# bridge preserves method names, so retain a snake-case entry
    // point for scene scripts and headless diagnostics.
    public Godot.Collections.Dictionary get_shared_lightning_diagnostics() => GetSharedLightningDiagnostics();

    /// <summary>Pure production-path cell query for deterministic headless replay diagnostics.</summary>
    public Godot.Collections.Dictionary QueryLightningCellAtTime(int aroundCell, int acrossCell, double timeSeconds)
    {
        var result = new Godot.Collections.Dictionary();
        if (_grid == null) return result;
        timeSeconds=double.IsFinite(timeSeconds)?Math.Max(0,timeSeconds):0;
        int cells=Math.Max(1,Mathf.CeilToInt((float)(_grid.GetCanonicalRingCircumferenceMeters64()/Mathf.Clamp(MacroWeatherCellMeters,16000,64000))));
        aroundCell=PositiveModulo(aroundCell,cells);
        return TryCreateNaturalEvent(aroundCell,acrossCell,timeSeconds,out RingLightningEvent e) ? ToDiagnostic(e,timeSeconds) : result;
    }
    public Godot.Collections.Dictionary query_lightning_cell_at_time(
        int around_cell, int across_cell, double time_seconds) =>
        QueryLightningCellAtTime(around_cell, across_cell, time_seconds);

    public void RegisterManualWeatherAt(string owner, double around, double across, float radius, int systemId = -9001)
    {
        RegisterManualWeather(owner, new LocalWeatherState(
            systemId, WeatherSystemKind.MajorFront, 1.0f, radius,
            new Vector2((float)WrapAround(around), (float)across), new Vector2(radius, radius * 0.6f),
            0.0f, 1.0f, 1.0f, Vector2.Zero, CloudFormationProfile.Cumulonimbus,
            CloudBaseMeters, CloudTopMeters) { InheritWind = true });
    }
    public void register_manual_weather_at(string owner, double around, double across, float radius, int system_id = -9001) =>
        RegisterManualWeatherAt(owner, around, across, radius, system_id);

    public Godot.Collections.Array<Godot.Collections.Dictionary> GetActiveLightningEventDiagnostics()
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        double now = CloudSimulationTimeSeconds;
        foreach (RingLightningEvent e in _activeLightningEvents) result.Add(ToDiagnostic(e, now));
        return result;
    }
    public Godot.Collections.Array<Godot.Collections.Dictionary> get_active_lightning_event_diagnostics() => GetActiveLightningEventDiagnostics();

    /// <summary>Godot-script friendly immutable preview endpoint, useful for diagnostics and UI.</summary>
    public Godot.Collections.Dictionary TriggerSharedLightningPreviewAt(double aroundMeters, double acrossMeters, int systemId = -9001,
        float precipitation = 1.0f, float radiusMeters = 45000.0f)
    {
        var weather = new LocalWeatherState(systemId, WeatherSystemKind.MajorFront, 1.0f, radiusMeters,
            new Vector2((float)WrapAround(aroundMeters), (float)acrossMeters), new Vector2(radiusMeters, radiusMeters * 0.6f),
            0.0f, 1.0f, precipitation, Vector2.Zero, CloudFormationProfile.Cumulus, CloudBaseMeters, CloudTopMeters);
        if (!double.IsFinite(aroundMeters) || !double.IsFinite(acrossMeters) || !float.IsFinite(precipitation) || !float.IsFinite(radiusMeters)) return new Godot.Collections.Dictionary();
        RingLightningEvent? e = TriggerSharedLightningPreview(new RingCanonicalPosition(aroundMeters, 0.0, acrossMeters), weather);
        return e.HasValue ? ToDiagnostic(e.Value, CloudSimulationTimeSeconds) : new Godot.Collections.Dictionary();
    }
    public Godot.Collections.Dictionary trigger_shared_lightning_preview_at(double around_meters, double across_meters, int system_id = -9001,
        float precipitation = 1.0f, float radius_meters = 45000.0f) => TriggerSharedLightningPreviewAt(around_meters, across_meters, system_id, precipitation, radius_meters);

    private void ProcessSharedLightning()
    {
        double now = double.IsFinite(CloudSimulationTimeSeconds) ? Math.Max(0.0, CloudSimulationTimeSeconds) : 0.0;
        if (now < _lastLightningProcessedTime)
        {
            // Restoring a save or rewinding must reproduce the same present slot,
            // never retain a cursor that suppresses it because of the abandoned future.
            _lightningCandidateCache.Clear(); _lightningSweepCursor = 0;
            _nextLightningLocalPriorityRefresh = 0.0;
        }
        _lastLightningProcessedTime = now;
        _activeLightningEvents.RemoveAll(e => e.StartTimeSeconds > now || now - e.StartTimeSeconds > RingLightningEvent.LifetimeSeconds);
        if (!Enabled || !EnableSharedLightning || _grid == null || _explorer == null) { _activeLightningEvents.Clear(); return; }

        ScanLightningCells(now);
        _lightningCandidatesEvaluatedLastUpdate = 0;
        foreach (var entry in _lightningCandidateCache)
        {
            _lightningCandidatesEvaluatedLastUpdate++;
            TryScheduleNaturalLightning(entry.Key.Around, entry.Key.Across, now);
        }
        foreach (LocalWeatherState manual in _manualWeather.Values) TryScheduleManualLightning(manual, now);
    }

    private void ScanLightningCells(double now)
    {
        Stopwatch stopwatch=Stopwatch.StartNew();
        float cell = Mathf.Clamp(MacroWeatherCellMeters, 16000, 64000);
        int aroundCount = Math.Max(1, Mathf.CeilToInt((float)(_grid!.GetCanonicalRingCircumferenceMeters64() / cell)));
        float maxRadius = Mathf.Clamp(MaximumMacroSystemRadiusMeters, 40000, 160000);
        int minAcross = Mathf.FloorToInt((float)((-maxRadius - WeatherWindMetersPerSecond.Y * now) / cell));
        int maxAcross = Mathf.CeilToInt((float)((_grid.RingAcrossWidthMeters + maxRadius - WeatherWindMetersPerSecond.Y * now) / cell));
        int acrossCount = Math.Max(1, maxAcross - minAcross + 1);
        long total = (long)aroundCount * acrossCount;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        foreach (var key in new List<(int Around,int Across)>(_lightningCandidateCache.Keys))
            if (key.Across < minAcross || key.Across > maxAcross) _lightningCandidateCache.Remove(key);
        int budget = Math.Clamp(SharedLightningScanCellsPerFrame, 1, 64);
        _lightningGlobalCellsScannedLastUpdate = 0;
        _lightningLocalCellsScannedLastUpdate = 0;
        for (int i=0; i<budget && stopwatch.Elapsed.TotalMilliseconds<1.0; i++) {
            _lightningGlobalCellsScannedLastUpdate++;
            long index = _lightningSweepCursor++ % total;
            int a = (int)(index % aroundCount); int z = minAcross + (int)(index / aroundCount);
            if (TryBuildWeatherSystem(a,z,seed,now,out WeatherSystemDefinition s) && s.Kind is WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront)
                _lightningCandidateCache[(a,z)] = s;
            else _lightningCandidateCache.Remove((a,z));
        }
        if(now>=_nextLightningLocalPriorityRefresh) {
            int centerA=PositiveModulo(Mathf.FloorToInt((float)(WrapAround(_explorer!.CanonicalPosition.AroundMeters-WeatherWindMetersPerSecond.X*now)/cell)),aroundCount);
            int centerZ=Mathf.FloorToInt((float)((_explorer.CanonicalPosition.AcrossMeters-WeatherWindMetersPerSecond.Y*now)/cell));
            for(int dz=-4;dz<=4;dz++) for(int da=-4;da<=4;da++) { _lightningLocalCellsScannedLastUpdate++; if(TryBuildWeatherSystem(PositiveModulo(centerA+da,aroundCount),centerZ+dz,seed,now,out WeatherSystemDefinition local) && local.Kind is WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront) _lightningCandidateCache[(PositiveModulo(centerA+da,aroundCount),centerZ+dz)]=local; }
            _nextLightningLocalPriorityRefresh=now+1.0;
        }
        _lightningCandidatesScannedLastUpdate = _lightningGlobalCellsScannedLastUpdate + _lightningLocalCellsScannedLastUpdate;
        _lightningScanMilliseconds=stopwatch.Elapsed.TotalMilliseconds;
    }

    private void TryScheduleNaturalLightning(int a, int z, double now)
    {
        if (_activeLightningEvents.Count >= 128) return;
        if (TryCreateNaturalEvent(a,z,now,out RingLightningEvent e) && !_activeLightningEvents.Exists(x=>x.EventId==e.EventId)) _activeLightningEvents.Add(e);
    }
    private bool TryCreateNaturalEvent(int a,int z,double now,out RingLightningEvent created)
    {
        created=default;
        int seed = _grid!.TerrainSampler?.Seed ?? 1337;
        if (!TryBuildWeatherSystem(a,z,seed,now,out WeatherSystemDefinition present)) return false;
        double cadence = 7.0 + Unit(LightningHash(seed,present.SystemId,0,31))*17.0;
        double phase = Unit(LightningHash(seed,present.SystemId,0,32))*cadence;
        long slot=(long)Math.Floor((now-phase)/cadence); double start=phase+slot*cadence;
        if (start < 0 || now-start > RingLightningEvent.LifetimeSeconds) return false;
        if (!TryBuildWeatherSystem(a,z,seed,start,out WeatherSystemDefinition s) || s.SystemId != present.SystemId) return false;
        LocalWeatherState weather=ToLightningWeather(s,start);
        if (!IsLightningEligible(weather) || Unit(LightningHash(seed,s.SystemId,slot,33)) > 0.18f+weather.PrecipitationPotential*.42f) return false;
        uint hash = LightningHash(seed, s.SystemId, slot, 8);
        long id = ((long)(hash & 0x7fffffff) << 32) | LightningHash(seed ^ 0x51ed, s.SystemId, slot, 9);
        if (id <= 0) id = 1;
        int liveIndex = _activeLightningEvents.FindIndex(x => x.EventId == id);
        if (liveIndex >= 0) { created = _activeLightningEvents[liveIndex]; return true; }
        if (!TrySampleStormPosition(weather, slot, out RingCanonicalPosition target)) return false;
        created=CreateLightningEvent(s.SystemId,start,target,weather,false,slot); return true;
    }
    private Godot.Collections.Dictionary ToDiagnostic(in RingLightningEvent e, double now) => new()
    {
        ["event_id"] = e.EventId, ["system_id"] = e.SystemId,
        ["shape_seed"] = e.ShapeSeed, ["start_time_seconds"] = e.StartTimeSeconds,
        ["ground_around_meters"] = e.GroundPosition.AroundMeters,
        ["ground_vertical_meters"] = e.GroundPosition.VerticalMeters,
        ["ground_across_meters"] = e.GroundPosition.AcrossMeters,
        ["cloud_around_meters"] = e.CloudPosition.AroundMeters,
        ["cloud_vertical_meters"] = e.CloudPosition.VerticalMeters,
        ["cloud_across_meters"] = e.CloudPosition.AcrossMeters,
        ["plane_angle_radians"] = e.PlaneAngleRadians, ["energy"] = e.Energy,
        ["flash_radius_meters"] = e.FlashRadiusMeters, ["is_preview"] = e.IsPreview,
        ["pulse"] = RingLightningEvent.PulseAtAge(now - e.StartTimeSeconds)
    };
    private void TryScheduleManualLightning(LocalWeatherState weather, double now)
    {
        if (_activeLightningEvents.Count >= 128 || !IsLightningEligible(weather)) return;
        int seed = _grid?.TerrainSampler?.Seed ?? 1337;
        double cadence = 7.0 + Unit(LightningHash(seed, weather.SystemId, 0, 31)) * 17.0;
        double phase = Unit(LightningHash(seed, weather.SystemId, 0, 32)) * cadence;
        long slot = (long)Math.Floor((now - phase) / cadence);
        double start = phase + slot * cadence;
        if (start < 0.0 || now - start > RingLightningEvent.LifetimeSeconds
            || !TrySampleStormPosition(weather, slot, out RingCanonicalPosition target)) return;
        RingLightningEvent e = CreateLightningEvent(weather.SystemId, start, target, weather, false, slot);
        if (!_activeLightningEvents.Exists(x => x.EventId == e.EventId)) _activeLightningEvents.Add(e);
    }
    private LocalWeatherState ToLightningWeather(WeatherSystemDefinition s,double time) => new(s.SystemId,s.Kind,s.Density,s.MajorRadiusMeters,new Vector2((float)WrapAround(s.SourceAroundMeters+WeatherWindMetersPerSecond.X*time),(float)(s.SourceAcrossMeters+WeatherWindMetersPerSecond.Y*time)),new Vector2(s.MajorRadiusMeters,s.MinorRadiusMeters),s.OrientationRadians,s.Density,s.PrecipitationPotential,WeatherWindMetersPerSecond,CloudFormationProfile.Cumulonimbus,CloudBaseMeters,CloudTopMeters);

    private bool IsLightningEligible(in LocalWeatherState weather) => weather.SystemId != 0
        && weather.SystemKind is WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront
        && weather.PrecipitationPotential >= SharedLightningPrecipitationThreshold
        && weather.SystemRadiiMeters.X > 1.0f && weather.SystemRadiiMeters.Y > 1.0f;

    private bool TrySampleStormPosition(in LocalWeatherState weather, long slot, out RingCanonicalPosition position)
    {
        float c = Mathf.Cos(weather.SystemOrientationRadians), s = Mathf.Sin(weather.SystemOrientationRadians);
        for (int attempt = 0; attempt < 6; attempt++) {
            uint h = LightningHash(_grid?.TerrainSampler?.Seed ?? 1337, weather.SystemId, slot, 3 + attempt * 11);
            float radius = Mathf.Sqrt(Unit(h)) * 0.6f;
            float angle = Unit(LightningHash((int)h, weather.SystemId, slot, 4 + attempt * 11)) * Mathf.Tau;
            float x = Mathf.Cos(angle) * weather.SystemRadiiMeters.X * radius, z = Mathf.Sin(angle) * weather.SystemRadiiMeters.Y * radius;
            double across = weather.SystemCenterMeters.Y + x * s + z * c;
            if (across < 0.0 || across > (_grid?.RingAcrossWidthMeters ?? 0.0)) continue;
            position = new RingCanonicalPosition(WrapAround(weather.SystemCenterMeters.X + x * c - z * s), 0.0, across); return true;
        }
        position = default; return false;
    }

    private RingLightningEvent CreateLightningEvent(int systemId, double start, RingCanonicalPosition target, in LocalWeatherState weather, bool preview, long slot)
    {
        int seed = _grid?.TerrainSampler?.Seed ?? 1337;
        uint hash = LightningHash(seed, systemId, slot == 0 && preview ? (long)Math.Round(start * 1000.0) : slot, preview ? 77 : 8);
        long id = ((long)(hash & 0x7fffffff) << 32) | LightningHash(seed ^ 0x51ed, systemId, slot, 9);
        if (id <= 0) id = 1;
        double across = target.AcrossMeters;
        double around = WrapAround(target.AroundMeters);
		float surface = 0;
		bool floatSurface = false;
		if (_grid?.TerrainSampler != null)
		{
			RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
			floatSurface = _grid.TerrainSampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
			surface = floatSurface ? edits.GetPresentationSurfaceHeight(_grid.TerrainSampler, (long)Math.Round(around), Mathf.RoundToInt((float)across)) : edits.GetSurfaceHeight(_grid.TerrainSampler, (long)Math.Round(around), Mathf.RoundToInt((float)across));
		}
		RingCanonicalPosition ground = new RingCanonicalPosition(around, surface + (floatSurface ? 0.0 : 1.0), across);
        RingCanonicalPosition cloud = ground with { VerticalMeters = Math.Max(ground.VerticalMeters + 250.0, Math.Max(weather.CloudBaseAltitudeMeters, CloudBaseMeters) + 180.0) };
        return new RingLightningEvent(id, systemId, start, ground, cloud, unchecked((int)hash), Unit(hash ^ 0xa31f) * Mathf.Tau,
            Mathf.Clamp(0.45f + weather.PrecipitationPotential * 0.55f, 0.0f, 1.0f), 2500.0f + Unit(hash ^ 0xf00d) * 9000.0f, preview);
    }

    private static uint LightningHash(int seed, int system, long slot, int salt)
    {
        unchecked
        {
            uint hash = (uint)seed * 0x9e3779b9u ^ (uint)system * 0x85ebca6bu
                ^ (uint)slot ^ (uint)(slot >> 32) * 0xc2b2ae35u ^ (uint)salt;
            hash ^= hash >> 16;
            hash *= 0x7feb352du;
            hash ^= hash >> 15;
            hash *= 0x846ca68bu;
            return hash ^ (hash >> 16);
        }
    }
    private static float Unit(uint hash) => (hash & 0x00ffffff) / 16777215.0f;

    private LocalWeatherState ApplyManualWeather(in RingCanonicalPosition position, LocalWeatherState natural, double? historyTime = null)
    {
        if (_grid == null) return natural;
        LocalWeatherState best = natural;
        foreach (LocalWeatherState manual in historyTime.HasValue ? ManualWindStatesAt(historyTime.Value) : _manualWeather.Values)
        {
            if (manual.SystemId == 0 || manual.SystemRadiiMeters.X <= 0 || manual.SystemRadiiMeters.Y <= 0) continue;
            double da = position.AroundMeters - manual.SystemCenterMeters.X;
            double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
            da -= Math.Round(da / circumference) * circumference;
            double dz = position.AcrossMeters - manual.SystemCenterMeters.Y;
            float c = Mathf.Cos(manual.SystemOrientationRadians), s = Mathf.Sin(manual.SystemOrientationRadians);
            double x = da * c + dz * s, z = -da * s + dz * c;
            float normalizedDistance = Mathf.Sqrt((float)(x * x / (manual.SystemRadiiMeters.X * manual.SystemRadiiMeters.X) + z * z / (manual.SystemRadiiMeters.Y * manual.SystemRadiiMeters.Y)));
            float influence = (1.0f - SmoothLightningUnit((normalizedDistance - 0.55f) / 0.75f)) * manual.SystemInfluence;
            if (influence <= 0.0001f) continue;
            bool chooseMetadata = influence >= best.SystemInfluence || best.SystemId == manual.SystemId;
            best = (chooseMetadata ? manual : best) with {
                SystemInfluence = Math.Max(best.SystemInfluence, influence),
                CloudCoverage = Math.Max(best.CloudCoverage, Mathf.Lerp(best.CloudCoverage, manual.CloudCoverage, influence)),
                PrecipitationPotential = Math.Max(best.PrecipitationPotential, manual.PrecipitationPotential * influence)
            };
        }
        return best;
    }

    private static float SmoothLightningUnit(float value)
    {
        float t = Mathf.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }
}
