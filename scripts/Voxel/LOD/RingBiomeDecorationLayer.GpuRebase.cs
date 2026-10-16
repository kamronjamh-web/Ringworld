using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    // A floating-origin anchor move changes only the world translation of the
    // canonical surface.  Rows stay valid when the layer follows that delta.
    // Shape/basis changes intentionally fall back to a full source rebuild.
    private bool TryRebaseGpuTreeSources(in RingLodGeometrySnapshot current)
    {
        if (_gpuTreeGeometry is not { } previous
            || previous == current
            || _gpuTreeLayerTransform != GlobalTransform
            || _gpuTreeRootEmbed != RootEmbedMeters
            || !TryGetGpuTreePureTranslationDelta(previous, current,
                _explorer?.CanonicalPosition ?? previous.FloatingOriginCanonicalAnchor
                    ?? current.FloatingOriginCanonicalAnchor ?? default, out Vector3 delta))
            return false;

        GlobalPosition += delta;
        _gpuTreeGeometry = current;
        _gpuTreeLayerTransform = GlobalTransform;
        InvalidateGpuTreeFrameCache();
        // Source rows are layer-local and the layer followed the delta, so nothing needs rebuilding. Only a plan
        // that was prepared against the old geometry is stale.
        if (TreePreparationPending)
        {
            CancelGpuTreeSourcePreparation();
            RefreshGpuTreeResidentSources(true);
        }
        return true;
    }

    internal static bool TryGetGpuTreePureTranslationDelta(in RingLodGeometrySnapshot previous,
        in RingLodGeometrySnapshot current, RingCanonicalPosition focus, out Vector3 delta)
    {
        delta = default;
        if (previous.GridGlobalTransform.Basis != current.GridGlobalTransform.Basis
            || previous.RingRadiusMeters != current.RingRadiusMeters
            || previous.CircumferenceMeters != current.CircumferenceMeters
            || previous.RingAcrossBaseOffsetMeters != current.RingAcrossBaseOffsetMeters
            || previous.RingVerticalBaseOffsetMeters != current.RingVerticalBaseOffsetMeters)
            return false;

        // Use the same canonical point in both snapshots. CanonicalToWorld
        // performs its large-coordinate subtraction in double precision.
        delta = current.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters)
            - previous.CanonicalToWorld(focus.AroundMeters, focus.VerticalMeters, focus.AcrossMeters);
        return true;
    }

    // CPU-only fixture seam for the headless rebase contract. It does not
    // create render resources or touch the normal streaming source lists.
    internal void ConfigureGpuTreeRebaseFixture(in RingLodGeometrySnapshot geometry, Transform3D layerTransform)
    {
        GlobalTransform = layerTransform;
        _gpuTreeGeometry = geometry;
        _gpuTreeLayerTransform = layerTransform;
        _gpuTreeRootEmbed = RootEmbedMeters;
    }

    internal bool StepGpuTreeRebaseFixture(in RingLodGeometrySnapshot current)
        => TryRebaseGpuTreeSources(current);
}
