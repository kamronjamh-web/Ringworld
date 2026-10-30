using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Tools;

/// <summary>
/// S6 WO3b test helper (tools/cloud_layers_gpu_test.gd, tools/cloud_layers_capture.gd): vertical SamplePoints density columns through the
/// real Sunshine compute, the macro map's layer values at a canonical position (readback), and manual weather placement for the fixed poses.
/// Canonical positions are (around, across) metres on the ring; altitudes are cloud-space altitudes (the Sunshine band's own axis).
/// </summary>
public partial class CloudLayersProbe : RefCounted
{
    private const int Capacity = 1024; // RingMacroWeatherMap capacity (SunshineClouds.gd MACRO_MAP_CAPACITY.x)
    private const string OwnerKey = "cloud_layers_probe";
    private const int SystemId = -1004;
    private const double BaseAround = 8150667.255809009;
    private const double BaseAcross = 119474.33742621914;

    private float[] _results = Array.Empty<float>();
    private int _pending;
    private byte[]? _layers;
    private Vector2 _origin;
    private float _texel;
    private Vector2I _size;
    private float _circumference;

    public string ErrorText { get; private set; } = "";
    public bool ReadbackReady { get; private set; }
    public bool ColumnReady => _pending == 0;

    public double BaseAroundMeters => BaseAround;
    public double BaseAcrossMeters => BaseAcross;

    private static GodotObject? Clouds(Node world)
        => world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge").GetSunshineCloudsForTests();

    public long Builds(Node world) => Clouds(world)?.Get("macro_map_builds_completed").AsInt64() ?? -1;

    private static double Wrap(Node world, double around)
    {
        double circumference = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").GetCanonicalRingCircumferenceMeters64();
        around %= circumference;
        return around < 0.0 ? around + circumference : around;
    }

    /// <summary>
    /// Queues one SamplePoints probe per altitude at the canonical (around, across) position; ColumnReady turns true when every result is back.
    /// Results are the shader's pow(sampleScene * densityMultiplier, sharpness) at lod 1 (the same density the primary march uses).
    /// </summary>
    public bool RequestColumn(Node world, double around, double across, Godot.Collections.Array altitudes)
    {
        GodotObject? clouds = Clouds(world);
        var bridge = world.GetNode<RingSunshineCloudBridge>("SunshineCloudBridge");
        if (clouds == null) { ErrorText = "no clouds resource"; return false; }
        _results = new float[altitudes.Count];
        _pending = altitudes.Count;
        around = Wrap(world, around);
        for (int i = 0; i < altitudes.Count; i++)
        {
            int index = i;
            Vector3 point = bridge.GetStormProbeWorldPosition(around, altitudes[i].AsDouble(), across);
            clouds.Call("add_sample", Callable.From<Vector3, float>((position, density) => { _results[index] = density; _pending--; }), point);
        }
        return true;
    }

    public float Result(int index) => _results[index];

    /// <summary>The results of the last column as one comma-separated line, for logs.</summary>
    public string ResultLine()
    {
        var parts = new List<string>();
        foreach (float value in _results) parts.Add(value.ToString("0.00"));
        return string.Join(",", parts);
    }

    /// <summary>Queues the readback of the front macro map's layer slice (low, mid, high coverage, weatherness).</summary>
    public void RequestReadback(Node world)
    {
        ReadbackReady = false;
        GodotObject? clouds = Clouds(world);
        if (clouds == null || !clouds.Get("macro_map_valid").AsBool()) { ErrorText = "macro map is not valid"; ReadbackReady = true; _layers = null; return; }
        Rid texture = clouds.Get("macro_map_texture").AsRid();
        uint front = (uint)clouds.Get("macro_map_front_layer").AsInt64();
        _origin = clouds.Get("macro_map_front_origin").AsVector2();
        _texel = (float)clouds.Get("macro_map_front_texel").AsDouble();
        _size = clouds.Get("macro_map_front_size").AsVector2I();
        _circumference = (float)clouds.Get("macro_map_front_circumference").AsDouble();
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            RenderingDevice rd = RenderingServer.GetRenderingDevice();
            _layers = rd.TextureGetData(texture, front + 2);
            ReadbackReady = true;
        }));
    }

    /// <summary>The map's (low, mid, high, weatherness) at a canonical position from the last readback; (-1, -1, -1, -1) outside the map.</summary>
    public Vector4 LayersAt(double around, double across)
    {
        if (_layers == null) return new Vector4(-1, -1, -1, -1);
        double dx = around - _origin.X;
        if (_circumference > 1.0f) dx -= _circumference * Math.Floor(dx / _circumference);
        int tx = (int)(dx / _texel);
        int ty = (int)((across - _origin.Y) / _texel);
        if (tx < 0 || ty < 0 || tx >= _size.X || ty >= _size.Y) return new Vector4(-1, -1, -1, -1);
        int o = (ty * Capacity + tx) * 8;
        return new Vector4((float)BitConverter.ToHalf(_layers, o), (float)BitConverter.ToHalf(_layers, o + 2),
            (float)BitConverter.ToHalf(_layers, o + 4), (float)BitConverter.ToHalf(_layers, o + 6));
    }

    /// <summary>
    /// Registers one static manual system (kind ExceptionalFront) of the given profile at an offset from the fixed camera position:
    /// Nimbostratus for the overcast / warm-front core (lowers the base, closed deck 500 m to <paramref name="topMeters"/>), AnvilStorm for the storm.
    /// <paramref name="precipitation"/> is the system's precipitation potential (0 for a dry cirrostratus bank) and
    /// <paramref name="coverage"/> its cloud coverage, which also raises the macro coverage under it (low it for a high-layer-only bank).
    /// </summary>
    public bool PlaceWeather(Node world, int profile, double offsetAround, double offsetAcross, double radiusMeters, double topMeters, int kind = 4, double precipitation = 0.94, double coverage = 0.97)
    {
        try
        {
            var clouds = world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds");
            float radius = (float)radiusMeters;
            clouds.RegisterManualWeather(OwnerKey, new RingVolumetricCloudLayer.LocalWeatherState(
                SystemId, (RingVolumetricCloudLayer.WeatherSystemKind)kind, 1.0f, radius * 1.25f,
                new Vector2((float)Wrap(world, BaseAround + offsetAround), (float)(BaseAcross + offsetAcross)), new Vector2(radius * 1.25f, radius),
                0.0f, (float)coverage, (float)precipitation, Vector2.Zero, (CloudFormationProfile)profile, 500.0f, (float)topMeters) { InheritWind = true });
            return true;
        }
        catch (Exception error) { ErrorText = error.ToString(); return false; }
    }

    /// <summary>Presentation terrain height (sea surface where lower) at an offset from the base position, canonical metres; NaN without a sampler.</summary>
    public float TerrainHeight(Node world, double offsetAround, double offsetAcross)
    {
        var sampler = world.GetNode<VoxelChunkGridManager>("ChunkGridManager").TerrainSampler;
        if (sampler == null) return float.NaN;
        double around = Wrap(world, BaseAround + offsetAround);
        return Math.Max(sampler.GetPresentationSurfaceHeight(around, BaseAcross + offsetAcross), sampler.SeaLevelY);
    }

    /// <summary>
    /// (yaw, pitch) in degrees that point the camera at the sun, for a camera placed at yaw 0 / pitch 0 one frame earlier (its basis is the
    /// explorer's local frame). The sun is the brightest visible DirectionalLight3D; light travels along its -Z, so the sun lies along +Z.
    /// </summary>
    public Vector2 SunYawPitch(Node world)
    {
        Camera3D? camera = world.GetViewport().GetCamera3D();
        DirectionalLight3D? sun = null;
        foreach (Node node in world.FindChildren("*", "DirectionalLight3D", true, false))
            if (node is DirectionalLight3D light && light.Visible && (sun == null || light.LightEnergy > sun.LightEnergy)) sun = light;
        if (camera == null || sun == null) return new Vector2(float.NaN, float.NaN);
        Vector3 toSun = sun.GlobalBasis.Z.Normalized();
        Basis basis = camera.GlobalBasis.Orthonormalized();
        Vector3 forward = -basis.Z, right = basis.X, up = basis.Y;
        float yaw = Mathf.RadToDeg(Mathf.Atan2(-toSun.Dot(right), toSun.Dot(forward)));
        float pitch = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(toSun.Dot(up), -1.0f, 1.0f)));
        return new Vector2(yaw, pitch);
    }

    public void ClearWeather(Node world)
        => world.GetNode<RingVolumetricCloudLayer>("RingVolumetricClouds").UnregisterManualWeather(OwnerKey);
}
