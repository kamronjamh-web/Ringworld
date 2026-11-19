using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>
/// T5-8 (docs/work-orders/terrain-vegetation/T5.md, design B7): the scatter collider pool (scripts/Vegetation/Scatter/RingScatterLayer.Colliders.cs), on the records of BuildScatterReviewTile at steep_eye and scree_eye plus synthetic rocks,
/// fed through the layer's fixed-host hook (no GPU: the layer runs with RenderingEnabled = false). Prints SCATTER_COLLISION=PASS.
///   a. after settling, a body exists for exactly the eligible records inside the radius (up to the cap, outcrops first); none for cobbles or boulders under 0.8 m.
///   b. a downward ray onto a boulder's top hits it inside its rendered AABB; a horizontal ray through an outcrop hits it.
///   c. a focus 500 m away releases every body; coming back restores the same poses (bit-exact).
///   d. cap: 300 eligible records in range give exactly 192 bodies, every outcrop included, the nearest boulders for the rest.
///   e. feature off: the generation 1 creation helper gives no layer, hence no bodies.
///   f. pose identity: for 20 records the body's transform scaled by the size equals the renderer's source row (BuildSources) in the layer's anchor frame, and a floating-origin rebase moves every body with it.
///   g. install rate: no frame installs more than 8 bodies (counted by the layer and as body-count deltas between frames).
///   h. timing: average and maximum main-thread ms of the per-frame collider update over a simulated 60 s walk at 6 m/s across steep_eye (printed; limits 0.05 ms average, 0.75 ms single frame).
/// Headless: tools/run_tests.ps1 -Tests scatter_collision_test. User arg --radius=0 sets the layer radius to 0 (the can-it-fail run: a and b must fail).
/// </summary>
public partial class ScatterCollisionTest : Node
{
    private const double Circ = LandscapeWorld.Circumference;
    private const float WalkSpeed = 6f;
    // FrameLimitMs 0.5 -> 0.75 (2026-11-19): the design-A5 boulder density (user's choice) puts about 2.5x the boulders in the 110 m pool, and the re-evaluation frame (not the installs, max 8) scales with them; the average stays far under its limit.
    private const double AverageLimitMs = .05, FrameLimitMs = .75;

    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();

    private RingScatterLayer _layer = null!;
    private RingLodGeometrySnapshot _geometry;
    private RingCanonicalPosition _focus;
    private long _epoch = 1;
    private List<ScatterRecord> _records = new();
    private readonly RingScatterClassTable _table = RingScatterClassTable.Production.ForMask(1 << 4);
    private RockCatalog _catalog = null!;
    private int _maxBodyDelta;
    private double _ringRadius = Circ / Math.Tau;
    private LandscapePose _steep = null!, _scree = null!;

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    public async void Start()
    {
        try
        {
            await Run();
            Result = "PASS " + string.Join("; ", _log);
        }
        catch (Exception error) { Result = "FAIL " + error.Message + " | " + string.Join("; ", _log); }
    }

    private async Task Frame() => await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

    private async Task Physics(int ticks = 3) { for (int i = 0; i < ticks; i++) await ToSignal(GetTree(), SceneTree.SignalName.PhysicsFrame); }

    /// <summary>Frames until the pool has nothing pending, the current record epoch applied and its body count held for 4 frames; tracks the largest body-count increase of one frame.</summary>
    private async Task Settle(string what)
    {
        int last = -1, steady = 0;
        for (int i = 0; i < 900; i++)
        {
            await Frame();
            int count = _layer.ColliderCount;
            if (last >= 0) _maxBodyDelta = Math.Max(_maxBodyDelta, count - last);
            steady = count == last && _layer.ColliderPendingCount == 0 && _layer.ColliderEpochApplied == _epoch ? steady + 1 : 0;
            last = count;
            if (steady >= 4) { await Physics(); return; }
        }
        throw new InvalidOperationException("timeout settling: " + what);
    }

    private RingLodGeometrySnapshot GeometryAt(RingCanonicalPosition anchor)
        => new(Transform3D.Identity, Vector3.Zero, Vector3.Zero, _ringRadius, 0.0, 0.0, Circ, 0.0, 0.0, anchor);

    private void Teleport(RingCanonicalPosition focus, bool reanchorGeometry)
    {
        _focus = focus;
        if (reanchorGeometry) _geometry = GeometryAt(focus);
    }

    private ScatterRecord Syn(ulong id, ushort cls, double around, double across, float size, int variant, double ground)
        => new(id, (long)Math.Round(around * 100.0), (int)Math.Round(across * 100.0), (float)ground, cls, (ushort)variant, size, .3f, ScatterRecord.EncodeUpOct(0, 1, 0), ScatterRecord.PackBaseTint(.5f, .5f, .5f));

    private async Task Run()
    {
        _catalog = RockCatalog.Load();
        FeatureOff();
        using RingTerrainSampler review = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" }).CreateSampler();
        var fixture = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath("res://tools/fixtures/landscape_poses_dressing_s1337.json"));
        _steep = fixture.Poses.First(p => p.Name == "steep_eye");
        _scree = fixture.Poses.First(p => p.Name == "scree_eye");
        double aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        List<ScatterRecord> TileRecords(LandscapePose p)
            => RingBiomeDecorationLayer.BuildScatterReviewTile(review, (long)Math.Floor(Math.Floor(p.Around / aroundCell) / 8.0), (long)Math.Floor(p.Across / 240.0), LandscapeWorld.Seed, Circ, LandscapeWorld.Width, _table);
        List<ScatterRecord> steepRecords = TileRecords(_steep), screeRecords = TileRecords(_scree);
        var real = new List<ScatterRecord>(steepRecords); real.AddRange(screeRecords);
        Check(steepRecords.Count > 0 && screeRecords.Count > 0, "a review tile holds no scatter records");

        var steepFocus = new RingCanonicalPosition(_steep.Around, _steep.Ground, _steep.Across);
        var screeFocus = new RingCanonicalPosition(_scree.Around, _scree.Ground, _scree.Across);
        _records = real;
        Teleport(steepFocus, true);
        float radiusOverride = float.NaN;
        foreach (string arg in OS.GetCmdlineUserArgs()) if (arg.StartsWith("--radius=")) radiusOverride = arg["--radius=".Length..].ToFloat();
        _layer = new RingScatterLayer
        {
            Name = "ScatterLayer", RenderingEnabled = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => _geometry, Focus = () => _focus, Epoch = () => _epoch, CopyRecords = into => into.AddRange(_records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Altitude = () => 0f,
            },
        };
        if (!float.IsNaN(radiusOverride)) { _layer.ScatterCollisionRadiusMeters = radiusOverride; _log.Add($"radius override {radiusOverride}"); }
        AddChild(_layer);

        // a. at steep_eye and at scree_eye
        await Settle("steep_eye");
        string a1 = CheckSelection(steepFocus, "steep_eye");
        Teleport(screeFocus, true);
        await Settle("scree_eye");
        string a2 = CheckSelection(screeFocus, "scree_eye");
        _log.Add($"a: {a1}; {a2}");

        // f. pose identity (20 of the scree_eye boulders; the 20 outcrops of the cap phase below) and floating origin
        PoseIdentity("scree_eye boulders");
        await Rebase();
        Teleport(steepFocus, true);
        await Settle("steep_eye again");

        // c. release and restore
        var before = Poses();
        Check(before.Count > 0, "c: no bodies at steep_eye to release");
        Teleport(new RingCanonicalPosition(_steep.Around + 500.0, _steep.Ground, _steep.Across), false);
        await Settle("500 m away");
        Check(_layer.ColliderCount == 0, $"c: {_layer.ColliderCount} bodies remain 500 m away");
        await Frame(); await Frame();
        Check(_layer.ColliderRoot!.GetChildCount() == 0, $"c: the collision root still holds {_layer.ColliderRoot.GetChildCount()} nodes after release");
        Teleport(steepFocus, false);
        await Settle("back at steep_eye");
        var after = Poses();
        Check(after.Count == before.Count, $"c: {after.Count} bodies after returning, {before.Count} before");
        foreach ((ulong id, Transform3D t) in before) Check(after.TryGetValue(id, out Transform3D u) && u == t, $"c: body {id:X} changed pose across the release (bit-exact comparison)");
        _log.Add($"c: {before.Count} bodies released at 500 m and restored with identical poses");

        // b. rays (two synthetic rocks, nothing else)
        _records = new List<ScatterRecord>
        {
            Syn(0xC001, 2, _steep.Around + 20.0, _steep.Across, 2.5f, 5, _steep.Ground),
            Syn(0xC002, 1, _steep.Around - 30.0, _steep.Across, 8f, 1, _steep.Ground),
        };
        _epoch++;
        await Settle("ray rocks");
        await Rays();

        // d. cap: 20 outcrops and 280 boulders within 105 m
        _records = new List<ScatterRecord>();
        _epoch++;
        await Settle("empty");
        Check(_layer.ColliderCount == 0, "d: bodies remain with no records");
        var rng = new Random(7);
        var capRecords = new List<ScatterRecord>();
        for (int i = 0; i < 300; i++)
        {
            double r = 105.0 * Math.Sqrt(rng.NextDouble()), t = rng.NextDouble() * Math.Tau;
            bool outcrop = i < 20;
            capRecords.Add(Syn(0xB000UL + (ulong)i, outcrop ? (ushort)1 : (ushort)2, _steep.Around + r * Math.Cos(t), _steep.Across + r * Math.Sin(t), outcrop ? 5f + (float)rng.NextDouble() * 10f : .9f + (float)rng.NextDouble() * 2.5f,
                outcrop ? rng.Next(0, 8) : rng.Next(0, 16), _steep.Ground));
        }
        _records = capRecords;
        _epoch++;
        _maxBodyDelta = 0;
        await Settle("300 records");
        Check(_layer.ColliderCount == 192, $"d: {_layer.ColliderCount} bodies for 300 eligible records, expected 192");
        ulong[] bodyIds = _layer.ColliderBodyMap.Keys.ToArray();
        Check(Enumerable.Range(0, 20).All(i => bodyIds.Contains(0xB000UL + (ulong)i)), "d: an outcrop in range has no body");
        CheckSelectionAgainst(capRecords, steepFocus, "d");
        PoseIdentity("cap-phase outcrops and boulders");
        _log.Add($"d: 300 eligible records in range -> {_layer.ColliderCount} bodies, all 20 outcrops included; install rate: max {_layer.ColliderMaxInstallsInFrame} per frame by the layer, max body-count step {_maxBodyDelta} per frame");
        Check(_layer.ColliderMaxInstallsInFrame <= 8, $"g: the layer installed {_layer.ColliderMaxInstallsInFrame} bodies in one frame");
        Check(_maxBodyDelta <= 8, $"g: the body count rose by {_maxBodyDelta} in one frame");
        Check(_layer.ColliderMaxInstallsInFrame >= 1 && _maxBodyDelta >= 1, "g: the install counters never moved (the rate check observed nothing)");

        // h. walk
        _records = real;
        _epoch++;
        Teleport(steepFocus, true);
        await Settle("walk start");
        _layer.ResetColliderTiming();
        long installsBefore = _layer.ColliderInstallsTotal;
        int minBodies = int.MaxValue, maxBodies = 0;
        const int Frames = 3600;
        double travelled = 0.0;
        for (int i = 0; i < Frames; i++)
        {
            travelled += WalkSpeed / 60.0;
            double phase = travelled % 600.0, offset = phase < 300.0 ? phase - 150.0 : 450.0 - phase; // triangle wave, +-150 m around the pose
            _focus = new RingCanonicalPosition(_steep.Around + offset, _steep.Ground, _steep.Across);
            if (i % 120 == 0) _epoch++; // tiles streaming in or out about every 2 s
            await Frame();
            int count = _layer.ColliderCount;
            minBodies = Math.Min(minBodies, count); maxBodies = Math.Max(maxBodies, count);
        }
        double average = _layer.ColliderUpdateMsTotal / Math.Max(1, _layer.ColliderUpdateFrames), maximum = _layer.ColliderUpdateMsMax;
        _log.Add($"h: simulated {Frames / 60} s walk at {WalkSpeed} m/s over {real.Count} records ({_layer.ColliderEligibleCount} eligible), {maxBodies} max bodies (min {minBodies}), {_layer.ColliderInstallsTotal - installsBefore} installs: update average {average:0.0000} ms, max {maximum:0.000} ms per frame over {_layer.ColliderUpdateFrames} frames");
        Check(average <= AverageLimitMs, $"h: the average collider update is {average:0.0000} ms (limit {AverageLimitMs})");
        Check(maximum <= FrameLimitMs, $"h: a collider update frame took {maximum:0.000} ms (limit {FrameLimitMs})");
        Check(_layer.ColliderMaxInstallsInFrame <= 8, "g: the layer installed more than 8 bodies in a walk frame");
    }

    // ------------------------------------------------------------------ e
    private void FeatureOff()
    {
        using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
        bool made = false;
        RingScatterLayer? none = RingBiomeDecorationLayer.CreateScatterLayerFor(gen1, () => { made = true; return new RingScatterLayer(); });
        Check(none == null && !made, "e: a generation 1 world created a scatter layer");
        _log.Add("e: feature off -> no layer, so no collider pool");
    }

    // ------------------------------------------------------------------ a, d
    private bool Eligible(ScatterRecord r)
    {
        RingScatterClass? cls = _table.Find(r.ClassId);
        return cls != null && cls.Collision != RingScatterCollision.None && r.Size >= cls.CollisionMinSizeM;
    }

    private bool IsOutcrop(ScatterRecord r) => _table.Find(r.ClassId)!.RenderGroup == RingScatterRenderGroup.Outcrops;

    private string CheckSelection(RingCanonicalPosition focus, string name) => CheckSelectionAgainst(_records, focus, "a " + name);

    /// <summary>The bodies must be exactly the reference selection: eligible records within the radius, outcrops first then nearest, capped.</summary>
    private string CheckSelectionAgainst(List<ScatterRecord> records, RingCanonicalPosition focus, string name)
    {
        double radius = _layer.ScatterCollisionRadiusMeters, r2 = radius * radius;
        var inRange = new List<(ScatterRecord Rec, double D2)>();
        foreach (ScatterRecord rec in records)
        {
            if (!Eligible(rec)) continue;
            double da = rec.AroundCm * .01 - focus.AroundMeters, dz = rec.AcrossCm * .01 - focus.AcrossMeters;
            double d2 = da * da + dz * dz;
            if (d2 <= r2) inRange.Add((rec, d2));
        }
        var expected = inRange.OrderBy(x => IsOutcrop(x.Rec) ? 0 : 1).ThenBy(x => x.D2).ThenBy(x => x.Rec.Id).Take(_layer.MaximumScatterColliders).Select(x => x.Rec.Id).ToHashSet();
        HashSet<ulong> actual = _layer.ColliderBodyMap.Keys.ToHashSet();
        Check(expected.Count > 0, name + ": the reference selection is empty, so the check would prove nothing");
        Check(actual.SetEquals(expected), $"{name}: {actual.Count} bodies, expected {expected.Count} (missing {expected.Except(actual).Count()}, extra {actual.Except(expected).Count()})");
        var byId = records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (ulong id in actual) Check(Eligible(byId[id]), $"{name}: a body exists for an ineligible record (class {byId[id].ClassId}, size {byId[id].Size})");
        int outcrops = expected.Count(id => IsOutcrop(byId[id])), cobblesNear = records.Count(x => x.ClassId == 3 && actual.Contains(x.Id));
        Check(cobblesNear == 0, name + ": a cobble has a body");
        int smallBoulders = records.Count(x => x.ClassId == 2 && x.Size < .8f && actual.Contains(x.Id));
        Check(smallBoulders == 0, name + ": a boulder under 0.8 m has a body");
        int smallInRange = records.Count(x => x.ClassId == 2 && x.Size < .8f), cobbles = records.Count(x => x.ClassId == 3);
        return $"{name}: {actual.Count} bodies = {expected.Count} expected ({outcrops} outcrops; {inRange.Count} eligible in range; {cobbles} cobbles and {smallInRange} boulders under 0.8 m of the tile records get none)";
    }

    // ------------------------------------------------------------------ f
    private Dictionary<ulong, Transform3D> Poses()
    {
        var map = new Dictionary<ulong, Transform3D>();
        foreach ((ulong id, StaticBody3D body) in _layer.ColliderBodyMap) map[id] = body.GlobalTransform;
        return map;
    }

    private void PoseIdentity(string what)
    {
        var bodies = _layer.ColliderBodyMap;
        var byId = _records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        float worst = 0f;
        int checkedCount = 0;
        foreach (ulong id in bodies.Keys.OrderBy(x => x).Take(20))
        {
            ScatterRecord rec = byId[id];
            (long anchorAround, long anchorAcross) = RingScatterLayer.ColliderAnchorOf(rec);
            var anchorWorld = new Transform3D(Basis.Identity, _geometry.CanonicalToWorld(anchorAround, 0.0, anchorAcross));
            int group = IsOutcrop(rec) ? 1 : 0;
            // The renderer's row for this record alone, built by the real source builder in the record's anchor frame.
            RingScatterLayer.SourceBuild build = RingScatterLayer.BuildSources(new[] { rec }, _geometry, anchorAround, anchorAcross, 1, default, new float[3]);
            float[] row = build.Rows[group]!;
            var rowBasis = new Basis(new Vector3(row[0], row[4], row[8]), new Vector3(row[1], row[5], row[9]), new Vector3(row[2], row[6], row[10]));
            Transform3D expected = anchorWorld * new Transform3D(rowBasis, new Vector3(row[3], row[7], row[11]));
            Transform3D actual = bodies[id].GlobalTransform;
            Basis scaled = actual.Basis.Scaled(new Vector3(rec.Size, rec.Size, rec.Size)); // the body is unscaled: its basis times the size is the row's basis
            float diff = Math.Max((scaled.X - expected.Basis.X).Length(), Math.Max((scaled.Y - expected.Basis.Y).Length(), Math.Max((scaled.Z - expected.Basis.Z).Length(), (actual.Origin - expected.Origin).Length())));
            worst = Math.Max(worst, diff);
            Check(diff <= 1e-5f, $"f: body {id:X} differs from the renderer's row by {diff}");
            Check(Math.Abs(bodies[id].Scale.X - 1f) < 1e-6f && bodies[id].Scale.IsEqualApprox(Vector3.One), $"f: body {id:X} node is scaled");
            checkedCount++;
        }
        Check(checkedCount == 20, $"f: only {checkedCount} bodies to compare");
        _log.Add($"f ({what}): {checkedCount} bodies match the renderer's source rows (BuildSources in the record's 512 m anchor frame placed by the anchor transform; body basis x size vs row basis, origin vs row origin) with a worst difference of {worst:0.#######e+0} (float against float)");
    }

    private async Task Rebase()
    {
        var before = Poses();
        _geometry = _geometry with { FloatingOriginWorldOffset = new Vector3(7f, 0f, 0f) };
        await Frame(); await Frame();
        var shifted = Poses();
        float worst = 0f;
        foreach ((ulong id, Transform3D t) in before) worst = Math.Max(worst, (shifted[id].Origin - (t.Origin - new Vector3(7f, 0f, 0f))).Length());
        Check(worst <= 1e-3f, $"f: after a floating-origin rebase of 7 m the bodies are off by {worst}");
        _geometry = _geometry with { FloatingOriginWorldOffset = Vector3.Zero };
        await Frame(); await Frame();
        _log.Add($"f: a 7 m floating-origin rebase moves {before.Count} bodies with it (worst {worst:0.#####e+0} m off)");
    }

    // ------------------------------------------------------------------ b
    private Godot.Collections.Dictionary Ray(Vector3 from, Vector3 to)
        => GetViewport().World3D.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));

    private async Task Rays()
    {
        var bodies = _layer.ColliderBodyMap;
        Check(bodies.Count == 2, $"b: {bodies.Count} bodies for the two ray rocks");
        await Physics(3);
        StaticBody3D boulder = bodies[0xC001], outcrop = bodies[0xC002];
        Transform3D bx = boulder.GlobalTransform;
        Vector3 up = bx.Basis.Y.Normalized();
        const float size = 2.5f;
        var hit = Ray(bx.Origin + up * (size * 4f), bx.Origin - up * size);
        Check(hit.Count > 0, "b: a downward ray onto the boulder's top hit nothing");
        Check(hit["collider"].AsGodotObject() == boulder, "b: the downward ray hit something other than the boulder");
        Vector3 local = bx.AffineInverse() * (Vector3)hit["position"] / size;
        Aabb box = _catalog.Boulders.Lods[0].UnitAabb.Grow(.02f);
        Check(box.HasPoint(local), $"b: the hit point {local} (unit space) is outside the boulder's rendered AABB {box}");
        float topY = local.Y;

        Transform3D ox = outcrop.GlobalTransform;
        const float osize = 8f;
        Aabb obox = _catalog.Outcrops.Lods[0].UnitAabb;
        Vector3 center = ox * (obox.GetCenter() * osize), direction = ox.Basis.X.Normalized();
        var side = Ray(center - direction * (osize * 6f), center + direction * (osize * 6f));
        Check(side.Count > 0 && side["collider"].AsGodotObject() == outcrop, "b: a horizontal ray through the outcrop did not hit it");
        // And a ray that passes beside everything must not hit.
        var miss = Ray(bx.Origin + up * (size * 4f) + direction * 60f, bx.Origin - up * size + direction * 60f);
        Check(miss.Count == 0, "b: a ray far from every rock hit something");
        _log.Add($"b: downward ray hit the 2.5 m boulder at unit height {topY:0.00} inside its AABB; horizontal ray hit the 8 m outcrop at {((Vector3)side["position"]).DistanceTo(center - direction * (osize * 6f)):0.0} m from its start");
    }
}
