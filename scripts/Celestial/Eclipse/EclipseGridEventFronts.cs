using System;
using Godot;
using RingworldTechDemo.Terrain.Sites;

namespace RingworldTechDemo.Celestial.Eclipse;

/// <summary>
/// The eclipse ignition fronts as grid shader lanes (docs/eclipse-false-sun-spec.md E.3, phase E4d): pure, a function of the timeline, the
/// published <see cref="EclipseEventState"/> and the grid pulse clock. Each of the six chains gets a forward front leaving the main spire toward
/// +p and a backward one toward -p (12 fronts); once they meet at the antipode the heads are absorbed (headless plateau spans) and a short
/// meeting-flare span per chain sits around p = C/2 (6 more), so at most 18 lanes are written.
/// </summary>
public static class EclipseGridEventFronts
{
    /// <summary>Two fronts plus one meeting-flare span per chain.</summary>
    public const int MaxPerColumn = 3;

    /// <summary>
    /// Writes the fronts for <paramref name="state"/> into <paramref name="destination"/> as (headQ, column, signed travelled, plateau) vectors
    /// and returns the count. <paramref name="weight"/> is the 0..1 event look (night forced, peak raised): 1 through the ignition, fading with the
    /// hand-off like the plateau. Nothing is written outside [Ignition, Sustain) or for an inactive event.
    /// </summary>
    public static int Build(EclipseEventTimeline? timeline, in EclipseEventState state, EmitterGridPulseClock clock, Span<Vector4> destination, out float weight)
    {
        weight = 0f;
        if (timeline == null || !timeline.Active || !state.Active) return 0;
        double tau = state.Tau;
        if (tau < timeline.TauIgnition || tau >= timeline.TauSustain) return 0;
        EclipseSiteTable sites = timeline.Sites;
        double c = sites.CircumferenceMeters, half = 0.5d * c;
        int columns = sites.Columns, count = 0;
        // Regular sites per chain + the main spire = links per chain; the meeting flare spans one link either side of the antipode.
        double flareHalfSpan = c / (sites.Count / Math.Max(1, columns) + 1);
        double forward = state.ForwardHeadMeters, travelled = forward;
        bool moving = forward < half;
        double plateau = state.PlateauLevel;
        weight = (float)Math.Clamp(state.RegularBeamIntensity, 0d, 1d);
        for (int col = 0; col < columns; col++)
        {
            if (travelled > 0d && (moving || plateau > 0d))
            {
                // The head is absorbed (headless) once it reaches the antipode; a plateau of 0 then leaves nothing to draw.
                if (count + 2 > destination.Length) break;
                destination[count++] = ToShader(clock.MakeEventFront(col, forward, travelled, towardIncreasingP: true, plateau, headless: !moving));
                destination[count++] = ToShader(clock.MakeEventFront(col, state.BackwardHeadMeters, travelled, towardIncreasingP: false, plateau, headless: !moving));
            }
            if (state.MeetingFlare > 0d && count < destination.Length)
            {
                // Headless span of +-one link around C/2 at the flare level (the plateau of the meeting links rises to 1 and falls back).
                // Head at C/2 + L with 2L covered behind it: [C/2 - L, C/2 + L] in either grid direction (the clock mirrors it for the retrograde grid).
                destination[count++] = ToShader(clock.MakeEventFront(col, half + flareHalfSpan, 2d * flareHalfSpan, towardIncreasingP: true, state.MeetingFlare, headless: true));
            }
        }
        return count;
    }

    private static Vector4 ToShader(in EmitterEventFront f) => new((float)f.HeadQ, f.Column, (float)f.SignedTravelled, (float)f.Plateau);

    /// <summary>The CPU twin lanes of <see cref="Build"/>: the same fronts as <see cref="EmitterEventFront"/> values (float-rounded like the shader's).</summary>
    public static int ToEventFronts(ReadOnlySpan<Vector4> lanes, Span<EmitterEventFront> destination)
    {
        int n = Math.Min(lanes.Length, destination.Length);
        for (int i = 0; i < n; i++) destination[i] = new EmitterEventFront(lanes[i].X, (int)lanes[i].Y, lanes[i].Z, lanes[i].W);
        return n;
    }
}
