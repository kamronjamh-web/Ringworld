using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingScaledFarViewport
{
    /// <summary>Capture-only inspection. Never called by the per-frame telemetry path.</summary>
    internal object CaptureSettingsForDiagnostics() => new
    {
        Enabled, CoordinateScale, ResolutionScale, AroundSegments, AcrossSegments, RingWidthMeters,
        RegionalFarLodsEnabled, IntermediateRegionalLodTiersEnabled, FadeStartMeters, FadeEndMeters,
        KeepLocalTerrainFallbackVisible, CoupleFarWaterCutoutToTerrain, FarWaterCutoutMeters,
        MinimumLocalTerrainCutoutMeters, CurrentTerrainFallbackCutoutMeters,
        adaptiveGeometry = new
        {
            AdaptiveGeometryEnabled, GeometryTargetPixelError, GeometryMaxAngularSegments,
            GeometrySectorCount, GeometryRefinementHysteresis, GeometryMaxVertices,
            GeometryUploadsPerFrame, GeometryUploadBudgetMilliseconds,
            ActiveGeometryAngularSegments, ActiveGlobalAroundSegments, ActiveGlobalAcrossSegments,
            ActiveGlobalVertexCount, ActiveRegionalVertexCount, ActiveGlobalTerrainSectorCount,
            ActiveTerrainSectorCount, PendingTerrainSectorCount, VisibleTerrainSectorCount,
            GlobalGeometryReady
        },
        surfaceTiles = new
        {
            FarSurfaceTilesEnabled, FarSurfaceTileResolution, FarSurfaceTileCpuBudgetMb,
            FarSurfaceTileGpuBudgetMb, FarSurfaceTileMaximumJobs, FarSurfaceTileUploadsPerFrame,
            FarSurfaceTileUploadBudgetMilliseconds, SurfaceTileResidentCount, SurfaceTilePendingCount,
            SurfaceTileCpuBytes, SurfaceTileGpuBytes, GlobalTerrainTilesReady
        },
        BuildStructuralUnderside, BuildTerrainRoofUnderside, BuildLowerHullCavityFloor,
        LocalVolumetricCloudsEnabled, FarCloudClipmapEnabled, FarCloudPatchCullingEnabled, FarCloudCoverage, FarCloudOpacity,
        CloudTransmittanceMaskEnabled, cloudClipmap = GetFarCloudClipmapDiagnostics(),
        outerMeshes = CaptureMeshRows(OuterViewportForDiagnostics),
        regionalMeshes = CaptureMeshRows(RegionalViewportForDiagnostics),
        note = "Capture-time resident mesh rows, not GPU draw counts. Visibility can still be limited by frustum/material discards."
    };

    private static object[] CaptureMeshRows(Node? root)
    {
        var rows = new List<object>();
        if (root == null) return rows.ToArray();
        var pending = new Stack<Node>(); pending.Push(root);
        while (pending.Count > 0)
        {
            Node node = pending.Pop();
            if (node is MeshInstance3D instance)
            {
                Mesh? mesh = instance.Mesh;
                long? triangles = mesh is ArrayMesh ? 0 : null;
                if (mesh is ArrayMesh array)
                    for (int surface = 0; surface < array.GetSurfaceCount(); surface++)
                        if (array.SurfaceGetPrimitiveType(surface) == Mesh.PrimitiveType.Triangles)
                            triangles += (array.SurfaceGetArrayIndexLen(surface) > 0
                                ? array.SurfaceGetArrayIndexLen(surface) : array.SurfaceGetArrayLen(surface)) / 3;
                rows.Add(new { name = instance.Name.ToString(), visible = instance.IsVisibleInTree(), layers = instance.Layers,
                    shadowMode = instance.CastShadow.ToString(), surfaces = mesh?.GetSurfaceCount(), triangles });
            }
            foreach (Node child in node.GetChildren()) pending.Push(child);
        }
        return rows.ToArray();
    }
}
