using System;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    private bool _presentationOutputCullingActive;
    private const float CardFrustumHoldMeters = 2f;

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
        bool coordinated = EnableCoordinatedGrassHandoff && EnableGrassAppearance;
        _presentationOutputCullingActive = (CullGrassTiles && camera != null) || coordinated;
        float farDistance = EffectiveGrassDrawDistance();
        float nearDistance = Mathf.Min(LodNearDistanceMeters, farDistance - .01f);
        bool cards = _style.IsCards;
        bool distanceLod = EnableDistanceLod && !cards;
        float halfTransition = distanceLod ? LodTransitionWidthMeters * .5f : 0f;
        Vector3? cameraWorld = camera?.GlobalPosition;
        Plane[]? frustum = CullGrassTiles && camera != null && GpuFrustumCulling
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
            bool cull = show && _presentationOutputCullingActive && cameraWorld != null;
            if (cull)
            {
                Aabb bounds = resident.Presentation.ExpandedWorldBounds;
                ConservativeAabbCameraDistances(bounds, cameraWorld!.Value, out minimum, out maximum);
                if (minimum > farDistance + halfTransition)
                {
                    show = false;
                    GrassDistanceRejectedTileCount++;
                }
                // Cards: a shown output is only retired once it is well outside the view (admit at the edge, retire CardFrustumHoldMeters
                // beyond it), so a sub-metre camera oscillation never retires and re-uploads edge tiles (streaming idle = 0).
                Aabb frustumBounds = cards && (resident.NearWanted || resident.IntermediateWanted) ? bounds.Grow(CardFrustumHoldMeters) : bounds;
                if (show && frustum != null && !AabbIntersectsFrustum(frustumBounds, frustum))
                {
                    show = false;
                    GrassFrustumRejectedTileCount++;
                }
            }
            if (show && _presentationOutputCullingActive && !cull && distance > farDistance + halfTransition)
            {
                show = false;
                GrassDistanceRejectedTileCount++;
            }
            float slop = Math.Max(0, GrassFrustumPaddingMeters);
            bool near = show && (!distanceLod || distance <= nearDistance + halfTransition + slop);
            bool intermediate = show && distanceLod && distance <= farDistance + halfTransition + slop;
            if (cull && distanceLod)
            {
                near = show && minimum <= nearDistance + halfTransition;
                intermediate = show && maximum >= nearDistance - halfTransition
                    && minimum <= farDistance + halfTransition;
            }
            if (IsAdaptiveGroup(resident)) { if (cards) intermediate = near; near = false; }
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
            bool needsIntermediate = (distanceLod || (cards && IsAdaptiveGroup(resident))) && resident.IntermediateRenderer == null
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
        state.TerrainOriginUv = null;
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
