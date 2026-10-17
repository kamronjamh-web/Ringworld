using System;
using Godot;
using RingworldTechDemo.Celestial.Eclipse;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// CPU side of the windowed shader parity test (tools/eclipse_hub_beam_shader_parity_test.gd): builds the ev_* uniform values the beam layer
/// sends for a timeline at an event time, generates sample inputs concentrated around the climbing tips, the handoff and the cut, and evaluates the
/// C# reference (<see cref="EclipseHubBeamMath"/>) for the same float-rounded inputs.
/// </summary>
public partial class EclipseHubBeamShaderFixture : RefCounted
{
    const int Seed = 1337;
    EclipseEventTimeline? _timeline;
    EclipseBeamSettings _beams = new();
    const int Stride = 12;
    EclipseHubBeamUniforms _uniforms;
    Vector4[] _fronts = Array.Empty<Vector4>();
    int _frontCount;
    float _direction = 1f, _circumference;
    double _tau;

    EclipseEventTimeline Timeline()
    {
        if (_timeline != null) return _timeline;
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(Seed);
        using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
        EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(Seed), source);
        EmitterGridTopology topology = EmitterGridTopology.Build(registry);
        var clock = new EmitterGridPulseClock(Seed, topology.CircumferenceMeters);
        _timeline = new EclipseEventTimeline(new EclipseEventSettings(), new EclipseEventWindow(0, 12_048.45d, 16_138.59d), EclipseSiteTable.FromTopology(topology), clock, Seed);
        return _timeline;
    }

    /// <summary>Event time (seconds from the window start) of a named case.</summary>
    public double CaseTau(string name)
    {
        EclipseEventTimeline t = Timeline();
        return name switch
        {
            "pre_fire" => t.TauMainFire - 3d,
            "main_climb" => t.TauMainFire + t.MainClimb * 0.6d,
            "ignition" => t.TauIgnition + 31.7d,
            "regular_climb" => t.TauIgnition + 77.25d,
            "handoff" => t.TauHandoff + t.HandoffFade * 0.45d,
            "sustain" => t.TauSustain + 12d,
            "sustain_flash" => t.TauSustain + 731.4d,
            "instability_edge" => t.TauInstability + 1.0d,
            "retract" => t.TauImplosion + t.BeamRetract * 0.4d,
            "after_cut" => t.TauImplosion + t.BeamRetract + 0.5d,
            _ => throw new ArgumentException("unknown case " + name, nameof(name))
        };
    }

    /// <summary>The shader uniform values (name to value) at the case time; <paramref name="level"/> is the global gain.</summary>
    public Godot.Collections.Dictionary Setup(string caseName, float level)
    {
        EclipseEventTimeline t = Timeline();
        _tau = CaseTau(caseName);
        _uniforms = EclipseHubBeamMath.FromTimeline(t, _beams, _tau, level);
        // The grid's normal fronts at the celestial grid time (RefuelClock Celestial), as the beam layer publishes them for the flashes.
        EmitterGridPulseClock clock = t.GridClock;
        _fronts = new Vector4[24];
        int count = 0;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        double grid = t.Window.Start + _tau;
        double behind = clock.SpeedMetersPerSecond * (t.Settings.OrbTravelSeconds + t.Settings.FlashHoldSeconds + t.Settings.FlashFadeSeconds + 2d);
        for (int column = 0; column < 6 && count < 24; column++)
        {
            int n = clock.GetFrontsWithin(grid, column, behind, buffer); // the orbs need fronts up to travel + hold + fade behind the loop end
            for (int i = 0; i < n && count < 24; i++) _fronts[count++] = new Vector4((float)buffer[i].HeadQ, column, 0f, 0f);
        }
        _frontCount = count;
        _direction = clock.Settings.Direction == EmitterPulseDirection.Prograde ? 1f : -1f;
        _circumference = (float)clock.CircumferenceMeters;
        return new Godot.Collections.Dictionary
        {
            ["ev_flash"] = _uniforms.Flash,
            ["ev_refuel"] = _uniforms.Refuel,
            ["ev_orb"] = _uniforms.Orb,
            ["eg_front_count"] = count,
            ["eg_fronts"] = _fronts,
            ["eg_direction"] = _direction,
            ["eg_circumference"] = _circumference,
            ["ev_time"] = _uniforms.Time,
            ["ev_marks"] = _uniforms.Marks,
            ["ev_climb"] = _uniforms.Climb,
            ["ev_level"] = _uniforms.Level,
            ["ev_shape"] = new Vector2(_uniforms.SoftTip, _uniforms.DayVisibility),
            ["cloud_params"] = new Vector4(3f, 0.5f, 1f, 1f)
        };
    }

    /// <summary>Stride of the sample array; the runner builds three RGBAF rows from it.</summary>
    public int SampleStride() => Stride;

    /// <summary>
    /// Three rows of 4 floats per sample: (fire tau, kind, f, hub day), (atmosphere, layer weight, cloud k, deck low) and (p_site, column, deck high or minus it when unqueried, height). A quarter of the
    /// regular beams get a spire coordinate a fraction of a second either side of a live grid front, so the refuel flashes are exercised.
    /// </summary>
    public float[] Samples(int count, int randomSeed)
    {
        EclipseEventTimeline t = Timeline();
        var rng = new Random(randomSeed);
        var flashRng = new Random(randomSeed + 1);
        var data = new float[count * Stride];
        double v = t.GridClock.SpeedMetersPerSecond, c = t.GridClock.CircumferenceMeters;
        int liveFronts = _frontCount;
        for (int i = 0; i < count; i++)
        {
            bool main = i % 17 == 0;
            int entry = rng.Next(t.SiteCount);
            double fire = main ? t.TauMainFire : t.SiteFireTau(entry);
            // Concentrate the fire times around the case time so the climbing tips are exercised (a regular beam climbs for 1.5 s).
            if (!main && i % 3 != 0) fire = _tau - rng.NextDouble() * 2.0d + 0.25d;
            double tip = EclipseHubBeamMath.Tip(_uniforms, (float)fire, main ? 1f : 0f);
            double f = rng.NextDouble();
            // Near the tip, where the soft end lives.
            if (i % 2 == 0 && tip > 0d && tip < 1d) f = Math.Clamp(tip + (rng.NextDouble() - 0.7d) * 0.012d, 0d, 1d);
            data[i * Stride + 0] = (float)fire;
            data[i * Stride + 1] = main ? 1f : 0f;
            data[i * Stride + 2] = (float)f;
            // Hub day concentrated on the 0.45..0.55 smoothing band half of the time.
            data[i * Stride + 3] = (float)(i % 2 == 0 ? 0.4d + rng.NextDouble() * 0.2d : rng.NextDouble());
            data[i * Stride + 4] = (float)rng.NextDouble();
            data[i * Stride + 5] = (float)rng.NextDouble();
            double p = t.Sites.P[entry];
            int column = t.Sites.Column[entry];
            if (!main && i % 4 == 1 && liveFronts > 0)
            {
                Vector4 front = _fronts[flashRng.Next(liveFronts)];
                double age = flashRng.NextDouble() * 3.6d - 0.4d;
                if (i % 8 == 5) age = flashRng.NextDouble() * (_timeline!.Settings.OrbTravelSeconds + 5d) - 0.4d; // orbs in flight, up to the arrival and the fade
                double q = Math.Clamp(front.X - age * v, 0d, c);
                p = _direction > 0f ? q : c - q;
                column = (int)front.Y;
                if (i % 53 == 1) data[i * Stride + 0] = EclipseHubBeamMath.NeverFireTau; // a dead site
            }
            data[i * Stride + 8] = (float)p;
            data[i * Stride + 9] = column;
            // In-cloud data: k (a quarter of the samples have none), deck low/high above the crown, queried flag (negative high = unqueried) and a height
            // concentrated on the deck edges half of the time.
            double k = i % 4 == 3 ? 0d : rng.NextDouble();
            double low = rng.NextDouble() * 3000d, high = low + 500d + rng.NextDouble() * 5000d;
            double height = i % 2 == 0 ? (rng.Next(2) == 0 ? low : high) + (rng.NextDouble() - 0.5d) * 500d : rng.NextDouble() * 9000d;
            data[i * Stride + 6] = (float)k;
            data[i * Stride + 7] = (float)low;
            data[i * Stride + 10] = (float)(i % 5 == 4 ? -high : high);
            data[i * Stride + 11] = (float)height;
        }
        return data;
    }

    /// <summary>Reference (tip, handoff, tip mask, refuel-aware level, flash envelope, flash tip, deck mask, in-cloud factor, core keep, orb envelope, orb fraction, orb level) per sample, from the float-rounded inputs the shader receives.</summary>
    public float[] Reference(float[] samples)
    {
        int count = samples.Length / Stride;
        var result = new float[count * 12];
        for (int i = 0; i < count; i++)
        {
            float fire = samples[i * Stride], kind = samples[i * Stride + 1], f = samples[i * Stride + 2], hubDay = samples[i * Stride + 3];
            float atmosphere = samples[i * Stride + 4], layer = samples[i * Stride + 5], p = samples[i * Stride + 8], column = samples[i * Stride + 9];
            float tip = EclipseHubBeamMath.Tip(_uniforms, fire, kind);
            var flash = EclipseHubBeamMath.RefuelFlash(_uniforms, _fronts.AsSpan(0, _frontCount), _direction, _circumference, fire, kind, p, column);
            result[i * 12 + 0] = tip;
            result[i * 12 + 1] = EclipseHubBeamMath.Handoff(_uniforms, kind);
            result[i * 12 + 2] = EclipseHubBeamMath.TipMask(_uniforms, f, tip);
            result[i * 12 + 3] = EclipseHubBeamMath.LevelRefuel(_uniforms, fire, kind, f, hubDay, atmosphere, layer, flash);
            result[i * 12 + 4] = flash.Envelope;
            result[i * 12 + 5] = flash.Tip;
            var orb = EclipseHubBeamMath.Orb(_uniforms, _fronts.AsSpan(0, _frontCount), _direction, _circumference, fire, kind, p, column);
            result[i * 12 + 9] = orb.Envelope;
            result[i * 12 + 10] = orb.Fraction;
            result[i * 12 + 11] = EclipseHubBeamMath.OrbLevel(_uniforms, orb.Envelope, hubDay, atmosphere, layer);
            float k = samples[i * Stride + 6], low = samples[i * Stride + 7], highSigned = samples[i * Stride + 10], height = samples[i * Stride + 11];
            var cloud = new Color(k, low, Math.Abs(highSigned), highSigned >= 0f ? 1f : 0f);
            float factor = EclipseHubBeamMath.CloudFactor(cloud, height);
            result[i * 12 + 6] = EclipseHubBeamMath.CloudMask(height, low, Math.Abs(highSigned));
            result[i * 12 + 7] = factor;
            result[i * 12 + 8] = EclipseHubBeamMath.CloudCoreKeep(factor, 0.5f);
        }
        return result;
    }
}
