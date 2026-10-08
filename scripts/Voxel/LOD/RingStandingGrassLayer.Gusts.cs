using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Phase2;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingStandingGrassLayer
{
    [ExportGroup("Texture Gusts")]
    [Export] public bool EnableGrassGustTexture { get; set; } = true;
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrassGustStrength { get; set; } = .45f;
    [Export(PropertyHint.Range, "48,2000,1")] public float GrassGustScaleMeters { get; set; } = 600;
    [Export(PropertyHint.Range, "0,4,0.01")] public float GrassGustTravelSpeedMultiplier { get; set; } = 1;
    [Export(PropertyHint.Range, "0,1,0.01")] public float GrassGustBaseBreeze { get; set; } = .65f;

    private readonly RingGrassGustState _grassGustState = new();
    private NoiseTexture2D? _grassGustTexture;
    private bool _grassGustTextureReady;
    private int _grassGustSeed = int.MinValue;
    private RingLocalWeatherEffects? _grassGustWeather;
    private bool _grassGustTestWeather;
    private Vector2 _grassGustTestWind;
    private double _grassGustTestTime;
    private long _grassGustCircumference;
    private readonly record struct GrassGustMaterialState(int Seed, bool TextureReady, bool Enabled, float Strength, float BaseBreeze);
    private readonly Dictionary<ShaderMaterial, GrassGustMaterialState> _grassGustMaterialStates = new();

    private bool UsesSharedWindField => !_grassGustTestWeather && _grassGustWeather != null
        && GodotObject.IsInstanceValid(_grassGustWeather) && _grassGustWeather.SharedWindFieldReady;
    public Vector2 GrassGustOffsetMeters => UsesSharedWindField ? _grassGustWeather!.GustOffsetMeters : _grassGustState.OffsetMeters;
    public double GrassGustSimulationTime => UsesSharedWindField ? _grassGustWeather!.GustSimulationTime : _grassGustState.SimulationTime;
    public float GrassGustEffectiveAroundScaleMeters { get; private set; }
    /// <summary>Set by NoiseTexture2D after Godot completes its asynchronous generation.</summary>
    public bool GrassGustTextureReady => UsesSharedWindField || _grassGustTextureReady;

    /// <summary>Updates the field from the weather simulation clock; it never uses shader TIME.</summary>
    public void UpdateGrassGusts(double delta, RingTerrainSampler sampler)
    {
        _ = delta;
        long circumference = Math.Max(1L, sampler.AroundRingVoxelLength);
        _grassGustCircumference = circumference;
        GetGrassGustPeriods(circumference, out double aroundPeriod, out double acrossPeriod);
        Vector2 wind;
        double time;
        if (_grassGustTestWeather) { wind = _grassGustTestWind; time = _grassGustTestTime; }
        else
        {
            if (_grassGustWeather == null || !GodotObject.IsInstanceValid(_grassGustWeather))
                _grassGustWeather = GetNodeOrNull<RingLocalWeatherEffects>("../LocalWeatherEffects");
            if (_grassGustWeather == null) return;
            if (UsesSharedWindField)
            {
                GrassGustEffectiveAroundScaleMeters = (float)_grassGustWeather.GustAroundPeriodMeters;
                return;
            }
            wind = _grassGustWeather.PublishedVegetationWind;
            time = _grassGustWeather.PublishedVegetationWindTime;
        }
        if (EnableGrassGustTexture) EnsureGrassGustTexture(sampler.Seed);
        _grassGustState.Advance(wind, time, GrassGustTravelSpeedMultiplier, aroundPeriod, acrossPeriod);
    }

    /// <summary>Deterministic fixture input for tests and the meadow benchmark.</summary>
    public void SetGrassGustWeatherForTests(Vector2 wind, double simulationTime, long circumference)
    {
        _grassGustTestWeather = true; _grassGustTestWind = wind; _grassGustTestTime = simulationTime;
        _grassGustCircumference = Math.Max(1L, circumference);
        GetGrassGustPeriods(_grassGustCircumference, out double aroundPeriod, out double acrossPeriod);
        _grassGustState.Advance(wind, simulationTime, GrassGustTravelSpeedMultiplier, aroundPeriod, acrossPeriod);
    }

    public void AdvanceGrassGustForTests(Vector2 wind, double simulationTime, long circumference) =>
        SetGrassGustWeatherForTests(wind, simulationTime, circumference);

    /// <summary>Fixture-only initialization while its authoritative sampler is still alive.</summary>
    public void InitializeGrassGustTextureForTests(int terrainSeed)
    {
        if (EnableGrassGustTexture) EnsureGrassGustTexture(terrainSeed);
    }

    private void EnsureGrassGustTexture(int seed)
    {
        if (_grassGustTexture != null && _grassGustSeed == seed) return;
        if (_grassGustTexture != null) { _grassGustTexture.Changed -= OnGrassGustTextureChanged; _grassGustTexture.Dispose(); }
        var noise = new FastNoiseLite { Seed = seed, NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = .012f,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm, FractalOctaves = 3, FractalGain = .55f, FractalLacunarity = 2f };
        _grassGustTextureReady = false;
        var texture = new NoiseTexture2D();
        texture.Changed += OnGrassGustTextureChanged;
        texture.Width = 512; texture.Height = 512; texture.Seamless = true; texture.Normalize = true; texture.GenerateMipmaps = true;
        texture.Noise = noise; // starts Godot's background generation after the observer is connected.
        _grassGustTexture = texture;
        _grassGustSeed = seed;
        _grassGustMaterialStates.Clear();
    }

    private void OnGrassGustTextureChanged() => _grassGustTextureReady = true;

    private void GetGrassGustPeriods(long circumference, out double aroundPeriod, out double acrossPeriod)
    {
        double requested = float.IsFinite(GrassGustScaleMeters) ? Math.Clamp(GrassGustScaleMeters, 48f, 2000f) : 600d;
        double wrap = Math.Max(1L, circumference);
        aroundPeriod = wrap / Math.Max(1d, Math.Round(wrap / requested));
        acrossPeriod = requested;
    }

    private void ConfigureGrassGustMaterial(ShaderMaterial? material)
    {
        if (material == null) return;
        float strength = float.IsFinite(GrassGustStrength) ? Mathf.Clamp(GrassGustStrength, 0, 2) : 0;
        float baseBreeze = float.IsFinite(GrassGustBaseBreeze) ? Mathf.Clamp(GrassGustBaseBreeze, 0, 1) : 0;
        var state = new GrassGustMaterialState(_grassGustSeed, GrassGustTextureReady, EnableGrassGustTexture, strength, baseBreeze);
        if (!_grassGustMaterialStates.TryGetValue(material, out GrassGustMaterialState previous) || state != previous)
        {
            if (_grassGustTexture != null) material.SetShaderParameter("grass_gust_texture", _grassGustTexture);
            material.SetShaderParameter("grass_gust_enabled", EnableGrassGustTexture && GrassGustTextureReady);
            material.SetShaderParameter("grass_gust_strength", strength);
            material.SetShaderParameter("grass_gust_base_breeze", baseBreeze);
            _grassGustMaterialStates[material] = state;
        }
    }

    internal bool VerifyGrassGustMaterialToggleForTests()
    {
        bool originalEnabled = EnableGrassGustTexture;
        float originalStrength = GrassGustStrength, originalBaseBreeze = GrassGustBaseBreeze;
        ShaderMaterial? near = null;
        ShaderMaterial? intermediate = null;
        try
        {
            EnsureGrassGustTexture(1);
            _grassGustTextureReady = true;
            near = new ShaderMaterial(); intermediate = new ShaderMaterial();
            EnableGrassGustTexture = true;
            ConfigureGrassGustMaterial(near); ConfigureGrassGustMaterial(intermediate);
            bool enabled = near.GetShaderParameter("grass_gust_enabled").AsBool()
                && intermediate.GetShaderParameter("grass_gust_enabled").AsBool();
            EnableGrassGustTexture = false;
            ConfigureGrassGustMaterial(near); ConfigureGrassGustMaterial(intermediate);
            bool disabled = !near.GetShaderParameter("grass_gust_enabled").AsBool()
                && !intermediate.GetShaderParameter("grass_gust_enabled").AsBool();
            EnableGrassGustTexture = true;
            GrassGustStrength = .8f; GrassGustBaseBreeze = .2f;
            ConfigureGrassGustMaterial(near); ConfigureGrassGustMaterial(intermediate);
            bool updated = Mathf.IsEqualApprox(near.GetShaderParameter("grass_gust_strength").AsSingle(), .8f)
                && Mathf.IsEqualApprox(intermediate.GetShaderParameter("grass_gust_strength").AsSingle(), .8f)
                && Mathf.IsEqualApprox(near.GetShaderParameter("grass_gust_base_breeze").AsSingle(), .2f)
                && Mathf.IsEqualApprox(intermediate.GetShaderParameter("grass_gust_base_breeze").AsSingle(), .2f);
            return enabled && disabled && updated;
        }
        finally
        {
            near?.Dispose(); intermediate?.Dispose();
            DisposeGrassGustResources();
            EnableGrassGustTexture = originalEnabled; GrassGustStrength = originalStrength; GrassGustBaseBreeze = originalBaseBreeze;
        }
    }

    private void ConfigureGrassGustRenderer(MultiMeshInstance3D renderer, RingGrassTileData data, RingLodGeometrySnapshot geometry)
    {
        long circumference = Math.Max(1L, (long)Math.Round(geometry.CircumferenceMeters));
        GetGrassGustPeriods(circumference, out double aroundPeriod, out double requested);
        GrassGustEffectiveAroundScaleMeters = (float)aroundPeriod;
        bool validFrame = CalculateGrassGroundCovectors(geometry, data.AnchorHeight, data.Bounds.AroundCenter,
            out Vector3 aroundCovector, out Vector3 acrossCovector);
        Vector3 origin = renderer.GlobalTransform.Origin;
        Vector2 originUv = _grassGustState.OriginUv(data.Bounds.AroundCenter, data.Bounds.AcrossCenter, aroundPeriod, requested);
        renderer.SetInstanceShaderParameter("grass_gust_origin_world", origin);
        renderer.SetInstanceShaderParameter("grass_gust_around_world", new Vector4(aroundCovector.X, aroundCovector.Y, aroundCovector.Z, validFrame ? (float)(1d / aroundPeriod) : 0));
        renderer.SetInstanceShaderParameter("grass_gust_across_world", new Vector4(acrossCovector.X, acrossCovector.Y, acrossCovector.Z, validFrame ? (float)(1d / requested) : 0));
        renderer.SetInstanceShaderParameter("grass_gust_origin_uv", originUv);
    }

    /// <summary>Dual canonical ground covectors. Unlike normalized world vectors,
    /// these remain correct under a rotated, scaled, or sheared grid transform.</summary>
    internal static bool CalculateGrassGroundCovectors(RingLodGeometrySnapshot geometry, double anchorHeight,
        double aroundCenter, out Vector3 around, out Vector3 across)
    {
        around = Vector3.Zero; across = Vector3.Zero;
        double radius = geometry.RingRadiusMeters;
        if (!double.IsFinite(radius) || radius <= 0 || !double.IsFinite(anchorHeight) || !double.IsFinite(aroundCenter)) return false;
        double arcScale = (radius - geometry.RingVerticalBaseOffsetMeters - anchorHeight) / radius;
        Basis basis = geometry.GridGlobalTransform.Basis;
        if (!double.IsFinite(arcScale) || Math.Abs(arcScale) < 1e-8 || !float.IsFinite(basis.Determinant()) || Math.Abs(basis.Determinant()) < 1e-8f) return false;
        double angle = aroundCenter / radius;
        Vector3 localAroundDual = new((float)(-Math.Sin(angle) / arcScale), 0, (float)(Math.Cos(angle) / arcScale));
        Basis inverseTranspose = basis.Inverse().Transposed();
        around = inverseTranspose * localAroundDual;
        across = inverseTranspose * Vector3.Up;
        if (!Finite(around) || !Finite(across)) { around = Vector3.Zero; across = Vector3.Zero; return false; }
        return true;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    internal static Vector2 CalculateGrassGustOriginUv(double around, double across, double offsetAround, double offsetAcross, double aroundPeriod, double acrossPeriod) =>
        new((float)(Modulo(around - offsetAround, aroundPeriod) / aroundPeriod),
            (float)(Modulo(across - offsetAcross, acrossPeriod) / acrossPeriod));
    private void DisposeGrassGustResources()
    {
        if (_grassGustTexture != null) _grassGustTexture.Changed -= OnGrassGustTextureChanged;
        _grassGustTexture?.Dispose();
        _grassGustTexture = null; _grassGustTextureReady = false; _grassGustSeed = int.MinValue;
        _grassGustMaterialStates.Clear();
    }
    private static double Modulo(double value, double period) { double result = value % period; return result < 0 ? result + period : result; }
}
