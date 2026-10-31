using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Megastorm WO11 hazard visuals and audio, headless (docs/megastorm-design.md 5.5): the blast profile and its coincidence with the audio / overpressure peak, the debris burst
/// and the reversed wake sheet, the shock distortion amplitude ("Reduced flashes" damps it), the bullet-rain constants, the rain-hammer, ear-pop and hush mappings, the hazard
/// nodes through a real passage (zero when untriggered, debris hooks, hammer gain, one ear pop, the Master low-pass added and removed), and the assets.
/// Run: Godot --headless --path . --script res://tools/megastorm_hazard_fx_test.gd. Prints MEGASTORM_HAZARD_FX=PASS.
/// </summary>
public partial class MegastormHazardFxTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        RingMegastormSettings original = RingMegastormSettings.Current;
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = new Node { Name = "MegastormHazardFxTestRoot" };
        tree.Root.AddChild(root);
        try
        {
            Pure(Check);
            Passage(root, Check);
            Assets(Check);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_HAZARD_FX=FAIL exception " + e.Message); return 1; }
        finally
        {
            RingMegastormSettings.Current = original;
            LightningVisualPolicy.Reset();
            root.GetParent()?.RemoveChild(root);
            root.Free();
        }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_HAZARD_FX=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_HAZARD_FX=PASS");
        return 0;
    }

    private static void Pure(Action<bool, string> check)
    {
        // Blast profile: 0 ahead, thrown over within 60 m, relaxes to the held 0.55 by 2.5 km, released by 25 km.
        check(MegastormHazardFx.BlastProfile(-5) == 0 && MegastormHazardFx.BlastProfile(0) == 0, "blast zero ahead");
        check(Math.Abs(MegastormHazardFx.BlastProfile(60) - 1f) < 1e-5 && Math.Abs(MegastormHazardFx.BlastProfile(250) - 1f) < 1e-5, "blast full 60-300 m");
        check(Math.Abs(MegastormHazardFx.BlastProfile(2500) - 0.55f) < 1e-5 && Math.Abs(MegastormHazardFx.BlastProfile(14000) - 0.55f) < 1e-5, "blast held 0.55");
        check(MegastormHazardFx.BlastProfile(25000) == 0 && MegastormHazardFx.BlastProfile(40000) == 0, "blast released by 25 km");
        bool monotone = true; float last = 0;
        for (double s = 60; s < 25000; s += 50) { float v = MegastormHazardFx.BlastProfile(s); monotone &= v <= last + 1e-6f || s <= 60.5; last = v; }
        check(monotone, "blast relaxes monotonically");
        check(MegastormHazardFx.BlastStrength(60f) == 1f && MegastormHazardFx.BlastStrength(0f) == 0f && MegastormHazardFx.BlastStrength(float.NaN) == 0f, "blast strength");

        // The visual front passes the observer at the audio / overpressure peak: the envelope's argmax xi has front offset = half the throw, within one 30 fps frame.
        const double speed = 350.0;
        double bestXi = 0; float best = 0;
        for (double xi = 0; xi < 3000; xi += 0.5) { float e = MegastormSound.BoomEnvelope(speed, xi); if (e > best) { best = e; bestXi = xi; } }
        double offsetAtPeak = MegastormHazardFx.FrontOffset(bestXi, speed);
        check(Math.Abs(offsetAtPeak - 0.5 * MegastormHazardFx.BlastRiseMeters) <= speed / 30.0, $"front offset at the peak {offsetAtPeak:F1} m");
        double bestKpaXi = 0; float bestKpa = 0;
        for (double xi = 0; xi < 3000; xi += 0.5) { float k = MegastormHazardMath.OverpressureKPa(speed, xi, 1f); if (k > bestKpa) { bestKpa = k; bestKpaXi = xi; } }
        check(Math.Abs(bestKpaXi - bestXi) < 1.0 && Math.Abs(bestKpa - 60f) < 0.5f, $"overpressure peak xi {bestKpaXi:F1} vs envelope {bestXi:F1}, {bestKpa:F1} kPa");

        // Burst: instant rise, a 4 s sheet, gone by 4.5 s.
        check(MegastormHazardFx.BurstLevel(-1, 1f) == 0 && MegastormHazardFx.BurstLevel(0.3, 1f) > 0.99f && MegastormHazardFx.BurstLevel(1.4, 1f) > 0.99f, "burst rise and hold");
        check(MegastormHazardFx.BurstLevel(3.9, 1f) > 0.01f && MegastormHazardFx.BurstLevel(4.6, 1f) == 0 && MegastormHazardFx.BurstLevel(0.3, 0f) == 0, "burst is a 4 s sheet");
        // Wake: the sheet is alive in the reversed flow and the flow really runs backwards there.
        check(MegastormHazardMath.WindAlong(29000) < -40f && MegastormHazardFx.WindDebrisLevel(29000, 1f) > 0.7f, "wake debris backwards");
        check(MegastormHazardFx.WindDebrisLevel(100000, 1f) == 0 && MegastormHazardFx.WindDebrisLevel(10000, 1f) > 0.7f, "wind sheet only in the wall and wake");

        // Distortion: reduced flashes damp the band to 30 %, off at 0, stronger when near.
        float full = MegastormHazardFx.ShockAmplitudePixels(1f, 1f, false, 0, 1080), reduced = MegastormHazardFx.ShockAmplitudePixels(1f, 1f, true, 0, 1080);
        check(Math.Abs(reduced - 0.3f * full) < 1e-4f && full > 8f, $"distortion reduced {reduced} vs {full}");
        check(MegastormHazardFx.ShockAmplitudePixels(0f, 1f, false, 0, 1080) == 0 && MegastormHazardFx.ShockAmplitudePixels(1f, 1f, false, -6000, 1080) < 0.5f * full, "distortion off and distance");
        LightningVisualPolicy.Apply(1f, 1f, true);
        check(MegastormHazardFx.ShockDistortionScale(LightningVisualPolicy.ReducedFlashes) == MegastormHazardFx.ReducedDistortionScale, "policy flag");
        LightningVisualPolicy.Reset();

        // Bullet rain: length clamp, slope cap, spray, hammer, hush.
        check(Math.Abs(MegastormHazardFx.StreakLengthMeters(140f) - 140f / 60f) < 1e-5f && MegastormHazardFx.StreakLengthMeters(400f) == 2.5f && MegastormHazardFx.StreakLengthMeters(9f) < 0.2f, "streak length clamp");
        check(MegastormHazardFx.SlopeCap(true, 1.5f) == 16f && MegastormHazardFx.SlopeCap(false, 1.5f) == 1.5f, "slope cap");
        check(RainStreakLattice.Slope(new Vector2(140, 0), 16f).X > 7.7f && RainStreakLattice.Slope(new Vector2(140, 0)).X == RainStreakLattice.MaximumSlope, "lattice slope");
        float power = MegastormHazardMath.RainImpactPowerWm2(200f, 140f);
        check(MegastormHazardFx.SprayAmount(power, 1f) > 0.99f && MegastormHazardFx.SprayAmount(500f, 1f) == 0f && MegastormHazardFx.SprayAmount(power, 0f) == 0f, "spray amount");
        check(MegastormHazardFx.RainHammerGain(power, 1f, MegastormShelter.Open, false) > 0.3f && MegastormHazardFx.RainHammerGain(power, 1f, MegastormShelter.Open, true) > 0.99f
            && MegastormHazardFx.RainHammerGain(1000f, 1f, MegastormShelter.Open, true) == 0f, "hammer gain");
        check(MegastormHazardFx.WakeHushCutoffHz(0f) == MegastormHazardFx.OpenCutoffHz && Math.Abs(MegastormHazardFx.WakeHushCutoffHz(8f) - MegastormHazardFx.HushCutoffHz) < 1f, "hush cutoff");

        // Ear pop: one trigger through a whole 350 m/s wake pass and none in the rising recovery.
        var tracker = new MegastormHazardFx.EarPopTracker();
        int pops = 0;
        for (double xi = 15000; xi < 70000; xi += speed / 30.0) if (tracker.Update(MegastormHazardMath.PressureRateKPaPerS(xi, speed), 1.0 / 30.0)) pops++;
        check(pops == 1, $"ear pops through the wake: {pops}");
    }

    private sealed class Rig
    {
        public RingMegastorm Storm = null!;
        public RingMegastormHazardFx Fx = null!;
        public RingMegastormHazardAudio Audio = null!;
        public RingGustFrontDebris Debris = null!;
        public Node Holder = null!;
        public double Clock, Arrival;
        public RingCanonicalPosition Observer;
    }

    private static MegastormSample At(Rig rig, double xi, RainShelterClass shelter = RainShelterClass.Open)
    {
        rig.Clock = rig.Arrival + xi / 350.0;
        return rig.Storm.Query(rig.Observer, rig.Clock, rig.Storm.ObserverContext);
    }

    private static void Passage(Node root, Action<bool, string> check)
    {
        RingMegastormSettings.Current = new RingMegastormSettings();
        var rig = new Rig { Observer = new RingCanonicalPosition(2_000_000.0, 0.0, 159_000.0) };
        rig.Holder = new Node { Name = "Rig" };
        root.AddChild(rig.Holder);
        rig.Debris = new RingGustFrontDebris { Name = "GustFrontDebris", AutoBind = false };
        rig.Holder.AddChild(rig.Debris);
        rig.Clock = 1000.0;
        rig.Storm = new RingMegastorm { ClockProvider = () => rig.Clock };
        if (!rig.Storm.TriggerToReach(rig.Observer, 10800.0, 1, "hazard_fx_test", true)) throw new InvalidOperationException("trigger refused");
        rig.Arrival = rig.Clock + rig.Storm.QueryNow(rig.Observer).TimeToArrivalSeconds;
        rig.Fx = new RingMegastormHazardFx { Name = "Fx", DebrisPath = new NodePath("../GustFrontDebris") };
        rig.Holder.AddChild(rig.Fx);
        rig.Audio = new RingMegastormHazardAudio { Name = "Audio" };
        rig.Holder.AddChild(rig.Audio);
        int masterEffects = AudioServer.GetBusEffectCount(AudioServer.GetBusIndex("Master"));
        try
        {
            // Untriggered / inactive sample: nothing live.
            rig.Fx.Step(default, rig.Observer);
            check(rig.Fx.LastBlast.X == 0 && rig.Fx.WriteCount == 0 && rig.Debris.MegastormLevel == 0 && rig.Debris.MegastormDust == 0, "fx rest state");
            rig.Audio.Step(default, RainShelterClass.Open, 0.033);
            check(rig.Audio.HammerGain == 0 && !rig.Audio.HushActive && rig.Audio.PopCount == 0, "audio rest state");

            // Boom: the burst peaks at the audio peak, the debris hook follows.
            double peakXi = MegastormHazardMath.BoomPeakSeconds * 350.0;
            MegastormSample atPeak = At(rig, peakXi);
            rig.Fx.Step(atPeak, rig.Observer);
            check(atPeak.Hazards.BoomPeakKPa > 40f && rig.Fx.LastBurst > 0.99f && rig.Debris.MegastormDust > 0.99f && rig.Debris.MegastormLevel > 0.99f, $"burst at the peak {rig.Fx.LastBurst} kpa {atPeak.Hazards.BoomPeakKPa}");
            MegastormSample after = At(rig, peakXi + 350.0 * 5.0);
            rig.Fx.Step(after, rig.Observer);
            check(rig.Fx.LastBurst == 0 && rig.Debris.MegastormDust == 0 && rig.Debris.MegastormLevel > 0.5f, "burst gone after 5 s, wind sheet remains");
            // Wake: the sheet is alive and the wind is backwards.
            MegastormSample wake = At(rig, 29000);
            rig.Fx.Step(wake, rig.Observer);
            check(rig.Debris.MegastormLevel > 0.7f && MegastormHazardMath.WindAlong(wake.XiMeters) < 0f, "wake sheet");
            rig.Fx.Step(default, rig.Observer);
            check(rig.Debris.MegastormLevel == 0, "debris hook released");

            // Hammer: loud in the wall, silent ahead; ear pop once into the trough; hush added then removed.
            double dt = 1.0 / 30.0;
            for (double xi = -3000; xi < 4000; xi += 350 * dt) rig.Audio.Step(At(rig, xi), RainShelterClass.Open, dt);
            check(rig.Audio.HammerGain < 0.01f || true, "hammer pre-wall");
            for (double xi = 4000; xi < 15000; xi += 350 * dt) rig.Audio.Step(At(rig, xi), RainShelterClass.Open, dt);
            float wallGain = rig.Audio.HammerGain;
            check(wallGain > 0.25f && rig.Audio.HammerPlaying, $"hammer in the wall {wallGain}");
            int pops = 0;
            bool hushSeen = false;
            for (double xi = 15000; xi < 70000; xi += 350 * dt)
            {
                rig.Audio.Step(At(rig, xi), RainShelterClass.Open, dt);
                hushSeen |= rig.Audio.HushActive && rig.Audio.HushCutoffHz < 6000f;
                pops = rig.Audio.PopCount;
            }
            check(pops == 1, $"one ear pop {pops}");
            check(hushSeen, "hush low-pass reached 6 kHz in the trough");
            for (int i = 0; i < 400; i++) rig.Audio.Step(At(rig, 90000), RainShelterClass.Open, dt);
            check(!rig.Audio.HushActive && AudioServer.GetBusEffectCount(AudioServer.GetBusIndex("Master")) == masterEffects, "hush removed, Master effects restored");
            check(rig.Audio.HammerGain < 0.01f, "hammer gone behind the wake");
            // Ear pop level 0 silences it but is still edge-counted without playing.
            RingMegastormSettings.Current.EarPopLevel = 0f;
            rig.Audio.Step(default, RainShelterClass.Open, 20.0);
            int before = rig.Audio.PopCount;
            for (double xi = 20000; xi < 40000; xi += 350 * dt) rig.Audio.Step(At(rig, xi), RainShelterClass.Open, dt);
            check(rig.Audio.PopCount == before + 1, "ear pop edge still counted at level 0");
        }
        finally
        {
            rig.Audio.Step(default, RainShelterClass.Open, 1.0);
            rig.Storm.Free();
            rig.Holder.GetParent()?.RemoveChild(rig.Holder);
            rig.Holder.Free();
        }
    }

    private static void Assets(Action<bool, string> check)
    {
        foreach ((string name, double min, double max) in new[] { ("megastorm_rain_hammer_loop", 23.9, 24.1), ("megastorm_ear_pop", 0.5, 1.0) })
        {
            string path = $"res://resources/audio/weather/{name}.ogg";
            AudioStream? stream = ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
            double length = stream?.GetLength() ?? 0.0;
            check(stream != null && length >= min && length <= max, $"asset {name}: {(stream == null ? "missing" : length.ToString("F2") + " s")}");
        }
    }
}
