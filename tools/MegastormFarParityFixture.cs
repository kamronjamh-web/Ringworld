using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// CPU side of the windowed far-megastorm shader parity test (tools/megastorm_far_parity_test.gd): the GLSL across noise, strip column terms
/// and flash glow (materials/ring_megastorm_profile.gdshaderinc, ring_far_storm_lighting.gdshaderinc) versus MegastormProfile.AcrossTerms /
/// Column and MegastormLightning.GlowAt. Builds the cases (two seeds, three weather times, column points over the whole profile, live flashes
/// sampled at their own point and offsets), packs the shader inputs as float rows and evaluates the C# reference on the same float-rounded inputs.
/// </summary>
public partial class MegastormFarParityFixture : RefCounted
{
    public const int FieldCount = 14, RowCount = 6;
    static readonly int[] Seeds = { 7, 4242 };
    static readonly double[] Times = { 1.0e5, 4.3e5 + 0.37, 2.9e6 + 1.9 };
    static readonly double[] FlashAges = { 0.02, 0.035, 0.1, 0.27, 0.5 };
    // (dx, dAcross, dAltitude) offsets from the flash point: its own point, then 3 km and 6 km away in x, across and altitude.
    static readonly (double X, double A, double Alt)[] FlashOffsets = { (0, 0, 0), (3000, 0, 0), (0, 6000, 0), (-3000, 0, 6000) };

    readonly struct Case
    {
        public readonly int Model; public readonly float X, Across, Altitude; public readonly double T;
        public Case(int model, double x, double across, double altitude, double t) { Model = model; X = (float)x; Across = (float)across; Altitude = (float)altitude; T = t; }
    }

    readonly List<MegastormModel> _models = new();
    readonly List<Case> _cases = new();
    float[] _inputs = Array.Empty<float>();
    float[] _reference = Array.Empty<float>();

    static MegastormModel MakeModel(int seed)
    {
        var model = new MegastormModel();
        // Triggered 1e5 s before the first test time: far past the 1800 s formation, so level = 1.
        model.Trigger(new MegastormTrigger("parity", 5_000_000.0, 1, -1.0e5, 350f, 1800f, seed));
        return model;
    }

    /// <summary>Builds the cases, shader inputs and C# reference; returns the case count.</summary>
    public int Build()
    {
        _models.Clear();
        _cases.Clear();
        foreach (int seed in Seeds) _models.Add(MakeModel(seed));
        var rng = new Random(20260430);
        for (int m = 0; m < _models.Count; m++)
            foreach (double time in Times)
            {
                MegastormModel model = _models[m];
                double tf = (float)time;
                double width = model.Settings.WidthMeters;
                for (int i = 0; i < 64; i++)
                {
                    double across = i % 8 == 0 ? rng.NextDouble() * 15000.0 : i % 8 == 4 ? width - rng.NextDouble() * 15000.0 : rng.NextDouble() * width;
                    double xi;
                    if (i < 20) xi = -2000.0 + 4000.0 * i / 19.0;                   // front edge
                    else if (i < 34) xi = 20000.0 + 35000.0 * (i - 20) / 13.0;     // transition
                    else if (i < 44) xi = -64000.0 + 14000.0 * (i - 34) / 9.0;      // canopy (avoids the exact coverage-0 edge at -60 km, where float vs double xi flips the heights presence test)
                    else xi = -72000.0 + rng.NextDouble() * 128000.0;
                    double af = (float)across;
                    double x = Math.Clamp(xi + model.LeadOffset(af, tf), -72000.0, 56000.0);
                    _cases.Add(new Case(m, x, across, 3000.0 + rng.NextDouble() * 9000.0, time));
                }
                var flashes = new List<MegastormLightning.MegastormFlash>();
                MegastormLightning.FlashesInWindow(model, time, time + 200.0, flashes);
                if (flashes.Count < 3) throw new InvalidOperationException("too few flashes in window");
                foreach (int pick in new[] { 0, flashes.Count / 3, 2 * flashes.Count / 3 })
                {
                    MegastormLightning.MegastormFlash f = flashes[pick];
                    foreach (double age in FlashAges)
                        foreach (var (ox, oa, oalt) in FlashOffsets)
                        {
                            double t = f.StartTimeSeconds + age;
                            double fx = f.XiMeters + model.LeadOffset(f.AcrossMeters, t);
                            _cases.Add(new Case(m, fx + ox, f.AcrossMeters + oa, f.AltitudeMeters + oalt, t));
                        }
                }
            }
        Pack();
        return _cases.Count;
    }

    // Rows (RGBAF texel per case): 0 (x, across, altitude, slot_time), 1 (t float, level, strength, seed), 2 (W, bow, meander amp, wavelength),
    // 3 (meander phase, ceiling, slot, reduced peak), 4 (C# tower noise, mammatus noise, 0, 0), 5 (glow strength, glow meander phase, 0, 0).
    void Pack()
    {
        int n = _cases.Count;
        _inputs = new float[RowCount * n * 4];
        _reference = new float[n * FieldCount];
        void Put(int row, int i, float a, float b, float c, float d)
        {
            int o = (row * n + i) * 4;
            _inputs[o] = a; _inputs[o + 1] = b; _inputs[o + 2] = c; _inputs[o + 3] = d;
        }
        for (int i = 0; i < n; i++)
        {
            Case c = _cases[i];
            MegastormModel model = _models[c.Model];
            MegastormSettings s = model.Settings;
            double tf = (float)c.T; // the shader's t is a float; the column and noise are evaluated at that same value
            MegastormFrame frame = model.Evaluate(tf), glowFrame = model.Evaluate(c.T);
            long slot = (long)Math.Floor(c.T / MegastormLightning.SlotSeconds);
            float slotTime = (float)(c.T - slot * MegastormLightning.SlotSeconds);
            var terms = MegastormProfile.AcrossTerms(model, c.Across, tf);
            Put(0, i, c.X, c.Across, c.Altitude, slotTime);
            Put(1, i, (float)tf, frame.Level, frame.Strength, model.State.Seed);
            Put(2, i, (float)s.WidthMeters, (float)s.BowMeters, (float)s.MeanderAmplitudeMeters, (float)s.MeanderWavelengthMeters);
            Put(3, i, (float)model.MeanderPhaseWrapped(tf), MegastormModel.CeilingMeters, slot, Lightning.LightningVisualPolicy.ReducedPeakScale);
            Put(4, i, (float)terms.Tower, (float)terms.Mammatus, 0f, 0f);
            Put(5, i, glowFrame.Strength, (float)model.MeanderPhaseWrapped(c.T), 0f, 0f);

            int o = i * FieldCount;
            _reference[o] = (float)terms.Tower; _reference[o + 1] = (float)terms.Mammatus; _reference[o + 2] = (float)terms.Shaft;
            MegastormColumn col = MegastormProfile.Column(model, c.X, c.Across, tf);
            _reference[o + 3] = col.BodyBase; _reference[o + 4] = col.BodyTop; _reference[o + 5] = col.CanopyUnderside; _reference[o + 6] = col.CanopyTop;
            _reference[o + 7] = col.CanopyCoverage; _reference[o + 8] = col.Mammatus; _reference[o + 9] = col.TowerCoverage; _reference[o + 10] = col.BodyCoverage;
            _reference[o + 11] = (float)MegastormLightning.GlowAt(model, c.X, c.Across, c.Altitude, c.T, false);
            _reference[o + 12] = (float)MegastormLightning.GlowAt(model, c.X, c.Across, c.Altitude, c.T, true);
            _reference[o + 13] = (float)c.T; // informational (debug print)
        }
    }

    /// <summary>The input rows, row-major (RowCount rows of CaseCount RGBAF texels).</summary>
    public float[] Inputs() => _inputs;

    /// <summary>Reference value per case and field (FieldCount floats per case; the last is the case time, not compared).</summary>
    public float[] Expected() => _reference;
}
