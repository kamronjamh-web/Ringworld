using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>One resolved camera pose of the landscape and vastness reports (fixture row, docs/landscape-audit-t0-spec.md 2.4).</summary>
public sealed class LandscapePose
{
    public string Name = "";
    public string[] Reports = Array.Empty<string>();
    public string Class = "";
    public string Habitat = "None";
    public double Around, Across, Ground;
    public string VerticalMode = "agl";
    public double Lift;
    public double Yaw, Pitch;
    public string Notes = "";
}

/// <summary>
/// Resolves every pose of the landscape (T0) and vastness (V0) reports with deterministic scans over the shared lattice and writes / validates the committed
/// fixture tools/fixtures/landscape_poses_v8_s1337.json. Every rule is the one in docs/landscape-audit-t0-spec.md 2.4; a rule with no candidate fails by name.
/// </summary>
public static class LandscapePoseFinder
{
    public const string FixturePath = "res://tools/fixtures/landscape_poses_v8_s1337.json";
    public const double EyeHeight = 1.7;
    const double Circ = LandscapeWorld.Circumference;
    static readonly string[] ExpectedNames =
    {
        "plains_eye", "hills_eye", "hills_300m", "hills_2km", "coast_eye", "forest_interior_eye", "forest_edge_eye", "forest_clearing_eye", "saved_18km",
        "v0_centre_eye", "v0_wall_lo_eye", "v0_wall_hi_eye", "v0_ridge_eye", "v0_alt_12km"
    };

    // ------------------------------------------------------------------ helpers

    static double Wrap(double around) { double r = around % Circ; return r < 0 ? r + Circ : r; }
    static double Cos(double deg) => Math.Cos(deg * Math.PI / 180d);
    static double Sin(double deg) => Math.Sin(deg * Math.PI / 180d);

    /// <summary>First non-null result in candidate order, evaluating chunks of candidates in parallel (one worker per thread).</summary>
    static T? FirstMatch<T>(LandscapeWorld.Worker[] workers, IReadOnlyList<(double A, double Z)> candidates, Func<LandscapeWorld.Worker, double, double, T?> eval) where T : class
    {
        int w = workers.Length, chunk = w * 4;
        for (int start = 0; start < candidates.Count; start += chunk)
        {
            int n = Math.Min(chunk, candidates.Count - start);
            var results = new T?[n];
            int begin = start;
            Parallel.For(0, w, k =>
            {
                for (int c = k; c < n; c += w) results[c] = eval(workers[k], candidates[begin + c].A, candidates[begin + c].Z);
            });
            for (int c = 0; c < n; c++) if (results[c] != null) return results[c];
        }
        return null;
    }

    static LandscapePose MakePose(LandscapeWorld.Worker w, string name, string[] reports, double around, double across, string mode, double lift, double yaw, double pitch, string notes)
    {
        around = Wrap(around);
        return new LandscapePose
        {
            Name = name, Reports = reports, Around = around, Across = across, Ground = w.Ground(around, across),
            Class = w.Class(around, across).ToString(), Habitat = w.Habitat(around, across).ToString(),
            VerticalMode = mode, Lift = lift, Yaw = yaw, Pitch = pitch, Notes = notes
        };
    }

    // ------------------------------------------------------------------ find

    /// <summary>Runs every scan. Throws InvalidOperationException naming the rule when a scan finds no candidate.</summary>
    public static List<LandscapePose> Find(LandscapeWorld.Worker[] workers, Action<string>? log = null)
    {
        log ??= _ => { };
        RingworldTechDemo.Vegetation.ImportedTreeCatalog.Load(); // static, not thread safe: warm it on the calling (main) thread before any worker builds a tree tile
        var lattice = LandscapeWorld.Lattice().ToArray();
        var classes = new LandscapeClass[lattice.Length];
        Parallel.For(0, workers.Length, k =>
        {
            for (int c = k; c < lattice.Length; c += workers.Length) classes[c] = workers[k].Class(lattice[c].Around, lattice[c].Across);
        });
        var share = Enum.GetValues<LandscapeClass>().ToDictionary(c => c, c => classes.Count(x => x == c));
        log("lattice classes: " + string.Join(" ", share.Select(p => $"{p.Key}={p.Value}")));
        List<(double A, double Z)> Cands(params LandscapeClass[] want)
            => lattice.Where((cell, idx) => want.Contains(classes[idx])).Select(cell => (cell.Around, cell.Across)).ToList();

        var poses = new List<LandscapePose>();
        LandscapeWorld.Worker w0 = workers[0];

        // plains_eye
        var plains = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.Plains), (w, a, z) =>
        {
            if (!w.ClassRing(a, z, LandscapeClass.Plains, 1500d) || !w.SiteFree(a, z, 2000d)) return null;
            if (w.Habitat(a, z) == ForestHabitat.DenseForest) return null;
            return MakePose(w, "plains_eye", new[] { "t0" }, a, z, "agl", EyeHeight, 0d, -3d, "Plains on a 1.5 km ring, not dense forest, site free on a 2 km ring");
        }) ?? throw new InvalidOperationException("rule plains_eye: no candidate");
        poses.Add(plains);
        log($"plains_eye at ({plains.Around:F1}, {plains.Across:F1}) ground {plains.Ground:F2}");

        // hills_eye, hills_300m, hills_2km
        var hills = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.HillCountry), (w, a, z) =>
        {
            if (!w.ClassRing(a, z, LandscapeClass.HillCountry, 1500d) || !w.SiteFree(a, z, 2000d)) return null;
            double dx = (w.Ground(a + 8, z) - w.Ground(a - 8, z)) / 16d, dz = (w.Ground(a, z + 8) - w.Ground(a, z - 8)) / 16d;
            double slope = Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180d / Math.PI;
            if (slope < 6d || slope > 18d) return null;
            double gx = (w.Ground(a + 32, z) - w.Ground(a - 32, z)) / 64d, gz = (w.Ground(a, z + 32) - w.Ground(a, z - 32)) / 64d;
            double yaw = Math.Round(Math.Atan2(-gz, -gx) * 180d / Math.PI);
            yaw = (yaw % 360d + 360d) % 360d;
            return MakePose(w, "hills_eye", new[] { "t0" }, a, z, "agl", EyeHeight, yaw, -4d, $"HillCountry on a 1.5 km ring, slope {slope:F1} deg at a 16 m baseline, yaw = steepest descent at 64 m");
        }) ?? throw new InvalidOperationException("rule hills_eye: no candidate");
        poses.Add(hills);
        poses.Add(MakePose(w0, "hills_300m", new[] { "t0" }, hills.Around, hills.Across, "agl", 300d, hills.Yaw, -15d, "hills_eye location, 300 m above ground"));
        poses.Add(MakePose(w0, "hills_2km", new[] { "t0", "v0" }, hills.Around, hills.Across, "agl", 2000d, hills.Yaw, -25d, "hills_eye location, 2,000 m above ground"));
        log($"hills_eye at ({hills.Around:F1}, {hills.Across:F1}) yaw {hills.Yaw}");

        // coast_eye
        var coast = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.CoastalBelt), (w, a, z) =>
        {
            for (int ix = -40; ix <= 40; ix++)
                for (int iz = -40; iz <= 40; iz++)
                {
                    double dx = ix * 50d, dz = iz * 50d;
                    if (dx * dx + dz * dz > 2000d * 2000d) continue;
                    double pa = a + dx, pz = z + dz;
                    if (pz < 1000d || pz > 317000d) continue;
                    double h = w.Ground(pa, pz);
                    if (h < 2d || h > 15d) continue;
                    for (int k = 0; k < 16; k++)
                    {
                        double deg = k * 22.5;
                        for (int d = 150; d <= 400; d += 50)
                        {
                            if (w.Ground(pa + d * Cos(deg), pz + d * Sin(deg)) >= 0d) continue;
                            if (!w.SiteFree(pa, pz, 2000d)) goto nextPoint;
                            return MakePose(w, "coast_eye", new[] { "t0", "v0" }, pa, pz, "agl", EyeHeight, deg, -2d, $"land {h:F1} m high, ocean {d} m away along yaw {deg}");
                        }
                    }
                nextPoint:;
                }
            return null;
        }) ?? throw new InvalidOperationException("rule coast_eye: no candidate");
        poses.Add(coast);
        log($"coast_eye at ({coast.Around:F1}, {coast.Across:F1}) yaw {coast.Yaw}");

        // forest focus: interior, edge, clearing
        var focus = FirstMatch<ForestFocus>(workers, Cands(LandscapeClass.Plains, LandscapeClass.Rolling), (w, a, z) => FindForestFocus(w, a, z))
            ?? throw new InvalidOperationException("rule forest_focus: no candidate (candidates / habitat-stat pass / interior / clearing / edge = " + string.Join(" / ", FocusStats) + ")");
        poses.Add(focus.Interior); poses.Add(focus.Edge); poses.Add(focus.Clearing);
        log($"forest focus near ({focus.Interior.Around:F1}, {focus.Interior.Across:F1})");

        // saved_18km
        poses.Add(MakePose(w0, "saved_18km", new[] { "t0", "v0" }, 27_984_760.943, 159_549.757, "absolute", 18_645.815, 28.974, -25d,
            "the user's saved pose from worlds/slot_1/menu_snapshot.json (read 2026-11-09); lift_m is the absolute vertical coordinate"));

        // v0 eyes
        LandscapePose Eye(string name, double acrossCentre)
        {
            var cands = new List<(double A, double Z)>();
            for (int i = 0; i < LandscapeWorld.LatticeAround; i++)
                for (double z = acrossCentre - 3000d; z <= acrossCentre + 3000d + 0.5; z += 500d) cands.Add((LandscapeWorld.LatticeAroundMeters(i), z));
            return FirstMatch<LandscapePose>(workers, cands, (w, a, z) =>
            {
                LandscapeClass c = w.Class(a, z);
                if (c == LandscapeClass.Ocean || c == LandscapeClass.Site || c == LandscapeClass.Mountain || !w.SiteFree(a, z, 2000d)) return null;
                return MakePose(w, name, new[] { "v0" }, a, z, "agl", EyeHeight, 0d, 0d, $"first lattice row with land near across {acrossCentre:F0} m");
            }) ?? throw new InvalidOperationException($"rule {name}: no candidate");
        }
        var centre = Eye("v0_centre_eye", 159_000d);
        poses.Add(centre);
        poses.Add(Eye("v0_wall_lo_eye", 30_000d));
        poses.Add(Eye("v0_wall_hi_eye", 288_000d));

        // v0_ridge_eye: hill-climb from each lattice point whose shaped height is 600-1700 m to its local summit, then test the window and the ring rule.
        var ridgeStarts = lattice.Select(c => (c.Around, c.Across)).Where(c => { double h = w0.Ground(c.Around, c.Across); return h >= 600d && h <= 1700d; }).ToList();
        var ridge = FirstMatch<LandscapePose>(workers, ridgeStarts, (w, a, z) =>
        {
            double pa = a, pz = z, h = w.Ground(pa, pz);
            double step = 200d;
            for (int iter = 0; iter < 600 && step >= 6d; iter++)
            {
                bool moved = false;
                for (int k = 0; k < 8; k++)
                {
                    double na = pa + step * Cos(k * 45d), nz = pz + step * Sin(k * 45d);
                    if (nz < 2000d || nz > 316000d) continue;
                    double nh = w.Ground(na, nz);
                    if (nh > h) { h = nh; pa = na; pz = nz; moved = true; break; }
                }
                if (!moved) step *= 0.5;
                if (h > 1700d) return null;
            }
            if (h < 1300d || h > 1700d) return null;
            foreach (double radius in new[] { 250d, 500d })
                for (int k = 0; k < 16; k++)
                    if (w.Ground(pa + radius * Cos(k * 22.5), pz + radius * Sin(k * 22.5)) >= h) return null;
            if (w.Class(pa, pz) == LandscapeClass.Site || !w.SiteFree(pa, pz, 2000d)) return null;
            return MakePose(w, "v0_ridge_eye", new[] { "v0" }, pa, pz, "agl", EyeHeight, 0d, -2d, $"local summit {h:F0} m (16 samples on 250 m and 500 m rings all lower), climbed from a lattice start");
        }) ?? throw new InvalidOperationException("rule v0_ridge_eye: no candidate");
        poses.Add(ridge);
        poses.Add(MakePose(w0, "v0_alt_12km", new[] { "v0" }, centre.Around, centre.Across, "agl", 12_000d, 0d, -25d, "v0_centre_eye location, 12 km above ground"));

        // fixture order follows ExpectedNames
        return ExpectedNames.Select(n => poses.First(p => p.Name == n)).ToList();
    }

    // ------------------------------------------------------------------ forest focus

    /// <summary>Diagnostic counters: candidates, habitat-statistics pass, interior found, clearing found, edge found.</summary>
    public static readonly int[] FocusStats = new int[8];
    sealed class ForestFocus { public LandscapePose Interior = null!, Edge = null!, Clearing = null!; }
    readonly record struct Tree(long Around, int Across);

    static ForestFocus? FindForestFocus(LandscapeWorld.Worker w, double a0, double z0)
    {
        const int half = 30, size = 2 * half + 1;
        var habitat = new ForestHabitat[size, size];
        int dense = 0, clearing = 0, edge = 0;
        for (int ix = -half; ix <= half; ix++)
            for (int iz = -half; iz <= half; iz++)
            {
                ForestHabitat h = w.Habitat(Wrap(a0 + ix * 50d), z0 + iz * 50d);
                habitat[ix + half, iz + half] = h;
                if (h == ForestHabitat.DenseForest) dense++; else if (h == ForestHabitat.Clearing) clearing++; else if (h == ForestHabitat.Edge) edge++;
            }
        int total = size * size;
        System.Threading.Interlocked.Increment(ref FocusStats[0]);
        if (dense < 0.20 * total || clearing < 0.05 * total || edge == 0) return null;
        System.Threading.Interlocked.Increment(ref FocusStats[1]);

        var tiles = new Dictionary<(long, long), List<RingBiomeDecorationLayer.ImportedForestPlacementRecord>>();
        long cells = (long)Math.Ceiling(Circ / 30d);
        List<Tree> TreesWithin(double a, double z, double radius)
        {
            var found = new List<Tree>();
            long t0 = (long)Math.Floor((a - radius) / 240d), t1 = (long)Math.Floor((a + radius) / 240d);
            long u0 = (long)Math.Floor((z - radius) / 240d), u1 = (long)Math.Floor((z + radius) / 240d);
            for (long tx = t0; tx <= t1; tx++)
                for (long tz = u0; tz <= u1; tz++)
                {
                    if (!tiles.TryGetValue((tx, tz), out var records))
                        tiles[(tx, tz)] = records = RingBiomeDecorationLayer.BuildImportedForestReviewTile(w.Sampler, tx, tz, cells, 30d, LandscapeWorld.Seed, Circ, LandscapeWorld.Width);
                    foreach (var r in records) found.Add(new Tree(r.Around, r.Across));
                }
            return found;
        }
        static double Delta(long treeAround, double a)
        {
            double d = treeAround - a;
            d -= Math.Round(d / Circ) * Circ;
            return d;
        }
        int Count(List<Tree> trees, double a, double z, double radius, double headingDeg = double.NaN)
        {
            int n = 0;
            foreach (Tree t in trees)
            {
                double da = Delta(t.Around, a), dz = t.Across - z;
                if (da * da + dz * dz > radius * radius) continue;
                if (!double.IsNaN(headingDeg) && da * Cos(headingDeg) + dz * Sin(headingDeg) <= 0d) continue;
                n++;
            }
            return n;
        }
        bool Dense(double a, double z, double dxm, double dzm) => w.Habitat(Wrap(a + dxm), z + dzm) == ForestHabitat.DenseForest;

        // interior: every grid point within 150 m is dense forest and >= 6 trees stand within 30 m
        LandscapePose? interior = null;
        for (int ix = -half + 3; ix <= half - 3 && interior == null; ix++)
            for (int iz = -half + 3; iz <= half - 3 && interior == null; iz++)
            {
                if (habitat[ix + half, iz + half] != ForestHabitat.DenseForest) continue;
                bool ok = true;
                for (int dx = -3; dx <= 3 && ok; dx++)
                    for (int dz = -3; dz <= 3 && ok; dz++)
                        if (dx * dx + dz * dz <= 9 && habitat[ix + dx + half, iz + dz + half] != ForestHabitat.DenseForest) ok = false;
                if (!ok) continue;
                double pa = Wrap(a0 + ix * 50d), pz = z0 + iz * 50d;
                if (!w.SiteFree(pa, pz, 2000d)) continue;
                int near = Count(TreesWithin(pa, pz, 30d), pa, pz, 30d);
                if (near < 6) continue;
                interior = MakePose(w, "forest_interior_eye", new[] { "t0" }, pa, pz, "agl", EyeHeight, 0d, 0d, $"dense forest within 150 m, {near} trees within 30 m");
            }
        if (interior == null) return null;
        System.Threading.Interlocked.Increment(ref FocusStats[2]);

        // clearing (amended 2026-11-09): Clearing habitat; first ring of 150 / 250 / 400 m with >= 4 of 8 directions DenseForest; no tree within 10 m.
        // Best point: most dense directions, then smallest ring, then largest tree-free radius (grid order breaks ties).
        LandscapePose? clearingPose = null;
        int bestDirs = 0, bestRing = int.MaxValue; double bestFree = -1d;
        for (int ix = -half + 3; ix <= half - 3; ix++)
            for (int iz = -half + 3; iz <= half - 3; iz++)
            {
                if (habitat[ix + half, iz + half] != ForestHabitat.Clearing) continue;
                double pa = Wrap(a0 + ix * 50d), pz = z0 + iz * 50d;
                int ringUsed = 0, around = 0;
                foreach (int ring in new[] { 150, 250, 400 })
                {
                    int n = 0;
                    for (int k = 0; k < 8; k++) if (Dense(pa, pz, ring * Cos(k * 45d), ring * Sin(k * 45d))) n++;
                    if (n >= 4) { ringUsed = ring; around = n; break; }
                }
                if (ringUsed == 0) continue;
                if (!w.SiteFree(pa, pz, 2000d)) continue;
                var trees = TreesWithin(pa, pz, 120d);
                double nearest = 120d;
                foreach (Tree t in trees) { double da = Delta(t.Around, pa), dz = t.Across - pz; nearest = Math.Min(nearest, Math.Sqrt(da * da + dz * dz)); }
                if (nearest < 10d) continue;
                if (around > bestDirs || (around == bestDirs && (ringUsed < bestRing || (ringUsed == bestRing && nearest > bestFree))))
                {
                    bestDirs = around; bestRing = ringUsed; bestFree = nearest;
                    clearingPose = MakePose(w, "forest_clearing_eye", new[] { "t0", "v0" }, pa, pz, "agl", EyeHeight, 0d, 0d,
                        $"clearing opening; dense forest in {around}/8 directions at ring {ringUsed} m; nearest tree {nearest:F0} m (amended rule, see spec 2.4)");
                }
            }
        if (clearingPose == null) return null;
        System.Threading.Interlocked.Increment(ref FocusStats[3]);

        // edge: Edge habitat, dense forest 100 m ahead (the yaw) and none 100 m behind, >= 3 trees within 60 m ahead
        LandscapePose? edgePose = null;
        for (int ix = -half + 3; ix <= half - 3 && edgePose == null; ix++)
            for (int iz = -half + 3; iz <= half - 3 && edgePose == null; iz++)
            {
                if (habitat[ix + half, iz + half] != ForestHabitat.Edge) continue;
                double pa = Wrap(a0 + ix * 50d), pz = z0 + iz * 50d;
                if (!w.SiteFree(pa, pz, 2000d)) continue;
                for (int k = 0; k < 8 && edgePose == null; k++)
                {
                    double deg = k * 45d;
                    if (!Dense(pa, pz, 100d * Cos(deg), 100d * Sin(deg)) || Dense(pa, pz, -100d * Cos(deg), -100d * Sin(deg))) continue;
                    int ahead = Count(TreesWithin(pa, pz, 60d), pa, pz, 60d, deg);
                    if (ahead < 3) continue;
                    edgePose = MakePose(w, "forest_edge_eye", new[] { "t0" }, pa, pz, "agl", EyeHeight, deg, 0d, $"edge, dense forest 100 m ahead (yaw {deg}), none behind, {ahead} trees within 60 m ahead");
                }
            }
        if (edgePose == null) return null;
        System.Threading.Interlocked.Increment(ref FocusStats[4]);
        return new ForestFocus { Interior = interior, Edge = edgePose, Clearing = clearingPose };
    }

    // ------------------------------------------------------------------ fixture io

    static string N(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    static string Q(string s) => JsonSerializer.Serialize(s);

    public static string ToJson(IReadOnlyList<LandscapePose> poses)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"schema\": 1,\n");
        sb.Append($"  \"world\": {{\"generator_id\": {Q(LandscapeWorld.GeneratorId)}, \"seed\": {LandscapeWorld.Seed}, \"definition_hash\": {Q(LandscapeWorld.DefinitionHashHex)}, \"settings\": {Q(LandscapeWorld.SettingsPath)}}},\n");
        sb.Append($"  \"eye_height_m\": {N(EyeHeight)},\n");
        sb.Append("  \"times\": {\n");
        sb.Append("    \"noon\": {\"mode\": \"noon\"},\n");
        sb.Append("    \"sun_8_set\": {\"mode\": \"elevation\", \"value\": 8.0, \"rising\": false},\n");
        sb.Append("    \"sun_2_set\": {\"mode\": \"elevation\", \"value\": 2.0, \"rising\": false},\n");
        sb.Append("    \"night_planet\": {\"mode\": \"night_planet\"}\n  },\n");
        sb.Append("  \"headings\": {\n");
        sb.Append("    \"along_pos\": {\"yaw\": 0.0, \"pitch\": 0.0}, \"along_neg\": {\"yaw\": 180.0, \"pitch\": 0.0},\n");
        sb.Append("    \"wall_hi\": {\"yaw\": 90.0, \"pitch\": 0.0}, \"wall_lo\": {\"yaw\": 270.0, \"pitch\": 0.0},\n");
        sb.Append("    \"arch_up\": {\"yaw\": 0.0, \"pitch\": 30.0}\n  },\n");
        sb.Append("  \"poses\": [\n");
        for (int i = 0; i < poses.Count; i++)
        {
            LandscapePose p = poses[i];
            sb.Append($"    {{\"name\": {Q(p.Name)}, \"reports\": [{string.Join(", ", p.Reports.Select(Q))}], \"class\": {Q(p.Class)}, \"habitat\": {Q(p.Habitat)},\n");
            sb.Append($"     \"around_m\": {N(p.Around)}, \"across_m\": {N(p.Across)}, \"ground_m\": {N(p.Ground)}, \"vertical_mode\": {Q(p.VerticalMode)}, \"lift_m\": {N(p.Lift)},\n");
            sb.Append($"     \"yaw_deg\": {N(p.Yaw)}, \"pitch_deg\": {N(p.Pitch)}, \"notes\": {Q(p.Notes)}}}{(i + 1 < poses.Count ? "," : "")}\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    public sealed record Fixture(string DefinitionHash, string GeneratorId, int Seed, List<LandscapePose> Poses);

    public static Fixture ReadFixture(string absolutePath)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(absolutePath));
        JsonElement root = doc.RootElement, world = root.GetProperty("world");
        var poses = new List<LandscapePose>();
        foreach (JsonElement e in root.GetProperty("poses").EnumerateArray())
            poses.Add(new LandscapePose
            {
                Name = e.GetProperty("name").GetString()!, Reports = e.GetProperty("reports").EnumerateArray().Select(r => r.GetString()!).ToArray(),
                Class = e.GetProperty("class").GetString()!, Habitat = e.GetProperty("habitat").GetString()!,
                Around = e.GetProperty("around_m").GetDouble(), Across = e.GetProperty("across_m").GetDouble(), Ground = e.GetProperty("ground_m").GetDouble(),
                VerticalMode = e.GetProperty("vertical_mode").GetString()!, Lift = e.GetProperty("lift_m").GetDouble(),
                Yaw = e.GetProperty("yaw_deg").GetDouble(), Pitch = e.GetProperty("pitch_deg").GetDouble(), Notes = e.GetProperty("notes").GetString() ?? ""
            });
        return new Fixture(world.GetProperty("definition_hash").GetString()!, world.GetProperty("generator_id").GetString()!, world.GetProperty("seed").GetInt32(), poses);
    }

    /// <summary>Re-checks every committed row against the model: class, habitat, ground height and (for ground poses) the site-free ring. Returns failures.</summary>
    public static List<string> Validate(Fixture fixture, LandscapeWorld.Worker w)
    {
        var failures = new List<string>();
        if (fixture.DefinitionHash != LandscapeWorld.DefinitionHashHex) failures.Add($"fixture definition_hash {fixture.DefinitionHash} != recipe {LandscapeWorld.DefinitionHashHex}");
        if (fixture.GeneratorId != LandscapeWorld.GeneratorId || fixture.Seed != LandscapeWorld.Seed) failures.Add("fixture generator or seed differs from the pinned world");
        foreach (string name in ExpectedNames)
            if (fixture.Poses.All(p => p.Name != name)) failures.Add("fixture has no row " + name);
        foreach (LandscapePose p in fixture.Poses)
        {
            string cls = w.Class(p.Around, p.Across).ToString();
            string hab = w.Habitat(p.Around, p.Across).ToString();
            double ground = w.Ground(p.Around, p.Across);
            if (cls != p.Class) failures.Add($"{p.Name}: class {cls} != fixture {p.Class}");
            if (hab != p.Habitat) failures.Add($"{p.Name}: habitat {hab} != fixture {p.Habitat}");
            if (Math.Abs(ground - p.Ground) > 1e-6) failures.Add($"{p.Name}: ground {ground} != fixture {p.Ground}");
            if (p.VerticalMode == "agl" && !w.SiteFree(p.Around, p.Across, 2000d)) failures.Add($"{p.Name}: site or weather lung within 2 km");
            if (p.VerticalMode != "agl" && p.VerticalMode != "absolute") failures.Add($"{p.Name}: unknown vertical_mode {p.VerticalMode}");
        }
        return failures;
    }

    /// <summary>Human-readable differences between a fresh scan and the committed fixture (used by --find).</summary>
    public static List<string> Diff(IReadOnlyList<LandscapePose> found, Fixture committed)
    {
        var diffs = new List<string>();
        foreach (LandscapePose f in found)
        {
            LandscapePose? c = committed.Poses.FirstOrDefault(p => p.Name == f.Name);
            if (c == null) { diffs.Add($"{f.Name}: not in the committed fixture"); continue; }
            if (c.Around != f.Around || c.Across != f.Across || c.Ground != f.Ground || c.Yaw != f.Yaw || c.Pitch != f.Pitch || c.Lift != f.Lift || c.Class != f.Class || c.Habitat != f.Habitat)
                diffs.Add($"{f.Name}: found ({N(f.Around)}, {N(f.Across)}) yaw {N(f.Yaw)} {f.Class}/{f.Habitat}, committed ({N(c.Around)}, {N(c.Across)}) yaw {N(c.Yaw)} {c.Class}/{c.Habitat}");
        }
        return diffs;
    }
}
