using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// CPU side of the windowed shader parity test (tools/emitter_grid_shader_parity_test.gd): builds the eg_* uniform values the beam
/// layer would send for a clock and time, generates sample inputs, and evaluates the C# reference
/// (<see cref="EmitterGridPulseClock"/> + <see cref="EmitterGridIntensity"/>) for the same inputs. Phase E4d adds the eclipse ignition
/// fronts (z/w lanes) and the forced-night background, and a fifth output: the vertex collapse rule <c>eg_interval_lit</c>.
/// </summary>
public partial class EmitterGridShaderFixture : RefCounted
{
    const int Seed = 1337;
    /// <summary>Floats per sample: three rows of four (p, column, kind, atmosphere | day camera, day fragment, distance, dot | link P0, link P1, 0, 0).</summary>
    const int Stride = 12;
    EmitterGridPulseClock _clock = null!;
    EmitterGridTopology _topology = null!;
    EmitterGridVisibilitySettings _visibility = new();
    EmitterEventFront[] _events = Array.Empty<EmitterEventFront>();
    float _forceNight;
    double _time;

    /// <summary>Returns the shader uniform values (name to value) for a clock configuration at time <paramref name="t"/>.</summary>
    public Godot.Collections.Dictionary Setup(bool retrograde, bool manualSpeed, double t) => SetupEvents(retrograde, manualSpeed, t, "");

    /// <summary>
    /// <see cref="Setup"/> with eclipse ignition fronts appended after the normal ones: "bidirectional" (12 moving fronts with heads and a
    /// plateau, event peak 1.5, background forced to night), "plateau" (the fronts absorbed at the antipode: headless plateau spans plus the
    /// six meeting-flare spans), or "" for none.
    /// </summary>
    public Godot.Collections.Dictionary SetupEvents(bool retrograde, bool manualSpeed, double t, string eventMode)
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
        double v = _clock.SpeedMetersPerSecond, c = _clock.CircumferenceMeters;
        var fronts = new Vector4[24];
        int count = 0;
        Span<EmitterPulseFront> buffer = stackalloc EmitterPulseFront[32];
        for (int column = 0; column < 6 && count < 24; column++)
        {
            int n = _clock.GetFronts(t, column, buffer);
            for (int i = 0; i < n && count < 24; i++) fronts[count++] = new Vector4((float)buffer[i].HeadQ, column, 0f, 0f);
        }
        // Ignition fronts: the float-rounded lanes are what the shader receives and what the reference evaluates.
        var lanes = new System.Collections.Generic.List<Vector4>();
        _forceNight = 0f;
        float peak = (float)_visibility.NightPeak;
        if (eventMode.Length > 0)
        {
            _forceNight = 1f;
            peak *= 1.5f;
            bool moving = eventMode == "bidirectional";
            double travelled = moving ? 4_200_000d : c * 0.5d, flareSpan = c / 100d;
            for (int col = 0; col < 6; col++)
            {
                lanes.Add(Lane(_clock.MakeEventFront(col, travelled, travelled, true, 0.35d, !moving)));
                lanes.Add(Lane(_clock.MakeEventFront(col, c - travelled, travelled, false, 0.35d, !moving)));
                if (!moving) lanes.Add(Lane(_clock.MakeEventFront(col, c * 0.5d + flareSpan, 2d * flareSpan, true, 0.8d, true)));
            }
        }
        var events = new System.Collections.Generic.List<EmitterEventFront>();
        for (int i = 0; i < lanes.Count && count < 24; i++)
        {
            fronts[count++] = lanes[i];
            events.Add(new EmitterEventFront(lanes[i].X, (int)lanes[i].Y, lanes[i].Z, lanes[i].W));
        }
        _events = events.ToArray();
        return new Godot.Collections.Dictionary
        {
            ["eg_circumference"] = (float)_clock.CircumferenceMeters,
            ["eg_direction"] = retrograde ? -1f : 1f,
            ["eg_pulse_shape"] = new Vector4((float)settings.LeadingEdgeMeters, (float)_clock.CutoffMeters, (float)(v * settings.FrameDeltaSeconds), (float)(v * settings.TrailSeconds)),
            ["eg_wake"] = new Vector2((float)(v * settings.WakeSeconds), (float)settings.WakeWeight),
            ["eg_front_count"] = count,
            ["eg_fronts"] = fronts,
            ["eg_force_night"] = _forceNight,
            ["eg_flare"] = (float)_clock.FlareEnvelope(t),
            ["eg_visibility"] = new Vector4(peak, (float)_visibility.DayPeak, (float)_visibility.NightResidual, (float)_visibility.DayResidual),
            ["eg_day_distance"] = new Vector2((float)_visibility.DayDistanceNearMeters, (float)_visibility.DayDistanceFarMeters)
        };
    }

    static Vector4 Lane(in EmitterEventFront f) => new((float)f.HeadQ, f.Column, (float)f.SignedTravelled, (float)f.Plateau);

    /// <summary>A time shortly after a relaunch (flare envelope near 1) for the clock configured by the last Setup call.</summary>
    public double TimeAfterLaunch() => _clock.LaunchTime(_clock.LatestLaunch(5000d)) + 0.2d;

    /// <summary>Number of ignition front lanes set by the last SetupEvents call.</summary>
    public int EventFrontCount() => _events.Length;

    /// <summary>Stride of the sample array (floats per sample); the runner builds three RGBAF rows from it.</summary>
    public int SampleStride() => Stride;

    /// <summary>Row 0 (p, column, kind, atmosphere), row 1 (day camera, day fragment, distance, dot) and row 2 (link P0, link P1) as a flat array.</summary>
    public float[] Samples(int count, int randomSeed)
    {
        var rng = new Random(randomSeed);
        var data = new float[count * Stride];
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
            else if (i % 3 == 1 && _events.Length > 0)
            {
                // Around an ignition front of this column: its head, the inside of its span, and the far end of the span.
                int k = rng.Next(_events.Length);
                for (int guard = 0; guard < _events.Length && _events[k].Column != column; guard++) k = (k + 1) % _events.Length;
                if (_events[k].Column == column)
                {
                    EmitterEventFront f = _events[k];
                    double span = Math.Abs(f.SignedTravelled), dir = f.SignedTravelled > 0d ? 1d : -1d, u = rng.NextDouble();
                    double q = u < 0.4d ? f.HeadQ + (rng.NextDouble() * 2d - 0.5d) * 400_000d
                        : u < 0.7d ? f.HeadQ - dir * rng.NextDouble() * span
                        : f.HeadQ - dir * span + (rng.NextDouble() * 2d - 1d) * 60_000d;
                    q = Math.Clamp(q, 0d, c);
                    p = _clock.Settings.Direction == EmitterPulseDirection.Prograde ? q : c - q;
                    link = LinkAt(column, p);
                }
            }
            EmitterLink at = LinkAt(column, p);
            data[i * Stride + 0] = (float)p;
            data[i * Stride + 1] = column;
            data[i * Stride + 2] = (float)(byte)link.Kind;
            data[i * Stride + 3] = (float)rng.NextDouble();
            data[i * Stride + 4] = (float)rng.NextDouble();
            data[i * Stride + 5] = (float)rng.NextDouble();
            data[i * Stride + 6] = (float)(rng.NextDouble() * 900_000d);
            data[i * Stride + 7] = (float)(rng.NextDouble() * 2d - 1d);
            data[i * Stride + 8] = (float)at.P0;
            data[i * Stride + 9] = (float)at.P1;
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

    /// <summary>Reference (pulse, intensity, background, day, lit) per sample, from the float-rounded inputs the shader receives.</summary>
    public float[] Reference(float[] samples, float solarVisibility, float hubDay = 0f)
    {
        int count = samples.Length / Stride;
        var result = new float[count * 5];
        EmitterGridVisibilitySettings visibility = _forceNight > 0f ? _visibility with { NightPeak = _visibility.NightPeak * 1.5d } : _visibility;
        for (int i = 0; i < count; i++)
        {
            double p = samples[i * Stride], column = samples[i * Stride + 1], kind = samples[i * Stride + 2], atmosphere = samples[i * Stride + 3];
            EmitterLink link = LinkAt((int)column, p) with { Kind = kind > 0.5 ? EmitterLinkKind.KnotOut : EmitterLinkKind.Chain };
            double pulse = _clock.PulseAtLink(_time, link, (p - link.P0) / (link.P1 - link.P0), _events);
            double b = EmitterGridIntensity.Background(samples[i * Stride + 4], samples[i * Stride + 5], samples[i * Stride + 6], _visibility) * (1d - _forceNight);
            result[i * 5 + 0] = (float)pulse;
            result[i * 5 + 1] = (float)EmitterGridIntensity.Compute(pulse, b, 0d, atmosphere, visibility);
            result[i * 5 + 2] = (float)b;
            result[i * 5 + 3] = (float)EmitterGridIntensity.DayAt(samples[i * Stride + 7], solarVisibility, hubDay);
            // The vertex rule on the link interval the shader is given (P0, P1 of the sampled link).
            EmitterLink interval = link with { P0 = samples[i * Stride + 8], P1 = samples[i * Stride + 9] };
            result[i * 5 + 4] = _clock.IsLinkLit(interval, _time, _events) ? 1f : 0f;
        }
        return result;
    }
}
