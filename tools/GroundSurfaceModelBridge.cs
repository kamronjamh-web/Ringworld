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

    public string ProxyShaderPath() => RingGroundSurfaceVariants.ProxyShaderPath;
}
