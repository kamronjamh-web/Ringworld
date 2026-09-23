using Godot;
using System.Collections.Generic;

namespace RingworldTechDemo.Voxel.LOD;

public partial class RingGraphicsQualityController
{
    private static readonly IReadOnlyList<int> ShadowAtlasSizes = System.Array.AsReadOnly(new[] { 2048, 4096, 8192, 16384, 32768, 65536, 131072 });
    public static IReadOnlyList<int> DirectionalShadowAtlasSizes => ShadowAtlasSizes;
    public const int UnverifiedDirectionalShadowAtlasLimit = 16384;
    public const float DefaultNearCascadeMeters = 60.0f;
    public const float MinimumNearCascadeMeters = 10.0f;
    public const float MaximumNearCascadeMeters = 20000.0f;
    [Export] public NodePath DayNightCyclePath { get; set; } = new("../RingDayNightCycle");

    private readonly Dictionary<string, Variant> _shadowTuningOverrides = new();
    private bool _shadowBaselineCaptured;
    private float _shadowBaselineBias;
    private float _shadowBaselineNormalBias;
    private bool _shadowBaselineEnabled;
    private bool _shadowBaselineCycleCaptured;
    private bool _shadowBaselineSunStabilizationEnabled;
    private float _shadowBaselineSunStepDegrees;
    private global::RingworldTechDemo.RingDayNightCycle? _dayNightCycle;
    private int _lastAppliedShadowAtlas = -1;
    private int _lastAppliedShadowDepthBits = -1;
    private int _lastAppliedShadowFilter = -1;
    private bool _shadowAtlasLimitResolved;
    public float EffectiveShadowDistanceMeters => _sun?.DirectionalShadowMaxDistance ?? 0.0f;
    public int RequestedDirectionalShadowAtlasSize { get; private set; }
    public int EffectiveDirectionalShadowAtlasSize { get; private set; }
    public int EffectiveDirectionalShadowDepthBits { get; private set; } = 16;
    public ulong EffectiveDirectionalShadowAtlasMinimumDepthBytes => DirectionalShadowAtlasMinimumDepthBytes(EffectiveDirectionalShadowAtlasSize, EffectiveDirectionalShadowDepthBits);
    public float EffectiveNearCascadeMeters => _sun == null ? DefaultNearCascadeMeters : _sun.DirectionalShadowSplit1 * _sun.DirectionalShadowMaxDistance;
    public int EffectiveDirectionalShadowFilter { get; private set; }
    public int DirectionalShadowAtlasHardwareLimit { get; private set; } = UnverifiedDirectionalShadowAtlasLimit;
    public bool DirectionalShadowAtlasLimitIsUnverified { get; private set; } = true;

    public void SetShadowTuningOverrides(Godot.Collections.Dictionary overrides)
    {
        _shadowTuningOverrides.Clear();
        foreach (Variant key in overrides.Keys)
            if (key.VariantType == Variant.Type.String && TryValidateShadowOverride(key.AsString(), overrides[key], out Variant value))
                _shadowTuningOverrides[key.AsString()] = value;
        ApplyShadowTuningOverrides();
    }

    public bool SetShadowTuningOverride(string key, Variant value)
    {
        if (!TryValidateSupportedShadowOverride(key, value, out Variant clean)) return false;
        if (key == "NearCascadeMeters") _shadowTuningOverrides.Remove("Split1");
        else if (key == "Split1") _shadowTuningOverrides.Remove("NearCascadeMeters");
        _shadowTuningOverrides[key] = clean;
        ApplyShadowTuningOverrides();
        return true;
    }

    private void ApplyShadowTuningOverrides()
    {
        Tuning profile = ResolveTuning(CurrentPreset);
        int atlas = _shadowTuningOverrides.TryGetValue("AtlasSize", out Variant atlasValue) ? atlasValue.AsInt32() : profile.ShadowAtlasSize;
        int depthBits = _shadowTuningOverrides.TryGetValue("DepthBits", out Variant depthValue) ? depthValue.AsInt32() : 16;
        int filter = _shadowTuningOverrides.TryGetValue("Filter", out Variant filterValue) ? filterValue.AsInt32() : (int)profile.ShadowFilter;
        ResolveDirectionalShadowAtlasHardwareLimit();
        RequestedDirectionalShadowAtlasSize = atlas;
        atlas = ResolveDirectionalShadowAtlasSize(atlas, DirectionalShadowAtlasHardwareLimit);
        EffectiveDirectionalShadowAtlasSize = atlas;
        EffectiveDirectionalShadowDepthBits = depthBits;
        EffectiveDirectionalShadowFilter = filter;
        if (_lastAppliedShadowAtlas != atlas || _lastAppliedShadowDepthBits != depthBits)
        {
            RenderingServer.DirectionalShadowAtlasSetSize(atlas, depthBits == 16);
            _lastAppliedShadowAtlas = atlas;
            _lastAppliedShadowDepthBits = depthBits;
        }
        if (_lastAppliedShadowFilter != filter)
        {
            RenderingServer.DirectionalSoftShadowFilterSetQuality((RenderingServer.ShadowQuality)filter);
            _lastAppliedShadowFilter = filter;
        }
        ApplySunShadowStabilizationOverrides();
        if (_sun == null) return;
        _sun.SetMeta("ringworld_shadow_quality_owned", true);
        if (!_shadowBaselineCaptured)
        {
            _shadowBaselineBias = _sun.ShadowBias;
            _shadowBaselineNormalBias = _sun.ShadowNormalBias;
            _shadowBaselineEnabled = _sun.ShadowEnabled;
            _shadowBaselineCaptured = true;
        }
        _sun.DirectionalShadowSplit2 = CurrentPreset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090 ? .05f : .06f;
        _sun.DirectionalShadowSplit3 = CurrentPreset == RingTerrainLodCoordinator.QualityPreset.Cinematic5090 ? .24f : .28f;
        _sun.DirectionalShadowFadeStart = .88f;
        _sun.ShadowBias = _shadowBaselineBias;
        _sun.ShadowNormalBias = _shadowBaselineNormalBias;
        _sun.ShadowEnabled = _shadowBaselineEnabled;
        _sun.LightAngularDistance = profile.SunAngularDistanceDegrees;
        if (_shadowTuningOverrides.TryGetValue("Enabled", out Variant enabled)) _sun.ShadowEnabled = enabled.AsBool();
        if (_shadowTuningOverrides.TryGetValue("NearCascadeMeters", out Variant nearCascade))
            _sun.DirectionalShadowSplit1 = Mathf.Clamp(nearCascade.AsSingle() / Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f), .0001f, .40f);
        else if (_shadowTuningOverrides.TryGetValue("Split1", out Variant split1)) _sun.DirectionalShadowSplit1 = split1.AsSingle();
        else _sun.DirectionalShadowSplit1 = Mathf.Clamp(DefaultNearCascadeMeters / Mathf.Max(_sun.DirectionalShadowMaxDistance, .0001f), .0001f, .40f);
        if (_shadowTuningOverrides.TryGetValue("Split2", out Variant split2)) _sun.DirectionalShadowSplit2 = split2.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("Split3", out Variant split3)) _sun.DirectionalShadowSplit3 = split3.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("FadeStart", out Variant fade)) _sun.DirectionalShadowFadeStart = fade.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("Bias", out Variant bias)) _sun.ShadowBias = bias.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("NormalBias", out Variant normalBias)) _sun.ShadowNormalBias = normalBias.AsSingle();
        if (_shadowTuningOverrides.TryGetValue("SunAngularDistance", out Variant angularDistance)) _sun.LightAngularDistance = angularDistance.AsSingle();
        NormalizeShadowSplits();
    }

    private void ResolveDirectionalShadowAtlasHardwareLimit()
    {
        if (_shadowAtlasLimitResolved) return;
        _shadowAtlasLimitResolved = true;
        try
        {
            RenderingDevice? device = RenderingServer.GetRenderingDevice();
            if (device is null) return;
            ulong texture = device.LimitGet(RenderingDevice.Limit.MaxTextureSize2D);
            ulong width = device.LimitGet(RenderingDevice.Limit.MaxFramebufferWidth);
            ulong height = device.LimitGet(RenderingDevice.Limit.MaxFramebufferHeight);
            DirectionalShadowAtlasHardwareLimit = ResolveDirectionalShadowAtlasHardwareLimit(texture, width, height);
            DirectionalShadowAtlasLimitIsUnverified = false;
        }
        catch (System.Exception)
        {
            // Compatibility renderers and unavailable devices retain the conservative fallback.
        }
    }

    public bool IsDirectionalShadowAtlasSizeSupported(int size)
    {
        ResolveDirectionalShadowAtlasHardwareLimit();
        return IsDirectionalShadowAtlasSize(size) && size <= DirectionalShadowAtlasHardwareLimit;
    }

    public bool TryValidateSupportedShadowOverride(string key, Variant value, out Variant clean)
    {
        if (!TryValidateShadowOverride(key, value, out clean)) return false;
        return key != "AtlasSize" || IsDirectionalShadowAtlasSizeSupported(clean.AsInt32());
    }

    public static int ResolveDirectionalShadowAtlasSize(int requestedSize, int maximumSize)
    {
        int ceiling = System.Math.Min(requestedSize, maximumSize);
        if (ceiling <= 0) return 0;
        int resolved = 0;
        foreach (int candidate in DirectionalShadowAtlasSizes)
        {
            if (candidate > requestedSize || candidate > maximumSize) break;
            resolved = candidate;
        }
        if (resolved > 0) return resolved;
        int floorPowerOfTwo = 1;
        while (floorPowerOfTwo <= ceiling / 2) floorPowerOfTwo *= 2;
        return floorPowerOfTwo;
    }

    public static int ResolveDirectionalShadowAtlasHardwareLimit(ulong textureLimit, ulong framebufferWidthLimit, ulong framebufferHeightLimit)
    {
        ulong limit = System.Math.Min(textureLimit, System.Math.Min(framebufferWidthLimit, framebufferHeightLimit));
        return limit > int.MaxValue ? int.MaxValue : (int)limit;
    }

    public static ulong DirectionalShadowAtlasMinimumDepthBytes(int size) => DirectionalShadowAtlasMinimumDepthBytes(size, 32);

    public static ulong DirectionalShadowAtlasMinimumDepthBytes(int size, int depthBits)
    {
        if (size <= 0 || depthBits is not (16 or 32)) return 0UL;
        return (ulong)size * (ulong)size * ((ulong)depthBits / 8UL);
    }

    private static bool IsDirectionalShadowAtlasSize(int size)
    {
        foreach (int candidate in DirectionalShadowAtlasSizes)
            if (candidate == size) return true;
        return false;
    }

    private void ApplySunShadowStabilizationOverrides()
    {
        if (_dayNightCycle == null || !GodotObject.IsInstanceValid(_dayNightCycle) || !_dayNightCycle.IsInsideTree())
        {
            _dayNightCycle = IsInsideTree()
                ? GetNodeOrNull<global::RingworldTechDemo.RingDayNightCycle>(DayNightCyclePath)
                : null;
            _shadowBaselineCycleCaptured = false;
        }
        if (_dayNightCycle == null) return;
        if (!_shadowBaselineCycleCaptured)
        {
            _shadowBaselineSunStabilizationEnabled = _dayNightCycle.SunShadowStabilizationEnabled;
            _shadowBaselineSunStepDegrees = _dayNightCycle.SunShadowStepDegrees;
            _shadowBaselineCycleCaptured = true;
        }
        _dayNightCycle.SunShadowStabilizationEnabled = _shadowTuningOverrides.TryGetValue("SunStabilizationEnabled", out Variant enabled)
            ? enabled.AsBool()
            : _shadowBaselineSunStabilizationEnabled;
        _dayNightCycle.SunShadowStepDegrees = _shadowTuningOverrides.TryGetValue("SunStepDegrees", out Variant step)
            ? step.AsSingle()
            : _shadowBaselineSunStepDegrees;
    }

    private void NormalizeShadowSplits()
    {
        if (_sun == null) return;
        _sun.DirectionalShadowSplit1 = Mathf.Clamp(_sun.DirectionalShadowSplit1, .0001f, .40f);
        _sun.DirectionalShadowSplit2 = Mathf.Clamp(_sun.DirectionalShadowSplit2, _sun.DirectionalShadowSplit1 + .01f, .70f);
        _sun.DirectionalShadowSplit3 = Mathf.Clamp(_sun.DirectionalShadowSplit3, _sun.DirectionalShadowSplit2 + .01f, .95f);
    }

    public static bool TryValidateShadowOverride(string key, Variant value, out Variant clean)
    {
        clean = default;
        if (key == "Enabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key == "SunStabilizationEnabled") { if (value.VariantType != Variant.Type.Bool) return false; clean = value.AsBool(); return true; }
        if (key == "AtlasSize" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { double raw = value.AsDouble(); if (!double.IsFinite(raw) || raw != System.Math.Truncate(raw) || raw < int.MinValue || raw > int.MaxValue) return false; int v = (int)raw; if (IsDirectionalShadowAtlasSize(v)) { clean = v; return true; } return false; }
        if (key == "DepthBits" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { double raw = value.AsDouble(); if (raw == 16.0 || raw == 32.0) { clean = (int)raw; return true; } return false; }
        if (key == "Filter" && value.VariantType is Variant.Type.Int or Variant.Type.Float)
        { float raw = value.AsSingle(); if (!float.IsFinite(raw) || raw != Mathf.Round(raw)) return false; int v = (int)raw; if (v is >= 0 and <= 5) { clean = v; return true; } return false; }
        if (value.VariantType is not (Variant.Type.Int or Variant.Type.Float) || !float.IsFinite(value.AsSingle())) return false;
        (float min, float max) = key switch { "NearCascadeMeters" => (MinimumNearCascadeMeters, MaximumNearCascadeMeters), "Split1" => (.0001f, .40f), "Split2" => (.02f, .70f), "Split3" => (.03f, .95f), "FadeStart" => (.50f, .98f), "Bias" => (0, 1), "NormalBias" => (0, 4), "SunAngularDistance" => (0, 3), "SunStepDegrees" => (0, .05f), _ => (1, 0) };
        if (min > max) return false;
        clean = Mathf.Clamp(value.AsSingle(), min, max); return true;
    }
}
