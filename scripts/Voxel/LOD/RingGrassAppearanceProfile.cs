using Godot;

namespace RingworldTechDemo.Voxel.LOD;

/// <summary>
/// Author-facing biome appearance controls. Placement values are captured into
/// an immutable snapshot before worker dispatch; palette values stay material
/// parameters and therefore never invalidate resident tiles.
/// </summary>
public partial class RingGrassAppearanceProfile : Resource
{
    [Export] public bool Enabled { get; set; } = true;
    /// <summary>Optional legacy blade-face diffuse, root AO, backlight and specular.</summary>
    [Export] public bool EnableBladeShading { get; set; } = false;
    [Export(PropertyHint.Range, "0,2,0.01")] public float GrasslandDensityMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float ForestDensityMultiplier { get; set; } = .65f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float SparseDensityMultiplier { get; set; } = .22f;
    [Export(PropertyHint.Range, "0.25,2,0.01")] public float GrasslandHeightMultiplier { get; set; } = 1.0f;
    [Export(PropertyHint.Range, "0.25,2,0.01")] public float ForestHeightMultiplier { get; set; } = 1.12f;
    [Export(PropertyHint.Range, "0.25,2,0.01")] public float SparseHeightMultiplier { get; set; } = .70f;
    [Export] public Color GrasslandTint { get; set; } = new(.22f, .36f, .12f, 1);
    [Export] public Color ForestTint { get; set; } = new(.14f, .28f, .10f, 1);
    [Export] public Color SparseTint { get; set; } = new(.42f, .38f, .18f, 1);
    /// <summary>Terrain palette match at every distance; retains the persisted handoff key.</summary>
    [Export(PropertyHint.Range, "0,1,0.01")] public float TerrainHandoffStrength { get; set; } = 1f;
    /// <summary>Distance band for ground lighting and final blade brightness convergence.</summary>
    [Export(PropertyHint.Range, "0,100,0.1")] public float TerrainBlendStartPercent { get; set; } = 70f;
    [Export(PropertyHint.Range, "0,100,0.1")] public float TerrainBlendEndPercent { get; set; } = 90f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float TerrainLightingMatch { get; set; } = 1f;

    internal RingGrassAppearanceSnapshot CapturePlacement()
        => new(Enabled, GrasslandDensityMultiplier, ForestDensityMultiplier, SparseDensityMultiplier,
            GrasslandHeightMultiplier, ForestHeightMultiplier, SparseHeightMultiplier);
}

/// <summary>Worker-safe scalar subset of <see cref="RingGrassAppearanceProfile"/>.</summary>
internal readonly record struct RingGrassAppearanceSnapshot(
    bool Enabled, float GrasslandDensityMultiplier, float ForestDensityMultiplier, float SparseDensityMultiplier,
    float GrasslandHeightMultiplier, float ForestHeightMultiplier, float SparseHeightMultiplier)
{
    public static readonly RingGrassAppearanceSnapshot Default = new(false, 1, 1, 1, 1, 1, 1);

    public float DensityFor(float forest, float sparse)
    {
        if (!Enabled) return 1.0f;
        forest = Mathf.Clamp(forest, 0, 1); sparse = Mathf.Clamp(sparse, 0, 1 - forest);
        return Mathf.Clamp(GrasslandDensityMultiplier * (1 - forest - sparse)
            + ForestDensityMultiplier * forest + SparseDensityMultiplier * sparse, 0, 2);
    }

    public float HeightFor(float forest, float sparse)
    {
        if (!Enabled) return 1.0f;
        forest = Mathf.Clamp(forest, 0, 1); sparse = Mathf.Clamp(sparse, 0, 1 - forest);
        return Mathf.Clamp(GrasslandHeightMultiplier * (1 - forest - sparse)
            + ForestHeightMultiplier * forest + SparseHeightMultiplier * sparse, .25f, 2);
    }
}
