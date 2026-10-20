using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;

namespace RingworldTechDemo.Diagnostics;

public partial class RingPerformanceMonitor
{
    internal readonly record struct CloudStagePartition(long SourceFrameId, double? Prepass, double? Raymarch,
        double? Reconstruct, double? Composite, double? ParentClouds, bool Complete);

    private static readonly int MaterialWriteSites = RingMaterialWriteCounters.SiteCount;
    private readonly long[] _materialWriteLast = new long[MaterialWriteSites], _materialTextureLast = new long[MaterialWriteSites];
    private readonly long[] _materialWriteScratch = new long[MaterialWriteSites], _materialTextureScratch = new long[MaterialWriteSites];
    private readonly long[] _materialWriteSum = new long[MaterialWriteSites], _materialWriteMax = new long[MaterialWriteSites], _materialTextureSum = new long[MaterialWriteSites];
    private long _materialWriteFrames;
    private bool _materialWriteBaselineStale = true;

    // Per-frame deltas of the main-thread material write counters; fixed arrays, no allocation.
    private void SampleMaterialWrites()
    {
        RingMaterialWriteCounters.Snapshot(_materialWriteScratch, _materialTextureScratch);
        if (!_materialWriteBaselineStale)
        {
            for (int i = 0; i < MaterialWriteSites; i++)
            {
                long writes = _materialWriteScratch[i] - _materialWriteLast[i];
                _materialWriteSum[i] += writes;
                if (writes > _materialWriteMax[i]) _materialWriteMax[i] = writes;
                _materialTextureSum[i] += _materialTextureScratch[i] - _materialTextureLast[i];
            }
            _materialWriteFrames++;
        }
        _materialWriteBaselineStale = false;
        Array.Copy(_materialWriteScratch, _materialWriteLast, MaterialWriteSites);
        Array.Copy(_materialTextureScratch, _materialTextureLast, MaterialWriteSites);
    }

    private void ResetMaterialWrites()
    {
        Array.Clear(_materialWriteSum); Array.Clear(_materialWriteMax); Array.Clear(_materialTextureSum);
        _materialWriteFrames = 0;
        _materialWriteBaselineStale = true;
    }

    private object CaptureMaterialWrites()
    {
        var sites = new object[MaterialWriteSites];
        double frames = Math.Max(1, _materialWriteFrames);
        for (int i = 0; i < MaterialWriteSites; i++)
            sites[i] = new { site = ((RingMaterialWriteCounters.Site)i).ToString(), meanWrites = _materialWriteSum[i] / frames,
                maxWrites = _materialWriteMax[i], meanTextureWrites = _materialTextureSum[i] / frames };
        return new { frames = _materialWriteFrames, perSite = sites };
    }

    private object CaptureRuntimeState() => new
    {
        maxFps = Engine.MaxFps, timeScale = Engine.TimeScale,
        vsync = DisplayServer.WindowGetVsyncMode().ToString(), display = DisplayServer.GetName(),
        refreshHz = DisplayServer.ScreenGetRefreshRate(), focused = DisplayServer.WindowIsFocused(),
        windowMode = DisplayServer.WindowGetMode().ToString(), commandLine = OS.GetCmdlineArgs(),
        lowProcessorUsageMode = OS.LowProcessorUsageMode
    };

    private object CaptureAttribution()
    {
        var frames = new List<RingPerformanceFrame>(_frameCount);
        for (int i = 0; i < _frameCount; i++) frames.Add(_frames[(_frameHead + i) % _frames.Length]);
        var gpu = new List<RingPerformanceTelemetry.Completed>(_gpuEventCount);
        for (int i = 0; i < _gpuEventCount; i++) gpu.Add(_gpuEvents[(_gpuEventHead + i) % _gpuEvents.Length]);
        CloudStagePartition[] partitions = BuildCloudPartitions(gpu);
        int complete = 0;
        foreach (var item in partitions) if (item.Complete) complete++;
        return new
        {
            runtime = CaptureRuntimeState(),
            terrainCloudSunVisibility = CaptureTerrainFieldState(),
            coverage = new { frameSamples = frames.Count,
                firstFrameSeconds = frames.Count == 0 ? (double?)null : frames[0].Seconds,
                lastFrameSeconds = frames.Count == 0 ? (double?)null : frames[^1].Seconds,
                retainedSeconds = frames.Count < 2 ? (double?)null : frames[^1].Seconds - frames[0].Seconds,
                cpuDropped = _dropped, gpuDropped = _gpuDropped },
            percentiles = new
            {
                frame = Stats(frames, f => f.FrameMilliseconds), processMonitor = Stats(frames, f => f.ProcessMilliseconds),
                physicsMonitor = Stats(frames, f => f.PhysicsMilliseconds), frameSetupCpu = Stats(frames, f => f.SetupMilliseconds),
                mainCpu = Stats(frames, f => f.ViewportCpuMilliseconds), mainGpu = Stats(frames, f => f.ViewportGpuMilliseconds),
                outerCpu = Stats(frames, f => f.FarViewportCpuMilliseconds), outerGpu = Stats(frames, f => f.FarViewportGpuMilliseconds),
                regionalCpu = Stats(frames, f => f.RegionalFarViewportCpuMilliseconds), regionalGpu = Stats(frames, f => f.RegionalFarViewportGpuMilliseconds),
                globalDrawCalls = Stats(frames, f => f.DrawCalls < 0 ? null : f.DrawCalls),
                globalPrimitives = Stats(frames, f => f.RenderPrimitives)
            },
            materialWrites = CaptureMaterialWrites(),
            mainWorkload = CaptureMainWorkload(),
            gpuCategoryCoverage = CaptureGpuCoverage(gpu),
            cloudStages = new { completeSourceFrames = complete, observedSourceFrames = partitions.Length, partitions,
                prepass = Stats(partitions, p => p.Complete ? p.Prepass : null),
                raymarch = Stats(partitions, p => p.Complete ? p.Raymarch : null),
                reconstruct = Stats(partitions, p => p.Complete ? p.Reconstruct : null),
                composite = Stats(partitions, p => p.Complete ? p.Composite : null),
                note = "Partition rows require unique stages from one GPU source frame. Missing/duplicate stages stay null; the inclusive parent is separate. No CPU/viewport/compute values are added as a device total." },
            note = "Null statistics mean unavailable, not zero cost. CPU monitor values are engine monitor samples, viewport timings are renderer intervals and exclude scripts, detailed GPU markers are asynchronous source-frame data. Nested CPU scopes remain inclusive; no summed script total is claimed."
        };
    }

    private object CaptureTerrainFieldState()
    {
        var bridge = GetParent()?.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        if (bridge == null) return new { status = "unavailable: Sunshine bridge missing" };
        try
        {
            // Convert Godot Variants through its JSON encoder once at capture.
            // System.Text.Json cannot directly serialize the Variant-backed dictionary.
            return JsonSerializer.Deserialize<JsonElement>(Godot.Json.Stringify(bridge.GetTerrainCloudSunVisibilityDiagnostics()));
        }
        catch (Exception error) { return new { status = "capture error", error = error.Message }; }
    }

    private static object[] CaptureGpuCoverage(IReadOnlyList<RingPerformanceTelemetry.Completed> samples)
    {
        var result = new List<object>();
        foreach (string name in new[] { "GPU.Clouds", "GPU.CloudPrepass", "GPU.CloudRaymarch", "GPU.CloudReconstruct", "GPU.CloudComposite", "GPU.CloudShadowNear", "GPU.CloudShadowFar", "GPU.CloudMacroMap", "GPU.CloudInterleave", "GPU.RainHaze", "GPU.TerrainCloudSunVisibility", "GPU.HeightShadow", "GPU.GrassCulling", "GPU.TreeCulling", "GPU.WaterFft" })
        {
            var values = new List<RingPerformanceTelemetry.Completed>();
            var sourceFrames = new HashSet<long>();
            double first = double.PositiveInfinity, last = double.NegativeInfinity;
            foreach (var item in samples) if (item.Name == name)
            { values.Add(item); sourceFrames.Add(item.SourceFrameId); first = Math.Min(first, item.CompletedSeconds); last = Math.Max(last, item.CompletedSeconds); }
            result.Add(new { category = name, status = values.Count == 0 ? "unavailable: inactive, disabled or no retained markers" : "retained asynchronous GPU markers",
                statistics = Stats(values, v => v.Milliseconds), distinctSourceFrames = sourceFrames.Count,
                firstCompletionSeconds = values.Count == 0 ? (double?)null : first,
                lastCompletionSeconds = values.Count == 0 ? (double?)null : last });
        }
        return result.ToArray();
    }

    private object CaptureMainWorkload()
    {
        Viewport viewport = GetViewport();
        object? Counters(RenderingServer.ViewportRenderInfoType type) => !_gpuAvailable ? null : new
        {
            objects = RenderingServer.ViewportGetRenderInfo(viewport.GetViewportRid(), type, RenderingServer.ViewportRenderInfo.ObjectsInFrame),
            primitives = RenderingServer.ViewportGetRenderInfo(viewport.GetViewportRid(), type, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
            drawCalls = RenderingServer.ViewportGetRenderInfo(viewport.GetViewportRid(), type, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)
        };
        return new { available = _gpuAvailable, visible = Counters(RenderingServer.ViewportRenderInfoType.Visible),
            shadow = Counters(RenderingServer.ViewportRenderInfoType.Shadow), canvas = Counters(RenderingServer.ViewportRenderInfoType.Canvas),
            note = "Capture-time per-viewport renderer counters; no source-frame token. Global engine counters can include other viewports." };
    }

    internal static CloudStagePartition[] BuildCloudPartitions(IReadOnlyList<RingPerformanceTelemetry.Completed> samples)
    {
        var groups = new SortedDictionary<long, List<RingPerformanceTelemetry.Completed>>();
        foreach (var item in samples)
        {
            if (!item.Gpu || item.SourceFrameId < 0 || !item.Name.StartsWith("GPU.Cloud", StringComparison.Ordinal)) continue;
            if (!groups.TryGetValue(item.SourceFrameId, out var group)) groups[item.SourceFrameId] = group = new();
            group.Add(item);
        }
        var result = new List<CloudStagePartition>();
        foreach (var (sourceFrame, group) in groups)
        {
            double? Unique(string name)
            {
                double? value = null; int count = 0;
                foreach (var item in group) if (item.Name == name) { count++; value = item.Milliseconds; }
                return count == 1 && value.HasValue && double.IsFinite(value.Value) && value.Value >= 0 ? value : null;
            }
            double? pre = Unique("GPU.CloudPrepass"), ray = Unique("GPU.CloudRaymarch"), reconstruction = Unique("GPU.CloudReconstruct"), composite = Unique("GPU.CloudComposite");
            // Shadow scopes are not members of the Sunshine partition.
            if (pre == null && ray == null && reconstruction == null && composite == null && Unique("GPU.Clouds") == null) continue;
            result.Add(new(sourceFrame, pre, ray, reconstruction, composite, Unique("GPU.Clouds"),
                pre.HasValue && ray.HasValue && reconstruction.HasValue && composite.HasValue));
        }
        return result.ToArray();
    }
}
