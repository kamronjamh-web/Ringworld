using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO6c audio diagnostics (docs/megastorm-design.md 5.2 and WO6/WO9). Drives the real <see cref="RingMegastormAudio"/> node (voices, buses,
/// gates) through <see cref="RingMegastorm"/> queries at a fixed observer over a passage:
/// 350 m/s from T-4 min to T+3 min at 1x (airborne gain, roar and the distant-storm and far-thunder gate are 0 until the edge, the hum ramps without pops, the
/// boom fires exactly once, the roar takes over after it), the same at 300 m/s (the roar leads, no boom), then the trailing rain and clearing at x10;
/// shelter muffles the roar but not the hum; the ground tremor is exactly 0 untriggered; thunder delays are physical d/343 with no cap; the six assets exist
/// with the right lengths. The 350 m/s timeline is written to megastorm_wo6c_passage.csv for the audio render (tools/media/megastorm_audio_render.py).
/// Run: Godot --headless --path . --script res://tools/megastorm_audio_test.gd [-- --out &lt;dir&gt;]. Prints MEGASTORM_AUDIO=PASS.
/// </summary>
public partial class MegastormAudioTest : RefCounted
{
    private sealed record Row(double T, double Xi, float Hum, float Roar, float RoarEff, float Gate, float AirGain, float Swell,
        float Tremor, float Wildlife, int Booms, float BoomGain, int BoomVariant, float BoomEnv, float HumDb, float RoarDb);

    public int Run(string outDir)
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        RingMegastormSettings original = RingMegastormSettings.Current;
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = new Node { Name = "MegastormAudioTestRoot" };
        tree.Root.AddChild(root);
        try
        {
            Assets(Check);
            Directory.CreateDirectory(outDir);
            // 350 m/s: the default. 300 m/s: M 0.87, the roar leads.
            List<Row> fast = Passage(root, 350f, -240.0, 180.0, 0.5, 1.0, Check, "350");
            WriteCsv(Path.Combine(outDir, "megastorm_wo6c_passage.csv"), fast);
            Check350(fast, Check);
            List<Row> slow = Passage(root, 300f, -240.0, 180.0, 0.5, 1.0, Check, "300");
            Check300(slow, Check);
            Ramp(root, Check);
            Trailing(root, Check);
            Shelter(root, Check);
            Volume(root, Check);
            Tremor(root, Check);
            Thunder(Check);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_AUDIO=FAIL exception " + e.Message); return 1; }
        finally
        {
            RingMegastormSettings.Current = original;
            root.GetParent()?.RemoveChild(root);
            root.Free();
        }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_AUDIO=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_AUDIO=PASS");
        return 0;
    }

    // ---- Fixture ----------------------------------------------------------------------------------------------------

    private sealed class Rig
    {
        public RingMegastorm Storm = null!;
        public RingMegastormAudio Audio = null!;
        public RingWeatherAmbience Ambience = null!;
        public RingWeatherAudio Thunder = null!;
        public Node Holder = null!;
        public double Clock;
        public double Arrival;
        public RingCanonicalPosition Observer;
    }

    private static Rig Build(Node root, float speed, string name)
    {
        var settings = new RingMegastormSettings { SpeedMetersPerSecond = speed };
        RingMegastormSettings.Current = settings;
        var rig = new Rig { Observer = new RingCanonicalPosition(2_000_000.0, 0.0, 159_000.0) };
        rig.Holder = new Node { Name = "Rig" + name };
        root.AddChild(rig.Holder);
        rig.Ambience = new RingWeatherAmbience { Name = "WeatherAmbience" };
        rig.Thunder = new RingWeatherAudio { Name = "WeatherAudio" };
        rig.Holder.AddChild(rig.Ambience);
        rig.Holder.AddChild(rig.Thunder);
        rig.Audio = new RingMegastormAudio { Name = "MegastormAudio" };
        rig.Holder.AddChild(rig.Audio);
        rig.Clock = 1000.0;
        rig.Storm = new RingMegastorm { ClockProvider = () => rig.Clock };
        if (!rig.Storm.TriggerToReach(rig.Observer, 10800.0, 1, "audio_test_" + name, true)) throw new InvalidOperationException("trigger refused");
        MegastormSample first = rig.Storm.QueryNow(rig.Observer);
        rig.Arrival = rig.Clock + first.TimeToArrivalSeconds;
        return rig;
    }

    private static void Free(Rig rig)
    {
        rig.Storm.Free();
        rig.Holder.GetParent()?.RemoveChild(rig.Holder);
        rig.Holder.Free();
    }

    private static Row Step(Rig rig, double t, double dt, RainShelterClass shelter = RainShelterClass.Open)
    {
        rig.Clock = t;
        MegastormSample s = rig.Storm.QueryNow(rig.Observer);
        rig.Audio.Step(s, shelter, 0f, dt);
        var d = rig.Audio.GetMegastormAudioDiagnostics();
        return new Row(t - rig.Arrival, s.XiMeters, d["hum_gain"].AsSingle(), d["roar_gain"].AsSingle(), d["roar_effective"].AsSingle(),
            d["airborne_gate"].AsSingle(), s.AirGain, s.GustSwell, s.TremorLevel, s.WildlifeQuiet, d["boom_count"].AsInt32(), d["boom_gain"].AsSingle(), d["boom_variant"].AsInt32(), s.BoomEnvelope,
            d["hum_volume_db"].AsSingle(), d["roar_volume_db"].AsSingle());
    }

    private static List<Row> Passage(Node root, float speed, double fromT, double toT, double realStep, double weatherPerReal, Action<bool, string> check, string name)
    {
        Rig rig = Build(root, speed, name);
        try
        {
            var rows = new List<Row>();
            // Pre-roll so the voices have settled on the first recorded row (a world loaded into the approach would crossfade the same way).
            for (double t = fromT - 12.0; t < fromT; t += realStep) Step(rig, rig.Arrival + t, realStep);
            rig.Audio.ResetGainStepPeak();
            for (double t = fromT; t <= toT + 1e-9; t += realStep * weatherPerReal)
            {
                Row row = Step(rig, rig.Arrival + t, realStep);
                rows.Add(row);
                // The node pushes the gate to the other weather audio every step.
                if (Math.Abs(rig.Ambience.MegastormAirGate - row.Gate) > 1e-6f || Math.Abs(rig.Thunder.MegastormAirGate - row.Gate) > 1e-6f)
                    check(false, $"{name}: gate not pushed to the ambience and thunder nodes at t={t}");
            }
            var d = rig.Audio.GetMegastormAudioDiagnostics();
            // No pops: no voice ever changes faster than the roar's cap (1.5 per second); the hum uses the slower default cap.
            check(d["max_gain_step"].AsSingle() <= 1.5f + 1e-3f, $"{name}: voice gain step {d["max_gain_step"].AsSingle()} per second");
            if (name == "350")
            {
                check(d["hum_asset"].AsBool() && d["roar_asset"].AsBool() && d["boom_stream_count"].AsInt32() == 3, "voice assets not loaded");
                check(d["hum_playing"].AsBool() && d["roar_playing"].AsBool(), "hum and roar are not both playing at T+3 min");
                check(d["voice_starts"].AsInt32() == 2, $"voices (re)started {d["voice_starts"].AsInt32()} times, expected 2 (hum, roar)");
            }
            return rows;
        }
        finally { Free(rig); }
    }

    // ---- 350 m/s ----------------------------------------------------------------------------------------------------

    private static void Check350(List<Row> rows, Action<bool, string> check)
    {
        check(rows.Count > 500, "passage too short");
        float previousHum = -1f;
        foreach (Row r in rows)
        {
            if (r.T < 0.0)
            {
                check(r.AirGain == 0f && r.Roar == 0f && r.RoarEff == 0f, $"350: airborne gain or roar {r.AirGain}/{r.Roar} nonzero ahead of the edge at T{r.T}");
                check(r.Gate == 0f, $"350: distant-storm and far-thunder gate {r.Gate} nonzero ahead of the edge at T{r.T}");
                check(r.Booms == 0 && r.BoomEnv == 0f, $"350: boom before the edge at T{r.T}");
            }
            check(r.Hum >= previousHum - 1e-6f || r.T > 0.0, $"350: hum not ramping up at T{r.T}");
            previousHum = r.Hum;
            check(float.IsFinite(r.Hum) && r.Hum <= 1f, "350: non-finite or loud voice");
        }
        Row at(double t) => rows.OrderBy(r => Math.Abs(r.T - t)).First();
        check(at(-240).Hum > 0.9f, $"350: hum {at(-240).Hum} not near full 4 minutes ahead (84 km from the edge)");
        // The hum is a long build-up: far weaker 2,000 km out. (Not on this window: an 84 km approach is already at the top of its ramp.)
        check(MegastormSound.HumLevel(-2_000_000.0, 220000.0, 390000.0) < 0.01f && MegastormSound.HumLevel(-300_000.0, 220000.0, 390000.0) > 0.4f, "350: hum onset shape");
        check(at(0).Booms == 1 && rows.Last().Booms == 1, $"350: boom count {rows.Last().Booms}, expected exactly 1 over the passage");
        Row firstBoom = rows.First(r => r.Booms == 1);
        check(firstBoom.T >= 0.0 && firstBoom.T < 0.6, $"350: boom fired at T{firstBoom.T}, not at the edge");
        check(firstBoom.BoomGain > 0.99f && firstBoom.BoomVariant is >= 0 and <= 2, $"350: boom gain {firstBoom.BoomGain} / variant {firstBoom.BoomVariant}");
        check(at(0.5).AirGain < 0.01f && at(4.5).AirGain > 0.99f && at(1.0).Roar < 0.1f, "350: the roar does not take over after the pile-up");
        check(at(60).Roar > 0.95f && at(60).RoarEff > 0.95f, $"350: roar {at(60).Roar} not on 60 s behind the edge");
        check(rows.Max(r => r.Tremor) >= 0.99f && at(-240).Tremor > 0.55f, "350: tremor missing at full hum and at the boom");
        check(at(-1).Tremor <= 0.6001f && at(0.5).Tremor > 0.7f, "350: boom does not lift the tremor");
        // The hum drops to half over the 5 km behind the edge (14 s at 350 m/s) and keeps sounding inside.
        check(at(60).Hum is > 0.4f and < 0.6f, $"350: hum {at(60).Hum} not about half behind the edge");
    }

    // ---- 300 m/s ----------------------------------------------------------------------------------------------------

    private static void Check300(List<Row> rows, Action<bool, string> check)
    {
        Row at(double t) => rows.OrderBy(r => Math.Abs(r.T - t)).First();
        check(rows.All(r => r.Booms == 0 && r.BoomEnv == 0f), "300: a boom fired");
        check(rows.All(r => r.AirGain == 1f && r.Gate == 1f), "300: the airborne gain or gate is not open ahead of and at the edge");
        // 240 s ahead is 72 km at 300 m/s: the roar is already rising (it leads from about 80 km), and is up before the edge.
        check(at(-240).Roar > 0.0f && at(-100).Roar > at(-240).Roar && at(-1).Roar > 0.9f, $"300: roar does not lead the storm ({at(-240).Roar} -> {at(-100).Roar} -> {at(-1).Roar})");
        check(rows.Where(r => r.T < 0).Select(r => r.Roar).Zip(rows.Where(r => r.T < 0).Skip(1).Select(r => r.Roar), (a, b) => b - a).Min() >= -0.03f, "300: roar not rising monotonically");
    }

    // ---- The long hum build-up at x10 ---------------------------------------------------------------------------------

    private static void Ramp(Node root, Action<bool, string> check)
    {
        Rig rig = Build(root, 350f, "ramp");
        try
        {
            float previous = 0f, peak = 0f, wildlifePrevious = 0f, previousGate = 1f;
            double firstAudible = double.NaN;
            bool stageTwoQuiet = true;
            rig.Audio.ResetGainStepPeak();
            // From 6,400 s ahead (2,240 km: outside the hum's 10 R reach) to T-4 min at x10 (one real second per ten seconds of weather).
            for (double t = -6400.0; t <= -240.0; t += 10.0)
            {
                Row r = Step(rig, rig.Arrival + t, 1.0);
                check(r.Hum >= previous - 1e-6f, $"ramp: hum falls at T{t}");
                check(r.Wildlife >= wildlifePrevious - 1e-6f, $"ramp: wildlife quiet falls at T{t}");
                check((r.Xi < -200000.0 ? r.Gate == 1f : r.Gate <= previousGate + 1e-6f) && r.Roar == 0f, $"ramp: ordinary weather gated beyond 200 km or roar sounding {-t:F0} s ahead");
                previousGate = r.Gate;
                if (double.IsNaN(firstAudible) && r.Hum > 0.001f) firstAudible = t;
                if (r.Xi < -2.0 * 220000.0 && r.Wildlife > 0.0f) stageTwoQuiet = false;
                previous = r.Hum; wildlifePrevious = r.Wildlife; peak = Math.Max(peak, r.Hum);
            }
            var d = rig.Audio.GetMegastormAudioDiagnostics();
            check(d["max_gain_step"].AsSingle() <= RainSoundField.MaxGainRatePerSecond + 1e-3f, $"ramp: hum step {d["max_gain_step"].AsSingle()} per second at x10");
            check(!double.IsNaN(firstAudible) && firstAudible < -5000.0 && peak > 0.98f, $"ramp: hum from T{firstAudible}, peak {peak}");
            check(stageTwoQuiet, "ramp: wildlife quiet before 2 R");
            check(d["hum_playing"].AsBool() && d["voice_starts"].AsInt32() >= 1, "ramp: hum not playing");
        }
        finally { Free(rig); }
    }

    // ---- Trailing rain and clearing at x10 --------------------------------------------------------------------------

    private static void Trailing(Node root, Action<bool, string> check)
    {
        Rig rig = Build(root, 350f, "trail");
        try
        {
            double clear = rig.Storm.Model.AftermathEndXi / 350.0;
            double lastHum = 2, lastWild = 0;
            bool humPlaying = false;
            for (double t = 30.0; t < 60.0; t += 0.5) Step(rig, rig.Arrival + t, 0.5);
            rig.Audio.ResetGainStepPeak();
            // x10: one real second per ten seconds of weather.
            for (double t = 60.0; t <= clear + 600.0; t += 10.0)
            {
                Row r = Step(rig, rig.Arrival + t, 1.0);
                check(r.Hum <= lastHum + 1e-4f, $"trail: hum rises behind the edge at T+{t:F0}");
                lastHum = r.Hum;
                check(r.Booms == 0 || r.Booms == 1, "trail: boom count");
                if (t > 600.0) humPlaying |= rig.Audio.GetMegastormAudioDiagnostics()["hum_playing"].AsBool();
                lastWild = r.Wildlife;
            }
            var d = rig.Audio.GetMegastormAudioDiagnostics();
            check(d["max_gain_step"].AsSingle() <= 1.5f + 1e-3f, $"trail: gain step {d["max_gain_step"].AsSingle()} per second at x10");
            check(d["hum_gain"].AsSingle() == 0f && d["roar_gain"].AsSingle() == 0f, "trail: voices not silent after clearing");
            check(!d["hum_playing"].AsBool() && !d["roar_playing"].AsBool(), "trail: voices still playing after clearing");
            check(d["airborne_gate"].AsSingle() == 1f && lastWild < 0.01f, $"trail: gate {d["airborne_gate"].AsSingle()} / wildlife {lastWild} after clearing");
            check(humPlaying, "trail: the hum never sounded behind the storm");
        }
        finally { Free(rig); }
    }

    // ---- Shelter: muffles the roar, not the hum ---------------------------------------------------------------------

    private static void Shelter(Node root, Action<bool, string> check)
    {
        (float Hum, float Roar, float Cutoff) Settle(RainShelterClass shelter)
        {
            Rig rig = Build(root, 350f, "shelter" + shelter);
            try
            {
                Row last = default!;
                // Standing 60 s behind the edge for ten seconds: the roar is up and everything has settled.
                for (double t = 60.0; t < 80.0; t += 0.5) last = Step(rig, rig.Arrival + t, 0.5, shelter);
                var d = rig.Audio.GetMegastormAudioDiagnostics();
                return (d["hum_effective"].AsSingle(), d["roar_effective"].AsSingle(), d["air_cutoff_hz"].AsSingle());
            }
            finally { Free(rig); }
        }
        var open = Settle(RainShelterClass.Open);
        var roof = Settle(RainShelterClass.Roof);
        var indoors = Settle(RainShelterClass.Indoors);
        var cave = Settle(RainShelterClass.DeepCave);
        check(open.Roar > 0.95f, $"shelter: open roar {open.Roar}");
        check(roof.Roar < open.Roar * 0.7f && indoors.Roar < open.Roar * 0.3f && cave.Roar < 0.01f, $"shelter: roar not muffled ({roof.Roar} {indoors.Roar} {cave.Roar} against {open.Roar})");
        check(indoors.Cutoff < 2000f && open.Cutoff > 17000f, $"shelter: roar bus cutoff {indoors.Cutoff} / {open.Cutoff}");
        check(Math.Abs(roof.Hum - open.Hum) < 1e-6f && Math.Abs(indoors.Hum - open.Hum) < 1e-6f && Math.Abs(cave.Hum - open.Hum) < 1e-6f && open.Hum > 0.4f, $"shelter: hum muffled ({open.Hum} {roof.Hum} {indoors.Hum} {cave.Hum})");
    }

    // ---- The menu volume --------------------------------------------------------------------------------------------

    private static void Volume(Node root, Action<bool, string> check)
    {
        float Hum(float db)
        {
            Rig rig = Build(root, 350f, "volume");
            RingMegastormSettings.Current.SoundVolumeDb = db;
            try { for (double t = -30.0; t < 0.0; t += 0.5) Step(rig, rig.Arrival + t, 0.5); return rig.Audio.GetMegastormAudioDiagnostics()["hum_volume_db"].AsSingle(); }
            finally { Free(rig); }
        }
        float quiet = Hum(-12f), loud = Hum(0f);
        check(Math.Abs(loud - quiet - 12f) < 0.01f, $"volume: -12 dB menu setting moved the hum by {loud - quiet} dB");
    }

    // ---- Tremor -----------------------------------------------------------------------------------------------------

    private static void Tremor(Node root, Action<bool, string> check)
    {
        var untriggered = new RingMegastorm();
        MegastormSample none = untriggered.QueryNow(new RingCanonicalPosition(1000.0, 0.0, 1000.0));
        check(!none.Active && none.TremorLevel == 0f && none.HumLevel == 0f && none.WildlifeQuiet == 0f && none.BoomEnvelope == 0f && none.GustSwell == 0f, "tremor: untriggered sample not zero");
        check(none.TremorLevel == 0f && MegastormAudioMix.AirborneGate(none) == 1f && MegastormAudioMix.Targets(none).Equals(MegastormVoiceTargets.Idle), "tremor: untriggered gate or targets not identity");
        untriggered.Free();
        var node = new RingGroundTremor { Name = "GroundTremorTest" };
        root.AddChild(node);
        // (The headless renderer keeps no global values, so the node reports what it wrote; the windowed capture reads the real global.)
        for (int i = 0; i < 120; i++) node.Step(0f, 1.0 / 60.0);
        check(node.WriteCount == 0 && node.PublishedLevel == 0f && !node.Shaking && node.MaxShakeStepDegrees == 0f, $"tremor: the untriggered node wrote the global {node.WriteCount} times");
        for (int i = 0; i < 120; i++) node.Step(0.6f, 1.0 / 60.0);
        Vector4 on = node.LastWritten;
        check(Math.Abs(on.X - 0.6f) < 1e-6f && on.Y == MegastormAudioMix.ShiverHz && on.Z >= 0f && on.Z < Mathf.Tau, $"tremor: global {on} at full hum");
        node.Step(0f, 1.0 / 60.0);
        Vector4 off = node.LastWritten;
        check(off.X == 0f && off.Z == 0f && node.PublishedLevel == 0f && node.WriteCount == 121, $"tremor: global not back at 0 ({off}, {node.WriteCount} writes)");
        node.GetParent().RemoveChild(node); node.Free();
        // Camera shake mapping: 0 at 0, about 0.02 degrees at full hum (0.6), 0.1 at the boom peak, monotone and continuous.
        check(MegastormAudioMix.ShakeDegrees(0f) == 0f && Math.Abs(MegastormAudioMix.ShakeDegrees(0.6f) - 0.02f) < 1e-5f && Math.Abs(MegastormAudioMix.ShakeDegrees(1f) - 0.1f) < 1e-5f, "tremor: shake degrees");
        float prev = 0f, worst = 0f;
        for (float x = 0f; x <= 1f; x += 0.001f) { float d = MegastormAudioMix.ShakeDegrees(x); check(d >= prev - 1e-7f, "tremor: shake not monotone"); worst = Math.Max(worst, d - prev); prev = d; }
        check(worst < 0.001f, $"tremor: shake mapping steps {worst}");
        check(MegastormAudioMix.ShakeDegrees(float.NaN) == 0f, "tremor: NaN shake");
        // No spikes: through a full hum-to-boom-to-hum tremor curve at 30 and 60 fps the camera shake stays under 0.2 degrees and no frame-to-frame step exceeds twice its peak (the bound for any smooth curve: there are no outlier jumps), and it is 0 when untriggered.
        foreach (int fps in new[] { 30, 60 })
        {
            double phase = 0.0, biggest = 0.0, peakAngle = 0.0;
            (float, float, float) last = (0f, 0f, 0f);
            for (int frame = 0; frame < 4 * fps; frame++)
            {
                float t = frame < fps ? 0.6f : frame < 2 * fps ? 0.6f + 0.4f * MathF.Sin(MathF.PI * (frame - fps) / fps) : 0.6f;
                phase = (phase + Math.Tau * MegastormAudioMix.ShiverHz / fps) % Math.Tau;
                (float p, float y, float r) = MegastormAudioMix.ShakeOffsetRadians((float)phase, MegastormAudioMix.ShakeDegrees(t));
                double step = Math.Sqrt((p - last.Item1) * (p - last.Item1) + (y - last.Item2) * (y - last.Item2) + (r - last.Item3) * (r - last.Item3)) * 180.0 / Math.PI;
                if (frame > 0) biggest = Math.Max(biggest, step);
                peakAngle = Math.Max(peakAngle, Math.Sqrt(p * p + y * y + r * r) * 180.0 / Math.PI);
                last = (p, y, r);
            }
            check(biggest <= 2.0 * peakAngle + 1e-6 && peakAngle < 0.2, $"tremor: camera shake step {biggest:F4} / peak {peakAngle:F4} degrees at {fps} fps");
        }
        check(MegastormAudioMix.ShakeOffsetRadians(1.234f, 0f) == (0f, 0f, 0f), "tremor: shake offset not exactly 0 at 0");
    }

    // ---- Thunder delay (physical, no cap) ---------------------------------------------------------------------------

    private static void Thunder(Action<bool, string> check)
    {
        var propagation = new ThunderPropagation();
        var context = new ThunderAcousticContext(31_415_936.0, 318_000.0, 20_000.0, 60_000.0, MinimumAltitudeMeters: -2_000.0);
        var listener = new RingCanonicalPosition(5_000_000.0, 0.0, 100_000.0);
        var ground = new RingCanonicalPosition(5_000_000.0 + 30_000.0, 0.0, 100_000.0);
        var strike = new RingLightningEvent(1, 1, 0.0, ground, ground with { VerticalMeters = 8000.0 }, 7, 0f, 1f, 80f, false);
        bool observed = propagation.Observe(strike, 0.0, listener, context);
        var arrivals = new List<ThunderArrival>();
        double firstAt = double.NaN;
        for (double t = 0.0; t < 120.0 && double.IsNaN(firstAt); t += 0.05)
        {
            arrivals.Clear();
            propagation.Advance(t, listener, context, true, arrivals);
            if (arrivals.Count > 0) firstAt = t;
        }
        check(observed && Math.Abs(firstAt - 30_000.0 / 343.0) < 0.2 && firstAt > 80.0, $"thunder: 30 km strike arrived at {firstAt:F2} s, expected {30_002.0 / 343.0:F2} s with no cap");
    }

    // ---- Assets -----------------------------------------------------------------------------------------------------

    private static void Assets(Action<bool, string> check)
    {
        foreach ((string name, double min, double max) in new[] { ("megastorm_ground_hum_loop", 23.9, 24.1), ("megastorm_roar_loop", 29.9, 30.1),
            ("megastorm_boom_1", 3.0, 5.0), ("megastorm_boom_2", 3.0, 5.0), ("megastorm_boom_3", 3.0, 5.0) })
        {
            string path = $"res://resources/audio/weather/{name}.ogg";
            AudioStream? stream = ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
            double length = stream?.GetLength() ?? 0.0;
            check(stream != null && length >= min && length <= max, $"asset {name}: {(stream == null ? "missing" : length.ToString("F2", CultureInfo.InvariantCulture) + " s")}");
        }
        check(!ResourceLoader.Exists("res://resources/audio/weather/megastorm_wall_sing_loop.ogg"), "asset megastorm_wall_sing_loop still present (the rim-wall singing was removed)");
    }

    private static void WriteCsv(string path, List<Row> rows)
    {
        var sb = new StringBuilder("t,xi,hum,roar,roar_eff,gate,air_gain,swell,tremor,wildlife,booms,boom_gain,boom_variant,boom_env,hum_db,roar_db\n");
        foreach (Row r in rows)
            sb.Append(string.Create(CultureInfo.InvariantCulture,
                $"{r.T:F3},{r.Xi:F1},{r.Hum:F5},{r.Roar:F5},{r.RoarEff:F5},{r.Gate:F5},{r.AirGain:F5},{r.Swell:F5},{r.Tremor:F5},{r.Wildlife:F5},{r.Booms},{r.BoomGain:F4},{r.BoomVariant},{r.BoomEnv:F5},{r.HumDb:F2},{r.RoarDb:F2}\n"));
        File.WriteAllText(path, sb.ToString());
    }
}
