using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo;

/// <summary>Focused regression check for the reusable standing-grass prototype.</summary>
public partial class RingGrassMeshValidation : RefCounted
{
    public bool Run()
    {
        try
        {
            ValidateIndexedVariants();
            Mesh mesh = RingGrassMeshFactory.Create();
            Vector3[] vertices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] normals = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector2[] uvs = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            int[] indices = mesh.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array();
            Require(indices.Length == 4 * 6 && vertices.Length == 10, "unexpected default blade triangle/vertex count");
            Require(uvs.Length == vertices.Length && normals.Length == vertices.Length, "blade attributes were not emitted");

            bool foundRoot = false;
            bool foundNonDegenerateTip = false;
            float minY = float.PositiveInfinity;
            float maxY = float.NegativeInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector3 vertex = vertices[i];
                minY = Mathf.Min(minY, vertex.Y);
                maxY = Mathf.Max(maxY, vertex.Y);
                foundRoot |= Mathf.IsZeroApprox(vertex.Y);
                Require(vertex.Y >= -.00001f && vertex.Y <= 1.00001f, "prototype escaped normalized height");
                Require(uvs[i].X >= 0 && uvs[i].X <= 1 && uvs[i].Y >= 0 && uvs[i].Y <= 1, "UV escaped normalized range");
            }
            Require(foundRoot && Mathf.IsZeroApprox(minY), "no pinned root vertices");
            Require(Mathf.IsEqualApprox(maxY, 1.0f), "prototype top is not normalized");

            for (int triangle = 0; triangle < indices.Length; triangle += 3)
            {
                float doubledArea = (vertices[indices[triangle + 1]] - vertices[indices[triangle]])
                    .Cross(vertices[indices[triangle + 2]] - vertices[indices[triangle]]).Length();
                Require(doubledArea > .000001f, "degenerate grass triangle");
                Vector3 godotFaceNormal = -(vertices[indices[triangle + 1]] - vertices[indices[triangle]])
                    .Cross(vertices[indices[triangle + 2]] - vertices[indices[triangle]]).Normalized();
                Require(godotFaceNormal.Dot(normals[indices[triangle]]) > .85f
                    && godotFaceNormal.Dot(normals[indices[triangle + 1]]) > .85f
                    && godotFaceNormal.Dot(normals[indices[triangle + 2]]) > .85f,
                    "blade normals do not match Godot clockwise winding");
                foundNonDegenerateTip |= vertices[indices[triangle]].Y > .99f
                    || vertices[indices[triangle + 1]].Y > .99f || vertices[indices[triangle + 2]].Y > .99f;
            }
            Require(foundNonDegenerateTip, "grass mesh has no usable tip geometry");
            Require(mesh.GetAabb().Size.X > .1f && mesh.GetAabb().Size.Z > .01f, "meadow blade bounds are too narrow");
            ValidateCoverageCluster(RingGrassMeshFactory.CreateIntermediate(), "intermediate");
            ValidateCoverageCluster(RingGrassMeshFactory.CreateFarCluster(), "far");
            Mesh legacy = RingGrassMeshFactory.CreateLegacyClump();
            Require(legacy.SurfaceGetArrays(0)[(int)Mesh.ArrayType.Index].AsInt32Array().Length == 3 * 4 * 6,
                "legacy clump compatibility mesh changed");
            ValidateCoordinateTransforms();
            return true;
        }
        catch (Exception exception)
        {
            GD.PushError("RING_GRASS_MESH=FAIL " + exception.Message);
            return false;
        }
    }

    private static void ValidateCoverageCluster(Mesh cluster, string label)
    {
        Godot.Collections.Array arrays = cluster.SurfaceGetArrays(0);
        Vector3[] vertices = arrays[(int)Mesh.ArrayType.Vertex].AsVector3Array();
        Vector3[] normals = arrays[(int)Mesh.ArrayType.Normal].AsVector3Array();
        Vector2[] uvs = arrays[(int)Mesh.ArrayType.TexUV].AsVector2Array();
        int[] indices = arrays[(int)Mesh.ArrayType.Index].AsInt32Array();
        Require(indices.Length == 4 * 3 && vertices.Length == 8, $"{label} cluster exceeded its four-triangle/eight-vertex budget");
        Require(normals.Length == vertices.Length && uvs.Length == vertices.Length,
            $"{label} cluster attributes were not emitted");
        bool root = false, tip = false;
        for (int i = 0; i < vertices.Length; i++)
        {
            root |= Mathf.IsZeroApprox(vertices[i].Y);
            tip |= Mathf.IsEqualApprox(vertices[i].Y, 1.0f);
            Require(vertices[i].Y >= -.00001f && vertices[i].Y <= 1.00001f,
                $"{label} cluster escaped normalized height");
            Require(uvs[i].X >= 0 && uvs[i].X <= 1 && uvs[i].Y >= 0 && uvs[i].Y <= 1,
                $"{label} cluster UV escaped normalized range");
        }
        Require(root && tip, $"{label} cluster is not pinned and normalized");
        Require(cluster.GetAabb().Size.X > .1f && cluster.GetAabb().Size.Z > .1f,
            $"{label} cluster does not provide crossed-card coverage");
        for (int triangle = 0; triangle < indices.Length; triangle += 3)
        {
            Vector3 cross = (vertices[indices[triangle + 1]] - vertices[indices[triangle]])
                .Cross(vertices[indices[triangle + 2]] - vertices[indices[triangle]]);
            Require(cross.Length() > .000001f, $"{label} cluster contains a degenerate triangle");
            Vector3 godotFaceNormal = -cross.Normalized();
            Require(godotFaceNormal.Dot(normals[indices[triangle]]) > .85f
                && godotFaceNormal.Dot(normals[indices[triangle + 1]]) > .85f
                && godotFaceNormal.Dot(normals[indices[triangle + 2]]) > .85f,
                $"{label} cluster normals do not match Godot clockwise winding");
        }
    }

    private static void ValidateIndexedVariants()
    {
        // These include the adopted gameplay near/mid geometry and every
        // compatibility/far prototype; each reference uses identical authoring.
        foreach (int segments in new[] { 2, 4, 12 })
        {
            ValidateIndexed(RingGrassMeshFactory.Create(segments), RingGrassMeshFactory.Create(segments, indexed: false),
                segments * 2, (segments + 1) * 2, $"blade/{segments}");
            ValidateIndexed(RingGrassMeshFactory.CreateClump(segments), RingGrassMeshFactory.CreateClump(segments, indexed: false),
                segments * 8, (segments + 1) * 8, $"near clump/{segments}");
            ValidateIndexed(RingGrassMeshFactory.CreateLegacyClump(segments), RingGrassMeshFactory.CreateLegacyClump(segments, indexed: false),
                segments * 6, (segments + 1) * 6, $"legacy/{segments}");
        }
        ValidateIndexed(RingGrassMeshFactory.CreateIntermediate(), RingGrassMeshFactory.CreateIntermediate(indexed: false), 4, 8, "intermediate");
        ValidateIndexed(RingGrassMeshFactory.CreateIntermediateClump(), RingGrassMeshFactory.CreateIntermediateClump(indexed: false), 16, 32, "crossed mid clump");
        ValidateIndexed(RingGrassMeshFactory.CreateIntermediateClump(true), RingGrassMeshFactory.CreateIntermediateClump(true, indexed: false), 8, 16, "single mid clump");
        ValidateIndexed(RingGrassMeshFactory.CreateFarClump(), RingGrassMeshFactory.CreateFarClump(indexed: false), 16, 32, "far clump");
        ValidateIndexed(RingGrassMeshFactory.CreateFarCluster(), RingGrassMeshFactory.CreateFarCluster(indexed: false), 4, 8, "far cluster");
    }

    private static void ValidateIndexed(Mesh indexed, Mesh reference, int triangles, int uniqueVertices, string label)
    {
        using (indexed)
        using (reference)
        {
            Godot.Collections.Array actual = indexed.SurfaceGetArrays(0);
            Godot.Collections.Array expected = reference.SurfaceGetArrays(0);
            Vector3[] vertices = actual[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] referenceVertices = expected[(int)Mesh.ArrayType.Vertex].AsVector3Array();
            Vector3[] normals = actual[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector3[] referenceNormals = expected[(int)Mesh.ArrayType.Normal].AsVector3Array();
            Vector2[] uvs = actual[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            Vector2[] referenceUvs = expected[(int)Mesh.ArrayType.TexUV].AsVector2Array();
            float[] anchors = actual[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            float[] referenceAnchors = expected[(int)Mesh.ArrayType.Custom0].AsFloat32Array();
            int[] indices = actual[(int)Mesh.ArrayType.Index].AsInt32Array();
            Require(expected[(int)Mesh.ArrayType.Index].VariantType == Variant.Type.Nil
                || expected[(int)Mesh.ArrayType.Index].AsInt32Array().Length == 0, $"{label} reference unexpectedly indexed");
            Require(referenceVertices.Length == triangles * 3 && indices.Length == referenceVertices.Length,
                $"{label} indexing changed the triangle budget");
            Require(vertices.Length == uniqueVertices && vertices.Length < referenceVertices.Length,
                $"{label} unique vertex budget changed");
            Require(normals.Length == vertices.Length && uvs.Length == vertices.Length && anchors.Length == vertices.Length * 4,
                $"{label} indexed attributes missing");
            Require(indexed.GetAabb() == reference.GetAabb(), $"{label} indexing changed bounds");
            // COLOR belongs to each MultiMesh instance, not this prototype.
            Require(actual[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil
                && expected[(int)Mesh.ArrayType.Color].VariantType == Variant.Type.Nil,
                $"{label} indexing changed the mesh color channel");
            for (int corner = 0; corner < indices.Length; corner++)
            {
                int vertex = indices[corner];
                Require(vertex >= 0 && vertex < vertices.Length, $"{label} invalid triangle index");
                Require(vertices[vertex] == referenceVertices[corner] && normals[vertex] == referenceNormals[corner]
                    && uvs[vertex] == referenceUvs[corner], $"{label} indexing changed ordered triangle attributes");
                for (int component = 0; component < 4; component++)
                    Require(anchors[vertex * 4 + component] == referenceAnchors[corner * 4 + component],
                        $"{label} indexing changed a source anchor/facing axis");
            }
        }
    }

    private static void ValidateCoordinateTransforms()
    {
        ValidateCoordinateTransform(
            (long)CertifiedRingScale.EffectiveCircumferenceMeters,
            CertifiedRingScale.EffectiveRadiusMeters, CertifiedRingScale.RingWidthMeters);
        ValidateCoordinateTransform(
            (long)MegaRingScale.EffectiveCircumferenceMeters,
            MegaRingScale.EffectiveRadiusMeters, (int)MegaRingScale.RingWidthMeters);
    }

    private static void ValidateCoordinateTransform(long circumference, double radius, int width)
    {
        const double tileSize = 48.0;
        long tileCount = RingGrassCoordinates.AroundTileCount(circumference, tileSize);
        RingGrassTileBounds seam = RingGrassCoordinates.Bounds(tileCount - 1, width / 2 / (long)tileSize, tileSize, circumference, width);
        Require(seam.AroundMax == circumference && seam.AroundMax - seam.AroundMin > 0.0 && seam.AroundMax - seam.AroundMin <= tileSize,
            "partial seam tile bounds are not clamped");
        Require(RingGrassCoordinates.TileDistanceSquared(seam,
            new RingCanonicalPosition(0.0, 0.0, seam.AcrossCenter), circumference) <= .000001,
            "seam tile does not measure from its real wrapped edge");

        double anchorHeight = 137.25;
        var gridTransform = new Transform3D(new Basis(new Vector3(1, 2, -1).Normalized(), .31f), new Vector3(241, -19, 77));
        var anchor = new RingCanonicalPosition(seam.AroundCenter, anchorHeight, seam.AcrossCenter);
        var geometry = new RingLodGeometrySnapshot(gridTransform, Vector3.Zero, Vector3.Zero, radius, 0.0, 17.0,
            circumference, -1000.0, 1000.0, anchor);
        double around = RingGrassCoordinates.Canonicalize(seam.AroundCenter + 7.25, circumference);
        double surface = anchorHeight + 2.75;
        double across = seam.AcrossCenter + 5.5;
        Transform3D tile = RingGrassCoordinates.CreateTileWorldTransform(seam, anchorHeight, geometry);
        Transform3D blade = RingGrassCoordinates.CreateBladeLocalTransform(seam, anchorHeight, around, surface, across,
            .07f, .8f, .63f, geometry);
        Vector3 expected = geometry.CanonicalToWorld(around, surface, across);
        Require((tile * blade).Origin.DistanceTo(expected) < .03f,
            "tile/blade transform origin diverged from canonical geometry");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
