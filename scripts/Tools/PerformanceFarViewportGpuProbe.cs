using System;
using System.Reflection;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Diagnostics;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>Small rendered fixture adapter using production far-target creation and monitor discovery.</summary>
public partial class PerformanceFarViewportGpuProbe : RefCounted
{
    public bool CaptureFinished { get; private set; }
    public string CaptureResult { get; private set; } = "";
    public string ConcurrentCaptureResult { get; private set; } = "";
    public async void BeginCapture(RingPerformanceMonitor monitor, Node? removeOwner = null, bool removeMonitor = false)
    {
        CaptureFinished = false; CaptureResult = ""; ConcurrentCaptureResult = "";
        try
        {
            var capture = monitor.CaptureFarRingViewports();
            if (monitor.FarRingCaptureBusy) ConcurrentCaptureResult = await monitor.CaptureFarRingViewports();
            if (removeOwner != null) removeOwner.Free();
            if (removeMonitor) monitor.Free();
            CaptureResult = await capture;
        }
        catch (Exception error) { CaptureResult = "Adapter failed: " + error.Message; }
        finally { CaptureFinished = true; }
    }
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private readonly Dictionary<Rid, bool> _measurementState = new();

    public RingPerformanceMonitor CreateMonitor(Node parent, string directory)
    {
        var monitor = new RingPerformanceMonitor { Enabled = true, DetailedGpuEnabled = false, Name = "PerformanceMonitor" };
        monitor.InstallSettingsPathForTests(directory + "/monitor.tres");
        monitor.InstallReportDirectoryForTests(directory);
        parent.AddChild(monitor);
        monitor.SetProcess(false);
        monitor.InstallFarMeasurementSetterForTests((rid, enabled) =>
        {
            RenderingServer.ViewportSetMeasureRenderTime(rid, enabled);
            _measurementState[rid] = enabled;
        });
        return monitor;
    }

    public RingScaledFarViewport CreateOwner(Node parent)
    {
        // Skip gameplay Ready requirements; then invoke the actual production
        // target factories with only the source-camera input they require.
        var owner = new RingScaledFarViewport { Enabled = false, Name = "ScaledFarViewport" };
        parent.AddChild(owner);
        owner.Enabled = true; owner.SetProcess(false);
        var source = new Camera3D { Name = "FixtureSourceCamera" }; owner.AddChild(source);
        typeof(RingScaledFarViewport).GetField("_sourceCamera", PrivateInstance)!.SetValue(owner, source);
        typeof(RingScaledFarViewport).GetMethod("CreateFarViewport", PrivateInstance)!.Invoke(owner, null);
        typeof(RingScaledFarViewport).GetMethod("CreateRegionalFarViewport", PrivateInstance)!.Invoke(owner, null);
        AddVisibleGeometry(owner.OuterViewportForDiagnostics!, new Vector2I(256, 144), false);
        AddVisibleGeometry(owner.RegionalViewportForDiagnostics!, new Vector2I(192, 128), true);
        // Execute the actual controller entry point once for attribution. This
        // synthetic owner has no terrain dependencies, so guarded stages remain
        // small; restore fixture target sizes after its normal size sync.
        owner._Process(0);
        owner.OuterViewportForDiagnostics!.Size = new Vector2I(256, 144);
        owner.RegionalViewportForDiagnostics!.Size = new Vector2I(192, 128);
        return owner;
    }

    private static void AddVisibleGeometry(SubViewport viewport, Vector2I size, bool regional)
    {
        viewport.Size = size;
        var mesh = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = regional ? new Vector3(.2f, .2f, .1f) : new Vector3(2, 2, 1) },
            Position = regional ? new Vector3(0, 0, -.5f) : new Vector3(0, 0, -5),
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                AlbedoColor = regional ? new Color(.2f, .6f, .3f) : new Color(.3f, .4f, .8f) }
        };
        viewport.AddChild(mesh);
    }

    public Godot.Collections.Dictionary Observe(RingPerformanceMonitor monitor)
    {
        var snapshot = monitor.GetSnapshot();
        if (snapshot.History.Count == 0) return new() { ["has_frame"] = false };
        var frame = snapshot.History[^1];
        return new() { ["has_frame"] = true, ["frame_count"] = snapshot.History.Count,
            ["process_frame"] = frame.FrameId,
            ["outer_ready"] = frame.FarViewportCpuMilliseconds.HasValue && frame.FarViewportGpuMilliseconds.HasValue,
            ["regional_ready"] = frame.RegionalFarViewportCpuMilliseconds.HasValue && frame.RegionalFarViewportGpuMilliseconds.HasValue,
            ["outer_cpu"] = Value(frame.FarViewportCpuMilliseconds), ["outer_gpu"] = Value(frame.FarViewportGpuMilliseconds),
            ["regional_cpu"] = Value(frame.RegionalFarViewportCpuMilliseconds), ["regional_gpu"] = Value(frame.RegionalFarViewportGpuMilliseconds) };
    }

    private static Variant Value(double? value) => value.HasValue ? Variant.From(value.Value) : default;

    public bool RendererMeasurementCallsMatch(RingScaledFarViewport owner, bool enabled)
    {
        foreach (SubViewport? viewport in new[] { owner.OuterViewportForDiagnostics, owner.RegionalViewportForDiagnostics })
        {
            if (viewport == null) continue;
            Rid rid = viewport.GetViewportRid();
            // Getter values can persist after disable in Godot4.7. Verify the
            // actual forwarded setter command, not the cached last interval.
            if (!_measurementState.TryGetValue(rid, out bool actual) || actual != enabled) return false;
        }
        return true;
    }
}
