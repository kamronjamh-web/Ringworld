using System;
using System.Reflection;
using Godot;
using RingworldTechDemo.Voxel.LOD;

namespace RingworldTechDemo.Tools;

/// <summary>TEST ONLY (MIST-1 before/after evidence). Re-creates the removed static ground mist inside the current build by feeding it through the weather-mist terms
/// (WeatherMistAddedSigma / WeatherMistHeightFactor), which reach the shaders exactly like the old static mist did: sigma_M0 = BaseSigmaMist x aerosol / 0.35 x mistDensity x density,
/// H_mist = mistHeight x falloff factor. The old build's mist-on shader path is identical to the new build's weather path, so this is the "before" image and cost.</summary>
public partial class MistAbHelper : RefCounted
{
    private static readonly BindingFlags Any = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>strength is a weather mist M (0 = none) published first through the real API; the old static mist is added on top.</summary>
    public void SetMist(bool oldStaticMist, float aerosol, float mistDensity, float mistHeight, float weatherStrength)
    {
        var t = typeof(RingAtmosphereModel);
        RingAtmosphereModel.ClearWeatherMist();
        // ClearWeatherMist returns early when no weather mist was published, so reset the reflected terms ourselves.
        t.GetProperty("WeatherMistAddedSigma", Any)!.SetValue(null, 0.0f);
        t.GetProperty("WeatherMistHeightFactor", Any)!.SetValue(null, 1.0f);
        if (weatherStrength > 0.0f) RingAtmosphereModel.SetWeatherMist(weatherStrength, 1.0e9, immediate: true);
        if (!oldStaticMist)
        {
            t.GetMethod("ApplyProfile", Any)!.Invoke(null, null);
            t.GetProperty("Revision", Any)!.SetValue(null, RingAtmosphereModel.Revision + 1);
            t.GetMethod("ApplyAll", Any)!.Invoke(null, null);
        }
        else
        {
            float staticSigma = RingAtmosphereModel.BaseSigmaMist * Mathf.Clamp(aerosol, 0.0f, 1.0f) / RingAtmosphereModel.AerosolControlDefault * mistDensity;
            float added = RingAtmosphereModel.WeatherMistAddedSigma + staticSigma;
            float factor = RingAtmosphereModel.WeatherMistHeightFactor * (mistHeight / RingAtmosphereModel.BaseMistHeightMeters);
            t.GetProperty("WeatherMistAddedSigma", Any)!.SetValue(null, added);
            t.GetProperty("WeatherMistHeightFactor", Any)!.SetValue(null, factor);
            t.GetMethod("ApplyProfile", Any)!.Invoke(null, null);
            t.GetProperty("Revision", Any)!.SetValue(null, RingAtmosphereModel.Revision + 1);
            t.GetMethod("ApplyAll", Any)!.Invoke(null, null);
        }
    }

    public Godot.Collections.Dictionary State() => new()
    {
        ["sigma_m"] = RingAtmosphereModel.HazeSigmaMist, ["h_mist"] = RingAtmosphereModel.HazeMistHeightMeters, ["h_a"] = RingAtmosphereModel.HazeAerosolHeightMeters,
        ["weather_mist"] = RingAtmosphereModel.WeatherMist,
    };
}
