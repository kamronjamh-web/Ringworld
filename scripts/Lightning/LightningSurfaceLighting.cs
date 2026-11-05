using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo.Lightning;

/// <summary>Bounded canonical light state shared by the local and isolated far worlds.</summary>
public sealed class LightningSurfaceLighting
{
    public const int MaximumLights = 8;
    public const float BaseEnergy = 3.0f;
    public readonly record struct SurfaceLight(long EventId, RingCanonicalPosition Center, float Energy, float ReachMeters, Color Color);
    readonly SurfaceLight[] _lights = new SurfaceLight[MaximumLights];
    readonly double[] _distances = new double[MaximumLights];
    readonly float[] _pulses = new float[MaximumLights];
    public int Count { get; private set; }
    public SurfaceLight this[int index] => _lights[index];

    public void Clear() => Count = 0;

    public void Update(IReadOnlyList<RingLightningEvent> events, MegastormModel? model, double now,
        RingCanonicalPosition observer, double circumference, Color color, float suppression)
    {
        Clear();
        for (int i = 0; i < events.Count; i++) Consider(events[i], now, observer, circumference, color);
        if (model?.Triggered == true)
        {
            long slot = (long)Math.Floor(now / MegastormLightning.SlotSeconds);
            for (long k = slot - 1; k <= slot; k++)
                for (int segment = 0; segment < MegastormLightning.Segments; segment++)
                {
                    if (MegastormLightning.Flash(model, k, segment, LightningVisualPolicy.NaturalStrikeChanceScale) is not { } flash) continue;
                    bool present = false;
                    for (int i = 0; i < events.Count; i++)
                        if (events[i].EventId == flash.Id) { present = true; break; }
                    if (!present && MegastormFieldCoupling.FlashEvent(model, flash) is { } e)
                        Consider(e, now, observer, circumference, color);
                }
        }
        LightningVisualPolicy.LimitSummed(_pulses.AsSpan(0, Count));
        float strength = BaseEnergy * LightningVisualPolicy.SurfaceIlluminationStrength * (1.0f - Math.Clamp(suppression, 0.0f, 1.0f));
        for (int i = 0; i < Count; i++) _lights[i] = _lights[i] with { Energy = _pulses[i] * strength };
    }

    void Consider(in RingLightningEvent e, double now, RingCanonicalPosition observer, double circumference, Color color)
    {
        float pulse = RingLightningEvent.PulseAtAge(now - e.StartTimeSeconds) * Math.Max(0.0f, e.Energy);
        if (pulse <= 0.0f) return;
        for (int i = 0; i < Count; i++) if (_lights[i].EventId == e.EventId) return;
        double around = MegastormModel.WrapPositive(e.GroundPosition.AroundMeters
            + 0.52 * MegastormModel.WrapSigned(e.CloudPosition.AroundMeters - e.GroundPosition.AroundMeters, circumference), circumference);
        var center = new RingCanonicalPosition(around,
            e.GroundPosition.VerticalMeters + 0.52 * (e.CloudPosition.VerticalMeters - e.GroundPosition.VerticalMeters),
            e.GroundPosition.AcrossMeters + 0.52 * (e.CloudPosition.AcrossMeters - e.GroundPosition.AcrossMeters));
        double da = MegastormModel.WrapSigned(center.AroundMeters - observer.AroundMeters, circumference);
        double dz = center.AcrossMeters - observer.AcrossMeters, dy = center.VerticalMeters - observer.VerticalMeters;
        double distance = da * da + dz * dz + dy * dy;
        int index = 0;
        while (index < Count && (_distances[index] < distance || (_distances[index] == distance && _lights[index].EventId < e.EventId))) index++;
        if (index == MaximumLights) return;
        int last = Math.Min(Count, MaximumLights - 1);
        for (int i = last; i > index; i--)
        {
            _lights[i] = _lights[i - 1]; _distances[i] = _distances[i - 1]; _pulses[i] = _pulses[i - 1];
        }
        _lights[index] = new SurfaceLight(e.EventId, center, 0.0f, LightningVisualPolicy.SurfaceIlluminationReachMeters, color);
        _distances[index] = distance;
        _pulses[index] = pulse;
        Count = Math.Min(Count + 1, MaximumLights);
    }

    public static OmniLight3D CreateLight(string name) => new()
    {
        Name = name, LightEnergy = 0.0f, OmniAttenuation = 0.0f,
        ShadowEnabled = false, LightVolumetricFogEnergy = 0.0f, Visible = false
    };
}
