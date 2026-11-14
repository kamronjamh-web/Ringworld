using System;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Integration;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;
using RingworldTechDemo.Water;

namespace RingworldTechDemo;

/// <summary>What the node reads each frame (T3.md, Spec W1 and W6): the listener, the weather that masks, the listener's own speed, and whether the camera is under water or underground.</summary>
public readonly record struct WaterAmbienceInputs(WaterSoundListener Listener, double Rain, double Wind, double ListenerSpeed, bool Underwater, double DepthMeters, double UndergroundBlend, double SeaWaveHeight);

/// <summary>
/// T3R-10d (docs/work-orders/terrain-vegetation/T3.md, Spec W): the sound of water in the world. Every source the finder reports (shores, rivers, falls, open water) is heard at the level W2's law gives for its
/// straight distance from the listener, height included, through positional players whose engine attenuation is off, on three buses with one distance low-pass each (WaterShore, WaterRiver, WaterDrop), plus a
/// stereo open-water player and the underwater player. Nothing is raised to be heard and no source has a floor. The node keeps the name WaterAmbience; the one meta it publishes, ring_water_ambience_proximity,
/// is the loudest water gain (linear) for scripts/outside_ambience.gd's ducking. The loops are nine synthesised files (resources/audio/water/); no code depends on their content, only on their order in W5.
/// </summary>
public partial class RingWaterAmbience : Node
{
    public const string ProximityMetadata = "ring_water_ambience_proximity";
    private static readonly StringName ProximityMetadataName = ProximityMetadata;
    private static readonly StringName UndergroundMetadata = new("ring_underground_ambience_blend");
    private static readonly StringName DepthMetadata = new("ring_water_depth_meters");

    // W7: voices per layer, players per voice, the bounds.
    private const int ShoreVoices = 2, RiverVoices = 2, DropVoices = 3, MaxSounding = 8;
    private const int SamplerCallsPerFrame = 4;
    private const double SlewSeconds = 0.3, FadeInSeconds = 1.0, SilentStopSeconds = 5.0;
    private const double AudibleGain = 1e-4;
    private const double CutoffSmoothSeconds = 0.1;
    private const float FullScaleCutoffHz = 20000f;
    private const double PositionMaxMeters = 40.0;
    // Below the 16-bit floor: a silent player that has not yet stopped adds nothing the ear or the capture can find.
    private const float SilentDb = -120f;

    [Export] public bool Enabled { get; set; } = true;
    /// <summary>Volume rows (T3R-10e) in dB: every water sound, the sea and surf, lakes, rivers and falls, and under water (today's -12 dB trim).</summary>
    [Export(PropertyHint.Range, "-30,10,0.5")] public float WaterVolumeDb { get; set; }
    [Export(PropertyHint.Range, "-30,10,0.5")] public float SeaVolumeDb { get; set; }
    [Export(PropertyHint.Range, "-30,10,0.5")] public float LakeVolumeDb { get; set; }
    [Export(PropertyHint.Range, "-30,10,0.5")] public float RiverVolumeDb { get; set; }
    [Export(PropertyHint.Range, "-30,10,0.5")] public float UnderwaterVolumeDb { get; set; } = -12f;
    /// <summary>The sea's significant wave height when no FFT controller supplies one: the base swell the surface shows (0.32 m, H49).</summary>
    [Export(PropertyHint.Range, "0,10,0.01")] public float FallbackSeaWaveHeightMeters { get; set; } = 0.32f;

    [Export] public AudioStream? LapStream { get; set; }
    [Export] public AudioStream? SurfBreakStream { get; set; }
    [Export] public AudioStream? SurfFarStream { get; set; }
    [Export] public AudioStream? OpenWaterStream { get; set; }
    [Export] public AudioStream? BabbleStream { get; set; }
    [Export] public AudioStream? RushStream { get; set; }
    [Export] public AudioStream? RoarStream { get; set; }
    [Export] public AudioStream? WaterfallStream { get; set; }
    [Export] public AudioStream? UnderwaterStream { get; set; }

    [Export] public NodePath ExplorerPath { get; set; } = new("../Explorer");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath LocalWeatherEffectsPath { get; set; } = new("../LocalWeatherEffects");
    [Export] public NodePath WaterFftPath { get; set; } = new("../WaterFftController");

    /// <summary>Test seams: a fixed finder in place of the real one, fixed inputs in place of the explorer, grid and weather, and the node that carries the proximity meta.</summary>
    public IWaterSoundFinder? FinderOverride { get; set; }
    public Func<WaterAmbienceInputs>? InputOverride { get; set; }
    public Node? MetaTarget { get; set; }
    /// <summary>Test only: the negative control of T3R-10d. With it on, a source's distance is its horizontal part alone (the altitude fault of H40).</summary>
    public bool TestIgnoreHeight { get; set; }

    // The loops of W5, by layer.
    private AudioStream?[] _shoreLoops = Array.Empty<AudioStream?>(), _riverLoops = Array.Empty<AudioStream?>(), _dropLoops = Array.Empty<AudioStream?>();

    private enum Layer { Shore, River, Drop, Open, Under }

    private sealed class Player
    {
        public AudioStreamPlayer? Flat;
        public AudioStreamPlayer3D? Spatial;
        public int Loop = -1;
        public double Gain, Target, SilentSeconds;
        public bool Playing;
        public AudioStream? Stream;
    }

    private sealed class Voice
    {
        public Layer Layer;
        public Player[] Players = Array.Empty<Player>();
        public long Key;
        public bool HasKey, Claimed;
        public double FadeIn;
        // Diagnostics of the last frame.
        public WaterSoundKind Kind;
        public double Distance, Level, Cutoff, MixGain;
        public WaterLoopWeights Weights;
    }

    private Voice[] _shore = Array.Empty<Voice>(), _river = Array.Empty<Voice>(), _drop = Array.Empty<Voice>();
    private Voice? _open, _under;
    private Voice[] _all = Array.Empty<Voice>();
    private Player[] _players = Array.Empty<Player>();
    private Node3D? _rig;
    private RingVoxelExplorerController? _explorer;
    private VoxelChunkGridManager? _grid;
    private RingLocalWeatherEffects? _local;
    private RingWaterFftController? _fft;
    private IWaterSoundFinder? _finder;
    private RingTerrainSampler? _finderSampler;
    private ulong _lastTicks;
    private readonly Random _random = new(unchecked((int)Time.GetTicksUsec()));

    private readonly AudioEffectLowPassFilter?[] _filters = new AudioEffectLowPassFilter?[3];
    private readonly double[] _cutoffTarget = { FullScaleCutoffHz, FullScaleCutoffHz, FullScaleCutoffHz }, _cutoffNow = { FullScaleCutoffHz, FullScaleCutoffHz, FullScaleCutoffHz };
    private static readonly string[] FilterBuses = { "WaterShore", "WaterRiver", "WaterDrop" };

    private double _masker, _seaWaveHeight, _proximity, _finderMs, _nodeMs;
    private long _frameStart;
    private int _finderCalls, _soundingPlayers;
    private bool _underwater;
    private WaterAmbienceInputs _inputs;
    private double _spectrumKey = double.NaN, _spectrumValue;
    private readonly WaterSoundSource[] _frameSources = new WaterSoundSource[WaterSoundSourceFinder.MaxSources];
    private readonly WaterSoundMix[] _frameMixes = new WaterSoundMix[WaterSoundSourceFinder.MaxSources];
    private int _frameSourceCount;

    public override void _Ready()
    {
        _explorer = GetNodeOrNull<RingVoxelExplorerController>(ExplorerPath);
        _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
        _local = GetNodeOrNull<RingLocalWeatherEffects>(LocalWeatherEffectsPath);
        _fft = GetNodeOrNull<RingWaterFftController>(WaterFftPath);
        MetaTarget ??= _explorer;
        _shoreLoops = new[] { Looped(LapStream), Looped(SurfBreakStream), Looped(SurfFarStream) };
        _riverLoops = new[] { Looped(BabbleStream), Looped(RushStream), Looped(RoarStream) };
        _dropLoops = new[] { Looped(BabbleStream), Looped(RushStream), Looped(RoarStream), Looped(WaterfallStream) };
        BuildVoices();
        for (int i = 0; i < FilterBuses.Length; i++) _filters[i] = FindFilter(FilterBuses[i]);
        _lastTicks = Time.GetTicksUsec();
    }

    public override void _ExitTree()
    {
        if (MetaTarget != null && GodotObject.IsInstanceValid(MetaTarget) && MetaTarget.HasMeta(ProximityMetadata)) MetaTarget.RemoveMeta(ProximityMetadata);
        foreach (Player p in _players) StopPlayer(p);
        for (int i = 0; i < _filters.Length; i++) if (_filters[i] != null) _filters[i]!.CutoffHz = FullScaleCutoffHz;
    }

    // ---- Setup ----------------------------------------------------------------------------------------------------

    private static AudioStream? Looped(AudioStream? source)
    {
        if (source is AudioStreamOggVorbis ogg) { var copy = (AudioStreamOggVorbis)ogg.Duplicate(); copy.Loop = true; return copy; }
        return source;
    }

    private void BuildVoices()
    {
        _rig = new Node3D { Name = "Rig" };
        AddChild(_rig);
        _shore = MakeSpatialVoices(Layer.Shore, ShoreVoices, "WaterShore");
        _river = MakeSpatialVoices(Layer.River, RiverVoices, "WaterRiver");
        _drop = MakeSpatialVoices(Layer.Drop, DropVoices, "WaterDrop");
        _open = MakeFlatVoice(Layer.Open, "OpenWater", "WaterAbove", Looped(OpenWaterStream));
        _under = MakeFlatVoice(Layer.Under, "Underwater", "WaterUnderwater", Looped(UnderwaterStream));
        var all = new System.Collections.Generic.List<Voice>();
        all.AddRange(_shore); all.AddRange(_river); all.AddRange(_drop); all.Add(_open); all.Add(_under);
        _all = all.ToArray();
        var players = new System.Collections.Generic.List<Player>();
        foreach (Voice v in _all) players.AddRange(v.Players);
        _players = players.ToArray();
    }

    private Voice[] MakeSpatialVoices(Layer layer, int count, string bus)
    {
        var voices = new Voice[count];
        for (int v = 0; v < count; v++)
        {
            var voice = new Voice { Layer = layer, Players = new Player[2] };
            for (int p = 0; p < 2; p++)
            {
                var spatial = new AudioStreamPlayer3D
                {
                    Name = $"{layer}{v}{(char)('A' + p)}", Bus = bus, VolumeDb = -80,
                    // Direction only: the gain is W2's, computed by the node. No distance cue, no Doppler.
                    AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.Disabled,
                    AttenuationFilterDb = 0, AttenuationFilterCutoffHz = 20500,
                    DopplerTracking = AudioStreamPlayer3D.DopplerTrackingEnum.Disabled,
                    PanningStrength = 0f, MaxPolyphony = 1,
                };
                _rig!.AddChild(spatial);
                voice.Players[p] = new Player { Spatial = spatial };
            }
            voices[v] = voice;
        }
        return voices;
    }

    private Voice MakeFlatVoice(Layer layer, string name, string bus, AudioStream? stream)
    {
        var node = new AudioStreamPlayer { Name = name, Bus = bus, VolumeDb = -80, Stream = stream };
        AddChild(node);
        return new Voice { Layer = layer, Players = new[] { new Player { Flat = node, Loop = 0, Stream = stream } } };
    }

    private static AudioEffectLowPassFilter? FindFilter(string busName)
    {
        int bus = AudioServer.GetBusIndex(busName);
        if (bus < 0) return null;
        for (int i = 0; i < AudioServer.GetBusEffectCount(bus); i++)
            if (AudioServer.GetBusEffect(bus, i) is AudioEffectLowPassFilter filter) return filter;
        return null;
    }

    // ---- Frame ----------------------------------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        _frameStart = Stopwatch.GetTimestamp();
        _finderMs = 0.0;
        ulong ticks = Time.GetTicksUsec();
        double dt = ticks >= _lastTicks ? (ticks - _lastTicks) / 1_000_000.0 : 0.0;
        _lastTicks = ticks;
        dt = Math.Min(dt, 0.25);
        if (!Enabled || !TryReadInputs(out WaterAmbienceInputs inputs))
        {
            Idle(dt);
            return;
        }
        _inputs = inputs;
        _underwater = inputs.Underwater;
        double enclosure = double.IsFinite(inputs.UndergroundBlend) ? 1.0 - Math.Clamp(inputs.UndergroundBlend, 0.0, 1.0) : 1.0;
        _masker = WaterSoundField.Masker(inputs.Rain, inputs.Wind, inputs.ListenerSpeed);
        _seaWaveHeight = inputs.SeaWaveHeight;
        foreach (Voice v in _all) v.Claimed = false;
        _frameSourceCount = 0;
        _proximity = 0.0;
        for (int i = 0; i < _cutoffTarget.Length; i++) _cutoffTarget[i] = FullScaleCutoffHz;

        if (_underwater)
        {
            // Under the surface only the underwater loop plays, ramped by depth as before; every other layer is silent.
            double depth = Math.Max(0.0, inputs.DepthMeters);
            double ramp = 0.36 + (1.0 - 0.36) * Math.Clamp(depth / 4.0, 0.0, 1.0);
            _under!.Claimed = true;
            _under.Players[0].Target = ramp * Db(UnderwaterVolumeDb + WaterVolumeDb) * enclosure;
            _under.MixGain = _under.Players[0].Target;
        }
        else
        {
            IWaterSoundFinder? finder = EnsureFinder();
            if (finder != null)
            {
                finder.SeaWaveHeight = inputs.SeaWaveHeight;
                finder.Masker = _masker;
                long t0 = Stopwatch.GetTimestamp();
                finder.Update(inputs.Listener, SamplerCallsPerFrame);
                _finderMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                _finderCalls = finder.LastSamplerCalls;
                ReadOnlySpan<WaterSoundSource> sources = finder.Sources;
                for (int i = 0; i < sources.Length && _frameSourceCount < _frameSources.Length; i++)
                {
                    WaterSoundSource s = sources[i];
                    if (TestIgnoreHeight) s = s with { Distance = Horizontal(inputs.Listener, s) };
                    _frameSources[_frameSourceCount] = s;
                    _frameMixes[_frameSourceCount] = WaterSoundField.Mix(in s, _masker);
                    _frameSourceCount++;
                }
            }
            PlaceRig();
            AssignSources(enclosure);
        }
        FinishFrame(dt);
    }

    private void Idle(double dt)
    {
        foreach (Voice v in _all) v.Claimed = false;
        _frameSourceCount = 0; _proximity = 0.0;
        for (int i = 0; i < _cutoffTarget.Length; i++) _cutoffTarget[i] = FullScaleCutoffHz;
        FinishFrame(dt);
    }

    private static double Db(double db) => Math.Pow(10.0, db / 20.0);

    private static double Horizontal(in WaterSoundListener listener, in WaterSoundSource s)
    {
        // Around wraps; the node does not know the circumference here, so the test seam's fixed sources stay far from the seam and the plain difference serves.
        double da = s.Around - listener.Around, dz = s.Across - listener.Across;
        return Math.Sqrt(da * da + dz * dz);
    }

    private bool TryReadInputs(out WaterAmbienceInputs inputs)
    {
        if (InputOverride != null) { inputs = InputOverride(); return true; }
        inputs = default;
        if (_explorer == null || _grid == null || _grid.TerrainSampler == null) return false;
        RingCanonicalPosition p = ResolveCameraListener();
        double rain = 0.0, wind = 0.0;
        if (_local != null && GodotObject.IsInstanceValid(_local))
        {
            rain = Finite(_local.PrecipitationIntensity);
            Vector2 w = _local.ResolvedWindMetersPerSecond;
            wind = w.IsFinite() ? w.Length() : 0.0;
        }
        double blend = _explorer.HasMeta(UndergroundMetadata) ? _explorer.GetMeta(UndergroundMetadata).AsDouble() : 0.0;
        double depth = _explorer.HasMeta(DepthMetadata) ? _explorer.GetMeta(DepthMetadata).AsDouble() : 0.0;
        inputs = new WaterAmbienceInputs(new WaterSoundListener(p.AroundMeters, p.VerticalMeters, p.AcrossMeters), rain, wind,
            _explorer.FastTravelSpeedMetersPerSecond, _explorer.IsCameraUnderwater, depth, blend, SeaWaveHeight());
        return true;
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0.0;

    /// <summary>The sea's significant wave height from the FFT's own spectrum (V10), cached on what the spectrum depends on.</summary>
    private double SeaWaveHeight()
    {
        if (_fft == null || !GodotObject.IsInstanceValid(_fft) || !_fft.Enabled) return FallbackSeaWaveHeightMeters;
        double wind = _fft.ActualWindSpeedMetersPerSecond;
        double scale = _fft.GeometryHeightScale;
        int cascades = Math.Clamp(_fft.ActiveCascadeCount, 1, 4);
        double key = Math.Round(wind * 20.0) + 1e4 * scale + 1e7 * cascades + 1e9 * _fft.ActiveResolution + _fft.Period0.X * 1e3;
        if (key == _spectrumKey) return _spectrumValue;
        Span<double> periods = stackalloc double[4];
        periods[0] = _fft.Period0.X;
        periods[1] = _fft.Period1.X;
        periods[2] = _fft.Period2.X > 0.01f ? _fft.Period2.X : _fft.Period1.X * 4.0;
        periods[3] = _fft.Period3.X > 0.01f ? _fft.Period3.X : _fft.Period1.X * 16.0;
        _spectrumValue = WaterWaveSpectrum.SignificantHeight(_fft.ActiveResolution, periods[..cascades], wind, scale);
        _spectrumKey = key;
        return _spectrumValue;
    }

    private IWaterSoundFinder? EnsureFinder()
    {
        if (FinderOverride != null) return _finder = FinderOverride;
        RingTerrainSampler? sampler = _grid?.TerrainSampler;
        if (sampler == null || _grid == null) return null;
        if (_finder == null || !ReferenceEquals(sampler, _finderSampler))
        {
            _finderSampler = sampler;
            // The closure reads the grid's sampler at each call, so a regenerated world is followed without a new finder.
            VoxelChunkGridManager grid = _grid;
            _finder = new WaterSoundSourceFinder((a, z) => (grid.TerrainSampler ?? sampler).GetTerrainSample(a, z), sampler.RuntimeRiverGraph,
                (long)Math.Round(grid.GetCanonicalRingCircumferenceMeters64()), Mathf.RoundToInt(grid.RingAcrossWidthMeters));
        }
        return _finder;
    }

    // ---- Sources to voices ----------------------------------------------------------------------------------------

    private void AssignSources(double enclosure)
    {
        Span<int> shore = stackalloc int[ShoreVoices], river = stackalloc int[RiverVoices], drop = stackalloc int[DropVoices];
        int ns = 0, nr = 0, nd = 0;
        double loudestOpen = 0.0;
        Span<double> loudest = stackalloc double[3];
        loudest.Clear();
        for (int i = 0; i < _frameSourceCount; i++)
        {
            WaterSoundSource s = _frameSources[i];
            WaterSoundMix mix = _frameMixes[i];
            _proximity = Math.Max(_proximity, mix.Gain * enclosure);
            switch (s.Kind)
            {
                case WaterSoundKind.SeaShore or WaterSoundKind.LakeShore: if (ns < shore.Length) shore[ns++] = i; break;
                case WaterSoundKind.River: if (nr < river.Length) river[nr++] = i; break;
                case WaterSoundKind.Drop: if (nd < drop.Length) drop[nd++] = i; break;
                default:
                    if (mix.Gain > loudestOpen || !_open!.Claimed)
                    {
                        loudestOpen = mix.Gain;
                        _open!.Claimed = true;
                        Apply(_open, in s, in mix, enclosure, _shoreLoops, 0);
                    }
                    break;
            }
        }
        DriveLayer(_shore, shore[..ns], enclosure, _shoreLoops, 0, loudest);
        DriveLayer(_river, river[..nr], enclosure, _riverLoops, 1, loudest);
        DriveLayer(_drop, drop[..nd], enclosure, _dropLoops, 2, loudest);
    }

    private static long KeyOf(in WaterSoundSource s)
        => s.Kind switch
        {
            WaterSoundKind.SeaShore => 1,
            WaterSoundKind.LakeShore => 2,
            WaterSoundKind.River => 1_000 + s.Stem,
            WaterSoundKind.Drop => 1_000_000L + s.Stem * 1_000_003L + (long)Math.Round(s.Around) * 31L + (long)Math.Round(s.Across),
            _ => 3,
        };

    /// <summary>Gives each source of a layer a voice: the voice that already holds its key, else a free one (a voice whose source has gone, quietest first). A source that takes over a voice fades in over a second.</summary>
    private void DriveLayer(Voice[] voices, ReadOnlySpan<int> indices, double enclosure, AudioStream?[] loops, int bus, Span<double> loudest)
    {
        Span<int> voiceOf = stackalloc int[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            voiceOf[i] = -1;
            long key = KeyOf(in _frameSources[indices[i]]);
            for (int v = 0; v < voices.Length; v++) if (voices[v].HasKey && voices[v].Key == key && !voices[v].Claimed) { voiceOf[i] = v; voices[v].Claimed = true; break; }
        }
        for (int i = 0; i < indices.Length; i++)
        {
            if (voiceOf[i] >= 0) continue;
            int best = -1; double bestGain = double.MaxValue;
            for (int v = 0; v < voices.Length; v++)
            {
                if (voices[v].Claimed) continue;
                double g = Math.Max(voices[v].Players[0].Gain, voices[v].Players[1].Gain);
                if (!voices[v].HasKey) g = -1.0;
                if (g < bestGain) { bestGain = g; best = v; }
            }
            if (best < 0) continue;
            voices[best].Claimed = true; voices[best].Key = KeyOf(in _frameSources[indices[i]]); voices[best].HasKey = true; voices[best].FadeIn = FadeInSeconds;
            voiceOf[i] = best;
        }
        for (int i = 0; i < indices.Length; i++)
        {
            if (voiceOf[i] < 0) continue;
            Voice voice = voices[voiceOf[i]];
            Apply(voice, in _frameSources[indices[i]], in _frameMixes[indices[i]], enclosure, loops, bus);
            if (voice.MixGain > loudest[bus]) { loudest[bus] = voice.MixGain; _cutoffTarget[bus] = voice.Cutoff; }
        }
    }

    private double Row(WaterSoundKind kind) => Db(WaterVolumeDb + kind switch
    {
        WaterSoundKind.SeaShore or WaterSoundKind.OpenSea => SeaVolumeDb,
        WaterSoundKind.LakeShore or WaterSoundKind.OpenLake => LakeVolumeDb,
        _ => RiverVolumeDb,
    });

    /// <summary>Sets a voice's targets from its source: the gain of W2 times the volume rows, split over the two players that carry its two loudest loops (renormalised), and its place.</summary>
    private void Apply(Voice voice, in WaterSoundSource s, in WaterSoundMix mix, double enclosure, AudioStream?[] loops, int bus)
    {
        double gain = mix.Gain * Row(s.Kind) * enclosure;
        voice.Kind = s.Kind; voice.Distance = s.Distance; voice.Level = mix.Level; voice.Cutoff = mix.CutoffHz; voice.MixGain = mix.Gain; voice.Weights = mix.Weights;
        if (voice.Layer == Layer.Open) { voice.Players[0].Target = gain; return; }
        Span<double> w = stackalloc double[4];
        w[0] = mix.Weights.A; w[1] = mix.Weights.B; w[2] = mix.Weights.C; w[3] = mix.Weights.D;
        int count = loops.Length;
        int first = -1, second = -1;
        for (int l = 0; l < count; l++)
        {
            if (!(w[l] > 1e-4)) continue;
            if (first < 0 || w[l] > w[first]) { second = first; first = l; }
            else if (second < 0 || w[l] > w[second]) second = l;
        }
        double norm = Math.Sqrt((first >= 0 ? w[first] * w[first] : 0.0) + (second >= 0 ? w[second] * w[second] : 0.0));
        // A loop already held keeps its player; a wanted loop with none takes a silent player; a player changes its loop only while silent.
        Span<int> wanted = stackalloc int[2];
        wanted[0] = first; wanted[1] = second;
        Span<int> holder = stackalloc int[2];
        holder[0] = -1; holder[1] = -1;
        for (int k = 0; k < 2; k++)
            for (int p = 0; p < 2; p++)
                if (wanted[k] >= 0 && voice.Players[p].Loop == wanted[k]) holder[k] = p;
        for (int k = 0; k < 2; k++)
        {
            if (wanted[k] < 0 || holder[k] >= 0) continue;
            for (int p = 0; p < 2; p++)
            {
                if (p == holder[0] || p == holder[1]) continue;
                Player player = voice.Players[p];
                if (player.Gain > AudibleGain && player.Loop >= 0) continue;
                SetLoop(player, wanted[k], loops);
                holder[k] = p;
                break;
            }
        }
        for (int p = 0; p < 2; p++) voice.Players[p].Target = 0.0;
        for (int k = 0; k < 2; k++)
            if (wanted[k] >= 0 && holder[k] >= 0 && norm > 0.0) voice.Players[holder[k]].Target = gain * w[wanted[k]] / norm;
        PlaceVoice(voice, in s);
    }

    private void SetLoop(Player player, int loop, AudioStream?[] loops)
    {
        if (player.Playing) StopPlayer(player);
        player.Loop = loop;
        player.Stream = loop >= 0 && loop < loops.Length ? loops[loop] : null;
        if (player.Spatial != null) player.Spatial.Stream = player.Stream;
        player.Gain = 0.0;
    }

    // ---- Placement ------------------------------------------------------------------------------------------------

    private Vector3 _axisAround = Vector3.Right, _axisAcross = Vector3.Back, _axisUp = Vector3.Up;

    private void PlaceRig()
    {
        if (_rig == null) return;
        if (_grid == null || _explorer == null || InputOverride != null)
        {
            Camera3D? cam = GetViewport()?.GetCamera3D();
            if (cam != null) _rig.GlobalPosition = cam.GlobalPosition;
            return;
        }
        RingCanonicalPosition l = new(_inputs.Listener.Around, _inputs.Listener.Vertical, _inputs.Listener.Across);
        Vector3 here = _grid.CanonicalRingPositionToWorld(l);
        Camera3D? camera = GetViewport().GetCamera3D();
        _rig.GlobalPosition = camera != null ? camera.GlobalPosition : here;
        // Ring-frame axes at the listener, from a 4 m step so a floating origin never degrades them.
        Vector3 around = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(l.AroundMeters + 4, l.VerticalMeters, l.AcrossMeters)) - here).Normalized();
        Vector3 across = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(l.AroundMeters, l.VerticalMeters, l.AcrossMeters + 4)) - here).Normalized();
        Vector3 up = (_grid.CanonicalRingPositionToWorld(new RingCanonicalPosition(l.AroundMeters, l.VerticalMeters + 4, l.AcrossMeters)) - here).Normalized();
        if (!around.IsFinite() || !across.IsFinite() || !up.IsFinite()) return;
        _axisAround = around; _axisAcross = across; _axisUp = up;
    }

    /// <summary>W7: in the direction of the source's nearest point, at min(x, 40) m, panned by smoothstep(0, 2a, h): over the water the sound is all around, from a bank it has a direction.</summary>
    private void PlaceVoice(Voice voice, in WaterSoundSource s)
    {
        WaterSoundListener l = _inputs.Listener;
        double da = s.Around - l.Around;
        if (_grid != null && InputOverride == null)
        {
            double c = _grid.GetCanonicalRingCircumferenceMeters64();
            da %= c;
            if (da > c / 2) da -= c; else if (da < -c / 2) da += c;
        }
        double dz = s.Across - l.Across, dv = s.Vertical - l.Vertical;
        double h = Math.Sqrt(da * da + dz * dz);
        Vector3 direction = _axisAround * (float)da + _axisAcross * (float)dz + _axisUp * (float)dv;
        double length = direction.Length();
        Vector3 offset = length > 1e-4 ? direction / (float)length * (float)Math.Min(s.Distance, PositionMaxMeters) : Vector3.Zero;
        double t = Math.Clamp(h / Math.Max(1e-6, 2.0 * s.HalfSize), 0.0, 1.0);
        float panning = (float)(t * t * (3.0 - 2.0 * t));
        foreach (Player p in voice.Players)
        {
            AudioStreamPlayer3D a = p.Spatial!;
            a.Position = offset;
            if (!Mathf.IsEqualApprox(a.PanningStrength, panning)) a.PanningStrength = panning;
        }
    }

    // ---- Playing --------------------------------------------------------------------------------------------------

    private void FinishFrame(double dt)
    {
        // At most eight players sound at once: the quietest give way.
        int sounding = 0;
        foreach (Player p in _players) if (p.Target > AudibleGain) sounding++;
        while (sounding > MaxSounding)
        {
            Player? quietest = null;
            foreach (Player p in _players) if (p.Target > AudibleGain && (quietest == null || p.Target < quietest.Target)) quietest = p;
            if (quietest == null) break;
            quietest.Target = 0.0; sounding--;
        }
        _soundingPlayers = 0;
        foreach (Voice voice in _all)
        {
            if (!voice.Claimed) { foreach (Player p in voice.Players) p.Target = 0.0; }
            voice.FadeIn = Math.Max(0.0, voice.FadeIn - dt);
            foreach (Player p in voice.Players) Drive(p, dt, voice.FadeIn > 0.0 ? FadeInSeconds : SlewSeconds);
        }
        UpdateFilters(dt);
        PublishProximity();
        _nodeMs = Math.Max(0.0, (Stopwatch.GetTimestamp() - _frameStart) * 1000.0 / Stopwatch.Frequency - _finderMs);
    }

    private void Drive(Player p, double dt, double seconds)
    {
        // A slew that is 95 % of the way in `seconds` (a time constant of a third of it), from wherever the gain is.
        p.Gain += (p.Target - p.Gain) * (1.0 - Math.Exp(-dt / (seconds / 3.0)));
        bool silent = p.Gain <= AudibleGain && p.Target <= AudibleGain;
        if (silent)
        {
            p.Gain = Math.Min(p.Gain, p.Target);
            p.SilentSeconds += dt;
            if (p.Playing && p.SilentSeconds >= SilentStopSeconds) StopPlayer(p);
            SetVolume(p, SilentDb);
            return;
        }
        p.SilentSeconds = 0.0;
        if (p.Stream == null) { SetVolume(p, SilentDb); return; }
        if (!p.Playing) StartPlayer(p);
        _soundingPlayers++;
        SetVolume(p, p.Gain > AudibleGain ? (float)(20.0 * Math.Log10(p.Gain)) : SilentDb);
    }

    private void SetVolume(Player p, float db)
    {
        if (p.Spatial != null) p.Spatial.VolumeDb = db; else p.Flat!.VolumeDb = db;
    }

    private void StartPlayer(Player p)
    {
        double length = p.Stream!.GetLength();
        float from = length > 0.0 ? (float)(_random.NextDouble() * length) : 0f;
        // A new start is at a random place in its loop, so two layers on one loop never line up.
        if (p.Spatial != null) { p.Spatial.Stream = p.Stream; p.Spatial.Play(from); }
        else { p.Flat!.Stream = p.Stream; p.Flat.Play(from); }
        p.Playing = true;
    }

    private static void StopPlayer(Player p)
    {
        if (p.Spatial != null) p.Spatial.Stop(); else p.Flat?.Stop();
        p.Playing = false;
    }

    private void UpdateFilters(double dt)
    {
        double blend = 1.0 - Math.Exp(-dt / CutoffSmoothSeconds);
        for (int i = 0; i < _filters.Length; i++)
        {
            // In the log domain, so the sweep sounds even.
            _cutoffNow[i] = Math.Exp(Math.Log(_cutoffNow[i]) + (Math.Log(Math.Max(1.0, _cutoffTarget[i])) - Math.Log(_cutoffNow[i])) * blend);
            _filters[i] ??= FindFilter(FilterBuses[i]);
            if (_filters[i] != null) _filters[i]!.CutoffHz = (float)_cutoffNow[i];
        }
    }

    private void PublishProximity()
    {
        Node? target = MetaTarget;
        if (target == null || !GodotObject.IsInstanceValid(target)) return;
        target.SetMeta(ProximityMetadataName, (float)Math.Clamp(_proximity, 0.0, 1.0));
    }

    // ---- Camera listener (as RingWeatherAmbience.ResolveCameraListener finds it) ----------------------------------

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
        double heightOffset = -(2.0 * radial * radialOffset + radialOffset * radialOffset + tangentOffset * tangentOffset)
            / (radial + cameraRadial);
        return new RingCanonicalPosition(
            listener.AroundMeters + geometry.RingRadiusMeters * Math.Atan2(tangentOffset, radial + radialOffset),
            listener.VerticalMeters + heightOffset, listener.AcrossMeters + offset.Y).Wrap(geometry.CircumferenceMeters);
    }

    // ---- Diagnostics ----------------------------------------------------------------------------------------------

    /// <summary>Per voice the kind, distance, level, gain, cutoff, loop weights and whether it plays; the masker; the finder's cost (T3R-10d, as RingWeatherAmbience has).</summary>
    public Godot.Collections.Dictionary GetWaterAmbienceDiagnostics()
    {
        var voices = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        int playing = 0;
        foreach (Voice v in _all)
        {
            bool any = false;
            foreach (Player p in v.Players) if (p.Playing) { any = true; playing++; }
            voices.Add(new Godot.Collections.Dictionary
            {
                ["layer"] = v.Layer.ToString(), ["claimed"] = v.Claimed, ["kind"] = v.Claimed ? v.Kind.ToString() : "", ["distance"] = v.Distance, ["level_db"] = v.Level,
                ["mix_gain"] = v.MixGain, ["cutoff_hz"] = v.Cutoff,
                ["weights"] = new[] { v.Weights.A, v.Weights.B, v.Weights.C, v.Weights.D },
                ["player_gain"] = Gains(v), ["player_loop"] = Loops(v), ["playing"] = any,
            });
        }
        int sounding = 0;
        foreach (Player p in _players) if (p.Playing && p.Gain > AudibleGain) sounding++;
        return new Godot.Collections.Dictionary
        {
            ["voices"] = voices, ["player_count"] = _players.Length, ["playing_count"] = playing, ["sounding_count"] = sounding,
            ["masker_db"] = _masker, ["sea_wave_height"] = _seaWaveHeight, ["proximity"] = _proximity, ["underwater"] = _underwater,
            ["finder_ms"] = _finderMs, ["node_ms"] = _nodeMs, ["finder_calls"] = _finderCalls, ["source_count"] = _frameSourceCount,
            ["loops_assigned"] = LoopsAssigned(), ["cutoffs_hz"] = new[] { _cutoffNow[0], _cutoffNow[1], _cutoffNow[2] },
        };
    }

    private static double[] Gains(Voice v) { var g = new double[v.Players.Length]; for (int i = 0; i < g.Length; i++) g[i] = v.Players[i].Gain; return g; }
    private static int[] Loops(Voice v) { var g = new int[v.Players.Length]; for (int i = 0; i < g.Length; i++) g[i] = v.Players[i].Loop; return g; }

    /// <summary>The nine loops as assigned to the node: (name, has a stream, loops). The test checks lengths and channels from the streams themselves.</summary>
    private Godot.Collections.Array<Godot.Collections.Dictionary> LoopsAssigned()
    {
        var list = new Godot.Collections.Array<Godot.Collections.Dictionary>();
        void Add(string name, AudioStream? s) => list.Add(new Godot.Collections.Dictionary { ["name"] = name, ["assigned"] = s != null, ["loop"] = s is AudioStreamOggVorbis o && o.Loop, ["length"] = s?.GetLength() ?? 0.0 });
        Add("lap", _shoreLoops.Length > 0 ? _shoreLoops[0] : null); Add("surf_break", _shoreLoops.Length > 1 ? _shoreLoops[1] : null); Add("surf_far", _shoreLoops.Length > 2 ? _shoreLoops[2] : null);
        Add("open_water", _open?.Players[0].Stream);
        Add("babble", _riverLoops.Length > 0 ? _riverLoops[0] : null); Add("rush", _riverLoops.Length > 1 ? _riverLoops[1] : null); Add("roar", _riverLoops.Length > 2 ? _riverLoops[2] : null);
        Add("waterfall", _dropLoops.Length > 3 ? _dropLoops[3] : null);
        Add("underwater", _under?.Players[0].Stream);
        return list;
    }
}
