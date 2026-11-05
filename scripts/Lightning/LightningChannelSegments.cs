using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Lightning;

/// <summary>One world-space piece of a megastorm bolt channel; widths are relative (0..1, from LightningPoint.Width), Intensity is a static per-segment weight. AlongA/AlongB (scythe cores only, else 0) are cumulative length fractions 0..1 along the whole core, so the GPU cuts the line at the travelling tip.</summary>
public readonly record struct ChannelSegment(Vector3 A, Vector3 B, float WidthA, float WidthB, float Intensity, int Bolt, float AlongA = 0.0f, float AlongB = 0.0f);

/// <summary>
/// Pure selection and segment building for the megastorm lightning channel overlay (scripts/render/RingLightningChannelComposite.gd).
/// No Node, no per-frame allocation beyond the caller's reused lists.
/// </summary>
public static class LightningChannelSegments
{
    public const int MegastormKind = 6;
    public const int SubdivisionLevels = 3;
    public const float DisplacementFraction = 0.18f;

    public static int SelectMegastormEvents(
        IReadOnlyList<RingLightningEvent> active, double now, Func<RingCanonicalPosition, double> distanceToObserver,
        double maxDistanceMeters, int maxBolts, List<RingLightningEvent> output) =>
        SelectEvents(active, now, distanceToObserver, maxDistanceMeters, maxBolts, output, natural: false, append: false);

    /// <summary>Natural cloud-to-ground strikes: every event that is neither a megastorm (kind 6) nor a field strike, nearest first. Appends after the existing entries of <paramref name="output"/>.</summary>
    public static int SelectNaturalEvents(
        IReadOnlyList<RingLightningEvent> active, double now, Func<RingCanonicalPosition, double> distanceToObserver,
        double maxDistanceMeters, int maxBolts, List<RingLightningEvent> output) =>
        SelectEvents(active, now, distanceToObserver, maxDistanceMeters, maxBolts, output, natural: true, append: true);

    private static int SelectEvents(
        IReadOnlyList<RingLightningEvent> active, double now, Func<RingCanonicalPosition, double> distanceToObserver,
        double maxDistanceMeters, int maxBolts, List<RingLightningEvent> output, bool natural, bool append)
    {
        if (!append) output.Clear();
        int first = append ? output.Count : 0;
        if (active == null || maxBolts <= 0) return 0;
        for (int i = 0; i < active.Count; i++)
        {
            RingLightningEvent e = active[i];
            if (e.FieldStrike || (e.SystemKind == MegastormKind) == natural) continue;   // field strikes: RingFieldStrikeLayer.SelectMain
            double age = now - e.StartTimeSeconds;
            if (age < 0.0 || age > RingLightningEvent.LifetimeSeconds) continue;
            double d = distanceToObserver(e.GroundPosition);
            if (!(d <= maxDistanceMeters)) continue;
            output.Add(e);
        }
        // Insertion sort by (distance, EventId): the list is tiny and this allocates nothing.
        var key = distanceToObserver;
        for (int i = first + 1; i < output.Count; i++)
        {
            RingLightningEvent item = output[i];
            double di = key(item.GroundPosition);
            int j = i - 1;
            while (j >= first)
            {
                double dj = key(output[j].GroundPosition);
                if (dj < di || (dj == di && output[j].EventId < item.EventId)) break;
                output[j + 1] = output[j];
                j--;
            }
            output[j + 1] = item;
        }
        if (output.Count - first > maxBolts) output.RemoveRange(first + maxBolts, output.Count - first - maxBolts);
        return output.Count - first;
    }

    /// <summary>Primary fork count for a natural channel of this length (3 for a 1 km channel, up to the megastorm's 9 at 8 km and more).</summary>
    public static int NaturalBranchBudget(float heightMeters) => Math.Clamp((int)Math.Round(2.0f + heightMeters / 1200.0f), 3, 9);

    public static float Pulse(RingLightningEvent e, double now) =>
        RingLightningEvent.PulseAtAge(now - e.StartTimeSeconds) * Math.Max(0.0f, e.Energy);

    /// <summary>Megastorm scythe core: the WHOLE polyline, no subdivision, intensity 1. AlongA/AlongB are the cumulative length fractions; the shader cuts at the tip and boosts the head.</summary>
    public static void BuildScytheCore(ReadOnlySpan<Vector3> points, float widthRel, int boltIndex, List<ChannelSegment> output)
    {
        if (points.Length < 2) return;
        float total = 0.0f;
        for (int i = 1; i < points.Length; i++) total += points[i - 1].DistanceTo(points[i]);
        if (!(total > 0.0f)) return;
        float walked = 0.0f;
        for (int i = 1; i < points.Length; i++)
        {
            float length = points[i - 1].DistanceTo(points[i]);
            if (length <= 1.0e-4f) continue;
            output.Add(new ChannelSegment(points[i - 1], points[i], widthRel, widthRel, 1.0f, boltIndex, walked / total, (walked + length) / total));
            walked += length;
        }
    }

    // ---- Megastorm channel shape (replaces ProceduralLightningGenerator for the overlay) ----
    public const int MaxBoltSegments = 1024;
    private const float Tau = 6.2831853f;

    private static float Rnd(int seed, int stream, int index) => Hash01(seed, stream, index, 11);

    private static Vector3 RandomPerpendicular(Vector3 dir, int seed, int stream, int index)
    {
        float z = Rnd(seed, stream, index * 2) * 2.0f - 1.0f, phi = Rnd(seed, stream, index * 2 + 1) * Tau;
        float r = Mathf.Sqrt(Math.Max(0.0f, 1.0f - z * z));
        Vector3 v = new(r * Mathf.Cos(phi), z, r * Mathf.Sin(phi));
        Vector3 perp = v - dir * dir.Dot(v);
        if (perp.LengthSquared() < 1.0e-6f) perp = dir.Cross(Math.Abs(dir.Y) < 0.9f ? Vector3.Up : Vector3.Right);
        return perp.Normalized();
    }

    // Recursive midpoint displacement of the chord a-b into 2^levels segments (points ends with 2^levels + 1 entries). Each split offsets the midpoint by
    // segLength x rough x m along a random direction perpendicular to the segment; m = h1 + h2 - 0.5. pinFirst keeps the vertex next to a on the chord so a branch leaves along its heading.
    private static void Displace(Vector3 a, Vector3 b, int levels, float rough, int seed, int stream, bool pinFirst, List<Vector3> points)
    {
        points.Clear();
        points.Add(a); points.Add(b);
        for (int level = 0; level < levels; level++)
        {
            int count = points.Count;
            for (int i = 0; i < count - 1; i++) points.Add(default);   // grow to 2 * count - 1; filled back to front below
            for (int i = count - 2; i >= 0; i--)
            {
                Vector3 p = points[i], q = points[i + 1];
                Vector3 d = q - p;
                float len = d.Length();
                Vector3 mid = (p + q) * 0.5f;
                if (len > 1.0e-4f)
                {
                    int index = level * 4096 + i;
                    float m = Rnd(seed, stream + 1, index * 3) + Rnd(seed, stream + 2, index * 3 + 1) - 0.5f;
                    // Roughness decays with level (0.8 per level): big-scale tortuosity, finer kinks smaller, so the channel reads as a jagged walk
                    // rather than a fuzzy fractal.
                    mid += RandomPerpendicular(d / len, seed, stream + 3, index) * (len * rough * MathF.Pow(0.8f, level) * m);
                }
                points[2 * i + 2] = q;
                points[2 * i + 1] = mid;
                points[2 * i] = p;
            }
        }
        if (pinFirst && points.Count > 2) points[1] = a.Lerp(b, 1.0f / (points.Count - 1));
    }

    private static Vector3 RotateAbout(Vector3 v, Vector3 unitAxis, float angle) =>
        v * Mathf.Cos(angle) + unitAxis.Cross(v) * Mathf.Sin(angle) + unitAxis * unitAxis.Dot(v) * (1.0f - Mathf.Cos(angle));

    private static void EmitPolyline(List<Vector3> pts, float widthStart, float widthEnd, float intensityStart, float intensityEnd, float alongA, float alongB, int slot, List<ChannelSegment> output)
    {
        int n = pts.Count - 1;
        for (int i = 0; i < n; i++)
        {
            float f0 = (float)i / n, f1 = (float)(i + 1) / n, fm = (f0 + f1) * 0.5f;
            output.Add(new ChannelSegment(pts[i], pts[i + 1], Mathf.Lerp(widthStart, widthEnd, f0), Mathf.Lerp(widthStart, widthEnd, f1), Mathf.Lerp(intensityStart, intensityEnd, fm), slot, alongA, alongB));
        }
    }

    /// <summary>
    /// Builds one megastorm bolt from the anvil <paramref name="top"/> to <paramref name="ground"/>: a slanted, jagged main channel, primary and secondary branches and an optional
    /// near-horizontal in-cloud run. Deterministic in seed: all randomness is a hash of (seed, stream, index). Appends at most <see cref="MaxBoltSegments"/> segments (main first) and returns the count.
    /// AlongA is a per-branch hash (0 for the main channel), AlongB is 1 for branch / run segments and 0 for the main channel.
    /// </summary>
    public static int BuildMegastormChannel(Vector3 top, Vector3 ground, int seed, int slot, List<ChannelSegment> output, int branchBudget = 0)
    {
        Vector3 axis = ground - top;
        float height = axis.Length();
        if (!(height > 10.0f) || !axis.IsFinite()) return 0;
        Vector3 an = axis / height;
        Vector3 p1 = an.Cross(Math.Abs(an.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 p2 = an.Cross(p1);
        float slant = 0.05f + 0.20f * Rnd(seed, 1, 0), azimuth = Rnd(seed, 1, 1) * Tau;
        Vector3 top2 = top + (p1 * Mathf.Cos(azimuth) + p2 * Mathf.Sin(azimuth)) * (slant * height);

        int levels = Math.Clamp((int)Math.Ceiling(Math.Log2(height / 30.0f)), 6, 8);
        var main = new List<Vector3>(257);
        Displace(top2, ground, levels, 0.34f, seed, 10, false, main);
        int n = main.Count - 1;
        var mainSegs = new List<ChannelSegment>(n);
        for (int i = 0; i < n; i++)
        {
            float t0 = (float)i / n, t1 = (float)(i + 1) / n;
            float w0 = Mathf.Lerp(0.55f, 1.0f, t0) * (0.85f + 0.3f * Rnd(seed, 2, i));
            float w1 = Mathf.Lerp(0.55f, 1.0f, t1) * (0.85f + 0.3f * Rnd(seed, 2, i + 1));
            mainSegs.Add(new ChannelSegment(main[i], main[i + 1], w0, w1, 1.0f, slot, 0.0f, 0.0f));
        }

        var primary = new List<ChannelSegment>(320);
        var secondary = new List<ChannelSegment>(256);
        var pts = new List<Vector3>(33);
        var pts2 = new List<Vector3>(33);
        int branches = 0;
        // branchBudget 0 = megastorm density (5% of the vertices, at most 10); above 0 (natural, shorter channels) about that many primary forks, whatever the vertex count.
        float forkChance = branchBudget > 0 ? Math.Min(0.5f, branchBudget / (0.7f * n)) : 0.05f;
        int maxBranches = branchBudget > 0 ? branchBudget + 2 : 10;
        for (int v = 1; v < n && branches < maxBranches; v++)
        {
            float t = (float)v / n;
            if (t < 0.05f || t > 0.75f || Rnd(seed, 200, v) >= forkChance) continue;
            Vector3 heading = (main[v + 1] - main[v - 1]).Normalized();
            Vector3 axisB = RandomPerpendicular(heading, seed, 201, v);
            float angle = Mathf.DegToRad(20.0f + 30.0f * Rnd(seed, 202, v));
            Vector3 dir = RotateAbout(heading, axisB, angle).Normalized();
            float length = (0.08f + 0.27f * Rnd(seed, 203, v)) * height * (1.0f - 0.5f * t);
            float rootWidth = 0.5f * mainSegs[v - 1].WidthB;
            Displace(main[v], main[v] + dir * length, 5, 0.30f, seed, 300 + v * 8, true, pts);
            EmitPolyline(pts, rootWidth, 0.08f, 0.55f, 0.12f, Rnd(seed, 204, v), 1.0f, slot, primary);
            branches++;
            int bn = pts.Count - 1;
            for (int j = 1; j < bn; j++)
            {
                if (Rnd(seed, 400 + v, j) >= 0.03f) continue;
                float fj = (float)j / bn;
                Vector3 local = (pts[j + 1] - pts[j - 1]).Normalized();
                Vector3 axis2 = RandomPerpendicular(local, seed, 401 + v, j);
                Vector3 dir2 = RotateAbout(local, axis2, Mathf.DegToRad(20.0f + 25.0f * Rnd(seed, 402 + v, j))).Normalized();
                float length2 = length * (1.0f - fj) * (0.3f + 0.2f * Rnd(seed, 403 + v, j));
                float width0 = Mathf.Lerp(rootWidth, 0.08f, fj) * 0.6f;
                float inten0 = Mathf.Lerp(0.55f, 0.12f, fj) * 0.6f;
                Displace(pts[j], pts[j] + dir2 * length2, 4, 0.30f, seed, 500 + v * 64 + j * 4, true, pts2);
                EmitPolyline(pts2, width0, width0 * 0.1f, inten0, inten0 * 0.2f, Rnd(seed, 404 + v, j), 1.0f, slot, secondary);
            }
        }

        var run = new List<ChannelSegment>(64);
        if (Rnd(seed, 600, 0) < 0.5f)
        {
            float az = Rnd(seed, 600, 1) * Tau, tilt = Mathf.DegToRad((Rnd(seed, 600, 2) * 2.0f - 1.0f) * 10.0f);
            Vector3 dir = ((p1 * Mathf.Cos(az) + p2 * Mathf.Sin(az)) * Mathf.Cos(tilt) + an * Mathf.Sin(tilt)).Normalized();
            float length = (0.4f + 0.8f * Rnd(seed, 600, 3)) * height;
            Displace(top2, top2 + dir * length, 6, 0.30f, seed, 700, false, pts);
            EmitPolyline(pts, 0.6f, 0.15f, 0.5f, 0.5f, Rnd(seed, 600, 4), 1.0f, slot, run);
        }

        // Budget: main first, then primaries, the in-cloud run, and the deepest branches last (dropped first).
        int added = 0;
        void Take(List<ChannelSegment> group)
        {
            int take = Math.Min(MaxBoltSegments - added, group.Count);
            for (int i = 0; i < take; i++) output.Add(group[i]);
            added += Math.Max(0, take);
        }
        Take(mainSegs); Take(primary); Take(run); Take(secondary);
        return added;
    }

    // ---- Field strikes (WO13): upward channel from a dome top to the containment field ----
    public const int FieldMaxSegments = 900;
    /// <summary>Bolt-slot tip values at or above this mark a field-strike slot in ring_lightning_channel.glsl: tip = FieldSlotTipBase + leader progress (0..1, 1 = attached).</summary>
    public const float FieldSlotTipBase = 10.0f;

    /// <summary>
    /// Field-strike channel from <paramref name="channelBase"/> (the dome top) up to <paramref name="contact"/> (50 m under the field). Segment fields: AlongA = height fraction
    /// 0..1 of the segment start along the channel axis (the leader reveals everything whose start is below its tip), AlongB = class + end height fraction (class 0 trunk, 1 fan
    /// branch, 2 terminal spray: floor(AlongB) is the class, the fraction the end height of a trunk piece). A trunk with a lower 30% leader, 5-8 fan branches in the upper
    /// 40% (a gigantic-jet tree up to 8 km wide, scaled with the channel), and a violet spray at the contact. Contact arcs (<paramref name="arc"/>) are the trunk and two forks.
    /// Deterministic in the seed; appends at most <see cref="FieldMaxSegments"/> segments and returns the count.
    /// </summary>
    public static int BuildFieldStrikeChannel(Vector3 channelBase, Vector3 contact, int seed, bool arc, int slot, List<ChannelSegment> output)
    {
        Vector3 axis = contact - channelBase;
        float length = axis.Length();
        if (!(length > 5.0f) || !axis.IsFinite()) return 0;
        Vector3 an = axis / length;
        Vector3 p1 = an.Cross(Math.Abs(an.Y) < 0.9f ? Vector3.Up : Vector3.Right).Normalized();
        Vector3 p2 = an.Cross(p1);
        float Height(Vector3 p) => Math.Clamp((p - channelBase).Dot(an) / length, 0.0f, 1.0f);
        // Nothing may pass the contact height: the field is there (displaced polylines are pushed back under it).
        void Cap(List<Vector3> path)
        {
            for (int i = 0; i < path.Count; i++)
            {
                float over = (path[i] - channelBase).Dot(an) - length;
                if (over > 0.0f) path[i] -= an * over;
            }
        }

        // Tortuous leader: the megastorm bolt midpoint-displacement walk (roughness 0.2 decaying 0.8 per level, forks 0.34), from the dome top to the contact point.
        int levels = arc ? Math.Clamp((int)Math.Ceiling(Math.Log2(length / 12.0f)), 2, 5) : Math.Clamp((int)Math.Ceiling(Math.Log2(length / 30.0f)), 5, 8);
        var main = new List<Vector3>(257);
        Displace(channelBase, contact, levels, 0.2f, seed, 10, false, main);
        Cap(main);
        int n = main.Count - 1;
        var trunk = new List<ChannelSegment>(n);
        float hPrev = 0.0f;
        for (int i = 0; i < n; i++)
        {
            float t0 = (float)i / n, t1 = (float)(i + 1) / n;
            float hA = Math.Max(hPrev, Height(main[i])), hB = Math.Max(hA, Height(main[i + 1]));
            hPrev = hB;
            float w0 = Mathf.Lerp(0.5f, 0.9f, t0) * (0.8f + 0.4f * Rnd(seed, 2, i)), w1 = Mathf.Lerp(0.5f, 0.9f, t1) * (0.8f + 0.4f * Rnd(seed, 2, i + 1));
            trunk.Add(new ChannelSegment(main[i], main[i + 1], w0, w1, hA < 0.3f ? 1.0f : 0.85f, slot, hA, Math.Min(hB, 0.999f)));
        }

        var branches = new List<ChannelSegment>(768);
        var pts = new List<Vector3>(33);
        var pts2 = new List<Vector3>(17);
        var pts3 = new List<Vector3>(9);
        void EmitBranch(List<Vector3> path, float rootHeight, float wStart, float wEnd, float iStart, float iEnd)
        {
            int count = path.Count - 1;
            for (int i = 0; i < count; i++)
            {
                float f0 = (float)i / count, f1 = (float)(i + 1) / count, fm = (f0 + f1) * 0.5f;
                branches.Add(new ChannelSegment(path[i], path[i + 1], Mathf.Lerp(wStart, wEnd, f0), Mathf.Lerp(wStart, wEnd, f1), Mathf.Lerp(iStart, iEnd, fm), slot, rootHeight, 1.0f));
            }
        }
        // A fork leaving a polyline vertex at an acute 20-50 degrees, tapering into a filament; recurses once into finer forks.
        void Fork(List<Vector3> from, int v, float lengthMeters, float width, float inten, int stream, int depth)
        {
            Vector3 heading = (from[Math.Min(v + 1, from.Count - 1)] - from[Math.Max(v - 1, 0)]).Normalized();
            Vector3 dir = RotateAbout(heading, RandomPerpendicular(heading, seed, stream + 1, v), Mathf.DegToRad(20.0f + 30.0f * Rnd(seed, stream + 2, v))).Normalized();
            var path = depth == 0 ? pts2 : pts3;
            Displace(from[v], from[v] + dir * lengthMeters, depth == 0 ? 4 : 3, 0.34f, seed, stream + 8 + v * 4, true, path);
            Cap(path);
            EmitBranch(path, Height(from[v]), width, width * 0.1f, inten, inten * 0.25f);
            if (depth == 0)
            {
                int bn = path.Count - 1;
                var saved = new List<Vector3>(path);
                for (int j = 2; j < bn - 1; j++)
                    if (Rnd(seed, stream + 3, j) < 0.14f)
                        Fork(saved, j, lengthMeters * (1.0f - (float)j / bn) * (0.35f + 0.25f * Rnd(seed, stream + 4, j)), width * 0.5f, inten * 0.6f, stream + 20 + j, 1);
            }
        }
        if (arc)
        {
            for (int k = 0; k < 2; k++)
            {
                int v = Math.Clamp((int)(n * (0.25f + 0.5f * Rnd(seed, 90 + k, 0))), 1, Math.Max(1, n - 1));
                Fork(main, v, length * (0.25f + 0.3f * Rnd(seed, 93 + k, 0)), 0.4f, 0.6f, 100 + k * 40, 0);
            }
        }
        else
        {
            // Irregular, asymmetric fan along the field underside: 3-9 forks rooted anywhere in the upper trunk (biased toward the top), each its own length and heading.
            int fan = 3 + (int)(6.99f * Rnd(seed, 100, 0));
            for (int b = 0; b < fan; b++)
            {
                float u = Rnd(seed, 101, b), tRoot = 0.2f + 0.75f * MathF.Pow(u, 0.55f);
                int v = Math.Clamp((int)Math.Round(tRoot * n), 1, n - 1);
                float remaining = Math.Max(0.0f, 1.0f - Height(main[v])) * length;
                float reach = remaining * (0.15f + 0.9f * Rnd(seed, 104, b)) + 0.01f * length;
                reach = Math.Min(reach, 4500.0f);   // a fan at most about 8-9 km wide
                if (reach < 5.0f) continue;
                Fork(main, v, reach, 0.5f, 0.75f, 300 + b * 60, 0);
            }
            // Creepers: a few branches running sideways along the underside from the last tenth of the trunk.
            int creepers = 1 + (int)(3.0f * Rnd(seed, 130, 0));
            for (int c = 0; c < creepers; c++)
            {
                int v = Math.Clamp((int)Math.Round((0.9f + 0.1f * Rnd(seed, 131, c)) * n), 1, n - 1);
                float az = Rnd(seed, 132, c) * Tau, reachC = Math.Min(length * (0.03f + 0.12f * Rnd(seed, 133, c)), 3000.0f);
                Vector3 dir = ((p1 * Mathf.Cos(az) + p2 * Mathf.Sin(az)) * 0.97f - an * 0.12f).Normalized();
                Displace(main[v], main[v] + dir * reachC, 4, 0.34f, seed, 900 + c * 16, true, pts);
                Cap(pts);
                EmitBranch(pts, Height(main[v]), 0.4f, 0.06f, 0.6f, 0.2f);
            }
        }

        // Terminal spray: filaments along the field underside (perpendicular to the axis, dipping away from the field), revealed when the leader attaches.
        var spray = new List<ChannelSegment>(128);
        int sprayCount = arc ? 2 : 3 + (int)(4.99f * Rnd(seed, 120, 0));
        float sprayLength = arc ? 0.25f * length : Math.Clamp(0.03f * length, 80.0f, 700.0f);
        for (int k = 0; k < sprayCount; k++)
        {
            float az = (k + Rnd(seed, 121, k)) / sprayCount * Tau, tilt = 0.1f + 0.4f * Rnd(seed, 122, k);
            Vector3 dir = ((p1 * Mathf.Cos(az) + p2 * Mathf.Sin(az)) * Mathf.Cos(tilt) - an * Mathf.Sin(tilt)).Normalized();
            Displace(contact, contact + dir * (sprayLength * (0.3f + 0.7f * Rnd(seed, 123, k))), 3, 0.34f, seed, 600 + k * 8, true, pts);
            Cap(pts);
            int count = pts.Count - 1;
            for (int i = 0; i < count; i++)
            {
                float f0 = (float)i / count, f1 = (float)(i + 1) / count;
                spray.Add(new ChannelSegment(pts[i], pts[i + 1], Mathf.Lerp(0.6f, 0.1f, f0), Mathf.Lerp(0.6f, 0.1f, f1), Mathf.Lerp(1.0f, 0.4f, (f0 + f1) * 0.5f), slot, 1.0f, 2.0f));
            }
        }

        int added = 0;
        void Take(List<ChannelSegment> group)
        {
            int take = Math.Min(FieldMaxSegments - added, group.Count);
            for (int i = 0; i < take; i++) output.Add(group[i]);
            added += Math.Max(0, take);
        }
        Take(trunk); Take(spray); Take(branches);
        return added;
    }

    /// <summary>
    /// Bolt pulse with an afterglow after each stroke and a soft fade-out. Returns the brightness (already times Energy) and the stroke index 0..2 (0 when flashes are reduced).
    /// </summary>
    public static float ChannelPulse(in RingLightningEvent e, double now, out int stroke)
    {
        double age = now - e.StartTimeSeconds;
        stroke = 0;
        if (age < 0.0 || age > RingLightningEvent.LifetimeSeconds) return 0.0f;
        float energy = Math.Max(0.0f, e.Energy);
        float p = RingLightningEvent.PulseAtAge(age);
        if (LightningVisualPolicy.ReducedFlashes) return p * energy;
        float after = 0.0f;
        for (int k = 0; k < 3; k++)
        {
            double c = k == 0 ? 0.035 : k == 1 ? 0.265 : 0.505;
            float w = k == 0 ? 1.0f : k == 1 ? 0.62f : 0.35f;
            if (age >= c) after = Math.Max(after, w * 0.45f * (float)Math.Exp(-(age - c) / 0.2));
        }
        float fade = 1.0f - Smooth01((float)((age - 0.62) / 0.10));
        stroke = age < 0.15 ? 0 : age < 0.385 ? 1 : 2;
        return Math.Max(p, after) * fade * energy;
    }

    private static float Smooth01(float x) { x = Math.Clamp(x, 0.0f, 1.0f); return x * x * (3.0f - 2.0f * x); }

    public static void BuildBolt(LightningBolt bolt, Vector3 startWorld, Vector3 sideAxis, Vector3 downAxis, int boltIndex, int seed, List<ChannelSegment> output)
    {
        Vector3 normal = sideAxis.Cross(downAxis);
        if (normal.LengthSquared() < 1.0e-8f) normal = Vector3.Up;
        normal = normal.Normalized();
        int pathIndex = 0;
        AppendPath(bolt.MainPath, 1.0f, startWorld, sideAxis, downAxis, normal, boltIndex, seed, ref pathIndex, output);
        foreach (LightningBranch branch in bolt.Branches) AppendBranch(branch, 1, startWorld, sideAxis, downAxis, normal, boltIndex, seed, ref pathIndex, output);
    }

    private static void AppendBranch(LightningBranch branch, int depth, Vector3 start, Vector3 side, Vector3 down, Vector3 normal, int boltIndex, int seed, ref int pathIndex, List<ChannelSegment> output)
    {
        AppendPath(branch.Path, depth == 1 ? 0.45f : 0.25f, start, side, down, normal, boltIndex, seed, ref pathIndex, output);
        foreach (LightningBranch child in branch.Children) AppendBranch(child, depth + 1, start, side, down, normal, boltIndex, seed, ref pathIndex, output);
    }

    private static void AppendPath(LightningPath path, float intensity, Vector3 start, Vector3 side, Vector3 down, Vector3 normal, int boltIndex, int seed, ref int pathIndex, List<ChannelSegment> output)
    {
        int thisPath = pathIndex++;
        for (int s = 1; s < path.Points.Count; s++)
        {
            LightningPoint p0 = path.Points[s - 1], p1 = path.Points[s];
            Vector3 a = start + side * p0.Position.X + down * p0.Position.Y;
            Vector3 b = start + side * p1.Position.X + down * p1.Position.Y;
            Subdivide(a, b, p0.Width, p1.Width, 0, thisPath, s, seed, normal, side, down, intensity, boltIndex, 1, output);
        }
    }

    // Midpoint displacement; endpoints are never moved, so the polyline stays connected and the main path still ends at the generator's end point.
    private static void Subdivide(Vector3 a, Vector3 b, float wa, float wb, int level, int pathIndex, int segIndex, int seed, Vector3 normal, Vector3 side, Vector3 down,
        float intensity, int boltIndex, int slot, List<ChannelSegment> output)
    {
        if (level >= SubdivisionLevels)
        {
            output.Add(new ChannelSegment(a, b, wa, wb, intensity, boltIndex));
            return;
        }
        Vector3 d = b - a;
        float length = d.Length();
        Vector3 mid = (a + b) * 0.5f;
        if (length > 1.0e-4f)
        {
            // In-plane perpendicular to the segment (the bolt lies in the side/down plane), mixed toward the plane normal by a hashed angle of up to 1 rad.
            Vector3 dir = d / length;
            Vector3 ip = side - dir * dir.Dot(side);
            if (ip.Length() < 1.0e-3f) ip = down - dir * dir.Dot(down);
            ip = ip.Normalized();
            float theta = (Hash01(seed, pathIndex, segIndex * 16 + slot, level + 7) * 2.0f - 1.0f) * 1.0f;
            Vector3 perp = (ip * Mathf.Cos(theta) + normal * Mathf.Sin(theta)).Normalized();
            float h = Hash01(seed, pathIndex, segIndex * 16 + slot, level) * 2.0f - 1.0f;
            mid += perp * (h * DisplacementFraction * length);
        }
        float wm = (wa + wb) * 0.5f;
        Subdivide(a, mid, wa, wm, level + 1, pathIndex, segIndex, seed, normal, side, down, intensity, boltIndex, slot * 2, output);
        Subdivide(mid, b, wm, wb, level + 1, pathIndex, segIndex, seed, normal, side, down, intensity, boltIndex, slot * 2 + 1, output);
    }

    private static float Hash01(int seed, int pathIndex, int segIndex, int level)
    {
        unchecked
        {
            uint h = (uint)seed * 0x9e3779b1u;
            h ^= (uint)pathIndex * 0x85ebca6bu + 0x7f4a7c15u;
            h = (h ^ (h >> 15)) * 0x2c1b3c6du;
            h ^= (uint)segIndex * 0xc2b2ae35u + 0x165667b1u;
            h = (h ^ (h >> 13)) * 0x297a2d39u;
            h ^= (uint)level * 0x27d4eb2fu + 0x1b873593u;
            h = (h ^ (h >> 16)) * 0x85ebca6bu;
            h ^= h >> 13;
            return (h & 0xffffffu) / 16777216.0f;
        }
    }
}
