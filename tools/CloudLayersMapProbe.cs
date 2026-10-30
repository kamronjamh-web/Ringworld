using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S6 WO2 test readback: the macro weather map's cloud layer array layer (low, mid, high coverage, weatherness) against the CPU twin
/// RingCloudLayers.EvaluateMap at 64 texels (32 spread over the map, 32 around the first manual storm). The CPU twin takes the map's own
/// coverage (layer R) as input, so the check isolates the new layer build; the cluster terms and systems come from the same uploaded blocks.
/// Used by tools/cloud_layers_map_gpu_test.gd (windowed Vulkan).
/// </summary>
public partial class CloudLayersMapProbe : RefCounted
{
    private const int Capacity = 1024; // RingMacroWeatherMap capacity (SunshineClouds.gd MACRO_MAP_CAPACITY.x)
    private byte[]? _cov;
    private byte[]? _layers;
    private Vector2 _origin;
    private float _texel;
    private Vector2I _size;
    private float _circumference;
    private Vector2 _shift;

    public string ErrorText { get; private set; } = "";
    public bool ReadbackReady { get; private set; }

    private static GodotObject? Clouds(Node world)
        => world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GetSunshineCloudsForTests();

    /// <summary>Completed macro map builds, or -1 when the clouds resource is missing.</summary>
    public long Builds(Node world) => Clouds(world)?.Get("macro_map_builds_completed").AsInt64() ?? -1;

    public bool Valid(Node world) => Clouds(world)?.Get("macro_map_valid").AsBool() ?? false;

    /// <summary>Queues the readback of the front coverage and layer slices on the render thread; ReadbackReady turns true a frame or two later.</summary>
    public void RequestReadback(Node world)
    {
        ReadbackReady = false;
        GodotObject? clouds = Clouds(world);
        if (clouds == null || !clouds.Get("macro_map_valid").AsBool()) { ErrorText = "macro map is not valid"; ReadbackReady = true; _cov = null; return; }
        Rid texture = clouds.Get("macro_map_texture").AsRid();
        uint front = (uint)clouds.Get("macro_map_front_layer").AsInt64();
        _origin = clouds.Get("macro_map_front_origin").AsVector2();
        _texel = (float)clouds.Get("macro_map_front_texel").AsDouble();
        _size = clouds.Get("macro_map_front_size").AsVector2I();
        _circumference = (float)clouds.Get("macro_map_front_circumference").AsDouble();
        Vector4 frame = clouds.Get("cloud_weather_frame").AsVector4();
        _shift = new Vector2(frame.Y, frame.Z);
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            _cov = rd.TextureGetData(texture, front);
            _layers = rd.TextureGetData(texture, front + 2);
            ReadbackReady = true;
        }));
    }

    /// <summary>Compares the 64 sample texels. Returns "PASS ..." or "FAIL ..."; the GPU values are RGBA16F, the tolerance 1e-3.</summary>
    public string Compare(Node world, float tolerance)
    {
        if (_cov == null || _layers == null) return "FAIL no readback: " + ErrorText;
        GodotObject? clouds = Clouds(world);
        if (clouds == null) return "FAIL no clouds resource";
        byte[] systemsBytes = clouds.Get("weather_systems_bytes").AsByteArray();
        byte[] layersBytes = clouds.Get("cloud_layers_bytes").AsByteArray();
        if (systemsBytes.Length < 64 || layersBytes.Length != RingCloudLayers.BlockBytes) return "FAIL blocks not uploaded";
        float F(byte[] bytes, int index) => BitConverter.ToSingle(bytes, index * 4);
        int count = Math.Clamp((int)(F(systemsBytes, 0) + 0.5f), 0, 64);
        float baseCoverage = F(systemsBytes, 2);
        float windAngle = F(systemsBytes, 7);
        var systems = new List<CloudLayerSystem>();
        float staticAround = float.NaN, staticAcross = float.NaN;
        for (int i = 0; i < count; i++)
        {
            int o = 16 + i * 16;
            uint flags = (uint)(F(systemsBytes, o + 11) + 0.5f);
            var system = new RingVolumetricCloudLayer.RenderWeatherSystem(
                0, RingVolumetricCloudLayer.WeatherSystemKind.None, F(systemsBytes, o), F(systemsBytes, o + 1), F(systemsBytes, o + 2), F(systemsBytes, o + 3),
                F(systemsBytes, o + 4), F(systemsBytes, o + 5), F(systemsBytes, o + 6), F(systemsBytes, o + 7), F(systemsBytes, o + 8), F(systemsBytes, o + 9),
                (CloudFormationProfile)(int)(F(systemsBytes, o + 10) + 0.5f), flags, F(systemsBytes, o + 12), F(systemsBytes, o + 13), F(systemsBytes, o + 15));
            systems.Add(new CloudLayerSystem(system, F(systemsBytes, o + 14)));
            if ((flags & RingVolumetricCloudLayer.RenderWeatherFlagStatic) != 0u && float.IsNaN(staticAround))
            { staticAround = (float)system.CenterAroundMeters; staticAcross = (float)system.CenterAcrossMeters; }
        }
        Vector3 sunLocal = new(F(layersBytes, 16), F(layersBytes, 17), F(layersBytes, 18));
        var table = CloudLayerTable.Shared;
        var layer = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");

        var texels = new List<Vector2I>();
        for (int i = 0; i < 32; i++)
            texels.Add(new Vector2I((i * 9973 + 17) % _size.X, (i * 7919 + 31) % _size.Y));
        if (!float.IsNaN(staticAround))
            for (int j = 0; j < 32; j++)
            {
                float angle = j * 2.39996323f;
                float radius = (j + 0.5f) / 32.0f * 80000.0f;
                float around = staticAround + MathF.Cos(angle) * radius;
                float across = staticAcross + MathF.Sin(angle) * radius;
                float tx = (around - _origin.X) / _texel;
                if (_circumference > 1.0f) tx -= _circumference / _texel * MathF.Floor(tx / (_circumference / _texel));
                texels.Add(new Vector2I(Math.Clamp((int)tx, 0, _size.X - 1), Math.Clamp((int)((across - _origin.Y) / _texel), 0, _size.Y - 1)));
            }
        else
            for (int j = 0; j < 32; j++) texels.Add(new Vector2I((j * 4001 + 5) % _size.X, (j * 2003 + 9) % _size.Y));

        float maxDifference = 0.0f, maxWeatherness = 0.0f, maxMid = 0.0f, maxHigh = 0.0f;
        string worst = "";
        foreach (Vector2I t in texels)
        {
            float cx = _origin.X + (t.X + 0.5f) * _texel;
            float cz = _origin.Y + (t.Y + 0.5f) * _texel;
            Vector4 gpuCov = Texel(_cov, t);
            Vector4 gpu = Texel(_layers, t);
            Vector2 source = new(cx + _shift.X, cz + _shift.Y);
            var point = new CloudLayerPoint(cx, cz, _shift, _circumference, gpuCov.X, baseCoverage,
                layer.SampleContinuousClusterTerm(source.X, source.Y, 0.0, table.MidClusterSalts.X, table.MidClusterSalts.Y),
                layer.SampleContinuousClusterTerm(source.X, source.Y, 0.0, table.HighClusterSalts.X, table.HighClusterSalts.Y),
                windAngle, sunLocal);
            Vector4 cpu = RingCloudLayers.EvaluateMap(point, systems, table);
            float difference = Math.Max(Math.Max(Math.Abs(cpu.X - gpu.X), Math.Abs(cpu.Y - gpu.Y)), Math.Max(Math.Abs(cpu.Z - gpu.Z), Math.Abs(cpu.W - gpu.W)));
            if (difference > maxDifference) { maxDifference = difference; worst = $"texel {t} cpu {cpu} gpu {gpu}"; }
            maxWeatherness = Math.Max(maxWeatherness, gpu.W);
            maxMid = Math.Max(maxMid, gpu.Y);
            maxHigh = Math.Max(maxHigh, gpu.Z);
        }
        if (maxDifference > tolerance) return $"FAIL max difference {maxDifference:G4} > {tolerance:G4} at {worst}";
        if (maxWeatherness < 0.2f) return $"FAIL no weatherness in the sample (max {maxWeatherness:F3}); the storm is not in the layer map";
        return $"PASS points={texels.Count} max_difference={maxDifference:G4} max_weatherness={maxWeatherness:F3} max_mid={maxMid:F3} max_high={maxHigh:F3} systems={count}";
    }

    private Vector4 Texel(byte[] data, Vector2I t)
    {
        int o = (t.Y * Capacity + t.X) * 8;
        return new Vector4(
            (float)BitConverter.ToHalf(data, o), (float)BitConverter.ToHalf(data, o + 2),
            (float)BitConverter.ToHalf(data, o + 4), (float)BitConverter.ToHalf(data, o + 6));
    }
}
