using System;
using Godot;
using RingworldTechDemo.Weather;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>
/// Rain streaks and splashes (docs/storm-revamp-s1-design.md sections 2-3, WO5/WO6). Stateless world-anchored hashed lattice drawn by a raster CompositorEffect
/// (scripts/render/RingRainStreaksComposite.gd) that runs after the Sunshine cloud composite and before the rain haze composite, so clouds never overdraw near rain.
/// This partial owns the CPU half: per frame it publishes the double-precision drop phase, the lattice anchors, the shared wind and gust frame, the occlusion map frame,
/// the local rain intensity field (gate per drop column) and the scene light terms. The shader does the rest.
/// </summary>
public partial class RingLocalWeatherEffects
{
    /// <summary>Rain streak instance budget per frame at heavy rain (a graphics preset sets it; see PresetWeather).</summary>
    [Export(PropertyHint.Range, "256,32000,256")] public int MaximumStreakCount { get; set; } = 12000;
    /// <summary>Test hook: replaces the storm level D used for streak lighting (null = read it from the Sunshine bridge).</summary>
    public float? StormLevelOverride { get; set; }
    /// <summary>Test hook: replaces the scene light radiance (null = derived from the day/night cycle, cloud shadow and storm level).</summary>
    public Vector3? StreakLightOverride { get; set; }
    /// <summary>Test and capture hook: the observer position of the streak lattice and field (null = the explorer). The camera itself is never moved by it.</summary>
    public RingCanonicalPosition? StreakObserverOverride { get; set; }
    /// <summary>Test hook: replaces the local rain intensity grid (25 values, rows over across, columns over around).</summary>
    public float[]? StreakFieldOverride { get; set; }
    /// <summary>Test and capture hook: replaces the storm arrival state the streaks read (fat drops, core, trailing, hail). Null = the observer's arrival state.</summary>
    public StormArrivalState? StreakArrivalOverride { get; set; }

    private const int StreakPayloadBytes = 672;
    private const int StreakGridCells = RainStreakLattice.GridSide * RainStreakLattice.GridSide;
    private static readonly StringName PublishStreakFrameName = "publish_frame";
    private static readonly StringName PublishStreakMarkersName = "publish_gpu_markers";
    private CompositorEffect? _streakCompositor;
    private bool? _streakMarkersEnabled;
    private string _streakBeginMarker = string.Empty, _streakEndMarker = string.Empty;
    private readonly byte[] _streakPayload = new byte[StreakPayloadBytes];
    private readonly float[] _streakGrid = new float[StreakGridCells];
    private readonly Variant[] _streakArgs = new Variant[6];
    private DirectionalLight3D? _streakStarLight;
    private bool _streakPublishedActive;
    private double _streakGridTime = double.NegativeInfinity;
    private ulong _streakGridManualRevision = ulong.MaxValue;
    private RingCanonicalPosition _streakGridPosition;
    private bool _streakGridValid;
    private double _streakStormTime = double.NegativeInfinity;
    private float _streakStormLevel;
    private Vector3 _lastStreakRadiance;
    private bool _streakPerStreakLight;
    private int _streakNearInstances, _streakMidInstances, _streakSplashInstances;
    private float _lastStreakSlope, _lastStreakFatDrop, _lastStreakHail;
    public double LastStreakGridMilliseconds { get; private set; }
    private double _streakGridTotalMs, _streakUpdateTotalMs;
    private long _streakUpdateFrames;
    public bool RainStreaksActive => _streakPublishedActive;
    public CompositorEffect? RainStreaksCompositor => _streakCompositor;
    /// <summary>Test hook: false forces the per-cell QueryLocalWeather sweep (the pre-mirror path) so its CPU cost can be measured against the macro mirror.</summary>
    public bool StreakGridMirrorEnabled { get; set; } = true;

    private void CreateRainStreaks()
    {
        if (RenderingServer.GetRenderingDevice() == null) return;
        _streakCompositor = GD.Load<GDScript>("res://scripts/render/RingRainStreaksComposite.gd")?.New().AsGodotObject() as CompositorEffect;
    }

    private void ReleaseRainStreaks()
    {
        _streakPublishedActive = false;
        if (_streakCompositor == null) return;
        DetachRainStreaksCompositor();
        _streakArgs[0] = new byte[StreakPayloadBytes]; _streakArgs[1] = new int[4]; _streakArgs[2] = default; _streakArgs[3] = default; _streakArgs[4] = false; _streakArgs[5] = Variant.From(new Rid());
        _streakCompositor.Call(PublishStreakFrameName, _streakArgs);
        _streakCompositor.Dispose(); _streakCompositor = null;
        _streakMarkersEnabled = null;
    }

    private void DetachRainStreaksCompositor()
    {
        if (_streakCompositor == null || _rainHazeWorldEnvironment == null || !GodotObject.IsInstanceValid(_rainHazeWorldEnvironment) || _rainHazeWorldEnvironment.Compositor == null) return;
        var effects = _rainHazeWorldEnvironment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        if (effects.Remove(_streakCompositor)) _rainHazeWorldEnvironment.Compositor.CompositorEffects = effects;
    }

    private static float SmoothStepUnit(float edge0, float edge1, float x) => Mathf.SmoothStep(edge0, edge1, x);

    /// <summary>Storm level D at the observer (CPU mirror of the storm-light map value), cached at 10 Hz.</summary>
    private float ResolveStreakStormLevel(RingCanonicalPosition observer)
    {
        if (StormLevelOverride.HasValue) return Mathf.Clamp(StormLevelOverride.Value, 0.0f, 1.0f);
        if (_sunshineBridge == null) return 0.0f;
        double now = Time.GetTicksMsec() / 1000.0;
        if (now - _streakStormTime >= 0.1)
        {
            _streakStormTime = now;
            _streakStormLevel = Mathf.Clamp(_sunshineBridge.GetStormLevelAt(observer.AroundMeters, observer.AcrossMeters), 0.0f, 1.0f);
        }
        return _streakStormLevel;
    }

    /// <summary>
    /// Lit radiance of a streak: sky ambient (storm ambient A(D), night) plus the sun (cloud shadow transmittance, storm shadow fade F(D), night). No emissive term: night and
    /// storm shade drive it to black, where the shader fades the streak out. Lightning adds its own flash separately (pulse).
    /// </summary>
    private Vector3 ComputeStreakRadiance(RingCanonicalPosition observer) => ComputeStreakLight(observer, out _, out _);

    /// <summary>The scalar radiance plus its two terms: <paramref name="sky"/> (ambient before the storm dimming) and <paramref name="sun"/> (direct, before the storm shadow fade). The shader re-weights them per streak from the storm map.</summary>
    private Vector3 ComputeStreakLight(RingCanonicalPosition observer, out Vector3 sky, out Vector3 sun)
    {
        if (StreakLightOverride.HasValue) { sky = StreakLightOverride.Value; sun = Vector3.Zero; return StreakLightOverride.Value; }
        float daylight = Mathf.Clamp(_rainHazeDayNight?.DaylightFactor ?? 1.0f, 0.0f, 1.0f);
        float stormLevel = ResolveStreakStormLevel(observer);
        float darkness = _sunshineBridge != null ? _sunshineBridge.StormDarkness : 0.85f;
        float fadeStart = _sunshineBridge != null ? _sunshineBridge.StormShadowFade : 0.35f;
        float ambient = 1.0f - 0.9f * darkness * stormLevel;
        float shadowFade = stormLevel <= 0.0f ? 0.0f : SmoothStepUnit(fadeStart, fadeStart + RingSunshineCloudBridge.StormShadowFadeWidth, stormLevel);
        float sunTransmittance = _sunshineBridge != null ? Mathf.Clamp(_sunshineBridge.CameraDirectSunTransmittance, 0.0f, 1.0f) : 1.0f;
        _streakStarLight ??= GetNodeOrNull<DirectionalLight3D>("../StarLight");
        Color sunColor = _streakStarLight != null && GodotObject.IsInstanceValid(_streakStarLight) ? _streakStarLight.LightColor.SrgbToLinear() : new Color(1.0f, 0.96f, 0.9f);
        float sunEnergy = _streakStarLight != null && GodotObject.IsInstanceValid(_streakStarLight) ? Mathf.Clamp(_streakStarLight.LightEnergy, 0.0f, 2.0f) : 1.0f;
        sky = new Vector3(0.36f, 0.44f, 0.56f) * daylight;
        sun = new Vector3(sunColor.R, sunColor.G, sunColor.B) * (sunEnergy * 0.30f * sunTransmittance * daylight);
        return sky * ambient + sun * (1.0f - shadowFade);
    }

    /// <summary>
    /// Local rain intensity field: a 7x7 grid (40 m spacing) around the observer of the real weather query. Stored as the player's smoothed intensity plus the difference of the
    /// grid's intensity to the centre's, so with a uniform sky the field is exactly the player's intensity and at a storm edge the clear side drops to zero.
    /// </summary>
    private readonly float[] _streakGridPending = new float[StreakGridCells], _streakGridRaw = new float[StreakGridCells];
    private int _streakGridSweep = -1;
    private RingCanonicalPosition _streakGridSweepOrigin;
    private float _streakGridCentreRaw;
    private Vector3 _streakGridCentreWorld;
    private const int StreakGridCellsPerFrame = 3;
    private bool _streakGridMirror;
    private float _streakGridFieldMax;

    private void RefreshStreakGrid(RingCanonicalPosition observer, float centreIntensity, double time, RingLodGeometrySnapshot geometry)
    {
        // The sweep is spread over frames (a handful of weather queries each) and re-armed after three seconds of clock or eight metres of movement.
        if (_streakGridSweep < 0)
        {
            bool moved = !_streakGridValid
                || Math.Abs(observer.AroundMeters - _streakGridPosition.AroundMeters) > 8.0
                || Math.Abs(observer.AcrossMeters - _streakGridPosition.AcrossMeters) > 8.0
                || Math.Abs(observer.VerticalMeters - _streakGridPosition.VerticalMeters) > 50.0;
            bool revised = _weatherSource!.ManualWeatherRevision != _streakGridManualRevision;
            if (moved || revised || time - _streakGridTime >= 3.0 || time < _streakGridTime) { _streakGridSweep = 0; _streakGridSweepOrigin = observer; _streakGridTime = time; _streakGridManualRevision = _weatherSource.ManualWeatherRevision; }
        }
        if (_streakGridSweep >= 0)
        {
            ulong start = Time.GetTicksUsec();
            float startThreshold = Mathf.Clamp(PrecipitationStart, 0.0f, 0.99f);
            int side = RainStreakLattice.GridSide, half = side / 2;
            // The bridge's CPU mirror of the macro precipitation costs microseconds per cell, so the whole sweep runs in one frame; without it (camera-dependent clouds, a test observer
            // override) each weather query costs ~0.15 ms and the sweep is spread over frames.
            bool mirror = StreakGridMirrorEnabled && StreakObserverOverride == null && _sunshineBridge != null && _sunshineBridge.PrecipitationMirrorAvailable;
            _streakGridMirror = mirror;
            int perFrame = mirror ? StreakGridCells : StreakGridCellsPerFrame;
            for (int n = 0; n < perFrame && _streakGridSweep < StreakGridCells; n++, _streakGridSweep++)
            {
                int u = _streakGridSweep % side, v = _streakGridSweep / side;
                var position = new RingCanonicalPosition(
                    _streakGridSweepOrigin.AroundMeters + (u - half) * RainStreakLattice.GridSpacingMeters, _streakGridSweepOrigin.VerticalMeters,
                    Math.Clamp(_streakGridSweepOrigin.AcrossMeters + (v - half) * RainStreakLattice.GridSpacingMeters, 0.0, Math.Max(1.0, _grid!.RingAcrossWidthMeters)));
                float potential = mirror ? _sunshineBridge!.GetPrecipitationPotentialAt(position.AroundMeters, position.AcrossMeters) : _weatherSource!.QueryLocalWeather(position).PrecipitationPotential;
                _streakGridPending[_streakGridSweep] = SmoothUnit((potential - startThreshold) / (1.0f - startThreshold));
            }
            if (_streakGridSweep >= StreakGridCells)
            {
                Array.Copy(_streakGridPending, _streakGridRaw, StreakGridCells);
                _streakGridCentreRaw = _streakGridPending[StreakGridCells / 2];
                _streakGridPosition = _streakGridSweepOrigin; _streakGridValid = true; _streakGridSweep = -1;
                _streakGridCentreWorld = geometry.CanonicalToWorld(_streakGridPosition.AroundMeters, _streakGridPosition.VerticalMeters, _streakGridPosition.AcrossMeters);
            }
            LastStreakGridMilliseconds = (Time.GetTicksUsec() - start) / 1000.0;
            _streakGridTotalMs += LastStreakGridMilliseconds;
        }
        if (!_streakGridValid) { for (int i = 0; i < StreakGridCells; i++) _streakGrid[i] = centreIntensity; _streakGridCentreWorld = geometry.CanonicalToWorld(observer.AroundMeters, observer.VerticalMeters, observer.AcrossMeters); return; }
        for (int i = 0; i < StreakGridCells; i++) _streakGrid[i] = Mathf.Clamp(centreIntensity + (_streakGridRaw[i] - _streakGridCentreRaw), 0.0f, 1.0f);
        if (StreakFieldOverride is { Length: StreakGridCells } injected) Array.Copy(injected, _streakGrid, StreakGridCells);
    }

    private void UpdateRainStreaks()
    {
        ulong updateStart = Time.GetTicksUsec();
        UpdateRainStreaksCore();
        _streakUpdateTotalMs += (Time.GetTicksUsec() - updateStart) / 1000.0; _streakUpdateFrames++;
    }

    private void UpdateRainStreaksCore()
    {
        if (_streakCompositor == null || _explorer == null || _grid == null || _weatherSource == null || _rainHazeCamera == null || !GodotObject.IsInstanceValid(_rainHazeCamera)) return;
        RingRainHazeSettings settings = RingRainHazeSettings.Current;
        float intensity = float.IsFinite(PrecipitationIntensity) ? Mathf.Clamp(PrecipitationIntensity, 0, 1) : 0;
        float densitySetting = FiniteHaze(settings.StreakDensity, 0, 2, 1);
        float brightness = FiniteHaze(settings.StreakBrightness, 0, 1, .5f);
        bool eligible = _activeRainHazeOwner == _rainHazeOwner && Enabled && !UndergroundSuspended && _weatherSource.Enabled
            && !Snowing && _explorer.IsCameraUnderwater != true && _rainHazeCamera.Current && _rainHazeWorldEnvironment != null;
        // The local field gates every drop column, so rain from a storm edge a few metres away still draws while the observer itself stands in clear air.
        // The grid costs weather queries, so it only runs while a rain-capable sky is overhead (cloud coverage) or it already rains here.
        float fieldMax = 0.0f;
        StormArrivalState arrival = StreakArrivalOverride ?? StormArrivalMath.Observer;
        float fatDrop = arrival.Valid ? Mathf.Clamp(arrival.FatDrop, 0.0f, 1.0f) : 0.0f;
        bool settingsOn = densitySetting > 0 && brightness > 0 && PrecipitationStreakFactor > 0;
        if (eligible && settingsOn && (intensity > .005f || LastResolvedWeather.CloudCoverage > 0.3f || _streakGridFieldMax > .005f))
        {
            RefreshStreakGrid(StreakObserverOverride ?? _explorer.CanonicalPosition, intensity, _weatherSource.CloudSimulationTimeSeconds, _grid.CaptureLodGeometrySnapshot());
            foreach (float g in _streakGrid) fieldMax = Math.Max(fieldMax, g);
        }
        else if (fatDrop > 0.01f) Array.Clear(_streakGrid);   // fat drops draw below the start threshold: a dry local field, never a stale one
        _streakGridFieldMax = fieldMax;
        bool active = eligible && settingsOn && Math.Max(Math.Max(intensity, fieldMax), fatDrop * 0.2f) > .005f;
        bool diagnostics = RingPerformanceGpu.Enabled;
        string begin = RingPerformanceGpu.RainStreaksBeginMarker, end = RingPerformanceGpu.RainStreaksEndMarker;
        if (_streakMarkersEnabled != diagnostics || _streakBeginMarker != begin || _streakEndMarker != end)
        {
            _streakCompositor.Call(PublishStreakMarkersName, diagnostics, begin, end);
            _streakMarkersEnabled = diagnostics; _streakBeginMarker = begin; _streakEndMarker = end;
        }
        if (!active)
        {
            if (_streakPublishedActive)
            {
                _streakArgs[0] = new byte[StreakPayloadBytes]; _streakArgs[1] = new int[4]; _streakArgs[2] = default; _streakArgs[3] = default; _streakArgs[4] = false; _streakArgs[5] = Variant.From(new Rid());
                _streakCompositor.Call(PublishStreakFrameName, _streakArgs);
                _streakPublishedActive = false;
            }
            return;
        }

        RingCanonicalPosition observer = StreakObserverOverride ?? _explorer.CanonicalPosition;
        double time = _weatherSource.CloudSimulationTimeSeconds;
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        RainStreakLattice.Layers layers = RainStreakLattice.Resolve(MaximumStreakCount);
        int nearBase = RainStreakLattice.BaseCell(observer.AroundMeters, layers.NearCellMeters), nearBaseAcross = RainStreakLattice.BaseCell(observer.AcrossMeters, layers.NearCellMeters);
        int midBase = RainStreakLattice.BaseCell(observer.AroundMeters, layers.MidCellMeters), midBaseAcross = RainStreakLattice.BaseCell(observer.AcrossMeters, layers.MidCellMeters);
        double vertical = observer.VerticalMeters;
        Vector3 nearOrigin = geometry.CanonicalToWorld(nearBase * (double)layers.NearCellMeters, vertical, nearBaseAcross * (double)layers.NearCellMeters);
        Vector3 midOrigin = geometry.CanonicalToWorld(midBase * (double)layers.MidCellMeters, vertical, midBaseAcross * (double)layers.MidCellMeters);
        Vector3 centre = geometry.CanonicalToWorld(observer.AroundMeters, vertical, observer.AcrossMeters);
        Vector3 aroundAxis = (geometry.CanonicalToWorld(observer.AroundMeters + 10.0, vertical, observer.AcrossMeters) - centre).Normalized();
        Vector3 acrossAxis = (geometry.CanonicalToWorld(observer.AroundMeters, vertical, observer.AcrossMeters + 10.0) - centre).Normalized();
        Vector3 upAxis = (geometry.CanonicalToWorld(observer.AroundMeters, vertical + 10.0, observer.AcrossMeters) - centre).Normalized();
        float nearPhase = RainStreakLattice.FallPhase(time, vertical, RainStreakLattice.NearPeriodMeters), midPhase = RainStreakLattice.FallPhase(time, vertical, RainStreakLattice.MidPeriodMeters);
        double cloudBase = PrecipitationCloudBaseMeters;
        float cloudBaseRelative = PrecipitationPhase == PrecipitationPhase.Below && double.IsFinite(cloudBase) ? (float)(cloudBase - vertical) : 1.0e6f;

        RingWindField.PublishedFrame gust = _weatherSource.SharedWindField.CapturePublishedFrame();
        Texture2D? gustTexture = gust.Texture ?? _rainHazeNeutral;
        Vector2 meanWind = CurrentWindSnapshot.Enabled && CurrentWindSnapshot.MeanWind.IsFinite() ? CurrentWindSnapshot.MeanWind : Vector2.Zero;
        Vector2 slope = RainStreakLattice.Slope(meanWind);
        _lastStreakSlope = slope.Length();

        var occlusion = RingPrecipitationOcclusion.Instance;
        bool mapReady = occlusion != null && occlusion.Ready && occlusion.PublishedTexture != null && PrecipitationPhase == PrecipitationPhase.Below;
        Transform3D map = mapReady ? occlusion!.PublishedTransform : Transform3D.Identity;
        Texture2D? occlusionTexture = mapReady ? occlusion!.PublishedTexture : _rainHazeNeutral;

        Vector3 radiance = ComputeStreakLight(observer, out Vector3 skyLight, out Vector3 sunLight);
        // Per-streak storm light: the shader reads the storm level from the macro weather map at each streak (the ringworld_storm_* globals, republished in the payload because a raster pass
        // cannot read globals). Test overrides and an unavailable map fall back to the scalar radiance at the observer.
        Rid stormRid = default; Projection stormFrame = default; Vector4 stormMapFrame = default, stormMapSize = default, stormLight = default;
        bool perStreakLight = !StreakLightOverride.HasValue && !StormLevelOverride.HasValue && _sunshineBridge != null
            && _sunshineBridge.TryGetStormShaderState(out stormRid, out stormFrame, out stormMapFrame, out stormMapSize, out stormLight);
        _streakPerStreakLight = perStreakLight;
        _lastStreakRadiance = radiance;
        float pulse = Mathf.Clamp(CurrentRenderedLightningPulse * CurrentRenderedLightningVisualWeight * (1.0f - Mathf.Clamp(UndergroundFlashSuppression, 0, 1)), 0, 1);
        Color lightning = LightningColor.SrgbToLinear();
        float splashFraction = Mathf.Clamp(0.3f * FiniteHaze(settings.SplashAmount, 0, 2, 1), 0.0f, 1.0f);
        float lengthSetting = FiniteHaze(settings.StreakLength, 0.5f, 2, 1);
        int drops = densitySetting > 1.0f ? 2 : 1;

        byte[] data = _streakPayload; int offset = 0;
        void V(float x, float y, float z, float w)
        {
            BitConverter.TryWriteBytes(data.AsSpan(offset, 4), x); BitConverter.TryWriteBytes(data.AsSpan(offset + 4, 4), y);
            BitConverter.TryWriteBytes(data.AsSpan(offset + 8, 4), z); BitConverter.TryWriteBytes(data.AsSpan(offset + 12, 4), w); offset += 16;
        }
        void V3(Vector3 v, float w) => V(v.X, v.Y, v.Z, w);
        V3(nearOrigin, nearPhase);
        V3(midOrigin, RainStreakLattice.MidPeriodMeters);
        V3(aroundAxis, RainStreakLattice.FallSpeedMetersPerSecond);
        V3(acrossAxis, cloudBaseRelative);
        V3(upAxis, midPhase);
        V(layers.NearCellMeters, layers.NearCellsPerSide, RainStreakLattice.NearRadiusMeters, nearBase);
        V(nearBaseAcross, 1017, drops, RainStreakLattice.NearPeriodMeters);
        V(layers.MidCellMeters, layers.MidCellsPerSide, RainStreakLattice.MidRadiusMeters, midBase);
        V(midBaseAcross, 5417, drops, RainStreakLattice.SplashMidRadiusMeters);
        V(densitySetting * PrecipitationStreakFactor,
            RainStreakLattice.StreakLengthMeters * lengthSetting,
            Mathf.Clamp(brightness, 0, 1), 0);
        V(RainStreakLattice.StreakWidthMeters, RainStreakLattice.MaximumSlope, RainStreakLattice.MinimumWidthPixels, gust.Enabled && gustTexture != null ? 1 : 0);
        V(meanWind.X, meanWind.Y, gust.Base, gust.Strength);
        V3(gust.Anchor, 0); V(gust.Around.X, gust.Around.Y, gust.Around.Z, gust.Around.W); V(gust.Across.X, gust.Across.Y, gust.Across.Z, gust.Across.W);
        V(gust.Uv.X, gust.Uv.Y, 0, 0);
        V3(map.Origin, mapReady ? occlusion!.PublishedFarMeters : 0);
        V3(map.Basis.Y, PrecipitationOcclusionFrame.HalfExtentMeters);
        V3(map.Basis.X, PrecipitationOcclusionFrame.TexelMeters);
        V(mapReady ? 1 : 0, PrecipitationOcclusionFrame.Resolution, 0, 0);
        V3(radiance, pulse);
        V(lightning.R, lightning.G, lightning.B, 0);
        V(RainStreakLattice.SplashLifeSeconds, RainStreakLattice.SplashStartRadiusMeters, RainStreakLattice.SplashEndRadiusMeters, splashFraction);
        V3(_streakGridCentreWorld, RainStreakLattice.GridSpacingMeters);
        for (int i = 0; i < 28; i += 4)
            V(i < StreakGridCells ? _streakGrid[i] : 0, i + 1 < StreakGridCells ? _streakGrid[i + 1] : 0, i + 2 < StreakGridCells ? _streakGrid[i + 2] : 0, i + 3 < StreakGridCells ? _streakGrid[i + 3] : 0);
        void V4(Vector4 v) => V(v.X, v.Y, v.Z, v.W);
        V4(stormFrame.X); V4(stormFrame.Y); V4(stormFrame.Z); V4(stormFrame.W);
        V4(stormMapFrame); V4(stormMapSize); V4(perStreakLight ? stormLight : Vector4.Zero);
        V3(skyLight, perStreakLight ? 1.0f : 0.0f);
        V3(sunLight, 0);
        _lastStreakFatDrop = fatDrop; _lastStreakHail = arrival.Valid ? arrival.HailIntensity : 0.0f;
        float hailIntensity = arrival.Valid ? Mathf.Clamp(arrival.HailIntensity, 0.0f, 1.0f) : 0.0f;
        V(arrival.Valid ? fatDrop : 0.0f, arrival.Valid ? Mathf.Clamp(arrival.Core, 0.0f, 1.0f) : 0.0f, arrival.Valid ? Mathf.Clamp(arrival.Trailing, 0.0f, 1.0f) : 0.0f, arrival.Valid ? arrival.Severity : 0.0f);
        V(hailIntensity, Mathf.Clamp(arrival.HailSize, 0.0f, 1.0f), RainStreakLattice.HailPhase(time, vertical, RainStreakLattice.HailNearPeriodMeters), RainStreakLattice.HailPhase(time, vertical, RainStreakLattice.HailMidPeriodMeters));

        _streakNearInstances = layers.NearCells * drops;
        _streakMidInstances = layers.MidCells * drops;
        _streakSplashInstances = splashFraction > 0 ? layers.NearCells + layers.MidCells : 0;
        _streakArgs[0] = data.AsSpan(0, StreakPayloadBytes).ToArray();
        _streakArgs[1] = new int[] { _streakNearInstances, _streakMidInstances, splashFraction > 0 ? layers.NearCells : 0, splashFraction > 0 ? layers.MidCells : 0 };
        _streakArgs[2] = gustTexture == null ? default : Variant.From(gustTexture);
        _streakArgs[3] = occlusionTexture == null ? default : Variant.From(occlusionTexture);
        _streakArgs[4] = true;
        _streakArgs[5] = Variant.From(perStreakLight ? stormRid : new Rid());
        _streakCompositor.Call(PublishStreakFrameName, _streakArgs);
        _streakArgs[2].Dispose(); _streakArgs[3].Dispose();
        _streakPublishedActive = true;
    }

    /// <summary>Diagnostic: largest absolute difference between the macro mirror and QueryLocalWeather precipitation over a 7x7 grid at the given spacing around the observer (and the largest value seen).</summary>
    public Godot.Collections.Dictionary CompareStreakPotentialMirror(double spacingMeters)
    {
        var result = new Godot.Collections.Dictionary { ["available"] = false };
        if (_explorer == null || _weatherSource == null || _sunshineBridge == null || !_sunshineBridge.PrecipitationMirrorAvailable) return result;
        RingCanonicalPosition centre = _explorer.CanonicalPosition;
        float maxDiff = 0.0f, maxValue = 0.0f; int samples = 0;
        for (int v = -3; v <= 3; v++) for (int u = -3; u <= 3; u++)
        {
            var p = new RingCanonicalPosition(centre.AroundMeters + u * spacingMeters, centre.VerticalMeters, Math.Clamp(centre.AcrossMeters + v * spacingMeters, 0.0, Math.Max(1.0, _grid!.RingAcrossWidthMeters)));
            float query = _weatherSource.QueryLocalWeather(p).PrecipitationPotential;
            float mirror = _sunshineBridge.GetPrecipitationPotentialAt(p.AroundMeters, p.AcrossMeters);
            maxDiff = Math.Max(maxDiff, Math.Abs(query - mirror)); maxValue = Math.Max(maxValue, Math.Max(query, mirror)); samples++;
        }
        result["available"] = true; result["max_abs_diff"] = maxDiff; result["max_value"] = maxValue; result["samples"] = samples;
        return result;
    }

    public Godot.Collections.Dictionary GetRainStreakDiagnostics()
    {
        RainStreakLattice.Layers layers = RainStreakLattice.Resolve(MaximumStreakCount);
        return new()
        {
            ["compositor_present"] = _streakCompositor != null,
            ["compositor_ready"] = _streakCompositor?.Get("ready").AsBool() ?? false,
            ["compositor_error"] = _streakCompositor?.Get("last_error").AsString() ?? "",
            ["active"] = _streakPublishedActive,
            ["budget"] = MaximumStreakCount,
            ["near_cells"] = layers.NearCells, ["mid_cells"] = layers.MidCells,
            ["near_instances"] = _streakNearInstances, ["mid_instances"] = _streakMidInstances, ["splash_instances"] = _streakSplashInstances,
            ["slope"] = _lastStreakSlope,
            ["fat_drop"] = _lastStreakFatDrop, ["hail"] = _lastStreakHail,
            ["radiance"] = _lastStreakRadiance, ["per_streak_light"] = _streakPerStreakLight,
            ["grid_ms"] = LastStreakGridMilliseconds, ["grid_mirror"] = _streakGridMirror, ["grid_total_ms"] = _streakGridTotalMs, ["update_total_ms"] = _streakUpdateTotalMs, ["grid_frames"] = _streakUpdateFrames, ["grid_centre"] = _streakGridCentreWorld,
            ["grid"] = (float[])_streakGrid.Clone(), ["grid_sweep"] = _streakGridSweep, ["grid_valid"] = _streakGridValid,
            ["draws"] = _streakCompositor?.Get("dispatches").AsInt64() ?? 0,
            ["timestamp_scopes"] = _streakCompositor?.Get("timestamp_scopes").AsInt64() ?? 0,
            ["in_chain"] = _rainHazeWorldEnvironment?.Compositor != null && _streakCompositor != null && StreakCompositorBeforeHaze(_rainHazeWorldEnvironment)
        };
    }

    private bool StreakCompositorBeforeHaze(WorldEnvironment environment)
    {
        var effects = environment.Compositor.CompositorEffects;
        using Godot.Collections.Array lifetime = (Godot.Collections.Array)effects;
        int streaks = -1, haze = -1;
        for (int i = 0; i < effects.Count; i++)
        {
            if (effects[i] == _streakCompositor) streaks = i;
            if (effects[i] == _rainHazeCompositor) haze = i;
        }
        return streaks >= 0 && haze == streaks + 1 && haze == effects.Count - 1;
    }
}
