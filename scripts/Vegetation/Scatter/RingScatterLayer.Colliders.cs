using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>
/// T5-8 (design B7): static colliders for boulders (convex hull of the LOD2 vertices) and outcrops (triangle mesh of the LOD1 triangles) near the player, from a pool like the imported-forest trunk colliders
/// (RingBiomeDecorationLayer.ImportedForest.cs). Gameplay only: no visual, independent of the GPU compute and the camera, driven by the scatter record epoch and the focus (4 m quantisation), never by the frustum.
/// Bodies sit under a collision root, keyed by record id, take the layer defaults the trunks use (collision layer 1, mask 1) and are never scaled: the size is baked into the shape's points.
/// The pool exists only where the layer does (worlds with scatter.rocks); nothing is created otherwise.
/// </summary>
internal sealed partial class RingScatterLayer
{
    [ExportGroup("Scatter collision")]
    [Export(PropertyHint.Range, "20,400,10")] public float ScatterCollisionRadiusMeters { get; set; } = 110f;
    [Export(PropertyHint.Range, "16,512,4")] public int MaximumScatterColliders { get; set; } = 192;

    /// <summary>At most this many bodies are created per frame (fewer when the frame's time budget runs out first); the rest wait in the pending list (outcrops first, then nearest).</summary>
    internal const int MaximumColliderInstallsPerFrame = 8;
    private const float ColliderFocusQuantumMeters = 4f;
    private const double ColliderEpochCopySeconds = .25;
    // A body costs about 0.1 ms (nodes, shape, physics-server registration), so eight in one frame would be about 0.8 ms. Installing stops for the frame once the update has used this long (after at least one body).
    private const double ColliderInstallBudgetMs = .3;
    // The trunk pool's reach logic (RingBiomeDecorationLayer.ImportedForest.cs, same values) with a taller allowance: outcrops stand up to about 30 m on terrain relief.
    private const float ColliderHeightAllowanceMeters = 50f;
    private const float ColliderLookaheadSeconds = 1.5f;
    private const float ColliderShrinkHysteresisMeters = 12f;
    private const float ColliderZeroHysteresisAltitudeMeters = 20f;
    private const float ColliderVerticalSpeedSmoothingSeconds = .3f;
    private const float ColliderTrackingResetAltitudeMeters = 1000f;
    private const float ColliderTrackingResetHorizontalMeters = 500f;
    private const int ColliderShapeCacheLimit = 2048;

    private readonly record struct ColliderRock(ScatterRecord Record, int Group, RingScatterRenderGroup RenderGroup, bool Hull, int Variant);
    private readonly record struct ColliderCandidate(int Index, double DistanceSquared);
    private readonly record struct ColliderPoseKey(ScatterRecord Record, Vector3 WorldProbe, Transform3D ParentGlobal);
    private readonly record struct ColliderShapeKey(bool Hull, int Variant, int Centimetres);

    private sealed class ColliderBody
    {
        public StaticBody3D Body = null!;
        public CollisionShape3D Shape = null!;
        public ColliderPoseKey Pose;
        public ColliderShapeKey ShapeKey;
    }

    private readonly Dictionary<ulong, ColliderBody> _colliderBodies = new();
    private readonly List<ColliderRock> _colliderEligible = new();
    private readonly List<ColliderCandidate> _colliderCandidates = new();
    private readonly List<ColliderCandidate> _colliderPending = new();
    private readonly HashSet<ulong> _colliderRequired = new();
    private readonly List<ulong> _colliderRemoval = new();
    private readonly Dictionary<ColliderShapeKey, Shape3D> _colliderShapes = new();
    private readonly Dictionary<ushort, RingScatterClass> _colliderClasses = new();
    private Node3D? _colliderRoot;
    private RockCatalog? _colliderCatalog;

    private bool _colliderEverCopied;
    private Vector3 _colliderWorldProbe = new(float.NaN, 0, 0);
    private Transform3D _colliderParentGlobal;
    private long _colliderFocusAround = long.MinValue, _colliderFocusAcross = long.MinValue, _colliderEpoch = long.MinValue;
    private ulong _colliderLastCopyMsec;
    private float _colliderRadius = float.NaN, _colliderEffectiveRadius = float.NaN;
    private int _colliderMaximum = int.MinValue;

    private bool _colliderAltitudeValid;
    private float _colliderPreviousAltitude, _colliderInstantVerticalSpeed, _colliderSmoothedVerticalSpeed, _colliderHeldRadius = float.NaN, _colliderHeldConfiguredRadius = float.NaN;
    private double _colliderPreviousSeconds, _colliderPreviousAround, _colliderPreviousAcross;
    private readonly List<ScatterRecord> _colliderRecordScratch = new();
    private long _colliderFrameStart;

    // ------------------------------------------------------------------ diagnostics and test hooks
    /// <summary>Bodies alive right now.</summary>
    public int ColliderCount => _colliderBodies.Count;
    /// <summary>Wanted bodies still waiting for an install slot.</summary>
    public int ColliderPendingCount => _colliderPending.Count;
    /// <summary>Records of the latest copy that qualify for a collider (any distance).</summary>
    public int ColliderEligibleCount => _colliderEligible.Count;
    /// <summary>Radius the latest selection used (the configured one after the altitude reach logic).</summary>
    public float ColliderEffectiveRadiusMeters => _colliderEffectiveRadius;
    /// <summary>The record epoch of the latest copy (a changed epoch is picked up within a quarter second).</summary>
    internal long ColliderEpochApplied => _colliderEpoch;
    public int ColliderInstallsLastFrame { get; private set; }
    public int ColliderMaxInstallsInFrame { get; private set; }
    public long ColliderInstallsTotal { get; private set; }
    /// <summary>Main-thread time of UpdateColliders per frame since the last reset.</summary>
    public double ColliderUpdateMsTotal { get; private set; }
    public double ColliderUpdateMsMax { get; private set; }
    public int ColliderUpdateFrames { get; private set; }
    internal IReadOnlyDictionary<ulong, StaticBody3D> ColliderBodyMap
    {
        get { var map = new Dictionary<ulong, StaticBody3D>(_colliderBodies.Count); foreach ((ulong id, ColliderBody b) in _colliderBodies) map[id] = b.Body; return map; }
    }
    /// <summary>The anchor frame a record's collider pose is built in: the record's own position snapped to 512 m. A pose is then a pure function of the record and the geometry (no history), so a body that is released and re-created lands on the same bits.</summary>
    internal static (long Around, long Across) ColliderAnchorOf(in ScatterRecord rec) => (Snap(rec.AroundCm * .01), Snap(rec.AcrossCm * .01));
    internal Node3D? ColliderRoot => _colliderRoot;
    internal void ResetColliderTiming() { ColliderUpdateMsTotal = 0; ColliderUpdateMsMax = 0; ColliderUpdateFrames = 0; ColliderMaxInstallsInFrame = 0; }

    // ------------------------------------------------------------------ per frame

    private void UpdateColliders()
    {
        long start = _colliderFrameStart = Stopwatch.GetTimestamp();
        ColliderInstallsLastFrame = 0;
        UpdateCollidersCore();
        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        ColliderUpdateMsTotal += ms; ColliderUpdateFrames++;
        if (ms > ColliderUpdateMsMax) ColliderUpdateMsMax = ms;
    }

    private void UpdateCollidersCore()
    {
        if (Host.Geometry() is not { } geometry || Host.Focus() is not { } focus) return;
        float configured = ScatterCollisionRadiusMeters;
        int maximum = Math.Max(0, MaximumScatterColliders);
        float predictedAltitude = TrackColliderAltitude(focus, Host.Altitude());
        float effective = configured;
        float reachHeight = Mathf.Max(0f, predictedAltitude - ColliderHeightAllowanceMeters);
        if (reachHeight > 0f)
        {
            double reachSquared = (double)configured * configured - (double)reachHeight * reachHeight;
            effective = reachSquared <= 0.0 ? 0f : Mathf.Min(configured, (float)(Math.Ceiling(Math.Sqrt(reachSquared) / 4.0) * 4.0));
        }
        // Hysteresis as the trunks: grow at once, shrink only by a clear margin; 0 only once well above the zero-reach altitude.
        if (float.IsNaN(_colliderHeldRadius) || configured != _colliderHeldConfiguredRadius || effective >= _colliderHeldRadius) _colliderHeldRadius = effective;
        else if (effective > 0f ? effective <= _colliderHeldRadius - ColliderShrinkHysteresisMeters : predictedAltitude >= ColliderHeightAllowanceMeters + configured + ColliderZeroHysteresisAltitudeMeters)
            _colliderHeldRadius = effective;
        _colliderHeldConfiguredRadius = configured;
        effective = _colliderHeldRadius;

        // A rebase (or any move of the grid) shows as a move of a fixed canonical point; it makes every pose stale.
        Vector3 worldProbe = geometry.CanonicalToWorld(0.0, 0.0, 0.0);
        _colliderRoot ??= CreateColliderRoot();
        Transform3D parentGlobal = _colliderRoot.GlobalTransform;

        long cellAround = (long)Math.Floor(focus.AroundMeters / ColliderFocusQuantumMeters), cellAcross = (long)Math.Floor(focus.AcrossMeters / ColliderFocusQuantumMeters);
        long epoch = Host.Epoch();
        ulong now = Time.GetTicksMsec();
        bool recopy = epoch != _colliderEpoch && (!_colliderEverCopied || (now - _colliderLastCopyMsec) * .001 >= ColliderEpochCopySeconds);
        bool dirty = recopy || cellAround != _colliderFocusAround || cellAcross != _colliderFocusAcross || configured != _colliderRadius || effective != _colliderEffectiveRadius
            || maximum != _colliderMaximum || worldProbe != _colliderWorldProbe || parentGlobal != _colliderParentGlobal;
        if (dirty)
        {
            _colliderFocusAround = cellAround; _colliderFocusAcross = cellAcross; _colliderRadius = configured; _colliderEffectiveRadius = effective; _colliderMaximum = maximum;
            _colliderWorldProbe = worldProbe; _colliderParentGlobal = parentGlobal;
            if (recopy) CopyColliderRecords(epoch, now);
            SelectColliders(geometry, focus, effective, maximum, worldProbe, parentGlobal);
        }
        InstallPendingColliders(geometry, worldProbe, parentGlobal);
    }

    private Node3D CreateColliderRoot()
    {
        var root = new Node3D { Name = "ScatterColliders" };
        AddChild(root);
        return root;
    }

    /// <summary>Reads the resident records and keeps the ones that may get a collider (class says so and the size is at least its minimum); a later selection scans only these.</summary>
    private void CopyColliderRecords(long epoch, ulong nowMsec)
    {
        _colliderEpoch = epoch; _colliderEverCopied = true; _colliderLastCopyMsec = nowMsec;
        if (_colliderClasses.Count == 0) foreach (RingScatterClass row in RingScatterClassTable.Production.Rows) _colliderClasses[row.Id] = row;
        _colliderRecordScratch.Clear();
        Host.CopyRecords(_colliderRecordScratch);
        _colliderEligible.Clear();
        foreach (ScatterRecord rec in _colliderRecordScratch)
        {
            if (!_colliderClasses.TryGetValue(rec.ClassId, out RingScatterClass? cls)) continue;
            if ((cls.Collision != RingScatterCollision.ConvexHull && cls.Collision != RingScatterCollision.TriMesh) || rec.Size < cls.CollisionMinSizeM) continue;
            int group = GroupIndex(cls.RenderGroup);
            if (group != Boulders && group != Outcrops) continue;
            _colliderEligible.Add(new ColliderRock(rec, group, cls.RenderGroup, cls.Collision == RingScatterCollision.ConvexHull, Math.Clamp(rec.Mesh - cls.MeshFirst, 0, 31)));
        }
        _colliderRecordScratch.Clear();
    }

    private static int CompareColliderCandidates(ColliderCandidate a, ColliderCandidate b, List<ColliderRock> rocks)
    {
        // Outcrops first (they are scarce and tall), then by horizontal distance, ties by id.
        int ga = rocks[a.Index].Group == Outcrops ? 0 : 1, gb = rocks[b.Index].Group == Outcrops ? 0 : 1;
        if (ga != gb) return ga.CompareTo(gb);
        int d = a.DistanceSquared.CompareTo(b.DistanceSquared);
        return d != 0 ? d : rocks[a.Index].Record.Id.CompareTo(rocks[b.Index].Record.Id);
    }

    private void SelectColliders(in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus, float radius, int maximum, Vector3 worldProbe, in Transform3D parentGlobal)
    {
        _colliderCandidates.Clear();
        _colliderPending.Clear();
        _colliderRequired.Clear();
        if (radius > 0f && maximum > 0)
        {
            double radiusSquared = (double)radius * radius, circumference = geometry.CircumferenceMeters;
            for (int i = 0; i < _colliderEligible.Count; i++)
            {
                ScatterRecord rec = _colliderEligible[i].Record;
                double da = Wrap(rec.AroundCm * .01 - focus.AroundMeters, circumference), dz = rec.AcrossCm * .01 - focus.AcrossMeters;
                double d2 = da * da + dz * dz;
                if (d2 <= radiusSquared) _colliderCandidates.Add(new ColliderCandidate(i, d2));
            }
            List<ColliderRock> rocks = _colliderEligible;
            _colliderCandidates.Sort((a, b) => CompareColliderCandidates(a, b, rocks));
            if (_colliderCandidates.Count > maximum) _colliderCandidates.RemoveRange(maximum, _colliderCandidates.Count - maximum);
        }
        foreach (ColliderCandidate candidate in _colliderCandidates)
        {
            ColliderRock rock = _colliderEligible[candidate.Index];
            _colliderRequired.Add(rock.Record.Id);
            if (!_colliderBodies.TryGetValue(rock.Record.Id, out ColliderBody? existing)) { _colliderPending.Add(candidate); continue; }
            var pose = new ColliderPoseKey(rock.Record, worldProbe, parentGlobal);
            if (existing.Pose == pose) continue; // unchanged pose: no physics-server write
            WriteColliderPose(existing, rock, pose, geometry);
        }
        _colliderRemoval.Clear();
        foreach (ulong id in _colliderBodies.Keys) if (!_colliderRequired.Contains(id)) _colliderRemoval.Add(id);
        foreach (ulong id in _colliderRemoval)
        {
            _colliderBodies[id].Body.QueueFree();
            _colliderBodies.Remove(id);
        }
    }

    private void InstallPendingColliders(in RingLodGeometrySnapshot geometry, Vector3 worldProbe, in Transform3D parentGlobal)
    {
        if (_colliderPending.Count == 0) return;
        int installed = 0;
        while (installed < MaximumColliderInstallsPerFrame && _colliderPending.Count > 0)
        {
            ColliderRock rock = _colliderEligible[_colliderPending[0].Index];
            _colliderPending.RemoveAt(0);
            if (_colliderBodies.ContainsKey(rock.Record.Id)) continue;
            var body = new StaticBody3D { Name = $"ScatterRock_{rock.Record.Id:X}" };
            var shape = new CollisionShape3D { Name = "Rock" };
            body.AddChild(shape);
            _colliderRoot!.AddChild(body);
            var entry = new ColliderBody { Body = body, Shape = shape };
            WriteColliderPose(entry, rock, new ColliderPoseKey(rock.Record, worldProbe, parentGlobal), geometry);
            _colliderBodies.Add(rock.Record.Id, entry);
            installed++;
            if ((Stopwatch.GetTimestamp() - _colliderFrameStart) * 1000.0 / Stopwatch.Frequency >= ColliderInstallBudgetMs) break;
        }
        ColliderInstallsLastFrame = installed; ColliderInstallsTotal += installed;
        if (installed > ColliderMaxInstallsInFrame) ColliderMaxInstallsInFrame = installed;
    }

    /// <summary>The body's world transform: the renderer's pose (<see cref="RecordPose"/>) without the size scale, in the record's anchor frame, then that anchor's transform (as a ScatterSet node places the renderer's rows).</summary>
    private void WriteColliderPose(ColliderBody entry, in ColliderRock rock, in ColliderPoseKey pose, in RingLodGeometrySnapshot geometry)
    {
        ScatterRecord rec = rock.Record;
        RingScatterClass cls = _colliderClasses[rec.ClassId];
        (long anchorAround, long anchorAcross) = ColliderAnchorOf(rec);
        (Basis rotation, Vector3 local) = RecordPose(rec, cls, rock.Group, geometry.CenteredAt(anchorAround, anchorAcross));
        entry.Pose = pose;
        entry.Body.GlobalTransform = AnchorTransform(geometry, anchorAround, anchorAcross) * new Transform3D(rotation, local);
        var key = new ColliderShapeKey(rock.Hull, rock.Variant, (int)Math.Round(rec.Size * 100f));
        if (entry.ShapeKey != key || entry.Shape.Shape == null) { entry.Shape.Shape = ColliderShape(key, rock.RenderGroup); entry.ShapeKey = key; }
    }

    /// <summary>Shapes are built with the size baked into the points (a scaled shape is slow and unreliable in GodotPhysics3D) and cached per variant and centimetre of size.</summary>
    private Shape3D ColliderShape(in ColliderShapeKey key, RingScatterRenderGroup group)
    {
        if (_colliderShapes.TryGetValue(key, out Shape3D? cached)) return cached;
        if (_colliderShapes.Count >= ColliderShapeCacheLimit) _colliderShapes.Clear(); // bodies keep the shapes they hold
        _colliderCatalog ??= RockCatalog.Load();
        RockGroupCatalog catalog = _colliderCatalog.Group(group);
        int variant = Math.Min(key.Variant, catalog.VariantCount - 1);
        float size = key.Centimetres * .01f;
        Shape3D shape;
        if (key.Hull)
        {
            Vector3[] points = catalog.HullPoints(variant);
            for (int i = 0; i < points.Length; i++) points[i] *= size;
            shape = new ConvexPolygonShape3D { Points = points };
        }
        else
        {
            Vector3[] soup = catalog.TriangleSoup(variant);
            for (int i = 0; i < soup.Length; i++) soup[i] *= size;
            shape = new ConcavePolygonShape3D { Data = soup, BackfaceCollision = true };
        }
        _colliderShapes[key] = shape;
        return shape;
    }

    /// <summary>The trunk pool's altitude tracking: altitude plus the descent the explorer will make over the lookahead (climbing and hovering predict the current altitude).</summary>
    private float TrackColliderAltitude(in RingCanonicalPosition focus, float altitude)
    {
        double now = Time.GetTicksUsec() * 1e-6;
        if (!_colliderAltitudeValid || Mathf.Abs(altitude - _colliderPreviousAltitude) > ColliderTrackingResetAltitudeMeters
            || Math.Abs(focus.AroundMeters - _colliderPreviousAround) > ColliderTrackingResetHorizontalMeters || Math.Abs(focus.AcrossMeters - _colliderPreviousAcross) > ColliderTrackingResetHorizontalMeters)
        {
            _colliderAltitudeValid = true; _colliderPreviousAltitude = altitude; _colliderPreviousSeconds = now;
            _colliderInstantVerticalSpeed = 0f; _colliderSmoothedVerticalSpeed = 0f;
        }
        else if (altitude != _colliderPreviousAltitude)
        {
            float dt = (float)Math.Max(now - _colliderPreviousSeconds, 1e-3);
            _colliderInstantVerticalSpeed = (altitude - _colliderPreviousAltitude) / dt;
            float blend = 1f - Mathf.Exp(-dt / ColliderVerticalSpeedSmoothingSeconds);
            _colliderSmoothedVerticalSpeed += (_colliderInstantVerticalSpeed - _colliderSmoothedVerticalSpeed) * blend;
            _colliderPreviousAltitude = altitude; _colliderPreviousSeconds = now;
        }
        else if (now - _colliderPreviousSeconds > .25)
        {
            _colliderInstantVerticalSpeed = 0f; _colliderSmoothedVerticalSpeed = 0f;
        }
        _colliderPreviousAround = focus.AroundMeters; _colliderPreviousAcross = focus.AcrossMeters;
        return altitude + Mathf.Min(0f, Mathf.Min(_colliderInstantVerticalSpeed, _colliderSmoothedVerticalSpeed)) * ColliderLookaheadSeconds;
    }
}
