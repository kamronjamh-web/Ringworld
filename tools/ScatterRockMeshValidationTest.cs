using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Godot;
using RingworldTechDemo.Vegetation.Scatter;
using Gen = RingworldTechDemo.Tools.PrepareRockAssets;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T5-6 (docs/work-orders/terrain-vegetation/T5.md, design B1): validates the procedural rock mesh set and the committed prepared artifact.
/// Counts 16/8/6 x 3 LODs, one topology per (group, LOD), triangle budgets and LOD ratios, closed meshes without degenerate triangles, the base embeds, boulder LOD vertices on LOD0's vertex set,
/// outcrop bed tops horizontal, regeneration identical to the committed bin and manifest, artifact size, and RockCatalog.Load() building the carriers, textures and collision data.
/// Prints SCATTER_ROCK_MESH_VALIDATION=PASS. Headless: tools/run_tests.ps1 -Tests scatter_rock_mesh_validation_test.
/// With the user arg --png it also writes the CPU-rendered contact sheet (no GPU).
/// </summary>
public partial class ScatterRockMeshValidationTest : RefCounted
{
    /// <summary>The seed the regeneration check uses; the committed artifact was made with <see cref="Gen.CommittedSeed"/>.</summary>
    private const int RegenSeed = Gen.CommittedSeed;
    private const string BinRes = "res://resources/rocks/prepared/rock_catalog.bin", ManifestRes = "res://resources/rocks/prepared/rock_catalog.manifest.json";
    private const string PngPath = "c:/Projects/Ringworld/.godot/audit_baseline/common/t5/t5-6_rock_contact_sheet.png";
    private static readonly int[] BoulderTris = { 1280, 320, 80 }, OutcropTargets = { 1700, 440, 190 }, ClusterTris = { 480, 120, 48 };

    private readonly List<string> _failures = new();
    private void Check(bool ok, string message) { if (!ok && _failures.Count < 40) _failures.Add(message); }
    private static void Detail(string text) => Console.WriteLine("SCATTER_ROCK_MESH_VALIDATION_DETAIL " + text);

    public int Run()
    {
        try
        {
            Gen.RockSet set = Gen.Generate(RegenSeed);
            Counts(set);
            Budgets(set);
            Closed(set);
            Bases(set);
            Subset(set);
            Beds(set);
            Orientation(set);
            Regeneration(set);
            Loader();
            if (OS.GetCmdlineUserArgs().Contains("--png")) ContactSheet(set);
        }
        catch (Exception e) { Console.Error.WriteLine("SCATTER_ROCK_MESH_VALIDATION=FAIL exception " + e); return 1; }
        if (_failures.Count != 0) { Console.Error.WriteLine("SCATTER_ROCK_MESH_VALIDATION=FAIL " + string.Join(" | ", _failures)); return 1; }
        Console.WriteLine("SCATTER_ROCK_MESH_VALIDATION=PASS");
        return 0;
    }

    private static int Tris(Gen.RockLod lod) => lod.Indices.Length / 3;

    // a. counts, shared topology, array sizes
    private void Counts(Gen.RockSet set)
    {
        Check(set.Groups.Length == 3, "expected three groups");
        int[] want = { 8, 16, 6 }; // outcrops, boulders, cobbles
        for (int g = 0; g < 3; g++)
        {
            Gen.RockGroup group = set.Groups[g];
            Check(group.VariantCount == want[g], $"{group.Name}: {group.VariantCount} variants, expected {want[g]}");
            Check(group.Lods.Length == 3, group.Name + ": LOD count");
            for (int l = 0; l < 3; l++)
            {
                Gen.RockLod lod = group.Lods[l];
                Check(lod.Q.Length == group.VariantCount && lod.Normals.Length == group.VariantCount, $"{group.Name} LOD{l}: per-variant array count");
                for (int v = 0; v < group.VariantCount; v++)
                    Check(lod.Q[v].Length == lod.VertexCount * 3 && lod.Normals[v].Length == lod.VertexCount * 3, $"{group.Name} LOD{l} v{v}: array length");
                int max = lod.Indices.Length == 0 ? -1 : lod.Indices.Max();
                Check(lod.Indices.Length % 3 == 0 && max < lod.VertexCount, $"{group.Name} LOD{l}: index buffer");
                Detail($"{group.Name} LOD{l}: {lod.VertexCount} vertices, {Tris(lod)} triangles, {group.VariantCount} variants");
            }
        }
    }

    // b. triangle budgets and LOD ratios
    private void Budgets(Gen.RockSet set)
    {
        Gen.RockGroup boulders = set.Group(RingScatterRenderGroup.Boulders), outcrops = set.Group(RingScatterRenderGroup.Outcrops), cobbles = set.Group(RingScatterRenderGroup.Cobbles);
        int[] bv = { 642, 162, 42 };
        for (int l = 0; l < 3; l++)
        {
            Check(Tris(boulders.Lods[l]) == BoulderTris[l] && boulders.Lods[l].VertexCount == bv[l], $"boulders LOD{l}: {Tris(boulders.Lods[l])} tris / {boulders.Lods[l].VertexCount} verts");
            Check(Math.Abs(Tris(outcrops.Lods[l]) - OutcropTargets[l]) <= OutcropTargets[l] * 0.10, $"outcrops LOD{l}: {Tris(outcrops.Lods[l])} tris, target {OutcropTargets[l]} +-10 %");
            Check(Math.Abs(Tris(cobbles.Lods[l]) - ClusterTris[l]) <= ClusterTris[l] * 0.10, $"cobbles LOD{l}: {Tris(cobbles.Lods[l])} tris, target {ClusterTris[l]}");
        }
        foreach (Gen.RockGroup g in set.Groups)
            Detail($"{g.Name} LOD ratios: {(double)Tris(g.Lods[0]) / Tris(g.Lods[1]):0.00} and {(double)Tris(g.Lods[1]) / Tris(g.Lods[2]):0.00}");
        Check(Tris(boulders.Lods[0]) == 4 * Tris(boulders.Lods[1]) && Tris(boulders.Lods[1]) == 4 * Tris(boulders.Lods[2]), "boulder LOD ratio is not exactly 4");
    }

    // c. closed, no degenerate triangles except collapsed stones
    private void Closed(Gen.RockSet set)
    {
        foreach (Gen.RockGroup g in set.Groups)
            for (int l = 0; l < 3; l++)
            {
                Gen.RockLod lod = g.Lods[l];
                for (int v = 0; v < g.VariantCount; v++)
                {
                    if (g.Id == RingScatterRenderGroup.Cobbles)
                    {
                        int stoneTris = Tris(lod) / g.StonesPerVariant;
                        for (int s = 0; s < g.StonesPerVariant; s++)
                        {
                            bool active = s < g.ActiveStones[v];
                            int degenerate = 0;
                            bool closed = ClosedRange(lod, v, s * stoneTris, stoneTris, false, ref degenerate);
                            if (active) Check(closed && degenerate == 0, $"cobbles LOD{l} v{v} stone {s}: open or degenerate ({degenerate})");
                            else Check(degenerate == stoneTris, $"cobbles LOD{l} v{v} stone {s}: collapsed stone is not fully degenerate");
                        }
                    }
                    else
                    {
                        int degenerate = 0;
                        // Outcrop ledges and caps use split vertices (sharp edges): welded by position, the surface must still be closed.
                        bool closed = ClosedRange(lod, v, 0, Tris(lod), g.Id == RingScatterRenderGroup.Outcrops, ref degenerate);
                        Check(closed && degenerate == 0, $"{g.Name} LOD{l} v{v}: open or degenerate triangles ({degenerate})");
                    }
                }
            }
    }

    private static bool ClosedRange(Gen.RockLod lod, int variant, int firstTri, int triCount, bool weld, ref int degenerate)
    {
        int[] q = lod.Q[variant];
        var welded = new Dictionary<(int, int, int), int>();
        int Id(int vertex)
        {
            if (!weld) return vertex;
            var key = (q[vertex * 3], q[vertex * 3 + 1], q[vertex * 3 + 2]);
            if (!welded.TryGetValue(key, out int id)) welded[key] = id = welded.Count;
            return id;
        }
        var edges = new Dictionary<(int, int), int>();
        for (int t = firstTri; t < firstTri + triCount; t++)
        {
            int a = lod.Indices[t * 3], b = lod.Indices[t * 3 + 1], c = lod.Indices[t * 3 + 2];
            long e1x = q[b * 3] - q[a * 3], e1y = q[b * 3 + 1] - q[a * 3 + 1], e1z = q[b * 3 + 2] - q[a * 3 + 2];
            long e2x = q[c * 3] - q[a * 3], e2y = q[c * 3 + 1] - q[a * 3 + 1], e2z = q[c * 3 + 2] - q[a * 3 + 2];
            if (e1y * e2z - e1z * e2y == 0 && e1z * e2x - e1x * e2z == 0 && e1x * e2y - e1y * e2x == 0) { degenerate++; continue; }
            int ia = Id(a), ib = Id(b), ic = Id(c);
            foreach ((int from, int to) in new[] { (ia, ib), (ib, ic), (ic, ia) })
                edges[(from, to)] = edges.GetValueOrDefault((from, to)) + 1;
        }
        if (degenerate == triCount) return true;
        foreach (((int from, int to), int count) in edges)
            if (count != 1 || edges.GetValueOrDefault((to, from)) != 1) return false;
        return true;
    }

    // d. bases and sizes
    private void Bases(Gen.RockSet set)
    {
        Gen.RockGroup boulders = set.Group(RingScatterRenderGroup.Boulders), outcrops = set.Group(RingScatterRenderGroup.Outcrops), cobbles = set.Group(RingScatterRenderGroup.Cobbles);
        for (int l = 0; l < 3; l++)
            for (int v = 0; v < boulders.VariantCount; v++)
            {
                (double min, double max) = YRange(boulders.Lods[l], v, 0, boulders.Lods[l].VertexCount);
                Check(Math.Abs(min + 0.25) <= 1e-3, $"boulders LOD{l} v{v}: min y {min}");
                if (l == 0) Check(Math.Abs(max - 1) <= 1e-3, $"boulders LOD0 v{v}: above-ground height {max}");
                else Check(max <= 1 + 1e-3 && max >= 0.7, $"boulders LOD{l} v{v}: max y {max}");
            }
        for (int l = 0; l < 3; l++)
            for (int v = 0; v < outcrops.VariantCount; v++)
            {
                (double min, double max) = YRange(outcrops.Lods[l], v, 0, outcrops.Lods[l].VertexCount);
                Check(Math.Abs(min - Gen.OutcropBaseY) <= 1e-3, $"outcrops LOD{l} v{v}: min y {min}");
                Check(max >= 0.25 - 1e-3 && max <= 0.6 + 1e-3, $"outcrops LOD{l} v{v}: top {max} outside 0.25..0.6");
                if (l == 0)
                {
                    double xMin = double.MaxValue, xMax = double.MinValue;
                    for (int i = 0; i < outcrops.Lods[0].VertexCount; i++) { double x = outcrops.Lods[0].X(v, i); xMin = Math.Min(xMin, x); xMax = Math.Max(xMax, x); }
                    Check(Math.Abs(xMax - xMin - 1) <= 2e-3, $"outcrops LOD0 v{v}: horizontal length {xMax - xMin}");
                }
            }
        // T5-10b: the class row's buried-base footprint radius covers every outcrop vertex (and is not slack by more than 10 %), so the pure placement rule needs no catalog.
        double outcropRadius = 0;
        for (int l = 0; l < 3; l++)
            for (int v = 0; v < outcrops.VariantCount; v++)
                for (int i = 0; i < outcrops.Lods[l].VertexCount; i++) outcropRadius = Math.Max(outcropRadius, Math.Sqrt(outcrops.Lods[l].X(v, i) * outcrops.Lods[l].X(v, i) + outcrops.Lods[l].Z(v, i) * outcrops.Lods[l].Z(v, i)));
        float rowRadius = RingScatterClassTable.Production.Rows.First(r => r.Key == "rock.outcrop").BuriedFootprintRadius;
        Check(rowRadius >= outcropRadius && rowRadius <= outcropRadius * 1.1 + 1e-6, $"outcrop row BuriedFootprintRadius {rowRadius} vs measured max horizontal radius {outcropRadius:F4}");
        Detail($"outcrop max horizontal radius {outcropRadius:F4} (row BuriedFootprintRadius {rowRadius})");
        Gen.RockLod c0 = cobbles.Lods[0];
        double maxRadius = 0;
        for (int v = 0; v < cobbles.VariantCount; v++)
            for (int s = 0; s < cobbles.ActiveStones[v]; s++)
            {
                (double min, double max) = YRange(c0, v, s * c0.StoneVertexCount, c0.StoneVertexCount);
                Check(Math.Abs(min + 0.3 * (max - min)) <= 1e-2, $"cobbles v{v} stone {s}: embed {-min / (max - min):0.000} of its height, expected 0.3");
                for (int i = s * c0.StoneVertexCount; i < (s + 1) * c0.StoneVertexCount; i++) maxRadius = Math.Max(maxRadius, Math.Sqrt(c0.X(v, i) * c0.X(v, i) + c0.Z(v, i) * c0.Z(v, i)));
            }
        Check(maxRadius <= 0.6, $"cobble clusters extend {maxRadius:0.00} from the centre (footprint diameter 1)");
        Detail($"cobble cluster maximum radius {maxRadius:0.000}");
    }

    private static (double Min, double Max) YRange(Gen.RockLod lod, int variant, int first, int count)
    {
        double min = double.MaxValue, max = double.MinValue;
        for (int i = first; i < first + count; i++) { double y = lod.Y(variant, i); min = Math.Min(min, y); max = Math.Max(max, y); }
        return (min, max);
    }

    // e. boulder LOD1 and LOD2 vertices coincide with LOD0 vertices
    private void Subset(Gen.RockSet set)
    {
        Gen.RockGroup boulders = set.Group(RingScatterRenderGroup.Boulders);
        double worst = 0;
        for (int v = 0; v < boulders.VariantCount; v++)
            for (int l = 1; l < 3; l++)
                for (int i = 0; i < boulders.Lods[l].VertexCount; i++)
                {
                    double best = double.MaxValue;
                    for (int j = 0; j < boulders.Lods[0].VertexCount; j++)
                    {
                        double dx = boulders.Lods[l].X(v, i) - boulders.Lods[0].X(v, j), dy = boulders.Lods[l].Y(v, i) - boulders.Lods[0].Y(v, j), dz = boulders.Lods[l].Z(v, i) - boulders.Lods[0].Z(v, j);
                        best = Math.Min(best, Math.Sqrt(dx * dx + dy * dy + dz * dz));
                        if (best == 0) break;
                    }
                    worst = Math.Max(worst, best);
                }
        Check(worst <= 1e-4, $"a boulder LOD1/LOD2 vertex is {worst} from LOD0's vertex set");
        Detail($"boulder LOD vertices lie on LOD0's vertex set (worst distance {worst})");
    }

    // i. outcrop bed tops are horizontal
    private void Beds(Gen.RockSet set)
    {
        Gen.RockGroup outcrops = set.Group(RingScatterRenderGroup.Outcrops);
        int[] beds = { 7, 4, 4 };
        for (int l = 0; l < 3; l++)
        {
            Gen.RockLod lod = outcrops.Lods[l];
            Check(lod.BedTopRings.Count == beds[l], $"outcrops LOD{l}: {lod.BedTopRings.Count} beds, expected {beds[l]}");
            for (int v = 0; v < outcrops.VariantCount; v++)
            {
                double prevTop = double.MinValue;
                foreach (int[] rings in lod.BedTopRings)
                {
                    double y = lod.Y(v, rings[0]);
                    foreach (int start in rings)
                        for (int k = 0; k < lod.RingSize; k++)
                            Check(Math.Abs(lod.Y(v, start + k) - y) <= 1e-6, $"outcrops LOD{l} v{v}: a bed top is not horizontal");
                    Check(y > prevTop, $"outcrops LOD{l} v{v}: bed tops are not rising");
                    prevTop = y;
                }
            }
        }
    }

    // outward orientation: with Godot's clockwise front faces the signed volume is negative
    private void Orientation(Gen.RockSet set)
    {
        foreach (Gen.RockGroup g in set.Groups)
            for (int l = 0; l < 3; l++)
                for (int v = 0; v < g.VariantCount; v++)
                {
                    Gen.RockLod lod = g.Lods[l];
                    if (g.Id == RingScatterRenderGroup.Cobbles)
                    {
                        int stoneTris = Tris(lod) / g.StonesPerVariant;
                        for (int s = 0; s < g.ActiveStones[v]; s++) Check(Volume(lod, v, s * stoneTris, stoneTris) < 0, $"cobbles LOD{l} v{v} stone {s}: not outward facing");
                    }
                    else Check(Volume(lod, v, 0, Tris(lod)) < 0, $"{g.Name} LOD{l} v{v}: not outward facing");
                }
    }

    private static double Volume(Gen.RockLod lod, int variant, int firstTri, int count)
    {
        double sum = 0;
        for (int t = firstTri; t < firstTri + count; t++)
        {
            int a = lod.Indices[t * 3], b = lod.Indices[t * 3 + 1], c = lod.Indices[t * 3 + 2];
            double ax = lod.X(variant, a), ay = lod.Y(variant, a), az = lod.Z(variant, a), bx = lod.X(variant, b), by = lod.Y(variant, b), bz = lod.Z(variant, b), cx = lod.X(variant, c), cy = lod.Y(variant, c), cz = lod.Z(variant, c);
            sum += ax * (by * cz - bz * cy) + ay * (bz * cx - bx * cz) + az * (bx * cy - by * cx);
        }
        return sum / 6;
    }

    // f, g. regeneration identical to the committed artifact, size under 8 MB
    private void Regeneration(Gen.RockSet set)
    {
        string bin = ProjectSettings.GlobalizePath(BinRes), manifest = ProjectSettings.GlobalizePath(ManifestRes);
        Check(File.Exists(bin) && File.Exists(manifest), "the committed rock artifact or manifest is missing");
        if (!File.Exists(bin) || !File.Exists(manifest)) return;
        byte[] committed = File.ReadAllBytes(bin), regenerated = Gen.Serialize(set);
        Check(committed.AsSpan().SequenceEqual(regenerated), $"regenerating with seed {RegenSeed} gives different artifact bytes ({regenerated.Length} vs committed {committed.Length})");
        var expected = new Dictionary<string, string>();
        using (System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(manifest)))
        {
            foreach (System.Text.Json.JsonElement m in doc.RootElement.GetProperty("meshes").EnumerateArray())
                expected[m.GetProperty("group").GetString() + "/" + m.GetProperty("lod").GetInt32() + "/" + m.GetProperty("variant").GetInt32()] = m.GetProperty("sha256").GetString()!;
            Check(doc.RootElement.GetProperty("artifact_sha256").GetString() == Convert.ToHexString(SHA256.HashData(committed)), "manifest artifact_sha256 does not match the committed bin");
            Check(doc.RootElement.GetProperty("seed").GetInt32() == RegenSeed, "manifest seed differs from the regeneration seed");
        }
        int mismatches = 0, count = 0;
        foreach ((string key, string hash) in Gen.MeshHashes(set)) { count++; if (!expected.TryGetValue(key, out string? want) || want != hash) mismatches++; }
        Check(mismatches == 0 && count == expected.Count, $"{mismatches} regenerated mesh hashes differ from the manifest ({count} vs {expected.Count} meshes)");
        Check(Gen.Check(bin, manifest), "PrepareRockAssets.Check failed");
        long total = new FileInfo(bin).Length + new FileInfo(manifest).Length;
        Check(total < 8L * 1024 * 1024, $"artifact is {total} bytes, over 8 MB");
        Detail($"artifact {new FileInfo(bin).Length} bytes + manifest {new FileInfo(manifest).Length} bytes = {total} (limit 8388608), sha256 {Convert.ToHexString(SHA256.HashData(committed))}");
    }

    // h. RockCatalog.Load()
    private void Loader()
    {
        RockCatalog catalog = RockCatalog.Load();
        Check(ReferenceEquals(catalog, RockCatalog.Load()), "RockCatalog.Load is not cached");
        Check(RockCatalog.CatalogVersion == 2, "catalog version");
        Check(catalog.TotalBytes == new FileInfo(ProjectSettings.GlobalizePath(BinRes)).Length, "TotalBytes");
        Check(RockCatalog.ArtifactSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ProjectSettings.GlobalizePath(BinRes)))), "ArtifactSha256 differs from the bin's SHA-256");
        Gen.RockSet set = Gen.Generate(RegenSeed);
        foreach (RingScatterRenderGroup id in new[] { RingScatterRenderGroup.Outcrops, RingScatterRenderGroup.Boulders, RingScatterRenderGroup.Cobbles })
        {
            RockGroupCatalog g = catalog.Group(id);
            Gen.RockGroup src = set.Group(id);
            Check(g.VariantCount == src.VariantCount && g.Lods.Length == 3, $"{id}: loaded counts");
            for (int l = 0; l < 3; l++)
            {
                RockLodMesh lod = g.Lods[l];
                Check(lod.VertexCount == src.Lods[l].VertexCount && lod.TriangleCount == Tris(src.Lods[l]), $"{id} LOD{l}: loaded vertex/triangle counts");
                Check(lod.Carrier.GetSurfaceCount() == 1 && lod.Carrier.CustomAabb.Size.Length() > 0.5f, $"{id} LOD{l}: carrier surface or AABB");
                Check(lod.Positions.GetWidth() == lod.VertexCount && lod.Positions.GetHeight() == g.VariantCount && lod.Positions.GetImage().GetFormat() == Image.Format.Rgbaf, $"{id} LOD{l}: positions texture");
                Check(lod.Normals.GetWidth() == lod.VertexCount && lod.Normals.GetHeight() == g.VariantCount && lod.Normals.GetImage().GetFormat() == Image.Format.Rgbah, $"{id} LOD{l}: normals texture");
                Color p = lod.Positions.GetImage().GetPixel(lod.VertexCount - 1, g.VariantCount - 1);
                int last = g.VariantCount - 1, vi = lod.VertexCount - 1;
                Check(Math.Abs(p.R - src.Lods[l].X(last, vi)) < 1e-6 && Math.Abs(p.G - src.Lods[l].Y(last, vi)) < 1e-6 && Math.Abs(p.A - p.G) < 1e-6, $"{id} LOD{l}: positions texel (w must equal y)");
                Aabb box = lod.UnitAabb;
                for (int v = 0; v < g.VariantCount; v++)
                    for (int i = 0; i < lod.VertexCount; i++) Check(box.Grow(1e-4f).HasPoint(new Vector3((float)src.Lods[l].X(v, i), (float)src.Lods[l].Y(v, i), (float)src.Lods[l].Z(v, i))), $"{id} LOD{l}: UnitAabb misses a vertex");
            }
            if (id == RingScatterRenderGroup.Boulders)
                for (int v = 0; v < g.VariantCount; v++) Check(g.HullPoints(v).Length == 42, $"boulder {v}: {g.HullPoints(v).Length} hull points, expected 42");
            if (id == RingScatterRenderGroup.Outcrops)
                for (int v = 0; v < g.VariantCount; v++) Check(g.TriangleSoup(v).Length == Tris(src.Lods[1]) * 3, $"outcrop {v}: triangle soup length");
        }
        Detail($"RockCatalog loaded: {catalog.TotalBytes} bytes");
    }

    // ------------------------------------------------------------------ contact sheet (CPU render)

    private const int CellW = 240, CellH = 230, Cols = 6, Rows = 6;

    private void ContactSheet(Gen.RockSet set)
    {
        int w = Cols * CellW, h = Rows * CellH;
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { rgb[i * 3] = 96; rgb[i * 3 + 1] = 112; rgb[i * 3 + 2] = 128; }
        var depth = new float[w * h];
        Array.Fill(depth, float.MinValue);
        Gen.RockGroup boulders = set.Group(RingScatterRenderGroup.Boulders), outcrops = set.Group(RingScatterRenderGroup.Outcrops), cobbles = set.Group(RingScatterRenderGroup.Cobbles);
        var items = new List<(Gen.RockGroup G, int V, int L, double Scale, string Label)>();
        for (int v = 0; v < boulders.VariantCount; v++) items.Add((boulders, v, 0, 95, "B" + v));
        for (int v = 0; v < outcrops.VariantCount; v++) items.Add((outcrops, v, 0, 130, "O" + v));
        for (int v = 0; v < cobbles.VariantCount; v++) items.Add((cobbles, v, 0, 200, "C" + v));
        for (int l = 0; l < 3; l++) items.Add((boulders, 0, l, 95, "B0 L" + l));
        for (int l = 0; l < 3; l++) items.Add((outcrops, 0, l, 130, "O0 L" + l));
        for (int i = 0; i < items.Count; i++)
        {
            (Gen.RockGroup g, int v, int l, double scale, string label) = items[i];
            int cx = (i % Cols) * CellW, cy = (i / Cols) * CellH;
            // ground line, so the embed reads
            for (int x = cx + 8; x < cx + CellW - 8; x++) { int idx = ((cy + (int)(CellH * 0.72)) * w + x) * 3; rgb[idx] = 70; rgb[idx + 1] = 84; rgb[idx + 2] = 96; }
            DrawRock(rgb, depth, w, g, v, l, scale, cx, cy);
            DrawText(rgb, w, cx + 8, cy + 6, label, 2);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(PngPath)!);
        Error err = Image.CreateFromData(w, h, false, Image.Format.Rgb8, rgb).SavePng(PngPath);
        Check(err == Error.Ok, "contact sheet could not be written");
        Detail("contact sheet " + PngPath);
    }

    private static void DrawRock(byte[] rgb, float[] depth, int width, Gen.RockGroup g, int variant, int lodIndex, double scale, int cellX, int cellY)
    {
        const double Az = 35 * Math.PI / 180, El = 30 * Math.PI / 180;
        double cA = Math.Cos(Az), sA = Math.Sin(Az), cE = Math.Cos(El), sE = Math.Sin(El);
        double vx = -cE * sA, vy = sE, vz = cE * cA; // towards the camera
        double lx = -0.45, ly = 0.8, lz = 0.4, ll = Math.Sqrt(lx * lx + ly * ly + lz * lz);
        lx /= ll; ly /= ll; lz /= ll;
        Gen.RockLod lod = g.Lods[lodIndex];
        int n = lod.VertexCount;
        var sx = new double[n]; var sy = new double[n]; var sd = new double[n]; var wy = new double[n];
        double originX = cellX + CellW / 2.0, originY = cellY + CellH * 0.72;
        for (int i = 0; i < n; i++)
        {
            double x = lod.X(variant, i), y = lod.Y(variant, i), z = lod.Z(variant, i);
            double x1 = x * cA + z * sA, z1 = -x * sA + z * cA;
            sx[i] = originX + x1 * scale; sy[i] = originY - (y * cE - z1 * sE) * scale; sd[i] = y * sE + z1 * cE; wy[i] = y;
        }
        for (int t = 0; t < lod.Indices.Length; t += 3)
        {
            int a = lod.Indices[t], b = lod.Indices[t + 1], c = lod.Indices[t + 2];
            double e1x = lod.X(variant, b) - lod.X(variant, a), e1y = lod.Y(variant, b) - lod.Y(variant, a), e1z = lod.Z(variant, b) - lod.Z(variant, a);
            double e2x = lod.X(variant, c) - lod.X(variant, a), e2y = lod.Y(variant, c) - lod.Y(variant, a), e2z = lod.Z(variant, c) - lod.Z(variant, a);
            // Godot order is clockwise from outside: the outward normal is cross(e2, e1).
            double fnx = e2y * e1z - e2z * e1y, fny = e2z * e1x - e2x * e1z, fnz = e2x * e1y - e2y * e1x;
            if (fnx * vx + fny * vy + fnz * vz <= 0) continue;
            double minX = Math.Min(sx[a], Math.Min(sx[b], sx[c])), maxX = Math.Max(sx[a], Math.Max(sx[b], sx[c])), minY = Math.Min(sy[a], Math.Min(sy[b], sy[c])), maxY = Math.Max(sy[a], Math.Max(sy[b], sy[c]));
            int x0 = Math.Max(cellX, (int)Math.Floor(minX)), x1i = Math.Min(cellX + CellW - 1, (int)Math.Ceiling(maxX)), y0 = Math.Max(cellY, (int)Math.Floor(minY)), y1i = Math.Min(cellY + CellH - 1, (int)Math.Ceiling(maxY));
            double den = (sy[b] - sy[c]) * (sx[a] - sx[c]) + (sx[c] - sx[b]) * (sy[a] - sy[c]);
            if (Math.Abs(den) < 1e-12) continue;
            for (int py = y0; py <= y1i; py++)
                for (int px = x0; px <= x1i; px++)
                {
                    double qx = px + 0.5, qy = py + 0.5;
                    double w0 = ((sy[b] - sy[c]) * (qx - sx[c]) + (sx[c] - sx[b]) * (qy - sy[c])) / den, w1 = ((sy[c] - sy[a]) * (qx - sx[c]) + (sx[a] - sx[c]) * (qy - sy[c])) / den, w2 = 1 - w0 - w1;
                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                    if (w0 * wy[a] + w1 * wy[b] + w2 * wy[c] < 0) continue; // below the ground
                    float d = (float)(w0 * sd[a] + w1 * sd[b] + w2 * sd[c]);
                    int pix = py * width + px;
                    if (d <= depth[pix]) continue;
                    depth[pix] = d;
                    double nx = w0 * lod.Normals[variant][a * 3] + w1 * lod.Normals[variant][b * 3] + w2 * lod.Normals[variant][c * 3];
                    double ny = w0 * lod.Normals[variant][a * 3 + 1] + w1 * lod.Normals[variant][b * 3 + 1] + w2 * lod.Normals[variant][c * 3 + 1];
                    double nz = w0 * lod.Normals[variant][a * 3 + 2] + w1 * lod.Normals[variant][b * 3 + 2] + w2 * lod.Normals[variant][c * 3 + 2];
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    double lambert = nl < 1e-9 ? 0 : Math.Max(0, (nx * lx + ny * ly + nz * lz) / nl);
                    byte v = (byte)Math.Clamp((0.16 + 0.84 * lambert) * 200, 0, 255);
                    rgb[pix * 3] = v; rgb[pix * 3 + 1] = v; rgb[pix * 3 + 2] = (byte)Math.Min(255, v + 4);
                }
        }
    }

    private static readonly Dictionary<char, string> Glyphs = new()
    {
        ['B'] = "110101110101110", ['O'] = "010101101101010", ['C'] = "011100100100011", ['L'] = "100100100100111", [' '] = "000000000000000",
        ['0'] = "111101101101111", ['1'] = "010110010010111", ['2'] = "110001010100111", ['3'] = "110001010001110", ['4'] = "101101111001001",
        ['5'] = "111100110001110", ['6'] = "011100111101111", ['7'] = "111001010010010", ['8'] = "111101111101111", ['9'] = "111101111001110",
    };

    private static void DrawText(byte[] rgb, int width, int x, int y, string text, int scale)
    {
        foreach (char ch in text)
        {
            if (Glyphs.TryGetValue(ch, out string? bits))
                for (int gy = 0; gy < 5; gy++)
                    for (int gx = 0; gx < 3; gx++)
                        if (bits[gy * 3 + gx] == '1')
                            for (int dy = 0; dy < scale; dy++)
                                for (int dx = 0; dx < scale; dx++)
                                {
                                    int idx = ((y + gy * scale + dy) * width + x + gx * scale + dx) * 3;
                                    rgb[idx] = 250; rgb[idx + 1] = 240; rgb[idx + 2] = 150;
                                }
            x += 4 * scale;
        }
    }
}
