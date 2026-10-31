using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>
/// Hazard audio of the megastorm (docs/megastorm-design.md 5.5.2 / 5.5.3, WO11): the rain-hammer bed (bullet rain on structures, level from
/// <see cref="MegastormHazards.RainImpactPowerWm2"/>), the cosmetic ear-pop one-shot when the pressure rate crosses 0.2 kPa/s into the wake (level
/// <see cref="RingMegastormSettings.EarPopLevel"/>), and the dull low-passed hush of the trough, a low-pass on the Master bus that exists only while the pressure
/// deficit does. Both voices play on the MegastormStructure bus (the roof drums through the shelter). Untriggered it costs one bool check per frame and adds nothing to a bus.
/// </summary>
public partial class RingMegastormHazardAudio : Node
{
    private const string Root = "res://resources/audio/weather/";
    private const float HammerDb = -6f, PopDb = -9f;
    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath MegastormPath { get; set; } = new("../RingMegastorm");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath AmbiencePath { get; set; } = new("../WeatherAmbience");

    public Func<MegastormSample>? SampleProvider { get; set; }
    public Func<RainShelterClass>? ShelterProvider { get; set; }

    private RingMegastorm? _megastorm;
    private RingVoxelExplorerController? _explorer;
    private RingWeatherAmbience? _ambience;
    private AudioStreamPlayer? _hammer, _pop;
    private AudioEffectLowPassFilter? _hush;
    private readonly MegastormHazardFx.EarPopTracker _popTracker = new();
    private float _hammerGain, _cutoffHz = MegastormHazardFx.OpenCutoffHz;
    private int _popCount;
    private ulong _lastTicks;

    public float HammerGain => _hammerGain;
    public float HushCutoffHz => _hush != null ? _cutoffHz : MegastormHazardFx.OpenCutoffHz;
    public bool HushActive => _hush != null;
    public int PopCount => _popCount;
    public bool HammerPlaying => _hammer?.Playing ?? false;
    public bool PopPlaying => _pop?.Playing ?? false;
    public bool HasAssets => _hammer?.Stream != null && _pop?.Stream != null;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _megastorm = GetNodeOrNull<RingMegastorm>(MegastormPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _ambience = GetNodeOrNull<RingWeatherAmbience>(AmbiencePath);
        EnsureBus(RingMegastormAudio.StructureBus);
        _hammer = new AudioStreamPlayer { Name = "RainHammer", Bus = RingMegastormAudio.StructureBus, VolumeDb = -80, Stream = LoadLoop("megastorm_rain_hammer_loop") };
        _pop = new AudioStreamPlayer { Name = "EarPop", Bus = RingMegastormAudio.StructureBus, VolumeDb = PopDb, Stream = LoadOnce("megastorm_ear_pop") };
        AddChild(_hammer); AddChild(_pop);
        _lastTicks = Time.GetTicksUsec();
    }

    public override void _Process(double delta)
    {
        ulong ticks = Time.GetTicksUsec();
        double dt = ticks >= _lastTicks ? (ticks - _lastTicks) / 1_000_000.0 : 0.0;
        _lastTicks = ticks;
        if (GetTree().Paused) { if (_hammer != null) _hammer.StreamPaused = true; return; }
        if (_hammer != null) _hammer.StreamPaused = false;
        MegastormSample s = default;
        if (Enabled)
        {
            if (SampleProvider != null) s = SampleProvider();
            else if (_megastorm != null && _explorer != null && (_megastorm.Triggered || _hammerGain > 0f || _hush != null)) s = _megastorm.QueryNow(_explorer.CanonicalPosition);
            else if (_megastorm == null || !_megastorm.Triggered) { _popTracker.Reset(); return; }
        }
        RainShelterClass shelter = ShelterProvider?.Invoke() ?? _ambience?.ShelterClass ?? RainShelterClass.Open;
        Step(s, shelter, dt);
    }

    public override void _ExitTree() { RemoveHush(); _hammer?.Stop(); _pop?.Stop(); }

    /// <summary>One update; public so a test can run a passage deterministically.</summary>
    public void Step(in MegastormSample s, RainShelterClass shelter, double dt)
    {
        MegastormHazards h = s.Active ? s.Hazards : MegastormHazards.None;
        bool underRoof = shelter != RainShelterClass.Open;
        MegastormShelter blast = shelter is RainShelterClass.Indoors or RainShelterClass.Cave or RainShelterClass.DeepCave ? MegastormShelter.Enclosed : MegastormShelter.Open;
        float target = MegastormHazardFx.RainHammerGain(h.RainImpactPowerWm2, h.RainExposure, blast, underRoof);
        float volumeDb = Mathf.Clamp(RingMegastormSettings.Current.SoundVolumeDb, -24f, 6f);
        _hammerGain = RainSoundField.SlewGain(_hammerGain, target, dt, .6f, 1.2f);
        if (_hammer?.Stream != null)
        {
            _hammer.VolumeDb = _hammerGain <= 1e-4f ? -80f : HammerDb + volumeDb + RainSoundField.GainToDb(_hammerGain);
            if (_hammerGain > .001f) { if (!_hammer.Playing) _hammer.Play(); }
            else if (_hammer.Playing && _hammerGain <= 1e-5f) _hammer.Stop();
        }
        // Wake: the ear pop on the falling-pressure edge, then the hush.
        if (_popTracker.Update(h.PressureRateKPaPerS, dt) && _pop?.Stream != null)
        {
            float level = Mathf.Clamp(RingMegastormSettings.Current.EarPopLevel, 0f, 1f);
            _popCount++;
            if (level > 0f) { _pop.VolumeDb = PopDb + volumeDb + RainSoundField.GainToDb(level); _pop.Play(); }
        }
        float cutoff = MegastormHazardFx.WakeHushCutoffHz(h.PressureDeficitKPa);
        if (cutoff >= MegastormHazardFx.OpenCutoffHz - 1f) { if (_hush != null) { _cutoffHz = RainSoundField.SlewCutoffHz(_cutoffHz, cutoff, dt); _hush.CutoffHz = _cutoffHz; if (_cutoffHz >= MegastormHazardFx.OpenCutoffHz - 1f) RemoveHush(); } return; }
        if (_hush == null)
        {
            int master = AudioServer.GetBusIndex("Master");
            if (master < 0) return;
            _hush = new AudioEffectLowPassFilter { CutoffHz = MegastormHazardFx.OpenCutoffHz };
            AudioServer.AddBusEffect(master, _hush);
            _cutoffHz = MegastormHazardFx.OpenCutoffHz;
        }
        _cutoffHz = RainSoundField.SlewCutoffHz(_cutoffHz, cutoff, dt);
        _hush.CutoffHz = _cutoffHz;
    }

    private void RemoveHush()
    {
        if (_hush == null) return;
        int master = AudioServer.GetBusIndex("Master");
        if (master >= 0)
            for (int i = 0; i < AudioServer.GetBusEffectCount(master); i++)
                if (AudioServer.GetBusEffect(master, i) == _hush) { AudioServer.RemoveBusEffect(master, i); break; }
        _hush = null; _cutoffHz = MegastormHazardFx.OpenCutoffHz;
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

    private static AudioStream? LoadOnce(string name)
    {
        string path = $"{Root}{name}.ogg";
        return ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
    }

    private static AudioStream? LoadLoop(string name)
    {
        AudioStream? source = LoadOnce(name);
        if (source is AudioStreamOggVorbis ogg) { var copy = (AudioStreamOggVorbis)ogg.Duplicate(); copy.Loop = true; return copy; }
        return source;
    }
}
