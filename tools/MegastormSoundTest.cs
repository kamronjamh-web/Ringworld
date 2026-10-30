using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO6b (docs/megastorm-design.md 5.2, section 8): the pure MegastormSound maths. Hum onset at 10 R and stage 2 at 2 R within 0.5%, AirSound Silent
/// ahead at 350/500 m/s and Leading at 25/300 m/s and continuous across 322-343 m/s, Boom only from M 0.94 for BoomSeconds starting exactly at xi = 0,
/// the gust swell allowed at 350 and suppressed at 500, a reload behind the edge gives no second boom unless still inside the window.
/// Run: Godot --headless --path . --script res://tools/megastorm_sound_test.gd. Prints MEGASTORM_SOUND=PASS.
/// </summary>
public partial class MegastormSoundTest : RefCounted
{
    private const double R = 220000.0, TrailEnd = 390000.0;

    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            Hum(Check);
            Air(Check);
            Boom(Check);
            Swell(Check);
            Reload(Check);
            Tremor(Check);
            Combined(Check);
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); Console.Error.WriteLine("MEGASTORM_SOUND=FAIL exception " + e.Message); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("MEGASTORM_SOUND=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("MEGASTORM_SOUND=PASS");
        return 0;
    }

    private static void Hum(Action<bool, string> check)
    {
        double onset = 10.0 * R, stage2 = 2.0 * R;
        check(MegastormSound.HumLevel(-onset * 1.005, R, TrailEnd) == 0.0f && MegastormSound.HumStage(-onset * 1.005, R) == 0, "hum present beyond 10 R + 0.5%");
        check(MegastormSound.HumLevel(-onset * 0.995, R, TrailEnd) > 0.0f && MegastormSound.HumStage(-onset * 0.995, R) == 1, "hum absent inside 10 R - 0.5%");
        check(MegastormSound.HumStage(-stage2 * 1.005, R) == 1 && MegastormSound.HumStage(-stage2 * 0.995, R) == 2, "stage 2 not at 2 R +- 0.5%");
        check(Math.Abs(MegastormSound.HumLevel(-1100000.0, R, TrailEnd) - 0.25f) < 0.001f, "hum at 1,100 km is not 0.25");
        float detected = MegastormSound.HumLevel(-1530000.0, R, TrailEnd);
        check(detected > 0.045f && detected < 0.055f, $"hum at 1,530 km {detected} is not about 0.05");
        check(MegastormSound.HumLevel(0.0, R, TrailEnd) > 0.999f, "hum not full at the edge");
        check(Math.Abs(MegastormSound.HumLevel(5000.0, R, TrailEnd) - 0.5f) < 0.01f, "hum not half just behind the edge");
        check(MegastormSound.HumLevel(TrailEnd, R, TrailEnd) == 0.0f && MegastormSound.HumLevel(TrailEnd + 1.0e6, R, TrailEnd) == 0.0f, "hum not gone by the end of the trailing rain");
        // No pops: the level moves smoothly everywhere, including the 5 km edge blend.
        float worst = 0.0f, previousLevel = MegastormSound.HumLevel(-2.5e6, R, TrailEnd);
        for (double xi = -2.5e6; xi <= 5.0e5; xi += 25.0)
        {
            float h = MegastormSound.HumLevel(xi, R, TrailEnd);
            worst = Math.Max(worst, Math.Abs(h - previousLevel)); previousLevel = h;
        }
        check(worst < 0.005f, $"hum step {worst} per 25 m");
        // Wall singing: the nearer wall is louder and the level scales with the hum.
        WallSingLevels near = MegastormSound.WallSing(-500000.0, R, TrailEnd, 5000.0, 318000.0);
        check(near.Low > near.High && near.Low <= MegastormSound.HumLevel(-500000.0, R, TrailEnd) + 1e-6f, "nearer wall not loudest");
        WallSingLevels far = MegastormSound.WallSing(-3.0e6, R, TrailEnd, 5000.0, 318000.0);
        check(far.Max == 0.0f, "wall singing audible beyond the hum reach");
        WallSingLevels mid = MegastormSound.WallSing(-500000.0, R, TrailEnd, 159000.0, 318000.0);
        check(Math.Abs(mid.Low - mid.High) < 1e-6f, "wall singing not symmetric mid-width");
        // Wildlife.
        check(MegastormSound.WildlifeQuiet(-2.0 * R * 1.001, R, TrailEnd, 600000.0) == 0.0f && MegastormSound.WildlifeQuiet(-R, R, TrailEnd, 600000.0) > 0.999f
            && Math.Abs(MegastormSound.WildlifeQuiet(-1.5 * R, R, TrailEnd, 600000.0) - 0.5f) < 0.001f, "wildlife ramp 2 R -> R");
        check(MegastormSound.WildlifeQuiet(100000.0, R, TrailEnd, 600000.0) == 1.0f && MegastormSound.WildlifeQuiet(TrailEnd + 600000.0, R, TrailEnd, 600000.0) == 0.0f, "wildlife recovery behind the storm");
    }

    private static void Air(Action<bool, string> check)
    {
        foreach (double xi in new[] { -1.0, -2000.0, -60000.0, -400000.0 })
        {
            foreach (float v in new[] { 350f, 500f }) check(MegastormSound.AirSound(v, xi).State == AirSoundState.Silent, $"AirSound not Silent ahead at {v} m/s xi {xi}");
            foreach (float v in new[] { 25f, 300f }) check(MegastormSound.AirSound(v, xi).State == AirSoundState.Leading, $"AirSound not Leading at {v} m/s xi {xi}");
        }
        float prevWeight = MegastormSound.LeadWeight(322.0), prevGain = MegastormSound.AirSound(322.0, -5000.0).AirGain;
        for (double v = 323.0; v <= 343.0; v += 1.0)
        {
            float w = MegastormSound.LeadWeight(v), g = MegastormSound.AirSound(v, -5000.0).AirGain;
            check(Math.Abs(w - prevWeight) <= 0.05f && Math.Abs(g - prevGain) <= 0.05f, $"AirSound lead steps at {v} m/s: {Math.Abs(w - prevWeight)}");
            prevWeight = w; prevGain = g;
        }
        check(MegastormSound.LeadWeight(322.0) > 0.99f && MegastormSound.LeadWeight(343.0) < 0.01f, "lead weight endpoints");
        // Well behind the edge the airborne beds are back to full, at any speed.
        foreach (float v in new[] { 25f, 300f, 350f, 500f }) check(MegastormSound.AirSound(v, v * 20.0).AirGain == 1.0f, $"airborne gain not 1 well inside at {v} m/s");
    }

    private static void Boom(Action<bool, string> check)
    {
        const double B = MegastormSound.BoomSeconds;
        foreach (float v in new[] { 25f, 300f, 320f, 322f })
        {
            bool any = false;
            for (double xi = 0.0; xi < 3000.0; xi += 5.0) any |= MegastormSound.AirSound(v, xi).State == AirSoundState.Boom || MegastormSound.BoomEnvelope(v, xi) != 0.0f;
            check(!any, $"boom below M 0.94 at {v} m/s");
        }
        foreach (float v in new[] { 323f, 350f, 500f })
        {
            double length = v * B;
            check(MegastormSound.AirSound(v, -1.0e-6).State != AirSoundState.Boom && MegastormSound.BoomEnvelope(v, -1.0e-6) == 0.0f, $"boom before xi = 0 at {v}");
            check(MegastormSound.AirSound(v, 0.0).State == AirSoundState.Boom, $"boom does not start exactly at xi = 0 at {v}");
            check(MegastormSound.AirSound(v, length - 1.0e-6).State == AirSoundState.Boom, $"boom ends early at {v}");
            check(MegastormSound.AirSound(v, length).State == AirSoundState.Inside && MegastormSound.BoomEnvelope(v, length) == 0.0f, $"boom lasts past BoomSeconds at {v}");
            float peak = 0.0f;
            for (double xi = 0.0; xi < length; xi += length / 400.0) peak = Math.Max(peak, MegastormSound.BoomEnvelope(v, xi));
            check(peak > 0.6f * MegastormSound.BoomGain(v) && peak <= 1.0f, $"boom envelope peak {peak} at {v}");
            check(MegastormSound.BoomEnvelope(v, length * 0.999) < 0.02f, $"boom envelope does not taper out at {v}");
            check(Math.Abs(MegastormSound.AirSound(v, length * 0.5).AirGain - (1.0f - MegastormSound.BoomGain(v))) < 1e-5f, $"roar not replaced by the pile-up mid-boom at {v}");
        }
        // A custom window length is honoured.
        check(MegastormSound.AirSound(350.0, 5.0 * 350.0 - 1.0, boomSeconds: 5.0).State == AirSoundState.Boom
            && MegastormSound.AirSound(350.0, 5.0 * 350.0 + 1.0, boomSeconds: 5.0).State == AirSoundState.Inside, "BoomSeconds parameter");
    }

    private static void Swell(Action<bool, string> check)
    {
        double band = MegastormBands.BandWidthMeters(350.0, 60.0);
        check(MegastormSound.AirSound(350.0, -1000.0).SwellAllowed && MegastormSound.GustSwell(350.0, -0.4 * band, band) > 0.5f, "gust swell not allowed at 350 m/s");
        check(!MegastormSound.AirSound(500.0, -1000.0).SwellAllowed && MegastormSound.GustSwell(500.0, -0.4 * band, MegastormBands.BandWidthMeters(500.0, 60.0)) == 0.0f, "gust swell not suppressed at 500 m/s");
        check(MegastormSound.AirSound(500.0, -1000.0).BoomIncludesSwell && !MegastormSound.AirSound(350.0, -1000.0).BoomIncludesSwell, "swell not folded into the boom above 384 m/s");
        check(MegastormSound.AirSound(383.9, -1.0).SwellAllowed && !MegastormSound.AirSound(384.1, -1.0).SwellAllowed, "swell cut-off is not 343 + 41 m/s");
        check(MegastormSound.GustSwell(350.0, 1.0, band) == 0.0f && MegastormSound.GustSwell(350.0, -band - 1.0, band) == 0.0f, "swell outside the gust band");
    }

    private static int Fires(float speed, double fromXi, double toXi)
    {
        var tracker = new MegastormBoomTracker();
        int fires = 0;
        for (double xi = fromXi; xi <= toXi; xi += 10.0) if (tracker.Update(MegastormSound.AirSound(speed, xi).State)) fires++;
        return fires;
    }

    private static void Reload(Action<bool, string> check)
    {
        check(Fires(350f, -3000.0, 8000.0) == 1, "boom did not fire exactly once in one passage");
        check(Fires(350f, 5000.0, 9000.0) == 0, "reload behind the window fired a boom");
        check(Fires(350f, -9000.0, 9000.0) == 1, "reload ahead of the edge did not fire exactly one boom");
        check(Fires(350f, 500.0, 9000.0) == 1, "reload inside the boom window did not fire once");
        check(Fires(300f, -3000.0, 8000.0) == 0, "boom fired at 300 m/s");
    }

    private static void Tremor(Action<bool, string> check)
    {
        check(MegastormSound.TremorLevel(0.0f, 0.0f) == 0.0f, "tremor not 0 with no hum or boom");
        check(Math.Abs(MegastormSound.TremorLevel(1.0f, 0.0f) - 0.6f) < 1e-6f && MegastormSound.TremorLevel(1.0f, 0.9f) == 1.0f, "tremor = 0.6 hum + boom, clamped");
    }

    private static MegastormModel Make(float speed, int seed = 5)
    {
        var m = new MegastormModel();
        m.Trigger(new MegastormTrigger("sound", 2.0e6, 1, 0.0, speed, 1800f, seed));
        return m;
    }

    private static void Combined(Action<bool, string> check)
    {
        MegastormModel m = Make(350f);
        MegastormSoundSample far = MegastormSound.Evaluate(m, -3.0e6, 159000.0, 350.0);
        check(far.HumLevel == 0.0f && far.WildlifeQuiet == 0.0f && far.TremorLevel == 0.0f && far.AirSound == AirSoundState.Silent, "sample not quiet beyond the hum reach");
        MegastormSoundSample edge = MegastormSound.Evaluate(m, 100.0, 159000.0, 350.0);
        check(edge.AirSound == AirSoundState.Boom && edge.BoomEnvelope > 0.5f && edge.TremorLevel > edge.HumLevel * 0.6f && edge.HumLevel > 0.9f, "sample at the edge");
        check(edge == MegastormSound.Evaluate(m, 100.0, 159000.0, 350.0), "Evaluate is not deterministic");
        MegastormSoundSample ahead = MegastormSound.Evaluate(m, -6000.0, 159000.0, 350.0);
        check(ahead.GustSwell > 0.0f && ahead.AirSound == AirSoundState.Silent, "gust swell missing 6 km ahead at 350 m/s");
        MegastormSoundSample slow = MegastormSound.Evaluate(Make(25f), -30000.0, 159000.0, 25.0);
        check(slow.AirSound == AirSoundState.Leading && slow.AirGain == 1.0f, "sample not Leading at 25 m/s");
    }
}
