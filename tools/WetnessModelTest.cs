using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Closed-form wetness (docs/storm-revamp-s2-wet-world-design.md 6): film dries before soak, standing water needs sustained rain and lingers, sun and wind speed drying,
/// drying-speed scaling, snow counts as dry, output continuous in TimeSinceRain when fed S3's interpolated history.
/// Run: Godot --headless --path . --script res://tools/wetness_model_test.gd. Prints WETNESS_MODEL=PASS.
/// </summary>
public partial class WetnessModelTest : RefCounted
{
    public int Run()
    {
        var failures = new List<string>();
        void Check(bool ok, string message) { if (!ok) failures.Add(message); }
        try
        {
            WetnessInputs Base(float tsr, float recent = 0.9f, float rain = 0.0f, float sun = 0.0f, float wind = 0.0f, float speed = 1.0f, bool snow = false)
                => new(recent, tsr, rain, sun, wind, speed, snow);
            WetnessState At(float tsr, float recent = 0.9f, float rain = 0.0f, float sun = 0.0f, float wind = 0.0f, float speed = 1.0f, bool snow = false)
                => WetnessModel.Evaluate(Base(tsr, recent, rain, sun, wind, speed, snow));

            // Raining now: everything is up (rain term needs recent + rain).
            WetnessState raining = At(0.0f, 0.9f, 0.8f);
            Check(raining.Film > 0.99f && raining.Soak > 0.79f && raining.Standing > 0.99f, $"raining state {raining}");
            // First seconds of rain with no history: the term is gated by recent + rain.
            Check(At(float.PositiveInfinity, 0.0f, 0.0f).Film == 0.0f, "dry no rain");
            Check(WetnessModel.Evaluate(new WetnessInputs(0.0f, float.PositiveInfinity, 0.6f, 0.5f, 0, 1, false)).Film > 0.5f, "rain now with no history wets the film");

            // Film dries before soak, soak before standing (overcast calm).
            float TimeBelow(Func<WetnessState, float> channel, float level)
            {
                for (float t = 0; t < 1500; t += 1.0f) if (channel(At(t)) < level) return t;
                return 1500;
            }
            float tFilm = TimeBelow(s => s.Film, 0.05f), tSoak = TimeBelow(s => s.Soak, 0.05f), tStanding = TimeBelow(s => s.Standing, 0.05f);
            Check(tFilm < tSoak && tSoak < tStanding, $"drying order film {tFilm} soak {tSoak} standing {tStanding}");
            // In sun, gloss is gone in 2-3 minutes; overcast calm about 8 minutes.
            float tFilmSun = 0; for (float t = 0; t < 1500; t++) if (At(t, 0.9f, 0, 1.0f).Film < 0.25f) { tFilmSun = t; break; }
            float tFilmDull = 0; for (float t = 0; t < 1500; t++) if (At(t).Film < 0.25f) { tFilmDull = t; break; }
            Check(tFilmSun > 40 && tFilmSun < 200, $"gloss threshold in sun at {tFilmSun}s");
            Check(tFilmDull > 200 && tFilmDull < 700, $"gloss threshold overcast at {tFilmDull}s");

            // Standing water needs sustained heavy rain and lingers.
            Check(At(30.0f, 0.2f).Standing == 0.0f, "light recent rain makes no puddle");
            Check(At(30.0f, 0.9f).Standing > 0.6f, "sustained rain makes a puddle");
            Check(At(300.0f, 0.9f).Standing > At(300.0f, 0.9f).Soak, "puddles outlast the soak");
            Check(At(150.0f, 0.9f, 0, 1.0f).Film < At(150.0f).Film, "sun speeds drying");
            Check(At(150.0f, 0.9f, 0, 0, 10.0f).Film < At(150.0f).Film, "wind speed speeds drying");
            // Drying speed scaling: speed 2 at t equals speed 1 at 2t (well inside the horizon fade).
            for (float t = 20; t <= 400; t += 40)
            {
                WetnessState fast = At(t, 0.9f, 0, 0.3f, 2.0f, 2.0f), slow = At(2 * t, 0.9f, 0, 0.3f, 2.0f, 1.0f);
                Check(Math.Abs(fast.Film - slow.Film) < 1e-4f && Math.Abs(fast.Soak - slow.Soak) < 1e-4f, $"drying speed scaling at {t}");
            }
            // Prior rain (how heavy the rain was before it stopped) keeps the soak and the puddles after RecentRainAmount has collapsed.
            WetnessState lowRecent = At(150.0f, 0.2f), withPrior = WetnessModel.Evaluate(new WetnessInputs(0.2f, 150.0f, 0.0f, 0.0f, 0.0f, 1.0f, false, 0.95f));
            Check(withPrior.Standing > lowRecent.Standing + 0.2f && withPrior.Soak > lowRecent.Soak, $"prior rain keeps puddles and soak {withPrior} vs {lowRecent}");
            var tuning0 = StormArrivalTuning.Default;
            var wetLags = new float[9]; for (int i = 0; i < 9; i++) wetLags[i] = 0.8f;
            Check(Math.Abs(WetnessModel.PriorRain(wetLags, 0.0f, tuning0) - StormArrivalMath.Wetness(0.8f, tuning0)) < 0.02f, "prior = wetness during sustained rain");
            Check(WetnessModel.PriorRain(wetLags, 1400.0f, tuning0) < 0.5f && WetnessModel.PriorRain(wetLags, float.PositiveInfinity, tuning0) == 0.0f, "prior fades toward the horizon and is zero with no rain");
            float lastPrior = WetnessModel.PriorRain(wetLags, 0.0f, tuning0); float worstPriorStep = 0.0f;
            for (float t = 1; t <= 1600; t += 1) { float p = WetnessModel.PriorRain(wetLags, t, tuning0); worstPriorStep = Math.Max(worstPriorStep, Math.Abs(p - lastPrior)); lastPrior = p; }
            Check(worstPriorStep < 0.03f, $"prior is continuous in the time since rain (largest step {worstPriorStep:0.0000})");
            // Snow counts as dry.
            Check(At(0.0f, 0.9f, 0.9f, 0, 0, 1, true) == WetnessState.Dry, "snow is dry");
            // Beyond the history horizon everything ends (no visible pop at overcast calm).
            WetnessState edge = At(1499.0f), beyond = At(float.PositiveInfinity);
            Check(edge.Standing < 0.02f && beyond.Standing == 0.0f, $"horizon fade: standing at 1499 s {edge.Standing}");

            // Continuity: feed S3's interpolated history while rain stopped T seconds ago (slow physical fade, as StormArrivalMathTest).
            var tuning = StormArrivalTuning.Default;
            float start = tuning.PrecipitationStart;
            WetnessState last = default; bool haveLast = false; float worst = 0.0f; float worstAt = 0.0f;
            for (float T = 0; T <= 1600; T += 1.0f)
            {
                Span<float> proxies = stackalloc float[9];
                for (int l = 0; l < 9; l++)
                {
                    float lag = l == 0 ? 0 : StormArrivalMath.HistoryLagsSeconds[l - 1];
                    proxies[l] = Math.Clamp(start + 0.45f * (lag - T) / 1000f, 0f, 0.7f);
                }
                RainHistorySample h = StormArrivalMath.ReduceHistory(proxies, tuning);
                WetnessState s = WetnessModel.Evaluate(new WetnessInputs(h.RecentRainAmount, h.TimeSinceRainSeconds, 0.0f, 0.2f, 1.0f, 1.0f, false));
                if (haveLast)
                {
                    float step = Math.Max(Math.Abs(s.Film - last.Film), Math.Max(Math.Abs(s.Soak - last.Soak), Math.Abs(s.Standing - last.Standing)));
                    if (step > worst) { worst = step; worstAt = T; }
                }
                last = s; haveLast = true;
            }
            Check(worst < 0.03f, $"continuity: largest 1 s step {worst:0.0000} at T={worstAt}");
        }
        catch (Exception e) { Console.Error.WriteLine(e.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("WETNESS_MODEL=FAIL " + string.Join(" | ", failures.Take(20))); return 1; }
        Console.WriteLine("WETNESS_MODEL=PASS");
        return 0;
    }
}
