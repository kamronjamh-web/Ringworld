using System;
using Godot;
using RingworldTechDemo.Voxel.Core;
using RingworldTechDemo.Voxel.Phase2;

namespace RingworldTechDemo.Weather;

/// <summary>One deterministic canonical gust field;
/// CPU samples use the same uploaded bytes.</summary>
public sealed class RingWindField : IDisposable
{
    public readonly record struct PublishedFrame(Texture2D? Texture, bool Enabled, Vector3 Anchor, Vector4 Around, Vector4 Across, Vector2 Uv, float Base, float Strength);
    /// <summary>Exact already-published GPU frame; no server reads or recomputed covectors.</summary>
    public PublishedFrame CapturePublishedFrame()
    {
        if (!lastGlobals.ContainsKey("ring_wind_anchor_world")) return default;
        Variant Read(string key) => lastGlobals.TryGetValue(key, out Variant value) ? value : default;
        Variant textureValue = Read("ring_wind_gust_texture");
        return new(textureValue.VariantType == Variant.Type.Object ? textureValue.AsGodotObject() as Texture2D : null,
            Read("ring_wind_field_enabled").AsSingle() > .5f, Read("ring_wind_anchor_world").AsVector3(),
            Read("ring_wind_around_covector").AsVector4(), Read("ring_wind_across_covector").AsVector4(),
            Read("ring_wind_anchor_uv").AsVector2(), Read("ring_wind_gust_base").AsSingle(), Read("ring_wind_gust_strength").AsSingle());
    }
    const int Size = 512;
    readonly System.Collections.Generic.Dictionary<string, Variant> lastGlobals = new();
    int currentSeed = int.MinValue;
    double integratedX, integratedY, lastTime = double.NaN;
    Vector2 previousTransport;
    readonly byte[] pixels = new byte[Size * Size];
    ImageTexture? texture;
    public bool Ready => texture != null;
    public string AtlasFingerprint { get; private set; } = "";
    public void InvalidatePublication() => lastGlobals.Clear();
    public void Dispose()
    {
        texture?.Dispose();
        texture = null;
        lastGlobals.Clear();
    }
    public void SetCached(string name, Variant value)
    {
        if (lastGlobals.TryGetValue(name, out var old) && old.Equals(value)) return;
        lastGlobals[name] = value;
        RenderingServer.GlobalShaderParameterSet(name, value);
    }
    public double AroundPeriodMeters { get; private set; } = 600;
    public double AcrossPeriodMeters { get; private set; } = 600;
    public Vector2 OffsetMeters { get; private set; }
    public Vector2 VelocityMetersPerSecond { get; private set; }
    public double SimulationTime { get; private set; }
    public RingWindField()
    {
        SetSeed(1337);
    }
    public void SetSeed(int seed)
    {
        if (seed == currentSeed) return;
        currentSeed = seed;
        // Periodic gradient lattices give moving local patches without a shared
        // sinusoidal carrier. The coarse cell is span/16 (37.5m at the default
        // 600m span); finer octaves add restrained detail, not independent clocks.
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                double u = (x + .5) / Size;
                double v = (y + .5) / Size;
                double noise = PeriodicPerlin(u, v, 16, seed) * .72
                    + PeriodicPerlin(u, v, 32, seed) * .22
                    + PeriodicPerlin(u, v, 64, seed) * .06;
                double normalized = Math.Clamp(.5 + noise * 1.25, 0, 1);
                pixels[y * Size + x] = (byte)Math.Round(normalized * 255);
            }
        }
        AtlasFingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels));
        texture?.Dispose();
        texture = null;
        lastGlobals.Clear();
    }
    private static double PeriodicPerlin(double u, double v, int cells, int seed)
    {
        double x = u * cells, y = v * cells;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;
        static double Fade(double value) => value * value * value * (value * (value * 6 - 15) + 10);
        static double Lerp(double a, double b, double weight) => a + (b - a) * weight;
        double Dot(int gx, int gy, double dx, double dy)
        {
            uint hash;
            unchecked
            {
                hash = (uint)seed ^ (uint)(gx & (cells - 1)) * 0x9e3779b9u
                    ^ (uint)(gy & (cells - 1)) * 0x85ebca6bu ^ (uint)cells * 0xc2b2ae35u;
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
            }
            const double diagonal = .7071067811865476;
            return (hash & 7) switch
            {
                0 => dx,
                1 => -dx,
                2 => dy,
                3 => -dy,
                4 => (dx + dy) * diagonal,
                5 => (dx - dy) * diagonal,
                6 => (-dx + dy) * diagonal,
                _ => (-dx - dy) * diagonal
            };
        }
        return Lerp(
            Lerp(Dot(ix, iy, fx, fy), Dot(ix + 1, iy, fx - 1, fy), Fade(fx)),
            Lerp(Dot(ix, iy + 1, fx, fy - 1), Dot(ix + 1, iy + 1, fx - 1, fy - 1), Fade(fx)), Fade(fy));
    }

    static double Wrap(double v, double period) => (v % period + period) % period;
    public void Configure(double circumference, Vector2 transport, double time, RingWindSettings settings)
    {
        SimulationTime = double.IsFinite(time) ? time : 0;
        float travel = float.IsFinite(settings.GustTravelMultiplier) ? Math.Clamp(settings.GustTravelMultiplier, 0, 4) : 1;
        if (!transport.IsFinite()) transport = Vector2.Zero;
        double scale = float.IsFinite(settings.GustScaleMeters) ? Math.Clamp(settings.GustScaleMeters, 48, 8000) : 600;
        AroundPeriodMeters = circumference > 0 ? circumference / Math.Max(1, Math.Round(circumference / scale)) : scale;
        AcrossPeriodMeters = scale;
        if (!double.IsFinite(lastTime) || SimulationTime < lastTime)
        {
            integratedX = transport.X * SimulationTime * travel;
            integratedY = transport.Y * SimulationTime * travel;
        }
        else
        {
            double dt = SimulationTime - lastTime;
            integratedX += previousTransport.X * dt * travel;
            integratedY += previousTransport.Y * dt * travel;
        }
        lastTime = SimulationTime;
        previousTransport = transport;
        VelocityMetersPerSecond = transport * travel;
        OffsetMeters = new Vector2((float)Wrap(integratedX, AroundPeriodMeters), (float)Wrap(integratedY, AcrossPeriodMeters));
    }
    public float SampleAtTime(RingCanonicalPosition position, Vector2 transport, double time, double circumference, RingWindSettings settings)
    {
        float travel = float.IsFinite(settings.GustTravelMultiplier) ? Math.Clamp(settings.GustTravelMultiplier, 0, 4) : 1;
        double scale = float.IsFinite(settings.GustScaleMeters) ? Math.Clamp(settings.GustScaleMeters, 48, 8000) : 600;
        time = double.IsFinite(time) ? time : 0;
        if (!transport.IsFinite()) transport = Vector2.Zero;
        double around = circumference > 0 ? circumference / Math.Max(1, Math.Round(circumference / scale)) : scale;
        return SampleCoordinates((position.AroundMeters - transport.X * time * travel) / around, (position.AcrossMeters - transport.Y * time * travel) / scale, settings);
    }
    public float Sample(RingCanonicalPosition position, RingWindSettings settings) => SampleCoordinates((position.AroundMeters - OffsetMeters.X) / AroundPeriodMeters, (position.AcrossMeters - OffsetMeters.Y) / AcrossPeriodMeters, settings);
    public float SampleNoise(RingCanonicalPosition position, float lagSeconds = 0) => SampleNoiseCoordinates(
        (position.AroundMeters - OffsetMeters.X + VelocityMetersPerSecond.X * lagSeconds) / AroundPeriodMeters,
        (position.AcrossMeters - OffsetMeters.Y + VelocityMetersPerSecond.Y * lagSeconds) / AcrossPeriodMeters);

    float SampleCoordinates(double u, double v, RingWindSettings settings) =>
        Mathf.Clamp(settings.GustBaseBreeze + settings.GustStrength * SampleNoiseCoordinates(u, v) * 1.6f, 0, 4);

    float SampleNoiseCoordinates(double u, double v)
    {
        double x = Wrap(u, 1) * Size - .5;
        double y = Wrap(v, 1) * Size - .5;
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        float fx = (float)(x - ix), fy = (float)(y - iy);
        float P(int a, int b) => pixels[((b % Size + Size) % Size) * Size + (a % Size + Size) % Size] / 255f;
        float n = Mathf.Lerp(Mathf.Lerp(P(ix, iy), P(ix + 1, iy), fx), Mathf.Lerp(P(ix, iy + 1), P(ix + 1, iy + 1), fx), fy);
        return n;
    }
    public void Publish(RingLodGeometrySnapshot geometry, RingCanonicalPosition anchor, Vector2 transport, Vector2 mean, RingWindSettings settings, bool enabled)
    {
        float travel = float.IsFinite(settings.GustTravelMultiplier) ? Math.Clamp(settings.GustTravelMultiplier, 0, 4) : 1;
        texture ??= ImageTexture.CreateFromImage(Image.CreateFromData(Size, Size, false, Image.Format.R8, pixels));
        double angle = anchor.AroundMeters / geometry.RingRadiusMeters;
        float arc = (float)((geometry.RingRadiusMeters - geometry.RingVerticalBaseOffsetMeters - anchor.VerticalMeters) / geometry.RingRadiusMeters);
        Basis dual = geometry.GridGlobalTransform.Basis.Inverse().Transposed();
        Vector3 around = dual * new Vector3(-(float)Math.Sin(angle) / Math.Max(.001f, arc), 0, (float)Math.Cos(angle) / Math.Max(.001f, arc));
        Vector3 across = dual * Vector3.Up;
        void Set(string name, Variant value) => SetCached(name, value);
        Set("ring_wind_field_enabled", enabled ? 1f : 0f);
        Set("ring_wind_gust_texture", texture);
        Set("ring_wind_mean_vector", mean);
        Set("ring_wind_gust_strength", settings.GustStrength);
        Set("ring_wind_gust_base", settings.GustBaseBreeze);
        Set("ring_wind_anchor_world", geometry.CanonicalToWorld(anchor.AroundMeters, anchor.VerticalMeters, anchor.AcrossMeters));
        Set("ring_wind_around_covector", new Vector4(around.X, around.Y, around.Z, (float)(1 / AroundPeriodMeters)));
        Set("ring_wind_across_covector", new Vector4(across.X, across.Y, across.Z, (float)(1 / AcrossPeriodMeters)));
        Set("ring_wind_anchor_uv", new Vector2((float)Wrap((anchor.AroundMeters - OffsetMeters.X) / AroundPeriodMeters, 1), (float)Wrap((anchor.AcrossMeters - OffsetMeters.Y) / AcrossPeriodMeters, 1)));
        Set("ring_wind_gust_velocity_uv", new Vector2((float)(VelocityMetersPerSecond.X / AroundPeriodMeters), (float)(VelocityMetersPerSecond.Y / AcrossPeriodMeters)));
    }
}
