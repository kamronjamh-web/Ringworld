using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Phase E2 of docs/eclipse-false-sun-spec.md (D): the pure darkness model. Neutral (bit exact) at weight 0 and when disabled; the spec
/// totality values (ambient 0.30 -> 0.045, atmosphere night x0.35, sky x0.5, clouds x0.4, stars 1.0); the cloud floor of 0.3 (D.2); monotone
/// partial phases; hub relief only when enabled; 0.5 s smoothing with no fade-in on the first sample; the weight comes from raw coverage so the
/// 0.95 GameplayEclipseDarknessCap (D.3) does not change it; the director node publishes the same multipliers.
/// </summary>
public partial class EclipseDarknessTest : RefCounted
{
    const double SmoothWindow = EclipseDarknessModel.SmoothSeconds;

    static void Expect(List<string> failures, bool ok, string message) { if (!ok) failures.Add(message); }
    static bool Near(double a, double b, double eps = 1e-9) => Math.Abs(a - b) <= eps;

    public int Run(bool unused)
    {
        _ = unused;
        var failures = new List<string>();
        try
        {
            CheckNeutral(failures);
            CheckTotalityValues(failures);
            CheckCloudFloor(failures);
            CheckMonotone(failures);
            CheckTargetWeight(failures);
            CheckSmoothing(failures);
            CheckCapIndependence(failures);
            CheckSettingsAndDirector(failures);
        }
        catch (Exception exception) { Console.Error.WriteLine(exception.ToString()); return 1; }
        if (failures.Count != 0) { Console.Error.WriteLine("ECLIPSE_DARKNESS=FAIL " + string.Join(" | ", failures.Take(30))); return 1; }
        Console.WriteLine("ECLIPSE_DARKNESS=PASS");
        return 0;
    }

    static void CheckNeutral(List<string> failures)
    {
        var d = new EclipseDarknessSettings();
        foreach (double w in new[] { 0d, -1d, double.NaN })
            Expect(failures, EclipseDarknessModel.Evaluate(d, w) == EclipseDarknessOutput.Neutral, $"weight {w} must be exactly neutral");
        EclipseDarknessOutput n = EclipseDarknessOutput.Neutral;
        Expect(failures, n.Weight == 0d && n.AmbientMultiplier == 1d && n.AtmosphereNightMultiplier == 1d && n.SkyNightMultiplier == 1d
            && n.CloudAmbientMultiplier == 1d && n.StarFloor == 0d, "neutral output values");
        Expect(failures, EclipseDarknessModel.Evaluate(d with { Enabled = false }, 1d) == EclipseDarknessOutput.Neutral, "disabled model must be neutral at weight 1");
        var model = new EclipseDarknessModel();
        Expect(failures, model.Update(d with { Enabled = false }, 1d, 0d, .1d, .016d) == EclipseDarknessOutput.Neutral, "disabled Update must be neutral at full coverage");
        Expect(failures, model.Update(d, 0d, 0d, .1d, .016d) == EclipseDarknessOutput.Neutral, "zero coverage Update must be neutral");
    }

    static void CheckTotalityValues(List<string> failures)
    {
        EclipseDarknessOutput o = EclipseDarknessModel.Evaluate(new EclipseDarknessSettings(), 1d);
        Expect(failures, Near(o.AmbientMultiplier, .15d) && Near(.30d * o.AmbientMultiplier, .045d), "ambient 0.30 -> 0.045 at totality");
        Expect(failures, Near(o.AtmosphereNightMultiplier, .35d), "atmosphere night x0.35");
        Expect(failures, Near(o.SkyNightMultiplier, .5d), "sky night x0.5");
        Expect(failures, Near(o.CloudAmbientMultiplier, .4d), "cloud ambient x0.4");
        Expect(failures, Near(o.StarFloor, 1d), "star floor 1.0");
        EclipseDarknessOutput half = EclipseDarknessModel.Evaluate(new EclipseDarknessSettings(), .5d);
        Expect(failures, Near(half.AmbientMultiplier, 1d - .5d * .85d) && Near(half.StarFloor, .5d), "half weight blends linearly");
        Expect(failures, EclipseDarknessModel.Evaluate(new EclipseDarknessSettings(), 7d).Weight == 1d, "weight above 1 clamps");
    }

    static void CheckCloudFloor(List<string> failures)
    {
        Expect(failures, new EclipseDarknessSettings { CloudAmbientScale = 0d }.Sanitized().CloudAmbientScale == EclipseDarknessModel.CloudAmbientFloor, "Sanitized lifts the cloud scale to the 0.3 floor");
        Expect(failures, new EclipseDarknessSettings { CloudAmbientScale = double.NaN }.Sanitized().CloudAmbientScale == .4d, "NaN cloud scale falls back to 0.4");
        // Even an unsanitised record cannot push the multiplier below the floor.
        EclipseDarknessOutput o = EclipseDarknessModel.Evaluate(new EclipseDarknessSettings { CloudAmbientScale = 0d }, 1d);
        Expect(failures, o.CloudAmbientMultiplier >= EclipseDarknessModel.CloudAmbientFloor - 1e-12, "cloud multiplier must never go below 0.3");
        Expect(failures, EclipseDarknessModel.CloudAmbientFloor == .3d, "floor constant");
    }

    static void CheckMonotone(List<string> failures)
    {
        var d = new EclipseDarknessSettings();
        EclipseDarknessOutput previous = EclipseDarknessModel.Evaluate(d, 0d);
        for (int i = 1; i <= 100; i++)
        {
            EclipseDarknessOutput o = EclipseDarknessModel.Evaluate(d, i / 100d);
            Expect(failures, o.AmbientMultiplier <= previous.AmbientMultiplier && o.AtmosphereNightMultiplier <= previous.AtmosphereNightMultiplier
                && o.SkyNightMultiplier <= previous.SkyNightMultiplier && o.CloudAmbientMultiplier <= previous.CloudAmbientMultiplier && o.StarFloor >= previous.StarFloor,
                $"partial ramp not monotone at {i}%");
            previous = o;
        }
    }

    static void CheckTargetWeight(List<string> failures)
    {
        Expect(failures, EclipseDarknessModel.TargetWeight(1d, 0d, .1d, false) == 1d, "totality before the kindle = 1");
        Expect(failures, EclipseDarknessModel.TargetWeight(.37d, 0d, .1d, false) == .37d, "weight follows raw coverage");
        Expect(failures, EclipseDarknessModel.TargetWeight(double.NaN, 0d, .1d, false) == 0d && EclipseDarknessModel.TargetWeight(-3d, 0d, .1d, false) == 0d, "bad coverage = 0");
        Expect(failures, EclipseDarknessModel.TargetWeight(2d, 0d, .1d, false) == 1d, "coverage above 1 clamps");
        // Phase E2 keeps the world dark through the whole window: the hub level does not relieve the darkness until E3 enables it.
        Expect(failures, !EclipseDarknessModel.HubReliefAvailable, "E2: hub relief must not be available yet");
        Expect(failures, EclipseDarknessModel.TargetWeight(1d, 1d, .1d, false) == 1d, "hub level ignored while relief is off");
        Expect(failures, Near(EclipseDarknessModel.TargetWeight(1d, .1d, .1d, true), .5d), "relief on: L = kindle level halves the weight");
        Expect(failures, EclipseDarknessModel.TargetWeight(1d, .2d, .1d, true) == 0d && EclipseDarknessModel.TargetWeight(1d, 1d, .1d, true) == 0d, "relief on: L >= 2 x kindle = 0");
        var model = new EclipseDarknessModel();
        Expect(failures, model.Update(new EclipseDarknessSettings(), 1d, 5d, .1d, .016d).Weight == 1d, "Update ignores the hub level by default (E2)");
    }

    static void CheckSmoothing(List<string> failures)
    {
        var d = new EclipseDarknessSettings();
        var model = new EclipseDarknessModel();
        Expect(failures, model.Update(d, 1d, 0d, .1d, .016d).Weight == 1d, "first sample adopts the target (no fade-in on load)");
        EclipseDarknessOutput down = model.Update(d, 0d, 0d, .1d, .1d);
        Expect(failures, down.Weight > .3d && down.Weight < .9d, $"smoothing leaves the weight in transit after 0.1 s (got {down.Weight:0.000})");
        for (int i = 0; i < 600; i++) down = model.Update(d, 0d, 0d, .1d, .016d);
        Expect(failures, down.Weight == 0d && down == EclipseDarknessOutput.Neutral, "weight settles exactly to 0 (neutral) after the eclipse");
        var up = new EclipseDarknessModel();
        up.Update(d, 0d, 0d, .1d, .016d);
        double w = 0d;
        for (int i = 0; i < 30; i++) w = up.Update(d, 1d, 0d, .1d, SmoothWindow / 30d).Weight;
        Expect(failures, w >= .94d && w < 1d, $"~95 % of a step within the 0.5 s smoothing window (got {w:0.000})");
        up.Update(d, 1d, 0d, .1d, -1d);
        Expect(failures, up.Weight == 1d, "negative delta adopts the target");
        up.Reset();
        Expect(failures, up.Update(d, .25d, 0d, .1d, .016d).Weight == .25d, "Reset re-primes");
    }

    // D.3: the model reads raw coverage only, so the capped GameplayEclipseDarkness (the partial-phase tint control) cannot change it.
    static void CheckCapIndependence(List<string> failures)
    {
        CelestialFrame Total(float cap)
        {
            var config = new CelestialConfiguration
            {
                RingDaySeconds = CelestialConfiguration.DefaultRingDaySeconds, StarAngularRadiusDegrees = .265f, GameplayEclipseDarknessCap = cap,
                GasGiant = new GasGiantConfiguration
                {
                    Enabled = true, RadiusKm = GasGiantConfiguration.DefaultRadiusKm, DistanceKm = GasGiantConfiguration.DefaultDistanceKm,
                    OrbitSeconds = GasGiantConfiguration.DefaultOrbitSeconds, GrazingMissDegrees = 0f, InitialPhaseDegrees = 0f
                }
            };
            return CelestialEclipseMath.EvaluateFrame(0d, Vector3.Forward, -Vector3.Right, Vector3.Up, config, Vector3.Zero);
        }
        CelestialFrame a = Total(.95f), b = Total(.5f);
        Expect(failures, Near(a.GameplayEclipseDarkness, .95d, 1e-5) && Near(b.GameplayEclipseDarkness, .5d, 1e-5), "cap still pins the gameplay darkness (fixture sanity)");
        double wa = EclipseDarknessModel.TargetWeight(a.RawEclipseCoverage, 0d, .1d, false), wb = EclipseDarknessModel.TargetWeight(b.RawEclipseCoverage, 0d, .1d, false);
        Expect(failures, wa > .99999d && wa == wb, $"totality darkness weight is independent of the cap ({wa} vs {wb})");
    }

    static void CheckSettingsAndDirector(List<string> failures)
    {
        // Persisted defaults == model defaults; darkness-only settings changes are adopted by the engine.
        var fresh = new GameplayEclipseEventSettings();
        EclipseDarknessSettings mapped = GameplayEclipseEventDefaults.ToCore(fresh).Sanitized().Darkness, model = new EclipseDarknessSettings();
        Expect(failures, mapped.Enabled == model.Enabled && Near(mapped.AmbientScale, model.AmbientScale, 1e-6) && Near(mapped.AtmosphereNightScale, model.AtmosphereNightScale, 1e-6)
            && Near(mapped.SkyNightScale, model.SkyNightScale, 1e-6) && Near(mapped.CloudAmbientScale, model.CloudAmbientScale, 1e-6) && Near(mapped.StarVisibility, model.StarVisibility, 1e-6), "persisted darkness defaults match the model defaults");
        var engine = new EclipseEventEngine { SynchronousSolve = true };
        engine.SetSettings(engine.Settings with { Darkness = new EclipseDarknessSettings { AmbientScale = .3d } });
        Expect(failures, Near(engine.Settings.Darkness.AmbientScale, .3d), "engine adopts darkness-only changes");

        var director = new EclipseEventDirector { AutoBind = false, LoadSavedDefaults = false };
        try
        {
            Expect(failures, director.DarknessOutput == EclipseDarknessOutput.Neutral && director.DarknessAmbientMultiplier == 1f && director.DarknessStarFloor == 0f, "director starts neutral");
            director.UpdateDarkness(1d, .016d);
            Expect(failures, director.DarknessWeight == 1f && Math.Abs(director.DarknessAmbientMultiplier - .15f) < 1e-6f && Math.Abs(director.DarknessCloudAmbientMultiplier - .4f) < 1e-6f
                && Math.Abs(director.DarknessAtmosphereNightMultiplier - .35f) < 1e-6f && Math.Abs(director.DarknessSkyNightMultiplier - .5f) < 1e-6f && director.DarknessStarFloor == 1f,
                "director publishes the totality multipliers");
            director.Configure(new GameplayEclipseEventSettings { EclipseDarknessEnabled = false });
            director.UpdateDarkness(1d, .016d);
            Expect(failures, director.DarknessOutput == EclipseDarknessOutput.Neutral, "disabled darkness publishes neutral immediately");
            director.Configure(new GameplayEclipseEventSettings { EclipseAmbientScale = .5f });
            director.UpdateDarkness(1d, 5d);
            Expect(failures, director.DarknessWeight > .99f && Math.Abs(director.DarknessAmbientMultiplier - .5f) < 1e-3f, "Configure relays the darkness scales");
            director.UpdateDarkness(0d, 5d);
            Expect(failures, director.DarknessOutput == EclipseDarknessOutput.Neutral, "weight returns to exact neutral after the eclipse");
        }
        finally { director.Free(); }
    }
}
