using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace RingworldTechDemo.Tools;

/// <summary>
/// T4-2: resolves the dressing poses (steep faces, scree, cliffs, breaks and mountain overviews, lowland) on the pinned seed 1337 world and validates the committed fixture
/// tools/fixtures/landscape_poses_dressing_s1337.json. Same row format as the T0 fixture (<see cref="LandscapePoseFinder.ToJson"/>), report tag "t4". The T0 fixture and finder are
/// untouched. Slope is the 2D slope at a 4 m baseline (T4.md R1), "in view" means the samples 30 to 150 m ahead of the eye along the yaw. Run through tools/landscape_dressing_poses_test.gd.
/// </summary>
public static class LandscapeDressingPoses
{
    public const string FixturePath = "res://tools/fixtures/landscape_poses_dressing_s1337.json";
    public const string Report = "t4";
    const double Circ = LandscapeWorld.Circumference;
    const double EyeHeight = LandscapePoseFinder.EyeHeight;
    public static readonly string[] ExpectedNames = { "steep_eye", "scree_eye", "cliff_eye", "breaks_300m", "mountain_2km", "lowland_eye" };

    /// <summary>A face pose is valid when at least <see cref="Hits"/> of the 13 samples 30..150 m ahead have a 4 m slope in [Min, Max] and none exceeds <see cref="Cap"/>.</summary>
    public sealed record FaceRule(double Min, double Max, int Hits, double Cap, double MinRise);

    public static readonly Dictionary<string, FaceRule> FaceRules = new()
    {
        ["steep_eye"] = new FaceRule(45d, 90d, 4, 90d, 10d),
        ["scree_eye"] = new FaceRule(28d, 38d, 8, 42d, 15d),
        ["cliff_eye"] = new FaceRule(60d, 90d, 3, 90d, 10d),
    };

    static double Wrap(double around) { double r = around % Circ; return r < 0 ? r + Circ : r; }
    static double Cos(double deg) => Math.Cos(deg * Math.PI / 180d);
    static double Sin(double deg) => Math.Sin(deg * Math.PI / 180d);

    /// <summary>2D slope (degrees) at a 4 m baseline, the R1 metric.</summary>
    public static double Slope4(LandscapeWorld.Worker w, double a, double z)
    {
        double dx = (w.Ground(a + 2, z) - w.Ground(a - 2, z)) / 4d, dz = (w.Ground(a, z + 2) - w.Ground(a, z - 2)) / 4d;
        return Math.Atan(Math.Sqrt(dx * dx + dz * dz)) * 180d / Math.PI;
    }

    /// <summary>Hits, maximum slope and rise (ground at 150 m ahead minus ground at the eye) for a pose looking along the yaw.</summary>
    public static (int Hits, double MaxSlope, double Rise) View(LandscapeWorld.Worker w, double a, double z, double yaw, FaceRule rule)
    {
        int hits = 0;
        double max = 0d, eyeGround = w.Ground(a, z), farGround = eyeGround;
        for (int d = 30; d <= 150; d += 10)
        {
            double pa = a + d * Cos(yaw), pz = z + d * Sin(yaw);
            double s = Slope4(w, pa, pz);
            max = Math.Max(max, s);
            if (s >= rule.Min && s <= rule.Max) hits++;
            if (d == 150) farGround = w.Ground(pa, pz);
        }
        return (hits, max, farGround - eyeGround);
    }

    static bool RuleHolds(LandscapeWorld.Worker w, double a, double z, double yaw, FaceRule rule)
    {
        var v = View(w, a, z, yaw, rule);
        return v.Hits >= rule.Hits && v.MaxSlope <= rule.Cap && v.Rise >= rule.MinRise;
    }

    static LandscapePose Make(LandscapeWorld.Worker w, string name, double around, double across, string mode, double lift, double yaw, double pitch, string notes)
    {
        around = Wrap(around);
        return new LandscapePose
        {
            Name = name, Reports = new[] { Report }, Around = around, Across = across, Ground = w.Ground(around, across),
            Class = w.Class(around, across).ToString(), Habitat = w.Habitat(around, across).ToString(),
            VerticalMode = mode, Lift = lift, Yaw = Math.Round(yaw), Pitch = pitch, Notes = notes
        };
    }

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

    /// <summary>Steepest-ascent yaw at a baseline (m): the direction the ground rises fastest.</summary>
    static double AscentYaw(LandscapeWorld.Worker w, double a, double z, double baseline)
    {
        double gx = (w.Ground(a + baseline, z) - w.Ground(a - baseline, z)) / (2d * baseline), gz = (w.Ground(a, z + baseline) - w.Ground(a, z - baseline)) / (2d * baseline);
        return ((Math.Round(Math.Atan2(gz, gx) * 180d / Math.PI) % 360d) + 360d) % 360d;
    }

    /// <summary>A face pose: scans a 3 km window (100 m grid) around each candidate lattice cell for a target point steeper than the rule's minimum, then stands 90 m away in one of eight directions.</summary>
    static LandscapePose? FindFace(LandscapeWorld.Worker[] workers, string name, IReadOnlyList<(double A, double Z)> candidates, FaceRule rule, string what)
    {
        return FirstMatch<LandscapePose>(workers, candidates, (w, a0, z0) =>
        {
            for (int ix = -15; ix <= 15; ix++)
                for (int iz = -15; iz <= 15; iz++)
                {
                    double ta = a0 + ix * 100d, tz = z0 + iz * 100d;
                    if (tz < 2000d || tz > 316000d) continue;
                    double target = Slope4(w, ta, tz);
                    if (target < rule.Min || target > rule.Max) continue;
                    for (int k = 0; k < 8; k++)
                    {
                        double yaw = k * 45d;
                        double pa = ta - 90d * Cos(yaw), pz = tz - 90d * Sin(yaw);
                        if (pz < 2000d || pz > 316000d || !w.SiteFree(pa, pz, 1000d)) continue;
                        if (!RuleHolds(w, pa, pz, yaw, rule)) continue;
                        var v = View(w, pa, pz, yaw, rule);
                        double pitch = Math.Clamp(Math.Atan2(w.Ground(ta, tz) - (w.Ground(pa, pz) + EyeHeight), 90d) * 180d / Math.PI, -10d, 25d);
                        pitch = Math.Round(pitch);
                        return Make(w, name, pa, pz, "agl", EyeHeight, yaw, pitch,
                            $"{what}: {v.Hits} of 13 samples 30-150 m ahead have a 4 m slope in {rule.Min}-{rule.Max} deg, max {v.MaxSlope:F1}, ground rises {v.Rise:F0} m over 150 m; target ({ta:F0}, {tz:F0}) {target:F1} deg");
                    }
                }
            return null;
        });
    }

    public static List<LandscapePose> Find(LandscapeWorld.Worker[] workers, Action<string>? log = null)
    {
        log ??= _ => { };
        RingworldTechDemo.Vegetation.ImportedTreeCatalog.Load();
        var lattice = LandscapeWorld.Lattice().ToArray();
        var classes = new LandscapeClass[lattice.Length];
        Parallel.For(0, workers.Length, k =>
        {
            for (int c = k; c < lattice.Length; c += workers.Length) classes[c] = workers[k].Class(lattice[c].Around, lattice[c].Across);
        });
        List<(double A, double Z)> Cands(params LandscapeClass[] want)
            => lattice.Where((cell, idx) => want.Contains(classes[idx])).Select(cell => (cell.Around, cell.Across)).ToList();

        var poses = new List<LandscapePose>();
        var rock = Cands(LandscapeClass.Mountain, LandscapeClass.Breaks);

        var steep = FindFace(workers, "steep_eye", rock, FaceRules["steep_eye"], "steep face on rock ground") ?? throw new InvalidOperationException("rule steep_eye: no candidate");
        poses.Add(steep); log($"steep_eye at ({steep.Around:F1}, {steep.Across:F1}) yaw {steep.Yaw} {steep.Class}");

        var scree = FindFace(workers, "scree_eye", Cands(LandscapeClass.HillCountry, LandscapeClass.Mountain, LandscapeClass.Breaks), FaceRules["scree_eye"], "scree slope")
            ?? throw new InvalidOperationException("rule scree_eye: no candidate (escalate: no 28-38 degree face within the search radius)");
        poses.Add(scree); log($"scree_eye at ({scree.Around:F1}, {scree.Across:F1}) yaw {scree.Yaw} {scree.Class}");

        // a different place from steep_eye (its lattice cell and the neighbouring ones are skipped)
        var cliffCands = rock.Where(c => { double da = Math.Abs(c.A - steep.Around); da = Math.Min(da, Circ - da); return da > 100_000d || Math.Abs(c.Z - steep.Across) > 20_000d; }).ToList();
        var cliff = FindFace(workers, "cliff_eye", cliffCands, FaceRules["cliff_eye"], "cliff face")
            ?? throw new InvalidOperationException("rule cliff_eye: no candidate");
        poses.Add(cliff); log($"cliff_eye at ({cliff.Around:F1}, {cliff.Across:F1}) yaw {cliff.Yaw} {cliff.Class}");

        var breaks = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.Breaks), (w, a, z) =>
        {
            if (!w.ClassRing(a, z, LandscapeClass.Breaks, 1000d, 6) || !w.SiteFree(a, z, 2000d)) return null;
            double yaw = AscentYaw(w, a, z, 64d);
            return Make(w, "breaks_300m", a, z, "agl", 300d, yaw, -15d, "Breaks on a 1 km ring (6 of 8), 300 m above ground, yaw = steepest ascent at 64 m");
        }) ?? throw new InvalidOperationException("rule breaks_300m: no candidate");
        poses.Add(breaks); log($"breaks_300m at ({breaks.Around:F1}, {breaks.Across:F1}) yaw {breaks.Yaw}");

        var mountain = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.Mountain), (w, a, z) =>
        {
            if (!w.ClassRing(a, z, LandscapeClass.Mountain, 1500d) || !w.SiteFree(a, z, 2000d)) return null;
            double yaw = AscentYaw(w, a, z, 256d);
            return Make(w, "mountain_2km", a, z, "agl", 2000d, yaw, -25d, "Mountain on a 1.5 km ring, 2,000 m above ground, yaw = steepest ascent at 256 m");
        }) ?? throw new InvalidOperationException("rule mountain_2km: no candidate");
        poses.Add(mountain); log($"mountain_2km at ({mountain.Around:F1}, {mountain.Across:F1}) yaw {mountain.Yaw}");

        var lowland = FirstMatch<LandscapePose>(workers, Cands(LandscapeClass.Rolling), (w, a, z) =>
        {
            if (!w.ClassRing(a, z, LandscapeClass.Rolling, 1000d, 6) || !w.SiteFree(a, z, 2000d)) return null;
            if (w.Habitat(a, z) == RingworldTechDemo.Vegetation.ForestHabitat.DenseForest) return null;
            if (Slope4(w, a, z) > 8d) return null;
            return Make(w, "lowland_eye", a, z, "agl", EyeHeight, 0d, -3d, "Rolling lowland on a 1 km ring (6 of 8), 4 m slope at most 8 deg, not dense forest");
        }) ?? throw new InvalidOperationException("rule lowland_eye: no candidate");
        poses.Add(lowland); log($"lowland_eye at ({lowland.Around:F1}, {lowland.Across:F1})");

        return ExpectedNames.Select(n => poses.First(p => p.Name == n)).ToList();
    }

    /// <summary>Re-checks every committed row: names, class, habitat, ground, the site-free ring and the face rule of the slope poses.</summary>
    public static List<string> Validate(LandscapePoseFinder.Fixture fixture, LandscapeWorld.Worker w)
    {
        var failures = new List<string>();
        if (fixture.DefinitionHash != LandscapeWorld.DefinitionHashHex) failures.Add($"fixture definition_hash {fixture.DefinitionHash} != recipe {LandscapeWorld.DefinitionHashHex}");
        if (fixture.GeneratorId != LandscapeWorld.GeneratorId || fixture.Seed != LandscapeWorld.Seed) failures.Add("fixture generator or seed differs from the pinned world");
        foreach (string name in ExpectedNames)
            if (fixture.Poses.All(p => p.Name != name)) failures.Add("fixture has no row " + name);
        foreach (LandscapePose p in fixture.Poses)
        {
            string cls = w.Class(p.Around, p.Across).ToString();
            double ground = w.Ground(p.Around, p.Across);
            if (cls != p.Class) failures.Add($"{p.Name}: class {cls} != fixture {p.Class}");
            if (Math.Abs(ground - p.Ground) > 1e-6) failures.Add($"{p.Name}: ground {ground} != fixture {p.Ground}");
            if (p.VerticalMode == "agl" && !w.SiteFree(p.Around, p.Across, 1000d)) failures.Add($"{p.Name}: site or weather lung within 1 km");
            if (!p.Reports.Contains(Report)) failures.Add($"{p.Name}: report tag {Report} missing");
            if (FaceRules.TryGetValue(p.Name, out FaceRule? rule) && !RuleHolds(w, p.Around, p.Across, p.Yaw, rule))
            {
                var v = View(w, p.Around, p.Across, p.Yaw, rule);
                failures.Add($"{p.Name}: face rule not met (hits {v.Hits} of {rule.Hits}, max slope {v.MaxSlope:F1}, rise {v.Rise:F1})");
            }
        }
        return failures;
    }
}
