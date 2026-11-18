using System;
using Godot;
using RingworldTechDemo.Terrain.Generation;
using RingworldTechDemo.Terrain.Surface;

namespace RingworldTechDemo.Tools;

/// <summary>Test-only instance wrapper so GDScript tests (terrain_rock_material_gpu_test) can call the static RingGroundSurfaceModel.</summary>
public partial class GroundSurfaceModelBridge : RefCounted
{
    public double RockExposure(double slopeDeg, double baselineM, double relief, double forestWeight, double rockStartDeg)
        => RingGroundSurfaceModel.RockExposure(slopeDeg, baselineM, relief, forestWeight, rockStartDeg);

    public double ScreeWeight(double slopeDeg, double baselineM, double relief, double vegetated)
        => RingGroundSurfaceModel.ScreeWeight(slopeDeg, baselineM, relief, vegetated);

    public double StrataFactor(double height, double footprint, double stepM, int seed, double contrast)
        => RingGroundSurfaceModel.StrataFactor(height, footprint, stepM, (uint)seed, contrast);

    /// <summary>Publishes a world feature mask (generation 1) before a surface or proxy material is created, so RingGroundSurfaceVariants picks its variant.</summary>
    public bool PublishMask(int mask) => WorldGeneration.PublishForTests(1, mask);

    /// <summary>The synthetic face of terrain_rock_material_gpu_test (BASE 8 m, 10 / 33 / 50 degree zones along z, ramp from d = 20 m over 40 m), plus Gaussian mounds (sigma 14 m, 6 m): a crest at (-120, 30) and a hollow at (-120, -30), and the same pair at d = -232, 24 m from the window's edge (the reach check).</summary>
    public static double FaceHeight(double d, double z)
    {
        double t10 = Math.Tan(10.0 * Math.PI / 180.0), t33 = Math.Tan(33.0 * Math.PI / 180.0), t50 = Math.Tan(50.0 * Math.PI / 180.0);
        double slope = Lerp(Lerp(t10, t33, RingGroundSurfaceModel.SmoothStep(-30.0, -10.0, z)), t50, RingGroundSurfaceModel.SmoothStep(10.0, 30.0, z));
        return 8.0 + slope * Math.Clamp(d - 20.0, 0.0, 40.0) + Mound(d, z, -120.0, 30.0, 6.0) + Mound(d, z, -120.0, -30.0, -6.0) + Mound(d, z, -232.0, 30.0, 6.0) + Mound(d, z, -232.0, -30.0, -6.0);
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
    private static double Mound(double d, double z, double cd, double cz, double amplitude)
        => amplitude * Math.Exp(-((d - cd) * (d - cd) + (z - cz) * (z - cz)) / (2.0 * 14.0 * 14.0));

    /// <summary>C64 at window position (d, z) of the face, with the window's across anchor and direction (canonical around = d, across = anchor + dir * z).</summary>
    public double FaceC64(double d, double z, double acrossAnchor, int dir)
        => RingGroundSurfaceModel.Convexity64((a, c) => FaceHeight(a, (c - acrossAnchor) * dir), d, acrossAnchor + dir * z);

    public double Relief(double c64) => RingGroundSurfaceModel.Relief(c64);

    /// <summary>The green channel of the relief colour of mid grey, at full strength, no rock, near the camera (the w = 2 debug view).</summary>
    public double ReliefGreyGreen(double relief) => RingGroundSurfaceModel.ReliefColour((0.5, 0.5, 0.5), relief, 0.0, 0.0).G;

    public double DetailNormalWeight(double rock, double scree, double distanceM) => RingGroundSurfaceModel.DetailNormalWeight(rock, scree, distanceM);

    public string ProxyShaderPath() => RingGroundSurfaceVariants.ProxyShaderPath;

    public string FarShaderPath() => RingGroundSurfaceVariants.FarShaderPath;
}
