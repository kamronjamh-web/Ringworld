using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Godot;
using RingworldTechDemo.Terrain.Editable;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// Voxel/cave direction spike B1 (docs/voxel-cave-direction-handoff.md section 5): gen-17 (runtime recipe, v5 sites) driving the smooth voxel backend with caves off.
/// Parity: real chunk captures + the real regular-cell mesher against GetPresentationSurfaceHeight at pads, cut faces, coasts, chunk and ring seams;
/// cached (per-sampler 2 m lattice cache) vs uncached density bit-identity for 1 m chunks and medium/coarse lattices; a golden fingerprint fixture.
/// Benchmark: density ms per chunk / lattice, gen-16 vs gen-17 cached/uncached.
/// </summary>
public partial class SmoothRuntimeSpikeTest : RefCounted
{
    const long Circumference = EmitterSiteTestWorlds.Circumference;
    const int Width = EmitterSiteTestWorlds.Width;
    const int Seed = 1337;
    const string Format = "smooth_runtime_gen17_v1";

    record Probe(string Name, long Around, int Across);

    // Baseline matching the Certified world's vertical domains well enough for density cost / topology (terrain bottom -8192, 512 m roof, wall top 10400).
    static RingTerrainSmoothBaseline NewBaseline(RingTerrainSampler s, bool cache)
        => new(s, structuralBottomVertical: -9992, terrainBottomVertical: -8192, protectedRoofThickness: 512, lowerHullTopVertical: -9192, structuralWallTopVertical: 10400, useRuntimeLatticeCache: cache);

    static SmoothTerrainEditSnapshot NoEdits(RingTerrainSmoothBaseline b) => new(new List<SmoothTerrainEditCommand>(), 0, b.AroundLength);

    static SmoothTerrainChunkCoordinate ChunkAt(RingTerrainSampler s, long around, int across)
    {
        long wrapped = RingCoordMath.WrapAround(around, Circumference);
        int vertical = (int)Math.Floor(s.GetPresentationSurfaceHeight(wrapped, across) / 32d);
        return new SmoothTerrainChunkCoordinate(wrapped / 32, vertical, Math.Clamp(across, 0, Width - 1) / 32);
    }

    public int Run(bool capture)
    {
        var failures = new List<string>();
        try
        {
            EmitterSiteRegistry.ClearCache();
            using RingTerrainSampler cachedSampler = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            using RingTerrainSampler plainSampler = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            RingTerrainSmoothBaseline cached = NewBaseline(cachedSampler, true);
            RingTerrainSmoothBaseline plain = NewBaseline(plainSampler, false);
            if (!cached.UsesRuntimeLatticeCache || plain.UsesRuntimeLatticeCache) failures.Add("lattice cache flag not honoured");
            if (cached.Revision != plain.Revision) failures.Add("cache flag changed the baseline revision");
            EmitterSiteRegistry registry = cachedSampler.EmitterSites ?? throw new InvalidOperationException("sampler has no site registry");
            Console.WriteLine($"SMOOTH_SPIKE sampler hash={cachedSampler.TerrainDefinitionHash:X16} baseline_revision={cached.Revision:X16} sites={registry.Count} main=({registry.MainSite.Around},{registry.MainSite.Across}) pad={registry.MainSite.PadHeight}");

            CheckColumnBitIdentity(cached, plain, failures);
            List<Probe> probes = BuildProbes(cachedSampler, registry);
            var golden = new Golden { Format = Format, Hash = cachedSampler.TerrainDefinitionHash.ToString("X16"), Revision = cached.Revision.ToString("X16"), Cases = new List<Case>() };
            double worstColumn = 0, worstRatio = 0, worstRaw = 0;
            foreach (Probe p in probes)
            {
                SmoothTerrainChunkCoordinate c = ChunkAt(cachedSampler, p.Around, p.Across);
                SmoothTerrainChunkSnapshot a = SmoothTerrainSnapshotCapture.Capture(c, cached, NoEdits(cached));
                SmoothTerrainChunkSnapshot b = SmoothTerrainSnapshotCapture.Capture(c, plain, NoEdits(plain));
                if (!a.Densities.SequenceEqual(b.Densities) || !a.Materials.SequenceEqual(b.Materials)) failures.Add($"{p.Name}: cached chunk density/material differs from uncached");
                SmoothTerrainMeshData mesh = SmoothTerrainMesher.Build(a, true);
                ulong meshHash = SmoothTerrainMeshHash.Compute(mesh);
                if (meshHash != SmoothTerrainMeshHash.Compute(SmoothTerrainMesher.Build(b, true))) failures.Add($"{p.Name}: cached mesh hash differs from uncached");
                if (mesh.Vertices.Length == 0) { failures.Add($"{p.Name}: empty mesh (chunk {c} does not contain the surface)"); continue; }
                (double colMax, double raw, double ratio, int covered, int missing, int holes) = Parity(cachedSampler, a, mesh);
                worstColumn = Math.Max(worstColumn, colMax); worstRaw = Math.Max(worstRaw, raw); worstRatio = Math.Max(worstRatio, ratio);
                Console.WriteLine($"SMOOTH_SPIKE_PARITY {p.Name,-22} chunk=({c.AroundChunk},{c.VerticalChunk},{c.AcrossChunk}) tris={mesh.Indices.Length / 3} column_crossing_max_err={colMax:F5} mesh_max_err={raw:F4} mesh_err_over_tol_max={ratio:F3} covered={covered} uncovered_expected={missing} multi_hits={holes}");
                if (colMax > 0.003) failures.Add($"{p.Name}: column zero-crossing error {colMax:F5} m > 0.003");
                if (ratio > 1.0) failures.Add($"{p.Name}: mesh error exceeds the interpolation bound (ratio {ratio:F2}, raw {raw:F3} m)");
                if (missing > covered / 50) failures.Add($"{p.Name}: {missing} expected surface points not covered by the mesh");
                // Tiers: cached vs uncached for the 1 m regular capture path, and the lattice paths below.
                var key = new SmoothTerrainVolumetricDensityCacheKey(new SmoothTerrainVolumetricDensitySnapshot(cached, NoEdits(cached)).Identity,
                    c.Origin.AroundVoxel - 2, c.Origin.VerticalVoxel - 2, c.Origin.AcrossVoxel - 2, 36, 1);
                var fa = new SmoothTerrainVolumetricDensitySnapshot(cached, NoEdits(cached));
                var fb = new SmoothTerrainVolumetricDensitySnapshot(plain, NoEdits(plain));
                SmoothTerrainRegularChunkCapture ra = fa.CaptureRegularChunk(key, c), rb = fb.CaptureRegularChunk(key, c);
                if (ra.Range != rb.Range || (ra.Snapshot == null) != (rb.Snapshot == null) || (ra.Snapshot != null && !ra.Snapshot.Densities.SequenceEqual(rb.Snapshot!.Densities)))
                    failures.Add($"{p.Name}: CaptureRegularChunk cached != uncached");
                if (ra.Snapshot != null && !ra.Snapshot.Densities.SequenceEqual(a.Densities)) failures.Add($"{p.Name}: CaptureRegularChunk != SnapshotCapture density");
                foreach (int spacing in new[] { 2, 4, 8, 16, 32 })
                {
                    int cells = 36;
                    long span = (long)cells * spacing;
                    long ao = (c.Origin.AroundVoxel / spacing) * spacing - (span / 2 / spacing) * spacing;
                    int vo = (int)(Math.Floor(cachedSampler.GetPresentationSurfaceHeight(p.Around, p.Across) / spacing) * spacing - (span / 2 / spacing) * spacing);
                    int zo = (int)(Math.Clamp((long)p.Across, 0, Width - 1) / spacing * spacing - (span / 2 / spacing) * spacing);
                    var lk = new SmoothTerrainVolumetricDensityCacheKey(fa.Identity, ao, vo, zo, cells, spacing);
                    int n = 37 * 37 * 37;
                    var da = new float[n]; var ma = new byte[n]; var db = new float[n]; var mb = new byte[n];
                    fa.CaptureNativeLattice(lk, da, ma); fb.CaptureNativeLattice(lk, db, mb);
                    if (!da.AsSpan().SequenceEqual(db) || !ma.AsSpan().SequenceEqual(mb)) failures.Add($"{p.Name}: lattice spacing {spacing} cached != uncached");
                }
                golden.Cases.Add(new Case
                {
                    Name = p.Name, Around = c.AroundChunk, Vertical = c.VerticalChunk, Across = c.AcrossChunk,
                    MeshHash = meshHash.ToString("X16"), DensityHash = HashFloats(a.Densities).ToString("X16"), MaterialHash = HashInts(a.Materials).ToString("X16"),
                    Vertices = mesh.Vertices.Length, Triangles = mesh.Indices.Length / 3,
                    Samples = new[] { 0, 1234, 20000, 25326, 40000, 50652 }.Select(i => new Sample { Index = i, Bits = BitConverter.SingleToInt32Bits(a.Densities[i]) }).ToList(),
                });
            }
            CheckSeams(cachedSampler, cached, registry, failures);
            Console.WriteLine($"SMOOTH_SPIKE_PARITY_WORST column_crossing={worstColumn:F5} m mesh_raw={worstRaw:F4} m mesh_err_over_tol={worstRatio:F3}");
            CheckGolden(golden, capture, failures);
        }
        catch (Exception exception) { failures.Add(exception.ToString()); }
        foreach (string failure in failures) GD.PrintErr("SMOOTH_SPIKE_FAIL " + failure);
        Console.WriteLine(failures.Count == 0 ? "SMOOTH_RUNTIME_SPIKE=PASS" : "SMOOTH_RUNTIME_SPIKE=FAIL failures=" + failures.Count);
        return failures.Count == 0 ? 0 : 1;
    }

    // --- bit identity at column level: random + seam + clamp edges -----------------------------------------------------------------
    static void CheckColumnBitIdentity(RingTerrainSmoothBaseline cached, RingTerrainSmoothBaseline plain, List<string> failures)
    {
        var rng = new Random(77);
        int bad = 0, total = 0;
        void Check(long a, int x)
        {
            total++;
            float c = cached.GetSmoothSurfaceHeight(a, x), p = plain.GetSmoothSurfaceHeight(a, x);
            if (BitConverter.SingleToInt32Bits(c) != BitConverter.SingleToInt32Bits(p)) { if (++bad <= 5) failures.Add($"column ({a},{x}) cached {c} != uncached {p}"); }
        }
        for (int i = 0; i < 60000; i++)
            Check((long)(rng.NextDouble() * Circumference), (int)(rng.NextDouble() * (Width + 1)));
        for (long a = Circumference - 6; a < Circumference + 6; a++) foreach (int x in new[] { 0, 1, 2, 3, Width - 3, Width - 2, Width - 1, Width, 158000, 158001 }) Check(a, x);
        for (long a = -4; a < 5; a++) foreach (int x in new[] { 0, 1, Width - 1, Width, 99999 }) Check(a, x);
        // Sequential streaming (cache warm, blocks evicted by a far walk) must stay identical.
        for (int i = 0; i < 20000; i++) Check(5_000_000 + (i % 700), 100_000 + i / 700 * 3);
        Console.WriteLine($"SMOOTH_SPIKE_COLUMN_IDENTITY columns={total} mismatches={bad} lattice_evals={cached.Sampler.LatticeVertexEvaluations} lattice_hits={cached.Sampler.LatticeVertexHits}");
        if (bad != 0) failures.Add($"{bad} cached/uncached column mismatches");
    }

    // --- probes ------------------------------------------------------------------------------------------------------------------------
    static List<Probe> BuildProbes(RingTerrainSampler s, EmitterSiteRegistry r)
    {
        var list = new List<Probe>();
        EmitterSite main = r.MainSite;
        list.Add(new Probe("main_pad_center", main.Around, main.Across));
        EmitterSiteKindShape mainShape = r.Recipe.ShapeFor(main.Kind);
        list.Add(new Probe("main_pad_edge", main.Around + (long)(mainShape.PadRadiusM / 2) * 2, main.Across));
        list.Add(new Probe("main_skirt", main.Around, main.Across + (int)(mainShape.RhoStartM + 60) / 2 * 2));
        EmitterSite land = r.Sites.ToArray().First(x => !x.IsMain && !x.IsIsland && !x.IsMountain);
        list.Add(new Probe("land_pad", land.Around, land.Across));
        EmitterSiteKindShape landShape = r.Recipe.ShapeFor(land.Kind);
        list.Add(new Probe("land_pad_edge", land.Around + (long)(landShape.PadRadiusM) / 2 * 2, land.Across));
        EmitterSite island = r.Sites.ToArray().First(x => !x.IsMain && x.IsIsland);
        list.Add(new Probe("island_pad", island.Around, island.Across));
        // Island coast/beach: walk outward along +across until the surface first drops to sea level.
        for (int dw = 2; dw < (int)island.InfluenceRadius; dw += 2)
        {
            if (s.GetPresentationSurfaceHeight(island.Around, island.Across + dw) <= 0.5f) { list.Add(new Probe("island_coast", island.Around, island.Across + dw)); break; }
        }
        // Steepest 2 m slope within a site's influence on a mountain site = terraced cut face.
        EmitterSite mountain = r.Sites.ToArray().First(x => !x.IsMain && x.IsMountain);
        double best = -1; (long, int) bestAt = (mountain.Around, mountain.Across);
        double radius = Math.Min(mountain.InfluenceRadius, 1500d);
        for (int du = -(int)radius; du <= radius; du += 8)
        for (int dw = -(int)radius; dw <= radius; dw += 8)
        {
            long a = mountain.Around + du; int x = mountain.Across + dw;
            if (x < 2 || x > Width - 4) continue;
            float h = s.GetPresentationSurfaceHeight(a, x);
            double slope = Math.Max(Math.Abs(s.GetPresentationSurfaceHeight(a + 2, x) - h), Math.Abs(s.GetPresentationSurfaceHeight(a, x + 2) - h));
            if (slope > best) { best = slope; bestAt = (a, x); }
        }
        list.Add(new Probe("mountain_cut_face", bestAt.Item1, bestAt.Item2));
        Console.WriteLine($"SMOOTH_SPIKE_PROBE mountain_cut_face steepest_2m_delta={best:F2} m at ({bestAt.Item1},{bestAt.Item2}) pad={mountain.PadHeight}");
        list.Add(new Probe("ring_seam_left", Circumference - 16, main.Across));
        list.Add(new Probe("ring_seam_right", 16, main.Across));
        list.Add(new Probe("across_chunk_seam", land.Around + 40, ((land.Across / 32) + 1) * 32));
        list.Add(new Probe("generic_land", 5_000_000, 158_000));
        return list;
    }

    // --- seams: adjacent independently captured chunks agree on the shared lattice and on boundary mesh vertices -------------------
    static void CheckSeams(RingTerrainSampler s, RingTerrainSmoothBaseline baseline, EmitterSiteRegistry r, List<string> failures)
    {
        var cases = new List<(string, SmoothTerrainChunkCoordinate, int, int, int)>();
        EmitterSite land = r.Sites.ToArray().First(x => !x.IsMain && !x.IsIsland && !x.IsMountain);
        SmoothTerrainChunkCoordinate c = ChunkAt(s, land.Around, land.Across);
        long ringChunks = Circumference / 32;
        cases.Add(("around_seam", c, 1, 0, 0));
        cases.Add(("across_seam", c, 0, 0, 1));
        SmoothTerrainChunkCoordinate wrap = ChunkAt(s, Circumference - 16, r.MainSite.Across);
        cases.Add(("ring_seam", wrap, 1, 0, 0));
        foreach ((string name, SmoothTerrainChunkCoordinate a, int da, int dv, int dz) in cases)
        {
            var b = new SmoothTerrainChunkCoordinate(RingCoordMath.WrapAround(a.AroundChunk + da, ringChunks), a.VerticalChunk + dv, a.AcrossChunk + dz);
            SmoothTerrainChunkSnapshot sa = SmoothTerrainSnapshotCapture.Capture(a, baseline, NoEdits(baseline));
            SmoothTerrainChunkSnapshot sb = SmoothTerrainSnapshotCapture.Capture(b, baseline, NoEdits(baseline));
            int mismatches = 0, compared = 0;
            for (int z = -2; z <= 34; z++) for (int y = -2; y <= 34; y++) for (int x = -2; x <= 34; x++)
            {
                int bx = x - 32 * da, by = y - 32 * dv, bz = z - 32 * dz;
                if (bx < -2 || bx > 34 || by < -2 || by > 34 || bz < -2 || bz > 34) continue;
                compared++;
                if (BitConverter.SingleToInt32Bits(sa.Density(x, y, z)) != BitConverter.SingleToInt32Bits(sb.Density(bx, by, bz))) mismatches++;
            }
            // Boundary mesh vertices on the shared plane must coincide.
            SmoothTerrainMeshData ma = SmoothTerrainMesher.Build(sa), mb = SmoothTerrainMesher.Build(sb);
            var pa = BoundaryVertices(ma, da != 0, 32f);
            var pb = BoundaryVertices(mb, da != 0, 0f);
            int crack = 0;
            foreach ((float u, float w) in pa) if (!pb.Any(q => Math.Abs(q.Item1 - u) < 1e-4f && Math.Abs(q.Item2 - w) < 1e-4f)) crack++;
            foreach ((float u, float w) in pb) if (!pa.Any(q => Math.Abs(q.Item1 - u) < 1e-4f && Math.Abs(q.Item2 - w) < 1e-4f)) crack++;
            Console.WriteLine($"SMOOTH_SPIKE_SEAM {name,-12} a=({a.AroundChunk},{a.VerticalChunk},{a.AcrossChunk}) b=({b.AroundChunk},{b.VerticalChunk},{b.AcrossChunk}) shared_samples={compared} density_mismatches={mismatches} boundary_vertices={pa.Count}/{pb.Count} unmatched={crack}");
            if (mismatches != 0) failures.Add($"{name}: {mismatches} shared density samples differ");
            if (crack != 0) failures.Add($"{name}: {crack} unmatched boundary vertices (crack)");
            if (pa.Count == 0) failures.Add($"{name}: no boundary vertices to compare (probe misses the surface)");
        }
    }

    // Returns (vertical, other-horizontal) coordinates of vertices on the shared plane x==32 (around seam) or z==32 (across seam).
    static List<(float, float)> BoundaryVertices(SmoothTerrainMeshData m, bool alongAround, float plane)
    {
        var list = new List<(float, float)>();
        foreach (Vector3 v in m.Vertices)
        {
            if (alongAround && Math.Abs(v.X - plane) < 1e-5f) list.Add((v.Y, v.Z));
            else if (!alongAround && Math.Abs(v.Z - plane) < 1e-5f) list.Add((v.Y, v.X));
        }
        return list;
    }

    // --- parity ---------------------------------------------------------------------------------------------------------------------------
    // Column zero-crossings straight from the density lattice, and the real mesh sampled on a 0.25 m grid against the exact presentation surface.
    static (double ColumnMax, double RawMax, double RatioMax, int Covered, int Missing, int MultiHit) Parity(RingTerrainSampler s, SmoothTerrainChunkSnapshot snap, SmoothTerrainMeshData mesh)
    {
        SmoothTerrainChunkCoordinate c = snap.Coordinate;
        long a0 = c.Origin.AroundVoxel; int v0 = c.Origin.VerticalVoxel, z0 = c.Origin.AcrossVoxel;
        double columnMax = 0;
        for (int z = 0; z <= 32; z++) for (int x = 0; x <= 32; x++)
        {
            double truth = s.GetPresentationSurfaceHeight(RingCoordMath.WrapAround(a0 + x, Circumference), z0 + z);
            if (truth - v0 < 0.01 || truth - v0 > 31.99) continue;
            for (int y = -2; y < 34; y++)
            {
                float d0 = snap.Density(x, y, z), d1 = snap.Density(x, y + 1, z);
                if (d0 < 0 && d1 >= 0 || d0 >= 0 && d1 < 0)
                {
                    double crossing = v0 + y + d0 / (double)(d0 - d1);
                    columnMax = Math.Max(columnMax, Math.Abs(crossing - truth) - 1e-6 * Math.Abs(truth));
                    break;
                }
            }
        }
        const int n = 128;
        var height = new double[n * n];
        var hits = new byte[n * n];
        Array.Fill(height, double.NaN);
        Vector3[] vs = mesh.Vertices; int[] ix = mesh.Indices;
        for (int t = 0; t < ix.Length; t += 3)
        {
            Vector3 p0 = vs[ix[t]], p1 = vs[ix[t + 1]], p2 = vs[ix[t + 2]];
            double det = (p1.Z - p2.Z) * (p0.X - p2.X) + (p2.X - p1.X) * (p0.Z - p2.Z);
            if (Math.Abs(det) < 1e-9) continue;
            int i0 = Math.Max(0, (int)Math.Floor((Math.Min(p0.X, Math.Min(p1.X, p2.X)) - 0.125) / 0.25));
            int i1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(p0.X, Math.Max(p1.X, p2.X)) - 0.125) / 0.25));
            int j0 = Math.Max(0, (int)Math.Floor((Math.Min(p0.Z, Math.Min(p1.Z, p2.Z)) - 0.125) / 0.25));
            int j1 = Math.Min(n - 1, (int)Math.Ceiling((Math.Max(p0.Z, Math.Max(p1.Z, p2.Z)) - 0.125) / 0.25));
            for (int j = j0; j <= j1; j++) for (int i = i0; i <= i1; i++)
            {
                double x = 0.125 + i * 0.25, z = 0.125 + j * 0.25;
                double l0 = ((p1.Z - p2.Z) * (x - p2.X) + (p2.X - p1.X) * (z - p2.Z)) / det;
                double l1 = ((p2.Z - p0.Z) * (x - p2.X) + (p0.X - p2.X) * (z - p2.Z)) / det;
                double l2 = 1 - l0 - l1;
                if (l0 < -1e-9 || l1 < -1e-9 || l2 < -1e-9) continue;
                double y = l0 * p0.Y + l1 * p1.Y + l2 * p2.Y;
                int idx = j * n + i;
                if (!double.IsNaN(height[idx]) && Math.Abs(height[idx] - y) > 1e-3) hits[idx] = 2;
                height[idx] = y;
            }
        }
        double rawMax = 0, ratioMax = 0; int covered = 0, missing = 0, multi = 0;
        for (int j = 0; j < n; j++) for (int i = 0; i < n; i++)
        {
            double x = 0.125 + i * 0.25, z = 0.125 + j * 0.25;
            double truth = s.GetPresentationSurfaceHeight((double)(a0 + x), z0 + z);
            bool expected = truth - v0 > 0.05 && truth - v0 < 31.95;
            double y = height[j * n + i];
            if (double.IsNaN(y)) { if (expected) missing++; continue; }
            if (hits[j * n + i] == 2) { multi++; continue; }
            covered++;
            int qi = (int)Math.Floor(x), qj = (int)Math.Floor(z);
            long qa = RingCoordMath.WrapAround(a0 + qi, Circumference);
            double h00 = s.GetPresentationSurfaceHeight(qa, z0 + qj), h10 = s.GetPresentationSurfaceHeight(qa + 1, z0 + qj);
            double h01 = s.GetPresentationSurfaceHeight(qa, z0 + qj + 1), h11 = s.GetPresentationSurfaceHeight(qa + 1, z0 + qj + 1);
            double twist = h00 + h11 - h10 - h01;
            // Marching cells cannot resolve more than their own height: add a quarter of the steepest corner-to-corner delta of the square.
            double steep = Math.Max(Math.Max(Math.Abs(h10 - h00), Math.Abs(h01 - h00)), Math.Max(Math.Abs(h11 - h10), Math.Abs(h11 - h01)));
            double tolerance = 0.5 * Math.Abs(twist) + 0.25 * Math.Max(0.0, steep - 1.0) + 0.02 + 1e-6 * Math.Abs(truth);
            double error = Math.Abs(y + v0 - truth);
            rawMax = Math.Max(rawMax, error);
            ratioMax = Math.Max(ratioMax, error / tolerance);
        }
        return (columnMax, rawMax, ratioMax, covered, missing, multi);
    }

    // --- golden ---------------------------------------------------------------------------------------------------------------------------
    static ulong HashFloats(ReadOnlySpan<float> values)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (float f in values) { h ^= (uint)BitConverter.SingleToInt32Bits(f); h *= 0x100000001B3UL; }
        return h;
    }

    static ulong HashInts(ReadOnlySpan<int> values)
    {
        ulong h = 0xCBF29CE484222325UL;
        foreach (int v in values) { h ^= (uint)v; h *= 0x100000001B3UL; }
        return h;
    }

    static void CheckGolden(Golden actual, bool capture, List<string> failures)
    {
        string path = ProjectSettings.GlobalizePath("res://tools/fixtures/smooth_runtime_gen17_golden.json");
        if (capture)
        {
            if (File.Exists(path)) { failures.Add("refusing to overwrite existing golden fixture " + path); return; }
            File.WriteAllText(path, JsonSerializer.Serialize(actual, JsonOptions) + System.Environment.NewLine);
            Console.WriteLine($"SMOOTH_SPIKE_GOLDEN captured {actual.Cases.Count} cases to {path}");
            return;
        }
        if (!File.Exists(path)) { failures.Add("golden fixture missing: " + path); return; }
        Golden? expected = JsonSerializer.Deserialize<Golden>(File.ReadAllText(path), JsonOptions);
        if (expected == null) { failures.Add("golden fixture unreadable"); return; }
        if (expected.Hash != actual.Hash) failures.Add($"terrain definition hash {actual.Hash} != golden {expected.Hash}");
        if (expected.Revision != actual.Revision) failures.Add($"baseline revision {actual.Revision} != golden {expected.Revision}");
        if (expected.Cases.Count != actual.Cases.Count) failures.Add("golden case count differs");
        foreach (Case e in expected.Cases)
        {
            Case? a = actual.Cases.FirstOrDefault(x => x.Name == e.Name);
            if (a == null) { failures.Add("golden case missing: " + e.Name); continue; }
            if (a.Around != e.Around || a.Vertical != e.Vertical || a.Across != e.Across) failures.Add($"{e.Name}: chunk coordinate moved");
            if (a.MeshHash != e.MeshHash) failures.Add($"{e.Name}: mesh hash {a.MeshHash} != golden {e.MeshHash}");
            if (a.DensityHash != e.DensityHash) failures.Add($"{e.Name}: density hash {a.DensityHash} != golden {e.DensityHash}");
            if (a.MaterialHash != e.MaterialHash) failures.Add($"{e.Name}: material hash differs");
            if (a.Vertices != e.Vertices || a.Triangles != e.Triangles) failures.Add($"{e.Name}: mesh size differs");
            for (int i = 0; i < e.Samples.Count && i < a.Samples.Count; i++) if (a.Samples[i].Bits != e.Samples[i].Bits) failures.Add($"{e.Name}: density sample {e.Samples[i].Index} differs");
        }
        Console.WriteLine($"SMOOTH_SPIKE_GOLDEN compared {actual.Cases.Count} cases");
    }

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };

    sealed class Golden { public string Format { get; set; } = ""; public string Hash { get; set; } = ""; public string Revision { get; set; } = ""; public List<Case> Cases { get; set; } = new(); }
    sealed class Case
    {
        public string Name { get; set; } = ""; public long Around { get; set; } public int Vertical { get; set; } public int Across { get; set; }
        public string MeshHash { get; set; } = ""; public string DensityHash { get; set; } = ""; public string MaterialHash { get; set; } = "";
        public int Vertices { get; set; } public int Triangles { get; set; }
        public List<Sample> Samples { get; set; } = new();
    }
    sealed class Sample { public int Index { get; set; } public int Bits { get; set; } }
}
