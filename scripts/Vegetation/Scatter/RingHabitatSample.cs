using System;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Vegetation.Scatter;

/// <summary>T5 design A2: what a habitat grid node or point reports. <c>RimClamped</c> marks an index that was clamped at the across rim; <c>Fine</c> marks a value read from the 8 m lattice.</summary>
[Flags] public enum RingHabitatFlags : byte { None = 0, Water = 1, SiteBlocked = 2, Fine = 4, RimClamped = 8 }

public readonly record struct RingHabitatSample(
    float HeightM, float SlopeDeg, float SlopeBaselineM, float C8M, float C64M,
    float Moisture, float Temperature, float ForestWeight, float GrassWeight,
    float Canopy, ForestHabitat Habitat,
    float Wetness, float ChannelWetness, float HeightAboveChannelM,
    float RockExposure, float Scree, float SoilDepth, RingHabitatFlags Flags,
    float Clearing = 0f, float MapleWeight = 0f, float FirWeight = 0f, float PineWeight = 0f)
{
    public float Relief64 => (float)RingGroundSurfaceModel.Relief(C64M);
    public float Relief8 => C8M / (MathF.Abs(C8M) + 0.25f);
    public float WetnessTotal => MathF.Max(Wetness, ChannelWetness);
}
