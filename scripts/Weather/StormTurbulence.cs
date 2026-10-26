using System;
using Godot;

namespace RingworldTechDemo.Weather;

/// <summary>Inputs of the turbulence strength at one point, from the storm arrival weights and the column.</summary>
public readonly record struct StormTurbulenceInput(
    float HeightAboveGroundMeters,
    float SurfaceSpeed,               // m/s, mean wind at the surface
    float GustFront,                  // arrival weights 0..1
    float Core,
    float Severity,                   // 0..1.5
    int KindCode,
    bool InCloud,
    float AltitudeMeters,             // above the datum, for the cloud-relative terms
    float CloudBaseMeters,
    float CloudTopMeters,
    float AnvilAltitudeMeters);

/// <summary>
/// Pure storm turbulence (docs/storm-revamp-s3-storm-arrival-design.md section 8). Sigma (m/s): clear boundary layer 0.5 + 0.1 U; gust front 4-8 near the
/// surface decaying over 1.5 km; core 5-10 through the cloud depth; in-cloud 2; anvil shear layer 3; overshooting top 6. The realisation is three octaves
/// of deterministic 3D gradient noise with a length scale of 50-300 m, frozen in the mean wind (advected by it) and evolved by a slow time coordinate.
/// </summary>
public static class StormTurbulence
{
    /// <summary>Std of one noise component of <see cref="Noise3"/> (measured by the test over a large lattice); realisations are divided by it so sigma is the std per component.</summary>
    public const float NoiseStd = 0.2700f;
    const float OctaveGain = 0.55f;
    static readonly float OctaveNorm = 1.0f / Mathf.Sqrt(1.0f + OctaveGain * OctaveGain + OctaveGain * OctaveGain * OctaveGain * OctaveGain);

    static float Smooth(float e0, float e1, float x)
    {
        float t = Mathf.Clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    public static float ClearSigma(float surfaceSpeed, float heightMeters)
        => (0.5f + 0.1f * Mathf.Max(0.0f, surfaceSpeed)) * (1.0f - 0.5f * Smooth(1000.0f, 6000.0f, heightMeters));

    /// <summary>Combined sigma: the root of the sum of squares of the terms.</summary>
    public static float Sigma(in StormTurbulenceInput i)
    {
        float h = Mathf.Max(0.0f, i.HeightAboveGroundMeters);
        float severity = Mathf.Clamp(i.Severity, 0.0f, 1.0f);
        float sum = Mathf.Pow(ClearSigma(i.SurfaceSpeed, h), 2.0f);
        float gust = Mathf.Lerp(4.0f, 8.0f, severity) * Mathf.Clamp(i.GustFront, 0.0f, 1.0f) * Mathf.Exp(-h / 1500.0f);
        sum += gust * gust;
        float depth = Mathf.Max(100.0f, i.CloudTopMeters - i.CloudBaseMeters);
        float throughCloud = Smooth(i.CloudBaseMeters - 200.0f, i.CloudBaseMeters + 300.0f, i.AltitudeMeters) * (1.0f - Smooth(i.CloudTopMeters - 500.0f, i.CloudTopMeters + 500.0f, i.AltitudeMeters));
        float core = Mathf.Lerp(5.0f, 10.0f, severity) * Mathf.Clamp(i.Core, 0.0f, 1.0f) * Mathf.Max(throughCloud, 0.25f * (1.0f - Smooth(0.0f, 1500.0f, h)));
        sum += core * core;
        float inCloud = i.InCloud ? 2.0f * Mathf.Max(Mathf.Clamp(i.Core + i.GustFront, 0.0f, 1.0f), 0.5f) : 0.0f;
        sum += inCloud * inCloud;
        if (i.KindCode >= 3)
        {
            float weight = Mathf.Clamp(Mathf.Max(i.Core, i.GustFront * 0.5f), 0.0f, 1.0f);
            float anvil = 3.0f * weight * Mathf.Exp(-Mathf.Pow((i.AltitudeMeters - i.AnvilAltitudeMeters) / 1500.0f, 2.0f));
            sum += anvil * anvil;
            if (i.KindCode >= 4)
            {
                float overshoot = 6.0f * weight * Mathf.Exp(-Mathf.Pow((i.AltitudeMeters - (i.CloudTopMeters + 400.0f)) / 800.0f, 2.0f));
                sum += overshoot * overshoot;
            }
        }
        return Mathf.Sqrt(sum);
    }

    /// <summary>Length scale 50-300 m: small at the surface, large in the core and aloft.</summary>
    public static float LengthMeters(float heightMeters, float core)
        => Mathf.Lerp(50.0f, 300.0f, Mathf.Clamp(Mathf.Max(Smooth(0.0f, 1500.0f, heightMeters), Mathf.Clamp(core, 0.0f, 1.0f)), 0.0f, 1.0f));

    // ---- deterministic 3D gradient noise ----
    static uint Hash(int x, int y, int z, uint seed)
    {
        unchecked
        {
            uint h = (uint)x * 0x8DA6B343u ^ (uint)y * 0xD8163841u ^ (uint)z * 0xCB1AB31Fu ^ seed * 0x9E3779B1u;
            h ^= h >> 15; h *= 0x2C1B3C6Du; h ^= h >> 12; h *= 0x297A2D39u; h ^= h >> 15;
            return h;
        }
    }

    static float Gradient(int ix, int iy, int iz, float fx, float fy, float fz, uint seed)
    {
        uint h = Hash(ix, iy, iz, seed) & 15u;
        // Ken Perlin's 12 edge gradients (16 hashes, four repeated).
        float u = h < 8u ? fx : fy;
        float v = h < 4u ? fy : (h == 12u || h == 14u ? fx : fz);
        return ((h & 1u) == 0u ? u : -u) + ((h & 2u) == 0u ? v : -v);
    }

    static float Fade(float t) => t * t * t * (t * (t * 6.0f - 15.0f) + 10.0f);

    /// <summary>Gradient noise at a lattice coordinate (unit lattice), range about +/-1.</summary>
    public static float Noise3(double x, double y, double z, uint seed)
    {
        double fx0 = Math.Floor(x), fy0 = Math.Floor(y), fz0 = Math.Floor(z);
        int x0 = (int)(fx0 % 1_000_000), y0 = (int)(fy0 % 1_000_000), z0 = (int)(fz0 % 1_000_000);
        float tx = (float)(x - fx0), ty = (float)(y - fy0), tz = (float)(z - fz0);
        float u = Fade(tx), v = Fade(ty), w = Fade(tz);
        float c000 = Gradient(x0, y0, z0, tx, ty, tz, seed);
        float c100 = Gradient(x0 + 1, y0, z0, tx - 1, ty, tz, seed);
        float c010 = Gradient(x0, y0 + 1, z0, tx, ty - 1, tz, seed);
        float c110 = Gradient(x0 + 1, y0 + 1, z0, tx - 1, ty - 1, tz, seed);
        float c001 = Gradient(x0, y0, z0 + 1, tx, ty, tz - 1, seed);
        float c101 = Gradient(x0 + 1, y0, z0 + 1, tx - 1, ty, tz - 1, seed);
        float c011 = Gradient(x0, y0 + 1, z0 + 1, tx, ty - 1, tz - 1, seed);
        float c111 = Gradient(x0 + 1, y0 + 1, z0 + 1, tx - 1, ty - 1, tz - 1, seed);
        float x00 = Mathf.Lerp(c000, c100, u), x10 = Mathf.Lerp(c010, c110, u), x01 = Mathf.Lerp(c001, c101, u), x11 = Mathf.Lerp(c011, c111, u);
        return Mathf.Lerp(Mathf.Lerp(x00, x10, v), Mathf.Lerp(x01, x11, v), w);
    }

    /// <summary>
    /// Turbulent velocity (around, up, across; m/s) at a position and time: three octaves of gradient noise per component, scaled so each component has std sigma,
    /// frozen in the mean wind (the pattern is advected with it) with a slow evolution. Position and time are doubles; the lattice index is reduced before it reaches float.
    /// </summary>
    public static Vector3 Realize(double around, double up, double across, double timeSeconds, Vector3 meanWind, float sigma, float lengthMeters, int seed)
    {
        if (sigma <= 0.0f) return Vector3.Zero;
        double inverseLength = 1.0 / Math.Max(10.0, lengthMeters);
        double px = (around - meanWind.X * timeSeconds) * inverseLength;
        double py = (up - meanWind.Y * timeSeconds) * inverseLength;
        double pz = (across - meanWind.Z * timeSeconds) * inverseLength;
        double evolve = timeSeconds * 0.02;      // the pattern changes shape over about a minute
        float vx = 0.0f, vy = 0.0f, vz = 0.0f, gain = 1.0f;
        double frequency = 1.0;
        for (int octave = 0; octave < 3; octave++)
        {
            uint s = (uint)(seed * 31 + octave * 977);
            vx += gain * Noise3(px * frequency + evolve, py * frequency, pz * frequency, s + 1u);
            vy += gain * Noise3(px * frequency, py * frequency + evolve, pz * frequency + 17.3, s + 2u);
            vz += gain * Noise3(px * frequency + 41.7, py * frequency, pz * frequency + evolve, s + 3u);
            gain *= OctaveGain; frequency *= 2.0;
        }
        float scale = sigma * OctaveNorm / NoiseStd;
        // Vertical motion is weaker than horizontal in a boundary layer; the core updraft carries the vertical part.
        return new Vector3(vx * scale, vy * scale * 0.6f, vz * scale);
    }
}
