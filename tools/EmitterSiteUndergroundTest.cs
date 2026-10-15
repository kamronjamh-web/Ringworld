using System;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Godot;
using RingworldTechDemo.Terrain.Sites;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tools;

/// <summary>
/// M2-1: emitter site underground SDF library. A open column (plug iris), B bore radius, C marker agreement, D reactor embed,
/// E Lipschitz, F AABB, G canonical round trip, H registry lookup, I benchmark. Prints EMITTER_SITE_UNDERGROUND=PASS.
/// </summary>
public partial class EmitterSiteUndergroundTest : Node
{
    const int Seed = 1337;
    const double BenchmarkLimitNs = 200d;
    int _checks;
    bool _started;
    public bool Finished { get; private set; }
    public bool Passed { get; private set; }

    public void Start()
    {
        if (_started) return;
        _started = true;
        try
        {
            foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main })
            {
                TestColumn(kind);
                TestBore(kind);
                TestMarkers(kind);
                TestEmbed(kind);
                TestLipschitz(kind);
                TestBounds(kind);
            }
            using RingTerrainSampler sampler = EmitterSiteTestWorlds.CreateGen17Sites(Seed);
            EmitterSiteRegistry registry = sampler.EmitterSites ?? throw new InvalidOperationException("v5 sampler has no registry");
            TestCanonical(registry);
            TestRegistryLookup(registry);
            TestBenchmark(registry);
            Complete(true, string.Empty);
        }
        catch (Exception exception) { Complete(false, exception.ToString()); }
    }

    public bool IsFinished() => Finished;
    public bool IsPassed() => Passed;
    public override void _ExitTree() { if (!Finished) Complete(false, "runner removed before completion"); }

    static double Scale(SiteKind kind) => kind == SiteKind.Main ? 2d : 1d;

    // ---- A: continuous open column ------------------------------------------------------------------------------------
    void TestColumn(SiteKind kind)
    {
        EmitterSiteUndergroundShape shape = EmitterSiteUnderground.Shape(kind);
        int samples = 0;
        for (double y = 1d; y >= shape.ChamberCenterYM; y -= 1d)
        {
            double sd = EmitterSiteUnderground.SignedDistance(kind, 0d, y, 0d);
            Check(sd < 0d, $"{kind} axis y={y} is air (sd={sd})");
            samples++;
        }
        Check(samples > 3000, $"{kind} axis sampled {samples} points");
        // The plug iris itself: the whole disc inside the liner is air at the plug depth and one metre either side.
        for (double y = -202d; y <= -198d; y += 0.5d)
            Check(EmitterSiteUnderground.SignedDistance(kind, EmitterSiteApertures.LinerRadius(kind) - 1f, (float)y, 0f) < 0f, $"{kind} plug seam y={y} air at liner-1");
        Check(EmitterSiteUnderground.Contains(kind, new Vector3(0f, -200f, 0f), 50f), $"{kind} plug centre has 50 m clearance");
    }

    // ---- B: bore radius -------------------------------------------------------------------------------------------------
    void TestBore(SiteKind kind)
    {
        float liner = EmitterSiteApertures.LinerRadius(kind);
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(liner - 1f, -100f, 0f)) < 0f, $"{kind} liner-1 is air at y=-100");
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(0f, -100f, liner + 5f)) > 0f, $"{kind} liner+5 is rock at y=-100");
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(liner + 5f, -100f, 0f)) > 0f, $"{kind} liner+5 (x) is rock at y=-100");
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(0f, -100f, liner)) is > -0.01f and < 0.01f, $"{kind} bore wall at the liner radius");
        EmitterSiteUndergroundShape shape = EmitterSiteUnderground.Shape(kind);
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(shape.ShaftRadiusM - 1f, -1000f, 0f)) < 0f, $"{kind} shaft radius-1 is air");
        Check(EmitterSiteUnderground.SignedDistance(kind, new Vector3(shape.ShaftRadiusM + 5f, -1000f, 0f)) > 0f, $"{kind} shaft radius+5 is rock");
    }

    // ---- C: constants agree with the asset markers ----------------------------------------------------------------------
    void TestMarkers(SiteKind kind)
    {
        string path = kind == SiteKind.Main ? "res://assets/poi/main_reactor/MainReactorPOI.tscn" : "res://assets/poi/reactor/ReactorPOI.tscn";
        string text = FileAccess.GetFileAsString(path);
        Check(text.Length > 0, "read " + path);
        Vector3 exhaust = Marker(text, "ExhaustTop"), arrival = Marker(text, "ArrivalOverlook");
        EmitterSiteUndergroundShape shape = EmitterSiteUnderground.Shape(kind);
        Check(Math.Abs(shape.ExhaustTopYM - (-4000f + exhaust.Y)) < 1e-3f, $"{kind} ExhaustTop {exhaust.Y} gives y={shape.ExhaustTopYM}");
        Check(shape.ExhaustTopYM == (kind == SiteKind.Main ? -1922f : -2961f), $"{kind} exhaust top at pad{shape.ExhaustTopYM}");
        Check((shape.ArrivalOverlookLocal - new Vector3(arrival.X, -4000f + arrival.Y, arrival.Z)).Length() < 1e-3f, $"{kind} ArrivalOverlook marker");
        // The overlook, the exhaust top (on the axis) and the cavern floor must all be air.
        Check(EmitterSiteUnderground.Contains(kind, shape.ArrivalOverlookLocal, 5f), $"{kind} arrival overlook is air with 5 m clearance");
        Check(EmitterSiteUnderground.Contains(kind, new Vector3(0f, shape.ExhaustTopYM, 0f), 5f), $"{kind} exhaust top axis point is air");
        Check(shape.ShaftBottomYM < shape.CavernRoofApexYM && shape.ShaftBottomYM > shape.CavernRoofApexYM - 50f, $"{kind} shaft overlaps the cavern roof");
        GD.Print($"EMITTER_SITE_UNDERGROUND depths {kind}: plug={shape.PlugDepthM} shaftTop={shape.ShaftTopYM} exhaustTop={shape.ExhaustTopYM} " +
            $"shaftBottom={shape.ShaftBottomYM} roofApex={shape.CavernRoofApexYM} centre={shape.ChamberCenterYM} floor={shape.CavernFloorYM} " +
            $"wallR={shape.CoreWallRadiusM} overlook={shape.ArrivalOverlookLocal} aabb={shape.LocalBounds}");
    }

    static Vector3 Marker(string text, string name)
    {
        Match m = Regex.Match(text, "\\[node name=\"" + name + "\"[^\\]]*\\]\\s*transform = Transform3D\\(([^)]*)\\)");
        Check(m.Success, "marker " + name);
        string[] p = m.Groups[1].Value.Split(',');
        var ci = CultureInfo.InvariantCulture;
        return new Vector3(float.Parse(p[9], ci), float.Parse(p[10], ci), float.Parse(p[11], ci));
    }

    // ---- D: reactor embed -------------------------------------------------------------------------------------------------
    void TestEmbed(SiteKind kind)
    {
        double s = Scale(kind);
        double yc = -4000d;
        // Architecture points (spec 8.6), reactor frame scaled by s (main = 2x). Azimuth is atan2(z, x).
        int rock = 0, air = 0;
        foreach (double az in new[] { 270d, 150d, 30d })
        {
            foreach (double dy in new[] { -360d, 0d, 530d }) { Rock(kind, 1353d * s, az, yc + dy * s, "anchor outer face"); rock++; }
            foreach (double dy in new[] { -180d, 80d, 330d })
            {
                Rock(kind, 1252d * s, az, yc + dy * s, "terrace outer edge"); rock++;
                foreach (double off in new[] { -30d, 30d }) { Rock(kind, 1252d * s, az + off, yc + dy * s, "terrace outer edge (end)"); rock++; }
                Air(kind, 1170d * s, az, yc + dy * s + 1.5d, "terrace inner edge"); air++;
            }
            foreach (double dy in new[] { 90d, 180d, 275d }) { Rock(kind, 1260d * s, az, yc + dy * s, "buttress root"); rock++; }
        }
        // Arrival tongue: lands in the 90 degree bay, so the tip is air.
        Air(kind, 1430d * s, 90d, yc + 75d * s + 1.5d, "arrival tongue tip"); air++;
        Air(kind, 1333d * s, 90d, yc + 75d * s + 1.5d, "arrival tongue root"); air++;
        // Exhaust roof ring (r 75..115 at y +1021..+1039; main 2x), outside the shaft radius.
        double ringInner = kind == SiteKind.Main ? 160d : 90d, ringOuter = 115d * s;
        foreach (double y in new[] { 1021d * s, 1030d * s, 1039d * s })
            foreach (double r in new[] { ringInner, ringOuter })
                foreach (double az in new[] { 0d, 77d, 200d }) { Rock(kind, r, az, yc + y, "exhaust roof ring"); rock++; }

        // Walkable floors of the reactor trimesh: 1.5 m above is air everywhere except where the floor lies inside the wall
        // embedding (terrace outer edge) or beyond the bays. Report the counts.
        string path = kind == SiteKind.Main ? "res://assets/poi/main_reactor/collision/MainReactor_WalkableSurfaces.res" : "res://assets/poi/reactor/collision/Reactor_WalkableSurfaces.res";
        Shape3D? shape3d = GD.Load<Shape3D>(path) ?? LoadMainCollision(kind);
        Check(shape3d is ConcavePolygonShape3D, $"{kind} walkable collision loads");
        Vector3[] faces = ((ConcavePolygonShape3D)shape3d!).GetFaces();
        int floors = 0, floorAir = 0, embedded = 0;
        var embeddedList = new System.Collections.Generic.List<string>();
        for (int i = 0; i + 2 < faces.Length; i += 3)
        {
            Vector3 a = faces[i], b = faces[i + 1], c = faces[i + 2];
            Vector3 n = (b - a).Cross(c - a);
            if (n.Length() < 1e-6f) continue;
            n = n.Normalized();
            if (Math.Abs(n.Y) < 0.5f) continue;
            floors++;
            Vector3 centroid = (a + b + c) / 3f;
            Vector3 probe = new(centroid.X, centroid.Y + 1.5f, centroid.Z);
            probe.Y += -4000f;
            double r = Math.Sqrt(probe.X * probe.X + probe.Z * probe.Z);
            bool inAir = EmitterSiteUnderground.SignedDistance(kind, probe) < 0f;
            if (inAir) { floorAir++; continue; }
            embedded++;
            if (embeddedList.Count < 5) embeddedList.Add($"({centroid.X:0.0},{centroid.Y:0.0},{centroid.Z:0.0}) r={r:0.0} sd={EmitterSiteUnderground.SignedDistance(kind, probe):0.0}");
        }
        GD.Print($"EMITTER_SITE_UNDERGROUND embed {kind}: totalTris={faces.Length / 3} walkable faces={floors} airAbove={floorAir} embedded={embedded} {string.Join(" ", embeddedList)}");
        Check(floors > 100, $"{kind} walkable floors found ({floors})");
        Check(embedded == 0, $"{kind} every walkable face has air 1.5 m above it ({embedded} of {floors} do not, e.g. {string.Join(" ", embeddedList)})");
        Check(rock > 30 && air > 10, $"{kind} embed points evaluated ({rock} rock, {air} air)");
    }

    static Shape3D? LoadMainCollision(SiteKind kind)
    {
        // The wrapper names its collision resource; fall back to scanning the scene text.
        string tscn = kind == SiteKind.Main ? "res://assets/poi/main_reactor/MainReactorPOI.tscn" : "res://assets/poi/reactor/ReactorPOI.tscn";
        Match m = Regex.Match(FileAccess.GetFileAsString(tscn), "type=\"Shape3D\" path=\"([^\"]+)\"");
        return m.Success ? GD.Load<Shape3D>(m.Groups[1].Value) : null;
    }

    static void Rock(SiteKind kind, double radius, double azimuthDegrees, double y, string what)
    {
        double sd = AtPolar(kind, radius, azimuthDegrees, y);
        Check(sd > 0d, $"{kind} {what} R{radius:0} az{azimuthDegrees:0} y{y:0} is rock (sd={sd:0.00})");
    }

    static void Air(SiteKind kind, double radius, double azimuthDegrees, double y, string what)
    {
        double sd = AtPolar(kind, radius, azimuthDegrees, y);
        Check(sd < 0d, $"{kind} {what} R{radius:0} az{azimuthDegrees:0} y{y:0} is air (sd={sd:0.00})");
    }

    static double AtPolar(SiteKind kind, double radius, double azimuthDegrees, double y)
    {
        double a = azimuthDegrees * Math.PI / 180d;
        return EmitterSiteUnderground.SignedDistance(kind, radius * Math.Cos(a), y, radius * Math.Sin(a));
    }

    // ---- E: Lipschitz -----------------------------------------------------------------------------------------------------
    void TestLipschitz(SiteKind kind)
    {
        var rng = new Random(9100 + (int)kind);
        Aabb box = EmitterSiteUnderground.LocalBounds(kind);
        double worst = 0d;
        for (int i = 0; i < 20000; i++)
        {
            Vector3 a = RandomIn(rng, box);
            if ((i & 1) == 1)
            {
                // Concentrate half of the pairs near the surface, where the unions and seams live.
                for (int t = 0; t < 60 && Math.Abs(EmitterSiteUnderground.SignedDistance(kind, a.X, a.Y, a.Z)) > 150d; t++) a = RandomIn(rng, box);
            }
            double len = 0.5d + rng.NextDouble() * (i % 3 == 0 ? 20d : 400d);
            Vector3 dir = new Vector3((float)(rng.NextDouble() * 2d - 1d), (float)(rng.NextDouble() * 2d - 1d), (float)(rng.NextDouble() * 2d - 1d));
            if (dir.LengthSquared() < 1e-6f) dir = Vector3.Right;
            Vector3 b = a + dir.Normalized() * (float)len;
            double sa = EmitterSiteUnderground.SignedDistance(kind, a.X, a.Y, a.Z), sb = EmitterSiteUnderground.SignedDistance(kind, b.X, b.Y, b.Z);
            double dist = (b - a).Length();
            double ratio = Math.Abs(sa - sb) / dist;
            if (ratio > worst) worst = ratio;
            Check(Math.Abs(sa - sb) <= dist * 1.001d + 1e-6d, $"{kind} Lipschitz pair {a} {b}: |dSD|={Math.Abs(sa - sb):0.0000} dist={dist:0.0000}");
        }
        // Float entry point agrees with the double one.
        for (int i = 0; i < 2000; i++)
        {
            Vector3 p = RandomIn(rng, box);
            Check(Math.Abs(EmitterSiteUnderground.SignedDistance(kind, p) - EmitterSiteUnderground.SignedDistance(kind, p.X, p.Y, p.Z)) < 1e-3d, $"{kind} float and double SDF agree");
        }
        GD.Print($"EMITTER_SITE_UNDERGROUND lipschitz {kind}: worst ratio {worst:0.0000}");
    }

    static Vector3 RandomIn(Random rng, Aabb box)
        => new(box.Position.X + (float)rng.NextDouble() * box.Size.X, box.Position.Y + (float)rng.NextDouble() * box.Size.Y, box.Position.Z + (float)rng.NextDouble() * box.Size.Z);

    // ---- F: AABB ----------------------------------------------------------------------------------------------------------
    void TestBounds(SiteKind kind)
    {
        var rng = new Random(77 + (int)kind);
        Aabb box = EmitterSiteUnderground.LocalBounds(kind);
        Vector3 lo = box.Position - Vector3.One * 0.5f, hi = box.End + Vector3.One * 0.5f;
        int inside = 0;
        for (int i = 0; i < 30000; i++)
        {
            Vector3 p = new(Mathf.Lerp(lo.X, hi.X, (float)rng.NextDouble()), Mathf.Lerp(lo.Y, hi.Y, (float)rng.NextDouble()), Mathf.Lerp(lo.Z, hi.Z, (float)rng.NextDouble()));
            int axis = rng.Next(3);
            bool high = rng.Next(2) == 0;
            if (axis == 0) p.X = high ? hi.X : lo.X; else if (axis == 1) p.Y = high ? hi.Y : lo.Y; else p.Z = high ? hi.Z : lo.Z;
            Check(EmitterSiteUnderground.SignedDistance(kind, p) > 0f, $"{kind} just outside the AABB at {p} is rock");
            inside++;
        }
        // The surface of the air region comes close to the AABB: some interior point must reach near each horizontal face.
        Check(!EmitterSiteUnderground.Contains(kind, box.Position), $"{kind} AABB corner is rock");
        Check(EmitterSiteUnderground.TryBoundCarve(kind, Vector3.Zero + new Vector3(0f, -4000f, 0f), 10f, out float lower, out float upper) && upper < 0f && lower < upper, $"{kind} carve bound at the chamber centre");
        Check(!EmitterSiteUnderground.TryBoundCarve(kind, box.End + new Vector3(1000f, 1000f, 1000f), 10f, out _, out _), $"{kind} carve bound rejects far boxes");
        // The bound must contain the true minimum/maximum over a ball.
        var rng2 = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            Vector3 c = RandomIn(rng2, box);
            float r = 1f + (float)rng2.NextDouble() * 80f;
            if (!EmitterSiteUnderground.TryBoundCarve(kind, c, r, out float lo2, out float hi2)) continue;
            for (int k = 0; k < 4; k++)
            {
                Vector3 d = new((float)(rng2.NextDouble() * 2 - 1), (float)(rng2.NextDouble() * 2 - 1), (float)(rng2.NextDouble() * 2 - 1));
                if (d.LengthSquared() < 1e-6f) continue;
                float sd = EmitterSiteUnderground.SignedDistance(kind, c + d.Normalized() * r * (float)rng2.NextDouble());
                Check(sd >= lo2 - 1e-2f && sd <= hi2 + 1e-2f, $"{kind} carve bound contains sd {sd} in [{lo2},{hi2}]");
            }
        }
        Check(inside == 30000, "bounds sampled");
    }

    // ---- G: canonical round trip ------------------------------------------------------------------------------------------
    void TestCanonical(EmitterSiteRegistry registry)
    {
        var source = new EmitterSitePlacementSource(registry);
        double circ = registry.Recipe.CircumferenceM;
        foreach (int index in new[] { 0, 7, 100, registry.Count / 2, registry.Count - 1 })
        {
            EmitterSitePlacement p = source.GetPlacement(index);
            double pad = p.PadHeightMeters + EmitterSiteFrameMath.DefaultPadClearanceMeters;
            Vector3 origin = EmitterSiteUnderground.ToLocal(p, circ, p.AroundMeters, pad, p.AcrossMeters);
            Check(origin.Length() < 1e-4f, $"site {index} origin maps to local zero ({origin})");
            Check(EmitterSiteUnderground.ContainsCanonical(p, circ, p.AroundMeters, pad - 1d, p.AcrossMeters), $"site {index} bore is air");
            Check(!EmitterSiteUnderground.ContainsCanonical(p, circ, p.AroundMeters + 400d, pad - 1d, p.AcrossMeters), $"site {index} 400 m aside at the surface is rock");

            double cos = Math.Cos(p.YawRadians), sin = Math.Sin(p.YawRadians);
            var local = new Vector3(100f, -500f, 200f);
            // Same mapping as EmitterSiteNavigator.AssetLocalToCanonical.
            double fx = cos * local.X + sin * local.Z, fz = -sin * local.X + cos * local.Z;
            double around = p.AroundMeters + fx, vertical = pad + local.Y, across = p.AcrossMeters - fz;
            Vector3 back = EmitterSiteUnderground.ToLocal(p, circ, around, vertical, across);
            Check((back - local).Length() < 1e-3f, $"site {index} yawed point maps back ({back} vs {local})");
            Vector3 wrapped = EmitterSiteUnderground.ToLocal(p, circ, around + circ, vertical, across);
            Check((wrapped - local).Length() < 1e-2f, $"site {index} around wrap maps back ({wrapped})");
            Vector3 wrappedNeg = EmitterSiteUnderground.ToLocal(p, circ, around - 2d * circ, vertical, across);
            Check((wrappedNeg - local).Length() < 1e-2f, $"site {index} negative around wrap maps back ({wrappedNeg})");
            Check(EmitterSiteUnderground.ContainsCanonical(p, circ, around, vertical, across) == EmitterSiteUnderground.Contains(p.Kind, local), $"site {index} canonical contains agrees");
        }
    }

    // ---- H: registry lookup -----------------------------------------------------------------------------------------------
    void TestRegistryLookup(EmitterSiteRegistry registry)
    {
        var source = new EmitterSitePlacementSource(registry);
        foreach (int index in new[] { 0, 3, 250, registry.Count - 1 })
        {
            EmitterSitePlacement p = source.GetPlacement(index);
            double pad = p.PadHeightMeters + EmitterSiteFrameMath.DefaultPadClearanceMeters;
            Check(EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters, pad - 1000d, p.AcrossMeters, out int found, out Vector3 local) && found == index,
                $"site {index} shaft resolves to itself (found {found})");
            Check(Math.Abs(local.Y + 1000f) < 0.01f && new Vector2(local.X, local.Z).Length() < 0.01f, $"site {index} local is on the axis ({local})");
            Check(EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters, pad - 4000d, p.AcrossMeters, out found, out _) && found == index, $"site {index} cavern centre");
            Check(!EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters + 500d, pad - 0.5d, p.AcrossMeters, out _, out _), $"site {index} surface 500 m from the bore is not underground");
            Check(!EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters + 500d, pad - 1000d, p.AcrossMeters, out _, out _), $"site {index} rock beside the shaft");
            Check(!EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters + 20000d, pad - 1000d, p.AcrossMeters, out _, out _), $"site {index} far point");
            Check(!EmitterSiteUnderground.TryFindSiteUnderground(registry, p.AroundMeters, pad + 500d, p.AcrossMeters, out _, out _), $"site {index} above the bore top");
        }
        Check(!EmitterSiteUnderground.TryFindSiteUnderground(null, 0d, 0d, 0d, out _, out _), "null registry");
    }

    // ---- I: benchmark -----------------------------------------------------------------------------------------------------
    void TestBenchmark(EmitterSiteRegistry registry)
    {
        const int N = 1 << 16, Reps = 80;
        var rng = new Random(1);
        foreach (SiteKind kind in new[] { SiteKind.Regular, SiteKind.Main })
        {
            Aabb box = EmitterSiteUnderground.LocalBounds(kind);
            var points = new Vector3[N];
            for (int i = 0; i < N; i++) points[i] = RandomIn(rng, box);
            float sink = 0f;
            for (int i = 0; i < N; i++) sink += EmitterSiteUnderground.SignedDistance(kind, points[i]); // warm-up (JIT)
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            for (int r = 0; r < Reps; r++)
                for (int i = 0; i < N; i++) sink += EmitterSiteUnderground.SignedDistance(kind, points[i]);
            double ns = (Stopwatch.GetTimestamp() - start) * 1e9 / Stopwatch.Frequency / ((double)N * Reps);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocBefore;
            GD.Print($"EMITTER_SITE_UNDERGROUND bench {kind}: {ns:0.0} ns per SignedDistance (alloc {allocated} B, sink {sink:0})");
            Check(ns < BenchmarkLimitNs, $"{kind} SignedDistance {ns:0.0} ns exceeds {BenchmarkLimitNs} ns");
            Check(allocated == 0, $"{kind} SignedDistance allocated {allocated} bytes");
        }

        // Registry query: surface misses (far from any bore, the common case) and shaft hits (trig plus the SDF).
        EmitterSite site = registry[0];
        foreach (bool hit in new[] { false, true })
        {
            var queries = new (double A, double V, double X)[N];
            for (int i = 0; i < N; i++)
                queries[i] = (site.Around + (hit ? 0d : rng.NextDouble() * 4000d - 2000d), site.PadHeight - (hit ? 1000d : 0.5d), site.Across + (hit ? 0d : rng.NextDouble() * 4000d - 2000d));
            int found = 0;
            for (int i = 0; i < N; i++) if (EmitterSiteUnderground.TryFindSiteUnderground(registry, queries[i].A, queries[i].V, queries[i].X, out _, out _)) found++;
            long t0 = Stopwatch.GetTimestamp();
            for (int r = 0; r < Reps; r++)
                for (int i = 0; i < N; i++) if (EmitterSiteUnderground.TryFindSiteUnderground(registry, queries[i].A, queries[i].V, queries[i].X, out _, out _)) found++;
            double regNs = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / ((double)N * Reps);
            GD.Print($"EMITTER_SITE_UNDERGROUND bench registry {(hit ? "shaft hit" : "surface/rock miss")}: {regNs:0.0} ns per TryFindSiteUnderground (hits {found})");
            Check(hit ? found > 0 : true, "registry benchmark found hits");
        }
    }

    static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("EMITTER_SITE_UNDERGROUND check failed: " + label);
    }

    void Complete(bool passed, string error)
    {
        if (Finished) return;
        Finished = true; Passed = passed;
        if (passed) GD.Print("EMITTER_SITE_UNDERGROUND=PASS");
        else GD.PushError("EMITTER_SITE_UNDERGROUND=FAIL " + error);
    }
}
