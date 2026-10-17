using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// CPU side of the windowed shader parity test (tools/emitter_grid_shader_parity_test.gd): builds the eg_* uniform values the beam
/// layer would send for a clock and time, generates sample inputs, and evaluates the C# reference
/// (<see cref="EmitterGridPulseClock"/> + <see cref="EmitterGridIntensity"/>) for the same inputs.
/// </summary>
public partial class EmitterGridShaderFixture : RefCounted
{
    const int Seed = 1337;
    EmitterGridPulseClock _clock = null!;
    EmitterGridTopology _topology = null!;
    EmitterGridVisibilitySettings _visibility = new();
    double _time;

    /// <summary>Returns the shader uniform values (name to value) for a clock configuration at time <paramref name="t"/>.</summary>
    public Godot.Collections.Dictionary Setup(bool retrograde, bool manualSpeed, double t)
    {
        using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17(Seed);
        using INaturalHeightSource source = EmitterSiteHeightSources.Create(sampler);
        EmitterSiteRegistry registry = EmitterSiteRegistry.GetOrBuild(EmitterSiteRecipe.Default(Seed), source);
        _topology = EmitterGridTopology.Build(registry);
        var settings = new EmitterGridPulseSettings
        {
            Direction = retrograde ? EmitterPulseDirection.Retrograde : EmitterPulseDirection.Prograde,
            AutoSpeed = !manualSpeed,
            ManualSpeedMetersPerSecond = 40_000d,
            ColumnStaggerSeconds = 1.5d
        };
        _clock = new EmitterGridPulseClock(Seed, _topology.CircumferenceMeters, settings);
        _time = t;
        double v = _clock.SpeedMetersPerSecond;
        var fronts = new Vector4[24];
        int count = 0;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        for (int column = 0; column < 6 && count < 24; column++)
        {
            int n = _clock.GetFronts(t, column, buffer);
            for (int i = 0; i < n && count < 24; i++) fronts[count++] = new Vector4((float)buffer[i].HeadQ, column, 0f, 0f);
        }
        return new Godot.Collections.Dictionary
        {
            ["eg_circumference"] = (float)_clock.CircumferenceMeters,
            ["eg_direction"] = retrograde ? -1f : 1f,
            ["eg_pulse_shape"] = new Vector4((float)settings.LeadingEdgeMeters, (float)_clock.CutoffMeters, (float)(v * settings.FrameDeltaSeconds), (float)(v * settings.TrailSeconds)),
            ["eg_wake"] = new Vector2((float)(v * settings.WakeSeconds), (float)settings.WakeWeight),
            ["eg_front_count"] = count,
            ["eg_fronts"] = fronts,
            ["eg_flare"] = (float)_clock.FlareEnvelope(t),
            ["eg_visibility"] = new Vector4((float)_visibility.NightPeak, (float)_visibility.DayPeak, (float)_visibility.NightResidual, (float)_visibility.DayResidual),
            ["eg_day_distance"] = new Vector2((float)_visibility.DayDistanceNearMeters, (float)_visibility.DayDistanceFarMeters)
        };
    }

    /// <summary>A time shortly after a relaunch (flare envelope near 1) for the clock configured by the last Setup call.</summary>
    public double TimeAfterLaunch() => _clock.LaunchTime(_clock.LatestLaunch(5000d)) + 0.2d;

    /// <summary>Row 0 (p, column, kind, atmosphere) and row 1 (day camera, day fragment, distance, dot) as an RGBAF image buffer of 2 rows.</summary>
    public float[] Samples(int count, int randomSeed)
    {
        var rng = new Random(randomSeed);
        var data = new float[count * 8];
        double c = _clock.CircumferenceMeters;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        for (int i = 0; i < count; i++)
        {
            int column = rng.Next(6);
            EmitterLink link = _topology[column * _topology.LinksPerChain + rng.Next(_topology.LinksPerChain)];
            double p = link.P0 + rng.NextDouble() * (link.P1 - link.P0);
            if (i % 3 == 0) // concentrate around a head: within the leading edge and the trail
            {
                int n = _clock.GetFronts(_time, column, buffer);
                if (n > 0)
                {
                    double q = buffer[rng.Next(n)].HeadQ + (rng.NextDouble() * 2d - 0.4d) * 400_000d;
                    q = Math.Clamp(q, 0d, c);
                    p = _clock.Settings.Direction == EmitterPulseDirection.Prograde ? q : c - q;
                    link = LinkAt(column, p);
                }
            }
            data[i * 8 + 0] = (float)p;
            data[i * 8 + 1] = column;
            data[i * 8 + 2] = (float)(byte)link.Kind;
            data[i * 8 + 3] = (float)rng.NextDouble();
            data[i * 8 + 4] = (float)rng.NextDouble();
            data[i * 8 + 5] = (float)rng.NextDouble();
            data[i * 8 + 6] = (float)(rng.NextDouble() * 900_000d);
            data[i * 8 + 7] = (float)(rng.NextDouble() * 2d - 1d);
        }
        return data;
    }

    EmitterLink LinkAt(int column, double p)
    {
        int first = _topology.ChainFirstLink(column);
        for (int k = 0; k < _topology.LinksPerChain; k++)
        {
            EmitterLink l = _topology[first + k];
            if (p >= l.P0 && p <= l.P1) return l;
        }
        return _topology[first + _topology.LinksPerChain - 1];
    }

    /// <summary>Reference (pulse, intensity, background, day) per sample, from the float-rounded inputs the shader receives.</summary>
    public float[] Reference(float[] samples, float solarVisibility, float hubDay = 0f)
    {
        int count = samples.Length / 8;
        var result = new float[count * 4];
        for (int i = 0; i < count; i++)
        {
            double p = samples[i * 8], column = samples[i * 8 + 1], kind = samples[i * 8 + 2], atmosphere = samples[i * 8 + 3];
            EmitterLink link = LinkAt((int)column, p);
            double pulse = _clock.PulseAtLink(_time, link with { Kind = kind > 0.5 ? EmitterLinkKind.KnotOut : EmitterLinkKind.Chain }, (p - link.P0) / (link.P1 - link.P0));
            double b = EmitterGridIntensity.Background(samples[i * 8 + 4], samples[i * 8 + 5], samples[i * 8 + 6], _visibility);
            result[i * 4 + 0] = (float)pulse;
            result[i * 4 + 1] = (float)EmitterGridIntensity.Compute(pulse, b, 0d, atmosphere, _visibility);
            result[i * 4 + 2] = (float)b;
            result[i * 4 + 3] = (float)EmitterGridIntensity.DayAt(samples[i * 8 + 7], solarVisibility, hubDay);
        }
        return result;
    }
}
