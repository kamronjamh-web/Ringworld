using System;
using System.IO;
using Godot;
using RingworldTechDemo.UI;

namespace RingworldTechDemo.Tools;

/// <summary>
/// WO-A (docs/storm-revamp-s4-design.md): the storm lifecycle code uploaded in each render system's d.w is continuous,
/// non-decreasing through one lifecycle (birth 0-0.3, develop 0.3-1, mature 1-2, dissipate 2-3), deterministic, and 1.5 when
/// lifecycles are off. Also checks the anvil settings (persistence whitelist and clamping; the bake reads them through the storm profile map parameters).
/// </summary>
public partial class StormLifecycleCodeTest : RefCounted
{
    public string Run()
    {
        var layer = new RingVolumetricCloudLayer { NaturalWeatherLifecyclesEnabled = true };
        double cycle = layer.WeatherBirthSeconds + layer.WeatherDevelopSeconds + layer.WeatherMatureSeconds
            + layer.WeatherDissipateSeconds + layer.WeatherQuietSeconds;
        int[] seeds = { 1337, 42 };
        (int a, int z)[] cells = { (0, 0), (5, 3), (-4, 11), (200, 7) };
        const double step = 1.0;
        int checkedSamples = 0;
        foreach (int seed in seeds)
        {
            foreach (var (a, z) in cells)
            {
                float previous = layer.EvaluateStormLifecycleCode(a, z, seed, 0.0);
                int drops = 0;
                float maxJump = 0.0f;
                for (double t = step; t <= cycle * 2.0; t += step)
                {
                    float code = layer.EvaluateStormLifecycleCode(a, z, seed, t);
                    if (!(code >= 0.0f && code <= 3.0f)) return $"FAIL code out of range {code} cell=({a},{z}) t={t}";
                    if (code < previous - 1e-5f)
                    {
                        // Only the quiet -> birth wrap of a new generation may decrease.
                        if (previous < 2.99f || code > 0.1f) return $"FAIL decreasing {previous}->{code} cell=({a},{z}) t={t}";
                        drops++;
                    }
                    else maxJump = Math.Max(maxJump, code - previous);
                    previous = code;
                    checkedSamples++;
                }
                // Over two cycles there is at most 3 wraps (phase offset).
                if (drops > 3) return $"FAIL too many generation wraps {drops}";
                // A stage lasts tens of seconds at the defaults; one second must never jump more than a few percent.
                if (maxJump > 0.08f) return $"FAIL discontinuity {maxJump} cell=({a},{z})";
                // Determinism, also across a fresh instance.
                var other = new RingVolumetricCloudLayer { NaturalWeatherLifecyclesEnabled = true };
                for (double t = 3.0; t < cycle; t += 97.0)
                    if (layer.EvaluateStormLifecycleCode(a, z, seed, t) != other.EvaluateStormLifecycleCode(a, z, seed, t))
                        return $"FAIL non-deterministic cell=({a},{z}) t={t}";
                other.Free();
            }
        }
        // The code visits every stage over a cycle.
        bool sawBirth = false, sawDevelop = false, sawMature = false, sawDissipate = false, sawQuiet = false;
        for (double t = 0.0; t < cycle; t += 2.0)
        {
            float code = layer.EvaluateStormLifecycleCode(0, 0, 1337, t);
            sawBirth |= code < 0.3f; sawDevelop |= code is >= 0.3f and < 1.0f; sawMature |= code is >= 1.0f and < 2.0f;
            sawDissipate |= code is >= 2.0f and < 3.0f; sawQuiet |= code >= 3.0f;
        }
        if (!(sawBirth && sawDevelop && sawMature && sawDissipate && sawQuiet)) return "FAIL a lifecycle stage never appeared";

        layer.NaturalWeatherLifecyclesEnabled = false;
        for (double t = 0.0; t < cycle; t += 211.0)
            if (Math.Abs(layer.EvaluateStormLifecycleCode(3, 4, 1337, t) - 1.5f) > 1e-6f) return "FAIL lifecycles off must give 1.5";
        if (Math.Abs(RingVolumetricCloudLayer.StormLifecycleMature - 1.5f) > 1e-6f) return "FAIL mature constant";
        layer.Free();

        // Settings: whitelisted, clamped, persisted as sparse overrides.
        string path = ProjectSettings.GlobalizePath("res://.godot/storm_anvil_settings_test.tres");
        string resPath = "res://.godot/storm_anvil_settings_test.tres";
        if (File.Exists(path)) File.Delete(path);
        var defaults = new GameplayCloudDefaults(resPath);
        defaults.Load();
        if (!defaults.SetOverride(null, "AnvilShear", 2.5f)) return "FAIL AnvilShear not whitelisted";
        if (!defaults.SetOverride(null, "MammatusStrength", 0.25f)) return "FAIL MammatusStrength not whitelisted";
        if (Math.Abs(defaults.Settings.Overrides["AnvilShear"].AsSingle() - 1.0f) > 1e-6f) return "FAIL AnvilShear not clamped to 1";
        if (!defaults.Save()) return "FAIL save: " + defaults.LastError;
        var reloaded = new GameplayCloudDefaults(resPath);
        reloaded.Load();
        if (Math.Abs(reloaded.Settings.Overrides["MammatusStrength"].AsSingle() - 0.25f) > 1e-6f) return "FAIL MammatusStrength not persisted";
        if (reloaded.SetOverride(null, "AnvilShear", float.NaN)) return "FAIL NaN accepted";
        if (File.Exists(path)) File.Delete(path);

        return $"PASS samples={checkedSamples}";
    }
}
