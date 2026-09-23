using System;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Loads a KayKit glTF scene on the main thread and converts its first mesh to
/// a unit-height mesh whose local base rests at Y = 0. The source mesh's
/// surface layout, materials, normals, UVs, and indices are retained.
/// </summary>
public static class KayKitTreeMeshLoader
{
	/// <summary>
	/// Loads a glTF PackedScene, finds its first MeshInstance3D recursively, and
	/// returns a normalized copy of its mesh. Call this from Godot's main thread:
	/// ResourceLoader and scene instantiation are not worker-thread operations.
	/// </summary>
	public static Mesh? LoadNormalizedMesh(string resourcePath)
	{
		PackedScene? scene = ResourceLoader.Load<PackedScene>(resourcePath);
		if (scene == null)
		{
			GD.PushWarning($"Unable to load KayKit tree scene: {resourcePath}");
			return null;
		}

		Node root = scene.Instantiate();
		try
		{
			MeshInstance3D? instance = FindMeshInstance(root);
			if (instance?.Mesh == null)
			{
				GD.PushWarning($"No MeshInstance3D found in KayKit tree scene: {resourcePath}");
				return null;
			}

			return NormalizeMesh(instance.Mesh, GetTransformFromRoot(root, instance));
		}
		finally
		{
			root.Free();
		}
	}

	/// <summary>Recursively finds the first mesh instance in a loaded scene.</summary>
	public static MeshInstance3D? FindMeshInstance(Node node)
	{
		if (node is MeshInstance3D meshInstance && meshInstance.Mesh != null)
			return meshInstance;
		foreach (Node child in node.GetChildren())
		{
			MeshInstance3D? found = FindMeshInstance(child);
			if (found != null) return found;
		}
		return null;
	}

	private static Transform3D GetTransformFromRoot(Node root, Node node)
	{
		Transform3D transform = Transform3D.Identity;
		for (Node? current = node; current != null; current = current.GetParent())
		{
			if (current is Node3D spatial) transform = spatial.Transform * transform;
			if (current == root) break;
		}
		return transform;
	}

	/// <summary>
	/// Copies each source surface while scaling all axes by the source height and
	/// remapping vertex Y so the mesh bounds start at zero and span one unit.
	/// </summary>
	public static Mesh? NormalizeMesh(Mesh source) => NormalizeMesh(source, Transform3D.Identity);

	/// <summary>
	/// Bakes a scene-local transform into a mesh before normalizing its vertical
	/// bounds. This retains a glTF node's authored placement and rotation.
	/// </summary>
	public static Mesh? NormalizeMesh(Mesh source, Transform3D bakeTransform)
	{
		if (!TryGetBakedYBounds(source, bakeTransform, out float minimumY, out float maximumY))
		{
			GD.PushWarning("KayKit tree mesh has no vertices and cannot be normalized.");
			return null;
		}
		float height = maximumY - minimumY;
		if (height <= Mathf.Epsilon)
		{
			GD.PushWarning("KayKit tree mesh has zero vertical extent and cannot be normalized.");
			return null;
		}

		var normalized = new ArrayMesh();
		for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
		{
			Godot.Collections.Array sourceArrays = source.SurfaceGetArrays(surface);
			var arrays = new Godot.Collections.Array();
			foreach (Variant value in sourceArrays) arrays.Add(value);

			Vector3[] vertices = sourceArrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			var normalizedVertices = new Vector3[vertices.Length];
			for (int vertexIndex = 0; vertexIndex < vertices.Length; vertexIndex++)
			{
				Vector3 vertex = bakeTransform * vertices[vertexIndex];
				normalizedVertices[vertexIndex] = new Vector3(vertex.X / height, (vertex.Y - minimumY) / height, vertex.Z / height);
			}
			arrays[(int)Mesh.ArrayType.Vertex] = normalizedVertices;
			if (sourceArrays[(int)Mesh.ArrayType.Normal].VariantType != Variant.Type.Nil)
			{
				Vector3[] normals = sourceArrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
				var bakedNormals = new Vector3[normals.Length];
				Basis normalTransform = bakeTransform.Basis.Inverse().Transposed();
				for (int normalIndex = 0; normalIndex < normals.Length; normalIndex++)
					bakedNormals[normalIndex] = (normalTransform * normals[normalIndex]).Normalized();
				arrays[(int)Mesh.ArrayType.Normal] = bakedNormals;
			}

			// KayKit glTF mesh surfaces are triangles. All array channels,
			// including indices, tangents, colors, and UVs, remain unchanged.
			normalized.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
			normalized.SurfaceSetMaterial(surface, source.SurfaceGetMaterial(surface));
		}
		return normalized;
	}

	private static bool TryGetBakedYBounds(Mesh source, Transform3D bakeTransform, out float minimumY, out float maximumY)
	{
		minimumY = float.PositiveInfinity;
		maximumY = float.NegativeInfinity;
		for (int surface = 0; surface < source.GetSurfaceCount(); surface++)
		{
			Vector3[] vertices = source.SurfaceGetArrays(surface)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
			foreach (Vector3 sourceVertex in vertices)
			{
				float y = (bakeTransform * sourceVertex).Y;
				minimumY = Mathf.Min(minimumY, y);
				maximumY = Mathf.Max(maximumY, y);
			}
		}
		return !float.IsPositiveInfinity(minimumY);
	}
}
