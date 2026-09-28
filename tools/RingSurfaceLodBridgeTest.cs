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
                (around, columnAcross) => sampler.GetPresentationSurfaceHeight(around, columnAcross));

            ulong lodBeforeAttach = grid.LodSurfaceEditVersion;
            ulong rainBeforeAttach = grid.RainSurfaceEditVersion;
            grid.AttachSurfaceHeightField(field);
            Require(grid.LodSurfaceEditVersion > lodBeforeAttach && grid.RainSurfaceEditVersion > rainBeforeAttach, "attach did not advance grid surface versions");
            ulong lodAfterAttach = grid.LodSurfaceEditVersion;
            ulong rainAfterAttach = grid.RainSurfaceEditVersion;
            RingLodSurfaceEditSnapshot noEdits = grid.CaptureLodSurfaceEditSnapshot();
            Require(noEdits.UsesFloatSurface && noEdits.CanonicalFloatColumns.Count == 0,
                "field-backed snapshot did not preserve the float-surface mode without edits");
            RequireNearly(noEdits.GetPresentationSurfaceHeight(sampler, 0L, across), sampler.GetPresentationSurfaceHeight(0L, across),
                "float presentation baseline changed without edits");

            Require(field.ApplyBrush(0, across, 3, .25f), "fractional seam brush was rejected");
            ulong lodBeforeBrush = grid.LodSurfaceEditVersion;
            ulong rainBeforeBrush = grid.RainSurfaceEditVersion;
            Require(lodBeforeBrush > lodAfterAttach && rainBeforeBrush > rainAfterAttach, "first brush revision did not advance grid versions");
            RingLodSurfaceEditSnapshot first = grid.CaptureLodSurfaceEditSnapshot();
            long seamAdjacent = circumference - 1;
            int expectedSeamHeight = checked((int)Math.Round(field.SampleHeight(seamAdjacent, across) - sampler.SurfaceHeightOffset));
            bool hasLegacySeam = first.TryGetEditedSurfaceHeight(seamAdjacent, across, out int legacySeamHeight);
            bool legacyProjectionChanged = expectedSeamHeight != sampler.GetSurfaceHeight(seamAdjacent, across);
            Require(hasLegacySeam == legacyProjectionChanged && (!hasLegacySeam || legacySeamHeight == expectedSeamHeight),
                "fractional edit no longer follows the established legacy integer projection");
            Require(first.TryGetEditedSurfaceHeightFloat(seamAdjacent, across, out float floatSeamHeight),
                "fractional seam edit was omitted from the float snapshot");
            RequireNearly(floatSeamHeight, field.SampleHeight(seamAdjacent, across), "float seam height lost precision");
            Require(first.CanonicalFloatColumns.ContainsKey(new RingTerrainColumnCoordinate(1, across)),
                "fractional edit did not project to adjacent positive float column");
            int immutableCenter = first.GetSurfaceHeight(sampler, 0L, across);
            float immutableFloatCenter = first.GetPresentationSurfaceHeight(sampler, 0L, across);

            Require(field.ApplyBrush(0, across, 3, 2), "second brush was rejected");
            RingLodSurfaceEditSnapshot second = grid.CaptureLodSurfaceEditSnapshot();
            Require(grid.LodSurfaceEditVersion > lodBeforeBrush && grid.RainSurfaceEditVersion > rainBeforeBrush, "second brush revision did not advance grid versions");
            Require(!ReferenceEquals(first, second) && first.GetSurfaceHeight(sampler, 0L, across) == immutableCenter,
                "captured LOD snapshot changed after mutable surface field edit");
            RequireNearly(first.GetPresentationSurfaceHeight(sampler, 0L, across), immutableFloatCenter,
                "captured float LOD snapshot changed after mutable surface field edit");

            var legacyColumns = new System.Collections.Generic.Dictionary<RingTerrainColumnCoordinate, int>
            {
                [new RingTerrainColumnCoordinate(1, across)] = sampler.GetSurfaceHeight(1, across) + 2,
                [new RingTerrainColumnCoordinate(2, across)] = sampler.GetSurfaceHeight(2, across) + 5
            };
            RingLodSurfaceEditSnapshot legacySnapshot = new(99, circumference, legacyColumns);
            RingLodSurfaceEditSnapshot legacyCoarse = legacySnapshot.ForSampleSpacing(sampler, 4, width - 1);
            Require(!legacyCoarse.UsesFloatSurface && legacyCoarse.CanonicalFloatColumns.Count == 0,
                "legacy integer projection unexpectedly acquired float surface state");
            Require(legacyCoarse.TryGetEditedSurfaceHeight(0, across, out int coarseLegacy),
                "legacy coarse projection lost its aligned column");
            Require(coarseLegacy == sampler.GetSurfaceHeight(0, across) + 5,
                "legacy coarse strongest integer delta changed");

            var floatColumns = new System.Collections.Generic.Dictionary<RingTerrainColumnCoordinate, float>
            {
                [new RingTerrainColumnCoordinate(1, across)] = sampler.GetPresentationSurfaceHeight(1, across) + .25f,
                [new RingTerrainColumnCoordinate(2, across)] = sampler.GetPresentationSurfaceHeight(2, across) + .75f
            };
            RingLodSurfaceEditSnapshot floatSnapshot = new(100, circumference,
                new System.Collections.Generic.Dictionary<RingTerrainColumnCoordinate, int>(), floatColumns, true);
            RingLodSurfaceEditSnapshot floatCoarse = floatSnapshot.ForSampleSpacing(sampler, 4, width - 1);
            Require(floatCoarse.UsesFloatSurface, "float coarse projection lost float mode");
            Require(floatCoarse.TryGetEditedSurfaceHeightFloat(0, across, out float coarseFloat),
                "float coarse projection lost its aligned column");
            RequireNearly(coarseFloat, sampler.GetPresentationSurfaceHeight(0, across) + .75f,
                "float coarse strongest delta lost fractional precision");

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
    private static void RequireNearly(float actual, float expected, string message)
    {
        if (!Mathf.IsEqualApprox(actual, expected))
            throw new InvalidOperationException($"{message}: expected {expected}, got {actual}");
    }
}
