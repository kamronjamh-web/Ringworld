using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Diagnostics;

public partial class RingPerformanceMonitor
{
    public bool FarRingCaptureBusy { get; private set; }
    public string LastFarRingCaptureStatus { get; private set; } = "";
    private const long FarCaptureByteLimit = 64L * 1024 * 1024;
    private TaskCompletionSource<bool>? _farCaptureExitSignal;
    private void CancelFarRingCaptureOnExit() => _farCaptureExitSignal?.TrySetResult(true);
    private async Task WaitForFarCaptureDraw() { await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw); }

    public async Task<string> CaptureFarRingViewports()
    {
        if (FarRingCaptureBusy) return LastFarRingCaptureStatus = "Export refused: far viewport capture already in progress.";
        if (!GodotObject.IsInstanceValid(this) || !IsInsideTree()) return LastFarRingCaptureStatus = "Export refused: diagnostics monitor is not in a live scene.";
        FarRingCaptureBusy = true;
        _farCaptureExitSignal = new TaskCompletionSource<bool>();
        string? directory = null;
        try
        {
            string root = _reportDirectoryForTests ?? RingGeneratedOutputPaths.Directory("reports/diagnostics");
            Directory.CreateDirectory(root);
            int count = Directory.GetDirectories(root, "ring-far-viewports-*").Length;
            long existingBytes = FarCaptureBytes(root);
            if (count >= 20 || existingBytes >= FarCaptureByteLimit)
                return LastFarRingCaptureStatus = "Export refused: diagnostics report cap reached (20 captures / 64 MiB).";

            // Capture-only discovery avoids waiting for the periodic monitor
            // lookup and never changes measurement/quality/player settings.
            RingScaledFarViewport? owner = GetParent()?.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport");
            bool ownerValid = owner != null && GodotObject.IsInstanceValid(owner) && owner.IsInsideTree()
                && !owner.IsQueuedForDeletion() && owner.Enabled;
            SubViewport? outer = ownerValid ? owner!.OuterViewportForDiagnostics : null;
            SubViewport? regional = ownerValid ? owner!.RegionalViewportForDiagnostics : null;
            var images = new[] { new FarCaptureImage("outer", outer), new FarCaptureImage("regional", regional) };
            var history = new List<RingPerformanceFrame>(_frameCount);
            for (int index = 0; index < _frameCount; index++) history.Add(_frames[(_frameHead + index) % _frames.Length]);
            var farCpu = CopyFarCpuEvents();
            // All statistics/counters/settings are frozen before the first
            // GPU image readback, compression, or disk image operation.
            object measurement = new
            {
                capturedUtc = DateTime.UtcNow.ToString("O"), processFrame = Engine.GetProcessFrames(),
                engine = EngineVersion(), renderer = RenderingServer.GetCurrentRenderingMethod(), gpuAdapter = RenderingServer.GetVideoAdapterName(),
                windowFocused = DisplayServer.WindowIsFocused(), attribution = CaptureAttribution(), mainRenderState = CaptureRenderState(),
                monitoring = new { Enabled, DetailedGpuEnabled, HistorySeconds, _gpuAvailable },
                context = _context, history,
                summary = new
                {
                    mainCpu = Stats(history, f => f.ViewportCpuMilliseconds), mainGpu = Stats(history, f => f.ViewportGpuMilliseconds),
                    outerCpu = Stats(history, f => f.FarViewportCpuMilliseconds), outerGpu = Stats(history, f => f.FarViewportGpuMilliseconds),
                    regionalCpu = Stats(history, f => f.RegionalFarViewportCpuMilliseconds), regionalGpu = Stats(history, f => f.RegionalFarViewportGpuMilliseconds)
                },
                oneSecondBins = FarCaptureBins(history),
                farRingCpu = new { summary = SummarizeFarCpu(farCpu), events = farCpu,
                    note = "Per-call CPU wall durations. Parent inclusive and child scopes overlap and must not be summed. Worker scopes, if present, are not frame-critical time." },
                outer = CaptureViewportWorkload(outer), regional = CaptureViewportWorkload(regional),
                owner = ownerValid ? owner!.CaptureSettingsForDiagnostics() : null,
                caveats = "Retained history reflects its original monitoring state. Main, outer and regional renderer intervals are delayed, separate, not CPU-frame paired or an additive device total. Viewport CPU is renderer wall time; it can include global geometry housekeeping and compositor callbacks, so it is not isolated far-mesh cost. Capture-time counters describe the last renderer frame; image readback/save work occurs after this snapshot. No render or monitoring setting is changed."
            };
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fffffff") + "-" + Guid.NewGuid().ToString("N")[..8];
            directory = Path.Combine(root, "ring-far-viewports-" + id);
            Directory.CreateDirectory(directory);
            string jsonPath = Path.Combine(directory, "report.json");
            WriteFarCaptureJson(jsonPath, measurement, images, existingBytes);

            bool rendererAvailable = !DisplayServer.GetName().Contains("headless", StringComparison.OrdinalIgnoreCase)
                && RenderingServer.GetRenderingDevice() != null;
            bool drawTimedOut = false;
            if (rendererAvailable && (FarCaptureTargetActive(outer) || FarCaptureTargetActive(regional)))
            {
                var timeout = new TaskCompletionSource<bool>();
                GetTree().CreateTimer(3, processAlways: true, ignoreTimeScale: true).Timeout += () => timeout.TrySetResult(true);
                Task completed = await Task.WhenAny(WaitForFarCaptureDraw(), _farCaptureExitSignal.Task, timeout.Task);
                drawTimedOut = ReferenceEquals(completed, timeout.Task);
            }
            foreach (FarCaptureImage item in images)
            {
                try
                {
                    if (!rendererAvailable) { item.Status = "unavailable: headless or no rendering device"; continue; }
                    if (drawTimedOut) { item.Status = "unavailable: no rendered frame before capture timeout"; continue; }
                    if (_farCaptureExitSignal.Task.IsCompleted || !GodotObject.IsInstanceValid(this) || !IsInsideTree())
                    { item.Status = "unavailable: monitor exited during capture"; continue; }
                    if (!ownerValid || owner == null || !GodotObject.IsInstanceValid(owner) || !owner.IsInsideTree()
                        || owner.IsQueuedForDeletion() || !ReferenceEquals(GetParent()?.GetNodeOrNull<RingScaledFarViewport>("ScaledFarViewport"), owner))
                    { item.Status = "unavailable: far owner removed or replaced during capture"; continue; }
                    SubViewport? current = item.Name == "outer" ? owner.OuterViewportForDiagnostics : owner.RegionalViewportForDiagnostics;
                    if (!ReferenceEquals(current, item.Target) || !FarCaptureTargetActive(item.Target) || item.Target!.Size != item.Size)
                    { item.Status = "unavailable: viewport missing, inactive, freed or replaced"; continue; }
                    Vector2I size = item.Target!.Size;
                    // Convert the image copy to RGBA8. Five bytes/pixel plus
                    // fixed overhead conservatively bounds its PNG before readback.
                    long pngBound = checked((long)size.X * size.Y * 5 + 65536);
                    if (FarCaptureBytes(root) + pngBound + 262144 > FarCaptureByteLimit)
                    { item.Status = "skipped: PNG would exceed the 64 MiB capture budget"; continue; }
                    using Image image = item.Target.GetTexture().GetImage();
                    if (image == null || image.IsEmpty()) { item.Status = "error: viewport image is empty"; continue; }
                    image.Convert(Image.Format.Rgba8);
                    byte[] png = image.SavePngToBuffer();
                    if (png.Length == 0) { item.Status = "error: PNG encoding failed"; continue; }
                    if (FarCaptureBytes(root) + png.Length + 262144 > FarCaptureByteLimit)
                    { item.Status = "skipped: encoded PNG exceeds the 64 MiB capture budget"; continue; }
                    string filename = item.Name + ".png";
                    File.WriteAllBytes(Path.Combine(directory, filename), png);
                    item.File = filename; item.Bytes = png.Length; item.Status = "saved";
                }
                catch (Exception error) { item.Status = "error: " + error.Message; }
            }
            WriteFarCaptureJson(jsonPath, measurement, images, FarCaptureBytes(root) - new FileInfo(jsonPath).Length);
            LastFarRingCaptureStatus = images[0].Status == "saved" && images[1].Status == "saved"
                ? "Saved outer and regional far viewport capture: " + directory : "Saved report: " + directory + "; image availability: outer " + images[0].Status + "; regional " + images[1].Status;
            return directory;
        }
        catch (Exception error)
        {
            LastFarRingCaptureStatus = "Export failed: " + error.Message + (directory == null ? "" : "; partial bundle: " + directory);
            return LastFarRingCaptureStatus;
        }
        finally { FarRingCaptureBusy = false; _farCaptureExitSignal = null; }
    }

    private static bool FarCaptureTargetActive(SubViewport? target) => target != null && GodotObject.IsInstanceValid(target)
        && target.IsInsideTree() && !target.IsQueuedForDeletion() && !target.Disable3D
        && target.RenderTargetUpdateMode != SubViewport.UpdateMode.Disabled && target.GetCamera3D() != null;

    private object CaptureViewportWorkload(SubViewport? target)
    {
        bool started = _outerFarMeasurement.HasStartedTarget(target) || _regionalFarMeasurement.HasStartedTarget(target);
        bool available = _gpuAvailable && FarCaptureTargetActive(target) && started;
        if (target == null || !GodotObject.IsInstanceValid(target))
            return new { status = "missing", available = false, visible = (object?)null, shadow = (object?)null, canvas = (object?)null };
        Camera3D? camera = target.GetCamera3D();
        Rid rid = target.GetViewportRid();
        if (available)
        {
            // All-zero visible counters cannot distinguish an empty viewport
            // from a new target not yet reported. Keep readiness unknown rather
            // than exporting initial zero as evidence of completed GPU work.
            available = RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.ObjectsInFrame) > 0
                || RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.PrimitivesInFrame) > 0
                || RenderingServer.ViewportGetRenderInfo(rid, RenderingServer.ViewportRenderInfoType.Visible, RenderingServer.ViewportRenderInfo.DrawCallsInFrame) > 0;
        }
        object? Counters(RenderingServer.ViewportRenderInfoType type) => !available ? null : new
        {
            objects = RenderingServer.ViewportGetRenderInfo(rid, type, RenderingServer.ViewportRenderInfo.ObjectsInFrame),
            primitives = RenderingServer.ViewportGetRenderInfo(rid, type, RenderingServer.ViewportRenderInfo.PrimitivesInFrame),
            drawCalls = RenderingServer.ViewportGetRenderInfo(rid, type, RenderingServer.ViewportRenderInfo.DrawCallsInFrame)
        };
        return new
        {
            status = available ? "last renderer frame counters; no source-frame identity" : "readiness unknown: inactive, headless, startup guard, empty or not yet reported",
            available, id = target.GetInstanceId(), dimensions = new[] { target.Size.X, target.Size.Y },
            updateMode = target.RenderTargetUpdateMode.ToString(), target.TransparentBg, target.Disable3D,
            shading = new { msaa = target.Msaa3D.ToString(), screenSpaceAa = target.ScreenSpaceAA.ToString(), target.UseTaa,
                target.UseDebanding, target.UseHdr2D, target.Scaling3DScale, scalingMode = target.Scaling3DMode.ToString() },
            camera = camera == null ? null : new { position = new[] { camera.GlobalPosition.X, camera.GlobalPosition.Y, camera.GlobalPosition.Z },
                forward = new[] { -camera.GlobalBasis.Z.X, -camera.GlobalBasis.Z.Y, -camera.GlobalBasis.Z.Z }, camera.Near, camera.Far, camera.Fov,
                projection = camera.Projection.ToString(), camera.CullMask },
            visible = Counters(RenderingServer.ViewportRenderInfoType.Visible), shadow = Counters(RenderingServer.ViewportRenderInfoType.Shadow),
            canvas = Counters(RenderingServer.ViewportRenderInfoType.Canvas)
        };
    }

    private static object[] FarCaptureBins(IReadOnlyList<RingPerformanceFrame> history)
    {
        var bins = new SortedDictionary<long, List<RingPerformanceFrame>>();
        foreach (RingPerformanceFrame frame in history)
        {
            long second = (long)Math.Floor(frame.Seconds);
            if (!bins.TryGetValue(second, out var bin)) bins[second] = bin = new();
            bin.Add(frame);
        }
        var rows = new List<object>();
        foreach (var (second, bin) in bins)
            rows.Add(new { startSeconds = second, endSeconds = second + 1,
                mainCpu = Stats(bin, f => f.ViewportCpuMilliseconds), mainGpu = Stats(bin, f => f.ViewportGpuMilliseconds),
                outerCpu = Stats(bin, f => f.FarViewportCpuMilliseconds), outerGpu = Stats(bin, f => f.FarViewportGpuMilliseconds),
                regionalCpu = Stats(bin, f => f.RegionalFarViewportCpuMilliseconds), regionalGpu = Stats(bin, f => f.RegionalFarViewportGpuMilliseconds) });
        return rows.ToArray();
    }

    private RingPerformanceTelemetry.Completed[] CopyFarCpuEvents()
    {
        var rows = new List<RingPerformanceTelemetry.Completed>();
        for (int index = 0; index < _eventCount; index++)
        {
            var sample = _events[(_eventHead + index) % _events.Length];
            if (!sample.Gpu && sample.Name.StartsWith("Far ring ", StringComparison.Ordinal)) rows.Add(sample);
        }
        return rows.ToArray();
    }

    private static object[] SummarizeFarCpu(IReadOnlyList<RingPerformanceTelemetry.Completed> events)
    {
        var groups = new SortedDictionary<string, List<RingPerformanceTelemetry.Completed>>();
        foreach (var sample in events)
        {
            if (!groups.TryGetValue(sample.Name, out var rows)) groups[sample.Name] = rows = new();
            rows.Add(sample);
        }
        var summaries = new List<object>();
        foreach (var (name, samples) in groups) summaries.Add(new { name, milliseconds = Stats(samples, sample => sample.Milliseconds) });
        return summaries.ToArray();
    }

    private static long FarCaptureBytes(string root)
    {
        long bytes = 0;
        foreach (string directory in Directory.GetDirectories(root, "ring-far-viewports-*"))
            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) bytes = checked(bytes + new FileInfo(file).Length);
        return bytes;
    }

    private static void WriteFarCaptureJson(string path, object measurement, FarCaptureImage[] images, long otherBytes)
    {
        bool pending = Array.Exists(images, item => item.Status == "pending");
        bool partial = Array.Exists(images, item => item.Status != "saved");
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1,
            reportStatus = pending ? "images pending" : partial ? "partial: image errors or unavailable targets" : "complete",
            imageErrors = Array.ConvertAll(Array.FindAll(images, item => item.Status != "saved"), item => new { item.Name, item.Status }), measurement,
            images = Array.ConvertAll(images, item => new { item.Name, item.Status, item.File, item.Bytes }) }, new JsonSerializerOptions { WriteIndented = true });
        if (otherBytes + json.Length > FarCaptureByteLimit) throw new IOException("JSON exceeds diagnostics capture budget.");
        File.WriteAllBytes(path, json);
    }

    private sealed class FarCaptureImage
    {
        public string Name { get; }
        public SubViewport? Target { get; }
        public Vector2I Size { get; }
        public string Status { get; set; } = "pending";
        public string? File { get; set; }
        public long? Bytes { get; set; }
        public FarCaptureImage(string name, SubViewport? target) { Name = name; Target = target;
            Size = target != null && GodotObject.IsInstanceValid(target) ? target.Size : default; }
    }
}
