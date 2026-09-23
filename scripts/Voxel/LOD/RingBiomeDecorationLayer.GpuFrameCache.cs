using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingBiomeDecorationLayer
{
    [Export] public bool OptimizeGpuTreeDispatches { get; set; } = true;
    [Export(PropertyHint.Range, "0,4,0.01")] public float TreeCullingMovementThresholdMeters { get; set; } = .5f;
    [Export(PropertyHint.Range, "0,0.5,0.001")] public float TreeCullingMaximumIntervalSeconds { get; set; } = .1f;

    private readonly List<RingGpuVegetationCompute.BatchFrameParameters> _gpuTreeFrameScratch = new();
    private long _gpuTreeFrameCacheVersion;
    private Vector3 _gpuTreeLastSubmittedCamera = new(float.NaN, float.NaN, float.NaN);
    private RingCanonicalPosition? _gpuTreeLastSubmittedFocus;
    private double _gpuTreeLastSubmittedCircumference = double.NaN;
    private ulong _gpuTreeLastSubmitUsec;
    private bool _gpuTreeLastOptimizeDispatches = true;

    private bool CanDeferGpuTreeFrame(Vector3 camera, RingCanonicalPosition? focus, double circumference,
        float[] edges, uint[] caps, float[] gates, float guard)
    {
        if (!OptimizeGpuTreeDispatches || !SkipUnchangedGpuCulling || TreeCullingMovementThresholdMeters <= 0 || TreeCullingMaximumIntervalSeconds <= 0
            || !_gpuFrameCacheComplete || _gpuTreePreparation != null || (_gpuTreeSourcesDirty && !_gpuTreeFixture) || !_gpuTreeSelectionValid
            || _gpuTreeSelectionEpoch != GpuTreeSourceEpoch || _gpuTreeSelectionProtectionRevision != _gpuTreeProtectionRevision
            || _gpuTreeLastOptimizeDispatches != OptimizeGpuTreeDispatches || _gpuLastFrameLayerTransform != GlobalTransform
            || _gpuTreeSelectionCircumference != circumference || _gpuTreeSelectionLayerTransform != GlobalTransform
            || _gpuTreeSelectionMaximumBatches != MaximumGpuTreeBatches || _gpuTreeSelectionMaximumNodes != MaximumVegetationRendererNodes
            || _gpuTreeSelectionMergeFar != MergeFarGpuTreeBatches
            || _gpuTreeSelectionSharedFar != EffectiveSharedFarTreeImpostors
            || _gpuTreeSelectionCompactProfile != EffectiveCompactTreeLodProfile || _gpuTreeSelectionMergeMid != MergeMidGpuTreeBatches
            || _gpuTreeSelectionMidSpan != EffectiveMidTreeBatchTileSpan || _gpuTreeSelectionPrune != PruneEmptyGpuTreeBatches
            || _gpuTreeSelectionPolicySignature != GpuTreeRenderPolicySignature()
            || _gpuLastEdges is null || !_gpuLastEdges.AsSpan().SequenceEqual(edges)
            || _gpuLastCaps is null || !_gpuLastCaps.AsSpan().SequenceEqual(caps)
            || _gpuLastRankGates is null || !_gpuLastRankGates.AsSpan().SequenceEqual(gates) || _gpuLastGuard != guard
            || !double.IsFinite(_gpuTreeLastSubmittedCircumference)) return false;

        // A pose that is exactly unchanged remains idle indefinitely. Fixture calls
        // have no canonical focus, so their world-space camera is the distance basis.
        double moved;
        bool samePose;
        if (focus is RingCanonicalPosition current && _gpuTreeLastSubmittedFocus is RingCanonicalPosition previous)
        {
            double around = WrappedVegetationDelta(current.AroundMeters - previous.AroundMeters, circumference);
            double across = current.AcrossMeters - previous.AcrossMeters;
            moved = Math.Sqrt(around * around + across * across);
            samePose = moved == 0;
        }
        else if (focus is null && _gpuTreeLastSubmittedFocus is null)
        {
            moved = camera.DistanceTo(_gpuTreeLastSubmittedCamera);
            samePose = moved == 0;
        }
        else return false;

        if (samePose) return true;
        ulong now = Time.GetTicksUsec();
        ulong interval = (ulong)(TreeCullingMaximumIntervalSeconds * 1_000_000.0f);
        return moved < TreeCullingMovementThresholdMeters && now - _gpuTreeLastSubmitUsec < interval;
    }

    private void RecordGpuTreeFrameSubmission(Vector3 camera, RingCanonicalPosition? focus, double circumference)
    {
        _gpuTreeLastSubmittedCamera = camera;
        _gpuTreeLastSubmittedFocus = focus;
        _gpuTreeLastSubmittedCircumference = circumference;
        _gpuTreeLastSubmitUsec = Time.GetTicksUsec();
        _gpuTreeLastOptimizeDispatches = OptimizeGpuTreeDispatches;
    }

    private static bool GpuTreeFramesMatch(IReadOnlyList<RingGpuVegetationCompute.BatchFrameParameters> frames,
        RingGpuVegetationCompute.BatchFrameParameters[]? previous)
    {
        if (previous is null || previous.Length != frames.Count) return false;
        for (int i = 0; i < previous.Length; i++) if (previous[i] != frames[i]) return false;
        return true;
    }

    internal string RunGpuTreeFrameCacheRegression()
    {
        try
        {
            OptimizeGpuTreeDispatches = true; SkipUnchangedGpuCulling = true;
            TreeCullingMovementThresholdMeters = .5f; TreeCullingMaximumIntervalSeconds = .1f;
            _gpuTreeFixture = true; _gpuFrameCacheComplete = true; _gpuTreeSelectionValid = true;
            _gpuTreeSelectionEpoch = GpuTreeSourceEpoch; _gpuTreeSelectionProtectionRevision = _gpuTreeProtectionRevision;
            _gpuTreeSelectionCircumference = 1_000; _gpuTreeSelectionLayerTransform = GlobalTransform;
            _gpuTreeSelectionMaximumBatches = MaximumGpuTreeBatches; _gpuTreeSelectionMaximumNodes = MaximumVegetationRendererNodes;
            _gpuTreeSelectionMergeFar = MergeFarGpuTreeBatches;
            _gpuTreeSelectionSharedFar = EffectiveSharedFarTreeImpostors;
            _gpuTreeSelectionCompactProfile = EffectiveCompactTreeLodProfile; _gpuTreeSelectionMergeMid = MergeMidGpuTreeBatches;
            _gpuTreeSelectionMidSpan = EffectiveMidTreeBatchTileSpan; _gpuTreeSelectionPrune = PruneEmptyGpuTreeBatches;
            _gpuTreeSelectionPolicySignature = GpuTreeRenderPolicySignature(); _gpuLastPolicySignature = _gpuTreeSelectionPolicySignature;
            float[] edges = GpuTreeLodEdges(); uint[] caps = GpuTreeLodCaps();
            float[] gates = { VegetationLodRankGate(1), VegetationLodRankGate(2), EffectiveCompactTreeLodProfile ? 1f : VegetationLodRankGate(3), VegetationLodRankGate(4), VegetationLodRankGate(5) };
            float guard = Math.Max(32, CellSizeMeters * 2);
            _gpuLastEdges = edges; _gpuLastCaps = caps; _gpuLastRankGates = gates; _gpuLastGuard = guard; _gpuLastFrameLayerTransform = GlobalTransform;
            var origin = new RingCanonicalPosition(999.9, 0, 0);
            RecordGpuTreeFrameSubmission(new Vector3(20, 0, 0), origin, 1_000);
            if (!CanDeferGpuTreeFrame(new Vector3(20, 0, 0), origin, 1_000, edges, caps, gates, guard)) return "FAIL: stationary frame submitted";
            var small = new RingCanonicalPosition(0.1, 0, 0);
            if (!CanDeferGpuTreeFrame(new Vector3(20.2f, 0, 0), small, 1_000, edges, caps, gates, guard)) return "FAIL: wrapped seam motion was not deferred";
            _gpuTreeLastSubmitUsec = 0;
            if (CanDeferGpuTreeFrame(new Vector3(20.2f, 0, 0), small, 1_000, edges, caps, gates, guard)) return "FAIL: movement deadline was deferred";
            RecordGpuTreeFrameSubmission(new Vector3(20, 0, 0), origin, 1_000);
            var teleport = new RingCanonicalPosition(2, 0, 0);
            if (CanDeferGpuTreeFrame(new Vector3(22, 0, 0), teleport, 1_000, edges, caps, gates, guard)) return "FAIL: teleport was deferred";
            long cacheVersion = _gpuTreeFrameCacheVersion;
            InvalidateGpuTreeFrameCache();
            if (_gpuTreeFrameCacheVersion == cacheVersion) return "FAIL: transform cache was not invalidated";
            if (CanDeferGpuTreeFrame(new Vector3(20, 0, 0), origin, 1_000, edges, caps, gates, guard)) return "FAIL: invalidation was deferred";
            return "PASS: stationary, small movement, deadline, seam, teleport, invalidation";
        }
        catch (Exception error) { return "FAIL: " + error.GetType().Name + " " + error.Message; }
    }
}
