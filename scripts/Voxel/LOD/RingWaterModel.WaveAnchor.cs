using Godot;
using System;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

// Water wave precision (W1, 2026-11-20). The analytic waves are seam-closed: every around-ring phase gradient is quantised to an integer number n of
// cycles per turn, so a train's around phase is 2 pi n u, with u the around fraction of the point. The shader used to build u from atan(world) * R in
// float32, which steps every ~1 m at 1e7 m round the ring (blocky cells in the reflections and the sun glitter). Here the CPU supplies, in double, the
// around fraction u_A of an anchor near the observer, split at 2^-20 into an integer K and a remainder r. The shader evaluates
// frac(n u_A) = frac(((n K) mod 2^20) 2^-20 + n r) exactly in integer arithmetic for the K part, and adds n times the small local angle of the pixel
// from the anchor (materials/ring_water_common.gdshaderinc, ring_water_anchor_phase).
public static partial class RingWaterModel
{
    // xyz: the anchor in the main view's world frame; w: its distance from the ring axis (m).
    private static readonly StringName WaveAnchorWorldName = "ring_water_wave_anchor_world";
    // xyz: the around tangent at the anchor (direction of increasing around angle); w: 1 when the anchor is valid, 0 keeps the legacy coordinate.
    private static readonly StringName WaveAnchorTangentName = "ring_water_wave_anchor_tangent";
    // x K, y r of the anchor's around fraction; zw the same for rivers, with the river advection folded in.
    private static readonly StringName WaveAnchorPhaseName = "ring_water_wave_anchor_phase";
    /// <summary>2^20: the split of the around fraction; matches the shader's 0xFFFFF mask.</summary>
    public const double AnchorFractionScale = 1048576.0;
    // The river profile scales the coordinate by 2.15 before adding the advection (ring_water_profile_*_filtered).
    private const double RiverCoordinateScale = 2.15;

    /// <summary>Debug A/B switch for perf tooling only (not a menu row): false publishes an invalid anchor, so the shader keeps the legacy absolute coordinate.</summary>
    public static bool WaveAnchorEnabled { get; set; } = true;
    private static Vector4 _anchorWorld, _anchorTangent, _anchorPhase;
    private static bool _anchorPublished;
    public static Vector4 PublishedWaveAnchorWorld => _anchorWorld;
    public static Vector4 PublishedWaveAnchorTangent => _anchorTangent;
    public static Vector4 PublishedWaveAnchorPhase => _anchorPhase;

    /// <summary>Splits an around fraction (any real; wrapped to [0, 1)) into K = floor(u 2^20) and r = u - K 2^-20, both exact in double.</summary>
    public static void SplitAroundFraction(double fraction, out double k, out double r)
    {
        if (!double.IsFinite(fraction)) { k = 0; r = 0; return; }
        double u = fraction - Math.Floor(fraction);
        double scaled = u * AnchorFractionScale;
        double whole = Math.Floor(scaled);
        if (whole >= AnchorFractionScale) { k = 0; r = 0; return; } // u rounded up to 1.0
        k = whole;
        r = (scaled - whole) / AnchorFractionScale;
    }

    /// <summary>
    /// The shader's reconstruction of frac(n u) from (K, r), in the same float32 arithmetic (headless reference for tests):
    /// fract(float((uint(int(n)) * uint(K)) &amp; 0xFFFFF) * 2^-20 + n * r).
    /// </summary>
    public static double ReconstructAnchorPhaseFraction(float n, float k, float r)
    {
        uint product = unchecked((uint)(int)n * (uint)k) & 0xFFFFFu;
        float sum = (float)product * (1.0f / 1048576.0f) + n * r;
        return sum - MathF.Floor(sum);
    }

    /// <summary>The river around-fraction offset of the advection: (clock * speed * 1.8) / (2.15 * 2 pi R), as the legacy advection term.</summary>
    public static double RiverAdvectionFraction(double clock, double waveSpeed, double materialRadius)
    {
        double circumference = Math.Tau * Math.Max(1.0, materialRadius);
        double advance = clock * waveSpeed * 1.8 / (RiverCoordinateScale * circumference);
        return double.IsFinite(advance) ? advance - Math.Floor(advance) : 0.0;
    }

    /// <summary>
    /// Publishes the wave anchor at the observer (vertical 0) once per frame and on every floating-origin change. Without a grid, an observer, a curved
    /// ring layout or the wind clock, the anchor is invalid and the shader keeps the legacy coordinate.
    /// </summary>
    public static void UpdateWaveAnchor(VoxelChunkGridManager? grid, RingCanonicalPosition? observer)
    {
        if (!WaveAnchorEnabled || !_windClockEnabled || grid == null || !GodotObject.IsInstanceValid(grid) || observer is not { } position
            || !grid.UseRingCurvedPlacement || !grid.UseHabitableRingAxisLayout)
        {
            InvalidateWaveAnchor();
            return;
        }
        RingLodGeometrySnapshot geometry = grid.CaptureLodGeometrySnapshot();
        double radius = Math.Max(1.0, geometry.RingRadiusMeters);
        double circumference = geometry.CircumferenceMeters > 0.0 ? geometry.CircumferenceMeters : Math.Tau * radius;
        double around = position.AroundMeters % circumference;
        if (around < 0.0) around += circumference;
        // The geometry places a canonical point at angle around / radius (RingLodGeometrySnapshot.CanonicalToAbsoluteLocal), which is the shader's
        // atan(radial.z, radial.x) for an identity grid basis.
        double angle = around / radius;
        Vector3 world = geometry.CanonicalToWorld(around, 0.0, position.AcrossMeters);
        double axisDistance = radius - geometry.RingVerticalBaseOffsetMeters;
        // Analytic tangent (a finite difference of two float world points would carry their rounding into every local coordinate).
        Vector3 tangent = (geometry.GridGlobalTransform.Basis * new Vector3((float)-Math.Sin(angle), 0.0f, (float)Math.Cos(angle))).Normalized();
        double fraction = angle / Math.Tau;
        SplitAroundFraction(fraction, out double k, out double r);
        double riverOffset = RiverAdvectionFraction(_windClock, WaveSpeed, grid.GetCanonicalRingRadiusMeters());
        SplitAroundFraction(fraction + riverOffset, out double riverK, out double riverR);
        PublishWaveAnchor(
            new Vector4(world.X, world.Y, world.Z, (float)axisDistance),
            new Vector4(tangent.X, tangent.Y, tangent.Z, 1.0f),
            new Vector4((float)k, (float)r, (float)riverK, (float)riverR));
    }

    public static void InvalidateWaveAnchor()
    {
        if (_anchorPublished && _anchorTangent.W == 0.0f) return;
        PublishWaveAnchor(_anchorWorld, new Vector4(_anchorTangent.X, _anchorTangent.Y, _anchorTangent.Z, 0.0f), _anchorPhase);
    }

    private static void PublishWaveAnchor(Vector4 world, Vector4 tangent, Vector4 phase)
    {
        if (!_anchorPublished || _anchorWorld != world) PublishWaterGlobal(WaveAnchorWorldName, world);
        if (!_anchorPublished || _anchorTangent != tangent) PublishWaterGlobal(WaveAnchorTangentName, tangent);
        if (!_anchorPublished || _anchorPhase != phase) PublishWaterGlobal(WaveAnchorPhaseName, phase);
        _anchorWorld = world;
        _anchorTangent = tangent;
        _anchorPhase = phase;
        _anchorPublished = true;
    }
}
