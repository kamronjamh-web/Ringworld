using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Vegetation;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>Gameplay-only imported-forest state. Bodies follow canonical tiles,
/// never the render frustum or the selected graphics LOD.</summary>
public partial class RingBiomeDecorationLayer
{
	[ExportGroup("Imported forest collision")]
	[Export] public bool EnableImportedForestCollisions { get; set; } = true;
	[Export(PropertyHint.Range, "20,400,10")] public float ImportedForestCollisionRadiusMeters { get; set; } = 110.0f;
	[Export(PropertyHint.Range, "16,512,4")] public int MaximumImportedForestColliders { get; set; } = 192;

	private readonly Dictionary<ulong, StaticBody3D> _importedForestBodies = new();
	// Last pose written to each body. An unchanged pose skips the physics-server
	// transform and shape writes entirely.
	private readonly record struct ImportedForestColliderPose(Transform3D Transform, float Radius, float Height, Transform3D ParentGlobal);
	private readonly Dictionary<ulong, ImportedForestColliderPose> _importedForestColliderPoses = new();
	private readonly List<ulong> _importedForestBodyRemovalScratch = new();
	private Node3D? _importedForestCollisionRoot;
	private long _lastImportedForestCollisionAround = long.MinValue;
	private long _lastImportedForestCollisionAcross = long.MinValue;
	private Vector3 _lastImportedForestCollisionCenter = new(float.PositiveInfinity, 0, 0);
	private ulong _lastImportedForestCollisionEditVersion = ulong.MaxValue;
	private long _lastImportedForestCollisionTileEpoch = long.MinValue;
	private float _lastImportedForestCollisionRadius = float.NaN;
	private int _lastMaximumImportedForestColliders = int.MinValue;
	private readonly List<(CachedTree Tree, double DistanceSquared)> _importedForestCollisionCandidates = new();
	private readonly HashSet<VegetationTileKey> _importedForestCollisionTileKeys = new();
	private readonly HashSet<ulong> _importedForestCollisionRequired = new();

	public int ImportedForestCollisionInspectedTileCount { get; private set; }
	public int ImportedForestCollisionInspectedRootCount { get; private set; }
	public int ImportedForestCollisionResultCount { get; private set; }

	public Godot.Collections.Dictionary GetImportedForestRuntimeDiagnostics()
	{
		var result = new Godot.Collections.Dictionary
		{
			["variant_count"] = _lod4AuthoredTemplates?.Length ?? 0,
			["canonical_record_count"] = CountImportedForestRecords(),
			["collision_count"] = _importedForestBodies.Count,
			["collision_inspected_tile_count"] = ImportedForestCollisionInspectedTileCount,
			["collision_inspected_root_count"] = ImportedForestCollisionInspectedRootCount,
			["collision_result_count"] = ImportedForestCollisionResultCount,
			["authored_lod_count"] = _lod4AuthoredTemplates == null ? 0 : 4,
			// Indirect counts stay on the GPU. -1 means unavailable, not an empty forest.
			["visible_instance_count"] = GpuTreeCullingActive ? -1 : CountImportedForestVisibleInstances(),
			["gpu_active"] = GpuTreeCullingActive,
			["gpu_batch_count"] = GpuTreeBatchCount,
			["gpu_resident_root_count"] = GpuTreeResidentRootCount,
			["gpu_buffer_bytes"] = GpuTreeResidentBytes
		};
		return result;
	}

	private int CountImportedForestVisibleInstances()
	{
		int count = 0;
		AccumulateImportedInstances(_lod1TrunkTiles, ref count);
		AccumulateImportedInstances(_lod2TrunkTiles, ref count);
		AccumulateImportedInstances(_lod3TrunkTiles, ref count);
		AccumulateImportedInstances(_vegetationLod4Tiles, ref count);
		AccumulateImportedInstances(_vegetationLod5Tiles, ref count);
		return count;
	}

	private static void AccumulateImportedInstances<TKey>(IReadOnlyDictionary<TKey, MultiMeshInstance3D> pool, ref int count) where TKey : notnull
	{
		foreach (MultiMeshInstance3D node in pool.Values)
			if (node.Visible && node.Multimesh != null) count += node.Multimesh.VisibleInstanceCount >= 0
				? node.Multimesh.VisibleInstanceCount : node.Multimesh.InstanceCount;
	}

	private int CountImportedForestRecords()
	{
		int count = 0;
		foreach (VegetationTile tile in _vegetationTiles.Values)
			foreach (CachedTree tree in tile.Trees)
				if (tree.Variant >= 0 && tree.Variant < ImportedTreeCatalog.VariantCount) count++;
		return count;
	}

	private void UpdateImportedForestCollisions()
	{
		using var collisionScope = RingPerformanceTelemetry.MeasureCpu("Trees collisions (inclusive)");
		// A refresh deferred by an in-flight source plan lands here even when the
		// scan below is skipped as unchanged.
		FlushDeferredGpuTreeProtectedRefresh();
		if (!UseRegionalImportedForest || !EnableImportedForestCollisions || _grid == null || _explorer == null) { ClearImportedForestCollisions(); return; }
		_importedForestCollisionRoot ??= CreateImportedForestCollisionRoot();
		RingCanonicalPosition focus = _explorer.CanonicalPosition;
		long focusAround = (long)Math.Floor(focus.AroundMeters / 4.0f);
		long focusAcross = (long)Math.Floor(focus.AcrossMeters / 4.0f);
		Vector3 center = _grid.GetCanonicalRingCenterWorld();
		float radius = Mathf.Max(10.0f, ImportedForestCollisionRadiusMeters);
		int maximum = Mathf.Max(16, MaximumImportedForestColliders);
		if (focusAround == _lastImportedForestCollisionAround && focusAcross == _lastImportedForestCollisionAcross
			&& _lastImportedForestCollisionEditVersion == _vegetationEditVersion && _lastImportedForestCollisionTileEpoch == _vegetationTileGenerationEpoch
			&& center == _lastImportedForestCollisionCenter && radius == _lastImportedForestCollisionRadius
			&& maximum == _lastMaximumImportedForestColliders) return;
		_lastImportedForestCollisionAround = focusAround;
		_lastImportedForestCollisionAcross = focusAcross;
		_lastImportedForestCollisionEditVersion = _vegetationEditVersion;
		_lastImportedForestCollisionTileEpoch = _vegetationTileGenerationEpoch;
		_lastImportedForestCollisionCenter = center;
		_lastImportedForestCollisionRadius = radius;
		_lastMaximumImportedForestColliders = maximum;
		double circumference = _grid.GetCanonicalRingCircumferenceMeters64();
		using (RingPerformanceTelemetry.MeasureCpu("Trees collision scan/sort"))
			SelectImportedForestCollisionTrees(focus, CaptureVegetationLayout(circumference), _grid.RingAcrossWidthMeters, radius, maximum);
		_importedForestCollisionRequired.Clear();
		using (RingPerformanceTelemetry.MeasureCpu("Trees collider maintenance"))
		{
			// These are identical for every collider in this pass.
			IReadOnlyList<ImportedTreeVariant> variants = ImportedTreeCatalog.Load();
			RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
			Transform3D parentGlobal = _importedForestCollisionRoot.GlobalTransform;
			foreach ((CachedTree tree, _) in _importedForestCollisionCandidates)
			{
				_importedForestCollisionRequired.Add(tree.Id);
				InstallImportedForestCollider(tree, variants, geometry, parentGlobal);
			}
			_importedForestBodyRemovalScratch.Clear();
			foreach (ulong id in _importedForestBodies.Keys)
				if (!_importedForestCollisionRequired.Contains(id)) _importedForestBodyRemovalScratch.Add(id);
			foreach (ulong id in _importedForestBodyRemovalScratch)
			{
				_importedForestBodies[id].QueueFree();
				_importedForestBodies.Remove(id);
				_importedForestColliderPoses.Remove(id);
			}
		}
		using (RingPerformanceTelemetry.MeasureCpu("Trees protected-metadata refresh"))
			RefreshGpuTreeProtectedFlags();
	}

	private void SelectImportedForestCollisionTrees(RingCanonicalPosition focus, in VegetationLayout layout, double ringAcrossWidthMeters, float radius, int maximum)
	{
		_importedForestCollisionCandidates.Clear();
		_importedForestCollisionTileKeys.Clear();
		ImportedForestCollisionInspectedTileCount = 0;
		ImportedForestCollisionInspectedRootCount = 0;
		double radiusSquared = radius * radius;
		long centerAroundCell = Math.Clamp((long)Math.Floor(focus.AroundMeters / layout.AroundCellMeters), 0L, layout.AroundCellCount - 1L);
		long centerAroundTile = centerAroundCell / layout.TileCells;
		long centerAcrossTile = FloorDiv((long)Math.Floor(focus.AcrossMeters), Math.Max(1, Mathf.RoundToInt(layout.AcrossTileMeters)));
		int aroundRadius = Math.Max(1, (int)Math.Ceiling(radius / layout.AroundTileMeters) + 1);
		int acrossRadius = Math.Max(1, Mathf.CeilToInt(radius / layout.AcrossTileMeters) + 1);
		for (long across = centerAcrossTile - acrossRadius; across <= centerAcrossTile + acrossRadius; across++)
		{
			if (across < 0 || across * layout.AcrossTileMeters >= ringAcrossWidthMeters) continue;
			for (long rawAround = centerAroundTile - aroundRadius; rawAround <= centerAroundTile + aroundRadius; rawAround++)
			{
				var key = new VegetationTileKey(PositiveModulo(rawAround, layout.AroundTileCount), across);
				if (!_importedForestCollisionTileKeys.Add(key) || !_desiredVegetationTiles.Contains(key) || !_vegetationTiles.TryGetValue(key, out VegetationTile? tile)) continue;
				if (TileDistanceSquared(key, focus, layout) > radiusSquared) continue;
				ImportedForestCollisionInspectedTileCount++;
				foreach (CachedTree tree in tile.Trees)
				{
					ImportedForestCollisionInspectedRootCount++;
					double da = WrappedVegetationDelta(tree.Around - focus.AroundMeters, layout.CircumferenceMeters);
					double dz = tree.Across - focus.AcrossMeters;
					double distanceSquared = da * da + dz * dz;
					if (distanceSquared > radiusSquared) continue;
					AddImportedForestCollisionCandidate(tree, distanceSquared, maximum);
				}
			}
		}
		_importedForestCollisionCandidates.Sort(CompareImportedForestCollisionCandidates);
		ImportedForestCollisionResultCount = _importedForestCollisionCandidates.Count;
	}

	private static double TileDistanceSquared(VegetationTileKey key, RingCanonicalPosition focus, in VegetationLayout layout)
	{
		long firstCell = key.Around * layout.TileCells;
		long cellCount = Math.Max(1L, Math.Min(layout.TileCells, layout.AroundCellCount - firstCell));
		double aroundMin = firstCell * layout.AroundCellMeters;
		double aroundMax = (firstCell + cellCount) * layout.AroundCellMeters;
		double acrossMin = key.Across * layout.AcrossTileMeters;
		double acrossMax = acrossMin + layout.AcrossTileMeters;
		double aroundDistance = Math.Min(Math.Abs(WrappedVegetationDelta(aroundMin - focus.AroundMeters, layout.CircumferenceMeters)), Math.Abs(WrappedVegetationDelta(aroundMax - focus.AroundMeters, layout.CircumferenceMeters)));
		if (focus.AroundMeters >= aroundMin && focus.AroundMeters <= aroundMax) aroundDistance = 0;
		double acrossDistance = focus.AcrossMeters < acrossMin ? acrossMin - focus.AcrossMeters : focus.AcrossMeters > acrossMax ? focus.AcrossMeters - acrossMax : 0;
		return aroundDistance * aroundDistance + acrossDistance * acrossDistance;
	}

	private void AddImportedForestCollisionCandidate(CachedTree tree, double distanceSquared, int maximum)
	{
		var candidate = (tree, distanceSquared);
		if (_importedForestCollisionCandidates.Count < maximum) { _importedForestCollisionCandidates.Add(candidate); return; }
		int worst = 0;
		for (int i = 1; i < _importedForestCollisionCandidates.Count; i++)
			if (CompareImportedForestCollisionCandidates(_importedForestCollisionCandidates[worst], _importedForestCollisionCandidates[i]) < 0) worst = i;
		if (CompareImportedForestCollisionCandidates(candidate, _importedForestCollisionCandidates[worst]) < 0) _importedForestCollisionCandidates[worst] = candidate;
	}

	private static int CompareImportedForestCollisionCandidates((CachedTree Tree, double DistanceSquared) left, (CachedTree Tree, double DistanceSquared) right)
	{
		int distance = left.DistanceSquared.CompareTo(right.DistanceSquared);
		return distance != 0 ? distance : left.Tree.Id.CompareTo(right.Tree.Id);
	}

	private Node3D CreateImportedForestCollisionRoot()
	{
		var root = new Node3D { Name = "ImportedForestTrunkCollisions" };
		AddChild(root);
		return root;
	}

	private void InstallImportedForestCollider(in CachedTree tree, IReadOnlyList<ImportedTreeVariant> variants,
		in RingLodGeometrySnapshot geometry, in Transform3D parentGlobal)
	{
		if (_grid == null || _importedForestCollisionRoot == null) return;
		if (tree.Variant < 0 || tree.Variant >= variants.Count) return;
		ImportedTreeVariant variant = variants[tree.Variant];
		Vector3 baseWorld = geometry.CanonicalToWorld(tree.Around, RootAltitude(tree.SurfaceHeight, RootEmbedMeters), tree.Across);
		float heightScale = tree.Height / Mathf.Max(.01f, variant.HeightMeters);
		// Rendering applies the catalog's uniform height normalization, so physics
		// must use that identical scale for the authored trunk radius.
		float widthScale = heightScale;
		Vector3 trunkTop = geometry.CanonicalToWorld(tree.Around, RootAltitude(tree.SurfaceHeight, RootEmbedMeters) + variant.TrunkHeightMeters * heightScale, tree.Across);
		float height = Mathf.Max(.4f, baseWorld.DistanceTo(trunkTop));
		float radius = Mathf.Max(.12f, variant.TrunkRadiusMeters * widthScale);
		Vector3 up = (trunkTop - baseWorld).Normalized();
		Vector3 right = up.Cross(Vector3.Forward).Normalized();
		if (right.LengthSquared() < .001f) right = up.Cross(Vector3.Right).Normalized();
		Vector3 forward = right.Cross(up).Normalized();
		Transform3D transform = new(new Basis(right, up, forward), baseWorld.Lerp(trunkTop, .5f));
		var pose = new ImportedForestColliderPose(transform, radius, height, parentGlobal);
		if (_importedForestBodies.TryGetValue(tree.Id, out StaticBody3D? existing))
		{
			if (_importedForestColliderPoses.TryGetValue(tree.Id, out ImportedForestColliderPose stored) && stored == pose) return;
			_importedForestColliderPoses[tree.Id] = pose;
			existing.GlobalTransform = transform;
			if (existing.GetNodeOrNull<CollisionShape3D>("Trunk")?.Shape is CylinderShape3D cylinder)
			{
				cylinder.Radius = radius;
				cylinder.Height = height;
			}
			return;
		}
		var body = new StaticBody3D { Name = $"ImportedTreeTrunk_{tree.Id:X}" };
		var shape = new CollisionShape3D { Name = "Trunk", Shape = new CylinderShape3D { Radius = radius, Height = height } };
		body.AddChild(shape);
		_importedForestCollisionRoot.AddChild(body);
		body.GlobalTransform = transform;
		_importedForestBodies.Add(tree.Id, body);
		_importedForestColliderPoses[tree.Id] = pose;
	}

	private void ClearImportedForestCollisions()
	{
		foreach (StaticBody3D body in _importedForestBodies.Values) body.QueueFree();
		_importedForestBodies.Clear();
		_importedForestColliderPoses.Clear();
		RefreshGpuTreeProtectedFlags();
		_lastImportedForestCollisionAround = long.MinValue;
		_lastImportedForestCollisionAcross = long.MinValue;
		_lastImportedForestCollisionRadius = float.NaN;
		_lastMaximumImportedForestColliders = int.MinValue;
		ImportedForestCollisionInspectedTileCount = 0;
		ImportedForestCollisionInspectedRootCount = 0;
		ImportedForestCollisionResultCount = 0;
	}
}
