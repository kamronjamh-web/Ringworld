using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Phase1;

/// <summary>
/// Opt-in correctness and allocation/throughput diagnostics for the managed
/// voxel mesher. Run through --voxel-mesher-self-test or
/// --voxel-mesher-benchmark; this never runs during normal gameplay.
/// </summary>
public static class VoxelChunkMesherDiagnostics
{
    private const int DefaultBenchmarkIterations = 64;
    private const ulong ExpectedRuggedMeshHash = 0xE8ADBDA7F6F2F0F0UL;

    public static bool RunSelfTest(out string summary)
    {
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(null);

        var empty = new VoxelChunkData();
        MeshBuildData emptyMesh = VoxelChunkMesher.Build(empty, palette);
        if (emptyMesh.HasGeometry || emptyMesh.Indices.Length != 0)
        {
            summary = "empty chunk emitted geometry";
            return false;
        }

        var solid = new VoxelChunkData();
        solid.Fill(BlockType.Rock);
        MeshBuildData solidMesh = VoxelChunkMesher.Build(solid, palette);
        if (solidMesh.Vertices.Length != 24 || solidMesh.Indices.Length != 36)
        {
            summary = $"solid greedy shell expected 24/36 vertices/indices, got {solidMesh.Vertices.Length}/{solidMesh.Indices.Length}";
            return false;
        }

        MeshBuildData collisionMesh = VoxelChunkMesher.Build(
            solid,
            palette,
            buildCollisionFaces: true);
        if (collisionMesh.CollisionFaces.Length != collisionMesh.Indices.Length)
        {
            summary = "collision expansion did not match the render index count";
            return false;
        }

        VoxelChunkData rugged = CreateRuggedChunk();
        MeshBuildData first = VoxelChunkMesher.Build(rugged, palette);
        MeshBuildData second = VoxelChunkMesher.Build(rugged, palette);
        if (!ValidateMesh(first, out summary))
        {
            return false;
        }
        if (!MeshesMatch(first, second))
        {
            summary = "repeat builds produced different mesh arrays";
            return false;
        }

        ulong ruggedHash = ComputeMeshHash(first);
        if (ruggedHash != ExpectedRuggedMeshHash)
        {
            summary = $"rugged geometry hash changed: expected 0x{ExpectedRuggedMeshHash:X16}, got 0x{ruggedHash:X16}";
            return false;
        }

        MeshBuildData managedReference = VoxelChunkMesher.BuildManaged(rugged, palette);
        if (!MeshesMatch(first, managedReference))
        {
            summary = $"active {VoxelChunkMesherNative.Status} output differs from the managed reference";
            return false;
        }

        Func<int, int, int, BlockType> solidNeighbor = static (_, _, _) => BlockType.Rock;
        MeshBuildData enclosed = VoxelChunkMesher.Build(
            solid,
            palette,
            externalBlockSampler: solidNeighbor);
        MeshBuildData enclosedManaged = VoxelChunkMesher.BuildManaged(
            solid,
            palette,
            externalBlockSampler: solidNeighbor);
        if (enclosed.HasGeometry || !MeshesMatch(enclosed, enclosedManaged))
        {
            summary = "solid neighbor boundaries were not culled identically";
            return false;
        }

        MeshBuildData unmerged = VoxelChunkMesher.Build(
            rugged,
            palette,
            useGreedyMerging: false);
        MeshBuildData unmergedManaged = VoxelChunkMesher.BuildManaged(
            rugged,
            palette,
            useGreedyMerging: false);
        if (!ValidateMesh(unmerged, out summary) || !MeshesMatch(unmerged, unmergedManaged))
        {
            summary = "non-greedy native/managed geometry differs";
            return false;
        }

        int concurrentWorkers = Math.Clamp(System.Environment.ProcessorCount, 2, 8);
        var concurrentBuilds = new Task<ulong>[concurrentWorkers];
        for (int worker = 0; worker < concurrentWorkers; worker++)
        {
            concurrentBuilds[worker] = Task.Run(() =>
            {
                ulong lastHash = 0;
                for (int iteration = 0; iteration < 16; iteration++)
                    lastHash = ComputeMeshHash(VoxelChunkMesher.Build(rugged, palette));
                return lastHash;
            });
        }
        try
        {
            Task.WaitAll(concurrentBuilds);
        }
        catch (AggregateException exception)
        {
            summary = $"concurrent meshing failed: {exception.Flatten().InnerException?.Message}";
            return false;
        }
        foreach (Task<ulong> build in concurrentBuilds)
        {
            if (build.Result == ExpectedRuggedMeshHash)
                continue;
            summary = $"concurrent meshing hash changed: 0x{build.Result:X16}";
            return false;
        }

        MeshBuildData warped = VoxelChunkMesher.Build(
            rugged,
            palette,
            localVertexWarp: static vertex => new Vector3(
                vertex.X,
                vertex.Y + vertex.X * vertex.X * 0.0001f,
                vertex.Z));
        if (!ValidateMesh(warped, out summary)
            || warped.Vertices.Length != first.Vertices.Length
            || warped.Indices.Length != first.Indices.Length)
        {
            summary = "vertex warping changed topology or emitted invalid geometry";
            return false;
        }

        MeshBuildData smooth = VoxelChunkMesher.Build(
            rugged,
            palette,
            buildCollisionFaces: true,
            smoothNaturalTerrain: true);
        MeshBuildData smoothRepeat = VoxelChunkMesher.Build(
            rugged,
            palette,
            buildCollisionFaces: true,
            smoothNaturalTerrain: true);
        if (!ValidateTriangleMesh(smooth, out summary)
            || smooth.CollisionFaces.Length != smooth.Indices.Length
            || !MeshesMatch(smooth, smoothRepeat))
        {
            summary = string.IsNullOrEmpty(summary)
                ? "smooth terrain build was nondeterministic or collision expansion differed"
                : summary;
            return false;
        }
        if (!ValidateSmoothChunkBoundaryNormals(palette, out summary))
        {
            return false;
        }

        var construction = new VoxelChunkData();
        construction.SetBlock(16, 16, 16, BlockType.Concrete);
        MeshBuildData hybridConstruction = VoxelChunkMesher.Build(
            construction,
            palette,
            smoothNaturalTerrain: true);
        if (hybridConstruction.Vertices.Length != 24 || hybridConstruction.Indices.Length != 36)
        {
            summary = $"hybrid construction block expected 24/36 vertices/indices, got {hybridConstruction.Vertices.Length}/{hybridConstruction.Indices.Length}";
            return false;
        }

        summary = $"32^3 contracts passed; backend={VoxelChunkMesherNative.Status}, concurrent={concurrentWorkers}x16, rugged={first.Vertices.Length} vertices/{first.Indices.Length / 3} triangles, smooth={smooth.Vertices.Length} vertices/{smooth.Indices.Length / 3} triangles, hash=0x{ruggedHash:X16}";
        return true;
    }

    public static bool RunTransvoxelTransitionSelfTest(out string summary)
    {
        if (!VoxelChunkMesherNative.IsSmoothTransitionAvailable)
        {
            summary = $"native transition backend is unavailable: {VoxelChunkMesherNative.Status}";
            return false;
        }

        int sampleCount = RingTerrainFineSdfHalo.SampleCount;
        int sampleTotal = sampleCount * sampleCount * sampleCount;
        var densities = new float[sampleTotal];
        var materials = new BlockType[sampleTotal];
        int index = 0;
        for (int z = 0; z < sampleCount; z++)
        for (int y = 0; y < sampleCount; y++)
        for (int x = 0; x < sampleCount; x++)
        {
            float density = y - 16.5f;
            densities[index] = density;
            materials[index] = density < 0.0f ? BlockType.Rock : BlockType.Air;
            index++;
        }

        var halo = new RingTerrainFineSdfHalo(Vector3I.Zero, densities, materials);
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(null);
        string fineError = string.Empty;
        if (!VoxelChunkMesherNative.TryBuildSmoothRegularFineSdf(
                halo,
                palette,
                localVertexWarp: null,
                buildCollisionFaces: false,
                out MeshBuildData fineMesh)
            || !ValidateTriangleMesh(fineMesh, out fineError)
            || !ValidateGodotSmoothWinding(fineMesh, out fineError)
            || !fineMesh.HasGeometry)
        {
            summary = string.IsNullOrEmpty(fineError)
                ? $"native fine SDF regular build failed: {VoxelChunkMesherNative.Status}"
                : $"native fine SDF regular mesh is invalid: {fineError}";
            return false;
        }
        int coarseSampleCount = RingTerrainCoarseSdfChunk.SampleCount;
        var coarseDensities = new float[coarseSampleCount * coarseSampleCount * coarseSampleCount];
        var coarseMaterials = new BlockType[coarseDensities.Length];
        index = 0;
        for (int z = 0; z < coarseSampleCount; z++)
        for (int y = 0; y < coarseSampleCount; y++)
        for (int x = 0; x < coarseSampleCount; x++)
        {
            float density = y * 2 - 16.5f;
            coarseDensities[index] = density;
            coarseMaterials[index] = density < 0.0f ? BlockType.Rock : BlockType.Air;
            index++;
        }
        var coarse = new RingTerrainCoarseSdfChunk(
            Vector3I.Zero,
            spacingMeters: 2,
            coarseDensities,
            coarseMaterials);
        string coarseError = string.Empty;
        if (!VoxelChunkMesherNative.TryBuildSmoothRegularSdf(
                coarse,
                palette,
                localVertexWarp: null,
                out MeshBuildData coarseMesh)
            || !ValidateTriangleMesh(coarseMesh, out coarseError)
            || !ValidateGodotSmoothWinding(coarseMesh, out coarseError)
            || !coarseMesh.HasGeometry)
        {
            summary = string.IsNullOrEmpty(coarseError)
                ? $"native coarse SDF regular build failed: {VoxelChunkMesherNative.Status}"
                : $"native coarse SDF regular mesh is invalid: {coarseError}";
            return false;
        }
        int populatedFaces = 0;
        int triangleCount = 0;
        for (int face = 0; face < 6; face++)
        {
            if (!VoxelChunkMesherNative.TryBuildSmoothTransition(
                    halo,
                    coarse,
                    face,
                    palette,
                    localVertexWarp: null,
                    out MeshBuildData mesh))
            {
                summary = $"native transition face {face} failed: {VoxelChunkMesherNative.Status}";
                return false;
            }
            if (!mesh.HasGeometry) continue;
            if (!ValidateTriangleMesh(mesh, out string error))
            {
                summary = $"native transition face {face} is invalid: {error}";
                return false;
            }
            populatedFaces++;
            triangleCount += mesh.Indices.Length / 3;
        }

        if (populatedFaces == 0 || triangleCount == 0)
        {
            summary = "transition fixture emitted no geometry";
            return false;
        }

        summary = $"native fine regular={fineMesh.Indices.Length / 3} triangles, coarse regular={coarseMesh.Indices.Length / 3} triangles, transition faces={populatedFaces}/6, transition={triangleCount} triangles, backend={VoxelChunkMesherNative.Status}";
        return true;
    }

    public static string RunBenchmark(
        int iterations = DefaultBenchmarkIterations,
        bool forceManaged = false)
    {
        iterations = Math.Clamp(iterations, 1, 4096);
        VoxelBlockPalette palette = VoxelBlockPalette.Capture(null);
        VoxelChunkData rugged = CreateRuggedChunk();

        for (int i = 0; i < 6; i++)
        {
            _ = forceManaged
                ? VoxelChunkMesher.BuildManaged(rugged, palette)
                : VoxelChunkMesher.Build(rugged, palette);
        }

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var timer = Stopwatch.StartNew();
        int vertexTotal = 0;
        MeshBuildData last = default;
        for (int i = 0; i < iterations; i++)
        {
            last = forceManaged
                ? VoxelChunkMesher.BuildManaged(rugged, palette)
                : VoxelChunkMesher.Build(rugged, palette);
            vertexTotal += last.Vertices.Length;
        }
        timer.Stop();
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        GC.KeepAlive(last.Vertices);

        double totalMilliseconds = timer.Elapsed.TotalMilliseconds;
        double millisecondsPerChunk = totalMilliseconds / iterations;
        double chunksPerSecond = totalMilliseconds > 0.0
            ? iterations * 1000.0 / totalMilliseconds
            : 0.0;
        double allocatedKiBPerChunk = allocatedBytes / 1024.0 / iterations;
        string backend = forceManaged ? "managed" : VoxelChunkMesherNative.Status;
        return $"backend={backend}, iterations={iterations}, avg={millisecondsPerChunk:0.000}ms/chunk, throughput={chunksPerSecond:0.0} chunks/s, allocated={allocatedKiBPerChunk:0.0}KiB/chunk, vertices/build={vertexTotal / iterations}, hash=0x{ComputeMeshHash(last):X16}";
    }

    private static VoxelChunkData CreateRuggedChunk()
    {
        var data = new VoxelChunkData();
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            uint columnHash = Hash((uint)x, 0u, (uint)z);
            int height = 7 + (int)(columnHash % 21u);
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            {
                if (y > height)
                {
                    data.SetBlock(x, y, z, BlockType.Air);
                    continue;
                }

                uint voxelHash = Hash((uint)x, (uint)y, (uint)z);
                bool cave = y > 2 && y < height - 1 && (voxelHash & 31u) == 0u;
                BlockType block = cave
                    ? BlockType.Air
                    : y == height
                        ? BlockType.Soil
                        : y == 0
                            ? BlockType.Bedrock
                            : BlockType.Rock;
                data.SetBlock(x, y, z, block);
            }
        }
        return data;
    }

    private static bool ValidateSmoothChunkBoundaryNormals(
        in VoxelBlockPalette palette,
        out string error)
    {
        const int chunkSize = VoxelConstants.ChunkSizeX;
        var left = new VoxelChunkData();
        var right = new VoxelChunkData();
        PopulateSlopedTerrain(left, 0);
        PopulateSlopedTerrain(right, chunkSize);

        MeshBuildData leftMesh = VoxelChunkMesher.Build(
            left,
            palette,
            externalBlockSampler: static (x, y, z) => SampleSlopedTerrain(x, y, z),
            smoothNaturalTerrain: true);
        MeshBuildData rightMesh = VoxelChunkMesher.Build(
            right,
            palette,
            externalBlockSampler: static (x, y, z) => SampleSlopedTerrain(x + chunkSize, y, z),
            smoothNaturalTerrain: true);
        if (!ValidateTriangleMesh(leftMesh, out error)
            || !ValidateTriangleMesh(rightMesh, out error))
        {
            return false;
        }

        var leftBoundaryNormals = new Dictionary<Vector2I, Vector3>();
        for (int index = 0; index < leftMesh.Vertices.Length; index++)
        {
            Vector3 vertex = leftMesh.Vertices[index];
            if (!Mathf.IsEqualApprox(vertex.X, chunkSize)) continue;
            leftBoundaryNormals[new Vector2I(
                Mathf.RoundToInt(vertex.Y * 2.0f),
                Mathf.RoundToInt(vertex.Z * 2.0f))] = leftMesh.Normals[index];
        }

        int sharedVertexCount = 0;
        for (int index = 0; index < rightMesh.Vertices.Length; index++)
        {
            Vector3 vertex = rightMesh.Vertices[index];
            if (!Mathf.IsZeroApprox(vertex.X)) continue;
            var key = new Vector2I(
                Mathf.RoundToInt(vertex.Y * 2.0f),
                Mathf.RoundToInt(vertex.Z * 2.0f));
            if (!leftBoundaryNormals.TryGetValue(key, out Vector3 leftNormal)) continue;
            sharedVertexCount++;
            if (leftNormal.Dot(rightMesh.Normals[index]) < 0.9999f)
            {
                error = $"smooth boundary normal mismatch at {key}";
                return false;
            }
        }

        if (sharedVertexCount == 0)
        {
            error = "smooth boundary fixture emitted no shared vertices";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static void PopulateSlopedTerrain(VoxelChunkData data, int globalXOffset)
    {
        for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
        for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
        for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
        {
            data.SetBlock(x, y, z, SampleSlopedTerrain(globalXOffset + x, y, z));
        }
    }

    private static BlockType SampleSlopedTerrain(int globalX, int y, int z)
    {
        int surface = 8 + globalX / 4 + z / 12;
        return y <= surface ? BlockType.Rock : BlockType.Air;
    }

    private static uint Hash(uint x, uint y, uint z)
    {
        uint value = x * 0x9E3779B9u ^ y * 0x85EBCA6Bu ^ z * 0xC2B2AE35u ^ 0xA511E9B3u;
        value ^= value >> 16;
        value *= 0x7FEB352Du;
        value ^= value >> 15;
        value *= 0x846CA68Bu;
        return value ^ (value >> 16);
    }

    private static bool ValidateMesh(in MeshBuildData mesh, out string error)
    {
        if (mesh.Vertices.Length != mesh.Normals.Length
            || mesh.Vertices.Length != mesh.Colors.Length
            || mesh.Vertices.Length % 4 != 0
            || mesh.Indices.Length % 6 != 0)
        {
            error = "vertex attribute or quad/index counts are inconsistent";
            return false;
        }

        foreach (int index in mesh.Indices)
        {
            if ((uint)index >= (uint)mesh.Vertices.Length)
            {
                error = $"index {index} is outside {mesh.Vertices.Length} vertices";
                return false;
            }
        }

        foreach (Vector3 vertex in mesh.Vertices)
        {
            if (!float.IsFinite(vertex.X)
                || !float.IsFinite(vertex.Y)
                || !float.IsFinite(vertex.Z))
            {
                error = "mesh contains a non-finite vertex";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateTriangleMesh(in MeshBuildData mesh, out string error)
    {
        if (!mesh.HasGeometry
            || mesh.Vertices.Length != mesh.Normals.Length
            || mesh.Vertices.Length != mesh.Colors.Length
            || mesh.Indices.Length % 3 != 0)
        {
            error = "smooth triangle mesh attributes or indices are inconsistent";
            return false;
        }

        foreach (int index in mesh.Indices)
        {
            if ((uint)index >= (uint)mesh.Vertices.Length)
            {
                error = $"smooth mesh index {index} is outside {mesh.Vertices.Length} vertices";
                return false;
            }
        }

        for (int index = 0; index < mesh.Vertices.Length; index++)
        {
            Vector3 vertex = mesh.Vertices[index];
            Vector3 normal = mesh.Normals[index];
            if (!float.IsFinite(vertex.X)
                || !float.IsFinite(vertex.Y)
                || !float.IsFinite(vertex.Z)
                || !float.IsFinite(normal.X)
                || !float.IsFinite(normal.Y)
                || !float.IsFinite(normal.Z)
                || normal.LengthSquared() < 0.5f)
            {
                error = "smooth mesh contains a non-finite vertex or invalid normal";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool ValidateGodotSmoothWinding(in MeshBuildData mesh, out string error)
    {
        for (int index = 0; index < mesh.Indices.Length; index += 3)
        {
            int ia = mesh.Indices[index];
            int ib = mesh.Indices[index + 1];
            int ic = mesh.Indices[index + 2];
            Vector3 geometricNormal = (mesh.Vertices[ib] - mesh.Vertices[ia])
                .Cross(mesh.Vertices[ic] - mesh.Vertices[ia]);
            Vector3 outwardNormal = (mesh.Normals[ia] + mesh.Normals[ib] + mesh.Normals[ic]).Normalized();
            if (geometricNormal.Dot(outwardNormal) >= -0.00001f)
            {
                error = "smooth terrain triangle does not use Godot clockwise front-face winding";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static bool MeshesMatch(in MeshBuildData left, in MeshBuildData right)
    {
        return left.Vertices.AsSpan().SequenceEqual(right.Vertices)
            && left.Normals.AsSpan().SequenceEqual(right.Normals)
            && left.Colors.AsSpan().SequenceEqual(right.Colors)
            && left.Indices.AsSpan().SequenceEqual(right.Indices);
    }

    private static ulong ComputeMeshHash(in MeshBuildData mesh)
    {
        const ulong offset = 1469598103934665603UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;

        static void Mix(ref ulong value, uint data)
        {
            const ulong innerPrime = 1099511628211UL;
            value = (value ^ data) * innerPrime;
        }

        foreach (Vector3 vertex in mesh.Vertices)
        {
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.X)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Y)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(vertex.Z)));
        }
        foreach (Vector3 normal in mesh.Normals)
        {
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.X)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Y)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(normal.Z)));
        }
        foreach (Color color in mesh.Colors)
        {
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(color.R)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(color.G)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(color.B)));
            Mix(ref hash, unchecked((uint)BitConverter.SingleToInt32Bits(color.A)));
        }
        foreach (int index in mesh.Indices)
        {
            Mix(ref hash, unchecked((uint)index));
        }

        return hash ^ prime;
    }
}
