using System;
using System.Collections.Generic;
using System.Threading;
using Godot;

namespace RingworldTechDemo.Diagnostics;

/// <summary>Non-blocking main-RD timestamp bridge. Samples are asynchronous partial GPU intervals, never frame totals.</summary>
public static class RingPerformanceGpu
{
    public const string WaterFft = "WaterFft", Vegetation = "Vegetation", TreeCulling = "TreeCulling", GrassCulling = "GrassCulling", Clouds = "Clouds", CloudPrepass = "CloudPrepass", CloudRaymarch = "CloudRaymarch", CloudReconstruct = "CloudReconstruct", CloudComposite = "CloudComposite";
    private const string Prefix = "RingPerfGpu:";
    private const int MaximumCSharpMarkersPerFrame = 30, MaximumTimestampScan = 512;
    private static int _enabled, _generation, _pendingGeneration, _markersThisFrame;
    private static long _lastCapturedFrame = -1;
    private static string _status = "Disabled";
    public static bool Enabled => Volatile.Read(ref _enabled) != 0;
    public static string Status => Volatile.Read(ref _status);
    public static string CloudBeginMarker => Marker(Volatile.Read(ref _generation), Clouds, 'B');
    public static string CloudEndMarker => Marker(Volatile.Read(ref _generation), Clouds, 'E');

    public static void Configure(bool enabled)
    {
        int value = enabled ? 1 : 0;
        if (Interlocked.Exchange(ref _enabled, value) == value) return;
        Interlocked.Increment(ref _generation); Interlocked.Exchange(ref _lastCapturedFrame, -1);
        // The callback that acquired this slot clears it in finally. Never clear it here.
        Volatile.Write(ref _status, enabled ? "Waiting for render-device timestamp readback" : "Disabled");
    }

    /// <summary>Construct/dispose on the render thread, outside active RD lists.</summary>
    public static GpuScope BeginScope(string category)
    {
        if (!Enabled || !KnownCategory(category)) return default;
        if (Interlocked.Add(ref _markersThisFrame, 2) > MaximumCSharpMarkersPerFrame)
        { Interlocked.Add(ref _markersThisFrame, -2); Volatile.Write(ref _status, "Timestamp marker budget reached"); return default; }
        int generation = Volatile.Read(ref _generation); string begin = Marker(generation, category, 'B'), end = Marker(generation, category, 'E');
        RenderingDevice? rd = RenderingServer.GetRenderingDevice();
        if (rd is null) { Interlocked.Add(ref _markersThisFrame, -2); Volatile.Write(ref _status, "Unavailable: no main RenderingDevice"); return default; }
        try { rd.CaptureTimestamp(begin); return new GpuScope(rd, end); }
        catch (Exception error) { Interlocked.Add(ref _markersThisFrame, -2); Volatile.Write(ref _status, "Timestamp capture unavailable: " + error.GetType().Name); return default; }
    }

    public readonly struct GpuScope : IDisposable
    {
        private readonly RenderingDevice? _rd; private readonly string? _endMarker;
        internal GpuScope(RenderingDevice rd, string endMarker) { _rd = rd; _endMarker = endMarker; }
        public void Dispose() { if (_rd is null || _endMarker is null) return; try { _rd.CaptureTimestamp(_endMarker); } catch (Exception error) { Volatile.Write(ref _status, "Timestamp capture unavailable: " + error.GetType().Name); } }
    }

    public static void RequestReadback()
    {
        if (!Enabled) return;
        int generation = Volatile.Read(ref _generation);
        if (Interlocked.CompareExchange(ref _pendingGeneration, generation, 0) != 0) return;
        RenderingServer.CallOnRenderThread(Callable.From(() => ReadbackOnRenderThread(generation)));
    }

    private static void ReadbackOnRenderThread(int callbackGeneration)
    {
        try
        {
            if (!Enabled || callbackGeneration != Volatile.Read(ref _generation)) return;
            RenderingDevice? rd = RenderingServer.GetRenderingDevice();
            if (rd is null) { Volatile.Write(ref _status, "Unavailable: no main RenderingDevice"); return; }
            ulong frame = rd.GetCapturedTimestampsFrame();
            if (frame > long.MaxValue) { Volatile.Write(ref _status, "Unavailable: invalid captured timestamp frame"); return; }
            long sourceFrame = (long)frame;
            if (sourceFrame <= Volatile.Read(ref _lastCapturedFrame)) { Volatile.Write(ref _status, "Waiting for fresh GPU timestamp results"); return; }
            uint count = rd.GetCapturedTimestampsCount(), first = count > MaximumTimestampScan ? count - MaximumTimestampScan : 0;
            bool truncated = first != 0; var starts = new Dictionary<string, List<ulong>>(3, StringComparer.Ordinal); var totals = new Dictionary<string, double>(3, StringComparer.Ordinal); int scopes = 0;
            for (uint i = first; i < count; i++)
            {
                string name = rd.GetCapturedTimestampName(i);
                if (!TryParseMarker(name, callbackGeneration, out string category, out char boundary)) continue;
                ulong timestamp = rd.GetCapturedTimestampGpuTime(i);
                if (boundary == 'B') { if (!starts.TryGetValue(category, out List<ulong>? stack)) starts.Add(category, stack = new List<ulong>(4)); if (stack.Count < MaximumCSharpMarkersPerFrame / 2) stack.Add(timestamp); continue; }
                if (!starts.TryGetValue(category, out List<ulong>? begins) || begins.Count == 0) continue;
                ulong begin = begins[^1]; begins.RemoveAt(begins.Count - 1);
                // Driver timestamps are nanoseconds: rendering_device.cpp timestamp_query_result_to_time / Vulkan timestampPeriod.
                if (timestamp <= begin) continue;
                double milliseconds = (timestamp - begin) / 1_000_000d;
                if (!double.IsFinite(milliseconds) || milliseconds < 0) continue;
                totals[category] = totals.GetValueOrDefault(category) + milliseconds; scopes++;
            }
            if (!Enabled || callbackGeneration != Volatile.Read(ref _generation)) return;
            foreach ((string category, double milliseconds) in totals) RingPerformanceTelemetry.RecordGpuMilliseconds("GPU." + category, milliseconds, sourceFrame);
            Volatile.Write(ref _lastCapturedFrame, sourceFrame);
            string tail = truncated ? $"; inspected tail {MaximumTimestampScan}/{count}" : string.Empty;
            Volatile.Write(ref _status, scopes > 0 ? $"GPU timestamps: {scopes} partial interval(s), source frame {sourceFrame}{tail}" : $"Fresh GPU timestamps contained no complete owned scopes{tail}");
        }
        catch (Exception error) { Volatile.Write(ref _status, "Timestamp readback unavailable: " + error.GetType().Name); }
        finally { if (Interlocked.CompareExchange(ref _pendingGeneration, 0, callbackGeneration) == callbackGeneration) Interlocked.Exchange(ref _markersThisFrame, 0); }
    }

    private static string Marker(int generation, string category, char boundary) => Prefix + generation + ":" + category + ":" + boundary;
    private static bool KnownCategory(string category) => category is WaterFft or Vegetation or TreeCulling or GrassCulling or Clouds or CloudPrepass or CloudRaymarch or CloudReconstruct or CloudComposite;
    private static bool TryParseMarker(string name, int expectedGeneration, out string category, out char boundary)
    {
        category = string.Empty; boundary = '\0'; if (!name.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string[] parts = name.Split(':');
        if (parts.Length != 4 || !int.TryParse(parts[1], out int generation) || generation != expectedGeneration) return false;
        category = parts[2]; boundary = parts[3].Length == 1 ? parts[3][0] : '\0'; return (boundary is 'B' or 'E') && KnownCategory(category);
    }
}
