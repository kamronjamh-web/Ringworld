using System;
using System.Linq;
using Godot;
using RingworldTechDemo.Terrain.Surface;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo;

/// <summary>Exercises the real grid's immutable Terrain3D surface-to-LOD bridge.</summary>
public partial class RingSurfaceLodBridgeTest : RefCounted
{
    public bool Run()
    {
        Node3D? fixture = null;
        VoxelChunkGridManager? grid = null;
        RingTerrainSampler? sampler = null;
        RingSurfaceHeightField? field = null;
        try
        {
            SceneTree tree = (Engine.GetMainLoop() as SceneTree) ?? throw new InvalidOperationException("scene tree unavailable");
            fixture = new Node3D { Name = "RingSurfaceLodBridgeFixture" };
            tree.Root.AddChild(fixture);
            grid = new VoxelChunkGridManager
            {
                BuildOnReady = false,
                DormantEditableBackend = true,
                EnableChunkStreaming = false,
                UseDeterministicTerrainGeneration = true,
                TerrainSettings = new VoxelTerrainSettings()
            };
            fixture.AddChild(grid);
            sampler = grid.CreateIndependentTerrainSampler() ?? throw new InvalidOperationException("grid sampler unavailable");
            long circumference = checked((long)Math.Round(grid.GetCanonicalRingCircumferenceMeters64()));
            int width = Mathf.RoundToInt(grid.RingAcrossWidthMeters);
            int across = Math.Min(width - 4, 100);
            field = new RingSurfaceHeightField(sampler.TerrainDefinitionHash, circumference, width, -100_000, 100_000,
                (around, columnAcross) => sampler.GetSurfaceHeight(around, columnAcross) + .5f);

            ulong lodBeforeAttach = grid.LodSurfaceEditVersion;
            ulong rainBeforeAttach = grid.RainSurfaceEditVersion;
            grid.AttachSurfaceHeightField(field);
            Require(grid.LodSurfaceEditVersion > lodBeforeAttach && grid.RainSurfaceEditVersion > rainBeforeAttach, "attach did not advance grid surface versions");
            ulong lodAfterAttach = grid.LodSurfaceEditVersion;
            ulong rainAfterAttach = grid.RainSurfaceEditVersion;

            Require(field.ApplyBrush(0, across, 3, 4), "seam brush was rejected");
            ulong lodBeforeBrush = grid.LodSurfaceEditVersion;
            ulong rainBeforeBrush = grid.RainSurfaceEditVersion;
            Require(lodBeforeBrush > lodAfterAttach && rainBeforeBrush > rainAfterAttach, "first brush revision did not advance grid versions");
            RingLodSurfaceEditSnapshot first = grid.CaptureLodSurfaceEditSnapshot();
            long seamAdjacent = circumference - 1;
            int expectedSeamHeight = checked((int)Math.Round(field.SampleHeight(seamAdjacent, across) - .5f));
            Require(first.TryGetEditedSurfaceHeight(seamAdjacent, across, out int seamHeight) && seamHeight == expectedSeamHeight,
                "2m seam edit did not project to wrapped adjacent 1m LOD column");
            Require(first.TryGetEditedSurfaceHeight(1, across, out _), "2m edit did not project to adjacent positive 1m LOD column");
            int immutableCenter = first.GetSurfaceHeight(sampler, 0L, across);

            Require(field.ApplyBrush(0, across, 3, 2), "second brush was rejected");
            RingLodSurfaceEditSnapshot second = grid.CaptureLodSurfaceEditSnapshot();
            Require(grid.LodSurfaceEditVersion > lodBeforeBrush && grid.RainSurfaceEditVersion > rainBeforeBrush, "second brush revision did not advance grid versions");
            Require(!ReferenceEquals(first, second) && first.GetSurfaceHeight(sampler, 0L, across) == immutableCenter,
                "captured LOD snapshot changed after mutable surface field edit");

            field.Clear();
            RingLodSurfaceEditSnapshot cleared = grid.CaptureLodSurfaceEditSnapshot();
            var removed = new RingTerrainColumnCoordinate(0, across);
            Require(cleared.GetSurfaceHeight(sampler, 0L, across) == sampler.GetSurfaceHeight(0L, across),
                "clear did not return the LOD surface to baseline");
            Require(cleared.CanonicalDirtyColumns.Contains(removed), "clear did not retain removed column in dirty set");
            grid.DetachSurfaceHeightField(field);
            GD.Print("RING_SURFACE_LOD_BRIDGE=PASS");
            return true;
        }
        catch (Exception exception) { GD.PushError("RING_SURFACE_LOD_BRIDGE=FAIL " + exception.Message); return false; }
        finally
        {
            if (grid != null && field != null) grid.DetachSurfaceHeightField(field);
            sampler?.Dispose();
            fixture?.Free();
        }
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
