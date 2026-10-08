using System;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Diagnostics;

public partial class RingPerformanceMonitor
{
    private RingScaledFarViewport? _farTimingOwner;
    private double _farTimingDiscoveryElapsed = .5;
    private bool _mainViewportMeasurementEnabled;
    private readonly ViewportMeasurement _outerFarMeasurement = new();
    private readonly ViewportMeasurement _regionalFarMeasurement = new();

    internal void InstallFarMeasurementSetterForTests(Action<Rid, bool> setter)
    {
        _outerFarMeasurement.InstallSetterForTests(setter);
        _regionalFarMeasurement.InstallSetterForTests(setter);
    }

    // Discovery is bounded; the steady path reads cached owner accessors rather
    // than traversing the node tree. Accessors also catch late target creation.
    private void UpdateViewportMeasurements(double delta)
    {
        bool enabled = Enabled && _gpuAvailable;
        if (_mainViewportMeasurementEnabled != enabled && _viewport.IsValid)
        {
            RenderingServer.ViewportSetMeasureRenderTime(_viewport, enabled);
            _mainViewportMeasurementEnabled = enabled;
        }
        if (!enabled)
        {
            _outerFarMeasurement.Reconcile(null, false);
            _regionalFarMeasurement.Reconcile(null, false);
            return;
        }
        _farTimingDiscoveryElapsed += double.IsFinite(delta) ? Math.Max(0, delta) : 0;
        if (_farTimingDiscoveryElapsed >= .5)
        {
            _farTimingDiscoveryElapsed = 0;
            // Resolve even a still-live cached owner so canonical replacement
            // does not leave measurement enabled on its retired render targets.
            _farTimingOwner = GetParent()?.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
        }
        bool ownerValid = _farTimingOwner != null && GodotObject.IsInstanceValid(_farTimingOwner)
            && _farTimingOwner.IsInsideTree() && !_farTimingOwner.IsQueuedForDeletion() && _farTimingOwner.Enabled;
        _outerFarMeasurement.Reconcile(ownerValid ? _farTimingOwner!.OuterViewportForDiagnostics : null, enabled);
        _regionalFarMeasurement.Reconcile(ownerValid ? _farTimingOwner!.RegionalViewportForDiagnostics : null, enabled);
    }

    private void ReleaseViewportMeasurements()
    {
        _outerFarMeasurement.Reconcile(null, false);
        _regionalFarMeasurement.Reconcile(null, false);
        if (_mainViewportMeasurementEnabled && _viewport.IsValid)
            RenderingServer.ViewportSetMeasureRenderTime(_viewport, false);
        _mainViewportMeasurementEnabled = false;
        _farTimingOwner = null;
    }

    private object CaptureFarViewportState() => new
    {
        outer = _outerFarMeasurement.CaptureState(), regional = _regionalFarMeasurement.CaptureState(),
        note = "Separate renderer-delayed viewport intervals; not CPU-frame paired or an additive device total. Null means inactive, unavailable or not yet measured."
    };

    internal sealed class ViewportMeasurement
    {
        private SubViewport? _target;
        private Rid _rid;
        private ulong _minimumReadFrame;
        private bool _measuring;
        private Action<Rid, bool> _setMeasurement;
        private readonly Func<Rid, double> _readCpu, _readGpu;
        private readonly Func<ulong> _frame;

        public ViewportMeasurement(Action<Rid, bool>? setMeasurement = null,
            Func<Rid, double>? readCpu = null, Func<Rid, double>? readGpu = null, Func<ulong>? frame = null)
        {
            _setMeasurement = setMeasurement ?? RenderingServer.ViewportSetMeasureRenderTime;
            _readCpu = readCpu ?? (rid => RenderingServer.ViewportGetMeasuredRenderTimeCpu(rid));
            _readGpu = readGpu ?? (rid => RenderingServer.ViewportGetMeasuredRenderTimeGpu(rid));
            _frame = frame ?? Engine.GetProcessFrames;
        }

        internal void InstallSetterForTests(Action<Rid, bool> setter)
        {
            if (_measuring) throw new InvalidOperationException("Install the fixture setter before a measurement lease starts.");
            _setMeasurement = setter;
        }

        private static bool Active(SubViewport? viewport) => viewport != null && GodotObject.IsInstanceValid(viewport)
            && viewport.IsInsideTree() && !viewport.IsQueuedForDeletion() && !viewport.Disable3D
            && viewport.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled
            && viewport.Size.X > 0 && viewport.Size.Y > 0 && viewport.GetCamera3D() != null;

        public void Reconcile(SubViewport? target, bool enabled)
        {
            if (!enabled || !Active(target)) target = null;
            if (ReferenceEquals(target, _target) && _measuring == (target != null)) return;
            // A disposed viewport has already released its RID. Never send a
            // disable command through a stale RID after that destruction.
            if (_measuring && _target != null && GodotObject.IsInstanceValid(_target) && _rid.IsValid)
                _setMeasurement(_rid, false);
            _target = target;
            _measuring = target != null;
            _rid = target?.GetViewportRid() ?? default;
            if (_measuring)
            {
                _setMeasurement(_rid, true);
                // Minimum startup guard only. Render timing readback has no
                // source-frame token and may be delayed beyond two process
                // frames; a positive result does not certify fresh pairing.
                _minimumReadFrame = _frame() + 2;
            }
        }

        public (double? Cpu, double? Gpu) Read()
        {
            if (!_measuring || !Active(_target) || !_rid.IsValid || _frame() < _minimumReadFrame) return (null, null);
            return (Available(_readCpu(_rid)), Available(_readGpu(_rid)));
        }

        internal bool HasStartedTarget(SubViewport? target) => ReferenceEquals(target, _target) && _measuring
            && Active(_target) && _frame() >= _minimumReadFrame;

        private static double? Available(double value) => double.IsFinite(value) && value > 0 ? value : null;
        public object CaptureState()
        {
            var sample = Read();
            return new { active = _measuring && Active(_target), viewportId = _target != null && GodotObject.IsInstanceValid(_target) ? (ulong?)_target.GetInstanceId() : null,
                size = _target != null && GodotObject.IsInstanceValid(_target) ? new[] { _target.Size.X, _target.Size.Y } : null,
                cpuMilliseconds = sample.Cpu, gpuMilliseconds = sample.Gpu };
        }
    }
}
