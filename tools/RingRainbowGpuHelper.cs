using Godot;
using RingworldTechDemo.Weather;

namespace RingworldTechDemo.Tools;

/// <summary>GDScript-callable access to the rainbow maths for tools/rainbow_gpu_test.gd (payload bytes and bow axes for a synthetic sun).</summary>
[GlobalClass]
public partial class RingRainbowGpuHelper : RefCounted
{
    /// <summary>A bow for a sun at the given elevation and azimuth (world up = +Y): payload (80 bytes) with all five azimuths at azimuthStrength, plus the axes.</summary>
    public Godot.Collections.Dictionary Make(float sunElevationDegrees, float sunAzimuthDegrees, float azimuthStrength, float globalStrength, float lightScale)
    {
        float e = Mathf.DegToRad(sunElevationDegrees), a = Mathf.DegToRad(sunAzimuthDegrees);
        var toSun = new Vector3(Mathf.Cos(e) * Mathf.Cos(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Sin(a));
        RingRainbowMath.Bow bow = RingRainbowMath.Compute(toSun, Vector3.Up);
        float[] strengths = { azimuthStrength, azimuthStrength, azimuthStrength, azimuthStrength, azimuthStrength };
        return new Godot.Collections.Dictionary
        {
            ["active"] = bow.Active, ["payload"] = RingRainbowMath.Payload(bow, strengths, globalStrength, lightScale),
            ["anti"] = bow.Antisolar, ["top"] = bow.Top, ["side"] = bow.Side, ["half_arc"] = bow.HalfArcRadians, ["to_sun"] = toSun
        };
    }

    public byte[] InactivePayload() => new byte[80];
}
