using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using Godot;

namespace RingworldTechDemo.Terrain.Sites;

/// <summary>One level of detail of a baked shell: welded, compacted arrays (Godot front-face winding, normals into the air).</summary>
public sealed class EmitterShellLevel
{
    public float CellSizeM { get; init; }
    public Vector3[] Vertices { get; init; } = Array.Empty<Vector3>();
    public Vector3[] Normals { get; init; } = Array.Empty<Vector3>();
    public int[] Indices { get; init; } = Array.Empty<int>();
    public int TriangleCount => Indices.Length / 3;
    /// <summary>Number of leading vertices (shaft wall rings) that belong to the analytic shaft; informational.</summary>
    public int SeamVertexCount { get; init; }
}

/// <summary>
/// M2-2: offline construction of the inward-facing rock shell of one kind's underground air volume
/// (<see cref="EmitterSiteUnderground"/>), for the part below the plug iris (-200 m). Pure C#, deterministic.
///
/// Parts per LOD level: (1) the cavern (core + bays) meshed from the cavern SDF with <see cref="EmitterSdfMesher"/> on a
/// uniform grid; (2) the shaft wall, an analytic ring x segment cylinder from the -200 m seam ring to the cavern roof; (3) a
/// ledge annulus at -200 m bridging the liner radius to the shaft radius (regular only); (4) the arrival tunnel stub, an analytic
/// capsule tube. The cavern roof is cut with a hole around the shaft axis (and the bay wall with a hole around the tunnel mouth);
/// each cut boundary is stitched to the analytic ring by an angle-sorted triangle strip, so the result is closed except for the
/// open seam ring at -200 m.
/// </summary>
public static class EmitterUndergroundShell
{
    public const int LevelCount = 3;

    /// <summary>Lattice cell sizes (metres) of the three levels; levels 1 and 2 target roughly 1/3 and 1/10 of the level 0 triangle count.</summary>
    public static float[] CellSizes(SiteKind kind)
        => kind == SiteKind.Main ? new[] { 58f, 100f, 183f } : new[] { 42f, 73f, 133f };

    public static int[] ShaftSegments(SiteKind kind) => kind == SiteKind.Main ? new[] { 96, 64, 48 } : new[] { 64, 48, 32 };
    public static float[] ShaftRingSpacing(SiteKind kind) => kind == SiteKind.Main ? new[] { 150f, 300f, 600f } : new[] { 100f, 200f, 400f };
    public static int[] TunnelSegments(SiteKind kind) => kind == SiteKind.Main ? new[] { 32, 24, 16 } : new[] { 24, 16, 12 };

    public static EmitterShellLevel[] Build(SiteKind kind)
    {
        float[] cells = CellSizes(kind);
        int[] segs = ShaftSegments(kind), tsegs = TunnelSegments(kind);
        float[] spacing = ShaftRingSpacing(kind);
        EmitterShellLevel[] levels = new EmitterShellLevel[LevelCount];
        for (int l = 0; l < LevelCount; l++) levels[l] = BuildLevel(kind, cells[l], segs[l], spacing[l], tsegs[l]);
        return levels;
    }

    /// <summary>SHA-256 (hex) over every level's vertex, normal and index bytes; the determinism check.</summary>
    public static string Hash(EmitterShellLevel[] levels)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> four = stackalloc byte[4];
        foreach (EmitterShellLevel level in levels)
        {
            foreach (Vector3 v in level.Vertices) { Put(hash, four, v.X); Put(hash, four, v.Y); Put(hash, four, v.Z); }
            foreach (Vector3 n in level.Normals) { Put(hash, four, n.X); Put(hash, four, n.Y); Put(hash, four, n.Z); }
            foreach (int i in level.Indices) { BitConverter.TryWriteBytes(four, i); hash.AppendData(four); }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Put(IncrementalHash hash, Span<byte> four, float value)
    {
        BitConverter.TryWriteBytes(four, BitConverter.SingleToInt32Bits(value));
        hash.AppendData(four);
    }

    private sealed class Accumulator
    {
        public readonly List<Vector3> V = new();
        public readonly List<Vector3> N = new();
        public readonly List<int> I = new();
        public int Add(Vector3 p, Vector3 n) { V.Add(p); N.Add(n); return V.Count - 1; }

        /// <summary>Adds a triangle, swapping two corners if its Godot front normal (-(b-a)x(c-a)) opposes <paramref name="desired"/>.</summary>
        public void Tri(int a, int b, int c, Vector3 desired)
        {
            Vector3 front = -(V[b] - V[a]).Cross(V[c] - V[a]);
            if (front.Dot(desired) < 0f) (b, c) = (c, b);
            I.Add(a); I.Add(b); I.Add(c);
        }
    }

    private static EmitterShellLevel BuildLevel(SiteKind kind, double cell, int shaftSegs, double ringSpacing, int tunnelSegs)
    {
        EmitterSiteUndergroundShape s = EmitterSiteUnderground.Shape(kind);
        double roofY = s.CavernRoofApexYM;

        // ---- (1) cavern from the cavern-only SDF ------------------------------------------------------------------
        double ox = Math.Floor((s.BoxMinX - 2d * cell) / cell) * cell, oy = Math.Floor((s.BoxMinY - 2d * cell) / cell) * cell, oz = Math.Floor((s.BoxMinZ - 2d * cell) / cell) * cell;
        int nx = (int)Math.Ceiling((s.BoxMaxX + 2d * cell - ox) / cell);
        int ny = (int)Math.Ceiling((roofY + 3d * cell - oy) / cell);
        int nz = (int)Math.Ceiling((s.BoxMaxZ + 2d * cell - oz) / cell);
        List<Vector3> cv = new();
        List<int> ci = new();
        EmitterSdfMesher.Mesh((x, y, z) => -s.CavernAir(x, y, z), ox, oy, oz, cell, nx, ny, nz, cv, ci);

        // ---- cut the shaft hole in the roof and the tunnel hole in the arrival bay wall --------------------------
        double ty = s.TunnelY;
        double mouthZ = s.Bay0Z + Math.Sqrt(s.Bay0R * s.Bay0R - (ty - s.ChamberCenterYM) * (ty - s.ChamberCenterYM));
        double shaftCut = s.ShaftR + 3d * cell;
        double tunnelCut = s.TunnelR + 1.5d * cell;
        bool[] removed = new bool[ci.Count / 3];
        List<int> roofRemoved = new(), mouthRemoved = new();
        for (int t = 0; t < removed.Length; t++)
        {
            Vector3 a = cv[ci[3 * t]], b = cv[ci[3 * t + 1]], c = cv[ci[3 * t + 2]];
            Vector3 g = (a + b + c) / 3f;
            if (Math.Abs(a.Y - roofY) < 0.5 && Math.Abs(b.Y - roofY) < 0.5 && Math.Abs(c.Y - roofY) < 0.5 && Math.Sqrt((double)g.X * g.X + (double)g.Z * g.Z) < shaftCut)
            { removed[t] = true; roofRemoved.Add(t); }
            else if (g.Z > mouthZ - 3d * cell && g.Z < mouthZ + cell && Math.Sqrt((double)g.X * g.X + (g.Y - ty) * (g.Y - ty)) < tunnelCut)
            { removed[t] = true; mouthRemoved.Add(t); }
        }
        if (roofRemoved.Count == 0 || mouthRemoved.Count == 0) throw new InvalidOperationException($"{kind}: shell hole not found (roof {roofRemoved.Count}, mouth {mouthRemoved.Count})");

        Accumulator acc = new();
        // Cavern vertices are appended lazily (only the used ones), with normals from the full SDF gradient.
        int[] remap = new int[cv.Count];
        Array.Fill(remap, -1);
        int Use(int index)
        {
            if (remap[index] < 0)
            {
                Vector3 p = cv[index];
                remap[index] = acc.Add(p, GradientNormal(s, p));
            }
            return remap[index];
        }
        for (int t = 0; t < removed.Length; t++)
        {
            if (removed[t]) continue;
            acc.I.Add(Use(ci[3 * t])); acc.I.Add(Use(ci[3 * t + 1])); acc.I.Add(Use(ci[3 * t + 2]));
        }

        // ---- (2) shaft: seam ring at -200, optional ledge, wall rings down to the roof, stitched to the roof hole --
        double seamY = s.PlugDepthM;
        double liner = s.BoreR, shaftR = s.ShaftR;
        Vector3 down = new(0f, -1f, 0f);
        int seamVertexCount = 0;
        int[] wallTop;
        if (shaftR > liner + 0.01d)
        {
            int[] seam = new int[shaftSegs];
            wallTop = new int[shaftSegs];
            int[] ledgeOuter = new int[shaftSegs];
            for (int j = 0; j < shaftSegs; j++)
            {
                double th = 2d * Math.PI * j / shaftSegs;
                double c = Math.Cos(th), sn = Math.Sin(th);
                seam[j] = acc.Add(new Vector3((float)(liner * c), (float)seamY, (float)(liner * sn)), down);
                ledgeOuter[j] = acc.Add(new Vector3((float)(shaftR * c), (float)seamY, (float)(shaftR * sn)), down);
                wallTop[j] = acc.Add(new Vector3((float)(shaftR * c), (float)seamY, (float)(shaftR * sn)), new Vector3((float)-c, 0f, (float)-sn));
            }
            for (int j = 0; j < shaftSegs; j++)
            {
                int j1 = (j + 1) % shaftSegs;
                acc.Tri(seam[j], seam[j1], ledgeOuter[j1], down);
                acc.Tri(seam[j], ledgeOuter[j1], ledgeOuter[j], down);
            }
            seamVertexCount = shaftSegs;
        }
        else
        {
            wallTop = new int[shaftSegs];
            for (int j = 0; j < shaftSegs; j++)
            {
                double th = 2d * Math.PI * j / shaftSegs;
                double c = Math.Cos(th), sn = Math.Sin(th);
                wallTop[j] = acc.Add(new Vector3((float)(shaftR * c), (float)seamY, (float)(shaftR * sn)), new Vector3((float)-c, 0f, (float)-sn));
            }
            seamVertexCount = shaftSegs;
        }
        int rings = Math.Max(1, (int)Math.Ceiling((seamY - roofY) / ringSpacing));
        int[] prev = wallTop;
        for (int r = 1; r <= rings; r++)
        {
            double y = r == rings ? roofY : seamY - (seamY - roofY) * r / rings;
            int[] cur = new int[shaftSegs];
            for (int j = 0; j < shaftSegs; j++)
            {
                double th = 2d * Math.PI * j / shaftSegs;
                double c = Math.Cos(th), sn = Math.Sin(th);
                cur[j] = acc.Add(new Vector3((float)(shaftR * c), (float)y, (float)(shaftR * sn)), new Vector3((float)-c, 0f, (float)-sn));
            }
            for (int j = 0; j < shaftSegs; j++)
            {
                int j1 = (j + 1) % shaftSegs;
                AddWallQuad(acc, prev[j], prev[j1], cur[j1], cur[j], v => new Vector3(-v.X, 0f, -v.Z));
            }
            prev = cur;
        }
        // Roof collar: the wall's last ring duplicated with the roof normal, stitched to the cut boundary.
        int[] roofRing = new int[shaftSegs];
        for (int j = 0; j < shaftSegs; j++)
        {
            double th = 2d * Math.PI * j / shaftSegs;
            roofRing[j] = acc.Add(new Vector3((float)(shaftR * Math.Cos(th)), (float)roofY, (float)(shaftR * Math.Sin(th))), down);
        }
        List<int> roofLoop = BoundaryVertices(ci, roofRemoved, removed, Use);
        Stitch(acc, roofLoop, roofRing, v => Math.Atan2(v.Z, v.X), down);

        // ---- (3) arrival tunnel: analytic capsule tube from the bay wall to the end cap -----------------------------
        double tr = s.TunnelR, z1 = s.TunnelZ1;
        int[] mouthRing = new int[tunnelSegs];
        for (int j = 0; j < tunnelSegs; j++)
        {
            double th = 2d * Math.PI * j / tunnelSegs;
            mouthRing[j] = acc.Add(new Vector3((float)(tr * Math.Cos(th)), (float)(ty + tr * Math.Sin(th)), (float)mouthZ), new Vector3(0f, 0f, -1f));
        }
        List<int> mouthLoop = BoundaryVertices(ci, mouthRemoved, removed, Use);
        Stitch(acc, mouthLoop, mouthRing, v => Math.Atan2(v.Y - ty, v.X), new Vector3(0f, 0f, -1f));

        int[] tprev = new int[tunnelSegs];
        for (int j = 0; j < tunnelSegs; j++) tprev[j] = AddTunnelVertex(acc, tunnelSegs, j, tr, ty, mouthZ);
        int tubeRings = Math.Max(1, (int)Math.Ceiling((z1 - mouthZ) / (ringSpacing * 0.5d)));
        for (int r = 1; r <= tubeRings; r++)
        {
            double z = r == tubeRings ? z1 : mouthZ + (z1 - mouthZ) * r / tubeRings;
            int[] cur = new int[tunnelSegs];
            for (int j = 0; j < tunnelSegs; j++) cur[j] = AddTunnelVertex(acc, tunnelSegs, j, tr, ty, z);
            for (int j = 0; j < tunnelSegs; j++)
                AddWallQuad(acc, tprev[j], tprev[(j + 1) % tunnelSegs], cur[(j + 1) % tunnelSegs], cur[j], v => new Vector3(-v.X, -(v.Y - (float)ty), 0f));
            tprev = cur;
        }
        const int capRings = 3;
        for (int r = 1; r <= capRings; r++)
        {
            double phi = Math.PI * 0.5d * r / (capRings + 1);
            double rr = tr * Math.Cos(phi), zz = z1 + tr * Math.Sin(phi);
            int[] cur = new int[tunnelSegs];
            for (int j = 0; j < tunnelSegs; j++)
            {
                double th = 2d * Math.PI * j / tunnelSegs;
                Vector3 p = new((float)(rr * Math.Cos(th)), (float)(ty + rr * Math.Sin(th)), (float)zz);
                cur[j] = acc.Add(p, (new Vector3(0f, (float)ty, (float)z1) - p).Normalized());
            }
            for (int j = 0; j < tunnelSegs; j++)
                AddWallQuad(acc, tprev[j], tprev[(j + 1) % tunnelSegs], cur[(j + 1) % tunnelSegs], cur[j], v => (new Vector3(0f, (float)ty, (float)z1) - v).Normalized());
            tprev = cur;
        }
        int apex = acc.Add(new Vector3(0f, (float)ty, (float)(z1 + tr)), new Vector3(0f, 0f, -1f));
        for (int j = 0; j < tunnelSegs; j++)
            acc.Tri(tprev[j], tprev[(j + 1) % tunnelSegs], apex, new Vector3(0f, 0f, -1f) + (new Vector3(0f, (float)ty, (float)z1) - acc.V[tprev[j]]).Normalized());

        return new EmitterShellLevel
        {
            CellSizeM = (float)cell,
            Vertices = acc.V.ToArray(),
            Normals = acc.N.ToArray(),
            Indices = acc.I.ToArray(),
            SeamVertexCount = seamVertexCount,
        };
    }

    private static int AddTunnelVertex(Accumulator acc, int segs, int j, double tr, double ty, double z)
    {
        double th = 2d * Math.PI * j / segs;
        double c = Math.Cos(th), sn = Math.Sin(th);
        return acc.Add(new Vector3((float)(tr * c), (float)(ty + tr * sn), (float)z), new Vector3((float)-c, (float)-sn, 0f));
    }

    private static void AddWallQuad(Accumulator acc, int a, int b, int c, int d, Func<Vector3, Vector3> desiredAt)
    {
        Vector3 desired = desiredAt((acc.V[a] + acc.V[b] + acc.V[c] + acc.V[d]) * 0.25f);
        acc.Tri(a, b, c, desired);
        acc.Tri(a, c, d, desired);
    }

    /// <summary>The single closed boundary loop of the removed triangle set (edges used by exactly one removed triangle), as remapped accumulator indices in chain order.</summary>
    private static List<int> BoundaryVertices(List<int> ci, List<int> removedTriangles, bool[] removedFlags, Func<int, int> use)
    {
        Dictionary<(int, int), int> uses = new();
        foreach (int t in removedTriangles)
            for (int e = 0; e < 3; e++)
            {
                int a = ci[3 * t + e], b = ci[3 * t + (e + 1) % 3];
                (int, int) key = a < b ? (a, b) : (b, a);
                uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
            }
        SortedDictionary<int, int> next = new();
        foreach (int t in removedTriangles)
            for (int e = 0; e < 3; e++)
            {
                int a = ci[3 * t + e], b = ci[3 * t + (e + 1) % 3];
                if (uses[a < b ? (a, b) : (b, a)] != 1) continue;
                if (next.ContainsKey(a)) throw new InvalidOperationException("shell hole boundary is not a simple loop");
                next[a] = b;
            }
        List<int> chain = new();
        int start = -1;
        foreach (int k in next.Keys) { start = k; break; }
        if (start < 0) throw new InvalidOperationException("shell hole has no boundary");
        int cur = start;
        do
        {
            chain.Add(cur);
            if (!next.TryGetValue(cur, out cur)) throw new InvalidOperationException("shell hole boundary is open");
        } while (cur != start && chain.Count <= next.Count);
        if (chain.Count != next.Count) throw new InvalidOperationException($"shell hole boundary has several loops ({chain.Count} of {next.Count} edges)");
        List<int> result = new();
        foreach (int v in chain) result.Add(use(v));
        return result;
    }

    /// <summary>
    /// Stitches a boundary loop (chain order) to an analytic ring (uniform angles from 0) with a merge-style triangle strip. The loop
    /// is oriented to increase in angle and its angles are unwrapped along the chain, so the strip shares exactly the boundary edges.
    /// </summary>
    private static void Stitch(Accumulator acc, List<int> loop, int[] ring, Func<Vector3, double> angleOf, Vector3 desired)
    {
        const double TwoPi = 2d * Math.PI;
        int nl = loop.Count, nr = ring.Length;
        double[] angle = new double[nl + 1];
        angle[0] = Norm(angleOf(acc.V[loop[0]]));
        for (int i = 1; i <= nl; i++)
        {
            double d = angleOf(acc.V[loop[i % nl]]) - angleOf(acc.V[loop[i - 1]]);
            while (d > Math.PI) d -= TwoPi;
            while (d < -Math.PI) d += TwoPi;
            angle[i] = angle[i - 1] + d;
        }
        if (angle[nl] < angle[0])
        {
            loop.Reverse(1, nl - 1); // keep loop[0] first, reverse the direction of travel
            angle[0] = Norm(angleOf(acc.V[loop[0]]));
            for (int i = 1; i <= nl; i++)
            {
                double d = angleOf(acc.V[loop[i % nl]]) - angleOf(acc.V[loop[i - 1]]);
                while (d > Math.PI) d -= TwoPi;
                while (d < -Math.PI) d += TwoPi;
                angle[i] = angle[i - 1] + d;
            }
        }
        int li = 0, rj = 0;
        while (li < nl || rj < nr)
        {
            bool advanceLoop = rj >= nr || (li < nl && angle[li + 1] <= TwoPi * (rj + 1) / nr);
            int l0 = loop[li % nl], r0 = ring[rj % nr];
            if (advanceLoop) { acc.Tri(l0, r0, loop[(li + 1) % nl], desired); li++; }
            else { acc.Tri(l0, r0, ring[(rj + 1) % nr], desired); rj++; }
        }

        static double Norm(double a) { a %= TwoPi; return a < 0d ? a + TwoPi : a; }
    }

    private static Vector3 GradientNormal(EmitterSiteUndergroundShape s, Vector3 p)
    {
        const double h = 0.5d;
        double gx = s.Evaluate(p.X + h, p.Y, p.Z) - s.Evaluate(p.X - h, p.Y, p.Z);
        double gy = s.Evaluate(p.X, p.Y + h, p.Z) - s.Evaluate(p.X, p.Y - h, p.Z);
        double gz = s.Evaluate(p.X, p.Y, p.Z + h) - s.Evaluate(p.X, p.Y, p.Z - h);
        double len = Math.Sqrt(gx * gx + gy * gy + gz * gz);
        if (len < 1e-9d) return new Vector3(0f, -1f, 0f);
        return new Vector3((float)(-gx / len), (float)(-gy / len), (float)(-gz / len));
    }
}
