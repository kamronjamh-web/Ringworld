using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Voxel.Phase1;

/// <summary>
/// Pilot smooth backend inspired by Voxel Tools' SDF/isosurface architecture.
/// Natural materials are polygonized while construction materials retain the
/// existing exact greedy-block path.
/// </summary>
public static class VoxelSmoothTerrainMesher
{
	private static readonly int[,] CubeCorners =
	{
		{ 0, 0, 0 }, { 1, 0, 0 }, { 1, 1, 0 }, { 0, 1, 0 },
		{ 0, 0, 1 }, { 1, 0, 1 }, { 1, 1, 1 }, { 0, 1, 1 }
	};

	private static readonly int[,] CubeTetrahedra =
	{
		{ 0, 5, 1, 6 }, { 0, 1, 2, 6 }, { 0, 2, 3, 6 },
		{ 0, 3, 7, 6 }, { 0, 7, 4, 6 }, { 0, 4, 5, 6 }
	};

	public static MeshBuildData BuildHybrid(
		VoxelChunkData source,
		VoxelBlockPalette palette,
		Func<int, int, int, BlockType>? externalBlockSampler,
		Func<Vector3, Vector3>? localVertexWarp,
		bool buildCollisionFaces,
		RingTerrainFineSdfHalo? fineSdfHalo = null)
	{
		MeshBuildData terrain = BuildNaturalSurface(
			source,
			palette,
			externalBlockSampler,
			localVertexWarp,
			buildCollisionFaces,
			fineSdfHalo);

		VoxelChunkData construction = FilterChunk(source, IsConstruction);
		MeshBuildData buildings = VoxelChunkMesher.Build(
			construction,
			palette,
			externalBlockSampler == null
				? null
				: (x, y, z) => Filter(externalBlockSampler(x, y, z), IsConstruction),
			localVertexWarp,
			useGreedyMerging: true,
			buildCollisionFaces: buildCollisionFaces);
		return Merge(terrain, buildings);
	}

	internal static MeshBuildData BuildNaturalSurface(
		VoxelChunkData source,
		VoxelBlockPalette palette,
		Func<int, int, int, BlockType>? externalBlockSampler = null,
		Func<Vector3, Vector3>? localVertexWarp = null,
		bool buildCollisionFaces = false,
		RingTerrainFineSdfHalo? fineSdfHalo = null)
	{
		if (fineSdfHalo != null
			&& VoxelChunkMesherNative.TryBuildSmoothRegularFineSdf(
				fineSdfHalo,
				palette,
				localVertexWarp,
				buildCollisionFaces,
				out MeshBuildData sdfMesh))
		{
			return sdfMesh;
		}
		if (VoxelChunkMesherNative.TryBuildSmoothRegular(
			source,
			palette,
			externalBlockSampler,
			localVertexWarp,
			buildCollisionFaces,
			out MeshBuildData nativeMesh))
		{
			return nativeMesh;
		}

		var vertices = new List<Vector3>(8192);
		var normals = new List<Vector3>(8192);
		var colors = new List<Color>(8192);
		var indices = new List<int>(12288);
		var vertexLookup = new Dictionary<QuantizedPosition, int>(8192);
		Span<Sample> samples = stackalloc Sample[8];
		Span<Sample> tetra = stackalloc Sample[4];

		for (int z = -1; z < VoxelConstants.ChunkSizeZ; z++)
		for (int y = -1; y < VoxelConstants.ChunkSizeY; y++)
		for (int x = -1; x < VoxelConstants.ChunkSizeX; x++)
		{
			int solidCount = 0;
			for (int corner = 0; corner < 8; corner++)
			{
				int sampleX = x + CubeCorners[corner, 0];
				int sampleY = y + CubeCorners[corner, 1];
				int sampleZ = z + CubeCorners[corner, 2];
				BlockType block = SampleBlock(source, externalBlockSampler, sampleX, sampleY, sampleZ);
				bool solid = IsNatural(block);
				if (solid) solidCount++;
				samples[corner] = new Sample(
					new Vector3(sampleX, sampleY, sampleZ),
					solid ? -1.0f : 1.0f,
					block);
			}
			if (solidCount is 0 or 8) continue;

			for (int tetrahedron = 0; tetrahedron < 6; tetrahedron++)
			{
				for (int corner = 0; corner < 4; corner++)
					tetra[corner] = samples[CubeTetrahedra[tetrahedron, corner]];
				PolygonizeTetrahedron(
					tetra,
					palette,
					localVertexWarp,
					vertices,
					normals,
					colors,
					indices,
					vertexLookup);
			}
		}

		Vector3[] vertexArray = vertices.ToArray();
		Vector3[] normalArray = normals.ToArray();
		for (int index = 0; index < normalArray.Length; index++)
			normalArray[index] = normalArray[index].LengthSquared() > 0.000001f
				? normalArray[index].Normalized()
				: Vector3.Up;
		int[] indexArray = indices.ToArray();
		return new MeshBuildData(
			vertexArray,
			normalArray,
			colors.ToArray(),
			indexArray,
			buildCollisionFaces ? ExpandCollisionFaces(vertexArray, indexArray) : Array.Empty<Vector3>());
	}

	private static void PolygonizeTetrahedron(
		Span<Sample> tetra,
		VoxelBlockPalette palette,
		Func<Vector3, Vector3>? warp,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Color> colors,
		List<int> indices,
		Dictionary<QuantizedPosition, int> vertexLookup)
	{
		Span<int> inside = stackalloc int[4];
		Span<int> outside = stackalloc int[4];
		int insideCount = 0;
		int outsideCount = 0;
		for (int index = 0; index < 4; index++)
		{
			if (tetra[index].Density < 0.0f) inside[insideCount++] = index;
			else outside[outsideCount++] = index;
		}
		if (insideCount is 0 or 4) return;

		Vector3 outward = AveragePosition(tetra, outside, outsideCount)
			- AveragePosition(tetra, inside, insideCount);
		Color color = AverageNaturalColor(tetra, inside, insideCount, palette);

		if (insideCount == 1 || insideCount == 3)
		{
			bool oneInside = insideCount == 1;
			int pivot = oneInside ? inside[0] : outside[0];
			Span<int> others = oneInside ? outside : inside;
			Vector3 a = Interpolate(tetra[pivot], tetra[others[0]], warp);
			Vector3 b = Interpolate(tetra[pivot], tetra[others[1]], warp);
			Vector3 c = Interpolate(tetra[pivot], tetra[others[2]], warp);
			AddTriangle(a, b, c, outward, color, vertices, normals, colors, indices, vertexLookup);
			return;
		}

		Vector3 p00 = Interpolate(tetra[inside[0]], tetra[outside[0]], warp);
		Vector3 p01 = Interpolate(tetra[inside[0]], tetra[outside[1]], warp);
		Vector3 p10 = Interpolate(tetra[inside[1]], tetra[outside[0]], warp);
		Vector3 p11 = Interpolate(tetra[inside[1]], tetra[outside[1]], warp);
		AddTriangle(p00, p01, p11, outward, color, vertices, normals, colors, indices, vertexLookup);
		AddTriangle(p00, p11, p10, outward, color, vertices, normals, colors, indices, vertexLookup);
	}

	private static void AddTriangle(
		Vector3 a,
		Vector3 b,
		Vector3 c,
		Vector3 outward,
		Color color,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Color> colors,
		List<int> indices,
		Dictionary<QuantizedPosition, int> vertexLookup)
	{
		Vector3 faceNormal = (b - a).Cross(c - a);
		if (faceNormal.LengthSquared() <= 0.0000001f) return;
		if (faceNormal.Dot(outward) < 0.0f)
		{
			(b, c) = (c, b);
			faceNormal = -faceNormal;
		}

		int ia = GetVertex(a, color, vertexLookup, vertices, normals, colors);
		int ib = GetVertex(b, color, vertexLookup, vertices, normals, colors);
		int ic = GetVertex(c, color, vertexLookup, vertices, normals, colors);
		normals[ia] += faceNormal;
		normals[ib] += faceNormal;
		normals[ic] += faceNormal;
		// Godot uses clockwise front faces; faceNormal remains outward.
		indices.Add(ia);
		indices.Add(ic);
		indices.Add(ib);
	}

	private static int GetVertex(
		Vector3 position,
		Color color,
		Dictionary<QuantizedPosition, int> lookup,
		List<Vector3> vertices,
		List<Vector3> normals,
		List<Color> colors)
	{
		var key = new QuantizedPosition(position);
		if (lookup.TryGetValue(key, out int existing)) return existing;
		int index = vertices.Count;
		lookup.Add(key, index);
		vertices.Add(position);
		normals.Add(Vector3.Zero);
		colors.Add(color);
		return index;
	}

	private static Vector3 Interpolate(in Sample a, in Sample b, Func<Vector3, Vector3>? warp)
	{
		float denominator = a.Density - b.Density;
		float weight = MathF.Abs(denominator) > 0.000001f ? a.Density / denominator : 0.5f;
		Vector3 position = a.Position.Lerp(b.Position, Mathf.Clamp(weight, 0.0f, 1.0f));
		return warp?.Invoke(position) ?? position;
	}

	private static BlockType SampleBlock(
		VoxelChunkData source,
		Func<int, int, int, BlockType>? external,
		int x,
		int y,
		int z)
	{
		if (source.IsInBounds(x, y, z)) return source.GetBlock(x, y, z);
		if (external != null) return external(x, y, z);

		return source.GetBlock(
			Math.Clamp(x, 0, VoxelConstants.ChunkSizeX - 1),
			Math.Clamp(y, 0, VoxelConstants.ChunkSizeY - 1),
			Math.Clamp(z, 0, VoxelConstants.ChunkSizeZ - 1));
	}

	private static VoxelChunkData FilterChunk(VoxelChunkData source, Func<BlockType, bool> predicate)
	{
		var filtered = new VoxelChunkData();
		for (int z = 0; z < VoxelConstants.ChunkSizeZ; z++)
		for (int y = 0; y < VoxelConstants.ChunkSizeY; y++)
		for (int x = 0; x < VoxelConstants.ChunkSizeX; x++)
		{
			BlockType block = source.GetBlock(x, y, z);
			filtered.SetBlock(x, y, z, Filter(block, predicate));
		}
		return filtered;
	}

	private static BlockType Filter(BlockType block, Func<BlockType, bool> predicate) =>
		predicate(block) ? block : BlockType.Air;

	private static bool IsNatural(BlockType block) =>
		block is BlockType.Soil or BlockType.Rock or BlockType.Bedrock;

	private static bool IsConstruction(BlockType block) =>
		block is BlockType.Concrete or BlockType.Glass;

	private static Vector3 AveragePosition(Span<Sample> samples, Span<int> indices, int count)
	{
		Vector3 sum = Vector3.Zero;
		for (int index = 0; index < count; index++) sum += samples[indices[index]].Position;
		return sum / count;
	}

	private static Color AverageNaturalColor(
		Span<Sample> samples,
		Span<int> indices,
		int count,
		VoxelBlockPalette palette)
	{
		Color sum = new(0, 0, 0, 0);
		for (int index = 0; index < count; index++) sum += palette.GetColor(samples[indices[index]].Block);
		return sum / count;
	}

	private static Vector3[] ExpandCollisionFaces(Vector3[] vertices, int[] indices)
	{
		var faces = new Vector3[indices.Length];
		for (int index = 0; index < indices.Length; index++) faces[index] = vertices[indices[index]];
		return faces;
	}

	private static MeshBuildData Merge(in MeshBuildData first, in MeshBuildData second)
	{
		if (!first.HasGeometry) return second;
		if (!second.HasGeometry) return first;
		var vertices = new Vector3[first.Vertices.Length + second.Vertices.Length];
		var normals = new Vector3[first.Normals.Length + second.Normals.Length];
		var colors = new Color[first.Colors.Length + second.Colors.Length];
		var indices = new int[first.Indices.Length + second.Indices.Length];
		Array.Copy(first.Vertices, vertices, first.Vertices.Length);
		Array.Copy(second.Vertices, 0, vertices, first.Vertices.Length, second.Vertices.Length);
		Array.Copy(first.Normals, normals, first.Normals.Length);
		Array.Copy(second.Normals, 0, normals, first.Normals.Length, second.Normals.Length);
		Array.Copy(first.Colors, colors, first.Colors.Length);
		Array.Copy(second.Colors, 0, colors, first.Colors.Length, second.Colors.Length);
		Array.Copy(first.Indices, indices, first.Indices.Length);
		for (int index = 0; index < second.Indices.Length; index++)
			indices[first.Indices.Length + index] = second.Indices[index] + first.Vertices.Length;

		var collision = new Vector3[first.CollisionFaces.Length + second.CollisionFaces.Length];
		Array.Copy(first.CollisionFaces, collision, first.CollisionFaces.Length);
		Array.Copy(second.CollisionFaces, 0, collision, first.CollisionFaces.Length, second.CollisionFaces.Length);
		return new MeshBuildData(vertices, normals, colors, indices, collision);
	}

	private readonly record struct Sample(Vector3 Position, float Density, BlockType Block);

	private readonly record struct QuantizedPosition(int X, int Y, int Z)
	{
		public QuantizedPosition(Vector3 position) : this(
			Mathf.RoundToInt(position.X * 4096.0f),
			Mathf.RoundToInt(position.Y * 4096.0f),
			Mathf.RoundToInt(position.Z * 4096.0f))
		{
		}
	}
}
