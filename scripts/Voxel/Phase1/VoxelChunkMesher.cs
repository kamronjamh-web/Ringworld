using System;
using System.Runtime.CompilerServices;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Phase1;

public static class VoxelChunkMesher
{
	private const byte EmptyMaskValue = 0;
	private const int InitialVertexCapacity = 2048;
	private const int InitialIndexCapacity = 3072;
	private const int MaximumRetainedVertexCapacity = 131072;
	private const int MaximumRetainedIndexCapacity = MaximumRetainedVertexCapacity * 3 / 2;

	[ThreadStatic]
	private static MesherWorkspace? _threadWorkspace;

	private enum FaceDirection : byte
	{
		PositiveX,
		NegativeX,
		PositiveY,
		NegativeY,
		PositiveZ,
		NegativeZ
	}

	public static MeshBuildData Build(
		VoxelChunkData chunkData,
		VoxelBlockPalette palette,
		Func<int, int, int, BlockType>? externalBlockSampler = null,
		Func<Vector3, Vector3>? localVertexWarp = null,
		bool useGreedyMerging = true,
		bool buildCollisionFaces = false,
		bool smoothNaturalTerrain = false,
		RingTerrainFineSdfHalo? smoothTerrainHalo = null)
	{
		if (smoothNaturalTerrain)
		{
			return VoxelSmoothTerrainMesher.BuildHybrid(
				chunkData,
				palette,
				externalBlockSampler,
				localVertexWarp,
				buildCollisionFaces,
				smoothTerrainHalo);
		}

		return VoxelChunkMesherNative.TryBuild(
			chunkData,
			palette,
			externalBlockSampler,
			localVertexWarp,
			useGreedyMerging,
			buildCollisionFaces,
			out MeshBuildData nativeMesh)
			? nativeMesh
			: BuildManaged(
				chunkData,
				palette,
				externalBlockSampler,
				localVertexWarp,
				useGreedyMerging,
				buildCollisionFaces);
	}

	internal static MeshBuildData BuildManaged(
		VoxelChunkData chunkData,
		VoxelBlockPalette palette,
		Func<int, int, int, BlockType>? externalBlockSampler = null,
		Func<Vector3, Vector3>? localVertexWarp = null,
		bool useGreedyMerging = true,
		bool buildCollisionFaces = false)
	{
		MesherWorkspace workspace = _threadWorkspace ??= new MesherWorkspace();
		workspace.Reset();
		ReadOnlySpan<BlockType> blocks = chunkData.BlockSpan;

		BuildPositiveXFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);
		BuildNegativeXFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);
		BuildPositiveYFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);
		BuildNegativeYFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);
		BuildPositiveZFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);
		BuildNegativeZFaces(blocks, externalBlockSampler, palette, localVertexWarp, useGreedyMerging, workspace);

		Vector3[] vertices = workspace.ExportVertices();
		int[] indices = workspace.ExportIndices();
		var result = new MeshBuildData(
			vertices,
			workspace.ExportNormals(),
			workspace.ExportColors(),
			indices,
			buildCollisionFaces
				? BuildCollisionFaces(vertices, indices)
				: Array.Empty<Vector3>());
		workspace.TrimOversizedBuffers();
		return result;
	}

	private static void BuildPositiveXFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			int maskIndex = 0;
			for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
			for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = x + 1 < VoxelConstants.ChunkSizeX
					? blocks[index + 1]
					: externalBlockSampler?.Invoke(x + 1, y, z) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.PositiveX,
				x,
				VoxelConstants.ChunkSizeZ,
				VoxelConstants.ChunkSizeY,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void BuildNegativeXFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			int maskIndex = 0;
			for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
			for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = x > 0
					? blocks[index - 1]
					: externalBlockSampler?.Invoke(x - 1, y, z) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.NegativeX,
				x,
				VoxelConstants.ChunkSizeZ,
				VoxelConstants.ChunkSizeY,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void BuildPositiveYFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
		{
			int maskIndex = 0;
			for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
			for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = y + 1 < VoxelConstants.ChunkSizeY
					? blocks[index + VoxelConstants.ChunkSizeX]
					: externalBlockSampler?.Invoke(x, y + 1, z) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.PositiveY,
				y,
				VoxelConstants.ChunkSizeX,
				VoxelConstants.ChunkSizeZ,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void BuildNegativeYFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
		{
			int maskIndex = 0;
			for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
			for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = y > 0
					? blocks[index - VoxelConstants.ChunkSizeX]
					: externalBlockSampler?.Invoke(x, y - 1, z) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.NegativeY,
				y,
				VoxelConstants.ChunkSizeX,
				VoxelConstants.ChunkSizeZ,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void BuildPositiveZFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		int zStride = VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY;
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		{
			int maskIndex = 0;
			for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
			for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = z + 1 < VoxelConstants.ChunkSizeZ
					? blocks[index + zStride]
					: externalBlockSampler?.Invoke(x, y, z + 1) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.PositiveZ,
				z,
				VoxelConstants.ChunkSizeX,
				VoxelConstants.ChunkSizeY,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void BuildNegativeZFaces(
		ReadOnlySpan<BlockType> blocks,
		Func<int, int, int, BlockType>? externalBlockSampler,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		bool useGreedyMerging,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		int zStride = VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY;
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		{
			int maskIndex = 0;
			for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
			for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
			{
				int index = LinearIndex(x, y, z);
				BlockType neighbor = z > 0
					? blocks[index - zStride]
					: externalBlockSampler?.Invoke(x, y, z - 1) ?? BlockType.Air;
				mask[maskIndex++] = VisibleBlock(blocks[index], neighbor);
			}

			EmitMask(
				FaceDirection.NegativeZ,
				z,
				VoxelConstants.ChunkSizeX,
				VoxelConstants.ChunkSizeY,
				useGreedyMerging,
				palette,
				localVertexWarp,
				workspace);
		}
	}

	private static void EmitMask(
		FaceDirection direction,
		int layer,
		int dimA,
		int dimB,
		bool useGreedyMerging,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		MesherWorkspace workspace)
	{
		byte[] mask = workspace.Mask;
		if (!useGreedyMerging)
		{
			for (int b = 0; b < dimB; b++)
			for (int a = 0; a < dimA; a++)
			{
				byte maskValue = mask[a + b * dimA];
				if (maskValue != EmptyMaskValue)
					EmitFace(direction, layer, a, b, 1, 1, (BlockType)maskValue, palette, localVertexWarp, workspace);
			}
			return;
		}

		for (int b = 0; b < dimB; b++)
		{
			for (int a = 0; a < dimA;)
			{
				int maskStart = a + b * dimA;
				byte maskValue = mask[maskStart];
				if (maskValue == EmptyMaskValue)
				{
					a++;
					continue;
				}

				int width = 1;
				while (a + width < dimA && mask[maskStart + width] == maskValue)
					width++;

				int height = 1;
				bool blocked = false;
				while (b + height < dimB && !blocked)
				{
					int rowStart = a + (b + height) * dimA;
					for (int k = 0; k < width; k++)
					{
						if (mask[rowStart + k] == maskValue)
							continue;
						blocked = true;
						break;
					}
					if (!blocked)
						height++;
				}

				EmitFace(
					direction,
					layer,
					a,
					b,
					width,
					height,
					(BlockType)maskValue,
					palette,
					localVertexWarp,
					workspace);

				for (int dy = 0; dy < height; dy++)
					Array.Clear(mask, a + (b + dy) * dimA, width);
				a += width;
			}
		}
	}

	private static void EmitFace(
		FaceDirection direction,
		int layer,
		int a,
		int b,
		int width,
		int height,
		BlockType blockType,
		in VoxelBlockPalette palette,
		Func<Vector3, Vector3>? localVertexWarp,
		MesherWorkspace workspace)
	{
		Vector3 origin;
		Vector3 uVector;
		Vector3 vVector;
		Vector3 normal;
		switch (direction)
		{
			case FaceDirection.PositiveX:
				origin = new Vector3(layer + 1, b, a);
				uVector = new Vector3(0, 0, width);
				vVector = new Vector3(0, height, 0);
				normal = Vector3.Right;
				break;
			case FaceDirection.NegativeX:
				origin = new Vector3(layer, b, a + width);
				uVector = new Vector3(0, 0, -width);
				vVector = new Vector3(0, height, 0);
				normal = Vector3.Left;
				break;
			case FaceDirection.PositiveY:
				origin = new Vector3(a, layer + 1, b);
				uVector = new Vector3(width, 0, 0);
				vVector = new Vector3(0, 0, height);
				normal = Vector3.Up;
				break;
			case FaceDirection.NegativeY:
				origin = new Vector3(a, layer, b + height);
				uVector = new Vector3(width, 0, 0);
				vVector = new Vector3(0, 0, -height);
				normal = Vector3.Down;
				break;
			case FaceDirection.PositiveZ:
				origin = new Vector3(a + width, b, layer + 1);
				uVector = new Vector3(-width, 0, 0);
				vVector = new Vector3(0, height, 0);
				normal = Vector3.Back;
				break;
			default:
				origin = new Vector3(a, b, layer);
				uVector = new Vector3(width, 0, 0);
				vVector = new Vector3(0, height, 0);
				normal = Vector3.Forward;
				break;
		}

		workspace.AddQuad(
			origin,
			uVector,
			vVector,
			normal,
			palette.GetColor(blockType),
			VoxelConstants.VoxelSizeMeters,
			localVertexWarp);
	}

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static byte VisibleBlock(BlockType current, BlockType neighbor) =>
		current != BlockType.Air && neighbor == BlockType.Air
			? (byte)current
			: EmptyMaskValue;

	[MethodImpl(MethodImplOptions.AggressiveInlining)]
	private static int LinearIndex(int x, int y, int z) =>
		x + VoxelConstants.ChunkSizeX * (y + VoxelConstants.ChunkSizeY * z);

	private static Vector3[] BuildCollisionFaces(Vector3[] vertices, int[] indices)
	{
		var faces = new Vector3[indices.Length];
		for (int i = 0; i < indices.Length; i++)
			faces[i] = vertices[indices[i]];
		return faces;
	}

	private sealed class MesherWorkspace
	{
		private Vector3[] _vertices = new Vector3[InitialVertexCapacity];
		private Vector3[] _normals = new Vector3[InitialVertexCapacity];
		private Color[] _colors = new Color[InitialVertexCapacity];
		private int[] _indices = new int[InitialIndexCapacity];
		private int _vertexCount;
		private int _indexCount;

		public byte[] Mask { get; } = new byte[Math.Max(
			VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeY,
			Math.Max(
				VoxelConstants.ChunkSizeX * VoxelConstants.ChunkSizeZ,
				VoxelConstants.ChunkSizeY * VoxelConstants.ChunkSizeZ))];

		public void Reset()
		{
			_vertexCount = 0;
			_indexCount = 0;
		}

		public void AddQuad(
			Vector3 origin,
			Vector3 uVector,
			Vector3 vVector,
			Vector3 normal,
			Color color,
			float voxelScale,
			Func<Vector3, Vector3>? localVertexWarp)
		{
			EnsureVertexCapacity(_vertexCount + 4);
			EnsureIndexCapacity(_indexCount + 6);

			Vector3 scaledOrigin = origin * voxelScale;
			Vector3 scaledU = uVector * voxelScale;
			Vector3 scaledV = vVector * voxelScale;
			Vector3 v0 = scaledOrigin;
			Vector3 v1 = scaledOrigin + scaledU;
			Vector3 v2 = scaledOrigin + scaledU + scaledV;
			Vector3 v3 = scaledOrigin + scaledV;
			if (localVertexWarp != null)
			{
				v0 = localVertexWarp(v0);
				v1 = localVertexWarp(v1);
				v2 = localVertexWarp(v2);
				v3 = localVertexWarp(v3);
			}

			int start = _vertexCount;
			_vertices[start] = v0;
			_vertices[start + 1] = v1;
			_vertices[start + 2] = v2;
			_vertices[start + 3] = v3;
			_normals[start] = normal;
			_normals[start + 1] = normal;
			_normals[start + 2] = normal;
			_normals[start + 3] = normal;
			_colors[start] = color;
			_colors[start + 1] = color;
			_colors[start + 2] = color;
			_colors[start + 3] = color;
			_vertexCount += 4;

			_indices[_indexCount] = start;
			_indices[_indexCount + 1] = start + 1;
			_indices[_indexCount + 2] = start + 2;
			_indices[_indexCount + 3] = start;
			_indices[_indexCount + 4] = start + 2;
			_indices[_indexCount + 5] = start + 3;
			_indexCount += 6;
		}

		public Vector3[] ExportVertices() => CopyExact(_vertices, _vertexCount);
		public Vector3[] ExportNormals() => CopyExact(_normals, _vertexCount);
		public Color[] ExportColors() => CopyExact(_colors, _vertexCount);
		public int[] ExportIndices() => CopyExact(_indices, _indexCount);

		public void TrimOversizedBuffers()
		{
			if (_vertices.Length > MaximumRetainedVertexCapacity)
			{
				_vertices = new Vector3[InitialVertexCapacity];
				_normals = new Vector3[InitialVertexCapacity];
				_colors = new Color[InitialVertexCapacity];
			}
			if (_indices.Length > MaximumRetainedIndexCapacity)
				_indices = new int[InitialIndexCapacity];
		}

		private void EnsureVertexCapacity(int required)
		{
			if (required <= _vertices.Length)
				return;
			int capacity = Math.Max(required, _vertices.Length * 2);
			Array.Resize(ref _vertices, capacity);
			Array.Resize(ref _normals, capacity);
			Array.Resize(ref _colors, capacity);
		}

		private void EnsureIndexCapacity(int required)
		{
			if (required <= _indices.Length)
				return;
			Array.Resize(ref _indices, Math.Max(required, _indices.Length * 2));
		}

		private static T[] CopyExact<T>(T[] source, int count)
		{
			if (count == 0)
				return Array.Empty<T>();
			var destination = new T[count];
			Array.Copy(source, destination, count);
			return destination;
		}
	}
}
