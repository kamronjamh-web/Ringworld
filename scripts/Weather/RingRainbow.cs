using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.LOD;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>
/// Pure rainbow geometry and rain illumination (docs/storm-revamp-s3-storm-arrival-design.md section 6). The antisolar point sits opposite the sun; the primary bow is
/// 40.6-42.4 deg from it (red outside), the secondary 50.3-53.5 deg (reversed, 0.43x). The bow is only visible while the sun is lower than 42.4 deg. Rain strength along
/// five azimuths of the visible arc comes from the sunlit rain standing at {0.5, 1.5, 3, 6, 10} km along each azimuth.
/// </summary>
public static class RingRainbowMath
{
    public const float PrimaryVioletDegrees = 40.6f, PrimaryRedDegrees = 42.4f, SecondaryRedDegrees = 50.3f, SecondaryVioletDegrees = 53.5f;
    /// <summary>Nominal bow radius used to place the azimuth samples (the primary band is 40.6-42.4 deg).</summary>
    public const float BowRadiusDegrees = 42.0f;
    public const float MaximumSunElevationDegrees = 42.4f;
    public const int AzimuthCount = 5;
    public static readonly float[] DistancesMeters = { 500.0f, 1500.0f, 3000.0f, 6000.0f, 10000.0f };
    public const float LitDepthMeters = 3000.0f;
    /// <summary>The lowest bow elevation (deg) still considered visible; below the horizon the bow only shows against rain in front of terrain.</summary>
    public const float LowestVisibleElevationDegrees = -5.0f;

    /// <summary>Where the bow is: antisolar direction, the perpendicular "top" and "side" axes and the half-arc of the visible part (radians).</summary>
    public readonly record struct Bow(bool Active, float SunElevationDegrees, Vector3 Antisolar, Vector3 Top, Vector3 Side, float HalfArcRadians);

    /// <summary>toSun and up are world directions (up is the local vertical); the antisolar direction is -toSun (the way a DirectionalLight travels).</summary>
    public static Bow Compute(Vector3 toSun, Vector3 up)
    {
        if (toSun.LengthSquared() < 1e-8f || up.LengthSquared() < 1e-8f) return new Bow(false, 0.0f, Vector3.Zero, Vector3.Zero, Vector3.Zero, 0.0f);
        toSun = toSun.Normalized(); up = up.Normalized();
        float elevation = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(toSun.Dot(up), -1.0f, 1.0f)));
        Vector3 anti = -toSun;
        Vector3 top = up - anti * up.Dot(anti);
        if (top.LengthSquared() < 1e-8f) return new Bow(false, elevation, anti, Vector3.Zero, Vector3.Zero, 0.0f);   // sun straight overhead or below
        top = top.Normalized();
        Vector3 side = anti.Cross(top).Normalized();
        bool active = elevation > 0.0f && elevation < MaximumSunElevationDegrees;
        // Bow point elevation: sin(el) = sin(a) cos(r) + cos(a) sin(r) cos(psi) with a = -sunElevation the antisolar elevation.
        float a = Mathf.DegToRad(-elevation), r = Mathf.DegToRad(BowRadiusDegrees);
        float lowest = Mathf.Sin(Mathf.DegToRad(LowestVisibleElevationDegrees));
        float denominator = Mathf.Cos(a) * Mathf.Sin(r);
        float cosPsi = denominator > 1e-6f ? (lowest - Mathf.Sin(a) * Mathf.Cos(r)) / denominator : -1.0f;
        float half = Mathf.Acos(Mathf.Clamp(cosPsi, -1.0f, 1.0f));
        return new Bow(active, elevation, anti, top, side, half);
    }

    /// <summary>The world direction of the point of the bow at azimuth psi (0 = top of the bow) and angular radius radiusDegrees from the antisolar point.</summary>
    public static Vector3 Direction(in Bow bow, float psiRadians, float radiusDegrees)
    {
        float r = Mathf.DegToRad(radiusDegrees);
        return (bow.Antisolar * Mathf.Cos(r) + (bow.Top * Mathf.Cos(psiRadians) + bow.Side * Mathf.Sin(psiRadians)) * Mathf.Sin(r)).Normalized();
    }

    public static float Psi(in Bow bow, int index) => AzimuthCount <= 1 ? 0.0f : Mathf.Lerp(-bow.HalfArcRadians, bow.HalfArcRadians, index / (float)(AzimuthCount - 1));

    /// <summary>
    /// Strength (0..1) of one azimuth from the wetness (0..1) of the rain at each of <see cref="DistancesMeters"/>. Each sample is lit by sun entering from the clear side,
    /// exp(-depth into the rain / 3 km), seen through the haze exp(-sigma s); rain above the cloud base along a rising line of sight does not count.
    /// </summary>
    public static float AzimuthStrength(ReadOnlySpan<float> wetness, float hazeSigmaPerMeter, float elevationRadians, float cloudBaseAglMeters)
    {
        float depth = 0.0f, previous = 0.0f, survive = 1.0f;
        float tan = elevationRadians > 0.001f ? Mathf.Tan(elevationRadians) : 0.0f;
        for (int k = 0; k < DistancesMeters.Length && k < wetness.Length; k++)
        {
            float s = DistancesMeters[k];
            float w = Mathf.Clamp(wetness[k], 0.0f, 1.0f);
            if (tan > 0.0f && s * tan > cloudBaseAglMeters) w = 0.0f;
            float lit = Mathf.Exp(-depth / LitDepthMeters);
            float transmittance = Mathf.Exp(-Mathf.Max(0.0f, hazeSigmaPerMeter) * s);
            survive *= 1.0f - w * lit * transmittance;
            depth += w * (s - previous);
            previous = s;
        }
        return Mathf.Clamp(1.0f - survive, 0.0f, 1.0f);
    }

    /// <summary>Per-system drop size factor 0.4-1.2: most storms give faint bows, some vivid ones.</summary>
    public static float DropSizeFactor(int systemId)
    {
        unchecked
        {
            uint h = (uint)systemId * 0x9E3779B1u; h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12;
            return 0.4f + 0.8f * ((h & 0xFFFFFF) / 16777216.0f);
        }
    }

    /// <summary>Lower edge of <see cref="SunReach"/>: just above the full-storm floor of the air sun visibility (1 - <see cref="RingAtmosphereModel.StormSunOcclusion"/> = 0.15),
    /// so no bow survives under a closed storm core (with 0.1 a 3-5% bow stayed visible through the whole core).</summary>
    public const float SunReachLow = 1.0f - RingAtmosphereModel.StormSunOcclusion + 0.03f;
    /// <summary>Global gate on the sun reaching the observer: direct-sun visibility (cloud shadow and storm occlusion), fading in between <see cref="SunReachLow"/> (0.18) and 0.6.</summary>
    public static float SunReach(float airSunVisibility) => StormArrivalMath.SmoothStep(SunReachLow, 0.6f, airSunVisibility);

    /// <summary>The 80-byte CPU payload of ring_rainbow.glsl: anti, top, side, five azimuth strengths, depth fade (near 30 m, range 200 m).</summary>
    public static byte[] Payload(in Bow bow, ReadOnlySpan<float> azimuthStrength, float globalStrength, float lightScale)
    {
        var bytes = new byte[80];
        int offset = 0;
        void Put(float v) { BitConverter.TryWriteBytes(new Span<byte>(bytes, offset, 4), v); offset += 4; }
        Put(bow.Antisolar.X); Put(bow.Antisolar.Y); Put(bow.Antisolar.Z); Put(bow.HalfArcRadians);
        Put(bow.Top.X); Put(bow.Top.Y); Put(bow.Top.Z); Put(globalStrength);
        Put(bow.Side.X); Put(bow.Side.Y); Put(bow.Side.Z); Put(lightScale);
        for (int i = 0; i < 5; i++) Put(i < azimuthStrength.Length ? azimuthStrength[i] : 0.0f);
        Put(30.0f); Put(200.0f); Put(0.0f);
        return bytes;
    }
}

/// <summary>
/// Rainbow driver (WO-F1): computes the bow geometry and the sunlit-rain strength along five azimuths at 2 Hz, publishes them to the full-screen additive compositor
/// effect RingRainbowComposite.gd and keeps that effect in its slot after Sunshine and before the rain streak and rain haze compositors every frame (no edit to the
/// rain owner's files: EnsureRainHazeCompositorLast only moves those two to the end). Rainbows 0 means no dispatch and no work. No GPU cost while the strength is 0.
/// </summary>
public partial class RingRainbow : Node
{
    [Export] public NodePath WeatherLayerPath { get; set; } = new("../RingVolumetricClouds");
    [Export] public NodePath SunLightPath { get; set; } = new("../StarLight");
    [Export] public NodePath GridPath { get; set; } = new("../ChunkGridManager");
    [Export] public NodePath WorldEnvironmentPath { get; set; } = new("../WorldEnvironment");
    [Export] public bool AutoBind { get; set; } = true;
    /// <summary>Peak additive brightness of the bow at full strength under a full-energy (4.0) sun.</summary>
    [Export(PropertyHint.Range, "0,2,0.01")] public float Brightness { get; set; } = 0.55f;
    [Export(PropertyHint.Range, "0.1,2,0.05")] public float UpdateSeconds { get; set; } = 0.5f;

    private RingVolumetricCloudLayer? _layer;
    private DirectionalLight3D? _sun;
    private VoxelChunkGridManager? _grid;
    private WorldEnvironment? _environment;
    private CompositorEffect? _effect;
    private double _accumulator = 1.0e9;
    private bool _published;
    /// <summary>Forced rain wetness for the capture harness: when 0 or more every azimuth sees this wetness at every distance (synthetic rain mask); negative = off.</summary>
    public float ForcedWetness { get; set; } = -1.0f;
    /// <summary>Forced air sun visibility for the harness (0 or more); negative = read it from the day-side atmosphere node.</summary>
    public float ForcedAirSunVisibility { get; set; } = -1.0f;
    public RingRainbowMath.Bow LastBow { get; private set; }
    public float[] LastStrengths { get; } = new float[RingRainbowMath.AzimuthCount];
    public float LastGlobalStrength { get; private set; }
    public bool Active { get; private set; }
    public double LastComputeMilliseconds { get; private set; }
    public int DispatchRequests { get; private set; }

    public override void _Ready()
    {
        if (AutoBind)
        {
            _layer = GetNodeOrNull<RingVolumetricCloudLayer>(WeatherLayerPath);
            _sun = GetNodeOrNull<DirectionalLight3D>(SunLightPath);
            _grid = GetNodeOrNull<VoxelChunkGridManager>(GridPath);
            _environment = GetNodeOrNull<WorldEnvironment>(WorldEnvironmentPath);
        }
        _effect = GD.Load<GDScript>("res://scripts/render/RingRainbowComposite.gd")?.New().AsGodotObject() as CompositorEffect;
    }

    public void Bind(RingVolumetricCloudLayer? layer, DirectionalLight3D? sun, VoxelChunkGridManager? grid, WorldEnvironment? environment)
    { _layer = layer; _sun = sun; _grid = grid; _environment = environment; }

    public override void _ExitTree()
    {
        DetachEffect();
        _effect = null;
    }

    public override void _Process(double delta)
    {
        if (_effect == null) return;
        _accumulator += delta;
        if (_accumulator >= UpdateSeconds)
        {
            _accumulator = 0.0;
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            Recompute();
            LastComputeMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
        if (Active) EnsureCompositorSlot();
    }

    private static string ScriptPath(GodotObject? o) => (o?.GetScript().AsGodotObject() as Script)?.ResourcePath ?? "";

    /// <summary>Keeps the effect immediately before the first rain streak / rain haze compositor (or last when neither exists), so Sunshine and anything else stay before it.</summary>
    public void EnsureCompositorSlot()
    {
        if (_effect == null) return;
        _environment ??= FindEnvironment();
        if (_environment == null || !GodotObject.IsInstanceValid(_environment)) return;
        _environment.Compositor ??= new Compositor();
        var effects = _environment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        int self = effects.IndexOf(_effect);
        int tail = TailIndex(effects);
        if (self >= 0 && self == tail - 1) return;
        if (self < 0 && tail == effects.Count && effects.Count == 0) { effects.Add(_effect); _environment.Compositor.CompositorEffects = effects; return; }
        if (self >= 0) { effects.RemoveAt(self); tail = TailIndex(effects); }
        effects.Insert(tail, _effect);
        _environment.Compositor.CompositorEffects = effects;
    }

    private static int TailIndex(Godot.Collections.Array<CompositorEffect> effects)
    {
        for (int i = 0; i < effects.Count; i++)
        {
            string path = ScriptPath(effects[i]);
            if (path.EndsWith("RingRainStreaksComposite.gd", StringComparison.Ordinal) || path.EndsWith("RingRainHazeComposite.gd", StringComparison.Ordinal)) return i;
        }
        return effects.Count;
    }

    private WorldEnvironment? FindEnvironment()
    {
        Viewport? viewport = GetViewport();
        if (viewport == null) return null;
        foreach (Node candidate in GetTree().Root.FindChildren("*", "WorldEnvironment", true, false))
            if (candidate is WorldEnvironment env && env.GetViewport() == viewport) return env;
        return null;
    }

    private void DetachEffect()
    {
        if (_environment == null || !GodotObject.IsInstanceValid(_environment) || _environment.Compositor == null || _effect == null) return;
        var effects = _environment.Compositor.CompositorEffects;
        using Godot.Collections.Array effectsLifetime = (Godot.Collections.Array)effects;
        int index = effects.IndexOf(_effect);
        if (index < 0) return;
        effects.RemoveAt(index);
        _environment.Compositor.CompositorEffects = effects;
    }

    private void Publish(bool active)
    {
        if (_effect == null) return;
        if (!active && !_published) return;
        _published = active;
        if (active)
        {
            float energy = _sun != null ? _sun.LightEnergy : 4.0f;
            float lightScale = Brightness * Mathf.Clamp(energy / 4.0f, 0.0f, 1.5f);
            _effect.Call("publish_frame", RingRainbowMath.Payload(LastBow, LastStrengths, LastGlobalStrength, lightScale), true);
            DispatchRequests++;
        }
        else _effect.Call("publish_frame", new byte[80], false);
    }

    private float ReadAirSunVisibility()
    {
        if (ForcedAirSunVisibility >= 0.0f) return ForcedAirSunVisibility;
        _environment ??= FindEnvironment();
        if (_environment != null && GodotObject.IsInstanceValid(_environment))
        {
            Variant v = _environment.Get("current_air_sun_visibility");
            if (v.VariantType == Variant.Type.Float) return v.AsSingle();
        }
        Node? atmosphere = GetParent()?.GetNodeOrNull("DaySideAtmosphere");
        if (atmosphere != null) { Variant v = atmosphere.Get("current_air_sun_visibility"); if (v.VariantType == Variant.Type.Float) return v.AsSingle(); }
        return 1.0f;
    }

    private readonly List<StormSystemGeometry> _systems = new(32);
    private readonly float[] _wetness = new float[RingRainbowMath.DistancesMeters.Length];

    private void Recompute()
    {
        Active = false; LastGlobalStrength = 0.0f; Array.Clear(LastStrengths);
        float setting = RingStormArrivalSettings.Current.Rainbows;
        if (setting <= 0.0f || _sun == null || _grid == null || !_sun.Visible) { Publish(false); return; }
        if (!_layer!.TryGetObserverCanonicalPosition(out RingCanonicalPosition position)) { Publish(false); return; }
        RingLodGeometrySnapshot geometry = _grid.CaptureLodGeometrySnapshot();
        Vector3 origin = geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters, position.AcrossMeters);
        Vector3 up = (geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters + 10.0, position.AcrossMeters) - origin).Normalized();
        Vector3 around = (geometry.CanonicalToWorld(position.AroundMeters + 10.0, position.VerticalMeters, position.AcrossMeters) - origin).Normalized();
        Vector3 across = (geometry.CanonicalToWorld(position.AroundMeters, position.VerticalMeters, position.AcrossMeters + 10.0) - origin).Normalized();
        LastBow = RingRainbowMath.Compute(_sun.GlobalTransform.Basis.Z, up);
        float sunReach = RingRainbowMath.SunReach(ReadAirSunVisibility());
        if (!LastBow.Active || sunReach <= 0.0f || _sun.LightEnergy <= 0.01f) { Publish(false); return; }

        StormArrivalTuning tuning = RingStormArrivalSettings.Current.ToTuning(RingVolumetricCloudLayer.StormArrivalPrecipitationStart, _layer.Coverage);
        _ = _layer.ObserverStormArrival;                              // refresh the observer cache
        _layer.CopyObserverStormGeometry(_systems);
        float cloudBaseAgl = _layer.ObserverCloudBaseAboveGroundMeters;
        float sigma = RingAtmosphereModel.HazeSigmaRayleigh.Y + RingAtmosphereModel.HazeSigmaAerosol + RingAtmosphereModel.HazeSigmaMist
            + RingAtmosphereModel.HazeStorm * RingAtmosphereModel.StormAerosolExtinctionPerMeter * Mathf.Clamp(RingAtmosphereModel.Density, 0.0f, 2.0f);
        for (int i = 0; i < RingRainbowMath.AzimuthCount; i++)
        {
            Vector3 d = RingRainbowMath.Direction(LastBow, RingRainbowMath.Psi(LastBow, i), RingRainbowMath.BowRadiusDegrees);
            float elevation = Mathf.Asin(Mathf.Clamp(d.Dot(up), -1.0f, 1.0f));
            var h = new Vector2(d.Dot(around), d.Dot(across));
            if (h.LengthSquared() < 1e-6f) { LastStrengths[i] = 0.0f; continue; }
            h = h.Normalized();
            int dominant = 0; float dominantWet = 0.0f;
            for (int k = 0; k < RingRainbowMath.DistancesMeters.Length; k++)
            {
                if (ForcedWetness >= 0.0f) { _wetness[k] = ForcedWetness; continue; }
                Vector2 delta = h * RingRainbowMath.DistancesMeters[k];
                float best = 0.0f;
                for (int s = 0; s < _systems.Count; s++)
                {
                    float proxy = StormArrivalMath.RainProxyAtObserver(_systems[s] with { OffsetMeters = _systems[s].OffsetMeters + delta }, tuning);
                    if (proxy > best) { best = proxy; if (proxy > dominantWet) { dominantWet = proxy; dominant = _systems[s].SystemId; } }
                }
                _wetness[k] = StormArrivalMath.Wetness(best, tuning);
            }
            float strength = RingRainbowMath.AzimuthStrength(_wetness, sigma, elevation, cloudBaseAgl);
            LastStrengths[i] = ForcedWetness >= 0.0f ? strength : strength * RingRainbowMath.DropSizeFactor(dominant);
        }
        LastGlobalStrength = Mathf.Clamp(setting, 0.0f, 2.0f) * sunReach;
        float peak = 0.0f; foreach (float s in LastStrengths) peak = Mathf.Max(peak, s);
        Active = peak * LastGlobalStrength > 0.002f;
        Publish(Active);
    }
}
