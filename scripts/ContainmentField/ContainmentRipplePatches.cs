using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Lightning;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.ContainmentField;

// Pure C# support for rendering the ripple patches (docs/containment-field-design.md 3.5 and 3.10.2): render-state evaluation, patch grids, near / far selection. No scene tree.

/// <summary>
/// Per-frame shader inputs of one ripple patch. Wave = (A x strength, front radius m, Rmax m, flash); Crawl = (Rc m or 0, crawl front m, seed 0..65535, rim bias -1..1);
/// StrikeLevel = E x flash x strength x tail. <c>default</c> is invisible with every component 0.
/// </summary>
public readonly record struct ContainmentRippleRenderState(bool Visible, Vector4 Wave, Vector4 Crawl, float StrikeLevel);

public static class ContainmentRipplePatches
{
    /// <summary>Quads per side of a ripple patch grid.</summary>
    public const int Quads = 32;
    /// <summary>Ripple patches drawn on the near patch at once.</summary>
    public const int MaxNearRipples = 4;
    public const double CrawlTimeConstantSeconds = 0.12, ReducedCrawlTimeConstantSeconds = 0.24;
    /// <summary>Megastorm contact crawl radius: 500 m + 1000 m x E.</summary>
    public const float ContactCrawlBaseMeters = 500f, ContactCrawlSpanMeters = 1000f;
    /// <summary>The strike level fades over the last tenth of the ripple's life so it reaches 0 exactly at T.</summary>
    public const double TailFraction = 0.1;
    /// <summary>Patch radius over max(Rmax, Rc); must equal CF_RIPPLE_PATCH in ring_containment_field_core.gdshaderinc.</summary>
    public const float PatchFactor = 1.15f;
    /// <summary>Far quad fade band as fractions of the camera far plane.</summary>
    public const double FarFadeStartFraction = 0.9, FarFadeEndFraction = 0.98;

    /// <summary>Crawl radius Rc in metres: the strike's own, 500 + 1000 E for a megastorm contact, 0 for every other kind.</summary>
    public static float CrawlRadius(ContainmentRipple r) => r.Kind switch
    {
        ContainmentDisturbanceKind.FieldStrike => r.CrawlRadiusMeters,
        ContainmentDisturbanceKind.MegastormContact => ContactCrawlBaseMeters + ContactCrawlSpanMeters * r.Energy,
        _ => 0f,
    };

    /// <summary>Radius of the patch mesh: 1.15 x max(Rmax, Rc).</summary>
    public static float RenderPatchRadius(ContainmentRipple r) => PatchFactor * Math.Max(r.RadiusMeters, CrawlRadius(r));

    /// <summary>
    /// The shader inputs of <paramref name="r"/> at <paramref name="now"/>. <paramref name="cappedFlash"/> is the ripple's flash after <see cref="CapFlashes"/>. Exactly
    /// <c>default</c> at tau &lt;= 0, at tau &gt;= T and with ripple strength 0; A and the strike level are continuous and reach 0 at T whatever the flash does.
    /// </summary>
    public static ContainmentRippleRenderState Evaluate(ContainmentRipple r, double now, float cappedFlash, float rippleStrength, bool reducedFlashes)
    {
        double tau = now - r.StartTimeSeconds;
        double duration = r.DurationSeconds;
        if (!(tau > 0.0) || !(tau < duration) || !(rippleStrength > 0.0f) || !double.IsFinite(tau) || !float.IsFinite(rippleStrength)) return default;
        float a = r.Amplitude(now) * rippleStrength, rho = r.FrontRadius(now);
        float flash = float.IsFinite(cappedFlash) ? Math.Clamp(cappedFlash, 0.0f, 1.0f) : 0.0f;
        float rc = CrawlRadius(r), strikeLevel = 0.0f;
        var crawl = Vector4.Zero;
        if (rc > 0.0f)
        {
            double tc = reducedFlashes ? ReducedCrawlTimeConstantSeconds : CrawlTimeConstantSeconds;
            float front = (float)(rc * (1.0 - Math.Exp(-tau / tc)));
            double x = Math.Clamp((1.0 - tau / duration) / TailFraction, 0.0, 1.0), tail = x * x * (3.0 - 2.0 * x);
            strikeLevel = (float)(Math.Clamp(r.Energy, 0.0f, 1.0f) * flash * rippleStrength * tail);
            long seed = r.Kind == ContainmentDisturbanceKind.FieldStrike ? (r.CrawlSeed & 0xFFFF) : (MegastormFieldCoupling.Mix64(r.SourceId, 0, 0, 0xC0A7) & 0xFFFF);
            double unit = (double)((ulong)MegastormFieldCoupling.Mix64(seed, 0, 0, 0xB1A5) >> 11) / (double)(1UL << 53);
            float bias = (float)((r.Surface.Phi < Math.PI * 0.5 ? -1.0 : 1.0) * (0.4 + 0.6 * unit));
            crawl = new Vector4(rc, front, seed, bias);
        }
        var wave = new Vector4(a, rho, r.RadiusMeters, flash);
        if (!float.IsFinite(a) || !float.IsFinite(rho) || !float.IsFinite(r.RadiusMeters) || !float.IsFinite(strikeLevel)
            || !float.IsFinite(crawl.X) || !float.IsFinite(crawl.Y) || !float.IsFinite(crawl.W)) return default;
        return new ContainmentRippleRenderState(a > 0.0f || strikeLevel > 0.0f, wave, crawl, strikeLevel);
    }

    /// <summary>
    /// The shader inputs of a far-list strike (> 350 km; drawn by the far strike layer only): no ring (Wave = (0, 0, Rc, 0)), the crawl and the contact glow at the strike level E x flash x strength x tail
    /// (tail over the last tenth of <see cref="ContainmentFarStrike.LifeSeconds"/>, like <see cref="Evaluate"/>). <c>default</c> outside (0, LifeSeconds), without a crawl radius or with strength 0.
    /// </summary>
    public static ContainmentRippleRenderState EvaluateFar(ContainmentFarStrike f, double now, float cappedFlash, float strength, bool reducedFlashes)
    {
        double tau = now - f.StartTimeSeconds, life = f.LifeSeconds;
        float rc = f.CrawlRadiusMeters;
        if (!(tau > 0.0) || !(tau < life) || !(strength > 0.0f) || !double.IsFinite(tau) || !float.IsFinite(strength) || !(rc > 0.0f) || !float.IsFinite(rc)) return default;
        float flash = float.IsFinite(cappedFlash) ? Math.Clamp(cappedFlash, 0.0f, 1.0f) : 0.0f;
        double tc = reducedFlashes ? ReducedCrawlTimeConstantSeconds : CrawlTimeConstantSeconds;
        float front = (float)(rc * (1.0 - Math.Exp(-tau / tc)));
        double x = Math.Clamp((1.0 - tau / life) / TailFraction, 0.0, 1.0), tail = x * x * (3.0 - 2.0 * x);
        float strikeLevel = (float)(Math.Clamp(f.Energy, 0.0f, 1.0f) * flash * strength * tail);
        long seed = MegastormFieldCoupling.Mix64(f.SourceId, 0, 0, 0xC0A7) & 0xFFFF;
        double unit = (double)((ulong)MegastormFieldCoupling.Mix64(seed, 0, 0, 0xB1A5) >> 11) / (double)(1UL << 53);
        float bias = (float)((f.Surface.Phi < Math.PI * 0.5 ? -1.0 : 1.0) * (0.4 + 0.6 * unit));
        if (!float.IsFinite(front) || !float.IsFinite(strikeLevel)) return default;
        return new ContainmentRippleRenderState(strikeLevel > 0.0f, new Vector4(0f, 0f, rc, 0f), new Vector4(rc, front, seed, bias), strikeLevel);
    }

    /// <summary>Straight-line distance between two canonical positions through the ring cross-section (around wrapped), the chord <c>PathTransmittance</c> samples.</summary>
    public static double ChordDistance(in RingCanonicalPosition a, in RingCanonicalPosition b, double ringRadiusMeters, double circumferenceMeters)
    {
        if (ringRadiusMeters <= 1.0 || circumferenceMeters <= 1.0) return 0.0;
        double angleA = a.AroundMeters / ringRadiusMeters;
        double around = WrapSigned(a.AroundMeters - b.AroundMeters, circumferenceMeters);
        double angleB = angleA - around / ringRadiusMeters;
        double radiusA = ringRadiusMeters - a.VerticalMeters, radiusB = ringRadiusMeters - b.VerticalMeters;
        double dx = radiusB * Math.Cos(angleB) - radiusA * Math.Cos(angleA), dy = radiusB * Math.Sin(angleB) - radiusA * Math.Sin(angleA), dz = b.AcrossMeters - a.AcrossMeters;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>Caps the summed flashes in place (reduced flashes); pass every live ripple's FlashAt and the far strikes' FlashAt together.</summary>
    public static void CapFlashes(Span<float> flashes) => LightningVisualPolicy.LimitSummed(flashes);

    /// <summary>The ripple's own patch grid: window +-<see cref="RenderPatchRadius"/> around its surface point, <see cref="Quads"/> quads per side.</summary>
    public static ContainmentNearPatchMesh BuildPatch(in ContainmentFieldProfile profile, in RingLodGeometrySnapshot snapshot, ContainmentRipple r)
        => ContainmentFieldNearPatch.BuildGrid(profile, snapshot, r.Surface, RenderPatchRadius(r), Quads);

    /// <summary>
    /// The ripple centre in <paramref name="patch"/>'s UV frame: (around UV, arch metres, surface metres per around UV unit at the ripple's height, 0). Double maths, cast at the end.
    /// </summary>
    public static Vector4 CentreIn(ContainmentNearPatchMesh patch, ContainmentRipple r, in RingLodGeometrySnapshot snapshot, double circumference)
    {
        double dAround = WrapSigned(r.Surface.AroundMeters - patch.Anchor.AroundMeters, circumference);
        double radial = snapshot.RingRadiusMeters - (snapshot.RingVerticalBaseOffsetMeters + r.Surface.VerticalMeters);
        return new Vector4(
            (float)(dAround * patch.NoiseAroundScale),
            (float)(r.Surface.ArchLengthMeters - patch.Anchor.ArchLengthMeters),
            (float)(radial / (snapshot.RingRadiusMeters * patch.NoiseAroundScale)),
            0f);
    }

    /// <summary>
    /// Slot indices (at most min(output.Length, <see cref="MaxNearRipples"/>)) of visible ripples whose render patch disc overlaps the near window, nearest to the near anchor
    /// first (surface metres). Conservative: the window is its around half-span in surface metres and its arch range, grown by the ripple's patch radius. Returns the count.
    /// </summary>
    public static int SelectNear(ContainmentNearPatchMesh near, in RingLodGeometrySnapshot snapshot, IReadOnlyList<ContainmentRipple?> slots, ReadOnlySpan<bool> visible, Span<int> output)
    {
        int cap = Math.Min(output.Length, MaxNearRipples), count = 0;
        if (cap <= 0) return 0;
        double circ = snapshot.CircumferenceMeters, r0 = snapshot.RingRadiusMeters;
        double halfSurface = near.AroundHalfSpan * (r0 - (snapshot.RingVerticalBaseOffsetMeters + near.Anchor.VerticalMeters)) / r0;
        Span<double> distances = stackalloc double[MaxNearRipples];
        int n = Math.Min(slots.Count, visible.Length);
        for (int i = 0; i < n; i++)
        {
            ContainmentRipple? rip = slots[i];
            if (rip == null || !visible[i]) continue;
            double p = RenderPatchRadius(rip);
            double scale = (r0 - (snapshot.RingVerticalBaseOffsetMeters + rip.Surface.VerticalMeters)) / r0;
            double dx = WrapSigned(rip.Surface.AroundMeters - near.Anchor.AroundMeters, circ) * scale;
            double s = rip.Surface.ArchLengthMeters;
            if (Math.Abs(dx) > halfSurface + p || s < near.ArchStart - p || s > near.ArchEnd + p) continue;
            double dy = s - near.Anchor.ArchLengthMeters, d = Math.Sqrt(dx * dx + dy * dy);
            int at = count;   // insertion by distance, ascending
            if (count == cap)
            {
                if (d >= distances[cap - 1]) continue;
                at = cap - 1;
            }
            else count++;
            while (at > 0 && distances[at - 1] > d) { distances[at] = distances[at - 1]; output[at] = output[at - 1]; at--; }
            distances[at] = d;
            output[at] = i;
        }
        return count;
    }

    /// <summary>A far quad stands in for a patch that reaches past the start of the far fade band and has something to show.</summary>
    public static bool NeedsFarQuad(double centreDistanceMeters, float patchRadiusMeters, double fadeStartMeters, in ContainmentRippleRenderState s)
        => s.Visible && (s.Wave.X * s.Wave.W > 0.0f || s.StrikeLevel > 0.0f) && centreDistanceMeters + patchRadiusMeters > fadeStartMeters;

    /// <summary>The far fade band (start, end) = (0.9, 0.98) x the camera far plane.</summary>
    public static (double Start, double End) FarFade(double cameraFarMeters)
        => (FarFadeStartFraction * cameraFarMeters, FarFadeEndFraction * cameraFarMeters);

    /// <summary>True when any pool slot would draw (flash uncapped) or a far strike is lit.</summary>
    public static bool AnyVisible(ContainmentDisturbanceBus? bus, double now, float strength)
    {
        if (bus == null) return false;
        IReadOnlyList<ContainmentRipple?> slots = bus.Slots;
        for (int i = 0; i < slots.Count; i++)
        {
            ContainmentRipple? r = slots[i];
            if (r != null && Evaluate(r, now, r.FlashAt(now), strength, false).Visible) return true;
        }
        IReadOnlyList<ContainmentFarStrike> far = bus.FarStrikes;
        for (int i = 0; i < far.Count; i++)
            if (far[i].FlashAt(now) > 0.0f) return true;
        return false;
    }

    private static double WrapSigned(double d, double m)
    {
        d %= m;
        if (d > m * 0.5) d -= m; else if (d < -m * 0.5) d += m;
        return d;
    }
}
