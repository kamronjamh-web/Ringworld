using System;
using Godot;
using RingworldTechDemo.Terrain.Hydrology;
using RingworldTechDemo.Voxel.Phase3;

namespace RingworldTechDemo.Terrain.Surface;

/// <summary>
/// T4-7 (docs/work-orders/terrain-vegetation/T4.md, D11): the per-sample material mask of river beds and banks, lake beds and shores, and dry basin floors, stored in four bytes of
/// <see cref="TerrainSample"/> and applied to the four biome texture weights (Desert, Forest, Tundra, Alpine; grass is the remainder). Weights only: no shader reads it.
/// Worlds with <c>water.rivers</c> and <c>ground.river_beds</c> only; every other world keeps the default (all zero) mask, which <see cref="Apply(RingTerrainSampler.BiomeTextureWeights, in RiverBedMask)"/> returns unchanged.
/// </summary>
/// <param name="RiverCover">Share of the river/lake target material [0, 255] / 255.</param>
/// <param name="TargetSand">Desert share of the target.</param>
/// <param name="TargetGravel">Alpine share of the target (Tundra is one minus both).</param>
/// <param name="PlayaCover">Share of the playa target (dry basin floor).</param>
public readonly record struct RiverBedMask(byte RiverCover, byte TargetSand, byte TargetGravel, byte PlayaCover)
{
    public bool IsDefault => RiverCover == 0 && PlayaCover == 0 && TargetSand == 0 && TargetGravel == 0;

    /// <summary>The mask as one uint for the sampler-cache record (RiverCover in the low byte).</summary>
    public uint Packed => RiverCover | ((uint)TargetSand << 8) | ((uint)TargetGravel << 16) | ((uint)PlayaCover << 24);

    public static RiverBedMask FromPacked(uint packed)
        => new((byte)(packed & 0xFFu), (byte)((packed >> 8) & 0xFFu), (byte)((packed >> 16) & 0xFFu), (byte)(packed >> 24));
}

/// <summary>The river or lake part of the mask before quantisation: cover, and the target's Desert and Alpine shares (Tundra is the rest).</summary>
public readonly record struct RiverBedPart(double Cover, double Sand, double Gravel);

public static class RiverBedSurfaceModel
{
    /// <summary>Mixed into the proxy cache definition of river-beds worlds: change any constant below and bump it.</summary>
    public const int Version = 2;
    public const int Bit = 3;

    // Materials as (Desert, Alpine) shares of the target; Tundra is the remainder (design D11, Materials).
    public const double WetSandSand = 0.55, WetGravelGravel = 0.60;
    public const double MudSand = 0.0, MudGravel = 0.0, GravelSand = 0.0, GravelGravel = 1.0;
    /// <summary>The playa target (dry lakebed): 0.30 Desert + 0.70 Alpine (Version 2: 0.45/0.55 rendered too warm in the T4-7 stills; real playa clay is pale grey-beige).</summary>
    public const double PlayaDesert = 0.30, PlayaAlpine = 0.70;

    // Coarseness g = smoothstep(0.6, 1.4, U), U the reach's mean speed in m/s.
    public const double CoarseStart = 0.6, CoarseEnd = 1.4;
    // River zones by height above the water dz.
    public const double BankBlendStart = 0.10, BankBlendEnd = 0.20;
    public const double RiverCoverFull = 0.25, RiverCoverEnd = 0.38;
    // Lake: silt with depth, and the shore.
    public const double SiltStartDepth = 1.0, SiltEndDepth = 4.0;
    public const double ShoreFull = 0.10, ShoreEnd = 0.25;
    /// <summary>Margins only where the level resolves them: above this footprint the cover is multiplied by 1 - smoothstep(-0.10, 0, dz).</summary>
    public const double MarginFootprintMeters = 4.0;
    public const double SubmergedFadeStart = -0.10, SubmergedFadeEnd = 0.0;
    // Rule P (playa).
    public const double PlayaTopMaxMeters = 8.0, PlayaTopDepthShare = 0.5, PlayaFullShare = 0.4;
    public const double PlayaMoistureStart = 0.30, PlayaMoistureEnd = 0.45;
    public const double PlayaMembershipStart = 0.0, PlayaMembershipEnd = 0.5;

    public static double SmoothStep(double e0, double e1, double x)
    {
        double t = Math.Clamp((x - e0) / (e1 - e0), 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    /// <summary>g in [0, 1]: 0 sand-bed, 1 gravel-bed, from the reach's mean speed U in m/s (Manning at n = 0.035 and 1 m depth: sand below a gradient of about 4e-4, gravel above about 2.5e-3).</summary>
    public static double Coarseness(double speed) => SmoothStep(CoarseStart, CoarseEnd, speed);

    /// <summary>
    /// The river or lake part at one sample: <paramref name="dz"/> is the presentation height minus the owner's level, <paramref name="g"/> the reach's coarseness (ignored for a lake),
    /// <paramref name="footprintMeters"/> the sample spacing (0 a point, +infinity the far views). No owner returns a zero part.
    /// </summary>
    public static RiverBedPart RiverPart(InlandWaterKind kind, double dz, double g, double footprintMeters)
    {
        double cover, sand, gravel;
        if (kind == InlandWaterKind.River)
        {
            // Bed and bar: mix(WetSand, WetGravel, g); the bank above mixes to mix(Mud, Gravel, g) between dz 0.10 and 0.20.
            double bedSand = WetSandSand * (1d - g), bedGravel = WetGravelGravel * g;
            double bankSand = MudSand + (GravelSand - MudSand) * g, bankGravel = MudGravel + (GravelGravel - MudGravel) * g;
            double t = SmoothStep(BankBlendStart, BankBlendEnd, dz);
            sand = bedSand + (bankSand - bedSand) * t;
            gravel = bedGravel + (bankGravel - bedGravel) * t;
            cover = 1d - SmoothStep(RiverCoverFull, RiverCoverEnd, dz);
        }
        else if (kind == InlandWaterKind.Lake)
        {
            // Sandy shallows, silty deep water: mix(WetSand, Mud, smoothstep(1, 4, depth)); the shore (dz >= 0) is WetSand.
            double silt = SmoothStep(SiltStartDepth, SiltEndDepth, Math.Max(0d, -dz));
            sand = WetSandSand + (MudSand - WetSandSand) * silt;
            gravel = 0d;
            cover = 1d - SmoothStep(ShoreFull, ShoreEnd, dz);
        }
        else return default;
        if (footprintMeters > MarginFootprintMeters) cover *= 1d - SmoothStep(SubmergedFadeStart, SubmergedFadeEnd, dz);
        return new RiverBedPart(cover, sand, gravel);
    }

    /// <summary>The top of the playa band above the basin floor: min(8, half the basin's depth); 0 when the basin has no depth.</summary>
    public static double PlayaTop(double floorLevel, double spillLevel)
        => Math.Min(PlayaTopMaxMeters, PlayaTopDepthShare * Math.Max(spillLevel - floorLevel, 0d));

    /// <summary>The playa cover without the membership term: height band above the floor times aridity. 0 above the band, for a humid basin and for a basin without depth.</summary>
    public static double PlayaBand(double height, double floorLevel, double spillLevel, double moisture)
    {
        double top = PlayaTop(floorLevel, spillLevel);
        if (!(top > 0d)) return 0d;
        double e = height - floorLevel;
        return (1d - SmoothStep(PlayaFullShare * top, top, e)) * (1d - SmoothStep(PlayaMoistureStart, PlayaMoistureEnd, moisture));
    }

    /// <summary>Rule P: <c>(1 - smoothstep(full, top, e)) (1 - smoothstep(0.30, 0.45, moisture)) smoothstep(0, 0.5, P_b)</c>, e = height - floor, top = min(8, D / 2), full = 0.4 top.</summary>
    public static double PlayaCover(double height, double floorLevel, double spillLevel, double moisture, double membership)
        => PlayaBand(height, floorLevel, spillLevel, moisture) * SmoothStep(PlayaMembershipStart, PlayaMembershipEnd, membership);

    private static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v * 255d), 0, 255);

    /// <summary>Quantises a river part and a playa cover to the four bytes. A part that rounds to no cover stores zero targets, so a sample with no effect is the default mask.</summary>
    public static RiverBedMask Pack(in RiverBedPart part, double playaCover)
    {
        byte cover = ToByte(part.Cover), playa = ToByte(playaCover);
        if (cover == 0) return new RiverBedMask(0, 0, 0, playa);
        return new RiverBedMask(cover, ToByte(part.Sand), ToByte(part.Gravel), playa);
    }

    /// <summary>The mask as floats: river cover, target sand, target gravel (target tundra is the rest, never below 0) and playa cover.</summary>
    public static (float RiverCover, float Sand, float Gravel, float Tundra, float PlayaCover) Unpack(in RiverBedMask mask)
    {
        float sand = mask.TargetSand / 255f, gravel = mask.TargetGravel / 255f;
        return (mask.RiverCover / 255f, sand, gravel, MathF.Max(0f, 1f - sand - gravel), mask.PlayaCover / 255f);
    }

    /// <summary>
    /// D11 decision 5: first <c>w = lerp(w, Playa, PlayaCover)</c>, then <c>w = lerp(w, RiverTarget, RiverCover)</c>. With both covers 0 it returns <paramref name="w"/> itself.
    /// </summary>
    public static RingTerrainSampler.BiomeTextureWeights Apply(RingTerrainSampler.BiomeTextureWeights w, in RiverBedMask mask)
    {
        if (mask.RiverCover == 0 && mask.PlayaCover == 0) return w;
        (float river, float sand, float gravel, float tundra, float playa) = Unpack(mask);
        float desert = w.Desert, forest = w.Forest, tundraW = w.Tundra, alpine = w.Alpine;
        if (playa > 0f)
        {
            desert += ((float)PlayaDesert - desert) * playa;
            forest += (0f - forest) * playa;
            tundraW += (0f - tundraW) * playa;
            alpine += ((float)PlayaAlpine - alpine) * playa;
        }
        if (river > 0f)
        {
            desert += (sand - desert) * river;
            forest += (0f - forest) * river;
            tundraW += (tundra - tundraW) * river;
            alpine += (gravel - alpine) * river;
        }
        return new RingTerrainSampler.BiomeTextureWeights(desert, forest, tundraW, alpine);
    }

    /// <summary>The same in vertex-colour form (R Desert, G Forest, B Tundra, A Alpine), for the proxy colour sites.</summary>
    public static Color Apply(Color w, in RiverBedMask mask)
    {
        if (mask.RiverCover == 0 && mask.PlayaCover == 0) return w;
        return Apply(new RingTerrainSampler.BiomeTextureWeights(w.R, w.G, w.B, w.A), mask).ToVertexColor();
    }
}
