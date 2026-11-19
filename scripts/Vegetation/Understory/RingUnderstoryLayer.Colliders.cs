using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using RingworldTechDemo.Vegetation.Scatter;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Vegetation.Understory;

/// <summary>
/// T6-6 (A7 "Colliders"): static colliders for dead wood near the player, from a pool that follows the rock pool (RingScatterLayer.Colliders.cs, itself the imported-forest trunk pool's logic): the same radius, altitude reach, hysteresis, focus quantisation,
/// epoch copy and install budget, bodies keyed by record id under a collision root, layer defaults (collision layer 1, mask 1), never scaled (the size is baked into the shape). Fallen trees get a <see cref="CapsuleShape3D"/> along the log axis, snags and stumps a
/// <see cref="CylinderShape3D"/> with the trunk radius (a stump is 0.6 m high). Saplings have none. Bodies follow the dead-wood rank gate (<see cref="RingUnderstoryLayer.DeadWoodDensity"/>), as the renderer's batches do.
/// Gameplay only: independent of the GPU compute and the camera.
/// </summary>
internal sealed partial class RingUnderstoryLayer
{
    [ExportGroup("Understory collision")]
    [Export(PropertyHint.Range, "20,400,10")] public float UnderstoryCollisionRadiusMeters { get; set; } = 110f;
    [Export(PropertyHint.Range, "16,512,4")] public int MaximumUnderstoryColliders { get; set; } = 128;

    /// <summary>At most this many bodies are created per frame (fewer when the frame's time budget runs out first); the rest wait in the pending list (nearest first).</summary>
    internal const int MaximumColliderInstallsPerFrame = 8;
    private const float ColliderFocusQuantumMeters = 4f;
    private const double ColliderEpochCopySeconds = .25;
    private const double ColliderInstallBudgetMs = .3;
    private const float ColliderHeightAllowanceMeters = 50f;
    private const float ColliderLookaheadSeconds = 1.5f;
    private const float ColliderShrinkHysteresisMeters = 12f;
    private const float ColliderZeroHysteresisAltitudeMeters = 20f;
    private const float ColliderVerticalSpeedSmoothingSeconds = .3f;
    private const float ColliderTrackingResetAltitudeMeters = 1000f;
    private const float ColliderTrackingResetHorizontalMeters = 500f;
    private const int ColliderShapeCacheLimit = 2048;

    private readonly record struct ColliderWood(ScatterRecord Record, int Row, TreeFormKind Form);
    private readonly record struct ColliderCandidate(int Index, double DistanceSquared);
    private readonly record struct ColliderPoseKey(ScatterRecord Record, Vector3 WorldProbe, Transform3D ParentGlobal);
    private readonly record struct ColliderShapeKey(int Row, int Centimetres);

    private sealed class ColliderBody
    {
        public StaticBody3D Body = null!;
        public CollisionShape3D Shape = null!;
        public ColliderPoseKey Pose;
        public ColliderShapeKey ShapeKey;
    }

    private readonly Dictionary<ulong, ColliderBody> _colliderBodies = new();
    private readonly List<ColliderWood> _colliderEligible = new();
    private readonly List<ColliderCandidate> _colliderCandidates = new();
    private readonly List<ColliderCandidate> _colliderPending = new();
    private readonly HashSet<ulong> _colliderRequired = new();
    private readonly List<ulong> _colliderRemoval = new();
    private readonly Dictionary<ColliderShapeKey, Shape3D> _colliderShapes = new();
    private readonly Dictionary<ushort, RingScatterClass> _colliderClasses = new();
    private Node3D? _colliderRoot;
    private UnderstoryDeadWoodDimensions? _colliderCatalog;

    private bool _colliderEverCopied;
    private Vector3 _colliderWorldProbe = new(float.NaN, 0, 0);
    private Transform3D _colliderParentGlobal;
    private long _colliderFocusAround = long.MinValue, _colliderFocusAcross = long.MinValue, _colliderEpoch = long.MinValue;
    private ulong _colliderLastCopyMsec;
    private float _colliderRadius = float.NaN, _colliderEffectiveRadius = float.NaN, _colliderGate = float.NaN;
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
    /// <summary>Records of the latest copy that qualify for a collider (any distance, any gate).</summary>
    public int ColliderEligibleCount => _colliderEligible.Count;
    /// <summary>Radius the latest selection used (the configured one after the altitude reach logic).</summary>
    public float ColliderEffectiveRadiusMeters => _colliderEffectiveRadius;
    /// <summary>The record epoch of the latest copy (a changed epoch is picked up within a quarter second).</summary>
    internal long ColliderEpochApplied => _colliderEpoch;
    public int ColliderInstallsLastFrame { get; private set; }
    public int ColliderMaxInstallsInFrame { get; private set; }
    public long ColliderInstallsTotal { get; private set; }
    internal IReadOnlyDictionary<ulong, StaticBody3D> ColliderBodyMap
    {
        get { var map = new Dictionary<ulong, StaticBody3D>(_colliderBodies.Count); foreach ((ulong id, ColliderBody b) in _colliderBodies) map[id] = b.Body; return map; }
    }
    /// <summary>The anchor frame a record's collider pose is built in: the record's own position snapped to 512 m (a pose is a pure function of the record and the geometry, so a released body re-created later lands on the same bits).</summary>
    internal static (long Around, long Across) ColliderAnchorOf(in ScatterRecord rec) => (Snap(rec.AroundCm * .01), Snap(rec.AcrossCm * .01));
    internal Node3D? ColliderRoot => _colliderRoot;

    // ------------------------------------------------------------------ per frame

    protected override void UpdateAlways()
    {
        _colliderFrameStart = Stopwatch.GetTimestamp();
        ColliderInstallsLastFrame = 0;
        UpdateCollidersCore();
    }

    private static float RankOf(in ScatterRecord rec) => (float)(rec.Id >> 40) / 16777215f;

    private void UpdateCollidersCore()
    {
        if (Host.Geometry() is not { } geometry || Host.Focus() is not { } focus) return;
        float configured = UnderstoryCollisionRadiusMeters, gate = Mathf.Clamp(DeadWoodDensity, 0f, 1f);
        int maximum = Math.Max(0, MaximumUnderstoryColliders);
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
            || maximum != _colliderMaximum || gate != _colliderGate || worldProbe != _colliderWorldProbe || parentGlobal != _colliderParentGlobal;
        if (dirty)
        {
            _colliderFocusAround = cellAround; _colliderFocusAcross = cellAcross; _colliderRadius = configured; _colliderEffectiveRadius = effective; _colliderMaximum = maximum; _colliderGate = gate;
            _colliderWorldProbe = worldProbe; _colliderParentGlobal = parentGlobal;
            if (recopy) CopyColliderRecords(epoch, now);
            SelectColliders(geometry, focus, effective, maximum, gate, worldProbe, parentGlobal);
        }
        InstallPendingColliders(geometry, worldProbe, parentGlobal);
    }

    private Node3D CreateColliderRoot()
    {
        var root = new Node3D { Name = "UnderstoryColliders" };
        AddChild(root);
        return root;
    }

    /// <summary>Reads the resident records and keeps the dead-wood ones whose class collides; a later selection scans only these.</summary>
    private void CopyColliderRecords(long epoch, ulong nowMsec)
    {
        _colliderEpoch = epoch; _colliderEverCopied = true; _colliderLastCopyMsec = nowMsec;
        if (_colliderClasses.Count == 0) foreach (RingScatterClass row in RingScatterClassTable.Production.Rows) _colliderClasses[row.Id] = row;
        _colliderCatalog ??= UnderstoryCatalog.LoadDeadWoodDimensions();
        _colliderRecordScratch.Clear();
        Host.CopyRecords(_colliderRecordScratch);
        _colliderEligible.Clear();
        foreach (ScatterRecord rec in _colliderRecordScratch)
        {
            if (!_colliderClasses.TryGetValue(rec.ClassId, out RingScatterClass? cls) || cls.RenderGroup != RingScatterRenderGroup.DeadWood || cls.Collision == RingScatterCollision.None) continue;
            int row = UnderstoryCatalog.DeadWoodRowOf(rec.Mesh);
            if (row < 0) continue;
            _colliderEligible.Add(new ColliderWood(rec, row, _colliderCatalog.Forms[row]));
        }
        _colliderRecordScratch.Clear();
    }

    private void SelectColliders(in RingLodGeometrySnapshot geometry, in RingCanonicalPosition focus, float radius, int maximum, float gate, Vector3 worldProbe, in Transform3D parentGlobal)
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
                if (RankOf(rec) > gate) continue; // the dead-wood rank gate: a record the renderer culls has no body either
                double da = Wrap(rec.AroundCm * .01 - focus.AroundMeters, circumference), dz = rec.AcrossCm * .01 - focus.AcrossMeters;
                double d2 = da * da + dz * dz;
                if (d2 <= radiusSquared) _colliderCandidates.Add(new ColliderCandidate(i, d2));
            }
            List<ColliderWood> wood = _colliderEligible;
            _colliderCandidates.Sort((a, b) => { int d = a.DistanceSquared.CompareTo(b.DistanceSquared); return d != 0 ? d : wood[a.Index].Record.Id.CompareTo(wood[b.Index].Record.Id); });
            if (_colliderCandidates.Count > maximum) _colliderCandidates.RemoveRange(maximum, _colliderCandidates.Count - maximum);
        }
        foreach (ColliderCandidate candidate in _colliderCandidates)
        {
            ColliderWood item = _colliderEligible[candidate.Index];
            _colliderRequired.Add(item.Record.Id);
            if (!_colliderBodies.TryGetValue(item.Record.Id, out ColliderBody? existing)) { _colliderPending.Add(candidate); continue; }
            var pose = new ColliderPoseKey(item.Record, worldProbe, parentGlobal);
            if (existing.Pose == pose) continue; // unchanged pose: no physics-server write
            WriteColliderPose(existing, item, pose, geometry);
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
            ColliderWood item = _colliderEligible[_colliderPending[0].Index];
            _colliderPending.RemoveAt(0);
            if (_colliderBodies.ContainsKey(item.Record.Id)) continue;
            var body = new StaticBody3D { Name = $"DeadWood_{item.Record.Id:X}" };
            var shape = new CollisionShape3D { Name = "Wood" };
            body.AddChild(shape);
            _colliderRoot!.AddChild(body);
            var entry = new ColliderBody { Body = body, Shape = shape };
            WriteColliderPose(entry, item, new ColliderPoseKey(item.Record, worldProbe, parentGlobal), geometry);
            _colliderBodies.Add(item.Record.Id, entry);
            installed++;
            if ((Stopwatch.GetTimestamp() - _colliderFrameStart) * 1000.0 / Stopwatch.Frequency >= ColliderInstallBudgetMs) break;
        }
        ColliderInstallsLastFrame = installed; ColliderInstallsTotal += installed;
        if (installed > ColliderMaxInstallsInFrame) ColliderMaxInstallsInFrame = installed;
    }

    /// <summary>The body's world transform: the renderer's pose (<see cref="RingScatterLayer.RecordPose"/> at the instance height, without the scale) in the record's anchor frame, then that anchor's transform (as an UnderstorySet node places the renderer's rows).</summary>
    private void WriteColliderPose(ColliderBody entry, in ColliderWood item, in ColliderPoseKey pose, in RingLodGeometrySnapshot geometry)
    {
        ScatterRecord rec = item.Record;
        RingScatterClass cls = _colliderClasses[rec.ClassId];
        (long anchorAround, long anchorAcross) = ColliderAnchorOf(rec);
        (Basis rotation, Vector3 local) = RingScatterLayer.RecordPose(rec, cls, 0.0, rec.Size * _colliderCatalog!.HeightMeters[item.Row], geometry.CenteredAt(anchorAround, anchorAcross));
        entry.Pose = pose;
        entry.Body.GlobalTransform = AnchorTransform(geometry, anchorAround, anchorAcross) * new Transform3D(rotation, local);
        var key = new ColliderShapeKey(item.Row, (int)Math.Round(rec.Size * 100f));
        if (entry.ShapeKey != key || entry.Shape.Shape == null)
        {
            entry.Shape.Shape = ColliderShape(key, item.Form);
            // A snag or stump cylinder stands on the record's base (the shape is centred, so lift it by half its height); a log's capsule axis (shape +Y) is turned onto the mesh's +X axis through its centre.
            entry.Shape.Transform = item.Form == TreeFormKind.FallenTree
                ? new Transform3D(new Basis(Vector3.Back, -Mathf.Pi * .5f), Vector3.Zero)
                : new Transform3D(Basis.Identity, new Vector3(0f, ((CylinderShape3D)entry.Shape.Shape).Height * .5f, 0f));
            entry.ShapeKey = key;
        }
    }

    /// <summary>Shapes are built with the size baked in (a scaled shape is slow and unreliable in GodotPhysics3D) and cached per form and centimetre of size. The radius is the source trunk radius times the record's size.</summary>
    private Shape3D ColliderShape(in ColliderShapeKey key, TreeFormKind form)
    {
        if (_colliderShapes.TryGetValue(key, out Shape3D? cached)) return cached;
        if (_colliderShapes.Count >= ColliderShapeCacheLimit) _colliderShapes.Clear(); // bodies keep the shapes they hold
        UnderstoryDeadWoodDimensions catalog = _colliderCatalog ??= UnderstoryCatalog.LoadDeadWoodDimensions();
        float size = key.Centimetres * .01f, radius = Math.Max(.02f, catalog.TrunkRadiusMeters[key.Row] * size), length = catalog.LengthMeters[key.Row] * size;
        Shape3D shape = form == TreeFormKind.FallenTree
            ? new CapsuleShape3D { Radius = radius, Height = Math.Max(length, radius * 2f + .01f) }
            : new CylinderShape3D { Radius = radius, Height = Math.Max(length, .05f) };
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
