using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Water;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T3R-10d (docs/work-orders/terrain-vegetation/T3.md, Spec W2, W5 to W7): the water sound as the player would hear it, measured on the real output with AudioEffectCapture on WaterAbove and
/// WaterUnderwater in front of a muted sink (the pattern of tools/thunder_audio_capture_test.gd). A bare scene holds the RingWaterAmbience node, a camera and fixed sources (a finder that reports a
/// fixed list, recomputing the distances for the listener as the real finder does), so every number is the law's and the node's, not the world's. Checks: the height (silent 200 m above open sea, at the
/// water above -60 dBFS, silent at 600 m from or above a fixed shore; no player plays after 6 s), the distance (30, 100 and 300 m above a torrent differ as W2 says within 2 dB, silent at 2 km), no steps
/// walking away from a brook, a duller far sound, a river that is not a splash on loop, under the surface, underground, the meta and the nine loops. Negative control: with the height switched off
/// through the node's test switch the height check must fail. Also prints the significant wave height the FFT spectrum gives (V10). Prints WATER_AMBIENCE_CAPTURE=PASS|FAIL.
/// </summary>
public partial class WaterAmbienceCaptureTest : Node
{
    private const string SinkBus = "WaterTestSink";
    private const double WindowSeconds = 0.25;

    private readonly List<string> _failures = new();
    private void Fail(string message) { if (_failures.Count < 40) _failures.Add(message); }
    private void Check(bool ok, string message) { if (!ok) Fail(message); }
    private static void Detail(string text) => Console.WriteLine("WATER_AMBIENCE_CAPTURE_DETAIL " + text);
    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private RingWaterAmbience _node = null!;
    private Node _metaHost = null!;
    private AudioEffectCapture _captureAbove = null!, _captureUnder = null!;
    private Meter _above = new(), _under = new();
    private readonly FixedFinder _finder = new();
    private WaterAmbienceInputs _inputs;
    private ulong _startMs;
    private double _walkStartedAt = -1, _walkStartMeters, _walkSpeed;
    private bool _done;

    /// <summary>One bus's windows of quarter-second RMS (dBFS, both channels) and the share of the energy above 4 kHz.</summary>
    private sealed class Meter
    {
        public readonly List<double> Db = new(), HighShare = new();
        private double _sum, _highSum;
        private int _frames;
        private double _lx, _ly, _hp1, _hp2, _hx1, _hy1;
        public static int Window = 11025;
        public void Add(float l, float r)
        {
            double mono = 0.5 * (l + r);
            // 4 kHz high-pass, two one-pole stages (12 dB per octave), for the dullness check.
            double hp = HighPass(mono);
            _sum += 0.5 * ((double)l * l + (double)r * r);
            _highSum += hp * hp;
            if (++_frames < Window) return;
            Db.Add(10.0 * Math.Log10(Math.Max(_sum / _frames, 1e-20)));
            HighShare.Add(_highSum / Math.Max(_sum / 1.0, 1e-20));
            _sum = 0; _highSum = 0; _frames = 0;
        }
        private double HighPass(double x)
        {
            double a = Math.Exp(-2.0 * Math.PI * 4000.0 / AudioServer.GetMixRate());
            double y1 = a * (_hy1 + x - _lx); _lx = x; _hy1 = y1;
            double y2 = a * (_hp1 + y1 - _ly); _ly = y1; _hp1 = y2;
            return y2;
        }
        public double MeanDb(int skip = 0) { var v = Db.Skip(skip).ToList(); return v.Count == 0 ? double.NegativeInfinity : 10.0 * Math.Log10(v.Average(d => Math.Pow(10.0, d / 10.0))); }
        public double Share(int skip = 0) { var v = HighShare.Skip(skip).ToList(); return v.Count == 0 ? 0 : v.Average(); }
    }

    // ------------------------------------------------------------------ the fixed finder

    private sealed class FixedFinder : IWaterSoundFinder
    {
        public readonly record struct Spec(WaterSoundKind Kind, double Power, double HalfSize, double Around, double Across, double Top, double Level, double WaveHeight, double Discharge, double Drop, int Stem);
        public List<Spec> Specs = new();
        private readonly WaterSoundSource[] _out = new WaterSoundSource[WaterSoundSourceFinder.MaxSources];
        private int _n;
        public double SeaWaveHeight { get; set; }
        public double Masker { get; set; }
        public int LastSamplerCalls => 0;
        public ReadOnlySpan<WaterSoundSource> Sources => new(_out, 0, _n);

        public void Update(in WaterSoundListener l, int maxSamplerCalls)
        {
            _n = 0;
            foreach (Spec k in Specs)
            {
                bool open = k.Kind == WaterSoundKind.OpenSea || k.Kind == WaterSoundKind.OpenLake;
                bool shore = k.Kind == WaterSoundKind.SeaShore || k.Kind == WaterSoundKind.LakeShore;
                double around = open ? l.Around : k.Around, across = open ? l.Across : k.Across;
                double da = around - l.Around, dz = across - l.Across, dist = Math.Sqrt(da * da + dz * dz);
                double reach = open ? dist : shore ? 0.0 : k.HalfSize;
                double h = Math.Max(0.0, dist - reach);
                double along = dist > 1e-9 ? Math.Min(reach, dist) / dist : 0.0;
                double v, nearV;
                if (l.Vertical > k.Top) { v = l.Vertical - k.Top; nearV = k.Top; }
                else if (l.Vertical < k.Level) { v = open ? 0.0 : k.Level - l.Vertical; nearV = k.Level; }
                else { v = 0.0; nearV = l.Vertical; }
                _out[_n++] = new WaterSoundSource(k.Kind, k.Power, k.HalfSize, around - da * along, nearV, across - dz * along, WaterSoundField.Distance(h, v),
                    k.WaveHeight, k.Discharge, k.Drop, 0.0, k.Stem);
            }
        }
    }

    private static FixedFinder.Spec OpenSea(double hs) => new(WaterSoundKind.OpenSea, WaterSoundField.OpenWaterPower(hs), WaterSoundConstants.Default.OpenWaterHalfSize, 0, 0, 0, 0, hs, 0, 0, -1);
    private static FixedFinder.Spec SeaShore(double hs) => new(WaterSoundKind.SeaShore, WaterSoundField.ShorePower(hs), WaterSoundField.ShoreHalfSize(hs), 0, 0, 0, 0, hs, 0, 0, -1);
    private static FixedFinder.Spec RiverSpec(double q, double sr, double width, double depth, double whiteness, int stem)
        => new(WaterSoundKind.River, WaterSoundField.RiverPower(q, sr, q / (width * depth), depth, whiteness), width / 2.0, 0, 0, 0, 0, 0, q, 0, stem);
    private static FixedFinder.Spec Torrent => RiverSpec(200, 0.03, 36, 1.75, 1.0, 1);
    private static FixedFinder.Spec Brook => RiverSpec(0.5, 0.02, 2, 0.35, 0.30, 2);
    private static FixedFinder.Spec Mountain => RiverSpec(20, 0.015, 13, 1.0, 0.35, 3);

    // ------------------------------------------------------------------ lifecycle

    public override void _Ready()
    {
        _startMs = Time.GetTicksMsec();
        Meter.Window = (int)(AudioServer.GetMixRate() * WindowSeconds);
        _ = RunAsync();
    }

    public override void _Process(double delta)
    {
        if (_done) return;
        if (Time.GetTicksMsec() - _startMs > 200_000) { Fail("real-tick deadline exceeded"); Finish(); return; }
        Drain(_captureAbove, _above);
        Drain(_captureUnder, _under);
        if (_walkStartedAt >= 0)
        {
            double t = (Time.GetTicksMsec() - _startMs) / 1000.0 - _walkStartedAt;
            _inputs = _inputs with { Listener = new WaterSoundListener(_walkStartMeters + _walkSpeed * t, _inputs.Listener.Vertical, _inputs.Listener.Across) };
        }
    }

    private static void Drain(AudioEffectCapture? capture, Meter meter)
    {
        if (capture == null) return;
        int n;
        while ((n = capture.GetFramesAvailable()) > 0)
        {
            var frames = capture.GetBuffer(Math.Min(n, 4096));
            foreach (Vector2 f in frames) meter.Add(f.X, f.Y);
        }
    }

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds, true, false, true), SceneTreeTimer.SignalName.Timeout);

    private void Finish()
    {
        if (_done) return;
        _done = true;
        if (_failures.Count == 0) Console.WriteLine("WATER_AMBIENCE_CAPTURE=PASS");
        else Console.Error.WriteLine("WATER_AMBIENCE_CAPTURE=FAIL " + string.Join(" | ", _failures));
        Cleanup();
        GetTree().Quit(_failures.Count == 0 ? 0 : 1);
    }

    private void Cleanup()
    {
        int above = AudioServer.GetBusIndex("WaterAbove"), under = AudioServer.GetBusIndex("WaterUnderwater");
        if (above >= 0) AudioServer.SetBusSend(above, "Master");
        if (under >= 0) AudioServer.SetBusSend(under, "Master");
        int sink = AudioServer.GetBusIndex(SinkBus);
        if (sink >= 0) AudioServer.RemoveBus(sink);
    }

    private void Setup()
    {
        // A quiet sink to the left of the water buses; the capture effects sit on the buses themselves, upstream of their (muted) sends.
        AudioServer.AddBus(1);
        AudioServer.SetBusName(1, SinkBus);
        AudioServer.SetBusVolumeDb(1, -80f);
        int above = AudioServer.GetBusIndex("WaterAbove"), under = AudioServer.GetBusIndex("WaterUnderwater");
        Check(above >= 0 && under >= 0, "WaterAbove or WaterUnderwater bus missing");
        AudioServer.SetBusSend(above, SinkBus);
        AudioServer.SetBusSend(under, SinkBus);
        _captureAbove = new AudioEffectCapture { BufferLength = 2.0f };
        _captureUnder = new AudioEffectCapture { BufferLength = 2.0f };
        AudioServer.AddBusEffect(above, _captureAbove);
        AudioServer.AddBusEffect(under, _captureUnder);
        foreach (string bus in new[] { "WaterShore", "WaterRiver", "WaterDrop" }) Check(AudioServer.GetBusIndex(bus) >= 0, $"bus {bus} missing");

        AddChild(new Camera3D { Current = true });
        _metaHost = new Node { Name = "MetaHost" };
        AddChild(_metaHost);
        const string Dir = "res://resources/audio/water/";
        _node = new RingWaterAmbience
        {
            Name = "WaterAmbience",
            LapStream = GD.Load<AudioStream>(Dir + "water_lap_loop.ogg"), SurfBreakStream = GD.Load<AudioStream>(Dir + "surf_break_loop.ogg"), SurfFarStream = GD.Load<AudioStream>(Dir + "surf_far_loop.ogg"),
            OpenWaterStream = GD.Load<AudioStream>(Dir + "open_water_loop.ogg"), BabbleStream = GD.Load<AudioStream>(Dir + "stream_babble_loop.ogg"), RushStream = GD.Load<AudioStream>(Dir + "river_rush_loop.ogg"),
            RoarStream = GD.Load<AudioStream>(Dir + "river_roar_loop.ogg"), WaterfallStream = GD.Load<AudioStream>(Dir + "waterfall_loop.ogg"), UnderwaterStream = GD.Load<AudioStream>(Dir + "underwater_loop.ogg"),
            FinderOverride = _finder, InputOverride = () => _inputs, MetaTarget = _metaHost,
        };
        AddChild(_node);
        _inputs = Still(0, 0);
    }

    private static WaterAmbienceInputs Still(double around, double vertical) => new(new WaterSoundListener(around, vertical, 0), 0, 0, 0, false, 0, 0, 0.5);

    private void Place(double around, double vertical, params FixedFinder.Spec[] specs)
    {
        _walkStartedAt = -1;
        _finder.Specs = specs.ToList();
        _inputs = Still(around, vertical);
    }

    private async Task<(double Db, double Share)> Measure(double settle, double seconds, bool under = false)
    {
        await Wait(settle);
        var meter = new Meter();
        if (under) _under = meter; else _above = meter;
        await Wait(seconds);
        return (meter.MeanDb(), meter.Share());
    }

    private (int Playing, int Sounding) Counts()
    {
        var d = _node.GetWaterAmbienceDiagnostics();
        return ((int)d["playing_count"], (int)d["sounding_count"]);
    }

    // ------------------------------------------------------------------ the scenarios

    private async Task RunAsync()
    {
        try
        {
            Setup();
            SpectrumHeights();
            await Loops();
            await Height();
            await ShoreHeight();
            await Distance();
            await Walk();
            await Dullness();
            await NotASplash();
            await UnderSurfaceAndGround();
            await Meta();
            await NegativeControl();
        }
        catch (Exception e) { Fail("exception " + e); }
        Finish();
    }

    /// <summary>V10: the significant wave height of the FFT's spectrum, printed for the default cascades; sane, and rising with the wind.</summary>
    private void SpectrumHeights()
    {
        double[] periods = { 12, 79, 316, 1264 };
        double last = 0;
        foreach (double wind in new[] { 1.0, 3.0, 6.0, 10.0, 15.0, 25.0 })
        {
            double hs = WaterWaveSpectrum.SignificantHeight(128, periods, wind, 1.0);
            Detail($"V10 Hs from the FFT spectrum (4 cascades, 128): wind {wind} m/s -> {hs:F3} m (fully developed sea would be {0.21 * wind * wind / 9.81:F2} m)");
            Check(hs > last, $"V10: Hs does not rise with the wind at {wind} m/s");
            last = hs;
        }
        // The default graphics preset runs two cascades (tiles of 12 m and 79 m): a sea of a fraction of a metre to a metre at 6 m/s.
        double h6 = WaterWaveSpectrum.SignificantHeight(128, periods[..2], 6.0, 1.0);
        Detail($"V10 Hs from the FFT spectrum (2 cascades, the default preset): wind 6 m/s -> {h6:F3} m");
        Check(h6 > 0.05 && h6 < 3.0, $"V10: Hs at 6 m/s with two cascades is {h6:F3} m, outside 0.05 to 3 m");
        Check(Math.Abs(WaterWaveSpectrum.SignificantHeight(128, periods[..2], 6.0, 2.0) - 2.0 * h6) < 1e-9, "V10: Hs is not linear in the height scale");
        Check(WaterWaveSpectrum.SignificantHeight(128, periods, 0.0, 1.0) == 0.0, "V10: Hs at no wind is not 0");
    }

    private async Task Loops()
    {
        await Wait(0.3);
        var d = _node.GetWaterAmbienceDiagnostics();
        var loops = (Godot.Collections.Array<Godot.Collections.Dictionary>)d["loops_assigned"];
        // T3R-10c's table: length in seconds and whether the loop is mono.
        var table = new (string Name, double Length, bool Mono)[]
        {
            ("lap", 40, true), ("surf_break", 64, true), ("surf_far", 32, true), ("open_water", 40, false), ("babble", 32, true), ("rush", 32, true), ("roar", 32, true), ("waterfall", 32, true), ("underwater", 40, false),
        };
        foreach (var row in table)
        {
            var entry = loops.First(l => (string)l["name"] == row.Name);
            Check((bool)entry["assigned"], $"loops: {row.Name} not assigned");
            Check((bool)entry["loop"], $"loops: {row.Name} does not loop");
            Check(Math.Abs((double)entry["length"] - row.Length) < 0.15, $"loops: {row.Name} is {F((double)entry["length"])} s, expected {row.Length} s");
        }
        Check((int)d["player_count"] <= 16, $"players: {d["player_count"]} exist, at most 16 allowed");
        Detail($"loops: nine assigned and looping; {d["player_count"]} players exist");
    }

    private async Task Height()
    {
        // Over a fixed open sea of 0.5 m waves, at the water, then 200 m up.
        Place(0, 0, OpenSea(0.5));
        (double atWater, _) = await Measure(2.0, 2.0);
        Detail($"height: at the water over open sea {F(atWater)} dBFS");
        Check(atWater > -60, $"height: at the water the output is {F(atWater)} dBFS, not above -60");
        _finder.Specs = new List<FixedFinder.Spec> { OpenSea(0.5) };
        _inputs = Still(0, 200);
        (double up, _) = await Measure(2.0, 1.0);
        Detail($"height: 200 m above open sea {F(up)} dBFS (2 to 3 s after the rise)");
        Check(up < -75, $"height: 200 m above open sea the output is {F(up)} dBFS, not under -75");
        await Wait(3.4);
        (int playing, _) = Counts();
        Detail($"height: players playing 6.4 s after the rise: {playing}");
        Check(playing == 0, $"height: {playing} players still play 6 s after the rise");
    }

    private async Task ShoreHeight()
    {
        Place(600, 0, SeaShore(0.5));
        (double far, _) = await Measure(2.0, 1.5);
        Check(far < -75, $"shore: 600 m from a shore of 0.5 m waves the output is {F(far)} dBFS, not under -75");
        Place(0, 600, SeaShore(0.5));
        (double up, _) = await Measure(2.0, 1.5);
        Check(up < -75, $"shore: 600 m above a shore of 0.5 m waves the output is {F(up)} dBFS, not under -75");
        Place(2, 0, SeaShore(0.5));
        (double near, _) = await Measure(2.0, 2.0);
        Detail($"shore: 600 m away {F(far)} dBFS, 600 m above {F(up)} dBFS, at the shore {F(near)} dBFS");
        Check(near > -60, $"shore: at the shore the output is {F(near)} dBFS, not above -60");
    }

    private async Task Distance()
    {
        // A torrent heard from 30, 100 and 300 m straight above it (height is distance; the sound is then all around, so panning does not enter), and 2 km.
        var measured = new Dictionary<int, double>();
        var expected = new Dictionary<int, double>();
        foreach (int x in new[] { 30, 100, 300, 2000 })
        {
            Place(0, x, Torrent);
            (double db, _) = await Measure(2.0, 2.0);
            measured[x] = db;
            _finder.Update(_inputs.Listener, 0);
            WaterSoundMix mix = WaterSoundField.Mix(_finder.Sources[0], 0.0);
            expected[x] = mix.GainDb + 20.0 * Math.Log10(Math.Max(mix.Audibility, 1e-9));
            Detail($"distance: torrent {x} m above: {F(db)} dBFS captured, W2 level {F(mix.Level)} dB, gain {F(mix.GainDb)} dB, audibility {F(mix.Audibility)}");
        }
        foreach (int x in new[] { 100, 300 })
        {
            double got = measured[30] - measured[x], want = expected[30] - expected[x];
            Check(Math.Abs(got - want) <= 2.0, $"distance: 30 m to {x} m differ by {F(got)} dB, W2 says {F(want)} dB");
        }
        Check(measured[2000] < -75, $"distance: 2 km above a torrent the output is {F(measured[2000])} dBFS, not under -75");
    }

    private async Task Walk()
    {
        // Walking away from a brook at 4 m/s. The brook is silent from about 65 m, so the walk runs from 10 m to 90 m.
        Place(10, 0, Brook);
        await Wait(1.5);
        _walkSpeed = 4.0; _walkStartMeters = 10.0;
        var meter = new Meter();
        _above = meter;
        var gains = new List<double>();
        _walkStartedAt = (Time.GetTicksMsec() - _startMs) / 1000.0;
        double end = 90.0, tEnd = (end - 10.0) / 4.0;
        for (double t = 0; t < tEnd; t += 0.05)
        {
            await Wait(0.05);
            var d = _node.GetWaterAmbienceDiagnostics();
            double g = 0;
            foreach (var v in (Godot.Collections.Array<Godot.Collections.Dictionary>)d["voices"]) foreach (double pg in (double[])v["player_gain"]) g = Math.Max(g, pg);
            gains.Add(g);
        }
        _walkStartedAt = -1;
        // The gain never rises, and its step per quarter second stays under 2 dB while the brook is clearly heard.
        int rises = 0; double worstStep = 0;
        for (int i = 1; i < gains.Count; i++) if (gains[i] > gains[i - 1] * (1.0 + 1e-6) + 1e-12) rises++;
        for (int i = 5; i < gains.Count; i++)
        {
            if (gains[i] < 0.5 * gains[0] || gains[i - 5] < 0.5 * gains[0]) continue;
            worstStep = Math.Max(worstStep, Math.Abs(20.0 * Math.Log10(gains[i] / gains[i - 5])));
        }
        // The captured level against the gain it should follow: windows of a quarter second, the level less the mean gain of the window (the loop's own fluctuation is what is left). A pop or a dropout
        // would stand out of it; its 5th to 95th percentile range stays under the 8 dB of the steadiness check, whatever the loop is.
        var residual = new List<double>();
        for (int i = 0; i < meter.Db.Count && (i + 1) * 5 <= gains.Count; i++)
        {
            double g = gains.Skip(i * 5).Take(5).Average();
            if (g < 0.5 * gains[0]) continue;
            residual.Add(meter.Db[i] - 20.0 * Math.Log10(g));
        }
        residual.Sort();
        double residualRange = residual.Count > 8 ? residual[(int)(residual.Count * 0.95)] - residual[(int)(residual.Count * 0.05)] : 0.0;
        Detail($"walk: brook 10 to 90 m at 4 m/s: gain rises {rises}, worst step {F(worstStep)} dB per 0.25 s; captured level less gain over {residual.Count} windows: 5th to 95th percentile range {F(residualRange)} dB");
        Check(rises == 0, $"walk: the gain rose {rises} times walking away");
        Check(worstStep <= 2.0, $"walk: the gain stepped {F(worstStep)} dB in a quarter second");
        Check(residual.Count >= 10 && residualRange < 8.0, $"walk: the captured level departs from the gain by a range of {F(residualRange)} dB over {residual.Count} windows (under 8 dB, at least 10 windows)");
    }

    private async Task Dullness()
    {
        // The share of energy above 4 kHz, from 10 m and from 300 m above the same mountain river.
        Place(0, 10, Mountain);
        (double near, double shareNear) = await Measure(2.0, 3.0);
        Place(0, 300, Mountain);
        (double far, double shareFar) = await Measure(2.5, 3.0);
        double drop = 10.0 * Math.Log10(Math.Max(shareNear, 1e-20) / Math.Max(shareFar, 1e-20));
        Detail($"dullness: mountain river from 10 m {F(near)} dBFS, share above 4 kHz {F(10 * Math.Log10(Math.Max(shareNear, 1e-20)))} dB; from 300 m {F(far)} dBFS, share {F(10 * Math.Log10(Math.Max(shareFar, 1e-20)))} dB; the far sound is {F(drop)} dB duller");
        Check(drop >= 10.0, $"dullness: the share above 4 kHz is only {F(drop)} dB under its share at 10 m (at least 10 dB)");
    }

    private async Task NotASplash()
    {
        // 20 s beside a fixed river: the quarter-second RMS has a 5th-to-95th percentile range under 8 dB and no window 15 dB under the mean.
        Place(8, 0, Mountain);
        await Wait(2.0);
        var meter = new Meter();
        _above = meter;
        await Wait(20.0);
        var db = meter.Db.ToList();
        var sorted = db.OrderBy(v => v).ToList();
        double p5 = sorted[(int)(sorted.Count * 0.05)], p95 = sorted[(int)(sorted.Count * 0.95)], mean = meter.MeanDb();
        double lowest = sorted[0];
        Detail($"splash: 20 s beside a mountain river: {db.Count} windows, mean {F(mean)} dBFS, 5th to 95th percentile range {F(p95 - p5)} dB, lowest window {F(lowest - mean)} dB from the mean");
        Check(db.Count >= 70, $"splash: only {db.Count} windows captured");
        Check(p95 - p5 < 8.0, $"splash: range {F(p95 - p5)} dB, not under 8");
        Check(lowest > mean - 15.0, $"splash: a window {F(mean - lowest)} dB under the mean (no more than 15)");
    }

    private async Task UnderSurfaceAndGround()
    {
        Place(0, 0, OpenSea(0.5), Torrent);
        _inputs = _inputs with { Underwater = true, DepthMeters = 3.0 };
        _under = new Meter(); _above = new Meter();
        await Wait(2.0);
        _under = new Meter(); _above = new Meter();
        await Wait(2.0);
        double under = _under.MeanDb(), above = _above.MeanDb();
        Detail($"under the surface: WaterUnderwater {F(under)} dBFS, WaterAbove {F(above)} dBFS");
        Check(under > -60, $"under the surface: WaterUnderwater carries {F(under)} dBFS, not above -60");
        Check(above < -90, $"under the surface: WaterAbove carries {F(above)} dBFS, not silence");
        _inputs = _inputs with { UndergroundBlend = 1.0 };
        await Wait(1.5);
        _under = new Meter(); _above = new Meter();
        await Wait(2.0);
        Detail($"underground, blend 1, under water: WaterUnderwater {F(_under.MeanDb())} dBFS, WaterAbove {F(_above.MeanDb())} dBFS");
        Check(_under.MeanDb() < -90 && _above.MeanDb() < -90, "underground: sound with the blend at 1");
        _inputs = Still(0, 0) with { UndergroundBlend = 1.0 };
        await Wait(1.5);
        _above = new Meter();
        await Wait(2.0);
        Check(_above.MeanDb() < -90, $"underground: open sea at the surface carries {F(_above.MeanDb())} dBFS with the blend at 1");
    }

    private async Task Meta()
    {
        Place(0, 200, OpenSea(0.5));
        await Wait(1.5);
        double high = (float)_metaHost.GetMeta("ring_water_ambience_proximity");
        Place(0, 0, SeaShore(0.5));
        await Wait(1.5);
        double shore = (float)_metaHost.GetMeta("ring_water_ambience_proximity");
        Detail($"meta: ring_water_ambience_proximity {F(high)} 200 m above open sea, {F(shore)} on the shore");
        Check(high == 0.0, $"meta: {F(high)} at 200 m above the sea, not 0");
        Check(shore > 0.2, $"meta: {F(shore)} on the shore, not above 0.2");
    }

    private async Task NegativeControl()
    {
        // With the height switched off (the fault of H40) the height check must fail: 200 m above open sea is as loud as at the water.
        _node.TestIgnoreHeight = true;
        Place(0, 200, OpenSea(0.5));
        (double up, _) = await Measure(2.0, 2.0);
        _node.TestIgnoreHeight = false;
        Detail($"negative control: height switched off, 200 m above open sea {F(up)} dBFS (the height check needs under -75)");
        Check(up > -60, $"negative control: with the height switched off the output at 200 m is {F(up)} dBFS; the height check would not fail");
    }
}
