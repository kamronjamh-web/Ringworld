using System;
using Godot;

namespace RingworldTechDemo;

/// <summary>
/// One Sunshine segment (line) light (tweaks T4, docs/eclipse-false-sun-spec.md): the in-scattered emission of a straight source from <see cref="A"/> to
/// <see cref="B"/> (world metres), reaching <see cref="Radius"/> from the line. <see cref="Color"/> is colour x intensity. A steady light goes through the
/// temporal history (smooth, for beams that do not move); a transient one bypasses it (orbs and pulses that cross the cloud).
/// </summary>
public readonly record struct SunshineSegmentLight(Vector3 A, Vector3 B, float Radius, Vector3 Color, bool Steady);

/// <summary>Where the cloud light feeder pushes its segments (the Sunshine bridge, or a test double).</summary>
public interface ISunshineSegmentLightSink
{
    /// <summary>False while the sink cannot take lights (no clouds resource yet); the feeder then pushes again once it can.</summary>
    bool SegmentLightsAvailable { get; }
    /// <summary>Replaces the whole segment set. Returns true when it changed (and was uploaded), false when identical to the set already held.</summary>
    bool SetSegmentLights(ReadOnlySpan<SunshineSegmentLight> lights);
    /// <summary>Sunshine's cloud floor and ceiling in metres, the distance the near clouds reach, and whether the numbers are valid.</summary>
    bool TryGetCloudLightBand(out float floorMeters, out float ceilingMeters, out float reachMeters);
}

public partial class RingSunshineCloudBridge : ISunshineSegmentLightSink
{
    /// <summary>Segments the Sunshine shader block holds.</summary>
    public const int MaximumSegmentLights = 8;
    private static readonly StringName SetSegmentLightsMethod = new("set_segment_lights");
    private readonly SunshineSegmentLight[] _segmentLights = new SunshineSegmentLight[MaximumSegmentLights];
    private readonly Godot.Collections.Array<Vector4> _segmentLightData = new();
    private int _segmentLightCount;

    /// <summary>Segments currently held by Sunshine.</summary>
    public int SegmentLightCount => _segmentLightCount;
    /// <summary>Uploads since construction (a changed set each); a steady frame adds none.</summary>
    public long SegmentLightPushCount { get; private set; }

    public bool SegmentLightsAvailable => _clouds != null && GodotObject.IsInstanceValid(_clouds) && _clouds.HasMethod(SetSegmentLightsMethod);

    public bool TryGetCloudLightBand(out float floorMeters, out float ceilingMeters, out float reachMeters)
    {
        Vector2 band = CloudTraversalAltitudeMeters;
        floorMeters = band.X;
        ceilingMeters = band.Y;
        reachMeters = EffectiveHandoffEndMeters > 0.0f ? Math.Min(NearCloudReachMeters, EffectiveHandoffEndMeters) : NearCloudReachMeters;
        return SegmentLightsAvailable && ceilingMeters > floorMeters && reachMeters > 1.0f;
    }

    public bool SetSegmentLights(ReadOnlySpan<SunshineSegmentLight> lights)
    {
        if (!SegmentLightsAvailable) return false;
        int count = Math.Min(lights.Length, MaximumSegmentLights);
        bool same = count == _segmentLightCount;
        for (int i = 0; same && i < count; i++) same = lights[i] == _segmentLights[i];
        if (same) return false;
        _segmentLightCount = count;
        for (int i = 0; i < count; i++) _segmentLights[i] = lights[i];
        // The array is reused (its elements are written in place); the shader block takes three vec4 per segment.
        while (_segmentLightData.Count < count * 3) _segmentLightData.Add(default);
        for (int i = 0; i < count; i++)
        {
            SunshineSegmentLight s = lights[i];
            _segmentLightData[i * 3] = new Vector4(s.A.X, s.A.Y, s.A.Z, s.Radius);
            _segmentLightData[i * 3 + 1] = new Vector4(s.B.X, s.B.Y, s.B.Z, s.Steady ? 1.0f : 0.0f);
            _segmentLightData[i * 3 + 2] = new Vector4(s.Color.X, s.Color.Y, s.Color.Z, 0.0f);
        }
        _clouds!.Call(SetSegmentLightsMethod, _segmentLightData, count);
        SegmentLightPushCount++;
        return true;
    }
}
