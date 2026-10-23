using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// Shader globals of the whole-ring far weather cloud shadow (materials/ring_far_weather_shadow.gdshaderinc).
/// RingScaledFarViewport publishes the baked far-shell cloud map and the ring frame; RingSunshineCloudBridge
/// publishes the strength and extinction the Sunshine projected maps use, so the two hand off without a step.
/// BAND_FILL (1.7) in the include was calibrated against those maps over the same ground at noon (RTX 4070,
/// 2026-10-22, three 18 km views of the band-limited bake): mean occlusion and the share of ground darker than
/// 0.2 occlusion both land within about 20 % of the Sunshine maps, with the shadows correlated in place (r ~0.3;
/// the bake integrates out the few-km Sunshine detail that the maps resolve).
/// Writes are skipped when unchanged.
/// </summary>
public static class RingFarWeatherShadowGlobals
{
    private static readonly StringName CloudMapName = "ring_far_weather_cloud_map";
    private static readonly StringName FrameName = "ring_far_weather_frame";
    private static readonly StringName ShadowName = "ring_far_weather_shadow";
    private static readonly StringName CloudLightsName = "ring_far_cloud_lights";
    private static readonly StringName CloudAmbientName = "ring_far_cloud_ambient";
    private static Projection _cloudLights = Projection.Zero;
    private static Vector4 _cloudAmbient;
    private static readonly HashSet<StringName> Written = new();

    private static Texture2D? _cloudMap;
    private static Vector4 _frame, _shadow;
    private static float _strength, _extinctionPerMeter;
    private static Vector2 _band;
    private static float _sunshineFade;

    public static ulong GlobalWriteCount { get; private set; }
    public static bool CloudMapPublished => _frame.X > 0.0f && _cloudMap != null;
    public static Vector4 Frame => _frame;
    public static Vector4 Shadow => _shadow;

    /// <summary>Installs the baked far-shell cloud map. A null map (or a non-positive ring size) disables the far term.</summary>
    public static void PublishCloudMap(Texture2D? cloudMap, double circumferenceMeters, float ringWidthMeters)
    {
        if (cloudMap != null && !ReferenceEquals(cloudMap, _cloudMap))
        {
            RenderingServer.GlobalShaderParameterSet(CloudMapName, cloudMap);
            GlobalWriteCount++;
        }
        _cloudMap = cloudMap;
        bool valid = cloudMap != null && circumferenceMeters > 1.0 && ringWidthMeters > 1.0f;
        SetVec4(FrameName, ref _frame, valid ? new Vector4((float)circumferenceMeters, ringWidthMeters, _sunshineFade, 0.0f) : Vector4.Zero);
    }

    /// <summary>Sunshine's altitude crossfade weight: the projected maps' coverage is scaled by 1 - fade and the far term fills the rest.</summary>
    public static void PublishSunshineFade(float fade)
    {
        _sunshineFade = Mathf.Clamp(fade, 0.0f, 1.0f);
        if (_frame.X > 0.0f) SetVec4(FrameName, ref _frame, new Vector4(_frame.X, _frame.Y, _sunshineFade, 0.0f));
    }

    /// <summary>The far shell's background band (its density slice is the band middle).</summary>
    public static void PublishBand(Vector2 backgroundAltitudeMeters)
    {
        _band = backgroundAltitudeMeters;
        PublishShadowVector();
    }

    /// <summary>Same strength and Beer-Lambert extinction as the Sunshine projected maps; strength 0 turns the far term off.</summary>
    public static void PublishShadow(float strength, float extinctionPerKilometer)
    {
        _strength = Mathf.Clamp(strength, 0.0f, 1.0f);
        _extinctionPerMeter = Mathf.Max(0.0f, extinctionPerKilometer) * 0.001f;
        PublishShadowVector();
    }

    /// <summary>
    /// The lights Sunshine marches with, for the far cloud layers' Sunshine-matched shading (ring_cloud_layer.gdshaderinc):
    /// columns 0/2 = light direction (toward the light) and 1 when present, columns 1/3 = Sunshine's per-light radiance
    /// pow(colour x intensity, 2.2) with the anisotropy in column 1 w. Ambient (rgb) = the ambient
    /// Sunshine adds per sample: colour x tint x max(total light power, 0.62) + 0.08 x distance fog colour; w = Sunshine's powder strength.
    /// </summary>
    public static void PublishCloudLighting(Projection lights, Vector4 ambient)
    {
        if (lights != _cloudLights || !Written.Contains(CloudLightsName))
        {
            Written.Add(CloudLightsName);
            _cloudLights = lights;
            RenderingServer.GlobalShaderParameterSet(CloudLightsName, lights);
            GlobalWriteCount++;
        }
        SetVec4(CloudAmbientName, ref _cloudAmbient, ambient);
    }

    /// <summary>World teardown: no far term until the next world bakes a map.</summary>
    public static void Clear() => SetVec4(FrameName, ref _frame, Vector4.Zero);

    private static void PublishShadowVector()
        => SetVec4(ShadowName, ref _shadow, new Vector4(_strength, _extinctionPerMeter, _band.X, _band.Y));

    private static void SetVec4(StringName name, ref Vector4 current, Vector4 value)
    {
        if (current == value && Written.Contains(name)) return;
        Written.Add(name);
        current = value;
        RenderingServer.GlobalShaderParameterSet(name, value);
        GlobalWriteCount++;
    }
}
