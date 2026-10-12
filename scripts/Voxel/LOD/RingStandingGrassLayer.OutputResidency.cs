using System;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private bool _presentationOutputCullingActive;

    private void PrepareGrassPresentation(RingLodGeometrySnapshot geometry, RingCanonicalPosition focus, long circumference)
    {
        using var diagnosticScope = RingPerformanceTelemetry.MeasureCpu("Grass output visibility");
        Camera3D? camera = GetViewport()?.GetCamera3D();
        UpdateAdaptiveGroups(geometry, focus, circumference, camera);
        var key = CaptureGrassVisibilityWorkKey(geometry, focus, circumference, camera);
        if (CanCacheGrassPresentation && _visibilityWorkKey == key)
        {
            GrassVisibilityCacheHits++;
            return;
        }
        GrassVisibilityScans++;
        InvalidateGrassWork();
        _presentationRevision++;
        _presentationLayerWorld = GlobalTransform;
        _presentationInverseLayer = _presentationLayerWorld.AffineInverse();
        _presentationOutputCullingActive = CullGrassTiles && camera != null;
        Vector3? cameraWorld = camera?.GlobalPosition;
        Plane[]? frustum = _presentationOutputCullingActive && GpuFrustumCulling
            ? CachedCameraFrustumPlanes(camera!) : null;
        GrassFrustumRejectedTileCount = GrassDistanceRejectedTileCount = 0;
        AdaptiveGrassVisibilityCheckCount = 0;
        foreach (var pair in _activeGrassPresentations)
        {
            AdaptiveGrassVisibilityCheckCount++;
            Resident resident = pair.Value;
            bool show = _wanted.Contains(pair.Key) && !StreamingSuspended;
            RefreshCanonicalTilePresentation(resident.Presentation, resident.Data, geometry);
            MultiMeshInstance3D? existing = resident.Renderer ?? resident.IntermediateRenderer;
            Transform3D world = existing != null && show
                ? RefreshTilePresentation(resident.Presentation, existing, resident.Data, geometry,
                    _presentationInverseLayer, resident.Renderer == null)
                : resident.Presentation.World;
            RefreshPresentationBounds(resident.Presentation, resident.Data, world, resident.MaximumSourceHeight);
            float distance = ConservativePresentationDistance(resident.Data, resident.Presentation.RawWorldBounds,
                cameraWorld, focus, circumference);
            float minimum = distance, maximum = distance;
            bool cull = show && _presentationOutputCullingActive;
            if (cull)
            {
                Aabb bounds = resident.Presentation.ExpandedWorldBounds;
                if (EnableDistanceLod)
                {
                    ConservativeAabbCameraDistances(bounds, cameraWorld!.Value, out minimum, out maximum);
                    if (minimum > LodFarDistanceMeters + LodTransitionWidthMeters * .5f)
                    {
                        show = false;
                        GrassDistanceRejectedTileCount++;
                    }
                }
                if (show && frustum != null && !AabbIntersectsFrustum(bounds, frustum))
                {
                    show = false;
                    GrassFrustumRejectedTileCount++;
                }
            }
            float halfTransition = LodTransitionWidthMeters * .5f;
            float slop = Math.Max(0, GrassFrustumPaddingMeters);
            bool near = show && (!EnableDistanceLod || distance <= LodNearDistanceMeters + halfTransition + slop);
            bool intermediate = show && EnableDistanceLod && distance <= LodFarDistanceMeters + halfTransition + slop;
            if (cull && EnableDistanceLod)
            {
                near = show && minimum <= LodNearDistanceMeters + halfTransition;
                intermediate = show && maximum >= LodNearDistanceMeters - halfTransition
                    && minimum <= LodFarDistanceMeters + halfTransition;
            }
            if (IsAdaptiveGroup(resident)) near = false;
            if (!resident.NearWanted && near) resident.NearVisibilityRevision++;
            if (!resident.IntermediateWanted && intermediate) resident.IntermediateVisibilityRevision++;
            resident.NearWanted = near;
            resident.IntermediateWanted = intermediate;
            resident.PresentationPrefix = VisiblePrefix(resident.Data.InstanceCount, distance);
            if (_presentationOutputCullingActive)
            {
                if (!near) RetireGrassOutput(resident, false);
                if (!intermediate) RetireGrassOutput(resident, true);
            }
            bool needsNear = resident.Renderer == null && (!_presentationOutputCullingActive || near);
            bool needsIntermediate = EnableDistanceLod && resident.IntermediateRenderer == null
                && (!_presentationOutputCullingActive || intermediate);
            if (IsAdaptiveGroup(resident))
            {
                if (needsIntermediate) QueueAdaptiveOutput(resident.Group!);
            }
            else if ((needsNear || needsIntermediate) && _uploadQueued.Add(pair.Key)) _uploads.Enqueue(pair.Key);
        }
        PruneUploads();
        // Output retirement and canonical transform writes may themselves have
        // advanced the mutation revision while preparing this completed result.
        _visibilityWorkKey = CaptureGrassVisibilityWorkKey(geometry, focus, circumference, camera);
    }

    private void RetireGrassOutput(Resident resident, bool intermediate)
    {
        int batch = intermediate ? resident.GpuIntermediateBatch : resident.GpuNearBatch;
        if (batch >= 0) _gpuCompute?.RemoveBatch(batch);
        MultiMeshInstance3D? renderer = intermediate ? resident.IntermediateRenderer : resident.Renderer;
        if (batch >= 0 || renderer != null) InvalidateGrassWork();
        if (renderer != null)
        {
            SetRendererVisible(renderer, false);
            renderer.QueueFree();
        }
        RendererPresentationCache state = intermediate ? resident.Presentation.Intermediate : resident.Presentation.Near;
        state.Renderer = null;
        state.Mapping = null;
        state.GustUv = null;
        state.GpuCulled = null;
        state.SnapshotRevision = state.LocalRevision = -1;
        if (intermediate)
        {
            resident.IntermediateRenderer = null;
            resident.GpuIntermediateBatch = -1;
        }
        else
        {
            resident.Renderer = null;
            resident.GpuNearBatch = -1;
        }
    }
}
