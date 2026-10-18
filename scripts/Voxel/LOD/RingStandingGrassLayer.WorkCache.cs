using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    /// <summary>Reuses whole-tile visibility and prepared compute requests while their inputs are unchanged.</summary>
    [Export] public bool CacheGrassPresentation { get; set; } = true;
    public long GrassVisibilityCacheHits { get; private set; }
    public long GrassVisibilityScans { get; private set; }
    public long GrassGpuRequestCacheHits { get; private set; }
    public long GrassGpuRequestScans { get; private set; }
    public long GrassPresentationCacheHits { get; private set; }
    public long GrassPresentationScans { get; private set; }

    private long _grassWorkRevision;
    private GrassVisibilityWorkKey? _visibilityWorkKey;
    private GrassGpuWorkKey? _gpuWorkKey;
    private bool _gpuRegistrationComplete;
    private long _lastGrassGpuCompletedUpdates = -1;
    private GrassTransformWorkKey? _transformWorkKey;
    private readonly HashSet<GrassPresentationRenderer> _nonOwnedGrassRenderers = new();
    // Local notifications cannot observe motion of an external renderer parent.
    // Keep the normal snapshot path active until every output is owned again.
    private bool CanCacheGrassPresentation => CacheGrassPresentation && _nonOwnedGrassRenderers.Count == 0;

    internal void TrackGrassPresentationOwnership(GrassPresentationRenderer renderer, bool nonOwned)
    {
        bool changed = nonOwned ? _nonOwnedGrassRenderers.Add(renderer) : _nonOwnedGrassRenderers.Remove(renderer);
        if (changed) InvalidateGrassWork();
    }

    // Mutations of resident membership/data/output resources and renderer
    // notifications invalidate the layer in constant time. No resident scan is
    // needed to establish whether the previous visibility decisions still apply.
    internal void InvalidateGrassWork()
    {
        _grassWorkRevision++;
        _gpuRegistrationComplete = false;
    }

    private readonly record struct GrassPresentationSettings(bool Enabled, bool Visible, bool Suspended,
        bool CullTiles, bool FrustumCull, bool DistanceLod, bool Coordinated, float NearDistance, float FarDistance,
        float Transition, float NearDensity, float FarDensity, float Padding,
        bool ReducedNear, bool SingleRibbon, bool Cards, RingGrassCardComputeParameters? CardCompute);

    private GrassPresentationSettings CaptureGrassPresentationSettings() => new(Enabled, IsVisibleInTree(),
        StreamingSuspended, CullGrassTiles, GpuFrustumCulling, EnableDistanceLod, EnableCoordinatedGrassHandoff && EnableGrassAppearance,
        LodNearDistanceMeters, EffectiveGrassDrawDistance(), LodTransitionWidthMeters,
        LodNearDensity, LodFarDensity, GrassFrustumPaddingMeters,
        UseReducedNearGrassGeometry, IsSingleRibbonIntermediateGrassActive, _style.IsCards,
        _style.IsCards ? CardComputeFrame() : null);

    // Every grass culling consumer reads the latched camera (see CullingCamera.cs), not the live one, so caches keyed on it hold between re-latches.
    private GpuCameraState CaptureGrassCameraState(Camera3D camera) => LatchedGrassCameraState(camera);

    private readonly record struct GrassVisibilityWorkKey(long Revision, RingLodGeometrySnapshot Geometry,
        Transform3D Layer, GpuCameraState? Camera, RingCanonicalPosition? Focus, long Circumference,
        GrassPresentationSettings Settings);

    private GrassVisibilityWorkKey CaptureGrassVisibilityWorkKey(RingLodGeometrySnapshot geometry,
        RingCanonicalPosition focus, long circumference, Camera3D? camera) => new(_grassWorkRevision,
        geometry, GlobalTransform, camera == null ? null : CaptureGrassCameraState(camera),
        camera == null ? focus : null, circumference, CaptureGrassPresentationSettings());

    private readonly record struct GrassGpuWorkKey(long Revision, GrassVisibilityWorkKey? Visibility,
        GpuCameraState Camera, GrassPresentationSettings Settings, RingGpuVegetationCompute Backend);
    private GrassGpuWorkKey CaptureGrassGpuWorkKey(Camera3D camera) => new(_grassWorkRevision,
        _visibilityWorkKey, CaptureGrassCameraState(camera), CaptureGrassPresentationSettings(), _gpuCompute!);

    // Shared weather animates the global field without per-output writes. The
    // fallback's simulation clock stays in this key so its moving UVs continue
    // to be refreshed, including deterministic weather fixtures.
    private readonly record struct GrassTransformWorkKey(long Revision, GrassVisibilityWorkKey? Visibility,
        Transform3D Layer, RingLodGeometrySnapshot Geometry, GrassPresentationSettings Settings,
        float GustScale, double LocalGustTime, bool SharedWind);
    private GrassTransformWorkKey CaptureGrassTransformWorkKey(RingLodGeometrySnapshot geometry) => new(
        _grassWorkRevision, _visibilityWorkKey, GlobalTransform, geometry, CaptureGrassPresentationSettings(),
        GrassGustScaleMeters, _grassGustState.SimulationTime, UsesSharedWindField);
}
