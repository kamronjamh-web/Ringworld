using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Poi;

/// <summary>
/// The sound of the emitters' field shots, gated and mixed like thunder (<see cref="RingWeatherAudio"/>). Two events per shot: a soft launch thump and whoosh at
/// the beam origin (audible within 8 km) and a distant electrical crackle where the orb meets the containment field (audible within 60 km). Each is a point
/// source whose sound arrives distance / 343 s after its weather-time event, on a real-time audio clock (so a faster or slower weather clock does not stretch the
/// delay); the distance and the air path come from <see cref="ThunderPropagation.TryDistance"/>. The shots are read from <see cref="EmitterFieldShotLayer.Schedule"/>
/// (launches seen as the weather clock passes them); a weather-time jump backwards or by more than 5 s clears everything. The megastorm air gate, the underground
/// blend and the shelter muffle are read from the <c>WeatherAudio</c> node and mixed as <c>ApplyVoiceMix</c> does; nothing plays underwater. Four voices on the
/// Thunder bus; a playing voice is never stolen.
/// </summary>
public partial class RingEmitterFieldShotAudio : Node
{
    public const double SoundSpeedMetersPerSecond = 343.0;
    public const double LaunchRangeMeters = 8_000.0, CrackleRangeMeters = 60_000.0;
    public const float LaunchBaseDb = -14.0f, CrackleBaseDb = -20.0f;
    public const int VoiceCount = 4, MaxPending = 128, SampleCount = 3;
    private const double ExpiryGraceSeconds = ThunderPropagation.ExpiryGraceSeconds;
    private const double MaxWeatherStepSeconds = 5.0;
    private const double AtmosphereAboveApexMeters = 1_000.0;

    public enum EventKind { Launch, Crackle }

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WeatherSourcePath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WeatherAudioPath { get; set; } = new("../WeatherAudio");

    /// <summary>Test seam: replaces the listener position (null = the camera).</summary>
    public RingCanonicalPosition? ListenerOverride { get; set; }
    /// <summary>Test seam: replaces the explorer's underwater state (null = the explorer's).</summary>
    public bool? UnderwaterOverride { get; set; }

    public int PlayedLaunch { get; private set; }
    public int PlayedCrackle { get; private set; }
    public int PendingCount => _pending.Count;
    public int ResetCount { get; private set; }
    public int ActiveVoiceCount => _voices.Count(v => v.Playing);
    public int SampleCountLoaded => _launch.Length + _crackle.Length;
    public double LastLaunchDistanceMeters { get; private set; }
    public double LastLaunchDelaySeconds { get; private set; }
    public double LastCrackleDistanceMeters { get; private set; }
    public double LastCrackleDelaySeconds { get; private set; }
    public double AudioClockSeconds => _audioClock;
    public bool CanHear => _canHear;

    private readonly record struct Pending(EventKind Kind, long ShotId, RingCanonicalPosition Source, double EmissionAudioTime, double ExpiryAudioTime, float Energy);
    private readonly record struct Playing(EventKind Kind, RingCanonicalPosition Source, float Energy);

    private readonly List<Pending> _pending = new();
    private readonly List<EmitterFieldShot> _launches = new();
    private readonly List<AudioStreamPlayer3D> _voices = new();
    private readonly Dictionary<AudioStreamPlayer3D, Playing> _playing = new();
    private AudioStream[] _launch = Array.Empty<AudioStream>(), _crackle = Array.Empty<AudioStream>();
    private RingVolumetricCloudLayer? _weather;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingWeatherAudio? _weatherAudio;
    private double _audioClock;
    private double _lastWeatherTime = double.NaN;
    private ulong _lastTicks;
    private bool _canHear, _suspended;
    private RingCanonicalPosition _listener;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _launch = LoadPool("emitter_orb_launch");
        _crackle = LoadPool("field_contact_crackle");
        _lastTicks = Time.GetTicksUsec();
        for (int i = 0; i < VoiceCount; i++)
        {
            var voice = new AudioStreamPlayer3D { Name = $"FieldShotVoice{i + 1}", Bus = "Thunder", UnitSize = 1000, MaxPolyphony = 1, PitchScale = 1.0f, MaxDistance = (float)CrackleRangeMeters };
            AddChild(voice);
            _voices.Add(voice);
        }
    }

    public override void _ExitTree() => Clear();

    private void Resolve()
    {
        if (_weather == null || !IsInstanceValid(_weather)) _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherSourcePath);
        if (_explorer == null || !IsInstanceValid(_explorer)) _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        if (_grid == null || !IsInstanceValid(_grid)) _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        if (_weatherAudio == null || !IsInstanceValid(_weatherAudio)) _weatherAudio = GetNodeOrNull<RingWeatherAudio>(WeatherAudioPath);
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
        Resolve();
        EmitterFieldShotLayer? shots = EmitterFieldShotLayer.Instance;
        EmitterFieldShotSchedule? schedule = shots?.Schedule;
        if (!Enabled || _weather == null || _grid == null || (_explorer == null && ListenerOverride == null) || !_weather.Enabled || schedule == null)
        {
            if (!_suspended) Clear();
            _suspended = true;
            _canHear = false;
            return;
        }
        _suspended = false;
        // The real active-playback audio clock, never the weather time (as thunder).
        _audioClock += Math.Max(0.0, realDelta);
        double weatherNow = _weather.CloudSimulationTimeSeconds;
        if (!double.IsFinite(weatherNow)) { Clear(); _canHear = false; return; }
        bool jumped = double.IsFinite(_lastWeatherTime) && (weatherNow < _lastWeatherTime || weatherNow - _lastWeatherTime > MaxWeatherStepSeconds);
        if (jumped) Clear();
        ThunderAcousticContext context = ContextFor(CrackleRangeMeters, schedule);
        _listener = ListenerOverride ?? ResolveCameraListener();
        bool underwater = UnderwaterOverride ?? _explorer?.IsCameraUnderwater ?? false;
        _canHear = !underwater && ThunderPropagation.TryDistance(_listener, _listener, context, out _);
        if (!_canHear) StopVoices();
        UpdateVoices(schedule);
        if (!jumped && double.IsFinite(_lastWeatherTime) && weatherNow > _lastWeatherTime)
        {
            schedule.CollectLaunches(_lastWeatherTime, weatherNow, _launches);
            foreach (EmitterFieldShot shot in _launches) Observe(schedule, shot, weatherNow);
        }
        _lastWeatherTime = weatherNow;
        Advance(schedule);
    }

    /// <summary>The acoustic context for an event audible within <paramref name="rangeMeters"/>: the thunder context with the air height raised to the field's apex.</summary>
    public ThunderAcousticContext ContextFor(double rangeMeters, EmitterFieldShotSchedule schedule) => new(
        _grid!.GetCanonicalRingCircumferenceMeters64(), _grid.RingAcrossWidthMeters,
        Math.Max(RingAtmosphereModel.UpperHeightMeters, schedule.Profile.ApexMeters + AtmosphereAboveApexMeters), rangeMeters,
        MinimumAltitudeMeters: -RingAtmosphereModel.FloorDepthMeters,
        DatumInwardOffsetMeters: _grid.RingVerticalBaseOffsetMeters);

    public static double RangeOf(EventKind kind) => kind == EventKind.Launch ? LaunchRangeMeters : CrackleRangeMeters;

    private void Observe(EmitterFieldShotSchedule schedule, in EmitterFieldShot shot, double weatherNow)
    {
        double timeScale = double.IsFinite(Engine.TimeScale) ? Math.Max(0.001, Engine.TimeScale) : 1.0;
        double launchAudio = _audioClock - Math.Max(0.0, weatherNow - shot.LaunchTimeSeconds) / timeScale;
        double crackleAudio = launchAudio + Math.Max(0.0, shot.ArrivalTimeSeconds - shot.LaunchTimeSeconds) / timeScale;
        (double oa, double ov, double ox) = schedule.OriginOf(shot.Beam);
        (double ca, double cv, double cx) = schedule.ContactOf(shot.Beam);
        Enqueue(schedule, new Pending(EventKind.Launch, shot.Id, new RingCanonicalPosition(oa, ov, ox), launchAudio, launchAudio + LaunchRangeMeters / SoundSpeedMetersPerSecond + ExpiryGraceSeconds, shot.Energy));
        Enqueue(schedule, new Pending(EventKind.Crackle, shot.Id, new RingCanonicalPosition(ca, cv, cx), crackleAudio, crackleAudio + CrackleRangeMeters / SoundSpeedMetersPerSecond + ExpiryGraceSeconds, shot.Energy));
    }

    private void Enqueue(EmitterFieldShotSchedule schedule, in Pending pending)
    {
        // Only events that are within their range of the listener now are kept (as thunder's observation gate): most shots ring-wide are never audible.
        if (!ThunderPropagation.TryDistance(pending.Source, _listener, ContextFor(RangeOf(pending.Kind), schedule), out _)) return;
        if (_pending.Count >= MaxPending) _pending.RemoveAt(0);
        _pending.Add(pending);
    }

    /// <summary>Starts every pending event whose sound front has reached the listener; drops the expired ones.</summary>
    private void Advance(EmitterFieldShotSchedule schedule)
    {
        for (int index = _pending.Count - 1; index >= 0; index--)
        {
            Pending p = _pending[index];
            if (_audioClock >= p.ExpiryAudioTime) { _pending.RemoveAt(index); continue; }
            if (!_canHear) continue;
            if (!ThunderPropagation.TryDistance(p.Source, _listener, ContextFor(RangeOf(p.Kind), schedule), out double distance)) continue;
            double travel = distance / SoundSpeedMetersPerSecond;
            double elapsed = Math.Max(0.0, _audioClock - p.EmissionAudioTime);
            if (elapsed < travel) continue;
            _pending.RemoveAt(index);
            Play(p, distance, travel, elapsed - travel);
        }
    }

    private void Play(in Pending p, double distance, double travel, double lateSeconds)
    {
        float gate = AirGate();
        if (p.Energy <= 0.0f || !(gate > 0.001f)) return;
        AudioStream[] pool = p.Kind == EventKind.Launch ? _launch : _crackle;
        if (pool.Length == 0) return;
        AudioStream stream = pool[(int)(Math.Abs(p.ShotId) % pool.Length)];
        if (!double.IsFinite(stream.GetLength()) || lateSeconds >= stream.GetLength()) return;
        AudioStreamPlayer3D? voice = _voices.Find(v => !v.Playing);
        if (voice == null || _grid == null) return;
        voice.Stream = stream;
        voice.GlobalPosition = _grid.CanonicalRingPositionToWorld(p.Source);
        _playing[voice] = new Playing(p.Kind, p.Source, p.Energy);
        ApplyVoiceMix(voice, p.Kind, distance, p.Energy);
        voice.Play((float)lateSeconds);
        if (p.Kind == EventKind.Launch) { PlayedLaunch++; LastLaunchDistanceMeters = distance; LastLaunchDelaySeconds = travel; }
        else { PlayedCrackle++; LastCrackleDistanceMeters = distance; LastCrackleDelaySeconds = travel; }
    }

    /// <summary>Follows each playing voice: its distance, position and mix change as the listener moves; a voice that left the air path or the range stops.</summary>
    private void UpdateVoices(EmitterFieldShotSchedule schedule)
    {
        foreach (AudioStreamPlayer3D voice in _voices)
        {
            if (!voice.Playing || !_playing.TryGetValue(voice, out Playing state)
                || !ThunderPropagation.TryDistance(state.Source, _listener, ContextFor(RangeOf(state.Kind), schedule), out double distance))
            {
                voice.Stop();
                _playing.Remove(voice);
                continue;
            }
            voice.GlobalPosition = _grid!.CanonicalRingPositionToWorld(state.Source);
            ApplyVoiceMix(voice, state.Kind, distance, state.Energy);
        }
    }

    private float AirGate() => _weatherAudio != null && IsInstanceValid(_weatherAudio) && float.IsFinite(_weatherAudio.MegastormAirGate)
        ? Mathf.Clamp(_weatherAudio.MegastormAirGate, 0.0f, 1.0f) : 1.0f;

    /// <summary>The thunder voice mix (distance falloff, distant low-pass, megastorm gate, underground and shelter muffle) at this event's base volume.</summary>
    private void ApplyVoiceMix(AudioStreamPlayer3D voice, EventKind kind, double distance, float energy)
    {
        voice.MaxDistance = (float)RangeOf(kind);
        voice.VolumeDb = (kind == EventKind.Launch ? LaunchBaseDb : CrackleBaseDb) + Mathf.LinearToDb(Mathf.Clamp(energy, 0.01f, 1.0f))
            + (float)Math.Clamp(-distance / 3_000.0, -18.0, 0.0);
        float gate = AirGate();
        if (gate < 1.0f) voice.VolumeDb += gate > 0.001f ? Mathf.LinearToDb(gate) : -80.0f;
        float underground = 0.0f, shelter = 0.0f, shelterCutoff = 18_000.0f;
        if (_weatherAudio != null && IsInstanceValid(_weatherAudio))
        {
            underground = Mathf.Clamp(_weatherAudio.UndergroundBlend, 0.0f, 1.0f);
            shelter = float.IsFinite(_weatherAudio.ShelterMuffleAmount) ? Mathf.Clamp(_weatherAudio.ShelterMuffleAmount, 0.0f, 1.0f) : 0.0f;
            shelterCutoff = _weatherAudio.ShelterCutoffHz;
        }
        float distant = Mathf.SmoothStep(500.0f, 10_000.0f, (float)distance);
        float muffle = Mathf.Max(underground, shelter);
        voice.VolumeDb -= Mathf.Max(12.0f * underground, 16.0f * shelter);
        float cutoff = Mathf.Lerp(Mathf.Lerp(18_000.0f, 1_000.0f, distant), 600.0f, muffle);
        if (float.IsFinite(shelterCutoff) && shelterCutoff > 0.0f) cutoff = Mathf.Min(cutoff, shelterCutoff);
        voice.AttenuationFilterCutoffHz = cutoff;
        voice.AttenuationFilterDb = Mathf.Lerp(Mathf.Lerp(0.0f, -24.0f, distant), -24.0f, muffle);
    }

    private static AudioStream[] LoadPool(string prefix)
    {
        var samples = new List<AudioStream>();
        for (int index = 1; index <= SampleCount; index++)
        {
            string path = $"res://resources/audio/weather/{prefix}_{index}.ogg";
            if (!ResourceLoader.Exists(path)) continue;
            AudioStream? stream = ResourceLoader.Load<AudioStream>(path);
            if (stream != null && double.IsFinite(stream.GetLength()) && stream.GetLength() > 0.0) samples.Add(stream);
        }
        return samples.ToArray();
    }

    // The camera as a canonical position, as RingWeatherAudio resolves it (thunder's listener is private to that node).
    private RingCanonicalPosition ResolveCameraListener()
    {
        RingCanonicalPosition listener = _explorer!.CanonicalPosition;
        Camera3D? camera = GetViewport().GetCamera3D();
        if (camera == null) return listener;
        if (!_explorer.IsAncestorOf(camera)) return _grid!.WorldToCanonicalRingPosition64(camera.GlobalPosition);
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
        double heightOffset = -(2.0 * radial * radialOffset + radialOffset * radialOffset + tangentOffset * tangentOffset) / (radial + cameraRadial);
        return new RingCanonicalPosition(
            listener.AroundMeters + geometry.RingRadiusMeters * Math.Atan2(tangentOffset, radial + radialOffset),
            listener.VerticalMeters + heightOffset, listener.AcrossMeters + offset.Y).Wrap(geometry.CircumferenceMeters);
    }

    private void StopVoices()
    {
        foreach (AudioStreamPlayer3D voice in _voices) voice.Stop();
        _playing.Clear();
    }

    private void SetVoicePause(bool paused)
    {
        foreach (AudioStreamPlayer3D voice in _voices) voice.StreamPaused = paused;
    }

    private void Clear()
    {
        _pending.Clear();
        _launches.Clear();
        _lastWeatherTime = double.NaN;
        ResetCount++;
        StopVoices();
    }

    public string DescribeStatus() => string.Create(CultureInfo.InvariantCulture,
        $"Emitter field shot audio: {SampleCountLoaded} samples, pending {PendingCount}, played launch {PlayedLaunch} crackle {PlayedCrackle}, voices {ActiveVoiceCount}/{VoiceCount}, can hear {_canHear}, last launch {LastLaunchDistanceMeters:0} m / {LastLaunchDelaySeconds:0.00} s, last crackle {LastCrackleDistanceMeters:0} m / {LastCrackleDelaySeconds:0.00} s.");
}
