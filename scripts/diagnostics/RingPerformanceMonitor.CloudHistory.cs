using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Integration;

namespace RingworldTechDemo.Diagnostics;

public sealed record RingCloudHistorySample(
    double CpuMonotonicSeconds, ulong CpuProcessFrame, double?[]? CameraWorld,
    double?[]? CameraForward, double?[]? CameraUp, double? CanonicalAround,
    double? CanonicalAcross, double? CanonicalHeight, double?[] ViewportSize,
    float? RenderScale, Dictionary<string, object?> Clouds,
    Dictionary<string, object?> Weather, Dictionary<string, object?> Renderer,
    Dictionary<string, long> Memory, bool WindowFocused, string Caveat);

public readonly record struct RingCloudHistoryDiagnostics(
    int Count, int Capacity, double? OldestSeconds, double? NewestSeconds,
    string Status, string TimeDomainNote, int CaptureErrors);

public partial class RingPerformanceMonitor
{
    private const double CloudHistoryPeriodSeconds = .2;
    private const int CloudHistoryBytesPerSlot = 8 * 1024;
    private RingCloudHistorySample?[] _cloudHistory = Array.Empty<RingCloudHistorySample?>();
    private int _cloudHistoryHead, _cloudHistoryCount, _cloudHistoryErrors;
    private double _cloudHistoryElapsed;
    private RingSunshineCloudBridge? _cloudHistoryBridge;
    private Node? _cloudHistoryDriver, _cloudHistoryWeather;

    public long CloudHistoryEstimatedBytes => (long)_cloudHistory.Length * CloudHistoryBytesPerSlot;
    public RingCloudHistoryDiagnostics CloudHistoryDiagnostics => new(
        _cloudHistoryCount, _cloudHistory.Length,
        _cloudHistoryCount == 0 ? null : _cloudHistory[_cloudHistoryHead]?.CpuMonotonicSeconds,
        _cloudHistoryCount == 0 ? null : _cloudHistory[(_cloudHistoryHead + _cloudHistoryCount - 1) % _cloudHistory.Length]?.CpuMonotonicSeconds,
        _cloudHistoryBridge == null ? "unavailable: SunshineCloudBridge missing" : "active",
        "CPU clock/process frame; not GPU-paired.", _cloudHistoryErrors);

    public void UpdateCloudHistory(double delta)
    {
        EnsureCloudHistoryCapacity();
        _cloudHistoryElapsed += Math.Max(0, delta);
        if (_cloudHistoryElapsed < CloudHistoryPeriodSeconds) return;
        _cloudHistoryElapsed = 0;
        TrimCloudHistory(_clock);
        try { CaptureCloudHistory(); }
        catch (Exception error) { _cloudHistoryErrors++; GD.PushWarning("Cloud history: " + error.Message); }
    }

    public void ClearCloudHistory()
    {
        Array.Clear(_cloudHistory);
        _cloudHistoryHead = _cloudHistoryCount = _cloudHistoryErrors = 0;
        _cloudHistoryElapsed = 0;
    }

    public RingCloudHistorySample[] CopyCloudHistory()
    {
        RingCloudHistorySample[] copy = new RingCloudHistorySample[_cloudHistoryCount];
        for (int i = 0; i < copy.Length; i++) copy[i] = _cloudHistory[(_cloudHistoryHead + i) % _cloudHistory.Length]!;
        return copy;
    }

    internal void InjectCloudSampleForTests(RingCloudHistorySample sample) => AddCloudHistorySample(sample);

    private void EnsureCloudHistoryCapacity()
    {
        int budgetCapacity = MemoryBudgetMiB <= 2 ? 64 : MemoryBudgetMiB <= 4 ? 128 : 384;
        int timeCapacity = (int)Math.Ceiling(Math.Clamp(HistorySeconds, 1, 60) / CloudHistoryPeriodSeconds);
        int capacity = Math.Clamp(Math.Min(budgetCapacity, timeCapacity), 1, 384);
        if (_cloudHistory.Length == capacity) return;
        RingCloudHistorySample[] retained = CopyCloudHistory();
        _cloudHistory = new RingCloudHistorySample?[capacity];
        _cloudHistoryHead = _cloudHistoryCount = 0;
        for (int i = Math.Max(0, retained.Length - capacity); i < retained.Length; i++) AddCloudHistorySample(retained[i]);
    }

    private void CaptureCloudHistory()
    {
        Node? root = GetParent();
        if (_cloudHistoryBridge == null || !GodotObject.IsInstanceValid(_cloudHistoryBridge))
            _cloudHistoryBridge = root?.GetNodeOrNull<RingSunshineCloudBridge>("SunshineCloudBridge");
        if (_cloudHistoryDriver == null || !GodotObject.IsInstanceValid(_cloudHistoryDriver))
            _cloudHistoryDriver = root?.GetNodeOrNull<Node>("SunshineCloudsDriver");
        if (_cloudHistoryWeather == null || !GodotObject.IsInstanceValid(_cloudHistoryWeather))
            _cloudHistoryWeather = root?.GetNodeOrNull<Node>("RingVolumetricClouds");

        GodotObject? resource = GetObject(_cloudHistoryDriver, "clouds_resource");
        Camera3D? camera = GetViewport().GetCamera3D();
        var canonical = _contextExplorer?.CanonicalPosition;
        Dictionary<string, object?> clouds = CaptureClouds(resource);
        Dictionary<string, object?> weather = CaptureWeather();
        Dictionary<string, object?> renderer = _cloudHistoryBridge == null
            ? new() { ["status"] = "unavailable" }
            : ConvertDictionary(_cloudHistoryBridge.GetCloudRendererDiagnostics(), 0);
        Dictionary<string, long> memory = new()
        {
            ["video_total_bytes"] = (long)Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed),
            ["texture_bytes"] = (long)Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed),
            ["buffer_bytes"] = (long)Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed),
            ["pipeline_mesh"] = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsMesh),
            ["pipeline_surface"] = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSurface),
            ["pipeline_draw"] = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsDraw),
            ["pipeline_specialization"] = (long)RenderingServer.GetRenderingInfo(RenderingServer.RenderingInfo.PipelineCompilationsSpecialization)
        };
        AddCloudHistorySample(new RingCloudHistorySample(
            _clock, Engine.GetProcessFrames(), camera == null ? null : CloudVector(camera.GlobalPosition),
            camera == null ? null : CloudVector(-camera.GlobalBasis.Z), camera == null ? null : CloudVector(camera.GlobalBasis.Y),
            CloudFinite(canonical?.AroundMeters), CloudFinite(canonical?.AcrossMeters), CloudFinite(canonical?.VerticalMeters),
            CloudVector(GetViewport().GetVisibleRect().Size), CloudFinite(GetViewport().Scaling3DScale), clouds, weather,
            renderer, memory, DisplayServer.WindowIsFocused(), "CPU capture; explicitly not GPU-paired."));
    }

    private Dictionary<string, object?> CaptureClouds(GodotObject? resource)
    {
        Dictionary<string, object?> clouds = new()
        {
            ["resource_enabled"] = Bool(resource, "enabled"),
            ["bridge_runtime_visible"] = _cloudHistoryBridge?.RuntimeVisible,
            ["bridge_requested"] = _cloudHistoryBridge?.CloudRenderingEnabled,
            ["effective_history_weight"] = CloudFinite(_cloudHistoryBridge?.EffectiveHistoryWeight),
            ["applied_history_weight"] = CloudFinite(_cloudHistoryBridge?.AppliedHistoryWeight),
            ["camera_history_motion"] = CloudFinite(_cloudHistoryBridge?.CameraHistoryMotion),
            ["temporal_apply_count"] = _cloudHistoryBridge?.TemporalHistoryApplyCount,
            ["shadow_generation_enabled"] = _cloudHistoryBridge?.GpuShadowGenerationEnabled,
            ["shadow_receivers_enabled"] = _cloudHistoryBridge?.CloudShadowReceiversEnabled,
            ["shadow_status"] = _cloudHistoryBridge?.GpuCloudShadowStatus,
            ["requested_raymarch_steps"] = _cloudHistoryBridge?.RaymarchSteps,
            ["requested_lighting_steps"] = _cloudHistoryBridge?.LightingSteps,
            ["authoritative_clock"] = CloudNumber(_cloudHistoryWeather, "CloudSimulationTimeSeconds")
        };
        foreach (string name in new[] { "current_time", "resolution_scale", "max_step_count", "max_lighting_steps", "clouds_density", "clouds_coverage", "clouds_sharpness", "cloud_floor", "cloud_ceiling" }) clouds[name] = CloudNumber(resource, name);
        foreach (string name in new[] { "background_altitude", "storm_altitude" }) clouds[name] = VectorValue(resource?.Get(name) ?? default);
        foreach (string name in new[] { "extra_large_scale_clouds_position", "large_scale_clouds_position", "medium_scale_clouds_position", "detail_clouds_position" }) clouds[name] = VectorValue(resource?.Get(name) ?? default);
        return clouds;
    }

    private Dictionary<string, object?> CaptureWeather()
    {
        if (_cloudHistoryBridge == null) return new() { ["status"] = "unavailable" };
        var weather = _cloudHistoryBridge.CurrentWeather;
        return new() { ["coverage"] = CloudFinite(weather.CloudCoverage), ["wind"] = CloudVector(weather.WindMetersPerSecond), ["authoritative_clock"] = CloudNumber(_cloudHistoryWeather, "CloudSimulationTimeSeconds") };
    }

    private void AddCloudHistorySample(RingCloudHistorySample sample)
    {
        if (_cloudHistory.Length == 0) return;
        if (_cloudHistoryCount == _cloudHistory.Length)
        {
            _cloudHistory[_cloudHistoryHead] = null;
            _cloudHistoryHead = (_cloudHistoryHead + 1) % _cloudHistory.Length;
            _cloudHistoryCount--;
        }
        _cloudHistory[(_cloudHistoryHead + _cloudHistoryCount) % _cloudHistory.Length] = sample;
        _cloudHistoryCount++;
    }

    private void TrimCloudHistory(double now)
    {
        double cutoff = now - Math.Clamp(HistorySeconds, 1, 60);
        while (_cloudHistoryCount > 0 && (_cloudHistory[_cloudHistoryHead]?.CpuMonotonicSeconds ?? now) < cutoff)
        {
            _cloudHistory[_cloudHistoryHead] = null;
            _cloudHistoryHead = (_cloudHistoryHead + 1) % _cloudHistory.Length;
            _cloudHistoryCount--;
        }
    }

    private static GodotObject? GetObject(Node? node, string property)
    {
        if (node == null || !GodotObject.IsInstanceValid(node)) return null;
        Variant value = node.Get(property);
        return value.VariantType == Variant.Type.Object ? value.AsGodotObject() : null;
    }

    private static object? CloudNumber(GodotObject? source, string property)
    {
        if (source == null || !GodotObject.IsInstanceValid(source)) return null;
        Variant value = source.Get(property);
        return value.VariantType is Variant.Type.Float or Variant.Type.Int ? CloudFinite(value.AsDouble()) : null;
    }

    private static double? CloudFinite(double? value) => value.HasValue && double.IsFinite(value.Value) ? value : null;
    private static float? CloudFinite(float value) => float.IsFinite(value) ? value : null;
    private static double?[] CloudVector(Vector2 value) => new[] { CloudFinite((double)value.X), CloudFinite((double)value.Y) };
    private static double?[] CloudVector(Vector3 value) => new[] { CloudFinite((double)value.X), CloudFinite((double)value.Y), CloudFinite((double)value.Z) };

    private static object? VectorValue(Variant value) => value.VariantType switch
    {
        Variant.Type.Vector2I => new[] { value.AsVector2I().X, value.AsVector2I().Y },
        Variant.Type.Vector2 => CloudVector(value.AsVector2()),
        Variant.Type.Vector3 => CloudVector(value.AsVector3()),
        Variant.Type.Vector4 => new[] { CloudFinite((double)value.AsVector4().X), CloudFinite((double)value.AsVector4().Y), CloudFinite((double)value.AsVector4().Z), CloudFinite((double)value.AsVector4().W) },
        _ => null
    };

    private static Dictionary<string, object?> ConvertDictionary(Godot.Collections.Dictionary source, int depth)
    {
        Dictionary<string, object?> result = new();
        if (depth > 3) return result;
        int count = 0;
        foreach (var pair in source)
        {
            if (count++ >= 48 || pair.Key.VariantType != Variant.Type.String) continue;
            result[pair.Key.AsString()] = ConvertVariant(pair.Value, depth + 1);
        }
        return result;
    }

    private static object? ConvertVariant(Variant value, int depth) => value.VariantType switch
    {
        Variant.Type.Bool => value.AsBool(), Variant.Type.String => value.AsString(), Variant.Type.Int => value.AsInt64(),
        Variant.Type.Float => CloudFinite(value.AsDouble()), Variant.Type.Vector2I or Variant.Type.Vector2 or Variant.Type.Vector3 or Variant.Type.Vector4 => VectorValue(value),
        Variant.Type.Dictionary when depth <= 3 => ConvertDictionary(value.AsGodotDictionary(), depth),
        Variant.Type.Array when depth <= 3 => ConvertArray(value.AsGodotArray(), depth), _ => null
    };

    private static object?[] ConvertArray(Godot.Collections.Array source, int depth)
    {
        int count = Math.Min(source.Count, 32);
        object?[] result = new object?[count];
        for (int i = 0; i < count; i++) result[i] = ConvertVariant(source[i], depth + 1);
        return result;
    }
}

