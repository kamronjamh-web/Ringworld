using System;
using Godot;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo;

/// <summary>
/// Rim sizzle (docs/megastorm-design.md 5.5.5, WO13): the continuous electrical sizzle of the megastorm's near-rim contact arcs, heard within 10 km of a rim wall
/// while the arcs are alive near the observer. A procedural loop (`megastorm_rim_sizzle_loop`, tools/media/weather_sfx_gen.py) on the MegastormAir bus (so shelter
/// muffles it), multiplied by the same airborne gate as thunder (`RingWeatherAudio.MegastormAirGate`, zero ahead of the wall's edge). Strikes 20+ km overhead need no
/// voice here: they are ordinary flagged events and arrive as a delayed long roll through the far thunder pool (RingWeatherAudio observes every active event).
/// </summary>
public partial class RingFieldStrikeAudio : Node
{
    private const string Root = "res://resources/audio/weather/";
    public const float AudibleRimMeters = 10000.0f, FullRimMeters = 2500.0f, MaxDb = -12.0f, SilentDb = -60.0f;

    [Export] public bool Enabled { get; set; } = true;
    [Export] public NodePath WeatherPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath WeatherAudioPath { get; set; } = new("../WeatherAudio");
    [Export] public AudioStream? SizzleStream { get; set; }

    /// <summary>Test seam: replaces the airborne gate.</summary>
    public Func<float>? GateProvider { get; set; }

    private RingVolumetricCloudLayer? _weather;
    private RingVoxelExplorerController? _explorer;
    private RingWeatherAudio? _weatherAudio;
    private Voxel.Phase2.VoxelChunkGridManager? _grid;
    private AudioStreamPlayer? _player;
    private float _activity;
    public float Level { get; private set; }

    /// <summary>Gain 0..1 of the bed from the observer's distance to the nearer rim wall and the smoothed share of live arcs: 0 beyond 10 km, full within 2.5 km.</summary>
    public static float RimLevel(double rimDistanceMeters, float activity)
    {
        float t = Math.Clamp((float)((AudibleRimMeters - rimDistanceMeters) / (AudibleRimMeters - FullRimMeters)), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t) * Math.Clamp(activity, 0.0f, 1.0f);
    }

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        _weather = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherPath);
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _weatherAudio = GetNodeOrNull<RingWeatherAudio>(WeatherAudioPath);
        string path = Root + "megastorm_rim_sizzle_loop.ogg";
        SizzleStream ??= ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
        if (SizzleStream is AudioStreamOggVorbis ogg) ogg.Loop = true;
        _player = new AudioStreamPlayer { Name = "RimSizzle", Bus = "Master", VolumeDb = SilentDb, Stream = SizzleStream };
        AddChild(_player);
    }

    public override void _Process(double delta)
    {
        if (_player == null) return;
        float level = 0.0f;
        if (Enabled && _weather != null && _explorer != null && _weather.ActiveContactArcs.Count > 0)
        {
            Voxel.Core.RingCanonicalPosition o = _explorer.CanonicalPosition;
            _grid ??= _weather.GetNodeOrNull<Voxel.Phase2.VoxelChunkGridManager>(_weather.GridManagerPath);
            double width = _grid?.RingAcrossWidthMeters ?? 318000.0, circumference = _grid?.GetCanonicalRingCircumferenceMeters64() ?? 31415936.0;
            double rim = Math.Min(o.AcrossMeters, width - o.AcrossMeters);
            int near = 0;
            foreach (Lightning.RingLightningEvent arc in _weather.ActiveContactArcs)
            {
                double da = Math.Abs(MegastormModel.WrapSigned(arc.GroundPosition.AroundMeters - o.AroundMeters, circumference)), dz = arc.GroundPosition.AcrossMeters - o.AcrossMeters;
                if (da * da + dz * dz < 20000.0 * 20000.0) near++;
            }
            _activity += ((near > 0 ? Math.Min(1.0f, near / 1.5f) : 0.0f) - _activity) * (float)Math.Min(1.0, delta * 2.0);
            level = RimLevel(rim, _activity);
        }
        else _activity += (0.0f - _activity) * (float)Math.Min(1.0, delta * 2.0);
        float gate = GateProvider != null ? GateProvider() : (_weatherAudio != null && GodotObject.IsInstanceValid(_weatherAudio) ? _weatherAudio.MegastormAirGate : 1.0f);
        Level = level * Math.Clamp(gate, 0.0f, 1.0f);
        if (AudioServer.GetBusIndex("MegastormAir") >= 0 && _player.Bus != "MegastormAir") _player.Bus = "MegastormAir";
        if (Level < 0.002f) { if (_player.Playing) _player.Stop(); _player.VolumeDb = SilentDb; return; }
        _player.VolumeDb = Mathf.Lerp(SilentDb, MaxDb, MathF.Pow(Level, 0.6f));
        if (!_player.Playing && SizzleStream != null) _player.Play();
    }
}
