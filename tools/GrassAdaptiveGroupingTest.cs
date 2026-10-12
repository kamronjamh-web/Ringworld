using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Security.Cryptography;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

/// <summary>Deterministic production contracts and canonical leaf snapshots for matched captures.</summary>
public partial class GrassAdaptiveGroupingTest : RefCounted
{
    public string Result { get; private set; } = "RUNNING";
    public async void Start()
    {
        Result = RunContracts();
        if (!Result.StartsWith("PASS", StringComparison.Ordinal)) return;
        Result = "RUNNING";
        try
        {
            await RunExternalParentContracts();
            if (RenderingServer.GetRenderingDevice() != null)
            {
                await RunGpuGroupingParity();
                await RunGroupingLifecycle();
            }
            Result = "PASS adaptive grouping, work-cache keys and external renderer-parent movement; GPU parity "
                + (RenderingServer.GetRenderingDevice() != null ? "verified" : "requires Forward+");
        }
        catch (Exception error) { Result = "FAIL " + error; }
    }

    private async Task RunGpuGroupingParity()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        async Task Frames(int count) { for (int i = 0; i < count; i++) await ToSignal(tree, SceneTree.SignalName.ProcessFrame); }
        void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
        using var gpu = new RingGpuVegetationCompute();
        using var mesh = new ArrayMesh();
        var arrays = new Godot.Collections.Array(); arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
        arrays[(int)Mesh.ArrayType.Index] = new[] { 0, 1, 2 }; mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        const long circumference = 981_748L * 32L;
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            circumference / Math.Tau, 0, 0, circumference, -1000, 1000, new RingCanonicalPosition(16, 0, 16));
        var leaves = new List<RingGrassTileData>();
        var ids = new List<int>();
        var transforms = new List<Transform3D>();
        for (int leaf = 0; leaf < 4; leaf++)
        {
            const int count = 29;
            float[] rows = new float[count * 20];
            for (int i = 0; i < count; i++)
            {
                int at = i * 20; rows[at] = rows[at + 5] = rows[at + 10] = 1;
                rows[at + 3] = (i % 7) - 3; rows[at + 11] = (i / 7) - 2;
                rows[at + 12] = rows[at + 13] = rows[at + 14] = rows[at + 15] = 1;
                rows[at + 16] = leaf * 100 + i; rows[at + 17] = i * .071f;
                rows[at + 18] = leaf * .1f; rows[at + 19] = i * .019f;
            }
            var data = new RingGrassTileData(new RingGrassTileKey(leaf % 2, leaf / 2),
                new RingGrassTileBounds((leaf % 2) * 16, (leaf % 2 + 1) * 16, (leaf / 2) * 16, (leaf / 2 + 1) * 16),
                0, rows, new Aabb(new Vector3(-8, -2, -8), new Vector3(16, 8, 16)), (ulong)leaf);
            leaves.Add(data); transforms.Add(RingGrassCoordinates.CreateTileWorldTransform(data.Bounds, 0, geometry));
            ids.Add(gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("group-parity-leaf-" + leaf,
                new MultiMesh(), mesh, rows, null, count, data.LocalAabb, true)));
        }
        RingGrassTileData merged = RingGrassGrouping.Build(leaves, geometry, out Vector4[] metadata);
        int mergedId = gpu.RegisterBatch(new RingGpuVegetationCompute.BatchRegistration("group-parity-merged",
            new MultiMesh(), mesh, merged.Buffer, metadata, merged.InstanceCount, merged.LocalAabb, true));
        Transform3D mergedTransform = RingGrassCoordinates.CreateTileWorldTransform(merged.Bounds, 0, geometry);
        int mergedRank = 0;
        float maximumPoseError = 0;
        Vector3 cameraPosition = new(0, 2, 10);
        int guaranteedNear = 0, guaranteedIntermediate = 0;
        for (int leaf = 0; leaf < leaves.Count; leaf++) for (int rank = 0; rank < leaves[leaf].InstanceCount; rank++, mergedRank++)
        {
            Vector3 baselineRoot = (transforms[leaf] * RingGrassGrouping.ReadTransform(leaves[leaf].Buffer, rank * 20)).Origin;
            Vector3 groupedRoot = (mergedTransform * RingGrassGrouping.ReadTransform(merged.Buffer, mergedRank * 20)).Origin;
            maximumPoseError = Math.Max(maximumPoseError, baselineRoot.DistanceTo(groupedRoot));
            float distance = baselineRoot.DistanceTo(cameraPosition);
            float sourceRank = (rank + .5f) / leaves[leaf].InstanceCount;
            // Outside the transition, acceptance is independent of the hash:
            // near handoff is zero, intermediate handoff is one, and all roots
            // remain well inside the final distance-fade band. The minimum
            // density used below must leave positive accepted roots in both.
            if (distance <= 8 && sourceRank <= .61f) guaranteedNear++;
            if (distance >= 16 && distance <= 76 && sourceRank <= .37f) guaranteedIntermediate++;
            Check(merged.Buffer[mergedRank * 20 + 16] == leaf * 100 + rank,
                $"GPU fixture identity payload changed leaf={leaf} rank={rank}");
        }
        Check(maximumPoseError < .006f, $"GPU fixture leaf/group coordinates differ maximumError={maximumPoseError}");
        Check(guaranteedNear > 0 && guaranteedIntermediate > 0,
            $"GPU fixture fails to guarantee both LOD outputs near={guaranteedNear} intermediate={guaranteedIntermediate}");
        for (int frame = 0; frame < 180 && !gpu.Ready; frame++) await Frames(1);
        Check(gpu.Ready && mergedId >= 0 && ids.All(id => id >= 0), "group parity registration/pipeline");
        async Task<HashSet<int>> Read(int id)
        {
            var completion = new TaskCompletionSource<(uint[], float[])>();
            gpu.RequestReadbackForTests(id, (commands, rows) => completion.TrySetResult((commands, rows)));
            for (int frame = 0; frame < 180 && !completion.Task.IsCompleted; frame++) await Frames(1);
            Check(completion.Task.IsCompleted, "group parity readback timeout");
            var (commands, rows) = await completion.Task;
            Check(commands.Length >= 5 && commands[1] <= rows.Length / 20, "group parity command bounds");
            var identities = new HashSet<int>();
            for (int i = 0; i < commands[1]; i++) Check(identities.Add((int)rows[i * 20 + 16]), "group parity duplicate root");
            return identities;
        }
        foreach (int pass in new[] { 0, 1 }) foreach (float density in new[] { 1f, .37f })
        {
            var parameters = new List<RingGpuVegetationCompute.BatchFrameParameters>();
            RingGpuVegetationCompute.BatchFrameParameters Parameter(int id, Transform3D transform) =>
                new(id, transform, true, pass, 12, 80, 8, .61f, density, true, 1, false, null, 1, null, 0);
            for (int i = 0; i < ids.Count; i++) parameters.Add(Parameter(ids[i], transforms[i]));
            parameters.Add(Parameter(mergedId, mergedTransform));
            Check(gpu.RequestFrame(new RingGpuVegetationCompute.FrameRequest(cameraPosition,
                new uint[] { 1000, 1000, 1000, 1000, 1000 }, parameters, false, 0)), "group parity request");
            await Frames(5);
            var baseline = new HashSet<int>();
            foreach (int id in ids) baseline.UnionWith(await Read(id));
            HashSet<int> grouped = await Read(mergedId);
            string missing = string.Join(",", baseline.Except(grouped).OrderBy(id => id).Take(20));
            string extra = string.Join(",", grouped.Except(baseline).OrderBy(id => id).Take(20));
            Check(baseline.Count > 0 && baseline.SetEquals(grouped),
                $"GPU grouped selector changed identities pass={pass} density={density} expectedCount={baseline.Count} actualCount={grouped.Count} missing=[{missing}] extra=[{extra}] maximumPoseError={maximumPoseError}");
        }
        const long budgetRevision = 707;
        var budgetParameters = new List<RingGpuVegetationCompute.BatchFrameParameters>();
        RingGpuVegetationCompute.BatchFrameParameters BudgetParameter(int id, Transform3D transform) =>
            new(id, transform, true, 1, 12, 80, 8, .61f, .37f, true, 1, false, null, 1, null, budgetRevision);
        for (int i = 0; i < ids.Count; i++) budgetParameters.Add(BudgetParameter(ids[i], transforms[i]));
        budgetParameters.Add(BudgetParameter(mergedId, mergedTransform));
        int[] allIds = ids.Append(mergedId).ToArray();
        Check(allIds.All(id => !gpu.IsBatchReady(id, budgetRevision)), "candidate-budget fixture revision already completed");
        bool oversizedCompleted = false;
        for (int attempt = 0; attempt < 16 && allIds.Any(id => !gpu.IsBatchReady(id, budgetRevision)); attempt++)
        {
            long updates = gpu.CompletedUpdates;
            Check(gpu.RequestFrame(new RingGpuVegetationCompute.FrameRequest(cameraPosition,
                new uint[] { 1000, 1000, 1000, 1000, 1000 }, budgetParameters, true, 16, 50)), "candidate-budget submission");
            for (int frame = 0; frame < 180 && gpu.CompletedUpdates == updates; frame++) await Frames(1);
            Check(gpu.CompletedUpdates > updates, "candidate-budget unchanged deferred request made no progress");
            int candidates = gpu.LastFrameDispatchedCandidateCount;
            Check(candidates <= 50 || (candidates == 116 && gpu.LastFrameDispatchedBatchCount == 1),
                $"candidate budget violated candidates={candidates} batches={gpu.LastFrameDispatchedBatchCount}");
            oversizedCompleted |= candidates == 116 && gpu.IsBatchReady(mergedId, budgetRevision);
        }
        Check(allIds.All(id => gpu.IsBatchReady(id, budgetRevision)) && oversizedCompleted,
            "candidate budget starved deferred or individually oversized group");
        var finalBaseline = new HashSet<int>();
        foreach (int id in ids) finalBaseline.UnionWith(await Read(id));
        Check(finalBaseline.Count > 0 && finalBaseline.SetEquals(await Read(mergedId)), "candidate budget changed final root identities");
        gpu.Dispose();
        await Frames(3);
    }
    public string RunContracts()
    {
        try
        {
            RingStandingGrassLayer.RunGrassAdaptiveGroupingTests();
            RunWorkCacheContracts();
            return "PASS adaptive grouping and work-cache production contracts";
        }
        catch (Exception error) { return "FAIL " + error; }
    }

    private static void RunWorkCacheContracts()
    {
        var tree = (SceneTree)Engine.GetMainLoop();
        var layer = new RingStandingGrassLayer { ProcessMode = Node.ProcessModeEnum.Disabled };
        var camera = new Camera3D();
        tree.Root.AddChild(layer); tree.Root.AddChild(camera);
        try
        {
            const long circumference = 981_748L * 32L;
            var focus = new RingCanonicalPosition(186000, 0, 141000);
            var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
                circumference / Math.Tau, 0, 0, circumference, -1000, 1000, focus);
            object Key(string method, params object[] arguments) => typeof(RingStandingGrassLayer)
                .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(layer, arguments)!;
            object Visibility() => Key("CaptureGrassVisibilityWorkKey", geometry, focus, circumference, camera);
            object Gpu() => Key("CaptureGrassGpuWorkKey", camera);
            object Transform() => Key("CaptureGrassTransformWorkKey", geometry);
            void Invalidate() => Key("InvalidateGrassWork");
            void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
            object original = Visibility(), gpu = Gpu(), transform = Transform();
            Check(original.Equals(Visibility()) && gpu.Equals(Gpu()) && transform.Equals(Transform()), "stationary work keys changed");
            void CameraChange(Action mutate, string label)
            {
                object prior = Visibility(), priorGpu = Gpu(); mutate();
                Check(!prior.Equals(Visibility()) && !priorGpu.Equals(Gpu()), "camera change reused work: " + label);
            }
            CameraChange(() => camera.Position += Vector3.Right, "movement");
            CameraChange(() => camera.Rotation += new Vector3(0, .1f, 0), "turn");
            CameraChange(() => camera.Fov += 1, "FOV");
            CameraChange(() => camera.HOffset += .1f, "projection offset");
            CameraChange(() => camera.Projection = Camera3D.ProjectionType.Orthogonal, "projection type");
            CameraChange(() => camera.Size += 1, "orthographic size");
            original = Visibility(); gpu = Gpu(); transform = Transform();
            layer.LodFarDistanceMeters += 1;
            Check(!original.Equals(Visibility()) && !gpu.Equals(Gpu()) && !transform.Equals(Transform()), "settings edit did not invalidate work");
            original = Visibility(); transform = Transform(); layer.Position += Vector3.Right;
            Check(!original.Equals(Visibility()) && !transform.Equals(Transform()), "external layer transform reused work");
            original = Visibility(); gpu = Gpu(); transform = Transform(); Invalidate();
            Check(!original.Equals(Visibility()) && !gpu.Equals(Gpu()) && !transform.Equals(Transform()), "membership/data/renderer revision reused work");
            original = Visibility(); geometry = geometry with { FloatingOriginWorldOffset = new Vector3(100, 0, 0) };
            Check(!original.Equals(Visibility()), "floating origin reused visibility work");
            original = Visibility(); layer.Enabled = false;
            Check(!original.Equals(Visibility()), "disable reused visibility work");
            layer.Enabled = true; Check(original.Equals(Visibility()), "restored inputs failed key equality");
        }
        finally { tree.Root.RemoveChild(camera); tree.Root.RemoveChild(layer); camera.Free(); layer.Free(); }
    }

    private async Task RunExternalParentContracts()
    {
        const long circumference = 981_748L * 32L;
        var tree = (SceneTree)Engine.GetMainLoop();
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("terrain fixture missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5000000, 318000, 8000);
        var layer = new RingStandingGrassLayer
        {
            ProcessMode = Node.ProcessModeEnum.Disabled, UseGpuCulling = false, CullGrassTiles = false,
            EnableAdaptiveGrassGrouping = false,
            EnableHierarchicalCoverage = false, VisibilityRadiusMeters = 48, SpatialTileMeters = 16,
            MaximumResidentTiles = 4, MaximumInstances = 2048, MaximumInstancesPerTile = 512,
            MaximumTileBuildsInFlight = 2, MaximumTileUploadsPerFrame = 2, CacheGrassPresentation = true
        };
        var external = new Node3D(); tree.Root.AddChild(layer); tree.Root.AddChild(external);
        var focus = new RingCanonicalPosition(186000, 0, 141000);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            circumference / Math.Tau, 0, 0, circumference, -1000, 1000, focus);
        var edits = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        try
        {
            layer.InitializeForTests();
            for (int frame = 0; frame < 600; frame++)
            {
                layer.StepForTests(sampler, edits, focus, geometry);
                await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
                if (layer.ResidentGrassInstanceCount > 0 && layer.PendingGrassTileBuildCount == 0 && layer.PendingGrassUploadCount == 0) break;
            }
            var tile = layer.CaptureTileAuditsForTests().FirstOrDefault(item => item.RenderInstanceId != 0);
            var renderer = GodotObject.InstanceFromId(tile.RenderInstanceId) as MultiMeshInstance3D
                ?? throw new InvalidOperationException("external-parent test has no renderer");
            Transform3D expected = renderer.GlobalTransform;
            renderer.Reparent(external, true);
            external.Position += new Vector3(17, 3, -11);
            if (renderer.GlobalTransform == expected) throw new InvalidOperationException("external parent did not move renderer");
            long scans = layer.GrassPresentationScans;
            layer.RefreshLodPresentationForTests(focus, geometry, circumference);
            if (!renderer.GlobalTransform.IsEqualApprox(expected) || layer.GrassPresentationScans <= scans)
                throw new InvalidOperationException("cache skipped external-parent transform correction");
            renderer.Reparent(layer, true);
            layer.RefreshLodPresentationForTests(focus, geometry, circumference);
            long hits = layer.GrassPresentationCacheHits;
            layer.RefreshLodPresentationForTests(focus, geometry, circumference);
            if (layer.GrassPresentationCacheHits <= hits) throw new InvalidOperationException("owned renderer failed to resume stationary caching");
        }
        finally
        {
            tree.Root.RemoveChild(layer); layer.Free();
            tree.Root.RemoveChild(external); external.Free();
        }
    }

    private async Task RunGroupingLifecycle()
    {
        const long circumference = 981_748L * 32L;
        var tree = (SceneTree)Engine.GetMainLoop();
        void Check(bool success, string message) { if (!success) throw new InvalidOperationException(message); }
        var settings = (VoxelTerrainSettings)(GD.Load<VoxelTerrainSettings>("res://resources/voxel/voxel_terrain_settings_ring_integration.tres")?.Duplicate(true)
            ?? throw new InvalidOperationException("group lifecycle terrain fixture missing"));
        using var sampler = new RingTerrainSampler(settings, true, circumference, 5000000, 318000, 8000);
        var layer = new RingStandingGrassLayer
        {
            ProcessMode = Node.ProcessModeEnum.Disabled, UseGpuCulling = true, CullGrassTiles = false,
            GpuFrustumCulling = false, EnableHierarchicalCoverage = false, EnableAdaptiveGrassGrouping = true,
            EnableGrassGustTexture = false,
            EnableDistanceLod = true, LodNearDistanceMeters = 22, LodFarDistanceMeters = 2000,
            LodTransitionWidthMeters = 4,
            GrassGroupingMaximumLevel = 1, GrassGroupingStartMeters = 32, GrassGroupingFarStartMeters = 128,
            GrassGroupingHysteresisMeters = 8, VisibilityRadiusMeters = 128, SpatialTileMeters = 16,
            MaximumResidentTiles = 96, MaximumInstances = 49152, MaximumInstancesPerTile = 512,
            MaximumTileBuildsInFlight = 4, MaximumTileUploadsPerFrame = 4, MaximumTileUploadMilliseconds = 8,
            MaximumGpuCullingBatchesPerFrame = 16
        };
        var camera = new Camera3D { Position = new Vector3(500, 500, 500), Far = 2000 };
        tree.Root.AddChild(layer); tree.Root.AddChild(camera); camera.Current = true;
        var focus = new RingCanonicalPosition(186000, 0, 141000);
        var geometry = new RingLodGeometrySnapshot(Transform3D.Identity, Vector3.Zero, Vector3.Zero,
            circumference / Math.Tau, 0, 0, circumference, -1000, 1000, focus);
        var edits = new RingLodSurfaceEditSnapshot(1, circumference, new Dictionary<RingTerrainColumnCoordinate, int>());
        string Sources() => string.Join(";", layer.CaptureTileAuditsForTests().OrderBy(tile => tile.Around).ThenBy(tile => tile.Across)
            .Select(tile => $"{tile.Around},{tile.Across},{tile.InstanceCount},{tile.PlacementHash}"));
        async Task Step()
        {
            layer.StepForTests(sampler, edits, focus, geometry);
            layer.AssertAdaptiveGrassOwnershipForTests();
            await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
            await Task.Delay(1);
        }
        async Task Settle(string label)
        {
            int stable = 0;
            for (int frame = 0; frame < 1200; frame++)
            {
                await Step();
                if (layer.IsGrassStreamingDiagnosticsSettled && layer.IsAdaptiveGrassGroupingSettled
                    && layer.ResidentGrassInstanceCount > 0) stable++; else stable = 0;
                if (stable >= 5) return;
            }
            throw new TimeoutException("production grouping lifecycle did not settle: " + label + " " + layer.AdaptiveGrassGroupingStatus);
        }
        try
        {
            layer.InitializeForTests();
            await Settle("initial distant camera");
            Check(layer.AdaptiveGrassLevel1GroupCount > 0, "production lifecycle activated no level 1 groups");
            int level1 = layer.AdaptiveGrassLevel1GroupCount;
            int groups = layer.AdaptiveGrassGroupCount;
            string sourceBaseline = Sources(); int builds = layer.TotalGrassBuildCount;
            int uploads = layer.TotalGrassUploadCount;
            int roots = layer.ResidentGrassInstanceCount;
            var owners = (System.Collections.IDictionary)typeof(RingStandingGrassLayer)
                .GetField("_adaptiveLeafOwners", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layer)!;
            var previousOwners = owners.Keys.Cast<RingGrassTileKey>().ToDictionary(key => key, key => owners[key]!);
            string GroupKey(object group) => group.GetType().GetField("Key", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(group)!.ToString()!;
            string Keys(IEnumerable<object> groupValues) => string.Join(";", groupValues.Distinct().Select(GroupKey).OrderBy(key => key).Take(16));
            string priorKeys = Keys(previousOwners.Values);
            var minimumMethod = typeof(RingStandingGrassLayer).GetMethod("AdaptiveGroupMinimumDistance", BindingFlags.Instance | BindingFlags.NonPublic)!;
            float minimumPriorDistance = previousOwners.Values.Distinct().Min(group =>
            {
                object resident = group.GetType().GetField("Resident", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(group)!;
                return (float)minimumMethod.Invoke(layer, new object[] { resident, geometry, camera.GlobalPosition, focus, circumference })!;
            });
            float farSplitThreshold = Math.Max(layer.GrassGroupingStartMeters - layer.GrassGroupingHysteresisMeters,
                layer.LodNearDistanceMeters + layer.LodTransitionWidthMeters * .5f + Math.Max(4, layer.GrassGroupingHysteresisMeters));
            Check(minimumPriorDistance > farSplitThreshold + 10,
                $"small camera movement fixture lacks threshold margin minimum={minimumPriorDistance} split={farSplitThreshold}");
            camera.Position += Vector3.One;
            await Settle("small camera movement");
            var lostOwners = previousOwners.Where(pair => !owners.Contains(pair.Key) || !ReferenceEquals(owners[pair.Key], pair.Value)).ToArray();
            string currentKeys = Keys(owners.Values.Cast<object>());
            string lostKeys = Keys(lostOwners.Select(pair => pair.Value));
            string movementDiagnostic = $"beforeGroups={groups}/{level1} afterGroups={layer.AdaptiveGrassGroupCount}/{layer.AdaptiveGrassLevel1GroupCount} "
                + $"beforeOwnedLeaves={previousOwners.Count} afterOwnedLeaves={owners.Count} lostOwners={lostOwners.Length} "
                + $"beforeRoots={roots} afterRoots={layer.ResidentGrassInstanceCount} builds={builds}->{layer.TotalGrassBuildCount} "
                + $"uploads={uploads}->{layer.TotalGrassUploadCount} beforeKeys=[{priorKeys}] afterKeys=[{currentKeys}] lostKeys=[{lostKeys}] "
                + $"canonicalSourcesEqual={Sources() == sourceBaseline} minimumPriorDistance={minimumPriorDistance} splitThreshold={farSplitThreshold}";
            GD.Print("GRASS_GROUPING_LIFECYCLE_MOVEMENT " + movementDiagnostic);
            Check(lostOwners.Length == 0, "small distant camera movement replaced existing group ownership: " + movementDiagnostic);
            Check(groups == layer.AdaptiveGrassGroupCount && level1 == layer.AdaptiveGrassLevel1GroupCount,
                "completed grouping convergence changed after small distant camera movement: " + movementDiagnostic);
            Check(Sources() == sourceBaseline && layer.TotalGrassBuildCount == builds,
                "small distant camera movement changed canonical sources: " + movementDiagnostic);
            RingGrassTileKey ownedKey = (RingGrassTileKey)owners.Keys.Cast<object>().First();
            object selectedGroup = owners[ownedKey]!;
            object selectedResident = selectedGroup.GetType().GetField("Resident", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(selectedGroup)!;
            var selectedData = (RingGrassTileData)selectedResident.GetType().GetField("Data", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(selectedResident)!;
            Transform3D selectedWorld = RingGrassCoordinates.CreateTileWorldTransform(selectedData.Bounds, selectedData.AnchorHeight, geometry);
            float Minimum(Vector3 position) => (float)minimumMethod.Invoke(layer, new object[] { selectedResident, geometry, position, focus, circumference })!;
            float splitThreshold = Math.Max(layer.GrassGroupingStartMeters - layer.GrassGroupingHysteresisMeters,
                layer.LodNearDistanceMeters + layer.LodTransitionWidthMeters * .5f + Math.Max(4, layer.GrassGroupingHysteresisMeters));
            float joinThreshold = Math.Max(layer.GrassGroupingStartMeters,
                layer.LodNearDistanceMeters + layer.LodTransitionWidthMeters * .5f + Math.Max(4, layer.GrassGroupingHysteresisMeters))
                + Math.Max(4, layer.GrassGroupingHysteresisMeters);
            float hysteresisTarget = (splitThreshold + joinThreshold) * .5f;
            float low = 0, high = 500;
            for (int iteration = 0; iteration < 30; iteration++)
            {
                float middle = (low + high) * .5f;
                if (Minimum(selectedWorld.Origin + selectedWorld.Basis.Y.Normalized() * middle) < hysteresisTarget) low = middle; else high = middle;
            }
            camera.GlobalPosition = selectedWorld.Origin + selectedWorld.Basis.Y.Normalized() * ((low + high) * .5f);
            float bandDistance = Minimum(camera.GlobalPosition);
            Check(bandDistance > splitThreshold && bandDistance < joinThreshold, "hysteresis fixture camera is outside production band");
            await Settle("inside hysteresis band");
            Check(owners.Contains(ownedKey) && ReferenceEquals(owners[ownedKey], selectedGroup),
                "active group split while inside hysteresis band");
            var leaf = layer.CaptureTileDataForTests().First(tile => tile.Key == ownedKey);
            Transform3D leafWorld = RingGrassCoordinates.CreateTileWorldTransform(leaf.Bounds, leaf.AnchorHeight, geometry);
            camera.GlobalPosition = leafWorld * new Vector3(0, 2, 0);
            await Settle("near camera split");
            Check(!owners.Contains(ownedKey), "near camera failed to split selected active group");
            Check(Sources() == sourceBaseline && layer.TotalGrassBuildCount == builds, "camera grouping changes rebuilt canonical sources");
            camera.Position = new Vector3(500, 500, 500);
            await Settle("distant camera regroup");
            Check(layer.AdaptiveGrassLevel1GroupCount > 0 && Sources() == sourceBaseline, "distant camera failed to regroup preserved sources");
            Check(owners.Contains(ownedKey), "terrain edit fixture leaf did not regain grouped ownership");
            long editedAround = (long)leaf.Bounds.AroundCenter;
            int editedAcross = (int)leaf.Bounds.AcrossCenter;
            var columns = new Dictionary<RingTerrainColumnCoordinate, int>
            {
                [new(editedAround, editedAcross)] = sampler.GetSurfaceHeight(editedAround, editedAcross) + 4
            };
            edits = new RingLodSurfaceEditSnapshot(2, circumference, columns);
            await Settle("edit invalidation");
            Check(layer.TotalGrassBuildCount > builds, "grouped terrain edit did not rebuild affected canonical sources");
            layer.Enabled = false; await Step(); await Step();
            Check(layer.ResidentGrassInstanceCount == 0 && layer.AdaptiveGrassGroupCount == 0,
                "disabled grouped layer retained canonical sources or parent outputs");
            layer.Enabled = true; await Settle("re-enabled edited scene");
            Check(layer.AdaptiveGrassLevel1GroupCount > 0, "re-enabled grouped layer did not restore parent outputs");
            layer.AssertAdaptiveGrassOwnershipForTests();
        }
        finally
        {
            tree.Root.RemoveChild(layer); layer.Free();
            tree.Root.RemoveChild(camera); camera.Free();
            for (int frame = 0; frame < 3; frame++) await ToSignal(tree, SceneTree.SignalName.ProcessFrame);
        }
    }

    public Godot.Collections.Dictionary CaptureRootInvariant(Node world)
    {
        var layer = world.GetNode<RingStandingGrassLayer>("StandingGrass");
        var geometry = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").CaptureLodGeometrySnapshot();
        using var sourceStream = new MemoryStream();
        using var poseStream = new MemoryStream();
        using var source = new BinaryWriter(sourceStream);
        using var pose = new BinaryWriter(poseStream);
        int roots = 0, leaves = 0;
        foreach (var tile in layer.CaptureTileDataForTests().OrderBy(item => item.Key.Around).ThenBy(item => item.Key.Across))
        {
            leaves++; roots += tile.InstanceCount;
            source.Write(tile.Key.Around); source.Write(tile.Key.Across);
            source.Write(tile.InstanceCount); source.Write(tile.SourceFraction); source.Write(tile.SourceTier);
            foreach (float value in tile.Buffer) source.Write(value);
            Transform3D transform = RingGrassCoordinates.CreateTileWorldTransform(tile.Bounds, tile.AnchorHeight, geometry);
            for (int at = 0; at < tile.Buffer.Length; at += RingGrassTileData.FloatsPerInstance)
            {
                Vector3 root = transform * new Vector3(tile.Buffer[at + 3], tile.Buffer[at + 7], tile.Buffer[at + 11]);
                pose.Write(tile.Key.Around); pose.Write(tile.Key.Across); pose.Write(at / RingGrassTileData.FloatsPerInstance);
                pose.Write(root.X); pose.Write(root.Y); pose.Write(root.Z);
            }
        }
        source.Flush(); pose.Flush();
        return new Godot.Collections.Dictionary
        {
            ["leafCount"] = leaves, ["rootCount"] = roots,
            ["identityRankWindHash"] = Convert.ToHexString(SHA256.HashData(sourceStream.ToArray())),
            ["canonicalWorldPoseHash"] = Convert.ToHexString(SHA256.HashData(poseStream.ToArray()))
        };
    }
}
