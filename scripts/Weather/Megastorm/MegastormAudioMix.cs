using System;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo;

/// <summary>What every megastorm voice should play at one instant (before slewing and the user volume): linear gains in 0..1.</summary>
public readonly record struct MegastormVoiceTargets(float Hum, float WallSingLow, float WallSingHigh, float Roar, float Swell, float AirborneGate, float Wildlife)
{
    public static readonly MegastormVoiceTargets Idle = new(0f, 0f, 0f, 0f, 0f, 1f, 0f);
}

/// <summary>
/// Pure mapping from a <see cref="MegastormSample"/> to voice gains and the tremor outputs (docs/megastorm-design.md 5.2, WO6c). The maths of the sample
/// itself is <see cref="MegastormSound"/>; this adds the parts the voices need: the airborne gate for the other weather audio, the roar level, the
/// shelter factors and the camera shake mapping. No nodes, no state.
/// </summary>
public static class MegastormAudioMix
{
    /// <summary>Distance ahead of the edge where the roar starts to lead the storm at low speeds ("audible from about 80 km ahead").</summary>
    public const double RoarLeadMeters = 80000.0;
    /// <summary>
    /// Ahead of the edge the airborne gate fully suppresses the other weather audio inside this distance (the dead-quiet approach starts about 70 km
    /// ahead at 350 m/s) and hands it back by twice this distance, so the rest of the ring's ordinary weather keeps sounding through the hours of
    /// the hum build-up.
    /// </summary>
    public const double GateFullMeters = 100000.0, GateSpanMeters = 100000.0;
    /// <summary>Camera micro-shake (degrees): about 0.02 at full hum (tremor 0.6), up to about 0.1 at the boom peak (tremor 1).</summary>
    public const float HumShakeDegrees = 0.02f, BoomShakeDegrees = 0.1f;
    /// <summary>Visible shiver frequency of the ground tremor in Hz (the 20-60 Hz hum itself is far above what a frame can show).</summary>
    public const float ShiverHz = 9.0f;

    private static float Smooth(double x) => (float)MegastormModel.Smooth(x);

    /// <summary>
    /// Gain for the airborne beds and thunder of the ordinary weather system: <see cref="MegastormSample.AirGain"/> near the wall, 1 far ahead of it
    /// (and 1 with no megastorm). Behind the edge it is the AirGain itself (the boom window's pile-up).
    /// </summary>
    public static float AirborneGate(in MegastormSample s)
    {
        if (!s.Active) return 1f;
        float shield = s.XiMeters >= 0.0 ? 1f : 1f - Smooth((s.DistanceToEdgeMeters - GateFullMeters) / GateSpanMeters);
        return 1f + (Math.Clamp(s.AirGain, 0f, 1f) - 1f) * shield;
    }

    /// <summary>
    /// The wall roar bed: ahead of the edge only while the airborne lead exists (below M 0.94), rising over the last 80 km; behind it 1 through the
    /// wall and receding with the trailing rain (the hum's decay, which is 1 in the first 5 km). Multiplied by AirGain, so the boom window's
    /// pile-up replaces it until the 1.5 s crossfade.
    /// </summary>
    public static float RoarLevel(in MegastormSample s)
    {
        if (!s.Active) return 0f;
        float proximity = s.XiMeters < 0.0 ? Smooth((RoarLeadMeters + s.XiMeters) / RoarLeadMeters) : Math.Min(1f, 2f * s.HumLevel);
        return Math.Clamp(s.AirGain, 0f, 1f) * proximity;
    }

    public static MegastormVoiceTargets Targets(in MegastormSample s)
    {
        if (!s.Active) return MegastormVoiceTargets.Idle;
        // The singing level of each wall is not in the sample (only the loudest one); the node splits it by the observer's across position.
        return new MegastormVoiceTargets(s.HumLevel, s.WallSingLevel, s.WallSingLevel, RoarLevel(s), s.GustSwell, AirborneGate(s), s.WildlifeQuiet);
    }

    /// <summary>Gain the shelter class leaves the airborne bus (the structure-borne hum and singing never see it). Deep caves still pass nothing airborne.</summary>
    public static float ShelterAirGain(RainShelterClass shelter, float undergroundBlend)
        => RainSoundField.Params(shelter).BedGain * (1f - Math.Clamp(float.IsFinite(undergroundBlend) ? undergroundBlend : 0f, 0f, 1f));

    /// <summary>Camera micro-shake amplitude in degrees from the tremor level: linear to 0.02 at 0.6, then up to 0.1 at 1. Exactly 0 at 0.</summary>
    public static float ShakeDegrees(float tremor)
    {
        float t = Math.Clamp(float.IsFinite(tremor) ? tremor : 0f, 0f, 1f);
        const float hum = (float)MegastormSound.TremorHumScale;
        return t <= hum ? HumShakeDegrees * (t / hum) : HumShakeDegrees + (BoomShakeDegrees - HumShakeDegrees) * ((t - hum) / (1f - hum));
    }

    /// <summary>
    /// Camera shake as an (pitch, yaw, roll) offset in radians: a smooth sum of incommensurate sines on the shared tremor phase, scaled by
    /// <paramref name="degrees"/>. No random per-frame jumps, so two frames can differ by at most the amplitude times the phase step.
    /// </summary>
    public static (float Pitch, float Yaw, float Roll) ShakeOffsetRadians(float phase, float degrees)
    {
        if (degrees <= 0f) return (0f, 0f, 0f);
        float scale = degrees * MathF.PI / 180f;
        return (scale * (0.6f * MathF.Sin(phase + 0.3f) + 0.4f * MathF.Sin(2.37f * phase + 1.1f)),
            scale * (0.6f * MathF.Sin(1.31f * phase + 2.0f) + 0.4f * MathF.Sin(2.9f * phase + 0.2f)),
            scale * (0.6f * MathF.Sin(0.83f * phase + 4.1f) + 0.4f * MathF.Sin(3.3f * phase + 3.0f)));
    }

    /// <summary>The one-shot to play at the boom: picked by the passage key so every approach of one passage agrees (3 variants).</summary>
    public static int BoomVariant(long episodeId, int variants) => variants <= 0 ? 0 : (int)(((episodeId >> 32) & 0x7FFFFF) % variants);
}
