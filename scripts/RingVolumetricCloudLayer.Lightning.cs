using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>World-weather lightning scheduler. It intentionally owns no rendering or camera policy.</summary>
public partial class RingVolumetricCloudLayer
{
    [ExportGroup("Shared lightning")]
    [Export] public bool EnableSharedLightning { get; set; } = true;
    [Export(PropertyHint.Range, "0.35,0.95,0.01")] public float SharedLightningPrecipitationThreshold { get; set; } = 0.62f;
    [Export(PropertyHint.Range, "1,64,1")] public int SharedLightningScanCellsPerFrame { get; set; } = 64;

    private readonly List<RingLightningEvent> _activeLightningEvents = new();
    private readonly List<(int Around, int Across)> _lightningPruneScratch = new();
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

    private readonly List<RingLightningEvent> _activeContactArcs = new();
    /// <summary>
    /// Megastorm near-rim contact arcs (WO13): short visual-only discharges drawn by the field-strike layer. They are deliberately NOT in
    /// <see cref="ActiveLightningEvents"/>, so no adapter ever publishes a disturbance for them (they only drive the MegastormContact FlashLevel).
    /// </summary>
    public IReadOnlyList<RingLightningEvent> ActiveContactArcs => _activeContactArcs;

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
        if (FindActiveLightningIndex(result.EventId) < 0) _activeLightningEvents.Add(result);
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
        for (int i = _activeLightningEvents.Count - 1; i >= 0; i--)
        {
            RingLightningEvent active = _activeLightningEvents[i];
            if (active.StartTimeSeconds > now || now - active.StartTimeSeconds > active.LifeSeconds)
                _activeLightningEvents.RemoveAt(i);
        }
        for (int i = _activeContactArcs.Count - 1; i >= 0; i--)
            if (_activeContactArcs[i].StartTimeSeconds > now || now - _activeContactArcs[i].StartTimeSeconds > _activeContactArcs[i].LifeSeconds) _activeContactArcs.RemoveAt(i);
        if (!Enabled || !EnableSharedLightning || _grid == null || _explorer == null) { _activeLightningEvents.Clear(); _activeContactArcs.Clear(); return; }

        ScanLightningCells(now);
        _lightningCandidatesEvaluatedLastUpdate = 0;
        foreach (var entry in _lightningCandidateCache)
        {
            _lightningCandidatesEvaluatedLastUpdate++;
            TryScheduleNaturalLightning(entry.Key.Around, entry.Key.Across, now);
            TryScheduleLeadingEdgeLightning(entry.Key.Around, entry.Key.Across, now);
        }
        foreach (LocalWeatherState manual in _manualWeather.Values) TryScheduleManualLightning(manual, now);
        TryScheduleMegastormLightning(now);
        TryScheduleMegastormScythes(now);
        TryScheduleMegastormFieldStrikes(now);
    }

    private void ScanLightningCells(double now)
    {
        long scanStart = Stopwatch.GetTimestamp();
        float cell = Mathf.Clamp(MacroWeatherCellMeters, 16000, 64000);
        int aroundCount = Math.Max(1, Mathf.CeilToInt((float)(_grid!.GetCanonicalRingCircumferenceMeters64() / cell)));
        float maxRadius = Mathf.Clamp(MaximumMacroSystemRadiusMeters, 40000, 160000);
        int minAcross = Mathf.FloorToInt((float)((-maxRadius - WeatherWindMetersPerSecond.Y * now) / cell));
        int maxAcross = Mathf.CeilToInt((float)((_grid.RingAcrossWidthMeters + maxRadius - WeatherWindMetersPerSecond.Y * now) / cell));
        int acrossCount = Math.Max(1, maxAcross - minAcross + 1);
        long total = (long)aroundCount * acrossCount;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        _lightningPruneScratch.Clear();
        foreach (var key in _lightningCandidateCache.Keys)
            if (key.Across < minAcross || key.Across > maxAcross) _lightningPruneScratch.Add(key);
        foreach (var key in _lightningPruneScratch) _lightningCandidateCache.Remove(key);
        int budget = Math.Clamp(SharedLightningScanCellsPerFrame, 1, 64);
        _lightningGlobalCellsScannedLastUpdate = 0;
        _lightningLocalCellsScannedLastUpdate = 0;
        for (int i=0; i<budget && Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds<1.0; i++) {
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
        _lightningScanMilliseconds=Stopwatch.GetElapsedTime(scanStart).TotalMilliseconds;
    }

    private void TryScheduleNaturalLightning(int a, int z, double now)
    {
        if (_activeLightningEvents.Count >= 128) return;
        if (TryCreateNaturalEvent(a,z,now,out RingLightningEvent e) && FindActiveLightningIndex(e.EventId) < 0) _activeLightningEvents.Add(e);
    }
    private bool TryCreateNaturalEvent(int a,int z,double now,out RingLightningEvent created)
    {
        created=default;
        int seed = _grid!.TerrainSampler?.Seed ?? 1337;
        // The present system id is a pure function of (cell, seed, generation), so
        // reject cells whose lightning slot is not live before building the system.
        int predictedId = WeatherSystemIdForGeneration(a, z, seed, ResolveWeatherGeneration(a, z, seed, now));
        if (!TryResolveLightningSlot(seed, predictedId, now, out _, out _)) return false;
        if (!TryBuildWeatherSystem(a,z,seed,now,out WeatherSystemDefinition present)) return false;
        if (!TryResolveLightningSlot(seed, present.SystemId, now, out long slot, out double start)) return false;
        if (!TryBuildWeatherSystem(a,z,seed,start,out WeatherSystemDefinition s) || s.SystemId != present.SystemId) return false;
        LocalWeatherState weather=ToLightningWeather(s,start);
        if (!IsLightningEligible(weather) || Unit(LightningHash(seed,s.SystemId,slot,33)) > (0.18f+weather.PrecipitationPotential*.42f)*LightningVisualPolicy.NaturalStrikeChanceScale) return false;
        uint hash = LightningHash(seed, s.SystemId, slot, 8);
        long id = ((long)(hash & 0x7fffffff) << 32) | LightningHash(seed ^ 0x51ed, s.SystemId, slot, 9);
        if (id <= 0) id = 1;
        int liveIndex = FindActiveLightningIndex(id);
        if (liveIndex >= 0) { created = _activeLightningEvents[liveIndex]; return true; }
        if (!TrySampleStormPosition(weather, slot, out RingCanonicalPosition target)) return false;
        created=CreateLightningEvent(s.SystemId,start,target,weather,false,slot); return true;
    }
    // ---- G1 leading-arc stream: a second, independent strike stream per Major+ system, concentrated on the advancing rim (closing in with the rain) ----
    /// <summary>The leading stream starts earlier than the natural one (a growing front is not yet at the full precipitation threshold), at this share of it.</summary>
    private const float LeadingEdgePotentialShare = 0.35f;
    /// <summary>Distinguishes leading-stream slots from natural slots in event ids.</summary>
    private const long LeadingEdgeSlotTag = 1L << 40;

    private void TryScheduleLeadingEdgeLightning(int a, int z, double now)
    {
        if (_activeLightningEvents.Count >= 128) return;
        if (TryCreateLeadingEdgeEvent(a, z, now, out RingLightningEvent e) && FindActiveLightningIndex(e.EventId) < 0) _activeLightningEvents.Add(e);
    }

    /// <summary>Cadence/phase slot of the leading stream: (10 + 20 hash) s, shortened by the frequency setting (same convention as the natural slot), salts 140/141.</summary>
    private static bool TryResolveLeadingEdgeSlot(int seed, int systemId, double now, out long slot, out double start)
    {
        double cadence = (10.0 + Unit(LightningHash(seed, systemId, 0, 140)) * 20.0) / Math.Max(1.0f, LightningVisualPolicy.FrequencyScale);
        double phase = Unit(LightningHash(seed, systemId, 0, 141)) * cadence;
        slot = (long)Math.Floor((now - phase) / cadence);
        start = phase + slot * cadence;
        return !(start < 0.0 || now - start > RingLightningEvent.LifetimeSeconds);
    }

    private bool TryCreateLeadingEdgeEvent(int a, int z, double now, out RingLightningEvent created)
    {
        created = default;
        int seed = _grid!.TerrainSampler?.Seed ?? 1337;
        int predictedId = WeatherSystemIdForGeneration(a, z, seed, ResolveWeatherGeneration(a, z, seed, now));
        if (!TryResolveLeadingEdgeSlot(seed, predictedId, now, out _, out _)) return false;
        if (!TryBuildWeatherSystem(a, z, seed, now, out WeatherSystemDefinition present)) return false;
        if (!TryResolveLeadingEdgeSlot(seed, present.SystemId, now, out long slot, out double start)) return false;
        if (!TryBuildWeatherSystem(a, z, seed, start, out WeatherSystemDefinition s) || s.SystemId != present.SystemId) return false;
        LocalWeatherState weather = ToLightningWeather(s, start);
        if (weather.SystemId == 0 || weather.SystemKind is not (WeatherSystemKind.MajorFront or WeatherSystemKind.ExceptionalFront)
            || weather.PrecipitationPotential < SharedLightningPrecipitationThreshold * LeadingEdgePotentialShare
            || weather.SystemRadiiMeters.X <= 1.0f || weather.SystemRadiiMeters.Y <= 1.0f) return false;
        if (Unit(LightningHash(seed, s.SystemId, slot, 143)) > (0.25f + weather.PrecipitationPotential * 0.5f) * LightningVisualPolicy.NaturalStrikeChanceScale) return false;
        long taggedSlot = slot | LeadingEdgeSlotTag;
        uint hash = LightningHash(seed, s.SystemId, taggedSlot, 8);
        long id = ((long)(hash & 0x7fffffff) << 32) | LightningHash(seed ^ 0x51ed, s.SystemId, taggedSlot, 9);
        if (id <= 0) id = 1;
        int liveIndex = FindActiveLightningIndex(id);
        if (liveIndex >= 0) { created = _activeLightningEvents[liveIndex]; return true; }
        float rho = 0.0f;
        if (NaturalWeatherLifecyclesEnabled)
        {
            float envelope = EffectiveSystemEnvelope(s, EvaluateWeatherLifecycle(a, z, seed, start).Envelope, start);   // megastorm suppression (4.3); identity when none
            if (envelope > 1e-4f)
            {
                float ahead = EffectiveSystemEnvelope(s, EvaluateWeatherLifecycle(a, z, seed, start + 1.0).Envelope, start + 1.0), behind = EffectiveSystemEnvelope(s, EvaluateWeatherLifecycle(a, z, seed, Math.Max(0.0, start - 1.0)).Envelope, Math.Max(0.0, start - 1.0));
                rho = (float)((ahead - behind) / (start >= 1.0 ? 2.0 : 1.0 + start) / envelope);
            }
        }
        if (!StormArrivalMath.SampleLeadingArc(s.SystemId, slot, WeatherWindMetersPerSecond, rho, s.OrientationCosine, s.OrientationSine,
                weather.SystemRadiiMeters.X, weather.SystemRadiiMeters.Y, out Vector2 offset, out _)) return false;
        double across = weather.SystemCenterMeters.Y + offset.Y;
        if (across < 0.0 || across > (_grid?.RingAcrossWidthMeters ?? 0.0)) return false;
        var target = new RingCanonicalPosition(WrapAround(weather.SystemCenterMeters.X + offset.X), 0.0, across);
        created = CreateLightningEvent(s.SystemId, start, target, weather, false, taggedSlot);
        return true;
    }

    /// <summary>
    /// Pure production-path replay of one cell's leading-arc strikes in [t0, t1] (diagnostics and tests; steps below the 0.72 s event lifetime so no slot is missed).
    /// Each entry is the same dictionary as <see cref="QueryLightningCellAtTime"/>, de-duplicated by event id.
    /// </summary>
    public Godot.Collections.Array<Godot.Collections.Dictionary> QueryLeadingEdgeLightningInWindow(int aroundCell, int acrossCell, double t0, double t1)
    {
        var result = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        if (_grid == null) return result;
        int cells = Math.Max(1, Mathf.CeilToInt((float)(_grid.GetCanonicalRingCircumferenceMeters64() / Mathf.Clamp(MacroWeatherCellMeters, 16000, 64000))));
        aroundCell = PositiveModulo(aroundCell, cells);
        var seen = new HashSet<long>();
        for (double t = Math.Max(0.0, t0); t <= t1; t += 0.5)
            if (TryCreateLeadingEdgeEvent(aroundCell, acrossCell, t, out RingLightningEvent e) && seen.Add(e.EventId)) result.Add(ToDiagnostic(e, e.StartTimeSeconds));
        return result;
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
        ["flash_radius_meters"] = e.FlashRadiusMeters, ["is_preview"] = e.IsPreview, ["system_kind"] = e.SystemKind,
        ["upper_atmosphere_kind"] = (int)(UpperAtmosphereLightning.TryDerive(e, CloudTopMeters, out UpperAtmosphereSprite sprite) ? sprite.Kind : UpperAtmosphereKind.None),
        ["pulse"] = RingLightningEvent.PulseAtAge(now - e.StartTimeSeconds)
    };
    private void TryScheduleManualLightning(LocalWeatherState weather, double now)
    {
        if (_activeLightningEvents.Count >= 128 || !IsLightningEligible(weather)) return;
        int seed = _grid?.TerrainSampler?.Seed ?? 1337;
        if (!TryResolveLightningSlot(seed, weather.SystemId, now, out long slot, out double start)
            || !TrySampleStormPosition(weather, slot, out RingCanonicalPosition target)) return;
        RingLightningEvent e = CreateLightningEvent(weather.SystemId, start, target, weather, false, slot);
        if (FindActiveLightningIndex(e.EventId) < 0) _activeLightningEvents.Add(e);
    }

    // ---- Megastorm near strike stream (docs/megastorm-design.md WO7) ----
    private readonly List<MegastormLightning.NearStrike> _megastormStrikeScratch = new();

    /// <summary>
    /// Near strikes of the megastorm: the cells of its flash field (and the "bolt from the blue" stream) within 50 km of the explorer whose 0.72 s life
    /// covers <paramref name="now"/>. Pure in (model, now, explorer, Lightning frequency, Reduced flashes), so rewinds and reloads reproduce ids and times;
    /// the event id is the flash-field cell id, shared with the far glow, thunder (RingWeatherAudio reads ActiveLightningEvents, so its d/343 delay and the
    /// megastorm air gate apply) and the sprite layer (UpperAtmosphereLightning derives from the event id, chance for kind 6 is the superstorm one).
    /// A no-op without an installed, triggered megastorm, so ordinary worlds are unchanged.
    /// </summary>
    private void TryScheduleMegastormLightning(double now)
    {
        if (MegastormSource == null || MegastormHazardModel is not MegastormModel model || !model.Triggered || _explorer == null) return;
        RingCanonicalPosition observer = _explorer.CanonicalPosition;
        MegastormLightning.NearStrikesInWindow(model, now - RingLightningEvent.LifetimeSeconds, Math.BitIncrement(now), observer.AroundMeters, observer.AcrossMeters,
            LightningVisualPolicy.NaturalStrikeChanceScale, _megastormStrikeScratch);
        foreach (MegastormLightning.NearStrike strike in _megastormStrikeScratch)
        {
            if (_activeLightningEvents.Count >= 128) return;
            MegastormLightning.MegastormFlash f = strike.Flash;
            if (FindActiveLightningIndex(f.Id) >= 0) continue;
            var weather = new LocalWeatherState(MegastormModel.WallRecordId, WeatherSystemKind.Megastorm, 1.0f, 25000.0f,
                new Vector2((float)strike.AroundMeters, (float)f.AcrossMeters), new Vector2(25000.0f, 25000.0f), 0.0f, 1.0f, 1.0f, Vector2.Zero,
                CloudFormationProfile.MegastormWall, CloudBaseMeters, CloudTopMeters);
            RingLightningEvent made = CreateLightningEvent(MegastormModel.WallRecordId, f.StartTimeSeconds,
                new RingCanonicalPosition(strike.AroundMeters, 0.0, f.AcrossMeters), weather, false, f.Id);
            _activeLightningEvents.Add(made with
            {
                EventId = f.Id,
                CloudPosition = made.GroundPosition with { VerticalMeters = Math.Max(made.GroundPosition.VerticalMeters + 250.0, f.AltitudeMeters) },
                ShapeSeed = unchecked((int)UpperAtmosphereLightning.Hash(f.Id, 201)),
                PlaneAngleRadians = UpperAtmosphereLightning.Unit(f.Id, 202) * Mathf.Tau,
                Energy = f.Energy,
                FlashRadiusMeters = 2500.0f + UpperAtmosphereLightning.Unit(f.Id, 203) * 9000.0f,
                SystemKind = (int)WeatherSystemKind.Megastorm
            });
        }
    }

    // ---- Megastorm lightning scythes (docs/megastorm-design.md 5.5.4, WO12) ----
    private readonly List<MegastormScythe> _megastormScytheScratch = new();
    /// <summary>Attachment strikes are scheduled for scythes whose attachment is within this distance of the explorer (the cost cap: "only while the observer is within 25 km of the attachment zone").</summary>
    public const double ScytheAttachmentReachMeters = 25000.0;

    /// <summary>
    /// The attachment strike of each scythe (MegastormScythes stream, <c>RingLightningEvent.Scythe</c> set, id = the scythe id) within 25 km of the explorer
    /// whose 0.72 s bolt covers <paramref name="now"/>. The sheet itself is drawn from the same pure stream by RingMegastormScythes, which re-derives it from
    /// the id, so a rewind or reload reproduces both. The bolt goes through the ordinary renderer and thunder (megastorm air gate included).
    /// </summary>
    private void TryScheduleMegastormScythes(double now)
    {
        if (MegastormSource == null || MegastormHazardModel is not MegastormModel model || !model.Triggered || _explorer == null) return;
        RingCanonicalPosition observer = _explorer.CanonicalPosition;
        MegastormScythes.ScythesInWindow(model, now - RingLightningEvent.LifetimeSeconds, Math.BitIncrement(now), observer.AroundMeters, observer.AcrossMeters,
            ScytheAttachmentReachMeters, LightningVisualPolicy.NaturalStrikeChanceScale, _megastormScytheScratch);
        foreach (MegastormScythe s in _megastormScytheScratch)
        {
            if (_activeLightningEvents.Count >= 128) return;
            if (FindActiveLightningIndex(s.Id) >= 0) continue;
            var weather = new LocalWeatherState(MegastormModel.WallRecordId, WeatherSystemKind.Megastorm, 1.0f, 25000.0f,
                new Vector2((float)s.AttachAroundMeters, (float)s.AttachAcrossMeters), new Vector2(25000.0f, 25000.0f), 0.0f, 1.0f, 1.0f, Vector2.Zero,
                CloudFormationProfile.MegastormWall, CloudBaseMeters, CloudTopMeters);
            RingLightningEvent made = CreateLightningEvent(MegastormModel.WallRecordId, s.StartTimeSeconds,
                new RingCanonicalPosition(s.AttachAroundMeters, 0.0, s.AttachAcrossMeters), weather, false, s.Id);
            _activeLightningEvents.Add(made with
            {
                EventId = s.Id,
                ShapeSeed = unchecked((int)UpperAtmosphereLightning.Hash(s.Id, 201)),
                PlaneAngleRadians = UpperAtmosphereLightning.Unit(s.Id, 202) * Mathf.Tau,
                Energy = s.Energy,
                SystemKind = (int)WeatherSystemKind.Megastorm,
                Scythe = true
            });
        }
    }

    // ---- Megastorm field strikes and contact arcs (docs/megastorm-design.md 5.5.5, WO13) ----
    private readonly List<MegastormFieldStrike> _megastormFieldStrikeScratch = new();
    private readonly List<MegastormContactArc> _megastormArcScratch = new();
    /// <summary>Contact arcs are tiny (50-500 m); they are only scheduled within this horizontal distance of the explorer.</summary>
    public const double ContactArcReachMeters = 60000.0;

    /// <summary>
    /// Field strikes of the whole wall (an upward channel is visible from anywhere, out to the antipode) whose 0.8-1.9 s life covers <paramref name="now"/>, as
    /// <c>FieldStrike</c>-flagged events (GroundPosition = contact point, CloudPosition = channel base, own DurationSeconds) so the field's lightning adapter publishes
    /// exactly one FieldStrike disturbance each (SourceId = EventId). Contact arcs within 60 km go to <see cref="ActiveContactArcs"/>. Pure in (model, now, provider,
    /// Lightning frequency, Reduced flashes); a no-op without a triggered megastorm and a field provider, so ordinary worlds are unchanged.
    /// </summary>
    private void TryScheduleMegastormFieldStrikes(double now)
    {
        if (MegastormSource == null || MegastormHazardModel is not MegastormModel model || !model.Triggered || _explorer == null
            || MegastormFieldCoupling.FieldUndersideProvider == null) { _fieldSlotCache.Clear(); _arcSlotCache.Clear(); return; }
        float scale = LightningVisualPolicy.NaturalStrikeChanceScale;
        // The per-slot results are pure in (model, provider, scale), so each slot is derived once and kept for a few seconds (a rewind simply finds or re-derives it).
        if (!ReferenceEquals(model, _fieldCacheModel) || !ReferenceEquals(MegastormFieldCoupling.FieldUndersideProvider, _fieldCacheProvider) || scale != _fieldCacheScale
            || model.State.Anchors.Count != _fieldCacheAnchors)
        {
            _fieldSlotCache.Clear(); _arcSlotCache.Clear();
            _fieldCacheModel = model; _fieldCacheProvider = MegastormFieldCoupling.FieldUndersideProvider; _fieldCacheScale = scale; _fieldCacheAnchors = model.State.Anchors.Count;
        }
        long firstSlot = Math.Max(0, (long)Math.Floor((now - RingLightningEvent.FieldStrikeMaxSeconds) / MegastormFieldStrikes.SlotSeconds)), lastSlot = (long)Math.Floor(now / MegastormFieldStrikes.SlotSeconds);
        for (long slot = firstSlot; slot <= lastSlot; slot++)
        {
            if (!_fieldSlotCache.TryGetValue(slot, out MegastormFieldStrike[]? strikes))
            {
                var made = new List<MegastormFieldStrike>(2);
                for (int seg = 0; seg < MegastormFieldStrikes.Segments; seg++)
                    if (MegastormFieldStrikes.Strike(model, slot, seg, scale) is MegastormFieldStrike strike) made.Add(strike);
                _fieldSlotCache[slot] = strikes = made.ToArray();
            }
            foreach (MegastormFieldStrike strike in strikes)
            {
                if (_activeLightningEvents.Count >= 128) break;
                if (strike.StartTimeSeconds > now || now - strike.StartTimeSeconds > strike.DurationSeconds || FindActiveLightningIndex(strike.Id) >= 0) continue;
                _activeLightningEvents.Add(MegastormFieldStrikes.ToEvent(strike));
            }
        }
        if (_fieldSlotCache.Count > 24) PruneSlotCache(_fieldSlotCache, firstSlot);
        RingCanonicalPosition observer = _explorer.CanonicalPosition;
        double circumference = model.Settings.CircumferenceMeters;
        long arcFirst = Math.Max(0, (long)Math.Floor((now - 0.5) / MegastormFieldStrikes.ArcSlotSeconds)), arcLast = (long)Math.Floor(now / MegastormFieldStrikes.ArcSlotSeconds);
        for (long slot = arcFirst; slot <= arcLast; slot++)
        {
            if (!_arcSlotCache.TryGetValue(slot, out MegastormContactArc[]? arcs))
            {
                var made = new List<MegastormContactArc>(2);
                for (int side = 0; side < 2; side++)
                    if (MegastormFieldStrikes.Arc(model, slot, side, scale) is MegastormContactArc arc) made.Add(arc);
                _arcSlotCache[slot] = arcs = made.ToArray();
            }
            foreach (MegastormContactArc arc in arcs)
            {
                if (arc.StartTimeSeconds > now || now - arc.StartTimeSeconds > arc.DurationSeconds) continue;
                double da = MegastormModel.WrapSigned(arc.AroundMeters - observer.AroundMeters, circumference), dz = arc.AcrossMeters - observer.AcrossMeters;
                if (da * da + dz * dz > ContactArcReachMeters * ContactArcReachMeters) continue;
                bool known = false;
                for (int i = 0; i < _activeContactArcs.Count && !known; i++) known = _activeContactArcs[i].EventId == arc.Id;
                if (!known && _activeContactArcs.Count < 32) _activeContactArcs.Add(MegastormFieldStrikes.ToEvent(arc));
            }
        }
        if (_arcSlotCache.Count > 24) PruneSlotCache(_arcSlotCache, arcFirst);
    }

    private readonly Dictionary<long, MegastormFieldStrike[]> _fieldSlotCache = new();
    private readonly Dictionary<long, MegastormContactArc[]> _arcSlotCache = new();
    private MegastormModel? _fieldCacheModel;
    private Func<double, double>? _fieldCacheProvider;
    private float _fieldCacheScale = float.NaN;
    private int _fieldCacheAnchors = -1;

    private static void PruneSlotCache<T>(Dictionary<long, T> cache, long keepFrom)
    {
        List<long>? stale = null;
        foreach (long key in cache.Keys) if (key < keepFrom - 2 || key > keepFrom + 40) (stale ??= new List<long>()).Add(key);
        if (stale != null) foreach (long key in stale) cache.Remove(key);
    }

    /// <summary>A terrain height sampler (around, across) with the surface-edit snapshot captured once, the same surface the strikes land on; null before the grid exists.</summary>
    public Func<double, double, double>? CreateSurfaceHeightSampler()
    {
        if (_grid?.TerrainSampler == null) return null;
        RingLodSurfaceEditSnapshot edits = _grid.CaptureLodSurfaceEditSnapshot();
        var sampler = _grid.TerrainSampler;
        bool floatSurface = sampler.UsesRuntimeSurfaceGeneration || edits.UsesFloatSurface;
        int maxAcross = Math.Max(0, Mathf.RoundToInt(_grid.RingAcrossWidthMeters));
        return (around, across) =>
        {
            long a = (long)Math.Round(WrapAround(around));
            int z = Math.Clamp(Mathf.RoundToInt((float)across), 0, maxAcross);
            return floatSurface ? edits.GetPresentationSurfaceHeight(sampler, a, z) : edits.GetSurfaceHeight(sampler, a, z);
        };
    }

    /// <summary>Cadence/phase slot of a system's lightning; false when no slot is live at <paramref name="now"/>.</summary>
    private static bool TryResolveLightningSlot(int seed, int systemId, double now, out long slot, out double start)
    {
        // Frequency above 1 shortens the cadence (below 1 it thins strikes in TryCreateNaturalEvent instead), so strong storms also speed up.
        double cadence = (7.0 + Unit(LightningHash(seed, systemId, 0, 31)) * 17.0) / Math.Max(1.0f, LightningVisualPolicy.FrequencyScale);
        double phase = Unit(LightningHash(seed, systemId, 0, 32)) * cadence;
        slot = (long)Math.Floor((now - phase) / cadence);
        start = phase + slot * cadence;
        return !(start < 0.0 || now - start > RingLightningEvent.LifetimeSeconds);
    }

    private int FindActiveLightningIndex(long id)
    {
        for (int i = 0; i < _activeLightningEvents.Count; i++)
            if (_activeLightningEvents[i].EventId == id) return i;
        return -1;
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
            Mathf.Clamp(0.45f + weather.PrecipitationPotential * 0.55f, 0.0f, 1.0f), 2500.0f + Unit(hash ^ 0xf00d) * 9000.0f, preview, (int)weather.SystemKind);
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
