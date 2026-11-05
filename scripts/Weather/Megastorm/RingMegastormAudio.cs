using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// The megastorm's own voices (docs/megastorm-design.md 5.2, WO6c): the structure-borne ground hum (one non-positional bed), which never sees the
/// shelter muffling; and the airborne edge boom (one-shot pool, once per passage through
/// <see cref="MegastormBoomTracker"/>) and the wall roar bed, which go through a low-passed bus the shelter class muffles. Every level comes from
/// <see cref="MegastormSample"/> through <see cref="MegastormAudioMix"/>. It also publishes the airborne gate to the ordinary weather audio
/// (distant-storm sectors in <see cref="RingWeatherAmbience"/>, thunder in <see cref="RingWeatherAudio"/>) and the gust swell, and the wildlife quiet
/// as explorer metadata <see cref="WildlifeQuietMeta"/> (no wildlife ambience exists yet: a future life bed multiplies by 1 minus it).
/// Untriggered it costs one bool check per frame.
/// </summary>
public partial class RingMegastormAudio : Node
{
    public const string WildlifeQuietMeta = "ring_wildlife_quiet";
    public const string StructureBus = "MegastormStructure", AirBus = "MegastormAir";
    private const string Root = "res://resources/audio/weather/";
    // Level calibration of each layer before the user volume (listening items).
    private const float HumDb = -2f, BoomDb = 0f, RoarDb = -1f;
    private const float AudibleGain = .001f, SilentStopSeconds = 3f;
    private const double GoldenRatio = .6180339887498949;
    private const int BoomVariants = 3;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath AmbiencePath { get; set; } = new("../WeatherAmbience");
    [Export] public NodePath WeatherAudioPath { get; set; } = new("../WeatherAudio");
    [Export] public AudioStream? HumStream { get; set; }
    [Export] public AudioStream? RoarStream { get; set; }
    [Export] public AudioStream[] BoomStreams { get; set; } = Array.Empty<AudioStream>();

    /// <summary>Test seam: replaces the query at the explorer.</summary>
    public Func<MegastormSample>? SampleProvider { get; set; }

    private sealed class Layer
    {
        public readonly AudioStreamPlayer Flat;
        public readonly int Id;
        public float Gain;
        public float SilentSeconds;
        public Layer(AudioStreamPlayer flat, int id) { Flat = flat; Id = id; }
        public bool HasStream => Flat.Stream != null;
        public bool Playing => Flat.Playing;
        public float VolumeDb { get; private set; } = -80f;
        public void SetVolumeDb(float value) { VolumeDb = value; Flat.VolumeDb = value; }
        public void Play(float from) => Flat.Play(from);
        public void Stop() => Flat.Stop();
        public void Pause(bool paused) => Flat.StreamPaused = paused;
        public double Length => Flat.Stream?.GetLength() ?? 0.0;
    }

    private Layer _hum = null!, _roar = null!;
    private readonly AudioStreamPlayer[] _boom = new AudioStreamPlayer[BoomVariants];
    private readonly MegastormBoomTracker _tracker = new();
    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private RingWeatherAmbience? _ambience;
    private RingWeatherAudio? _weatherAudio;
    private AudioEffectLowPassFilter? _airFilter;
    private ulong _lastTicks;
    private bool _idle = true;
    private float _airBusGain = 1f, _airCutoffHz = RainSoundField.OpenCutoffHz;
    private MegastormVoiceTargets _targets = MegastormVoiceTargets.Idle;
    private int _boomCount, _lastBoomVariant = -1, _voiceStarts;
    private float _maxGainStep, _lastBoomGain, _tremor;
    private double _lastBoomSeek;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _ambience = GetNodeOrNull<RingWeatherAmbience>(AmbiencePath);
        _weatherAudio = GetNodeOrNull<RingWeatherAudio>(WeatherAudioPath);
        HumStream ??= LoadLoop("megastorm_ground_hum_loop");
        RoarStream ??= LoadLoop("megastorm_roar_loop");
        if (BoomStreams.Length == 0)
        {
            var boom = new AudioStream[BoomVariants];
            int found = 0;
            for (int i = 0; i < BoomVariants; i++)
            {
                string path = $"{Root}megastorm_boom_{i + 1}.ogg";
                if (ResourceLoader.Exists(path) && ResourceLoader.Load<AudioStream>(path) is { } stream) boom[found++] = stream;
            }
            BoomStreams = found == BoomVariants ? boom : Array.Empty<AudioStream>();
        }
        EnsureBus(StructureBus);
        int air = EnsureBus(AirBus);
        _airFilter = new AudioEffectLowPassFilter { CutoffHz = RainSoundField.OpenCutoffHz };
        AudioServer.AddBusEffect(air, _airFilter);
        _hum = CreateFlat("Hum", StructureBus, HumStream, 0);
        _roar = CreateFlat("Roar", AirBus, RoarStream, 3);
        for (int i = 0; i < BoomVariants; i++)
        {
            _boom[i] = new AudioStreamPlayer { Name = "Boom" + (i + 1), Bus = AirBus, VolumeDb = -80, Stream = i < BoomStreams.Length ? BoomStreams[i] : null };
            AddChild(_boom[i]);
        }
        _lastTicks = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        ulong ticks = Time.GetTicksUsec();
        double dt = ticks >= _lastTicks ? (ticks - _lastTicks) / 1_000_000.0 : 0.0;
        _lastTicks = ticks;
        if (GetTree().Paused) { PauseVoices(true); return; }
        PauseVoices(false);
        if (!Enabled) { if (!_idle) Step(default, RainShelterClass.Open, 0f, dt); return; }
        MegastormSample sample;
        if (SampleProvider != null) sample = SampleProvider();
        else
        {
            if (_megastorm == null || _explorer == null || (!_megastorm.Triggered && _idle)) return;
            RingCanonicalPosition observer = _explorer.CanonicalPosition;
            sample = _megastorm.QueryNow(observer);
        }
        RainShelterClass shelter = _ambience?.ShelterClass ?? RainShelterClass.Open;
        Step(sample, shelter, _ambience?.UndergroundBlend ?? 0f, dt);
    }

    public override void _ExitTree()
    {
        PublishGates(1f, 0f);
        if (GodotObject.IsInstanceValid(_explorer) && _explorer!.HasMeta(WildlifeQuietMeta)) _explorer.RemoveMeta(WildlifeQuietMeta);
        if (_airFilter != null)
        {
            // Bus indices can shift when another system inserts a bus after _Ready, so look the effect up again.
            int bus = AudioServer.GetBusIndex(AirBus);
            if (bus >= 0)
            {
                AudioServer.SetBusVolumeDb(bus, 0f);
                for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
                    if (AudioServer.GetBusEffect(bus, i) == _airFilter) { AudioServer.RemoveBusEffect(bus, i); break; }
            }
            _airFilter = null;
        }
        StopVoices();
    }

    /// <summary>
    /// One update: slews every voice toward its target, drives the airborne bus from the shelter, fires the boom once per passage and publishes the
    /// gates. Public so a test can run a passage deterministically.
    /// </summary>
    public void Step(in MegastormSample s, RainShelterClass shelter, float underground, double dt)
    {
        bool active = s.Active;
        _targets = MegastormAudioMix.Targets(s);
        _tremor = s.TremorLevel;
        float volumeDb = RingMegastormSettings.Current.SoundVolumeDb;
        if (!float.IsFinite(volumeDb)) volumeDb = 0f;
        volumeDb = Mathf.Clamp(volumeDb, -24f, 6f);
        _idle = !active && _hum.Gain <= 0f && _roar.Gain <= 0f;

        // Structure-borne: the hum is slow (the sample is smooth), the default slew keeps it pop-free.
        Drive(_hum, _targets.Hum, HumDb + volumeDb, dt, RainSoundField.GainTauSeconds, RainSoundField.MaxGainRatePerSecond);
        // Airborne: the roar follows AirGain's 1.5 s crossfade, so it slews faster. The shelter acts on the bus (boom and roar together).
        Drive(_roar, _targets.Roar, RoarDb + volumeDb, dt, .4f, 1.5f);
        UpdateAirBus(shelter, underground, dt);
        if (_tracker.Update(s.AirSound)) FireBoom(s, volumeDb);
        PublishGates(_targets.AirborneGate, active ? s.GustSwell : 0f);
        if (GodotObject.IsInstanceValid(_explorer)) _explorer!.SetMeta(WildlifeQuietMeta, _targets.Wildlife);
    }

    private void UpdateAirBus(RainShelterClass shelter, float underground, double dt)
    {
        RainShelterParams p = RainSoundField.Params(shelter);
        float gain = MegastormAudioMix.ShelterAirGain(shelter, underground);
        _airBusGain = RainSoundField.SlewGain(_airBusGain, gain, dt);
        _airCutoffHz = RainSoundField.SlewCutoffHz(_airCutoffHz, p.BedCutoffHz, dt);
        if (_airFilter != null) _airFilter.CutoffHz = _airCutoffHz;
        int bus = AudioServer.GetBusIndex(AirBus);
        if (bus >= 0) AudioServer.SetBusVolumeDb(bus, RainSoundField.GainToDb(_airBusGain));
    }

    private void FireBoom(in MegastormSample s, float volumeDb)
    {
        int variant = MegastormAudioMix.BoomVariant(s.EpisodeId, BoomVariants);
        _boomCount++;
        _lastBoomVariant = variant;
        _lastBoomGain = MegastormSound.BoomGain(s.SpeedMetersPerSecond);
        _lastBoomSeek = 0.0;
        if (_lastBoomGain <= 0f || _boom[variant].Stream == null) return;
        AudioStreamPlayer player = _boom[variant];
        player.VolumeDb = BoomDb + volumeDb + RainSoundField.GainToDb(_lastBoomGain);
        // A reload inside the window seeks by the time since the edge, so the roll continues instead of restarting.
        _lastBoomSeek = Math.Max(0.0, s.XiMeters / Math.Max(1.0, s.SpeedMetersPerSecond));
        if (_lastBoomSeek >= player.Stream!.GetLength()) return;
        player.Play((float)_lastBoomSeek);
    }

    private void PublishGates(float gate, float swell)
    {
        if (_ambience != null) { _ambience.MegastormAirGate = gate; _ambience.MegastormGustSwell = swell; }
        if (_weatherAudio != null) _weatherAudio.MegastormAirGate = gate;
    }

    private void Drive(Layer layer, float target, float baseDb, double dt, float tau, float cap)
    {
        if (layer == null) return;
        float before = layer.Gain;
        layer.Gain = RainSoundField.SlewGain(layer.Gain, Math.Clamp(target, 0f, 1f), dt, tau, cap);
        if (dt > 1e-4) _maxGainStep = Math.Max(_maxGainStep, MathF.Abs(layer.Gain - before) / (float)dt);
        if (!layer.HasStream) { layer.Gain = 0; layer.Stop(); return; }
        layer.SetVolumeDb(layer.Gain <= AudibleGain * .1f ? -80f : baseDb + RainSoundField.GainToDb(layer.Gain));
        if (layer.Gain > AudibleGain)
        {
            layer.SilentSeconds = 0;
            if (!layer.Playing) { layer.Play(StartOffset(layer)); _voiceStarts++; }
        }
        else
        {
            layer.SilentSeconds += (float)dt;
            if (layer.Playing && (layer.Gain <= .00001f || layer.SilentSeconds >= SilentStopSeconds)) layer.Stop();
        }
    }

    private static float StartOffset(Layer layer)
    {
        double length = layer.Length;
        return (float)(length > 0 ? length * ((layer.Id + 1) * GoldenRatio % 1.0) : 0.0);
    }

    public Godot.Collections.Dictionary GetMegastormAudioDiagnostics() => new()
    {
        ["hum_gain"] = _hum?.Gain ?? 0f,
        ["roar_gain"] = _roar?.Gain ?? 0f, ["air_bus_gain"] = _airBusGain, ["air_cutoff_hz"] = _airCutoffHz,
        ["roar_effective"] = (_roar?.Gain ?? 0f) * _airBusGain, ["hum_effective"] = _hum?.Gain ?? 0f,
        ["airborne_gate"] = _targets.AirborneGate, ["swell"] = _targets.Swell, ["wildlife_quiet"] = _targets.Wildlife, ["tremor"] = _tremor,
        ["boom_count"] = _boomCount, ["boom_variant"] = _lastBoomVariant, ["boom_gain"] = _lastBoomGain, ["boom_seek_seconds"] = _lastBoomSeek,
        ["boom_playing"] = Array.Exists(_boom, b => b != null && b.Playing), ["boom_stream_count"] = BoomStreams.Length,
        ["hum_volume_db"] = _hum?.VolumeDb ?? -80f, ["roar_volume_db"] = _roar?.VolumeDb ?? -80f, ["hum_playing"] = _hum?.Playing ?? false, ["roar_playing"] = _roar?.Playing ?? false,
        ["voice_starts"] = _voiceStarts, ["max_gain_step"] = _maxGainStep, ["idle"] = _idle,
        ["hum_asset"] = HumStream != null, ["roar_asset"] = RoarStream != null
    };

    public void ResetGainStepPeak() => _maxGainStep = 0f;

    private void PauseVoices(bool paused)
    {
        _hum?.Pause(paused); _roar?.Pause(paused);
        foreach (AudioStreamPlayer boom in _boom) if (boom != null) boom.StreamPaused = paused;
    }

    private void StopVoices()
    {
        _hum?.Stop(); _roar?.Stop();
        foreach (AudioStreamPlayer boom in _boom) boom?.Stop();
    }

    private Layer CreateFlat(string name, string bus, AudioStream? stream, int id)
    {
        var player = new AudioStreamPlayer { Name = name, Bus = bus, VolumeDb = -80, Stream = stream };
        AddChild(player);
        return new Layer(player, id);
    }

    private static int EnsureBus(string name)
    {
        int index = AudioServer.GetBusIndex(name);
        if (index >= 0) return index;
        AudioServer.AddBus();
        index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, name);
        AudioServer.SetBusSend(index, "Master");
        return index;
    }

    private static AudioStream? LoadLoop(string name)
    {
        string path = $"{Root}{name}.ogg";
        if (!ResourceLoader.Exists(path)) return null;
        AudioStream? source = ResourceLoader.Load<AudioStream>(path);
        if (source == null || !double.IsFinite(source.GetLength()) || source.GetLength() <= 0.0) return null;
        if (source is AudioStreamOggVorbis ogg) { var copy = (AudioStreamOggVorbis)ogg.Duplicate(); copy.Loop = true; return copy; }
        return source;
    }
}
