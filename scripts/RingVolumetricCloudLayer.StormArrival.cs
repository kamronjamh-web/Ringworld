using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.UI;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Storm arrival adapter (docs/storm-revamp-s3-storm-arrival-design.md, WO-A2): feeds the pure <see cref="StormArrivalMath"/> from the
/// production weather field without editing it. Reads TryBuildWeatherSystem, EvaluateWeatherLifecycle and the weather systems' cells.
/// No node and no _Process hook: the observer state is computed lazily once per process frame. Providers are installed on tree entry.
/// </summary>
public partial class RingVolumetricCloudLayer
{
    [Export] public string StormArrivalPreferencesPath { get; set; } = GameplayStormArrivalDefaults.UserPath;

    /// <summary>Precipitation start used by the arrival maths (the local effects' default; both RingLocalWeatherEffects and the distant curtains use 0.18).</summary>
    public const float StormArrivalPrecipitationStart = 0.18f;
    const double StormArrivalSearchRadiusMeters = 20000.0;
    const float StormArrivalCullMeters = 30000.0f;       // systems whose rain/cloud rim is further than this are ignored
    const double ArrivalLatticeSeconds = 0.1;            // observer cache lattice (design says 0.25 s; 0.1 keeps the front-edge ramp smooth at fast rims)
    const double ArrivalMoveMeters = 16.0;
    const double ArrivalHistoryTickSeconds = 0.125;      // one lag per tick: a full history refresh every second

    private readonly List<StormSystemGeometry> _arrivalNow = new(64);
    private readonly List<StormSystemGeometry> _arrivalLag = new(64);
    private readonly float[] _arrivalProxies = new float[StormArrivalMath.HistoryLagsSeconds.Length + 1];
    private readonly double[] _arrivalLagTimes = new double[StormArrivalMath.HistoryLagsSeconds.Length + 1];
    private readonly RainHistorySnapshot _arrivalSnapshot = new();
    private StormArrivalState _arrivalObserverState = StormArrivalState.Calm;
    private RainHistorySample _arrivalObserverHistory = RainHistorySample.None;
    private RingCanonicalPosition _arrivalObserverPosition;
    private double _arrivalObserverTime = double.NaN;
    private ulong _arrivalFrame = ulong.MaxValue;
    private int _arrivalNextLag;
    private int _arrivalStaleLagMask;               // lags to refresh after a jump (two per evaluation, so a teleport does not hitch)
    private ulong _arrivalNextTickMsec;
    private bool _arrivalHistoryReady;
    private int _arrivalSettingsHash;
    private Func<RingCanonicalPosition, double, StormArrivalState>? _arrivalProvider;
    private Func<StormArrivalState>? _arrivalObserverProvider;
    private Func<RingCanonicalPosition, double, RainHistorySample>? _arrivalHistoryProvider;
    private Func<RingCanonicalPosition, double, RingWindColumn>? _flightColumnProvider;
    private double _arrivalLastMilliseconds, _arrivalMaxMilliseconds, _arrivalSumMilliseconds;
    private int _arrivalEvaluations, _arrivalJumps, _arrivalLagRefreshes;
    private static readonly (StormArrivalPhase Phase, float Weight, float Hail)? ArrivalOverride = ReadArrivalOverride();

    public override void _EnterTree()
    {
        if (GameplayStormArrivalDefaults.StartupDefaults?.GetPath() != StormArrivalPreferencesPath)
        {
            RingStormArrivalSettings.Current = new RingStormArrivalSettings();
            GameplayStormArrivalDefaults.ApplyStartup(StormArrivalPreferencesPath);
        }
        _arrivalProvider = QueryStormArrivalCached;
        _arrivalObserverProvider = () => ObserverStormArrival;
        _arrivalHistoryProvider = QueryRainHistoryCached;
        StormArrivalMath.Provider = _arrivalProvider;
        StormArrivalMath.ObserverProvider = _arrivalObserverProvider;
        StormArrivalMath.RainHistoryProvider = _arrivalHistoryProvider;
        RingWindQuery.ColumnProvider = _flightColumnProvider = BuildFlightWindColumn;
        TreeExiting += UninstallStormArrivalProviders;
    }

    private void UninstallStormArrivalProviders()
    {
        TreeExiting -= UninstallStormArrivalProviders;
        if (StormArrivalMath.Provider == _arrivalProvider) StormArrivalMath.Provider = null;
        if (StormArrivalMath.ObserverProvider == _arrivalObserverProvider) StormArrivalMath.ObserverProvider = null;
        if (StormArrivalMath.RainHistoryProvider == _arrivalHistoryProvider) StormArrivalMath.RainHistoryProvider = null;
        if (RingWindQuery.ColumnProvider == _flightColumnProvider) RingWindQuery.ColumnProvider = null;
    }

    // ---- storm travel seam ----
    // The only code that knows how a system moves. Today every system is advected by the one WeatherWindMetersPerSecond; when per-kind
    // travel lands, change these two (and the source-frame search origin in CollectStormGeometry, which must then reach by the largest
    // drift). The maths, history and tests take the per-system drift from StormSystemGeometry.DriftMetersPerSecond.
    private Vector2 StormSystemDriftMetersPerSecond(in WeatherSystemDefinition system) => WeatherWindMetersPerSecond;

    private (double Around, double Across) StormSystemCenterAtTime(in WeatherSystemDefinition system, double timeSeconds)
        => (WrapAround(system.SourceAroundMeters + WeatherWindMetersPerSecond.X * timeSeconds),
            system.SourceAcrossMeters + WeatherWindMetersPerSecond.Y * timeSeconds);

    // ---- geometry ----
    private StormArrivalTuning CurrentStormArrivalTuning() => RingStormArrivalSettings.Current.ToTuning(StormArrivalPrecipitationStart, Coverage);

    /// <summary>
    /// Natural systems within reach of a point at one time, as arrival geometry (offset = observer minus centre, enveloped radii, drift, rho from a
    /// +/-1 s central difference of the lifecycle envelope). Manual (preview/test) systems are not included in v1. The megastorm bundle
    /// (docs/megastorm-design.md) is appended last, as slab records with their own drift (<see cref="AppendMegastormSystems(double, double, double, List{StormSystemGeometry})"/>).
    /// </summary>
    private void CollectStormGeometry(double aroundMeters, double acrossMeters, double timeSeconds, List<StormSystemGeometry> into)
    {
        into.Clear();
        if (_grid == null) return;
        int seed = _grid.TerrainSampler?.Seed ?? 1337;
        float macroCell = Mathf.Clamp(MacroWeatherCellMeters, 16000.0f, 64000.0f);
        float maximumRadius = Mathf.Clamp(MaximumMacroSystemRadiusMeters, 40000.0f, 160000.0f);
        double circumference = Math.Max(1.0, _grid.GetCanonicalRingCircumferenceMeters64());
        double sourceAround = WrapAround(aroundMeters - WeatherWindMetersPerSecond.X * timeSeconds);
        double sourceAcross = acrossMeters - WeatherWindMetersPerSecond.Y * timeSeconds;
        int aroundCellCount = Math.Max(1, Mathf.CeilToInt((float)(circumference / macroCell)));
        int centerCellAround = Mathf.FloorToInt((float)(sourceAround / macroCell));
        int centerCellAcross = Mathf.FloorToInt((float)(sourceAcross / macroCell));
        int searchRadius = Math.Clamp(Mathf.CeilToInt((float)(StormArrivalSearchRadiusMeters + maximumRadius) / macroCell) + 1, 2, 12);
        // A system centre lies within 0.41 cells (x and y) of its cell centre, its radius is at most maximumRadius, and systems whose rim is more
        // than StormArrivalCullMeters away are dropped: cells beyond that circle cannot contribute, which removes about half of the square search window.
        double cullRadius = maximumRadius + StormArrivalCullMeters + 0.58 * macroCell;
        double cullRadiusSquared = cullRadius * cullRadius;
        for (int dz = -searchRadius; dz <= searchRadius; dz++)
        for (int dx = -searchRadius; dx <= searchRadius; dx++)
        {
            double cellDeltaAround = (centerCellAround + dx + 0.5) * macroCell - sourceAround;
            double cellDeltaAcross = (centerCellAcross + dz + 0.5) * macroCell - sourceAcross;
            if (cellDeltaAround * cellDeltaAround + cellDeltaAcross * cellDeltaAcross > cullRadiusSquared) continue;
            int cellAround = PositiveModulo(centerCellAround + dx, aroundCellCount);
            int cellAcross = centerCellAcross + dz;
            if (!TryBuildWeatherSystem(cellAround, cellAcross, seed, timeSeconds, out WeatherSystemDefinition system)) continue;
            var centre = StormSystemCenterAtTime(system, timeSeconds);
            var offset = new Vector2((float)ShortestAroundDelta(centre.Around, aroundMeters, circumference), (float)(acrossMeters - centre.Across));
            WeatherLifecycle life = EvaluateWeatherLifecycle(cellAround, cellAcross, seed, timeSeconds);
            float rho = 0.0f;
            float envelope = EffectiveSystemEnvelope(system, life.Envelope, timeSeconds);   // megastorm suppression (4.3); identity when none
            if (NaturalWeatherLifecyclesEnabled && envelope > 1e-4f)
            {
                float ahead = EffectiveSystemEnvelope(system, EvaluateWeatherLifecycle(cellAround, cellAcross, seed, timeSeconds + 1.0).Envelope, timeSeconds + 1.0);
                float behind = EffectiveSystemEnvelope(system, EvaluateWeatherLifecycle(cellAround, cellAcross, seed, Math.Max(0.0, timeSeconds - 1.0)).Envelope, Math.Max(0.0, timeSeconds - 1.0));
                double span = timeSeconds >= 1.0 ? 2.0 : 1.0 + timeSeconds;
                rho = (float)((ahead - behind) / span / envelope);
            }
            var geometry = new StormSystemGeometry(system.SystemId, (int)system.Kind, offset, system.MajorRadiusMeters, system.MinorRadiusMeters,
                system.OrientationCosine, system.OrientationSine, envelope, rho, StormSystemDriftMetersPerSecond(system),
                system.Density, system.PrecipitationPotential, StormLifecycleCode(life));
            float n = StormArrivalMath.NormalizedDistance(geometry, out float ray, out float dist);
            if (dist - ray > StormArrivalCullMeters) continue;
            into.Add(geometry);
        }
        if (MegastormSource != null) AppendMegastormSystems(aroundMeters, acrossMeters, timeSeconds, into);
    }

    private static void CopyToSnapshot(RainHistorySnapshot snapshot, int lag, List<StormSystemGeometry> systems)
    {
        // Keep the 32 systems whose rim is nearest (the +/-8 km window of a surface map only needs those).
        int count = Math.Min(systems.Count, RainHistorySnapshot.MaxSystemsPerLag);
        if (systems.Count > count) systems.Sort((a, b) => RimClearance(a).CompareTo(RimClearance(b)));
        for (int i = 0; i < count; i++) snapshot.Systems[lag * RainHistorySnapshot.MaxSystemsPerLag + i] = systems[i];
        snapshot.Counts[lag] = count;
    }

    private static float RimClearance(in StormSystemGeometry g)
    {
        StormArrivalMath.NormalizedDistance(g, out float ray, out float dist);
        return dist - ray;
    }

    // ---- pure queries ----
    /// <summary>Pure, uncached arrival state at a position and time (tests and flight); includes the exact 9-lag rain history.</summary>
    public StormArrivalState QueryStormArrivalAtTime(RingCanonicalPosition position, double timeSeconds)
    {
        if (_grid == null) return StormArrivalState.Calm;
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        var now = new List<StormSystemGeometry>(32);
        CollectStormGeometry(position.AroundMeters, position.AcrossMeters, timeSeconds, now);
        StormArrivalTuning tuning = CurrentStormArrivalTuning();
        RainHistorySample history = QueryRainHistoryAtTime(position, timeSeconds, now);
        StormArrivalState state = StormArrivalMath.Evaluate(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(now), tuning, history);
        return ApplyStormArrivalAltitudeGate(state, position);
    }

    /// <summary>Pure, uncached rain history (nine lags: now plus StormArrivalMath.HistoryLagsSeconds). TimeSinceRainSeconds is continuous (interpolated between lags).</summary>
    public RainHistorySample QueryRainHistoryAtTime(RingCanonicalPosition position, double timeSeconds)
        => QueryRainHistoryAtTime(position, timeSeconds, null);

    private RainHistorySample QueryRainHistoryAtTime(RingCanonicalPosition position, double timeSeconds, List<StormSystemGeometry>? nowGeometry)
    {
        if (_grid == null) return RainHistorySample.None;
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        StormArrivalTuning tuning = CurrentStormArrivalTuning();
        Span<float> proxies = stackalloc float[StormArrivalMath.HistoryLagsSeconds.Length + 1];
        var list = new List<StormSystemGeometry>(32);
        for (int l = 0; l < proxies.Length; l++)
        {
            float lag = l == 0 ? 0.0f : StormArrivalMath.HistoryLagsSeconds[l - 1];
            List<StormSystemGeometry> systems = l == 0 && nowGeometry != null ? nowGeometry : list;
            if (!(l == 0 && nowGeometry != null))
                CollectStormGeometry(position.AroundMeters, position.AcrossMeters, Math.Max(0.0, timeSeconds - lag), list);
            proxies[l] = StormArrivalMath.ProxyOf(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(systems), tuning);
        }
        return StormArrivalMath.ReduceHistory(proxies, tuning);
    }

    /// <summary>Exact per-lag geometry the history reduces from, for a position and time (parity tests, S2 snapshot).</summary>
    public void BuildRainHistorySnapshot(RingCanonicalPosition position, double timeSeconds, RainHistorySnapshot snapshot)
    {
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        var list = new List<StormSystemGeometry>(32);
        snapshot.Time = timeSeconds;
        snapshot.LagCount = StormArrivalMath.HistoryLagsSeconds.Length + 1;
        for (int l = 0; l < snapshot.LagCount; l++)
        {
            float lag = l == 0 ? 0.0f : StormArrivalMath.HistoryLagsSeconds[l - 1];
            snapshot.LagSeconds[l] = lag;
            CollectStormGeometry(position.AroundMeters, position.AcrossMeters, Math.Max(0.0, timeSeconds - lag), list);
            CopyToSnapshot(snapshot, l, list);
        }
    }

    [ThreadStatic] private static bool _arrivalGateActive;

    private StormArrivalState ApplyStormArrivalAltitudeGate(StormArrivalState state, RingCanonicalPosition position)
    {
        // Above the cloud top all precipitation weights are 0 (flight stays sane); the lead-in weights and the aftermath are unaffected.
        // The column provider can be costly, so ask only when a gated weight is actually non-zero.
        if (!state.Valid || state.FatDrop + state.Core + state.Trailing + state.HailIntensity <= 0.001f) return state;
        // The column provider reads the local weather, whose wind path reads the arrival state again: a re-entrant call skips the gate instead of recursing.
        if (_arrivalGateActive) return state;
        _arrivalGateActive = true;
        try { if (PrecipitationAltitude.QueryPrecipitationColumn(position).Phase != PrecipitationPhase.Above) return state; }
        finally { _arrivalGateActive = false; }
        state = state with { FatDrop = 0.0f, Core = 0.0f, Trailing = 0.0f, HailIntensity = 0.0f };
        return state with { Phase = StormArrivalMath.ResolvePhase(state) };
    }

    // ---- thread-local caches for the static providers (physics-thread and flight callers) ----
    private struct ArrivalCacheEntry { public long Key; public StormArrivalState State; public RainHistorySample History; public bool Valid; }
    [ThreadStatic] private static ArrivalCacheEntry[]? _arrivalCache;
    [ThreadStatic] private static ArrivalCacheEntry[]? _historyCache;

    private static long ArrivalKey(RingCanonicalPosition p, double t, double cellMeters, double lattice)
        => HashCode.Combine((long)Math.Floor(p.AroundMeters / cellMeters), (long)Math.Floor(p.AcrossMeters / cellMeters), (long)Math.Floor(t / lattice));

    /// <summary>Cached variant behind <see cref="StormArrivalMath.Provider"/>: 32 m cells and a 0.25 s lattice, thread-local.</summary>
    public StormArrivalState QueryStormArrivalCached(RingCanonicalPosition position, double timeSeconds)
    {
        if (_grid == null) return StormArrivalState.Calm;
        _arrivalCache ??= new ArrivalCacheEntry[16];
        long key = ArrivalKey(position, timeSeconds, 32.0, 0.25);
        ref ArrivalCacheEntry entry = ref _arrivalCache[(int)((ulong)key % (ulong)_arrivalCache.Length)];
        if (entry.Valid && entry.Key == key) return entry.State;
        entry = new ArrivalCacheEntry { Key = key, State = QueryStormArrivalAtTime(position, timeSeconds), Valid = true };
        return entry.State;
    }

    /// <summary>Cached variant behind <see cref="StormArrivalMath.RainHistoryProvider"/>: 64 m cells and a 1 s lattice, thread-local.</summary>
    public RainHistorySample QueryRainHistoryCached(RingCanonicalPosition position, double timeSeconds)
    {
        if (_grid == null) return RainHistorySample.None;
        _historyCache ??= new ArrivalCacheEntry[16];
        long key = ArrivalKey(position, timeSeconds, 64.0, 1.0);
        ref ArrivalCacheEntry entry = ref _historyCache[(int)((ulong)key % (ulong)_historyCache.Length)];
        if (entry.Valid && entry.Key == key) return entry.History;
        entry = new ArrivalCacheEntry { Key = key, History = QueryRainHistoryAtTime(position, timeSeconds), Valid = true };
        return entry.History;
    }

    // ---- observer ----
    /// <summary>
    /// Arrival state at the explorer, computed lazily once per process frame (main thread). The rain history is refreshed round-robin, one lag per
    /// 0.125 s tick, so a full refresh takes one second; a position or time jump rebuilds all lags at once.
    /// </summary>
    public StormArrivalState ObserverStormArrival
    {
        get
        {
            ulong frame = Engine.GetProcessFrames();
            if (frame == _arrivalFrame) return _arrivalObserverState;
            _arrivalFrame = frame;
            if (_grid == null || _explorer == null) return _arrivalObserverState = StormArrivalState.Calm;
            long started = Stopwatch.GetTimestamp();
            RingCanonicalPosition position = _explorer.CanonicalPosition;
            double time = CloudSimulationTimeSeconds;
            StormArrivalTuning tuning = CurrentStormArrivalTuning();
            int settingsHash = HashCode.Combine(tuning.GustFrontLeadSeconds, tuning.Hail, tuning.BaseCoverage, WeatherWindMetersPerSecond);
            double dAround = ShortestAroundDelta(_arrivalObserverPosition.AroundMeters, position.AroundMeters);
            double dAcross = position.AcrossMeters - _arrivalObserverPosition.AcrossMeters;
            double moved = Math.Sqrt(dAround * dAround + dAcross * dAcross);
            bool jump = double.IsNaN(_arrivalObserverTime) || moved > 2000.0 || Math.Abs(time - _arrivalObserverTime) > 5.0 || settingsHash != _arrivalSettingsHash;
            // A pending history refresh (after a jump) continues every frame, also under a paused clock and a fixed camera.
            bool stale = jump || _arrivalStaleLagMask != 0 || moved > ArrivalMoveMeters
                || Math.Floor(time / ArrivalLatticeSeconds) != Math.Floor(_arrivalObserverTime / ArrivalLatticeSeconds);
            if (stale)
            {
                _arrivalSettingsHash = settingsHash;
                CollectStormGeometry(position.AroundMeters, position.AcrossMeters, time, _arrivalNow);
                if (jump) _arrivalJumps++;
                if (jump || !_arrivalHistoryReady) MarkArrivalHistoryStale(tuning);
                RefreshArrivalHistory(position, time, tuning);
                _arrivalProxies[0] = StormArrivalMath.ProxyOf(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_arrivalNow), tuning);
                _arrivalLagTimes[0] = time;
                _arrivalSnapshot.Time = time;
                _arrivalSnapshot.LagSeconds[0] = 0.0f;
                CopyToSnapshot(_arrivalSnapshot, 0, _arrivalNow);
                _arrivalObserverHistory = StormArrivalMath.ReduceHistory(_arrivalProxies, tuning);
                StormArrivalState state = StormArrivalMath.Evaluate(
                    System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_arrivalNow), tuning, _arrivalObserverHistory);
                state = ApplyStormArrivalAltitudeGate(state, position);
                _arrivalObserverState = ApplyArrivalOverride(state);
                _arrivalObserverPosition = position;
                _arrivalObserverTime = time;
                double ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                _arrivalLastMilliseconds = ms; if (_arrivalEvaluations >= 3) _arrivalMaxMilliseconds = Math.Max(_arrivalMaxMilliseconds, ms);   // the first calls include JIT
                _arrivalSumMilliseconds += ms; _arrivalEvaluations++;
            }
            return _arrivalObserverState;
        }
    }

    private void MarkArrivalHistoryStale(in StormArrivalTuning tuning)
    {
        _arrivalStaleLagMask = (1 << _arrivalProxies.Length) - 2;      // lags 1..8
        _arrivalSnapshot.LagCount = _arrivalProxies.Length;
        _arrivalHistoryReady = true;
        _arrivalNextLag = 1;
        _arrivalNextTickMsec = 0;
    }

    private void RefreshArrivalHistory(RingCanonicalPosition position, double time, in StormArrivalTuning tuning)
    {
        if (_arrivalStaleLagMask != 0)
        {
            for (int done = 0, l = 1; l < _arrivalProxies.Length && done < 2; l++)
            {
                if ((_arrivalStaleLagMask & (1 << l)) == 0) continue;
                RefreshHistoryLag(l, position, time, tuning);
                _arrivalStaleLagMask &= ~(1 << l);
                done++;
            }
            return;
        }
        ulong now = Time.GetTicksMsec();
        if (now < _arrivalNextTickMsec) return;
        _arrivalNextTickMsec = now + (ulong)(ArrivalHistoryTickSeconds * 1000.0);
        RefreshHistoryLag(_arrivalNextLag, position, time, tuning);
        _arrivalNextLag = _arrivalNextLag % StormArrivalMath.HistoryLagsSeconds.Length + 1;
    }

    private void RefreshHistoryLag(int lag, RingCanonicalPosition position, double time, in StormArrivalTuning tuning)
    {
        _arrivalLagRefreshes++;
        float seconds = StormArrivalMath.HistoryLagsSeconds[lag - 1];
        CollectStormGeometry(position.AroundMeters, position.AcrossMeters, Math.Max(0.0, time - seconds), _arrivalLag);
        _arrivalProxies[lag] = StormArrivalMath.ProxyOf(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_arrivalLag), tuning);
        _arrivalLagTimes[lag] = time;
        _arrivalSnapshot.LagSeconds[lag] = seconds;
        CopyToSnapshot(_arrivalSnapshot, lag, _arrivalLag);
    }

    /// <summary>
    /// The per-lag geometry behind the observer's rain history, plus the history itself (S2 evaluates it per texel with
    /// <see cref="StormArrivalMath.RainHistoryAt"/>). The snapshot object is reused: copy what you need on the main thread. False before the first observer evaluation.
    /// </summary>
    public bool TryGetRainHistorySnapshot(out RainHistorySnapshot snapshot)
    {
        _ = ObserverStormArrival;
        snapshot = _arrivalSnapshot;
        return _arrivalHistoryReady && _arrivalSnapshot.LagCount > 0;
    }

    /// <summary>Observer rain history (same value that fed <see cref="ObserverStormArrival"/>).</summary>
    public RainHistorySample ObserverRainHistory { get { _ = ObserverStormArrival; return _arrivalObserverHistory; } }

    // ---- flight wind column (RingWindQuery.ColumnProvider) ----
    // The surface wind comes from QueryWindSnapshotAtTime, whose gust field and cache are main-thread state in the wind partial (not edited here), so the world
    // side takes _flightWindLock for that one call and caches the last column per thread. Callers on other threads are safe against each other; they are only
    // safe against main-thread wind queries once the wind partial's cache is made lockable (open item). The arrival and terrain parts are thread-safe.
    private readonly object _flightWindLock = new();
    private struct FlightColumnCache { public double Around, Across, Time; public RingWindColumn Column; public bool Valid; }
    [ThreadStatic] private static FlightColumnCache _flightColumnCache;
    private struct GroundCell { public long Key; public float Height; public bool Valid; }
    [ThreadStatic] private static GroundCell[]? _flightGround;

    private RingWindColumn BuildFlightWindColumn(RingCanonicalPosition position, double timeSeconds)
    {
        if (_grid == null || _grid.TerrainSampler == null) return default;
        timeSeconds = double.IsFinite(timeSeconds) ? Math.Max(0.0, timeSeconds) : 0.0;
        ref FlightColumnCache cache = ref _flightColumnCache;
        if (cache.Valid && cache.Around == position.AroundMeters && cache.Across == position.AcrossMeters && cache.Time == timeSeconds) return cache.Column;
        var surfacePosition = new RingCanonicalPosition(position.AroundMeters, 0.0, position.AcrossMeters);
        RingWindSnapshot snapshot;
        LocalWeatherState weather;
        lock (_flightWindLock)
        {
            snapshot = QueryWindSnapshotAtTime(surfacePosition, timeSeconds);
            weather = QueryLocalWeatherCoverageAtTime(surfacePosition, timeSeconds);
        }
        _flightGround ??= new GroundCell[64];
        long key = HashCode.Combine((long)Math.Floor(position.AroundMeters / 64.0), (long)Math.Floor(position.AcrossMeters / 64.0));
        ref GroundCell ground = ref _flightGround[(int)((ulong)key % (ulong)_flightGround.Length)];
        if (!ground.Valid || ground.Key != key)
        {
            double across = Math.Clamp(position.AcrossMeters, 0.0, _grid.RingAcrossWidthMeters);
            ground = new GroundCell { Key = key, Height = _grid.TerrainSampler.GetPresentationSurfaceHeight(WrapAround(position.AroundMeters), across), Valid = true };
        }
        StormArrivalState arrival = QueryStormArrivalCached(surfacePosition, timeSeconds);
        float top = Math.Max(weather.CloudTopAltitudeMeters, weather.CloudBaseAltitudeMeters + 500.0f);
        var column = new RingWindColumn(snapshot.MeanWind, snapshot.InstantWind, WeatherWindMetersPerSecond, false, ground.Height, arrival,
            weather.CloudBaseAltitudeMeters, top, _grid.TerrainSampler.Seed, true);
        cache = new FlightColumnCache { Around = position.AroundMeters, Across = position.AcrossMeters, Time = timeSeconds, Column = column, Valid = true };
        return column;
    }

    // ---- observer accessors for the rainbow and other CPU consumers ----
    /// <summary>The explorer's canonical position (false before the layer is bound to an explorer).</summary>
    public bool TryGetObserverCanonicalPosition(out RingCanonicalPosition position)
    {
        position = default;
        if (_grid == null || _explorer == null) return false;
        position = _explorer.CanonicalPosition;
        return true;
    }

    /// <summary>Copies the arrival geometry of the natural systems around the observer at the current time (offset = observer minus centre, as evaluated by the last observer update).</summary>
    public int CopyObserverStormGeometry(List<StormSystemGeometry> into)
    {
        _ = ObserverStormArrival;
        into.Clear();
        into.AddRange(_arrivalNow);
        return into.Count;
    }

    /// <summary>Cloud base above the observer's ground (m, 300-4000), from the raw weather field at the observer.</summary>
    public float ObserverCloudBaseAboveGroundMeters
    {
        get
        {
            if (!TryGetObserverCanonicalPosition(out RingCanonicalPosition p)) return 1500.0f;
            LocalWeatherState weather = QueryLocalWeatherCoverageAtTime(p, CloudSimulationTimeSeconds);
            return Mathf.Clamp(weather.CloudBaseAltitudeMeters - ((float)p.VerticalMeters - 2.0f), 300.0f, 4000.0f);
        }
    }

    // ---- debug override and diagnostics ----
    private static (StormArrivalPhase, float, float)? ReadArrivalOverride()
    {
        string? text = System.Environment.GetEnvironmentVariable("RING_STORM_ARRIVAL");
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Split(':');
        if (!Enum.TryParse(parts[0], true, out StormArrivalPhase phase) || phase == StormArrivalPhase.Clear) return null;
        float weight = 1.0f;
        if (parts.Length > 1 && !float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out weight)) return null;
        float hail = 0.0f;
        if (parts.Length > 2 && !float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hail)) return null;
        return (phase, Mathf.Clamp(weight, 0.0f, 1.0f), Mathf.Clamp(hail, 0.0f, 1.0f));
    }

    /// <summary>RING_STORM_ARRIVAL=&lt;phase&gt;:&lt;weight&gt;[:&lt;hail&gt;] forces one weight (all others 0, severity at least 1) and optionally a hail intensity (size 0.6) for captures.</summary>
    private static StormArrivalState ApplyArrivalOverride(StormArrivalState state)
    {
        if (ArrivalOverride is not { } forced) return state;
        state = state with { Outflow = 0, Approach = 0, GustFront = 0, FatDrop = 0, Core = 0, Trailing = 0, Clearing = 0, Valid = true, Phase = forced.Phase, Severity = Math.Max(state.Severity, 1.0f), HailIntensity = forced.Hail, HailSize = forced.Hail > 0.0f ? 0.6f : 0.0f };
        if (state.StormBearing == Vector2.Zero) state = state with { StormBearing = Vector2.Right, OutflowDirection = Vector2.Left };
        return forced.Phase switch
        {
            StormArrivalPhase.Approach => state with { Approach = forced.Weight },
            StormArrivalPhase.GustFront => state with { GustFront = forced.Weight },
            StormArrivalPhase.FatDrop => state with { FatDrop = forced.Weight },
            StormArrivalPhase.Core => state with { Core = forced.Weight },
            StormArrivalPhase.Trailing => state with { Trailing = forced.Weight },
            _ => state with { Clearing = forced.Weight }
        };
    }

    public Godot.Collections.Dictionary GetStormArrivalDiagnostic()
    {
        StormArrivalState s = ObserverStormArrival;
        return new Godot.Collections.Dictionary
        {
            ["valid"] = s.Valid, ["phase"] = s.Phase.ToString(), ["system_id"] = s.SystemId, ["kind"] = s.Kind, ["severity"] = s.Severity,
            ["approach"] = s.Approach, ["gust_front"] = s.GustFront, ["fat_drop"] = s.FatDrop, ["core"] = s.Core, ["trailing"] = s.Trailing, ["clearing"] = s.Clearing,
            ["rim_distance_m"] = s.RimDistanceMeters, ["rim_speed_mps"] = s.RimSpeedMetersPerSecond,
            ["time_to_rain_s"] = float.IsFinite(s.TimeToRainSeconds) ? s.TimeToRainSeconds : -1.0f,
            ["bearing"] = s.StormBearing, ["outflow"] = s.OutflowDirection,
            ["hail_intensity"] = s.HailIntensity, ["hail_size"] = s.HailSize, ["cooling_offset"] = s.CoolingOffset,
            ["time_since_rain_s"] = float.IsFinite(s.TimeSinceRainSeconds) ? s.TimeSinceRainSeconds : -1.0f, ["recent_rain"] = s.RecentRainAmount,
            ["systems_in_reach"] = _arrivalNow.Count, ["cpu_last_ms"] = _arrivalLastMilliseconds, ["cpu_max_ms"] = _arrivalMaxMilliseconds,
            ["cpu_mean_ms"] = _arrivalEvaluations > 0 ? _arrivalSumMilliseconds / _arrivalEvaluations : 0.0, ["evaluations"] = _arrivalEvaluations, ["jumps"] = _arrivalJumps, ["lag_refreshes"] = _arrivalLagRefreshes,
            ["override"] = ArrivalOverride.HasValue
        };
    }

    /// <summary>Test hook: the Coverage formula of the arrival maths against ResolveLocalCloudCoverage at the fixed mean weather noise 0.5.</summary>
    public float StormArrivalCoverageParity(float combinedInfluence, float strongestInfluence, float density)
    {
        var macro = new MacroWeatherSample(1, WeatherSystemKind.MajorFront, combinedInfluence, strongestInfluence, 0.0, 0.0, 1.0f, 1.0f, 0.0f, density, 0.5f);
        return ResolveLocalCloudCoverage(0.5f, macro);
    }

    /// <summary>Test hook: terrain surface altitude (m, unedited) at a canonical position; 0 without a world.</summary>
    public float GetGroundHeightForTests(double aroundMeters, double acrossMeters)
        => _grid?.TerrainSampler == null ? 0.0f : _grid.TerrainSampler.GetPresentationSurfaceHeight(WrapAround(aroundMeters), Math.Clamp(acrossMeters, 0.0, _grid.RingAcrossWidthMeters));

    /// <summary>Test hook: the canonical ring circumference in metres.</summary>
    public double GetRingCircumferenceForTests() => _grid?.GetCanonicalRingCircumferenceMeters64() ?? 0.0;

    /// <summary>Test hook: the arrival geometry of all natural systems in reach of a point and time (no history).</summary>
    public StormSystemGeometry[] GetStormGeometryAtTime(RingCanonicalPosition position, double timeSeconds)
    {
        var list = new List<StormSystemGeometry>(32);
        CollectStormGeometry(position.AroundMeters, position.AcrossMeters, Math.Max(0.0, timeSeconds), list);
        return list.ToArray();
    }
}
