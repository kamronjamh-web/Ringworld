using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Phase1;

/// <summary>
/// Loads the optional native mesher once on the main thread, then calls its
/// plain C ABI from background workers. Batching one whole chunk per call keeps
/// Godot/.NET interop out of the hot face scan. Missing or failed native code
/// always falls back to the managed mesher.
/// </summary>
public static class VoxelChunkMesherNative
{
    private const int ExpectedAbiVersion = 7;
    private const int MaximumVertexCount = 6 * VoxelConstants.ChunkVolume * 4;
    private const int MaximumIndexCount = 6 * VoxelConstants.ChunkVolume * 6;
    private const int MaximumSmoothVertexCount = 33 * 33 * 33 * 36;
    private const int MaximumSmoothIndexCount = MaximumSmoothVertexCount;

    private static IntPtr _libraryHandle;
    private static BuildDelegate? _build;
    private static BuildSmoothRegularDelegate? _buildSmoothRegular;
    private static BuildSmoothRegularSdfDelegate? _buildSmoothRegularSdf;
    private static BuildSmoothRegularFineSdfDelegate? _buildSmoothRegularFineSdf;
    private static BuildSmoothTransitionDelegate? _buildSmoothTransition;
    private static FreeDelegate? _free;
    private static int _initializationAttempted;
    private static int _disabledAfterFailure;

    [ThreadStatic]
    private static NativeInputWorkspace? _threadInput;

    public static bool IsAvailable =>
        _build != null
        && _free != null
        && Volatile.Read(ref _disabledAfterFailure) == 0;

    public static bool IsSmoothRegularAvailable =>
        _buildSmoothRegular != null
        && _free != null
        && Volatile.Read(ref _disabledAfterFailure) == 0;

    public static bool IsSmoothRegularSdfAvailable =>
        _buildSmoothRegularSdf != null
        && _free != null
        && Volatile.Read(ref _disabledAfterFailure) == 0;

    public static bool IsSmoothRegularFineSdfAvailable =>
        _buildSmoothRegularFineSdf != null
        && _free != null
        && Volatile.Read(ref _disabledAfterFailure) == 0;

    public static bool IsSmoothTransitionAvailable =>
        _buildSmoothTransition != null
        && _free != null
        && Volatile.Read(ref _disabledAfterFailure) == 0;

    public static string Status { get; private set; } = "managed fallback (native backend not initialized)";

    public static void InitializeFromProject(string[] userArguments)
    {
        if (Interlocked.Exchange(ref _initializationAttempted, 1) != 0)
            return;

        if (Array.Exists(
            userArguments,
            static argument => argument.Equals("--disable-native-voxel-mesher", StringComparison.OrdinalIgnoreCase)))
        {
            Status = "managed fallback (disabled by command line)";
            return;
        }

        string relativePath = OS.GetName().Equals("Windows", StringComparison.OrdinalIgnoreCase)
            ? "res://native/ring_voxel_mesher/bin/windows/ring_voxel_mesher_runtime_v7.dll"
            : OS.GetName().Equals("Linux", StringComparison.OrdinalIgnoreCase)
                ? "res://native/ring_voxel_mesher/bin/linux/libring_voxel_mesher.so"
                : string.Empty;
        if (string.IsNullOrEmpty(relativePath))
        {
            Status = $"managed fallback (native backend is not packaged for {OS.GetName()})";
            return;
        }

        string libraryPath = ProjectSettings.GlobalizePath(relativePath);
        bool requireNative = Array.Exists(
            userArguments,
            static argument => argument.Equals("--require-native-voxel-mesher", StringComparison.OrdinalIgnoreCase));
        if (!File.Exists(libraryPath))
        {
            Status = $"managed fallback (native binary missing: {relativePath})";
            if (requireNative)
                GD.PushError($"Required native voxel mesher is missing: {libraryPath}");
            return;
        }

        try
        {
            if (!NativeLibrary.TryLoad(libraryPath, out _libraryHandle))
                throw new DllNotFoundException(libraryPath);

            AbiVersionDelegate abiVersion = LoadExport<AbiVersionDelegate>(
                _libraryHandle,
                "ring_voxel_mesher_abi_version");
            int actualAbi = abiVersion();
            if (actualAbi != ExpectedAbiVersion)
                throw new InvalidOperationException($"native ABI {actualAbi}, expected {ExpectedAbiVersion}");

            _build = LoadExport<BuildDelegate>(_libraryHandle, "ring_voxel_mesher_build");
            _buildSmoothRegular = LoadExport<BuildSmoothRegularDelegate>(
                _libraryHandle,
                "ring_voxel_mesher_build_smooth_regular");
            _buildSmoothRegularSdf = LoadExport<BuildSmoothRegularSdfDelegate>(
                _libraryHandle,
                "ring_voxel_mesher_build_smooth_regular_sdf");
            _buildSmoothRegularFineSdf = LoadExport<BuildSmoothRegularFineSdfDelegate>(
                _libraryHandle,
                "ring_voxel_mesher_build_smooth_regular_fine_sdf");
            _buildSmoothTransition = LoadExport<BuildSmoothTransitionDelegate>(
                _libraryHandle,
                "ring_voxel_mesher_build_smooth_transition");
            _free = LoadExport<FreeDelegate>(_libraryHandle, "ring_voxel_mesher_free");
            Status = $"native ABI {actualAbi} ({relativePath})";
            GD.Print($"Native voxel mesher ready: {Status}");
        }
        catch (Exception exception)
        {
            _build = null;
            _buildSmoothRegular = null;
            _buildSmoothRegularSdf = null;
            _buildSmoothRegularFineSdf = null;
            _buildSmoothTransition = null;
            _free = null;
            if (_libraryHandle != IntPtr.Zero)
            {
                NativeLibrary.Free(_libraryHandle);
                _libraryHandle = IntPtr.Zero;
            }
            Status = $"managed fallback ({exception.Message})";
            if (requireNative)
                GD.PushError($"Required native voxel mesher failed to load: {exception.Message}");
            else
                GD.PushWarning($"Native voxel mesher unavailable; using managed backend: {exception.Message}");
        }
    }

    internal static unsafe bool TryBuild(
        VoxelChunkData chunkData,
        in VoxelBlockPalette palette,
        Func<int, int, int, BlockType>? externalBlockSampler,
        Func<Vector3, Vector3>? localVertexWarp,
        bool useGreedyMerging,
        bool buildCollisionFaces,
        out MeshBuildData mesh)
    {
        BuildDelegate? build = _build;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            mesh = default;
            return false;
        }

        NativeInputWorkspace input = _threadInput ??= new NativeInputWorkspace();
        input.Capture(externalBlockSampler);
        NativeMeshResult native = default;

        try
        {
            ReadOnlySpan<byte> blockBytes = MemoryMarshal.AsBytes(chunkData.BlockSpan);
            fixed (byte* blocks = blockBytes)
            fixed (byte* negativeX = input.NegativeX)
            fixed (byte* positiveX = input.PositiveX)
            fixed (byte* negativeY = input.NegativeY)
            fixed (byte* positiveY = input.PositiveY)
            fixed (byte* negativeZ = input.NegativeZ)
            fixed (byte* positiveZ = input.PositiveZ)
            {
                int error = build(
                    blocks,
                    negativeX,
                    positiveX,
                    negativeY,
                    positiveY,
                    negativeZ,
                    positiveZ,
                    useGreedyMerging ? 1 : 0,
                    &native);
                if (error != 0)
                    throw new InvalidOperationException($"native build returned error {error}");
            }

            ValidateNativeResult(in native, requireQuadTopology: true);
            var vertices = new Vector3[native.VertexCount];
            var normals = new Vector3[native.VertexCount];
            var colors = new Color[native.VertexCount];
            var indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions;
            float* nativeNormals = (float*)native.Normals;
            byte* blockTypes = (byte*)native.BlockTypes;
            int* nativeIndices = (int*)native.Indices;

            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                Vector3 vertex = new(
                    positions[component],
                    positions[component + 1],
                    positions[component + 2]);
                vertices[i] = localVertexWarp != null
                    ? localVertexWarp(vertex)
                    : vertex;
                normals[i] = new Vector3(
                    nativeNormals[component],
                    nativeNormals[component + 1],
                    nativeNormals[component + 2]);
                colors[i] = palette.GetColor((BlockType)blockTypes[i]);
            }
            for (int i = 0; i < native.IndexCount; i++)
                indices[i] = nativeIndices[i];

            mesh = new MeshBuildData(
                vertices,
                normals,
                colors,
                indices,
                buildCollisionFaces
                    ? BuildCollisionFaces(vertices, indices)
                    : Array.Empty<Vector3>());
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native build disabled after failure: {exception.Message})";
            mesh = default;
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero
                || native.Normals != IntPtr.Zero
                || native.BlockTypes != IntPtr.Zero
                || native.Indices != IntPtr.Zero)
            {
                free(&native);
            }
        }
    }

    internal static unsafe bool TryBuildSmoothRegular(
        VoxelChunkData chunkData,
        in VoxelBlockPalette palette,
        Func<int, int, int, BlockType>? externalBlockSampler,
        Func<Vector3, Vector3>? localVertexWarp,
        bool buildCollisionFaces,
        out MeshBuildData mesh)
    {
        BuildSmoothRegularDelegate? build = _buildSmoothRegular;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            mesh = default;
            return false;
        }

        NativeInputWorkspace input = _threadInput ??= new NativeInputWorkspace();
        input.CaptureSmoothHalo(chunkData, externalBlockSampler);
        NativeMeshResult native = default;
        try
        {
            fixed (byte* halo = input.SmoothHalo)
            {
                int error = build(halo, &native);
                if (error != 0)
                    throw new InvalidOperationException($"native smooth build returned error {error}");
            }
            ValidateNativeResult(in native, requireQuadTopology: false);
            var vertices = new Vector3[native.VertexCount];
            var normals = new Vector3[native.VertexCount];
            var colors = new Color[native.VertexCount];
            var indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions;
            float* nativeNormals = (float*)native.Normals;
            byte* blockTypes = (byte*)native.BlockTypes;
            int* nativeIndices = (int*)native.Indices;
            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                Vector3 vertex = new(positions[component], positions[component + 1], positions[component + 2]);
                vertices[i] = localVertexWarp != null ? localVertexWarp(vertex) : vertex;
                normals[i] = new Vector3(
                    nativeNormals[component],
                    nativeNormals[component + 1],
                    nativeNormals[component + 2]);
                colors[i] = palette.GetColor((BlockType)blockTypes[i]);
            }
            for (int i = 0; i < native.IndexCount; i++) indices[i] = nativeIndices[i];
            mesh = new MeshBuildData(
                vertices,
                normals,
                colors,
                indices,
                buildCollisionFaces ? BuildCollisionFaces(vertices, indices) : Array.Empty<Vector3>());
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native smooth build disabled after failure: {exception.Message})";
            mesh = default;
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero
                || native.Normals != IntPtr.Zero
                || native.BlockTypes != IntPtr.Zero
                || native.Indices != IntPtr.Zero)
            {
                free(&native);
            }
        }
    }

    internal static unsafe bool TryBuildSmoothRegularSdf(
        RingTerrainCoarseSdfChunk coarse,
        in VoxelBlockPalette palette,
        Func<Vector3, Vector3>? localVertexWarp,
        out MeshBuildData mesh)
    {
        BuildSmoothRegularSdfDelegate? build = _buildSmoothRegularSdf;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            mesh = default;
            return false;
        }

        NativeMeshResult native = default;
        try
        {
            fixed (float* densities = coarse.Densities)
            fixed (BlockType* materials = coarse.Materials)
            {
                int error = build(densities, (byte*)materials, &native);
                if (error != 0)
                    throw new InvalidOperationException($"native coarse SDF build returned error {error}");
            }
            ValidateNativeResult(in native, requireQuadTopology: false);
            var vertices = new Vector3[native.VertexCount];
            var normals = new Vector3[native.VertexCount];
            var colors = new Color[native.VertexCount];
            var indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions;
            float* nativeNormals = (float*)native.Normals;
            byte* blockTypes = (byte*)native.BlockTypes;
            int* nativeIndices = (int*)native.Indices;
            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                Vector3 vertex = new(
                    positions[component] * coarse.SpacingMeters,
                    positions[component + 1] * coarse.SpacingMeters,
                    positions[component + 2] * coarse.SpacingMeters);
                vertices[i] = localVertexWarp != null ? localVertexWarp(vertex) : vertex;
                normals[i] = new Vector3(
                    nativeNormals[component],
                    nativeNormals[component + 1],
                    nativeNormals[component + 2]);
                colors[i] = palette.GetColor((BlockType)blockTypes[i]);
            }
            for (int i = 0; i < native.IndexCount; i++) indices[i] = nativeIndices[i];
            mesh = new MeshBuildData(vertices, normals, colors, indices, Array.Empty<Vector3>());
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native coarse SDF build disabled after failure: {exception.Message})";
            mesh = default;
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero || native.Normals != IntPtr.Zero || native.BlockTypes != IntPtr.Zero || native.Indices != IntPtr.Zero)
                free(&native);
        }
    }

    internal static unsafe bool TryBuildSmoothRegularFineSdf(
        RingTerrainFineSdfHalo halo,
        in VoxelBlockPalette palette,
        Func<Vector3, Vector3>? localVertexWarp,
        bool buildCollisionFaces,
        out MeshBuildData mesh)
    {
        BuildSmoothRegularFineSdfDelegate? build = _buildSmoothRegularFineSdf;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            mesh = default;
            return false;
        }

        NativeMeshResult native = default;
        try
        {
            fixed (float* densities = halo.Densities)
            fixed (BlockType* materials = halo.Materials)
            {
                int error = build(densities, (byte*)materials, &native);
                if (error != 0)
                    throw new InvalidOperationException($"native fine SDF build returned error {error}");
            }
            ValidateNativeResult(in native, requireQuadTopology: false);
            var vertices = new Vector3[native.VertexCount];
            var normals = new Vector3[native.VertexCount];
            var colors = new Color[native.VertexCount];
            var indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions;
            float* nativeNormals = (float*)native.Normals;
            byte* blockTypes = (byte*)native.BlockTypes;
            int* nativeIndices = (int*)native.Indices;
            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                Vector3 vertex = new(positions[component], positions[component + 1], positions[component + 2]);
                vertices[i] = localVertexWarp != null ? localVertexWarp(vertex) : vertex;
                normals[i] = new Vector3(nativeNormals[component], nativeNormals[component + 1], nativeNormals[component + 2]);
                colors[i] = palette.GetColor((BlockType)blockTypes[i]);
            }
            for (int i = 0; i < native.IndexCount; i++) indices[i] = nativeIndices[i];
            mesh = new MeshBuildData(vertices, normals, colors, indices,
                buildCollisionFaces ? BuildCollisionFaces(vertices, indices) : Array.Empty<Vector3>());
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native fine SDF build disabled after failure: {exception.Message})";
            mesh = default;
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero || native.Normals != IntPtr.Zero || native.BlockTypes != IntPtr.Zero || native.Indices != IntPtr.Zero)
                free(&native);
        }
    }

    internal static unsafe bool TryBuildSmoothTransition(
        RingTerrainFineSdfHalo halo,
        RingTerrainCoarseSdfChunk coarse,
        int face,
        in VoxelBlockPalette palette,
        Func<Vector3, Vector3>? localVertexWarp,
        out MeshBuildData mesh)
    {
        BuildSmoothTransitionDelegate? build = _buildSmoothTransition;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            mesh = default;
            return false;
        }

        if (coarse.SpacingMeters != 2)
        {
            mesh = default;
            return false;
        }

        long aroundDelta = halo.GlobalOrigin.AroundVoxel - coarse.GlobalOrigin.AroundVoxel;
        long verticalDelta = halo.GlobalOrigin.VerticalVoxel - coarse.GlobalOrigin.VerticalVoxel;
        long acrossDelta = halo.GlobalOrigin.AcrossVoxel - coarse.GlobalOrigin.AcrossVoxel;
        if (aroundDelta is < int.MinValue or > int.MaxValue
            || verticalDelta is < int.MinValue or > int.MaxValue
            || acrossDelta is < int.MinValue or > int.MaxValue)
        {
            mesh = default;
            return false;
        }
        NativeMeshResult native = default;
        try
        {
            fixed (float* densities = halo.Densities)
            fixed (BlockType* materials = halo.Materials)
            fixed (float* coarseDensities = coarse.Densities)
            fixed (BlockType* coarseMaterials = coarse.Materials)
            {
                int error = build(
                    densities,
                    (byte*)materials,
                    coarseDensities,
                    (byte*)coarseMaterials,
                    (int)aroundDelta,
                    (int)verticalDelta,
                    (int)acrossDelta,
                    coarse.SpacingMeters,
                    face,
                    &native);
                if (error != 0)
                    throw new InvalidOperationException($"native smooth transition build returned error {error}");
            }
            ValidateNativeResult(in native, requireQuadTopology: false);
            var vertices = new Vector3[native.VertexCount];
            var normals = new Vector3[native.VertexCount];
            var colors = new Color[native.VertexCount];
            var indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions;
            float* nativeNormals = (float*)native.Normals;
            byte* blockTypes = (byte*)native.BlockTypes;
            int* nativeIndices = (int*)native.Indices;
            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                Vector3 vertex = new(positions[component], positions[component + 1], positions[component + 2]);
                vertices[i] = localVertexWarp != null ? localVertexWarp(vertex) : vertex;
                normals[i] = new Vector3(nativeNormals[component], nativeNormals[component + 1], nativeNormals[component + 2]);
                colors[i] = palette.GetColor((BlockType)blockTypes[i]);
            }
            for (int i = 0; i < native.IndexCount; i++) indices[i] = nativeIndices[i];
            mesh = new MeshBuildData(vertices, normals, colors, indices, Array.Empty<Vector3>());
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native transition build disabled after failure: {exception.Message})";
            mesh = default;
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero || native.Normals != IntPtr.Zero || native.BlockTypes != IntPtr.Zero || native.Indices != IntPtr.Zero)
                free(&native);
        }
    }

    /// <summary>
    /// Direct transition-cell bridge for systems which already own their SDF
    /// material ids. Coordinates passed to the native table are in fine-cell
    /// units; callers scale positions after this call. Keeping the ids as
    /// bytes avoids an unnecessary palette/colour round-trip.
    /// </summary>
    internal static unsafe bool TryBuildSmoothTransitionRaw(
        float[] haloDensities, byte[] haloMaterials,
        float[] coarseDensities, byte[] coarseMaterials,
        int haloOriginMinusCoarseX, int haloOriginMinusCoarseY, int haloOriginMinusCoarseZ,
        int face,
        out Vector3[] vertices, out Vector3[] normals, out byte[] materials, out int[] indices)
    {
        const int fineSamples = 35;
        const int coarseSamples = 33;
        if (haloDensities.Length != fineSamples * fineSamples * fineSamples
            || haloMaterials.Length != haloDensities.Length
            || coarseDensities.Length != coarseSamples * coarseSamples * coarseSamples
            || coarseMaterials.Length != coarseDensities.Length)
            throw new ArgumentException("Transition lattices must be 35^3 fine and 33^3 coarse samples.");
        BuildSmoothTransitionDelegate? build = _buildSmoothTransition;
        FreeDelegate? free = _free;
        if (build == null || free == null || Volatile.Read(ref _disabledAfterFailure) != 0)
        {
            vertices = Array.Empty<Vector3>(); normals = Array.Empty<Vector3>();
            materials = Array.Empty<byte>(); indices = Array.Empty<int>();
            return false;
        }
        NativeMeshResult native = default;
        try
        {
            fixed (float* fineDensity = haloDensities)
            fixed (byte* fineMaterial = haloMaterials)
            fixed (float* coarseDensity = coarseDensities)
            fixed (byte* coarseMaterial = coarseMaterials)
            {
                int error = build(fineDensity, fineMaterial, coarseDensity, coarseMaterial,
                    haloOriginMinusCoarseX, haloOriginMinusCoarseY, haloOriginMinusCoarseZ,
                    coarseSpacing: 2, face, &native);
                if (error != 0) throw new InvalidOperationException($"native smooth transition build returned error {error}");
            }
            ValidateNativeResult(in native, requireQuadTopology: false);
            vertices = new Vector3[native.VertexCount]; normals = new Vector3[native.VertexCount];
            materials = new byte[native.VertexCount]; indices = new int[native.IndexCount];
            float* positions = (float*)native.Positions; float* nativeNormals = (float*)native.Normals;
            byte* nativeMaterials = (byte*)native.BlockTypes; int* nativeIndices = (int*)native.Indices;
            for (int i = 0; i < native.VertexCount; i++)
            {
                int component = i * 3;
                vertices[i] = new Vector3(positions[component], positions[component + 1], positions[component + 2]);
                normals[i] = new Vector3(nativeNormals[component], nativeNormals[component + 1], nativeNormals[component + 2]);
                materials[i] = nativeMaterials[i];
            }
            for (int i = 0; i < native.IndexCount; i++) indices[i] = nativeIndices[i];
            return true;
        }
        catch (Exception exception)
        {
            Interlocked.Exchange(ref _disabledAfterFailure, 1);
            Status = $"managed fallback (native transition build disabled after failure: {exception.Message})";
            vertices = Array.Empty<Vector3>(); normals = Array.Empty<Vector3>();
            materials = Array.Empty<byte>(); indices = Array.Empty<int>();
            return false;
        }
        finally
        {
            if (native.Positions != IntPtr.Zero || native.Normals != IntPtr.Zero || native.BlockTypes != IntPtr.Zero || native.Indices != IntPtr.Zero)
                free(&native);
        }
    }

    private static T LoadExport<T>(IntPtr handle, string name) where T : Delegate
    {
        IntPtr address = NativeLibrary.GetExport(handle, name);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private static void ValidateNativeResult(in NativeMeshResult result, bool requireQuadTopology)
    {
        if (result.VertexCount < 0
            || result.VertexCount > (requireQuadTopology ? MaximumVertexCount : MaximumSmoothVertexCount)
            || result.IndexCount < 0
            || result.IndexCount > (requireQuadTopology ? MaximumIndexCount : MaximumSmoothIndexCount)
            || (requireQuadTopology && (result.VertexCount % 4 != 0 || result.IndexCount % 6 != 0))
            || (!requireQuadTopology && result.IndexCount % 3 != 0))
        {
            throw new InvalidOperationException(
                $"invalid native counts {result.VertexCount} vertices/{result.IndexCount} indices");
        }
        if (result.VertexCount > 0
            && (result.Positions == IntPtr.Zero
                || result.Normals == IntPtr.Zero
                || result.BlockTypes == IntPtr.Zero))
        {
            throw new InvalidOperationException("native vertex output contains a null buffer");
        }
        if (result.IndexCount > 0 && result.Indices == IntPtr.Zero)
            throw new InvalidOperationException("native index output contains a null buffer");
    }

    private static Vector3[] BuildCollisionFaces(Vector3[] vertices, int[] indices)
    {
        var faces = new Vector3[indices.Length];
        for (int i = 0; i < indices.Length; i++)
            faces[i] = vertices[indices[i]];
        return faces;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int BuildDelegate(
        byte* blocks,
        byte* negativeX,
        byte* positiveX,
        byte* negativeY,
        byte* positiveY,
        byte* negativeZ,
        byte* positiveZ,
        int useGreedyMerging,
        NativeMeshResult* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int BuildSmoothRegularDelegate(
        byte* haloBlocks,
        NativeMeshResult* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int BuildSmoothRegularSdfDelegate(
        float* densities,
        byte* materials,
        NativeMeshResult* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int BuildSmoothRegularFineSdfDelegate(
        float* densities,
        byte* materials,
        NativeMeshResult* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int BuildSmoothTransitionDelegate(
        float* haloDensities,
        byte* haloMaterials,
        float* coarseDensities,
        byte* coarseMaterials,
        int haloOriginMinusCoarseX,
        int haloOriginMinusCoarseY,
        int haloOriginMinusCoarseZ,
        int coarseSpacing,
        int face,
        NativeMeshResult* result);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate void FreeDelegate(NativeMeshResult* result);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMeshResult
    {
        public IntPtr Positions;
        public IntPtr Normals;
        public IntPtr BlockTypes;
        public IntPtr Indices;
        public int VertexCount;
        public int IndexCount;
    }

    private sealed class NativeInputWorkspace
    {
        private const int SmoothHaloSize = VoxelConstants.ChunkSizeX + 3;
        public byte[] NegativeX { get; } = new byte[VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ];
        public byte[] PositiveX { get; } = new byte[VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ];
        public byte[] NegativeY { get; } = new byte[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
        public byte[] PositiveY { get; } = new byte[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ];
        public byte[] NegativeZ { get; } = new byte[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY];
        public byte[] PositiveZ { get; } = new byte[VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY];
        public byte[] SmoothHalo { get; } = new byte[SmoothHaloSize * SmoothHaloSize * SmoothHaloSize];

        public void CaptureSmoothHalo(
            VoxelChunkData source,
            Func<int, int, int, BlockType>? sampler)
        {
            int index = 0;
            for (int z = -1; z <= VoxelConstants.ChunkSizeZ + 1; z++)
            for (int y = -1; y <= VoxelConstants.ChunkSizeY + 1; y++)
            for (int x = -1; x <= VoxelConstants.ChunkSizeX + 1; x++)
            {
                BlockType block = source.IsInBounds(x, y, z)
                    ? source.GetBlock(x, y, z)
                    : sampler?.Invoke(x, y, z) ?? source.GetBlock(
                        Math.Clamp(x, 0, VoxelConstants.ChunkSizeX - 1),
                        Math.Clamp(y, 0, VoxelConstants.ChunkSizeY - 1),
                        Math.Clamp(z, 0, VoxelConstants.ChunkSizeZ - 1));
                SmoothHalo[index++] = (byte)block;
            }
        }

        public void Capture(Func<int, int, int, BlockType>? sampler)
        {
            if (sampler == null)
            {
                Array.Clear(NegativeX);
                Array.Clear(PositiveX);
                Array.Clear(NegativeY);
                Array.Clear(PositiveY);
                Array.Clear(NegativeZ);
                Array.Clear(PositiveZ);
                return;
            }

            for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            {
                int index = y + VoxelConstants.ChunkSizeY * z;
                NegativeX[index] = (byte)sampler(-1, y, z);
                PositiveX[index] = (byte)sampler(VoxelConstants.ChunkSizeX, y, z);
            }

            for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            {
                int index = x + VoxelConstants.ChunkSizeX * z;
                NegativeY[index] = (byte)sampler(x, -1, z);
                PositiveY[index] = (byte)sampler(x, VoxelConstants.ChunkSizeY, z);
            }

            for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
            for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
            {
                int index = x + VoxelConstants.ChunkSizeX * y;
                NegativeZ[index] = (byte)sampler(x, y, -1);
                PositiveZ[index] = (byte)sampler(x, y, VoxelConstants.ChunkSizeZ);
            }
        }
    }
}
