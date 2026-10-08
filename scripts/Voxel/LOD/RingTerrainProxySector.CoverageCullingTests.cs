using System;
using System.Collections.Generic;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingTerrainProxySector
{
    /// <summary>
    /// Headless regression fixture for the optional CPU-side partial coverage mesh path.
    /// It deliberately builds a regular 64x64 grid plus non-grid skirt triangles so the
    /// filter can prove it removes only completely covered terrain quads.
    /// </summary>
    public Godot.Collections.Dictionary RunPartialCoverageMeshCullingContractTest()
    {
        var result = new Godot.Collections.Dictionary();
        const int cells = 64;
        const int gridTriangleCount = cells * cells * 2;
        const int expectedQuarterRemoved = gridTriangleCount / 4;
        long retainedBefore = Interlocked.Read(ref _partialCoverageRetainedBytesAllLevels);
        var coordinate = new RingTerrainSectorCoordinate(91_337, 17);
        MeshInstance3D? node = null;
        Mesh? fullMesh = null;
        try
        {
            PartialCoverageMinimumRemovedTriangles = 1;
            PartialCoverageSourceMemoryMiB = 64;
            SectorSizeMeters = 128;
            SampleSpacingMeters = 2;
            CastTerrainShadows = true;
            ShadowIgnoresTerrainFade = true;
            // Exercise lazy production material creation, including geomorph inputs.
            FadeOutStartMeters = 240;
            FadeOutEndMeters = 320;
            EnableGeomorphing = true;
            UseBlockyHeightfield = false;
            SetPartialCoverageMeshCulling(false);

            (Vector3[] vertices, Vector3[] normals, Color[] colors, Vector2[] uvs,
                Vector3[] chords, int[] terrainIndices, int[] oceanIndices) = BuildPartialCoverageFixture(cells);
            var temporarySource = new PartialCoverageMeshSource
            {
                Vertices = vertices, Normals = normals, Colors = colors, Uvs = uvs,
                MorphData = new Vector2[vertices.Length], ParentChordOffsets = chords,
                TerrainIndices = terrainIndices, OceanIndices = oceanIndices,
                UsesRuntimeParentChord = false, Cells = cells, FullMesh = new ArrayMesh()
            };
            fullMesh = CreatePartialCoverageMesh(temporarySource, terrainIndices);
            temporarySource.FullMesh.Dispose();
            var data = new SectorData(coordinate, 0, 0, Vector3.Zero, vertices, normals, colors, uvs,
                new Vector2[vertices.Length], chords, false, terrainIndices, oceanIndices,
                null, null, false, null);
            node = new MeshInstance3D { Mesh = fullMesh,
                MaterialOverride = new ShaderMaterial { Shader = GD.Load<Shader>("res://materials/ring_terrain_proxy.gdshader") } };
            AddChild(node);
            var atlasRect = new Vector4(.125f, .25f, .0625f, .0625f);
            node.SetInstanceShaderParameter("coverage_atlas_rect", atlasRect);
            _active[coordinate] = node;
            RetainPartialCoverageSource(coordinate, data, fullMesh);
            if (!_partialCoverageSources.TryGetValue(coordinate, out PartialCoverageMeshSource? source))
                throw new InvalidOperationException("Disabled-at-admission sector did not retain a reusable source.");
            SetPartialCoverageMeshCulling(true);

            var quarterMask = CreateCoverageMask(cells, coveredWidth: cells / 2, coveredHeight: cells / 2);
            _activeCoverageMasks[coordinate] = quarterMask;
            QueuePartialCoverageCulling(coordinate, node, quarterMask);
            ServicePartialCoverageMeshCulling();
            if (node.Mesh is not ArrayMesh filteredMesh || ReferenceEquals(filteredMesh, fullMesh))
                throw new InvalidOperationException("Partial mask did not install a filtered terrain mesh.");
            int filteredTerrainTriangles = GetSurfaceTriangleCount(filteredMesh, 0);
            int filteredOceanIndices = GetSurfaceIndexCount(filteredMesh, 1);
            if (filteredTerrainTriangles != gridTriangleCount + 2 - expectedQuarterRemoved)
                throw new InvalidOperationException("Covered grid triangles or preserved non-grid triangles were counted incorrectly.");
            if (filteredOceanIndices != oceanIndices.Length)
                throw new InvalidOperationException("Partial terrain filtering modified the ocean index surface.");
            MeshInstance3D? shadowCaster = node.GetNodeOrNull<MeshInstance3D>("OpaqueShadowCaster");
            if (shadowCaster == null || !ReferenceEquals(shadowCaster.Mesh, fullMesh))
                throw new InvalidOperationException("Opaque shadow caster did not retain the original full mesh.");
            SetTerrainShadowCasting(true);
            if (node.CastShadow != ShadowCastingSetting.Off
                || shadowCaster.CastShadow != ShadowCastingSetting.ShadowsOnly
                || !shadowCaster.Visible
                || shadowCaster.GetInstanceShaderParameter("coverage_atlas_rect").AsVector4() != atlasRect)
                throw new InvalidOperationException("Persistent terrain caster did not preserve atlas ownership independently of visual casting.");
            if (_opaqueShadowMaterial?.Shader?.ResourcePath != "res://materials/ring_terrain_proxy_shadow.gdshader"
                || !_opaqueShadowMaterial.GetShaderParameter("geomorph_enabled").AsBool()
                || _opaqueShadowMaterial.GetShaderParameter("morph_start_meters").AsSingle() != FadeOutStartMeters
                || _opaqueShadowMaterial.GetShaderParameter("morph_end_meters").AsSingle() != FadeOutEndMeters)
                throw new InvalidOperationException("Persistent terrain caster lost production geomorph configuration.");
            SetTerrainShadowCasting(false);
            if (shadowCaster.CastShadow != ShadowCastingSetting.Off || shadowCaster.Visible)
                throw new InvalidOperationException("Disabling terrain shadows left the persistent caster active.");
            SetTerrainShadowCasting(true);

            // A second candidate in the same process frame must remain queued: one mesh
            // rebuild per frame is the culling path's scene-thread budget.
            QueuePartialCoverageCulling(coordinate, node, quarterMask);
            ServicePartialCoverageMeshCulling();
            int pendingAfterBudget = PartialCoveragePendingRebuildCount;
            if (pendingAfterBudget != 1)
                throw new InvalidOperationException("Partial coverage culling exceeded its one-rebuild-per-frame budget.");
            _partialCoverageBudgetFrame = ulong.MaxValue;
            ServicePartialCoverageMeshCulling();

            RestoreFullPartialCoverageMesh(coordinate, node);
            if (!ReferenceEquals(node.Mesh, fullMesh) || PartialCoverageActiveRemovedTriangleCount != 0)
                throw new InvalidOperationException("Restoring full coverage did not restore the original mesh accounting.");
            SetPartialCoverageMeshCulling(false);
            if (!ReferenceEquals(node.Mesh, fullMesh) || PartialCoveragePendingRebuildCount != 0)
                throw new InvalidOperationException("Disabling partial coverage culling did not restore the full mesh.");
            SetPartialCoverageMeshCulling(true);

            var fullMask = CreateCoverageMask(cells, cells, cells);
            _activeCoverageMasks[coordinate] = fullMask;
            QueuePartialCoverageCulling(coordinate, node, fullMask);
            _partialCoverageBudgetFrame = ulong.MaxValue;
            ServicePartialCoverageMeshCulling();
            if (!ReferenceEquals(node.Mesh, fullMesh) || PartialCoveragePendingRebuildCount != 0)
                throw new InvalidOperationException("Fully covered sector should not create a partial filtered mesh.");
            var uncoveredMask = CreateCoverageMask(cells, 0, 0);
            _activeCoverageMasks[coordinate] = uncoveredMask;
            QueuePartialCoverageCulling(coordinate, node, uncoveredMask);
            _partialCoverageBudgetFrame = ulong.MaxValue;
            ServicePartialCoverageMeshCulling();
            if (!ReferenceEquals(node.Mesh, fullMesh) || PartialCoveragePendingRebuildCount != 0)
                throw new InvalidOperationException("Uncovered sector should not create a partial filtered mesh.");

            _activeCoverageMasks[coordinate] = quarterMask;
            _warmUntil[coordinate] = 0.0;
            QueuePartialCoverageCulling(coordinate, node, quarterMask);
            if (PartialCoveragePendingRebuildCount != 0)
                throw new InvalidOperationException("Warm sectors must not queue partial mesh work.");
            _warmUntil.Remove(coordinate);
            RecycleActiveSector(coordinate);
            if (PartialCoverageSourceBytes != 0 || PartialCoverageFilteredMeshBytes != 0
                || PartialCoverageActiveRemovedTriangleCount != 0)
                throw new InvalidOperationException("Recycling a partial coverage sector left local accounting behind.");
            if (Interlocked.Read(ref _partialCoverageRetainedBytesAllLevels) != retainedBefore)
                throw new InvalidOperationException("Recycling a partial coverage sector leaked shared retained-byte accounting.");

            result["passed"] = true;
            result["grid_triangles"] = gridTriangleCount;
            result["expected_removed_triangles"] = expectedQuarterRemoved;
            result["removed_triangles"] = expectedQuarterRemoved;
            result["filtered_terrain_triangles"] = filteredTerrainTriangles;
            result["preserved_non_grid_triangles"] = 2;
            result["ocean_index_count"] = filteredOceanIndices;
            result["pending_after_frame_budget"] = pendingAfterBudget;
            result["source_bytes_after_recycle"] = PartialCoverageSourceBytes;
            result["shared_retained_bytes_after_recycle"] = Interlocked.Read(ref _partialCoverageRetainedBytesAllLevels);
            return result;
        }
        catch (Exception exception)
        {
            GD.PushError($"Partial coverage mesh culling contract failed: {exception.Message}");
            result["passed"] = false;
            result["error"] = exception.Message;
            return result;
        }
        finally
        {
            _partialCoveragePending.Clear();
            _partialCoveragePendingSet.Clear();
            if (_active.ContainsKey(coordinate)) RecycleActiveSector(coordinate);
            if (node != null && node.GetParent() == this) RemoveChild(node);
            node?.QueueFree();
            fullMesh?.Dispose();
            _opaqueShadowMaterial?.Dispose();
            _opaqueShadowMaterial = null;
            _partialCoverageBudgetFrame = ulong.MaxValue;
        }
    }

    private static SurfaceCoverageMask CreateCoverageMask(int cells, int coveredWidth, int coveredHeight)
    {
        var covered = new bool[cells * cells];
        for (int z = 0; z < coveredHeight; z++)
        for (int x = 0; x < coveredWidth; x++) covered[z * cells + x] = true;
        return new SurfaceCoverageMask(cells, 1, covered);
    }

    private static (Vector3[] Vertices, Vector3[] Normals, Color[] Colors, Vector2[] Uvs,
        Vector3[] Chords, int[] TerrainIndices, int[] OceanIndices) BuildPartialCoverageFixture(int cells)
    {
        int gridVertices = (cells + 1) * (cells + 1);
        const int extraVertices = 6;
        var vertices = new Vector3[gridVertices + extraVertices];
        var normals = new Vector3[vertices.Length];
        var colors = new Color[vertices.Length];
        var uvs = new Vector2[vertices.Length];
        var chords = new Vector3[vertices.Length];
        for (int z = 0; z <= cells; z++)
        for (int x = 0; x <= cells; x++)
        {
            int index = z * (cells + 1) + x;
            vertices[index] = new Vector3(x, 0, z);
            normals[index] = Vector3.Up;
            colors[index] = Colors.White;
            uvs[index] = new Vector2(x / (float)cells, z / (float)cells);
        }
        int skirt = gridVertices;
        vertices[skirt] = new Vector3(-1, -1, 0); vertices[skirt + 1] = new Vector3(0, -1, 0); vertices[skirt + 2] = new Vector3(0, -1, 1);
        uvs[skirt] = new Vector2(-1, 0); uvs[skirt + 1] = new Vector2(0, 0); uvs[skirt + 2] = new Vector2(0, 1);
        int nonGrid = skirt + 3;
        vertices[nonGrid] = new Vector3(1, 1, 1); vertices[nonGrid + 1] = new Vector3(2, 1, 1); vertices[nonGrid + 2] = new Vector3(1, 1, 2);
        uvs[nonGrid] = new Vector2(0.25f, 0.25f); uvs[nonGrid + 1] = new Vector2(0.5f, 0.25f); uvs[nonGrid + 2] = new Vector2(0.25f, 0.5f);
        for (int index = gridVertices; index < vertices.Length; index++) { normals[index] = Vector3.Up; colors[index] = Colors.White; }
        var terrain = new List<int>(cells * cells * 6 + 6);
        for (int z = 0; z < cells; z++)
        for (int x = 0; x < cells; x++)
        {
            int a = z * (cells + 1) + x, b = a + 1, c = a + cells + 1, d = c + 1;
            terrain.Add(a); terrain.Add(c); terrain.Add(b);
            terrain.Add(b); terrain.Add(c); terrain.Add(d);
        }
        terrain.Add(skirt); terrain.Add(skirt + 1); terrain.Add(skirt + 2);
        terrain.Add(nonGrid); terrain.Add(nonGrid + 1); terrain.Add(nonGrid + 2);
        return (vertices, normals, colors, uvs, chords, terrain.ToArray(), new[] { 0, 1, cells + 1 });
    }

    private static int GetSurfaceIndexCount(ArrayMesh mesh, int surface)
    {
        Godot.Collections.Array arrays = mesh.SurfaceGetArrays(surface);
        return ((int[])arrays[(int)Mesh.ArrayType.Index]).Length;
    }

    private static int GetSurfaceTriangleCount(ArrayMesh mesh, int surface) => GetSurfaceIndexCount(mesh, surface) / 3;
}
