using System;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;

/// <summary>Headless pure-logic evidence for storm revamp S4 WO-E and the lightning policy: sprite determinism, rates by kind, reduced-flash slew cap.</summary>
public partial class UpperAtmosphereLightningTest : Node
{
    public string Result { get; private set; } = "RUNNING";

    public override void _Ready()
    {
        try { Run(); Result = "PASS"; }
        catch (Exception error) { Result = "FAIL " + error.Message; }
        finally { LightningVisualPolicy.Reset(); }
    }

    /// <summary>Lets the GDScript wrapper drive the policy before querying the production scheduler.</summary>
    public void SetPolicy(float frequency, float upperAtmosphere, bool reduced) => LightningVisualPolicy.Apply(frequency, upperAtmosphere, reduced);
    public void ResetPolicy() => LightningVisualPolicy.Reset();
    /// <summary>Discharge kind (UpperAtmosphereKind) an event id would produce if its roll succeeded; capture harnesses use it to pick ids by kind.</summary>
    public int KindForEventId(long id) => UpperAtmosphereLightning.TryDerive(MakeEvent(id, 5), 6000f, out UpperAtmosphereSprite s, 1f) ? (int)s.Kind : 0;

    private static void Require(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    private static RingLightningEvent MakeEvent(long id, int kind) =>
        new(id, 1, 100.0, new RingCanonicalPosition(1000, 10, 2000), new RingCanonicalPosition(1000, 3000, 2000), 1, 0f, 0.8f, 5000f, false, kind);

    private static double Rate(int kind, int count, float? scale, int[]? kindCounts = null)
    {
        int hits = 0;
        for (long i = 1; i <= count; i++)
        {
            long id = i * 2654435761L + 12345;
            if (!UpperAtmosphereLightning.TryDerive(MakeEvent(id, kind), 6000f, out UpperAtmosphereSprite s, scale)) continue;
            hits++;
            if (kindCounts != null) kindCounts[(int)s.Kind]++;
        }
        return hits / (double)count;
    }

    private static void Run()
    {
        LightningVisualPolicy.Reset();
        const int n = 60000;
        // Determinism: identical derivation for identical events, and a derived discharge fits inside the parent event lifetime.
        for (long i = 1; i <= 2000; i++)
        {
            RingLightningEvent e = MakeEvent(i * 7919L, 4);
            bool a = UpperAtmosphereLightning.TryDerive(e, 6000f, out UpperAtmosphereSprite sa, 1f);
            bool b = UpperAtmosphereLightning.TryDerive(e, 6000f, out UpperAtmosphereSprite sb, 1f);
            Require(a == b && sa == sb, "derivation is not deterministic");
            if (a) Require(sa.StartTimeSeconds >= e.StartTimeSeconds && sa.StartTimeSeconds + sa.DurationSeconds <= e.StartTimeSeconds + RingLightningEvent.LifetimeSeconds,
                "sprite window escapes the parent event lifetime");
        }
        // Rates by kind (scale 1): 0.08 / 0.2 / 0.45.
        double major = Rate(3, n, 1f), exceptional = Rate(4, n, 1f), super = Rate(5, n, 1f);
        Require(Math.Abs(major - 0.08) < 0.008 && Math.Abs(exceptional - 0.2) < 0.01 && Math.Abs(super - 0.45) < 0.012, $"kind rates off: {major:0.000} {exceptional:0.000} {super:0.000}");
        // Type split 70 / 25 / 5.
        var counts = new int[4];
        Rate(5, n, 1f, counts);
        double total = counts[1] + counts[2] + counts[3];
        Require(Math.Abs(counts[1] / total - 0.70) < 0.02 && Math.Abs(counts[2] / total - 0.25) < 0.02 && Math.Abs(counts[3] / total - 0.05) < 0.01, "type split off");
        // The setting scales the chance: 0 none, 2 doubles.
        Require(Rate(4, 5000, 0f) == 0.0, "setting 0 still produced discharges");
        double doubled = Rate(4, n, 2f);
        Require(Math.Abs(doubled - 0.4) < 0.012, $"setting 2 did not double the rate: {doubled:0.000}");
        // Policy: reduced flashes scales the sprite chance (0.35) through the default path.
        LightningVisualPolicy.Apply(1f, 1f, true);
        double reducedRate = Rate(4, n, null);
        Require(Math.Abs(reducedRate - 0.2 * 0.35) < 0.01, $"reduced flashes did not scale sprites: {reducedRate:0.000}");
        UpperAtmosphereLightning.TryDerive(MakeEvent(1, 5), 6000f, out UpperAtmosphereSprite probe, 10f);
        Require(probe.Energy <= LightningVisualPolicy.ReducedPeakScale + 1e-4f, "reduced flashes did not cap sprite energy");
        // Reduced pulse: peak capped, per-frame luminance change capped at 60 and 144 fps; normal pulse is untouched when off.
        float peak = 0f, maxStep60 = 0f, maxStep144 = 0f;
        for (double age = 0; age < RingLightningEvent.LifetimeSeconds; age += 1.0 / 144.0)
        {
            peak = Math.Max(peak, RingLightningEvent.PulseAtAge(age));
            maxStep144 = Math.Max(maxStep144, Math.Abs(RingLightningEvent.PulseAtAge(age + 1.0 / 144.0) - RingLightningEvent.PulseAtAge(age)));
            maxStep60 = Math.Max(maxStep60, Math.Abs(RingLightningEvent.PulseAtAge(age + 1.0 / 60.0) - RingLightningEvent.PulseAtAge(age)));
        }
        Require(peak <= 0.4f + 1e-4f && maxStep60 <= 0.055f && maxStep144 <= 0.025f, $"reduced pulse not capped: peak {peak:0.000} step60 {maxStep60:0.000} step144 {maxStep144:0.000}");
        LightningVisualPolicy.Apply(1f, 1f, false);
        float normalPeak = 0f, normalStep = 0f;
        for (double age = 0; age < RingLightningEvent.LifetimeSeconds; age += 1.0 / 240.0)
        {
            normalPeak = Math.Max(normalPeak, RingLightningEvent.PulseAtAge(age));
            normalStep = Math.Max(normalStep, Math.Abs(RingLightningEvent.PulseAtAge(age + 1.0 / 60.0) - RingLightningEvent.PulseAtAge(age)));
        }
        Require(normalPeak > 0.95f && normalStep > 0.1f, "normal pulse changed when reduced flashes are off");
        // Air transmittance: the opposite arch is nearly clear, a long low path along the near arch is dimmed, and nothing exceeds 1 or the 0.8 cap.
        const double radius = 5_000_000.0, circumference = radius * Math.Tau;
        var eye = new RingCanonicalPosition(0.0, 1200.0, 160_000.0);
        float across = UpperAtmosphereLightning.PathTransmittance(eye, new RingCanonicalPosition(circumference * 0.5, 61_000.0, 160_000.0), radius, circumference);
        float near150 = UpperAtmosphereLightning.PathTransmittance(eye, new RingCanonicalPosition(150_000.0, 61_000.0, 160_000.0), radius, circumference);
        float near400 = UpperAtmosphereLightning.PathTransmittance(eye, new RingCanonicalPosition(400_000.0, 61_000.0, 160_000.0), radius, circumference);
        float adjacent = UpperAtmosphereLightning.PathTransmittance(eye, new RingCanonicalPosition(1_000.0, 61_000.0, 160_000.0), radius, circumference);
        Require(across > 0.85f && across <= 1.0f, $"opposite arch should be nearly clear: {across:0.000}");
        Require(adjacent > 0.9f && near150 < adjacent && near150 >= 0.2f - 1e-4f && near400 >= 0.2f - 1e-4f, $"transmittance out of range: adjacent {adjacent:0.000} 150km {near150:0.000} 400km {near400:0.000}");
        // Reduced flashes: summed overlapping flash levels are capped; untouched when off.
        Span<float> overlap = stackalloc float[] { 0.4f, 0.4f, 0.4f };
        LightningVisualPolicy.Apply(1f, 1f, true);
        LightningVisualPolicy.LimitSummed(overlap);
        Require(overlap[0] + overlap[1] + overlap[2] <= LightningVisualPolicy.ReducedSummedCap + 1e-4f, "reduced flashes did not cap summed overlap");
        Span<float> single = stackalloc float[] { 0.4f, 0.1f };
        Require(LightningVisualPolicy.LimitSummed(single) == 1.0f && single[0] == 0.4f, "cap changed an in-budget flash");
        LightningVisualPolicy.Apply(1f, 1f, false);
        Span<float> unreduced = stackalloc float[] { 0.9f, 0.9f };
        LightningVisualPolicy.LimitSummed(unreduced);
        Require(unreduced[0] == 0.9f, "cap applied while flashes are not reduced");
        // Policy sanitising.
        LightningVisualPolicy.Apply(float.NaN, 9f, false);
        Require(LightningVisualPolicy.FrequencyScale == 1f && LightningVisualPolicy.UpperAtmosphereScale == 2f, "policy sanitise");
    }
}
