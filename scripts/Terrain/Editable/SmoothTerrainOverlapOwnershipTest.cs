using System;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Editable;

/// <summary>Disposable lifecycle regression; no world or journal is saved.</summary>
public partial class SmoothTerrainOverlapOwnershipTest : Node
{
    public override async void _Ready()
    {
        if (!Array.Exists(OS.GetCmdlineUserArgs(), a => a == "--smooth-terrain-overlap-ownership-test"))
        {
            GetTree().Quit(1);
            return;
        }

        SmoothTerrainGrid? grid = null;
        VoxelChunkGridManager? source = null;
        int exitCode = 0;
        try
        {
            source = new VoxelChunkGridManager
            {
                Name = "Source", BuildOnReady = false, DormantEditableBackend = true,
                EnableChunkStreaming = false, UseDeterministicTerrainGeneration = true,
                TerrainSettings = new VoxelTerrainSettings { Seed = 7, EnableUnified3dDensityTerrain = false },
                UseRingCurvedPlacement = true, UseHabitableRingAxisLayout = true,
                AroundRingChunkLength = 1280, RingRadiusMeters = (float)(40960 / Math.Tau),
                RingAcrossWidthMeters = 512, EnableCanonicalFloatingOriginAnchor = true
            };
            AddChild(source);
            using RingTerrainSampler sampler = source.CreateIndependentTerrainSampler()
                ?? throw new InvalidOperationException("source sampler missing");
            int ground = sampler.GetSurfaceHeight(135, 135);
            grid = new SmoothTerrainGrid
            {
                SourceGridPath = new NodePath("../Source"),
                RadiusAround = 0, RadiusAcross = 0, RadiusVertical = 0, CollisionRadius = 0,
                EnableMediumVolumetricVisualCoverage = true, MediumVisualRadiusAround = 1,
                MediumVisualRadiusAcross = 1, MediumVisualRadiusVertical = 1,
                EnableHotChunkPatches = false, MaxWorkers = 1
            };
            AddChild(grid);
            grid.SetCanonicalFocus(new RingCanonicalPosition(135, ground + 101, 135));
            await WaitForReady(grid);

            // Pick an actual surface-bearing medium child in the neighbouring
            // 32m column, within the SAME 64m medium streaming window.
            int nextGround = sampler.GetSurfaceHeight(167, 135);
            SmoothTerrainChunkCoordinate nextChild = default;
            bool found = false;
            int surfaceChunk = RingCoordMath.FloorDiv(nextGround, 32);
            for (int y = surfaceChunk - 1; y <= surfaceChunk + 1; y++)
            {
                var candidate = new SmoothTerrainChunkCoordinate(5, y, 4);
                if (!grid.IsMediumChildVisibleForTests(Parent(candidate), candidate)) continue;
                nextChild = candidate;
                found = true;
                break;
            }
            Require(found, "fixture did not find an uncovered medium surface");
            Require(!grid.IsFineChildReadyVisualOwnerForTests(nextChild), "fixture child is already fine-owned");
            grid.PauseWorkersForTests();
            grid.SetCanonicalFocus(new RingCanonicalPosition(167, nextGround + 101, 135));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            Require(!grid.IsFineChildReadyVisualOwnerForTests(nextChild)
                && grid.IsMediumChildVisibleForTests(Parent(nextChild), nextChild),
                "pending fine build removed its medium fallback");
            grid.ReleaseWorkersForTests();
            await WaitForReady(grid);
            AssertExclusiveFine(grid, nextChild, "completed fine build");

            grid.EnableHotChunkPatches = true;
            grid.EnableFocusCenteredVisualCoverage = true;
            grid.SetCanonicalFocus(new RingCanonicalPosition(167, nextGround + 2, 135));
            grid.RefreshDesired();
            await WaitForReady(grid);
            AssertExclusiveFine(grid, nextChild, "hot-patch promotion");
            Require(grid.CaptureHotPatchMasksForTests().Count > 0, "hot-patch fixture was not activated");

            grid.PauseWorkersForTests();
            grid.AddEdit(new SmoothTerrainEditCommand(1, 99101, 99101, false,
                167, nextGround, 135, 1, (int)SmoothTerrainMaterial.Rock));
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
            AssertExclusiveFine(grid, nextChild, "pending edit with retained fine mesh");
            grid.ReleaseWorkersForTests();
            await WaitForReady(grid);
            AssertExclusiveFine(grid, nextChild, "completed edit");
            ulong meshHash = grid.CaptureDiagnostics()[nextChild].LastInstalledMeshHash;

            source.SetFloatingOriginCanonicalAnchor(new RingCanonicalPosition(183, nextGround, 151));
            await WaitForReady(grid);
            AssertExclusiveFine(grid, nextChild, "floating-origin rebase");
            Require(grid.CaptureDiagnostics()[nextChild].LastInstalledMeshHash == meshHash,
                "translation-only rebase changed canonical mesh identity");
            GD.Print("SMOOTH_TERRAIN_OVERLAP_OWNERSHIP_TEST_PASS pending/fine/hot/edit/rebase");
        }
        catch (Exception exception)
        {
            exitCode = 1;
            GD.PushError("SMOOTH_TERRAIN_OVERLAP_OWNERSHIP_TEST_FAIL " + exception);
        }
        finally
        {
            if (grid != null)
            {
                grid.ReleaseWorkersForTests();
                Task stopped = grid.AwaitShutdownForTestsAsync();
                grid.QueueFree();
                while (!stopped.IsCompleted) await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
                await stopped;
            }
            source?.QueueFree();
        }
        GetTree().Quit(exitCode);
    }

    private static SmoothTerrainChunkCoordinate Parent(SmoothTerrainChunkCoordinate child) => new(
        child.AroundChunk / 2 * 2,
        RingCoordMath.FloorDiv(child.VerticalChunk, 2) * 2,
        RingCoordMath.FloorDiv(child.AcrossChunk, 2) * 2);

    private static void AssertExclusiveFine(SmoothTerrainGrid grid, SmoothTerrainChunkCoordinate child, string label)
    {
        Require(grid.IsFineChildReadyVisualOwnerForTests(child)
            && !grid.IsMediumChildVisibleForTests(Parent(child), child)
            && grid.CaveLodVisibleOwnershipOverlapCountForTests == 0,
            label + " did not exclusively own its terrain region");
    }

    private async Task WaitForReady(SmoothTerrainGrid grid)
    {
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        ulong deadline = Time.GetTicksMsec() + 120000;
        while (!grid.IsQuiescent && Time.GetTicksMsec() < deadline)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        Require(grid.IsQuiescent, "terrain did not settle before timeout");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
