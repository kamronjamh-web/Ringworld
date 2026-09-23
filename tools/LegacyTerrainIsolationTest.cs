using System;
using System.Collections.Generic;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase1;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>Small assertions shared by the headless legacy-presentation isolation test.</summary>
public partial class LegacyTerrainIsolationTest : RefCounted
{
    public bool VerifyDormantGrid(VoxelChunkGridManager grid)
    {
        try
        {
            grid.RebuildGrid();
            grid.ApplySurfaceStreamingRadius(2);
            grid.ApplyVerticalStreamingRadius(2);
            grid.SetStreamingCanonicalFocusOverride(new RingCanonicalPosition(0, 0, 0));
            bool editAccepted = grid.TrySetBlockByWorldPosition(Vector3.Zero, BlockType.Concrete, out _, out _);
            Require(grid.LoadedChunkCount == 0, "dormant grid created legacy chunks");
            Require(!editAccepted, "dormant grid accepted a legacy block edit");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_TERRAIN_ISOLATION=FAIL " + exception.Message);
            return false;
        }
    }

    public bool VerifyLegacyGridBuild(VoxelChunkGridManager grid)
    {
        try
        {
            grid.RebuildGrid();
            Require(grid.LoadedChunkCount > 0, "explicit legacy mode did not build a chunk");
            Require(CountMeshes(grid) > 0, "explicit legacy mode did not install a chunk mesh");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_TERRAIN_ISOLATION=FAIL " + exception.Message);
            return false;
        }
    }

    public bool VerifyCoarseSuppressed(RingTerrainCoarseSdfPilot pilot, Node3D lod1)
    {
        try
        {
            Require(CountCoarseMeshes(pilot) == 0, "suppressed coarse pilot installed a mesh");
            Require(lod1.Visible, "suppressed coarse pilot hid the LOD1 proxy");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_TERRAIN_ISOLATION=FAIL " + exception.Message);
            return false;
        }
    }

    public bool VerifyCoarseBuildQueued(RingTerrainCoarseSdfPilot pilot)
    {
        try
        {
            Require(GetCount(pilot, "_tilesBuilding") > 0, "legacy coarse pilot did not queue a tile build");
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_TERRAIN_ISOLATION=FAIL " + exception.Message);
            return false;
        }
    }

    public bool IsCoarseIdle(RingTerrainCoarseSdfPilot pilot)
    {
        object queue = GetField(pilot, "_completedTiles");
        bool queueEmpty = (bool)queue.GetType().GetProperty("IsEmpty")!.GetValue(queue)!;
        return GetCount(pilot, "_tilesBuilding") == 0 && queueEmpty;
    }

    // A cleared building-set is not evidence that a worker has finished.
    // Inject completion records deterministically to test that exact race,
    // including a later re-enable with the SAME desired tile coordinate.
    public bool VerifyLateCompletionIsolation(RingTerrainCoarseSdfPilot pilot, VoxelChunkGridManager grid)
    {
        try
        {
            var origin = new RingTerrainSampleCoordinate(0, 0, 0);
            var desired = (HashSet<RingTerrainSampleCoordinate>)GetField(pilot, "_desiredTiles");
            var building = (Dictionary<RingTerrainSampleCoordinate, int>)GetField(pilot, "_tilesBuilding");
            var vertices = new[] { Vector3.Zero, Vector3.Right, Vector3.Up };
            var build = new MeshBuildData(vertices, new[] { Vector3.Back, Vector3.Back, Vector3.Back },
                new[] { Colors.White, Colors.White, Colors.White }, new[] { 0, 1, 2 }, Array.Empty<Vector3>());
            int oldGeneration = (int)GetField(pilot, "_presentationGeneration");
            desired.Add(origin);
            Invoke(pilot, "ClearTiles");
            int currentGeneration = (int)GetField(pilot, "_presentationGeneration");
            Require(currentGeneration != oldGeneration, "suppression did not advance generation");
            desired.Add(origin);
            grid.DormantEditableBackend = false;
            building[origin] = currentGeneration;
            Enqueue(oldGeneration);
            Invoke(pilot, "InstallCompletedTiles");
            Require(CountCoarseMeshes(pilot) == 0 && building.ContainsKey(origin),
                "late old result installed or stole the replacement worker slot");
            grid.DormantEditableBackend = true;
            Enqueue(currentGeneration);
            Invoke(pilot, "InstallCompletedTiles");
            Require(CountCoarseMeshes(pilot) == 0, "dormant pilot installed a current-generation result");
            grid.DormantEditableBackend = false;
            Enqueue(currentGeneration);
            Invoke(pilot, "InstallCompletedTiles");
            Require(CountCoarseMeshes(pilot) == 1, "active legacy pilot rejected a valid current result");
            grid.DormantEditableBackend = true;
            Invoke(pilot, "UpdatePresentation", false);
            return true;

            void Enqueue(int generation)
            {
                Type resultType = typeof(RingTerrainCoarseSdfPilot).GetNestedType(
                    "CoarseTileBuildResult", BindingFlags.NonPublic)!;
                object result = Activator.CreateInstance(resultType, origin, build, true, generation)!;
                object queue = GetField(pilot, "_completedTiles");
                queue.GetType().GetMethod("Enqueue")!.Invoke(queue, new[] { result });
            }
        }
        catch (Exception exception)
        {
            GD.PushError("LEGACY_TERRAIN_ISOLATION=FAIL " + exception);
            return false;
        }
    }

    private static void Invoke(object instance, string method, params object[] arguments) =>
        instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static int CountCoarseMeshes(Node node)
    {
        int count = 0;
        foreach (Node child in node.GetChildren())
            if (child is MeshInstance3D) count++;
        return count;
    }

    private static int CountMeshes(Node node)
    {
        int count = 0;
        foreach (Node child in node.FindChildren("*", "MeshInstance3D", true, false))
            if (child is MeshInstance3D { Mesh: not null }) count++;
        return count;
    }

    private static int GetCount(object instance, string fieldName) =>
        (int)GetField(instance, fieldName).GetType().GetProperty("Count")!.GetValue(GetField(instance, fieldName))!;

    private static object GetField(object instance, string fieldName) =>
        instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
