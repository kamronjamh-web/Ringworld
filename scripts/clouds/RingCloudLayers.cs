using System;
using System.Collections.Generic;
using Godot;

namespace RingworldTechDemo;

/// <summary>One weather system as the layer build sees it: the uploaded record and its overflow-fade weight (block d.z).</summary>
public readonly record struct CloudLayerSystem(RingVolumetricCloudLayer.RenderWeatherSystem System, float Fade = 1.0f);

/// <summary>
/// Everything <see cref="RingCloudLayers.EvaluateMap"/> needs at one canonical position. <see cref="Coverage"/> is the macro map's R
/// (coverage before the user bias); the cluster terms are the signed (small * 0.62 + large * 0.38 - 0.5) * strength values at the
/// layer's own noise salts, evaluated at the source-space position (canonical + Shift).
/// </summary>
public readonly record struct CloudLayerPoint(
    float CanonicalAround,
    float CanonicalAcross,
    Vector2 Shift,
    float CircumferenceMeters,
    float Coverage,
    float BaseCoverage,
    float ClusterMid,
    float ClusterHigh,
    float WindAngleRadians,
    Vector3 SunLocal);

/// <summary>One resolved layer of a column. Coverage 0 means the layer is absent.</summary>
public readonly record struct CloudLayerSlot(float Coverage, float BaseMeters, float TopMeters, float Stratiformity)
{
    public bool Active => Coverage > 0.0f;
}

/// <summary>The cloud layers over one position: the macro-map values plus each layer's base, top and stratiformity.</summary>
public readonly record struct CloudLayerColumn(float Weatherness, IReadOnlyList<CloudLayerSlot> Layers)
{
    public CloudLayerSlot Low => Layers[CloudLayerTable.LowIndex];
    public CloudLayerSlot Mid => Layers[CloudLayerTable.MidIndex];
    public CloudLayerSlot High => Layers[CloudLayerTable.HighIndex];
}

/// <summary>
/// Pure CPU column evaluation of the S6 cloud layers (docs/storm-revamp-s6-cloud-layers-design.md section 2). It is the exact twin of the
/// layer build in RingMacroWeatherMap.glsl / RingCloudLayers.glslinc (as GetPrecipitationPotentialAt is for precipitation): same
/// inputs, same formulas, float arithmetic. Any change here changes the GLSL and the table upload in the same commit.
/// </summary>
public static class RingCloudLayers
{
    /// <summary>The hard cloud ceiling. Nothing the renderer, the CPU weather or the settings produce exceeds it (tropopause).</summary>
    public const float CeilingMeters = 18000.0f;

    /// <summary>Size of the std140 layers block (RingCloudLayers.glslinc, SunshineClouds.gd CLOUD_LAYERS_BYTES): 41 vec4.</summary>
    public const int BlockBytes = 656;

    /// <summary>
    /// Writes the table into the layers uniform block layout of RingCloudLayers.glslinc. <paramref name="sunLocal"/> is the direction to
    /// the sun in the ring-grid frame (ring axis +Y); pass <see cref="Vector3.Zero"/> when unknown (the diurnal term then does nothing).
    /// </summary>
    public static void BuildBlock(CloudLayerTable table, Vector3 sunLocal, byte[] block)
    {
        if (block.Length != BlockBytes) throw new ArgumentException("layers block must be " + BlockBytes + " bytes");
        Array.Clear(block);
        void Write(int vec4, int component, float value)
            => System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(block.AsSpan((vec4 * 4 + component) * 4, 4), value);
        void WriteVec(int vec4, float x, float y, float z, float w) { Write(vec4, 0, x); Write(vec4, 1, y); Write(vec4, 2, z); Write(vec4, 3, w); }
        WriteVec(0, CeilingMeters, table.MidFairScale, table.HighFairScale, table.EffectiveDiurnalStrength);
        WriteVec(1, table.DiurnalFloor, table.DiurnalPeakHoursAfterNoon, table.StormSupportFactor, table.LowAmountBias);
        WriteVec(2, table.WeathernessDensityRamp.X, table.WeathernessDensityRamp.Y, table.MidClusterSalts.X, table.MidClusterSalts.Y);
        WriteVec(3, table.HighClusterSalts.X, table.HighClusterSalts.Y, table.MidAmountBias, table.HighAmountBias);
        bool sunValid = sunLocal.LengthSquared() > 0.0001f;
        WriteVec(4, sunLocal.X, sunLocal.Y, sunLocal.Z, sunValid ? 1.0f : 0.0f);
        for (int i = 0; i < table.Layers.Length && i < CloudLayerTable.MaximumLayers; i++)
        {
            CloudLayerDefinition layer = table.Layers[i];
            int at = 5 + i * 4;
            WriteVec(at, layer.FairBaseMeters, layer.WeatherBaseMeters, layer.TopCapMeters, layer.GradientRow);
            WriteVec(at + 1, layer.ConvectiveThicknessMeters.X, layer.ConvectiveThicknessMeters.Y,
                layer.StratiformThicknessMeters.X, layer.StratiformThicknessMeters.Y);
            WriteVec(at + 2, layer.StratiformNoiseStretch, layer.ErosionScale.X, layer.ErosionScale.Y, layer.ExtinctionPerMeter);
            WriteVec(at + 3, layer.PhaseG, layer.AmbientScale, layer.ShadowWeight, (float)(int)layer.RenderMode);
            WriteVec(37 + i, layer.NoiseOffsetMeters.X, layer.NoiseOffsetMeters.Y, layer.NoiseOffsetMeters.Z, 0.0f);
        }
        foreach (CloudGenusRow row in table.Genera)
        {
            int profile = (int)row.Profile;
            if (profile < 0 || profile >= 16) continue;
            WriteVec(21 + profile, row.Low, row.Mid, row.High, (float)(int)row.LowersBase + 4.0f * (int)row.FrontType);
        }
    }

    /// <summary>
    /// The per-system part of <see cref="EvaluateMap"/>: the multiplicative remaining (1 - coverage) per layer and the weatherness (clamped) over the systems.
    /// Combining two system lists is remaining = remainingA * remainingB and weatherness = max, which is what the far layer's analytic megastorm table relies on.
    /// </summary>
    public static void AccumulateSystems(in CloudLayerPoint point, IReadOnlyList<CloudLayerSystem> systems, CloudLayerTable table,
        out float remainingLow, out float remainingMid, out float remainingHigh, out float weatherness)
    {
        float circumference = point.CircumferenceMeters;
        float sourceAround = point.CanonicalAround + point.Shift.X;
        float sourceAcross = point.CanonicalAcross + point.Shift.Y;
        float windX = MathF.Cos(point.WindAngleRadians);
        float windY = MathF.Sin(point.WindAngleRadians);
        Vector2 densityRamp = table.WeathernessDensityRamp;

        remainingLow = 1.0f;
        remainingMid = 1.0f;
        remainingHigh = 1.0f;
        weatherness = 0.0f;
        for (int i = 0; i < systems.Count; i++)
        {
            RingVolumetricCloudLayer.RenderWeatherSystem system = systems[i].System;
            float fade = systems[i].Fade;
            CloudGenusRow? row = table.Genus(system.PhysicalProfile);
            bool lowersBase = (system.Flags & RingVolumetricCloudLayer.RenderWeatherFlagLowersBase) != 0u;
            float majorRadius = system.MajorRadiusMeters;
            float minorRadius = system.MinorRadiusMeters;
            float density = system.Influence;
            float influenceLow;
            float influenceUpper;
            float frontLow = 1.0f, frontMid = 1.0f, frontHigh = 1.0f, frontWeather = 1.0f;
            if (system.IsStatic)
            {
                float deltaAround = Wrap(point.CanonicalAround - (float)system.CenterAroundMeters, circumference);
                float deltaAcross = point.CanonicalAcross - (float)system.CenterAcrossMeters;
                float x = deltaAround * system.OrientationCosine + deltaAcross * system.OrientationSine;
                float z = -deltaAround * system.OrientationSine + deltaAcross * system.OrientationCosine;
                float normalized = MathF.Sqrt(x * x / (majorRadius * majorRadius) + z * z / (minorRadius * minorRadius));
                influenceLow = (1.0f - SmoothUnit((normalized - 0.55f) / 0.75f)) * density * fade;
                if (influenceLow <= 0.0001f) continue;
                influenceUpper = influenceLow;
            }
            else
            {
                float support = MathF.Max(1.0f, system.SupportMultiplier);
                float deltaAround = Wrap((float)system.CenterAroundMeters - sourceAround, circumference);
                float deltaAcross = (float)system.CenterAcrossMeters - sourceAcross;
                float reach = majorRadius * support;
                if (deltaAround * deltaAround + deltaAcross * deltaAcross >= reach * reach) continue;
                float along = system.OrientationCosine * deltaAround + system.OrientationSine * deltaAcross;
                float across = -system.OrientationSine * deltaAround + system.OrientationCosine * deltaAcross;
                float normalized = MathF.Sqrt(along * along / MathF.Max(1.0f, majorRadius * majorRadius)
                    + across * across / MathF.Max(1.0f, minorRadius * minorRadius));
                if (normalized >= support) continue;
                influenceLow = normalized >= 1.0f ? 0.0f : (1.0f - SmoothUnit((normalized - 0.32f) / 0.68f)) * density * fade;
                influenceUpper = (1.0f - SmoothUnit((normalized / support - 0.32f) / 0.68f)) * density * fade;
                if (row != null && row.FrontType != CloudFrontType.None)
                {
                    // Position along the downwind axis in units of the ellipse's extent along it (+1 = leading edge).
                    float systemX = -along;
                    float downwindX = system.OrientationCosine * windX + system.OrientationSine * windY;
                    float downwindY = -system.OrientationSine * windX + system.OrientationCosine * windY;
                    float systemY = -across;
                    float q = systemX * downwindX + systemY * downwindY;
                    float s = q * MathF.Sqrt(downwindX * downwindX / (majorRadius * majorRadius)
                        + downwindY * downwindY / (minorRadius * minorRadius));
                    if (row.FrontType == CloudFrontType.Warm)
                    {
                        float lowRamp = RampDown(s, 0.5f, 0.0f);
                        float behind = SmoothUnit((-0.6f - s) / 0.4f);
                        frontHigh = RampDown(s, 2.0f, 0.8f);
                        frontMid = RampDown(s, 1.2f, 0.3f);
                        frontLow = lowRamp * (1.0f + (0.6f - 1.0f) * behind);
                        frontWeather = lowRamp * (1.0f - behind);
                    }
                    else
                    {
                        float band = 1.0f - SmoothUnit((MathF.Abs(s) - 0.4f) / 0.25f);
                        frontLow = band;
                        frontMid = band;
                        frontWeather = band;
                        frontHigh = RampDown(s, 1.6f, 0.2f);
                    }
                }
            }

            if (row != null)
            {
                remainingLow *= 1.0f - Math.Clamp(row.Low * frontLow * influenceLow, 0.0f, 1.0f);
                remainingMid *= 1.0f - Math.Clamp(row.Mid * frontMid * influenceUpper, 0.0f, 1.0f);
                remainingHigh *= 1.0f - Math.Clamp(row.High * frontHigh * influenceUpper, 0.0f, 1.0f);
            }
            if (lowersBase)
            {
                float densityGate = Math.Clamp((density - densityRamp.X) / MathF.Max(densityRamp.Y, 0.0001f), 0.0f, 1.0f);
                weatherness = MathF.Max(weatherness, influenceLow * densityGate * frontWeather);
            }
        }
        weatherness = Math.Clamp(weatherness, 0.0f, 1.0f);
    }

    /// <summary>Layer coverages (low, mid, high) and weatherness (0..1) at the point: the macro map's second array layer.</summary>
    public static Vector4 EvaluateMap(in CloudLayerPoint point, IReadOnlyList<CloudLayerSystem> systems, CloudLayerTable table)
    {
        float circumference = point.CircumferenceMeters;
        AccumulateSystems(point, systems, table, out float remainingLow, out float remainingMid, out float remainingHigh, out float weatherness);

        float diurnal = DiurnalCoverageFactor(point.CanonicalAround, circumference, point.SunLocal, table);
        float lowFair = point.Coverage * (diurnal + (1.0f - diurnal) * weatherness);
        float midFair = Math.Clamp(point.BaseCoverage + point.ClusterMid, 0.015f, 0.985f) * table.MidFairScale;
        float highFair = Math.Clamp(point.BaseCoverage + point.ClusterHigh, 0.015f, 0.985f) * table.HighFairScale;
        lowFair = ApplyAmountBias(lowFair, table.LowAmountBias);
        midFair = ApplyAmountBias(midFair, table.MidAmountBias);
        highFair = ApplyAmountBias(highFair, table.HighAmountBias);
        return new Vector4(
            Math.Clamp(1.0f - (1.0f - lowFair) * remainingLow, 0.0f, 1.0f),
            Math.Clamp(1.0f - (1.0f - midFair) * remainingMid, 0.0f, 1.0f),
            Math.Clamp(1.0f - (1.0f - highFair) * remainingHigh, 0.0f, 1.0f),
            weatherness);
    }

    /// <summary>Menu amount bias on one layer's fair-weather coverage: + lifts toward 1, - scales toward 0, exactly 0 returns the input.</summary>
    public static float ApplyAmountBias(float coverage, float bias)
        => bias > 0.0f ? coverage + (1.0f - coverage) * bias : bias < 0.0f ? coverage * (1.0f + bias) : coverage;

    /// <summary>The column at the point: <see cref="EvaluateMap"/> resolved against the table into bases, tops and stratiformity.</summary>
    public static CloudLayerColumn Evaluate(in CloudLayerPoint point, IReadOnlyList<CloudLayerSystem> systems, CloudLayerTable table)
        => Resolve(EvaluateMap(point, systems, table), table);

    /// <summary>What the shader derives from the stored map values (the menu bases need no rebuild).</summary>
    public static CloudLayerColumn Resolve(Vector4 map, CloudLayerTable table)
    {
        var slots = new CloudLayerSlot[CloudLayerTable.MaximumLayers];
        float weatherness = map.W;
        for (int i = 0; i < table.Layers.Length && i < 3; i++)
        {
            CloudLayerDefinition layer = table.Layers[i];
            float coverage = i == CloudLayerTable.LowIndex ? map.X : i == CloudLayerTable.MidIndex ? map.Y : map.Z;
            float baseMeters = layer.FairBaseMeters + (layer.WeatherBaseMeters - layer.FairBaseMeters) * weatherness;
            float stratiformity = i == CloudLayerTable.LowIndex
                ? Math.Clamp(SmoothUnit((coverage - 0.4f) / 0.45f) * 0.6f + weatherness * 0.8f, 0.0f, 1.0f)
                : SmoothUnit((coverage - 0.4f) / 0.4f);
            float convective = layer.ConvectiveThicknessMeters.X + (layer.ConvectiveThicknessMeters.Y - layer.ConvectiveThicknessMeters.X) * coverage;
            float stratiform = layer.StratiformThicknessMeters.X + (layer.StratiformThicknessMeters.Y - layer.StratiformThicknessMeters.X) * coverage;
            float topMeters = baseMeters + convective + (stratiform - convective) * stratiformity;
            topMeters = MathF.Max(baseMeters, MathF.Min(MathF.Min(topMeters, layer.TopCapMeters), CeilingMeters));
            slots[i] = new CloudLayerSlot(coverage, baseMeters, topMeters, stratiformity);
        }
        return new CloudLayerColumn(weatherness, slots);
    }

    /// <summary>
    /// Daytime-cumulus factor on fair-weather low coverage (1 = no change). Local solar phase is analytic: with up(around) the surface
    /// normal in the ring-grid frame and the sun rotating about the ring axis (+Y), cos h = dot(up, sun_xz) and sin h = -dot(up, Y x sun_xz)
    /// give the hour angle h (0 = local noon, positive afternoon). d = smoothed 0.5 + 0.5 cos(h - peak) peaks PeakHoursAfterNoon after noon.
    /// </summary>
    public static float DiurnalCoverageFactor(float aroundMeters, float circumferenceMeters, Vector3 sunLocal, CloudLayerTable table)
    {
        float planar = MathF.Sqrt(sunLocal.X * sunLocal.X + sunLocal.Z * sunLocal.Z);
        if (planar < 0.0001f || circumferenceMeters < 1.0f || table.EffectiveDiurnalStrength <= 0.0f) return 1.0f;
        float wrapped = aroundMeters - circumferenceMeters * MathF.Floor(aroundMeters / circumferenceMeters);
        float angle = wrapped / circumferenceMeters * MathF.Tau;
        float upX = -MathF.Cos(angle);
        float upZ = -MathF.Sin(angle);
        float sunX = sunLocal.X / planar;
        float sunZ = sunLocal.Z / planar;
        float elevation = upX * sunX + upZ * sunZ;
        float rate = upX * sunZ + upZ * -sunX;
        float hourAngle = MathF.Atan2(-rate, elevation);
        float d = SmoothUnit(0.5f + 0.5f * MathF.Cos(hourAngle - table.DiurnalPeakHoursAfterNoon * MathF.PI / 12.0f));
        float scaled = table.DiurnalFloor + (1.0f - table.DiurnalFloor) * d;
        return 1.0f + (scaled - 1.0f) * table.EffectiveDiurnalStrength;
    }

    /// <summary>Hour angle of <see cref="DiurnalCoverageFactor"/> in hours after local noon (-12..12), for tests.</summary>
    public static float LocalSolarHours(float aroundMeters, float circumferenceMeters, Vector3 sunLocal)
    {
        float planar = MathF.Sqrt(sunLocal.X * sunLocal.X + sunLocal.Z * sunLocal.Z);
        if (planar < 0.0001f || circumferenceMeters < 1.0f) return 0.0f;
        float wrapped = aroundMeters - circumferenceMeters * MathF.Floor(aroundMeters / circumferenceMeters);
        float angle = wrapped / circumferenceMeters * MathF.Tau;
        float upX = -MathF.Cos(angle);
        float upZ = -MathF.Sin(angle);
        float sunX = sunLocal.X / planar;
        float sunZ = sunLocal.Z / planar;
        return MathF.Atan2(-(upX * sunZ + upZ * -sunX), upX * sunX + upZ * sunZ) * 12.0f / MathF.PI;
    }

    private static float SmoothUnit(float value)
    {
        float t = Math.Clamp(value, 0.0f, 1.0f);
        return t * t * (3.0f - 2.0f * t);
    }

    /// <summary>1 at s = to and below, 0 at s = from and above (from &gt; to), smooth between.</summary>
    private static float RampDown(float s, float from, float to) => SmoothUnit((from - s) / (from - to));

    private static float Wrap(float delta, float circumference)
        => circumference > 1.0f ? delta - circumference * MathF.Floor(delta / circumference + 0.5f) : delta;
}
