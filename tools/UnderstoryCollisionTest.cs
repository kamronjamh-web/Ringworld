using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Tools;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Vegetation.Understory;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Tests;

/// <summary>
/// T6-6 (docs/work-orders/terrain-vegetation/T6.md, design A7 "Colliders"): the dead-wood collider pool of the understory layer (scripts/Vegetation/Understory/RingUnderstoryLayer.Colliders.cs), on the real records of the tiles around
/// forest_interior_eye in a vegetation.forest_structure world, fed through the layer's fixed-host hook (no GPU: RenderingEnabled = false). Prints UNDERSTORY_COLLISION=PASS.
///   a. after settling, a body exists for exactly the dead-wood records inside 110 m (nearest first, up to the cap of 128), of every class (snag, fallen tree, stump); none for a sapling.
///   b. rays: a downward ray onto a real log hits its capsule at the trunk radius; a ray along its axis from beyond the end hits the end cap at half the log length; a horizontal ray through a snag hits it; a ray onto a stump top hits it at its 0.6 m height.
///   c. a focus 500 m away releases every body; coming back restores the same poses (bit-exact).
///   d. cap: 200 synthetic records in range give exactly 128 bodies, the nearest.
///   e. rank gate: DeadWoodDensity 0 removes every body, 0.5 keeps exactly the records of rank 0.5 or lower; restoring 1 brings them back.
///   f. pose identity: for 20 records the body's transform scaled by size x source height equals the renderer's source row (BuildSources) in the layer's anchor frame; install rate at most 8 per frame.
///   h. stand fillers (SF-3): a stem body from the ground to the crown base (min 1.5 m) with the trunk radius (rays), none beyond the radius, none for a sapling, StandFillDensity gates them; a and e also count class 15 (own cap, own gate).
///   g. feature off: generation 1 and a rocks-only world create no understory layer, hence no bodies.
/// Headless: tools/run_tests.ps1 -Tests understory_collision_test.
/// </summary>
public partial class UnderstoryCollisionTest : Node
{
    private const double Circ = LandscapeWorld.Circumference;

    public string Result { get; private set; } = "RUNNING";
    private readonly List<string> _log = new();

    private RingUnderstoryLayer _layer = null!;
    private RingLodGeometrySnapshot _geometry;
    private RingCanonicalPosition _focus;
    private long _epoch = 1;
    private List<ScatterRecord> _records = new();
    private readonly RingScatterClassTable _table = RingScatterClassTable.Production.ForMask(1 << 6);
    private UnderstoryDeadWoodDimensions _dims = null!;
    private int _maxBodyDelta;
    private readonly double _ringRadius = Circ / Math.Tau;
    private LandscapePose _pose = null!;

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

    /// <summary>Id = rank in the top 24 bits (the renderer's and the gate's rank) and a serial in the rest.</summary>
    private static ulong IdOf(double rank, int serial) => ((ulong)Math.Round(rank * 16777215.0) << 40) | (uint)serial;

    private ScatterRecord Syn(ulong id, ushort cls, double around, double across, float size, int mesh, double ground, float yaw = .3f)
        => new(id, (long)Math.Round(around * 100.0), (int)Math.Round(across * 100.0), (float)ground, cls, (ushort)mesh, size, yaw, ScatterRecord.EncodeUpOct(0, 1, 0), ScatterRecord.PackBaseTint(.5f, .5f, .5f));

    private async Task Run()
    {
        _dims = UnderstoryCatalog.LoadDeadWoodDimensions();
        FeatureOff();
        using RingTerrainSampler review = LandscapeWorld.For(1, new[] { "vegetation.forest_structure" }).CreateSampler();
        var fixture = LandscapePoseFinder.ReadFixture(ProjectSettings.GlobalizePath(LandscapePoseFinder.FixturePath));
        _pose = fixture.Poses.First(p => p.Name == "forest_interior_eye");
        double aroundCell = Circ / Math.Ceiling(Circ / 30.0);
        long ta = (long)Math.Floor(Math.Floor(_pose.Around / aroundCell) / 8.0), tz = (long)Math.Floor(_pose.Across / 240.0);
        long tileCount = (long)Math.Ceiling(Math.Ceiling(Circ / 30.0) / 8.0);
        var real = new List<ScatterRecord>();
        for (int dz = -1; dz <= 1; dz++)
            for (int da = -1; da <= 1; da++)
            {
                if (tz + dz < 0) continue;
                real.AddRange(RingBiomeDecorationLayer.BuildScatterReviewTile(review, ((ta + da) % tileCount + tileCount) % tileCount, tz + dz, LandscapeWorld.Seed, Circ, LandscapeWorld.Width, RingScatterClassTable.Production));
            }
        Check(real.Any(r => r.ClassId == 13) && real.Any(r => r.ClassId == 10) && real.Any(r => r.ClassId == 12), "the tiles around forest_interior_eye lack fallen trees, snags or saplings");

        var focusAt = new RingCanonicalPosition(_pose.Around, _pose.Ground, _pose.Across);
        _records = real;
        Teleport(focusAt, true);
        _layer = new RingUnderstoryLayer
        {
            Name = "UnderstoryLayer", RenderingEnabled = false,
            Host = new ScatterLayerHost
            {
                Geometry = () => _geometry, Focus = () => _focus, Epoch = () => _epoch, CopyRecords = into => into.AddRange(_records),
                RingAcrossWidthMeters = () => LandscapeWorld.Width, Altitude = () => 0f,
            },
        };
        AddChild(_layer);

        // a. selection at the pose
        await Settle("forest_interior_eye");
        string a = CheckSelection(real, focusAt, "a forest_interior_eye");
        _log.Add("a: " + a);

        // f. pose identity
        PoseIdentity(real);

        // c. release and restore
        var before = Poses();
        Teleport(new RingCanonicalPosition(_pose.Around + 500.0, _pose.Ground, _pose.Across), false);
        await Settle("500 m away");
        Check(_layer.ColliderCount == 0, $"c: {_layer.ColliderCount} bodies remain 500 m away");
        await Frame(); await Frame();
        Check(_layer.ColliderRoot!.GetChildCount() == 0, $"c: the collision root still holds {_layer.ColliderRoot.GetChildCount()} nodes after release");
        Teleport(focusAt, false);
        await Settle("back at the pose");
        var after = Poses();
        Check(after.Count == before.Count, $"c: {after.Count} bodies after returning, {before.Count} before");
        foreach ((ulong id, Transform3D t) in before) Check(after.TryGetValue(id, out Transform3D u) && u == t, $"c: body {id:X} changed pose across the release (bit-exact comparison)");
        _log.Add($"c: {before.Count} bodies released at 500 m and restored with identical poses");

        // b. rays on three real records (the nearest log, snag and stump of the synthetic set below, at known sizes, so the geometry is exact)
        double ground = _pose.Ground;
        _records = new List<ScatterRecord>
        {
            Syn(IdOf(.1, 1), 13, _pose.Around + 20.0, _pose.Across, 1f, 36, ground, 0.6f),
            Syn(IdOf(.1, 2), 10, _pose.Around - 30.0, _pose.Across, 1f, 30, ground),
            Syn(IdOf(.1, 3), 14, _pose.Around, _pose.Across + 25.0, 1f, 40, ground),
            Syn(IdOf(.1, 4), 12, _pose.Around, _pose.Across - 25.0, 1f, 0, ground), // a sapling: no body
        };
        _epoch++;
        await Settle("ray wood");
        await Rays();

        // d. cap: 200 synthetic records within 105 m -> 128 bodies, the nearest
        _records = new List<ScatterRecord>();
        _epoch++;
        await Settle("empty");
        Check(_layer.ColliderCount == 0, "d: bodies remain with no records");
        var rng = new Random(7);
        var capRecords = new List<ScatterRecord>();
        for (int i = 0; i < 200; i++)
        {
            double r = 105.0 * Math.Sqrt(rng.NextDouble()), t = rng.NextDouble() * Math.Tau;
            (ushort cls, int mesh) = (i % 3) switch { 0 => ((ushort)10, 30 + rng.Next(0, 6)), 1 => ((ushort)13, 36 + rng.Next(0, 4)), _ => ((ushort)14, 40 + rng.Next(0, 3)) };
            capRecords.Add(Syn(IdOf(rng.NextDouble() * .9, i), cls, _pose.Around + r * Math.Cos(t), _pose.Across + r * Math.Sin(t), .9f + (float)rng.NextDouble() * .25f, mesh, ground, (float)(rng.NextDouble() * Math.Tau)));
        }
        _records = capRecords;
        _epoch++;
        _maxBodyDelta = 0;
        await Settle("200 records");
        Check(_layer.ColliderCount == 128, $"d: {_layer.ColliderCount} bodies for 200 records in range, expected 128");
        CheckSelection(capRecords, focusAt, "d cap");
        Check(_layer.ColliderMaxInstallsInFrame <= 8 && _maxBodyDelta <= 8, $"f: the layer installed {_layer.ColliderMaxInstallsInFrame} bodies in a frame (body-count step {_maxBodyDelta}), limit 8");
        Check(_layer.ColliderMaxInstallsInFrame >= 1 && _maxBodyDelta >= 1, "f: the install counters never moved");
        _log.Add($"d: 200 records in range -> {_layer.ColliderCount} bodies, the nearest; install rate max {_layer.ColliderMaxInstallsInFrame} per frame (body-count step {_maxBodyDelta})");

        // e. rank gate
        _layer.DeadWoodDensity = 0f;
        await Settle("gate 0");
        Check(_layer.ColliderCount == 0, $"e: {_layer.ColliderCount} bodies with DeadWoodDensity 0");
        _layer.DeadWoodDensity = .5f;
        await Settle("gate 0.5");
        string half = CheckSelection(capRecords, focusAt, "e gate 0.5");
        Check(_layer.ColliderBodyMap.Keys.All(id => (float)(id >> 40) / 16777215f <= .5f), "e: a body has a rank above the gate");
        _layer.DeadWoodDensity = 1f;
        await Settle("gate 1");
        Check(_layer.ColliderCount == 128, $"e: {_layer.ColliderCount} bodies after restoring the gate, expected 128");
        _log.Add("e: DeadWoodDensity 0 -> 0 bodies, 0.5 -> " + half + ", 1 -> 128 again");

        // h. stand fillers (SF-3): a trunk body from the ground to the crown base, none beyond the collider radius, the gate removes them
        UnderstoryStandFill fill = UnderstoryCatalog.LoadStandFill();
        int fillMesh = UnderstoryCatalog.StandFillSources[0];
        float fillHeight = 1.1f * fill.HeightMeters[0], trunk = fill.TrunkRadiusMeters[0] * fillHeight, crownBase = fill.Hulls[0].Base * fillHeight, trunkHeight = Math.Max(1.5f, crownBase);
        _records = new List<ScatterRecord>
        {
            Syn(IdOf(.1, 21), 15, _pose.Around + 10.0, _pose.Across, 1.1f, fillMesh, ground),
            Syn(IdOf(.1, 22), 15, _pose.Around + 200.0, _pose.Across, 1.1f, fillMesh, ground), // beyond the 110 m radius
            Syn(IdOf(.9, 23), 15, _pose.Around, _pose.Across + 15.0, 1.1f, fillMesh, ground),
            Syn(IdOf(.1, 24), 12, _pose.Around, _pose.Across - 25.0, 1f, 0, ground), // a sapling: none
        };
        _epoch++;
        await Settle("stand fillers");
        Check(_layer.ColliderCount == 2, $"h: {_layer.ColliderCount} bodies for two stand fillers in range, a far one and a sapling, expected 2");
        await Physics(3);
        StaticBody3D stem = _layer.ColliderBodyMap[IdOf(.1, 21)];
        Transform3D sx = stem.GlobalTransform;
        Vector3 sup = sx.Basis.Y.Normalized(), sacross = sx.Basis.Z.Normalized();
        var downward = Ray(sx.Origin + sup * (trunkHeight + 1f), sx.Origin + sup * (trunkHeight * .5f));
        Check(downward.Count > 0 && downward["collider"].AsGodotObject() == stem, "h: a ray onto the stem top did not hit it");
        float top = ((Vector3)downward["position"] - sx.Origin).Dot(sup);
        Check(Math.Abs(top - trunkHeight) <= .05f, $"h: the stem top is at {top:0.000} m, expected {trunkHeight:0.000} m (crown base {crownBase:0.00} m, minimum 1.5 m)");
        Vector3 midStem = sx.Origin + sup * (trunkHeight * .5f);
        var sideRay = Ray(midStem - sacross * 3f, midStem + sacross * 3f);
        Check(sideRay.Count > 0 && sideRay["collider"].AsGodotObject() == stem, "h: a horizontal ray through the stem did not hit it");
        float hitDistance = ((Vector3)sideRay["position"]).DistanceTo(midStem);
        Check(Math.Abs(hitDistance - trunk) <= .05f, $"h: the horizontal ray hit the stem {hitDistance:0.000} m from its axis, expected the trunk radius {trunk:0.000} m");
        var above = Ray(sx.Origin + sup * (trunkHeight + 3f) - sacross * 3f, sx.Origin + sup * (trunkHeight + 3f) + sacross * 3f);
        Check(above.Count == 0, "h: a ray through the crown above the stem hit a body");
        Check(!_layer.ColliderBodyMap.ContainsKey(IdOf(.1, 22)), "h: a stand filler beyond the collider radius has a body");
        _layer.StandFillDensity = .5f;
        await Settle("fill gate 0.5");
        Check(_layer.ColliderCount == 1 && _layer.ColliderBodyMap.ContainsKey(IdOf(.1, 21)), $"h: {_layer.ColliderCount} bodies with StandFillDensity 0.5, expected only the rank-0.1 stem");
        _layer.StandFillDensity = 0f;
        await Settle("fill gate 0");
        Check(_layer.ColliderCount == 0, $"h: {_layer.ColliderCount} bodies with StandFillDensity 0");
        _layer.StandFillDensity = 1f;
        _log.Add($"h: stand filler stem body {trunkHeight:0.00} m tall (crown base {crownBase:0.00} m), radius {trunk:0.000} m by rays; none at 200 m or for the sapling; StandFillDensity 0.5 -> 1 body, 0 -> none");
    }

    // ------------------------------------------------------------------ g
    private void FeatureOff()
    {
        using RingTerrainSampler gen1 = LandscapeWorld.For(1).CreateSampler();
        bool made = false;
        RingUnderstoryLayer? none = RingBiomeDecorationLayer.CreateUnderstoryLayerFor(gen1, () => { made = true; return new RingUnderstoryLayer(); });
        Check(none == null && !made, "g: a generation 1 world created an understory layer");
        using RingTerrainSampler rocks = LandscapeWorld.For(1, new[] { "ground.surface", "scatter.rocks" }).CreateSampler();
        RingUnderstoryLayer? rocksOnly = RingBiomeDecorationLayer.CreateUnderstoryLayerFor(rocks, () => { made = true; return new RingUnderstoryLayer(); });
        Check(rocksOnly == null && !made, "g: a rocks-only world created an understory layer");
        _log.Add("g: bit 6 off (generation 1, rocks only) -> no understory layer, so no collider pool or body");
    }

    // ------------------------------------------------------------------ a, d, e
    private bool Eligible(ScatterRecord r)
    {
        RingScatterClass? cls = _table.Find(r.ClassId);
        if (cls == null) return false;
        float rank = (float)(r.Id >> 40) / 16777215f;
        if (cls.RenderGroup == RingScatterRenderGroup.StandFill) return rank <= _layer.StandFillDensity; // SF-3: stand-filler trunks, own gate
        return cls.RenderGroup == RingScatterRenderGroup.DeadWood && cls.Collision != RingScatterCollision.None && rank <= _layer.DeadWoodDensity;
    }

    /// <summary>The bodies must be exactly the reference selection: dead-wood records within the radius and the rank gate, nearest first, capped.</summary>
    private string CheckSelection(List<ScatterRecord> records, RingCanonicalPosition focus, string name)
    {
        double radius = _layer.UnderstoryCollisionRadiusMeters, r2 = radius * radius;
        var inRange = new List<(ScatterRecord Rec, double D2)>();
        foreach (ScatterRecord rec in records)
        {
            if (!Eligible(rec)) continue;
            double da = rec.AroundCm * .01 - focus.AroundMeters, dz = rec.AcrossCm * .01 - focus.AcrossMeters;
            double d2 = da * da + dz * dz;
            if (d2 <= r2) inRange.Add((rec, d2));
        }
        // The cap applies per kind (dead wood, stand fillers), nearest first.
        var expected = inRange.Where(x => x.Rec.ClassId != 15).OrderBy(x => x.D2).ThenBy(x => x.Rec.Id).Take(_layer.MaximumUnderstoryColliders)
            .Concat(inRange.Where(x => x.Rec.ClassId == 15).OrderBy(x => x.D2).ThenBy(x => x.Rec.Id).Take(_layer.MaximumUnderstoryColliders)).Select(x => x.Rec.Id).ToHashSet();
        HashSet<ulong> actual = _layer.ColliderBodyMap.Keys.ToHashSet();
        Check(expected.Count > 0, name + ": the reference selection is empty, so the check would prove nothing");
        Check(actual.SetEquals(expected), $"{name}: {actual.Count} bodies, expected {expected.Count} (missing {expected.Except(actual).Count()}, extra {actual.Except(expected).Count()})");
        var byId = records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        foreach (ulong id in actual) Check(byId[id].ClassId is 10 or 13 or 14 or 15, $"{name}: a body exists for a record of class {byId[id].ClassId}");
        Check(records.Where(x => x.ClassId == 12).All(x => !actual.Contains(x.Id)), name + ": a sapling has a body");
        string classes = string.Join("/", new ushort[] { 10, 13, 14 }.Select(c => expected.Count(id => byId[id].ClassId == c)));
        if (name.StartsWith("a "))
            foreach (ushort c in new ushort[] { 10, 13, 14 })
                if (inRange.Any(x => x.Rec.ClassId == c)) Check(actual.Any(id => byId[id].ClassId == c), $"{name}: class {c} has records in range but no body");
        return $"{name}: {actual.Count} bodies = {expected.Count} expected (snags/logs/stumps {classes}; {inRange.Count} eligible in range, {records.Count(x => x.ClassId == 12)} saplings get none)";
    }

    // ------------------------------------------------------------------ f
    private Dictionary<ulong, Transform3D> Poses()
    {
        var map = new Dictionary<ulong, Transform3D>();
        foreach ((ulong id, StaticBody3D body) in _layer.ColliderBodyMap) map[id] = body.GlobalTransform;
        return map;
    }

    private void PoseIdentity(List<ScatterRecord> records)
    {
        var bodies = _layer.ColliderBodyMap;
        var byId = records.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        float worst = 0f;
        int checkedCount = 0;
        foreach (ulong id in bodies.Keys.Where(k => byId[k].ClassId != 15).OrderBy(x => x).Take(20)) // dead wood only; stand fillers are checked in h
        {
            ScatterRecord rec = byId[id];
            (long anchorAround, long anchorAcross) = RingUnderstoryLayer.ColliderAnchorOf(rec);
            var anchorWorld = new Transform3D(Basis.Identity, _geometry.CanonicalToWorld(anchorAround, 0.0, anchorAcross));
            // The renderer's row for this record alone, built by the real source builder in the record's anchor frame (dead-wood slot 2).
            RingUnderstoryLayer.SourceBuild build = RingUnderstoryLayer.BuildSources(new[] { rec }, _geometry, anchorAround, anchorAcross, 1, default, new float[7], 1f, _dims.HeightMeters, 1f);
            float[] row = build.Rows[2]!;
            var rowBasis = new Basis(new Vector3(row[0], row[4], row[8]), new Vector3(row[1], row[5], row[9]), new Vector3(row[2], row[6], row[10]));
            Transform3D expected = anchorWorld * new Transform3D(rowBasis, new Vector3(row[3], row[7], row[11]));
            Transform3D actual = bodies[id].GlobalTransform;
            float scale = rec.Size * _dims.HeightMeters[rec.Mesh - UnderstoryCatalog.DeadWoodFirstIndex];
            Basis scaled = actual.Basis.Scaled(new Vector3(scale, scale, scale)); // the body is unscaled: its basis times size x source height is the row's basis
            float diff = Math.Max((scaled.X - expected.Basis.X).Length() / scale, Math.Max((scaled.Y - expected.Basis.Y).Length() / scale, Math.Max((scaled.Z - expected.Basis.Z).Length() / scale, (actual.Origin - expected.Origin).Length())));
            worst = Math.Max(worst, diff);
            Check(diff <= 1e-4f, $"f: body {id:X} differs from the renderer's row by {diff}");
            Check(bodies[id].Scale.IsEqualApprox(Vector3.One), $"f: body {id:X} node is scaled");
            checkedCount++;
        }
        Check(checkedCount == 20, $"f: only {checkedCount} bodies to compare");
        _log.Add($"f: {checkedCount} bodies match the renderer's source rows (BuildSources, dead-wood slot) with a worst difference of {worst:0.#######e+0}");
    }

    // ------------------------------------------------------------------ b
    private Godot.Collections.Dictionary Ray(Vector3 from, Vector3 to)
        => GetViewport().World3D.DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(from, to));

    private async Task Rays()
    {
        var bodies = _layer.ColliderBodyMap;
        Check(bodies.Count == 3, $"b: {bodies.Count} bodies for the log, snag and stump (the sapling gets none)");
        await Physics(3);
        StaticBody3D log = bodies[IdOf(.1, 1)], snag = bodies[IdOf(.1, 2)], stump = bodies[IdOf(.1, 3)];

        int row = 36 - UnderstoryCatalog.DeadWoodFirstIndex;
        float radius = _dims.TrunkRadiusMeters[row], length = _dims.LengthMeters[row];
        Transform3D lx = log.GlobalTransform;
        Vector3 up = lx.Basis.Y.Normalized(), axis = lx.Basis.X.Normalized();
        var top = Ray(lx.Origin + up * (radius * 4f), lx.Origin - up * (radius * 4f));
        Check(top.Count > 0 && top["collider"].AsGodotObject() == log, "b: a downward ray onto the log did not hit it");
        float topHeight = ((Vector3)top["position"] - lx.Origin).Dot(up);
        Check(Math.Abs(topHeight - radius) <= .05f, $"b: the ray hit the log at {topHeight:0.000} m above its axis, expected its radius {radius:0.000} m");
        Vector3 start = lx.Origin - axis * (length * 1.5f);
        var along = Ray(start, lx.Origin);
        Check(along.Count > 0 && along["collider"].AsGodotObject() == log, "b: a ray along the log's axis did not hit it");
        float endDistance = ((Vector3)along["position"]).DistanceTo(lx.Origin);
        Check(Math.Abs(endDistance - length * .5f) <= .06f, $"b: the axis ray hit the log end at {endDistance:0.000} m from its middle, expected half its length {length * .5f:0.000} m");

        int srow = 30 - UnderstoryCatalog.DeadWoodFirstIndex;
        Transform3D sx = snag.GlobalTransform;
        Vector3 sup = sx.Basis.Y.Normalized(), across = sx.Basis.Z.Normalized();
        float snagHeight = _dims.LengthMeters[srow];
        Vector3 mid = sx.Origin + sup * (snagHeight * .5f);
        var side = Ray(mid - across * 3f, mid + across * 3f);
        Check(side.Count > 0 && side["collider"].AsGodotObject() == snag, "b: a horizontal ray through the snag did not hit it");

        int trow = 40 - UnderstoryCatalog.DeadWoodFirstIndex;
        Transform3D tx = stump.GlobalTransform;
        Vector3 tup = tx.Basis.Y.Normalized();
        var topStump = Ray(tx.Origin + tup * 3f, tx.Origin - tup * 1f);
        Check(topStump.Count > 0 && topStump["collider"].AsGodotObject() == stump, "b: a downward ray onto the stump did not hit it");
        float stumpTop = ((Vector3)topStump["position"] - tx.Origin).Dot(tup);
        Check(Math.Abs(stumpTop - _dims.LengthMeters[trow]) <= .03f, $"b: the stump top is at {stumpTop:0.000} m, expected {_dims.LengthMeters[trow]:0.000} m");
        var miss = Ray(lx.Origin + up * 3f + axis * 60f, lx.Origin - up * 3f + axis * 60f);
        Check(miss.Count == 0, "b: a ray far from every body hit something");
        _log.Add($"b: downward ray hit the {length:0.0} m log at its radius ({topHeight:0.00} of {radius:0.00} m), the axis ray hit its end at {endDistance:0.00} m from the middle, a horizontal ray hit the snag, a ray hit the {stumpTop:0.00} m stump top");
    }
}
