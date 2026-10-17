using System;
using Godot;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// Uniform values the hub beam shaders receive (materials/ring_eclipse_hub_beam_math.gdshaderinc). <see cref="Time"/> is the event time tau
/// (seconds from the window start); the other fields are constant for one timeline.
/// </summary>
/// <param name="Time">ev_time.</param>
/// <param name="Marks">ev_marks: handoff start tau, handoff fade seconds, cut tau, retract seconds.</param>
/// <param name="Climb">ev_climb: main climb seconds, main exponent, regular climb seconds, regular exponent.</param>
/// <param name="Level">ev_level: global brightness gain (1 until the collapse stage drives it).</param>
/// <param name="SoftTip">Soft tip length as a fraction of the beam (ev_shape.x).</param>
/// <param name="DayVisibility">Factor applied while the hub day is above 0.5 (ev_shape.y).</param>
/// <param name="Flash">ev_flash (phase E4d): refuel flash climb, hold and fade seconds, and the grid front speed in m/s.</param>
/// <param name="Refuel">ev_refuel: the Sustain start tau and the Instability start tau; only a pass inside that range flashes.</param>
public readonly record struct EclipseHubBeamUniforms(float Time, Vector4 Marks, Vector4 Climb, float Level, float SoftTip, float DayVisibility,
    Vector4 Flash = default, Vector2 Refuel = default);

/// <summary>
/// CPU twin of materials/ring_eclipse_hub_beam_math.gdshaderinc (docs/eclipse-false-sun-spec.md E.2): the per-beam tip, handoff, soft-tip
/// mask, day visibility and level, all pure functions of the uniforms and the per-beam custom data (fire tau, kind). The GPU parity test
/// (tools/eclipse_hub_beam_shader_parity_test.gd) compares the shader with these. Float arithmetic on purpose, like the shader.
/// </summary>
public static class EclipseHubBeamMath
{
    /// <summary>Fire time written for a dead site: the beam never lights.</summary>
    public const float NeverFireTau = 1e9f;
    /// <summary>Climb exponent of the regular beams (the main beam uses <c>MainClimbExponent</c>): a linear tip.</summary>
    public const float RegularClimbExponent = 1f;
    /// <summary>The hub day factor above which <see cref="DayVisibility"/> applies, smoothed over this half width so the factor does not pop while the level ramps.</summary>
    public const float DayVisibilityEdge = 0.5f, DayVisibilityHalfWidth = 0.05f;

    public static float Smooth01(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        return x * x * (3f - 2f * x);
    }

    /// <summary>GLSL smoothstep (edge0 &lt; edge1).</summary>
    public static float SmoothStep(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>Uniforms for a timeline at event time <paramref name="tau"/> (E4b: the global level is 1).</summary>
    public static EclipseHubBeamUniforms FromTimeline(EclipseEventTimeline timeline, EclipseBeamSettings beams, double tau, float level = 1f)
        => new((float)tau,
            new Vector4((float)timeline.TauHandoff, (float)timeline.HandoffFade, (float)timeline.TauImplosion, (float)timeline.BeamRetract),
            new Vector4((float)timeline.MainClimb, (float)timeline.Settings.MainClimbExponent, (float)timeline.RegularClimb, RegularClimbExponent),
            level, (float)beams.SoftTipFraction, (float)beams.DayVisibility,
            new Vector4((float)timeline.Settings.FlashClimbSeconds, (float)timeline.Settings.FlashHoldSeconds, (float)timeline.Settings.FlashFadeSeconds,
                (float)timeline.GridClock.SpeedMetersPerSecond),
            new Vector2((float)timeline.TauSustain, (float)timeline.TauInstability));

    /// <summary>
    /// Refuel flash of a regular beam (ehb_refuel): the flash envelope 0..1 (the same shape as <see cref="EclipseEventTimeline.RefuelFlash"/>) and the
    /// fraction of the beam the bolt has climbed (age / climb). <paramref name="fronts"/> are the grid's normal pulse fronts as shader lanes
    /// (head q, column, 0, 0); <paramref name="direction"/> and <paramref name="circumference"/> are <c>eg_direction</c> and <c>eg_circumference</c>.
    /// (0, 1) when the beam does not flash.
    /// </summary>
    public static (float Envelope, float Tip) RefuelFlash(in EclipseHubBeamUniforms u, ReadOnlySpan<Vector4> fronts, float direction, float circumference,
        float fireTau, float kind, float pSite, float column)
    {
        if (kind > 0.5f || fireTau >= 1e8f || fronts.Length == 0) return (0f, 1f);
        float q = direction > 0f ? pSite : circumference - pSite;
        float age = 1e9f;
        for (int i = 0; i < fronts.Length; i++)
            if (MathF.Abs(fronts[i].Y - column) < 0.5f && fronts[i].X >= q) age = MathF.Min(age, (fronts[i].X - q) / MathF.Max(u.Flash.W, 1f));
        float passTau = u.Time - age;
        if (age >= 1e8f || passTau < u.Refuel.X || passTau >= u.Refuel.Y) return (0f, 1f);
        float climb = MathF.Max(u.Flash.X, 1e-4f);
        float envelope;
        if (age < climb) envelope = Smooth01(age / climb);
        else if (age < climb + u.Flash.Y) envelope = 1f;
        else envelope = 1f - Smooth01((age - climb - u.Flash.Y) / MathF.Max(u.Flash.Z, 1e-4f));
        return (envelope, Math.Clamp(age / climb, 0f, 1f));
    }

    /// <summary><see cref="IsLit"/> with a refuel flash: a sub-segment starting at <paramref name="fA"/> is also lit below the bolt's tip (ehb_lit_refuel).</summary>
    public static bool IsLitRefuel(in EclipseHubBeamUniforms u, float fireTau, float kind, float fA, (float Envelope, float Tip) flash)
    {
        bool bolt = flash.Envelope > 0f && u.Level > 0f && (flash.Tip >= 1f || fA < flash.Tip + Math.Max(u.SoftTip, 1e-6f));
        return bolt || IsLit(u, fireTau, kind, fA);
    }

    /// <summary><see cref="Level"/> with a refuel flash: the brighter of the beam's own state and the bolt (ehb_level_refuel).</summary>
    public static float LevelRefuel(in EclipseHubBeamUniforms u, float fireTau, float kind, float f, float hubDay, float atmosphereVisibility, float layerWeight,
        (float Envelope, float Tip) flash)
    {
        float own = Handoff(u, kind) * TipMask(u, f, Tip(u, fireTau, kind));
        float bolt = flash.Envelope * TipMask(u, f, flash.Tip);
        return MathF.Max(own, bolt) * DayVisibility(u, hubDay) * u.Level * atmosphereVisibility * layerWeight;
    }

    /// <summary>Climb tip fraction 0..1 of a beam fired at <paramref name="fireTau"/> (ehb_climb_tip). Kind 1 is the main spire.</summary>
    public static float ClimbTip(in EclipseHubBeamUniforms u, float fireTau, float kind)
    {
        bool main = kind > 0.5f;
        float climb = Math.Max(main ? u.Climb.X : u.Climb.Z, 1e-4f);
        float x = Math.Clamp((u.Time - fireTau) / climb, 0f, 1f);
        return MathF.Pow(x, main ? u.Climb.Y : u.Climb.W);
    }

    /// <summary>Top of the part of the beam still lit by the collapse retract: 1 until the cut, then falling to 0 (ehb_cut_top).</summary>
    public static float CutTop(in EclipseHubBeamUniforms u)
    {
        if (u.Time < u.Marks.Z) return 1f;
        return 1f - Math.Clamp((u.Time - u.Marks.Z) / Math.Max(u.Marks.W, 1e-4f), 0f, 1f);
    }

    public static float Tip(in EclipseHubBeamUniforms u, float fireTau, float kind) => Math.Min(ClimbTip(u, fireTau, kind), CutTop(u));

    /// <summary>Regular beams fade out over the handoff; the main beam stays (ehb_handoff).</summary>
    public static float Handoff(in EclipseHubBeamUniforms u, float kind)
    {
        if (kind > 0.5f) return 1f;
        return 1f - Smooth01((u.Time - u.Marks.X) / Math.Max(u.Marks.Y, 1e-4f));
    }

    /// <summary>1 below the tip, soft fall to 0 over the soft tip length above it; the full beam (tip 1) is never cut (ehb_tip_mask).</summary>
    public static float TipMask(in EclipseHubBeamUniforms u, float f, float tip)
    {
        if (tip >= 1f) return 1f;
        float soft = Math.Max(u.SoftTip, 1e-6f);
        return 1f - SmoothStep(tip - soft, tip, f);
    }

    /// <summary>1 while the hub day is low, DayVisibility once it is above 0.5 (smoothed over 0.45..0.55) (ehb_day_visibility).</summary>
    public static float DayVisibility(in EclipseHubBeamUniforms u, float hubDay)
    {
        float t = SmoothStep(DayVisibilityEdge - DayVisibilityHalfWidth, DayVisibilityEdge + DayVisibilityHalfWidth, Math.Clamp(hubDay, 0f, 1f));
        return 1f + (u.DayVisibility - 1f) * t;
    }

    /// <summary>True when any part of a sub-segment starting at <paramref name="fA"/> can be lit (the vertex collapse rule; ehb_lit).</summary>
    public static bool IsLit(in EclipseHubBeamUniforms u, float fireTau, float kind, float fA)
    {
        float tip = Tip(u, fireTau, kind);
        return tip > 0f && Handoff(u, kind) > 0f && u.Level > 0f && (tip >= 1f || fA < tip + Math.Max(u.SoftTip, 1e-6f));
    }

    /// <summary>
    /// Beam level at chord fraction <paramref name="f"/> before the emission, the cross profile and the colour (ehb_level):
    /// handoff x tip mask x day visibility x level x atmosphere visibility x layer weight. The beams ignore the background B and the day
    /// factor by design (spec E.2): they are the event.
    /// </summary>
    public static float Level(in EclipseHubBeamUniforms u, float fireTau, float kind, float f, float hubDay, float atmosphereVisibility, float layerWeight)
        => Handoff(u, kind) * TipMask(u, f, Tip(u, fireTau, kind)) * DayVisibility(u, hubDay) * u.Level * atmosphereVisibility * layerWeight;

    // ---------------------------------------------------------------- in-cloud diffusion (phase E4e, spec E.6)

    /// <summary>Half width (metres) of the smooth deck edges (ehb_cloud_mask).</summary>
    public const float CloudEdgeMeters = 400f;

    /// <summary>1 inside the cloud deck at <paramref name="height"/> metres above the crown, smooth over <see cref="CloudEdgeMeters"/> (ehb_cloud_mask).</summary>
    public static float CloudMask(float height, float deckLow, float deckHigh)
        => SmoothStep(deckLow - CloudEdgeMeters, deckLow + CloudEdgeMeters, height) * (1f - SmoothStep(deckHigh - CloudEdgeMeters, deckHigh + CloudEdgeMeters, height));

    /// <summary>
    /// In-cloud factor k x mask at <paramref name="height"/> metres above the crown for a beam whose COLOR is (k, deck low, deck high, flag); 0 for an unqueried
    /// beam (flag 0) or with the feature off (ehb_cloud_factor).
    /// </summary>
    public static float CloudFactor(Color cloud, float height, bool enabled = true)
    {
        if (cloud.A < 0.5f || cloud.R <= 0f || !enabled) return 0f;
        return cloud.R * CloudMask(height, cloud.G, cloud.B);
    }

    /// <summary>Depth-core keep factor: 1 outside dense cloud, 0 once the in-cloud factor passes <paramref name="coreCut"/> (smooth over +-0.1; ehb_cloud_core_keep).</summary>
    public static float CloudCoreKeep(float factor, float coreCut) => 1f - SmoothStep(coreCut - 0.1f, coreCut + 0.1f, factor);

    /// <summary>MultiMesh COLOR of one beam: (k, deck base above the crown, deck top above the crown, 1 = queried). k = 0 and the flag 0 for an unqueried beam.</summary>
    public static Color EncodeCloud(double coverage, double deckBaseAltitude, double deckTopAltitude, double crownAltitude)
        => new((float)Math.Clamp(coverage, 0d, 1d), (float)(deckBaseAltitude - crownAltitude), (float)(deckTopAltitude - crownAltitude), 1f);

    /// <summary>MultiMesh custom data of one beam: (p_site metres, column, fire tau, kind 0 regular / 1 main).</summary>
    public static Color EncodeCustom(double pSiteMeters, int column, double fireTau, bool main)
        => new((float)pSiteMeters, column, double.IsFinite(fireTau) ? (float)fireTau : NeverFireTau, main ? 1f : 0f);
}
