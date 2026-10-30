using System;
using System.Collections.Generic;
using Godot;
using RingworldTechDemo.Voxel.Core;

namespace RingworldTechDemo;

/// <summary>
/// Megastorm far-layer coverage (docs/megastorm-design.md 3.4). The far atlases exclude the megastorm, so the far sky adds the trailing deck (T) and the
/// precursor (P) analytically: a 1D table over xi (distance behind the wall's edge) holding, per texel, those two records' contribution to the S6 layer map
/// (1 - remaining per layer, and weatherness). The GPU combines it with the atlas value as
/// <c>layers.xyz' = 1 - (1 - layers.xyz) * (1 - lut.xyz)</c>, <c>weatherness' = max(layers.w, lut.w)</c>, which reproduces <see cref="RingCloudLayers.EvaluateMap"/>'s
/// multiplicative combination. T and P are slabs (1e7 m across semi-axis), so the table does not depend on the across position beyond the lead offset.
/// </summary>
public static class MegastormFarCoverage
{
    public const int Texels = 256;
    const double MarginMeters = 1000.0;

    [ThreadStatic] private static List<RingVolumetricCloudLayer.RenderWeatherSystem>? _scratch;
    [ThreadStatic] private static List<CloudLayerSystem>? _layerScratch;

    /// <summary>
    /// The xi interval covering the T and P records (centre +- half depth; non-sculpt records render with support 1.0) with a 1 km margin each side.
    /// </summary>
    public static (double XiMin, double XiMax) Range(MegastormModel model)
    {
        MegastormSettings s = model.Settings;
        double reachScale = s.WarningReachMeters / MegastormModel.PrecursorBaseWarningReachMeters;
        double pCentre = -150000.0 * reachScale, pHalf = 110000.0 * reachScale;
        double halfT = 0.5 * s.TrailingLengthMeters;
        double tCentre = MegastormModel.TransitionEndXi + halfT, tHalf = halfT / 0.7;
        double min = Math.Min(pCentre - pHalf, tCentre - tHalf) - MarginMeters;
        double max = Math.Max(pCentre + pHalf, tCentre + tHalf) + MarginMeters;
        return (min, max);
    }

    /// <summary>
    /// Builds the table into <paramref name="rgba"/> (<see cref="Texels"/> x 4 floats: low, mid, high, weatherness). Returns false and zero-fills when the
    /// model is not active at <paramref name="t"/>. Texel i is at xi = XiMin + (i + 0.5) * (XiMax - XiMin) / Texels.
    /// </summary>
    public static bool Build(RingVolumetricCloudLayer weather, MegastormModel model, double t, double queryAcross, Vector3 sunLocal, float[] rgba,
        out double xiMin, out double xiMax)
    {
        (xiMin, xiMax) = Range(model);
        if (rgba.Length < Texels * 4) throw new ArgumentException("rgba must hold Texels * 4 floats");
        Array.Clear(rgba, 0, Texels * 4);
        if (!model.Evaluate(t).Active) return false;

        double circumference = Math.Max(1.0, model.Settings.CircumferenceMeters);
        double aroundNow = model.AroundAt(t);
        var systems = _scratch ??= new List<RingVolumetricCloudLayer.RenderWeatherSystem>(8);
        var layerSystems = _layerScratch ??= new List<CloudLayerSystem>(4);
        systems.Clear();
        layerSystems.Clear();
        weather.CollectMegastormRenderSystems(new RingCanonicalPosition(aroundNow, 0.0, queryAcross), circumference, t, systems, 0.0f, 500.0f);
        foreach (RingVolumetricCloudLayer.RenderWeatherSystem system in systems)
            if (system.SystemId == MegastormModel.TrailingRecordId || system.SystemId == MegastormModel.PrecursorRecordId)
                layerSystems.Add(new CloudLayerSystem(system, 1.0f));
        if (layerSystems.Count == 0) return true;

        Vector2 wind = weather.WeatherWindMetersPerSecond;
        float circumferenceF = (float)circumference;
        Vector2 shift = new((float)(-wind.X * t), (float)(-wind.Y * t));
        shift.X -= circumferenceF * MathF.Floor(shift.X / circumferenceF);
        float windAngle = MathF.Atan2(wind.Y, wind.X);
        CloudLayerTable table = CloudLayerTable.Shared;
        double dir = model.State.Direction;
        double lead = model.LeadOffset(queryAcross, t);
        double step = (xiMax - xiMin) / Texels;
        for (int i = 0; i < Texels; i++)
        {
            double xi = xiMin + (i + 0.5) * step;
            double around = MegastormModel.WrapPositive(aroundNow - dir * (xi + lead), circumference);
            var point = new CloudLayerPoint((float)around, (float)queryAcross, shift, circumferenceF, 0.0f, 0.5f, 0.0f, 0.0f, windAngle, sunLocal);
            RingCloudLayers.AccumulateSystems(point, layerSystems, table, out float low, out float mid, out float high, out float weatherness);
            int at = i * 4;
            rgba[at] = Math.Clamp(1.0f - low, 0.0f, 1.0f);
            rgba[at + 1] = Math.Clamp(1.0f - mid, 0.0f, 1.0f);
            rgba[at + 2] = Math.Clamp(1.0f - high, 0.0f, 1.0f);
            rgba[at + 3] = Math.Clamp(weatherness, 0.0f, 1.0f);
        }
        return true;
    }
}
