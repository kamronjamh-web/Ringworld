using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

/// <summary>
/// The containment field's two sounds (docs/containment-field-design.md decisions 4 and 8, WO10), owned and driven by <see cref="RingContainmentDisturbanceHost"/>.
/// <list type="bullet">
/// <item>The crossing whump: a soft membrane thump (`field_crossing_whump`, tools/media/weather_sfx_gen.py) on Master when the player passes through the field
/// (the body-contact adapter's player crossing, after its cooldown), pitched and levelled by the crossing's energy. It is heard on both sides.</item>
/// <item>The vacuum side: while the camera is outside the field (<see cref="ContainmentFieldGeometry.SignedDistance"/> &gt; 0) the weather and ambience buses
/// (<see cref="VacuumBuses"/>) get a low-pass and a gain drop, ramped by <see cref="VacuumWeight"/> over +-<see cref="RampHalfWidthMeters"/> of signed distance so the
/// crossing is smooth either way. Each bus gets its own filter and amplifier effect (never the bus volume, which the megastorm audio drives), added on first use,
/// bypassed while the weight is 0 and removed on exit. No gameplay penalty.</item>
/// <item>The field hum hook (user decision 2026-11-05): a user-supplied seamless loop at <see cref="HumPath"/> (.ogg, else .wav; mono or stereo). No file means no hum and no error. Two voices on the same stream on the ContainmentField bus, which gets the vacuum low-pass and gain drop like the weather buses (outer space): a positional AudioStreamPlayer3D at the nearest field point and a flat stereo AudioStreamPlayer, crossfaded equal-power by the distance to the field (<see cref="HumGains"/>: directional beyond <see cref="HumDirectionalMeters"/>, stereo within <see cref="HumStereoMeters"/>), loudness rising to full at the field and silent beyond <see cref="HumRangeMeters"/> (both voices stopped then). The volume is the grave-menu setting Field hum volume (dB).</item>
/// </list>
/// </summary>
public partial class RingContainmentFieldAudio : Node
{
    private const string Root = "res://resources/audio/weather/";
    public const float RampHalfWidthMeters = 3.0f, VacuumCutoffHz = 600.0f, OpenCutoffHz = 18000.0f, VacuumGainDb = -12.0f;
    public const float WhumpBaseDb = -12.0f, WhumpEnergyDb = 8.0f;
    public const double WeightSmoothingSeconds = 0.05, WallBodyFadeMeters = 4.0;
    public const string HumPath = "res://resources/audio/weather/field_hum_loop", HumBus = "ContainmentField";
    public const double HumRangeMeters = 3000.0, HumDirectionalMeters = 300.0, HumStereoMeters = 20.0;
    public const float HumBaseDb = -12.0f, SilentDb = -80.0f;
    /// <summary>Grave-menu "Field hum volume (dB)" (GameplayContainmentFieldDefaults.Apply); added to <see cref="HumBaseDb"/>.</summary>
    public static float HumVolumeDb { get; set; } = 0.0f;
    public static readonly string[] VacuumBuses = { "Thunder", "Rain", "Wind", "DistantStorm", "RainBed", "RainField", "MegastormAir", "MegastormStructure", "ContainmentField" };

    [Export] public bool Enabled { get; set; } = true;
    public AudioStream[] WhumpStreams { get; set; } = Array.Empty<AudioStream>();

    /// <summary>The smoothed vacuum weight 0..1 currently applied to the buses.</summary>
    public float Weight { get; private set; }
    public float CutoffHz { get; private set; } = OpenCutoffHz;
    public float GainDb { get; private set; }
    public int WhumpCount { get; private set; }
    public bool WhumpPlaying => _whump is { Playing: true };
    public float LastWhumpDb { get; private set; } = float.NaN;
    public float LastWhumpPitch { get; private set; } = float.NaN;

    private sealed class BusFx { public AudioEffectLowPassFilter Filter = null!; public AudioEffectAmplify Amp = null!; }
    private readonly Dictionary<string, BusFx> _fx = new();
    private AudioStreamPlayer? _whump, _humStereo;
    private AudioStreamPlayer3D? _humPositional;
    private double _humSyncTimer;

    /// <summary>The hum loop (null = no hum). Loaded from <see cref="HumPath"/> in _Ready when still null; a test may inject a stand-in with <see cref="SetHumStream"/>.</summary>
    public AudioStream? HumStream { get; private set; }
    public bool HumPlaying => _humStereo is { Playing: true } && _humPositional is { Playing: true };
    public double HumDistanceMeters { get; private set; } = double.NaN;
    public float HumPositionalGain { get; private set; }
    public float HumStereoGain { get; private set; }
    public float HumPositionalDb => _humPositional?.VolumeDb ?? SilentDb;
    public float HumStereoDb => _humStereo?.VolumeDb ?? SilentDb;
    public Vector3 HumPosition => _humPositional?.GlobalPosition ?? Vector3.Zero;

    /// <summary>Equal-power crossfade share 0 (directional) .. 1 (stereo) from the distance to the field: smoothstep from 300 m to 20 m.</summary>
    public static float HumBlend(double distanceMeters)
    {
        float u = Math.Clamp((float)((HumDirectionalMeters - distanceMeters) / (HumDirectionalMeters - HumStereoMeters)), 0.0f, 1.0f);
        return u * u * (3.0f - 2.0f * u);
    }

    /// <summary>Overall loudness 0..1: 0 at and beyond the range, 1 at the field, a smoothstep between.</summary>
    public static float HumLoudness(double distanceMeters, double rangeMeters = HumRangeMeters)
    {
        float t = Math.Clamp((float)(1.0 - distanceMeters / rangeMeters), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Linear gains of the positional and the stereo voice (before the volume setting): loudness x cos / sin of the blend, so the power sum is the loudness squared.</summary>
    public static (float Positional, float Stereo) HumGains(double distanceMeters, double rangeMeters = HumRangeMeters)
    {
        float loud = HumLoudness(distanceMeters, rangeMeters), b = HumBlend(distanceMeters) * (MathF.PI * 0.5f);
        return (loud * MathF.Cos(b), loud * MathF.Sin(b));
    }

    /// <summary>
    /// 0 on the contained side, 1 on the vacuum side, a smoothstep over +-<see cref="RampHalfWidthMeters"/> of signed distance. The wall body below the wall top outboard
    /// of the roots is ground, not vacuum, so there the weight fades out over <see cref="WallBodyFadeMeters"/> below the wall top.
    /// </summary>
    public static float VacuumWeight(in ContainmentFieldProfile profile, in RingCanonicalPosition camera)
    {
        if (!profile.IsValid) return 0.0f;
        float w = WeightOfSignedDistance(ContainmentFieldGeometry.SignedDistance(profile, camera));
        if (w <= 0.0f) return 0.0f;
        bool outboard = camera.AcrossMeters <= profile.RootAcross0 || camera.AcrossMeters >= profile.RootAcrossW;
        if (outboard)
        {
            float g = Math.Clamp((float)((camera.VerticalMeters - (profile.WallTopMeters - 2.0 * WallBodyFadeMeters)) / WallBodyFadeMeters), 0.0f, 1.0f);
            w *= g * g * (3.0f - 2.0f * g);
        }
        return w;
    }

    public static float WeightOfSignedDistance(double signedDistanceMeters)
    {
        float t = Math.Clamp((float)((signedDistanceMeters + RampHalfWidthMeters) / (2.0 * RampHalfWidthMeters)), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>Low-pass cutoff in Hz for a weight: logarithmic from open to the vacuum cutoff.</summary>
    public static float CutoffFor(float weight) => OpenCutoffHz * MathF.Pow(VacuumCutoffHz / OpenCutoffHz, Math.Clamp(weight, 0.0f, 1.0f));

    public static float GainDbFor(float weight) => VacuumGainDb * Math.Clamp(weight, 0.0f, 1.0f);

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        if (WhumpStreams.Length == 0)
        {
            var list = new List<AudioStream>();
            for (int i = 1; i <= 2; i++)
            {
                string path = $"{Root}field_crossing_whump_{i}.ogg";
                if (ResourceLoader.Exists(path) && ResourceLoader.Load<AudioStream>(path) is { } s) list.Add(s);
            }
            WhumpStreams = list.ToArray();
        }
        _whump = new AudioStreamPlayer { Name = "FieldWhump", Bus = "Master", VolumeDb = WhumpBaseDb };
        AddChild(_whump);
        if (HumStream == null) HumStream = LoadHum();
        string bus = EnsureHumBus();
        _humPositional = new AudioStreamPlayer3D { Name = "FieldHumPositional", Bus = bus, VolumeDb = SilentDb, AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled, MaxPolyphony = 1 };
        _humStereo = new AudioStreamPlayer { Name = "FieldHumStereo", Bus = bus, VolumeDb = SilentDb };
        AddChild(_humPositional);
        AddChild(_humStereo);
        _humPositional.Stream = HumStream; _humStereo.Stream = HumStream;
    }

    public override void _ExitTree() { StopHum(); ReleaseBuses(); }

    public static AudioStream? LoadHum()
    {
        foreach (string ext in new[] { ".ogg", ".wav" })
        {
            string path = HumPath + ext;
            if (!ResourceLoader.Exists(path)) continue;
            AudioStream? loaded = ResourceLoader.Load<AudioStream>(path);
            if (loaded == null) continue;
            return LoopOf(loaded);
        }
        return null;
    }

    private static AudioStream LoopOf(AudioStream stream)
    {
        if (stream is AudioStreamOggVorbis ogg) { var copy = (AudioStreamOggVorbis)ogg.Duplicate(); copy.Loop = true; return copy; }
        if (stream is AudioStreamWav wav && wav.LoopMode == AudioStreamWav.LoopModeEnum.Disabled)
        {
            var copy = (AudioStreamWav)wav.Duplicate();
            int bytesPerSample = (copy.Format == AudioStreamWav.FormatEnum.Format8Bits ? 1 : 2) * (copy.Stereo ? 2 : 1);
            copy.LoopMode = AudioStreamWav.LoopModeEnum.Forward; copy.LoopBegin = 0; copy.LoopEnd = copy.Data.Length / Math.Max(bytesPerSample, 1);
            return copy;
        }
        return stream;
    }

    private static string EnsureHumBus()
    {
        if (AudioServer.GetBusIndex(HumBus) >= 0) return HumBus;
        AudioServer.AddBus();
        int index = AudioServer.BusCount - 1;
        AudioServer.SetBusName(index, HumBus);
        AudioServer.SetBusSend(index, "Master");
        return HumBus;
    }

    /// <summary>Sets (or, with null, removes) the hum loop. The shipped path loads in _Ready; tests inject a stand-in here.</summary>
    public void SetHumStream(AudioStream? stream)
    {
        StopHum();
        HumStream = stream == null ? null : LoopOf(stream);
        if (_humPositional != null) _humPositional.Stream = HumStream;
        if (_humStereo != null) _humStereo.Stream = HumStream;
    }

    private void StopHum()
    {
        if (_humPositional is { Playing: true }) _humPositional.Stop();
        if (_humStereo is { Playing: true }) _humStereo.Stop();
        if (_humPositional != null) _humPositional.VolumeDb = SilentDb;
        if (_humStereo != null) _humStereo.VolumeDb = SilentDb;
        HumPositionalGain = 0.0f; HumStereoGain = 0.0f;
    }

    /// <summary>
    /// Per frame: with a hum loop and the camera within <see cref="HumRangeMeters"/> of the field, both voices play in sync on the same stream; the positional one sits
    /// at the nearest field point and the pair crossfades by distance. Beyond the range (or with no stream) both stop: no cost.
    /// </summary>
    public void UpdateHum(in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot, in RingCanonicalPosition camera, double deltaSeconds)
    {
        if (!Enabled || HumStream == null || _humPositional == null || _humStereo == null || !profile.IsValid) { StopHum(); return; }
        double d = Math.Abs(ContainmentFieldGeometry.SignedDistance(profile, camera));
        HumDistanceMeters = d;
        if (!(d < HumRangeMeters)) { StopHum(); return; }
        ContainmentFieldPoint pt = ContainmentFieldGeometry.Nearest(profile, camera);
        _humPositional.GlobalPosition = snapshot.CanonicalToWorld(pt.AroundMeters, pt.VerticalMeters, pt.AcrossMeters);
        ApplyHumGains(d);
        if (!_humPositional.Playing || !_humStereo.Playing)
        {
            _humPositional.Play(0.0f); _humStereo.Play(0.0f);   // together, from the start
            _humSyncTimer = 0.0;
        }
        else if ((_humSyncTimer += deltaSeconds) > 1.0)
        {
            _humSyncTimer = 0.0;
            double a = _humPositional.GetPlaybackPosition(), b = _humStereo.GetPlaybackPosition(), length = HumStream.GetLength();
            double diff = Math.Abs(a - b);
            if (length > 0.0) diff = Math.Min(diff, length - diff);
            if (diff > 0.04) _humStereo.Seek((float)a);
        }
    }

    /// <summary>Sets both voice volumes from the distance (also the test hook: no engine voices are needed).</summary>
    public void ApplyHumGains(double distanceMeters)
    {
        HumDistanceMeters = distanceMeters;
        (float pos, float st) = HumGains(distanceMeters);
        HumPositionalGain = pos; HumStereoGain = st;
        float db = HumBaseDb + HumVolumeDb;
        if (_humPositional != null) _humPositional.VolumeDb = pos > 1e-4f ? db + Mathf.LinearToDb(pos) : SilentDb;
        if (_humStereo != null) _humStereo.VolumeDb = st > 1e-4f ? db + Mathf.LinearToDb(st) : SilentDb;
    }

    /// <summary>Plays the whump for a player crossing; the energy (0..1) sets level and pitch (heavier crossings are lower and louder).</summary>
    public void PlayWhump(float energy, int variant)
    {
        if (!Enabled || _whump == null || WhumpStreams.Length == 0) return;
        float e = Math.Clamp(energy, 0.0f, 1.0f);
        LastWhumpDb = WhumpBaseDb + WhumpEnergyDb * e;
        LastWhumpPitch = Mathf.Lerp(1.12f, 0.88f, e);
        _whump.Stream = WhumpStreams[Math.Abs(variant) % WhumpStreams.Length];
        _whump.VolumeDb = LastWhumpDb;
        _whump.PitchScale = LastWhumpPitch;
        _whump.Play();
        WhumpCount++;
    }

    /// <summary>Per frame: the vacuum weight of the camera, smoothed, applied to the buses.</summary>
    public void UpdateVacuum(in ContainmentFieldProfile profile, in RingCanonicalPosition camera, double deltaSeconds)
        => ApplyWeight(Enabled ? VacuumWeight(profile, camera) : 0.0f, deltaSeconds);

    public void ApplyWeight(float target, double deltaSeconds)
    {
        float k = deltaSeconds <= 0.0 ? 1.0f : 1.0f - MathF.Exp((float)(-deltaSeconds / WeightSmoothingSeconds));
        Weight += (target - Weight) * k;
        if (Weight < 1e-4f && target <= 0.0f) Weight = 0.0f;
        CutoffHz = CutoffFor(Weight);
        GainDb = GainDbFor(Weight);
        if (Weight <= 0.0f && _fx.Count == 0) return;   // nothing installed, nothing to do
        foreach (string name in VacuumBuses) Apply(name, Weight > 0.0f);
    }

    private void Apply(string name, bool active)
    {
        int bus = AudioServer.GetBusIndex(name);
        if (bus < 0) return;   // the bus does not exist (yet): the megastorm buses are created when their audio node is ready
        if (!_fx.TryGetValue(name, out BusFx? fx))
        {
            if (!active) return;
            fx = new BusFx { Filter = new AudioEffectLowPassFilter { CutoffHz = OpenCutoffHz }, Amp = new AudioEffectAmplify { VolumeDb = 0.0f } };
            _fx[name] = fx;
            AudioServer.AddBusEffect(bus, fx.Filter);
            AudioServer.AddBusEffect(bus, fx.Amp);
        }
        // Bus indices can shift when another system inserts a bus, so find the effects again.
        int filterIndex = -1, ampIndex = -1;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
        {
            AudioEffect e = AudioServer.GetBusEffect(bus, i);
            if (e == fx.Filter) filterIndex = i; else if (e == fx.Amp) ampIndex = i;
        }
        if (filterIndex < 0) { AudioServer.AddBusEffect(bus, fx.Filter); filterIndex = AudioServer.GetBusEffectCount(bus) - 1; }
        if (ampIndex < 0) { AudioServer.AddBusEffect(bus, fx.Amp); ampIndex = AudioServer.GetBusEffectCount(bus) - 1; }
        fx.Filter.CutoffHz = CutoffHz;
        fx.Amp.VolumeDb = GainDb;
        AudioServer.SetBusEffectEnabled(bus, filterIndex, active);
        AudioServer.SetBusEffectEnabled(bus, ampIndex, active);
    }

    /// <summary>Removes every effect this node added (exit, or a test).</summary>
    public void ReleaseBuses()
    {
        foreach (KeyValuePair<string, BusFx> kv in _fx)
        {
            int bus = AudioServer.GetBusIndex(kv.Key);
            if (bus < 0) continue;
            for (int i = AudioServer.GetBusEffectCount(bus) - 1; i >= 0; i--)
            {
                AudioEffect e = AudioServer.GetBusEffect(bus, i);
                if (e == kv.Value.Filter || e == kv.Value.Amp) AudioServer.RemoveBusEffect(bus, i);
            }
        }
        _fx.Clear();
        Weight = 0.0f; CutoffHz = OpenCutoffHz; GainDb = 0.0f;
    }

    /// <summary>Debug/test: whether the node's effects are installed and enabled on a bus.</summary>
    public bool IsEngagedOn(string busName)
    {
        int bus = AudioServer.GetBusIndex(busName);
        if (bus < 0 || !_fx.TryGetValue(busName, out BusFx? fx)) return false;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
            if (AudioServer.GetBusEffect(bus, i) == fx.Filter) return AudioServer.IsBusEffectEnabled(bus, i);
        return false;
    }
}
