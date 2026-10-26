using System;
using Godot;

namespace RingworldTechDemo.Weather;

public partial class WeatherAmbienceDiagnosticRunner : RefCounted
{
    public string RunAll()
    {
        try
        {
            CheckTargets();
            CheckFiniteSafety();
            CheckSmoothing();
            CheckSectorFolding();
            CheckMaterialPower();
            CheckExposureField();
            CheckShelterClassifier();
            CheckSlewCaps();
            CheckSchedulers();
            CheckShotsAndWetness();
            return "Weather ambience diagnostics: PASS";
        }
        catch (Exception exception)
        {
            return "Weather ambience diagnostics: FAIL: " + exception.Message;
        }
    }

    /// <summary>Fixture helper: publishes the production local-effects state through its public override API.</summary>
    public void ConfigureLocalWeather(Node localEffects, float rain, Vector2 wind, bool snow)
    {
        if (localEffects is not global::RingworldTechDemo.RingLocalWeatherEffects local)
            throw new ArgumentException("Expected RingLocalWeatherEffects.");
        local.SnowGuaranteedAboveAltitudeMeters = snow ? -10_000 : 1_000_000;
        local.SnowTemperatureThreshold = snow ? 100 : -100;
        var state = new global::RingworldTechDemo.RingVolumetricCloudLayer.LocalWeatherState(
            1, default, 1, 1, Vector2.Zero, Vector2.One, 0, 1, rain, wind, default, 2000, 6000);
        local.SetExternalWeatherOverride(true, state);
        for (int index = 0; index < 16; index++) local._Process(.25);
    }

    private static void CheckTargets()
    {
        for (int index = 0; index <= 20; index++)
        {
            float rain = index / 20f;
            WeatherAmbienceMix.Targets(rain, false, 0, 0, out float light, out float heavy, out _, out _);
            Require(Near(light + heavy, rain), "rain split");
        }
        WeatherAmbienceMix.Targets(.2f, false, 0, 0, out _, out float lowHeavy, out _, out _);
        WeatherAmbienceMix.Targets(1, false, 0, 0, out _, out float fullHeavy, out _, out _);
        Require(lowHeavy == 0 && Near(fullHeavy, 1), "heavy bounds");
        WeatherAmbienceMix.Targets(1, true, 0, 0, out float snowLight, out float snowHeavy, out _, out _);
        Require(snowLight == 0 && snowHeavy == 0, "snow silence");
        WeatherAmbienceMix.Targets(0, false, 0, 1, out _, out _, out float wind0, out float distant0);
        WeatherAmbienceMix.Targets(0, false, 8, 1, out _, out _, out float wind8, out _);
        WeatherAmbienceMix.Targets(0, false, 16, 1, out _, out _, out float wind16, out _);
        WeatherAmbienceMix.Targets(1, false, 0, 1, out _, out _, out _, out float distantHeavy);
        Require(wind0 == 0 && Near(wind8, .5f) && wind16 == 1 && distant0 == 1 && distantHeavy == 0, "wind distant");
    }

    private static void CheckFiniteSafety()
    {
        WeatherAmbienceMix.Targets(float.NaN, false, float.PositiveInfinity, float.NegativeInfinity, out float a, out float b, out float c, out float d);
        Require(a == 0 && b == 0 && c == 0 && d == 0, "finite targets");
        Require(WeatherAmbienceMix.Smooth(float.NaN, 1, 1) == 0, "invalid current");
        Require(WeatherAmbienceMix.Smooth(0, float.PositiveInfinity, 1) == 0, "invalid target");
        Require(WeatherAmbienceMix.Smooth(0, 1, -1, -1) == 0, "invalid delta seconds");
        foreach (double delta in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0 })
            Require(Near(WeatherAmbienceMix.Smooth(.3f, 1, delta), .3f), "invalid delta preserves current");
        foreach (float seconds in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1.0f, 0.0f })
        {
            float value = WeatherAmbienceMix.Smooth(.3f, 1, .1, seconds);
            Require(float.IsFinite(value) && value >= .3f && value <= 1, "invalid smoothing duration stays bounded");
        }
        WeatherAmbienceMix.Targets(-1, false, -8, -1, out a, out b, out c, out d);
        Require(a == 0 && b == 0 && c == 0 && d == 0, "negative targets");
    }

    private static void CheckSmoothing()
    {
        float once = WeatherAmbienceMix.Smooth(0, 1, .5, 1);
        float split = WeatherAmbienceMix.Smooth(WeatherAmbienceMix.Smooth(0, 1, .25, 1), 1, .25, 1);
        Require(Near(once, split) && once <= 1, "partition no overshoot");
        Require(WeatherAmbienceMix.Smooth(0, .2f, 100) == .2f, "rising target no overshoot");
        Require(WeatherAmbienceMix.Smooth(1, .2f, 100) == .2f, "falling target no overshoot");
    }

    private static float Db(float ratio) => 20f * MathF.Log10(ratio);

    private static void CheckSectorFolding()
    {
        // Wind: louder toward the upwind sector, zero in calm air, never above 1.
        Span<float> wind = stackalloc float[RainSoundField.SectorCount];
        for (int upwind = 0; upwind < 4; upwind++)
        {
            RainSoundField.WindSectorGains(16, 1, RainSoundField.SectorAngle(upwind), wind);
            for (int s = 0; s < 4; s++) Require(wind[upwind] >= wind[s] - 1e-6f && wind[s] <= 1f + 1e-6f, "wind lobe peaks upwind");
            Require(Near(wind[upwind], 1f) && Near(wind[(upwind + 2) % 4], .5f), "wind lobe depth");
        }
        RainSoundField.WindSectorGains(0, 0, 0, wind);
        Require(wind[0] == 0 && wind[1] == 0 && wind[2] == 0 && wind[3] == 0, "calm wind silent");
        RainSoundField.WindSectorGains(float.NaN, float.PositiveInfinity, float.NaN, wind);
        for (int s = 0; s < 4; s++) Require(float.IsFinite(wind[s]), "finite wind gains");
        Require(Near(MathF.Abs(RainSoundField.UpwindAngle(5, 0)), MathF.PI), "upwind opposes the wind vector");
        // Gusts stay inside [1 - depth, 1] at 0.08..0.25 Hz.
        for (int seed = 0; seed < 8; seed++)
        for (int s = 0; s < 4; s++)
        {
            float lo = 2, hi = -1;
            for (double t = 0; t < 40; t += .05) { float g = RainSoundField.GustMultiplier(seed, s, t); lo = Math.Min(lo, g); hi = Math.Max(hi, g); }
            Require(lo >= .8f && hi <= 1f + 1e-6f && hi - lo > .05f, "gust range");
        }
        // Distant storm: a single probe on a sector axis feeds that sector; the loudest sector equals the old total.
        Span<float> distant = stackalloc float[RainSoundField.SectorCount];
        Span<float> angles = stackalloc float[16];
        Span<float> strengths = stackalloc float[16];
        for (int i = 0; i < 16; i++) angles[i] = (i % 8) * MathF.Tau / 8f;
        strengths.Clear();
        strengths[2] = .7f; // 90 degrees = the +across sector
        RainSoundField.DistantSectorGains(angles, strengths, .7f, distant);
        Require(Near(distant[1], .7f) && distant[0] < 1e-3f && distant[3] < 1e-3f && distant[2] < 1e-3f, "single distant probe stays directional");
        strengths.Fill(.4f);
        RainSoundField.DistantSectorGains(angles, strengths, .4f, distant);
        for (int s = 0; s < 4; s++) Require(Near(distant[s], .4f), "all-round storm is uniform");
        strengths.Clear(); strengths[1] = 1f; // 45 degrees, between the +around and +across sectors
        RainSoundField.DistantSectorGains(angles, strengths, .9f, distant);
        Require(Near(distant[0], distant[1]) && Near(distant[1], .9f) && distant[2] < 1e-3f, "diagonal storm shared by its neighbours");
        RainSoundField.DistantSectorGains(angles, strengths, 0f, distant);
        Require(distant[0] == 0 && distant[1] == 0, "no distant target no output");
    }

    private static void CheckMaterialPower()
    {
        Span<float> gains = stackalloc float[RainSoundField.MaterialCount];
        float reference = -1;
        var mixes = new[] { new[] { 1f, 0f, 0f, 0f, 0f }, new[] { .5f, 0f, .5f, 0f, 0f }, new[] { .25f, .25f, .25f, .25f, 0f }, new[] { .1f, .3f, .2f, .4f, 0f }, new[] { 0f, 0f, 0f, 1f, 0f }, new[] { .995f, 0f, 0f, .005f, 0f }, new[] { .2f, .2f, .2f, .2f, .2f }, new[] { 0f, 0f, .3f, 0f, .7f }, new[] { 0f, 0f, 0f, 0f, 1f } };
        foreach (float[] mix in mixes)
        {
            RainSoundField.SurfaceGains(.8f, .7f, mix, gains);
            float power = 0;
            for (int m = 0; m < RainSoundField.MaterialCount; m++) power += gains[m] * gains[m];
            if (reference < 0) reference = power;
            Require(MathF.Abs(Db(MathF.Sqrt(power / reference))) < .5f, "equal power across material mixes");
        }
        RainSoundField.SurfaceGains(1, 1, new[] { .995f, 0f, 0f, .005f, 0f }, gains);
        Require(gains[3] == 0 && gains[0] > .99f, "materials under 1% stay silent");
        RainSoundField.SurfaceGains(1, 1, new[] { 0f, 0f, 0f, 0f, 0f }, gains);
        Require(gains[0] == 0 && gains[3] == 0, "empty mix silent");
        RainSoundField.SurfaceGains(float.NaN, 1, new[] { 1f, 0f, 0f, 0f, 0f }, gains);
        Require(gains[0] == 0, "invalid intensity silent");
    }

    private static void CheckExposureField()
    {
        var field = new RainExposureField();
        Require(!field.FullySeeded && field.MeanExposure() == 0, "unseeded field is silent");
        for (int i = 0; i < RainSoundField.ColumnCount; i++) field.ApplyColumn(i, 1, RainMaterial.Soil);
        Require(field.FullySeeded && Near(field.MeanExposure(), 1), "seeding takes first readings directly");
        // One column update never moves a sector by more than 1/6, whichever column flips.
        for (int column = 0; column < RainSoundField.ColumnCount; column++)
        {
            var f = new RainExposureField();
            for (int i = 0; i < RainSoundField.ColumnCount; i++) f.ApplyColumn(i, 1, RainMaterial.Soil);
            int sector = column / RainSoundField.ColumnsPerSector;
            float before = f.SectorExposure(sector);
            f.ApplyColumn(column, 0, RainMaterial.Soil);
            Require(before - f.SectorExposure(sector) <= RainExposureField.MaxSectorStep + 1e-5f, "sector step cap");
            for (int other = 0; other < 4; other++) if (other != sector) Require(Near(f.SectorExposure(other), 1), "update leaks into other sectors");
        }
        // Material fractions follow the landing columns.
        var mixField = new RainExposureField();
        for (int i = 0; i < RainSoundField.ColumnCount; i++) mixField.ApplyColumn(i, i / RainSoundField.ColumnsPerSector == 0 ? 1 : 0, i % 2 == 0 ? RainMaterial.Soil : RainMaterial.Water);
        Span<float> fractions = stackalloc float[RainSoundField.MaterialCount];
        mixField.SectorMaterials(0, fractions);
        Require(Near(fractions[0] + fractions[3], 1) && fractions[0] > 0 && fractions[3] > 0 && fractions[1] == 0, "sector materials");
        mixField.SectorMaterials(2, fractions);
        Require(fractions[0] + fractions[1] + fractions[2] + fractions[3] + fractions[4] == 0, "dry sector has no materials");
        mixField.Invalidate();
        Require(!mixField.FullySeeded && mixField.MeanExposure() == 0, "invalidate");
        // Geometry: columns of a sector straddle its axis.
        for (int i = 0; i < RainSoundField.ColumnCount; i++)
        {
            RainSoundField.ColumnGeometry(i, out int sector, out float around, out float across);
            float angle = MathF.Atan2(across, around);
            float diff = MathF.Abs(MathF.Atan2(MathF.Sin(angle - RainSoundField.SectorAngle(sector)), MathF.Cos(angle - RainSoundField.SectorAngle(sector))));
            Require(sector == i / 6 && MathF.Abs(diff - 22f * MathF.PI / 180f) < 1e-3f, "column geometry");
        }
        Require(RainSoundField.LandedNearListener(0) == 1 && RainSoundField.LandedNearListener(8) == 0 && RainSoundField.LandedNearListener(-30) == 0
            && RainSoundField.ColumnExposure(true, 0, true) < .2f && RainSoundField.ColumnExposure(false, 0, false) == 0, "column exposure");
    }

    private static void CheckShelterClassifier()
    {
        var classifier = new RainShelterClassifier();
        var open = new RainShelterInput(false, false, false, 0, 1);
        var porch = new RainShelterInput(false, true, false, 0, .8f);
        var room = new RainShelterInput(false, true, false, 0, .1f);
        var cave = new RainShelterInput(false, true, true, 6, .1f);
        var deep = new RainShelterInput(false, true, true, 30, 0);
        Require(RainShelterClassifier.Raw(open, true) == RainShelterClass.Open && RainShelterClassifier.Raw(porch, true) == RainShelterClass.Roof
            && RainShelterClassifier.Raw(room, false) == RainShelterClass.Indoors && RainShelterClassifier.Raw(cave, false) == RainShelterClass.Cave
            && RainShelterClassifier.Raw(deep, false) == RainShelterClass.DeepCave && RainShelterClassifier.Raw(new RainShelterInput(true, false, false, 0, 1), true) == RainShelterClass.DeepCave, "raw classes");
        void Settle(RainShelterInput input) { for (int i = 0; i < 20; i++) classifier.Update(.1, input); }
        Settle(porch); Require(classifier.Current == RainShelterClass.Roof, "porch");
        Settle(room); Require(classifier.Current == RainShelterClass.Indoors, "room");
        Settle(cave); Require(classifier.Current == RainShelterClass.Cave, "cave");
        Settle(deep); Require(classifier.Current == RainShelterClass.DeepCave, "deep cave");
        Settle(open); Require(classifier.Current == RainShelterClass.Open, "back outside");
        // A side exposure dithering inside the hysteresis band never changes class.
        classifier.Reset();
        Settle(new RainShelterInput(false, true, false, 0, .6f));
        Require(classifier.Current == RainShelterClass.Roof, "start in a porch");
        int changes = 0; RainShelterClass last = classifier.Current;
        for (int i = 0; i < 400; i++)
        {
            float side = .35f + (i % 2 == 0 ? .08f : -.08f);
            RainShelterClass now = classifier.Update(.05, new RainShelterInput(false, true, false, 0, side));
            if (now != last) { changes++; last = now; }
        }
        Require(changes == 0, "no flapping at the side-open threshold");
        // A cover reading that flips faster than the dwell never changes class.
        classifier.Reset(); changes = 0; last = classifier.Current;
        for (int i = 0; i < 400; i++)
        {
            RainShelterClass now = classifier.Update(.1, i % 2 == 0 ? porch : open);
            if (now != last) { changes++; last = now; }
        }
        Require(changes == 0, "dwell holds against fast cover flips");
        // A real change takes the dwell time but not much longer.
        classifier.Reset();
        double elapsed = 0;
        while (classifier.Current == RainShelterClass.Open && elapsed < 2) { classifier.Update(.05, porch); elapsed += .05; }
        Require(elapsed >= .4 - 1e-6 && elapsed <= .5, "dwell duration");
    }

    private static void CheckSlewCaps()
    {
        var random = new Random(7);
        float gain = 0, cutoff = 18000;
        for (int i = 0; i < 20000; i++)
        {
            double dt = random.NextDouble() * .2;
            float target = random.Next(0, 5) == 0 ? (float)random.NextDouble() : (i / 300 % 2);
            float next = RainSoundField.SlewGain(gain, target, dt);
            Require(MathF.Abs(next - gain) <= RainSoundField.MaxGainRatePerSecond * dt + 1e-6f && next >= 0 && next <= 1, "gain slew cap");
            gain = next;
            float hz = random.Next(0, 4) == 0 ? 600 + (float)random.NextDouble() * 17400 : (i / 200 % 2 == 0 ? 18000 : 700);
            float nextCutoff = RainSoundField.SlewCutoffHz(cutoff, hz, dt);
            Require(MathF.Abs(MathF.Log2(nextCutoff / cutoff)) <= RainSoundField.MaxCutoffOctavesPerSecond * dt + 1e-4f, "cutoff slew cap");
            cutoff = nextCutoff;
        }
        Require(Near(RainSoundField.SlewGain(0, 1, 100), 1) && Near(RainSoundField.SlewGain(.3f, .3f, .1), .3f) && RainSoundField.SlewGain(.4f, 1, 0) == .4f && RainSoundField.SlewGain(float.NaN, 1, .1) >= 0, "gain slew endpoints");
        Require(RainSoundField.SlewCutoffHz(18000, 18000, .1) == 18000 && RainSoundField.SlewCutoffHz(float.NaN, 900, .1) == 900, "cutoff endpoints");
        Require(RainSoundField.GainToDb(0) == -80 && MathF.Abs(RainSoundField.GainToDb(1)) < 1e-6f && MathF.Abs(RainSoundField.GainToDb(.5f) + 6.0206f) < 1e-3f, "gain to dB");
        RainShelterParams indoors = RainSoundField.Params(RainShelterClass.Indoors);
        Require(indoors.FieldCutoffHz > indoors.BedCutoffHz && indoors.FieldCutoffHz < RainSoundField.OpenCutoffHz && RainSoundField.Params(RainShelterClass.Open).FieldCutoffHz == RainSoundField.OpenCutoffHz, "field filter is milder than the bed");
    }

    private static void CheckSchedulers()
    {
        var a = new RainShotScheduler(11); var b = new RainShotScheduler(11);
        int total = 0;
        double clock = 0; double last = -1; double minimumGap = 1e9; int repeats = 0; int previous = -1;
        for (int i = 0; i < 60 * 600; i++)
        {
            clock += 1.0 / 60.0;
            int ea = a.Advance(1.0 / 60.0, RainShotScheduler.RateFor(1f), 0, 8);
            int eb = b.Advance(1.0 / 60.0, RainShotScheduler.RateFor(1f), 0, 8);
            Require(ea == eb, "scheduler determinism");
            if (ea == 0) continue;
            total++;
            if (last >= 0) minimumGap = Math.Min(minimumGap, clock - last);
            last = clock;
            int pick = a.PickIndex(8); b.PickIndex(8);
            if (pick == previous) repeats++;
            previous = pick;
            float pitch = a.NextPitchScale(); float gainDb = a.NextGainDb(); b.NextPitchScale(); b.NextGainDb();
            Require(pitch >= .92f && pitch <= 1.08f && gainDb >= -3f && gainDb <= 3f, "shot pitch and gain jitter");
        }
        double rate = total / 600.0;
        Require(rate > 3.0 && rate < 4.8, "shot rate tracks 4/s at full intensity (got " + rate + ")");
        Require(minimumGap >= RainShotScheduler.MinimumSpacingSeconds - 1e-9, "shot spacing");
        Require(repeats == 0, "no immediate repeat");
        var half = new RainShotScheduler(3);
        int halfCount = 0;
        for (int i = 0; i < 60 * 600; i++) halfCount += half.Advance(1.0 / 60.0, RainShotScheduler.RateFor(.5f), 0, 8);
        Require(halfCount > 0 && halfCount < total * .7, "shot rate tracks intensity");
        // Rain ramping up from almost nothing: an interval drawn at the tiny starting rate must not delay the full-rate shots.
        var ramp = new RainShotScheduler(9);
        for (int i = 0; i < 60; i++) ramp.Advance(1.0 / 60.0, 1e-4f, 0, 8);
        int rampCount = 0;
        for (int i = 0; i < 60 * 10; i++) rampCount += ramp.Advance(1.0 / 60.0, RainShotScheduler.RateFor(1f), 0, 8);
        Require(rampCount >= 25 && rampCount <= 60, $"shot rate follows a rising intensity at once ({rampCount} in 10 s at 4/s)");
        var capped = new RainShotScheduler(5);
        var empty = new RainShotScheduler(5);
        int cappedCount = 0, emptyCount = 0;
        for (int i = 0; i < 600; i++) { cappedCount += capped.Advance(1.0 / 60.0, 4f, RainShotScheduler.MaxVoices, 8); emptyCount += empty.Advance(1.0 / 60.0, 4f, 0, 0); }
        Require(cappedCount == 0 && emptyCount == 0, "voice cap and empty library are silent");
        Require(new RainShotScheduler(1).PickIndex(0) == -1 && RainShotScheduler.RateFor(float.NaN) == 0, "scheduler guards");
        var picker = new RainShotScheduler(21);
        foreach (int count in new[] { 2, 3, 8, 16 })
        {
            int lastIndex = -1;
            for (int i = 0; i < 1000; i++)
            {
                int before = lastIndex;
                int pick = picker.PickIndex(count, ref lastIndex);
                Require(pick >= 0 && pick < count && pick != before && lastIndex == pick, "PickIndex(ref) never repeats");
            }
        }
        int single = -1;
        Require(picker.PickIndex(1, ref single) == 0 && picker.PickIndex(0, ref single) == -1, "PickIndex(ref) guards");
        var weights = new float[] { 0f, 2f, 0f, 1f, 0f, 0.5f };
        var counts = new int[6];
        for (int i = 0; i < 5000; i++) { int kind = picker.PickKind(weights); Require(kind >= 0 && weights[kind] > 0f, "PickKind never returns a zero-rate kind"); counts[kind]++; }
        Require(counts[1] > counts[3] && counts[3] > counts[5], "PickKind follows the weights");
        Require(picker.PickKind(new float[6]) == -1, "PickKind with no rate is -1");
    }

    private static void CheckShotsAndWetness()
    {
        Require(RainSoundField.MaterialCount == 5 && (int)RainMaterial.Metal == 4 && RainSoundField.ShotKindCount == 8, "material and shot kind counts");
        float wet = 1f;
        for (int i = 0; i < 90 * 60; i++) wet = RainSoundField.UpdateWetness(wet, 0f, 1.0 / 60.0);
        Require(MathF.Abs(wet - MathF.Exp(-1f)) < .02f * MathF.Exp(-1f), "wetness falls to 1/e after 90 s");
        wet = 0f;
        for (int i = 0; i < 60 * 60; i++) wet = RainSoundField.UpdateWetness(wet, 1f, 1.0 / 60.0);
        Require(wet > .99f && wet <= 1f, "wetness rises toward 1");
        wet = 0f;
        for (int i = 0; i < 10 * 60; i++) wet = RainSoundField.UpdateWetness(wet, 1f, 1.0 / 60.0);
        Require(MathF.Abs(wet - (1f - MathF.Exp(-1f))) < .02f, "wetness rises with tau 10 s");
        Require(RainSoundField.UpdateWetness(float.NaN, 1f, .1) >= 0f && RainSoundField.UpdateWetness(.5f, float.NaN, .1) <= .5f
            && RainSoundField.UpdateWetness(2f, 5f, 1) <= 1f && RainSoundField.UpdateWetness(-1f, -1f, 1) == 0f, "wetness is NaN and range safe");

        var rates = new float[RainSoundField.ShotKindCount];
        var mixed = new float[] { .15f, .15f, .2f, .2f, .15f };
        foreach (RainShelterClass shelter in Enum.GetValues<RainShelterClass>())
        {
            RainSoundField.ShotRates(1f, 1f, shelter, mixed, rates);
            for (int k = 0; k < rates.Length; k++) Require(float.IsFinite(rates[k]) && rates[k] >= 0f, "shot rates finite and non-negative");
            if (shelter == RainShelterClass.DeepCave) foreach (float r in rates) Require(r == 0f, "deep cave shot rates are zero");
            Require((rates[(int)RainShotKind.RoofPlink] > 0f) == (shelter is RainShelterClass.Roof or RainShelterClass.Indoors), "roof plinks only under a roof or indoors");
            Require((rates[(int)RainShotKind.CaveDrip] > 0f) == (shelter == RainShelterClass.Cave), "cave drips only in caves");
        }
        RainSoundField.ShotRates(0f, 0f, RainShelterClass.Open, mixed, rates);
        foreach (float r in rates) Require(r == 0f, "dry rates are zero");
        RainSoundField.ShotRates(0f, 0f, RainShelterClass.Cave, mixed, rates);
        foreach (float r in rates) Require(r == 0f, "dry cave rates are zero");
        var half = new float[RainSoundField.ShotKindCount];
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.Open, mixed, rates);
        RainSoundField.ShotRates(.5f, .5f, RainShelterClass.Open, mixed, half);
        foreach (int k in new[] { (int)RainShotKind.Plip, (int)RainShotKind.Tick })
            Require(rates[k] > 0f && MathF.Abs(half[k] - .5f * rates[k]) < 1e-4f, "surface shot rates scale linearly with intensity");
        // Aftermath: a dry, wet canopy still drips, a dry bare surface does not.
        RainSoundField.ShotRates(0f, .8f, RainShelterClass.Open, new float[] { 0f, 1f, 0f, 0f, 0f }, rates);
        Require(rates[(int)RainShotKind.LeafDrip] > 0f && rates[(int)RainShotKind.Plip] == 0f && rates[(int)RainShotKind.Tick] == 0f, "aftermath drips from foliage only");
        RainSoundField.ShotRates(float.NaN, float.NaN, RainShelterClass.Roof, new float[] { float.NaN, 1f, -1f, 0f, 0f }, rates);
        foreach (float r in rates) Require(float.IsFinite(r) && r >= 0f, "shot rates survive NaN input");

        // Storm arrival pools (S3): fat plips fire in the open before the rain tier covers them, hail hits scale with hail (louder rate under a roof), neither in a deep cave.
        RainSoundField.ShotRates(0f, 0f, RainShelterClass.Open, mixed, rates, 1f, 0f);
        Require(rates[(int)RainShotKind.FatPlip] > 2f && rates[(int)RainShotKind.HailHit] == 0f && rates[(int)RainShotKind.Plip] == 0f, "fat drops alone give fat plips only (dry weather)");
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.Open, mixed, rates, 1f, 0f);
        Require(rates[(int)RainShotKind.FatPlip] == 0f, "heavy rain ducks the fat plips");
        RainSoundField.ShotRates(.2f, .2f, RainShelterClass.Open, mixed, rates, 1f, 0f);
        Require(rates[(int)RainShotKind.FatPlip] > 1.5f, "light rain keeps the fat plips");
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.Open, mixed, rates, 0f, 1f);
        Require(rates[(int)RainShotKind.HailHit] > 5f, "hail hits in the open");
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.Open, mixed, half, 0f, .5f);
        Require(MathF.Abs(half[(int)RainShotKind.HailHit] - .5f * rates[(int)RainShotKind.HailHit]) < 1e-4f, "hail hit rate scales linearly with hail");
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.Roof, mixed, rates, 0f, 1f);
        Require(rates[(int)RainShotKind.HailHit] > 5f && rates[(int)RainShotKind.FatPlip] == 0f, "hail hits on a roof, no fat plips under cover");
        RainSoundField.ShotRates(1f, 1f, RainShelterClass.DeepCave, mixed, rates, 1f, 1f);
        foreach (float r in rates) Require(r == 0f, "deep cave stays silent under hail");
        RainSoundField.ShotRates(float.NaN, float.NaN, RainShelterClass.Open, mixed, rates, float.NaN, float.PositiveInfinity);
        foreach (float r in rates) Require(float.IsFinite(r) && r >= 0f, "arrival shot rates survive NaN input");
        // Layer gains: roar swell with a lobe toward the storm, hail by exposure (roof stands in under cover), skitter by biome, tier ducking monotone.
        var gains = new float[RainSoundField.SectorCount];
        RainSoundField.GustRoarSectorGains(0f, 0f, gains);
        foreach (float g in gains) Require(g == 0f, "no gust front, no roar");
        RainSoundField.GustRoarSectorGains(1f, 0f, gains);
        Require(Near(gains[0], 1f) && Near(gains[1], .3f) && Near(gains[2], .3f), "roar loudest toward the storm, 30 percent elsewhere");
        RainSoundField.GustRoarSectorGains(.5f, MathF.PI * .5f, gains);
        Require(gains[1] > gains[0] && gains[1] > gains[2] && gains[1] < 1f, "roar swell follows the weight and the bearing");
        RainSoundField.HailSectorGains(1f, new float[] { 1f, .5f, 0f, 0f }, .55f, false, gains);
        Require(Near(gains[0], 1f) && Near(gains[1], .5f) && gains[2] == 0f, "hail bed follows sector exposure");
        RainSoundField.HailSectorGains(1f, new float[] { 0f, 0f, 0f, 0f }, .55f, true, gains);
        Require(Near(gains[0], .55f), "roof gain carries the hail bed under cover");
        Require(RainSoundField.LeafSkitterGain(1f, 1f) == 1f && RainSoundField.LeafSkitterGain(1f, 0f) == 0f && RainSoundField.LeafSkitterGain(0f, 1f) == 0f, "leaf skitter needs a gust and leaves");
        Require(RainSoundField.WindTierDuck(0f) == 1f && Near(RainSoundField.WindTierDuck(1f), .75f) && RainSoundField.WindTierDuck(.6f) < RainSoundField.WindTierDuck(.4f), "heavy tier ducks the wind");
        Require(RainSoundField.HailRainDuck(0f) == 1f && Near(RainSoundField.HailRainDuck(1f), .6f) && Near(RainSoundField.GustLightBedDuck(1f), .7f), "hail and gust ducks");
    }

    private static bool Near(float first, float second) => Math.Abs(first - second) < .001f;
    private static void Require(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); }
}
