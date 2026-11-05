using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>Small, independent thunder consumer for the shared lightning event stream.</summary>
public partial class RingWeatherAudio : Node
{
    [Export] public bool Enabled { get; set; } = true;
    [Export(PropertyHint.Range, "-48,12,0.5")] public float ThunderVolumeDb { get; set; } = -8.0f;
    [Export] public AudioStream[] ThunderSamples { get; set; } = Array.Empty<AudioStream>();
    /// <summary>Thunder heard within <see cref="NearMaxMeters"/>; empty fills by convention from thunder_near_NN.ogg.</summary>
    [Export] public AudioStream[] ThunderNearSamples { get; set; } = Array.Empty<AudioStream>();
    /// <summary>Thunder heard beyond <see cref="FarMinMeters"/>; empty fills by convention from thunder_far_NN.ogg.</summary>
    [Export] public AudioStream[] ThunderFarSamples { get; set; } = Array.Empty<AudioStream>();
    public const double NearMaxMeters = 1500.0;
    public const double FarMinMeters = 8000.0;
    [Export(PropertyHint.Range, "1000,100000,1000")] public float MaxAudibleDistanceMeters { get; set; } = 25_000.0f;
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");

    /// <summary>Megastorm airborne gate (1 with no megastorm): thunder that arrives while it is 0 is not heard (sound cannot outrun the wall), and a playing voice follows it. <see cref="RingMegastormAudio"/> pushes it every frame.</summary>
    public float MegastormAirGate { get; set; } = 1f;
    /// <summary>Underground-mode enclosure blend (0..1): thunder is low-passed and attenuated by 12 dB, never muted or restarted.</summary>
    public float UndergroundBlend { get; set; }
    /// <summary>Cover muffle from the rain sound field (0..1 maps to 0..16 dB); the stronger of this and the underground blend wins. The ambience node pushes it every frame.</summary>
    public float ShelterMuffleAmount { get; set; }
    /// <summary>Cutoff the current shelter class allows thunder (Hz); the lower of this and the distance/underground cutoff wins.</summary>
    public float ShelterCutoffHz { get; set; } = 18_000.0f;
    private readonly ThunderPropagation _propagation = new();
    private readonly List<ThunderArrival> _arrivals = new();
    private readonly List<AudioStreamPlayer3D> _voices = new();
    private readonly Dictionary<AudioStreamPlayer3D, ThunderArrival> _voiceEvents = new();
    private readonly HashSet<long> _activeIds = new();
    private readonly Queue<long> _suppressionOrder = new();
    private RingVolumetricCloudLayer? _weather;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private double _audioClock;
    private double _lastWeatherTime = double.NaN;
    private int _playedCount;
    private long _lastId;
    private double _lastDistance;
    private double _lastDelay;
    private double _lastStartAudioSeconds;
    private double _lastSeekSeconds;
    private RingCanonicalPosition _listener;
    private bool _canHear;
    private bool _suspended;
    private bool _warnedMissingAssets;
    private int _resetCount;
    private readonly HashSet<long> _suppressedIds = new();
    private readonly HashSet<long> _observedActiveIds = new();
    private ulong _lastTicks;
    private string _lastPool = "mid";

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        if (ThunderSamples.Length == 0)
        {
            var samples = new List<AudioStream>();
            for (int index = 1; index <= 3; index++)
            {
                string path = $"res://resources/audio/weather/thunder_{index:00}.ogg";
                if (!ResourceLoader.Exists(path)) continue;
                AudioStream? stream = ResourceLoader.Load<AudioStream>(path);
                if (stream != null && double.IsFinite(stream.GetLength()) && stream.GetLength() > 0.0)
                    samples.Add(stream);
            }
            ThunderSamples = samples.ToArray();
        }
        if (ThunderNearSamples.Length == 0) ThunderNearSamples = LoadPool("thunder_near");
        if (ThunderFarSamples.Length == 0) ThunderFarSamples = LoadPool("thunder_far");
        _lastTicks = Time.GetTicksUsec();
        for (int i = 0; i < 8; i++)
        {
            var voice = new AudioStreamPlayer3D
            {
                Name = $"ThunderVoice{i + 1}", Bus = "Thunder", MaxDistance = ResolveMaximumDistance(),
                UnitSize = 1000, VolumeDb = ResolveVolume(), MaxPolyphony = 1, PitchScale = 1.0f
            };
            AddChild(voice);
            _voices.Add(voice);
        }
    }

    public override void _Process(double delta)
    {
        ulong ticks = Time.GetTicksUsec();
        if (GetTree().Paused)
        {
            SetVoicePause(true);
            _lastTicks = ticks;
            return;
        }
        SetVoicePause(false);
        double realDelta = _lastTicks == 0 ? 0.0 : (ticks - _lastTicks) / 1_000_000.0;
        _lastTicks = ticks;
        if (!Enabled || _weather == null || _explorer == null || _grid == null
            || !_weather.Enabled || !_weather.EnableSharedLightning)
        {
            if (!_suspended) Clear(true);
            else SuppressActiveEvents();
            _suspended = true;
            _canHear = false;
            return;
        }
        _suspended = false;
        // This clock deliberately follows only real active scene playback, never weather/day-night time or Engine.TimeScale.
        _audioClock += Math.Max(0.0, realDelta);
        double weatherNow = _weather.CloudSimulationTimeSeconds;
        if (!double.IsFinite(weatherNow))
        {
            Clear(true);
            _canHear = false;
            return;
        }
        if (double.IsFinite(_lastWeatherTime)
            && (weatherNow < _lastWeatherTime || weatherNow - _lastWeatherTime > 5.0)) Clear(true);
        _lastWeatherTime = weatherNow;
        ThunderAcousticContext context = ResolveAcousticContext();
        _listener = ResolveCameraListener();
        _canHear = !_explorer.IsCameraUnderwater && ThunderPropagation.TryDistance(_listener, _listener, context, out _);
        if (!_canHear) StopVoices();
        foreach (AudioStreamPlayer3D voice in _voices)
        {
            if (!voice.Playing || !_voiceEvents.TryGetValue(voice, out ThunderArrival state)
                || !ThunderPropagation.TryDistance(state.SourcePosition, _listener, context, out double distance))
            {
                voice.Stop();
                _voiceEvents.Remove(voice);
                continue;
            }
            voice.GlobalPosition = _grid.CanonicalRingPositionToWorld(state.SourcePosition);
            ApplyVoiceMix(voice, distance, state.Strike.Energy);
        }
        _activeIds.Clear();
        foreach (RingLightningEvent active in _weather.ActiveLightningEvents) _activeIds.Add(active.EventId);
        _observedActiveIds.RemoveWhere(id => !_activeIds.Contains(id));
        foreach (RingLightningEvent strike in _weather.ActiveLightningEvents)
        {
            if (_suppressedIds.Contains(strike.EventId) || !_observedActiveIds.Add(strike.EventId)) continue;
            double weatherAge = weatherNow - strike.StartTimeSeconds;
            if (!double.IsFinite(weatherAge) || weatherAge < 0.0 || weatherAge > strike.LifeSeconds) continue;
            double timeScale = double.IsFinite(Engine.TimeScale) ? Math.Max(0.001, Engine.TimeScale) : 1.0;
            double realAge = weatherAge / timeScale;
            _propagation.Observe(strike, _audioClock - realAge, _listener, context);
        }
        _arrivals.Clear();
        _propagation.Advance(_audioClock, _listener, context, _canHear, _arrivals);
        foreach (ThunderArrival arrival in _arrivals) Play(arrival);
    }

    public override void _ExitTree()
    {
        Clear();
        _suppressedIds.Clear();
        _suppressionOrder.Clear();
    }

    public Godot.Collections.Dictionary GetThunderDiagnostics() => new()
    {
        ["pending_count"] = _propagation.PendingCount, ["history_count"] = _propagation.HistoryCount,
        ["voice_count"] = _voices.Count, ["active_voice_count"] = _voices.Count(v => v.Playing),
        ["asset_count"] = ThunderSamples.Count(s => s != null && s.GetLength() > 0.0),
        ["near_asset_count"] = ThunderNearSamples.Count(s => s != null && s.GetLength() > 0.0),
        ["far_asset_count"] = ThunderFarSamples.Count(s => s != null && s.GetLength() > 0.0), ["last_pool"] = _lastPool,
        ["reset_count"] = _resetCount, ["can_hear"] = _canHear, ["played_count"] = _playedCount,
        ["last_id"] = _lastId, ["last_distance_meters"] = _lastDistance,
        ["last_delay_seconds"] = _lastDelay, ["audio_clock"] = _audioClock,
        ["last_start_audio_seconds"] = _lastStartAudioSeconds, ["last_seek_seconds"] = _lastSeekSeconds,
        ["suppressed_count"] = _suppressedIds.Count, ["observed_count"] = _observedActiveIds.Count,
        ["listener_around"] = _listener.AroundMeters, ["listener_vertical"] = _listener.VerticalMeters,
        ["listener_across"] = _listener.AcrossMeters
    };

    private void Play(in ThunderArrival arrival)
    {
        if (arrival.Strike.Energy <= 0.0f || !(MegastormAirGate > 0.001f)) return;
        AudioStream? stream = LoadSample(arrival.Strike.ShapeSeed, arrival.DistanceMeters, out string pool);
        if (stream == null) return;
        if (!double.IsFinite(stream.GetLength()) || arrival.LateSeconds >= stream.GetLength()) return;
        AudioStreamPlayer3D? voice = _voices.Find(v => !v.Playing);
        if (voice == null) return;
        voice.Stream = stream;
        voice.GlobalPosition = _grid!.CanonicalRingPositionToWorld(arrival.SourcePosition);
        _voiceEvents[voice] = arrival;
        ApplyVoiceMix(voice, arrival.DistanceMeters, arrival.Strike.Energy);
        voice.Play((float)Math.Max(0.0, arrival.LateSeconds));
        _playedCount++;
        _lastPool = pool;
        _lastId = arrival.Strike.EventId;
        _lastDistance = arrival.DistanceMeters;
        _lastDelay = arrival.TravelSeconds;
        _lastStartAudioSeconds = _audioClock;
        _lastSeekSeconds = arrival.LateSeconds;
    }

    private void ApplyVoiceMix(AudioStreamPlayer3D voice, double distance, float energy)
    {
        voice.MaxDistance = ResolveMaximumDistance();
        voice.VolumeDb = ResolveVolume() + Mathf.LinearToDb(Mathf.Clamp(energy, 0.01f, 1.0f))
            + (float)Math.Clamp(-distance / 3_000.0, -18.0, 0.0);
        float gate = float.IsFinite(MegastormAirGate) ? Mathf.Clamp(MegastormAirGate, 0.0f, 1.0f) : 1.0f;
        if (gate < 1.0f) voice.VolumeDb += gate > 0.001f ? Mathf.LinearToDb(gate) : -80.0f;
        float distant = Mathf.SmoothStep(500.0f, 10_000.0f, (float)distance);
        float underground = Mathf.Clamp(UndergroundBlend, 0.0f, 1.0f);
        float shelter = float.IsFinite(ShelterMuffleAmount) ? Mathf.Clamp(ShelterMuffleAmount, 0.0f, 1.0f) : 0.0f;
        float muffle = Mathf.Max(underground, shelter);
        voice.VolumeDb -= Mathf.Max(12.0f * underground, 16.0f * shelter);
        float cutoff = Mathf.Lerp(Mathf.Lerp(18_000.0f, 1_000.0f, distant), 600.0f, muffle);
        if (float.IsFinite(ShelterCutoffHz) && ShelterCutoffHz > 0.0f) cutoff = Mathf.Min(cutoff, ShelterCutoffHz);
        voice.AttenuationFilterCutoffHz = cutoff;
        voice.AttenuationFilterDb = Mathf.Lerp(Mathf.Lerp(0.0f, -24.0f, distant), -24.0f, muffle);
    }

    private float ResolveMaximumDistance() => float.IsFinite(MaxAudibleDistanceMeters)
        ? Mathf.Clamp(MaxAudibleDistanceMeters, 1000.0f, 100_000.0f) : 25_000.0f;

    private float ResolveVolume() => float.IsFinite(ThunderVolumeDb)
        ? Mathf.Clamp(ThunderVolumeDb, -80.0f, 12.0f) : -8.0f;

    private ThunderAcousticContext ResolveAcousticContext() => new(
        _grid!.GetCanonicalRingCircumferenceMeters64(), _grid.RingAcrossWidthMeters,
        RingAtmosphereModel.UpperHeightMeters, ResolveMaximumDistance(),
        MinimumAltitudeMeters: -RingAtmosphereModel.FloorDepthMeters,
        DatumInwardOffsetMeters: _grid.RingVerticalBaseOffsetMeters);

    private static AudioStream[] LoadPool(string prefix)
    {
        var samples = new List<AudioStream>();
        for (int index = 1; index <= 8; index++)
        {
            string path = $"res://resources/audio/weather/{prefix}_{index:00}.ogg";
            if (!ResourceLoader.Exists(path)) break;
            AudioStream? stream = ResourceLoader.Load<AudioStream>(path);
            if (stream != null && double.IsFinite(stream.GetLength()) && stream.GetLength() > 0.0) samples.Add(stream);
        }
        return samples.ToArray();
    }

    private AudioStream? LoadSample(int shapeSeed, double distanceMeters, out string pool)
    {
        AudioStream[] samples = ThunderSamples;
        pool = "mid";
        if (distanceMeters < NearMaxMeters && ThunderNearSamples.Length > 0) { samples = ThunderNearSamples; pool = "near"; }
        else if (distanceMeters > FarMinMeters && ThunderFarSamples.Length > 0) { samples = ThunderFarSamples; pool = "far"; }
        AudioStream? stream = samples.Length == 0 ? null : samples[Math.Abs(shapeSeed % samples.Length)];
        if (stream == null && !_warnedMissingAssets) { GD.PushWarning("Thunder audio assets are missing; thunder playback is safely disabled."); _warnedMissingAssets = true; }
        return stream;
    }

    private RingCanonicalPosition ResolveCameraListener()
    {
        RingCanonicalPosition listener = _explorer!.CanonicalPosition;
        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null) return listener;
        if (!_explorer.IsAncestorOf(camera)) return _grid!.WorldToCanonicalRingPosition64(camera.GlobalPosition);

        // Accumulate local transforms, not two large translated world positions.
        Transform3D relative = Transform3D.Identity;
        Node3D? node = camera;
        while (node != null && node != _explorer)
        {
            relative = node.Transform * relative;
            node = node.GetParentOrNull<Node3D>();
        }
        if (node == null) return _grid!.WorldToCanonicalRingPosition64(camera.GlobalPosition);
        RingLodGeometrySnapshot geometry = _grid!.CaptureLodGeometrySnapshot();
        Vector3 offset = geometry.GridGlobalTransform.Basis.Inverse() * (_explorer.GlobalBasis * relative.Origin);
        double angle = listener.AroundMeters / geometry.RingRadiusMeters;
        double radial = geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - listener.VerticalMeters;
        double radialOffset = offset.X * Math.Cos(angle) + offset.Z * Math.Sin(angle);
        double tangentOffset = -offset.X * Math.Sin(angle) + offset.Z * Math.Cos(angle);
        double cameraRadial = Math.Sqrt((radial + radialOffset) * (radial + radialOffset) + tangentOffset * tangentOffset);
        if (radial <= 0.0 || cameraRadial <= 0.0) return listener;
        // Stable radial difference retains a metre-sized camera offset on Mega.
        double heightOffset = -(2.0 * radial * radialOffset + radialOffset * radialOffset + tangentOffset * tangentOffset)
            / (radial + cameraRadial);
        return new RingCanonicalPosition(
            listener.AroundMeters + geometry.RingRadiusMeters * Math.Atan2(tangentOffset, radial + radialOffset),
            listener.VerticalMeters + heightOffset, listener.AcrossMeters + offset.Y).Wrap(geometry.CircumferenceMeters);
    }

    private void StopVoices()
    {
        foreach (AudioStreamPlayer3D voice in _voices) voice.Stop();
        _voiceEvents.Clear();
    }

    private void SetVoicePause(bool paused)
    {
        foreach (AudioStreamPlayer3D voice in _voices) voice.StreamPaused = paused;
    }

    private void Clear(bool suppressActive = false)
    {
        if (suppressActive) SuppressActiveEvents();
        _propagation.Reset();
        _arrivals.Clear();
        _observedActiveIds.Clear();
        _lastWeatherTime = double.NaN;
        _resetCount++;
        StopVoices();
    }

    private void SuppressActiveEvents()
    {
        if (_weather == null) return;
        foreach (RingLightningEvent strike in _weather.ActiveLightningEvents)
        {
            if (!_suppressedIds.Add(strike.EventId)) continue;
            _suppressionOrder.Enqueue(strike.EventId);
            while (_suppressionOrder.Count > ThunderPropagation.MaxHistoryEntries)
                _suppressedIds.Remove(_suppressionOrder.Dequeue());
        }
    }
}
