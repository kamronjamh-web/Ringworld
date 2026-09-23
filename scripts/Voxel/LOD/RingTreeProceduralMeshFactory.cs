using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

internal readonly record struct RingTreeMeshSet(Mesh Trunk, Mesh Canopy);

/// <summary>
/// Builds normalized, deterministic tree prototypes using Godot-managed mesh
/// APIs. Prototypes are generated once and then rendered exclusively through
/// MultiMesh; no native extension or per-tree scene nodes are involved.
/// </summary>
internal static class RingTreeProceduralMeshFactory
{
	public static RingTreeMeshSet Create(int seed, int radialSegments, int branchCount, bool addForks)
	{
		var random = new Random(seed);
		var trunk = new SurfaceTool();
		var canopy = new SurfaceTool();
		trunk.Begin(Mesh.PrimitiveType.Triangles);
		canopy.Begin(Mesh.PrimitiveType.Triangles);

		Vector3 trunkBase = Vector3.Zero;
		Vector3 trunkTop = new(0.018f, .84f, -.012f);
		AddTaperedSegment(trunk, trunkBase, trunkTop, .052f, .017f, radialSegments);
		var leafAnchors = new List<Vector3>(branchCount * (addForks ? 3 : 1) + 3);
		const float goldenAngle = 2.39996323f;

		for (int i = 0; i < branchCount; i++)
		{
			float t = branchCount == 1 ? 0 : i / (float)(branchCount - 1);
			float startHeight = Mathf.Lerp(.24f, .76f, t);
			float azimuth = i * goldenAngle + Lerp(random, -.22f, .22f);
			float length = Mathf.Lerp(.36f, .18f, t) * Lerp(random, .84f, 1.12f);
			Vector3 horizontal = new(Mathf.Cos(azimuth), 0, Mathf.Sin(azimuth));
			Vector3 direction = (horizontal + Vector3.Up * Lerp(random, .28f, .48f)).Normalized();
			Vector3 start = trunkTop * (startHeight / trunkTop.Y);
			Vector3 end = start + direction * length;
			float startRadius = Mathf.Lerp(.024f, .012f, t);
			AddTaperedSegment(trunk, start, end, startRadius, startRadius * .28f, Math.Max(3, radialSegments - 2));
			leafAnchors.Add(end);

			if (!addForks || i % 2 != 0) continue;
			for (int fork = -1; fork <= 1; fork += 2)
			{
				float forkAngle = azimuth + fork * Lerp(random, .50f, .78f);
				Vector3 forkHorizontal = new(Mathf.Cos(forkAngle), 0, Mathf.Sin(forkAngle));
				Vector3 forkDirection = (forkHorizontal + Vector3.Up * Lerp(random, .38f, .62f)).Normalized();
				Vector3 forkEnd = end + forkDirection * length * Lerp(random, .28f, .43f);
				AddTaperedSegment(trunk, end, forkEnd, startRadius * .28f, startRadius * .08f, Math.Max(3, radialSegments - 3));
				leafAnchors.Add(forkEnd);
			}
		}

		leafAnchors.Add(trunkTop + new Vector3(0, .075f, 0));
		leafAnchors.Add(trunkTop + new Vector3(.055f, .025f, -.035f));
		leafAnchors.Add(trunkTop + new Vector3(-.045f, .01f, .05f));
		for (int i = 0; i < leafAnchors.Count; i++)
		{
			float scale = Lerp(random, .82f, 1.16f);
			AddLeafCluster(canopy, leafAnchors[i], .105f * scale, .075f * scale);
		}

		return new RingTreeMeshSet(trunk.Commit(), canopy.Commit());
	}

	private static void AddTaperedSegment(SurfaceTool surface, Vector3 start, Vector3 end, float startRadius, float endRadius, int sides)
	{
		Vector3 axis = (end - start).Normalized();
		Vector3 reference = Mathf.Abs(axis.Dot(Vector3.Up)) > .92f ? Vector3.Right : Vector3.Up;
		Vector3 tangent = axis.Cross(reference).Normalized();
		Vector3 bitangent = tangent.Cross(axis).Normalized();
		for (int side = 0; side < sides; side++)
		{
			float a0 = Mathf.Tau * side / sides;
			float a1 = Mathf.Tau * (side + 1) / sides;
			Vector3 n0 = (tangent * Mathf.Cos(a0) + bitangent * Mathf.Sin(a0)).Normalized();
			Vector3 n1 = (tangent * Mathf.Cos(a1) + bitangent * Mathf.Sin(a1)).Normalized();
			Vector3 p00 = start + n0 * startRadius;
			Vector3 p01 = start + n1 * startRadius;
			Vector3 p10 = end + n0 * endRadius;
			Vector3 p11 = end + n1 * endRadius;
			float u0 = side / (float)sides;
			float u1 = (side + 1) / (float)sides;
			// Godot front faces are clockwise. Keep the supplied radial normals
			// outward while reversing the original counter-clockwise triangles.
			AddVertex(surface, p00, n0, new Vector2(u0, 0));
			AddVertex(surface, p11, n1, new Vector2(u1, 1));
			AddVertex(surface, p10, n0, new Vector2(u0, 1));
			AddVertex(surface, p00, n0, new Vector2(u0, 0));
			AddVertex(surface, p01, n1, new Vector2(u1, 0));
			AddVertex(surface, p11, n1, new Vector2(u1, 1));
		}
	}

	private static void AddLeafCluster(SurfaceTool surface, Vector3 center, float radius, float halfHeight)
	{
		Vector3 top = center + Vector3.Up * halfHeight;
		Vector3 bottom = center - Vector3.Up * halfHeight;
		Vector3[] ring =
		{
			center + Vector3.Right * radius,
			center + Vector3.Back * radius,
			center + Vector3.Left * radius,
			center + Vector3.Forward * radius
		};
		for (int i = 0; i < ring.Length; i++)
		{
			Vector3 current = ring[i];
			Vector3 next = ring[(i + 1) % ring.Length];
			AddFlatTriangle(surface, top, next, current);
			AddFlatTriangle(surface, bottom, current, next);
		}
	}

	private static void AddFlatTriangle(SurfaceTool surface, Vector3 a, Vector3 b, Vector3 c)
	{
		// Calculate the outward mathematical normal from the authored order,
		// then emit b/c reversed for Godot's clockwise front-face convention.
		Vector3 normal = (b - a).Cross(c - a).Normalized();
		AddVertex(surface, a, normal, new Vector2(.5f, 0));
		AddVertex(surface, c, normal, new Vector2(0, 1));
		AddVertex(surface, b, normal, new Vector2(1, 1));
	}

	private static void AddVertex(SurfaceTool surface, Vector3 position, Vector3 normal, Vector2 uv)
	{
		surface.SetNormal(normal);
		surface.SetUV(uv);
		surface.AddVertex(position);
	}

	private static float Lerp(Random random, float minimum, float maximum) =>
		minimum + (maximum - minimum) * (float)random.NextDouble();
}
